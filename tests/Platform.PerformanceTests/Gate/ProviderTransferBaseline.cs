using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Ratios one arm held against the buffering arm of the same run.</summary>
internal sealed record ProviderTransferArmBaseline(
    string ArmId,
    double AllocationRatio,
    double ThroughputRatio,
    double MaxLatencyRatio,
    double PeakHeapRatio,
    double PeakWorkingSetRatio,
    double AllocatedBytesPerOperation,
    double LatencyMaxMilliseconds,
    double ThroughputBytesPerSecond);

/// <summary>One cell of the matrix: a corpus at a concurrency, with its arms.</summary>
internal sealed record ProviderTransferCellBaseline(
    string ProfileId,
    long AttachmentBytes,
    int AttachmentCount,
    string ContentShape,
    int SourceChunkBytes,
    int OperationsPerArm,
    int ConfiguredConcurrency,
    bool ContentLengthDeclared,
    string SourceContentSha256,
    long BodyBytes,
    int RunsMerged,
    IReadOnlyList<ProviderTransferArmBaseline> Arms);

/// <summary>
/// The versioned reference of the provider-transfer comparison.
/// <para>
/// It holds ratios and configuration, never a ceiling: the ceilings are
/// constants of the gate, because the option that rewrites this file is part of
/// the same command that reads it, and a ceiling kept here would be rewritten
/// by the run it judges.
/// </para>
/// <para>
/// Every cell is the median of at least three runs, each one a process of its
/// own. A reference taken from a single run grades the luck of that run, and a
/// reference taken by the same process that then compares against it certifies
/// itself.
/// </para>
/// </summary>
internal sealed record ProviderTransferBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    bool ServerGarbageCollection,
    int GarbageCollectorHeapCount,
    IReadOnlyList<string> SourceReports,
    IReadOnlyList<ProviderTransferCellBaseline> Cells)
{
    /// <summary>Runs a cell needs before its median means anything.</summary>
    internal const int MinimumRunsPerCell = 3;

    private const int ExpectedFormat = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Merges the reports of several isolated runs into one reference. Runs are
    /// grouped by the cell they measured, and a cell that did not run enough
    /// times is refused rather than recorded thin.
    /// </summary>
    internal static ProviderTransferBaseline From(
        IReadOnlyList<ProviderTransferOutcome> runs,
        IReadOnlyList<string> sourceReports,
        string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(sourceReports);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordedOn);
        if (runs.Count == 0)
        {
            throw new InvalidOperationException("A linha de base exige ao menos um relatório de rodada.");
        }

        foreach (ProviderTransferOutcome run in runs)
        {
            EnsureRecordable(run);
        }

        ProviderTransferOutcome collector = runs[0];
        if (runs.Any(run => run.ServerGarbageCollection != collector.ServerGarbageCollection
            || run.GarbageCollectorHeapCount != collector.GarbageCollectorHeapCount))
        {
            throw new InvalidOperationException(
                "As rodadas não rodaram sob o mesmo coletor; uma referência não pode misturar configurações.");
        }

        var cells = runs
            .GroupBy(run => (run.ProfileId, run.ConfiguredConcurrency))
            .OrderBy(group => group.Key.ProfileId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ConfiguredConcurrency)
            .Select(group => Cell([.. group]))
            .ToList();
        return new ProviderTransferBaseline(
            ExpectedFormat,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            collector.ServerGarbageCollection,
            collector.GarbageCollectorHeapCount,
            sourceReports,
            cells);
    }

    internal static async Task<ProviderTransferBaseline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        ProviderTransferBaseline baseline =
            await JsonSerializer.DeserializeAsync<ProviderTransferBaseline>(stream, Options, cancellationToken)
            ?? throw new InvalidOperationException($"A linha de base em {path} está vazia.");
        return baseline.FormatVersion == ExpectedFormat
            ? baseline
            : throw new InvalidOperationException(
                $"A linha de base em {path} está no formato {baseline.FormatVersion}; "
                + $"o portão exige o formato {ExpectedFormat}. Regrave a referência.");
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

    internal ProviderTransferCellBaseline? CellFor(string profileId, int concurrency)
        => Cells.FirstOrDefault(cell =>
            string.Equals(cell.ProfileId, profileId, StringComparison.Ordinal)
            && cell.ConfiguredConcurrency == concurrency);

    private static ProviderTransferCellBaseline Cell(IReadOnlyList<ProviderTransferOutcome> runs)
    {
        ProviderTransferOutcome first = runs[0];
        if (runs.Count < MinimumRunsPerCell)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"O perfil {first.ProfileId} na concorrência {first.ConfiguredConcurrency} trouxe "
                + $"{runs.Count} rodada(s) e a referência exige ao menos {MinimumRunsPerCell}."));
        }

        if (runs.Any(run => run.AttachmentBytes != first.AttachmentBytes
            || run.AttachmentCount != first.AttachmentCount
            || !string.Equals(run.ContentShape, first.ContentShape, StringComparison.Ordinal)
            || run.SourceChunkBytes != first.SourceChunkBytes
            || run.OperationsPerArm != first.OperationsPerArm
            || run.ContentLengthDeclared != first.ContentLengthDeclared
            || !string.Equals(run.SourceContentSha256, first.SourceContentSha256, StringComparison.Ordinal)
            || run.BodyBytes != first.BodyBytes))
        {
            throw new InvalidOperationException(
                $"As rodadas do perfil {first.ProfileId} não mediram o mesmo corpus e não podem virar uma mediana.");
        }

        var arms = ProviderTransferArms.All
            .Select(armId => new ProviderTransferArmBaseline(
                armId,
                Median(runs, armId, static (arm, reference) =>
                    Ratio(arm.AllocatedBytesPerOperation, reference.AllocatedBytesPerOperation)),
                Median(runs, armId, static (arm, reference) =>
                    Ratio(arm.ThroughputBytesPerSecond, reference.ThroughputBytesPerSecond)),
                Median(runs, armId, static (arm, reference) =>
                    Ratio(arm.LatencyMaxMilliseconds, reference.LatencyMaxMilliseconds)),
                Median(runs, armId, static (arm, reference) => Ratio(arm.PeakHeapBytes, reference.PeakHeapBytes)),
                Median(runs, armId, static (arm, reference) =>
                    Ratio(arm.PeakWorkingSetBytes, reference.PeakWorkingSetBytes)),
                Median(runs, armId, static (arm, _) => arm.AllocatedBytesPerOperation),
                Median(runs, armId, static (arm, _) => arm.LatencyMaxMilliseconds),
                Median(runs, armId, static (arm, _) => arm.ThroughputBytesPerSecond)))
            .ToList();

        return new ProviderTransferCellBaseline(
            first.ProfileId,
            first.AttachmentBytes,
            first.AttachmentCount,
            first.ContentShape,
            first.SourceChunkBytes,
            first.OperationsPerArm,
            first.ConfiguredConcurrency,
            first.ContentLengthDeclared,
            first.SourceContentSha256,
            first.BodyBytes,
            runs.Count,
            arms);
    }

    private static double Median(
        IReadOnlyList<ProviderTransferOutcome> runs,
        string armId,
        Func<ProviderTransferArm, ProviderTransferArm, double> select)
    {
        double[] values =
        [
            .. runs
                .Select(run => select(ArmOf(run, armId), ArmOf(run, ProviderTransferArms.BufferArm)))
                .Order(),
        ];
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    private static ProviderTransferArm ArmOf(ProviderTransferOutcome outcome, string armId)
        => outcome.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"A rodada não mediu o braço {armId}.");

    private static double Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : double.NaN;

    private static void EnsureRecordable(ProviderTransferOutcome outcome)
    {
        GateCheck? failed = ProviderTransferInvariants.RecordableChecks(outcome)
            .FirstOrDefault(check => !check.Passes);
        if (failed is not null)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Uma rodada que viola as invariantes não pode virar referência: {failed.Metric}, "
                + $"medido {failed.Measured:0.###}, limite {failed.Limit:0.###}."));
        }
    }
}
