using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// The knobs of the scheduler are deployment decisions, and this proves they
/// reach the scan rather than only the options binder. A value that binds and
/// is then ignored is the failure that looks exactly like a working
/// configuration until someone tries to use it in an incident.
/// </summary>
[Collection(SchedulerScanCollectionDefinition.Name)]
public sealed class SchedulerScanConfigurationTests(SchedulerScanFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_round_interval_is_configuration_and_defaults_to_two_seconds()
    {
        await using ServiceProvider byDefault = fixture.BuildSecondReplica();
        OptionsOf(byDefault).Interval.ShouldBe(
            TimeSpan.FromSeconds(2),
            "o intervalo é termo do orçamento até o SMS de fallback, e o padrão é o valor que "
            + "mantém a soma dentro do aceite; ele não é uma escolha isolada.");

        await using ServiceProvider tuned = fixture.BuildReplicaWith(
            new Dictionary<string, string?>
            {
                [$"{SchedulerScanOptions.SectionName}:Interval"] = "00:00:05",
            });
        OptionsOf(tuned).Interval.ShouldBe(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The batch size proved through what a round claims, not through what the
    /// binder returns: the statement carries it as a bind value, so a rewrite
    /// that dropped the limit would leave the option bound and unused.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_batch_size_bounds_what_one_round_claims()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        for (var index = 0; index < 5; index++)
        {
            await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
            {
                Class = NotificationClasses.Critical,
                Status = NotificationAttemptStatuses.Sent,
                CreatedAt = now.AddMinutes(-2),
                FallbackDeadline = now.AddMinutes(-1),
                StatusChangedAt = now.AddMinutes(-2),
            });
        }

        await using ServiceProvider tuned = fixture.BuildReplicaWith(
            new Dictionary<string, string?>
            {
                [$"{SchedulerScanOptions.SectionName}:BatchSize"] = "2",
            });
        using IServiceScope scope = tuned.CreateScope();
        OverdueFallbackScanResult result = await scope.ServiceProvider
            .GetRequiredService<OverdueFallbackScan>()
            .RunAsync(CancellationToken.None);

        result.DeadlineRequested.ShouldBe(2);
    }

    /// <summary>
    /// The grace before an inconclusive verdict is asked about, proved the same
    /// way: one attempt, two configurations, opposite answers.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_grace_before_an_inconclusive_verdict_is_configuration()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        SeededAttempt seeded = await fixture.SeedDispatchedAttemptAsync(new AttemptSeed
        {
            Class = NotificationClasses.Critical,
            Status = NotificationAttemptStatuses.Unknown,
            CreatedAt = now.AddMinutes(-5),
            FallbackDeadline = now.AddMinutes(30),
            StatusChangedAt = now.AddSeconds(-90),
        });

        await using (ServiceProvider patient = fixture.BuildReplicaWith(
            new Dictionary<string, string?>
            {
                [$"{SchedulerScanOptions.SectionName}:UnknownGrace"] = "00:05:00",
            }))
        {
            await RunOverdueAsync(patient);
        }

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(
            0, "com tolerância de cinco minutos um veredito de noventa segundos ainda espera.");

        await using ServiceProvider impatient = fixture.BuildReplicaWith(
            new Dictionary<string, string?>
            {
                [$"{SchedulerScanOptions.SectionName}:UnknownGrace"] = "00:01:00",
            });
        await RunOverdueAsync(impatient);

        (await fixture.CountFallbackTriggersAsync(seeded.NotificationId)).ShouldBe(1);
    }

    private static async Task RunOverdueAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OverdueFallbackScan>()
            .RunAsync(CancellationToken.None);
    }

    private static SchedulerScanOptions OptionsOf(ServiceProvider provider)
        => provider.GetRequiredService<IOptions<SchedulerScanOptions>>().Value;
}
