using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The channel that stops itself when its provider circuit stays open, and
/// everything that must not stop it. The stop is global and its reversal is
/// human, so what is asserted here is both the effect and its absence.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class AutomaticChannelKillSwitchTests(CorePipelineFixture fixture)
{
    private const string SmsChannel = "sms";
    private const string SwitchEntityId = "channel:sms";
    private const string EnabledKey = "Modules:Notifications:AutomaticChannelKillSwitch:Enabled";

    /// <summary>Past the shipped ten-minute window, without configuring it: the default is the subject.</summary>
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

    [RequiresDockerFact]
    public async Task A_circuit_open_past_the_window_stops_the_channel_with_a_system_actor_and_a_trail()
    {
        IReadOnlyList<QueuedDispatch> dispatches = await PrepareAsync(notifications: 2);
        await ClearChannelSwitchAsync();
        var trailBefore = await TrailCountAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using ServiceProvider dispatcher = BuildDispatcher(clock, gateEnabled: true);

        try
        {
            (await ProcessAsync(dispatcher, dispatches[0]))
                .ShouldBeOfType<MessageDisposition.Postponed>()
                .Reason.ShouldBe(DispatchMessageProcessor.ReasonCircuitOpen);

            // One observation is a blip; ten minutes of them is an outage.
            (await SwitchAsync()).ShouldBeNull();

            clock.Advance(PastTheWindow);
            await ProcessAsync(dispatcher, dispatches[1]);

            KillSwitchState? stopped = await SwitchAsync();
            stopped.ShouldNotBeNull();
            stopped!.State.ShouldBe(KillSwitchStates.Active);
            stopped.Actor.ShouldBe("dispatcher");

            (await TrailCountAsync() - trailBefore).ShouldBe(1);
            var details = await LastTrailDetailsAsync();
            details.ShouldContain(AutomaticChannelKillSwitch.StopReason);
            (await LastTrailActorTypeAsync()).ShouldBe("system");
        }
        finally
        {
            // The switch is global and this collection shares one store: a
            // channel left stopped would silently hold every neighbour's SMS.
            await ClearChannelSwitchAsync();
        }
    }

    [RequiresDockerFact]
    public async Task With_the_gate_off_which_is_the_default_nothing_stops_the_channel()
    {
        IReadOnlyList<QueuedDispatch> dispatches = await PrepareAsync(notifications: 2);
        await ClearChannelSwitchAsync();
        var trailBefore = await TrailCountAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);

        // No gate setting at all: what is under test is the shipped default,
        // not a value this test wrote.
        await using ServiceProvider dispatcher = BuildDispatcher(clock, gateEnabled: null);

        try
        {
            await ProcessAsync(dispatcher, dispatches[0]);
            clock.Advance(PastTheWindow);
            await ProcessAsync(dispatcher, dispatches[1]);

            (await SwitchAsync()).ShouldBeNull(
                "o canal parou sozinho com o gate desligado: uma instância degradada pararia o "
                + "canal inteiro e o OTP ficaria em espera até vencer.");
            (await TrailCountAsync()).ShouldBe(trailBefore);
        }
        finally
        {
            await ClearChannelSwitchAsync();
        }
    }

    [RequiresDockerFact]
    public async Task A_validity_that_ran_out_never_feeds_the_window()
    {
        IReadOnlyList<QueuedDispatch> dispatches = await PrepareAsync(notifications: 3);
        await ExpireAsync(dispatches[1].NotificationId);
        await ClearChannelSwitchAsync();
        var trailBefore = await TrailCountAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using ServiceProvider dispatcher = BuildDispatcher(clock, gateEnabled: true);

        try
        {
            await ProcessAsync(dispatcher, dispatches[0]);
            clock.Advance(PastTheWindow);

            // The attempt ends here as a definitive failure, like any other,
            // and its code is the customer's deadline, not the provider's
            // health. It never reached a provider, so it cannot say the
            // provider is degraded.
            await ProcessAsync(dispatcher, dispatches[1]);

            (await SwitchAsync()).ShouldBeNull(
                "a expiração de TTL contou como falha de provedor e parou o canal: o kill switch "
                + "dispararia por prazo do cliente, e pior, exatamente quando a fila está atrasada.");
            (await TrailCountAsync()).ShouldBe(trailBefore);

            // Falsification: the window and the gate are working, and what the
            // silence above proves is that the expiry alone does not feed them.
            await ProcessAsync(dispatcher, dispatches[2]);
            (await SwitchAsync()).ShouldNotBeNull();
        }
        finally
        {
            await ClearChannelSwitchAsync();
        }
    }

    /// <summary>
    /// SMS notifications queued with an hour of validity, so moving the clock
    /// past the observation window says nothing about their own deadline.
    /// </summary>
    private async Task<IReadOnlyList<QueuedDispatch>> PrepareAsync(int notifications)
    {
        var application = DispatchApi.NewApplication();
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("sms", null));
        (var recipientId, _) = await DispatchApi.RegisterSmsRecipientAsync(fixture);

        List<QueuedDispatch> dispatches = [];
        for (var index = 0; index < notifications; index++)
        {
            (var templateKey, _) = await DispatchApi.CreatePublishedSmsTemplateAsync(
                fixture, application, "transactional", $"circuit-{index}");
            Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
                fixture, application, templateKey, "transactional", recipientId, "core-transactional",
                ttlSeconds: 3600);
            Guid attemptId = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
                .AsNoTracking()
                .Where(candidate => candidate.NotificationId == notificationId)
                .Select(candidate => candidate.Id)
                .SingleAsync());
            dispatches.Add(new QueuedDispatch(notificationId, attemptId));
        }

        return dispatches;
    }

    private ServiceProvider BuildDispatcher(MutableTimeProvider clock, bool? gateEnabled)
    {
        Dictionary<string, string?> settings = [];
        if (gateEnabled is { } enabled)
        {
            settings[EnabledKey] = enabled ? "true" : "false";
        }

        return fixture.BuildDispatcherWorkerProvider(settings, replaceServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);

            // The circuit is what this measures, and an adapter would only
            // reach it through failing calls this test would have to stage.
            services.RemoveAll<IChannelProviderResolver>();
            services.AddSingleton<IChannelProviderResolver>(new OpenCircuitResolver());
        });
    }

    private static async Task<MessageDisposition> ProcessAsync(
        ServiceProvider dispatcher,
        QueuedDispatch dispatch)
    {
        await using AsyncServiceScope scope = dispatcher.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DispatchMessageProcessor>()
            .ProcessAsync(Envelope(dispatch), CancellationToken.None);
    }

    private static MessageEnvelope Envelope(QueuedDispatch dispatch)
        => new()
        {
            MessageId = Guid.CreateVersion7(),
            Type = DispatchMessages.AttemptQueuedType,
            SchemaVersion = DispatchMessages.SchemaVersion,
            SourceQueue = "dispatch-sms-transactional",
            Payload = JsonSerializer.SerializeToElement(new
            {
                notificationId = dispatch.NotificationId,
                attemptId = dispatch.AttemptId,
            }),
        };

    private async Task<KillSwitchState?> SwitchAsync()
        => await fixture.QueryNotificationsDbAsync(db => db.KillSwitches
            .AsNoTracking()
            .SingleOrDefaultAsync(entry =>
                entry.Scope == KillSwitchScopes.Channel && entry.Key == SmsChannel));

    private async Task ClearChannelSwitchAsync()
        => await fixture.QueryNotificationsDbAsync(db => db.KillSwitches
            .Where(entry => entry.Scope == KillSwitchScopes.Channel && entry.Key == SmsChannel)
            .ExecuteDeleteAsync());

    private async Task<int> TrailCountAsync()
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.EntityId == SwitchEntityId));

    private async Task<string> LastTrailDetailsAsync()
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == SwitchEntityId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => entry.DetailsJson)
            .FirstAsync());

    private async Task<string> LastTrailActorTypeAsync()
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(entry => entry.EntityId == SwitchEntityId)
            .OrderByDescending(entry => entry.OccurredAt)
            .Select(entry => entry.ActorType)
            .FirstAsync());

    private async Task ExpireAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .Where(candidate => candidate.Id == notificationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                candidate => candidate.ExpiresAt,
                DateTimeOffset.UtcNow.AddMinutes(-1))));

    private sealed record QueuedDispatch(Guid NotificationId, Guid AttemptId);

    /// <summary>An SMS provider whose pipeline refuses every call with the circuit open.</summary>
    private sealed class OpenCircuitResolver : IChannelProviderResolver
    {
        private readonly OpenCircuitProvider _provider = new();

        public Task<Result<IChannelProvider>> ResolveAsync(
            Channel channel,
            CancellationToken cancellationToken)
        {
            _ = channel;
            _ = cancellationToken;
            return Task.FromResult(Result.Success<IChannelProvider>(_provider));
        }
    }

    private sealed class OpenCircuitProvider : IChannelProvider
    {
        public Channel Channel => Channel.Sms;

        public string ProviderKey => "twilio";

        public bool CarriesAttachments => false;

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(ProviderResult.Transient(
                DispatchMessageProcessor.CircuitOpenErrorCode, null));
        }
    }
}
