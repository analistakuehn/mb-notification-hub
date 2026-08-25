using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using NotificationHub.PerformanceTests.Infrastructure;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>
/// What one provider callback costs to ingest, at one batch size and one
/// transaction shape.
/// </summary>
internal sealed record WebhookIngestionCost(
    string Shape,
    int EventsPerCallback,
    PhaseStatistics Callback,
    double PerEventP50Ms,
    double PerEventP99Ms,
    int Callbacks);

/// <summary>
/// Measures the write path of the only public surface of this hub, at batch
/// sizes up to the ceiling the route accepts.
/// <para>
/// The design fixes a budget per event and the route answers per callback, so
/// the number that matters is how the response time grows with the number of
/// events in a batch. It grows because the cost per event is a transaction: the
/// deduplication claim, the evidence row and the outbox append commit together,
/// once per event. That is what makes the response time of this route a number
/// the caller picks, and it is why the route now refuses a batch above a
/// ceiling. This measures both halves of that statement: what a callback costs
/// at each batch size, and what one event costs inside it.
/// </para>
/// <para>
/// The two shapes are the comparison the review asked for. <c>por evento</c> is
/// what production does today. <c>por lote</c> commits the whole verified batch
/// once, which is the alternative, and the difference between the two columns
/// is the value of making that change, in milliseconds, at each size.
/// </para>
/// <para>
/// The sealing of the payload runs once per callback, exactly where the handler
/// runs it, so its cost appears in the callback column and never in the
/// per-event one. The envelope is rebuilt here rather than imported, for the
/// same reason the statements are: the probe is deliberately not a friend of
/// the API assembly. It is the same construction, HKDF-SHA256 to derive the
/// data key and AES-256-GCM to seal, over a payload of the same size, so the
/// cost is the production cost even though the code is not the production code.
/// </para>
/// <para>
/// What is outside this measurement: TLS, the signature verification of the
/// authentication handler, and the request pipeline. Those do not grow with the
/// batch, and measuring them needs a host and a client, which is the load gate
/// against a real environment.
/// </para>
/// </summary>
internal static class WebhookIngestionCostScenario
{
    /// <summary>Key scope of the stored evidence, as the writer names it.</summary>
    private const string PayloadKeyScope = "notifications-delivery-evidence";

    private const string ProviderKey = "sendgrid";

    /// <summary>
    /// A callback body of a size a provider actually posts, so the cipher and
    /// the row width are not measured against something implausibly small.
    /// </summary>
    private const int BytesPerEvent = 420;

    /// <summary>
    /// Events one cell is allowed to write, whatever the batch size. Without a
    /// bound the largest cell writes as many rows as the smallest one writes
    /// callbacks times the batch size, and the run stops being something anybody
    /// waits for: the interesting cell is the expensive one, so the naive shape
    /// spends almost the whole run inside it.
    /// </summary>
    private const int EventBudgetPerCell = 5_000;

    /// <summary>
    /// Callbacks a cell measures at least, whatever the budget says. A
    /// percentile over a handful of samples is not a percentile, and the report
    /// carries the sample count so a thin tail can be discounted rather than
    /// believed.
    /// </summary>
    private const int MinimumCallbacks = 15;

    private const string DedupeClaimSql = """
        INSERT INTO notifications.provider_event_dedupe (provider, provider_event_id, processed_at)
        VALUES (@provider, @providerEventId, @now)
        ON CONFLICT (provider, provider_event_id) DO NOTHING
        """;

    private const string EvidenceInsertSql = """
        INSERT INTO notifications.delivery_event
            (id, received_at, attempt_id, notification_id, provider_key, provider_event_id,
             provider_message_id, kind, occurred_at, error_code, suppression_signal,
             payload_enc, applied_at, suppression_reported_at)
        VALUES
            (@id, @now, NULL, NULL, @provider, @providerEventId,
             @providerMessageId, 'delivered', @now, NULL, 'none',
             @payload, NULL, NULL)
        """;

    private const string OutboxAppendSql = """
        INSERT INTO platform.outbox
            (id, destination, event_type, message_key, headers, payload,
             priority_class, created_at, sent_at, transport)
        VALUES
            (@outboxId, 'delivery-tracking', 'delivery.event_received', @evidenceKey,
             '{}'::jsonb, @outboxPayload::jsonb, 'critical', @now, NULL, 'sqs')
        """;

    internal static async Task<IReadOnlyList<WebhookIngestionCost>> RunAsync(
        ProbeDatabase database,
        IReadOnlyList<int> batchSizes,
        int callbacks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(batchSizes);

        var measured = new List<WebhookIngestionCost>();
        foreach (var size in batchSizes)
        {
            var cell = Math.Clamp(EventBudgetPerCell / Math.Max(size, 1), MinimumCallbacks, callbacks);
            measured.Add(await MeasureAsync(
                database, "transação por evento", size, cell, perBatch: false, cancellationToken));
            measured.Add(await MeasureAsync(
                database, "transação por lote", size, cell, perBatch: true, cancellationToken));
        }

        return measured;
    }

    private static async Task<WebhookIngestionCost> MeasureAsync(
        ProbeDatabase database,
        string shape,
        int eventsPerCallback,
        int callbacks,
        bool perBatch,
        CancellationToken cancellationToken)
    {
        var samples = new LatencyHistogram();
        var body = new byte[eventsPerCallback * BytesPerEvent];
        Array.Fill(body, (byte)'e');

        // A discarded pass first: the opening callback of a cell pays for a cold
        // plan cache and a cold pool, and at the smaller sizes that lands whole
        // on the percentile.
        await IngestAsync(database, body, eventsPerCallback, perBatch, cancellationToken);

        // Every cell starts from the same place, which is the discipline the
        // contention arms already keep and the first version of this scenario
        // did not. Each cell writes thousands of rows, so without it the cell
        // that happens to run while the checkpointer is flushing the previous
        // cell's writes carries a tail that belongs to the host. That is not a
        // subtle effect here: it made a batch of fifty read as five times more
        // expensive per event than a batch of two hundred, which is impossible
        // for a path that is linear in the number of events, and it made the
        // per-batch shape read as slower than the per-event one at the same
        // size. A measurement that produces an impossible ordering is measuring
        // the bench, and publishing it would have been worse than not measuring.
        await database.ExecuteAsync("CHECKPOINT", cancellationToken);

        for (var callback = 0; callback < callbacks; callback++)
        {
            var started = Stopwatch.GetTimestamp();
            await IngestAsync(database, body, eventsPerCallback, perBatch, cancellationToken);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        PhaseStatistics stats = samples.Snapshot();
        return new WebhookIngestionCost(
            shape,
            eventsPerCallback,
            stats,
            stats.P50 / eventsPerCallback,
            stats.P99 / eventsPerCallback,
            callbacks);
    }

    /// <summary>
    /// One callback, written the way the handler writes it: seal the body once,
    /// then record every event. The only thing the shape changes is where the
    /// transaction boundary sits.
    /// </summary>
    private static async Task IngestAsync(
        ProbeDatabase database,
        byte[] body,
        int eventsPerCallback,
        bool perBatch,
        CancellationToken cancellationToken)
    {
        var sealedPayload = Seal(body);
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);

        NpgsqlTransaction? batch = perBatch
            ? await connection.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            for (var index = 0; index < eventsPerCallback; index++)
            {
                NpgsqlTransaction transaction = batch
                    ?? await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    var eventId = Guid.CreateVersion7().ToString("N");
                    var evidenceId = Guid.CreateVersion7();
                    DateTimeOffset now = DateTimeOffset.UtcNow;

                    await ExecuteAsync(connection, transaction, DedupeClaimSql, command =>
                    {
                        Text(command, "provider", ProviderKey);
                        Text(command, "providerEventId", eventId);
                        Instant(command, "now", now);
                    }, cancellationToken);

                    await ExecuteAsync(connection, transaction, EvidenceInsertSql, command =>
                    {
                        Uuid(command, "id", evidenceId);
                        Instant(command, "now", now);
                        Text(command, "provider", ProviderKey);
                        Text(command, "providerEventId", eventId);
                        Text(command, "providerMessageId", $"msg-{eventId}");
                        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea)
                        {
                            Value = sealedPayload,
                        });
                    }, cancellationToken);

                    await ExecuteAsync(connection, transaction, OutboxAppendSql, command =>
                    {
                        Uuid(command, "outboxId", Guid.CreateVersion7());
                        Text(command, "evidenceKey", evidenceId.ToString());
                        Text(command, "outboxPayload", string.Create(
                            CultureInfo.InvariantCulture,
                            $$"""{"deliveryEventId":"{{evidenceId}}"}"""));
                        Instant(command, "now", now);
                    }, cancellationToken);
                }
                finally
                {
                    if (batch is null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        await transaction.DisposeAsync();
                    }
                }
            }

            if (batch is not null) await batch.CommitAsync(cancellationToken);
        }
        finally
        {
            if (batch is not null) await batch.DisposeAsync();
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 0;
        bind(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Text(NpgsqlCommand command, string name, string value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value });

    private static void Uuid(NpgsqlCommand command, string name, Guid value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value });

    private static void Instant(NpgsqlCommand command, string name, DateTimeOffset value)
        => command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value });

    private const int NonceSize = 12;

    private const int TagSize = 16;

    private static readonly byte[] MasterKey =
        SHA256.HashData(Encoding.UTF8.GetBytes("probe-master-key-for-measurement"));

    private static readonly byte[] KeyIdBytes = Encoding.UTF8.GetBytes("probe");

    /// <summary>
    /// The envelope the writer produces, rebuilt: the data key of the scope is
    /// derived with HKDF-SHA256 from the master key, the payload is sealed with
    /// AES-256-GCM, and the key id and the scope are bound as associated data.
    /// Which master key a deployment holds does not change what sealing costs.
    /// </summary>
    private static byte[] Seal(byte[] plaintext)
    {
        var envelope = new byte[2 + KeyIdBytes.Length + NonceSize + TagSize + plaintext.Length];
        envelope[0] = 1;
        envelope[1] = (byte)KeyIdBytes.Length;
        KeyIdBytes.CopyTo(envelope, 2);

        Span<byte> nonce = envelope.AsSpan(2 + KeyIdBytes.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> tag = envelope.AsSpan(2 + KeyIdBytes.Length + NonceSize, TagSize);
        Span<byte> ciphertext = envelope.AsSpan(2 + KeyIdBytes.Length + NonceSize + TagSize);

        Span<byte> dataKey = stackalloc byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            MasterKey,
            dataKey,
            salt: null,
            info: Encoding.UTF8.GetBytes(PayloadKeyScope));

        using var cipher = new AesGcm(dataKey, TagSize);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, KeyIdBytes);
        return envelope;
    }
}
