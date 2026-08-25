using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// What a suppression does to the next notification. Two properties are at
/// stake and only one of them is about the rule: the policy stage has to
/// refuse the closed channel with a stable reason and rule-by-rule evidence,
/// and the refusal has to start holding now rather than whenever the encrypted
/// snapshot cache happens to expire.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineSuppressionTests(CorePipelineFixture fixture)
{
    private const string SuppressionRule = "SuppressionGate";
    private const string ChannelSuppressed = "channel-suppressed";

    private static readonly string[] SmsOnly = ["sms"];

    [RequiresDockerFact]
    public async Task A_suppressed_contact_stops_being_eligible_at_once_instead_of_when_the_cache_expires()
    {
        var application = CorePipelineApi.NewApplication();
        (var warmingTemplate, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        (var beforeTemplate, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        (var afterTemplate, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await PublishSmsOnlyPolicyAsync(application);

        // No device: the class allows one channel and the recipient has one
        // address on it, so closing that address closes the notification.
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture, withDevice: false);
        Guid contactPointId = await ContactPointOfAsync(recipientId);

        // The first notification is what puts the snapshot in the cache. The
        // trap this test exists for only appears once something is cached.
        Guid warmed = await ProcessOneAsync(application, warmingTemplate, recipientId);
        (await StatusAsync(warmed)).ShouldBe(NotificationStatuses.Dispatched);

        await SuppressAsync(recipientId, contactPointId, ContactChannels.Sms);

        // Half one, and the reason the invalidation is not optional: the
        // ledger already closed the channel, and a fresh cached snapshot still
        // answers that the recipient is reachable. Left alone this would hold
        // for the whole 24-hour lifetime of the entry.
        Guid beforeInvalidation = await ProcessOneAsync(application, beforeTemplate, recipientId);
        (await StatusAsync(beforeInvalidation)).ShouldBe(
            NotificationStatuses.Dispatched,
            "sem invalidação o snapshot cacheado responde o estado antigo, que é exatamente "
            + "o risco que a invalidação existe para fechar.");

        // Half two: the write announced the invalidation in its own
        // transaction, and the role that consumes it marks the entry stale.
        await InvalidateSnapshotCacheAsync();

        // A template of its own, so a broken invalidation shows up as the harm
        // it is: the notification would be dispatched to a channel the ledger
        // already closed, instead of being caught by the deduplication window
        // of the previous send and reading as a refusal that means nothing.
        Guid afterInvalidation = await ProcessOneAsync(application, afterTemplate, recipientId);

        (await StatusAsync(afterInvalidation)).ShouldBe(
            NotificationStatuses.Rejected,
            "o canal está suprimido no ledger; despachar aqui seria endereçar um destino "
            + "que o provedor já recusou.");
        PolicyEvaluation refusal = await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .SingleAsync(evaluation => evaluation.NotificationId == afterInvalidation
                && evaluation.Result == PolicyEvaluationResults.Reject));
        refusal.Rule.ShouldBe(SuppressionRule);
        refusal.Reason.ShouldBe(ChannelSuppressed);

        // Rule by rule, as every other policy decision is recorded: the
        // consent gate ran and allowed, this rule ran and refused, and the
        // rules after it never ran because the first refusal ends the stage.
        Dictionary<string, string> resultByRule = await ResultByRuleAsync(afterInvalidation);
        resultByRule["ConsentGate"].ShouldBe(PolicyEvaluationResults.Allow);
        resultByRule[SuppressionRule].ShouldBe(PolicyEvaluationResults.Reject);
        resultByRule.ShouldNotContainKey("QuietHours");
        resultByRule.ShouldNotContainKey("ChannelSelection");

        using JsonDocument evidence = JsonDocument.Parse(refusal.EvidenceJson);
        Channels(evidence, "suppressed").ShouldBe([ContactChannels.Sms]);
        Channels(evidence, "surviving").ShouldBeEmpty();

        // No attempt was created and nothing left for a provider.
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == afterInvalidation)))
            .ShouldBe(0);
    }

    /// <summary>
    /// One channel and one step, so closing the only address of the recipient
    /// is what the stage has to answer. With a second channel in the class the
    /// refusal would come from the channel selection instead, under a different
    /// reason, and this rule would never be the one under test.
    /// </summary>
    private async Task PublishSmsOnlyPolicyAsync(string application)
    {
        await ClassPolicyApi.CreateDraftAsync(
            fixture.CreateAuthorClient("policy-author"),
            application,
            "transactional",
            new
            {
                schemaVersion = 1,
                channelsAllowed = SmsOnly,
                deliveryPlan = new object[] { new { channel = "sms" } },
                defaultTtl = "300s",
                dedupeWindow = "60s",
                quietHours = (object?)null,
                consentPurpose = (string?)null,
            });
        await ClassPolicyApi.PublishAsync(
            fixture.CreatePublisherClient("policy-publisher"), application, "transactional");
    }

    private async Task<Guid> ProcessOneAsync(string application, string templateKey, string recipientId)
    {
        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "transactional", recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid id).ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        await CorePipelineFixture.RunCorePassAsync(worker, "core-transactional");
        return id;
    }

    /// <summary>
    /// Drains the invalidation queue with the role that owns it. The queue is
    /// shared by everything that writes contact data, and one pass reads a
    /// bounded batch, so the drain runs until a pass comes back empty: reading
    /// a single batch would leave the mark of this recipient behind whichever
    /// messages happened to be in front of it.
    /// </summary>
    private async Task InvalidateSnapshotCacheAsync()
    {
        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider contacts = fixture.BuildContactConsentWorkerProvider();
        var empty = 0;
        for (var pass = 0; pass < 30 && empty < 2; pass++)
        {
            SqsConsumePassResult drained =
                await CorePipelineFixture.RunContactsChangedPassAsync(contacts);

            // Two empty reads in a row, not one: a receive samples the queue
            // and may come back empty while messages are still there.
            empty = drained.Received == 0 ? empty + 1 : 0;
        }
    }

    private async Task SuppressAsync(string recipientId, Guid contactPointId, string channel)
    {
        // The channel needs two refusals inside a week, so the scenario spends
        // both instead of assuming the e-mail rule.
        for (var refusal = 0; refusal < 2; refusal++)
        {
            using IServiceScope scope = fixture.Services.CreateScope();
            Result<SuppressionOutcome> reported = await scope.ServiceProvider
                .GetRequiredService<ISuppressionLedger>()
                .ReportDeliveryFeedbackAsync(
                    new SuppressionReport(
                        recipientId,
                        contactPointId,
                        channel,
                        "hard-bounce",
                        Guid.CreateVersion7(),
                        DateTimeOffset.UtcNow.AddMinutes(refusal)),
                    CancellationToken.None);
            reported.IsSuccess.ShouldBeTrue();
        }
    }

    private async Task<Guid> ContactPointOfAsync(string recipientId)
        => await fixture.QueryContactConsentDbAsync(db => db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId && point.RemovedAt == null)
            .Select(point => point.Id)
            .SingleAsync());

    private async Task<string> StatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync());

    private async Task<Dictionary<string, string>> ResultByRuleAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .Where(evaluation => evaluation.NotificationId == notificationId)
            .ToDictionaryAsync(evaluation => evaluation.Rule, evaluation => evaluation.Result));

    private static string[] Channels(JsonDocument evidence, string member)
        => [.. evidence.RootElement.GetProperty(member)
            .EnumerateArray()
            .Select(element => element.GetString()!)];
}
