using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplateVersion
{
    internal sealed partial class Handler(
        TemplateManagementDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<TemplateKey> key = TemplateKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<TemplateKey, Response>();
            }

            if (command.FromVersion is < 1)
            {
                return Result.ValidationError<Response>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "fromVersion must be a positive version number."));
            }

            bool templateExists = await dbContext.Templates
                .AsNoTracking()
                .WhereKey(key.Value!)
                .AnyAsync(cancellationToken);
            if (!templateExists)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{key.Value!.Value}' does not exist."));
            }

            int? openDraft = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(key.Value!)
                .Where(version => version.Status == TemplateVersionStatus.Draft)
                .Select(version => (int?)version.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (openDraft is not null)
            {
                return DraftConflict(key.Value!, openDraft.Value);
            }

            int nextVersion = (await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(key.Value!)
                .MaxAsync(version => (int?)version.Version, cancellationToken) ?? 0) + 1;

            TemplateVersion draft;
            if (command.FromVersion is int fromVersion)
            {
                TemplateVersion? source = await dbContext.TemplateVersions
                    .AsNoTracking()
                    .WhereTemplateKey(key.Value!)
                    .FirstOrDefaultAsync(version => version.Version == fromVersion, cancellationToken);
                if (source is null)
                {
                    return Result.NotFound<Response>(DomainError.Format(
                        ErrorCodes.TemplateVersionNotFound,
                        $"Template '{key.Value!.Value}' has no version {fromVersion} to clone from."));
                }

                draft = TemplateVersion.CreateDraftFrom(source, nextVersion, command.Actor, timeProvider.GetUtcNow());
            }
            else
            {
                draft = TemplateVersion.CreateDraft(key.Value!, nextVersion, command.Actor, timeProvider.GetUtcNow());
            }

            dbContext.TemplateVersions.Add(draft);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return DraftConflict(key.Value!, openDraft ?? nextVersion);
            }

            if (command.FromVersion is int clonedFrom)
            {
                DraftCloned(draft.TemplateKey.Value, draft.Version, clonedFrom);
            }
            else
            {
                DraftOpened(draft.TemplateKey.Value, draft.Version);
            }

            return Result.Success(Response.From(draft));
        }

        private static Result<Response> DraftConflict(TemplateKey key, int draftVersion)
            => Result.BusinessRuleViolation<Response>(DomainError.Format(
                ErrorCodes.DraftAlreadyExists,
                $"Template '{key.Value}' already has draft version {draftVersion}. "
                + "Edit or discard it before opening a new draft."));
    }
}
