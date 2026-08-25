using System.Globalization;
using System.Text;
using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Instrumentation;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Reporting;

/// <summary>Renders the run as the text an engineer reads before deciding anything.</summary>
internal static class ReportRenderer
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    internal static string Render(ProbeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var text = new StringBuilder();
        Header(text, outcome);
        Arms(text, outcome);
        Ratios(text, outcome);
        Sensitivity(text, outcome);
        Sustained(text, outcome);
        TailIndex(text, outcome);
        ReadPaths(text, outcome);
        Interference(text, outcome);
        Relay(text, outcome);
        Verification(text, outcome);
        FallbackLatencies(text, outcome);
        WebhookIngestion(text, outcome);
        Verdict(text, outcome);
        return text.ToString();
    }

    private static void Header(StringBuilder text, ProbeOutcome outcome)
    {
        text.AppendLine("=======================================================================");
        text.AppendLine(" Sonda de contenção da cadeia de auditoria");
        text.AppendLine("=======================================================================");
        text.AppendLine(Culture, $" Gerado em      : {outcome.GeneratedAtUtc}");
        text.AppendLine(Culture, $" Modo           : {outcome.Mode}");
        text.AppendLine(Culture, $" Host           : {outcome.Environment.Host} ({outcome.Environment.ProcessorCount} CPUs)");
        text.AppendLine(Culture, $" Runtime        : {outcome.Environment.Runtime}");
        text.AppendLine(Culture, $" Alvo           : {outcome.Environment.Target}");
        text.AppendLine(Culture, $" Appenders      : {outcome.Environment.Appenders}");
        text.AppendLine(Culture, $" Duração/braço  : {outcome.Environment.ArmSeconds:0.0} s");
        text.AppendLine();
        text.AppendLine(" Aritmética de demanda (valor de planejamento, não medição):");
        text.AppendLine(Culture, $"   {Demand.AppendsPerNotification} appends por notificação; sustentado {Demand.SustainedAppendsPerSecond}/s; pico {Demand.PeakAppendsPerSecond}/s.");
        text.AppendLine(Culture, $"   Para utilização {Demand.TargetUtilization:0.0} no sustentado a posse precisa ficar abaixo de {Demand.RequiredHoldMs * 1000:0} microssegundos;");
        text.AppendLine(Culture, $"   para o pico, abaixo de {Demand.RequiredPeakHoldMs * 1000:0} microssegundos.");
        text.AppendLine();
    }

    private static void Arms(StringBuilder text, ProbeOutcome outcome)
    {
        text.AppendLine("-- Braços -------------------------------------------------------------");
        text.AppendLine(" braço  volume     n     appends/s  setup p50  espera p50/p99   posse p50/p99    commit p50   janela p99");
        foreach (ArmResult arm in outcome.Arms)
        {
            text.AppendLine(Culture, $" {arm.ArmId,-5}  {arm.Volume,-9:N0}  {arm.Transactions,-5}  {arm.AppendsPerSecond,8:N1}   "
                + $"{arm.Setup.P50,8:0.000}  {arm.Wait.P50,6:0.000}/{arm.Wait.P99,-8:0.000} {arm.Hold.P50,6:0.000}/{arm.Hold.P99,-8:0.000} "
                + $"{arm.Commit.P50,8:0.000}  {arm.Window.P99,10:0.000}");
        }

        text.AppendLine();
        text.AppendLine(" Tudo em milissegundos. Setup = conexão, início de transação e statements de negócio.");
        text.AppendLine(" Posse = trabalho pré-commit sob o lock + commit.");
        text.AppendLine(" Janela = espera + posse, que é o sub-orçamento do aceite.");
        if (outcome.RoundTrip is { } roundTrip)
        {
            text.AppendLine(Culture, $" Ida trivial ao banco nesta rodada: p50 {roundTrip.P50:0.000} ms, "
                + $"p99 {roundTrip.P99:0.000} ms, n={roundTrip.Samples}. É o divisor que normaliza a posse.");
        }

        text.AppendLine(Culture, $" Origem do índice de cauda no braço mitigado: {outcome.TailIndexSource}.");
        text.AppendLine();

        IReadOnlyList<int> inverted = ProbeAnalysis.VolumesWhereControlCostsMore(outcome.Arms);
        if (inverted.Count > 0)
        {
            text.AppendLine(" Leitura contraintuitiva, e não é anomalia: nos volumes "
                + string.Join(", ", inverted.Select(volume => volume.ToString("N0", Culture)))
                + " o braço de");
            text.AppendLine(" controle segura o lock por mais tempo que o braço de tratamento. Sem índice, N");
            text.AppendLine(" appenders em partições distintas fazem N varreduras concorrentes, enquanto os");
            text.AppendLine(" mesmos N na mesma partição serializam e mantêm uma varredura só, quente em");
            text.AppendLine(" cache. A serialização estava protegendo o banco. Consequência de método: o");
            text.AppendLine(" isolamento da contenção só é limpo onde a varredura é barata, e é por isso que");
            text.AppendLine(" ele foi feito no menor volume. Com o índice aplicado, refazer o isolamento no");
            text.AppendLine(" volume alto é barato e confirma a assinatura sem o ruído da varredura.");
            text.AppendLine();
        }

        foreach (ArmResult arm in outcome.Arms)
        {
            text.AppendLine(Culture, $" {arm.ArmId} @ {arm.Volume:N0}: {arm.Question}");
            if (arm.Transactions < 100)
            {
                text.AppendLine(Culture, $"   atenção: {arm.Transactions} amostras, cauda pouco confiável");
            }

            if (arm.Failures > 0)
            {
                text.AppendLine(Culture, $"   appends recusados pelo banco: {arm.Failures} ({arm.FailureDiagnosis})");
            }

            foreach ((var operation, PhaseStatistics stats) in arm.HoldByOperation.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                text.AppendLine(Culture, $"   posse por operação {operation,-20} n={stats.Samples,-5} p50={stats.P50:0.000} p99={stats.P99:0.000}");
            }

            foreach (WaitEventTally tally in arm.WaitEvents.Take(4))
            {
                text.AppendLine(Culture, $"   pg_stat_activity {tally.WaitEventType}/{tally.WaitEvent}: {tally.Samples} amostras");
            }

            text.AppendLine();
        }
    }

    private static void Ratios(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Ratios.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Razão de contenção (A2 sobre A1) -----------------------------------");
        text.AppendLine(" volume     latência p50 A1  latência p50 A2  razão   vazão A1    vazão A2");
        foreach (ContentionRatio ratio in outcome.Ratios)
        {
            text.AppendLine(Culture, $" {ratio.Volume,-9:N0}  {ratio.ControlLatencyP50Ms,14:0.000}  {ratio.TreatmentLatencyP50Ms,15:0.000}  "
                + $"{ratio.LatencyRatio,5:0.00}   {ratio.ControlThroughput,9:N1}  {ratio.TreatmentThroughput,9:N1}");
        }

        text.AppendLine();
    }

    private static void Sensitivity(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Sensitivity.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Sensibilidade do teto por partição à latência de commit ------------");
        text.AppendLine(" O commit é o termo que o Docker local não reproduz: em RDS Multi-AZ ele");
        text.AppendLine(" inclui replicação síncrona ao standby. A tabela mantém o trabalho");
        text.AppendLine(" pré-commit medido e substitui apenas o commit.");
        text.AppendLine(" braço  volume     pré-commit p50   teto com commit de 0,5/1/2/4 ms (appends/s)");
        foreach (SensitivityRow row in outcome.Sensitivity)
        {
            var ceilings = string.Join(
                "  ",
                row.CeilingByCommitLatency.Select(entry => entry.Value.ToString("N0", Culture)));
            text.AppendLine(Culture, $" {row.ArmId,-5}  {row.Volume,-9:N0}  {row.PreCommitP50Ms,14:0.000}   {ceilings}");
        }

        text.AppendLine();
    }

    private static void Sustained(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Sustained.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Célula de taxa oferecida (malha aberta) ----------------------------");
        foreach (SustainedRateResult cell in outcome.Sustained)
        {
            text.AppendLine(Culture, $" volume {cell.Volume:N0}, oferta {cell.OfferedRate}/s por {cell.Seconds:0.0} s: "
                + $"concluídos {cell.Completed:N0}, recusados por falta de vaga {cell.Refused:N0}, "
                + $"taxa alcançada {cell.AchievedRate:N1}/s");
            text.AppendLine(Culture, $"   janela p50 {cell.Window.P50:0.000} ms, p99 {cell.Window.P99:0.000} ms; "
                + $"{(cell.Diverged ? "a fila divergiu: a oferta passou do teto da partição" : "a fila não divergiu")}");
        }

        text.AppendLine();
    }

    private static void TailIndex(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.TailIndex is not { } choice)
        {
            return;
        }

        text.AppendLine("-- Consulta de cauda dentro do lock, por forma de índice --------------");
        foreach (TailPlan plan in choice.Plans)
        {
            text.AppendLine(Culture, $" {plan.Variant} (volume {plan.Volume:N0}): execução {plan.ExecutionMs:0.000} ms");
            foreach (var line in plan.Plan)
            {
                text.AppendLine(Culture, $"   {line}");
            }

            text.AppendLine();
        }

        text.AppendLine(Culture, $" Forma escolhida para o braço de mitigações: {choice.Variant}.");
        text.AppendLine();
    }

    private static void ReadPaths(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.ReadPaths.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Caminhos que percorrem a partição por seq --------------------------");
        text.AppendLine(" Os índices de cauda e de pré-cadeia são parciais, e um índice parcial só");
        text.AppendLine(" atende statement que carrega o predicado dele. Por isso a leitura por");
        text.AppendLine(" faixa foi separada nas duas metades, cada uma com o seu predicado. O");
        text.AppendLine(" termo que precisa ter sumido é a ordenação: ela carregava o texto");
        text.AppendLine(" canônico de cada linha da partição por uma intercalação em disco.");
        text.AppendLine(" volume     execução (ms)   buffers      atendimento  ordenação  caminho");
        foreach (ReadPathPlan path in outcome.ReadPaths)
        {
            text.AppendLine(Culture, $" {path.Volume,-9:N0}  {path.ExecutionMs,13:0.000}  {path.Buffers,10:N0}  "
                + $"{(path.ScansSequentially ? "varredura" : "índice"),11}  "
                + $"{(path.SortsOnDisk ? "em disco" : "nenhuma"),9}  {path.Path}");
        }

        text.AppendLine();
        foreach (ReadPathPlan path in outcome.ReadPaths)
        {
            text.AppendLine(Culture, $" {path.Path} @ {path.Volume:N0}:");
            foreach (var line in path.Plan)
            {
                text.AppendLine(Culture, $"   {line}");
            }

            text.AppendLine();
        }
    }

    private static void Interference(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Interference is not { } interference)
        {
            return;
        }

        text.AppendLine("-- Braço de interferência: purga de processed_messages ----------------");
        text.AppendLine(Culture, $" marcas semeadas {interference.Marks:N0}; a rodada removeu {interference.PurgedRows:N0} em {interference.PurgeSeconds:0.000} s");
        text.AppendLine(Culture, $" A3 quieto      : janela p99 {interference.Quiet.Window.P99:0.000} ms, posse p50 {interference.Quiet.Hold.P50:0.000} ms, {interference.Quiet.AppendsPerSecond:N1} appends/s");
        text.AppendLine(Culture, $" A3 com a purga : janela p99 {interference.WithPurge.Window.P99:0.000} ms, posse p50 {interference.WithPurge.Hold.P50:0.000} ms, {interference.WithPurge.AppendsPerSecond:N1} appends/s");
        text.AppendLine(Culture, $" deslocamento do p99: {interference.WindowP99Shift:+0.000;-0.000;0.000} ms (razão {interference.WindowP99Ratio:0.00})");
        var coverage = interference.WithPurge.ElapsedSeconds > 0
            ? interference.PurgeSeconds / interference.WithPurge.ElapsedSeconds
            : double.NaN;
        text.AppendLine(Culture, $" a rodada da purga cobriu {coverage:P1} da janela do braço. Com cobertura baixa, o");
        text.AppendLine(" deslocamento medido é ruído entre rodadas, não efeito da purga.");
        text.AppendLine();
    }

    private static void Relay(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.RelayPlans.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Plano de execução do relay sobre backlog sintético -----------------");
        text.AppendLine(" O caminho de autenticação tem 300 ms de orçamento entre o outbox e a fila,");
        text.AppendLine(" com o laço de 100 ms do relay por cima, então é o tempo por lote que se lê");
        text.AppendLine(" contra orçamento; o plano diz por que ele é o que é.");
        text.AppendLine();
        text.AppendLine(" braço                          banda          descartadas  varre  ordena  lote p50   lote máx  lotes");
        foreach (RelayPlan plan in outcome.RelayPlans)
        {
            text.AppendLine(Culture, $" {plan.Arm,-29}  {plan.Band} ({plan.BandName,-12})  {plan.RowsRemovedByFilter,11:N0}  "
                + $"{(plan.ScansSequentially ? "sim" : "não"),5}  {(plan.SortsOnDisk ? "sim" : "não"),6}  "
                + $"{plan.BatchP50Ms,8:0.000}  {plan.BatchMaxMs,8:0.000}  {plan.BatchesDrained,5}");
        }

        text.AppendLine();
        text.AppendLine(" Descartadas = linhas que o filtro jogou fora para encher um lote.");
        text.AppendLine(" Lote = reivindicar mais carimbar mais commit, como o relay faz, medido com");
        text.AppendLine(" commit por lote e não com transação descartada, senão a segunda medição");
        text.AppendLine(" leria a mesma cabeça do backlog inteira em cache.");
        text.AppendLine();
        foreach (RelayPlan plan in outcome.RelayPlans)
        {
            text.AppendLine(Culture, $" {plan.Arm}, banda {plan.Band} ({plan.BandName}), backlog pendente {plan.Backlog:N0}, execução {plan.ExecutionMs:0.000} ms");
            foreach (var line in plan.Plan)
            {
                text.AppendLine(Culture, $"   {line}");
            }

            text.AppendLine();
        }
    }

    private static void Verification(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Verification.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Custo da verificação integral da partição corrente -----------------");
        text.AppendLine(" Sem meta fixada: a cadência depende de volume real de produção.");
        text.AppendLine(" Curva medida com o schema como as migrações o deixam, e este cenário lê");
        text.AppendLine(" por faixa de seq como o verificador lê. Leia a direção da curva, nunca o");
        text.AppendLine(" delta entre rodadas: é medição longa dominada por IO em host compartilhado,");
        text.AppendLine(" e dezenas de segundos de diferença entre duas rodadas dizem respeito ao");
        text.AppendLine(" host, não ao schema. Se a forma continuar superlinear, a seção de caminhos");
        text.AppendLine(" por seq acima é que diz por quê.");
        text.AppendLine(" volume     linhas lidas  elos     segundos  s/100k linhas  íntegra  quebras  1ª quebra em seq");
        foreach (VerificationCost cost in outcome.Verification)
        {
            text.AppendLine(Culture, $" {cost.Volume,-9:N0}  {cost.RowsRead,12:N0}  {cost.ChainedRows,7:N0}  {cost.Seconds,8:0.00}  "
                + $"{cost.SecondsPer100K,13:0.00}  {(cost.Intact ? "sim" : "não"),7}  {cost.Breaks,7:N0}  {cost.FirstBrokenSeq,16:N0}");
            if (!cost.Intact)
            {
                text.AppendLine(Culture, $"   bifurcações {cost.Forks:N0}, elos que não fecham com o próprio texto {cost.Relinks:N0}");
                text.AppendLine(Culture, $"   primeira quebra: {cost.FirstDiagnosis}");
            }
        }

        text.AppendLine();
    }

    private static void FallbackLatencies(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.FallbackLatencies.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Rodada do scheduler no caminho de fallback -------------------------");
        text.AppendLine(" O prazo até o SMS de fallback é uma soma, e todo termo dela é orçamento");
        text.AppendLine(" fixo menos este: quanto a rodada leva para achar as tentativas vencidas.");
        text.AppendLine(" É o termo que cresce com a retenção. Os saltos de fila e a chamada ao");
        text.AppendLine(" provedor estão fora desta medição e pertencem ao gate de carga.");
        text.AppendLine();
        text.AppendLine(" statement                       notificações  reivindicadas  varre     p50       p95       p99      máx");
        foreach (FallbackLatency measured in outcome.FallbackLatencies)
        {
            text.AppendLine(Culture,
                $" {measured.Statement,-30}  {measured.Notifications,12:N0}  {measured.Claimed,13:N0}  "
                + $"{(measured.ScansSequentially ? "sim" : "não"),5}  {measured.Round.P50,8:0.000}  "
                + $"{measured.Round.P95,8:0.000}  {measured.Round.P99,8:0.000}  {measured.Round.Max,8:0.000}");
        }

        text.AppendLine();
        foreach (FallbackLatency measured in outcome.FallbackLatencies)
        {
            text.AppendLine(Culture,
                $" {measured.Statement}, {measured.Notifications:N0} notificações, n={measured.Round.Samples}");
            foreach (var line in measured.Plan)
            {
                text.AppendLine(Culture, $"   {line}");
            }

            text.AppendLine();
        }
    }

    private static void WebhookIngestion(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.WebhookIngestion.Count == 0)
        {
            return;
        }

        text.AppendLine("-- Custo de ingestão de um callback de provedor -----------------------");
        text.AppendLine(" O orçamento do desenho é por evento e a rota responde por callback, então");
        text.AppendLine(" o que interessa é como o tempo cresce com o tamanho do lote. As duas");
        text.AppendLine(" formas são a comparação: por evento é o que a produção faz hoje, por lote");
        text.AppendLine(" é a alternativa, e a diferença entre as linhas é o que mudar valeria.");
        text.AppendLine(" Fora desta medição: TLS, verificação de assinatura e pipeline HTTP, que");
        text.AppendLine(" não crescem com o lote.");
        text.AppendLine();
        text.AppendLine(" forma                  eventos   callback p50   callback p95   callback p99   por evento p50   por evento p99");
        foreach (WebhookIngestionCost cost in outcome.WebhookIngestion)
        {
            text.AppendLine(Culture,
                $" {cost.Shape,-21}  {cost.EventsPerCallback,7:N0}  {cost.Callback.P50,13:0.000}  "
                + $"{cost.Callback.P95,13:0.000}  {cost.Callback.P99,13:0.000}  "
                + $"{cost.PerEventP50Ms,15:0.000}  {cost.PerEventP99Ms,15:0.000}");
        }

        text.AppendLine();
        text.AppendLine(" Tempos em milissegundos. O selo do payload roda uma vez por callback, como");
        text.AppendLine(" no handler, então ele aparece na coluna do callback e nunca na por evento.");
        if (outcome.RoundTrip is { } yardstick)
        {
            text.AppendLine();
            text.AppendLine(Culture,
                $" Cada evento custa cinco idas ao banco: begin, três comandos e commit. Nesta");
            text.AppendLine(Culture,
                $" bancada a ida trivial é {yardstick.P50:0.000} ms (p50), então o piso teórico por");
            text.AppendLine(Culture,
                $" evento na forma por evento é cerca de {yardstick.P50 * 5:0.000} ms só de ida e volta.");
            text.AppendLine(" Comparar as duas formas é sempre válido; ler o absoluto como custo do");
            text.AppendLine(" banco não é, e é por isso que o divisor está aqui.");
        }

        text.AppendLine();
    }

    private static void Verdict(StringBuilder text, ProbeOutcome outcome)
    {
        if (outcome.Verdict is not { } verdict)
        {
            return;
        }

        text.AppendLine("-- Escada de acionamento do plano B -----------------------------------");
        text.AppendLine(Culture, $" Regra de capacidade: teto (1/posse p50) precisa alcançar {Demand.RequiredCeiling:N0} appends/s.");
        text.AppendLine(" braço  volume     posse p50   teto (appends/s)  atende");
        foreach (CapacityCheck check in verdict.Capacity)
        {
            text.AppendLine(Culture, $" {check.ArmId,-5}  {check.Volume,-9:N0}  {check.HoldP50Ms,9:0.000}  {check.Ceiling,16:N1}  {(check.Passes ? "sim" : "não")}");
        }

        text.AppendLine();
        text.AppendLine(Culture, $" Sub-orçamento: espera mais posse precisa caber em {Demand.WindowBudgetMs:0} ms no p99.");
        text.AppendLine(" A coluna abaixo é DIRECIONAL e não aprova nada: o p99 desta bancada não é");
        text.AppendLine(" transferível, porque a cauda vem do host e não do append. O sub-orçamento");
        text.AppendLine(" permanece aberto e se decide em infraestrutura representativa.");
        text.AppendLine(" braço  volume     janela p99   n       direção");
        foreach (BudgetCheck check in verdict.Budget)
        {
            text.AppendLine(Culture, $" {check.ArmId,-5}  {check.Volume,-9:N0}  {check.WindowP99Ms,10:0.000}  {check.Samples,-6}  {(check.Passes ? "favorável" : "desfavorável")}");
        }

        text.AppendLine();
        text.AppendLine(Culture, $" Veredito: {verdict.Summary}");
        text.AppendLine(Culture, $" Plano B (sub-cadeias dentro da partição) {(verdict.Triggered ? "dispara" : "não dispara")} pela medição desta rodada.");
        text.AppendLine();
    }
}
