using System.Globalization;
using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests;

/// <summary>What the probe run is for.</summary>
internal enum ProbeMode
{
    /// <summary>The whole factorial design, the interference arm and the read scenarios.</summary>
    Full,

    /// <summary>The short guard run compared against the versioned baseline.</summary>
    Smoke,

    /// <summary>
    /// The outbox claim alone: seed the pending backlog and read the plan and
    /// the per-batch cost of every band. It exists apart from the full run
    /// because the trail arms take hours and answer a different question.
    /// </summary>
    Relay,

    /// <summary>
    /// The two delivery paths whose budgets the phase-two design states and
    /// nothing measured: what one scheduler round costs on the fallback path at
    /// a real table volume, and what one provider callback costs to ingest at
    /// each batch size. It is separate from the full run because it seeds the
    /// delivery tables, which the trail arms neither need nor want.
    /// </summary>
    Delivery,

    /// <summary>
    /// The published-read memoization under concurrent misses with its budget
    /// full. It touches no database at all, so it never starts a container and
    /// it is the one mode that runs anywhere.
    /// </summary>
    Memoization,

    /// <summary>
    /// What one notification form costs to render, on the context its fields
    /// share and on one context per render. Like the memoization mode it needs
    /// no database, so it runs anywhere.
    /// </summary>
    Render,

    /// <summary>
    /// What a published catalogue already in memory costs to look up in the
    /// parse memoization, and whether it survives being read. In process like
    /// the two modes above, so it runs anywhere.
    /// </summary>
    Parse,

    /// <summary>
    /// Compares buffer, streaming and disk spool with one deterministic input.
    /// It is entirely in process and starts no database.
    /// </summary>
    AttachmentTransfer,

    /// <summary>
    /// Compares the same three shapes doing the work a send actually does:
    /// read the attachment, encode it as base64, compose the whole provider
    /// body and push it over a socket to a double that reads it back. It needs
    /// no database either, and it promotes nothing on its own.
    /// </summary>
    ProviderTransfer,
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

    /// <summary>
    /// Regression tolerated on the normalized hold, derived rather than picked:
    /// twice the dispersion measured for a ratio on this bench, which was about
    /// 27 %. The floor that keeps it useful is the distance to the known
    /// failure: without the tail index the same metric moves by more than an
    /// order of magnitude, so a limit at 1.55 of the reference still sits far
    /// below anything a real regression produces.
    /// </summary>
    internal double Tolerance { get; private init; } = 0.55;

    /// <summary>
    /// How much the hold window may grow between the two guard volumes; the
    /// ceiling is one plus this value.
    /// </summary>
    /// <remarks>
    /// A threshold belongs in the empty region between the healthy distribution
    /// and the failure distribution, never on the edge of the noise: the
    /// previous ceiling of 1.25 sat inside the observed spread of healthy runs
    /// (0.917 and 1.272 on the same code and schema), which is how a gate
    /// fabricates a failure and gets itself silenced. Healthy reaches about
    /// 1.33; the failure signature, with the tail index missing, is around
    /// 55 times. The ceiling of 3.0 sits 2.4 times above the worst healthy run
    /// observed and roughly 18 times below the known failure, and still catches
    /// a partial regression.
    /// </remarks>
    internal double VolumeDrift { get; private init; } = 2.0;

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

    internal string BaselinePath { get; private init; } = BaselinePathOf("audit-chain-contention.json");

    internal bool UpdateBaseline { get; private init; }

    internal string? ReportPath { get; private init; }

    /// <summary>
    /// Notification rows the delivery run seeds before it measures. The overdue
    /// attempts are a rare fraction of it, because a scan whose matches are
    /// common has proved nothing about the plan it gets in production.
    /// </summary>
    internal IReadOnlyList<int> DeliveryVolumes { get; private init; } = [50_000, 500_000];

    /// <summary>Batch sizes the callback cost is measured at, up to the route ceiling.</summary>
    internal IReadOnlyList<int> CallbackBatches { get; private init; } = [1, 10, 50, 200, 500];

    /// <summary>Callbacks measured per cell, and scheduler rounds measured per statement.</summary>
    internal int DeliveryRepeats { get; private init; } = 30;

    /// <summary>
    /// Rows one scheduler round claims, which is the deployed default of the
    /// scan. It is a term of the measurement and not a knob of the probe: a
    /// round that claimed a different number would answer about a scheduler
    /// nobody runs.
    /// </summary>
    internal int BatchSize { get; private init; } = 200;

    /// <summary>
    /// Threads that drive an in-process memoization at the same time, whether
    /// they miss on it or read it hot. It defaults to every core because the
    /// question is what the policy costs when the whole machine asks at once,
    /// which is what a burst of distinct template keys does to a worker role.
    /// </summary>
    internal int MemoizationWorkers { get; private init; } = Environment.ProcessorCount;

    /// <summary>
    /// Forms one render arm measures. The cost of a form is a few tens of
    /// microseconds, so thousands of them are what turn the timer resolution
    /// and the odd background collection into a rounding error.
    /// </summary>
    internal int RenderForms { get; private init; } = 2_000;

    /// <summary>
    /// Notification forms the parse arm keeps hot, five sources each. The
    /// default sits just past a thousand sources, which is the size a published
    /// catalogue of a few dozen templates reaches across channels and locales,
    /// and it is the size at which a memoization bounded by counting entries
    /// stops holding one.
    /// </summary>
    internal int ParseForms { get; private init; } = 205;

    /// <summary>UTF-8 bytes transferred by every attachment arm.</summary>
    internal int AttachmentCorpusBytes { get; private init; } = 4_194_304;

    /// <summary>Envelope bytes prepended to the same corpus in every arm.</summary>
    internal int AttachmentEnvelopeBytes { get; private init; } = 1_024;

    /// <summary>Operations offered to every attachment arm.</summary>
    internal int AttachmentRepeats { get; private init; } = 12;

    /// <summary>Maximum simultaneous operations in every attachment arm.</summary>
    internal int AttachmentConcurrency { get; private init; } = Math.Min(2, Environment.ProcessorCount);

    /// <summary>The complete comparison set. Partial comparisons are refused.</summary>
    internal IReadOnlyList<string> AttachmentArms { get; private init; } = ["buffer", "streaming", "spool"];

    /// <summary>
    /// Which corpus of the matrix the run measures. Naming a profile sets the
    /// size, the count and the shape of the content together, because those
    /// three are what a cell of the matrix is; setting any of them by hand
    /// turns the run into a custom one, which the reference has no cell for.
    /// </summary>
    internal string ProviderProfileId { get; private init; } = ProviderTransferProfiles.MaxSingle;

    /// <summary>Raw bytes of every attachment the provider run transfers.</summary>
    internal long ProviderAttachmentBytes { get; private init; }
        = ProviderTransferProfiles.Of(ProviderTransferProfiles.MaxSingle).AttachmentBytes;

    /// <summary>Attachments per message; the ratified envelope bounds them together with their size.</summary>
    internal int ProviderAttachmentCount { get; private init; } = 1;

    /// <summary>What the attachment bytes look like, which decides whether the body checks can fail.</summary>
    internal AttachmentContentShape ProviderContentShape { get; private init; }
        = AttachmentContentShape.Readable;

    /// <summary>Bytes the source hands over per read, which is what a remote read chunks at.</summary>
    internal int ProviderSourceChunkBytes { get; private init; } = 64 * 1_024;

    /// <summary>Pause the source takes per chunk, which is the latency of that read.</summary>
    internal TimeSpan ProviderSourceLatency { get; private init; }

    /// <summary>
    /// Operations offered to every provider-transfer arm. Two hundred is the
    /// floor at which the ninety-fifth percentile stops being the largest
    /// sample under another name.
    /// </summary>
    internal int ProviderRepeats { get; private init; } = ProviderTransferBudget.MinimumSamplesPerArm;

    /// <summary>Maximum simultaneous sends in every provider-transfer arm.</summary>
    internal int ProviderConcurrency { get; private init; }
        = ProviderTransferBudget.SendsInFlightPerReplica;

    /// <summary>
    /// The arm the budget is charged to. It is the candidate for promotion and
    /// nothing else; pointing it at the buffering arm is how the budget checks
    /// are shown to be able to fail.
    /// </summary>
    internal string ProviderCandidateArm { get; private init; } = ProviderTransferInvariants.DefaultCandidate;

    /// <summary>
    /// Reports of isolated runs a new reference is the median of. Recording a
    /// reference from the run that then compares against it is
    /// self-certification, so the two never share a process.
    /// </summary>
    internal IReadOnlyList<string> ProviderBaselineReports { get; private init; } = [];

    /// <summary>
    /// Whether the body declares its length or travels chunked. Both are
    /// measurable and neither is decided here: the run records which one it
    /// used, and the choice is taken elsewhere.
    /// </summary>
    internal bool ProviderContentLength { get; private init; } = true;

    /// <summary>The complete comparison set. Partial comparisons are refused.</summary>
    internal IReadOnlyList<string> ProviderArms { get; private init; } = ["buffer", "streaming", "spool"];

    internal static ProbeSettings Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var settings = new ProbeSettings();
        var explicitDuration = false;
        var explicitVolumes = false;
        var explicitAppends = false;
        var explicitArms = false;
        var explicitRepeats = false;
        var explicitBaseline = false;

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
                case "--delivery-volumes":
                    settings = settings with { DeliveryVolumes = ParseVolumes(Value(args, ref index)) };
                    break;
                case "--callback-batches":
                    settings = settings with { CallbackBatches = ParseVolumes(Value(args, ref index)) };
                    break;
                case "--delivery-repeats":
                    settings = settings with { DeliveryRepeats = Number(args, ref index) };
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
                    explicitBaseline = true;
                    break;
                case "--update-baseline":
                    settings = settings with { UpdateBaseline = true };
                    break;
                case "--memoization-workers":
                    settings = settings with { MemoizationWorkers = Number(args, ref index) };
                    break;
                case "--render-forms":
                    settings = settings with { RenderForms = Number(args, ref index) };
                    break;
                case "--parse-forms":
                    settings = settings with { ParseForms = Number(args, ref index) };
                    break;
                case "--attachment-corpus-bytes":
                    settings = settings with { AttachmentCorpusBytes = Number(args, ref index) };
                    break;
                case "--attachment-envelope-bytes":
                    settings = settings with { AttachmentEnvelopeBytes = Number(args, ref index) };
                    break;
                case "--attachment-repeats":
                    settings = settings with { AttachmentRepeats = Number(args, ref index) };
                    break;
                case "--attachment-concurrency":
                    settings = settings with { AttachmentConcurrency = Number(args, ref index) };
                    break;
                case "--attachment-arms":
                    settings = settings with
                    {
                        AttachmentArms =
                        [
                            .. Value(args, ref index)
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        ],
                    };
                    break;
                case "--provider-profile":
                    {
                        var profileId = Value(args, ref index);
                        ProviderTransferCorpus corpus = ProviderTransferProfiles.Of(profileId);
                        settings = settings with
                        {
                            ProviderProfileId = profileId,
                            ProviderAttachmentBytes = corpus.AttachmentBytes,
                            ProviderAttachmentCount = corpus.AttachmentCount,
                            ProviderContentShape = corpus.ContentShape,
                        };
                    }

                    break;
                case "--provider-attachment-bytes":
                    settings = settings with
                    {
                        ProviderAttachmentBytes = Amount(args, ref index),
                        ProviderProfileId = ProviderTransferProfiles.Custom,
                    };
                    break;
                case "--provider-attachments":
                    settings = settings with
                    {
                        ProviderAttachmentCount = Number(args, ref index),
                        ProviderProfileId = ProviderTransferProfiles.Custom,
                    };
                    break;
                case "--provider-content-shape":
                    settings = settings with
                    {
                        ProviderContentShape = ParseContentShape(Value(args, ref index)),
                        ProviderProfileId = ProviderTransferProfiles.Custom,
                    };
                    break;
                case "--provider-candidate":
                    settings = settings with { ProviderCandidateArm = Value(args, ref index) };
                    break;
                case "--provider-baseline-from":
                    settings = settings with
                    {
                        ProviderBaselineReports =
                        [
                            .. Value(args, ref index)
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        ],
                    };
                    break;
                case "--provider-source-chunk-bytes":
                    settings = settings with { ProviderSourceChunkBytes = Number(args, ref index) };
                    break;
                case "--provider-source-latency-micros":
                    settings = settings with
                    {
                        ProviderSourceLatency = TimeSpan.FromMicroseconds(Number(args, ref index)),
                    };
                    break;
                case "--provider-repeats":
                    settings = settings with { ProviderRepeats = Number(args, ref index) };
                    break;
                case "--provider-concurrency":
                    settings = settings with { ProviderConcurrency = Number(args, ref index) };
                    break;
                case "--provider-transfer-encoding":
                    settings = settings with
                    {
                        ProviderContentLength = ParseTransferEncoding(Value(args, ref index)),
                    };
                    break;
                case "--provider-arms":
                    settings = settings with
                    {
                        ProviderArms =
                        [
                            .. Value(args, ref index)
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        ],
                    };
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

        if (settings.Mode is ProbeMode.Memoization)
        {
            // Seconds, not the twenty of a trail arm: the arm is a tight
            // in-process loop, so it collects millions of samples in five.
            settings = settings with
            {
                ArmDuration = explicitDuration ? settings.ArmDuration : TimeSpan.FromSeconds(5),
                BaselinePath = explicitBaseline
                    ? settings.BaselinePath
                    : BaselinePathOf("published-read-memoization.json"),
            };
        }

        if (settings.Mode is ProbeMode.Render && !explicitBaseline)
        {
            settings = settings with { BaselinePath = BaselinePathOf("published-render-cost.json") };
        }

        if (settings.Mode is ProbeMode.Parse)
        {
            // Seconds, like the sibling in-process mode and for the same
            // reason: the arm is a tight loop over memory and collects tens of
            // millions of samples in five.
            settings = settings with
            {
                ArmDuration = explicitDuration ? settings.ArmDuration : TimeSpan.FromSeconds(5),
                BaselinePath = explicitBaseline
                    ? settings.BaselinePath
                    : BaselinePathOf("scriban-parse-memoization.json"),

                // Three passes, like the guard run of the trail: the spread
                // between two honest passes of this arm is a quarter of the
                // value, which is half the tolerance, so a reference from a
                // single pass grades the luck of that pass.
                GuardRepeats = explicitRepeats ? settings.GuardRepeats : 3,
            };
        }

        if (settings.Mode is ProbeMode.AttachmentTransfer)
        {
            settings = settings with
            {
                BaselinePath = explicitBaseline
                    ? settings.BaselinePath
                    : BaselinePathOf("attachment-transfer-method.json"),
            };
            ValidateAttachmentTransfer(settings);
        }

        if (settings.Mode is ProbeMode.ProviderTransfer)
        {
            settings = settings with
            {
                BaselinePath = explicitBaseline
                    ? settings.BaselinePath
                    : BaselinePathOf("provider-transfer.json"),
            };
            ValidateProviderTransfer(settings);
        }

        if (settings.Mode is ProbeMode.Smoke)
        {
            // Two volumes and the production shape only. The guard reads how
            // many round trips the append holds the lock for, and whether that
            // grows with the partition; both are ratios taken inside one run.
            //
            // The two volumes sit two orders of magnitude apart on purpose.
            // Widening the separation is worth more than moving a threshold:
            // the healthy ratio stays around one whatever the separation, while
            // a broken one grows with it, so the signal rises and the noise
            // does not. Every cell is the median of three passes, because half
            // a millisecond of host jitter over a two millisecond hold is noise
            // that yields to sampling.
            settings = settings with
            {
                Volumes = explicitVolumes ? settings.Volumes : [10_000, 1_000_000],
                Arms = explicitArms ? settings.Arms : ["A5"],
                ArmDuration = explicitDuration ? settings.ArmDuration : TimeSpan.FromSeconds(5),
                MaxAppendsPerArm = explicitAppends ? settings.MaxAppendsPerArm : 4_000,
                GuardRepeats = explicitRepeats ? settings.GuardRepeats : 3,
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
    private static string BaselinePathOf(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Platform.PerformanceTests.csproj")))
            {
                return Path.Combine(directory.FullName, "baselines", fileName);
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "baselines", fileName);
    }

    private static ProbeMode ParseMode(string value) => value switch
    {
        "full" => ProbeMode.Full,
        "smoke" => ProbeMode.Smoke,
        "relay" => ProbeMode.Relay,
        "delivery" => ProbeMode.Delivery,
        "memoization" => ProbeMode.Memoization,
        "render" => ProbeMode.Render,
        "parse" => ProbeMode.Parse,
        "attachment-transfer" => ProbeMode.AttachmentTransfer,
        "provider-transfer" => ProbeMode.ProviderTransfer,
        _ => throw new ArgumentException($"Modo desconhecido: {value}", nameof(value)),
    };

    private static void ValidateAttachmentTransfer(ProbeSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AttachmentCorpusBytes, 1_024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.AttachmentCorpusBytes, 536_870_912);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AttachmentEnvelopeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.AttachmentEnvelopeBytes, 1_048_576);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AttachmentRepeats, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.AttachmentRepeats, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.AttachmentConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.AttachmentConcurrency, 256);
        if (settings.AttachmentConcurrency > settings.AttachmentRepeats)
        {
            throw new ArgumentException(
                "A concorrência da transferência não pode exceder o número de repetições.",
                nameof(settings));
        }

        string[] expectedArms = ["buffer", "streaming", "spool"];
        if (settings.AttachmentArms.Count != expectedArms.Length
            || settings.AttachmentArms.Distinct(StringComparer.Ordinal).Count() != expectedArms.Length
            || expectedArms.Except(settings.AttachmentArms, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException(
                "A comparação exige exatamente os braços buffer, streaming e spool, sem duplicatas.",
                nameof(settings));
        }

        if (settings.Tolerance <= 0 || settings.Tolerance >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Tolerance,
                "A tolerância do modo attachment-transfer deve ficar entre zero e um.");
        }

        if (string.IsNullOrWhiteSpace(settings.BaselinePath)
            || !string.Equals(Path.GetExtension(settings.BaselinePath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A linha de base deve apontar para um arquivo JSON.", nameof(settings));
        }
    }

    private static bool ParseTransferEncoding(string value) => value switch
    {
        "content-length" => true,
        "chunked" => false,
        _ => throw new ArgumentException(
            $"Codificação de transferência desconhecida: {value}. Use content-length ou chunked.",
            nameof(value)),
    };

    private static AttachmentContentShape ParseContentShape(string value) => value switch
    {
        "readable" => AttachmentContentShape.Readable,
        "escapable" => AttachmentContentShape.Escapable,
        _ => throw new ArgumentException(
            $"Forma de conteúdo desconhecida: {value}. Use readable ou escapable.",
            nameof(value)),
    };

    private static void ValidateProviderTransfer(ProbeSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ProviderAttachmentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ProviderAttachmentCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ProviderAttachmentCount, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ProviderSourceChunkBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ProviderSourceChunkBytes, 8 * 1_024 * 1_024);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ProviderRepeats, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ProviderRepeats, 1_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ProviderConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.ProviderConcurrency, 256);
        if (settings.ProviderConcurrency > settings.ProviderRepeats)
        {
            throw new ArgumentException(
                "A concorrência da transferência não pode exceder o número de repetições.",
                nameof(settings));
        }

        string[] expectedArms = ["buffer", "streaming", "spool"];
        if (settings.ProviderArms.Count != expectedArms.Length
            || settings.ProviderArms.Distinct(StringComparer.Ordinal).Count() != expectedArms.Length
            || expectedArms.Except(settings.ProviderArms, StringComparer.Ordinal).Any())
        {
            throw new ArgumentException(
                "A comparação exige exatamente os braços buffer, streaming e spool, sem duplicatas.",
                nameof(settings));
        }

        if (!settings.ProviderArms.Contains(settings.ProviderCandidateArm, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"O candidato {settings.ProviderCandidateArm} não está entre os braços medidos.",
                nameof(settings));
        }

        if (settings.UpdateBaseline
            && settings.ProviderBaselineReports.Count < ProviderTransferBaseline.MinimumRunsPerCell)
        {
            throw new ArgumentException(
                "A referência do modo provider-transfer é a mediana de rodadas isoladas: passe ao menos "
                + $"{ProviderTransferBaseline.MinimumRunsPerCell} relatórios em --provider-baseline-from.",
                nameof(settings));
        }
    }

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

    private static long Amount(string[] args, ref int index)
        => long.Parse(Value(args, ref index), CultureInfo.InvariantCulture);

    private static double Fraction(string[] args, ref int index)
        => double.Parse(Value(args, ref index), CultureInfo.InvariantCulture);
}
