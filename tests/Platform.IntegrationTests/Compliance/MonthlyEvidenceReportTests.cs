using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Features.Reporting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.Audit;
using NotificationHub.IntegrationTests.TemplateManagement;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.Compliance;

/// <summary>
/// The recurring evidence report end to end: composed by the module that owns
/// no data, over the published contracts of the modules that do, and archived
/// in the write-once store of the module that owns immutability.
/// </summary>
/// <remarks>
/// Every test works on a month of its own. The report counts everything the
/// window holds, so two tests sharing a month would each measure the other's
/// setup.
/// </remarks>
[Collection(AuditMaintenanceCollectionDefinition.Name)]
public sealed class MonthlyEvidenceReportTests(AuditMaintenanceFixture fixture)
{
    /// <summary>
    /// A recipient identity nothing may echo. The report is aggregate, and an
    /// aggregate that carries one of these stopped being one.
    /// </summary>
    private const string RecipientProbe = "recipient-probe-4f7a1c9e";

    private const string ContactProbe = "probe.destinatario@exemplo.invalido";

    private const string ContentProbe = "codigo-de-autenticacao-probe-91731";

    [RequiresDockerFact]
    public async Task The_month_is_archived_under_a_deterministic_key_and_declares_what_it_measured()
    {
        DateTimeOffset month = MonthOffset(-30);
        await SeedAsync(month);

        await using ServiceProvider provider = fixture.BuildProvider();
        ComposeMonthlyEvidence.Outcome outcome = await ComposeAsync(provider, month);

        // The key is a function of the month and of nothing else, which is
        // what lets a rerun address exactly the object the first run wrote.
        var expected = "audit-export/v1/evidence/monthly/"
            + month.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            + "/evidence-report.v1.json";
        outcome.Key.ShouldBe(expected);
        outcome.AlreadyPresent.ShouldBeFalse();

        JsonElement root = await ReadReportAsync(outcome.Key);

        root.GetProperty("formatVersion").GetInt32().ShouldBe(1);
        root.GetProperty("report").GetString().ShouldBe("monthly-evidence");
        JsonElement window = root.GetProperty("window");
        window.GetProperty("month").GetString()
            .ShouldBe(month.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        window.GetProperty("fromInclusive").GetDateTimeOffset().ShouldBe(month);
        window.GetProperty("toExclusive").GetDateTimeOffset().ShouldBe(month.AddMonths(1));
        window.GetProperty("reconciliationGrace").GetString().ShouldBe("P3D");

        // Volumes and outcomes, aggregated inside the owning module.
        JsonElement critical = root.GetProperty("volumes").EnumerateArray()
            .Single(volume => volume.GetProperty("class").GetString() == "critical");
        critical.GetProperty("requested").GetInt64().ShouldBe(3);

        JsonElement email = Channel(root, "email");
        email.GetProperty("attempts").GetInt64().ShouldBe(4);
        email.GetProperty("acceptedByProvider").GetInt64().ShouldBe(3);
        email.GetProperty("delivered").GetInt64().ShouldBe(2);
        email.GetProperty("bounced").GetInt64().ShouldBe(1);
        email.GetProperty("deliveryConfirmation").GetString()
            .ShouldBe(DeliveryConfirmationSources.ProviderFeedback);
        email.GetProperty("deliveryRate").GetDouble().ShouldBe(2d / 3, 0.000001);

        // Push is unknown by design: the provider offers no delivery report
        // and no later lookup, so the report states the source of confirmation
        // and declares no rate at all.
        JsonElement push = Channel(root, "push");
        push.GetProperty("deliveryConfirmation").GetString()
            .ShouldBe(DeliveryConfirmationSources.AcceptanceOnly);
        push.TryGetProperty("deliveryRate", out _).ShouldBeFalse();

        root.GetProperty("refusals").GetProperty("byPolicyReason").EnumerateArray()
            .Single(reason => reason.GetProperty("name").GetString() == NotificationRejectionReasons.NoConsent)
            .GetProperty("count").GetInt64().ShouldBe(2);
        root.GetProperty("refusals").GetProperty("byTrailAction").EnumerateArray()
            .Single(action => action.GetProperty("action").GetString() == "notification.rejected_at_ingress")
            .GetProperty("byReason").EnumerateArray()
            .Single(reason => reason.GetProperty("name").GetString() == NotificationRejectionReasons.TemplateNotFound)
            .GetProperty("count").GetInt64().ShouldBe(1);

        // Catalog, policy and configuration changes with their approvers, and
        // the outcome of the chain verification, all out of the trail.
        JsonElement[] changes = [.. root.GetProperty("governedChanges").EnumerateArray()];
        changes.ShouldContain(change => change.GetProperty("action").GetString() == AuditActions.TemplateCreated);
        JsonElement killSwitch = changes
            .Single(change => change.GetProperty("action").GetString() == AuditActions.KillSwitchChanged);
        killSwitch.GetProperty("actorType").GetString().ShouldBe(AuditActorTypes.System);

        root.GetProperty("approvals").EnumerateArray().Single()
            .GetProperty("approverOid").GetString().ShouldBe("7c1c2a56-approver");

        JsonElement verification = root.GetProperty("chainVerification").EnumerateArray().Single();
        verification.GetProperty("intactRounds").GetInt64().ShouldBe(2);
        verification.GetProperty("failedRounds").GetInt64().ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task No_recipient_no_destination_and_no_content_reaches_the_report()
    {
        DateTimeOffset month = MonthOffset(-31);
        await SeedAsync(month);

        await using ServiceProvider provider = fixture.BuildProvider();
        ComposeMonthlyEvidence.Outcome outcome = await ComposeAsync(provider, month);

        var body = Encoding.UTF8.GetString(await ReadObjectAsync(outcome.Key));

        // The probes were planted on the rows the report reads: the recipient
        // of a seeded notification, the destination inside a trail detail, and
        // a fragment of rendered content. A member-name check would pass a
        // leak that reused a member the report already declares.
        body.Contains(RecipientProbe, StringComparison.Ordinal).ShouldBeFalse(
            "O identificador de destinatário apareceu no relatório mensal.");
        body.Contains(ContactProbe, StringComparison.Ordinal).ShouldBeFalse(
            "O valor de contato apareceu no relatório mensal.");
        body.Contains(ContentProbe, StringComparison.Ordinal).ShouldBeFalse(
            "Um fragmento de conteúdo renderizado apareceu no relatório mensal.");
    }

    [RequiresDockerFact]
    public async Task A_section_with_no_source_in_this_hub_is_absent_from_the_archived_bytes()
    {
        DateTimeOffset month = MonthOffset(-32);
        await SeedAsync(month);

        await using ServiceProvider provider = fixture.BuildProvider();
        ComposeMonthlyEvidence.Outcome outcome = await ComposeAsync(provider, month);
        JsonElement root = await ReadReportAsync(outcome.Key);

        foreach (var section in UnsourcedReportSections.All)
        {
            root.TryGetProperty(section, out JsonElement declared).ShouldBeFalse(
                $"A seção '{section}' não tem fonte no hub e apareceu como {declared.ValueKind}; "
                + "uma lista vazia afirmaria que nada daquele tipo aconteceu.");
        }

        // The counterpart, on the same bytes: what does have a source stays
        // declared, so the rule above is a rule and not a habit of omitting.
        root.GetProperty("governedChanges").GetArrayLength().ShouldBeGreaterThan(0);
        root.GetProperty("chainVerification").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [RequiresDockerFact]
    public async Task A_rerun_recognizes_the_digest_already_archived_and_overwrites_nothing()
    {
        DateTimeOffset month = MonthOffset(-33);
        await SeedAsync(month);

        await using ServiceProvider provider = fixture.BuildProvider();
        ComposeMonthlyEvidence.Outcome first = await ComposeAsync(provider, month);
        GetObjectMetadataResponse afterFirst = await HeadAsync(first.Key);

        ComposeMonthlyEvidence.Outcome second = await ComposeAsync(provider, month);
        GetObjectMetadataResponse afterSecond = await HeadAsync(second.Key);

        first.AlreadyPresent.ShouldBeFalse();
        second.AlreadyPresent.ShouldBeTrue();
        second.Key.ShouldBe(first.Key);
        second.Sha256Hex.ShouldBe(first.Sha256Hex);

        // Nothing was written the second time: the object still carries the
        // version and the instant of the first round.
        afterSecond.ETag.ShouldBe(afterFirst.ETag);
        afterSecond.LastModified.ShouldBe(afterFirst.LastModified);

        // And the trail carries one archival, not two: the rerun changed
        // nothing, so it had nothing to record.
        var archived = await fixture.ScalarAsync<long>(
            "SELECT COUNT(*) FROM audit.audit_event WHERE action = 'evidence.archived' "
            + $"AND entity_id = '{first.Key}'");
        archived.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_month_whose_sources_moved_after_the_archive_is_a_finding_and_never_an_overwrite()
    {
        DateTimeOffset month = MonthOffset(-34);
        await SeedAsync(month);

        await using ServiceProvider provider = fixture.BuildProvider();
        ComposeMonthlyEvidence.Outcome first = await ComposeAsync(provider, month);
        var archived = await ReadObjectAsync(first.Key);

        await SeedNotificationAsync(month.AddDays(9), "transactional", "delivered");
        Result<ComposeMonthlyEvidence.Outcome> second = await ComposeResultAsync(provider, month);

        second.IsFailure.ShouldBeTrue();
        second.ErrorKind.ShouldBe(ResultErrorKind.Integration);
        Convert.ToHexString(await ReadObjectAsync(first.Key)).ShouldBe(Convert.ToHexString(archived));
    }

    private static JsonElement Channel(JsonElement root, string channel)
        => root.GetProperty("channels").EnumerateArray()
            .Single(entry => entry.GetProperty("channel").GetString() == channel);

    private static async Task<Result<ComposeMonthlyEvidence.Outcome>> ComposeResultAsync(
        ServiceProvider provider,
        DateTimeOffset month)
    {
        // Awaited inside the scope on purpose: the handler holds a scoped
        // context, and a scope disposed while the read is in flight closes the
        // connection under it.
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ComposeMonthlyEvidence.Handler>()
            .HandleAsync(new ReportMonth(month.Year, month.Month), CancellationToken.None);
    }

    private static async Task<ComposeMonthlyEvidence.Outcome> ComposeAsync(
        ServiceProvider provider,
        DateTimeOffset month)
    {
        Result<ComposeMonthlyEvidence.Outcome> composed = await ComposeResultAsync(provider, month);
        composed.IsSuccess.ShouldBeTrue(composed.Error);
        return composed.Value!;
    }

    /// <summary>A month far enough in the past that no other suite writes into it.</summary>
    private static DateTimeOffset MonthOffset(int months)
    {
        DateTime utc = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .AddMonths(months);
    }

    private async Task<JsonElement> ReadReportAsync(string key)
        => JsonDocument.Parse(await ReadObjectAsync(key)).RootElement.Clone();

    private async Task<byte[]> ReadObjectAsync(string key)
    {
        using GetObjectResponse response = await fixture.S3.GetObjectAsync(
            AuditMaintenanceFixture.Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private Task<GetObjectMetadataResponse> HeadAsync(string key)
        => fixture.S3.GetObjectMetadataAsync(AuditMaintenanceFixture.Bucket, key);

    /// <summary>
    /// One month of sources: notifications and attempts in the owning module,
    /// governed changes, refusals, approvals and verification rounds in the
    /// trail. Every row that can carry a person carries a probe.
    /// </summary>
    private async Task SeedAsync(DateTimeOffset month)
    {
        await fixture.EnsurePartitionAsync(month);
        await fixture.EnsurePartitionAsync(DateTimeOffset.UtcNow);
        await fixture.EnsureNotificationsPartitionsAsync(month);

        DateTimeOffset day = month.AddDays(3);
        Guid critical = await SeedNotificationAsync(day, "critical", "delivered");
        await SeedNotificationAsync(day.AddHours(1), "critical", "delivered");
        await SeedNotificationAsync(day.AddHours(2), "critical", "rejected");
        await SeedNotificationAsync(day.AddHours(3), "transactional", "failed");

        await SeedAttemptAsync(critical, 1, "email", "delivered", day);
        await SeedAttemptAsync(critical, 2, "email", "delivered", day.AddMinutes(1));
        await SeedAttemptAsync(critical, 3, "email", "bounced", day.AddMinutes(2));
        await SeedAttemptAsync(critical, 4, "email", "queued", day.AddMinutes(3));
        await SeedAttemptAsync(critical, 5, "push", "sent", day.AddMinutes(4));

        await SeedRejectionAsync(critical, NotificationRejectionReasons.NoConsent, day.AddMinutes(5));
        await SeedRejectionAsync(critical, NotificationRejectionReasons.NoConsent, day.AddMinutes(6));
        await SeedRejectionAsync(critical, NotificationRejectionReasons.DuplicateWindow, day.AddMinutes(7));

        await fixture.AppendAsync($"evidence-report-template-{Guid.CreateVersion7():N}", day.AddHours(4));
        await fixture.AppendEntryAsync(new AuditEntry
        {
            ActorType = AuditActorTypes.System,
            ActorId = "dispatch-worker",
            Action = AuditActions.KillSwitchChanged,
            EntityType = AuditEntityTypes.KillSwitch,
            EntityId = "channel:sms",
            DetailsJson = """{"before":"open","after":"closed","scope":"channel","key":"sms"}""",
            OccurredAt = day.AddHours(5),
        });
        await fixture.AppendEntryAsync(new AuditEntry
        {
            ActorType = "producer",
            ActorId = "evidence-report-tests",
            Action = "notification.rejected_at_ingress",
            EntityType = AuditEntityTypes.Notification,
            EntityId = $"billing:{Guid.CreateVersion7():N}",
            DetailsJson =
                $$"""{"reason":"{{NotificationRejectionReasons.TemplateNotFound}}","target":"{{ContactProbe}}"}""",
            OccurredAt = day.AddHours(6),
        });
        await fixture.AppendEntryAsync(VerificationEntry(AuditActions.AuditChainVerified, day.AddHours(7)));
        await fixture.AppendEntryAsync(VerificationEntry(AuditActions.AuditChainVerified, day.AddHours(8)));
        await fixture.AppendEntryAsync(
            VerificationEntry(AuditActions.AuditChainVerificationFailed, day.AddHours(9)));

        await fixture.RecordApprovalAsync(new ApprovalGrant
        {
            SubjectType = ApprovalSubjectTypes.ClassPolicyVersion,
            SubjectId = $"billing:critical:{Guid.CreateVersion7():N}",
            SubjectVersion = 3,
            ContentHash = "0f0f0f",
            Role = ApprovalRoles.Publisher,
            ApproverOid = "7c1c2a56-approver",
            ApprovedAt = day.AddHours(10),
        });
    }

    private static AuditEntry VerificationEntry(string action, DateTimeOffset occurredAt)
        => new()
        {
            ActorType = AuditActorTypes.System,
            ActorId = "audit-maintenance",
            Action = action,
            EntityType = AuditEntityTypes.AuditPartition,
            EntityId = "audit_event_evidence_report_tests",
            DetailsJson = """{"origin":"evidence-report-tests"}""",
            OccurredAt = occurredAt,
        };

    private async Task<Guid> SeedNotificationAsync(
        DateTimeOffset createdAt,
        string notificationClass,
        string status)
    {
        var id = Guid.CreateVersion7();
        var instant = Instant(createdAt);
        await fixture.ExecuteAsync($$"""
            INSERT INTO notifications."notification" (
                id, application, idempotency_key, recipient_id, class, template_key, auth_flow,
                template_version, policy_version, variables_masked, correlation_id, requested_by,
                status, expires_at, created_at)
            VALUES (
                '{{id}}', 'billing', '{{Guid.CreateVersion7():N}}', '{{RecipientProbe}}',
                '{{notificationClass}}', 'billing.invoice', false, 1, 1,
                '{"amount":"***"}'::jsonb, NULL, 'evidence-report-tests',
                '{{status}}', '{{instant}}', '{{instant}}')
            """);
        return id;
    }

    private async Task SeedAttemptAsync(
        Guid notificationId,
        int sequence,
        string channel,
        string status,
        DateTimeOffset createdAt)
    {
        var instant = Instant(createdAt);
        var content = Convert.ToHexString(Encoding.UTF8.GetBytes(ContentProbe));
        await fixture.ExecuteAsync($"""
            INSERT INTO notifications."notification_attempt" (
                id, notification_id, sequence, channel, provider_key, rendered_content_enc,
                content_hash_full, content_hash_masked, status, status_changed_at, created_at)
            VALUES (
                '{Guid.CreateVersion7()}', '{notificationId}', {sequence}, '{channel}', 'sendgrid',
                '\x{content}'::bytea, 'aa', 'bb', '{status}', '{instant}', '{instant}')
            """);
    }

    private async Task SeedRejectionAsync(Guid notificationId, string reason, DateTimeOffset evaluatedAt)
    {
        var instant = Instant(evaluatedAt);
        await fixture.ExecuteAsync($$"""
            INSERT INTO notifications."policy_evaluation" (
                id, notification_id, rule, result, reason, evidence, evaluated_at)
            VALUES (
                '{{Guid.CreateVersion7()}}', '{{notificationId}}', 'consent', 'reject', '{{reason}}',
                '{"contact":"{{ContactProbe}}"}'::jsonb, '{{instant}}')
            """);
    }

    private static string Instant(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00";
}
