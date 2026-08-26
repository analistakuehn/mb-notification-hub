using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The versioned reference the memoization run is compared against.
/// </summary>
/// <remarks>
/// It records the two numbers that move when the eviction policy changes shape,
/// the cost of one operation on the miss path and how much lock contention that
/// operation buys, and nothing about the machine beyond the label. The third
/// guard the run applies needs no reference at all: the resident set has an
/// absolute ceiling the policy declares, and passing it is a leak whatever the
/// host is.
/// </remarks>
internal sealed record MemoizationBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    int Workers,
    int KeySpace,
    int Ceiling,
    double MicrosecondsPerOperation,
    double ContentionsPerThousand)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static MemoizationBaseline From(MemoizationArm arm, string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return new MemoizationBaseline(
            1,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            arm.Workers,
            arm.KeySpace,
            arm.Ceiling,
            Round(MicrosecondsPerOperationOf(arm)),
            Round(arm.ContentionsPerThousand));
    }

    internal static double MicrosecondsPerOperationOf(MemoizationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return arm.OperationsPerSecond > 0 ? 1_000_000d / arm.OperationsPerSecond : double.NaN;
    }

    internal static async Task<MemoizationBaseline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        MemoizationBaseline baseline =
            await JsonSerializer.DeserializeAsync<MemoizationBaseline>(stream, Options, cancellationToken)
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

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
