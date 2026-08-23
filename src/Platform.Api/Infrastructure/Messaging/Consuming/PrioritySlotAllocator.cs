namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Shared processing slots with priority at allocation time: when a slot
/// frees, the waiter with the lowest rank wins it, so a burst of low-priority
/// messages can never starve the authentication traffic while polling order
/// stays fair. Ranks are dense small integers; zero is the highest priority.
/// </summary>
public sealed class PrioritySlotAllocator
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<int, Queue<TaskCompletionSource>> _waiters = [];
    private int _available;

    public PrioritySlotAllocator(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        _available = slots;
    }

    /// <summary>Waits for one slot; dispose the lease to free it.</summary>
    public async Task<IDisposable> AcquireAsync(int priorityRank, CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_gate)
        {
            if (_available > 0)
            {
                _available--;
                return new Lease(this);
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(priorityRank, out Queue<TaskCompletionSource>? queue))
            {
                queue = new Queue<TaskCompletionSource>();
                _waiters[priorityRank] = queue;
            }

            queue.Enqueue(waiter);
        }

        await using CancellationTokenRegistration registration =
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        await waiter.Task;
        return new Lease(this);
    }

    private void Release()
    {
        while (true)
        {
            TaskCompletionSource? next = null;
            lock (_gate)
            {
                foreach ((var rank, Queue<TaskCompletionSource> queue) in _waiters)
                {
                    if (queue.TryDequeue(out next))
                    {
                        if (queue.Count == 0)
                        {
                            _waiters.Remove(rank);
                        }

                        break;
                    }
                }

                if (next is null)
                {
                    _available++;
                    return;
                }
            }

            // A cancelled waiter abandoned its place; pass the slot on.
            if (next.TrySetResult())
            {
                return;
            }
        }
    }

    private sealed class Lease(PrioritySlotAllocator owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
