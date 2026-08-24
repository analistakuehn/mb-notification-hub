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
        NotificationRejectionReasons.IsCanonical(NotificationRejectionReasons.RecipientRateLimited).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(NotificationRejectionReasons.PayloadInvalid).ShouldBeTrue();
    }

    [Fact]
    public void The_protocol_only_problem_types_of_the_route_stay_out_of_the_catalog()
    {
        // Neither protocol condition travels as the reason of a rejection
        // event, so promoting either would publish vocabulary the bus never
        // speaks. The principal budget in particular announces nothing,
        // because one event per refused request is the storm the control
        // exists to stop.
        NotificationRejectionReasons.IsCanonical(IngestionProblems.IdempotencyKeyRequiredType).ShouldBeFalse();
        NotificationRejectionReasons.IsCanonical(IngestionProblems.PrincipalRateLimitedType).ShouldBeFalse();
    }

    [Fact]
    public void Kill_switch_unavailability_stays_out_of_the_rejection_catalog()
    {
        // Operational unavailability answers only the synchronous caller and
        // never becomes a rejection event reason on the bus.
        NotificationRejectionReasons.IsCanonical(IngestionProblems.KillSwitchUnavailableType).ShouldBeFalse();
    }

    [Fact]
    public void Catalog_covers_the_envelope_refusal_of_the_bus_ingress()
    {
        // The bus refuses an envelope whose declared type it does not consume,
        // and the reason is its own instead of the generic shape refusal: the
        // producer has to tell "your body is wrong" from "your version is not
        // the one this topic speaks".
        NotificationRejectionReasons.IsCanonical(NotificationRejectionReasons.EventTypeUnsupported).ShouldBeTrue();
        NotificationRejectionReasons.EventTypeUnsupported
            .ShouldNotBe(NotificationRejectionReasons.PayloadInvalid);
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
    public void Provider_failure_codes_stay_out_of_the_rejection_catalog()
    {
        // Delivery providers own an open failure vocabulary. Adding this FCM
        // code to the closed rejection set must make this contract fail.
        NotificationRejectionReasons.IsCanonical("UNREGISTERED").ShouldBeFalse();
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
    public void Catalog_covers_the_disabled_producer_reason()
    {
        // A producer kill switch rejects through the same closed vocabulary
        // consumed by notification producers.
        NotificationRejectionReasons.IsCanonical(NotificationRejectionReasons.ProducerDisabled).ShouldBeTrue();
        NotificationRejectionReasons.ProducerDisabled
            .ShouldNotBe(NotificationRejectionReasons.ProducerNotAuthorized);
    }
}
