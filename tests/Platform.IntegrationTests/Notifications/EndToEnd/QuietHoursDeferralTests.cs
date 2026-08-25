using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications.Scheduling;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.EndToEnd;

/// <summary>
/// The silence window from the outside: what it does to a notification of the
/// class that has one, what it must never do to the two flows that cannot
/// wait, and what it takes for a parked notification to come back and leave.
/// <para>
/// The release instant is the whole subject. A notification handed back to the
/// pipeline is evaluated by the same rules that parked it, silence window
/// included, so any release instant earlier than the end of the window buys
/// nothing: the rule looks again, finds the window still open and parks it
/// once more. The scenario below reaches that trap deliberately, by taking the
/// clock of the release from the stored instant rather than from a value the
/// test chose.
/// </para>
/// </summary>
[Collection(EndToEndPipelineCollectionDefinition.Name)]
public sealed class QuietHoursDeferralTests(EndToEndPipelineFixture fixture)
{
    private const string FcmAccepted = """{"name":"projects/test-project/messages/0:1"}""";
    private const string FcmSendPath = ":send";

    /// <summary>
    /// Hours of the recipient, deliberately not the default of the contact
    /// context. A rule that read a fixed zone instead of the profile would
    /// place this recipient an hour outside the window it is in, and the
    /// deferral this suite asserts would never happen.
    /// </summary>
    private const string RecipientTimezone = "America/Manaus";

    private static readonly (string Channel, string? Timeout)[] PushOnly = [("push", null)];

    /// <summary>
    /// How far ahead the window of the release scenario ends. Narrow enough
    /// that the release runs against a clock only minutes ahead, wide enough
    /// that no window closes while the catalog of the scenario is published.
    /// </summary>
    private static readonly TimeSpan ReleasableWindowEnds = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How far ahead the window of the exemption scenario ends. An hour out on
    /// purpose: what it parks stays parked, and a neighbouring scenario whose
    /// release scan stands minutes ahead must not find it.
    /// </summary>
    private static readonly TimeSpan LastingWindowEnds = TimeSpan.FromHours(1);

    [RequiresDockerFact]
    public async Task An_operational_notification_parked_by_the_window_leaves_when_the_window_ends()
    {
        await using FakeProviderServer provider = await StartProviderAsync();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(RecipientTimezone);
        (var from, var to) = OpenWindow(zone, ReleasableWindowEnds);
        DateTimeOffset windowEnds = NextOccurrenceOf(to, zone);

        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture,
            application,
            NotificationClasses.Operational,
            "account-maintenance",
            legalBasis: "legitimo-interesse");
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture,
            application,
            NotificationClasses.Operational,
            quietHours: new { from, to },
            deliveryPlan: PushOnly);
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(
            fixture, timezone: RecipientTimezone);
        await fixture.SeedProviderConfigAsync(("push", "fcm"));

        Guid notificationId = await AcceptAndRunCoreAsync(
            application,
            templateKey,
            NotificationClasses.Operational,
            recipientId,
            NotificationsApi.SendOperational,
            "core-operational");

        Notification parked = await ReadAsync(notificationId);
        parked.Status.ShouldBe(
            NotificationStatuses.Deferred,
            "a janela de silêncio cobre o instante do pedido no fuso do destinatário.");
        parked.ReleaseAt.ShouldBe(
            windowEnds,
            "o instante de liberação precisa ser o fim da janela no fuso do destinatário: "
            + "qualquer instante anterior devolve a notificação para dentro da janela.");

        // The clock of the release comes from the stored instant, so a release
        // instant shorter than the window would be released back into the
        // window and parked again. Choosing the clock here instead would hide
        // exactly that.
        var clock = new MutableClock(parked.ReleaseAt!.Value.AddSeconds(30));
        await ReleaseAsync(clock, expectedReleases: 1);
        await CarryBackToTheProviderAsync(provider, clock);

        // First, because it is the harm: a notification that parks and never
        // leaves is the failure the class was blocked for, and it looks
        // identical to a healthy queue from every metric.
        PushSends(provider).Count.ShouldBe(
            1,
            "a notificação adiada voltou ao pipeline e nada saiu pelo canal: "
            + "se o instante de liberação não for o fim da janela, a regra adia de novo "
            + "e a notificação nunca chega ao destinatário.");
        (await CountTrailAsync(notificationId, PipelineAuditVocabulary.NotificationDispatched))
            .ShouldBe(1, "a retomada precisa passar pelo despacho, e não apenas mudar de estado.");

        // Delivered rather than dispatched, and that is the plan of this
        // policy speaking: its only step has no later one, so acceptance by
        // the push provider is the strongest signal this hub will ever hold
        // about it.
        (await StatusOfAsync(notificationId)).ShouldBe(NotificationStatuses.Delivered);
        (await CountTrailAsync(notificationId, PipelineAuditVocabulary.NotificationDeferred)).ShouldBe(
            1,
            "a retomada reavalia a janela com o relógio do worker; um segundo adiamento "
            + "significaria que o instante de liberação não era o fim da janela.");
        (await CountTrailAsync(notificationId, SchedulerAuditVocabulary.NotificationReleased))
            .ShouldBe(1);
        (await AttemptChannelsAsync(notificationId)).ShouldBe(["push"]);
    }

    [RequiresDockerFact]
    public async Task The_window_that_parks_an_operational_notification_never_holds_critical_or_authentication()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(RecipientTimezone);
        (var from, var to) = OpenWindow(zone, LastingWindowEnds);
        var application = CorePipelineApi.NewApplication();
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(
            fixture, timezone: RecipientTimezone);

        Guid parked = await PublishAndAcceptAsync(
            application, NotificationClasses.Operational, "account-maintenance",
            recipientId, from, to, NotificationsApi.SendOperational, "core-operational");
        Guid critical = await PublishAndAcceptAsync(
            application, NotificationClasses.Critical, "security-alert",
            recipientId, from, to, NotificationsApi.SendCritical, "core-critical");
        Guid authentication = await PublishAndAcceptAsync(
            application, NotificationClasses.Transactional, "authentication",
            recipientId, from, to, NotificationsApi.SendTransactional, "core-auth");

        // The control, and it comes first because the two assertions after it
        // mean nothing without it: a window that happened to be closed would
        // let everything through and prove no exemption at all.
        (await StatusOfAsync(parked)).ShouldBe(
            NotificationStatuses.Deferred,
            "sem uma notificação adiada por esta janela, as duas afirmações seguintes "
            + "não provam isenção nenhuma.");

        (await StatusOfAsync(critical)).ShouldBe(
            NotificationStatuses.Dispatched,
            "a mesma janela que adiou a operacional segurou uma notificação crítica: "
            + "um código de acesso ficaria esperando o amanhecer.");
        (await StatusOfAsync(authentication)).ShouldBe(
            NotificationStatuses.Dispatched,
            "a mesma janela segurou um fluxo de autenticação, que a regra isenta "
            + "pelo propósito do template e não pela classe.");
    }

    /// <summary>The push provider double, with its token endpoint and its send endpoint.</summary>
    private static async Task<FakeProviderServer> StartProviderAsync()
    {
        FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, FcmAccepted, null));
        return provider;
    }

    /// <summary>
    /// A window that covers this instant in the recipient's hours and ends
    /// after the given span. Written as the two wall-clock times a policy
    /// carries, because that is what a human publishes.
    /// </summary>
    private static (string From, string To) OpenWindow(TimeZoneInfo zone, TimeSpan endsIn)
    {
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        return (
            localNow.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture),
            localNow.Add(endsIn).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The next instant, in UTC, at which the recipient's wall clock reads the
    /// given time. Computed from the wall time and the zone alone, so it is an
    /// independent answer rather than a transcription of the rule.
    /// </summary>
    private static DateTimeOffset NextOccurrenceOf(string localTime, TimeZoneInfo zone)
    {
        var time = TimeOnly.ParseExact(localTime, "HH:mm", CultureInfo.InvariantCulture);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var day = DateOnly.FromDateTime(localNow.DateTime);
        var candidate = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (candidate <= localNow.DateTime)
        {
            candidate = day.AddDays(1).ToDateTime(time, DateTimeKind.Unspecified);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
    }

    /// <summary>Publishes the catalog of one class under the given window and accepts one notification.</summary>
    private async Task<Guid> PublishAndAcceptAsync(
        string application,
        string notificationClass,
        string purpose,
        string recipientId,
        string from,
        string to,
        string role,
        string coreQueue)
    {
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, notificationClass, purpose);
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture,
            application,
            notificationClass,
            quietHours: new { from, to },
            deliveryPlan: PushOnly);
        return await AcceptAndRunCoreAsync(
            application, templateKey, notificationClass, recipientId, role, coreQueue);
    }

    /// <summary>
    /// Accepts one notification and runs the Core over it once. The disposition
    /// of the relay pass is asserted because the acceptance always announces
    /// itself; what the Core then decides is the subject of the test and is
    /// read from the store, never from a counter.
    /// </summary>
    private async Task<Guid> AcceptAndRunCoreAsync(
        string application,
        string templateKey,
        string notificationClass,
        string recipientId,
        string role,
        string coreQueue)
    {
        HttpClient producer = fixture.CreateProducerClient("quiet-hours-producer", role);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(
                application, templateKey, notificationClass, recipientId, ttlSeconds: 3600),
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid notificationId)
            .ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        (await CorePipelineFixture.RunRelayPassAsync(relay)).Published.ShouldBeGreaterThanOrEqualTo(1);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, coreQueue))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        return notificationId;
    }

    /// <summary>One round of the release scan through the composed delivery-tracker role.</summary>
    private async Task ReleaseAsync(MutableClock clock, int expectedReleases)
    {
        await using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        using IServiceScope scope = tracker.CreateScope();
        DeferredReleaseScanResult result = await scope.ServiceProvider
            .GetRequiredService<DeferredReleaseScan>()
            .RunAsync(CancellationToken.None);
        result.Released.ShouldBe(
            expectedReleases,
            "a varredura precisa liberar exatamente a notificação vencida deste cenário.");
    }

    /// <summary>
    /// Carries the released notification back through the relay, the Core and
    /// the push dispatcher, on the clock of the release, because the rule that
    /// parked it looks at the window again with that clock.
    /// <para>
    /// No disposition of these passes is asserted: a run in which the release
    /// enqueued without transitioning settles as a duplicate, which is a
    /// legitimate disposition and would stop the test before the assertion that
    /// says what went wrong.
    /// </para>
    /// </summary>
    private async Task CarryBackToTheProviderAsync(FakeProviderServer provider, MutableClock clock)
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);

        await using ServiceProvider core = fixture.BuildCoreWorkerProvider(
            replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        await CorePipelineFixture.RunCorePassAsync(core, "core-operational");
        await CorePipelineFixture.RunRelayPassAsync(relay);

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-operational");
    }

    /// <summary>Sends the push provider received, told apart from its token calls by the endpoint.</summary>
    private static List<FakeProviderRequest> PushSends(FakeProviderServer provider)
        => [.. provider.Requests.Where(request =>
            request.Path.EndsWith(FcmSendPath, StringComparison.Ordinal))];

    private async Task<Notification> ReadAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(notification => notification.Id == notificationId));

    private async Task<string> StatusOfAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<IReadOnlyList<string>> AttemptChannelsAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.NotificationId == notificationId)
            .OrderBy(attempt => attempt.Sequence)
            .Select(attempt => attempt.Channel)
            .ToListAsync());

    private async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == notificationId.ToString()));
}
