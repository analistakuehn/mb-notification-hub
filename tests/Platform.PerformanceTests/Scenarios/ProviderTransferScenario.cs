using System.Diagnostics;
using System.Globalization;
using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.Instrumentation;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>Corpus and offered load of one provider-transfer comparison.</summary>
internal sealed record ProviderTransferProfile(
    string ProfileId,
    long AttachmentBytes,
    int AttachmentCount,
    AttachmentContentShape ContentShape,
    int SourceChunkBytes,
    TimeSpan SourceLatency,
    int Operations,
    int Concurrency,
    bool DeclareContentLength)
{
    internal long TotalRawAttachmentBytes => checked(AttachmentBytes * AttachmentCount);
}

/// <summary>
/// Runs the three transfer methods against a provider double, doing the work a
/// real send does: read the attachment, encode it, compose the Mail Send body
/// and push it over the connection.
/// <para>
/// Every arm is measured twice on purpose. The first pass runs against a double
/// that decodes what it received, and answers whether the arms are equivalent;
/// the measured passes run against a double that only digests the body, because
/// the double lives in this process and its decoding would otherwise be charged
/// to the arm.
/// </para>
/// <para>
/// The arms are measured one after another and never at the same time. The
/// pause the collector imposes is read from the whole process, so two arms
/// running together would each be charged the other's pause, and the reading
/// would fail quietly instead of loudly.
/// </para>
/// </summary>
internal static class ProviderTransferScenario
{
    internal static async Task<ProviderTransferOutcome> RunAsync(
        ProviderTransferProfile profile,
        IReadOnlyList<string> arms,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<IAttachmentByteSource> sources = BuildSources(profile);
        MailSendEnvelope envelope = MailSendEnvelope.Default;
        MailSendBodyLayout layout = MailSendComposer.Layout(envelope, sources, "probe-size-check");
        EnsureWithinProviderCeiling(layout.TotalBytes);

        await using ProviderCaptureServer server = await ProviderCaptureServer.StartAsync(cancellationToken);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };
        report(string.Create(
            CultureInfo.InvariantCulture,
            $"Duplo do provedor em {server.BaseAddress}; perfil {profile.ProfileId}, "
            + $"corpo de {layout.TotalBytes:N0} bytes, teto de {MailSendLimits.MaxMessageBytes:N0}."));

        var measured = new List<ProviderTransferArm>(arms.Count);
        foreach (var armId in arms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            report(string.Create(
                CultureInfo.InvariantCulture,
                $"Braço {armId}: {profile.Operations:N0} operações, concorrência {profile.Concurrency:N0}."));
            ProviderTransferArm arm = await MeasureAsync(
                new ArmRun(armId, profile, envelope, sources, server, client), cancellationToken);
            measured.Add(arm);
            report(string.Create(
                CultureInfo.InvariantCulture,
                $"  p95 {arm.LatencyP95Milliseconds:N3} ms, máximo {arm.LatencyMaxMilliseconds:N3} ms, "
                + $"{arm.ThroughputBytesPerSecond / (1_024 * 1_024):N2} MiB/s, "
                + $"{arm.AllocatedBytesPerOperation:N0} bytes alocados/operação, "
                + $"{arm.AcceptedCalls:N0} de {arm.ProviderCalls:N0} aceitas."));
        }

        var agree = measured
            .Select(arm => arm.CapturedBodySha256)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1
            && measured.TrueForAll(arm => arm.DistinctCapturedDigests == 1);

        return new ProviderTransferOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            CollectorPin.ServerGarbageCollection,
            CollectorPin.HeapCount,
            CollectorPin.LatencyMode,
            profile.ProfileId,
            profile.ContentShape.ToString(),
            profile.AttachmentBytes,
            profile.AttachmentCount,
            profile.TotalRawAttachmentBytes,
            MailSendLimits.Base64Length(profile.AttachmentBytes) * profile.AttachmentCount,
            layout.EnvelopeBytes,
            layout.TotalBytes,
            MailSendLimits.MaxMessageBytes,
            profile.SourceChunkBytes,
            profile.SourceLatency.TotalMilliseconds,
            profile.Operations,
            profile.Concurrency,
            profile.DeclareContentLength,
            sources[0].ContentSha256,
            agree,
            measured);
    }

    internal static IReadOnlyList<IAttachmentByteSource> BuildSources(ProviderTransferProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var sources = new IAttachmentByteSource[profile.AttachmentCount];
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index] = new SyntheticAttachmentByteSource(
                profile.AttachmentBytes,
                string.Create(CultureInfo.InvariantCulture, $"comprovante-{index}.pdf"),
                "application/pdf",
                profile.SourceChunkBytes,
                profile.SourceLatency,
                profile.ContentShape);
        }

        return sources;
    }

    /// <summary>
    /// A message the provider would refuse never becomes a measurement. The
    /// ceiling is the documented total for one call, and base64 is what turns a
    /// comfortable attachment into a body that crosses it.
    /// </summary>
    internal static void EnsureWithinProviderCeiling(long bodyBytes)
    {
        if (bodyBytes >= MailSendLimits.MaxMessageBytes)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"O corpo composto ocuparia {bodyBytes:N0} bytes e o provedor aceita no máximo "
                + $"{MailSendLimits.MaxMessageBytes:N0} por mensagem. Reduza o anexo ou a quantidade."));
        }
    }

    private static async Task<ProviderTransferArm> MeasureAsync(ArmRun run, CancellationToken cancellationToken)
    {
        var spoolRoot = run.ArmId == ProviderTransferArms.SpoolArm
            ? Path.Combine(
                Path.GetTempPath(),
                $"notification-hub-provider-transfer-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)}")
            : null;
        if (spoolRoot is not null)
        {
            Directory.CreateDirectory(spoolRoot);
        }

        try
        {
            return await MeasureWithinSpoolRootAsync(run, spoolRoot, cancellationToken);
        }
        finally
        {
            RemoveSpoolRoot(spoolRoot);
        }
    }

    private static async Task<ProviderTransferArm> MeasureWithinSpoolRootAsync(
        ArmRun run,
        string? spoolRoot,
        CancellationToken cancellationToken)
    {
        // The equivalence pass: one send against a double that decodes what it
        // was given, so the answer about name, type, order, length and digest
        // comes from the wire and not from the sender.
        run.Server.Depth = CaptureDepth.Decoded;
        var firstOrdinal = run.Server.CallCount + 1;
        await SendOnceAsync(run, spoolRoot, cancellationToken);
        CapturedMailSend verification = run.Server.Calls.Single(call => call.Ordinal == firstOrdinal);
        IReadOnlyList<ProviderTransferAttachmentCheck> checks = Compare(run.Sources, verification);

        // A discarded pass, then the measured ones, both against a double that
        // only digests the body.
        run.Server.Depth = CaptureDepth.BodyDigest;
        await SendOnceAsync(run, spoolRoot, cancellationToken);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        var measuredFrom = run.Server.CallCount;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var generation0Before = GC.CollectionCount(0);
        var generation1Before = GC.CollectionCount(1);
        var generation2Before = GC.CollectionCount(2);
        TimeSpan pauseBefore = CollectorPin.TotalPause;
        TimeSpan cpuBefore = process.TotalProcessorTime;

        var latency = new LatencyHistogram();
        var samples = new double[run.Profile.Operations];
        var accepted = 0;
        var temporaryFiles = 0;
        var active = 0;
        var peakConcurrency = 0;
        using var residency = ResidencySampler.Start();
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, run.Profile.Operations),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = run.Profile.Concurrency,
            },
            async (operation, token) =>
            {
                SetMaximum(ref peakConcurrency, Interlocked.Increment(ref active));
                var operationStarted = Stopwatch.GetTimestamp();
                try
                {
                    TransferAttempt attempt = await SendOnceAsync(run, spoolRoot, token);
                    if (string.Equals(attempt.Classification, "accepted", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref accepted);
                    }

                    Interlocked.Add(ref temporaryFiles, attempt.TemporaryFilesCreated);
                }
                finally
                {
                    samples[operation] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
                    Interlocked.Decrement(ref active);
                }
            });
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        ResidencyPeak peak = await residency.StopAsync();
        foreach (var sample in samples)
        {
            latency.Add(sample);
        }

        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        TimeSpan pause = CollectorPin.TotalPause - pauseBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        IReadOnlyList<CapturedMailSend> measuredCalls =
            [.. run.Server.Calls.Where(call => call.Ordinal > measuredFrom)];
        var remainingFiles = spoolRoot is null
            ? 0
            : Directory.EnumerateFiles(spoolRoot, "*", SearchOption.AllDirectories).Count();
        var rootRemoved = spoolRoot is null;
        if (spoolRoot is not null)
        {
            RemoveSpoolRoot(spoolRoot);
            rootRemoved = !Directory.Exists(spoolRoot);
            Directory.CreateDirectory(spoolRoot);
        }

        var transferred = checked(verification.BodyBytes * run.Profile.Operations);
        return new ProviderTransferArm(
            run.ArmId,
            run.Profile.Operations,
            run.Profile.Concurrency,
            peakConcurrency,
            measuredCalls.Count,
            accepted,
            verification.BodyBytes,
            verification.DeclaredContentLength ?? -1,
            verification.BodySha256,
            measuredCalls
                .Select(call => call.BodySha256)
                .Append(verification.BodySha256)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            run.Profile.DeclareContentLength,
            verification.Chunked,
            checks,
            peak.HeapBytes,
            peak.WorkingSetBytes,
            peak.Samples,
            allocated,
            GC.CollectionCount(0) - generation0Before,
            GC.CollectionCount(1) - generation1Before,
            GC.CollectionCount(2) - generation2Before,
            pause.TotalMilliseconds,
            cpu.TotalMilliseconds,
            elapsed.TotalMilliseconds,
            latency.Count,
            latency.Percentile(50),
            latency.Percentile(95),

            // The ninety-ninth percentile of fewer than a thousand samples is
            // the largest sample under another name, so the run reports the
            // maximum and calls it the maximum instead of dressing it up.
            latency.Count >= ProviderTransferBudget.PercentileNinetyNineFloor ? latency.Percentile(99) : null,
            latency.Max(),
            transferred / elapsed.TotalSeconds,
            temporaryFiles,
            remainingFiles,
            rootRemoved,
            run.Sources.Sum(source => source.OpenStreams));
    }

    private static async Task<TransferAttempt> SendOnceAsync(
        ArmRun run,
        string? spoolRoot,
        CancellationToken cancellationToken)
    {
        using TransferInterrupter interrupter = TransferInterrupter.Idle(cancellationToken);
        var plan = new TransferPlan(
            run.ArmId,
            run.Envelope,
            run.Sources,
            "probe-api-key",
            spoolRoot,
            run.Profile.DeclareContentLength,
            interrupter);
        return await ProviderTransferArms.SendAsync(run.Client, plan, interrupter.Token);
    }

    private static List<ProviderTransferAttachmentCheck> Compare(
        IReadOnlyList<IAttachmentByteSource> sources,
        CapturedMailSend captured)
    {
        var checks = new List<ProviderTransferAttachmentCheck>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            IAttachmentByteSource source = sources[index];
            CapturedAttachment? received = captured.Attachments.Count > index
                ? captured.Attachments[index]
                : null;
            checks.Add(new ProviderTransferAttachmentCheck(
                index,
                source.FileName,
                source.ContentType,
                source.Length,
                received?.Base64Bytes ?? 0,
                received?.DecodedBytes ?? 0,
                received is not null
                    && received.DecodedSuccessfully
                    && string.Equals(received.DecodedSha256, source.ContentSha256, StringComparison.Ordinal),
                received is not null
                    && received.Order == index
                    && string.Equals(received.FileName, source.FileName, StringComparison.Ordinal)
                    && string.Equals(received.Type, source.ContentType, StringComparison.Ordinal)
                    && string.Equals(
                        received.Disposition, MailSendLimits.AttachmentDisposition, StringComparison.Ordinal)));
        }

        return checks;
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

    private sealed record ArmRun(
        string ArmId,
        ProviderTransferProfile Profile,
        MailSendEnvelope Envelope,
        IReadOnlyList<IAttachmentByteSource> Sources,
        ProviderCaptureServer Server,
        HttpClient Client);
}
