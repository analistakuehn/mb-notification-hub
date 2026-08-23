using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateLayout
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        IAuditTrail auditTrail,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            Command command,
            string actor,
            CancellationToken cancellationToken)
        {
            Result<LayoutKey> key = LayoutKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<LayoutKey, Response>();
            }

            Locale? defaultLocale = null;
            if (command.DefaultLocale is not null)
            {
                Result<Locale> parsed = Locale.Create(command.DefaultLocale);
                if (parsed.IsFailure)
                {
                    return parsed.AsFailure<Locale, Response>();
                }

                defaultLocale = parsed.Value;
            }

            Result<Layout> layout = Layout.Create(key.Value!, new LayoutMetadata
            {
                OwnerTeam = command.OwnerTeam,
                DefaultLocale = defaultLocale,
            });
            if (layout.IsFailure)
            {
                return layout.AsFailure<Layout, Response>();
            }

            var exists = await dbContext.Layouts
                .AsNoTracking()
                .WhereKey(key.Value!)
                .AnyAsync(cancellationToken);
            if (exists)
            {
                return Conflict(key.Value!);
            }

            dbContext.Layouts.Add(layout.Value!);
            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = actor,
                Action = AuditActions.LayoutCreated,
                EntityType = AuditEntityTypes.Layout,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ownerTeam = layout.Value!.OwnerTeam,
                    defaultLocale = layout.Value!.DefaultLocale?.Value,
                }),
                OccurredAt = timeProvider.GetUtcNow(),
            };

            // One database transaction shared with the audit contract: the new
            // layout and its audit event land together or not at all.
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await auditTrail.AppendAsync(transaction.GetDbTransaction(), entry, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Conflict(key.Value!);
            }

            var response = Response.From(layout.Value!);
            logger.LayoutCreated(response.Key, response.OwnerTeam);
            return Result.Success(response);
        }

        private static Result<Response> Conflict(LayoutKey key)
            => Result.BusinessRuleViolation<Response>(DomainError.Format(
                ErrorCodes.LayoutAlreadyExists,
                $"A layout with key '{key.Value}' already exists."));
    }
}
