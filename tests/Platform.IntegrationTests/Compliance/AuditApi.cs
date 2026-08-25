using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.IntegrationTests.Notifications;

namespace NotificationHub.IntegrationTests.Compliance;

/// <summary>
/// Request builders and oracles of the audit surface: reading a route with its
/// raw body, so a test can assert on what actually crossed the wire and not on a
/// deserialized shape that hides an omitted member, plus the chain replay that
/// proves the trail block was rebuilt from the canonical text.
/// </summary>
internal static class AuditApi
{
    internal const string AuditorSubject = "compliance-auditor";

    internal static string EvidencePath(Guid notificationId)
        => $"/v1/audit/notifications/{NotificationIdentity.Format(notificationId)}";

    internal static string ContentPath(Guid notificationId, int sequence)
        => $"{EvidencePath(notificationId)}/attempts/{sequence}/content";

    internal static async Task<(int Status, JsonElement Body, string Raw)> ReadAsync(
        HttpClient client,
        string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        JsonElement body = raw.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(raw);
        return ((int)response.StatusCode, body, raw);
    }

    /// <summary>Counts the disclosures recorded for one subject of the trail.</summary>
    internal static Task<int> CountDisclosuresAsync(
        CorePipelineFixture fixture,
        string entityType,
        string entityId)
        => fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "audit.read"
                && auditEvent.EntityType == entityType
                && auditEvent.EntityId == entityId));

    internal static IReadOnlyList<JsonElement> Items(JsonElement parent, string member)
        => [.. parent.GetProperty(member).EnumerateArray()];

    internal static IReadOnlyList<string> Actions(JsonElement trail, string member)
        => [.. Items(trail, member).Select(link => link.GetProperty("action").GetString()!)];

    internal static JsonElement LinkOf(JsonElement trail, string action)
        => Items(trail, "links").Single(link => link.GetProperty("action").GetString() == action);

    /// <summary>
    /// Writes one chained link whose <c>details</c> column disagrees with the
    /// canonical text it stores. The chain still replays, because the hash
    /// covers the text and never the column: that is precisely the drift the
    /// module documents, and the only arrangement in which reading the column
    /// and parsing the text produce different answers.
    /// </summary>
    internal static async Task InsertDriftedLinkAsync(
        CorePipelineFixture fixture,
        Guid notificationId,
        string action)
    {
        var seq = await fixture.QueryAuditDbAsync(db => db.Database
            .SqlQuery<long>($"""
                SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq')) AS "Value"
                """)
            .SingleAsync());

        var id = Guid.CreateVersion7();
        DateTimeOffset occurredAt = DateTimeOffset.UtcNow;
        var canonical = Canonical(id, seq, action, notificationId, occurredAt);
        var prevHash = new byte[32];
        var material = new byte[prevHash.Length + Encoding.UTF8.GetByteCount(canonical)];
        prevHash.CopyTo(material, 0);
        Encoding.UTF8.GetBytes(canonical).CopyTo(material, prevHash.Length);
        var hash = SHA256.HashData(material);

        // The column carries a different document from the text on purpose.
        const string driftedDetails = """{"probe":"column"}""";
        await fixture.QueryAuditDbAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO audit.audit_event
                (id, seq, occurred_at, actor_type, actor_id, application, action,
                 entity_type, entity_id, details, canonical, prev_hash, hash)
            VALUES
                ({id}, {seq}, {occurredAt}, 'system', 'column-actor', NULL, {action},
                 'notification', {notificationId.ToString()}, CAST({driftedDetails} AS jsonb),
                 {canonical}, {prevHash}, {hash})
            """));
    }

    /// <summary>The attempt of a notification the pipeline produced exactly one attempt for.</summary>
    internal static Task<Guid> SingleAttemptIdAsync(CorePipelineFixture fixture, Guid notificationId)
        => fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.NotificationId == notificationId)
            .Select(attempt => attempt.Id)
            .SingleAsync());

    /// <summary>
    /// Writes one piece of provider feedback straight into the evidence table,
    /// sealed under the key scope the tracker seals with. The callback path is
    /// not exercised here on purpose: it needs a signed provider request and the
    /// queue that hands the event to its application, and this test is about
    /// what the evidence route does with a row that already exists.
    /// </summary>
    /// <remarks>
    /// <paramref name="rawPayload"/> is the verified provider body, which is
    /// where the destination travels in the clear. The seeded value carries a
    /// real contact so the disclosure assertions have something to catch.
    /// </remarks>
    internal static async Task SeedProviderFeedbackAsync(
        CorePipelineFixture fixture,
        Guid notificationId,
        Guid attemptId,
        string providerKey,
        string providerEventId,
        string kind,
        DateTimeOffset occurredAt,
        string rawPayload,
        string? errorCode = null)
    {
        var sealedPayload = await fixture.UsingScopeAsync(services => services
            .GetRequiredService<IEnvelopeCipher>()
            .EncryptAsync(
                DeliveryEventWriter.PayloadKeyScope,
                Encoding.UTF8.GetBytes(rawPayload),
                CancellationToken.None));

        var id = Guid.CreateVersion7();

        // The reception instant is this hub's own and lands the row in the
        // partition the migration provisioned; the provider's instant is the
        // one the answer publishes and the one the ordering follows.
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
        await fixture.QueryNotificationsDbAsync(db => db.Database.ExecuteSqlAsync($"""
            INSERT INTO notifications.delivery_event
                (id, received_at, attempt_id, notification_id, provider_key, provider_event_id,
                 provider_message_id, kind, occurred_at, error_code, suppression_signal,
                 payload_enc, applied_at)
            VALUES
                ({id}, {receivedAt}, {attemptId}, {notificationId}, {providerKey}, {providerEventId},
                 NULL, {kind}, {occurredAt}, {errorCode}, 'none',
                 {sealedPayload}, {receivedAt})
            """));
    }

    /// <summary>Stamps on the attempt the delivery instant the applier would have stamped.</summary>
    internal static Task StampDeliveredAtAsync(
        CorePipelineFixture fixture,
        Guid attemptId,
        DateTimeOffset deliveredAt)
        => fixture.QueryNotificationsDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE notifications.notification_attempt
               SET status = 'delivered', delivered_at = {deliveredAt}
             WHERE id = {attemptId}
            """));

    /// <summary>The canonical document of the crafted link, in the shape the chain writes.</summary>
    private static string Canonical(
        Guid id,
        long seq,
        string action,
        Guid notificationId,
        DateTimeOffset occurredAt)
    {
        var timestamp = occurredAt.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        return $$"""
            {"action":"{{action}}","actorId":"canonical-actor","actorType":"system","application":null,"details":{"probe":"canonical"},"entityId":"{{notificationId}}","entityType":"notification","id":"{{id:D}}","occurredAt":"{{timestamp}}","seq":{{seq}}}
            """;
    }

    /// <summary>
    /// Replays one link exactly as an independent verifier would: hash the
    /// predecessor hash followed by the canonical bytes and compare. A link that
    /// replays proves the answer carried the text the chain covers, not a
    /// re-serialization of the query columns.
    /// </summary>
    internal static bool ReplaysCleanly(JsonElement link)
    {
        var prevHash = Convert.FromHexString(link.GetProperty("prevHash").GetString()!);
        var canonical = Encoding.UTF8.GetBytes(link.GetProperty("canonical").GetString()!);
        var material = new byte[prevHash.Length + canonical.Length];
        prevHash.CopyTo(material, 0);
        canonical.CopyTo(material, prevHash.Length);
        return string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(material)),
            link.GetProperty("hash").GetString(),
            StringComparison.Ordinal);
    }
}
