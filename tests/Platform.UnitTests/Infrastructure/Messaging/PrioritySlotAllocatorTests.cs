using NotificationHub.Api.Infrastructure.Messaging.Consuming;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class PrioritySlotAllocatorTests
{
    [Fact]
    public async Task Free_slots_are_granted_immediately()
    {
        var allocator = new PrioritySlotAllocator(2);

        using IDisposable first = await allocator.AcquireAsync(3, CancellationToken.None);
        using IDisposable second = await allocator.AcquireAsync(3, CancellationToken.None);
    }

    [Fact]
    public async Task A_freed_slot_goes_to_the_highest_priority_waiter_not_the_first_in_line()
    {
        var allocator = new PrioritySlotAllocator(1);
        IDisposable held = await allocator.AcquireAsync(0, CancellationToken.None);

        // The low-priority waiter queues first; the auth-band waiter arrives later.
        Task<IDisposable> operational = allocator.AcquireAsync(3, CancellationToken.None);
        await Task.Delay(20);
        Task<IDisposable> auth = allocator.AcquireAsync(0, CancellationToken.None);
        await Task.Delay(20);
        operational.IsCompleted.ShouldBeFalse();
        auth.IsCompleted.ShouldBeFalse();

        held.Dispose();

        using IDisposable granted = await auth.WaitAsync(TimeSpan.FromSeconds(5));
        operational.IsCompleted.ShouldBeFalse();

        granted.Dispose();
        using IDisposable next = await operational.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disposing_a_lease_twice_frees_a_single_slot()
    {
        var allocator = new PrioritySlotAllocator(1);
        IDisposable lease = await allocator.AcquireAsync(0, CancellationToken.None);

        lease.Dispose();
        lease.Dispose();

        using IDisposable first = await allocator.AcquireAsync(0, CancellationToken.None);
        Task<IDisposable> second = allocator.AcquireAsync(0, CancellationToken.None);
        await Task.Delay(20);

        second.IsCompleted.ShouldBeFalse();
        first.Dispose();
        using IDisposable granted = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_cancelled_waiter_abandons_its_place_and_the_slot_passes_on()
    {
        var allocator = new PrioritySlotAllocator(1);
        IDisposable held = await allocator.AcquireAsync(0, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> cancelled = allocator.AcquireAsync(0, cancellation.Token);
        await Task.Delay(20);
        Task<IDisposable> surviving = allocator.AcquireAsync(1, CancellationToken.None);
        await Task.Delay(20);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(cancelled);

        held.Dispose();
        using IDisposable granted = await surviving.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
