using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The exported evidence and what can be proved from it alone. Every test here
/// works on a month of its own, because the closing tests detach partitions
/// and the export tests assert exact sequence ranges.
/// </summary>
[Collection(AuditMaintenanceCollectionDefinition.Name)]
public sealed class AuditWormExportTests(AuditMaintenanceFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_exported_lines_are_the_stored_canonical_bytes_and_a_rerun_writes_nothing_new()
    {
        DateTimeOffset month = MonthOffset(-6);
        DateTimeOffset day = month.AddDays(4);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"export-bytes-1-{Guid.CreateVersion7():N}", day);
        await fixture.AppendAsync($"export-bytes-2-{Guid.CreateVersion7():N}", day.AddHours(1));

        // A row whose canonical text carries spacing no serializer would
        // produce again. If the export reparsed and rewrote the JSON, both the
        // byte comparison below and the chain replay would fail.
        await AppendWithIrregularSpacingAsync(month, day.AddHours(2));

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        var firstRun = await RunDailyAsync(provider, window);
        var secondRun = await RunDailyAsync(provider, window);

        firstRun.ShouldBe(1);
        secondRun.ShouldBe(0);

        List<string> stored = await StoredCanonicalAsync(month);
        stored.Count.ShouldBe(3);

        var folder = DailyFolder(window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime));
        List<string> exported = Lines(AuditGzip.Decompress(
            await ReadObjectAsync(folder + AuditExportKeys.EventsObject)));
        exported.ShouldBe(stored);

        // The spacing of the hand-written row survives the round trip. An
        // export that reparsed the JSON would emit compact text here, match
        // nothing that was hashed, and pass the comparison above only because
        // both sides had been normalized.
        exported.ShouldContain(line => line.StartsWith("{ \"action\"", StringComparison.Ordinal));

        AuditExportManifest manifest = AuditExportManifest.Parse(
            await ReadObjectAsync(folder + AuditExportKeys.ManifestObject));
        manifest.ChainedCount.ShouldBe(3);
        manifest.Type.ShouldBe("daily");
        manifest.Partition.ShouldBe(window.PartitionName);
        manifest.HeadPrevHash.ShouldBe(
            Convert.ToHexStringLower(AuditChain.PartitionAnchor(window.PartitionName)));
    }

    [RequiresDockerFact]
    public async Task The_written_objects_carry_compliance_retention_in_a_bucket_with_object_lock()
    {
        DateTimeOffset month = MonthOffset(-7);
        DateTimeOffset day = month.AddDays(2);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"export-lock-{Guid.CreateVersion7():N}", day);

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        await RunDailyAsync(provider, window);

        GetObjectLockConfigurationResponse lockConfiguration = await fixture.S3
            .GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest
            {
                BucketName = AuditMaintenanceFixture.Bucket,
            });
        lockConfiguration.ObjectLockConfiguration.ObjectLockEnabled.ShouldBe(ObjectLockEnabled.Enabled);

        var folder = DailyFolder(window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime));
        GetObjectMetadataResponse head = await fixture.S3.GetObjectMetadataAsync(
            AuditMaintenanceFixture.Bucket, folder + AuditExportKeys.EventsObject);
        head.ObjectLockMode.ShouldBe(ObjectLockMode.Compliance);
        head.ObjectLockRetainUntilDate.ShouldNotBeNull();
        head.ObjectLockRetainUntilDate.Value.ShouldBeGreaterThan(DateTime.UtcNow.AddDays(300));
    }

    [RequiresDockerFact]
    public async Task An_auditor_verifies_the_export_from_the_bucket_alone()
    {
        DateTimeOffset month = MonthOffset(-8);
        DateTimeOffset day = month.AddDays(6);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"export-verify-1-{Guid.CreateVersion7():N}", day);
        await fixture.AppendAsync($"export-verify-2-{Guid.CreateVersion7():N}", day.AddMinutes(30));

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        await RunDailyAsync(provider, window);

        // Nothing here can reach the database: the verifier's only
        // collaborator is the object store, and it is built here by hand.
        WormExportVerifier verifier = BucketOnlyVerifier();
        var manifestKey = DailyFolder(window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime))
            + AuditExportKeys.ManifestObject;

        WormVerificationResult verdict = await verifier.VerifyAsync(manifestKey, CancellationToken.None);

        verdict.IsValid.ShouldBeTrue(verdict.Failure);
        verdict.Manifest!.ChainedCount.ShouldBe(2);
    }

    [RequiresDockerFact]
    public async Task A_single_altered_byte_in_the_exported_events_fails_the_bucket_only_verification()
    {
        DateTimeOffset month = MonthOffset(-9);
        DateTimeOffset day = month.AddDays(8);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"export-tamper-{Guid.CreateVersion7():N}", day);

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        await RunDailyAsync(provider, window);

        var folder = DailyFolder(window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime));
        var eventsKey = folder + AuditExportKeys.EventsObject;
        var original = await ReadObjectAsync(eventsKey);
        var altered = (byte[])original.Clone();
        altered[^1] ^= 0x01;
        await OverwriteObjectAsync(eventsKey, altered);

        WormVerificationResult verdict = await BucketOnlyVerifier()
            .VerifyAsync(folder + AuditExportKeys.ManifestObject, CancellationToken.None);

        verdict.IsValid.ShouldBeFalse();
        verdict.Failure.ShouldBe("compressed-digest-mismatch");
    }

    [RequiresDockerFact]
    public async Task A_forged_manifest_fails_the_signature_check_against_the_archived_public_key()
    {
        DateTimeOffset month = MonthOffset(-10);
        DateTimeOffset day = month.AddDays(3);
        await fixture.EnsurePartitionAsync(month);
        await fixture.AppendAsync($"export-forge-{Guid.CreateVersion7():N}", day);

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        await RunDailyAsync(provider, window);

        var folder = DailyFolder(window.PartitionName, DateOnly.FromDateTime(day.UtcDateTime));
        var manifestKey = folder + AuditExportKeys.ManifestObject;
        AuditExportManifest manifest = AuditExportManifest.Parse(await ReadObjectAsync(manifestKey));
        AuditExportManifest forged = manifest with { ChainedCount = manifest.ChainedCount + 1 };
        await OverwriteObjectAsync(manifestKey, forged.CanonicalBytes());

        WormVerificationResult verdict = await BucketOnlyVerifier()
            .VerifyAsync(manifestKey, CancellationToken.None);

        verdict.IsValid.ShouldBeFalse();
        verdict.Failure.ShouldBe("manifest-digest-mismatch");
    }

    [RequiresDockerFact]
    public async Task Each_day_links_to_the_previous_one_and_removing_a_day_breaks_the_walk()
    {
        DateTimeOffset month = MonthOffset(-11);
        await fixture.EnsurePartitionAsync(month);
        DateTimeOffset[] days = [month.AddDays(9), month.AddDays(10), month.AddDays(11)];
        foreach (DateTimeOffset day in days)
        {
            await fixture.AppendAsync($"export-chain-{day:yyyyMMdd}-{Guid.CreateVersion7():N}", day);
        }

        await using ServiceProvider provider = fixture.BuildProvider();
        MonthlyPartitionWindow window = WindowOf(month);
        (await RunDailyAsync(provider, window)).ShouldBe(3);

        WormExportVerifier verifier = BucketOnlyVerifier();
        var lastKey = DailyFolder(window.PartitionName, DateOnly.FromDateTime(days[2].UtcDateTime))
            + AuditExportKeys.ManifestObject;
        var middleKey = DailyFolder(window.PartitionName, DateOnly.FromDateTime(days[1].UtcDateTime))
            + AuditExportKeys.ManifestObject;

        // Each manifest continues the chain of the day before it: the head of
        // one slice is the tail of the previous.
        AuditExportManifest last = AuditExportManifest.Parse(await ReadObjectAsync(lastKey));
        AuditExportManifest middle = AuditExportManifest.Parse(await ReadObjectAsync(middleKey));
        last.Previous!.Key.ShouldBe(middleKey);
        last.Previous.TailHash.ShouldBe(middle.TailHash);
        last.HeadPrevHash.ShouldBe(middle.TailHash);

        // The walk does not stop at the partition boundary: the first slice of
        // a partition links to the last manifest of the one before it, so the
        // count is at least these three days and grows with the history.
        WormChainWalkResult intact = await verifier.WalkAsync(lastKey, CancellationToken.None);
        intact.IsValid.ShouldBeTrue(intact.Failure);
        intact.VisitedCount.ShouldBeGreaterThanOrEqualTo(3);

        await fixture.S3.DeleteObjectAsync(AuditMaintenanceFixture.Bucket, middleKey);
        WormChainWalkResult broken = await verifier.WalkAsync(lastKey, CancellationToken.None);

        broken.IsValid.ShouldBeFalse();
        broken.Failure.ShouldBe("previous-manifest-missing");
        broken.BrokenKey.ShouldBe(middleKey);
    }

    /// <summary>
    /// Appends a row whose canonical text is not what a serializer would emit
    /// for the same content, with the hash computed over those exact bytes.
    /// Only an export that copies the stored bytes keeps such a row verifiable.
    /// </summary>
    private async Task AppendWithIrregularSpacingAsync(DateTimeOffset month, DateTimeOffset occurredAt)
    {
        MonthlyPartitionWindow window = WindowOf(month);
        var id = Guid.CreateVersion7();
        var seq = await fixture.ScalarAsync<long>(
            "SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))");
        var previousHex = await fixture.ScalarAsync<string>($"""
            SELECT encode(hash, 'hex')
            FROM audit.audit_event
            WHERE occurred_at >= '{window.FromInclusive:yyyy-MM-dd}' AND occurred_at < '{window.ToExclusive:yyyy-MM-dd}'
              AND hash IS NOT NULL
            ORDER BY seq DESC
            LIMIT 1
            """);
        var previous = previousHex is null
            ? AuditChain.PartitionAnchor(window.PartitionName)
            : Convert.FromHexString(previousHex);

        var occurred = occurredAt.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var canonical = "{ \"action\":\"template.created\", \"actorId\":\"spacing\", "
            + "\"actorType\":\"system\", \"application\":null, \"details\":{\"origin\":\"spacing\"}, "
            + $"\"entityId\":\"spacing-{id:N}\", \"entityType\":\"template\", \"id\":\"{id:D}\", "
            + $"\"occurredAt\":\"{occurred}\", \"seq\":{seq} }}";
        var hash = SHA256.HashData([.. previous, .. Encoding.UTF8.GetBytes(canonical)]);

        var details = "'" + """{"origin":"spacing"}""" + "'::jsonb";
        await fixture.ExecuteAsync($"""
            INSERT INTO audit.audit_event
                (id, seq, occurred_at, actor_type, actor_id, application, action,
                 entity_type, entity_id, details, canonical, prev_hash, hash)
            VALUES
                ('{id:D}', {seq}, '{occurred}', 'system', 'spacing', NULL, 'template.created',
                 'template', 'spacing-{id:N}', {details},
                 $canonical${canonical}$canonical$,
                 decode('{Convert.ToHexStringLower(previous)}', 'hex'),
                 decode('{Convert.ToHexStringLower(hash)}', 'hex'))
            """);
    }

    private static async Task<int> RunDailyAsync(ServiceProvider provider, MonthlyPartitionWindow window)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<AuditExportPlanner>()
            .RunDailyAsync([window], CancellationToken.None);
    }

    private WormExportVerifier BucketOnlyVerifier()
    {
        IOptions<WormExportOptions> options = Options.Create(new WormExportOptions
        {
            Bucket = AuditMaintenanceFixture.Bucket,
            ServiceUrl = fixture.AwsEndpoint,
            Region = "us-east-1",
            AccessKey = "test",
            SecretKey = "test",
            ForcePathStyle = true,
        });
        return new WormExportVerifier(
            new S3WormObjectStore(fixture.S3, options, TimeProvider.System), options);
    }

    private async Task<byte[]> ReadObjectAsync(string key)
    {
        using GetObjectResponse response = await fixture.S3.GetObjectAsync(
            AuditMaintenanceFixture.Bucket, key);
        using var buffer = new MemoryStream();
        await response.ResponseStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task OverwriteObjectAsync(string key, byte[] content)
    {
        using var body = new MemoryStream(content, writable: false);
        await fixture.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = AuditMaintenanceFixture.Bucket,
            Key = key,
            InputStream = body,
        });
    }

    private Task<List<string>> StoredCanonicalAsync(DateTimeOffset month)
    {
        MonthlyPartitionWindow window = WindowOf(month);
        return fixture.QueryTextsAsync($"""
            SELECT canonical
            FROM audit.audit_event
            WHERE occurred_at >= '{window.FromInclusive:yyyy-MM-dd}' AND occurred_at < '{window.ToExclusive:yyyy-MM-dd}'
              AND canonical IS NOT NULL
            ORDER BY seq
            """);
    }

    private static string DailyFolder(string partition, DateOnly day)
        => AuditExportKeys.DailyFolder("audit-export/v1", "audit_event", partition, day);

    private static List<string> Lines(byte[] plain)
        => [.. Encoding.UTF8.GetString(plain).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static MonthlyPartitionWindow WindowOf(DateTimeOffset month)
        => MonthlyPartitions.Plan("audit_event", month, 0)[0];

    /// <summary>A month far enough in the past that every day of it is already stabilized.</summary>
    private static DateTimeOffset MonthOffset(int months)
    {
        DateTime utc = DateTime.UtcNow;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero)
            .AddMonths(months);
    }
}
