using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

/// <summary>Builders of pipeline inputs shared by the rule and stage tests.</summary>
internal static class PipelineTestData
{
    internal sealed class NoopCommitter : IPipelineCommitter
    {
        public NotificationContext? Committed { get; private set; }

        public Task<PipelineCommitResult> CommitAsync(
            NotificationContext context,
            CancellationToken cancellationToken)
        {
            Committed = context;
            return Task.FromResult<PipelineCommitResult>(
                new PipelineCommitResult.Committed(PipelineResultKind.Dispatched));
        }
    }

    internal static Notification AcceptedNotification(
        string @class = NotificationClasses.Transactional,
        int ttlSeconds = 300)
        => Notification.Accept(new NotificationDraft
        {
            Application = "araia-cambio",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            RecipientId = "recipient-1",
            Class = @class,
            TemplateKey = "orders.confirmation",
            TemplateVersion = 3,
            VariablesMaskedJson = "{}",
            RequestedBy = "producer-app",
            TtlSeconds = ttlSeconds,
            AcceptedAt = DateTimeOffset.UtcNow,
        });

    internal static NotificationContext Context(
        Notification? notification = null,
        PublishedTemplate? template = null,
        RecipientSnapshot? recipient = null,
        IEnumerable<string>? remainingChannels = null)
    {
        var context = new NotificationContext(
            notification ?? AcceptedNotification(),
            Guid.NewGuid(),
            new NoopCommitter())
        {
            Template = template,
            Recipient = recipient,
        };
        context.InitializeRemainingChannels(remainingChannels ?? ["push", "sms", "email"]);
        return context;
    }

    internal static PublishedTemplate Template(
        string purpose = "transactional-notice",
        params string[] channels)
        => new()
        {
            Application = "araia-cambio",
            TemplateKey = "orders.confirmation",
            Class = NotificationClasses.Transactional,
            OwnerTeam = "produto",
            Purpose = purpose,
            LegalBasis = "contract",
            SensitiveVariables = [],
            ChannelsWithContent = [.. (channels.Length == 0 ? ["push", "sms", "email"] : channels)
                .Select(value => Channel.Create(value).Value!)],
            DefaultLocale = "pt-BR",
            Version = 3,
            ContentHash = "hash",
        };

    internal static RecipientSnapshot Recipient(
        string timezone = "America/Sao_Paulo",
        IReadOnlyList<ContactPointSnapshot>? contactPoints = null,
        IReadOnlyList<ConsentDecision>? consents = null,
        IReadOnlyList<DeviceRegistration>? devices = null)
        => new()
        {
            RecipientId = "recipient-1",
            Timezone = timezone,
            Locale = "pt-BR",
            ContactPoints = contactPoints ?? [new ContactPointSnapshot(Guid.NewGuid(), "sms", Verified: true)],
            Consents = consents ?? [],
            Devices = devices ?? [],
        };

    internal static ClassPolicyDefinition Policy(
        IReadOnlyList<string>? channelsAllowed = null,
        IReadOnlyList<(string Channel, TimeSpan? Timeout)>? plan = null,
        TimeSpan? dedupeWindow = null,
        QuietHoursWindow? quietHours = null,
        string? consentPurpose = null)
        => new()
        {
            SchemaVersion = 1,
            ChannelsAllowed = [.. (channelsAllowed ?? ["push", "sms", "email"])
                .Select(value => Channel.Create(value).Value!)],
            DeliveryPlan = [.. (plan ?? [("push", TimeSpan.FromSeconds(30)), ("sms", null)])
                .Select(step => new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))],
            DefaultTtl = TimeSpan.FromSeconds(300),
            DedupeWindow = dedupeWindow ?? TimeSpan.FromSeconds(60),
            QuietHours = quietHours,
            ConsentPurpose = consentPurpose,
        };
}
