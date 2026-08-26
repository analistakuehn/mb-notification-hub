using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The versioned reference one parse memoization run is compared against.
/// </summary>
/// <remarks>
/// It records the two numbers that move when the eviction policy changes shape,
/// the cost of one hot lookup and how much lock contention that lookup buys,
/// and nothing about the machine beyond the label. The other two guards the run
/// applies need no reference at all: a catalogue that fits the budget must not
/// be parsed twice and must not leave memory, whatever the host is.
/// </remarks>
internal sealed record ParseMemoizationBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    int Workers,
    int Forms,
    int Sources,
    long OfferedChars,
    long Budget,
    double MicrosecondsPerOperation,
    double ContentionsPerThousand)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static ParseMemoizationBaseline From(ParseMemoizationArm arm, string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return new ParseMemoizationBaseline(
            1,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            arm.Workers,
            arm.Forms,
            arm.Sources,
            arm.OfferedChars,
            arm.Budget,
            Round(MicrosecondsPerOperationOf(arm)),
            Round(arm.ContentionsPerThousand));
    }

    internal static double MicrosecondsPerOperationOf(ParseMemoizationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return arm.OperationsPerSecond > 0 ? 1_000_000d / arm.OperationsPerSecond : double.NaN;
    }

    internal static async Task<ParseMemoizationBaseline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        ParseMemoizationBaseline baseline =
            await JsonSerializer.DeserializeAsync<ParseMemoizationBaseline>(stream, Options, cancellationToken)
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
