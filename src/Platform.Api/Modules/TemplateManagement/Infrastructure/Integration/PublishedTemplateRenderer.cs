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
/// sandboxed engine: locale fallback chain, URL variables enforced against the
/// template allowlist, pinned layout wrapped around the body, and, on demand,
/// the masked form rendered with every sensitive variable masked. Each form
/// carries the canonical hash of exactly the fields it shipped.
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

        Result urlGuard = EnforceUrlVariables(template, version, request.Variables);
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
            channel.Value!, content, request.Variables, wrapper.Value, cancellationToken);
        if (full.IsFailure)
        {
            return full.AsFailure<RenderedForm, PublishedTemplateRender>();
        }

        if (CarriesAuthenticationSmsLink(template, channel.Value!, full.Value!))
        {
            // Alarm, not a note: publication already refuses this shape, so a
            // render that produces one means the link arrived through a
            // variable value at request time. The message never leaves.
            logger.AuthenticationSmsLinkRefused(
                request.Application, request.TemplateKey, version.Version);
            return Result.ValidationError<PublishedTemplateRender>(
                TemplateValidation.AuthenticationSmsLinkCode);
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

        return await RenderFormAsync(channel, content, masked.Value, wrapper, cancellationToken);
    }

    /// <summary>
    /// Whether this render puts something clickable inside an authentication
    /// SMS. One authentication code is the price of a false positive here; a
    /// false negative is a phishing link inside the one message people are
    /// trained to act on without thinking.
    /// </summary>
    private static bool CarriesAuthenticationSmsLink(Template template, Channel channel, RenderedForm form)
        => channel == Channel.Sms
            && string.Equals(
                template.Purpose, TemplateValidation.AuthenticationPurpose, StringComparison.Ordinal)
            && (TemplateValidation.ContainsLinkLikeText(form.Body)
                || TemplateValidation.ContainsLinkLikeText(form.Subject)
                || TemplateValidation.ContainsLinkLikeText(form.BodyText));

    private async Task<Result<RenderedForm>> RenderFormAsync(
        Channel channel,
        TemplateContent content,
        JsonElement? variables,
        LayoutWrapper? wrapper,
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

        // Normalization comes before the hash, and that order is the whole
        // point: the audited hash has to describe the bytes the provider
        // receives, so normalizing afterwards would break the equality the
        // audit checks and leave every SMS looking tampered with.
        var normalizedSubject = subject.Value;
        var normalizedBody = wrappedBody;
        var normalizedBodyText = wrappedBodyText;
        if (channel == Channel.Sms)
        {
            normalizedSubject = normalizedSubject is null
                ? null
                : SmsContentNormalizer.Normalize(normalizedSubject);
            normalizedBody = SmsContentNormalizer.Normalize(normalizedBody);
            normalizedBodyText = normalizedBodyText is null
                ? null
                : SmsContentNormalizer.Normalize(normalizedBodyText);
        }

        return Result.Success(new RenderedForm(
            normalizedSubject,
            normalizedBody,
            normalizedBodyText,
            CanonicalHash.OfFields(normalizedSubject, normalizedBody, normalizedBodyText)));
    }

    /// <summary>
    /// Resolves the layout content the pinned layout version provides for the
    /// rendered channel and the locale the template resolution landed on,
    /// following the layout's own fallback chain.
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
        var cacheKey = $"layout:{layoutKey}:{pinnedNumber}";
        if (!cache.TryGetImmutable(cacheKey, out PinnedLayout pinnedLayout))
        {
            var key = LayoutKey.Trusted(layoutKey);
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

            Layout? layout = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(key)
                .FirstOrDefaultAsync(cancellationToken);

            // A pinned layout version is published and immutable: memoize it
            // without expiration.
            pinnedLayout = new PinnedLayout(pinned, layout?.DefaultLocale);
            cache.SetImmutable(cacheKey, pinnedLayout);
        }

        var channelContents = pinnedLayout.Version.Contents
            .Where(candidate => candidate.Channel == channel)
            .ToList();
        var availableLocales = channelContents.Select(candidate => candidate.Locale).ToList();
        Locale? layoutLocale = LocaleResolution.Resolve(resolved, availableLocales, pinnedLayout.DefaultLocale);
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

    private static Result EnforceUrlVariables(Template template, TemplateVersion version, JsonElement? variables)
    {
        if (!VariablesSchema.TryParse(version.VariablesSchemaJson, out IReadOnlyList<VariableDeclaration> declarations))
        {
            return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.TemplateRenderFailed,
                "The variables schema is not readable; run the validation report."));
        }

        if (variables is not { ValueKind: JsonValueKind.Object } provided)
        {
            return Result.Success();
        }

        foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.IsUrl))
        {
            if (!provided.TryGetProperty(declaration.Name, out JsonElement value))
            {
                continue;
            }

            if (!LinkDomainPolicy.IsAllowedUrlValue(template, value))
            {
                // The value never travels in the error: it may embed tokens
                // or personal data in the query string.
                return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                    ErrorCodes.UrlDomainNotAllowed,
                    $"Variable '{declaration.Name}' must be an absolute http(s) URL "
                    + "inside the template's allowed domains."));
            }
        }

        var offending = LinkDomainPolicy.FirstDisallowedHost(variables, template);
        if (offending is not null)
        {
            // Only the host travels in the error, never the value: the
            // query string may carry a token or personal data. Same reason
            // as the loop above.
            return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.UrlDomainNotAllowed,
                $"A variable value carries link host '{offending}', "
                + "which is outside the template's allowed domains."));
        }

        return Result.Success();
    }

    /// <summary>Layout sources that frame the rendered body and, optionally, the text variant.</summary>
    private sealed record LayoutWrapper(string Body, string? BodyText);

    /// <summary>One pinned layout version with the identity's default locale, memoized without expiration.</summary>
    private sealed record PinnedLayout(LayoutVersion Version, Locale? DefaultLocale);
}
