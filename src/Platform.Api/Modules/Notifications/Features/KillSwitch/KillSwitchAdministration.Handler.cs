using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

internal static partial class KillSwitchAdministration
{
    /// <summary>
    /// One transition of one switch. <paramref name="ActorType" /> and
    /// <paramref name="Reason" /> exist because the hub itself can stop a
    /// channel: the row, the transition and the audit action stay the same, so
    /// an operator reading the trail sees one vocabulary, and these two members
    /// are what tell them a person did not do it and what the hub saw. The
    /// defaults describe the human path, which is the only one that ever turns
    /// a switch back off.
    /// </summary>
    internal sealed record ChangeCommand(
        KillSwitchScope Scope,
        string Key,
        bool Active,
        string Actor,
        string ActorType = AuditActorTypes.User,
        string? Reason = null);

    internal sealed record ChangeResult(
        string State,
        long Version,
        DateTimeOffset? UpdatedAt,
        bool Changed,
        bool Conflict = false);

    internal sealed class Handler(
        NotificationsDbContext db,
        IAuditTrail auditTrail,
        KillSwitchCache cache,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<ChangeResult>> HandleAsync(
            ChangeCommand command,
            CancellationToken cancellationToken)
        {
            var scope = command.Scope.Canonical();
            KillSwitchState? current = await db.KillSwitches.SingleOrDefaultAsync(
                entry => entry.Scope == scope && entry.Key == command.Key,
                cancellationToken);
            if (current is null && !command.Active)
            {
                // A command that changes nothing writes no audit entry, so this
                // line is the only record that somebody addressed the control.
                // It is worth having: an operator turning off a switch that was
                // never on believes traffic is being held that is not.
                logger.KillSwitchUnchanged(
                    scope, command.Key, KillSwitchStates.Inactive, command.Actor);
                return Result.Success<ChangeResult>(new ChangeResult(
                    KillSwitchStates.Inactive,
                    Version: 0,
                    UpdatedAt: null,
                    Changed: false));
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            string before;
            string after;
            if (current is null)
            {
                before = KillSwitchStates.Inactive;
                after = KillSwitchStates.Active;
                current = KillSwitchState.Activate(command.Scope, command.Key, command.Actor, now);
                db.KillSwitches.Add(current);
            }
            else
            {
                KillSwitchTransition? transition = current.Change(
                    command.Active,
                    command.Actor,
                    secondActor: null,
                    now);
                if (transition is null)
                {
                    logger.KillSwitchUnchanged(
                        scope, command.Key, current.State, command.Actor);
                    return Result.Success<ChangeResult>(new ChangeResult(
                        current.State,
                        current.Version,
                        current.UpdatedAt,
                        Changed: false));
                }

                before = transition.Before;
                after = transition.After;
            }

            await using IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await auditTrail.AppendAsync(
                    transaction.GetDbTransaction(),
                    AuditEntry(command, before, after, now),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            // Both collisions come back as a successful result carrying
            // Conflict, and that stays. A concurrency conflict is not a domain
            // failure: nothing was asked that this hub refuses, another actor
            // simply reached the same switch first, and the answer is read the
            // state again and retry. This module carries a single error axis
            // and decodes it at its edge, which is where the conflict becomes
            // the 409 the caller acts on; folding it into the failure axis
            // would make the transport spell the difference out a second time.
            // What was missing here was never the contract, it was the line:
            // a stop that did not happen on the control that compensates the
            // rate limit failing open is exactly what nobody may discover from
            // the absence of a record.
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.KillSwitchConcurrencyConflict(scope, command.Key, command.Actor);
                return Result.Success<ChangeResult>(
                    new ChangeResult(before, 0, null, Changed: false, Conflict: true));
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                })
            {
                await transaction.RollbackAsync(cancellationToken);

                // Its own event: this one can only be reached on the insert
                // path, so it says two administrators created the same switch
                // at once, while the one above says somebody changed a switch
                // that already existed.
                logger.KillSwitchUniqueViolationConflict(scope, command.Key, command.Actor);
                return Result.Success<ChangeResult>(
                    new ChangeResult(before, 0, null, Changed: false, Conflict: true));
            }

            cache.Invalidate();
            LogTransition(logger, scope, command, after);
            return Result.Success<ChangeResult>(
                new ChangeResult(current.State, current.Version, current.UpdatedAt, Changed: true));
        }

        /// <summary>
        /// One line per effected transition, split by direction because the two
        /// are different events to whoever watches them: one stops traffic, the
        /// other resumes traffic that somebody stopped for a reason, possibly a
        /// reason this hub found on its own and has not re-checked.
        /// </summary>
        private static void LogTransition(
            ILogger logger,
            string scope,
            ChangeCommand command,
            string after)
        {
            if (after == KillSwitchStates.Active)
            {
                logger.KillSwitchActivated(scope, command.Key, command.Actor);
            }
            else
            {
                logger.KillSwitchDeactivated(scope, command.Key, command.Actor);
            }
        }

        private static AuditEntry AuditEntry(
            ChangeCommand command,
            string before,
            string after,
            DateTimeOffset now)
            => new()
            {
                ActorType = command.ActorType,
                ActorId = command.Actor,
                Application = command.Scope == KillSwitchScope.Application ? command.Key : null,
                Action = "kill_switch.changed",
                EntityType = "kill_switch",
                EntityId = $"{command.Scope.Canonical()}:{command.Key}",
                DetailsJson = JsonSerializer.Serialize(Details(command, before, after, now)),
                OccurredAt = now,
            };

        /// <summary>
        /// The trail of one transition. The reason joins only when there is
        /// one: a human transition carries its justification outside the hub,
        /// and an empty field would read as a missing one.
        /// </summary>
        private static object Details(
            ChangeCommand command,
            string before,
            string after,
            DateTimeOffset now)
            => command.Reason is { } reason
                ? new
                {
                    before,
                    after,
                    scope = command.Scope.Canonical(),
                    key = command.Key,
                    actor = command.Actor,
                    reason,
                    instant = now,
                }
                : new
                {
                    before,
                    after,
                    scope = command.Scope.Canonical(),
                    key = command.Key,
                    actor = command.Actor,
                    instant = now,
                };
    }
}
