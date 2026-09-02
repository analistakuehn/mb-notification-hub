using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using NotificationHub.PerformanceTests.Reporting;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>Runs the same deterministic attachment transfer through three storage shapes.</summary>
internal static class AttachmentTransferMethodScenario
{
    internal const string BufferArm = "buffer";

    internal const string StreamingArm = "streaming";

    internal const string SpoolArm = "spool";

    private const int ChunkBytes = 64 * 1_024;

    private static readonly string[] RequiredArms = [BufferArm, StreamingArm, SpoolArm];

    internal static AttachmentTransferOutcome Run(
        int payloadUtf8Bytes,
        int envelopeBytes,
        int operations,
        int concurrency,
        IReadOnlyList<string> arms,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadUtf8Bytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(envelopeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(operations, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        EnsureCompleteArmSet(arms);

        var payload = DeterministicUtf8(payloadUtf8Bytes, "attachment-payload-0123456789abcdef|");
        var envelope = DeterministicUtf8(envelopeBytes, "envelope:v1;content-type=application/octet-stream;|");
        var expectedDigest = Digest(envelope, payload);
        var measured = new List<AttachmentTransferArm>(RequiredArms.Length);

        foreach (var armId in arms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report($"Braço {armId}: {operations:N0} operações, concorrência {concurrency:N0}.");
            AttachmentTransferArm arm = Measure(
                armId,
                payload,
                envelope,
                expectedDigest,
                operations,
                concurrency,
                cancellationToken);
            measured.Add(arm);
            report(string.Create(
                CultureInfo.InvariantCulture,
                $"  p95 {arm.LatencyP95Milliseconds:N3} ms, "
                + $"{arm.ThroughputBytesPerSecond / (1_024 * 1_024):N2} MiB/s, "
                + $"{arm.AllocatedBytes / arm.Operations:N0} bytes alocados/operação."));
        }

        return new AttachmentTransferOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            payload.Length,
            envelope.Length,
            operations,
            concurrency,
            expectedDigest,
            measured);
    }

    private static AttachmentTransferArm Measure(
        string armId,
        byte[] payload,
        byte[] envelope,
        string expectedDigest,
        int operations,
        int concurrency,
        CancellationToken cancellationToken)
    {
        var spoolRoot = armId == SpoolArm
            ? Path.Combine(
                Path.GetTempPath(),
                $"notification-hub-attachment-transfer-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}")
            : null;
        try
        {
            if (spoolRoot is not null)
            {
                Directory.CreateDirectory(spoolRoot);
            }

            return MeasureWithinSpoolRoot(
                armId,
                payload,
                envelope,
                expectedDigest,
                operations,
                concurrency,
                spoolRoot,
                cancellationToken);
        }
        finally
        {
            RemoveSpoolRoot(spoolRoot);
        }
    }

    private static AttachmentTransferArm MeasureWithinSpoolRoot(
        string armId,
        byte[] payload,
        byte[] envelope,
        string expectedDigest,
        int operations,
        int concurrency,
        string? spoolRoot,
        CancellationToken cancellationToken)
    {
        _ = Transfer(armId, payload, envelope, spoolRoot, -1);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: false);
        var workingSetBefore = process.WorkingSet64;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var generation0Before = GC.CollectionCount(0);
        var generation1Before = GC.CollectionCount(1);
        var generation2Before = GC.CollectionCount(2);
        TimeSpan cpuBefore = process.TotalProcessorTime;

        var latencies = new double[operations];
        var digests = new string[operations];
        var active = 0;
        var peakConcurrency = 0;
        var temporaryFilesCreated = 0;
        var started = Stopwatch.GetTimestamp();
        Parallel.For(
            0,
            operations,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = concurrency,
            },
            operation =>
            {
                var activeNow = Interlocked.Increment(ref active);
                SetMaximum(ref peakConcurrency, activeNow);
                var operationStarted = Stopwatch.GetTimestamp();
                try
                {
                    digests[operation] = Transfer(armId, payload, envelope, spoolRoot, operation);
                    if (spoolRoot is not null)
                    {
                        Interlocked.Increment(ref temporaryFilesCreated);
                    }
                }
                finally
                {
                    latencies[operation] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
                    Interlocked.Decrement(ref active);
                }
            });
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var heapAfter = GC.GetTotalMemory(forceFullCollection: false);
        var workingSetAfter = process.WorkingSet64;
        var remainingFiles = spoolRoot is null
            ? 0
            : Directory.EnumerateFiles(spoolRoot, "*", SearchOption.AllDirectories).Count();
        var rootRemoved = spoolRoot is null;
        if (spoolRoot is not null)
        {
            RemoveSpoolRoot(spoolRoot);
            rootRemoved = !Directory.Exists(spoolRoot);
        }

        string[] observedDigests = [.. digests.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var bytesPerOperation = checked(payload.Length + envelope.Length);
        var transferredBytes = checked((long)bytesPerOperation * operations);
        return new AttachmentTransferArm(
            armId,
            payload.Length,
            envelope.Length,
            bytesPerOperation,
            operations,
            concurrency,
            peakConcurrency,
            operations,
            expectedDigest,
            observedDigests,
            observedDigests.Length == 1 && string.Equals(observedDigests[0], expectedDigest, StringComparison.Ordinal),
            heapBefore,
            heapAfter,
            workingSetBefore,
            workingSetAfter,
            allocated,
            GC.CollectionCount(0) - generation0Before,
            GC.CollectionCount(1) - generation1Before,
            GC.CollectionCount(2) - generation2Before,
            cpu.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            Percentile(latencies, 0.50),
            Percentile(latencies, 0.95),
            Percentile(latencies, 0.99),
            transferredBytes / elapsed.TotalSeconds,
            spoolRoot is null ? null : transferredBytes,
            spoolRoot is null ? null : transferredBytes,
            temporaryFilesCreated,
            remainingFiles,
            rootRemoved);
    }

    private static void RemoveSpoolRoot(string? spoolRoot)
    {
        if (spoolRoot is null || !Directory.Exists(spoolRoot))
        {
            return;
        }

        Directory.Delete(spoolRoot, recursive: true);
        if (Directory.Exists(spoolRoot))
        {
            throw new IOException($"A raiz temporária {spoolRoot} permaneceu após a tentativa de limpeza.");
        }
    }

    private static string Transfer(
        string armId,
        byte[] payload,
        byte[] envelope,
        string? spoolRoot,
        int operation)
        => armId switch
        {
            BufferArm => TransferThroughBuffer(payload, envelope),
            StreamingArm => TransferThroughStreaming(payload, envelope),
            SpoolArm => TransferThroughSpool(
                payload,
                envelope,
                spoolRoot ?? throw new InvalidOperationException("O braço spool exige um diretório temporário."),
                operation),
            _ => throw new InvalidOperationException($"Braço de transferência desconhecido: {armId}"),
        };

    private static string TransferThroughBuffer(byte[] payload, byte[] envelope)
    {
        using var destination = new MemoryStream(checked(payload.Length + envelope.Length));
        destination.Write(envelope);
        destination.Write(payload);
        return Convert.ToHexString(SHA256.HashData(destination.GetBuffer().AsSpan(0, checked((int)destination.Length))));
    }

    private static string TransferThroughStreaming(byte[] payload, byte[] envelope)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope);
        foreach (ReadOnlyMemory<byte> chunk in Chunks(payload))
        {
            hash.AppendData(chunk.Span);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string TransferThroughSpool(
        byte[] payload,
        byte[] envelope,
        string spoolRoot,
        int operation)
    {
        var path = Path.Combine(spoolRoot, $"transfer-{operation:D6}-{Guid.NewGuid():N}.spool");
        try
        {
            using (var destination = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                ChunkBytes,
                FileOptions.SequentialScan))
            {
                destination.Write(envelope);
                foreach (ReadOnlyMemory<byte> chunk in Chunks(payload))
                {
                    destination.Write(chunk.Span);
                }

                destination.Flush(flushToDisk: true);
            }

            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ChunkBytes,
                FileOptions.SequentialScan);
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] source)
    {
        for (var offset = 0; offset < source.Length; offset += ChunkBytes)
        {
            yield return source.AsMemory(offset, Math.Min(ChunkBytes, source.Length - offset));
        }
    }

    private static byte[] DeterministicUtf8(int length, string seed)
    {
        var pattern = System.Text.Encoding.UTF8.GetBytes(seed);
        var value = new byte[length];
        for (var offset = 0; offset < value.Length; offset++)
        {
            value[offset] = pattern[offset % pattern.Length];
        }

        return value;
    }

    private static string Digest(byte[] envelope, byte[] payload)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope);
        hash.AppendData(payload);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static double Percentile(double[] samples, double percentile)
    {
        double[] ordered = [.. samples.Order()];
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static void SetMaximum(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private static void EnsureCompleteArmSet(IReadOnlyList<string> arms)
    {
        if (arms.Count != RequiredArms.Length
            || arms.Distinct(StringComparer.Ordinal).Count() != RequiredArms.Length
            || RequiredArms.Except(arms, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException(
                "A rodada exige exatamente os braços buffer, streaming e spool, sem duplicatas.");
        }
    }
}
