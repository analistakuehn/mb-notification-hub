---
language: pt-BR
---

# Engenharia de software

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## ENG-001: a tabela nomeia onze fatias, o texto afirma doze, e a F2-3 não existe no documento

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `203`
- `evidence`: `grep -o "F2-[0-9]*" | sort -u` devolve onze identificadores no documento e doze na decomposição. A conclusão da linha 203 diz `as doze fatias de código da fase estão concluídas e validadas`. A ausente é `F2-3, Pergunta 7 da reconstrução respondível, F2-2, Concluída (commit 9c4cbb4)`, registrada em [`fase-2-decomposicao.md:275`](../../fases/fase-2-decomposicao.md). `git show --stat 9c4cbb4` mostra que a fatia alterou `Integration/V1/NotificationEvidence.cs`, a rota de evidência do Compliance, o §9.5 do design de sistema e cinco arquivos de teste.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `true`
- `impact`: A aritmética do documento não fecha, e a entrega que a F2-3 executou não tem linha na tabela de status nem item em `Escopo por entrega`. Compliance auditando a fase pelo documento não encontra a entrega que fecha uma das oito perguntas do §9.5, e engenharia não consegue reconciliar onze linhas com a afirmação de doze.
- `recommendation`: Acrescentar a entrega da F2-3 ao escopo e à tabela, ou corrigir a conclusão para a contagem que a tabela sustenta, declarando onde a F2-3 é acompanhada.
- `verification`: Contar os identificadores distintos de fatia no documento. Resultado diferente de doze falsifica a frase da linha 203.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`
- `dissent`: O `dotnet-engineer` graduou `MEDIUM` e o `dotnet-specialist` graduou `LOW`, ambos lendo o achado como defeito de contagem. A consolidação preserva o `HIGH` do `dotnet-architect`, que o leu como omissão de inventário: falta a entrega, não só o número.

## ENG-002: a dependência declarada atribui à fase 1b uma capacidade que esta fase produziu

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `100`
- `evidence`: A linha 100 declara como dependência satisfeita `Fase 1b completa, com seus critérios de saída atingidos (§15): ... consulta de auditoria respondendo às 8 perguntas do §9.5`. [`fase-1b-fundacao.md:164`](../../fases/fase-1b-fundacao.md) e a linha 258 do mesmo arquivo dizem o oposto: `responde a sete perguntas do §9.5 e declara a oitava como lacuna`, com a nota acrescentada depois `Lacuna fechada na fase 2`. `git blame -L 100,100` aponta `e57f7db`, a criação do documento, nunca corrigida.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Quem checa as pré-condições antes do kickoff valida uma dependência que não existia, e a fatia que de fato a produziu (a F2-3 de ENG-001) não aparece como entrega desta fase. As duas omissões se reforçam: a capacidade é creditada à fase anterior e a fatia que a construiu é invisível.
- `recommendation`: Corrigir a linha 100 para sete perguntas com a oitava fechada nesta fase, e ligar a correção à linha de entrega da F2-3.
- `verification`: Comparar a linha 100 com as linhas 164 e 258 da fase 1b.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`

## ENG-003: o corpo do documento ainda descreve a semântica anterior à segunda correção

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `40`
- `evidence`: A linha 40 afirma `Fan-out de push: o fallback só dispara se todos os attempts de push da notificação falharem (§4.3)`, e a linha 213 afirma `delivered de push significa aceito pelo FCM; o fallback de 30 s compensa para critical`. A linha 182, que registra a própria correção, fixa o contrário: `A aceitação passou a declarar entrega somente no passo sem prazo`. O código confirma a correção em `DispatchMessageProcessor.cs:276` (`DeliversOnAcceptance` exige `attempt.FallbackDeadline is null`). A regra de irmãos vive somente no gatilho reativo (`NotificationPlanOutcome.IsStepExhaustedAsync`) e o gatilho por prazo não tem condição de irmão nenhuma (`OverdueFallbackScan.cs:80`). O design de sistema escopa igual: a linha 468 traz a forma incondicional e a linha 559 a corrige nomeando os dois produtores; o documento da fase copiou a primeira.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Lida literalmente, a linha 40 torna o critério de saída inalcançável, porque um push aceito pelo FCM nunca falha, e é justamente essa a razão da correção 2. É a leitura que uma implementação futura pode restaurar como portão de irmãos no caminho por prazo, matando o fallback em silêncio. A linha 213 afirma o oposto do que a linha 182 fixa.
- `recommendation`: Qualificar a linha 40 como propriedade do gatilho reativo e reescrever a linha 213 com a semântica pós correção. A correção 1 também não aparece no corpo da seção de fallback, só no título da linha da tabela de status.
- `verification`: Rodar `An_accepted_push_with_no_delivery_event_falls_back_to_sms_and_the_callback_closes_it`. Nenhuma tentativa de push está em `failed` quando o SMS sai, o que falsifica a linha 40 como enunciado universal.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`

## ENG-004: a varredura por prazo alcança dois estados e o documento promete os demais

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `32`
- `evidence`: A linha 32 afirma que com o circuito aberto `a mensagem volta à fila com visibilidade estendida e o tracker aciona fallback de canal se o plano permitir`, e a linha 127 repete a promessa para o kill switch. [`OverdueFallbackScan.cs:88`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Scheduling/OverdueFallbackScan.cs) filtra `status = 'sent'` e a linha 129 filtra `status = 'unknown'`; `grep` por `status = 'queued'` em `Features/DeliveryTracking/` devolve zero. `AttemptDispatchWriter.RevertToQueuedAsync` devolve a tentativa a `queued` preservando o `fallback_deadline` e anulando o `ProviderKey`; throttle e circuito aberto viram `DispatchVerdict.Requeue` (`DispatchMessageProcessor.cs:290`) e o kill switch vira `MessageDisposition.Postponed`, sem avanço de plano. A reconciliação não cobre a lacuna: `DeliveryReconciliationScan` exige `sent` ou `unknown` e ainda `ProviderKey != null`, que o requeue acabou de anular. Uma linha presa em `sending` após queda de processo tem o mesmo destino, porque `TryClaimAsync` só reivindica a partir de `queued`.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: No cenário exato que essas frases descrevem, a tentativa fica em `queued` com prazo vencido e nenhuma varredura a enxerga. Não existe gatilho de fallback para ela. Se a indisponibilidade do provedor durar mais que o TTL, o ponto de decisão do dispatcher encerra a notificação sem nunca tentar o segundo canal, o que é a negação direta do critério de saída 1.
- `recommendation`: Escolher explicitamente entre estender a varredura aos estados não terminais que carregam prazo e registrar o limite no corpo e na tabela de riscos. Hoje o documento afirma a cobertura mais larga sem que ela exista.
- `verification`: Semear uma tentativa `critical` de push em `queued`, com `fallback_deadline` vencido, `plan_advanced_at` e `fallback_requested_at` nulos e `notification.status = 'dispatched'`, e rodar `OverdueFallbackScan.RunAsync`. `DeadlineRequested = 1` sustenta o documento; `0` confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`

## ENG-005: a supressão por token FCM não passa pelo ledger que o documento promete

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `60`
- `evidence`: A linha 60 declara `Requisito: supressão automática por hard bounce, número inválido e token FCM UNREGISTERED (RF-10, §2.1)` e três linhas abaixo `Reversível e auditada: suppression.added (automática ou manual) e suppression.removed, com ator registrado; supressão manual é atribuição de Platform Admin com PIM`. No código `UNREGISTERED` é invalidação de token de dispositivo: `DispatchMessageProcessor.cs:99` traz `TokenInvalidationCodes = ["UNREGISTERED", "INVALID_ARGUMENT"]`, e o único chamador de `ISuppressionLedger.ReportDeliveryFeedbackAsync` no repositório é `DeliveryStateApplier.cs:440`, no caminho de feedback assíncrono.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Das três fontes que o documento lista no mesmo regime, uma nunca produz `suppression.added` nem `suppression.removed` e não é revertida pelo caminho de Platform Admin com PIM. A trilha que o rito mensal revisa não conterá as supressões de push, e a reversibilidade uniforme não vale para esse canal.
- `recommendation`: Separar no documento a invalidação de token de push da supressão de contato, com a trilha e o caminho de reversão de cada uma, ou justificar por que a fonte FCM fica fora do ledger.
- `verification`: Provocar um `UNREGISTERED` no despacho de push e consultar `contactconsent.suppression`. Nenhuma linha nova confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## ENG-006: a seção de honestidade do relatório mensal declara duas lacunas onde o código tem três

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `201`
- `evidence`: A linha 201 afirma `as seções de fila morta e de falhas de provedor permanecem ausentes do relatório até existir a fonte de métricas operacionais, que pertence à unidade I2`. O código omite três seções, nomeadas em `MonthlyEvidenceReport.cs:270` como `[DeadLetterQueues, ProviderFailures, PrivilegedAccessActivations]`, e `MonthlyEvidenceComposition.cs:137` confirma os nulos. A terceira é a `ativações de PIM` que a linha 86 promete no conteúdo do relatório, e o motivo registrado em `MonthlyEvidenceReport.cs:83` não é métrica pendente: a elevação acontece no provedor de identidade, fora deste hub, então nenhuma fonte aqui pode afirmá-la.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `true`
- `impact`: O critério de saída 3 é revisado por Compliance com a expectativa de que só a unidade I2 falta. Uma lacuna que a I2 não resolve, porque é estrutural, fica invisível na seção que existe justamente para inventariar o que não está provado.
- `recommendation`: Listar a terceira omissão e separar as causas: duas dependem de fonte de métricas, uma depende de integração com o provedor de identidade.
- `verification`: Gerar o relatório de um mês e conferir as chaves ausentes contra as três constantes de `UnsourcedReportSections`.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## ENG-007: o contrato de retry herdado do §8 não é expressável na configuração entregue

- `severity`: `MEDIUM`
- `confidence`: `MEDIUM`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `33`
- `evidence`: A linha 33 afirma `As filas novas herdam o contrato de confiabilidade do §8: retry com backoff (critical: até 3 em 60 s)`. No revision o backoff é único por papel, não por classe: `SqsConsumerOptions` liga a uma seção única (`Platform:Messaging:Consumer`) e dá `BackoffBaseSeconds = 5` e `BackoffMaxSeconds = 900`; `SqsBackoff` computa `base * 2^(tentativa-1)` com jitter, ou seja 5, 10, 20 e 40 segundos, que já ultrapassa 60 s na quarta recepção. O teto de três recepções vive no `maxReceiveCount` da redrive policy do SQS, e `grep` por `maxReceiveCount` e `RedrivePolicy` em `src` e `infra` não retorna nada.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Os números que a linha 33 apresenta como herdados não existem em lugar nenhum do repositório, e o critério de saída 2 depende de um teto que ninguém impõe. O TTL rígido por ponto de decisão segura o resultado, mas por outro mecanismo, então o documento credita a garantia à camada errada.
- `recommendation`: Declarar que o contrato do §8 é dependência de infraestrutura não entregue nesta fase, ou tornar o backoff configurável por fila e nomear onde o `maxReceiveCount` é definido.
- `verification`: Apontar o arquivo que define o `maxReceiveCount` das filas `dispatch-sms-*`. A ausência confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`
- `dissent`: A confiança é `MEDIUM` porque não há infraestrutura versionada neste revision. Se as definições de fila existirem fora deste repositório, o achado se reduz a um problema de referência e não de lacuna.
