using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The plan of one notification advances once per step, whatever asked for it.
/// Two producers of the same trigger exist by design (an exhausted step, and a
/// deadline that elapsed with no answer) and they write two distinct queue
/// rows with two distinct message identities, so message deduplication cannot
/// tell them apart. Every test here runs the two triggers in two real
/// concurrent transactions, because two calls in sequence would pass against a
/// handler with no claim at all.
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

        return new Scenario(notificationId, recipientId);
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

    private sealed record Scenario(Guid NotificationId, string RecipientId);
}
