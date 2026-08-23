using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NSubstitute;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class SqsOutboxPublisherTests
{
    // The exact text a jsonb column returns: spaces after colons, keys in
    // storage order. The publisher must ship it untouched.
    private const string StoredPayload = """{"type": "notification.accepted", "payload": {"notificationId": "n"}}""";

    [Fact]
    public async Task Publishes_the_stored_payload_text_as_the_message_body_without_rewrapping()
    {
        (IAmazonSQS sqs, List<SendMessageBatchRequest> requests) = AcceptingSqs();
        SqsOutboxPublisher publisher = Publisher(sqs, out _);
        PendingOutboxMessage message = Message(
            "core-critical",
            headersJson: """{"traceparent": "00-abc-def-01"}""");

        OutboxPublishOutcome outcome = await publisher.PublishAsync([message], CancellationToken.None);

        outcome.AcceptedIds.ShouldBe([message.Id]);
        outcome.Failures.ShouldBeEmpty();
        SendMessageBatchRequestEntry entry = requests.ShouldHaveSingleItem().Entries.ShouldHaveSingleItem();
        entry.MessageBody.ShouldBe(StoredPayload);
        entry.MessageAttributes["messageKey"].StringValue.ShouldBe(message.MessageKey);
        entry.MessageAttributes["eventType"].StringValue.ShouldBe(message.EventType);
        entry.MessageAttributes["traceparent"].StringValue.ShouldBe("00-abc-def-01");
    }

    [Fact]
    public async Task Splits_a_destination_into_send_calls_of_at_most_ten_entries()
    {
        (IAmazonSQS sqs, List<SendMessageBatchRequest> requests) = AcceptingSqs();
        SqsOutboxPublisher publisher = Publisher(sqs, out _);
        PendingOutboxMessage[] messages = [.. Enumerable.Range(0, 25).Select(_ => Message("core-transactional"))];

        OutboxPublishOutcome outcome = await publisher.PublishAsync(messages, CancellationToken.None);

        outcome.AcceptedIds.Count.ShouldBe(25);
        requests.Count.ShouldBe(3);
        requests.Sum(request => request.Entries.Count).ShouldBe(25);
        requests.ShouldAllBe(request => request.Entries.Count <= 10);
    }

    [Fact]
    public async Task A_missing_queue_fails_its_whole_destination_reports_health_and_never_creates_a_queue()
    {
        (IAmazonSQS sqs, List<SendMessageBatchRequest> requests) = AcceptingSqs();
        sqs.GetQueueUrlAsync("core-missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GetQueueUrlResponse>(
                new QueueDoesNotExistException("fila inexistente")));
        SqsOutboxPublisher publisher = Publisher(sqs, out OutboxRelayHealthState healthState);
        PendingOutboxMessage missing = Message("core-missing");
        PendingOutboxMessage healthy = Message("core-critical");

        OutboxPublishOutcome outcome = await publisher.PublishAsync([missing, healthy], CancellationToken.None);

        outcome.AcceptedIds.ShouldBe([healthy.Id]);
        OutboxPublishFailure failure = outcome.Failures.ShouldHaveSingleItem();
        failure.MessageId.ShouldBe(missing.Id);
        failure.Reason.ShouldBe("queue-missing");
        healthState.MissingQueues.Keys.ShouldBe(["core-missing"]);
        requests.ShouldAllBe(request => !request.QueueUrl.Contains("core-missing"));
        await sqs.DidNotReceiveWithAnyArgs().CreateQueueAsync(default(CreateQueueRequest)!, default);
        await sqs.DidNotReceiveWithAnyArgs().CreateQueueAsync(default(string)!, default);
    }

    [Fact]
    public async Task Entries_the_batch_call_rejects_stay_out_of_the_accepted_set()
    {
        IAmazonSQS sqs = Substitute.For<IAmazonSQS>();
        sqs.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new GetQueueUrlResponse { QueueUrl = $"http://sqs/{call.Arg<string>()}" });
        PendingOutboxMessage accepted = Message("core-critical");
        PendingOutboxMessage rejected = Message("core-critical");
        sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageBatchResponse
            {
                Successful = [new SendMessageBatchResultEntry { Id = accepted.Id.ToString("N") }],
                Failed =
                [
                    new BatchResultErrorEntry
                    {
                        Id = rejected.Id.ToString("N"),
                        Code = "InvalidMessageContents",
                        Message = "conteúdo rejeitado",
                    },
                ],
            });
        SqsOutboxPublisher publisher = Publisher(sqs, out _);

        OutboxPublishOutcome outcome = await publisher.PublishAsync([accepted, rejected], CancellationToken.None);

        outcome.AcceptedIds.ShouldBe([accepted.Id]);
        outcome.Failures.ShouldHaveSingleItem().MessageId.ShouldBe(rejected.Id);
    }

    private static (IAmazonSQS Sqs, List<SendMessageBatchRequest> Requests) AcceptingSqs()
    {
        IAmazonSQS sqs = Substitute.For<IAmazonSQS>();
        sqs.GetQueueUrlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new GetQueueUrlResponse { QueueUrl = $"http://sqs/{call.Arg<string>()}" });
        var requests = new List<SendMessageBatchRequest>();
        sqs.SendMessageBatchAsync(Arg.Do<SendMessageBatchRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(call => new SendMessageBatchResponse
            {
                Successful = [.. call.Arg<SendMessageBatchRequest>().Entries
                    .Select(entry => new SendMessageBatchResultEntry { Id = entry.Id })],
            });
        return (sqs, requests);
    }

    private static SqsOutboxPublisher Publisher(IAmazonSQS sqs, out OutboxRelayHealthState healthState)
    {
        healthState = new OutboxRelayHealthState();
        return new SqsOutboxPublisher(
            sqs,
            new SqsQueueUrlResolver(sqs, Options.Create(new OutboxSqsOptions())),
            Options.Create(new OutboxRelayOptions()),
            healthState,
            TimeProvider.System,
            NullLogger<SqsOutboxPublisher>.Instance);
    }

    private static PendingOutboxMessage Message(string destination, string headersJson = "{}")
        => new(
            Guid.CreateVersion7(),
            destination,
            "notification.accepted",
            $"cus_{Guid.NewGuid():N}",
            headersJson,
            StoredPayload,
            DateTimeOffset.UtcNow);
}
