using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(NotificationsApiCollectionDefinition.Name)]
public sealed class KillSwitchAdministrationTests(NotificationsApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Platform_admin_changes_state_with_token_actor_and_transactional_audit()
    {
        var key = $"producer-{Guid.NewGuid():N}";
        var objectId = $"oid-{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("subject-fallback", objectId);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("X-Actor", "forged-header");

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/producer/{key}",
            new { active = true, actor = "forged-body" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        KillSwitchRow stored = await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<KillSwitchRow>(
                $"""
                SELECT scope AS "Scope", key AS "Key", state AS "State",
                       version AS "Version", actor AS "Actor",
                       second_actor AS "SecondActor", updated_at AS "UpdatedAt"
                FROM notifications.kill_switch
                WHERE scope = 'producer' AND key = {key}
                """)
            .SingleAsync());
        stored.State.ShouldBe("active");
        stored.Version.ShouldBe(1);
        stored.Actor.ShouldBe(objectId);
        stored.SecondActor.ShouldBeNull();

        AuditEvent audit = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Action == "kill_switch.changed"
                && candidate.EntityId == $"producer:{key}"));
        audit.ActorId.ShouldBe(objectId);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        details.RootElement.GetProperty("before").GetString().ShouldBe("inactive");
        details.RootElement.GetProperty("after").GetString().ShouldBe("active");
        details.RootElement.GetProperty("scope").GetString().ShouldBe("producer");
        details.RootElement.GetProperty("key").GetString().ShouldBe(key);
        details.RootElement.GetProperty("actor").GetString().ShouldBe(objectId);
        details.RootElement.TryGetProperty("instant", out _).ShouldBeTrue();
    }

    [RequiresDockerFact]
    public async Task Repeating_the_same_state_is_a_no_op_without_a_second_audit_transition()
    {
        var key = $"application-{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("admin-no-op");

        HttpResponseMessage first = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/application/{key}",
            new { active = true });
        HttpResponseMessage second = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/application/{key}",
            new { active = true });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("changed").GetBoolean().ShouldBeFalse();
        body.GetProperty("version").GetInt64().ShouldBe(1);
        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.Action == "kill_switch.changed"
                && candidate.EntityId == $"application:{key}"));
        auditCount.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task Missing_active_is_a_bad_request_without_state_or_audit()
    {
        var key = $"producer-{Guid.NewGuid():N}";
        HttpClient admin = fixture.CreatePlatformAdminClient("admin-missing-active");

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/producer/{key}",
            new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().ShouldBe("kill-switch-active-required");

        var stateCount = await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.kill_switch
                WHERE scope = 'producer' AND key = {key}
                """)
            .SingleAsync());
        stateCount.ShouldBe(0);
        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.Action == "kill_switch.changed"
                && candidate.EntityId == $"producer:{key}"));
        auditCount.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task Missing_role_or_stable_actor_changes_neither_state_nor_audit()
    {
        var withoutRoleKey = $"channel-{Guid.NewGuid():N}";
        var withoutActorKey = $"channel-{Guid.NewGuid():N}";
        HttpClient producer = fixture.CreateProducerClient(
            "not-an-admin",
            NotificationsApi.SendTransactional);
        HttpClient unidentifiedAdmin = fixture.CreatePlatformAdminClientWithoutActor();

        HttpResponseMessage withoutRole = await producer.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/channel/{withoutRoleKey}",
            new { active = true });
        HttpResponseMessage withoutActor = await unidentifiedAdmin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/channel/{withoutActorKey}",
            new { active = true });

        withoutRole.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        withoutActor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var stateCount = await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.kill_switch
                WHERE key IN ({withoutRoleKey}, {withoutActorKey})
                """)
            .SingleAsync());
        stateCount.ShouldBe(0);
        var auditCount = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(candidate => candidate.Action == "kill_switch.changed"
                && (candidate.EntityId == $"channel:{withoutRoleKey}"
                    || candidate.EntityId == $"channel:{withoutActorKey}")));
        auditCount.ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task Audit_failure_rolls_back_the_state_transition()
    {
        var key = $"rollback-{Guid.NewGuid():N}";
        using WebApplicationFactory<Program> host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuditTrail>();
                services.AddScoped<IAuditTrail, ThrowingAuditTrail>();
            }));
        HttpClient admin = fixture.CreatePlatformAdminClient(host, "admin-rollback");

        HttpResponseMessage response = await admin.PutAsJsonAsync(
            $"/v1/notifications/kill-switch/producer/{key}",
            new { active = true });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var stateCount = await fixture.QueryNotificationsDbAsync(db => db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.kill_switch
                WHERE scope = 'producer' AND key = {key}
                """)
            .SingleAsync());
        stateCount.ShouldBe(0);
    }

    private sealed record KillSwitchRow(
        string Scope,
        string Key,
        string State,
        long Version,
        string Actor,
        string? SecondActor,
        DateTimeOffset UpdatedAt);

    private sealed class ThrowingAuditTrail : IAuditTrail
    {
        public Task AppendAsync(
            DbTransaction transaction,
            AuditEntry entry,
            CancellationToken cancellationToken)
        {
            _ = transaction;
            _ = entry;
            _ = cancellationToken;
            return Task.FromException(new InvalidOperationException("audit unavailable"));
        }

        public Task RecordApprovalAsync(
            DbTransaction transaction,
            ApprovalGrant grant,
            CancellationToken cancellationToken)
        {
            _ = transaction;
            _ = grant;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
