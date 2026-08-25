using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.Reconciliation;

/// <summary>
/// The rear-guard correction, from the attempt nobody came back for to the
/// state the provider says it is in.
/// <para>
/// Every assertion here is about a job that exists precisely because the
/// ordinary path failed: the callback never arrived, or arrived and could not
/// be applied. So the suite is written against the two things that make such a
/// job trustworthy rather than merely busy: that its answer travels the same
/// state machine a callback would have travelled, and that asking the question
/// leaves no trace of the person it was asked about.
/// </para>
/// </summary>
[Collection(ReconciliationCollectionDefinition.Name)]
public sealed class DeliveryReconciliationTests(ReconciliationFixture fixture)
{
    private const string DeliveredAction = "delivery.event_applied";

    /// <summary>What the provider answers about an attempt this test is not asking about.</summary>
    private const string EmptyActivity = """{"messages":[]}""";

    /// <summary>
    /// One round, one question. The provider answer is a read at the provider,
    /// so a second question would cost nothing anybody sees, which is exactly
    /// why the count has to be pinned: a scan that asked once per row per pass
    /// of its own loop would look identical from the outside until the
    /// provider's bill arrived.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_eligible_attempt_is_asked_about_once_per_round()
    {
        ParkedEmail parked = await SeedParkedEmailAsync();
        Answer(parked.Seeded.AttemptId, Delivered(parked.MessageId));

        await fixture.RunRoundAsync();

        RequestsAbout(parked.Seeded.AttemptId).Count.ShouldBe(
            1,
            "uma pergunta por rodada: a consulta é uma leitura no provedor, então uma segunda "
            + "pergunta não apareceria em lugar nenhum até a fatura chegar.");
        (await fixture.AttemptStateAsync(parked.Seeded.AttemptId)).Status
            .ShouldBe(NotificationAttemptStatuses.Delivered);
    }

    /// <summary>
    /// The whole reason the pulled answer is the same record as the pushed one:
    /// two sources of feedback must reach the same state, the same stamp and
    /// the same trail, or the hub has two state machines and no way to say
    /// which one an attempt went through.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_pulled_answer_settles_the_attempt_exactly_as_the_callback_would_have()
    {
        ParkedEmail reconciled = await SeedParkedEmailAsync();
        ParkedEmail viaCallback = await SeedParkedEmailAsync(eligible: false);
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow().AddHours(-5);
        Answer(reconciled.Seeded.AttemptId, Delivered(reconciled.MessageId, arrivedAt));

        await fixture.RunRoundAsync();
        await ApplyAsCallbackAsync(
            new ProviderDeliveryEvent(
                "sendgrid",
                $"{viaCallback.MessageId}:delivered",
                DeliveryFeedbackKind.Delivered,
                arrivedAt,
                viaCallback.MessageId,
                new DispatchCorrelation(
                    viaCallback.Seeded.NotificationId, viaCallback.Seeded.AttemptId),
                null,
                SuppressionSignal.None));

        AttemptStateView pulled = await fixture.AttemptStateAsync(reconciled.Seeded.AttemptId);
        AttemptStateView pushed = await fixture.AttemptStateAsync(viaCallback.Seeded.AttemptId);
        pulled.Status.ShouldBe(pushed.Status);
        pulled.DeliveredAt.ShouldBe(
            pushed.DeliveredAt,
            "o carimbo de entrega é o instante do provedor nas duas metades, nunca o desta rodada.");
        Transition(await fixture.TrailDetailsAsync(reconciled.Seeded.NotificationId))
            .ShouldBe(Transition(await fixture.TrailDetailsAsync(viaCallback.Seeded.NotificationId)));
    }

    /// <summary>
    /// Every correction is evidence, and evidence that is not written down did
    /// not happen: the monthly rite reads this trail to state a delivery rate.
    /// </summary>
    [RequiresDockerFact]
    public async Task Every_correction_writes_its_own_trail_entry()
    {
        ParkedEmail parked = await SeedParkedEmailAsync();
        Answer(parked.Seeded.AttemptId, Delivered(parked.MessageId));

        await fixture.RunRoundAsync();

        (await fixture.CountTrailAsync(parked.Seeded.NotificationId, DeliveredAction)).ShouldBe(1);
        (await fixture.CountEvidenceAsync($"{parked.MessageId}:delivered")).ShouldBe(
            1,
            "a resposta do provedor vira evidência, como o callback viraria, e é por ela que o "
            + "ledger de contatos identifica uma recusa.");
    }

    /// <summary>
    /// A provider that cannot be asked is not a provider that answered
    /// nothing. The attempt stays exactly where it is and the hub never spends
    /// a call learning what it already knows.
    /// <para>
    /// It is left out of the batch and not merely skipped inside it, and the
    /// difference is the whole point. Such an attempt is never settled by
    /// asking, so it stays eligible for the life of its partition; selected
    /// oldest first, those rows took every seat of every round and the channels
    /// this job exists to correct were never reached. The round therefore
    /// reports no unanswerable attempt at all: there is none in the batch.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task A_provider_without_a_later_lookup_is_left_out_of_the_batch_and_stays_parked()
    {
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Unknown,
            Channel = "push",
            ProviderKey = "fcm",
            CreatedAt = fixture.Clock.GetUtcNow().AddHours(-10),
            StatusChangedAt = fixture.Clock.GetUtcNow().AddHours(-9),
        });
        var capturing = new CapturingLoggerProvider();
        AnswerAll(EmptyActivity);

        ReconciliationRoundView round = await RunWithLogAsync(capturing);

        round.WithoutLookup.ShouldBe(
            0,
            "a exclusão acontece na seleção; um zero aqui significa que a linha nem entrou no "
            + "lote, que é o que impede o lote de encher com o mesmo conjunto todo dia.");
        RequestsAbout(seeded.AttemptId).ShouldBeEmpty(
            "o provedor de push não oferece consulta posterior; perguntar a ele seria uma chamada "
            + "que nenhuma resposta pode responder.");
        (await fixture.AttemptStateAsync(seeded.AttemptId)).Status
            .ShouldBe(NotificationAttemptStatuses.Unknown);
        (await fixture.CountTrailAsync(seeded.NotificationId, DeliveredAction)).ShouldBe(0);
    }

    /// <summary>
    /// An attempt the dispatcher refused to send, because the notification's
    /// own validity had already ended, never reached a provider and carries no
    /// message identity. Asking about it would be asking a provider about a
    /// message it was never given.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_settled_by_its_own_validity_is_never_asked_about()
    {
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            // The last step of a plan ends as failed with the expiry code: the
            // status the fallback handler writes is the notification's, and the
            // attempt's is written by the dispatcher that refused to send it.
            Status = NotificationAttemptStatuses.Failed,
            ErrorCode = "notification-expired",
            NotificationStatus = NotificationStatuses.Expired,
            ProviderKey = "sendgrid",
            ProviderMessageId = null,
            CreatedAt = fixture.Clock.GetUtcNow().AddHours(-10),
            StatusChangedAt = fixture.Clock.GetUtcNow().AddHours(-9),
        });
        AnswerAll(EmptyActivity);

        await fixture.RunRoundAsync();

        RequestsAbout(seeded.AttemptId).ShouldBeEmpty(
            "um attempt encerrado pela própria validade nunca chegou ao provedor e não carrega "
            + "identidade de mensagem: perguntar seria perguntar por uma mensagem que não existe.");
        AttemptStateView state = await fixture.AttemptStateAsync(seeded.AttemptId);
        state.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        state.ErrorCode.ShouldBe("notification-expired");
    }

    /// <summary>
    /// The eligibility window, measured against the database rather than
    /// described: a send whose silence is younger than the window is not the
    /// job's business yet, because a provider that reports asynchronously is
    /// still allowed to report.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_parked_for_less_than_the_window_is_not_asked_about()
    {
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Sent,
            ProviderKey = "sendgrid",
            ProviderMessageId = $"sg-{Guid.NewGuid():N}",
            CreatedAt = fixture.Clock.GetUtcNow().AddHours(-6),
            StatusChangedAt = fixture.Clock.GetUtcNow().AddHours(-5).AddMinutes(-59),
        });
        AnswerAll(EmptyActivity);

        await fixture.RunRoundAsync();

        RequestsAbout(seeded.AttemptId).ShouldBeEmpty();
        (await fixture.AttemptStateAsync(seeded.AttemptId)).Status
            .ShouldBe(NotificationAttemptStatuses.Sent);
    }

    /// <summary>
    /// The rows the status stamp arrived too late for. They read null, they can
    /// never match an age predicate, and the scheduler is right to leave them
    /// alone: acting early there costs a second message to a person. Here the
    /// same mistake costs one provider read, so this job is the declared owner
    /// of that backlog, and the creation instant is the age it uses.
    /// </summary>
    [RequiresDockerFact]
    public async Task An_attempt_written_before_the_status_stamp_existed_is_still_asked_about()
    {
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Unknown,
            ProviderKey = "sendgrid",
            ProviderMessageId = $"sg-{Guid.NewGuid():N}",
            CreatedAt = fixture.Clock.GetUtcNow().AddDays(-2),
            StatusChangedAt = null,
        });
        Answer(
            seeded.AttemptId,
            Delivered((await fixture.AttemptStateAsync(seeded.AttemptId)).ProviderMessageId!));

        await fixture.RunRoundAsync();

        RequestsAbout(seeded.AttemptId).Count.ShouldBe(1);
        (await fixture.AttemptStateAsync(seeded.AttemptId)).Status
            .ShouldBe(NotificationAttemptStatuses.Delivered);
    }

    /// <summary>
    /// The one route that needs a destination, and the promise that comes with
    /// it. The provider that cannot search by metadata is asked what it sent to
    /// an address inside a window, so the address is read from the contact
    /// context at the moment of the call and must exist nowhere afterwards:
    /// not in a log, not in the trail, not in a column, not inside the evidence
    /// this job writes.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_contact_value_of_the_destination_route_leaves_no_trace_behind_it()
    {
        var phoneNumber = $"+5511{Random.Shared.Next(100_000_000, 999_999_999)}";
        var recipientId = $"cus_{Guid.NewGuid():N}";
        Guid contactPointId = await fixture.SeedContactPointAsync(recipientId, "sms", phoneNumber);
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Unknown,
            Channel = "sms",
            ProviderKey = "twilio",
            ProviderMessageId = null,
            RecipientId = recipientId,
            ContactPointId = contactPointId,
            CreatedAt = fixture.Clock.GetUtcNow().AddHours(-10),
            StatusChangedAt = fixture.Clock.GetUtcNow().AddHours(-9),
        });
        var messageSid = $"SM{Guid.NewGuid():N}";
        AnswerAll($$"""
            {"messages":[{"sid":"{{messageSid}}","status":"delivered",
              "date_updated":"2026-08-25T03:30:00Z"}]}
            """);
        var capturing = new CapturingLoggerProvider();

        await RunWithLogAsync(capturing);

        // The route really was the destination one, or the rest of this test
        // would be proving that an unused value leaves no trace.
        FakeProviderRequest asked = fixture.Provider.Requests
            .Last(request => request.Path.Contains("/Messages.json", StringComparison.Ordinal));
        Uri.UnescapeDataString(asked.Query).ShouldContain(phoneNumber);
        (await fixture.AttemptStateAsync(seeded.AttemptId)).Status
            .ShouldBe(NotificationAttemptStatuses.Delivered);

        // And it is nowhere this hub can be read from afterwards.
        capturing.Lines.ShouldNotContain(
            line => line.Contains(phoneNumber, StringComparison.Ordinal),
            "o destino revelado para a correlação não pode aparecer em log.");
        (await fixture.TrailDetailsAsync(seeded.NotificationId)).ShouldNotContain(
            details => details.Contains(phoneNumber, StringComparison.Ordinal),
            "o destino revelado para a correlação não pode aparecer na trilha.");
        (await StoredTextAsync(seeded.NotificationId)).ShouldNotContain(
            stored => stored.Contains(phoneNumber, StringComparison.Ordinal),
            "o destino revelado para a correlação não pode ser persistido em coluna alguma.");
        (await SealedEvidenceAsync(seeded.NotificationId)).ShouldNotContain(
            payload => Encoding.UTF8.GetString(payload).Contains(phoneNumber, StringComparison.Ordinal),
            "a evidência da consulta guarda o evento canônico, que não carrega destino por contrato.");
    }

    /// <summary>
    /// The debt the deadline scan was left with. An attempt of a notification
    /// that ended without advancing its plan keeps a deadline and an empty
    /// claim, which is exactly the predicate of the indexes the scheduler
    /// reads, so those rows are read and discarded once per round for the life
    /// of the partition. Retiring them changes no behaviour and empties the
    /// index.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_attempts_of_concluded_notifications_are_retired_from_the_scheduler_indexes()
    {
        var parked = new List<SeededReconciliationAttempt>();
        for (var index = 0; index < 5; index++)
        {
            // The shape the debt really has: the deadline scan asked for a
            // fallback, the handler ended the notification instead of advancing
            // a plan that had no next step, and the attempt kept the status the
            // provider left it in.
            parked.Add(await fixture.SeedAttemptAsync(new ReconciliationSeed
            {
                Status = NotificationAttemptStatuses.Sent,
                Channel = "push",
                ProviderKey = "fcm",
                NotificationStatus = NotificationStatuses.Failed,
                CreatedAt = fixture.Clock.GetUtcNow().AddDays(-1),
                StatusChangedAt = fixture.Clock.GetUtcNow().AddDays(-1),
                FallbackDeadline = fixture.Clock.GetUtcNow().AddDays(-1).AddSeconds(30),
            }));
        }

        SeededReconciliationAttempt alive = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Sent,
            ProviderKey = "sendgrid",
            ProviderMessageId = $"sg-{Guid.NewGuid():N}",
            CreatedAt = fixture.Clock.GetUtcNow().AddMinutes(-1),
            StatusChangedAt = fixture.Clock.GetUtcNow().AddMinutes(-1),
            FallbackDeadline = fixture.Clock.GetUtcNow().AddMinutes(4),
        });
        var before = await fixture.CountParkedInScanIndexAsync();

        var retired = await fixture.RunLiabilitySweepAsync();

        retired.ShouldBe(parked.Count);
        (await fixture.CountParkedInScanIndexAsync()).ShouldBe(
            before - parked.Count,
            "cada tentativa de notificação encerrada sai dos índices; nenhuma outra linha é tocada.");
        foreach (SeededReconciliationAttempt attempt in parked)
            (await fixture.AttemptStateAsync(attempt.AttemptId)).PlanAdvancedAt.ShouldNotBeNull();

        (await fixture.AttemptStateAsync(alive.AttemptId)).PlanAdvancedAt.ShouldBeNull(
            "uma notificação ainda despachada continua podendo avançar de etapa, e a etapa é do "
            + "handler de fallback, nunca desta varredura.");
    }

    /// <summary>
    /// Seeds one e-mail attempt parked past the window, with the identity the
    /// provider gave the send.
    /// </summary>
    private async Task<ParkedEmail> SeedParkedEmailAsync(bool eligible = true)
    {
        var messageId = $"sg-{Guid.NewGuid():N}";
        SeededReconciliationAttempt seeded = await fixture.SeedAttemptAsync(new ReconciliationSeed
        {
            Status = NotificationAttemptStatuses.Sent,
            ProviderKey = "sendgrid",
            ProviderMessageId = messageId,
            CreatedAt = fixture.Clock.GetUtcNow().AddHours(-10),
            SentAt = fixture.Clock.GetUtcNow().AddHours(-10),

            // Inside the window, this attempt is not the round's business and
            // the comparison arm can settle it through the callback half
            // without the two halves racing for the same answer.
            StatusChangedAt = fixture.Clock.GetUtcNow().AddHours(eligible ? -9 : -1),
        });
        return new ParkedEmail(seeded, messageId);
    }

    /// <summary>
    /// Applies one canonical event exactly as the callback half does: the
    /// evidence is written by the same writer the receiving route uses, and
    /// the consumer of the feedback queue is the code that hands it to the
    /// applier.
    /// </summary>
    private async Task ApplyAsCallbackAsync(ProviderDeliveryEvent callbackEvent)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        DeliveryEventWriter writer = scope.ServiceProvider.GetRequiredService<DeliveryEventWriter>();
        SealedDeliveryPayload sealedPayload = await writer.SealPayloadAsync(
            callbackEvent.ProviderKey,
            DeliveryPayloadSources.Webhook,
            Encoding.UTF8.GetBytes("[callback-body]"), CancellationToken.None);
        DeliveryEventRecorded recorded = await writer.RecordDiscoveredAsync(
            callbackEvent, callbackEvent.Correlation, sealedPayload, CancellationToken.None);

        var processor = new DeliveryEventMessageProcessor(
            scope.ServiceProvider.GetRequiredService<NotificationsDbContext>(),
            scope.ServiceProvider.GetRequiredService<DeliveryStateApplier>(),
            Options.Create(new DeliveryTrackingOptions()),
            fixture.Clock,
            scope.ServiceProvider.GetRequiredService<ILogger<DeliveryEventMessageProcessor>>());
        using var payload = JsonDocument.Parse(
            $$"""{"deliveryEventId":"{{recorded.DeliveryEventId}}"}""");
        MessageDisposition disposition = await processor.ProcessAsync(
            new MessageEnvelope
            {
                MessageId = Guid.CreateVersion7(),
                Type = DeliveryTrackingMessages.EventReceivedType,
                SchemaVersion = DeliveryTrackingMessages.SchemaVersion,
                Payload = payload.RootElement.Clone(),
            },
            CancellationToken.None);
        disposition.ShouldBeOfType<MessageDisposition.Processed>();
    }

    private async Task<ReconciliationRoundView> RunWithLogAsync(CapturingLoggerProvider capturing)
    {
        using ServiceProvider role = fixture.BuildRoleWith(loggerProvider: capturing);
        using IServiceScope scope = role.CreateScope();
        DeliveryReconciliationResult result = await scope.ServiceProvider
            .GetRequiredService<DeliveryReconciliationScan>()
            .RunAsync(CancellationToken.None);
        return new ReconciliationRoundView(
            result.Examined,
            result.Queried,
            result.Corrected,
            result.WithoutLookup,
            result.LiabilityRetired);
    }

    /// <summary>The activity answer of a delivered e-mail, as the provider shapes it.</summary>
    private static string Delivered(string messageId, DateTimeOffset? occurredAt = null)
        => $$"""
            {"messages":[{"msg_id":"{{messageId}}","status":"delivered",
              "last_event_time":"{{(occurredAt ?? new DateTimeOffset(2026, 8, 25, 6, 0, 0, TimeSpan.Zero)):yyyy-MM-ddTHH:mm:ssZ}}"}]}
            """;

    /// <summary>
    /// Answers for one attempt only. The suite shares a database, so a round
    /// may examine rows another test seeded; answering everything with one
    /// canned body would let an answer meant for one attempt settle another.
    /// </summary>
    private void Answer(Guid attemptId, string body)
        => fixture.Provider.Handler = request => Task.FromResult(new FakeProviderResponse(
            200,
            Mentions(request, attemptId.ToString()) ? body : EmptyActivity,
            null));

    private void AnswerAll(string body)
        => fixture.Provider.Handler = _ => Task.FromResult(new FakeProviderResponse(200, body, null));

    /// <summary>
    /// Calls this hub made about one attempt, which is the only count these
    /// tests may assert on: the round also examines whatever other tests of
    /// this collection left parked.
    /// </summary>
    private IReadOnlyList<FakeProviderRequest> RequestsAbout(Guid attemptId)
        => [.. fixture.Provider.Requests.Where(request => Mentions(request, attemptId.ToString()))];

    private static bool Mentions(FakeProviderRequest request, string token)
        => Uri.UnescapeDataString(request.Query).Contains(token, StringComparison.Ordinal)
            || request.Path.Contains(token, StringComparison.Ordinal);

    /// <summary>Every text column of the evidence this job wrote for one notification.</summary>
    private async Task<IReadOnlyList<string>> StoredTextAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(async db =>
        {
            List<string> stored = await db.DeliveryEvents
                .AsNoTracking()
                .Where(evidence => evidence.NotificationId == notificationId)
                .Select(evidence => evidence.ProviderKey + "|" + evidence.ProviderEventId + "|"
                    + (evidence.ProviderMessageId ?? "") + "|" + evidence.Kind + "|"
                    + (evidence.ErrorCode ?? "") + "|" + evidence.SuppressionSignal)
                .ToListAsync();
            List<string> attempts = await db.NotificationAttempts
                .AsNoTracking()
                .Where(attempt => attempt.NotificationId == notificationId)
                .Select(attempt => (attempt.ProviderKey ?? "") + "|" + (attempt.ProviderMessageId ?? "")
                    + "|" + (attempt.ErrorCode ?? "") + "|" + attempt.Status)
                .ToListAsync();
            return (IReadOnlyList<string>)[.. stored, .. attempts];
        });

    private async Task<IReadOnlyList<byte[]>> SealedEvidenceAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(async db => (IReadOnlyList<byte[]>)await db.DeliveryEvents
            .AsNoTracking()
            .Where(evidence => evidence.NotificationId == notificationId)
            .Join(
                db.DeliveryPayloads.AsNoTracking(),
                evidence => new { Id = evidence.PayloadId, evidence.ReceivedAt },
                payload => new { payload.Id, payload.ReceivedAt },
                (evidence, payload) => payload.PayloadEncrypted)
            .ToListAsync());

    /// <summary>The transition one trail records, without the identifiers that differ per attempt.</summary>
    private static string Transition(IReadOnlyList<string> details)
    {
        foreach (var entry in details)
        {
            using var parsed = JsonDocument.Parse(entry);
            if (!parsed.RootElement.TryGetProperty("toStatus", out JsonElement toStatus)) continue;

            return string.Join(
                '|',
                parsed.RootElement.GetProperty("kind").GetString(),
                parsed.RootElement.GetProperty("fromStatus").GetString(),
                toStatus.GetString(),
                parsed.RootElement.GetProperty("providerKey").GetString());
        }

        return "sem transição na trilha";
    }

    private sealed record ParkedEmail(SeededReconciliationAttempt Seeded, string MessageId);
}
