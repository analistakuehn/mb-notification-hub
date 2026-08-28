using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class CreateLayoutVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<LayoutKey> key = LayoutKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<LayoutKey, Response>();
            }

            if (command.FromVersion is < 1)
            {
                return Result.ValidationError<Response>(DomainError.Format(
                    ErrorCodes.InvalidRequest,
                    "fromVersion must be a positive version number."));
            }

            var layoutExists = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(key.Value!)
                .AnyAsync(cancellationToken);
            if (!layoutExists)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{key.Value!.Value}' does not exist."));
            }

            var openDraft = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key.Value!)
                .Where(version => version.Status == LayoutVersionStatus.Draft)
                .Select(version => (int?)version.Version)
                .FirstOrDefaultAsync(cancellationToken);
            if (openDraft is not null)
            {
                return DraftConflict(key.Value!, openDraft.Value);
            }

            var nextVersion = (await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key.Value!)
                .MaxAsync(version => (int?)version.Version, cancellationToken) ?? 0) + 1;

            LayoutVersion draft;
            if (command.FromVersion is int fromVersion)
            {
                LayoutVersion? source = await dbContext.LayoutVersions
                    .AsNoTracking()
                    .WhereLayoutKey(key.Value!)
                    .FirstOrDefaultAsync(version => version.Version == fromVersion, cancellationToken);
                if (source is null)
                {
                    return Result.NotFound<Response>(DomainError.Format(
                        ErrorCodes.LayoutVersionNotFound,
                        $"Layout '{key.Value!.Value}' has no version {fromVersion} to clone from."));
                }

                draft = LayoutVersion.CreateDraftFrom(source, nextVersion, command.Actor, timeProvider.GetUtcNow());
            }
            else
            {
                draft = LayoutVersion.CreateDraft(key.Value!, nextVersion, command.Actor, timeProvider.GetUtcNow());
            }

            dbContext.LayoutVersions.Add(draft);
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
                logger.LayoutDraftCloned(draft.LayoutKey.Value, draft.Version, clonedFrom);
            }
            else
            {
                logger.LayoutDraftOpened(draft.LayoutKey.Value, draft.Version);
            }

            return Result.Success(Response.From(draft));
        }

        private static Result<Response> DraftConflict(LayoutKey key, int draftVersion)
            => Result.BusinessRuleViolation<Response>(DomainError.Format(
                ErrorCodes.DraftAlreadyExists,
                $"Layout '{key.Value}' already has draft version {draftVersion}. "
                + "Edit or discard it before opening a new draft."));
    }
}
