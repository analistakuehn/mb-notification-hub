---
language: pt-BR
---

# Fase 2: decomposição em fatias

| Campo | Valor |
|---|---|
| **Tipo** | Decomposição de fase (implementação) |
| **Status** | ACCEPTED |
| **Data** | 2026-08-24 |
| **Dono** | Arquitetura (dotnet-architect) |
| **Público** | Engenharia do hub (execução das fatias); Arquitetura e Compliance como leitores |
| **Fontes** | [Fase 2: resiliência e SMS](fase-2-resiliencia-e-sms.md); [Design de sistema](../notification-hub-system-design.md) (§4.2, §4.3, §5.1, §5.2, §8, §9.5, §9.6, §11.3); [ADR-0008](../ADR-0008-at-least-once-com-idempotencia.md); [ADR-0011](../ADR-0011-politica-como-configuracao-de-classe.md); `AGENTS.md` dos módulos; estado do repositório em 2026-08-24, HEAD `eb6a1e1` |

Este documento fecha as sete decisões de fronteira que o design técnico da fase 2 deixou explicitamente para o kickoff e decompõe a fase em fatias numeradas de implementação. Ele não cria requisito novo: cada decisão é ancorada no design de sistema, nas ADRs vigentes, nas regras de módulo dos `AGENTS.md` ou em fato observado no código, citado por `arquivo:linha`. Onde o design e o código divergem, a divergência é registrada como achado, com a correção atribuída a uma fatia.

Convenção de evidência: `§` aponta para o design de sistema; fato de código cita `arquivo:linha` na revisão de 2026-08-24; decisão sem evidência de código é declarada como decisão, não como fato.

## 1. Escopo desta decisão

Fixa: fronteira do Delivery Tracking, morada das duas metades do webhook, morada e contrato do ledger de supressão, papel de worker do scheduler, morada do relatório mensal, contrato da reconciliação por canal e a convivência dos dois gatilhos de fallback. Fixa também a ordem das fatias, o conjunto de escrita de cada uma e o critério de aceite verificável.

Não fixa: cronograma, alocação de pessoas, forma final do sender BR, contratação do add-on Email Activity do SendGrid e o conteúdo declarativo do Terraform da fase, que forma a unidade bloqueante I2 (seção 5.4).

## 2. Decisões de fronteira

### 2.1 D1: o Delivery Tracking permanece no módulo Notifications

**Decisão.** O tracker não vira módulo. Ele nasce como a slice `src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/`, com quatro sub-slices: `Webhooks/` (rota, autenticação por assinatura, deduplicação e persistência da evidência), `Events/` (aplicação assíncrona da máquina de estados e relato de supressão), `Scheduling/` (varredura de prazo e de liberação) e `Reconciliation/` (correção por consulta ao provedor).

**Evidência procurada, e o que ela mostra.** O documento da fase manda extrair somente com evidência concreta de atrito de fronteira. A procura encontrou o contrário, atrito de extração:

- O tracker escreve transição de `notification_attempt` e lê `notification`. Essas tabelas pertencem a Notifications (`Modules/Notifications/AGENTS.md`, "Estado sob responsabilidade"), e o módulo Dispatch declara explicitamente que estado de tentativa não vive nele (`Modules/Dispatch/AGENTS.md`, seção Boundary). Extrair exigiria publicar um contrato de escrita de estado de tentativa, que é a invariante mais protegida do módulo.
- O avanço de plano precisa ser único por passo, qualquer que seja o gatilho, e o gatilho reativo já vive dentro da transação do veredito do dispatcher (`Infrastructure/Persistence/AttemptDispatchWriter.cs:418-437`). Um tracker em outro módulo colocaria os dois produtores do mesmo `fallback.requested` em lados opostos de uma fronteira, transformando um `UPDATE` condicional numa coordenação distribuída.
- A evidência de entrega é lida junto do estado da notificação pela superfície de auditoria, que hoje declara a lacuna da pergunta 7 (§9.5, errata de 2026-08-23) e a preenche por `Notifications.Integration.V1`.
- O tracker usa cifra de envelope, outbox e append de auditoria, que já são dependências do módulo.

**O que a extração compraria, e por que não é preciso extrair para comprar.** O ganho real seria unidade de implantação e de escala própria. Essa unidade já existe sem quebrar o módulo: o papel de worker é a fronteira de implantação neste repositório (`src/Platform.Worker/WorkerRoleCatalog.cs`), e a D4 cria um papel dedicado.

**Consequência aceita.** O módulo Notifications cresce mais uma slice e mais um papel. O gatilho de revisão fica nomeado: se a evidência de entrega passar a ter dono operacional distinto, com ciclo de release próprio e consumidores fora do hub, a extração volta à mesa com o contrato de escrita de tentativa como primeiro item.

### 2.2 D2: as duas metades do webhook

**Decisão.** A metade de provedor vive no Dispatch, atrás de contrato publicado; a metade de correlação, persistência e efeito vive em Notifications, dona da rota.

| Metade | Onde | Conteúdo |
|---|---|---|
| Conhecimento de provedor | `Modules/Dispatch/Integration/V1/` e `Modules/Dispatch/Infrastructure/Webhooks/` | verificação de `X-Twilio-Signature` (HMAC) e da assinatura ECDSA do Event Webhook, janela de timestamp, allowlist de origem, extração do `provider_event_id`, tradução do vocabulário do provedor para evento canônico e classificação do sinal de supressão |
| Conhecimento de notificação | `Modules/Notifications/Features/DeliveryTracking/Webhooks/` e `.../Events/` | rota, esquema de autenticação por assinatura, deduplicação por `(provider, provider_event_id)`, gravação da evidência, enfileiramento, correlação com o attempt e máquina de estados |

**Contrato novo em `Dispatch/Integration/V1`** (fatia F2-1):

```csharp
public sealed record ProviderWebhookRequest(
    string ProviderKey,
    string RequestUrl,
    IReadOnlyDictionary<string, string> Headers,
    string? RemoteIpAddress,
    ReadOnlyMemory<byte> Body);

public sealed record VerifiedProviderWebhook(
    string ProviderKey,
    DateTimeOffset VerifiedAt,
    ReadOnlyMemory<byte> Body);

public enum DeliveryFeedbackKind { Sent, Delivered, Read, Failed, Bounced }

public enum SuppressionSignal { None, HardBounce, InvalidDestination }

public sealed record ProviderDeliveryEvent(
    string ProviderKey,
    string ProviderEventId,
    DeliveryFeedbackKind Kind,
    DateTimeOffset OccurredAt,
    string? ProviderMessageId,
    DispatchCorrelation? Correlation,
    string? ErrorCode,
    SuppressionSignal Signal);

public interface IProviderWebhookInterpreter
{
    string ProviderKey { get; }

    Result<VerifiedProviderWebhook> Verify(ProviderWebhookRequest request);

    Result<IReadOnlyList<ProviderDeliveryEvent>> Interpret(VerifiedProviderWebhook webhook);
}

public interface IProviderWebhookInterpreterResolver
{
    Result<IProviderWebhookInterpreter> Resolve(string providerKey);
}
```

`Verify` e `Interpret` são membros separados porque a autenticação acontece antes do endpoint e a interpretação dentro dele. A recusa é dado, nunca exceção, com códigos estáveis (`signature-invalid`, `timestamp-out-of-window`, `origin-not-allowed`, `payload-unreadable`, `provider-unknown`), porque `origin-not-allowed` precisa gerar alarme de segurança próprio (§10.2 A5) e assinatura inválida não.

**A rota é autenticada, não anônima.** O teste `State_changing_endpoints_must_declare_authorization_and_rate_limiting` (`tests/Platform.SecurityArchTests/SecurityArchitectureTests.cs:37-47`) exige `RequireAuthorization` e `RequireRateLimiting` na mesma instrução fluente de todo `MapPost`, e a fallback policy do host já exige usuário autenticado (`src/Platform.Api/Program.cs:29-32`). Em vez de abrir carve-out, a assinatura do provedor entra como esquema de autenticação (`ProviderSignature`): o handler do esquema chama `Verify`, publica o principal do provedor e guarda o `VerifiedProviderWebhook` para o endpoint, que declara `RequireAuthorization` e `RequireRateLimiting` com políticas nomeadas próprias. O teste de segurança fica verde sem exceção, o rate limit particiona por provedor e o corpo é verificado uma única vez.

**Rejeitado.** Rota anônima com verificação dentro do handler: exigiria carve-out no teste de segurança vigente e trocaria uma fronteira declarada por uma convenção.

### 2.3 D3: o ledger de supressão vive no ContactConsent

**Decisão.** Tabelas `contactconsent.suppression` e `contactconsent.suppression_signal`, propriedade do módulo ContactConsent. O modelo do §6 já ancora `SUPPRESSION` em `CONTACT_POINT`, e o ponto de extensão do módulo já nomeia o retorno (`Modules/ContactConsent/Integration/V1/IRecipientDirectory.cs:28-30`; `Modules/ContactConsent/AGENTS.md`, "Extension points").

**Quem escreve.** O evento de webhook chega em Notifications; o efeito é escrito por ContactConsent. O consumidor de eventos de entrega, depois de confirmar a transição do attempt, relata o sinal pelo contrato publicado, no mesmo regime já usado para o token de dispositivo, que é relato best effort e idempotente do lado responsável (`Modules/Notifications/AGENTS.md`, slice de dispatch; `Modules/ContactConsent/AGENTS.md`, `IDeviceTokenLifecycle`). ContactConsent grava sinal, supressão, `audit_event`, invalidação de cache e evento de saída na sua própria transação, conforme a invariante transacional do módulo.

**Contrato de escrita** (`ContactConsent/Integration/V1/`, fatia F2-6):

```csharp
public enum SuppressionOutcome { SignalRecorded, ContactSuppressed, AlreadyApplied }

public sealed record SuppressionReport(
    string RecipientId,
    Guid ContactPointId,
    string Channel,
    string Reason,
    Guid SourceEventId,
    DateTimeOffset ObservedAt);

public interface ISuppressionLedger
{
    Task<Result<SuppressionOutcome>> ReportDeliveryFeedbackAsync(
        SuppressionReport report,
        CancellationToken cancellationToken);
}
```

`SourceEventId` é o id da linha de `delivery_event` que originou o relato e carrega chave única no lado do ledger, de modo que a reentrega da mensagem interna é no-op declarativo com trilha própria. A regra de acumulação (e-mail suprime na primeira ocorrência definitiva, SMS somente na segunda em sete dias, §10.2 A5) fica dentro do ContactConsent, porque só ele tem o histórico de sinais e exportá-lo seria exportar dado de contato.

**Contrato de leitura.** A supressão entra como membro novo do snapshot já publicado, e não como superfície V2:

```csharp
public sealed record SuppressionState(
    Guid ContactPointId,
    string Channel,
    string Reason,
    DateTimeOffset SuppressedAt,
    DateTimeOffset? Until);

// RecipientSnapshot ganha:
public required IReadOnlyList<SuppressionState> Suppressions { get; init; }
```

Razão: o estágio Resolve já carrega o snapshot uma vez por notificação (`Features/Pipeline/Stages/ResolveStage.cs`), e uma leitura separada acrescentaria ida ao banco no caminho quente e permitiria decidir sobre um estado diferente do que foi resolvido. A superfície de escrita manual (`Platform.Admin` com PIM, §9.1 e §9.3) é rota REST do próprio ContactConsent, não contrato publicado, porque nenhum módulo irmão a chama.

**Armadilha registrada.** O snapshot é cacheado cifrado no Redis do módulo com TTL de 24 h (`Modules/ContactConsent/AGENTS.md`, "Snapshot cache"). Sem invalidação e sem trocar a versão da chave de cache na mesma fatia, um contato recém-suprimido continuaria elegível por até 24 h, e a entrada antiga desserializaria sem o membro novo.

### 2.4 D4: papel de worker `delivery-tracker`

**Decisão.** Papel novo `delivery-tracker`, de propriedade do módulo Notifications, hospedando o consumidor de eventos de entrega e o scheduler. Hoje existem oito papéis: `outbox-relay`, `core`, `dispatcher`, `kafka-ingress`, `notifications-maintenance`, `contact-consent`, `contacts-ingress` e `audit-maintenance` (`src/Platform.Worker/WorkerRoleCatalog.cs:19-27` e os arquivos `*WorkerRole.cs` dos módulos).

**Por que não `notifications-maintenance`.** O precedente do próprio repositório separa papéis por sinal de escala e por consequência de rodar em várias réplicas (`Modules/Notifications/NotificationsMaintenanceWorkerRole.cs:16-24`; `Modules/Audit/AuditMaintenanceWorkerRole.cs:17-22`). Três razões concretas:

1. **Número de réplicas oposto.** O papel de manutenção reescreve texto cifrado de linhas governadas e roda como singleton. O scheduler precisa de mais de uma réplica: se ele para, ninguém dispara fallback por prazo nem libera `release_at`, e o sintoma é entrega parada em silêncio. A varredura foi desenhada para claim concorrente, então várias réplicas são seguras; o mesmo não vale para o backfill de conteúdo.
2. **Latência é contrato.** A varredura de 5 s entra no orçamento do OTP (§11.2) contra um timeout de fallback de 30 s. Compartilhar processo com um backfill em lote coloca uma transação longa na frente do caminho crítico.
3. **Raio de falha.** Uma falha do backfill que derrubasse o processo derrubaria junto o gatilho de fallback de `critical`, que é justamente o critério de saída da fase.

**Por que não é papel de módulo novo.** Papel é unidade de implantação; módulo é unidade de fronteira. A D1 mantém o módulo, e o papel resolve escala e implantação sem tocar na fronteira.

O releaser de holds do kill switch continua em `notifications-maintenance` (`Infrastructure/KillSwitch/KillSwitchHoldReleaseService.cs:7-12`), porque só trabalha durante uma parada de emergência, e não em regime.

### 2.5 D5: o relatório mensal é composto pelo Compliance e arquivado pelo Audit

**Decisão em duas partes.** O job de composição vive no módulo Compliance; a escrita no bucket WORM entra como membro novo de `Audit.Integration.V1`. O job roda no papel `audit-maintenance`, que já é singleton em cadência de lote.

**Por que não dentro do Audit.** A primeira leitura desta decisão colocava o job no Audit, dono do `IWormObjectStore` (`Modules/Audit/Infrastructure/Worm/IWormObjectStore.cs`) e único lugar onde jobs de trilha rodam. O problema é a direção da dependência: todo módulo depende de `Audit.Integration.V1` para o append transacional, e um Audit que também lesse Notifications e TemplateManagement fecharia ciclo entre contextos. Esse é exatamente o motivo pelo qual o módulo Compliance existe e é folha do grafo (`Modules/Compliance/AGENTS.md`, Boundary). O relatório mensal é composição de evidência, que é a única coisa que o Compliance faz.

**Como ele lê dado de outros módulos.** Só por `Integration/V1`, como já faz hoje. Mudanças de catálogo, política e configuração com aprovadores, ativações de acesso privilegiado e resultado das verificações de cadeia saem da trilha e da tabela de aprovação, ambas do Audit. A metade que só o dono sabe agregar entra por contrato novo:

```csharp
public interface INotificationOutcomeReport
{
    Task<Result<NotificationOutcomeSummary>> SummarizeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);
}
```

`NotificationOutcomeSummary` carrega volumes por classe e canal, rejeições por motivo e contagens de entrega, bounce e expiração, agregados dentro do módulo dono, sobre o contexto somente leitura.

**Contrato de arquivamento** (`Audit/Integration/V1/`):

```csharp
public interface IEvidenceArchive
{
    Task<Result<ArchivedEvidence>> ArchiveAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);
}
```

A semântica de imutabilidade, retenção e verificação continua dentro do Audit; o Compliance entrega bytes e recebe o recibo. O Compliance continua sem `DbContext`, sem migração e sem schema, e o teste `Bounded_contexts_must_not_depend_on_each_other` (`tests/Platform.ArchTests/ArchitectureTests.cs:64-91`) reprova qualquer atalho.

### 2.6 D6: reconciliação, contrato de consulta no Dispatch e job em Notifications

**Decisão.** O Dispatch publica a consulta ao provedor; Notifications executa o job e corrige o attempt, pelo mesmo aplicador que o webhook usa.

```csharp
public sealed record ProviderDeliveryQuery(
    DispatchCorrelation Correlation,
    string? ProviderMessageId,
    DeliveryTarget? Target,
    DateTimeOffset SentAt);

public interface IProviderDeliveryLookup
{
    string ProviderKey { get; }

    Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> LookupAsync(
        ProviderDeliveryQuery query,
        CancellationToken cancellationToken);
}

public interface IProviderDeliveryLookupResolver
{
    Result<IProviderDeliveryLookup> Resolve(string providerKey);
}
```

A consulta devolve o **mesmo** `ProviderDeliveryEvent` do webhook, de propósito: um único vocabulário e um único aplicador de estado, então webhook e reconciliação não podem divergir na máquina de estados. `Target` é opcional porque a Twilio não busca por metadado customizado e correlaciona por destino e janela temporal (§8); o valor sai de `RevealContactValueAsync` no instante do job, transitório, e nunca é persistido. Provedor sem consulta posterior (FCM) simplesmente não registra implementação, e o resolver recusa, o que deixa o attempt em `unknown` com registro, exatamente como o §8 descreve.

O job roda no papel `notifications-maintenance`: é lote diário, correção de retaguarda, singleton, e a regra do papel já é trabalho que não pode depender de tráfego para rodar.

### 2.7 D7: os dois gatilhos de fallback, e a idempotência que falta

**Decisão.** Os dois caminhos continuam existindo, e a unicidade passa a ser garantida por claim de estado no banco, não por deduplicação de mensagem.

**A idempotência existente não basta.** Fato: o handler de fallback marca a deduplicação com `DedupeMessageId(envelope.MessageId, notification.Id)` (`Features/Fallback/FallbackRequestHandler.cs:221` e `:296`). O gatilho reativo e o gatilho por prazo são duas linhas distintas de outbox, com `messageId` distintos, e as duas marcas passam. O resultado seriam dois attempts do passo seguinte, ou seja, dois SMS ao cliente, que é exatamente o que a ADR-0008 proíbe.

**Correção fixada.** Coluna `notification_attempt.plan_advanced_at` e claim condicional por passo, dentro da transação que enfileira o próximo attempt:

```text
UPDATE notifications.notification_attempt
   SET plan_advanced_at = @now
 WHERE notification_id = @notificationId
   AND channel = @failedChannel
   AND plan_advanced_at IS NULL
```

Zero linhas afetadas significa que o passo já avançou, e o handler devolve `Duplicate`. O claim é por passo, e não por attempt, porque o fan-out de push cria irmãos com o mesmo prazo absoluto (`Infrastructure/Persistence/AttemptDispatchWriter.cs:108-157`) e dois irmãos vencidos disputariam o mesmo avanço. É o mesmo idioma de lock otimista que o módulo já usa em `queued` para `sending`.

**Segundo achado, que bloqueia a fase inteira se não for tratado.** Hoje, o push aceito pelo FCM leva a **notificação** a `delivered` (`Infrastructure/Persistence/AttemptDispatchWriter.cs:246`, com `deliveredOnAcceptance: context.IsPush` em `Features/Dispatching/DispatchMessageProcessor.cs:250`), e o handler de fallback trata qualquer estado diferente de `dispatched` como duplicata (`Features/Fallback/FallbackRequestHandler.cs:79-86`). Consequência: o cenário do §5.1, push aceito e sem confirmação em 30 s, seria descartado em silêncio, e o critério de saída de 100 % de `critical` com fallback seria inatingível.

**Correção fixada.** A aceitação do push só declara a notificação `delivered` quando o attempt não tem prazo de fallback, isto é, quando ele é o último passo do plano. `fallback_deadline` não nulo equivale a existir passo posterior, porque o prazo deriva do `Timeout` do passo e o último passo não tem timeout (`Domain/NotificationAttempt.cs:125-126`; `Features/Pipeline/Stages/RouteStage.cs:39`). Com passo posterior, a notificação permanece `dispatched` até haver confirmação real ou até o plano concluir. Consequência observável, que precisa viajar com a mudança: `araia.notification.delivered.v1` deixa de ser emitido na aceitação do push para planos com passo posterior. Isso muda contrato de saída, então a fatia F2-4 registra ADR curta e atualiza o guia de integração do produtor.

**Terceiro achado.** `fallback.requested` é roteado para `core-{classe}` (`Infrastructure/Persistence/DispatchMessages.cs:49`), enquanto o §4.2 e o §5.1 exigem `core-auth` quando o template tem finalidade de autenticação, que é o que o destino de dispatch já faz (`Features/Pipeline/Stages/RouteStage.cs:49-52`). Como a banda de topo do relay é decidida por destino (`Infrastructure/Messaging/Migrations/20260824083644_AddOutboxPriorityBand.cs:42`), o fallback de um OTP drena hoje na banda `critical` e não na banda `auth`. Correção na F2-4, com o sinal de fluxo de autenticação materializado em `notification.auth_flow` para que nenhum dos dois produtores precise consultar o catálogo no caminho quente.

## 3. Contratos novos e alterados, consolidado

| Contrato | Módulo | Fatia | Natureza |
|---|---|---|---|
| `ProviderWebhookRequest`, `VerifiedProviderWebhook`, `ProviderDeliveryEvent`, `DeliveryFeedbackKind`, `SuppressionSignal`, `IProviderWebhookInterpreter`, `IProviderWebhookInterpreterResolver` | Dispatch | F2-1 | novo |
| `NotificationAttemptEvidence.DeliveredAt` e `.DeliveryEvents`, `DeliveryEventEvidence` | Notifications | F2-3 | aditivo |
| `ISuppressionLedger`, `SuppressionReport`, `SuppressionOutcome`, `SuppressionState`, `RecipientSnapshot.Suppressions` | ContactConsent | F2-6 | novo e aditivo |
| `DispatchRequest.Validity` | Dispatch | F2-7 | aditivo, membro opcional |
| `NotificationRejectionReasons` ganha o motivo de link em SMS de autenticação e o motivo de canal suprimido | Notifications | F2-6, F2-7 | aditivo |
| `IProviderDeliveryLookup`, `IProviderDeliveryLookupResolver`, `ProviderDeliveryQuery` | Dispatch | F2-9 | novo |
| `INotificationOutcomeReport`, `NotificationOutcomeSummary` | Notifications | F2-10 | novo |
| `IEvidenceArchive`, `ArchivedEvidence` | Audit | F2-10 | novo |

Mensagens internas novas: `delivery.event_received`, payload `{ deliveryEventId }`, destino `delivery-events`. Nenhum conteúdo trafega, conforme o claim check do §4.2.

## 4. Ordem, dependências e paralelismo

Status observado no repositório na revisão de 2026-08-24:

| Fatia | Entrega | Depende de | Status em 2026-08-24 |
|---|---|---|---|
| F2-1 | Contratos de feedback de provedor no Dispatch | nenhuma | Concluída (commit `7af6e32`) |
| F2-2 | Ingestão de webhooks e evidência de entrega | F2-1 | Concluída (commit `7af6e32`) |
| F2-3 | Pergunta 7 da reconstrução respondível | F2-2 | Pendente |
| F2-4 | Convivência dos dois gatilhos de fallback | F2-2 | Concluída (commit `7af6e32`) |
| F2-5 | Scheduler DB-backed no papel `delivery-tracker` | F2-4 | Concluída (commit `7af6e32`) |
| F2-6 | Supressão automática, reversível e auditada | F2-2 | Concluída (commit `47ab335`) |
| F2-7 | Adapter SMS completo | F2-1, F2-2 | Concluída (commit `b2a885e`), com o pool de sender por aplicação transferido para a F2-8 |
| F2-8 | Rate limit por provedor e kill switch automático de canal | F2-7 | Concluída, ampliada com o pool de sender por aplicação e o motivo de segurança no fallback |
| F2-9 | Reconciliação por canal | F2-2, F2-5, F2-7 | Pendente |
| F2-10 | Relatório mensal de evidências | F2-2 | Pendente |
| F2-11 | Ativação do fallback push para SMS | F2-5, F2-7 | Pendente |
| F2-12 | Ativação da classe `operational` com janela de silêncio | F2-5 | Pendente |
| I2 | Unidade bloqueante de infraestrutura da fase | F2-2, F2-5, F2-7 | Pendente, fora de código |

Três achados que a implementação confirmou e que não estavam previstos nesta decomposição:

1. **A correlação da Twilio não cabe na interpretação.** O contrato da D2 entrega a `Interpret` um `VerifiedProviderWebhook` que carrega apenas provedor, instante e corpo, e a Twilio não ecoa parâmetros de query no corpo do callback. A F2-1 devolve correlação nula para esse provedor, e a rota da F2-2 preenche a correlação a partir dos próprios parâmetros que o `StatusCallback` aponta. O contrato do Dispatch fica intacto e a correlação continua sendo conhecimento de Notifications, como a D2 fixou.
2. **A URL assinada é material de autenticação, não diagnóstico.** A assinatura da Twilio cobre a URL completa. Atrás de balanceador, a URL vista pelo processo pode divergir da que o provedor assinou, e toda assinatura válida seria recusada em produção enquanto passa em teste. A F2-2 monta a URL a partir de uma base pública configurável.
3. **O sinal de supressão não é persistido na evidência.** O esquema de `delivery_event` fixado na F2-2 não tem coluna para ele, e o consumidor reconstrói o evento canônico sem o sinal. A F2-6 acrescenta a coluna em migração própria, em vez de reclassificar dentro de Notifications, porque a classificação é conhecimento de provedor e pertence ao Dispatch.
4. **A migração da F2-5 não fechava a exigência que ela mesma carregava.** Com o avanço de plano reivindicado só no handler, nenhuma coluna marcava pedido em voo, e a varredura reencontraria o mesmo attempt a cada ciclo. Entrou `notification_attempt.fallback_requested_at`, carimbada na transação da varredura e incorporada ao predicado parcial, com janela de reemissão em vez de bandeira permanente, para que a perda de uma mensagem se cure sozinha. Medição: um attempt vencido drenado por duas réplicas gerava 43 linhas de outbox e passou a gerar uma.
5. **O Messaging Service por aplicação não cabe no contrato publicado.** O design pede sender pool por `application`, mas `DispatchRequest` não carrega a aplicação, e a F2-7 tinha autorização para exatamente um membro novo. O adaptador ficou com Messaging Service por deployment, com queda para número de origem. Fechar a promessa do design exige mais um membro opcional na requisição, o que a F2-8 executa no mesmo idioma aditivo já usado por `Correlation` e `Validity`.
6. **A recusa de segurança do SMS de autenticação se perde no caminho de fallback.** `FallbackRequestHandler` mapeia qualquer falha de renderização para um motivo genérico de template, então uma recusa por link em SMS de autenticação chegaria ao registro como defeito de template e não como bloqueio de segurança. Corrigido na F2-8.
7. **Attempt de notificação já encerrada permanece no índice de varredura.** `SettleTerminalAsync` encerra em `expired` ou `failed` sem reivindicar o avanço, então o attempt guarda prazo e avanço nulo indefinidamente. A varredura da F2-5 filtra por `notification.status = 'dispatched'`, o que impede o pedido eterno, mas não remove a linha do índice; a limpeza desse passivo pertence à F2-9.

A ordem respeita a ativação do documento da fase: tracker e scheduler primeiro (F2-1 a F2-5), adapter SMS depois (F2-7 e F2-8), ativações por política no fim (F2-11 e F2-12). Cada fatia compila e passa a suíte sozinha.

Paralelismos previstos: F2-3 com F2-4; F2-6 com F2-4 e F2-5; F2-7 com F2-5, desde que a F2-2 já tenha entregue a rota que o `StatusCallback` aponta; F2-10 com F2-7 e F2-8.

## 5. Fatias

### F2-1: contratos de feedback de provedor no Dispatch

**Objetivo.** Publicar a verificação de assinatura e a normalização de evento de provedor, com adaptadores Twilio e SendGrid, sem nenhuma rota HTTP.

**Dependências.** Nenhuma.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Dispatch/Integration/V1/`, `src/Platform.Api/Modules/Dispatch/Infrastructure/Webhooks/`, `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/Twilio/`, `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/`, `src/Platform.Api/Modules/Dispatch/DispatchModule.cs`, `src/Platform.Api/Modules/Dispatch/AGENTS.md`, `tests/Platform.UnitTests/Dispatch/`, `tests/Platform.IntegrationTests/Dispatch/`.

**Contratos.** Os tipos e as duas interfaces da D2, mais o catálogo de recusas com os cinco códigos estáveis.

**Migração de banco.** Nenhuma. Segredo de verificação (auth token da Twilio, chave pública do Event Webhook do SendGrid) entra por opções ligadas à configuração, com guarda em tempo de uso, no mesmo regime dos adaptadores atuais.

**Critério de aceite verificável.** Vetor de assinatura válido é aceito e vetor com um byte alterado é recusado com `signature-invalid`; timestamp fora da janela configurada é recusado; origem fora da allowlist é recusada com código próprio; um lote do Event Webhook com N eventos produz N eventos canônicos com `providerEventId` estável; um bounce classificado como definitivo produz `HardBounce` e um bounce transitório produz `None`; a lista de códigos definitivos é configuração do adaptador, não constante escondida.

**Testes.** Unidade para verificação, janela, normalização e mapeamento de sinal; integração para resolução por `provider_config`; arquitetura para manter o contrato apenas em `Integration/V1`.

**Riscos e armadilhas.** A comparação de assinatura precisa ser em tempo constante. O teste `Security_paths_must_not_use_pseudo_random_generators` casa caminhos com `Crypto`, `Auth` ou `Token` no nome, então o arquivo do verificador cai na regra e não pode usar gerador pseudoaleatório. O adaptador não pode registrar em log corpo, destino ou assinatura.

### F2-2: ingestão de webhooks e evidência de entrega

**Objetivo.** Receber, autenticar por assinatura, deduplicar e persistir o evento do provedor em menos de 20 ms, e aplicar de forma assíncrona a máquina de estados do attempt.

**Dependências.** F2-1.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/`, `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/` (contexto, configuração, migração), `src/Platform.Api/Modules/Notifications/Infrastructure/Http/`, `src/Platform.Api/Modules/Notifications/Infrastructure/RateLimiting/`, `src/Platform.Api/Modules/Notifications/Infrastructure/Authentication/`, `src/Platform.Api/Modules/Notifications/Domain/NotificationAttempt.cs`, `src/Platform.Api/Modules/Notifications/DeliveryTrackerWorkerRole.cs`, `src/Platform.Api/Modules/Notifications/NotificationsModule.cs`, `src/Platform.Api/Modules/Notifications/AGENTS.md`, `docker/localstack/init-aws.sh`, `tests/Platform.UnitTests/Notifications/`, `tests/Platform.IntegrationTests/Notifications/`.

**Contratos.** Nenhum em `Integration/V1`. Fila interna `delivery-events` e mensagem `delivery.event_received` com payload `{ deliveryEventId }`.

**Migração de banco.** `AddDeliveryTracking`, schema `notifications`:

- `delivery_event`, tabela-mãe particionada por mês em `received_at` (instante de recepção, e não `occurred_at`, porque o provedor pode datar para trás e uma linha cairia fora das partições provisionadas), chave primária composta `(id, received_at)`, colunas `attempt_id` e `notification_id` anuláveis, `provider_key`, `provider_event_id`, `provider_message_id`, `kind`, `occurred_at`, `error_code`, `payload_enc`, `applied_at`; partições iniciais provisionadas na própria migração, no padrão de `20260823122306_CreateCorePipelineState.cs`, e registro no provisionador de partições da plataforma com health check próprio;
- `provider_event_dedupe(provider, provider_event_id, processed_at)`, **não particionada**, chave única `(provider, provider_event_id)`, com purga em 30 dias (§6);
- índice `(provider_message_id)` em `notification_attempt`, parcial em `provider_message_id IS NOT NULL` (§11.3);
- estados de attempt novos: `delivered`, `read` e `bounced`.

**Critério de aceite verificável.** Assinatura inválida não grava linha e produz log de segurança; o mesmo `provider_event_id` entregue duas vezes produz uma linha e um efeito; a transação do webhook contém as três escritas e **nenhum** append de auditoria, provado por teste que reprova se `IAuditTrail` entrar nesse caminho, porque o append segura o lock da cadeia e serializaria o webhook contra a ingestão (§11.3); `delivered` de e-mail move o attempt de `sent` para `delivered` e carimba `delivered_at`; um bounce definitivo move para `bounced`; evento sem correlação resolve o attempt por `provider_message_id`; evento de attempt desconhecido fica armazenado e não aplicado, sem erro e sem retentativa infinita.

**Testes.** Integração com Postgres e LocalStack SQS, exercitando rota, deduplicação, aplicação assíncrona e o papel novo; unidade para o aplicador de estado; segurança de arquitetura para a rota com autorização e rate limit na mesma instrução; arquitetura para descoberta do papel e para o eixo `Result` dos handlers.

**Riscos e armadilhas.** O payload bruto do provedor carrega contato em claro, e o módulo proíbe PII em claro em repouso: selar com a cifra de envelope em escopo próprio do tracker, porque a aplicação da notificação pode ainda não ser conhecida no instante do insert e uma consulta extra estouraria o orçamento de 20 ms. A fila nova precisa existir no LocalStack e no Terraform (I2), senão o papel sobe saudável e não consome nada. A banda do relay para `delivery-events` sai da classe da notificação, e não da banda `auth`, o que é aceitável porque o caminho sensível a prazo é o do scheduler.

### F2-3: pergunta 7 da reconstrução respondível

**Objetivo.** Fazer `GET /v1/audit/notifications/{id}` responder se o provedor confirmou a entrega, fechando a lacuna declarada na fase 1b (§9.5, errata de 2026-08-23).

**Dependências.** F2-2.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Notifications/Integration/V1/NotificationEvidence.cs`, `src/Platform.Api/Modules/Notifications/Infrastructure/Reads/`, `src/Platform.Api/Modules/Compliance/Features/Queries/GetNotificationEvidence/`, `src/Platform.Api/Modules/Compliance/Infrastructure/Http/`, `src/Platform.Api/Modules/Compliance/AGENTS.md`, `docs/notification-hub-system-design.md` (errata do §9.5), `tests/Platform.UnitTests/Compliance/`, `tests/Platform.IntegrationTests/Compliance/`.

**Contratos.** `NotificationAttemptEvidence` ganha `DeliveredAt` e `DeliveryEvents`; novo `DeliveryEventEvidence` com `ProviderKey`, `ProviderEventId`, `Kind`, `OccurredAt` e `ErrorCode`.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** Uma notificação com evento de entrega responde a lista em ordem cronológica; uma notificação sem evento responde lista vazia, o que agora é afirmação legítima porque a tabela existe; nenhum payload bruto e nenhum dado de contato sai na resposta; o OpenAPI descreve a mudança de significado, porque até esta fatia a resposta afirmava aceitação pelo provedor e nunca entrega.

**Testes.** Integração da rota com registro de divulgação; unidade da projeção; arquitetura confirmando que o Compliance continua sem `DbContext` e sem migração.

**Riscos e armadilhas.** A convenção da casa é que ausência de membro diz que a fase não sabe, enquanto array vazio afirma um fato; publicar a lista sem atualizar a errata do §9.5 deixaria a documentação afirmando o contrário do código.

### F2-4: convivência dos dois gatilhos de fallback

**Objetivo.** Garantir um único avanço de plano por passo, qualquer que seja o gatilho, e devolver ao push a semântica de entrega que o fallback por prazo exige.

**Dependências.** F2-2.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Notifications/Domain/`, `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/` (`AttemptDispatchWriter.cs`, `DispatchMessages.cs`, `PipelineCommitWriter.cs`, configuração e migração), `src/Platform.Api/Modules/Notifications/Features/Fallback/`, `src/Platform.Api/Modules/Notifications/Features/Dispatching/`, `src/Platform.Api/Modules/Notifications/AGENTS.md`, `docs/ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md`, `docs/guia-integracao-produtor.md`, `tests/`.

**Contratos.** Nenhum em `Integration/V1`. `fallback.requested` passa a ser roteado para `core-auth` em fluxo de autenticação.

**Migração de banco.** `AddAttemptPlanAdvance`:

- `notification_attempt.plan_advanced_at timestamptz` anulável;
- índice parcial novo `ix_notification_attempt_fallback_due (status, fallback_deadline) WHERE fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL`, substituindo `ix_notification_attempt_fallback`;
- `notification.auth_flow boolean NOT NULL DEFAULT false`.

Coluna anulável e coluna com default constante são operação de catálogo no PostgreSQL 11 ou superior, sem reescrita de tabela, ao contrário da coluna gerada da corretiva C3 da fase anterior. Ainda assim, aplicar `lock_timeout`, que é o padrão do repositório para migração que toma lock exclusivo (§11.3).

**Critério de aceite verificável.** Dois `fallback.requested` para o mesmo passo, um do dispatcher e outro do tracker, produzem exatamente um attempt seguinte, provado com duas transações concorrentes reais; dois irmãos de push do mesmo passo produzem um único avanço; push aceito num passo com prazo mantém a notificação em `dispatched` e não emite o evento de entrega; push aceito no último passo mantém o comportamento atual; `fallback.requested` de template de autenticação chega em `core-auth` e é reivindicado pela banda de topo do relay.

**Testes.** Integração com Postgres, incluindo concorrência real; unidade para a escolha do passo seguinte; arquitetura e segurança sem mudança esperada.

**Riscos e armadilhas.** Mudança observável de `araia.notification.delivered.v1` para planos com passo posterior: exige ADR e nota no guia do produtor na mesma fatia. Sem esta fatia, a F2-5 duplica SMS ao cliente, que é violação direta da ADR-0008. O `UPDATE` de claim precisa podar partição por `created_at`, senão varre todas as partições de `notification_attempt`.

### F2-5: scheduler DB-backed

**Objetivo.** Varrer prazo vencido, `unknown` prolongado em fluxo crítico ou de autenticação e `release_at` vencido, gravando a próxima ação via outbox.

**Dependências.** F2-4.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Scheduling/`, `src/Platform.Api/Modules/Notifications/DeliveryTrackerWorkerRole.cs`, `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/` (migração), `src/Platform.Api/Modules/Notifications/AGENTS.md`, `tests/`.

**Contratos.** Nenhum novo.

**Migração de banco.** `AddSchedulerScanState`:

- `notification_attempt.status_changed_at timestamptz` anulável, carimbada em toda transição, com índice parcial `WHERE status = 'unknown'`; linhas anteriores à migração ficam nulas e nunca casam, e a reconciliação da F2-9 é quem resolve esse passivo;
- substituição de `ix_notification_release` por `ix_notification_release_due (release_at) WHERE status = 'deferred'`, para que a notificação liberada saia do índice em vez de acumular nele.

**Critério de aceite verificável.** Attempt `sent` com prazo vencido e sem evento de entrega gera exatamente um `fallback.requested`; attempt `unknown` há mais de 60 s gera fallback em `critical` e em autenticação, e não gera nas demais classes; notificação `deferred` com `release_at` vencido volta a `accepted` e é reenfileirada em `core-{classe}` exatamente uma vez, com trilha própria; duas instâncias do papel varrendo em paralelo não produzem efeito duplicado; o intervalo de varredura é configuração, com 5 s como padrão.

**Testes.** Integração com Postgres e duas instâncias concorrentes, e teste de plano de execução para as duas varreduras. Vale a convenção de oráculo provado falível da fase anterior: derrubar o índice parcial e observar a reprovação com a mensagem que nomeia o defeito.

**Riscos e armadilhas.** O predicado parcial precisa aparecer literalmente na consulta, senão o planejador ignora o índice, que é o defeito que custou uma rodada de medição na fase 1b. A liberação precisa transitar de `deferred` para `accepted` dentro da transação do claim: sem isso, `CoreMessageProcessor.cs:78` trata a retomada como duplicata e a notificação adiada nunca sai. A varredura de 5 s soma até 5 s ao prazo, contra um timeout de fallback de 30 s no plano de `critical`, e esse trade-off já está aceito no design.

### F2-6: supressão automática, reversível e auditada

**Objetivo.** Registrar sinal de supressão vindo do provedor, suprimir o contato quando a regra do canal manda, permitir reversão auditada e recusar o canal suprimido no estágio Policy.

**Dependências.** F2-2. Pode correr em paralelo com F2-4 e F2-5.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/ContactConsent/` (Domain, Features, Infrastructure, Integration/V1, `AGENTS.md`), `src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Events/`, `src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/SuppressionGateRule.cs`, `src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/PolicyStage.cs`, `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs`, `docs/ADR-0015-regra-de-supressao-no-estagio-policy.md`, `docs/guia-integracao-produtor.md`, `tests/`.

**Contratos.** `ISuppressionLedger`, `SuppressionReport`, `SuppressionOutcome`, `SuppressionState` e o membro `Suppressions` de `RecipientSnapshot`.

**Migração de banco.** `AddSuppressionLedger`, schema `contactconsent`:

- `suppression_signal(id, contact_point_id, channel, reason, source_event_id, observed_at)`, chave única em `source_event_id` e índice `(contact_point_id, observed_at)`;
- `suppression(id, contact_point_id, channel, reason, source, actor_type, actor_id, created_at, until, removed_at, removed_by)`, com índice único parcial `(contact_point_id) WHERE removed_at IS NULL`.

**Critério de aceite verificável.** Bounce definitivo de e-mail suprime na primeira ocorrência; sinal de SMS suprime somente na segunda ocorrência dentro de sete dias; relato repetido do mesmo `source_event_id` é no-op declarativo com trilha; remoção manual por `Platform.Admin` grava `suppression.removed` com ator; a notificação seguinte para o canal suprimido é recusada no estágio Policy com motivo estável e evidência regra a regra; `araia.notification.contact_suppressed.v1` é publicado uma vez, com `recipientId`, `channel` e `reason`; um evento de origem fora da allowlist não produz efeito e gera alarme de segurança.

**Testes.** Integração com Postgres e Redis para ledger, invalidação de snapshot e recusa no pipeline; unidade para a regra de acumulação e para a regra de política; segurança de arquitetura para a rota administrativa; arquitetura para a direção da dependência.

**Riscos e armadilhas.** A ADR-0011 fixou a lista de regras da v1 sem supressão, enquanto o §4.3 a lista entre as regras do estágio Policy; a regra nova entra pelo caminho de nível 3 da própria ADR, que exige ADR curta, e a fatia a produz. A posição na ordem é depois de `ConsentGate` e antes de `QuietHours`, para não adiar trabalho que será recusado. O snapshot cacheado no Redis por 24 h precisa de invalidação e de troca de versão da chave na mesma fatia, senão um contato suprimido continua elegível e a entrada antiga desserializa sem o membro novo.

### F2-7: adapter SMS completo

**Objetivo.** Fechar as três lacunas do adaptador Twilio (Messaging Service, `StatusCallback` e `ValidityPeriod`) e a normalização de encoding do SMS na renderização.

**Dependências.** F2-1 e F2-2.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Dispatch/Integration/V1/DispatchRequest.cs`, `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/Twilio/`, `src/Platform.Api/Modules/Dispatch/AGENTS.md`, `src/Platform.Api/Modules/TemplateManagement/Infrastructure/Rendering/`, `src/Platform.Api/Modules/TemplateManagement/Features/` (validação de publicação), `src/Platform.Api/Modules/Notifications/Features/Dispatching/`, `src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/RenderStage.cs`, `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs`, `docs/guia-integracao-produtor.md`, `tests/`.

**Contratos.** `DispatchRequest` ganha `TimeSpan? Validity` como quarto membro opcional, o que preserva todas as chamadas existentes. Motivo de rejeição novo para link em SMS de finalidade de autenticação.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** A requisição Twilio carrega `MessagingServiceSid`, `StatusCallback` com o identificador do attempt e `ValidityPeriod` igual ao TTL restante; TTL restante nulo ou vencido não chama o provedor; o corpo renderizado é normalizado em NFC e sem caracteres de controle, e o hash auditado é calculado sobre a forma normalizada; template de autenticação em SMS contendo URL falha na publicação, e um render que produza URL é recusado com motivo estável e alarme de segurança.

**Testes.** Unidade para montagem da requisição e para a normalização; integração com WireMock para o contrato de requisição e o mapeamento de resultado; integração para a recusa de publicação; arquitetura sem mudança esperada.

**Riscos e armadilhas.** A normalização muda os bytes renderizados, então conteúdo de SMS já armazenado deixa de casar com o `content_hash_masked` gravado, e o backfill da corretiva C1 deixará essas linhas intocadas, com registro estruturado de revisão; isso é comportamento correto do backfill, e não defeito, mas precisa estar escrito. Normalizar depois do hash quebraria a igualdade que a auditoria confere, então a normalização é do render, nunca do adaptador, conforme a regra do módulo Dispatch de nunca reescrever conteúdo. A recusa de link em OTP tem falso positivo possível, que custa um OTP; o falso negativo custa vetor de phishing, e a decisão é recusar.

### F2-8: rate limit por provedor e kill switch automático de canal

**Objetivo.** Limitar a taxa por provedor nos limites contratados e acionar o kill switch de canal quando o circuito permanecer aberto por mais de 10 min.

**Dependências.** F2-7.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Dispatch/Infrastructure/Resilience/`, `src/Platform.Api/Modules/Dispatch/AGENTS.md`, `src/Platform.Api/Modules/Notifications/Features/Dispatching/`, `src/Platform.Api/Modules/Notifications/Features/KillSwitch/`, `tests/`.

**Contratos.** Um membro opcional novo em `DispatchRequest`, para a aplicação, absorvido da F2-7 junto com o pool de sender por aplicação. O dispatcher já observa o circuito aberto pelo resultado `circuit-open` do adaptador (`Infrastructure/Providers/Twilio/TwilioChannelProvider.cs:60-63`), e o kill switch já pertence ao módulo Notifications.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** O token bucket em Redis limita a taxa por provedor com burst local de 1 s, e a falha do Redis opera em modo fail open com alarme, na mesma postura do rate limit de ingestão; circuito aberto observado por mais de 10 min aciona o kill switch do canal com ator de sistema e trilha, sob gate de configuração desligado por padrão; a reversão é sempre humana.

**Testes.** Integração com Redis e `TimeProvider` controlável; unidade para a janela de observação; integração do gate desligado provando ausência de acionamento.

**Riscos e armadilhas.** A observação do circuito é por processo e o kill switch é global, então uma instância degradada pode parar o canal inteiro. Pior: com SMS como último passo do plano, parar o canal coloca OTP em hold até expirar. Por isso o gate nasce desligado e a ativação é decisão operacional registrada.

### F2-9: reconciliação por canal

**Objetivo.** Corrigir attempts `sent` e `unknown` sem evento há mais de 6 h, consultando o provedor onde o provedor permite.

**Dependências.** F2-2, F2-5 e F2-7.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Dispatch/Integration/V1/`, `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/`, `src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Reconciliation/`, `src/Platform.Api/Modules/Notifications/NotificationsMaintenanceWorkerRole.cs`, `src/Platform.Api/Modules/Notifications/AGENTS.md`, `tests/`.

**Contratos.** `IProviderDeliveryLookup`, `IProviderDeliveryLookupResolver` e `ProviderDeliveryQuery`.

**Migração de banco.** Nenhuma: usa `status_changed_at` da F2-5 e o índice por `provider_message_id` da F2-2.

**Critério de aceite verificável.** Attempt elegível é consultado uma vez por ciclo; a resposta do provedor entra pelo mesmo aplicador de estado do webhook; provedor sem consulta posterior não é chamado e o attempt permanece `unknown` com registro; toda correção grava `audit_event`; o valor de contato usado na correlação por destino é transitório e não aparece em log, resposta ou trilha.

**Testes.** Integração com WireMock para as duas consultas e com Postgres para a correção; unidade para elegibilidade e janela; arquitetura para a direção da dependência.

**Riscos e armadilhas.** O alcance histórico do e-mail depende de decisão externa (seção 6). A correlação de SMS por destino e janela é best effort e pode casar a mensagem errada quando o mesmo destino recebeu duas mensagens na janela; a mitigação é preferir `provider_message_id` e só cair para destino e janela quando ele faltar.

### F2-10: relatório mensal de evidências

**Objetivo.** Gerar o relatório mensal e arquivá-lo no bucket WORM, alimentando o rito mensal.

**Dependências.** F2-2.

**Conjunto de escrita permitido.**
`src/Platform.Api/Modules/Notifications/Integration/V1/`, `src/Platform.Api/Modules/Notifications/Infrastructure/Reads/`, `src/Platform.Api/Modules/Audit/Integration/V1/`, `src/Platform.Api/Modules/Audit/Infrastructure/Worm/`, `src/Platform.Api/Modules/Audit/AuditMaintenanceWorkerRole.cs`, `src/Platform.Api/Modules/Audit/AGENTS.md`, `src/Platform.Api/Modules/Compliance/Features/Reporting/`, `src/Platform.Api/Modules/Compliance/AGENTS.md`, `tests/`.

**Contratos.** `INotificationOutcomeReport` e `NotificationOutcomeSummary` em Notifications; `IEvidenceArchive` e `ArchivedEvidence` em Audit.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** O job mensal grava um objeto com chave determinística por mês; a reexecução reconhece o digest existente e não sobrescreve, na mesma postura do exportador atual; seções sem fonte no hub ficam ausentes, e não vazias; o relatório declara a janela e a versão do formato; nenhuma leitura cruza fronteira fora de `Integration/V1`, o que o teste de dependência entre contextos comprova.

**Testes.** Integração com LocalStack S3 e Postgres; unidade para a composição e para a regra de omissão; arquitetura para direção de dependência e ausência de `DbContext` no Compliance.

**Riscos e armadilhas.** Profundidade de DLQ e contagem de falhas de provedor não têm fonte no banco; ficam ausentes até existir a fonte de métricas, que pertence à I2. O rito mensal precisa saber que a taxa de entrega, antes de a F2-9 estar em regime, ainda carrega attempts `unknown` não reconciliados.

### F2-11: ativação do fallback push para SMS

**Objetivo.** Provar o cenário do §5.1 de ponta a ponta e ativar o plano com SMS por publicação de política.

**Dependências.** F2-5 e F2-7.

**Conjunto de escrita permitido.**
`tests/Platform.IntegrationTests/Notifications/`, `tests/Platform.IntegrationTests/Dispatching/`, `tools/Platform.GoLiveChecks/`, `docs/fases/fase-2-resiliencia-e-sms.md` (status), `docs/guia-integracao-produtor.md`.

**Contratos.** Nenhum. A ativação é publicação de nova versão da política de classe pela API de gestão, com quatro olhos, e o rollback é a republicação da versão anterior.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** Push aceito, sem evento de entrega, prazo de 30 s vencido, SMS enfileirado, webhook de entrega fechando a notificação, tudo num único teste de integração; TTL vencido no instante do `fallback.requested` termina em `expired` e não consome SMS; o portão executável reprova enquanto existir política `critical` publicada cujo plano não tenha passo posterior.

**Testes.** Integração ponta a ponta com Postgres, Redis, LocalStack SQS e WireMock; extensão do portão executável.

**Riscos e armadilhas.** A ativação real depende do sender BR (seção 6) e da publicação da política com quatro olhos, que é ato humano e não entrega de código.

### F2-12: ativação da classe `operational` com janela de silêncio

**Objetivo.** Ativar a classe `operational` com janela de silêncio e fechar o item bloqueante herdado da fase 1b.

**Dependências.** F2-5.

**Conjunto de escrita permitido.**
`tests/Platform.IntegrationTests/Notifications/`, `tools/Platform.GoLiveChecks/`, `src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/QuietHoursRule.cs` (somente se o teste apontar defeito), `docs/`.

**Contratos.** Nenhum.

**Migração de banco.** Nenhuma.

**Critério de aceite verificável.** Notificação `operational` dentro da janela recebe `Defer` com `release_at` no fuso do destinatário e é liberada pelo scheduler no ciclo seguinte ao vencimento; `critical` e autenticação não são afetadas; o portão executável deixa de reprovar por ausência de template `operational` publicado e de papel de envio concedido; a decisão sobre o rodízio 3:1 entre `transactional` e `operational` é registrada, porque o §4.2 marca a ativação desta classe como o gatilho da reavaliação.

**Testes.** Integração com fuso do destinatário e relógio controlável; extensão do portão executável.

**Riscos e armadilhas.** Sem a F2-5 a notificação adiada fica parada sem envio e sem alarme, que é exatamente a razão de o item ser bloqueante de go-live desde a fase 1b.

### 5.4 I2: unidade bloqueante de infraestrutura da fase

Pertence à fase 2, é de responsabilidade da Engenharia de Plataforma e bloqueia qualquer afirmação de que a fase está implantável. Escopo declarativo obrigatório:

- fila `delivery-events` e sua DLQ, com redrive, retenção e criptografia;
- exposição pública do host da API restrita às rotas de webhook, com WAF, regra de taxa, allowlist de IP dos provedores e TLS, mais o pentest específico da superfície;
- segredos de verificação (auth token da Twilio, chave pública do Event Webhook do SendGrid) no gerenciador de segredos, com rotação;
- Messaging Service da Twilio por `application`, com sender pool de número longo ou short code BR, e assinatura do Event Webhook habilitada no SendGrid;
- deployment do papel `delivery-tracker` com no mínimo duas réplicas, health check próprio e alarme de idade da linha vencida mais antiga;
- alarme de profundidade de `dispatch-sms-critical` e alarme de segurança para evento de origem fora da allowlist;
- fonte de métricas operacionais que o relatório mensal consome para DLQ e falhas de provedor.

Sem receipt de aplicação e inspeção, a fase não é considerada implantável, no mesmo regime da unidade I1 da fase anterior.

### 5.5 Estratégia e infraestrutura de teste

O roster da fase anterior continua valendo: Testcontainers para Postgres, Redis e Kafka; LocalStack para SQS, S3 e KMS; WireMock para provedores no CI; FCM sempre falso. A fase 2 acrescenta duas regras:

1. **Assinatura de provedor é testada por vetor fixo**, nunca por servidor real: um vetor válido, um vetor com um byte alterado, um vetor fora da janela de timestamp e um de origem não permitida.
2. **Toda asserção sobre plano de execução ou sobre regra materializada precisa ser provada falível**, mutando o que ela guarda, conforme a convenção da fase 1b. Vale para os dois índices parciais da F2-4 e da F2-5 e para o claim de avanço de plano.

A suíte de integração continua rodando sequencial.

## 6. O que depende de decisão externa

| Item | O que é implementável hoje | O que fica bloqueado |
|---|---|---|
| Add-on Email Activity do SendGrid | Todo o job de reconciliação: elegibilidade, claim, consulta, aplicação pelo mesmo aplicador do webhook, trilha, e o braço da Twilio. O braço de e-mail funciona dentro do alcance histórico do plano vigente | Somente o alcance histórico da consulta de e-mail. A decisão muda um valor de configuração de janela, não o desenho |
| Sender ID no Brasil, country guidelines da Twilio | Todo o adapter SMS, com Messaging Service, `StatusCallback` e `ValidityPeriod`, e o teste com WireMock | A ativação real do canal em produção e o teste por operadora. O desenho já assume número longo ou short code BR |
| Fonte de métricas operacionais (I2) | O relatório mensal com as seções que o banco e a trilha sustentam | As seções de DLQ e de falhas de provedor, que ficam ausentes até a fonte existir |
| Publicação de política com quatro olhos | Todo o código de fallback e de janela de silêncio, exercitado por política publicada em ambiente de teste | A ativação em produção, que é ato humano registrado |

Nenhum desses bloqueios impede fatia alguma de ser concluída, testada e integrada.

## 7. Riscos e armadilhas transversais

| Risco | Onde aparece | Tratamento |
|---|---|---|
| Duplicata de SMS ao cliente pelo duplo gatilho de fallback | F2-4, F2-5 | Claim de avanço por passo no banco; a deduplicação por mensagem não cobre dois produtores distintos |
| Fallback de push nunca dispara por prazo | F2-4 | Aceitação de push só declara entrega no passo sem prazo; ADR e nota no guia do produtor |
| Webhook serializado contra a ingestão | F2-2 | Nenhum append de auditoria na transação do webhook; a trilha é escrita pelo consumidor assíncrono |
| Índice parcial ignorado pelo planejador | F2-4, F2-5 | Predicado literal na consulta e teste de plano provado falível |
| Contato suprimido continua elegível por até 24 h | F2-6 | Invalidação do snapshot e troca de versão da chave de cache na mesma fatia |
| Conteúdo de SMS antigo deixa de casar com o hash mascarado | F2-7 | Comportamento esperado do backfill; linhas ficam intocadas e aparecem em log de revisão |
| Kill switch automático parando o canal de fallback do OTP | F2-8 | Gate de configuração desligado por padrão e reversão humana |
| Migração que toma lock exclusivo enfileira a ingestão | F2-2, F2-4, F2-5 | `lock_timeout` em toda migração dessa classe, padrão do repositório |
| Fila nova sem provisionamento | F2-2 | LocalStack na fatia, Terraform na I2; papel que sobe sem fila não consome nada e parece saudável |

## 8. Referências

- [Fase 2: resiliência e SMS](fase-2-resiliencia-e-sms.md): design técnico que esta decomposição realiza.
- [Design de sistema](../notification-hub-system-design.md): §4.2, §4.3, §5.1, §5.2, §8, §9.5, §9.6, §11.3.
- [ADR-0008](../ADR-0008-at-least-once-com-idempotencia.md) e [ADR-0011](../ADR-0011-politica-como-configuracao-de-classe.md).
- [Fase 1b: fundação](fase-1b-fundacao.md): formato de decomposição, convenções de teste e unidade bloqueante de infraestrutura.
- `AGENTS.md` dos módulos Notifications, Dispatch, ContactConsent, Audit e Compliance.
