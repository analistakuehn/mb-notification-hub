using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Renders the published version of a template for sibling modules with the
/// sandboxed engine: variables payload refused above the published byte
/// ceiling before anything walks it, locale fallback chain, URL variables
/// enforced against the template allowlist, pinned layout wrapped around the
/// body, and, on demand, the masked form rendered with every sensitive
/// variable masked. Each form carries the canonical hash of exactly the fields
/// it shipped.
/// </summary>
internal sealed class PublishedTemplateRenderer(
    TemplateManagementDbContext dbContext,
    ScribanTemplateEngine engine,
    PublishedReadCache cache,
    PublishedContextLoader contextLoader,
    ILogger<PublishedTemplateRenderer> logger) : IPublishedTemplateRenderer
{
    public async Task<Result<PublishedTemplateRender>> RenderAsync(
        PublishedRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<Channel> channel = Channel.Create(request.Channel);
        if (channel.IsFailure)
        {
            return channel.AsFailure<Channel, PublishedTemplateRender>();
        }

        Result<Locale> locale = Locale.Create(request.Locale);
        if (locale.IsFailure)
        {
            return locale.AsFailure<Locale, PublishedTemplateRender>();
        }

        // Ahead of the catalog, and ahead of the scan: from here on every step
        // walks the payload once more, so a payload nobody bounded makes each
        // of them cost whatever the caller decided to send. Cheap checks first
        // means the refusal costs one pass and no query.
        if (VariablesPayloadSize.ExceedsMaxBytes(request.Variables))
        {
            return Result.ValidationError<PublishedTemplateRender>(DomainError.Format(
                ErrorCodes.VariablesPayloadTooLarge,
                "The variables payload must serialize to at most "
                + $"{VariablesPayloadSize.MaxBytes} bytes of JSON."));
        }

        Result<PublishedTemplateContext> context = await contextLoader.LoadAsync(
            request.Application, request.TemplateKey, cancellationToken);
        if (context.IsFailure)
        {
            return context.AsFailure<PublishedTemplateContext, PublishedTemplateRender>();
        }

        (Template template, TemplateVersion version) = context.Value!;
        var channelContents = version.Contents
            .Where(content => content.Channel == channel.Value)
            .ToList();
        if (channelContents.Count == 0)
        {
            return Result.NotFound<PublishedTemplateRender>(DomainError.Format(
                ErrorCodes.TemplateContentNotFound,
                $"The published version of template '{version.TemplateKey.Value}' has no content "
                + $"for channel '{channel.Value!.Value}'."));
        }

        var availableLocales = channelContents.Select(content => content.Locale).ToList();
        Locale? resolved = LocaleResolution.Resolve(locale.Value!, availableLocales, template.DefaultLocale);
        if (resolved is null)
        {
            return Result.NotFound<PublishedTemplateRender>(DomainError.Format(
                ErrorCodes.TemplateContentNotFound,
                $"Locale '{locale.Value!.Value}' does not resolve for channel '{channel.Value!.Value}': "
                + "no exact match, no base-language content and no default-locale content."));
        }

        Result urlGuard = VariablesDestinationPolicy.Validate(template, version, request.Variables);
        if (urlGuard.IsFailure)
        {
            return urlGuard.AsFailure<PublishedTemplateRender>();
        }

        Result<LayoutWrapper?> wrapper = await ResolveLayoutWrapperAsync(
            version, channel.Value!, resolved, cancellationToken);
        if (wrapper.IsFailure)
        {
            return wrapper.AsFailure<LayoutWrapper?, PublishedTemplateRender>();
        }

        TemplateContent content = channelContents.First(candidate => candidate.Locale == resolved);
        Result<RenderedForm> full = await RenderFormAsync(
            template,
            channel.Value!,
            content,
            request.Variables,
            wrapper.Value,
            AuthenticationLinkBan.Enforce,
            cancellationToken);
        if (full.IsFailure)
        {
            // The alarm stays here and not inside the policy, which has no
            // application, no key and no version to name and does not log at
            // all. This recognizes the refusal the way the consuming module
            // recognizes it, by equality against the bare word.
            if (string.Equals(full.Error, TemplateValidation.AuthenticationSmsLinkCode, StringComparison.Ordinal))
            {
                // Alarm, not a note: publication already refuses this shape, so
                // a render that produces one means the link arrived through a
                // variable value at request time. The message never leaves.
                logger.AuthenticationSmsLinkRefused(
                    request.Application, request.TemplateKey, version.Version);
            }

            return full.AsFailure<RenderedForm, PublishedTemplateRender>();
        }

        RenderedForm? masked = null;
        if (request.IncludeMaskedForm)
        {
            Result<RenderedForm> maskedForm = await RenderMaskedFormAsync(
                template, channel.Value!, content, request.Variables, wrapper.Value, full.Value!, cancellationToken);
            if (maskedForm.IsFailure)
            {
                return maskedForm.AsFailure<RenderedForm, PublishedTemplateRender>();
            }

            masked = maskedForm.Value;
        }

        return Result.Success(new PublishedTemplateRender
        {
            Channel = channel.Value!.Value,
            RequestedLocale = locale.Value!.Value,
            ResolvedLocale = resolved.Value,
            Version = version.Version,
            Full = full.Value!,
            Masked = masked,
        });
    }

    /// <summary>
    /// The masked form repeats the render with the sensitive values masked, so
    /// masking can never leak through a template transformation; a payload
    /// with nothing to mask reuses the full form, hash included.
    /// <para>
    /// One walk decides both things. Asking whether anything needs masking and
    /// then masking are the same traversal, and splitting them lets the two
    /// answers drift: a "nothing to mask" computed apart from the mask that ran
    /// returns the complete form as the masked one, hash included, into a trail
    /// that cannot be corrected afterwards.
    /// </para>
    /// </summary>
    private async Task<Result<RenderedForm>> RenderMaskedFormAsync(
        Template template,
        Channel channel,
        TemplateContent content,
        JsonElement? variables,
        LayoutWrapper? wrapper,
        RenderedForm full,
        CancellationToken cancellationToken)
    {
        SensitiveValueMask.Outcome masked = VariableMasking.Mask(variables, template.SensitiveVariables);
        if (!masked.Changed)
        {
            return Result.Success(full);
        }

        // The full form already answered the authentication-SMS ban over this
        // same content, and masking only replaces a value with a fixed marker:
        // it can remove a link and never write one. A second pass would be a
        // second scan of every rendered field for nothing.
        return await RenderFormAsync(
            template,
            channel,
            content,
            masked.Value,
            wrapper,
            AuthenticationLinkBan.AlreadyEnforced,
            cancellationToken);
    }

    private async Task<Result<RenderedForm>> RenderFormAsync(
        Template template,
        Channel channel,
        TemplateContent content,
        JsonElement? variables,
        LayoutWrapper? wrapper,
        AuthenticationLinkBan ban,
        CancellationToken cancellationToken)
    {
        // The fields of one form share the execution context, which is what a
        // render mostly pays for, and nothing else: each of them still renders
        // over its own data, its own buffer and its own deadline. The scope
        // lives in this call frame and dies with the form, so the masked form
        // below, which repeats the render, never touches this one.
        ScribanTemplateEngine.FormRenderScope scope = engine.BeginForm();
        Result<string?> subject = await RenderFieldAsync(
            scope, TemplateContentFields.Subject, content.Subject, variables, cancellationToken);
        if (subject.IsFailure)
        {
            return subject.AsFailure<string?, RenderedForm>();
        }

        Result<string?> body = await RenderFieldAsync(
            scope, TemplateContentFields.Body, content.Body, variables, cancellationToken);
        if (body.IsFailure)
        {
            return body.AsFailure<string?, RenderedForm>();
        }

        Result<string?> bodyText = await RenderFieldAsync(
            scope, TemplateContentFields.BodyText, content.BodyText, variables, cancellationToken);
        if (bodyText.IsFailure)
        {
            return bodyText.AsFailure<string?, RenderedForm>();
        }

        // The subject never wraps: the layout only frames the body, and the
        // text variant only when the layout ships a text wrapper of its own.
        var wrappedBody = body.Value!;
        var wrappedBodyText = bodyText.Value;
        if (wrapper is not null)
        {
            Result<string> framed = await WrapInLayoutAsync(
                scope, TemplateContentFields.Body, wrapper.Body, wrappedBody, cancellationToken);
            if (framed.IsFailure)
            {
                return framed.AsFailure<string, RenderedForm>();
            }

            wrappedBody = framed.Value!;
            if (wrappedBodyText is not null && wrapper.BodyText is not null)
            {
                Result<string> framedText = await WrapInLayoutAsync(
                    scope, TemplateContentFields.BodyText, wrapper.BodyText, wrappedBodyText, cancellationToken);
                if (framedText.IsFailure)
                {
                    return framedText.AsFailure<string, RenderedForm>();
                }

                wrappedBodyText = framedText.Value!;
            }
        }

        // Normalizing, banning, guarding and hashing are one decision taken in
        // one order, and it lives in the policy so this path and the preview
        // cannot take it differently. The refusal travels bare here: the
        // consuming module compares the whole error text against the word.
        Result<RenderedOutput> output = RenderedOutputPolicy.Apply(
            template,
            channel,
            new RenderedFields(subject.Value, wrappedBody, wrappedBodyText),
            RefusalShape.Bare,
            ban);
        if (output.IsFailure)
        {
            return output.AsFailure<RenderedOutput, RenderedForm>();
        }

        RenderedOutput rendered = output.Value!;
        return Result.Success(new RenderedForm(
            rendered.Subject,
            rendered.Body,
            rendered.BodyText,
            rendered.ContentHash));
    }

    /// <summary>
    /// Resolves the layout content the pinned layout version provides for the
    /// rendered channel and the locale the template resolution landed on,
    /// following the layout's own fallback chain.
    /// <para>
    /// The identity answers before the pinned version, and that order is part
    /// of the rule: a layout somebody took out of service refuses the render
    /// without paying for a read of the version it pins, and without leaving
    /// behind a never-expiring entry for a layout just refused.
    /// </para>
    /// </summary>
    private async Task<Result<LayoutWrapper?>> ResolveLayoutWrapperAsync(
        TemplateVersion version,
        Channel channel,
        Locale resolved,
        CancellationToken cancellationToken)
    {
        if (version.LayoutKey is not string layoutKey)
        {
            return Result.Success<LayoutWrapper?>(null);
        }

        var pinnedNumber = version.LayoutVersion!.Value;
        var key = LayoutKey.Trusted(layoutKey);
        Result<LayoutIdentity> identity = await ResolveLayoutIdentityAsync(key, cancellationToken);
        if (identity.IsFailure)
        {
            return identity.AsFailure<LayoutIdentity, LayoutWrapper?>();
        }

        // A deprecated layout still frames what already pins it: deprecation
        // says the layout takes no new reference, and what this asks is
        // whether the wrapper may still frame a message. Disablement is the
        // terminal state of the identity, and it answers no for every template
        // that pins it, whatever its class: rendering the body unframed would
        // ship a canonical hash that matches nothing anyone approved, and a
        // layout is disabled precisely when its own text must stop going out.
        LayoutIdentity layout = identity.Value!;
        if (!layout.Status.FramesMessages())
        {
            logger.DisabledLayoutRefused(layoutKey, pinnedNumber);

            // The bare word, never a formatted payload: the consuming module
            // compares the error text against it for equality, and anything
            // wrapped around it collapses this refusal into a render failure.
            return Result.BusinessRuleViolation<LayoutWrapper?>(LayoutRejectionReasons.Disabled);
        }

        var cacheKey = PublishedPointerKeys.LayoutVersion(layoutKey, pinnedNumber);
        if (!cache.TryGetImmutable(cacheKey, out LayoutVersion pinnedLayout))
        {
            LayoutVersion? pinned = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key)
                .FirstOrDefaultAsync(candidate => candidate.Version == pinnedNumber, cancellationToken);
            if (pinned is null)
            {
                return Result.NotFound<LayoutWrapper?>(DomainError.Format(
                    ErrorCodes.LayoutVersionNotFound,
                    $"The version pins layout '{layoutKey}' version {pinnedNumber}, which does not exist."));
            }

            // A pinned layout version is published and immutable: memoize it
            // without expiration. Nothing the governance can still move
            // travels inside this entry any more.
            pinnedLayout = pinned;
            cache.SetImmutable(cacheKey, pinnedLayout);
        }

        var channelContents = pinnedLayout.Contents
            .Where(candidate => candidate.Channel == channel)
            .ToList();
        var availableLocales = channelContents.Select(candidate => candidate.Locale).ToList();
        Locale? layoutLocale = LocaleResolution.Resolve(resolved, availableLocales, layout.DefaultLocale);
        if (layoutLocale is null)
        {
            return Result.NotFound<LayoutWrapper?>(DomainError.Format(
                ErrorCodes.LayoutContentNotFound,
                $"Layout '{layoutKey}' version {pinnedNumber} has no content that resolves "
                + $"for ({channel.Value}, {resolved.Value})."));
        }

        LayoutContent content = channelContents.First(candidate => candidate.Locale == layoutLocale);
        return Result.Success<LayoutWrapper?>(new LayoutWrapper(content.Body, content.BodyText));
    }

    /// <summary>
    /// The status of a layout identity and the default locale its fallback
    /// chain lands on, memoized as a "current published" pointer. Both come
    /// off the same mutable row, so neither belongs in the entry of a pinned
    /// version, which never expires: a status held there would answer with
    /// "active" for the life of the process after an operator disabled the
    /// layout. Nothing moves the default locale today, and it travels here all
    /// the same, because holding it in the entry that never expires arms the
    /// trap for the day somebody adds a way to move it.
    /// <para>
    /// The entry holds the fact and not the decision the render takes from it,
    /// unlike the catalog next door: this same entry also answers for the
    /// locale, which decides nothing.
    /// </para>
    /// </summary>
    private async Task<Result<LayoutIdentity>> ResolveLayoutIdentityAsync(
        LayoutKey key,
        CancellationToken cancellationToken)
    {
        var cacheKey = PublishedPointerKeys.LayoutIdentity(key.Value);
        if (cache.TryGetPointer(cacheKey, out LayoutIdentity cached))
        {
            return Result.Success(cached);
        }

        // The fence is read before the query leaves: a disable or a deprecation
        // that commits while this load is in flight refuses the write below
        // instead of having the previous status put back on top of it.
        var generation = cache.Generation;
        Layout? layout = await dbContext.Layouts
            .AsNoTracking()
            .WhereKey(key)
            .FirstOrDefaultAsync(cancellationToken);
        if (layout is null)
        {
            // A pinned version whose identity is gone is a broken invariant,
            // and a status nobody can read may never pass for an active one.
            return Result.NotFound<LayoutIdentity>(DomainError.Format(
                ErrorCodes.LayoutNotFound,
                $"The version pins layout '{key.Value}', whose identity does not exist."));
        }

        var identity = new LayoutIdentity(layout.Status, layout.DefaultLocale);
        cache.SetPointerIfCurrent(cacheKey, identity, generation);
        return Result.Success(identity);
    }

    /// <summary>
    /// Renders the layout wrapper with the already-rendered template field
    /// exposed as the single <c>content</c> variable: the layout sees no
    /// template variable and no template source, only the finished text.
    /// </summary>
    private async Task<Result<string>> WrapInLayoutAsync(
        ScribanTemplateEngine.FormRenderScope scope,
        string field,
        string layoutSource,
        string renderedContent,
        CancellationToken cancellationToken)
    {
        Result<string> wrapped = await engine.RenderContentAsync(
            scope,
            layoutSource,
            LayoutValidation.ContentPlaceholderVariable,
            renderedContent,
            cancellationToken);
        return wrapped.IsFailure
            ? Result.ValidationError<string>(DomainError.Format(
                ErrorCodes.TemplateRenderFailed,
                $"Layout wrapper for field '{field}': {wrapped.Error}"))
            : wrapped;
    }

    private async Task<Result<string?>> RenderFieldAsync(
        ScribanTemplateEngine.FormRenderScope scope,
        string field,
        string? source,
        JsonElement? variables,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(source))
        {
            return Result.Success<string?>(null);
        }

        Result<string> rendered = await engine.RenderAsync(scope, source, variables, cancellationToken);
        return rendered.IsFailure
            ? Result.ValidationError<string?>(DomainError.Format(
                ErrorCodes.TemplateRenderFailed,
                $"Field '{field}': {rendered.Error}"))
            : Result.Success<string?>(rendered.Value);
    }

    /// <summary>Layout sources that frame the rendered body and, optionally, the text variant.</summary>
    private sealed record LayoutWrapper(string Body, string? BodyText);
}

/// <summary>
/// The half of a layout that the governance still moves after a version is
/// pinned: the status of the identity and the default locale its fallback
/// chain lands on. It travels apart from the pinned version because the row it
/// comes from is mutable, and it is memoized as a "current published" pointer
/// so a layout an operator disables stops framing messages within the same
/// window a new publication takes to reach a worker.
/// </summary>
internal sealed record LayoutIdentity(LayoutStatus Status, Locale? DefaultLocale);
