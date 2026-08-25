---
language: pt-BR
---

# Desempenho

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## PRF-001: orçamento de 20 ms declarado como propriedade, sem percentil e sem medição

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `48`
- `evidence`: A linha 48 fixa `resposta 200 em menos de 20 ms` e a linha 131 repete o número como fato operacional, inclusive para justificar o desenho assíncrono e o risco de reenvio pelo provedor. O caminho síncrono real é por evento, não por callback: [`ReceiveProviderWebhook.Handler.cs:71`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Webhooks/ReceiveProviderWebhook.Handler.cs) itera os eventos do callback chamando `writer.RecordAsync`, e cada chamada abre transação própria em [`DeliveryEventWriter.cs:108`](../../../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/DeliveryEventWriter.cs) (marca de dedupe, linha de evidência, append de outbox, commit). O payload do SendGrid é um array percorrido sem teto, e não existe `MaxRequestBodySize`, `RequestSizeLimit` ou limite de lote no revision. O projeto `Platform.PerformanceTests` não tem cenário de webhook: `Scenarios/` contém `ChainReadPathsScenario`, `InterferenceScenario`, `RelayPlanScenario`, `SustainedRateScenario`, `TailQueryPlanScenario` e `VerificationCostScenario`.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: O número é usado como orçamento sem percentil, sem baseline, sem gate e sem limite de lote, então um callback de N eventos custa N transações e o alvo descreve apenas o caso de um evento. Como a própria linha 131 nomeia a consequência de estourar (o provedor reenvia, degradando mais), o modo de falha é auto amplificante na única superfície pública do hub.
- `recommendation`: Declarar o alvo como percentil sobre um perfil de carga e por evento, fixar teto de eventos por callback, e alocar a medição ao gate de carga do §11.6.
- `verification`: Postar em `/webhooks/sendgrid` um lote assinado com 200 eventos rastreados e medir o p95 da resposta. Crescimento linear com N confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`

## PRF-002: índices parciais descritos não existem na forma descrita

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `55`
- `evidence`: A linha 55 afirma `sobre índices parciais WHERE status = 'sent' AND fallback_deadline IS NOT NULL e em release_at`, e a linha 118 repete a forma. O que a migração `20260825010029_AddSchedulerScanState.cs` cria é outra coisa: `ix_notification_attempt_fallback_due` tem `status` como coluna e não como filtro, e o filtro é `fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL AND fallback_requested_at IS NULL`; `ix_notification_release_due` filtra `status = 'deferred'`, não `release_at IS NOT NULL`. A mesma migração cria `ix_notification_attempt_unknown_due` e `ix_notification_attempt_fallback_inflight`, que o documento não menciona e que são exatamente os índices que sustentam o fallback de 60 s da linha 39 e a liberação de pedidos em voo.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: O casamento literal do predicado parcial com o statement é requisito operacional nesta base, e os próprios comentários do código o repetem. Um leitor que use o documento como fonte para revisar ou recriar índices restaura a forma errada e derruba o plano em silêncio; um filtro em `status = 'sent'` não serviria nem à varredura de `unknown` nem à purga de pedidos em voo.
- `recommendation`: Transcrever os quatro predicados vigentes nas linhas 55 e 118, ou remover o literal e citar apenas as colunas.
- `verification`: Comparar cada `HasFilter` de `NotificationAttemptConfiguration` e `NotificationConfiguration` com o texto do documento.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`, `dotnet-specialist`
- `dissent`: O `dotnet-engineer` classificou o achado como `LOW` e na lente de engenharia. A consolidação preserva `MEDIUM` do `dotnet-specialist`, pela consequência de plano de consulta, e atribui a lente de desempenho.

## PRF-003: o prazo de 60 s do veredito inconclusivo substitui o timeout de 30 s do plano

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `39`
- `evidence`: O documento apresenta os dois prazos como camadas independentes: a linha 38 diz que o scheduler detecta o deadline vencido sem `delivered`, e a linha 39 que o attempt `unknown` de fluxo `critical` ou de autenticação por mais de 60 s dispara fallback imediato. No código as varreduras particionam por status e não se sobrepõem: [`OverdueFallbackScan.cs:88`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Scheduling/OverdueFallbackScan.cs) reivindica só `WHERE attempt.status = 'sent'` e a linha 129 só `WHERE attempt.status = 'unknown'` com `status_changed_at < @threshold`, sendo o limite `SchedulerScanOptions.UnknownGrace = 60 s`. O veredito `TransientError` do provedor escreve `unknown` sem avanço reativo de plano (`DispatchMessageProcessor.cs:293` e `:378`; `AttemptDispatchWriter.RecordUnknownAsync` não chama `NotificationPlanOutcome.AdvanceAsync`).
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Um push `critical` cujo provedor estoura o timeout em t0+5 s vai a `unknown`, o `fallback_deadline` de 30 s nunca é lido porque o attempt não está em `sent`, e o fallback só é pedido em t0+65 s. O documento promete 30 s e o sistema entrega 65 s exatamente no cenário de provedor degradado, que é o cenário que motiva o fallback e o que o §11.6 mede.
- `recommendation`: Declarar que o prazo efetivo em veredito inconclusivo é `UnknownGrace` e não o timeout do passo, ou fazer o `unknown` herdar o prazo do passo do plano.
- `verification`: Aceitar uma `critical` com plano de 30 s, forçar `ProviderOutcome.TransientError` no provedor falso e medir o instante do primeiro `fallback.attempt_queued`. Valor perto de 30 s refuta o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## PRF-004: a aritmética do fallback não fecha contra o aceite de 35 s do §11.6

- `severity`: `HIGH`
- `confidence`: `MEDIUM`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `155`
- `evidence`: A linha 155 trata o trade-off como resolvido: `a varredura de 5 s adiciona até 5 s à liberação, contra um menor timeout de fallback de 30 s no plano de critical`. O §11.6 do design de sistema fixa, no cenário de provedor degradado, o aceite de fallback SMS em até 35 s. Somando o que o revision fixa: 30 s de prazo, mais até 5 s de `SchedulerScanOptions.Interval`, mais dois saltos outbox e relay que o §11.2 orça em 300 ms cada, mais 200 ms do estágio Core, mais a chamada Twilio com `TwilioOptions.TimeoutSeconds = 5` (o §11.3 manda 2 s para `critical`). O piso é aproximadamente 35,8 s e o teto passa de 40 s. O documento invoca o §11.6 na linha 160 e não o lista em `Fontes` nem em `Referências`, e o §11.2 não aparece em nenhum dos dois lugares.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: O critério de aceite do gate de carga desta fase é inatingível pela configuração default, e o documento fecha o trade-off sem a conta que o desmentiria. Como nenhum oráculo mede latência de fallback no revision (ver TST-005), a divergência não é detectada por teste.
- `recommendation`: Registrar o orçamento somado no documento e derivar dele o `Interval` do scheduler e o `TimeoutSeconds` do canal SMS, ou renegociar o aceite de 35 s do §11.6.
- `verification`: Rodar o cenário de provedor degradado do §11.6 e medir o p95 do intervalo entre o `queued` do push e a requisição ao provedor de SMS.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`
- `dissent`: A confiança é `MEDIUM` porque o instante zero do aceite de 35 s não está definido no design de sistema. O reviewer o fixou no enfileiramento do attempt de push, que é onde o `fallback_deadline` é carimbado; se a intenção do §11.6 for medir a partir do primeiro erro do provedor, a folga muda.

## PRF-005: a reconciliação está funcional em teste e inerte em escala

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `174`
- `evidence`: A tabela de status dá a entrega como concluída. `DeliveryReconciliationOptions` fixa `Interval = TimeSpan.FromDays(1)` e `BatchSize = 200`, ou seja no máximo 200 attempts por dia, contra as aproximadamente 150 mil notificações diárias que o §11.1 planeja. O conjunto candidato nunca drena: os attempts de push não têm lookup posterior, o que a própria linha 74 admite, o comentário de `DeliveryReconciliationScan` registra que as mesmas linhas voltam a cada rodada pela vida da partição, e a ordenação é do silêncio mais antigo primeiro, então esses registros ocupam as 200 vagas permanentemente. Além disso o predicado de `CandidatesAsync` (`(Status == Sent || Status == Unknown) && ProviderKey != null && (StatusChangedAt ?? CreatedAt) < threshold`) não implica o predicado de nenhum dos quatro índices parciais existentes, e o join com `notification` não carrega a janela `created_at` que os demais statements do módulo trazem para podar partição.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: A reconciliação nunca alcança e-mail nem SMS, que são os dois canais que ela existe para corrigir, e a varredura tende a sequencial sobre toda partição de `notification_attempt` e de `notification`, com ordenação externa sobre expressão. A entrega está provada em teste e não cumpre a função em produção.
- `recommendation`: Excluir do candidato o canal sem lookup e o attempt de notificação concluída, adicionar índice parcial que case o predicado literalmente, e trazer a janela de `created_at` para o join.
- `verification`: `EXPLAIN (ANALYZE)` sobre o SQL que o EF gera para `CandidatesAsync` em base com duas partições populadas. `Index Scan` com poda de partição refuta a parte de plano; a parte de vazão se verifica contando attempts de push elegíveis contra o `BatchSize`.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## PRF-006: a correlação de callback não carrega a chave de partição

- `severity`: `MEDIUM`
- `confidence`: `MEDIUM`
- `lens`: `Performance`
- `file`: `src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Events/DeliveryStateApplier.cs`
- `line`: `365`
- `evidence`: A resolução do attempt usa `candidate.Id == correlation.AttemptId && candidate.NotificationId == correlation.NotificationId`, sem predicado sobre `created_at`. A tabela é particionada mensalmente e a chave primária é `(id, created_at)`, então a busca sonda o índice de toda partição existente. O padrão correto está no mesmo módulo, em `OverdueFallbackScan.cs:86` e no `RetireSql` de `ScanIndexLiabilitySweep`, que trazem `@attemptWindow` exatamente para podar.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Este é o caminho principal de correlação dos dois provedores, percorrido ao menos uma vez por evento de entrega, e o custo cresce linearmente com a retenção de partições. Degrada justamente o handler cujo orçamento a linha 48 fixa em 20 ms.
- `recommendation`: Propagar a janela de criação para a resolução do attempt, como os outros statements do módulo já fazem.
- `verification`: `EXPLAIN` da consulta com doze partições criadas. Poda de partição no plano refuta o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`
