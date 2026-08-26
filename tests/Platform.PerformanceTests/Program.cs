using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.Infrastructure;
using NotificationHub.PerformanceTests.Instrumentation;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests;

/// <summary>
/// Entry point of the contention probe. One command runs the whole factorial
/// design against a throwaway PostgreSQL, or the short guard run that a pull
/// request compares against the versioned baseline.
/// </summary>
internal static class Program
{
    private const int ExitPass = 0;

    private const int ExitGateFailed = 1;

    private const int ExitRefused = 2;

    private static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(3);

    private const int WarmupTransactions = 40;

    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    internal static async Task<int> Main(string[] args)
    {
        var settings = ProbeSettings.Parse(args);
        if (settings.ConnectionString is not null && !settings.AllowTrailWrites)
        {
            Console.Error.WriteLine(
                "A sonda grava na trilha, que é append-only: nada do que ela escrever pode ser apagado depois.");
            Console.Error.WriteLine(
                "Para apontar para um banco existente, repita o comando com --allow-trail-writes.");
            return ExitRefused;
        }

        // The delivery mode is refused against anything but a throwaway
        // container, and no flag opens it. The other modes write rows that sit
        // still; this one writes outbox rows addressed to the delivery tracker,
        // and an outbox row is not inert: a relay pointed at that database
        // publishes it. The failure would not be a dirty table, it would be
        // synthetic delivery events entering a real hub.
        if (settings.Mode is ProbeMode.Delivery && settings.ConnectionString is not null)
        {
            Console.Error.WriteLine(
                "O modo delivery escreve linhas no outbox, que um relay apontado para esse banco "
                + "publicaria como evento de entrega real.");
            Console.Error.WriteLine(
                "Ele roda apenas contra o contêiner descartável: repita o comando sem "
                + "--connection-string.");
            return ExitRefused;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopping.Cancel();
        };

        // Before anything reaches for a database: this mode measures an
        // in-process cache and starting a container for it would only add the
        // one dependency that keeps the guard from running everywhere.
        if (settings.Mode is ProbeMode.Memoization)
        {
            return await RunMemoizationAsync(settings, stopping.Token);
        }

        if (settings.Mode is ProbeMode.Render)
        {
            return await RunRenderCostAsync(settings, stopping.Token);
        }

        var started = Stopwatch.GetTimestamp();
        var poolSize = Math.Max(settings.Appenders + 8, 64);
        await using ProbeDatabase database =
            await ProbeDatabase.StartAsync(settings.ConnectionString, poolSize, stopping.Token);
        Report($"Banco pronto: {(database.IsThrowaway ? "contêiner descartável" : "conexão informada")}.");

        ProbeOutcome outcome = await RunAsync(database, settings, stopping.Token);
        var text = ReportRenderer.Render(outcome);
        Console.WriteLine();
        Console.WriteLine(text);

        if (settings.ReportPath is not null)
        {
            var directory = Path.GetDirectoryName(settings.ReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                settings.ReportPath, JsonSerializer.Serialize(outcome, ReportOptions), stopping.Token);
            Report($"Relatório em JSON: {settings.ReportPath}");
        }

        Report($"Rodada completa em {Stopwatch.GetElapsedTime(started).TotalMinutes:0.0} minutos.");
        return settings.Mode is ProbeMode.Smoke
            ? await GateAsync(outcome, settings, stopping.Token)
            : ExitPass;
    }

    private static async Task<ProbeOutcome> RunAsync(
        ProbeDatabase database,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode is ProbeMode.Relay)
        {
            return await RunRelayAsync(database, settings, cancellationToken);
        }

        if (settings.Mode is ProbeMode.Delivery)
        {
            return await RunDeliveryAsync(database, settings, cancellationToken);
        }

        var current = PartitionMonth.Of(DateTimeOffset.UtcNow);
        IReadOnlyList<PartitionMonth> distinct =
        [
            .. Enumerable.Range(1, settings.Appenders)
                .Select(offset => PartitionMonth.Of(DateTimeOffset.UtcNow.AddMonths(-offset))),
        ];

        await database.EnsurePartitionAsync(current, cancellationToken);
        foreach (PartitionMonth month in distinct)
        {
            await database.EnsurePartitionAsync(month, cancellationToken);
        }

        var progress = new Progress<string>(Console.WriteLine);

        // The yardstick is sampled at both ends of the run and merged, so a
        // host that drifts during the run shows up in the divisor instead of
        // being attributed to the append.
        var roundTripSamples = new LatencyHistogram();
        await RoundTripProbe.SampleAsync(
            database.DataSource, roundTripSamples, settings.Appenders, 250, cancellationToken);

        var arms = new List<ArmResult>();
        var verification = new List<VerificationCost>();
        var sustained = new List<SustainedRateResult>();
        var readPaths = new List<ReadPathPlan>();
        TailIndexChoice? tailIndex = null;
        InterferenceResult? interference = null;
        IReadOnlyList<RelayPlan> relayPlans = [];
        var tailIndexSource = "não aplicável";

        for (var index = 0; index < settings.Volumes.Count; index++)
        {
            var volume = settings.Volumes[index];

            // The interference question is whether the purge moves the tail of
            // the append, and it is answered at the smallest volume on purpose.
            // There the append is cheap, so a round of the purge shows up as a
            // shift instead of disappearing inside the cost of the scan, and
            // the cell collects thousands of samples instead of dozens.
            var interferenceCell = index == 0;
            var needsControlPartitions = settings.Arms.Contains("A1", StringComparer.Ordinal);
            Report(needsControlPartitions
                ? $"Volume {volume:N0}: carregando a partição corrente e as {distinct.Count} partições de controle."
                : $"Volume {volume:N0}: carregando a partição corrente.");
            await TrailSeeder.EnsureRowsAsync(database, current, volume, progress, cancellationToken);
            if (needsControlPartitions)
            {
                foreach (PartitionMonth month in distinct)
                {
                    await TrailSeeder.EnsureRowsAsync(database, month, volume, progress, cancellationToken);
                }
            }

            // Whether the schema already answers the tail read decides who owns
            // the index during the mitigated arm, and the report says which,
            // because a guard that watches an index the probe created itself
            // cannot notice a migration dropping production's.
            var schemaAnswers = await TailQueryPlanScenario.SchemaAnswersTailAsync(
                database, current, cancellationToken);
            tailIndexSource = schemaAnswers
                ? "índice já presente no schema"
                : "índice criado pela sonda durante o braço";

            if (settings.Mode is ProbeMode.Full && !schemaAnswers)
            {
                Report($"Volume {volume:N0}: comparando as formas de índice da consulta de cauda.");
                tailIndex = await TailQueryPlanScenario.RunAsync(database, current, volume, cancellationToken);
                Report($"  forma escolhida: {tailIndex.Variant}");
            }

            if (settings.Mode is ProbeMode.Full)
            {
                // Read before any arm runs: an arm that has to create an index
                // of its own would change the answer, and the question here is
                // what the schema does on its own.
                Report($"Volume {volume:N0}: planos dos caminhos que percorrem a partição por seq.");
                readPaths.AddRange(
                    await ChainReadPathsScenario.RunAsync(database, current, volume, cancellationToken));
                foreach (ReadPathPlan path in readPaths.Where(entry => entry.Volume == volume))
                {
                    Report($"  {path.Path}: {path.ExecutionMs:0.000} ms, {path.Buffers:N0} buffers, "
                        + $"{(path.ScansSequentially ? "varredura sequencial" : "atendido por índice")}");
                }
            }

            IReadOnlyList<ContentionArm> definitions =
                ContentionArms.Build(current, distinct, settings.Appenders);
            foreach (ContentionArm arm in definitions)
            {
                if (!settings.Arms.Contains(arm.Id, StringComparer.Ordinal))
                {
                    continue;
                }

                var createIndex = arm.RequiresTailIndex && !schemaAnswers
                    ? tailIndex?.CreateSql ?? TailQueryPlanScenario.RatifiedIndexSql(current)
                    : null;
                if (createIndex is not null)
                {
                    await database.ExecuteAsync(createIndex, cancellationToken);
                }

                try
                {
                    // Every arm starts from the same place. Without this the
                    // arm that happens to run while the checkpointer is
                    // flushing the previous arm's writes carries a tail that
                    // belongs to the host, and the report would read it as
                    // contention.
                    await database.ExecuteAsync("CHECKPOINT", cancellationToken);

                    // A discarded pass first. The opening appends of an arm pay
                    // for a cold buffer cache, a cold pool and a cold plan
                    // cache, and at the smaller volumes that cost lands whole
                    // on the percentile the gate reads. The pass is bounded by
                    // transactions as well as by time, because at the larger
                    // volumes a single append already outlasts the seconds.
                    await ArmRunner.RunAsync(
                        database.DataSource, arm, volume, WarmupDuration, WarmupTransactions, cancellationToken);
                    await database.ExecuteAsync("CHECKPOINT", cancellationToken);

                    Report($"Volume {volume:N0}: braço {arm.Id}.");
                    ArmResult result = await RunMedianAsync(
                        database, arm, volume, settings, cancellationToken);
                    arms.Add(result);
                    Report($"  {result.Transactions} transações, posse p50 {result.Hold.P50:0.000} ms, "
                        + $"janela p99 {result.Window.P99:0.000} ms, {result.AppendsPerSecond:N1} appends/s");

                    if (settings.Mode is ProbeMode.Full && index == 0 && arm.Id is "A3" or "A5")
                    {
                        Report($"  célula de taxa oferecida ({settings.SustainedRate}/s) sobre o braço {arm.Id}.");
                        sustained.Add(await SustainedRateScenario.RunAsync(
                            database.DataSource,
                            current,
                            arm.Shape,
                            AppendProfiles.RealMixture,
                            volume,
                            settings.SustainedRate,
                            settings.SustainedDuration,
                            cancellationToken));
                    }
                }
                finally
                {
                    if (createIndex is not null)
                    {
                        await database.ExecuteAsync(
                            TailQueryPlanScenario.DropOf(createIndex), cancellationToken);
                    }
                }
            }

            if (settings.Mode is ProbeMode.Full)
            {
                Report($"Volume {volume:N0}: custo da verificação integral da partição corrente.");
                verification.Add(await VerificationCostScenario.RunAsync(
                    database, current, volume, cancellationToken));

                if (interferenceCell && settings.Arms.Contains("A3", StringComparer.Ordinal))
                {
                    Report($"Volume {volume:N0}: braço de interferência da purga de dedupe.");
                    ContentionArm mixture = definitions.Single(arm => arm.Id == "A3");
                    interference = await InterferenceScenario.RunAsync(
                        database,
                        mixture,
                        volume,
                        settings.PurgeBacklog,
                        TimeSpan.FromDays(15),
                        settings.ArmDuration,
                        settings.MaxAppendsPerArm,
                        cancellationToken);
                }
            }
        }

        if (settings.Mode is ProbeMode.Full)
        {
            Report($"Plano de execução do relay sobre backlog de {settings.RelayBacklog:N0} linhas pendentes.");
            relayPlans = await RelayPlanScenario.RunAsync(
                database, settings.RelayBacklog, 100, cancellationToken);
        }

        await RoundTripProbe.SampleAsync(
            database.DataSource, roundTripSamples, settings.Appenders, 250, cancellationToken);
        PhaseStatistics roundTrip = roundTripSamples.Snapshot();
        Report($"Ida trivial ao banco nesta rodada: p50 {roundTrip.P50:0.000} ms, "
            + $"p99 {roundTrip.P99:0.000} ms, n={roundTrip.Samples}.");

        return new ProbeOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            settings.Mode.ToString(),
            new ProbeEnvironment(
                Environment.MachineName,
                Environment.ProcessorCount,
                Environment.Version.ToString(),
                database.IsThrowaway ? "postgres:17-alpine em contêiner" : "conexão informada",
                database.IsThrowaway,
                settings.Appenders,
                settings.ArmDuration.TotalSeconds),
            arms,
            roundTrip,
            tailIndexSource,
            ProbeAnalysis.Ratios(arms),
            ProbeAnalysis.Sensitivity(arms),
            sustained,
            tailIndex,
            readPaths,
            interference,
            relayPlans,
            verification,
            [],
            [],
            ProbeAnalysis.Verdict(arms));
    }

    /// <summary>
    /// The outbox claim on its own: seed the pending backlog, then read the
    /// plan and the per-batch cost of every band. Nothing of the trail runs
    /// here, and the report carries no verdict, because the escalation ladder
    /// reads the arms this mode never measures.
    /// </summary>
    private static async Task<ProbeOutcome> RunRelayAsync(
        ProbeDatabase database,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        Report($"Semeando {settings.RelayBacklog:N0} linhas pendentes no outbox.");
        IReadOnlyList<RelayPlan> plans = await RelayPlanScenario.RunAsync(
            database, settings.RelayBacklog, 100, cancellationToken);
        foreach (RelayPlan plan in plans)
        {
            Report($"  {plan.Arm}, banda {plan.Band} ({plan.BandName}): lote p50 {plan.BatchP50Ms:0.000} ms, "
                + $"{plan.RowsRemovedByFilter:N0} linhas descartadas pelo filtro.");
        }

        return new ProbeOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            settings.Mode.ToString(),
            new ProbeEnvironment(
                Environment.MachineName,
                Environment.ProcessorCount,
                Environment.Version.ToString(),
                database.IsThrowaway ? "postgres:17-alpine em contêiner" : "conexão informada",
                database.IsThrowaway,
                settings.Appenders,
                settings.ArmDuration.TotalSeconds),
            [],
            null,
            "não aplicável",
            [],
            [],
            [],
            null,
            [],
            null,
            plans,
            [],
            [],
            [],
            null);
    }

    /// <summary>
    /// The two delivery paths the phase-two design states a budget for and that
    /// nothing measured: the scheduler round on the fallback path, and the
    /// ingestion of one provider callback.
    /// <para>
    /// They share a mode because they share a seed and a schema, and they are
    /// outside the full run because the trail arms take hours and answer a
    /// different question. The report carries no verdict: the escalation ladder
    /// reads the contention arms this mode never runs, and a budget is compared
    /// against the accepted window by a person, not by this exit code.
    /// </para>
    /// </summary>
    private static async Task<ProbeOutcome> RunDeliveryAsync(
        ProbeDatabase database,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(Console.WriteLine);

        // The yardstick, sampled at both ends of the run and merged. It is not
        // decoration here: every event of the ingestion cell costs five round
        // trips, so on a bench whose round trip is expensive the measurement is
        // dominated by the hop and not by the database. Reporting the divisor is
        // what lets a reader tell the two apart instead of reading a laptop's
        // port forwarding as the cost of a commit.
        var roundTripSamples = new LatencyHistogram();
        await RoundTripProbe.SampleAsync(
            database.DataSource, roundTripSamples, settings.Appenders, 250, cancellationToken);

        var fallback = new List<FallbackLatency>();
        foreach (var volume in settings.DeliveryVolumes)
        {
            Report($"Volume {volume:N0}: preparando notificações e tentativas.");
            await DeliverySeeder.FillAttemptsAsync(database, volume, progress, cancellationToken);

            Report($"Volume {volume:N0}: rodada do scheduler no caminho de fallback.");
            IReadOnlyList<FallbackLatency> measured = await FallbackLatencyScenario.RunAsync(
                database, volume, settings.BatchSize, settings.DeliveryRepeats, cancellationToken);
            foreach (FallbackLatency entry in measured)
            {
                Report($"  {entry.Statement}: p50 {entry.Round.P50:0.000} ms, p99 {entry.Round.P99:0.000} ms, "
                    + $"{entry.Claimed} reivindicadas, "
                    + $"{(entry.ScansSequentially ? "varredura sequencial" : "atendido por índice")}");
            }

            fallback.AddRange(measured);
        }

        PhaseStatistics interim = roundTripSamples.Snapshot();
        Report($"Ida trivial ao banco nesta bancada: p50 {interim.P50:0.000} ms, p99 {interim.P99:0.000} ms.");
        Report("Custo de ingestão de um callback, por tamanho de lote e por forma de transação.");
        IReadOnlyList<WebhookIngestionCost> ingestion = await WebhookIngestionCostScenario.RunAsync(
            database, settings.CallbackBatches, settings.DeliveryRepeats, cancellationToken);
        foreach (WebhookIngestionCost cost in ingestion)
        {
            Report($"  {cost.Shape}, {cost.EventsPerCallback} eventos: callback p50 {cost.Callback.P50:0.000} ms, "
                + $"por evento p50 {cost.PerEventP50Ms:0.000} ms");
        }

        await RoundTripProbe.SampleAsync(
            database.DataSource, roundTripSamples, settings.Appenders, 250, cancellationToken);
        PhaseStatistics roundTrip = roundTripSamples.Snapshot();
        Report($"Ida trivial ao banco nesta rodada: p50 {roundTrip.P50:0.000} ms, "
            + $"p99 {roundTrip.P99:0.000} ms, n={roundTrip.Samples}.");

        return new ProbeOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            settings.Mode.ToString(),
            new ProbeEnvironment(
                Environment.MachineName,
                Environment.ProcessorCount,
                Environment.Version.ToString(),
                database.IsThrowaway ? "postgres:17-alpine em contêiner" : "conexão informada",
                database.IsThrowaway,
                settings.Appenders,
                settings.ArmDuration.TotalSeconds),
            [],
            roundTrip,
            "não aplicável",
            [],
            [],
            [],
            null,
            [],
            null,
            [],
            [],
            fallback,
            ingestion,
            null);
    }

    /// <summary>
    /// The memoization guard: run the two arms, then compare the run against the
    /// versioned reference or record a new one. It shares the tolerance knob with
    /// the trail guard and nothing else, because it measures a different thing.
    /// </summary>
    private static async Task<int> RunMemoizationAsync(
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        MemoizationOutcome outcome = PublishedReadMemoizationScenario.Run(
            settings.MemoizationWorkers, settings.ArmDuration, Report);

        if (settings.ReportPath is not null)
        {
            var directory = Path.GetDirectoryName(settings.ReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                settings.ReportPath, JsonSerializer.Serialize(outcome, ReportOptions), cancellationToken);
            Report($"Relatório em JSON: {settings.ReportPath}");
        }

        MemoizationArm throughput = outcome.Arms.Single(arm =>
            string.Equals(arm.ArmId, PublishedReadMemoizationScenario.ThroughputArm, StringComparison.Ordinal));
        if (settings.UpdateBaseline)
        {
            MemoizationBaseline recorded = MemoizationBaseline.From(
                throughput, $"{outcome.Host} / {outcome.Processors} núcleos / .NET {outcome.Runtime}");
            await recorded.SaveAsync(settings.BaselinePath, cancellationToken);
            Report($"Linha de base gravada em {settings.BaselinePath}.");
            return ExitPass;
        }

        if (!File.Exists(settings.BaselinePath))
        {
            Console.Error.WriteLine(
                $"Não existe linha de base em {settings.BaselinePath}; grave uma com --update-baseline.");
            return ExitRefused;
        }

        MemoizationBaseline baseline = await MemoizationBaseline.LoadAsync(
            settings.BaselinePath, cancellationToken);
        GateOutcome gate = MemoizationGate.Evaluate(baseline, outcome, settings.Tolerance);
        Console.WriteLine();
        Console.WriteLine("-- Portão da memoização de leitura publicada --------------------------");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $" Linha de base de {baseline.RecordedAtUtc} em {baseline.RecordedOn}, "
            + $"tolerância {gate.Tolerance:P0}."));
        foreach (GateCheck check in gate.Checks)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $" {check.Metric,-40} referência {check.Reference,10:0.000}  medido {check.Measured,10:0.000}  "
                + $"limite {check.Limit,10:0.000}  {(check.Passes ? "passa" : "REPROVA")}"));
        }

        Console.WriteLine();
        if (gate.Passes)
        {
            Console.WriteLine(" Portão aprovado: a política de evicção não regrediu e o residente seguiu limitado.");
            return ExitPass;
        }

        Console.Error.WriteLine(
            " Portão reprovado: a memoização regrediu contra a linha de base versionada.");
        return ExitGateFailed;
    }

    /// <summary>
    /// The render guard: measure one form on a shared context and on one
    /// context per render, then compare the first against the versioned
    /// reference. Only bytes are compared, so the same file is honest on any
    /// host.
    /// </summary>
    private static async Task<int> RunRenderCostAsync(
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        RenderCostOutcome outcome = PublishedRenderCostScenario.Run(settings.RenderForms, Report);

        if (settings.ReportPath is not null)
        {
            var directory = Path.GetDirectoryName(settings.ReportPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                settings.ReportPath, JsonSerializer.Serialize(outcome, ReportOptions), cancellationToken);
            Report($"Relatório em JSON: {settings.ReportPath}");
        }

        RenderCostArm shared = outcome.Arms.Single(arm =>
            string.Equals(arm.ArmId, PublishedRenderCostScenario.SharedArm, StringComparison.Ordinal));
        if (settings.UpdateBaseline)
        {
            RenderCostBaseline recorded = RenderCostBaseline.From(
                shared, $"{outcome.Host} / {outcome.Processors} núcleos / .NET {outcome.Runtime}");
            await recorded.SaveAsync(settings.BaselinePath, cancellationToken);
            Report($"Linha de base gravada em {settings.BaselinePath}.");
            return ExitPass;
        }

        if (!File.Exists(settings.BaselinePath))
        {
            Console.Error.WriteLine(
                $"Não existe linha de base em {settings.BaselinePath}; grave uma com --update-baseline.");
            return ExitRefused;
        }

        RenderCostBaseline baseline = await RenderCostBaseline.LoadAsync(
            settings.BaselinePath, cancellationToken);
        GateOutcome gate = RenderCostGate.Evaluate(baseline, outcome);
        Console.WriteLine();
        Console.WriteLine("-- Portão do custo de uma forma renderizada ----------------------------");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $" Linha de base de {baseline.RecordedAtUtc} em {baseline.RecordedOn}, "
            + $"folga {gate.Tolerance:P0}."));
        foreach (GateCheck check in gate.Checks)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $" {check.Metric,-45} referência {check.Reference,12:N0}  medido {check.Measured,12:N0}  "
                + $"limite {check.Limit,12:N0}  {(check.Passes ? "passa" : "REPROVA")}"));
        }

        Console.WriteLine();
        if (gate.Passes)
        {
            Console.WriteLine(" Portão aprovado: uma forma segue custando o que a referência registrou.");
            return ExitPass;
        }

        Console.Error.WriteLine(
            " Portão reprovado: o custo de uma forma regrediu contra a linha de base versionada.");
        return ExitGateFailed;
    }

    /// <summary>
    /// Runs the arm as many times as the guard asks for and returns the run
    /// whose hold window sits in the middle. One run is the default; a baseline
    /// takes the median of three, because a reference recorded on an unlucky
    /// run leaves the next honest run without margin.
    /// </summary>
    private static async Task<ArmResult> RunMedianAsync(
        ProbeDatabase database,
        ContentionArm arm,
        int volume,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        var results = new List<ArmResult>();
        for (var repeat = 0; repeat < Math.Max(settings.GuardRepeats, 1); repeat++)
        {
            if (repeat > 0)
            {
                await database.ExecuteAsync("CHECKPOINT", cancellationToken);
            }

            results.Add(await ArmRunner.RunAsync(
                database.DataSource,
                arm,
                volume,
                settings.ArmDuration,
                settings.MaxAppendsPerArm,
                cancellationToken));
        }

        results.Sort((left, right) => left.Hold.P50.CompareTo(right.Hold.P50));
        return results[results.Count / 2];
    }

    private static async Task<int> GateAsync(
        ProbeOutcome outcome,
        ProbeSettings settings,
        CancellationToken cancellationToken)
    {
        var measurement = GateMeasurement.From(
            outcome.Arms, settings.GateArm, outcome.RoundTrip
                ?? throw new InvalidOperationException("A rodada de guarda não mediu a ida trivial ao banco."));

        if (settings.UpdateBaseline)
        {
            var recorded = ContentionBaseline.From(
                measurement, settings.Appenders, $"{outcome.Environment.Host} / {outcome.Environment.Target}");
            await recorded.SaveAsync(settings.BaselinePath, cancellationToken);
            Report($"Linha de base gravada em {settings.BaselinePath}.");
            return ExitPass;
        }

        if (!File.Exists(settings.BaselinePath))
        {
            Console.Error.WriteLine(
                $"Não existe linha de base em {settings.BaselinePath}; grave uma com --update-baseline.");
            return ExitRefused;
        }

        ContentionBaseline baseline = await ContentionBaseline.LoadAsync(settings.BaselinePath, cancellationToken);
        GateOutcome gate = SmokeGate.Evaluate(
            baseline, measurement, settings.Tolerance, settings.VolumeDrift);
        Console.WriteLine("-- Portão da rodada de guarda ----------------------------------------");
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $" Linha de base de {baseline.RecordedAtUtc} em {baseline.RecordedOn}, tolerância {gate.Tolerance:P0}."));
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $" Ida trivial ao banco nesta rodada: {measurement.RoundTripP50Ms:0.000} ms (p50). "
                + $"Posse do braço mitigado: {measurement.HoldP50Ms:0.000} ms em {measurement.Volumes[0]:N0} linhas "
                + $"e {measurement.HoldP50AtLargerVolumeMs:0.000} ms em {measurement.Volumes[^1]:N0}."));
        foreach (GateCheck check in gate.Checks)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $" {check.Metric,-38} referência {check.Reference,8:0.000}  medido {check.Measured,8:0.000}  "
                + $"limite {check.Limit,8:0.000}  desvio {check.Drift,7:P1}  {(check.Passes ? "passa" : "REPROVA")}"));
        }

        Console.WriteLine();
        if (gate.Passes)
        {
            Console.WriteLine(" Portão aprovado: nenhuma métrica de guarda regrediu além da tolerância.");
            return ExitPass;
        }

        Console.Error.WriteLine(
            " Portão reprovado: a forma do append regrediu contra a linha de base versionada.");
        return ExitGateFailed;
    }

    private static void Report(string message)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}"));
}
