using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// The two scans that ask the Core for the next plan step. Every assertion
/// here is about which rows the predicate selects and how many times it asks,
/// because both halves of the defect this slice exists to avoid are counting
/// defects: asking zero times leaves a critical notification undelivered in
/// silence, and asking twice sends the same person the same message twice.
/// </summary>
[Collection(SchedulerScanCollectionDefinition.Name)]
public sealed class OverdueFallbackScanTests(SchedulerScanFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_elapsed_deadline_asks_for_the_next_step_exactly_once()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Sent,
            CreatedAt = now.AddMinutes(-2),
            FallbackDeadline = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-2),
        });

        (await fixture.RunOverdueScanAsync()).DeadlineRequested.ShouldBeGreaterThanOrEqualTo(1);
        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(1);

        // The round that follows is the whole point. The plan claim lives in
        // the handler, so nothing about the attempt has changed yet by the time
        // the next round runs; without a record that this one already asked,
        // the scan writes another trigger every five seconds until the message
        // it wrote is finally processed.
        await fixture.RunOverdueScanAsync();
        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(
            1,
            "a varredura pediu de novo antes de o handler responder ao primeiro pedido: "
            + "uma linha de outbox por ciclo por attempt é desperdício mensurável e o handler "
            + "devolveria Duplicate para todas menos uma.");
    }

    [RequiresDockerFact]
    public async Task An_attempt_whose_step_already_advanced_is_never_asked_for()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Sent,
            CreatedAt = now.AddMinutes(-2),
            FallbackDeadline = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-2),
        });
        await fixture.ExecuteNotificationsDbAsync(db => db.NotificationAttempts
            .Where(attempt => attempt.Id == seeded.AttemptId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.PlanAdvancedAt, now.AddSeconds(-30))));

        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(
            0,
            "o passo já avançou, então pedir de novo compraria um segundo attempt do mesmo "
            + "passo, que é a duplicata de mensagem que o claim existe para impedir.");
    }

    /// <summary>
    /// The record of the ask is a window and not a flag, and this is why. A
    /// trigger that never reached its handler would otherwise park the step
    /// forever with nobody left to ask, and the failure would be invisible: the
    /// attempt simply never advances.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_request_nobody_answered_returns_to_the_scan_when_it_ages_out()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Sent,
            CreatedAt = now.AddMinutes(-2),
            FallbackDeadline = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-2),
        });
        await fixture.RunOverdueScanAsync();
        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(1);

        // Past the retry window with the attempt untouched: whatever the first
        // trigger was going to do, it never did it.
        fixture.Clock.Advance(new SchedulerScanOptions().FallbackRequestRetry + TimeSpan.FromMinutes(1));
        OverdueFallbackScanResultView result = await fixture.RunOverdueScanAsync();

        result.StaleRequestsReleased.ShouldBeGreaterThanOrEqualTo(1);
        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(2);
    }

    /// <summary>
    /// The defect the join to the notification exists to prevent. An attempt
    /// whose plan ended without advancing its step keeps a deadline and an
    /// empty claim forever, so a scan that only looked at the attempt would ask
    /// for its next step once per round for the life of the partition.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_notification_that_already_ended_is_never_asked_for()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Sent,
            CreatedAt = now.AddMinutes(-2),
            FallbackDeadline = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-2),
        });
        await fixture.ExecuteNotificationsDbAsync(db => db.Notifications
            .Where(notification => notification.Id == seeded.NotificationId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(notification => notification.Status, NotificationStatuses.Failed)));

        await fixture.RunOverdueScanAsync();
        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(
            0,
            "a notificação já terminou, e o attempt guarda prazo e claim vazio para sempre: "
            + "sem o estado da notificação no predicado a varredura pediria o passo seguinte "
            + "a cada rodada, e o handler gravaria trilha de duplicata a cada uma.");
    }

    [RequiresDockerFact]
    public async Task A_trigger_of_an_authentication_flow_names_the_authentication_queue()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            AuthFlow = true,
            Status = NotificationAttemptStatuses.Sent,
            CreatedAt = now.AddMinutes(-2),
            FallbackDeadline = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-2),
        });

        await fixture.RunOverdueScanAsync();

        (await fixture.FallbackDestinationAsync(seeded.NotificationId)).ShouldBe(
            OutboxBands.AuthDestination,
            "a banda de drenagem do relay sai do destino, então a segunda metade de um código "
            + "de autenticação tem de manter a banda que a primeira teve.");
    }

    [RequiresDockerTheory]
    [InlineData("critical", false, 1)]
    [InlineData("transactional", true, 1)]
    [InlineData("transactional", false, 0)]
    [InlineData("operational", false, 0)]
    public async Task An_inconclusive_verdict_asks_only_where_waiting_costs_more_than_asking(
        string priorityClass,
        bool authFlow,
        int expectedTriggers)
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = priorityClass,
            AuthFlow = authFlow,
            Status = NotificationAttemptStatuses.Unknown,
            CreatedAt = now.AddMinutes(-5),
            FallbackDeadline = now.AddMinutes(-4),
            StatusChangedAt = now.AddMinutes(-3),
        });

        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(expectedTriggers);
    }

    [RequiresDockerFact]
    public async Task An_inconclusive_verdict_inside_the_grace_period_is_left_alone()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        TimeSpan grace = new SchedulerScanOptions().UnknownGrace;
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Unknown,
            CreatedAt = now.AddMinutes(-5),

            // Deliberately not overdue by the deadline either, so the only
            // rule that could select this row is the grace period.
            FallbackDeadline = now.AddMinutes(30),
            StatusChangedAt = now - grace + TimeSpan.FromSeconds(10),
        });

        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(0);
    }

    /// <summary>
    /// The liability the migration accepted: an attempt parked before the age
    /// column existed carries no age, and a scan must not act on an age nobody
    /// can compute. Reconciliation owns those rows, not this scan.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_whose_age_nobody_knows_is_never_asked_for()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Unknown,
            CreatedAt = now.AddDays(-1),
            FallbackDeadline = now.AddMinutes(30),
            StatusChangedAt = null,
        });

        await fixture.RunOverdueScanAsync();

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(0);
    }

    /// <summary>
    /// Two replicas of the role, two connection pools, scanning at the same
    /// time over the same backlog. The batch is deliberately far smaller than
    /// the backlog so the two of them interleave over many rounds instead of
    /// one of them taking everything on the first pass and the other finding
    /// an empty table.
    /// </summary>
    [RequiresDockerFact]
    public async Task Two_replicas_scanning_at_once_ask_once_per_attempt()
    {
        const int Backlog = 40;
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        List<SeededAttempt> seeded = [];
        for (var index = 0; index < Backlog; index++)
        {
            seeded.Add(await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
            {
                Class = NotificationClasses.Critical,
                Status = NotificationAttemptStatuses.Sent,
                CreatedAt = now.AddMinutes(-2),
                FallbackDeadline = now.AddMinutes(-1),
                StatusChangedAt = now.AddMinutes(-2),
            }));
        }

        IDictionary<string, string?> smallBatches = new Dictionary<string, string?>
        {
            [$"{SchedulerScanOptions.SectionName}:BatchSize"] = "3",
        };
        await using ServiceProvider first = fixture.BuildReplicaWith(smallBatches);
        await using ServiceProvider second = fixture.BuildReplicaWith(smallBatches);

        await Task.WhenAll(DrainAsync(first), DrainAsync(second));

        foreach (SeededAttempt attempt in seeded)
        {
            (await fixture.CountFallbackTriggersAsync(attempt.NotificationId)).ShouldBe(
                1,
                "duas réplicas varrendo em paralelo produziram efeito duplicado para o mesmo "
                + "attempt; o pedido tem de ser reivindicado por linha, senão cada réplica "
                + "compra o mesmo passo seguinte.");
        }
    }

    private static async Task DrainAsync(ServiceProvider provider)
    {
        for (var round = 0; round < 40; round++)
        {
            using IServiceScope scope = provider.CreateScope();
            OverdueFallbackScanResult result = await scope.ServiceProvider
                .GetRequiredService<OverdueFallbackScan>()
                .RunAsync(CancellationToken.None);
            if (result is { DeadlineRequested: 0, UnknownRequested: 0 })
            {
                return;
            }
        }
    }
}
