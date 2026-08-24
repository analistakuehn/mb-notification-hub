using NotificationHub.Api.Modules.Notifications;

namespace NotificationHub.UnitTests.Notifications.KillSwitch;

public sealed class KillSwitchContractTests
{
    [Fact]
    public void The_evaluation_contract_is_public_for_future_ingress_adapters()
    {
        Type? contract = typeof(NotificationsModule).Assembly.GetType(
            "NotificationHub.Api.Modules.Notifications.Features.KillSwitch.IKillSwitch");

        contract.ShouldNotBeNull();
        contract.IsPublic.ShouldBeTrue();
    }
}
