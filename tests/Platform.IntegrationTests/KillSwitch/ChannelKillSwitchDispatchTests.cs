using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class ChannelKillSwitchDispatchTests(CorePipelineFixture fixture)
{
    private const string UnavailableReason = "channel-kill-switch-unavailable";

    [RequiresDockerFact]
    public async Task A_blocked_channel_holds_the_queued_attempt_before_provider_resolution()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Blocked);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        killSwitch.CallCount.ShouldBe(2);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Channel);
        killSwitch.LastKey.ShouldBe(scenario.Attempt.Channel);
        resolver.CallCount.ShouldBe(0);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        KillSwitchHold hold = await RequiredDispatchHoldAsync(scenario.Attempt.Id);
        AssertMinimalDispatchHold(
            hold, scenario, KillSwitchScopes.Channel, scenario.Attempt.Channel);
    }

    [RequiresDockerFact]
    public async Task A_blocked_application_holds_the_queued_attempt_before_provider_resolution()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(KillSwitchEvaluation.Blocked);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        killSwitch.CallCount.ShouldBe(1);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Application);
        killSwitch.LastKey.ShouldBe(scenario.Application);
        resolver.CallCount.ShouldBe(0);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        KillSwitchHold hold = await RequiredDispatchHoldAsync(scenario.Attempt.Id);
        AssertMinimalDispatchHold(
            hold, scenario, KillSwitchScopes.Application, scenario.Application);
    }

    [RequiresDockerFact]
    public async Task A_channel_blocked_after_claim_reverts_and_holds_before_send()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Blocked);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        killSwitch.CallCount.ShouldBe(4);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Channel);
        killSwitch.LastKey.ShouldBe(scenario.Attempt.Channel);
        resolver.CallCount.ShouldBe(1);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        KillSwitchHold hold = await RequiredDispatchHoldAsync(scenario.Attempt.Id);
        AssertMinimalDispatchHold(
            hold, scenario, KillSwitchScopes.Channel, scenario.Attempt.Channel);
    }

    [RequiresDockerFact]
    public async Task An_application_blocked_after_claim_reverts_and_holds_before_send()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Blocked);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        disposition.ShouldBeOfType<MessageDisposition.Processed>();
        killSwitch.CallCount.ShouldBe(3);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Application);
        killSwitch.LastKey.ShouldBe(scenario.Application);
        resolver.CallCount.ShouldBe(1);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        KillSwitchHold hold = await RequiredDispatchHoldAsync(scenario.Attempt.Id);
        AssertMinimalDispatchHold(
            hold, scenario, KillSwitchScopes.Application, scenario.Application);
    }

    [RequiresDockerFact]
    public async Task An_unavailable_channel_postpones_before_claim_without_a_hold_or_provider()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Unavailable);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        MessageDisposition.Postponed postponed =
            disposition.ShouldBeOfType<MessageDisposition.Postponed>();
        postponed.Delay.ShouldBeNull();
        postponed.Reason.ShouldBe(UnavailableReason);
        killSwitch.CallCount.ShouldBe(2);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Channel);
        killSwitch.LastKey.ShouldBe(scenario.Attempt.Channel);
        resolver.CallCount.ShouldBe(0);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        (await DispatchHoldAsync(scenario.Attempt.Id)).ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task An_unavailable_application_postpones_before_claim_without_a_hold_or_provider()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(KillSwitchEvaluation.Unavailable);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        MessageDisposition.Postponed postponed =
            disposition.ShouldBeOfType<MessageDisposition.Postponed>();
        postponed.Delay.ShouldBeNull();
        postponed.Reason.ShouldBe(ApplicationKillSwitchGate.UnavailableReason);
        killSwitch.CallCount.ShouldBe(1);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Application);
        killSwitch.LastKey.ShouldBe(scenario.Application);
        resolver.CallCount.ShouldBe(0);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        (await DispatchHoldAsync(scenario.Attempt.Id)).ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task A_channel_unavailable_after_claim_reverts_and_postpones_before_send()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Unavailable);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        MessageDisposition.Postponed postponed =
            disposition.ShouldBeOfType<MessageDisposition.Postponed>();
        postponed.Delay.ShouldBeNull();
        postponed.Reason.ShouldBe(UnavailableReason);
        killSwitch.CallCount.ShouldBe(4);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Channel);
        killSwitch.LastKey.ShouldBe(scenario.Attempt.Channel);
        resolver.CallCount.ShouldBe(1);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        (await DispatchHoldAsync(scenario.Attempt.Id)).ShouldBeNull();
    }

    [RequiresDockerFact]
    public async Task An_application_unavailable_after_claim_reverts_and_postpones_before_send()
    {
        DispatchScenario scenario = await PrepareEmailDispatchAsync();
        var killSwitch = new ScriptedKillSwitch(
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Allowed,
            KillSwitchEvaluation.Unavailable);
        var provider = new TargetCountingProvider(scenario.NotificationId);
        var resolver = new RecordingProviderResolver(provider);
        await using ServiceProvider dispatcher = BuildDispatcher(killSwitch, resolver);

        MessageDisposition disposition = await ProcessAsync(dispatcher, scenario.Envelope);

        MessageDisposition.Postponed postponed =
            disposition.ShouldBeOfType<MessageDisposition.Postponed>();
        postponed.Delay.ShouldBeNull();
        postponed.Reason.ShouldBe(ApplicationKillSwitchGate.UnavailableReason);
        killSwitch.CallCount.ShouldBe(3);
        killSwitch.LastScope.ShouldBe(KillSwitchScope.Application);
        killSwitch.LastKey.ShouldBe(scenario.Application);
        resolver.CallCount.ShouldBe(1);
        provider.TargetCallCount.ShouldBe(0);
        await AssertAttemptQueuedWithoutProviderAsync(scenario.Attempt.Id);
        (await DispatchHoldAsync(scenario.Attempt.Id)).ShouldBeNull();
    }

    private async Task<DispatchScenario> PrepareEmailDispatchAsync()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture,
            application,
            NotificationClasses.Transactional,
            "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture,
            application,
            NotificationClasses.Transactional,
            (Channel.Email.Value, null));
        (var recipientId, var email, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        HttpClient producer = fixture.CreateProducerClient(
            "channel-kill-switch-tests",
            NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            new
            {
                application,
                recipientId,
                @class = NotificationClasses.Transactional,
                templateKey,
                locale = "pt-BR",
                variables = new { code = "123456" },
                ttlSeconds = 300,
            },
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid notificationId)
            .ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        OutboxMessage dispatch = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.EventType == DispatchMessages.AttemptQueuedType
                && message.MessageKey == recipientId));
        await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .Where(message => message.Id == dispatch.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.SentAt, DateTimeOffset.UtcNow)));
        MessageEnvelopeParse parsed = MessageEnvelopeParser.Parse(dispatch.PayloadJson);
        parsed.InvalidReason.ShouldBeNull();
        MessageEnvelope envelope = parsed.Envelope! with { SourceQueue = dispatch.Destination };
        return new DispatchScenario(
            notificationId, application, recipientId, email, attempt, envelope);
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

    private async Task AssertAttemptQueuedWithoutProviderAsync(Guid attemptId)
    {
        NotificationAttempt stored = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(attempt => attempt.Id == attemptId));
        stored.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        stored.ProviderKey.ShouldBeNull();
        stored.ProviderMessageId.ShouldBeNull();
    }

    private async Task<KillSwitchHold> RequiredDispatchHoldAsync(Guid attemptId)
    {
        KillSwitchHold? hold = await DispatchHoldAsync(attemptId);
        hold.ShouldNotBeNull();
        return hold;
    }

    private async Task<KillSwitchHold?> DispatchHoldAsync(Guid attemptId)
        => await fixture.QueryNotificationsDbAsync(db => db.KillSwitchHolds
            .AsNoTracking()
            .SingleOrDefaultAsync(hold => hold.WorkKind == KillSwitchWorkKinds.Dispatch
                && hold.WorkId == $"dispatch:{attemptId:N}"));

    private static void AssertMinimalDispatchHold(
        KillSwitchHold hold,
        DispatchScenario scenario,
        string expectedScope,
        string expectedKey)
    {
        hold.Scope.ShouldBe(expectedScope);
        hold.Key.ShouldBe(expectedKey);
        hold.Destination.ShouldBe(scenario.Envelope.SourceQueue);
        hold.ReleasedAt.ShouldBeNull();
        using var claimCheck = JsonDocument.Parse(hold.PayloadJson);
        claimCheck.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Order()
            .ShouldBe(["attemptId", "notificationId"]);
        claimCheck.RootElement.GetProperty("notificationId").GetGuid()
            .ShouldBe(scenario.NotificationId);
        claimCheck.RootElement.GetProperty("attemptId").GetGuid()
            .ShouldBe(scenario.Attempt.Id);
        hold.PayloadJson.ShouldNotContain(scenario.RecipientId);
        hold.PayloadJson.ShouldNotContain(scenario.ContactValue);
        hold.PayloadJson.ShouldNotContain("123456");
        hold.PayloadJson.ShouldNotContain("target");
        hold.PayloadJson.ShouldNotContain("token");
    }

    private sealed record DispatchScenario(
        Guid NotificationId,
        string Application,
        string RecipientId,
        string ContactValue,
        NotificationAttempt Attempt,
        MessageEnvelope Envelope);

    private sealed class ScriptedKillSwitch(params KillSwitchEvaluation[] evaluations) : IKillSwitch
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);

        internal KillSwitchScope? LastScope { get; private set; }

        internal string? LastKey { get; private set; }

        public ValueTask<KillSwitchEvaluation> EvaluateAsync(
            KillSwitchScope scope,
            string key,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastScope = scope;
            LastKey = key;
            var index = Interlocked.Increment(ref _callCount) - 1;
            return ValueTask.FromResult(evaluations[Math.Min(index, evaluations.Length - 1)]);
        }
    }

    private sealed class RecordingProviderResolver(IChannelProvider provider) : IChannelProviderResolver
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

    private sealed class TargetCountingProvider(Guid targetNotificationId) : IChannelProvider
    {
        private int _targetCallCount;

        public Channel Channel => Channel.Email;

        public string ProviderKey => "fake-channel";

        public bool CarriesAttachments => true;

        internal int TargetCallCount => Volatile.Read(ref _targetCallCount);

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (request.Correlation?.NotificationId == targetNotificationId)
            {
                Interlocked.Increment(ref _targetCallCount);
            }

            return Task.FromResult(ProviderResult.Accepted("fake-message"));
        }
    }
}
