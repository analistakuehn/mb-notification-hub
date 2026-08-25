using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// The release proved where it actually has to work: through the Core that
/// reads the message it writes.
/// <para>
/// The scans can be tested against rows, and are. This one cannot. The failure
/// it guards against is not a wrong row, it is a right row that the next stage
/// throws away: the Core answers any state other than accepted with a
/// duplicate trail and no effect, so a release that enqueued without
/// transitioning would leave the notification parked forever while every queue
/// metric showed the work being done. Nothing short of running the Core over
/// the released message can tell the two apart.
/// </para>
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DeferredReleaseEndToEndTests(CorePipelineFixture fixture)
{
    /// <summary>
    /// How far past its release instant the notification is when the scheduler
    /// and the Core look at it. Wide enough to leave the silence window,
    /// narrow enough that no notification parked by another test in this
    /// collection comes into range.
    /// </summary>
    private static readonly TimeSpan PastTheWindow = TimeSpan.FromMinutes(3);

    [RequiresDockerFact]
    public async Task A_parked_notification_is_released_and_the_core_dispatches_it_instead_of_dropping_it()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");

        // A window that is closing: it covers this instant, so the pipeline
        // parks the notification, and it ends a minute from now, so a clock
        // moved three minutes forward is outside it while every notification
        // parked by a neighbouring test is still hours from its release.
        TimeZoneInfo saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, saoPaulo);
        var from = localNow.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = localNow.AddMinutes(1).ToString("HH:mm", CultureInfo.InvariantCulture);
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", quietHours: new { from, to });
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        Guid notificationId = await AcceptAndRunCoreAsync(application, templateKey, recipientId);
        (await StatusOfAsync(notificationId)).ShouldBe(
            NotificationStatuses.Deferred, "o cenário exige a notificação estacionada.");

        // One clock for both sides, because both have to agree that the window
        // closed: the scheduler to release, and the silence rule to stop
        // parking the notification again the moment it comes back.
        var clock = new MutableClock(DateTimeOffset.UtcNow + PastTheWindow);
        await using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        using (IServiceScope scope = tracker.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DeferredReleaseScan>()
                .RunAsync(CancellationToken.None);
        }


        (await StatusOfAsync(notificationId)).ShouldBe(
            NotificationStatuses.Accepted,
            "a liberação precisa transitar dentro da transação do claim; sem isso o Core "
            + "trata a retomada como duplicata e a notificação adiada nunca sai.");

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));

        // The disposition of the pass is deliberately not asserted here. A
        // release that failed to transition settles as a duplicate, which is a
        // legitimate disposition, so a counter would stop the test one line
        // before the assertion that says what actually went wrong.
        await CorePipelineFixture.RunCorePassAsync(core, "core-transactional");

        // The three answers that separate "released" from "released and
        // actually processed". The duplicate count comes with them because it
        // is the exact shape the defect takes: the Core would record the
        // retomada as a redelivery and change nothing.
        (await CountTrailAsync(notificationId, "notification.duplicate")).ShouldBe(
            0,
            "o Core descartou a retomada como duplicata em vez de processá-la: "
            + "é exatamente o sintoma de uma liberação que enfileira sem transitar.");
        (await StatusOfAsync(notificationId)).ShouldBe(NotificationStatuses.Dispatched);
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == notificationId)))
            .ShouldBe(1, "a retomada precisa produzir exatamente uma tentativa.");

        // The release leaves its own trail, once, whatever else happened after.
        (await CountTrailAsync(notificationId, SchedulerAuditVocabulary.NotificationReleased))
            .ShouldBe(1);
        (await CountTrailAsync(notificationId, "notification.dispatched")).ShouldBe(1);
    }

    private async Task<Guid> AcceptAndRunCoreAsync(
        string application,
        string templateKey,
        string recipientId)
    {
        HttpClient producer = fixture.CreateProducerClient(
            "release-producer", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(
                application, templateKey, NotificationClasses.Transactional, recipientId),
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
        return notificationId;
    }

    private async Task<string> StatusOfAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == notificationId.ToString()));
}
