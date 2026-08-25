using System.Text;
using System.Text.Json;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Features.Reporting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.UnitTests.Compliance;

/// <summary>
/// The composition of the recurring evidence report and, above all, its rule
/// about absence. The archive is immutable, so what the document states it
/// states forever; a member written empty because nothing answered would be a
/// claim this hub cannot support and could never take back.
/// </summary>
public sealed class MonthlyEvidenceCompositionTests
{
    private static readonly ReportMonth Month = new(2026, 7);

    private static readonly TimeSpan Grace = TimeSpan.FromDays(3);

    [Fact]
    public void The_document_declares_the_window_the_format_version_and_the_grace_it_observed()
    {
        JsonElement root = Serialize(Compose());

        root.GetProperty("formatVersion").GetInt32().ShouldBe(MonthlyEvidenceReport.CurrentFormatVersion);
        root.GetProperty("report").GetString().ShouldBe(MonthlyEvidenceReport.ReportKind);

        JsonElement window = root.GetProperty("window");
        window.GetProperty("month").GetString().ShouldBe("2026-07");
        window.GetProperty("fromInclusive").GetDateTimeOffset()
            .ShouldBe(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        window.GetProperty("toExclusive").GetDateTimeOffset()
            .ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

        // The grace is part of the claim: delivery figures move backwards in
        // time, so a reader has to be able to tell how much correction the
        // window waited for before it was sealed.
        window.GetProperty("reconciliationGrace").GetString().ShouldBe("P3D");
    }

    [Fact]
    public void A_section_with_no_source_in_this_hub_is_absent_and_never_an_empty_list()
    {
        JsonElement root = Serialize(Compose());

        foreach (var section in UnsourcedReportSections.All)
        {
            root.TryGetProperty(section, out JsonElement declared)
                .ShouldBeFalse(
                    $"A seção '{section}' não tem fonte no hub e apareceu no documento como {declared.ValueKind}; "
                    + "uma lista vazia afirma que nada daquele tipo aconteceu, e nada aqui sabe disso.");
        }
    }

    [Fact]
    public void A_section_with_a_source_stays_declared_even_when_the_month_holds_nothing()
    {
        // The counterpart of the rule above, and what keeps it from degrading
        // into "omit whatever is empty": the trail exists, so a month without
        // a single governed change states exactly that.
        JsonElement root = Serialize(Compose(trail: EmptyTrail()));

        root.GetProperty("governedChanges").GetArrayLength().ShouldBe(0);
        root.GetProperty("approvals").GetArrayLength().ShouldBe(0);
        root.GetProperty("chainVerification").GetArrayLength().ShouldBe(0);
        root.GetProperty("trail").GetProperty("byAction").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void A_channel_whose_providers_never_report_a_delivery_declares_no_rate()
    {
        JsonElement root = Serialize(Compose());
        JsonElement push = Channel(root, "push");

        push.GetProperty("deliveryConfirmation").GetString()
            .ShouldBe(DeliveryConfirmationSources.AcceptanceOnly);
        push.TryGetProperty("deliveryRate", out _)
            .ShouldBeFalse("O push não recebe confirmação de entrega, e uma taxa ali seria medida inventada.");
        push.TryGetProperty("bounceRate", out _).ShouldBeFalse();

        // The same document does state the rate where a provider reports one,
        // so the omission above is a rule and not a gap in the composition.
        JsonElement email = Channel(root, "email");
        email.GetProperty("deliveryRate").GetDouble().ShouldBe(0.9, 0.000001);
        email.GetProperty("bounceRate").GetDouble().ShouldBe(0.05, 0.000001);
    }

    [Fact]
    public void A_channel_where_no_provider_accepted_anything_declares_no_rate()
    {
        NotificationOutcomeSummary outcomes = Outcomes(
        [
            new NotificationChannelOutcome
            {
                Channel = "sms",
                DeliveryConfirmation = DeliveryConfirmationSources.ProviderFeedback,
                Attempts = 4,
                AcceptedByProvider = 0,
                Delivered = 0,
                Bounced = 0,
                Failed = 1,
                Unknown = 0,
                Pending = 3,
            },
        ]);

        JsonElement sms = Channel(Serialize(Compose(outcomes: outcomes)), "sms");

        sms.GetProperty("attempts").GetInt64().ShouldBe(4);
        sms.TryGetProperty("deliveryRate", out _)
            .ShouldBeFalse("Sem denominador não existe taxa, e um zero ali leria como canal que não entregou nada.");
    }

    [Fact]
    public void The_document_repeats_byte_for_byte_over_the_same_sources()
    {
        // The whole idempotence of the job rests on this: a rerun recomputes
        // the digest and settles on the object already archived. A clock or a
        // run identifier inside the document would break it silently.
        var first = Compose().CanonicalBytes();
        var second = Compose().CanonicalBytes();

        Convert.ToHexString(first).ShouldBe(Convert.ToHexString(second));
    }

    [Fact]
    public void The_document_carries_what_the_owning_modules_stated()
    {
        JsonElement root = Serialize(Compose());

        JsonElement critical = root.GetProperty("volumes").EnumerateArray()
            .Single(volume => volume.GetProperty("class").GetString() == "critical");
        critical.GetProperty("requested").GetInt64().ShouldBe(30);
        critical.GetProperty("byStatus").EnumerateArray()
            .Single(status => status.GetProperty("name").GetString() == "delivered")
            .GetProperty("count").GetInt64().ShouldBe(28);

        JsonElement change = root.GetProperty("governedChanges").EnumerateArray().Single();
        change.GetProperty("action").GetString().ShouldBe(AuditActions.KillSwitchChanged);

        // A switch this platform threw by itself is the same action a person
        // throws; only the actor type separates them, so the report carries it.
        change.GetProperty("actorType").GetString().ShouldBe(AuditActorTypes.System);

        JsonElement approval = root.GetProperty("approvals").EnumerateArray().Single();
        approval.GetProperty("approverOid").GetString().ShouldBe("11111111-2222-3333-4444-555555555555");

        JsonElement refusals = root.GetProperty("refusals");
        refusals.GetProperty("byPolicyReason").EnumerateArray()
            .Single(reason => reason.GetProperty("name").GetString() == NotificationRejectionReasons.NoConsent)
            .GetProperty("count").GetInt64().ShouldBe(7);
        refusals.GetProperty("byTrailAction").EnumerateArray()
            .Single(action => action.GetProperty("action").GetString() == "notification.rejected_at_ingress")
            .GetProperty("byReason").EnumerateArray()
            .Single(reason => reason.GetProperty("name").GetString() == NotificationRejectionReasons.TemplateNotFound)
            .GetProperty("count").GetInt64().ShouldBe(3);

        root.GetProperty("chainVerification").EnumerateArray().Single()
            .GetProperty("intactRounds").GetInt64().ShouldBe(720);
        root.GetProperty("trail").GetProperty("unchainedRows").GetInt64().ShouldBe(0);
    }

    [Fact]
    public void The_top_level_members_of_the_archived_format_are_pinned()
    {
        // The archive is immutable, so a member renamed by refactoring would
        // split the format in two without anybody deciding to.
        var members = Serialize(Compose())
            .EnumerateObject()
            .Select(member => member.Name)
            .ToArray();

        members.ShouldBe(
        [
            "formatVersion",
            "report",
            "window",
            "volumes",
            "channels",
            "refusals",
            "trail",
            "governedChanges",
            "approvals",
            "chainVerification",
        ]);
    }

    private static JsonElement Channel(JsonElement root, string channel)
        => root.GetProperty("channels").EnumerateArray()
            .Single(entry => entry.GetProperty("channel").GetString() == channel);

    private static JsonElement Serialize(MonthlyEvidenceReport report)
        => JsonDocument.Parse(Encoding.UTF8.GetString(report.CanonicalBytes())).RootElement.Clone();

    private static MonthlyEvidenceReport Compose(
        NotificationOutcomeSummary? outcomes = null,
        AuditPeriodEvidence? trail = null)
        => MonthlyEvidenceComposition.Compose(
            Month, Grace, outcomes ?? Outcomes(DefaultChannels()), trail ?? Trail());

    private static IReadOnlyList<NotificationChannelOutcome> DefaultChannels() =>
    [
        new NotificationChannelOutcome
        {
            Channel = "email",
            DeliveryConfirmation = DeliveryConfirmationSources.ProviderFeedback,
            Attempts = 24,
            AcceptedByProvider = 20,
            Delivered = 18,
            Bounced = 1,
            Failed = 2,
            Unknown = 1,
            Pending = 1,
        },
        new NotificationChannelOutcome
        {
            Channel = "push",
            DeliveryConfirmation = DeliveryConfirmationSources.AcceptanceOnly,
            Attempts = 10,
            AcceptedByProvider = 9,
            Delivered = 0,
            Bounced = 0,
            Failed = 1,
            Unknown = 0,
            Pending = 0,
        },
    ];

    private static NotificationOutcomeSummary Outcomes(IReadOnlyList<NotificationChannelOutcome> channels)
        => new()
        {
            FromInclusive = Month.FromInclusive,
            ToExclusive = Month.ToExclusive,
            VolumesByClass =
            [
                new NotificationClassVolume
                {
                    Class = "critical",
                    Requested = 30,
                    ByStatus =
                    [
                        new NotificationStatusCount { Status = "delivered", Count = 28 },
                        new NotificationStatusCount { Status = "rejected", Count = 2 },
                    ],
                },
            ],
            OutcomesByChannel = channels,
            RejectionsByReason =
            [
                new NotificationRejectionCount { Reason = NotificationRejectionReasons.NoConsent, Count = 7 },
            ],
        };

    private static AuditPeriodEvidence Trail()
        => new()
        {
            FromInclusive = Month.FromInclusive,
            ToExclusive = Month.ToExclusive,
            ActionCounts =
            [
                new AuditActionCount { Action = AuditActions.AuditRead, Count = 4 },
                new AuditActionCount { Action = AuditActions.KillSwitchChanged, Count = 1 },
            ],
            ReasonCounts =
            [
                new AuditActionReasonCount
                {
                    Action = "notification.rejected_at_ingress",
                    Reason = NotificationRejectionReasons.TemplateNotFound,
                    Count = 3,
                },
            ],
            GovernedChanges =
            [
                new AuditGovernedChange
                {
                    Seq = 91,
                    Action = AuditActions.KillSwitchChanged,
                    EntityType = AuditEntityTypes.KillSwitch,
                    EntityId = "channel:sms",
                    ActorType = AuditActorTypes.System,
                    ActorId = "dispatch-worker",
                    Application = null,
                    OccurredAt = new DateTimeOffset(2026, 7, 12, 9, 30, 0, TimeSpan.Zero),
                    Hash = "abcdef",
                },
            ],
            Approvals =
            [
                new ApprovalRecord
                {
                    SubjectType = ApprovalSubjectTypes.ClassPolicyVersion,
                    SubjectId = "billing:critical",
                    SubjectVersion = 4,
                    ContentHash = "0f0f",
                    Role = ApprovalRoles.Publisher,
                    ApproverOid = "11111111-2222-3333-4444-555555555555",
                    ApprovedAt = new DateTimeOffset(2026, 7, 3, 14, 0, 0, TimeSpan.Zero),
                },
            ],
            ChainVerifications =
            [
                new AuditChainVerificationOutcome
                {
                    Partition = "audit_event_2026_07",
                    IntactRounds = 720,
                    FailedRounds = 0,
                    LastIntactAt = new DateTimeOffset(2026, 7, 31, 23, 0, 0, TimeSpan.Zero),
                    LastFailureAt = null,
                },
            ],
            UnchainedRows = 0,
        };

    private static AuditPeriodEvidence EmptyTrail()
        => new()
        {
            FromInclusive = Month.FromInclusive,
            ToExclusive = Month.ToExclusive,
            ActionCounts = [],
            ReasonCounts = [],
            GovernedChanges = [],
            Approvals = [],
            ChainVerifications = [],
            UnchainedRows = 0,
        };
}
