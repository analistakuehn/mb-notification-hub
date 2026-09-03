using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatching;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchEnvelopeBindingTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task Crossed_notification_and_attempt_ids_are_discarded_without_dispatch_effects()
    {
        EmailDispatchScenario scenario = await PrepareEmailDispatchesAsync(2);
        EmailDispatch first = scenario.Dispatches[0];
        EmailDispatch second = scenario.Dispatches[1];
        var killSwitch = new RecordingKillSwitch();
        var provider = new CountingProvider();
        var resolver = new CountingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);
        DispatchEffects before = await ReadEffectsAsync();

        MessageDisposition disposition = await ProcessAsync(
            dispatcher,
            CreateEnvelope(first.NotificationId, second.Attempt.Id));

        MessageDisposition.Discard discarded = disposition.ShouldBeOfType<MessageDisposition.Discard>();
        discarded.Reason.ShouldBe(DispatchMessageProcessor.ReasonAttemptNotFound);
        killSwitch.CallCount.ShouldBe(0);
        resolver.CallCount.ShouldBe(0);
        provider.CallCount.ShouldBe(0);
        (await ReadEffectsAsync()).ShouldBe(before);
        await AssertStillQueuedAsync(first.Attempt.Id);
        await AssertStillQueuedAsync(second.Attempt.Id);
        await AssertNotificationDispatchedAsync(first.NotificationId);
        await AssertNotificationDispatchedAsync(second.NotificationId);
    }

    [RequiresDockerFact]
    public async Task Matching_notification_and_attempt_ids_complete_the_normal_dispatch_flow()
    {
        EmailDispatchScenario scenario = await PrepareEmailDispatchesAsync(1);
        EmailDispatch dispatch = scenario.Dispatches.ShouldHaveSingleItem();
        var killSwitch = new RecordingKillSwitch();
        var provider = new CountingProvider();
        var resolver = new CountingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(
            dispatcher,
            CreateEnvelope(dispatch.NotificationId, dispatch.Attempt.Id));

        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        killSwitch.Scopes.ShouldBe([
            KillSwitchScope.Application,
            KillSwitchScope.Channel,
            KillSwitchScope.Application,
            KillSwitchScope.Channel,
        ]);
        killSwitch.Keys.ShouldBe([
            scenario.Application,
            Channel.Email.Value,
            scenario.Application,
            Channel.Email.Value,
        ]);
        resolver.CallCount.ShouldBe(1);
        provider.CallCount.ShouldBe(1);
        NotificationAttempt stored = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(attempt => attempt.Id == dispatch.Attempt.Id));
        stored.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        stored.ProviderKey.ShouldBe(CountingProvider.Key);
        stored.ProviderMessageId.ShouldBe(CountingProvider.MessageId);
        await AssertNotificationDispatchedAsync(dispatch.NotificationId);
    }

    private async Task<EmailDispatchScenario> PrepareEmailDispatchesAsync(int count)
    {
        var application = DispatchApi.NewApplication();
        List<string> templateKeys = [];
        for (var index = 0; index < count; index++)
        {
            (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
                fixture,
                application,
                NotificationClasses.Transactional,
                $"envelope-binding-{index}");
            templateKeys.Add(templateKey);
        }

        await DispatchApi.CreatePublishedPolicyAsync(
            fixture,
            application,
            NotificationClasses.Transactional,
            (Channel.Email.Value, null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        List<EmailDispatch> dispatches = [];
        foreach (var templateKey in templateKeys)
        {
            Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
                fixture,
                application,
                templateKey,
                NotificationClasses.Transactional,
                recipientId,
                "core-transactional");
            NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.NotificationId == notificationId));
            dispatches.Add(new EmailDispatch(notificationId, attempt));
        }

        return new EmailDispatchScenario(application, dispatches);
    }

    private ServiceProvider BuildDispatcher(
        IKillSwitch killSwitch,
        IChannelProviderResolver resolver)
        => fixture.BuildDispatcherWorkerProvider(replaceServices: services =>
        {
            services.RemoveAll<IKillSwitch>();
            services.AddSingleton(killSwitch);
            services.RemoveAll<IChannelProviderResolver>();
            services.AddSingleton(resolver);
        });

    private static async Task<MessageDisposition> ProcessAsync(
        ServiceProvider dispatcher,
        MessageEnvelope envelope)
    {
        await using AsyncServiceScope scope = dispatcher.CreateAsyncScope();
        DispatchMessageProcessor processor = scope.ServiceProvider
            .GetRequiredService<DispatchMessageProcessor>();
        return await processor.ProcessAsync(envelope, CancellationToken.None);
    }

    private static MessageEnvelope CreateEnvelope(Guid notificationId, Guid attemptId)
        => new()
        {
            MessageId = Guid.NewGuid(),
            Type = DispatchMessages.AttemptQueuedType,
            SchemaVersion = DispatchMessages.SchemaVersion,
            SourceQueue = "dispatch-email-transactional",
            Payload = JsonSerializer.SerializeToElement(new { notificationId, attemptId }),
        };

    private async Task<DispatchEffects> ReadEffectsAsync()
    {
        var outbox = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages.CountAsync());
        var processed = await fixture.QueryPlatformDbAsync(db => db.ProcessedMessages.CountAsync());
        var audit = await fixture.QueryAuditDbAsync(db => db.AuditEvents.CountAsync());
        var holds = await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds.CountAsync());
        return new DispatchEffects(outbox, processed, audit, holds);
    }

    private async Task AssertStillQueuedAsync(Guid attemptId)
    {
        NotificationAttempt stored = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(attempt => attempt.Id == attemptId));
        stored.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        stored.ProviderKey.ShouldBeNull();
        stored.ProviderMessageId.ShouldBeNull();
        stored.SentAt.ShouldBeNull();
        stored.ErrorCode.ShouldBeNull();
    }

    private async Task AssertNotificationDispatchedAsync(Guid notificationId)
    {
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Dispatched);
    }

    private sealed record EmailDispatchScenario(
        string Application,
        IReadOnlyList<EmailDispatch> Dispatches);

    private sealed record EmailDispatch(Guid NotificationId, NotificationAttempt Attempt);

    private sealed record DispatchEffects(int Outbox, int Processed, int Audit, int Holds);

    private sealed class RecordingKillSwitch : IKillSwitch
    {
        private readonly List<KillSwitchScope> _scopes = [];
        private readonly List<string> _keys = [];

        internal int CallCount => _scopes.Count;

        internal IReadOnlyList<KillSwitchScope> Scopes => _scopes;

        internal IReadOnlyList<string> Keys => _keys;

        public ValueTask<KillSwitchEvaluation> EvaluateAsync(
            KillSwitchScope scope,
            string key,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _scopes.Add(scope);
            _keys.Add(key);
            return ValueTask.FromResult(KillSwitchEvaluation.Allowed);
        }
    }

    private sealed class CountingProviderResolver(IChannelProvider provider) : IChannelProviderResolver
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<Result<IChannelProvider>> ResolveAsync(
            Channel channel,
            CancellationToken cancellationToken)
        {
            _ = channel;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(Result.Success(provider));
        }
    }

    private sealed class CountingProvider : IChannelProvider
    {
        internal const string Key = "dispatch-binding-test";
        internal const string MessageId = "dispatch-binding-message";

        private int _callCount;

        public Channel Channel => Channel.Email;

        public string ProviderKey => Key;

        public bool CarriesAttachments => true;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(ProviderResult.Accepted(MessageId));
        }
    }
}
