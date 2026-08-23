using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Compliance;

/// <summary>
/// The guarantee of the surface: no answer leaves without its trail record, and
/// a record that cannot be written takes the answer down with it. The order is
/// proved by breaking the append: if the body were written first, a broken
/// append would still return an answer.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class AuditDisclosureTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_served_reconstruction_records_one_disclosure_per_subject_it_disclosed()
    {
        (Guid notificationId, var recipientId) = await AcceptedNotificationAsync();
        var notificationSubject = notificationId.ToString();

        var beforeNotification = await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject);
        var beforeRecipient = await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Recipient, recipientId);

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, _, _) = await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(notificationId));

        status.ShouldBe(200);
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject))
            .ShouldBe(beforeNotification + 1);
        (await AuditApi.CountDisclosuresAsync(fixture, AuditEntityTypes.Recipient, recipientId))
            .ShouldBe(beforeRecipient + 1);
    }

    [RequiresDockerFact]
    public async Task The_recorded_disclosure_carries_the_route_and_the_hashes_and_no_content()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();

        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);
        (var status, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));
        status.ShouldBe(200);

        // Read the record back through the surface itself: the second call sees
        // the first one as a prior access, with its details.
        (_, JsonElement second, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));
        JsonElement recorded = AuditApi.Items(second.GetProperty("trail"), "priorAccesses")
            .First(link => link.GetProperty("entityType").GetString() == AuditEntityTypes.Notification);

        JsonElement details = recorded.GetProperty("details");
        details.GetProperty("scope").GetString().ShouldBe("notification-evidence");
        details.GetProperty("route").GetString().ShouldNotBeNullOrWhiteSpace();
        JsonElement attempt = details.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("contentHashMasked").GetString().ShouldBe(
            AuditApi.Items(body.GetProperty("state"), "attempts").Single()
                .GetProperty("contentHashMasked").GetString());

        // The record names what left; it never quotes it.
        details.TryGetProperty("body", out _).ShouldBeFalse();
        details.TryGetProperty("subject", out _).ShouldBeFalse();
        details.TryGetProperty("maskedValue", out _).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task The_two_links_of_one_answer_share_one_access_identifier()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var first, _, _) = await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(notificationId));
        first.ShouldBe(200);
        (var second, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));
        second.ShouldBe(200);

        IReadOnlyList<JsonElement> priorAccesses = AuditApi.Items(
            body.GetProperty("trail"), "priorAccesses");

        // Two subjects, two links, one access. Without the shared identifier an
        // auditor counting rows would read two accesses where there was one.
        priorAccesses.Count.ShouldBe(2);
        var accessIds = priorAccesses
            .Select(link => link.GetProperty("details").GetProperty("accessId").GetString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        accessIds.ShouldHaveSingleItem();

        var subjects = priorAccesses
            .Select(link => link.GetProperty("entityType").GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();
        subjects.ShouldBe([AuditEntityTypes.Notification, AuditEntityTypes.Recipient]);
    }

    [RequiresDockerFact]
    public async Task A_second_call_earns_an_access_identifier_of_its_own()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(notificationId));
        await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(notificationId));
        (var status, JsonElement body, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));

        status.ShouldBe(200);
        var accessIds = AuditApi.Items(body.GetProperty("trail"), "priorAccesses")
            .Select(link => link.GetProperty("details").GetProperty("accessId").GetString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Sharing an identifier across subjects must not collapse two real
        // accesses into one.
        accessIds.Length.ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_refused_disclosure_takes_the_answer_down_and_discloses_nothing()
    {
        (Guid notificationId, var recipientId) = await AcceptedNotificationAsync();
        var notificationSubject = notificationId.ToString();
        var before = await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject);

        // The append is the only thing broken; everything the answer needed was
        // already composed when it ran.
        using WebApplicationFactory<Program> brokenTrail = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditDisclosureTrail>();
                services.AddScoped<IAuditDisclosureTrail, RefusingDisclosureTrail>();
            }));
        HttpClient auditor = fixture.CreateAuditorClient(brokenTrail, AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));

        // The answer had to be composed before the append ran, so a body-first
        // implementation would have returned 200 with the evidence in it.
        status.ShouldBe(503);
        raw.ShouldNotContain("\"trail\"");
        raw.ShouldNotContain("\"state\"");
        raw.ShouldContain("disclosure-record-unavailable");

        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject))
            .ShouldBe(before);
        (await AuditApi.CountDisclosuresAsync(fixture, AuditEntityTypes.Recipient, recipientId))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_refused_disclosure_of_content_never_serves_the_content()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();

        using WebApplicationFactory<Program> brokenTrail = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuditDisclosureTrail>();
                services.AddScoped<IAuditDisclosureTrail, RefusingDisclosureTrail>();
            }));
        HttpClient auditor = fixture.CreateAuditorClient(brokenTrail, AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 1));

        status.ShouldBe(503);
        raw.ShouldNotContain("\"body\"");
        raw.ShouldNotContain("\"disclosedForm\"");
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationId.ToString()))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_subject_that_does_not_exist_answers_the_same_way_and_records_no_trail_row()
    {
        var unknown = Guid.CreateVersion7();
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(unknown));

        status.ShouldBe(404);
        raw.ShouldContain("audit-subject-not-found");

        // The body never echoes what was asked, so the route is no existence
        // oracle even behind the audit role.
        raw.ShouldNotContain(unknown.ToString());
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, unknown.ToString()))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_principal_without_the_audit_role_is_refused_and_records_no_trail_row()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();
        var notificationSubject = notificationId.ToString();

        // The support role reads the query surface and nothing else: the two
        // role sets are disjoint by decision.
        HttpClient support = fixture.CreateReaderClient("support-agent");
        (var status, _, _) = await AuditApi.ReadAsync(support, AuditApi.EvidencePath(notificationId));

        status.ShouldBe(403);
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject))
            .ShouldBe(0);

        HttpClient producer = fixture.CreateProducerClient(
            "audit-producer", NotificationsApi.SendTransactional);
        (var producerStatus, _, _) = await AuditApi.ReadAsync(
            producer, AuditApi.EvidencePath(notificationId));

        producerStatus.ShouldBe(403);
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationSubject))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_refused_access_leaves_a_security_log_instead_of_a_trail_row()
    {
        (Guid notificationId, _) = await AcceptedNotificationAsync();
        var captured = new CapturingLoggerProvider();

        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(captured)));
        HttpClient support = fixture.CreateReaderClient(observed, "support-agent");

        (var status, _, _) = await AuditApi.ReadAsync(support, AuditApi.EvidencePath(notificationId));

        status.ShouldBe(403);
        captured.Lines.ShouldContain(message =>
            message.Contains("negado", StringComparison.Ordinal)
            && message.Contains("support-agent", StringComparison.Ordinal));
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationId.ToString()))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_subject_that_does_not_exist_leaves_a_security_log_instead_of_a_trail_row()
    {
        var unknown = Guid.CreateVersion7();
        var captured = new CapturingLoggerProvider();

        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(captured)));
        HttpClient auditor = fixture.CreateAuditorClient(observed, AuditApi.AuditorSubject);

        (var status, _, _) = await AuditApi.ReadAsync(auditor, AuditApi.EvidencePath(unknown));

        status.ShouldBe(404);
        captured.Lines.ShouldContain(message =>
            message.Contains("não encontrou o sujeito", StringComparison.Ordinal)
            && message.Contains(AuditApi.AuditorSubject, StringComparison.Ordinal));
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, unknown.ToString()))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_malformed_identity_is_a_bad_request_and_never_reaches_a_store()
    {
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(auditor, "/v1/audit/notifications/not-an-id");

        status.ShouldBe(400);
        raw.ShouldContain("invalid-request");
    }

    /// <summary>
    /// One notification queued on its dispatch step, without a dispatch pass:
    /// enough state for every audit answer, and no provider involved.
    /// </summary>
    private async Task<(Guid NotificationId, string RecipientId)> AcceptedNotificationAsync()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");
        return (notificationId, recipientId);
    }

    /// <summary>An append that always refuses, standing in for a trail the platform cannot write to.</summary>
    private sealed class RefusingDisclosureTrail : IAuditDisclosureTrail
    {
        public Task RecordAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A trilha está indisponível neste teste.");
    }
}
