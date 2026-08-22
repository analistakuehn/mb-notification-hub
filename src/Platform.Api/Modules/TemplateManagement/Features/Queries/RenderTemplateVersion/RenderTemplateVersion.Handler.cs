using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

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

            Result urlGuard = EnforceUrlVariables(template, version, request.Variables);
            if (urlGuard.IsFailure)
            {
                return urlGuard.AsFailure<Response>();
            }

            TemplateContent content = channelContents.First(candidate => candidate.Locale == resolved);
            Result<Response> rendered = await RenderContentAsync(
                content, locale.Value!, resolved, request.Variables, cancellationToken);
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
            TemplateContent content,
            Locale requested,
            Locale resolved,
            JsonElement? variables,
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

            return Result.Success(new Response(
                content.Channel.Value,
                requested.Value,
                resolved.Value,
                subject.Value,
                body.Value!,
                bodyText.Value));
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

                if (!IsAllowedUrl(template, value))
                {
                    // The value never travels in the error: it may embed tokens
                    // or personal data in the query string.
                    return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                        ErrorCodes.UrlDomainNotAllowed,
                        $"Variable '{declaration.Name}' must be an absolute http(s) URL "
                        + "inside the template's allowed domains."));
                }
            }

            return Result.Success();
        }

        private static bool IsAllowedUrl(Template template, JsonElement value)
            => value.ValueKind == JsonValueKind.String
                && Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                && template.IsLinkDomainAllowed(url.Host);
    }
}
