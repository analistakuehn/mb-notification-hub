using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

/// <summary>One seeded attempt and the notification it belongs to.</summary>
internal sealed record SeededAttempt(Guid NotificationId, Guid AttemptId, string Application);

/// <summary>
/// Seeding and signing shared by the delivery-tracking tests. The attempt
/// rows are written straight into the store instead of through the whole
/// pipeline: these tests are about what provider feedback does to an attempt,
/// and reaching that state through ingestion, policy and dispatch would make
/// every one of them depend on the correctness of paths they are not testing.
/// </summary>
internal static class DeliveryTrackingApi
{
    internal const string TwilioProvider = "twilio";
    internal const string SendGridProvider = "sendgrid";

    private const string InsertNotificationSql = """
        INSERT INTO notifications.notification (
            id, created_at, application, idempotency_key, recipient_id, class,
            template_key, template_version, policy_version, variables_masked,
            variables_enc, correlation_id, requested_by, status, release_at, expires_at)
        VALUES (
            {0}, {1}, {2}, {3}, {4}, 'transactional',
            'tpl-delivery-tracking', 1, 1, CAST({5} AS jsonb),
            NULL, NULL, 'integration-tests', 'dispatched', NULL, {6})
        """;

    private const string InsertAttemptSql = """
        INSERT INTO notifications.notification_attempt (
            id, created_at, notification_id, sequence, channel, provider_key,
            contact_point_id, device_token_id, provider_message_id, rendered_content_enc,
            content_hash_full, content_hash_masked, status, error_code,
            fallback_deadline, sent_at, delivered_at)
        VALUES (
            {0}, {1}, {2}, 1, {3}, {4},
            NULL, NULL, NULLIF({5}, ''), {6},
            {7}, {7}, {8}, NULL,
            NULL, {9}, NULL)
        """;

    /// <summary>Writes one notification and one attempt in the given stored status.</summary>
    internal static async Task<SeededAttempt> SeedAttemptAsync(
        DeliveryTrackingFixture fixture,
        string channel,
        string providerKey,
        string? providerMessageId,
        string status = NotificationAttemptStatuses.Sent)
    {
        var application = $"app-{Guid.NewGuid():N}";
        var notificationId = Guid.CreateVersion7();
        var attemptId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await fixture.ExecuteNotificationsDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(
                InsertNotificationSql,
                [
                    notificationId,
                    now,
                    application,
                    $"idem-{Guid.NewGuid():N}",
                    $"rcp-{Guid.NewGuid():N}",
                    "{}",
                    now.AddHours(1),
                ]);
            await db.Database.ExecuteSqlRawAsync(
                InsertAttemptSql,
                [
                    attemptId,
                    now,
                    notificationId,
                    channel,
                    providerKey,
                    providerMessageId ?? "",
                    new byte[] { 1, 2, 3 },
                    new string('a', 64),
                    status,
                    now,
                ]);
        });

        return new SeededAttempt(notificationId, attemptId, application);
    }

    internal static async Task<string> ReadAttemptStatusAsync(
        DeliveryTrackingFixture fixture,
        Guid attemptId)
        => await fixture.QueryNotificationsDbAsync(async db => await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.Id == attemptId)
            .Select(attempt => attempt.Status)
            .SingleAsync());

    internal static async Task<DateTimeOffset?> ReadAttemptDeliveredAtAsync(
        DeliveryTrackingFixture fixture,
        Guid attemptId)
        => await fixture.QueryNotificationsDbAsync(async db => await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.Id == attemptId)
            .Select(attempt => attempt.DeliveredAt)
            .SingleAsync());

    /// <summary>How many evidence rows the store holds for one provider event identity.</summary>
    internal static async Task<int> CountEvidenceAsync(
        DeliveryTrackingFixture fixture,
        string providerEventId)
        => await fixture.QueryNotificationsDbAsync(async db => await db.DeliveryEvents
            .AsNoTracking()
            .CountAsync(evidence => evidence.ProviderEventId == providerEventId));

    /// <summary>The one evidence row of a provider event identity, projected to what a test asserts.</summary>
    internal static async Task<EvidenceRow> ReadEvidenceAsync(
        DeliveryTrackingFixture fixture,
        string providerEventId)
        => await fixture.QueryNotificationsDbAsync(async db => await db.DeliveryEvents
            .AsNoTracking()
            .Where(evidence => evidence.ProviderEventId == providerEventId)
            .Select(evidence => new EvidenceRow(
                evidence.Id,
                evidence.AttemptId,
                evidence.NotificationId,
                evidence.Kind,
                evidence.ProviderMessageId,
                evidence.ErrorCode,
                evidence.AppliedAt,
                evidence.PayloadEncrypted))
            .SingleAsync());

    internal static async Task<int> CountDedupeAsync(
        DeliveryTrackingFixture fixture,
        string provider,
        string providerEventId)
        => await fixture.QueryNotificationsDbAsync(async db => await db.ProviderEventDedupes
            .AsNoTracking()
            .CountAsync(mark => mark.Provider == provider && mark.ProviderEventId == providerEventId));

    /// <summary>
    /// How many queue messages the store holds for one evidence row. Read
    /// through raw SQL because the payload column is jsonb, and jsonb has no
    /// pattern operator: the comparison has to name the text projection.
    /// </summary>
    internal static async Task<int> CountAnnouncedAsync(
        DeliveryTrackingFixture fixture,
        Guid deliveryEventId)
        => await fixture.QueryPlatformDbAsync(async db =>
        {
            var pattern = $"%{deliveryEventId}%";
            return await db.Database
                .SqlQuery<int>(
                    $"""
                     SELECT count(*)::int AS "Value" FROM platform.outbox
                     WHERE destination = 'delivery-events' AND payload::text LIKE {pattern}
                     """)
                .SingleAsync();
        });

    /// <summary>Builds one SendGrid batch callback and signs it as the provider does.</summary>
    internal static HttpRequestMessage SendGridCallback(
        DeliveryTrackingFixture fixture,
        string body,
        string? query = null,
        long? timestampOverride = null,
        bool tamper = false)
    {
        var timestamp = (timestampOverride
            ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes(body);
        var signature = fixture.SignSendGrid(timestamp, payload);
        if (tamper) signature[^1] ^= 0xFF;

        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/webhooks/{SendGridProvider}{query}")
        {
            Content = new ByteArrayContent(payload),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            "X-Twilio-Email-Event-Webhook-Signature", Convert.ToBase64String(signature));
        request.Headers.TryAddWithoutValidation("X-Twilio-Email-Event-Webhook-Timestamp", timestamp);
        return request;
    }

    /// <summary>
    /// Builds one Twilio status callback and signs it as the provider does:
    /// HMAC-SHA1 over the full request URL followed by every form field, name
    /// then value, ordered by name. The token defaults to the one this
    /// environment mints, and a host that verifies with another one passes its
    /// own, because a callback is only authentic against the secret the
    /// receiver holds.
    /// </summary>
    internal static HttpRequestMessage TwilioCallback(
        IReadOnlyList<KeyValuePair<string, string>> form,
        string? query = null,
        string signedBaseUrl = "http://localhost",
        bool tamper = false,
        string authToken = DeliveryTrackingFixture.TwilioAuthToken)
    {
        var path = $"/webhooks/{TwilioProvider}{query}";
        var body = string.Join(
            '&',
            form.Select(field =>
                $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
        var signature = TwilioSignature($"{signedBaseUrl}{path}", form, authToken);
        if (tamper) signature[^1] ^= 0xFF;

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.TryAddWithoutValidation(
            "X-Twilio-Signature", Convert.ToBase64String(signature));
        return request;
    }

    private static byte[] TwilioSignature(
        string requestUrl,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string authToken)
    {
        List<KeyValuePair<string, string>> ordered = [.. form];
        ordered.Sort(static (left, right) =>
        {
            var byName = string.CompareOrdinal(left.Key, right.Key);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });

        var payload = new StringBuilder(requestUrl);
        foreach (KeyValuePair<string, string> field in ordered) payload.Append(field.Key).Append(field.Value);

#pragma warning disable CA5350 // The provider dictates the algorithm of its own signature.
        return HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(authToken),
            Encoding.UTF8.GetBytes(payload.ToString()));
#pragma warning restore CA5350
    }
}

/// <summary>What a test reads back from one evidence row.</summary>
internal sealed record EvidenceRow(
    Guid Id,
    Guid? AttemptId,
    Guid? NotificationId,
    string Kind,
    string? ProviderMessageId,
    string? ErrorCode,
    DateTimeOffset? AppliedAt,
    byte[] PayloadEncrypted);
