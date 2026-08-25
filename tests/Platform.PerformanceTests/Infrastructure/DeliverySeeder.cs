using System.Globalization;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// Synthetic backlogs for the two delivery tables whose cost only shows up in
/// volume: the attempts the scheduler claims from, and the evidence the webhook
/// path writes into.
/// <para>
/// The shape matters more than the count. The scans this seeds for are
/// interesting precisely because their matches are rare: an overdue attempt is
/// a small minority of a table that is mostly settled, and a scan that finds
/// its rows quickly on a table where everything matches has proved nothing. So
/// the mixture is written the way the producers write it, with the overdue rows
/// as a fraction and the rest terminal.
/// </para>
/// </summary>
internal static class DeliverySeeder
{
    /// <summary>Marker of the rows this probe wrote, so a repeated run counts what it owns.</summary>
    internal const string RequestedBy = "delivery-probe";

    /// <summary>
    /// One overdue attempt in every this many. Production has far fewer, and
    /// that is the direction that matters: a rarer match makes the index the
    /// only affordable plan, so understating the rarity would flatter the scan.
    /// </summary>
    private const int OverdueEvery = 500;

    private const int UnknownEvery = 997;

    /// <summary>
    /// Fills notifications and one attempt each, up to the requested count.
    /// Rows already present from an earlier round are kept: the probe measures
    /// the same table twice at different volumes, and reseeding from scratch
    /// would spend the run on inserts instead of on the question.
    /// </summary>
    internal static async Task<int> FillAttemptsAsync(
        ProbeDatabase database,
        int notifications,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var present = (int)await database.ScalarAsync<long>(
            $"SELECT count(*) FROM notifications.notification WHERE requested_by = '{RequestedBy}'",
            cancellationToken);
        var missing = notifications - present;
        if (missing <= 0)
        {
            return present;
        }

        progress?.Report(string.Create(
            CultureInfo.InvariantCulture,
            $"  semeando {missing:N0} notificações e uma tentativa cada."));

        var seedNotifications = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO notifications.notification
                 (id, created_at, application, idempotency_key, recipient_id, class, template_key,
                  auth_flow, template_version, policy_version, variables_masked, variables_enc,
                  correlation_id, requested_by, status, release_at, expires_at)
             SELECT
                 gen_random_uuid(), now() - (n * interval '1 second'),
                 'app-probe', 'idem-{RequestedBy}-' || (n + {present}), 'cus_' || n,
                 CASE WHEN n % 4 = 0 THEN 'critical' ELSE 'transactional' END,
                 'tpl-probe', n % 40 = 0, 1, 1, jsonb_build_object(), NULL, NULL,
                 '{RequestedBy}', 'dispatched', NULL, now() + interval '1 day'
             FROM generate_series(1, {missing}) AS n
             """);
        await database.ExecuteAsync(seedNotifications, cancellationToken);

        var seedAttempts = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO notifications.notification_attempt
                 (id, created_at, notification_id, sequence, channel, provider_key, contact_point_id,
                  device_token_id, provider_message_id, rendered_content_enc, content_hash_full,
                  content_hash_masked, status, error_code, fallback_deadline, plan_advanced_at,
                  status_changed_at, fallback_requested_at, sent_at, delivered_at)
             SELECT
                 gen_random_uuid(), source.created_at, source.id, 1, 'push', 'fcm',
                 NULL, NULL, NULL, '\x01'::bytea, repeat('a', 64), repeat('a', 64),
                 CASE
                     WHEN source.row_number % {OverdueEvery} = 0 THEN 'sent'
                     WHEN source.row_number % {UnknownEvery} = 0 THEN 'unknown'
                     ELSE 'delivered'
                 END,
                 NULL,
                 CASE
                     WHEN source.row_number % {OverdueEvery} = 0
                       OR source.row_number % {UnknownEvery} = 0
                     THEN source.created_at + interval '30 seconds'
                     ELSE NULL
                 END,
                 NULL, source.created_at, NULL, source.created_at, NULL
             FROM (
                 SELECT notification.id, notification.created_at,
                        row_number() OVER (ORDER BY notification.created_at) AS row_number
                 FROM notifications.notification AS notification
                 WHERE notification.requested_by = '{RequestedBy}'
                   AND NOT EXISTS (
                       SELECT 1 FROM notifications.notification_attempt AS existing
                       WHERE existing.notification_id = notification.id)
             ) AS source
             """);
        await database.ExecuteAsync(seedAttempts, cancellationToken);
        await database.ExecuteAsync("ANALYZE notifications.notification", cancellationToken);
        await database.ExecuteAsync("ANALYZE notifications.notification_attempt", cancellationToken);
        return notifications;
    }

    /// <summary>
    /// Removes everything this probe wrote into the delivery tables. It exists
    /// for the operator pointing the probe at a database that is not a
    /// throwaway container, where leaving synthetic attempts behind would feed
    /// a scheduler that is not part of the experiment.
    /// </summary>
    internal static async Task ClearAsync(ProbeDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await database.ExecuteAsync(
            $"""
             DELETE FROM notifications.notification_attempt AS attempt
             USING notifications.notification AS notification
             WHERE notification.id = attempt.notification_id
               AND notification.requested_by = '{RequestedBy}'
             """,
            cancellationToken);
        await database.ExecuteAsync(
            $"DELETE FROM notifications.notification WHERE requested_by = '{RequestedBy}'",
            cancellationToken);
    }
}
