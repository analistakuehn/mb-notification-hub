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
        NotificationRejectionReasons.IsCanonical(LayoutRejectionReasons.Disabled).ShouldBeTrue();
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
        NotificationRejectionReasons.IsCanonical(RenderStage.ReasonAuthenticationSmsLink).ShouldBeTrue();
        NotificationRejectionReasons.IsCanonical(RenderStage.ReasonLayoutDisabled).ShouldBeTrue();
    }

    [Fact]
    public void The_security_refusal_of_the_render_keeps_its_own_reason()
    {
        // Collapsing it into the render failure would tell the producer its
        // template is broken, when what happened is that a security rule
        // refused content the template rendered correctly.
        NotificationRejectionReasons.AuthenticationSmsLink
            .ShouldNotBe(NotificationRejectionReasons.TemplateRenderFailed);
    }

    [Fact]
    public void The_reason_of_the_stage_is_the_word_the_renderer_refuses_with()
    {
        // The two live on opposite sides of a module boundary and neither may
        // reference the other's internals, so the word travelling between them
        // is pinned here: a rename on one side without the other would leave
        // the stage silently mapping a security refusal to a render failure.
        RenderStage.ReasonAuthenticationSmsLink
            .ShouldBe(RenderedContentRejectionReasons.AuthenticationSmsLink);
    }

    [Fact]
    public void The_layout_refusal_keeps_its_own_reason()
    {
        // The template is fine and its render worked: what stopped the message
        // is the wrapper the version pins. Collapsing it into either of the
        // two neighbours would send the owner of the template looking for a
        // defect that is not in the template.
        NotificationRejectionReasons.LayoutDisabled
            .ShouldNotBe(NotificationRejectionReasons.TemplateDisabled);
        NotificationRejectionReasons.LayoutDisabled
            .ShouldNotBe(NotificationRejectionReasons.TemplateRenderFailed);
    }

    [Fact]
    public void The_layout_reason_of_the_stage_is_the_word_the_renderer_refuses_with()
    {
        // Same crossing as the security refusal above, and the same failure
        // mode if it drifts: the stage would map a disabled layout onto a
        // render failure and no consumer could tell the two apart.
        RenderStage.ReasonLayoutDisabled.ShouldBe(LayoutRejectionReasons.Disabled);
    }

    [Fact]
    public void The_size_refusal_of_the_render_keeps_its_own_reason()
    {
        // The render worked and the payload passed the published schema. What
        // stopped the message is that the text it produced does not fit the
        // channel, and the producer can act on that by shortening a variable
        // value. Collapsing it into either neighbour sends the producer to the
        // template owner or back to the schema, and neither has the defect.
        NotificationRejectionReasons.IsCanonical(RenderStage.ReasonRenderedContentTooLarge)
            .ShouldBeTrue();
        NotificationRejectionReasons.RenderedContentTooLarge
            .ShouldNotBe(NotificationRejectionReasons.TemplateRenderFailed);
        NotificationRejectionReasons.RenderedContentTooLarge
            .ShouldNotBe(NotificationRejectionReasons.TemplateVariablesInvalid);
    }

    [Fact]
    public void The_size_reason_of_the_stage_is_the_word_the_renderer_refuses_with()
    {
        // Same module crossing as the two refusals above, and the same failure
        // mode if it drifts: the stage would map a message that is too large
        // onto a render failure and no consumer could tell the two apart.
        RenderStage.ReasonRenderedContentTooLarge.ShouldBe(RenderedContentRejectionReasons.TooLarge);
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
