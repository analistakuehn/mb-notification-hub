using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RenderTemplateVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        ScribanTemplateEngine engine,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            string key,
            int versionNumber,
            Request request,
            CancellationToken cancellationToken)
        {
            Result<TemplateKey> templateKey = TemplateKey.Create(key);
            if (templateKey.IsFailure)
            {
                return templateKey.AsFailure<TemplateKey, Response>();
            }

            Result<Channel> channel = Channel.Create(request.Channel);
            if (channel.IsFailure)
            {
                return channel.AsFailure<Channel, Response>();
            }

            Result<Locale> locale = Locale.Create(request.Locale);
            if (locale.IsFailure)
            {
                return locale.AsFailure<Locale, Response>();
            }

            Template? template = await dbContext.Templates
                .AsNoTracking()
                .WhereKey(templateKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{templateKey.Value!.Value}' does not exist."));
            }

            // Nothing here reads the status of the template identity, and that
            // is the line this preview draws. It renders drafts by definition,
            // which is to say things dispatch would never send, so the goal is
            // not "preview equals dispatch": it is that the preview never
            // approves what dispatch refuses on account of the content. A
            // deprecated or a disabled template is an identity decision, not
            // content, and it is still being edited by whoever is preparing the
            // next version. A disabled layout is the other case, and it is
            // refused below: its text has to stop going out, and a body without
            // its frame carries a hash nobody approved.
            TemplateVersion? version = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(templateKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateVersionNotFound,
                    $"Template '{templateKey.Value!.Value}' has no version {versionNumber}."));
            }

            var channelContents = version.Contents
                .Where(content => content.Channel == channel.Value)
                .ToList();
            if (channelContents.Count == 0)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateContentNotFound,
                    $"Version {versionNumber} of template '{templateKey.Value!.Value}' has no content "
                    + $"for channel '{channel.Value!.Value}'."));
            }

            var availableLocales = channelContents.Select(content => content.Locale).ToList();
            Locale? resolved = LocaleResolution.Resolve(locale.Value!, availableLocales, template.DefaultLocale);
            if (resolved is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateContentNotFound,
                    $"Locale '{locale.Value!.Value}' does not resolve for channel '{channel.Value!.Value}': "
                    + "no exact match, no base-language content and no default-locale content."));
            }

            Result urlGuard = VariablesDestinationPolicy.Validate(template, version, request.Variables);
            if (urlGuard.IsFailure)
            {
                return urlGuard.AsFailure<Response>();
            }

            Result<LayoutWrapper?> wrapper = await ResolveLayoutWrapperAsync(
                version, channel.Value!, resolved, cancellationToken);
            if (wrapper.IsFailure)
            {
                return wrapper.AsFailure<LayoutWrapper?, Response>();
            }

            TemplateContent content = channelContents.First(candidate => candidate.Locale == resolved);
            Result<Response> rendered = await RenderContentAsync(
                template, content, locale.Value!, resolved, request.Variables, wrapper.Value, cancellationToken);
            if (rendered.IsSuccess)
            {
                logger.VersionRendered(
                    version.TemplateKey.Value,
                    version.Version,
                    channel.Value!.Value,
                    resolved.Value);
            }

            return rendered;
        }

        private async Task<Result<Response>> RenderContentAsync(
            Template template,
            TemplateContent content,
            Locale requested,
            Locale resolved,
            JsonElement? variables,
            LayoutWrapper? wrapper,
            CancellationToken cancellationToken)
        {
            Result<string?> subject = await RenderFieldAsync(
                TemplateContentFields.Subject, content.Subject, variables, cancellationToken);
            if (subject.IsFailure)
            {
                return subject.AsFailure<string?, Response>();
            }

            Result<string?> body = await RenderFieldAsync(
                TemplateContentFields.Body, content.Body, variables, cancellationToken);
            if (body.IsFailure)
            {
                return body.AsFailure<string?, Response>();
            }

            Result<string?> bodyText = await RenderFieldAsync(
                TemplateContentFields.BodyText, content.BodyText, variables, cancellationToken);
            if (bodyText.IsFailure)
            {
                return bodyText.AsFailure<string?, Response>();
            }

            // The subject never wraps: the layout only frames the body, and the
            // text variant only when the layout ships a text wrapper of its own.
            var wrappedBody = body.Value!;
            var wrappedBodyText = bodyText.Value;
            if (wrapper is not null)
            {
                Result<string> framed = await WrapInLayoutAsync(
                    TemplateContentFields.Body, wrapper.Body, wrappedBody, cancellationToken);
                if (framed.IsFailure)
                {
                    return framed.AsFailure<string, Response>();
                }

                wrappedBody = framed.Value!;
                if (wrappedBodyText is not null && wrapper.BodyText is not null)
                {
                    Result<string> framedText = await WrapInLayoutAsync(
                        TemplateContentFields.BodyText, wrapper.BodyText, wrappedBodyText, cancellationToken);
                    if (framedText.IsFailure)
                    {
                        return framedText.AsFailure<string, Response>();
                    }

                    wrappedBodyText = framedText.Value!;
                }
            }

            // Normalizing, banning, guarding and hashing are one decision taken
            // in one order, and the dispatch path takes exactly the same one.
            // The refusal is formatted here because a person reads this
            // response, while over there it travels bare so a sibling module
            // can compare the whole error text against the word.
            Result<RenderedOutput> output = RenderedOutputPolicy.Apply(
                template,
                content.Channel,
                new RenderedFields(subject.Value, wrappedBody, wrappedBodyText),
                RefusalShape.Formatted,
                AuthenticationLinkBan.Enforce);
            if (output.IsFailure)
            {
                return output.AsFailure<RenderedOutput, Response>();
            }

            // The hash the policy computed stops here. It exists so an audit can
            // compare a stored render against a fresh one, and a preview stores
            // nothing; this response does not even carry the version that
            // answered, so a hash on it would be a number with no subject.
            RenderedOutput rendered = output.Value!;
            return Result.Success(new Response(
                content.Channel.Value,
                requested.Value,
                resolved.Value,
                rendered.Subject,
                rendered.Body,
                rendered.BodyText));
        }

        /// <summary>
        /// Resolves the layout content the pinned layout version provides for
        /// the rendered channel and the locale the template resolution landed
        /// on, following the layout's own fallback chain.
        /// <para>
        /// The identity answers before the pinned version, for the same reason
        /// the published render asks it first: whichever version the author
        /// picks, a layout out of service gives the one answer that helps.
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

            var key = LayoutKey.Trusted(layoutKey);
            var pinnedNumber = version.LayoutVersion!.Value;

            // Read fresh on every preview, with nothing memoized behind it.
            // The published read holds the identity for a window because it
            // sits on the dispatch path and has throughput to protect; a
            // preview has none, and an author who has just disabled a layout
            // has to see the refusal now instead of a minute from now.
            Layout? layout = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(key)
                .FirstOrDefaultAsync(cancellationToken);

            // Second implementation of one rule: PublishedTemplateRenderer
            // refuses the same status with the same word, and the two have to
            // agree, because a preview that frames what the dispatch refuses
            // reads to the author as proof that everything is in order. The
            // payload is the one difference, and it is deliberate: a person
            // reads this one, so the word travels as the code of a typed
            // problem alongside a sentence, while over there it travels bare
            // because a sibling module compares it for equality.
            if (layout is not null && !layout.Status.FramesMessages())
            {
                return Result.BusinessRuleViolation<LayoutWrapper?>(DomainError.Format(
                    LayoutRejectionReasons.Disabled,
                    $"Layout '{layoutKey}' is disabled and frames no message any more."));
            }

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

            var channelContents = pinned.Contents
                .Where(candidate => candidate.Channel == channel)
                .ToList();
            var availableLocales = channelContents.Select(candidate => candidate.Locale).ToList();
            Locale? layoutLocale = LocaleResolution.Resolve(resolved, availableLocales, layout?.DefaultLocale);
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
            string field,
            string layoutSource,
            string renderedContent,
            CancellationToken cancellationToken)
        {
            JsonElement globals = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                [LayoutValidation.ContentPlaceholderVariable] = renderedContent,
            });
            Result<string> wrapped = await engine.RenderAsync(layoutSource, globals, cancellationToken);
            return wrapped.IsFailure
                ? Result.ValidationError<string>(DomainError.Format(
                    ErrorCodes.TemplateRenderFailed,
                    $"Layout wrapper for field '{field}': {wrapped.Error}"))
                : wrapped;
        }

        private async Task<Result<string?>> RenderFieldAsync(
            string field,
            string? source,
            JsonElement? variables,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(source))
            {
                return Result.Success<string?>(null);
            }

            Result<string> rendered = await engine.RenderAsync(source, variables, cancellationToken);
            return rendered.IsFailure
                ? Result.ValidationError<string?>(DomainError.Format(
                    ErrorCodes.TemplateRenderFailed,
                    $"Field '{field}': {rendered.Error}"))
                : Result.Success<string?>(rendered.Value);
        }
    }

    /// <summary>Layout sources that frame the rendered body and, optionally, the text variant.</summary>
    private sealed record LayoutWrapper(string Body, string? BodyText);
}
