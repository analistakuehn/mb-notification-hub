using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.ContactConsent;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class NotificationQuerySecurityTests(NotificationsApiFixture fixture)
{
    [RequiresDockerTheory]
    [InlineData("ntf_")]
    [InlineData("ntf_TOOSHORT")]
    [InlineData("ntf_IIIIIIIIIIIIIIIIIIIIIIIIII")]
    [InlineData("ntf_ZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("NTF_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public async Task A_malformed_identity_is_a_bad_request_and_the_answer_never_echoes_it(string id)
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var status, JsonElement problem, var raw) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{id}");

        status.ShouldBe(400);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
        raw.ShouldNotContain(id);
    }

    [RequiresDockerFact]
    public async Task A_well_formed_identity_that_does_not_exist_is_a_plain_not_found_without_the_value()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");
        var absent = NotificationId.Format(Guid.CreateVersion7());

        (var status, JsonElement problem, var raw) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{absent}");

        status.ShouldBe(404);
        problem.GetProperty("type").GetString().ShouldBe("notification-not-found");
        raw.ShouldNotContain(absent);
    }

    [RequiresDockerFact]
    public async Task Two_different_absent_identities_answer_with_the_very_same_body()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (_, JsonElement first, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{NotificationId.Format(Guid.CreateVersion7())}");
        (_, JsonElement second, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{NotificationId.Format(Guid.CreateVersion7())}");

        // An answer that varied with the identity asked about would turn the
        // route into an existence oracle for anyone holding the read role. The
        // trace id of the request is the only member allowed to differ, and it
        // comes from the request, not from the store.
        Comparable(first).ShouldBe(Comparable(second));
        Comparable(first).ShouldNotBeEmpty();
    }

    [RequiresDockerFact]
    public async Task Listing_without_a_subject_is_refused_and_no_route_lists_by_application()
    {
        HttpClient reader = fixture.CreateReaderClient("support-agent");

        (var bare, JsonElement bareProblem, _) = await NotificationQueryApi.ReadAsync(
            reader, "/v1/notifications");
        bare.ShouldBe(400);
        bareProblem.GetProperty("type").GetString().ShouldBe("invalid-request");

        (var byApplication, JsonElement applicationProblem, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications?application={NotificationsApi.Application}");
        byApplication.ShouldBe(400);
        applicationProblem.GetProperty("type").GetString().ShouldBe("invalid-request");

        (var byBlank, _, _) = await NotificationQueryApi.ReadAsync(reader, "/v1/notifications?correlationId=");
        byBlank.ShouldBe(400);
    }

    [RequiresDockerFact]
    public async Task A_producer_token_carries_no_read_grant()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId);

        HttpClient producer = fixture.CreateProducerClient("producer-only", NotificationsApi.SendTransactional);

        (await producer.GetAsync($"/v1/notifications/{accepted.PublicId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await producer.GetAsync($"/v1/recipients/{recipientId}/notifications"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await producer.GetAsync("/v1/notifications?correlationId=whatever"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task An_unauthenticated_caller_never_reaches_the_query_surface()
    {
        HttpClient anonymous = fixture.CreateClient();

        (await anonymous.GetAsync($"/v1/notifications/{NotificationId.Format(Guid.CreateVersion7())}"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [RequiresDockerFact]
    public async Task The_query_opens_the_read_connection_while_the_ingestion_keeps_the_write_one()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId);

        // A read connection pointed at a database that does not exist: if the
        // query still answered, the option would be decoration.
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Modules:Notifications:Persistence:Ef:ReadConnectionString"] =
                        fixture.PostgresConnectionString.Replace(
                            "Database=", "Database=nao_existe_", StringComparison.Ordinal),
                })));

        HttpClient reader = fixture.CreateReaderClient(host, "support-agent");
        (await reader.GetAsync($"/v1/notifications/{accepted.PublicId}"))
            .IsSuccessStatusCode.ShouldBeFalse();

        // The write path of the same host is untouched: it still uses the
        // write connection and still accepts.
        HttpClient producer = fixture.CreateProducerClient(
            host, NotificationQueryApi.ProducerSubject, NotificationsApi.SendTransactional);
        HttpResponseMessage stillAccepted = await NotificationsApi.PostNotificationAsync(
            producer,
            NotificationsApi.RequestBody(templateKey, recipientId: recipientId),
            Guid.NewGuid().ToString("N"));
        stillAccepted.IsSuccessStatusCode.ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task Without_a_read_connection_the_query_answers_from_the_write_database()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId);

        HttpClient reader = fixture.CreateReaderClient("support-agent");
        (var status, JsonElement body, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");

        status.ShouldBe(200);
        body.GetProperty("id").GetString().ShouldBe(accepted.PublicId);
        body.GetProperty("status").GetString().ShouldBe(NotificationStatuses.Accepted);

        // The arrays that always exist come empty and stay legible, because
        // the status explains why nothing happened yet.
        body.GetProperty("attempts").GetArrayLength().ShouldBe(0);
        body.GetProperty("policyEvaluations").GetArrayLength().ShouldBe(0);
        NotificationQueryApi.HasMember(body, "policyVersion").ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task Every_read_is_logged_with_the_principal_and_the_subject_and_appends_nothing_to_the_trail()
    {
        (var templateKey, var recipientId) = await SeedAsync();
        NotificationQueryApi.Accepted accepted = await NotificationQueryApi.AcceptAsync(
            fixture, templateKey, "transactional", recipientId);

        var logs = new CapturingLoggerProvider();
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(
            builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        HttpClient reader = fixture.CreateReaderClient(host, "compliance-analyst");

        var trailBefore = await fixture.QueryAuditDbAsync(db => db.AuditEvents.CountAsync());

        (var status, _, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/notifications/{accepted.PublicId}");
        status.ShouldBe(200);
        (var listStatus, _, _) = await NotificationQueryApi.ReadAsync(
            reader, $"/v1/recipients/{recipientId}/notifications");
        listStatus.ShouldBe(200);

        // Principal, route and subject, in the log and only in the log.
        var accessLines = logs.Lines
            .Where(line => line.Contains("Consulta de notificações atendida", StringComparison.Ordinal))
            .ToArray();
        accessLines.Length.ShouldBe(2);
        accessLines.ShouldAllBe(line => line.Contains("compliance-analyst"));
        accessLines.ShouldContain(line => line.Contains(accepted.PublicId));
        accessLines.ShouldContain(line => line.Contains(recipientId));
        accessLines.ShouldAllBe(line => line.Contains("/v1/"));

        // No audit entry: the trail of a read belongs to the audit routes, and
        // appending one here would serialize every query against ingestion.
        var trailAfter = await fixture.QueryAuditDbAsync(db => db.AuditEvents.CountAsync());
        trailAfter.ShouldBe(trailBefore);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "audit.read")))
            .ShouldBe(0);
    }

    private static Dictionary<string, string> Comparable(JsonElement problem)
        => problem.EnumerateObject()
            .Where(member => !member.NameEquals("traceId"))
            .ToDictionary(member => member.Name, member => member.Value.ToString(), StringComparer.Ordinal);

    private async Task<(string TemplateKey, string RecipientId)> SeedAsync()
    {
        (var templateKey, _) = await NotificationsApi.CreatePublishedTemplateAsync(fixture);
        return (templateKey, ContactConsentApi.NewRecipientId());
    }
}
