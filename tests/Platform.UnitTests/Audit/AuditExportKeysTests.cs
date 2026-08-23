using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.UnitTests.Audit;

/// <summary>
/// Keys are derived, never recorded. A rerun addresses the objects the first
/// run wrote because both compute the same key from the same facts.
/// </summary>
public sealed class AuditExportKeysTests
{
    [Fact]
    public void A_daily_slice_lives_under_its_partition_and_its_day()
    {
        AuditExportKeys.DailyFolder(
                "audit-export/v1", "audit_event", "audit_event_2026_08", new DateOnly(2026, 8, 3))
            .ShouldBe("audit-export/v1/audit_event/audit_event_2026_08/daily/2026-08-03/");
    }

    [Fact]
    public void The_authoritative_export_of_a_partition_has_one_place_only()
    {
        AuditExportKeys.ClosingFolder("audit-export/v1", "audit_event", "audit_event_2026_08")
            .ShouldBe("audit-export/v1/audit_event/audit_event_2026_08/closing/");
    }

    [Theory]
    [InlineData("audit-export/v1")]
    [InlineData("audit-export/v1/")]
    [InlineData("/audit-export/v1/")]
    public void The_prefix_is_normalized_so_configuration_spelling_never_moves_the_evidence(string prefix)
    {
        AuditExportKeys.ClosingFolder(prefix, "audit_event", "audit_event_2026_08")
            .ShouldBe("audit-export/v1/audit_event/audit_event_2026_08/closing/");
    }

    [Fact]
    public void An_empty_prefix_puts_the_evidence_at_the_root_of_the_bucket()
    {
        AuditExportKeys.ClosingFolder(" ", "audit_event", "audit_event_2026_08")
            .ShouldBe("audit_event/audit_event_2026_08/closing/");
    }

    [Fact]
    public void A_key_identifier_that_looks_like_a_path_becomes_one_object_not_a_tree()
    {
        // Managed keys are named by ARN, whose separators would otherwise turn
        // the archived public key into a folder structure.
        AuditExportKeys.PublicKeyObject(
                "audit-export/v1", "arn:aws:kms:us-east-1:000000000000:key/abc-123")
            .ShouldBe("audit-export/v1/attestation-keys/arn_aws_kms_us-east-1_000000000000_key_abc-123.json");
    }
}
