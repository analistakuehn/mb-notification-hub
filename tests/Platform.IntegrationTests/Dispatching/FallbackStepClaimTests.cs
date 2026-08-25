using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.Notifications.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The plan of one notification advances once per step, whatever asked for it.
/// Two producers of the same trigger exist by design (an exhausted step, and a
/// deadline that elapsed with no answer) and they write two distinct queue
/// rows with two distinct message identities, so message deduplication cannot
/// tell them apart. Each claim test runs the two triggers in two real
/// concurrent transactions, because two calls in sequence would pass against a
/// handler with no claim at all. The last two cover the other ways a step can
/// be settled twice: a trigger whose pair of identifiers does not belong
/// together, and a dispatcher that still holds the message of a step the
/// deadline scan already asked for.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class FallbackStepClaimTests(CorePipelineFixture fixture)
{
    private const string FcmAccepted = """{"name":"projects/test-project/messages/0:1"}""";

    private const string InvalidArgumentBody = """
        {"error":{"code":400,"message":"Invalid argument.","status":"INVALID_ARGUMENT",
        "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}
        """;

    [RequiresDockerFact]
    public async Task Two_triggers_of_one_step_from_two_producers_queue_a_single_next_attempt()
    {
        Scenario scenario = await PrepareAsync(deviceCount: 1, acceptPush: false);

        // The trigger the failed dispatch produced, read back from the outbox
        // exactly as the relay would hand it to the Core.
        MessageEnvelope fromDispatcher = await DispatcherTriggerAsync(scenario.NotificationId);
        List<Guid> pushAttempts = await AttemptIdsAsync(scenario.NotificationId, "push");
        pushAttempts.Count.ShouldBe(1);

        // The trigger the deadline scan will produce: same step, same failed
        // attempt, a queue row of its own with a message identity of its own.
        MessageEnvelope fromScheduler = FallbackTrigger(scenario.NotificationId, pushAttempts[0]);
        fromScheduler.MessageId.ShouldNotBe(
            fromDispatcher.MessageId,
            "os dois gatilhos precisam ser mensagens distintas, senão a marca de deduplicação "
            + "resolveria o caso e o claim de avanço não estaria sendo exercitado.");

        IReadOnlyList<MessageDisposition> outcomes = await ProcessConcurrentlyAsync(
            fromDispatcher, fromScheduler);

        // The count of next attempts comes first, because it is the defect
        // itself: more than one row here is more than one message to the
        // same person.
        (await AttemptIdsAsync(scenario.NotificationId, "email")).Count.ShouldBe(
            1,
            "dois gatilhos do mesmo passo produziram mais de um attempt seguinte: "
            + "o destinatário receberia a mesma etapa duas vezes.");
        (await CountTrailAsync(scenario.NotificationId, "fallback.attempt_queued")).ShouldBe(1);
        outcomes.Count(outcome => outcome is MessageDisposition.Processed).ShouldBe(1);
        outcomes.Count(outcome => outcome is MessageDisposition.Duplicate).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Two_expired_push_siblings_of_one_step_advance_the_plan_once()
    {
        Scenario scenario = await PrepareAsync(deviceCount: 2, acceptPush: true);

        // Both siblings were accepted and share the step's absolute deadline,
        // so the deadline scan finds two overdue attempts of the same step and
        // writes one trigger for each.
        List<Guid> siblings = await AttemptIdsAsync(scenario.NotificationId, "push");
        siblings.Count.ShouldBe(2);

        IReadOnlyList<MessageDisposition> outcomes = await ProcessConcurrentlyAsync(
            FallbackTrigger(scenario.NotificationId, siblings[0]),
            FallbackTrigger(scenario.NotificationId, siblings[1]));

        (await AttemptIdsAsync(scenario.NotificationId, "email")).Count.ShouldBe(
            1,
            "dois irmãos vencidos do mesmo passo disputaram o mesmo avanço e ambos passaram: "
            + "o claim é por passo justamente porque o fan-out cria irmãos com um único prazo.");
        (await CountTrailAsync(scenario.NotificationId, "fallback.attempt_queued")).ShouldBe(1);
        outcomes.Count(outcome => outcome is MessageDisposition.Processed).ShouldBe(1);
        outcomes.Count(outcome => outcome is MessageDisposition.Duplicate).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task The_claim_stamps_every_attempt_of_the_step_and_none_of_the_next_one()
    {
        Scenario scenario = await PrepareAsync(deviceCount: 2, acceptPush: true);
        List<Guid> siblings = await AttemptIdsAsync(scenario.NotificationId, "push");

        await ProcessConcurrentlyAsync(
            FallbackTrigger(scenario.NotificationId, siblings[0]),
            FallbackTrigger(scenario.NotificationId, siblings[1]));

        List<NotificationAttempt> attempts = await fixture.QueryNotificationsDbAsync(
            db => db.NotificationAttempts
                .AsNoTracking()
                .Where(candidate => candidate.NotificationId == scenario.NotificationId)
                .OrderBy(candidate => candidate.Sequence)
                .ToListAsync());

        attempts.Where(attempt => attempt.Channel == "push")
            .ShouldAllBe(attempt => attempt.PlanAdvancedAt != null);
        attempts.Where(attempt => attempt.Channel == "email")
            .ShouldAllBe(attempt => attempt.PlanAdvancedAt == null);
    }

    /// <summary>
    /// The two identifiers of a trigger are resolved independently, so a
    /// crossed pair has to be rejected before anything is written. This is the
    /// adversarial case: one notification paired with the failed attempt of
    /// another. Advancing would apply the first notification's published plan
    /// to the second one's channel and cross their audit trails.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_trigger_whose_attempt_belongs_to_another_notification_settles_nothing()
    {
        Scenario first = await PrepareAsync(deviceCount: 1, acceptPush: true);
        Scenario second = await PrepareAsync(deviceCount: 1, acceptPush: true);
        List<Guid> strangerAttempts = await AttemptIdsAsync(second.NotificationId, "push");
        strangerAttempts.Count.ShouldBe(1);

        MessageDisposition outcome = await ProcessOneAsync(
            FallbackTrigger(first.NotificationId, strangerAttempts[0]));

        outcome.ShouldBeOfType<MessageDisposition.Discard>()
            .Reason.ShouldBe(FallbackRequestHandler.ReasonAttemptNotificationMismatch);

        // Neither notification moved: the plan of the first did not advance,
        // neither trail records a queued attempt, and both remain open for
        // their own deadline.
        (await AttemptIdsAsync(first.NotificationId, "email")).ShouldBeEmpty(
            "o gatilho cruzado avançou o plano da notificação errada: "
            + "o destinatário da primeira receberia uma etapa que ninguém pediu.");
        (await AttemptIdsAsync(second.NotificationId, "email")).ShouldBeEmpty();
        (await CountTrailAsync(first.NotificationId, "fallback.attempt_queued")).ShouldBe(0);
        (await CountTrailAsync(second.NotificationId, "fallback.attempt_queued")).ShouldBe(0);
        (await StatusOfAsync(first.NotificationId)).ShouldBe(NotificationStatuses.Dispatched);
        (await StatusOfAsync(second.NotificationId)).ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// The invariant the conditional claim exists for. A step that ran out of
    /// time while its message was still on the queue is asked for by the
    /// deadline scan, and the dispatcher may pick that message up afterwards:
    /// the message is durable and nothing recalls it. Sending it then would put
    /// the same notification on two channels, which is the duplicate the
    /// at-least-once decision refuses at the customer.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_step_asked_for_by_the_deadline_is_no_longer_sendable_by_its_dispatcher()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        // Routed and queued, deliberately never dispatched: this is the window
        // between the message being written and a dispatcher claiming it.
        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");
        List<Guid> pushAttempts = await AttemptIdsAsync(notificationId, "push");
        pushAttempts.Count.ShouldBe(1);

        var clock = new MutableClock(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5));
        await using (ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock)))
        {
            using IServiceScope scope = tracker.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<OverdueFallbackScan>()
                .RunAsync(CancellationToken.None);
        }

        // The stamp of this attempt, not the count of the round: the store is
        // shared with the tests above and their overdue rows are claimed by the
        // same round, so a global count would grade the suite instead of this
        // scenario.
        (await RequestStampOfAsync(pushAttempts[0])).ShouldNotBeNull(
            "a varredura por prazo precisa alcançar a tentativa que ainda está na fila, "
            + "senão o passo vencido nunca é pedido.");

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, FcmAccepted, null));
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth");

        // The provider is the oracle: a send here is a second message to the
        // same person, and no assertion on internal state says that as plainly.
        provider.Requests.Count(request => request.Path != DispatchApi.FcmTokenPath).ShouldBe(
            0,
            "o dispatcher enviou o passo que a varredura já havia pedido: "
            + "o destinatário recebe o canal antigo e o canal do fallback.");
        (await StatusOfAttemptAsync(pushAttempts[0])).ShouldBe(
            NotificationAttemptStatuses.Queued,
            "a reivindicação tinha de ser recusada, então a tentativa não sai de 'queued'.");
    }

    /// <summary>
    /// A republication must not reach a notification already admitted. Both
    /// activating a plan and rolling one back are republications of the class
    /// policy, so a fallback that re-read the current version would make the
    /// documented rollback change the behaviour of messages already accepted.
    /// Here the plan is rolled back to a single step after the notification was
    /// admitted under two: the notification keeps the plan it was accepted
    /// under and still reaches its second channel.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_republished_plan_does_not_reach_a_notification_already_admitted()
    {
        Scenario scenario = await PrepareAsync(deviceCount: 1, acceptPush: true);
        List<Guid> pushAttempts = await AttemptIdsAsync(scenario.NotificationId, "push");
        pushAttempts.Count.ShouldBe(1);

        // The rollback: the same class, published again without the step the
        // notification above was admitted with.
        var rolledBack = await DispatchApi.CreatePublishedPolicyAsync(
            fixture, scenario.Application, "critical", ("push", "30s"));
        rolledBack.ShouldBeGreaterThan(1, "a republicação precisa gerar uma versão nova.");

        MessageDisposition outcome = await ProcessOneAsync(
            FallbackTrigger(scenario.NotificationId, pushAttempts[0]));

        outcome.ShouldBeOfType<MessageDisposition.Processed>();
        (await AttemptIdsAsync(scenario.NotificationId, "email")).Count.ShouldBe(
            1,
            "o fallback usou o plano publicado agora em vez do plano admitido: "
            + "uma republicação passou a mudar o comportamento de mensagem já aceita, "
            + "e o rollback descrito como seguro deixou de ser.");
        (await StatusOfAsync(scenario.NotificationId)).ShouldBe(NotificationStatuses.Dispatched);
    }

    /// <summary>
    /// Accepts one notification of an authentication template over a
    /// two-step plan (push with a deadline, then e-mail), routes it and runs
    /// one dispatch pass. With <paramref name="acceptPush"/> the provider takes
    /// the message and the attempts stay alive with their deadline; without it
    /// the provider refuses and the dispatch side itself asks for the next
    /// step.
    /// </summary>
    private async Task<Scenario> PrepareAsync(int deviceCount, bool acceptPush)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, deviceCount: deviceCount);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : acceptPush
                ? new FakeProviderResponse(200, FcmAccepted, null)
                : new FakeProviderResponse(400, InvalidArgumentBody, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        for (var pass = 0; pass < deviceCount; pass++)
        {
            (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
                .Processed.ShouldBeGreaterThanOrEqualTo(1);
            if (pass < deviceCount - 1)
            {
                // Only between passes: the sibling the fan-out inserted needs
                // its announcement published before a dispatcher can claim it.
                // Relaying after the last pass would publish the trigger this
                // test hands to the handler itself.
                await CorePipelineFixture.RunRelayPassAsync(relay);
            }
        }

        return new Scenario(notificationId, recipientId, application);
    }

    /// <summary>
    /// Runs two triggers through two handlers in two scopes at once. Two
    /// connections and two transactions: the claim of the loser waits on the
    /// row lock of the winner and only resolves once the winner commits, which
    /// is the whole point of proving this with concurrency instead of with two
    /// sequential calls.
    /// </summary>
    private async Task<IReadOnlyList<MessageDisposition>> ProcessConcurrentlyAsync(
        MessageEnvelope first,
        MessageEnvelope second)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope firstScope = core.CreateScope();
        using IServiceScope secondScope = core.CreateScope();
        Task<MessageDisposition> firstRun = firstScope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(first, CancellationToken.None);
        Task<MessageDisposition> secondRun = secondScope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(second, CancellationToken.None);
        return await Task.WhenAll(firstRun, secondRun);
    }

    /// <summary>The trigger the dispatch side wrote, parsed back out of the outbox row it committed.</summary>
    private async Task<MessageEnvelope> DispatcherTriggerAsync(Guid notificationId)
    {
        List<string> payloads = await DispatchApi.ReadOutboxPayloadsAsync(
            fixture, "core-auth", notificationId);
        var trigger = payloads.Single(payload => payload.Contains(
            DispatchMessages.FallbackRequestedType, StringComparison.Ordinal));
        MessageEnvelopeParse parsed = MessageEnvelopeParser.Parse(trigger);
        parsed.InvalidReason.ShouldBeNull();
        return parsed.Envelope! with { SourceQueue = "core-auth" };
    }

    /// <summary>
    /// One trigger of the shape the deadline scan writes: the same claim check
    /// the dispatch side writes, with a message identity of its own.
    /// </summary>
    private static MessageEnvelope FallbackTrigger(Guid notificationId, Guid failedAttemptId)
        => new()
        {
            MessageId = Guid.CreateVersion7(),
            Type = DispatchMessages.FallbackRequestedType,
            SchemaVersion = DispatchMessages.SchemaVersion,
            OccurredAt = DateTimeOffset.UtcNow,
            PriorityClass = "critical",
            SourceQueue = "core-auth",
            Payload = JsonSerializer.SerializeToElement(new { notificationId, failedAttemptId }),
        };

    /// <summary>Runs one trigger through one handler in one scope.</summary>
    private async Task<MessageDisposition> ProcessOneAsync(MessageEnvelope envelope)
    {
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        using IServiceScope scope = core.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<FallbackRequestHandler>()
            .ProcessAsync(envelope, CancellationToken.None);
    }

    private async Task<string> StatusOfAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<DateTimeOffset?> RequestStampOfAsync(Guid attemptId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.Id == attemptId)
            .Select(candidate => candidate.FallbackRequestedAt)
            .SingleAsync());

    private async Task<string> StatusOfAttemptAsync(Guid attemptId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.Id == attemptId)
            .Select(candidate => candidate.Status)
            .SingleAsync());

    private async Task<List<Guid>> AttemptIdsAsync(Guid notificationId, string channel)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(candidate => candidate.NotificationId == notificationId
                && candidate.Channel == channel)
            .OrderBy(candidate => candidate.Sequence)
            .Select(candidate => candidate.Id)
            .ToListAsync());

    private async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == notificationId.ToString()));

    private sealed record Scenario(Guid NotificationId, string RecipientId, string Application);
}
