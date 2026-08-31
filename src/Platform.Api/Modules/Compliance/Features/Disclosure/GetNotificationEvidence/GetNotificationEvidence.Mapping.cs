using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetNotificationEvidence
{
    private static LinkView ToLink(AuditLink link) => new()
    {
        Seq = link.Seq,
        Hash = link.Hash,
        PrevHash = link.PrevHash,
        Action = link.Action,
        ActorType = link.ActorType,
        ActorId = link.ActorId,
        Application = link.Application,
        EntityType = link.EntityType,
        EntityId = link.EntityId,
        OccurredAt = link.OccurredAt,
        Details = link.Details,
        Canonical = link.Canonical,
    };

    private static NotificationView ToNotification(NotificationEvidence evidence) => new()
    {
        Id = NotificationIdentity.Format(evidence.Id),
        Application = evidence.Application,
        RecipientId = evidence.RecipientId,
        Class = evidence.Class,
        Status = evidence.Status,
        TemplateKey = evidence.TemplateKey,
        TemplateVersion = evidence.TemplateVersion,
        RequestedBy = evidence.RequestedBy,
        CreatedAt = evidence.CreatedAt,
        ExpiresAt = evidence.ExpiresAt,
        VariablesMasked = evidence.VariablesMasked,
        PolicyVersion = evidence.PolicyVersion,
        CorrelationId = evidence.CorrelationId,
        ReleaseAt = evidence.ReleaseAt,
    };

    /// <summary>
    /// Projects one attempt member by member. The list of provider feedback is
    /// always materialized, even when it is empty, because an empty array here
    /// states that the store holds no feedback for the attempt.
    /// </summary>
    internal static AttemptView ToAttempt(NotificationAttemptEvidence attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return new AttemptView
        {
            Sequence = attempt.Sequence,
            Channel = attempt.Channel,
            Status = attempt.Status,
            ContentHashFull = attempt.ContentHashFull,
            ContentHashMasked = attempt.ContentHashMasked,
            CreatedAt = attempt.CreatedAt,
            DeliveryEvents = [.. attempt.DeliveryEvents.Select(ToDeliveryEvent)],
            ProviderKey = attempt.ProviderKey,
            ProviderMessageId = attempt.ProviderMessageId,
            ErrorCode = attempt.ErrorCode,
            FallbackDeadline = attempt.FallbackDeadline,
            SentAt = attempt.SentAt,
            DeliveredAt = attempt.DeliveredAt,
            ContactPointId = attempt.ContactPointId,
            DeviceTokenId = attempt.DeviceTokenId,
        };
    }

    /// <summary>
    /// Projects one piece of provider feedback member by member. The sealed
    /// provider payload has no member to land in, which is the point: the
    /// projection cannot forward what it never names.
    /// </summary>
    private static DeliveryEventView ToDeliveryEvent(DeliveryEventEvidence feedback) => new()
    {
        ProviderKey = feedback.ProviderKey,
        ProviderEventId = feedback.ProviderEventId,
        Kind = feedback.Kind,
        OccurredAt = feedback.OccurredAt,
        ErrorCode = feedback.ErrorCode,
    };

    private static PolicyEvaluationView ToEvaluation(PolicyEvaluationEvidence evaluation) => new()
    {
        Rule = evaluation.Rule,
        Result = evaluation.Result,
        Reason = evaluation.Reason,
        EvaluatedAt = evaluation.EvaluatedAt,
        Evidence = evaluation.Evidence,
        UndisclosedEvidenceKeys = evaluation.UndisclosedEvidenceKeys,
    };

    /// <summary>
    /// Projects the historical version member by member. The pin and the
    /// resolved layout are carried as two members and never folded into one:
    /// the catalog omits the layout both for a version that pinned nothing and
    /// for one whose pin it could not vouch for, and only the pin separates a
    /// message that went out framed by nothing from one whose frame this answer
    /// cannot name a hash for.
    /// </summary>
    internal static TemplateVersionView ToTemplate(HistoricalTemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new TemplateVersionView
        {
            Application = version.Application,
            TemplateKey = version.TemplateKey,
            Version = version.Version,
            VersionStatus = version.VersionStatus,
            TemplateStatus = version.TemplateStatus,
            Class = version.Class,
            OwnerTeam = version.OwnerTeam,
            Purpose = version.Purpose,
            LegalBasis = version.LegalBasis,
            SensitiveVariables = version.SensitiveVariables,
            ContentHash = version.ContentHash,
            PublishedAt = version.PublishedAt,
            RolledBackFromVersion = version.RolledBackFromVersion,
            LayoutPin = version.LayoutPin is null ? null : ToLayoutPin(version.LayoutPin),
            Layout = version.Layout is null ? null : ToLayout(version.Layout),
        };
    }

    private static LayoutPinView ToLayoutPin(HistoricalLayoutPin pin) => new()
    {
        LayoutKey = pin.LayoutKey,
        Version = pin.Version,
    };

    private static LayoutVersionView ToLayout(HistoricalLayoutVersion layout) => new()
    {
        LayoutKey = layout.LayoutKey,
        Version = layout.Version,
        VersionStatus = layout.VersionStatus,
        ContentHash = layout.ContentHash,
        PublishedAt = layout.PublishedAt,
    };

    private static ApprovalView ToApproval(ApprovalRecord approval) => new()
    {
        SubjectType = approval.SubjectType,
        SubjectId = approval.SubjectId,
        SubjectVersion = approval.SubjectVersion,
        ContentHash = approval.ContentHash,
        Role = approval.Role,
        ApproverOid = approval.ApproverOid,
        ApprovedAt = approval.ApprovedAt,
    };

    private static ContactPointView ToContactPoint(HistoricalContactPoint point) => new()
    {
        ContactPointId = point.ContactPointId,
        Channel = point.Channel,
        MaskedValue = point.MaskedValue,
        Verified = point.Verified,
        Active = point.Active,
        RemovedAt = point.RemovedAt,
    };

    private static DeviceRegistrationView ToDevice(HistoricalDeviceRegistration device) => new()
    {
        DeviceTokenId = device.DeviceTokenId,
        Platform = device.Platform,
        RegisteredAt = device.RegisteredAt,
        LastSeenAt = device.LastSeenAt,
        Active = device.Active,
        AppVersion = device.AppVersion,
        InvalidatedAt = device.InvalidatedAt,
    };

    private static ConsentEntryView ToConsentEntry(ConsentLedgerEntry entry) => new()
    {
        ContactPointId = entry.ContactPointId,
        Channel = entry.Channel,
        Purpose = entry.Purpose,
        Granted = entry.Granted,
        Source = entry.Source,
        ActorId = entry.ActorId,
        TermsVersion = entry.TermsVersion,
        RecordedAt = entry.RecordedAt,
    };
}
