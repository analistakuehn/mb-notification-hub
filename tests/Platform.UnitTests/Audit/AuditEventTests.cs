using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.UnitTests.Audit;

public sealed class AuditEventTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 22, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_recorded_event_preserves_actor_action_entity_and_evidence()
    {
        var auditEvent = AuditEvent.Record(Entry());

        auditEvent.Id.ShouldNotBe(Guid.Empty);
        auditEvent.ActorType.ShouldBe("user");
        auditEvent.ActorId.ShouldBe("publisher-1");
        auditEvent.Application.ShouldBe("araia-cambio");
        auditEvent.Action.ShouldBe("template.version.published");
        auditEvent.EntityType.ShouldBe("template_version");
        auditEvent.EntityId.ShouldBe("auth.otp.login:3");
        auditEvent.DetailsJson.ShouldBe("""{"contentHash":"abc"}""");
        auditEvent.OccurredAt.ShouldBe(OccurredAt);
    }

    [Fact]
    public void The_application_is_optional_and_preserved_as_absent()
    {
        var auditEvent = AuditEvent.Record(Entry() with { Application = null });

        auditEvent.Application.ShouldBeNull();
    }

    [Fact]
    public void A_recorded_event_keeps_only_the_precision_the_store_keeps()
    {
        DateTimeOffset zoned = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(-3)).AddTicks(1234567);

        var auditEvent = AuditEvent.Record(Entry() with { OccurredAt = zoned });

        auditEvent.OccurredAt.Offset.ShouldBe(TimeSpan.Zero);
        auditEvent.OccurredAt.ShouldBe(
            new DateTimeOffset(2026, 8, 22, 15, 0, 0, TimeSpan.Zero).AddTicks(1234560));
    }

    [Theory]
    [InlineData(nameof(AuditEntry.ActorType))]
    [InlineData(nameof(AuditEntry.ActorId))]
    [InlineData(nameof(AuditEntry.Action))]
    [InlineData(nameof(AuditEntry.EntityType))]
    [InlineData(nameof(AuditEntry.EntityId))]
    [InlineData(nameof(AuditEntry.DetailsJson))]
    public void An_event_missing_a_mandatory_field_is_rejected(string field)
    {
        AuditEntry entry = field switch
        {
            nameof(AuditEntry.ActorType) => Entry() with { ActorType = " " },
            nameof(AuditEntry.ActorId) => Entry() with { ActorId = " " },
            nameof(AuditEntry.Action) => Entry() with { Action = " " },
            nameof(AuditEntry.EntityType) => Entry() with { EntityType = " " },
            nameof(AuditEntry.EntityId) => Entry() with { EntityId = " " },
            nameof(AuditEntry.DetailsJson) => Entry() with { DetailsJson = " " },
            _ => throw new InvalidOperationException($"Unmapped field '{field}'."),
        };

        Should.Throw<ArgumentException>(() => AuditEvent.Record(entry));
    }

    private static AuditEntry Entry() => new()
    {
        ActorType = AuditActorTypes.User,
        ActorId = "publisher-1",
        Application = "araia-cambio",
        Action = AuditActions.TemplateVersionPublished,
        EntityType = AuditEntityTypes.TemplateVersion,
        EntityId = "auth.otp.login:3",
        DetailsJson = """{"contentHash":"abc"}""",
        OccurredAt = OccurredAt,
    };
}
