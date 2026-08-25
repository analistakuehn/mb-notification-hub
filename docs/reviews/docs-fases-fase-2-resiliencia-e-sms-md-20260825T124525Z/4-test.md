---
language: pt-BR
---

# Teste

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## TST-001: na revisão sob review a solução não compila, então nada do que o documento chama de validado é verificável

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `203`
- `evidence`: `dotnet build MonteBravo.NotificationHub.sln --nologo -v q` na revisão-fonte termina em `Build FAILED`, `1 Error(s)`, com `DeclareContactPoints.Handler.cs(4,1): error IDE0005: Using directive is unnecessary`. O erro não depende de flag externa: `Directory.Build.props` declara `TreatWarningsAsErrors` e `EnforceCodeStyleInBuild`. `dotnet test tests/Platform.ArchTests/Platform.ArchTests.csproj` falha com o mesmo erro, ou seja nenhum projeto de teste executa. O documento afirma na linha 203 `as doze fatias de código da fase estão concluídas e validadas` e na linha 201 `O job de composição e o arquivamento imutável estão implementados e verdes`. `git log -5` do arquivo mostra que a última alteração é `63aee51 style: padronizar a inferência de tipos na base existente`, posterior a `4d36b15 docs: fechar o status das fatias da fase 2`.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `true`
- `impact`: A afirmação de estado é datada 2026-08-25 e a revisão é do mesmo dia, mas na revisão a suíte é inexecutável. Verde e validado descrevem uma revisão anterior, não esta. Qualquer decisão de go-live tomada com base nesta seção parte de uma prova que ninguém pode reproduzir no `HEAD` da revisão-fonte.
- `recommendation`: Reparar o erro de build e reexecutar a suíte antes de manter a afirmação, ou ancorar a afirmação no commit em que ela foi medida (`4d36b15`) em vez de na data.
- `verification`: `dotnet build MonteBravo.NotificationHub.sln` na revisão-fonte. Qualquer resultado diferente de `Build FAILED` falsifica o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`
- `dissent`: O `dotnet-specialist` registrou em pontos cegos que presumiu que a revisão compila, justamente porque `TreatWarningsAsErrors` está ligado, e não confirmou. A consolidação executou o build e confirmou o achado do `dotnet-architect`.

## TST-002: o item de caos da estratégia de testes não tem artefato e não consta como lacuna

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `150`
- `evidence`: A linha 150 fixa `Caos, herdado da ADR-0008: matar pods durante burst, rebalance sob carga e failover do banco, com zero duplicata ao cliente e zero perda`. `grep -rli` por caos, chaos, failover, rebalance e kill de pod em `tests/` devolve somente `ContactsIngressDedupeTests.cs` e `KafkaIngressDedupeTests.cs`, e nesses dois a única ocorrência é um comentário sobre reentrega por rebalance, num teste de dedupe da fase 1b, sem derrubar processo e sem failover. A seção `Estado em 2026-08-25` enumera os limites honestos de cada critério de saída nas linhas 195, 198 e 201, e não menciona caos em nenhum deles.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `true`
- `impact`: O item mais forte da estratégia, zero duplicata e zero perda sob falha de infraestrutura, é o que sustenta a promessa da ADR-0008 nesta fase, e o leitor conclui que foi exercitado porque a seção de estado se apresenta como o inventário honesto do que está e do que não está provado. Duplicata ao cliente em rebalance é justamente o defeito que a correção 1 da linha 181 existe para impedir.
- `recommendation`: Declarar o caos como pendente, atribuído ao gate de carga do §11.6 ou à unidade I2, e nomear quem o executa.
- `verification`: Apontar o teste, o runbook ou o recibo de execução de caos. A ausência confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`

## TST-003: o portão executável não alcança fluxo de autenticação fora da classe `critical`

- `severity`: `LOW`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `195`
- `evidence`: A linha 195 afirma `o portão executável reprova enquanto existir política critical publicada cujo plano não tenha passo posterior` e declara um único limite: `ele mede plano publicado, não alcance em tempo de execução`. `CriticalPlanWithoutFallbackSource.cs:23` conta `class_policy_version` com `WHERE policy_version.class = @notificationClass` e `jsonb_array_length(...'deliveryPlan') < 2`, e `Program.cs:43` liga o parâmetro em `NotificationClasses.Critical`. O resto do desenho trata `critical` ou fluxo de autenticação como unidade: `OverdueFallbackScan.cs:135` usa `notification.class = 'critical' OR notification.auth_flow`, e `AcceptanceDeliveryTests.cs:80` prova que um fluxo de autenticação nomeia a fila `auth` qualquer que seja a classe.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `true`
- `impact`: Uma política `transactional` que hospede template de finalidade de autenticação, com plano de um passo, passa no portão. O parágrafo apresenta o portão com um limite declarado quando há dois.
- `recommendation`: Acrescentar o segundo limite ao parágrafo, ou estender a fonte do portão às políticas cujas classes hospedam template de autenticação.
- `verification`: Publicar política `transactional` com `deliveryPlan` de um passo mais template de autenticação nessa classe e rodar o portão. Ele passa.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`

## TST-004: a prova do critério de saída é condicional a Docker e o documento a apresenta como incondicional

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `195`
- `evidence`: A linha 195 afirma `O caminho existe de ponta a ponta, num único teste com banco, cache, filas e provedor falso`. O teste é `PushToSmsFallbackTests.cs:51`, `An_accepted_push_with_no_delivery_event_falls_back_to_sms_and_the_callback_closes_it`, decorado com `[RequiresDockerFact]`, cujo atributo em `TemplateManagement/DockerEnvironment.cs:39` atribui `Skip` quando o daemon não responde. Um teste ignorado passa na suíte.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `true`
- `impact`: A prova do critério de saída 1 desaparece em silêncio em qualquer ambiente sem Docker, inclusive em CI mal configurado, sem que a suíte fique vermelha. O documento não diz sob que condição a prova roda.
- `recommendation`: Registrar a precondição de ambiente ao lado da afirmação, e falhar em vez de ignorar quando o ambiente for o de verificação de critério de saída.
- `verification`: `dotnet test` sem Docker e conferir se a suíte fica verde com o cenário ignorado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## TST-005: nenhum oráculo mede latência de fallback

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Test`
- `file`: `tests/Platform.IntegrationTests/Notifications/EndToEnd/PushToSmsFallbackTests.cs`
- `line`: `190`
- `evidence`: O cenário de ponta a ponta roda com `MutableClock` injetado no lugar do `TimeProvider` e chama cada estágio à mão (`RunDispatchPassAsync`, `RunOverdueScanAsync`, relay, Core, dispatcher SMS). O que ele prova é que a composição encadeia; nenhum número de tempo é asserido. Cobertura assimétrica que sustenta PRF-005: existem testes de plano para as três varreduras do scheduler e para a retirada (`SchedulerScanPlanTests.cs`, `ScanIndexLiabilityPlanTests.cs`, ambos com asserções sobre `Index Scan` e `Seq Scan`), e nenhum para a consulta de candidatos da reconciliação. `Platform.PerformanceTests/Scenarios/` não tem cenário de despacho nem de fallback.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Os três achados de tempo desta revisão (PRF-001, PRF-003 e PRF-004) não têm contraprova executável no revision, então uma divergência entre o prazo prometido e o prazo entregue não é detectada por teste. A linha 198 declara o comportamento sob carga como lacuna e não estende a mesma honestidade aos prazos que o documento afirma como resolvidos.
- `recommendation`: Um teste de plano para `CandidatesAsync` no molde dos existentes, e um cenário de medição de latência de fallback no projeto de performance.
- `verification`: Procurar asserção de plano ou de tempo decorrido sobre o caminho de fallback. A ausência confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## Verificado sem achado nesta lente

Os testes que o documento descreve existem e correspondem à descrição. O cenário de ponta a ponta prova o que a linha 195 diz: push aceito sem evento, prazo de 45 s contra timeout de 30 s, `DeadlineRequested` asserido em exatamente 1, SMS único, callback pela URL que o próprio hub entregou, notificação encerrada por webhook assinado; e a validade vencida termina em `expired` com contagem zero no provedor falso. O teste nomeado na linha 160 existe com o nome exato (`PrioritySlotAllocatorTests.cs:17`) e prova o comportamento que o documento afirma, e `OutboxBand` traz os postos citados (`Auth = 0`, `Critical = 1`, `Transactional = 2`, `Operational = 3`), ligados ao posto do consumidor em `CoreWorkerRole.cs:143` e `DispatcherWorkerRole.cs:110`. As duas lacunas declaradas na linha 198 são lacunas reais. Webhooks, supressão, máquina de estados, janela de silêncio, relatório mensal e as duas correções têm cobertura que prova o afirmado.
