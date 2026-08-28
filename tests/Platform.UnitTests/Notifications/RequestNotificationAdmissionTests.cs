using System.Reflection;
using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.UnitTests.Notifications;

public sealed class RequestNotificationAdmissionTests
{
    [Fact]
    public void Handler_constructors_have_at_most_seven_dependencies()
    {
        ConstructorInfo[] constructors = typeof(RequestNotification.Handler).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        constructors.ShouldNotBeEmpty();
        constructors.ShouldAllBe(constructor => constructor.GetParameters().Length <= 7);
    }
}
