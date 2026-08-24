using System.Globalization;

namespace NotificationHub.PerformanceTests;

/// <summary>What the probe run is for.</summary>
internal enum ProbeMode
{
    /// <summary>The whole factorial design, the interference arm and the read scenarios.</summary>
    Full,

    /// <summary>The short guard run compared against the versioned baseline.</summary>
    Smoke,
}

/// <summary>
/// Everything the probe reads from the command line. Defaults describe the
/// full local run; every knob exists so the same binary can point at a managed
/// database without a rebuild.
/// </summary>
internal sealed record ProbeSettings
{
    internal ProbeMode Mode { get; private init; } = ProbeMode.Full;

    /// <summary>When absent, the probe starts a throwaway PostgreSQL container.</summary>
    internal string? ConnectionString { get; private init; }

    /// <summary>Writing the trail is irreversible, so an external target has to say so out loud.</summary>
    internal bool AllowTrailWrites { get; private init; }

    internal int Appenders { get; private init; } = 4;

    internal IReadOnlyList<int> Volumes { get; private init; } = [10_000, 500_000, 2_000_000];

    internal IReadOnlyList<string> Arms { get; private init; } = ["A1", "A2", "A3", "A4", "A5"];

    internal TimeSpan ArmDuration { get; private init; } = TimeSpan.FromSeconds(20);

    internal int MaxAppendsPerArm { get; private init; } = 4_000;

    /// <summary>Offered rate of the open-loop cell, in appends per second.</summary>
    internal int SustainedRate { get; private init; } = 900;

    internal TimeSpan SustainedDuration { get; private init; } = TimeSpan.FromSeconds(10);

    internal int RelayBacklog { get; private init; } = 1_000_000;

    internal int PurgeBacklog { get; private init; } = 1_000_000;

    internal double Tolerance { get; private init; } = 0.30;

    /// <summary>
    /// How much the hold window may grow between the two guard volumes. Small
    /// on purpose: with the tail index the hold stops depending on the size of
    /// the partition, so any real growth is the regression the gate exists for.
    /// </summary>
    internal double VolumeDrift { get; private init; } = 0.25;

    /// <summary>
    /// Which arm the guard reads. It is the mitigated shape by default;
    /// pointing it at an unmitigated arm is how the volume-dependence check
    /// itself gets falsified, by showing it fails when the index is absent.
    /// </summary>
    internal string GateArm { get; private init; } = "A5";

    /// <summary>
    /// How many times the guard arm runs before a value is taken. Recording a
    /// baseline from a single run is what made the previous format unusable: a
    /// reference taken on an unlucky run leaves the next honest run with no
    /// margin at all.
    /// </summary>
    internal int GuardRepeats { get; private init; } = 1;

    internal string BaselinePath { get; private init; } = DefaultBaselinePath();

    internal bool UpdateBaseline { get; private init; }

    internal string? ReportPath { get; private init; }

    internal static ProbeSettings Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var settings = new ProbeSettings();
        var explicitDuration = false;
        var explicitVolumes = false;
        var explicitAppends = false;
        var explicitArms = false;
        var explicitRepeats = false;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            switch (name)
            {
                case "--mode":
                    settings = settings with { Mode = ParseMode(Value(args, ref index)) };
                    break;
                case "--connection-string":
                    settings = settings with { ConnectionString = Value(args, ref index) };
                    break;
                case "--allow-trail-writes":
                    settings = settings with { AllowTrailWrites = true };
                    break;
                case "--appenders":
                    settings = settings with { Appenders = Number(args, ref index) };
                    break;
                case "--volumes":
                    settings = settings with { Volumes = ParseVolumes(Value(args, ref index)) };
                    explicitVolumes = true;
                    break;
                case "--arms":
                    settings = settings with
                    {
                        Arms =
                        [
                            .. Value(args, ref index)
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        ],
                    };
                    explicitArms = true;
                    break;
                case "--arm-seconds":
                    settings = settings with { ArmDuration = TimeSpan.FromSeconds(Number(args, ref index)) };
                    explicitDuration = true;
                    break;
                case "--max-appends":
                    settings = settings with { MaxAppendsPerArm = Number(args, ref index) };
                    explicitAppends = true;
                    break;
                case "--sustained-rate":
                    settings = settings with { SustainedRate = Number(args, ref index) };
                    break;
                case "--sustained-seconds":
                    settings = settings with { SustainedDuration = TimeSpan.FromSeconds(Number(args, ref index)) };
                    break;
                case "--relay-backlog":
                    settings = settings with { RelayBacklog = Number(args, ref index) };
                    break;
                case "--purge-backlog":
                    settings = settings with { PurgeBacklog = Number(args, ref index) };
                    break;
                case "--tolerance":
                    settings = settings with { Tolerance = Fraction(args, ref index) };
                    break;
                case "--volume-drift":
                    settings = settings with { VolumeDrift = Fraction(args, ref index) };
                    break;
                case "--gate-arm":
                    settings = settings with { GateArm = Value(args, ref index) };
                    break;
                case "--baseline":
                    settings = settings with { BaselinePath = Value(args, ref index) };
                    break;
                case "--update-baseline":
                    settings = settings with { UpdateBaseline = true };
                    break;
                case "--guard-repeats":
                    settings = settings with { GuardRepeats = Number(args, ref index) };
                    explicitRepeats = true;
                    break;
                case "--report":
                    settings = settings with { ReportPath = Value(args, ref index) };
                    break;
                default:
                    throw new ArgumentException($"Opção desconhecida: {name}", nameof(args));
            }
        }

        if (settings.Mode is ProbeMode.Smoke)
        {
            // Two volumes and the mitigated arm only. The guard reads how many
            // round trips the append holds the lock for, and whether that grows
            // with the partition; both need the shape the project is heading
            // for, and both are ratios taken inside one run.
            settings = settings with
            {
                Volumes = explicitVolumes ? settings.Volumes : [10_000, 200_000],
                Arms = explicitArms ? settings.Arms : ["A5"],
                ArmDuration = explicitDuration ? settings.ArmDuration : TimeSpan.FromSeconds(5),
                MaxAppendsPerArm = explicitAppends ? settings.MaxAppendsPerArm : 1_500,
                GuardRepeats = explicitRepeats
                    ? settings.GuardRepeats
                    : settings.UpdateBaseline ? 3 : 1,
            };
        }

        return settings;
    }

    /// <summary>
    /// The versioned file, found by walking up from the binary to the project
    /// that owns it. Reading a copy in the output directory would compare this
    /// run against whatever the last build happened to copy there, which is the
    /// one thing a gate must never do.
    /// </summary>
    private static string DefaultBaselinePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Platform.PerformanceTests.csproj")))
            {
                return Path.Combine(directory.FullName, "baselines", "audit-chain-contention.json");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "baselines", "audit-chain-contention.json");
    }

    private static ProbeMode ParseMode(string value) => value switch
    {
        "full" => ProbeMode.Full,
        "smoke" => ProbeMode.Smoke,
        _ => throw new ArgumentException($"Modo desconhecido: {value}", nameof(value)),
    };

    private static IReadOnlyList<int> ParseVolumes(string value)
        =>
        [
            .. value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => int.Parse(item, CultureInfo.InvariantCulture)),
        ];

    private static string Value(string[] args, ref int index)
    {
        index++;
        return index < args.Length
            ? args[index]
            : throw new ArgumentException($"A opção {args[index - 1]} exige um valor.", nameof(args));
    }

    private static int Number(string[] args, ref int index)
        => int.Parse(Value(args, ref index), CultureInfo.InvariantCulture);

    private static double Fraction(string[] args, ref int index)
        => double.Parse(Value(args, ref index), CultureInfo.InvariantCulture);
}
