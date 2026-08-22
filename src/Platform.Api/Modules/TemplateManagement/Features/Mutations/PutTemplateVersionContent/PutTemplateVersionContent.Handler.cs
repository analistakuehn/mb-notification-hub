using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutTemplateVersionContent
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<TemplateKey> key = TemplateKey.Create(command.Route.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<TemplateKey, Response>();
            }

            Result<Channel> channel = Channel.Create(command.Route.Channel);
            if (channel.IsFailure)
            {
                return channel.AsFailure<Channel, Response>();
            }

            Result<Locale> locale = Locale.Create(command.Route.Locale);
            if (locale.IsFailure)
            {
                return locale.AsFailure<Locale, Response>();
            }

            TemplateVersion? version = await dbContext.TemplateVersions
                .WhereTemplateKey(key.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == command.Route.Version, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateVersionNotFound,
                    $"Template '{key.Value!.Value}' has no version {command.Route.Version}."));
            }

            Result precondition = EntityTags.CheckIfMatch(command.Route.IfMatch, version.EntityTag);
            if (precondition.IsFailure)
            {
                return precondition.AsFailure<Response>();
            }

            Result edited = version.SetContent(
                new ContentEdit(
                    channel.Value!,
                    locale.Value!,
                    command.Content.Subject,
                    command.Content.Body,
                    command.Content.BodyText),
                command.Actor);
            if (edited.IsFailure)
            {
                return edited.AsFailure<Response>();
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Response>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The version changed while this edit was in flight. "
                    + "Fetch it again and retry with the current entity tag."));
            }

            logger.ContentUpdated(version.TemplateKey.Value, version.Version, channel.Value!.Value, locale.Value!.Value);
            return Result.Success(Response.From(version));
        }
    }
}
