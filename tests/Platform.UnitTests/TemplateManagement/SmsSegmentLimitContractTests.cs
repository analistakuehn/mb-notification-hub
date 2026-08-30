using System.Reflection;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The published SMS size surface: what it says, and what it deliberately does
/// not offer.
/// </summary>
public sealed class SmsSegmentLimitContractTests
{
    [Fact]
    public void The_published_number_is_the_number_the_render_refuses_by()
    {
        // Two numbers would drift, and the drift reads as a consumer telling a
        // producer one budget while the render enforces another.
        SmsSegmentLimit.MaxSegments.ShouldBe(SmsSegmentCeiling.MaxSegments);
        SmsSegmentLimit.MaxSegments.ShouldBe(10);
    }

    [Fact]
    public void The_published_counter_predicts_the_refusal_exactly()
    {
        // The contract worth publishing is that a consumer holding the text can
        // work out the answer the render will give. Both sides are asserted,
        // because a counter that always answered "too many" would satisfy the
        // refusal half on its own.
        var admitted = new string('a', 1530);
        var refused = new string('a', 1531);
        Template template = MakeTemplate();

        SmsSegmentLimit.CountSegments(admitted).ShouldBe(SmsSegmentLimit.MaxSegments);
        SmsSegmentLimit.CountSegments(refused).ShouldBe(SmsSegmentLimit.MaxSegments + 1);

        Refuses(template, admitted).ShouldBeFalse();
        Refuses(template, refused).ShouldBeTrue();
    }

    [Fact]
    public void The_published_surface_offers_no_admission_gate()
    {
        // A gate here would be a contract nothing can satisfy. The size that
        // matters belongs to text that does not exist until the render has
        // interpolated the variables, framed the body in the pinned layout and
        // normalized the result, so no consumer holding a request can answer
        // the question, and one that believed a published gate would pass it
        // and still be refused at the render.
        //
        // The surface is pinned member by member rather than by absence of one
        // name, because the failure is any entry point shaped like a verdict on
        // a request, whatever it ends up being called.
        const BindingFlags Published = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var properties = typeof(SmsSegmentLimit)
            .GetProperties(Published)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var methods = typeof(SmsSegmentLimit)
            .GetMethods(Published)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        properties.ShouldBe(["MaxSegments"]);
        methods.ShouldBe(["CountSegments"]);
    }

    private static bool Refuses(Template template, string body)
    {
        Result<RenderedOutput> result = RenderedOutputPolicy.Apply(
            template,
            Channel.Sms,
            new RenderedFields(null, body, null),
            RefusalShape.Bare,
            AuthenticationLinkBan.Enforce,
            RenderedSizeCeiling.Enforce);

        return result.IsFailure && result.Error == RenderedContentRejectionReasons.TooLarge;
    }

    private static Template MakeTemplate()
        => Template.Create(TemplateKey.Create("orders.status.changed").Value!, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = ["montebravo.com.br"],
        }).Value!;
}
