using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class OutboxRelayTests
{
    [Fact]
    public async Task Drains_bands_in_auth_critical_transactional_operational_order()
    {
        var store = new FakePendingStore();
        // Added out of order on purpose; the auth destination carries an
        // operational stored class and must still drain first.
        PendingOutboxMessage operational = Message("core-operational", MinutesAgo(40));
        PendingOutboxMessage auth = Message("core-auth", MinutesAgo(10));
        PendingOutboxMessage transactional = Message("core-transactional", MinutesAgo(30));
        PendingOutboxMessage critical = Message("core-critical", MinutesAgo(20));
        store.Add("operational", operational);
        store.Add("operational", auth);
        store.Add("transactional", transactional);
        store.Add("critical", critical);
        var publisher = new RecordingPublisher();
        OutboxRelay relay = Relay(store, publisher);

        OutboxRelayPassResult result = await relay.RunPassAsync(CancellationToken.None);

        result.Published.ShouldBe(4);
        result.Failed.ShouldBe(0);
        publisher.PublishedIds.ShouldBe([auth.Id, critical.Id, transactional.Id, operational.Id]);
        store.IsSent(auth.Id).ShouldBeTrue();
        store.IsSent(operational.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Stamps_sent_only_on_the_accepted_messages_of_a_partially_failed_batch()
    {
        var store = new FakePendingStore();
        PendingOutboxMessage first = Message("core-critical", MinutesAgo(3));
        PendingOutboxMessage second = Message("core-critical", MinutesAgo(2));
        PendingOutboxMessage third = Message("core-critical", MinutesAgo(1));
        store.Add("critical", first);
        store.Add("critical", second);
        store.Add("critical", third);
        var publisher = new RecordingPublisher { Accepts = message => message.Id != second.Id };
        OutboxRelay relay = Relay(store, publisher);

        OutboxRelayPassResult result = await relay.RunPassAsync(CancellationToken.None);

        result.Published.ShouldBe(2);
        result.Failed.ShouldBe(1);
        store.IsSent(first.Id).ShouldBeTrue();
        store.IsSent(second.Id).ShouldBeFalse();
        store.IsSent(third.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Republishes_when_the_stamp_fails_after_a_successful_publish()
    {
        var store = new FakePendingStore();
        PendingOutboxMessage message = Message("core-critical", MinutesAgo(1));
        store.Add("critical", message);
        var publisher = new RecordingPublisher();
        OutboxRelay relay = Relay(store, publisher);

        store.FailOnComplete = true;
        await Should.ThrowAsync<InvalidOperationException>(() => relay.RunPassAsync(CancellationToken.None));
        store.IsSent(message.Id).ShouldBeFalse();

        store.FailOnComplete = false;
        OutboxRelayPassResult retry = await relay.RunPassAsync(CancellationToken.None);

        // Published twice, lost never: the duplicate belongs to the consumer.
        publisher.PublishedIds.ShouldBe([message.Id, message.Id]);
        retry.Published.ShouldBe(1);
        store.IsSent(message.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task A_band_restricted_instance_leaves_the_other_bands_pending()
    {
        var store = new FakePendingStore();
        PendingOutboxMessage critical = Message("core-critical", MinutesAgo(2));
        PendingOutboxMessage transactional = Message("core-transactional", MinutesAgo(1));
        store.Add("critical", critical);
        store.Add("transactional", transactional);
        var publisher = new RecordingPublisher();
        OutboxRelay relay = Relay(store, publisher, new OutboxRelayOptions { Bands = ["critical"] });

        OutboxRelayPassResult result = await relay.RunPassAsync(CancellationToken.None);

        result.Published.ShouldBe(1);
        publisher.PublishedIds.ShouldBe([critical.Id]);
        store.IsSent(critical.Id).ShouldBeTrue();
        store.IsSent(transactional.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task Stops_reclaiming_a_band_in_the_same_pass_after_a_failed_batch()
    {
        var store = new FakePendingStore();
        store.Add("operational", Message("core-operational", MinutesAgo(2)));
        store.Add("operational", Message("core-operational", MinutesAgo(1)));
        var publisher = new RecordingPublisher { Accepts = _ => false };
        OutboxRelay relay = Relay(
            store, publisher, new OutboxRelayOptions { Bands = ["operational"], BatchSize = 1 });

        OutboxRelayPassResult result = await relay.RunPassAsync(CancellationToken.None);

        // One claim, one publish attempt: re-claiming inside the pass would
        // spin over the same failing rows; the next pass retries instead.
        result.Published.ShouldBe(0);
        result.Failed.ShouldBe(1);
        store.ClaimCount.ShouldBe(1);
        publisher.Batches.Count.ShouldBe(1);
    }

    private static OutboxRelay Relay(
        FakePendingStore store,
        RecordingPublisher publisher,
        OutboxRelayOptions? options = null)
        => new(
            store,
            publisher,
            Options.Create(options ?? new OutboxRelayOptions()),
            TimeProvider.System,
            NullLogger<OutboxRelay>.Instance);

    private static PendingOutboxMessage Message(string destination, DateTimeOffset createdAt)
        => new(
            Guid.CreateVersion7(),
            destination,
            "notification.accepted",
            $"cus_{Guid.NewGuid():N}",
            "{}",
            """{"payload": {"notificationId": "n"}}""",
            createdAt);

    private static DateTimeOffset MinutesAgo(int minutes)
        => DateTimeOffset.UtcNow.AddMinutes(-minutes);

    private sealed class FakeRow(string priorityClass, PendingOutboxMessage message)
    {
        public string PriorityClass { get; } = priorityClass;

        public PendingOutboxMessage Message { get; } = message;

        public DateTimeOffset? SentAt { get; set; }
    }

    private sealed class FakePendingStore : IOutboxPendingStore
    {
        private readonly List<FakeRow> _rows = [];

        public bool FailOnComplete { get; set; }

        public int ClaimCount { get; private set; }

        public void Add(string priorityClass, PendingOutboxMessage message)
            => _rows.Add(new FakeRow(priorityClass, message));

        public bool IsSent(Guid id) => _rows.Single(row => row.Message.Id == id).SentAt is not null;

        public Task<IOutboxClaim> ClaimAsync(OutboxBand band, int batchSize, CancellationToken cancellationToken)
        {
            ClaimCount++;
            List<FakeRow> claimed = [.. _rows
                .Where(row => row.SentAt is null
                    && OutboxBands.Classify(row.Message.Destination, row.PriorityClass) == band)
                .OrderBy(row => row.Message.CreatedAt)
                .Take(batchSize)];
            return Task.FromResult<IOutboxClaim>(new FakeClaim(this, claimed));
        }

        private sealed class FakeClaim(FakePendingStore store, List<FakeRow> claimed) : IOutboxClaim
        {
            public IReadOnlyList<PendingOutboxMessage> Messages { get; } =
                [.. claimed.Select(row => row.Message)];

            public Task CompleteAsync(
                IReadOnlyCollection<Guid> sentIds,
                DateTimeOffset sentAt,
                CancellationToken cancellationToken)
            {
                if (store.FailOnComplete)
                {
                    throw new InvalidOperationException("Falha induzida ao carimbar sent_at.");
                }

                foreach (FakeRow row in claimed.Where(row => sentIds.Contains(row.Message.Id)))
                {
                    row.SentAt = sentAt;
                }

                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<Guid> PublishedIds { get; } = [];

        public List<IReadOnlyList<PendingOutboxMessage>> Batches { get; } = [];

        public Func<PendingOutboxMessage, bool> Accepts { get; set; } = _ => true;

        public Task<OutboxPublishOutcome> PublishAsync(
            IReadOnlyList<PendingOutboxMessage> messages,
            CancellationToken cancellationToken)
        {
            Batches.Add(messages);
            var accepted = new List<Guid>();
            var failures = new List<OutboxPublishFailure>();
            foreach (PendingOutboxMessage message in messages)
            {
                PublishedIds.Add(message.Id);
                if (Accepts(message))
                {
                    accepted.Add(message.Id);
                }
                else
                {
                    failures.Add(new OutboxPublishFailure(message.Id, message.Destination, "induced-failure"));
                }
            }

            return Task.FromResult(new OutboxPublishOutcome { AcceptedIds = accepted, Failures = failures });
        }
    }
}
