using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// The catalog is only worth something if it is complete. A reason produced by
/// code but missing from it would reach a producer as a value no consumer can
/// interpret, and mapping it to a generic one instead would destroy the
/// diagnosis. These tests fail the moment a new reason appears anywhere the
/// catalog does not already cover.
/// </summary>
public sealed class NotificationRejectionReasonsTests
{
    [Fact]
    public void Catalog_covers_every_reason_the_published_catalog_reports()
    {
        NotificationRejectionReasons.IsCanonical(TemplateRejectionReasons.Deprecated).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(TemplateRejectionReasons.Disabled).ShouldBeTrue();
    }

    [Fact]
    public void Catalog_covers_every_reason_the_template_gate_reports()
    {
        NotificationRejectionReasons.IsCanonical(TemplateGateReasons.NotFound).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(TemplateGateReasons.ClassMismatch).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(TemplateGateReasons.VariablesInvalid).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(TemplateGateReasons.SensitiveVariablesOnBus).ShouldBeTrue();
    }

    [Fact]
    public void Catalog_covers_every_rejection_reason_the_pipeline_rules_produce()
    {
        NotificationRejectionReasons.IsCanonical(ConsentGateRule.ReasonNoConsent).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(DedupeWindowRule.ReasonDuplicateWindow).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(ChannelSelectionRule.ReasonNoValidContact).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(ResolveStage.ReasonNoValidContact).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(RenderStage.ReasonRenderFailed).ShouldBeTrue();
    }

    [Fact]
    public void Catalog_covers_every_problem_type_the_ingestion_route_publishes()
    {
        NotificationRejectionReasons.IsCanonical(IngestionProblems.ClassNotAllowedType).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(IngestionProblems.IdempotencyKeyConflictType).ShouldBeTrue();
    }

    [Fact]
    public void A_value_outside_the_catalog_is_not_canonical()
    {
        // Falsification: the membership check must be able to answer no, or
        // every assertion above would hold for any string at all.
        NotificationRejectionReasons.IsCanonical("template-taken-by-aliens").ShouldBeFalse();
        NotificationRejectionReasons.IsCanonical(null).ShouldBeFalse();
    }

    [Fact]
    public void The_quiet_hours_reason_stays_out_of_the_rejection_catalog()
    {
        // A deferral is not a rejection: the notification is still going out,
        // later. Putting the reason in the rejection catalog would tell a
        // producer its request was refused when it was only postponed.
        NotificationRejectionReasons.IsCanonical(QuietHoursRule.ReasonQuietHours).ShouldBeFalse();
    }

    [Fact]
    public void The_disabled_producer_reason_exists_and_is_unreachable_in_this_phase()
    {
        // Declared, never produced: the registry has no enabled column, on
        // purpose, because a switched-off row would be a slow lever pretending
        // to be an emergency stop. The value stays in the catalog so the
        // vocabulary does not shift when the kill switch lands.
        NotificationRejectionReasons.IsCanonical(NotificationRejectionReasons.ProducerDisabled).ShouldBeTrue();
    }
}
