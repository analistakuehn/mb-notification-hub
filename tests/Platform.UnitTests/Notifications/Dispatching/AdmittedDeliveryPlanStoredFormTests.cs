using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

/// <summary>
/// The exact text one notification stores in its admitted plan column. Nothing
/// asserted it before, so any change to how the vocabulary or the step writes
/// itself reached the column without a single red. The stored document is read
/// back by rows written long before the change that alters it, so its shape is
/// a compatibility contract and not an implementation detail.
/// <para>
/// The projection between the published step and the stored one is what keeps
/// this shape still: an optional member added to the published step changes
/// the contract and leaves the column alone. Serializing the published step
/// directly would put every future member in the column, starting with a null
/// for the ones a stored row never had.
/// </para>
/// </summary>
public sealed class AdmittedDeliveryPlanStoredFormTests
{
    [Fact]
    public void The_stored_plan_names_the_channel_by_its_canonical_word()
        => AdmittedDeliveryPlan.Serialize(
                Plan(("sms", TimeSpan.FromMinutes(10)), ("email", null)))
            .ShouldBe("[{\"channel\":\"sms\",\"timeout\":\"00:10:00\"},{\"channel\":\"email\",\"timeout\":null}]");

    [Fact]
    public void Every_channel_of_the_closed_set_stores_as_its_own_word()
        => AdmittedDeliveryPlan.Serialize(
                [.. Channel.All.Select(channel => new DeliveryPlanStep(channel, null))])
            .ShouldBe(
                "[{\"channel\":\"email\",\"timeout\":null},{\"channel\":\"sms\",\"timeout\":null},"
                + "{\"channel\":\"push\",\"timeout\":null},{\"channel\":\"whatsapp\",\"timeout\":null}]");

    private static DeliveryPlanStep[] Plan(params (string Channel, TimeSpan? Timeout)[] steps)
        => [.. steps.Select(step =>
            new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))];
}
