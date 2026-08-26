using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The versioned reference one render run is compared against.
/// </summary>
/// <remarks>
/// It records the one number that moves when the render path changes shape,
/// the bytes one form allocates, and nothing about the machine beyond the
/// label. Time is deliberately absent: it belongs to the host that measured it,
/// and a reference that carries it turns every slower runner into a red gate.
/// </remarks>
internal sealed record RenderCostBaseline(
    int FormatVersion,
    string RecordedAtUtc,
    string RecordedOn,
    int Forms,
    long BytesPerForm)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static RenderCostBaseline From(RenderCostArm arm, string recordedOn)
    {
        ArgumentNullException.ThrowIfNull(arm);
        return new RenderCostBaseline(
            1,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            recordedOn,
            arm.Forms,
            arm.BytesPerForm);
    }

    internal static async Task<RenderCostBaseline> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        RenderCostBaseline baseline =
            await JsonSerializer.DeserializeAsync<RenderCostBaseline>(stream, Options, cancellationToken)
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
}
