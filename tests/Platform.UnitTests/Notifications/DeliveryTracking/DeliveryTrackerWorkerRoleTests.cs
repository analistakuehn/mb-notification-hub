using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications;

namespace NotificationHub.UnitTests.Notifications.DeliveryTracking;

public sealed class DeliveryTrackerWorkerRoleTests
{
    [Fact]
    public void The_role_answers_to_the_name_the_deployment_configures()
        => DeliveryTrackerWorkerRole.Role.ShouldBe("delivery-tracker");

    [Fact]
    public void The_role_drains_the_queue_the_ingestion_announces_to()
        => DeliveryTrackerWorkerRole.Queues
            .Select(binding => binding.QueueName)
            .ShouldBe(["delivery-events"]);

    [Fact]
    public void A_role_with_one_queue_ranks_it_first_so_no_slot_starves()
        => DeliveryTrackerWorkerRole.Queues
            .ShouldAllBe(binding => binding.PriorityRank == 0);

    [Fact]
    public void The_role_binds_no_queue_twice()
    {
        SqsQueueBinding[] queues = DeliveryTrackerWorkerRole.Queues;

        queues.Select(binding => binding.QueueName)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(queues.Length);
    }
}
