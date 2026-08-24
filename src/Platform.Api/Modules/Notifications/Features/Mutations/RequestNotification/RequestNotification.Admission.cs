using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    /// <summary>
    /// Admission decisions that precede template evaluation and persistence.
    /// A remembered acceptance wins before emergency and rate-limit controls.
    /// </summary>
    internal abstract record AdmissionDecision
    {
        private AdmissionDecision()
        {
        }

        internal sealed record Replay(RememberedAcceptance Acceptance) : AdmissionDecision;

        internal sealed record ProducerDisabled : AdmissionDecision;

        internal sealed record KillSwitchUnavailable : AdmissionDecision;

        internal sealed record RateLimited(RateLimitDecision Decision) : AdmissionDecision;

        internal sealed record Allowed : AdmissionDecision;
    }

    /// <summary>
    /// Orders the ingress controls that decide whether a valid, authorized
    /// request may proceed to catalog evaluation and persistence.
    /// </summary>
    internal interface IIngressAdmission
    {
        Task<AdmissionDecision> EvaluateAsync(
            Command command,
            string producer,
            IngestionOrigin origin,
            string idempotencyKey,
            CancellationToken cancellationToken);

        Task RememberAsync(
            string application,
            string idempotencyKey,
            RememberedAcceptance acceptance,
            CancellationToken cancellationToken);
    }

    internal sealed class IngressAdmission : IIngressAdmission
    {
        private readonly IngressControls _controls;
        private readonly IKillSwitch? _killSwitch;
        private readonly IServiceScopeFactory? _scopeFactory;

        public IngressAdmission(
            IngressControls controls,
            IKillSwitch killSwitch,
            IServiceScopeFactory scopeFactory)
        {
            ArgumentNullException.ThrowIfNull(controls);
            ArgumentNullException.ThrowIfNull(killSwitch);
            ArgumentNullException.ThrowIfNull(scopeFactory);
            _controls = controls;
            _killSwitch = killSwitch;
            _scopeFactory = scopeFactory;
        }

        private IngressAdmission(IngressControls controls)
        {
            ArgumentNullException.ThrowIfNull(controls);
            _controls = controls;
        }

        /// <summary>
        /// Creates the bus composition. Its producer kill switch has already
        /// been evaluated before authorization and handler invocation.
        /// </summary>
        internal static IIngressAdmission ForBus(IngressControls controls) => new IngressAdmission(controls);

        public async Task<AdmissionDecision> EvaluateAsync(
            Command command,
            string producer,
            IngestionOrigin origin,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            RememberedAcceptance? remembered = await _controls.FindRememberedAsync(
                command.Application,
                idempotencyKey,
                cancellationToken);
            if (remembered is { } acceptance)
            {
                return new AdmissionDecision.Replay(acceptance);
            }

            if (origin.Source == IngestionSource.Rest)
            {
                RememberedAcceptance? authoritative = await FindAuthoritativeAsync(
                    command.Application,
                    idempotencyKey,
                    cancellationToken);
                if (authoritative is { } authoritativeAcceptance)
                {
                    return new AdmissionDecision.Replay(authoritativeAcceptance);
                }

                IKillSwitch killSwitch = _killSwitch ?? throw new InvalidOperationException(
                    "A composição REST da admissão exige o kill switch do producer.");
                KillSwitchEvaluation evaluation = await killSwitch.EvaluateAsync(
                    KillSwitchScope.Producer,
                    producer,
                    cancellationToken);
                switch (evaluation)
                {
                    case KillSwitchEvaluation.Blocked:
                        return new AdmissionDecision.ProducerDisabled();
                    case KillSwitchEvaluation.Unavailable:
                        return new AdmissionDecision.KillSwitchUnavailable();
                    case KillSwitchEvaluation.Allowed:
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Avaliação de kill switch desconhecida: {evaluation}.");
                }
            }

            RateLimitDecision rateDecision = await _controls.EvaluateRateLimitAsync(
                new RateLimitSubject(producer, command.Application, command.RecipientId, command.Class),
                enforcePrincipalLimit: origin.Source == IngestionSource.Rest,
                cancellationToken);
            return rateDecision.Allowed
                ? new AdmissionDecision.Allowed()
                : new AdmissionDecision.RateLimited(rateDecision);
        }

        public Task RememberAsync(
            string application,
            string idempotencyKey,
            RememberedAcceptance acceptance,
            CancellationToken cancellationToken)
            => _controls.RememberAsync(application, idempotencyKey, acceptance, cancellationToken);

        private async Task<RememberedAcceptance?> FindAuthoritativeAsync(
            string application,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            IServiceScopeFactory scopeFactory = _scopeFactory ?? throw new InvalidOperationException(
                "A composição REST da admissão exige acesso à autoridade de idempotência.");
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            IdempotencyRegistration? registration = await db.IdempotencyRegistrations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Application == application
                        && candidate.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            return registration is null
                ? null
                : new RememberedAcceptance(registration.NotificationId, registration.PayloadHash);
        }
    }
}
