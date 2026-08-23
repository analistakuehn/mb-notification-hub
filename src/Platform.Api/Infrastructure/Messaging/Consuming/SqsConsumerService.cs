using Amazon.SQS;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>The queues one consuming role drains, with their slot-allocation ranks.</summary>
internal sealed record SqsConsumerPlan<TProcessor>(IReadOnlyList<SqsQueueBinding> Queues)
    where TProcessor : ISqsMessageProcessor;

/// <summary>
/// Hosts one consume loop per queue of the role's plan. Every loop long-polls
/// its own queue; the shared slot allocator applies the priority, so polling
/// stays independent while processing capacity flows to the highest band. A
/// failed pass rests one idle interval and resumes; the messages are still on
/// the queue.
/// </summary>
internal sealed class SqsConsumerService<TProcessor> : BackgroundService
    where TProcessor : ISqsMessageProcessor
{
    private readonly IReadOnlyList<SqsQueueConsumer<TProcessor>> _consumers;
    private readonly string _queueNames;
    private readonly IOptions<SqsConsumerOptions> _options;
    private readonly ILogger<SqsConsumerService<TProcessor>> _logger;

    public SqsConsumerService(
        SqsConsumerPlan<TProcessor> plan,
        IAmazonSQS sqs,
        SqsQueueUrlResolver queueUrlResolver,
        IServiceScopeFactory scopeFactory,
        IOptions<SqsConsumerOptions> options,
        TimeProvider timeProvider,
        ILogger<SqsConsumerService<TProcessor>> logger)
    {
        _options = options;
        _logger = logger;
        var slots = new PrioritySlotAllocator(options.Value.Concurrency);
        _consumers = [.. plan.Queues.Select(binding => new SqsQueueConsumer<TProcessor>(
            binding, sqs, queueUrlResolver, slots, scopeFactory, options, timeProvider, logger))];
        _queueNames = string.Join(",", _consumers.Select(consumer => consumer.QueueName));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.ConsumerServiceStarted(_queueNames, _options.Value.Concurrency);
        try
        {
            await Task.WhenAll(_consumers.Select(consumer => RunLoopAsync(consumer, stoppingToken)));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: in-flight messages return via visibility timeout.
        }
    }

    private async Task RunLoopAsync(SqsQueueConsumer<TProcessor> consumer, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Value.IdleInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            SqsConsumePassResult result;
            try
            {
                result = await consumer.RunPassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.ConsumerPassFailed(consumer.QueueName, exception);
                result = SqsConsumePassResult.None;
            }

            if (result.Received == 0)
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
    }
}
