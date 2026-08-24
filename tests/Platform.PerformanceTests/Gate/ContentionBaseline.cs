using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The versioned reference the per-pull-request run is compared against.
/// </summary>
/// <remarks>
/// Version 2 dropped the absolute hold windows of version 1. On a busy host the
/// median hold of one arm moved thirty per cent between runs of the same code,
/// which is the tolerance itself, so the metric was measuring the machine.
/// What replaced it are two signals internal to a single run, both immune to
/// how fast the host is on the day: the hold window expressed in trivial round
/// trips, and how much the hold grows between two partition volumes. A loose
/// absolute ceiling stays behind them, at an order of magnitude, only to catch
/// a normalization that misbehaves.
/// </remarks>
internal sealed record ContentionBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    IReadOnlyList<int> Volumes,
    int Appenders,
    double RoundTripP50Ms,
    double MitigatedHoldP50Ms,
    double NormalizedHold,
    double VolumeDrift)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static ContentionBaseline From(
        GateMeasurement measurement,
        int appenders,
        string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return new ContentionBaseline(
            2,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            measurement.Volumes,
            appenders,
            Round(measurement.RoundTripP50Ms),
            Round(measurement.HoldP50Ms),
            Round(measurement.NormalizedHold),
            Round(measurement.VolumeDrift));
    }

    internal static async Task<ContentionBaseline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        ContentionBaseline baseline =
            await JsonSerializer.DeserializeAsync<ContentionBaseline>(stream, Options, cancellationToken)
            ?? throw new InvalidOperationException($"A linha de base em {path} está vazia.");
        return baseline.FormatVersion == 2
            ? baseline
            : throw new InvalidOperationException(
                $"A linha de base em {path} está no formato {baseline.FormatVersion}; "
                + "o portão exige o formato 2. Regrave com --update-baseline.");
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

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

/// <summary>What one guard run measured, before it is compared to anything.</summary>
internal sealed record GateMeasurement(
    IReadOnlyList<int> Volumes,
    double RoundTripP50Ms,
    double HoldP50Ms,
    double HoldP50AtLargerVolumeMs,
    int Samples)
{
    /// <summary>The hold window expressed in trivial round trips to the same database.</summary>
    internal double NormalizedHold => RoundTripP50Ms > 0 ? HoldP50Ms / RoundTripP50Ms : double.NaN;

    /// <summary>How much the hold window grows when the partition grows.</summary>
    internal double VolumeDrift => HoldP50Ms > 0 ? HoldP50AtLargerVolumeMs / HoldP50Ms : double.NaN;

    internal static GateMeasurement From(
        IReadOnlyList<ArmResult> arms,
        string armId,
        PhaseStatistics roundTrip)
    {
        ArgumentNullException.ThrowIfNull(arms);
        ArgumentNullException.ThrowIfNull(roundTrip);
        List<ArmResult> cells =
        [
            .. arms.Where(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
                .OrderBy(arm => arm.Volume),
        ];
        if (cells.Count < 2)
        {
            throw new InvalidOperationException(
                $"O portão exige o braço {armId} em dois volumes na mesma rodada; encontrou {cells.Count}.");
        }

        return new GateMeasurement(
            [.. cells.Select(cell => cell.Volume)],
            roundTrip.P50,
            cells[0].Hold.P50,
            cells[^1].Hold.P50,
            cells.Sum(cell => cell.Transactions));
    }
}

/// <summary>One guard metric compared against its limit.</summary>
internal sealed record GateCheck(string Metric, double Reference, double Measured, double Limit, bool Passes)
{
    internal double Drift => Reference > 0 ? (Measured - Reference) / Reference : double.NaN;
}

/// <summary>The gate's answer.</summary>
internal sealed record GateOutcome(bool Passes, double Tolerance, IReadOnlyList<GateCheck> Checks);

/// <summary>Compares one guard run against the versioned baseline.</summary>
internal static class SmokeGate
{
    /// <summary>
    /// The loose ceiling behind the two normalized signals. An order of
    /// magnitude is deliberately far away: it exists to catch a normalizer that
    /// went wrong, not to grade the host.
    /// </summary>
    private const double AbsoluteGuardFactor = 10;

    internal static GateOutcome Evaluate(
        ContentionBaseline baseline,
        GateMeasurement measurement,
        double tolerance,
        double volumeDrift)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(measurement);
        GateCheck[] checks =
        [
            Relative(
                "posse em idas ao banco (normalizada)",
                baseline.NormalizedHold,
                measurement.NormalizedHold,
                tolerance),
            Absolute(
                "crescimento da posse com o volume",
                1,
                measurement.VolumeDrift,
                1 + volumeDrift),
            Absolute(
                "guarda absoluta frouxa da posse",
                baseline.MitigatedHoldP50Ms,
                measurement.HoldP50Ms,
                baseline.MitigatedHoldP50Ms * AbsoluteGuardFactor),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), tolerance, checks);
    }

    private static GateCheck Relative(string metric, double reference, double measured, double tolerance)
    {
        var limit = reference * (1 + tolerance);
        return new GateCheck(metric, reference, measured, limit, measured <= limit);
    }

    private static GateCheck Absolute(string metric, double reference, double measured, double limit)
        => new(metric, reference, measured, limit, measured <= limit);
}
