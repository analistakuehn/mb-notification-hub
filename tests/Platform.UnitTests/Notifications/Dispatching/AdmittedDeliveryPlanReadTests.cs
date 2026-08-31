using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

/// <summary>
/// Reading the stored plan answers three cases the caller has to tell apart.
/// Two of them continue on the published plan today, and that is precisely why
/// they may not arrive as the same answer: one is the ordinary history of a
/// row older than the column, the other is a document that stopped reading and
/// whose continuation may address a channel the admission removed.
/// </summary>
public sealed class AdmittedDeliveryPlanReadTests
{
    [Fact]
    public void A_stored_plan_reads_back_with_its_order_and_its_timeouts()
    {
        var json = AdmittedDeliveryPlan.Serialize(
            Plan(("sms", TimeSpan.FromMinutes(2)), ("email", null)));

        AdmittedPlanRead read = AdmittedDeliveryPlan.Read(json);

        AdmittedPlanRead.Present present = read.ShouldBeOfType<AdmittedPlanRead.Present>();
        present.Plan.Select(step => step.Channel.Value).ShouldBe(["sms", "email"]);
        present.Plan.Select(step => step.Timeout).ShouldBe([TimeSpan.FromMinutes(2), null]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_without_the_column_reads_as_absent(string? stored)
        => AdmittedDeliveryPlan.Read(stored).ShouldBeOfType<AdmittedPlanRead.Absent>();

    /// <summary>
    /// An empty array and a literal null collapse into absence by decision: no
    /// producer emits either, because the plan is only stored after the
    /// selection rule already rejected the request whose surviving set was
    /// empty. The collapse is asserted so that a producer which starts writing
    /// one arrives as a change in this test rather than as silence.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_document_no_producer_writes_reads_as_absent(string stored)
        => AdmittedDeliveryPlan.Read(stored).ShouldBeOfType<AdmittedPlanRead.Absent>();

    [Theory]
    [InlineData("{ nao e json")]
    [InlineData("[{\"channel\":\"sms\",")]
    public void A_document_that_does_not_parse_reads_as_unreadable(string stored)
        => AdmittedDeliveryPlan.Read(stored)
            .ShouldBeOfType<AdmittedPlanRead.Unreadable>()
            .Refused.ShouldBe(AdmittedDeliveryPlan.RefusedMalformedDocument);

    /// <summary>
    /// The witness carries the raw word the vocabulary turned down, never the
    /// formatted refusal: the formatted one is the HTTP boundary codec of the
    /// module that owns the vocabulary, and it separates its fields with a
    /// control character. Neither the control character nor another module's
    /// error codec belongs in this module's trail.
    /// </summary>
    [Fact]
    public void An_unknown_channel_reads_as_unreadable_carrying_the_raw_refused_word()
    {
        AdmittedPlanRead.Unreadable unreadable = AdmittedDeliveryPlan
            .Read("[{\"channel\":\"telegram\",\"timeout\":null}]")
            .ShouldBeOfType<AdmittedPlanRead.Unreadable>();

        unreadable.Refused.ShouldBe("telegram");
        unreadable.Refused.Contains('\u001F').ShouldBeFalse(
            "o separador de unidade pertence ao codec de erro da fronteira HTTP do módulo "
            + "dono do vocabulário e não pode viajar para a trilha deste módulo.");
    }

    /// <summary>A step that names no channel has no raw word to quote, so it takes the stand-in.</summary>
    [Fact]
    public void A_step_without_a_channel_reads_as_unreadable_with_the_stand_in()
        => AdmittedDeliveryPlan.Read("[{\"timeout\":\"00:01:00\"}]")
            .ShouldBeOfType<AdmittedPlanRead.Unreadable>()
            .Refused.ShouldBe(AdmittedDeliveryPlan.RefusedMalformedDocument);

    /// <summary>
    /// One unknown channel refuses the whole document rather than the step:
    /// dropping it would silently shorten a plan the admission decided, which
    /// is the same harm as continuing on the published one.
    /// </summary>
    [Fact]
    public void One_unknown_channel_refuses_the_whole_document()
        => AdmittedDeliveryPlan.Read("[{\"channel\":\"sms\"},{\"channel\":\"pombo\"}]")
            .ShouldBeOfType<AdmittedPlanRead.Unreadable>()
            .Refused.ShouldBe("pombo");

    private static DeliveryPlanStep[] Plan(params (string Channel, TimeSpan? Timeout)[] steps)
        => [.. steps.Select(step =>
            new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))];
}
