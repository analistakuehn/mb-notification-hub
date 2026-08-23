using Microsoft.Extensions.Configuration;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class CoreWorkerRoleTests
{
    private static IConfiguration Configuration(params string[] bands)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < bands.Length; index++)
        {
            settings[$"{CoreWorkerOptions.SectionName}:Bands:{index}"] = bands[index];
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void Without_a_restriction_the_role_drains_the_four_core_queues_in_priority_order()
    {
        SqsQueueBinding[] bindings = CoreWorkerRole.QueueBindings(Configuration());

        bindings.Select(binding => binding.QueueName).ShouldBe(
            ["core-auth", "core-critical", "core-transactional", "core-operational"]);
        bindings.Select(binding => binding.PriorityRank).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public void A_restriction_selects_queues_without_changing_their_priority_ranks()
    {
        SqsQueueBinding[] bindings = CoreWorkerRole.QueueBindings(
            Configuration("operational", "auth"));

        bindings.Select(binding => binding.QueueName).ShouldBe(["core-auth", "core-operational"]);
        bindings.Select(binding => binding.PriorityRank).ShouldBe([0, 3]);
    }

    [Fact]
    public void An_unknown_band_name_refuses_to_boot()
        => Should.Throw<InvalidOperationException>(
                () => CoreWorkerRole.QueueBindings(Configuration("bogus")))
            .Message.ShouldContain("bogus");
}
