using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Versioned reference produced by one complete transfer comparison.</summary>
internal sealed record AttachmentTransferArmBaseline(
    string ArmId,
    long AllocatedBytesPerOperation,
    double CpuMillisecondsPerOperation,
    double LatencyP95Milliseconds,
    double ThroughputBytesPerSecond,
    long HeapDeltaBytes,
    long WorkingSetDeltaBytes,
    double Generation0CollectionsPerOperation,
    double Generation1CollectionsPerOperation,
    double Generation2CollectionsPerOperation,
    long? LogicalFileReadBytes,
    long? LogicalFileWrittenBytes);

/// <summary>Corpus, profile and measurements recorded by the attachment runner.</summary>
internal sealed record AttachmentTransferMethodBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    int PayloadUtf8Bytes,
    int EnvelopeBytes,
    int OperationsPerArm,
    int ConfiguredConcurrency,
    string ExpectedDigest,
    IReadOnlyList<AttachmentTransferArmBaseline> Arms)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static AttachmentTransferMethodBaseline From(
        AttachmentTransferOutcome outcome,
        string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordedOn);
        EnsureCompleteAndClean(outcome);
        return new AttachmentTransferMethodBaseline(
            1,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            outcome.PayloadUtf8Bytes,
            outcome.EnvelopeBytes,
            outcome.OperationsPerArm,
            outcome.ConfiguredConcurrency,
            outcome.ExpectedDigest,
            [.. outcome.Arms.Select(ToBaseline)]);
    }

    internal static async Task<AttachmentTransferMethodBaseline> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        AttachmentTransferMethodBaseline baseline =
            await JsonSerializer.DeserializeAsync<AttachmentTransferMethodBaseline>(
                stream,
                Options,
                cancellationToken)
            ?? throw new InvalidOperationException($"A linha de base em {path} está vazia.");
        return baseline.FormatVersion == 1
            ? baseline
            : throw new InvalidOperationException(
                $"A linha de base em {path} está no formato {baseline.FormatVersion}; "
                + "o portão exige o formato 1. Regrave com --update-baseline.");
    }

    internal async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, Options, cancellationToken);
    }

    private static AttachmentTransferArmBaseline ToBaseline(AttachmentTransferArm arm)
        => new(
            arm.ArmId,
            arm.AllocatedBytes / arm.Operations,
            arm.CpuMilliseconds / arm.Operations,
            arm.LatencyP95Milliseconds,
            arm.ThroughputBytesPerSecond,
            arm.HeapBytesAfter - arm.HeapBytesBefore,
            arm.WorkingSetBytesAfter - arm.WorkingSetBytesBefore,
            (double)arm.Generation0Collections / arm.Operations,
            (double)arm.Generation1Collections / arm.Operations,
            (double)arm.Generation2Collections / arm.Operations,
            arm.LogicalFileReadBytes,
            arm.LogicalFileWrittenBytes);

    private static void EnsureCompleteAndClean(AttachmentTransferOutcome outcome)
    {
        string[] requiredArms =
        [
            AttachmentTransferMethodScenario.BufferArm,
            AttachmentTransferMethodScenario.StreamingArm,
            AttachmentTransferMethodScenario.SpoolArm,
        ];
        if (outcome.Arms.Count != requiredArms.Length
            || requiredArms.Except(outcome.Arms.Select(arm => arm.ArmId), StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("A linha de base exige os três braços da mesma rodada.");
        }

        if (outcome.Arms.Any(arm => !arm.DigestsEqual
            || arm.TemporaryFilesRemaining != 0
            || !arm.TemporaryRootRemoved))
        {
            throw new InvalidOperationException(
                "A linha de base não pode registrar digest divergente ou arquivo temporário residual.");
        }
    }
}
