using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The stored purpose is what routes a notification, and the authentication
/// queue is the one that keeps a login code ahead of ordinary traffic. This
/// test walks the value the aggregate actually persists into the routing
/// decision, because that is the pairing production makes: nothing between the
/// two ever sees the text the author typed.
/// </summary>
public sealed class TemplatePurposeRoutingTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;

    [Fact]
    public void A_template_created_in_mixed_case_routes_to_the_authentication_queue()
    {
        Template template = Template.Create(Key, Metadata("  Authentication  ")).Value!;

        var destination = RouteStage.DestinationFor(template.Purpose, "sms", NotificationClasses.Critical);

        // The claim is about the stored value reaching the routing decision.
        // The routing decision itself reads one canonical word and is not, and
        // must not become, case-insensitive.
        destination.ShouldBe("dispatch-sms-auth");
    }

    private static TemplateMetadata Metadata(string purpose) => new()
    {
        Application = "araia-cambio",
        Class = NotificationClass.Critical,
        OwnerTeam = "identity-squad",
        Purpose = purpose,
        LegalBasis = "execucao-de-contrato",
    };
}
