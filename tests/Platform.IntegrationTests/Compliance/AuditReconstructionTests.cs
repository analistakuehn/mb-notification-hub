using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Compliance;

/// <summary>
/// The reconstruction of a notification that really crossed ingestion, pipeline
/// and dispatch. The answer is read raw, so an omitted member is observable, and
/// the trail block is replayed link by link, so "rebuilt from the canonical
/// text" is proved rather than asserted.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class AuditReconstructionTests(CorePipelineFixture fixture)
{
    private const string AccessCode = "123456";
    private const string ConsentPurpose = "marketing";

    /// <summary>Action of the crafted link that carries a drifted details column.</summary>
    private const string DriftProbeAction = "notification.drift_probe";

    private static readonly string[] SensitiveCode = ["code"];

    [RequiresDockerFact]
    public async Task The_reconstruction_answers_the_eight_questions_with_trail_and_state_apart()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        JsonElement body = sent.Body;

        sent.Status.ShouldBe(200);

        // The two blocks exist and mean different things.
        JsonElement trail = body.GetProperty("trail");
        JsonElement state = body.GetProperty("state");

        // Every chained link replays from its own bytes: an independent
        // verifier reaches the same hash without trusting this response.
        IReadOnlyList<JsonElement> links = AuditApi.Items(trail, "links");
        links.ShouldAllBe(link => AuditApi.ReplaysCleanly(link));
        trail.GetProperty("unchainedRows").GetInt32().ShouldBe(0);

        // Question 1, who asked and when: the acceptance link plus the state.
        AuditApi.Actions(trail, "links").ShouldContain("notification.accepted");
        JsonElement notification = state.GetProperty("notification");
        notification.GetProperty("requestedBy").GetString().ShouldBe("dispatch-producer");
        notification.GetProperty("createdAt").GetDateTimeOffset().ShouldBeGreaterThan(default);

        // Question 2, legal basis, from the version that actually rendered.
        JsonElement template = state.GetProperty("template");
        template.GetProperty("legalBasis").GetString().ShouldBe("execucao-de-contrato");
        template.GetProperty("version").GetInt32().ShouldBe(sent.TemplateVersion);

        // Question 3, consent and channel: the rule evidence plus the ledger.
        JsonElement consentGate = EvaluationOf(state, "ConsentGate");
        consentGate.GetProperty("evidence").GetProperty("basis").GetString()
            .ShouldBe("contractual-or-legal");
        JsonElement ledgerEntry = AuditApi
            .Items(state.GetProperty("recipient"), "consentLedger")
            .ShouldHaveSingleItem();
        ledgerEntry.GetProperty("purpose").GetString().ShouldBe(ConsentPurpose);
        ledgerEntry.GetProperty("granted").GetBoolean().ShouldBeTrue();
        ledgerEntry.GetProperty("channel").GetString().ShouldBe("email");

        // Question 4, why this channel: the channel rule evidence carries the
        // sets it intersected, including the ones the recipient could reach.
        JsonElement channelSelection = EvaluationOf(state, "ChannelSelection");
        channelSelection.GetProperty("result").GetString().ShouldBe("filter-channels");
        JsonElement channelEvidence = channelSelection.GetProperty("evidence");
        channelEvidence.GetProperty("selected").EnumerateArray()
            .Select(value => value.GetString()).ShouldContain("email");
        channelEvidence.GetProperty("reachable").EnumerateArray()
            .Select(value => value.GetString()).ShouldContain("email");

        // Question 5, the exact text: the attempt carries both hashes and the
        // version carries the content hash of what was published.
        JsonElement attempt = AuditApi.Items(state, "attempts").ShouldHaveSingleItem();
        attempt.GetProperty("contentHashMasked").GetString().ShouldNotBeNullOrWhiteSpace();
        attempt.GetProperty("contentHashFull").GetString()
            .ShouldNotBe(attempt.GetProperty("contentHashMasked").GetString());
        template.GetProperty("contentHash").GetString().ShouldNotBeNullOrWhiteSpace();

        // Question 6, who approved: the approval of that exact version.
        JsonElement approval = AuditApi.Items(state, "approvals").ShouldHaveSingleItem();
        approval.GetProperty("subjectVersion").GetInt32().ShouldBe(sent.TemplateVersion);
        approval.GetProperty("contentHash").GetString()
            .ShouldBe(template.GetProperty("contentHash").GetString());
        approval.GetProperty("approverOid").GetString().ShouldBe("template-publisher");
        approval.GetProperty("role").GetString().ShouldBe("publisher");

        // Question 7, did the provider confirm delivery: answerable, and the
        // answer for this notification is that no feedback arrived. The empty
        // list states it; nothing was sent to this attempt's provider after the
        // acceptance above.
        AuditApi.Items(attempt, "deliveryEvents").ShouldBeEmpty();
        attempt.TryGetProperty("deliveredAt", out _).ShouldBeFalse();

        // Question 8, who looked afterwards: legitimate and empty on the first
        // read, because the disclosure of this very call is cut out.
        AuditApi.Items(trail, "priorAccesses").ShouldBeEmpty();
        body.GetProperty("disclosure").GetProperty("composedAt").GetDateTimeOffset()
            .ShouldBeGreaterThan(default);
    }

    [RequiresDockerFact]
    public async Task The_trail_block_carries_the_canonical_text_and_not_the_queried_columns()
    {
        Reconstructed sent = await SendAndReconstructAsync();

        JsonElement accepted = AuditApi.LinkOf(sent.Body.GetProperty("trail"), "notification.accepted");
        using var canonical = JsonDocument.Parse(accepted.GetProperty("canonical").GetString()!);
        JsonElement inside = canonical.RootElement;

        // Each scalar of the link equals the value inside the text the hash
        // covers, and so does the details document: reading the jsonb column
        // instead would quote bytes no hash vouches for.
        accepted.GetProperty("seq").GetInt64().ShouldBe(inside.GetProperty("seq").GetInt64());
        accepted.GetProperty("action").GetString().ShouldBe(inside.GetProperty("action").GetString());
        accepted.GetProperty("entityId").GetString().ShouldBe(inside.GetProperty("entityId").GetString());
        accepted.GetProperty("actorId").GetString().ShouldBe(inside.GetProperty("actorId").GetString());
        accepted.GetProperty("details").GetRawText()
            .ShouldBe(inside.GetProperty("details").GetRawText());
        AuditApi.ReplaysCleanly(accepted).ShouldBeTrue();
    }

    /// <summary>
    /// The named guard of the rule "evidence is served from the canonical text,
    /// never from the queried columns". Every other assertion about the trail is
    /// blind to it: on any row the platform writes, the column and the text
    /// agree, so an implementation reading the column passes them all. The only
    /// arrangement that tells the two apart is a row where they disagree, and
    /// that row has to be written by raw SQL, below the layer under test.
    /// Deleting this test does not weaken a slice, it turns the rule it guards
    /// into a comment.
    /// </summary>
    [RequiresDockerFact]
    public async Task Evidence_is_served_from_the_canonical_text_even_when_the_column_beside_it_drifted()
    {
        Reconstructed sent = await SendAndReconstructAsync();

        // The drift the chain does not cover: the column is jsonb and no hash
        // reaches it, so only the text is evidence.
        await AuditApi.InsertDriftedLinkAsync(fixture, sent.NotificationId, DriftProbeAction);

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(sent.NotificationId));

        status.ShouldBe(200);
        JsonElement drifted = AuditApi.LinkOf(body.GetProperty("trail"), DriftProbeAction);
        drifted.GetProperty("details").GetProperty("probe").GetString().ShouldBe("canonical");
        drifted.GetProperty("actorId").GetString().ShouldBe("canonical-actor");
        AuditApi.ReplaysCleanly(drifted).ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task Publishing_a_newer_version_does_not_move_the_answer_of_the_older_notification()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        JsonElement before = sent.Body.GetProperty("state").GetProperty("template");
        var hashWhenSent = before.GetProperty("contentHash").GetString();

        // The sensitive declaration travels with the newer version because a
        // version that dropped it would be refused: demoting a variable to an
        // ordinary one is how masking would be taken away from a template that
        // already had it. What this case needs is only that the published
        // phrasing move on.
        var newerVersion = await DispatchApi.PublishVersionAsync(
            fixture, sent.TemplateKey, "Texto totalmente diferente com o código {{ code }}.", SensitiveCode);
        newerVersion.ShouldBeGreaterThan(sent.TemplateVersion);

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(sent.NotificationId));

        status.ShouldBe(200);
        JsonElement after = body.GetProperty("state").GetProperty("template");
        after.GetProperty("version").GetInt32().ShouldBe(sent.TemplateVersion);
        after.GetProperty("contentHash").GetString().ShouldBe(hashWhenSent);

        // The version the notification used is now superseded, and the answer
        // says so instead of pretending it is the current one.
        after.GetProperty("versionStatus").GetString().ShouldBe("superseded");
        body.GetProperty("state").GetProperty("notification").GetProperty("templateVersion").GetInt32()
            .ShouldBe(sent.TemplateVersion);
    }

    [RequiresDockerFact]
    public async Task The_answer_carries_the_masked_variables_of_the_request_and_never_the_originals()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        JsonElement notification = sent.Body.GetProperty("state").GetProperty("notification");

        // The business payload of the request answers repudiation, and this is
        // the only surface it leaves through: withholding it here would mean it
        // leaves nowhere at all.
        JsonElement variables = notification.GetProperty("variablesMasked");
        variables.ValueKind.ShouldBe(JsonValueKind.Object);
        variables.GetProperty("code").GetString().ShouldNotBe(AccessCode);

        // What travels is the durable masked projection, never the encrypted
        // originals and never the plaintext value the producer sent.
        sent.Raw.ShouldNotContain(AccessCode);
        sent.Raw.Contains("variablesEnc", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task An_attempt_with_no_provider_feedback_answers_an_empty_list_and_names_acceptance()
    {
        Reconstructed sent = await SendAndReconstructAsync();

        JsonElement attempt = AuditApi.Items(sent.Body.GetProperty("state"), "attempts")
            .ShouldHaveSingleItem();

        // The list is present and empty, which is an assertion: the store holds
        // no feedback for this attempt. The delivery instant stays absent,
        // because nothing confirmed anything.
        AuditApi.Items(attempt, "deliveryEvents").ShouldBeEmpty();
        attempt.TryGetProperty("deliveredAt", out _).ShouldBeFalse();

        // What the answer does state about the provider is still acceptance.
        attempt.GetProperty("status").GetString().ShouldBe("sent");
        attempt.GetProperty("sentAt").GetDateTimeOffset().ShouldBeGreaterThan(default);
        attempt.GetProperty("providerMessageId").GetString().ShouldNotBeNullOrWhiteSpace();

        // The read receipt has no table behind it, so it is still not declared
        // in any form, not even as an empty array.
        sent.Raw.Contains("readAt", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        sent.Raw.Contains("readReceipt", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task An_attempt_with_provider_feedback_answers_it_in_the_order_the_provider_dated_it()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        Guid attemptId = await AuditApi.SingleAttemptIdAsync(fixture, sent.NotificationId);
        DateTimeOffset accepted = Truncated(DateTimeOffset.UtcNow.AddMinutes(-10));
        DateTimeOffset confirmed = accepted.AddMinutes(2);

        // Written newest first on purpose: an answer ordered by insertion, by
        // reception or by identity comes back reversed and fails here.
        await AuditApi.SeedProviderFeedbackAsync(
            fixture, sent.NotificationId, attemptId, "sendgrid", "sg-event-delivered", "delivered",
            confirmed, ProviderPayload(sent.Email, "delivered", "confirmed-probe"));
        await AuditApi.SeedProviderFeedbackAsync(
            fixture, sent.NotificationId, attemptId, "sendgrid", "sg-event-processed", "sent",
            accepted, ProviderPayload(sent.Email, "processed", "accepted-probe"));
        await AuditApi.StampDeliveredAtAsync(fixture, attemptId, confirmed);

        var disclosedBefore = await AuditApi.CountDisclosuresAsync(
            fixture, "notification", sent.NotificationId.ToString());

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(sent.NotificationId));

        status.ShouldBe(200);
        JsonElement attempt = AuditApi.Items(body.GetProperty("state"), "attempts").ShouldHaveSingleItem();
        IReadOnlyList<JsonElement> feedback = AuditApi.Items(attempt, "deliveryEvents");

        feedback.Select(item => item.GetProperty("providerEventId").GetString())
            .ShouldBe(["sg-event-processed", "sg-event-delivered"]);
        feedback.Select(item => item.GetProperty("kind").GetString()).ShouldBe(["sent", "delivered"]);
        feedback.Select(item => item.GetProperty("occurredAt").GetDateTimeOffset())
            .ShouldBe([accepted, confirmed]);
        feedback.ShouldAllBe(item => item.GetProperty("providerKey").GetString() == "sendgrid");

        // The attempt states the conclusion the hub applied, beside the
        // feedback that produced it.
        attempt.GetProperty("status").GetString().ShouldBe("delivered");
        attempt.GetProperty("deliveredAt").GetDateTimeOffset().ShouldBe(confirmed);

        // The answer that disclosed all of this left its own trail row, and it
        // did so before the body existed.
        (await AuditApi.CountDisclosuresAsync(fixture, "notification", sent.NotificationId.ToString()))
            .ShouldBe(disclosedBefore + 1);
    }

    /// <summary>
    /// The named guard of the rule "the provider payload is evidence held, not
    /// evidence served". The stored callback body carries the destination in
    /// the clear and the module keeps it sealed for that reason; the only thing
    /// keeping it out of this answer is a projection that names five columns.
    /// Every other assertion about the feedback list passes with a projection
    /// that also forwards the payload, so deleting this test does not weaken a
    /// slice, it turns the rule it guards into a comment.
    /// </summary>
    [RequiresDockerFact]
    public async Task No_raw_provider_payload_and_no_contact_value_leaves_with_the_feedback()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        Guid attemptId = await AuditApi.SingleAttemptIdAsync(fixture, sent.NotificationId);
        var probe = $"payload-probe-{Guid.NewGuid():N}";

        await AuditApi.SeedProviderFeedbackAsync(
            fixture, sent.NotificationId, attemptId, "sendgrid", "sg-event-bounced", "bounced",
            Truncated(DateTimeOffset.UtcNow), ProviderPayload(sent.Email, "bounce", probe),
            errorCode: "hard-bounce");

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(sent.NotificationId));

        status.ShouldBe(200);

        // The assertions are on the serialized body: a member added to the
        // projection is invisible to a check on a deserialized shape.
        raw.ShouldNotContain(probe);
        raw.ShouldNotContain(sent.Email);
        raw.Contains("payload", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        raw.Contains("suppressionSignal", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();

        JsonElement attempt = AuditApi.Items(body.GetProperty("state"), "attempts").ShouldHaveSingleItem();
        JsonElement recorded = AuditApi.Items(attempt, "deliveryEvents").ShouldHaveSingleItem();
        var members = recorded.EnumerateObject()
            .Select(member => member.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        members.ShouldBe(["errorCode", "kind", "occurredAt", "providerEventId", "providerKey"]);
        recorded.GetProperty("errorCode").GetString().ShouldBe("hard-bounce");
    }

    [RequiresDockerFact]
    public async Task The_recipient_block_masks_every_contact_and_never_carries_a_device_token()
    {
        Reconstructed sent = await SendAndReconstructAsync(deviceCount: 1);
        JsonElement recipient = sent.Body.GetProperty("state").GetProperty("recipient");

        JsonElement contactPoint = AuditApi.Items(recipient, "contactPoints").ShouldHaveSingleItem();
        contactPoint.GetProperty("channel").GetString().ShouldBe("email");
        contactPoint.GetProperty("maskedValue").GetString()!.ShouldContain("*");
        contactPoint.GetProperty("active").GetBoolean().ShouldBeTrue();
        contactPoint.TryGetProperty("removedAt", out _).ShouldBeFalse();

        // The plaintext contact never crosses the boundary, and neither does a
        // routing token, which is a credential rather than an address.
        sent.Raw.ShouldNotContain(sent.Email);
        foreach (var token in sent.DeviceTokens)
        {
            sent.Raw.ShouldNotContain(token);
        }

        sent.Raw.Contains("\"token\"", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task The_prior_access_list_carries_only_the_accesses_before_this_call()
    {
        Reconstructed sent = await SendAndReconstructAsync();
        AuditApi.Items(sent.Body.GetProperty("trail"), "priorAccesses").ShouldBeEmpty();

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement second, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(sent.NotificationId));

        status.ShouldBe(200);
        IReadOnlyList<JsonElement> priorAccesses = AuditApi.Items(
            second.GetProperty("trail"), "priorAccesses");

        // The first read disclosed two subjects and therefore left two links.
        // The second read sees exactly those and never its own footprint.
        priorAccesses.Count.ShouldBe(2);
        priorAccesses.ShouldAllBe(link => link.GetProperty("action").GetString() == "audit.read");
        DateTimeOffset composedAt = second.GetProperty("disclosure").GetProperty("composedAt")
            .GetDateTimeOffset();
        priorAccesses.ShouldAllBe(link => link.GetProperty("occurredAt").GetDateTimeOffset() < composedAt);
        priorAccesses.ShouldAllBe(link => AuditApi.ReplaysCleanly(link));

        // The lifecycle block never repeats a disclosure link.
        AuditApi.Actions(second.GetProperty("trail"), "links").ShouldNotContain("audit.read");
    }

    /// <summary>
    /// One verified provider body in the shape the callback route stores,
    /// carrying the destination in the clear exactly as the real one does. The
    /// probe is what a leak of any payload field drags into the answer.
    /// </summary>
    private static string ProviderPayload(string email, string providerEvent, string probe)
        => $$"""
            [{"email":"{{email}}","event":"{{providerEvent}}","sg_message_id":"sg-audit-1","probe":"{{probe}}"}]
            """;

    /// <summary>
    /// Cuts an instant to the microsecond the store keeps, so a comparison
    /// against the answer is about the value and never about the precision the
    /// column dropped.
    /// </summary>
    private static DateTimeOffset Truncated(DateTimeOffset instant)
        => new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerMicrosecond), instant.Offset);

    private static JsonElement EvaluationOf(JsonElement state, string rule)
        => AuditApi.Items(state, "policyEvaluations")
            .Single(evaluation => evaluation.GetProperty("rule").GetString() == rule);

    /// <summary>
    /// One notification pushed through ingestion, pipeline and a real dispatch
    /// pass against a fake provider, then reconstructed through the audit route.
    /// </summary>
    private async Task<Reconstructed> SendAndReconstructAsync(int deviceCount = 0)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, var templateVersion) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, var email, IReadOnlyList<string> tokens) =
            await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: deviceCount);

        HttpClient contacts = fixture.CreateContactsClient("contacts-writer");
        HttpResponseMessage consents = await ContactConsentApi.PutConsentsAsync(
            contacts, recipientId, ContactConsentApi.ConsentEntry(ConsentPurpose, "email", granted: true));
        consents.EnsureSuccessStatusCode();

        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));
        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-audit-1" }));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));

        return new Reconstructed(
            status, body, raw, notificationId, templateKey, templateVersion, email, tokens);
    }

    private sealed record Reconstructed(
        int Status,
        JsonElement Body,
        string Raw,
        Guid NotificationId,
        string TemplateKey,
        int TemplateVersion,
        string Email,
        IReadOnlyList<string> DeviceTokens);
}
