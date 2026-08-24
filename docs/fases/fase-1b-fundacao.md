---
language: pt-BR
---

# Fase 1b: fundação do Notification Hub

| Campo | Valor |
|---|---|
| **Tipo** | Design técnico (technical-design) |
| **Status** | ACCEPTED |
| **Data** | 2026-08-23 |
| **Dono** | Arquitetura (dotnet-architect) |
| **Público** | Engenharia do hub (implementação das fatias); Arquitetura e Compliance como leitores |
| **Fontes** | [Design de sistema](../notification-hub-system-design.md) (§4, §5, §6, §7, §9, §15, §16); ADRs 0002, 0003, 0006, 0008, 0010 e 0012; decisão aceita do mapa de módulos da fase 1b (dotnet-architect, 2026-08-23); estado do repositório em 2026-08-23 |

Este documento formaliza a decisão aceita do dotnet-architect (2026-08-23) sobre o mapa de módulos e a decomposição em fatias da fase 1b. Ele não cria decisões novas: resume a decisão, ancora cada fronteira em evidência e registra as pendências que a própria decisão carregou. O consumidor imediato é o backlog de implementação da fase; o insumo upstream é a linha da fase 1b no [roadmap do design de sistema](../notification-hub-system-design.md) (§15).

## 1. Objetivo e escopo

### 1.1 Contexto

A fase 1a entregou o módulo TemplateManagement: autoria, validação integral, render com Scriban, publicação com quatro olhos, layouts e configuração de classe governada, com `audit_event` particionado e o job `partition-manager` (histórico do repositório, de `be492f3` a `e3fb937`). A fase 1b constrói a fundação de envio sobre essa base.

### 1.2 Objetivo

Entregar o caminho completo de uma notificação nas classes `critical` e `transactional`: ingestão REST e Kafka, pipeline de estágios, resolução de contato e consentimento, despacho por e-mail (SendGrid) e push (FCM), auditoria com cadeia de hash e export WORM, e APIs de consulta e auditoria, conforme a linha da fase 1b do roadmap (§15).

### 1.3 Escopo

Conforme o §15 do design de sistema, a entrega da fase 1b compreende: Ingestion API REST; Kafka Ingress Worker (`notifications.requested.v1`, `.dlt`, `PRODUCER_REGISTRY`); saída `notifications.events.v1`; outbox relay; Core pipeline consumindo versões `published`; Contact & Consent v1 (`RECIPIENT_PROFILE`, `DEVICE_TOKEN`, `contacts.events.v1`, escrita REST; ADR-0012); `audit_event` com cadeia de hash e export WORM; API REST de consulta e auditoria; canais e-mail (SendGrid) e push (FCM); classes `critical` e `transactional`; guia de integração do produtor com biblioteca .NET compartilhada opcional. A duração prevista no roadmap é de 6 a 8 semanas; este documento não deriva cronograma por fatia. A emissão de `notifications.events.v1` e o publicador Kafka do outbox relay movem para a fatia B10 (§5.1): o escopo da fase não muda; muda o sequenciamento.

### 1.4 Não objetivos

- SMS e WhatsApp, fallback declarativo, tracker com webhooks, scheduler DB-backed, supressão, reconciliação e classe `operational`: fase 2 (§15).
- Módulo Delivery Tracking: adiado para a fase 2 por decisão do mapa de módulos, com a consequência registrada na seção 9 (o `delivered` de e-mail depende dos webhooks).
- Template Studio, aprovação dupla por classe e promoção entre ambientes: pontos de extensão com gatilho definido (§4.3 e §15).
- Provisionamento Terraform: a linha do roadmap da 1b inclui Terraform completo; a decomposição B1 a B16 cobre as entregas de código do hub e não atribui fatia à infraestrutura declarativa, que permanece governada pelo §14 e §15.

## 2. Arquitetura e fronteiras de módulo

A decisão aceita define quatro módulos no monólito modular existente (`src/Platform.Api/Modules`, conforme `.araia/stack-profile.yaml` e o [padrão de arquitetura do projeto](../architecture/standards/modular-monolith-architecture.md)), além do TemplateManagement herdado da fase 1a.

### 2.1 Notifications (subdomínio core)

Ingestão REST e Kafka, pipeline de estágios (ADR-0003), estados de notificação e de attempt (§5.2) e a fatia de despacho: as filas `dispatch-*` e o ciclo de estados do attempt até `sent`.

**Justificativa e trade-off.** O invariante transacional mantém o despacho dentro de Notifications: attempt, outbox e `audit_event` são gravados na mesma transação, e a posse do attempt é tomada com lock otimista (`UPDATE ... WHERE status = 'queued'`, §5.2 e ADR-0008). Separar o despacho em outro módulo quebraria esse invariante ou exigiria transação distribuída, que a ADR-0008 descarta. O custo aceito é um módulo maior, que concentra ingestão, pipeline e despacho.

**Delivery Tracking fica para a fase 2.** Evidência: a linha da fase 2 do roadmap (§15) inclui tracker com webhooks Twilio/SendGrid e scheduler DB-backed; nenhuma entrega da 1b depende deles, exceto a ressalva do `delivered` registrada na seção 9.

### 2.2 ContactConsent (subdomínio supporting)

Fonte da verdade de `RECIPIENT_PROFILE`, `CONTACT_POINT`, `CONSENT` e `DEVICE_TOKEN`, com escrita REST (app role `Contacts.Write`) e ingestão de `contacts.events.v1`, conforme a [ADR-0012](../ADR-0012-contact-consent-fonte-da-verdade.md): módulo interno, mesmo processo e mesmo Postgres, sem serviço remoto na v1; modo degradado por cache stale-while-revalidate sobre consulta local.

### 2.3 Audit (subdomínio core)

Dono de `audit_event` e `approval` com a cadeia de hash por partição mensal ([ADR-0006](../ADR-0006-auditoria-append-only-hash-chain-worm.md), §9.4). Expõe o contrato de append transacional na superfície `Integration/V1`. Evidência no repositório: `src/Platform.Api/Modules/Audit/Integration/V1/IAuditTrail.cs`, `AuditEntry.cs` e `ApprovalGrant.cs`; a cadeia em `src/Platform.Api/Modules/Audit/Domain/AuditChain.cs`; a migração de adoção em `src/Platform.Api/Modules/Audit/Infrastructure/Persistence/Migrations/20260823023623_AdoptAuditTrail.cs`.

### 2.4 Dispatch (subdomínio generic)

`IChannelProvider` e os adapters SendGrid e FCM, sem estado próprio.

**Justificativa e trade-off.** Dispatch é a costura comercial do hub: traduz `RenderedMessage` em chamadas de provedor (§4.3 "Dispatchers"), na linha da ADR-0009 (construir o core, comprar a entrega). Sem estado próprio, a troca de provedor permanece reversível e o invariante transacional continua inteiro em Notifications. O custo aceito é que toda transição de estado do attempt atravessa a fronteira Notifications, e o adapter devolve apenas `ProviderResult`.

### 2.5 Workers em host único

Os workers da fase (Outbox Relay, Core, Kafka Ingress, dispatchers) rodam em um único projeto host `src/Platform.Worker`, com o papel selecionado por configuração `Worker:Role`. Na data de autoria o repositório contém apenas `src/Platform.Api`; o host de worker nasce na fatia B5.

**Errata de 2026-08-23 (fatia B13).** O que o design chamava de job `audit export` e de job `partition-manager` consolida em um único papel de worker, `audit-maintenance`, de propriedade do módulo Audit e descoberto pelo catálogo de papéis (`IWorkerRoleModule`), sem que o host de worker referencie o módulo. O papel hospeda provisão de partições, export diário, ciclo de fechamento e verificação da cadeia; a API deixa de hospedar o `partition-manager` e mantém apenas o health check `audit-partitions`. A motivação é operacional e de segurança: o ciclo revoga permissões e destaca partições, e essas ações não podem rodar uma vez por réplica que atende requisição. As rodadas são serializadas por advisory lock de manutenção, com escopo distinto do lock da cadeia.

### 2.6 Infraestrutura de plataforma

Outbox, Outbox Relay e `processed_messages` são infraestrutura de plataforma, não módulos de negócio: implementam os padrões transversais das ADRs [0002](../ADR-0002-sqs-sdk-direto.md) e [0008](../ADR-0008-at-least-once-com-idempotencia.md) para todos os módulos. O provisionamento de partições mensais também é infraestrutura de plataforma, promovido no commit `2a0dd86` (`src/Platform.Api/Infrastructure/Partitioning/`); a semântica de fechamento (revoke de escrita, retenção WORM) permanece no módulo Audit. A decisão determina registrar essa nota no [documento de padrões de arquitetura](../architecture/standards/modular-monolith-architecture.md); a nota foi registrada em 2026-08-23 (seção 9, item 8).

### 2.7 Conformidade com decisões aceitas

O desenho não reverte nem contorna nenhuma ADR aceita: SQS com SDK direto e `SqsConsumer<T>` interno (ADR-0002), pipeline de estágios com `StageOutcome` explícito (ADR-0003), auditoria transacional em três camadas (ADR-0006), at-least-once com idempotência em três chaves (ADR-0008), Kafka na borda e SQS interno (ADR-0010) e ContactConsent como módulo interno (ADR-0012).

## 3. Contratos entre módulos e eventos de integração

### 3.1 Contratos publicados entre módulos

Dependência entre módulos ocorre apenas via contratos publicados na superfície `Integration/V1` de cada módulo, com a exceção correspondente no teste de arquitetura (§4.3 "Políticas").

| Contrato | Módulo dono | Consumidor | Estado na data de autoria |
|---|---|---|---|
| `ClassPolicyDefinition`, `IPolicyRule`, `Channel` | TemplateManagement | Notifications (estágio Policy) | Publicado: `src/Platform.Api/Modules/TemplateManagement/Integration/V1/` (fatia B1, commit `5b50445`) |
| `IAuditTrail`, `AuditEntry`, `ApprovalGrant` (append transacional) | Audit | Todos os módulos que gravam efeito auditável | Publicado: `src/Platform.Api/Modules/Audit/Integration/V1/` (fatia B2, commit `6fc95b1`) |
| Contratos de leitura de templates (versão `published`, render) | TemplateManagement | Notifications (estágios Validate e Render) | Em implementação (fatia B3) |

### 3.2 Eventos de integração Kafka (CloudEvents 1.0)

Conforme §7.2, §7.3 e ADR-0010, todos em envelope CloudEvents 1.0:

- `notifications.requested.v1` (entrada): mesmo payload do REST, key `recipientId`, `idempotencyKey` obrigatório, autorização por ACL do broker mais `PRODUCER_REGISTRY`; erro permanente vai para `notifications.requested.dlt` com headers de diagnóstico.
- `notifications.events.v1` (saída): `rejected`, `delivered`, `failed`, `contact_suppressed`, `consent_changed`; sem conteúdo renderizado e sem contato; publicado pelo Outbox Relay.
- `contacts.events.v1` (entrada): contatos, consentimentos e device tokens para o ContactConsent, mesmo padrão at-least-once do ingress com dedupe em `processed_messages` (ADR-0012).

### 3.3 Mensagens internas SQS (claim check)

Conforme §4.2: envelope comum versionado (`messageId`, `type`, `schemaVersion`, `occurredAt`, `traceparent`, `priorityClass`, `payload`), sem conteúdo sensível nas filas. Payloads: `{ notificationId }` nas filas `core-*`; `{ notificationId, attemptId }` nas filas `dispatch-*`; cada estágio lê o estado no banco.

## 4. Dados e posse de estado

Posse por módulo, conforme o modelo de dados do §6:

| Módulo | Estado que possui |
|---|---|
| Notifications | `NOTIFICATION`, `NOTIFICATION_ATTEMPT`, `POLICY_EVALUATION`, `IDEMPOTENCY_KEY` |
| ContactConsent | `RECIPIENT_PROFILE`, `CONTACT_POINT`, `CONSENT`, `DEVICE_TOKEN` |
| Audit | `AUDIT_EVENT`, `APPROVAL`, com cadeia de hash por partição mensal e append-only por construção (§9.4) |
| TemplateManagement | `TEMPLATE`, `TEMPLATE_VERSION`, `TEMPLATE_CONTENT`, `LAYOUT_VERSION`, `CLASS_POLICY_VERSION` (fase 1a) |
| Dispatch | Nenhum estado próprio (decisão do mapa de módulos) |
| Infraestrutura de plataforma | `outbox`, `processed_messages` |

`PRODUCER_REGISTRY` tem forma canônica no repositório de IaC, materializada em tabela Postgres por job de deploy (§6); `KILL_SWITCH` consta do modelo (§6, §10.3) sem fase atribuída no roadmap (pendência na seção 9).

## 5. Decomposição em fatias

### 5.1 Fatias, dependências e status

Status observado no repositório na data de autoria (2026-08-23):

```text
$ git log --oneline
ed6b840 feat(compliance): reconstruir a trilha de uma notificação por API
f85b5a1 fix(notifications): descartar a forma completa do conteúdo renderizado
695e7f6 feat(notifications): publicar a API de consulta de notificações
229c474 feat(contacts): ingerir contatos e consentimentos do barramento
fbaa33d feat(ingress): consumir notificações do Kafka e publicar os eventos
5b24c95 feat(audit): exportar a trilha para WORM e verificar a cadeia
c44222a feat(dispatch): implementar o despacho pelas filas dispatch-*
5afe146 feat(core): implementar o pipeline de estágios consumindo core-*
d293da9 docs: alinhar design, ADRs e padrões às decisões da fase 1b
2a604dc feat(worker): criar host Platform.Worker com o Outbox Relay
ead7d8b feat(contacts): criar módulo ContactConsent dono dos contatos
02f41ff feat(notifications): criar módulo Notifications com ingestão REST
2a0dd86 refactor(platform): extrair provisionamento de partições do Audit
974b421 docs: alinhar contrato do Dispatch na ADR-0001 e no design
208a81c feat(dispatch): criar módulo Dispatch com adapters SendGrid e FCM
d532414 feat(templates): publicar contratos de leitura em Integration/V1
e57f7db docs: adicionar documentos completos das fases pendentes
6fc95b1 feat(audit): criar módulo Audit dono da trilha com cadeia de hash
5b50445 refactor(templates): mover contrato de política para Integration/V1
```

| Fatia | Entrega | Depende de | Status em 2026-08-23 |
|---|---|---|---|
| B1 | Contrato de política movido para `Integration/V1` de TemplateManagement | Nenhuma | Concluída (commit `5b50445`) |
| B2 | Módulo Audit assume a auditoria (cadeia de hash, contrato de append transacional) | B1 | Concluída (commit `6fc95b1`) |
| B3 | Contratos de leitura de TemplateManagement (`Integration/V1`) | B1 | Concluída (commit `d532414`) |
| B4 | Ingestão REST com idempotência e outbox (`POST /v1/notifications`, §7.1) | B2, B3 | Concluída (commit `02f41ff`) |
| B5 | Host `src/Platform.Worker` e Outbox Relay Worker (§4.2, ADR-0002) | B4 | Concluída (commit `2a604dc`) |
| B6 | ContactConsent v1: modelo, escrita REST (ADR-0012); desvio aceito de escopo: o cache de contatos saiu da B6 e entrou na B7 (decisão de arquitetura: o cache pertence ao leitor no tempo, mas vive no módulo dono atrás do contrato) | B2, B4 | Concluída (commit `ead7d8b`) |
| B7 | Core pipeline consumindo as filas `core-*` (ADR-0003, §4.3) | B3, B5, B6 | Concluída (commit `5afe146`) |
| B8 | Módulo Dispatch: `IChannelProvider`, adapters SendGrid e FCM | B1 | Concluída (commit `208a81c`) |
| B9 | Fatia de despacho: filas `dispatch-*`, estados do attempt, fan-out de push (§4.2, §4.3) | B6, B7, B8 | Concluída (commit `c44222a`) |
| B10 | Kafka Ingress Worker (`notifications.requested.v1`, `.dlt`, `PRODUCER_REGISTRY`, §7.2), emissão de `notifications.events.v1` e publicador Kafka do outbox relay (§1.3) | B4, B5 | Concluída (commit `fbaa33d`) |
| B11 | Ingestão de `contacts.events.v1` no ContactConsent (ADR-0012) | B6, B10 | Concluída (commit `229c474`) |
| B12 | API de consulta (`Notifications.Read`, §7.4) | B7, B9 | Concluída (commit `695e7f6`) |
| B13 | Export WORM e verificação da cadeia de hash (ADR-0006, §9.4) | B2 | Concluída (commit `5b24c95`) |
| B14 | API `/v1/audit/*` respondendo às 8 perguntas do §9.5 | B6, B7, B9, B13 | Concluída (commit `ed6b840`) |
| B15 | Gate de carga do risco 7: p99 de ingestão sob advisory lock; plano B por sub-cadeias | B4, B7, B9, B10, B13 | Em implementação |
| C2 (corretiva) | Cadeia de auditoria: aplicar o índice parcial `(seq DESC) WHERE hash IS NOT NULL`, colapsar quatro round trips sob o lock para três (lock e `nextval` juntos, leitura do elo anterior em statement próprio) e instalar a guarda de nível de isolamento no escritor, que recusa transação fora de READ COMMITTED. Prioridade acima da B16 | B13; medição da B15 | Em implementação |
| C3 (corretiva) | Relay: reivindicação por banda em tempo de varredura completa da tabela. Direção decidida, forma final sai do plano medido: materializar a banda (coluna gerada persistida ou coluna comum escrita pelo escritor, ambas derivadas de `destination` e `priority_class`, que já são conhecidos no insert) com índice parcial em `(transport, banda, created_at) WHERE sent_at IS NULL`. Índice por expressão espelhando o `CASE` está descartado como direção: exige casamento literal da expressão e qualquer edição no `CASE` o derruba em silêncio, que é a mesma classe de fragilidade que custou uma rodada de medição na B15. Prioridade acima da B16 | B5; B10; medição da B15 | Não iniciada |
| B16 | Guia de integração do produtor e biblioteca .NET compartilhada opcional (§15) | B10 | Não iniciada |
| C1 (corretiva) | Transição de fase do conteúdo renderizado (§10.2 A4): pipeline e fallback selam as duas formas, o veredito terminal do despacho descarta a completa na mesma transação, varredura de retaguarda no papel `notifications-maintenance` e backfill do conteúdo já gravado sob gate de configuração | B7, B9 | Concluída (commit `f85b5a1`) |

Nota sobre a ordem: a decisão que ordenava B4 após B2 e B3 foi cumprida; entre B3 e B4 entrou também a promoção do provisionamento de partições a infraestrutura de plataforma (commit `2a0dd86`, §2.6). As erratas documentais das decisões de arquitetura da fase entraram no commit `d293da9`. B15 está em implementação na data, sem commit.

### 5.2 Paralelismos previstos na decisão

- B5 em paralelo com B6.
- B8 em paralelo com B7.
- B10 em paralelo com B9.
- B13 em paralelo com B12.

### 5.3 Estratégia e infraestrutura de teste

A suíte atual usa xUnit com Testcontainers PostgreSQL (`tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:28`), além dos projetos de testes de arquitetura, segurança, unidade e performance em `tests/`. A decisão fixa o roster de infraestrutura de teste da fase: Testcontainers para Postgres, Redis e Kafka; LocalStack para SQS, S3 e KMS; WireMock para SendGrid e FCM no CI. Regras da decisão:

- FCM é sempre fake: nenhum teste chama o FCM real.
- SendGrid sandbox somente atrás de gate explícito; o CI usa WireMock.
- Object Lock em modo Compliance e KMS reais são exercidos apenas em AWS pré-prod, antes do gate de 90 dias sem falha da verificação de cadeia e do export WORM que a ADR-0006 exige para o go-live.

A infraestrutura por fatia deriva do escopo de cada uma sobre esse roster: B4, B6 e B12 usam Postgres e Redis; B5, B7 e B9 acrescentam LocalStack SQS; B8 usa WireMock; B10 e B11 acrescentam Testcontainers Kafka; B13 usa LocalStack S3 e KMS.

**Errata da B15 (2026-08-23): instrumento de medição, não campanha de carga.** A parte da B15 que entra no repositório é uma **sonda de contenção** em `tests/Platform.PerformanceTests`, escrita em .NET puro sobre Testcontainers PostgreSQL, parametrizável por connection string para rodar contra Docker local e, sem recompilar, contra um banco gerido. Não entra ferramenta de carga de terceiros. A razão é o experimento e não a preferência: isolar a contenção do lock exige controlar a partição de destino de cada append, escrevendo com `occurred_at` em meses distintos, e nenhum driver HTTP consegue isso porque a API carimba o instante. A medição precisa ser in-process contra o PostgreSQL real. `BenchmarkDotNet` sai do projeto de performance junto: a decomposição por fases do append dá o mesmo sinal que o micro-benchmark daria, com o volume da partição como variável, que é o que o micro-benchmark não teria.

A campanha do §11.6 do design (sustentado, burst, provedor degradado, failover e rebalance) não entra na fatia: ela é ferramenta de carga mais fakes contra pré-produção, e pertence ao portão de go-live.

O desenho da sonda é fatorial: cinco braços na mesma taxa, no mesmo banco e no mesmo pool, com a partição corrente pré-carregada em três volumes, porque o volume é variável de primeira ordem. O controle escreve em partições mensais distintas, onde o lock nunca disputa, e as partições de controle são carregadas no mesmo volume da corrente, senão o delta contra o tratamento misturaria contenção de lock com custo de varredura. Três sinais precisam se corroborar antes de atribuir um delta ao lock: o delta entre controle e tratamento, a instrumentação separando espera de posse, e a amostragem de `pg_stat_activity` por tipo de evento de espera.

**Nota da B13 sobre o LocalStack (2026-08-23).** O suporte a assinatura assimétrica foi verificado antes da implementação: o LocalStack 4.4 cria chave `ECC_NIST_P256` com uso `SIGN_VERIFY`, assina sobre digest com `ECDSA_SHA_256`, devolve assinatura em DER e expõe a chave pública em SPKI que verifica a assinatura fora do emulador. O caminho de fallback previsto (assinar localmente no CI) não foi necessário e permanece disponível sem mudança de contrato, porque a attestation carrega keyId e algoritmo. Sobre Object Lock, o emulador aceita bucket criado com Object Lock habilitado e registra modo Compliance com data de retenção nos objetos, mas não impõe a imutabilidade; nenhum teste depende de deleção negada, e a demonstração de imutabilidade fica para o smoke de pré-prod.

## 6. Segurança

Aplicam-se as fronteiras já decididas no design de sistema, sem alteração nesta fase:

- PII nasce no estágio Resolve e não volta para trás da fronteira (§4.4); filas internas carregam apenas referências (claim check, §4.2).
- Variáveis sensíveis não trafegam no barramento: template com `sensitive_variables` só aceita solicitação via REST; evento Kafka com variável sensível vai para `.dlt` com rejeição e `audit_event` (§7.2, ADR-0010).
- Autorização em duas camadas na entrada: app roles do Entra no REST e ACL do broker mais `PRODUCER_REGISTRY` no Kafka (§7.2).
- Papéis e segregação de funções conforme §9.1; leitura de conteúdo e contato apenas por `/v1/audit/*` com trilha `audit.read` (fatia B14).
- Cadeia de hash e export WORM protegem a integridade da trilha (ADR-0006); a fatia B13 implementa a verificação e o export.

## 7. Observabilidade e operações

- Job horário de verificação da cadeia com watermark de estabilização e tolerância a buracos de `seq` (§9.4, fatia B13).
- Alarmes operacionais do ingress: partição pausada por mais de 5 minutos gera page; objetivo de restauração do ingress de até 1 hora, abaixo da retenção de 24 horas do tópico de entrada (§4.2, ADR-0010).
- DLQ por fila SQS com alarme por profundidade e `.dlt` Kafka com alarme por taxa; redrive apenas por ferramenta interna auditada (§8).
- Escala por KEDA: consumer lag no ingress, profundidade de fila nos workers (§4.2, §14).
- O perfil de stack (`.araia/stack-profile.yaml`) declara na data `messaging: [sqs]` (AWSSDK.SQS é dependência de produção desde a B5) e `telemetry: none`; kafka e telemetria entram no perfil quando materializarem (pendência na seção 9).

## 8. Rollout e critérios de saída

A migração segue o padrão strangler por template definido no §15: cada template migrado sai do código do produtor somente depois de `published` no hub, começando pelos de maior risco regulatório. Critérios de saída da fase 1b, conforme o roadmap:

1. OTP de login (REST) e confirmação de operação de câmbio (Kafka) migrados do `araia-cambio-api`.
2. `kyc.document.approved` chegando pelo barramento.
3. `GET /v1/audit/notifications/{id}` responde às 8 perguntas do §9.5.
4. Verificação de cadeia de hash rodando.
5. **Contenção da cadeia medida (2026-08-23), critério cumprido.** O critério ligado ao gate do risco 7 é instrumento commitado, linha de base versionada, contenção medida e decisão do plano B registrada. Ele **não** é p99 absoluto provado em AWS: pré-produção não existe na data, e amarrar o p99 absoluto travaria a fase numa dependência de infraestrutura de outra equipe. O p99 absoluto é critério do portão de go-live, junto da campanha de carga do §11.6 e do ensaio de noventa dias que a ADR-0006 exige.

**Decisão registrada: plano B não acionado, regra mantida, foro de reavaliação nomeado.** A medição mostrou que a bancada local não é foro competente para a regra absoluta, porque o braço em que o lock nunca disputa também não alcança o teto exigido, entre 160 e 200 appends/s por partição: reprovar a cadeia com esse número seria confundir instrumento com objeto. A reavaliação vai para infraestrutura representativa, RDS Multi-AZ com PgBouncer, onde o termo dominante da janela de posse, o commit síncrono, existe de verdade. O sub-orçamento de espera mais posse **continua aberto** e se decide lá; a evidência local é direcional e favorável, não aprovação.

**O risco 7 não fecha, muda de forma.** Ele deixa de ser custo quadrático por volume, que o índice de cauda elimina ao tornar a posse independente do tamanho da partição, e passa a ser serialização residual a avaliar em infraestrutura representativa.

**Ressalva do `delivered`, registrada na decisão.** O `delivered` de e-mail só existe com os webhooks do SendGrid, que entram com o Delivery Tracker na fase 2 (§15). Na 1b, os critérios de saída valem para `rejected` e `failed` de ambos os canais e para o `delivered` de push, que por definição do design equivale a aceito pelo FCM (§4.3 "Dispatchers"; §16, risco 4). O evento `araia.notification.delivered.v1` em `notifications.events.v1` (§7.3) é emitido na 1b apenas para push.

Rollback de fase não exige mecanismo próprio: enquanto um template não é migrado, o produtor original continua enviando; a migração por template é reversível interrompendo o strangler antes da remoção do texto no produtor (§15).

## 9. Riscos e pendências

| # | Risco ou pendência | Tratamento e dono | Fonte |
|---|---|---|---|
| 1 | Serialização de inserções em `audit_event` pelo advisory lock da cadeia por partição pode comprometer o p99 de ingestão | Medida em 2026-08-23 pela sonda da B15, em contêiner local com quatro appenders. A contenção é do lock e não do banco: com appenders em partições distintas a espera mediana é 0,62 ms, com todos na mesma partição é 15,4 ms, e a posse fica igual nos dois casos, entre 5 e 6 ms; a amostragem de `pg_stat_activity` acusou 1.853 amostras em `Lock/advisory` no braço da mesma partição e nenhuma no braço de controle. O que domina a posse é a consulta de cauda sem índice (pendência 44): sem índice a posse mediana vai de 5 a 7 ms com dez mil linhas para 232 a 375 ms com dois milhões; com o índice e um round trip a menos ela fica plana em cerca de 2 ms nos três volumes. Ordem decidida: aplicar índice e colapso, remedir em infraestrutura representativa, e só então avaliar o plano B. **Índice e colapso aplicados em 2026-08-24 pela fatia C2 e remedidos na mesma bancada local**, agora com o índice vindo do schema e não da sonda: a posse mediana ficou em 2,255 ms, 1,899 ms e 3,008 ms nos três volumes na forma anterior de quatro round trips, e em 2,433 ms, 2,056 ms e 2,835 ms na forma de três, ou seja plana com o volume nas duas. O round trip a menos não aparece na mediana desta bancada, mas aparece na cauda sob contenção com dois milhões de linhas: janela p99 de 100,017 ms contra 10,725 ms, e 234,8 contra 426,4 appends por segundo. O passo que falta continua sendo infraestrutura representativa, e nenhum percentil desta bancada aprova o sub-orçamento; dono: Engenharia com Arquitetura | §16 risco 7; ADR-0006; fatia B15; fatia C2 |
| 2 | Faixas de política (os seis campos da configuração de classe) dependem de validação externa com Produto e Compliance | Confirmação externa pendente; dono: Produto e Compliance | §16 risco 20 |
| 3 | Reconciliação de e-mail além de poucos dias exige add-on pago da SendGrid Email Activity API | Decisão de contratação registrada como pendente no design; sem efeito nos critérios de saída da 1b (reconciliação é fase 2) | §8; ADR-0008 |
| 4 | Regra `QuietHours` existe na ordem fixa da v1 do estágio Policy, mas a única classe com janela de silêncio (`operational`) entra apenas na fase 2: na 1b a regra roda sem classe que a exercite | Pendência documental registrada pelo architect; comportamento esperado: `quietHours` nulo para `critical` e `transactional` | §4.3 "Regras da v1, em ordem fixa"; §3; §15 fase 2 |
| 5 | `KILL_SWITCH` consta do modelo de dados e da mecânica de segurança, mas nenhuma linha do roadmap atribui sua implementação a uma fase | Pendência documental registrada pelo architect; dono: Arquitetura, na próxima revisão do roadmap | §6; §10.3; §15 |
| 6 | `delivered` de e-mail inexiste na 1b sem webhooks; critério de saída vale para `rejected`, `failed` e `delivered` de push | Ressalva formalizada na seção 8; fecha na fase 2 com o Delivery Tracker | §15; §16 risco 4 |
| 7 | `.araia/stack-profile.yaml` declara `telemetry: none` e `messaging` sem kafka, divergindo do que a fase ainda introduz | Parcialmente resolvida em 2026-08-23: `messaging: [sqs, kafka]` aplicado com a materialização do Kafka Ingress Worker e do publicador Kafka do relay, mais os eixos `object-storage: [s3]`, `key-management: [local, kms]` e `distributed-locks: postgres-advisory` (convenção de advisory lock em espaços de chave disjuntos, cadeia e manutenção); resta a telemetria, que entra quando materializar; dono: Engenharia | `.araia/stack-profile.yaml` |
| 8 | Nota de infraestrutura de plataforma (outbox, relay, `processed_messages`) precisava constar no documento de padrões | Resolvida em 2026-08-23: nota registrada em [modular-monolith-architecture.md](../architecture/standards/modular-monolith-architecture.md); dono: Arquitetura | Decisão do mapa de módulos (2026-08-23) |
| 9 | Topologia de filas (`core-*`, `dispatch-*`, `contacts-changed`, DLQs) sem entrega Terraform | Pendência de entrega, pré-requisito de AWS pré-prod; dono: Engenharia de Plataforma | §4.2; §14; memorando B5/B6 (2026-08-23) |
| 10 | Transporte de observabilidade do health do `Platform.Worker` em produção (endpoint mínimo, publisher ou probe) sem definição | Decisão em aberto | §12; memorando B5/B6 (2026-08-23) |
| 11 | Ingestão aceita `scheduledAt` sem teto enquanto não existe liberador de deferred, e `expires_at` calculado como aceite + TTL é incoerente para agendamentos | Revisão curta do §7.1; dono: Arquitetura com Engenharia | §7.1; memorando B4 (2026-08-23) |
| 12 | Attempts em `unknown` não progridem na 1b: sem tracker e sem reconciliação, um timeout ou 5xx sem veredito estaciona o attempt até a fase 2 | Ressalva aceita na decisão da fatia de despacho; fecha com o Delivery Tracker e a reconciliação da fase 2; dono: Engenharia | §5.2; decisão da fatia de despacho (2026-08-23) |
| 13 | Planos de classe da 1b restritos a `email` e `push`: um plano publicado com `sms` ou `whatsapp` faria o fallback terminar em `failed` por falta de adapter hospedado | Restrição operacional das políticas publicadas na 1b; os canais entram na fase 2; dono: Engenharia com Produto | §15; decisão da fatia de despacho (2026-08-23) |
| 14 | O evento Kafka de `failed` em `notifications.events.v1` aguarda a fatia B10: a notificação transita a `failed` no banco sem emissão externa até lá | Sequenciamento aceito; a B10 publica os eventos de saída; dono: Engenharia | §7.3; fatia B10 |
| 15 | O snapshot do ContactConsent não expõe mais o token do dispositivo: o envio revela o token por leitura dedicada (`RevealDeviceTokenAsync`), desvio aceito do modelo que expunha o token no snapshot | Desvio aceito na decisão da fatia de despacho: fronteira de PII mais estreita, todo egresso de token vira ponto de chamada explícito; dono: Engenharia | §4.4; decisão da fatia de despacho (2026-08-23) |
| 16 | `MaxConcurrency` por provedor segue em configuração com default de desenvolvimento; a calibração aos limites contratados de SendGrid e FCM está pendente | Realvada em 2026-08-23 para a campanha de carga de pré-produção: o insumo é o limite contratado, que é fato comercial ainda não obtido, e o fake do provedor não reproduz rate limit, então não há o que calibrar na bancada local; dono: Engenharia de Plataforma | §11.3; decisão da fatia de despacho (2026-08-23); fatia B15 |
| 17 | Prazo legal de retenção do bucket WORM ainda não confirmado; o código aplica cinco anos como piso conservador em cada objeto gravado, e a retenção em modo Compliance é irreversível por definição | Confirmar com o Jurídico antes do primeiro export em pré-prod; dono: Compliance com Jurídico | ADR-0006; §9.6 |
| 18 | Residência em banco antes do drop da partição destacada usa noventa dias como default, sem decisão de negócio | Decidir com Produto e Compliance antes de ligar o gate de drop; dono: Produto com Compliance | §9.6; decisão da fatia B13 (2026-08-23) |
| 19 | O ciclo completo de fechamento nunca rodou fora de teste; ligar os gates em produção sem ensaio arrisca destacar partição com evidência incompleta | Ensaio do ciclo completo em pré-prod é pré-requisito do gate de noventa dias sem falha que a ADR-0006 exige para o go-live; dono: Engenharia com SRE | ADR-0006; §15 |
| 20 | Cadência da verificação integral (semanal por partição aberta) foi escolhida sem medição do custo de releitura da partição corrente | **Rótulo de curva pré-índice removido em 2026-08-24, e a pendência muda de forma.** Direção medida com o índice de cauda aplicado: a releitura integral **não melhorou**, e a hipótese de que a superlinearidade fosse a ordenação sem índice do caminho quente não se sustentou, porque o índice de cauda é parcial e a leitura por faixa de `seq` não carregava o predicado dele. Só a direção é usada aqui: o delta entre rodadas é medição longa dominada por IO em host compartilhado e não é insumo de decisão. Causa identificada e endereçada pela pendência 50, com a leitura separada em duas metades indexadas. **A cadência deixa de ser "de quanto em quanto tempo cabe uma passagem inteira" e passa a ser "quantos blocos por execução"**, o que a desacopla do tamanho da partição e sobrevive ao crescimento de volume. Fixar só depois da remedição com o mesmo instrumento, sem rótulo provisório desta vez; dono: Engenharia | §9.4; fatia B15; fatia C2 |
| 21 | Usuários LOGIN por ambiente, bucket WORM com Object Lock, chave KMS de assinatura e o deployment do papel `audit-maintenance` não têm entrega Terraform | A migração cria apenas a role de concessão `audit_appender` (NOLOGIN) e os grants; o restante é entrega de infraestrutura declarativa; dono: Engenharia de Plataforma | §14; decisão da fatia B13 (2026-08-23) |
| 22 | O ERD do §6 e a trilha divergem quanto ao `notification_id` do `audit_event`: a trilha grava `entity_type` mais `entity_id` genéricos | Decidir na fatia da API de auditoria (B14), que precisa consultar por notificação; dono: Arquitetura com Engenharia | §6; §9.5; fatia B14 |
| 23 | A cadeia cobre o texto `canonical`; a verificação compara também as colunas escalares da linha com esse texto, mas não a coluna `details`, porque o armazenamento jsonb reescreve os bytes na leitura e uma comparação exata alarmaria por formatação | Critério de aceite da B14: a API `/v1/audit/*` monta a resposta a partir do parse do texto `canonical`, tratando `details` apenas como superfície de consulta e indexação, nunca como payload de prova; servir `details` da coluna exige trazer a comparação de volta com oráculo de valor parseado; dono: Engenharia com Arquitetura | §9.4; decisão da fatia B13 (2026-08-23) |
| 24 | O manifest do export carrega `windowFrom` e `windowTo` sem declarar que a janela é do gatilho: a reivindicação autoritativa de cobertura é a faixa contígua de seq, e um auditor externo pode ler a janela como cobertura por `occurred_at` | Documentar a semântica no guia de verificação independente, ou acrescentar os limites de `occurred_at` do segmento com bump de `formatVersion` (barato enquanto nada foi exportado em produção); decidir antes do smoke de pré-prod; dono: Arquitetura com Engenharia | §9.4; ratificação da fatia B13 (2026-08-23) |
| 25 | Assimetria de autorização por `application` entre os dois caminhos de entrada: o `PRODUCER_REGISTRY` autoriza a tripla principal, `application` e classe, enquanto as app roles do Entra autorizam só a classe, de modo que um produtor REST autorizado para uma classe pode declarar qualquer `application` no corpo. É lacuna do REST, não do Kafka; a fatia do barramento apenas a tornou visível | Decidir a forma do vínculo principal para `application` no caminho REST (app role por aplicação, claim dedicada ou consulta ao mesmo registro) antes do onboarding do segundo produtor REST; dono: Arquitetura com Segurança | §7.1; §7.2; §10.2 A1; fatia B10 (2026-08-23) |
| 26 | Retenção de `contacts.events.v1` abaixo da retenção de `processed_messages`: a purga de dedupe usa quinze dias, folgada para os tópicos de 24 h, mas o tópico de contatos ainda não tem retenção declarada e uma retenção maior que quinze dias reabriria a janela de reprocessamento sem marca | Resolvida na parte documental em 2026-08-23: a errata da B11 declara 24 h para `contacts.events.v1` na tabela de topologia (§4.2), dentro da janela de purga de quinze dias; resta conferir o valor aplicado no provisionamento (pendência 32); dono: Engenharia de Plataforma com Engenharia | §4.2; §6; fatia B11 |
| 27 | Estratégia de purga de `processed_messages` em volume: a purga por idade varre a tabela inteira, e com o ingress do barramento a taxa de marcas cresce com o tráfego de entrada, não só com o interno | **Fechada em 2026-08-23 por premissa incorreta, não por remédio.** A afirmação de varredura integral que abriu a pendência nunca foi verificada contra o DDL: a tabela tem `ix_processed_messages_processed_at` desde a migração que a criou, e o predicado da purga é exatamente por essa coluna. Nenhum problema existiu e nada foi corrigido; registrar como remediada criaria o precedente falso de que houve defeito e alguém o resolveu. Uma rodada removeu um milhão de marcas em 0,42 s, cobrindo cerca de 2 % da janela do braço de append concorrente, e o deslocamento observado no p99 do append ficou dentro do ruído entre rodadas (razão 0,61 numa rodada e 0,78 em outra, ambas abaixo de 1). Reabrir só com gatilho nomeado: rodada de purga cuja duração se aproxime do intervalo entre rodadas, ou marcas em ordem de grandeza acima de um milhão; dono: Engenharia | §6; §11.3; fatia B15 |
| 28 | Provisionamento Terraform dos tópicos Kafka, das ACLs, das partições e das retenções (`notifications.requested.v1`, `notifications.requested.dlt`, `notifications.events.v1`, `contacts.events.v1`) sem entrega, no mesmo estado da topologia de filas da pendência 9 | Pendência de entrega, pré-requisito de AWS pré-prod; o hub nunca cria tópico, então sem a entrega o relay deixa as linhas pendentes e o ingress não assina; dono: Engenharia de Plataforma | §4.2; §7.2; §14 |
| 29 | `failed.reason` viaja com o vocabulário de erro do despacho (código do provedor, `no-active-device-token`), não com o catálogo canônico de rejeição, e o §7.3 não distingue os dois | Resolvida em 2026-08-23 pela errata da B12: o §7.3 passa a declarar que o catálogo canônico vale só para `rejected.reason` e que `failed.reason` é vocabulário aberto de falha de entrega. A consulta expõe os dois em membros distintos (`policyEvaluations[].reason` e `attempts[].errorCode`). Consequência registrada: agregação e alarme por motivo de falha toleram cardinalidade aberta e agrupam por família de código, e nenhum consumidor valida `failed.reason` contra o catálogo; dono: Arquitetura com Engenharia | §7.3; §7.4; fatia B10 (2026-08-23); fatia B12 |
| 30 | Sem alarme por taxa sobre a contagem de dead letters segmentada por motivo, uma recusa que não chega ao tópico de eventos vira ponto cego. O caso nomeado é `payload-invalid` sem destinatário: o contrato de saída exige subject e não há um, então a recusa só existe na `.dlt` e no log | Mitigação aplicada agora: a produção da dead letter emite log estruturado com o motivo em campo próprio (`IngressDeadLetterWriter`), o que permite contagem por motivo assim que houver coleta. Falta o alarme por taxa, amarrado à fatia que introduzir telemetria, junto da revisão do eixo `telemetry` do stack profile (pendência 7); dono: Engenharia com SRE | §12; §7.2; ratificação do desvio 2 da fatia B10 (2026-08-23) |
| 31 | A semântica de conjunto completo da ingestão de contatos só é segura se o sistema de cadastro for o dono único dos pontos de contato do destinatário; se outra origem escrevesse contatos, uma declaração do cadastro apagaria o que não conhece | Resolvida em 2026-08-23: confirmação de negócio de que o cadastro é o dono único. Efeito na implementação: o comando não ganha escopo por canal, e o evento continua carregando o conjunto completo, com o hub marcando como removido o que não veio, exatamente como o `PUT` correspondente. Registrada na ADR-0012 e no §7.2; dono: Produto com Arquitetura | ADR-0012; §7.2; fatia B11 |
| 32 | Provisionamento Terraform de `contacts.events.v1` e `contacts.events.dlt`: partições, retenções (24 h e 14 dias) e ACLs (escrita restrita ao principal do cadastro na entrada, leitura restrita ao time do cadastro e à operação do hub na dead-letter) sem entrega, no mesmo estado da pendência 28 | Pendência de entrega, pré-requisito de AWS pré-prod; o hub nunca cria tópico, então sem a entrega o papel `contacts-ingress` não assina e a produção da dead letter falha; a ACL de escrita é a primeira das duas camadas de autorização da entrada, e a lista de origens aceitas do papel é a segunda. Recomendação de topologia do architect (2026-08-23): três partições em `contacts.events.v1`. A ordem de que o contrato depende é preservada por construção, porque a key é o `recipientId` e a mesma key cai sempre na mesma partição, então pontos de contato continuam chegando antes do consentimento que neles se ancora; o ganho é que a pausa por conflito de escrita deixa de parar a ingestão inteira e passa a parar só a partição afetada; dono: Engenharia de Plataforma | §4.2; §7.2; fatia B11 |
| 33 | A `contacts.events.dlt` não admite redrive: o corpo publicado é resumo reconstruído por lista de permissão, não cópia fiel, porque todo corpo de entrada carrega dado pessoal em claro e a dead-letter retém quatorze vezes mais | Consequência aceita da errata de redação da B11: a correção de uma declaração recusada é o cadastro reemitir o estado correto, idempotente por construção; o produtor diagnostica por motivo, coordenadas e `id` do CloudEvent, e alcança o corpo original na entrada dentro das 24 h. Precisa constar do guia de integração do produtor (fatia B16); dono: Engenharia com o time do cadastro | §7.2; §8; fatia B11 |
| 34 | O alarme por taxa da `contacts.events.dlt` precisa tolerar registro duplicado: a dead letter é produzida antes da marca de dedupe commitar, então um laço de falhas entre as duas grava o mesmo motivo mais de uma vez e o alarme passaria a medir instabilidade do hub em vez de erro do produtor | Contar coordenadas de origem distintas, ou tolerar duplicata na janela, quando o alarme for construído; amarrado à fatia que introduzir telemetria, junto da pendência 7 e do alarme por motivo da pendência 30; dono: Engenharia com SRE | §12; §7.2; fatia B11 |
| 35 | Refinamento opcional com gatilho nomeado: o processador de contatos devolve `Retry` no primeiro conflito de escrita concorrente, e uma retentativa em processo antes de pausar a partição pode ser mais barata | Não implementar antes do dado: o gatilho é a telemetria mostrar conflitos com frequência não desprezível na ingestão de contatos; até lá a pausa é o comportamento correto e visível; dono: Engenharia | §7.2; fatia B11 |
| 36 | Ator de sistema sem versão da imagem na trilha: `DeviceTokenInvalidation` grava `actor_id = dispatcher`, enquanto o §9.3 pede nome do worker mais versão da imagem para ator de sistema. A divergência é anterior à ingestão de contatos e vale para os demais atores de sistema já gravados | Decidir a forma do identificador (sufixo de versão no `actor_id` ou campo próprio nos detalhes) e aplicá-la de uma vez em todos os atores de sistema, porque valor novo em trilha append-only deixa as linhas já gravadas ambíguas; dono: Engenharia | §9.3; fatia B9 |
| 37 | A consulta da 1b não tem escopo por `application`: quem porta `Notifications.Read` enxerga notificação de qualquer aplicação. O gate é de rota, e as contenções que existem são a busca por identidade exata, a janela obrigatória, o rate limit próprio e o log de acesso | Decidir a forma do escopo por `application` na leitura, amarrada à decisão do vínculo entre principal e aplicação da pendência 25, porque as duas dependem do mesmo vínculo inexistente hoje. Prazo: antes do primeiro consumidor da consulta fora do time da plataforma; dono: Arquitetura com Segurança | §7.4; §9.1; §10.2 A3; fatia B12 |
| 38 | Réplica de leitura física sem entrega Terraform: a costura existe no código (`ReadConnectionString` opcional, contexto somente leitura próprio), mas sem réplica provisionada a consulta lê o primário e concorre com o caminho quente | Pendência de entrega, no mesmo estado da topologia de filas da pendência 9 e dos tópicos da 28. A parte de medição foi absorvida em 2026-08-23 pelo braço de interferência da sonda, que responde a pergunta genérica de que ela dependia: uma varredura concorrente sobre o mesmo banco desloca o p99 do append? Na purga de um milhão de marcas não deslocou (pendência 27). **Reaberta para reconferência pela regra da pendência 49**: a afirmação de que a consulta compete com o caminho quente é da mesma classe da premissa que derrubou a pendência 27, custo de consulta afirmado sem plano nem definição de índice, e nunca foi verificada. Conferir o plano da consulta antes de manter a afirmação; dono: Engenharia de Plataforma | §11.3; §4.3; fatia B12; fatia B15 |
| 39 | O mascaramento de contato da consulta responde também sobre ponto de contato já removido, marcando que não está mais ativo. É ampliação deliberada de acesso histórico: o dado de um contato apagado do conjunto ativo continua legível na forma mascarada enquanto a notificação existir | Confirmar com Compliance, junto da revisão do §9.6 sobre retenção e acesso, que a forma mascarada de um contato removido pode ser servida ao atendimento. A alternativa, caso a resposta seja negativa, é a consulta devolver só o identificador do ponto de contato quando ele estiver removido; dono: Compliance com Arquitetura | §7.4; §9.6; ADR-0012; fatia B12 |
| 40 | A consulta expõe `policyEvaluations[].reason` sem projeção da evidência da regra, e isso deixa lacuna de triagem real: com `reason: no-valid-contact` e nada mais, o atendimento não distingue "o cliente precisa atualizar o cadastro" de "o template não tem conteúdo publicado para o canal", que pedem ações opostas. O desvio para `/v1/audit/*` não é caminho operacional, porque aquele papel é de Compliance e Auditoria Interna e lê conteúdo renderizado e contato completo | Decidir o recorte da projeção na mesma rodada da pendência 39. A forma já está decidida e a reabertura é implementação, não desenho: nunca o jsonb bruto, sempre projeção por lista de permissão por regra, no mesmo precedente da dead letter de contatos. Campos candidatos: `remaining`, `plan`, `withContent`, `reachable`, `selected` (ChannelSelection); `purpose`, `granted`, `denied` (ConsentGate); `windowSeconds`, `acquired`, `failOpen` (DedupeWindow); `window` e `releaseAt` (QuietHours). Fora da projeção por default: `timezone` e `localTime`, que não respondem pergunta de triagem que `window` e `releaseAt` já não respondam. O meio-termo de expor só os campos derivados do catálogo foi descartado: com `withContent` cheio e `selected` vazio, o fato pessoal sai por eliminação; dono: Arquitetura com Compliance | §7.4; §9.6; fatia B12; fatia B14 |
| 41 | Lacuna entre o §10.2 A4 e o código, encontrada em 2026-08-23: nenhuma fatia implementava a transição de fase do conteúdo renderizado. O `RenderStage` selava apenas a forma completa, o despacho a consumia e ninguém a substituía, de modo que conteúdo de template com variável sensível ficava em forma completa até o drop da partição; os dois hashes já eram gravados, o que prova que a transição estava projetada e ficou de fora | Corrigida em 2026-08-23 pela fatia corretiva C1: pipeline e fallback selam as duas formas no envelope cifrado, o veredito terminal do despacho (`sent`, `failed`, `unknown`) descarta a forma completa na mesma transação, a varredura de retaguarda alcança a tentativa que nunca chega a veredito e o backfill sob gate substitui o conteúdo já gravado apenas quando o hash recomputado confere com o `content_hash_masked`. Consequências registradas: o papel de worker `notifications-maintenance` precisa de entrega de deployment própria, no mesmo estado da pendência 9, e a janela da varredura fica além do TTL mais uma carência, de modo que uma mensagem entregue depois desse ponto enviaria a forma mascarada; dono: Engenharia com Arquitetura | §10.2 A4; §9.4; fatia B7; fatia B9 |
| 42 | Duas posições de Compliance pendentes sobre a superfície de auditoria, ambas de acesso e não de implementação. A primeira é a revelação do valor de contato **em claro** para auditoria, inclusive de ponto de contato já removido: o membro está nomeado e adiado, e até o aval ele fica ausente da resposta, com o motivo declarado no contrato. A segunda é exigir justificativa registrada por chamada no endpoint de conteúdo, como o PIM já exige do Platform Admin: hoje a rota tem papel dedicado, limite de taxa próprio, alarme por volume e `audit.read` por chamada, mas nenhum campo obriga o auditor a dizer por que abriu aquele conteúdo | Levar as duas à mesma rodada das pendências 39 e 40, junto da revisão do §9.6 sobre retenção e acesso. Consequência da primeira, se a resposta for positiva: entra membro dedicado no contrato do ContactConsent, com trilha própria de divulgação. Consequência da segunda, se for positiva: a rota passa a exigir um campo de justificativa e a gravá-lo nos `details` do `audit.read`; dono: Compliance com Arquitetura | §7.4; §9.1; §9.6; §10.2 A6; fatia B14 |
| 43 | O portão por pull request não tem cabeamento: o repositório não tem pipeline (`.github` só tem template de PR), então a sonda em modo `smoke` existe e roda por comando, mas nada a executa automaticamente | Cabear o modo `smoke` no pipeline quando ele existir. A métrica de guarda deixou de ser posse absoluta em 2026-08-23, justamente porque absoluta ela media o host: a posse do mesmo código variou perto de 30 % entre rodadas na mesma máquina, que era a própria tolerância. Ela passa a ser posse normalizada pelo custo de uma ida trivial ao banco medida na mesma rodada, mais o crescimento da posse entre dois volumes na mesma rodada, ambas razões internas à rodada; a guarda absoluta que resta é frouxa, de uma ordem de grandeza. Condição que permanece: a linha de base é regravada no próprio runner, como mediana de três rodadas; dono: Engenharia de Plataforma | §11.6; fatia B15 |
| 44 | Nenhum índice atende à consulta de cauda que roda dentro do lock em todo append, e a mesma ausência afeta o verificador horário e o export por faixa de `seq` | **Resolvida para o caminho quente em 2026-08-24 pela fatia C2, e apenas para ele.** Achado da medição de 2026-08-23, com plano lido sobre partição de dois milhões de linhas: sem índice a consulta custa 330 ms lendo 181.230 buffers; com índice parcial em `(occurred_at, seq DESC)` o planejador ignora o índice e repete a varredura em 371 ms; com índice parcial em `(seq DESC)` custa 0,174 ms lendo 4 buffers. A forma composta não resolve porque a poda de partição já satisfez o predicado de tempo, deixando a coluna da frente como prefixo inútil, e porque o predicado restante é faixa e não igualdade. Forma ratificada pelo architect em 2026-08-23 e aplicada como `ix_audit_event_chain_tail`, `(seq DESC) WHERE hash IS NOT NULL`, criado **na partição-mãe** para que o PostgreSQL o propague a toda partição existente e futura. Medido depois da aplicação, com o schema das migrações: a cauda passa a ser `Index Scan` sobre o índice propagado da partição e custa 0,040 ms lendo 3 buffers com dez mil linhas, 0,042 ms lendo 4 com quinhentas mil e 0,046 ms lendo 4 com dois milhões, ou seja plana com o volume. A segunda nota da errata, de que o mesmo índice serviria o verificador horário e o export por faixa de `seq`, era inferência sobre a forma do índice e não leitura de plano: **o índice de cauda serve um caminho**, e o resto virou a pendência 50; dono: Engenharia | §9.4; §11.3; fatia B15; fatia C2 |
| 45 | Alarme de idade do outbox (`outbox_lag_seconds` com limiar de 2 s no §11.2, `sqs_oldest_message_age` no §11.5) não existe e não tem contra o que ser calibrado | Fora do escopo da medição por falta de insumo: o perfil de stack declara `telemetry: none` e não existe coleta contra a qual calibrar limiar. Amarrado à fatia que introduzir telemetria, junto das pendências 7, 30 e 34. Ressalva acrescentada na reconferência pela regra da pendência 49: os próprios limiares de 2 s e o orçamento de 300 ms do §11.2 para o trecho outbox são valores declarados sem medição, e a medição da B15 mostrou que a reivindicação por banda sozinha já os estoura (pendência 46), então o limiar precisa ser refeito depois da fatia corretiva do relay, não antes; dono: Engenharia com SRE | §11.2; §11.5; §12; fatia B15 |
| 46 | Custo do `ORDER BY` por banda no relay: a banda é expressão `CASE` no `WHERE` que nenhum índice atende | **Promovida a fatia corretiva C3 em 2026-08-23, acima da B16, porque é defeito no caminho quente do OTP e não pendência de calibração.** Medido com backlog sintético de um milhão de linhas pendentes, lendo o plano: `ix_outbox_pending` não é usado em nenhuma banda, o plano é varredura sequencial da tabela inteira mais ordenação externa em disco (4,3 MB na banda de autenticação, 49 MB na `critical`, 43 MB na `operational`), e uma reivindicação de lote de cem linhas custa de 621 ms a 1.275 ms, descartando 981.434 linhas pelo filtro na banda de autenticação. Isso sozinho estoura os 300 ms que o §11.2 dá ao trecho outbox até `core-auth`, antes do laço de 100 ms, e o §11.2 já diz em texto que o relay é o componente que mais facilmente estoura o orçamento. Direção na C3; dono: Engenharia | §4.2; §11.2; §11.3; fatia B15; fatia C3 |
| 47 | Transação curta de rejeição ou duplicata: a ingestão que recusa abre transação própria só para gravar a trilha, sem efeito de negócio para amortizar a posse do lock | Resolvida em 2026-08-23 pela medição, como parâmetro da mistura real e não como braço separado: a posse mediana da rejeição foi 5,99 ms contra 6,11 ms do aceite completo, diferença dentro do ruído. O trabalho de negócio roda antes do lock e por isso não amortiza nada, e a rejeição não é pior nem melhor que qualquer outro append. O que segura o lock é a consulta de cauda, igual para todos (pendência 44); dono: Engenharia | §7.1; §11.3; fatia B15 |
| 48 | Crescimento de `platform.outbox` pelas linhas com `sent_at` preenchido: não está confirmado se existe purga ou arquivamento delas. O índice parcial da C3 resolve leitura, mas linha entregue que nunca sai da tabela é problema diferente, de tamanho de tabela e de trabalho de vacuum | Confirmar se existe purga ou arquivamento e, se não existir, decidir a política junto da C3, porque as duas mexem na mesma tabela e a ordem importa: criar índice antes de decidir retenção é criar índice sobre volume que ninguém dimensionou; dono: Engenharia | §4.2; §6; fatia C3 |
| 49 | Regra de abertura de pendência, nascida do fechamento da 27 por premissa incorreta | **Pendência que afirma custo de consulta cita definição de índice ou plano de execução, ou não é aberta.** A 27 afirmou varredura integral sem abrir a migração, e a tabela tinha índice pela coluna do predicado desde a criação. Reconferência aplicada na mesma data: a 38 foi reaberta para conferência (afirma competição com o caminho quente sem plano), a 45 ganhou ressalva (limiares declarados sem medição). Pendências de telemetria não são dessa classe, porque afirmam ausência de coleta e não custo de consulta; dono: Arquitetura com Engenharia | seção 9; fatia B15 |
| 50 | A leitura por faixa de `seq`, que verificação integral e export compartilham, não é atendida por nenhum índice, e o índice de cauda da C2 não a atende por construção | Aberta em 2026-08-24 com plano de execução, conforme a regra da pendência 49. O índice de cauda é parcial em `hash IS NOT NULL` e um índice parcial só casa com statement que carregue o predicado dele; a leitura compartilhada por `AuditTrailReader` precisa devolver também as linhas pré-cadeia, que não têm hash, então acrescentar o predicado a ela mudaria o que o export enxerga e não é opção. Medido sobre a partição corrente, lote de vinte mil linhas: 24,835 ms e 2.601 buffers com dez mil linhas, 438,547 ms e 170.438 buffers com quinhentas mil, 2.133,796 ms e 672.321 buffers com dois milhões, este último com `Gather Merge` e ordenação externa em disco. Como a releitura integral avança por lotes, o custo do mês é superlinear e é a causa da curva da pendência 20. O mesmo vale, em menor escala, para o `MAX(seq)` do planejamento do export: 212,134 ms e 182.097 buffers com dois milhões. **Decidida pelo architect em 2026-08-24 e aplicada na mesma fatia: separar o leitor.** Duas consultas, encadeadas e pré-cadeia, cada uma carregando o seu predicado e atendida pelo seu índice parcial, mescladas fora do banco, com paginação por chave sobre `seq` em blocos. O índice não parcial foi recusado por direção de custo: cobraria manutenção em todo insert da tabela mais quente para servir dois trabalhos de retaguarda. `ix_audit_event_prechain_seq` é grátis no caminho quente, porque pré-cadeia é conjunto fechado e linha nova não casa o predicado. O `MAX(seq)` do export passa a carregar os predicados nas duas metades. **Falseabilidade declarada**: a ordenação em disco tem que sumir do plano da faixa de `seq`; se não sumir, o recurso é índice único não parcial, com o parcial removido, e exige nova ratificação. Aberta apenas para a remedição da curva; dono: Arquitetura com Engenharia | §9.4; §11.3; ADR-0006; fatia C2 |
| 51 | Transação de terceiro que engole a exceção do escritor da trilha pode ficar aberta indefinidamente segurando o lock consultivo da partição | O escritor recusa transação fora de READ COMMITTED conferindo também o nível que o servidor reporta, e essa conferência viaja no statement que já toma o lock, então a recusa acontece com o lock na mão. O lock é de transação e cai no rollback, logo o perigo não é a recusa e sim transação aberta indefinidamente, que é problema de infraestrutura e não de código. **Fecha fora do código**: `idle_in_transaction_session_timeout` em parâmetro de banco ou de papel, junto das pendências de Terraform. Mais código no escritor para isso seria tratar sintoma de conexão ociosa dentro do caminho probatório; dono: Engenharia de Plataforma | §9.4; §11.3; ADR-0006; fatia C2 |

## 10. Referências

- [Design de sistema do Notification Hub](../notification-hub-system-design.md): §3 (taxonomia), §4 (arquitetura e topologia), §5 (fluxos e máquina de estados), §6 (modelo de dados), §7 (contratos), §8 (confiabilidade), §9 (governança e auditoria), §15 (roadmap), §16 (riscos).
- [ADR-0002: SQS com SDK direto da AWS](../ADR-0002-sqs-sdk-direto.md)
- [ADR-0003: pipeline de estágios com resultado explícito](../ADR-0003-pipeline-de-estagios.md)
- [ADR-0006: auditoria em banco, append-only, com hash chain e export WORM](../ADR-0006-auditoria-append-only-hash-chain-worm.md)
- [ADR-0008: entrega at-least-once com idempotência](../ADR-0008-at-least-once-com-idempotencia.md)
- [ADR-0010: Kafka para integração, SQS para filas de trabalho internas](../ADR-0010-kafka-integracao-sqs-filas-internas.md)
- [ADR-0012: Contact & Consent como fonte da verdade](../ADR-0012-contact-consent-fonte-da-verdade.md)
- [Padrão de arquitetura do monólito modular](../architecture/standards/modular-monolith-architecture.md)
- Decisão aceita do mapa de módulos da fase 1b (dotnet-architect, 2026-08-23), formalizada por este documento.
- Evidência de repositório citada em linha: `src/Platform.Api/Modules/Audit/Integration/V1/`, `src/Platform.Api/Modules/TemplateManagement/Integration/V1/`, `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:28`, commits `5b50445` e `6fc95b1`.
