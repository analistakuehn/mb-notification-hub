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
| B7 | Core pipeline consumindo as filas `core-*` (ADR-0003, §4.3) | B3, B5, B6 | Em implementação |
| B8 | Módulo Dispatch: `IChannelProvider`, adapters SendGrid e FCM | B1 | Concluída (commit `208a81c`) |
| B9 | Fatia de despacho: filas `dispatch-*`, estados do attempt, fan-out de push (§4.2, §4.3) | B6, B7, B8 | Não iniciada |
| B10 | Kafka Ingress Worker (`notifications.requested.v1`, `.dlt`, `PRODUCER_REGISTRY`, §7.2), emissão de `notifications.events.v1` e publicador Kafka do outbox relay (§1.3) | B4, B5 | Não iniciada |
| B11 | Ingestão de `contacts.events.v1` no ContactConsent (ADR-0012) | B6, B10 | Não iniciada |
| B12 | API de consulta (`Notifications.Read`, §7.4) | B7, B9 | Não iniciada |
| B13 | Export WORM e verificação da cadeia de hash (ADR-0006, §9.4) | B2 | Não iniciada |
| B14 | API `/v1/audit/*` respondendo às 8 perguntas do §9.5 | B6, B7, B9, B13 | Não iniciada |
| B15 | Gate de carga do risco 7: p99 de ingestão sob advisory lock; plano B por sub-cadeias | B4, B7, B9, B10, B13 | Não iniciada |
| B16 | Guia de integração do produtor e biblioteca .NET compartilhada opcional (§15) | B10 | Não iniciada |

Nota sobre a ordem: a decisão que ordenava B4 após B2 e B3 foi cumprida; entre B3 e B4 entrou também a promoção do provisionamento de partições a infraestrutura de plataforma (commit `2a0dd86`, §2.6). B7 está em implementação na data, sem commit.

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

A infraestrutura por fatia deriva do escopo de cada uma sobre esse roster: B4, B6 e B12 usam Postgres e Redis; B5, B7 e B9 acrescentam LocalStack SQS; B8 usa WireMock; B10 e B11 acrescentam Testcontainers Kafka; B13 usa LocalStack S3 e KMS; B15 exercita a stack completa.

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

**Ressalva do `delivered`, registrada na decisão.** O `delivered` de e-mail só existe com os webhooks do SendGrid, que entram com o Delivery Tracker na fase 2 (§15). Na 1b, os critérios de saída valem para `rejected` e `failed` de ambos os canais e para o `delivered` de push, que por definição do design equivale a aceito pelo FCM (§4.3 "Dispatchers"; §16, risco 4). O evento `araia.notification.delivered.v1` em `notifications.events.v1` (§7.3) é emitido na 1b apenas para push.

Rollback de fase não exige mecanismo próprio: enquanto um template não é migrado, o produtor original continua enviando; a migração por template é reversível interrompendo o strangler antes da remoção do texto no produtor (§15).

## 9. Riscos e pendências

| # | Risco ou pendência | Tratamento e dono | Fonte |
|---|---|---|---|
| 1 | Serialização de inserções em `audit_event` pelo advisory lock da cadeia por partição pode comprometer o p99 de ingestão | Gate de carga da fatia B15 valida o p99; plano B decidido: sub-cadeias por `application` dentro da partição; dono: Engenharia com Arquitetura | §16 risco 7; ADR-0006 |
| 2 | Faixas de política (os seis campos da configuração de classe) dependem de validação externa com Produto e Compliance | Confirmação externa pendente; dono: Produto e Compliance | §16 risco 20 |
| 3 | Reconciliação de e-mail além de poucos dias exige add-on pago da SendGrid Email Activity API | Decisão de contratação registrada como pendente no design; sem efeito nos critérios de saída da 1b (reconciliação é fase 2) | §8; ADR-0008 |
| 4 | Regra `QuietHours` existe na ordem fixa da v1 do estágio Policy, mas a única classe com janela de silêncio (`operational`) entra apenas na fase 2: na 1b a regra roda sem classe que a exercite | Pendência documental registrada pelo architect; comportamento esperado: `quietHours` nulo para `critical` e `transactional` | §4.3 "Regras da v1, em ordem fixa"; §3; §15 fase 2 |
| 5 | `KILL_SWITCH` consta do modelo de dados e da mecânica de segurança, mas nenhuma linha do roadmap atribui sua implementação a uma fase | Pendência documental registrada pelo architect; dono: Arquitetura, na próxima revisão do roadmap | §6; §10.3; §15 |
| 6 | `delivered` de e-mail inexiste na 1b sem webhooks; critério de saída vale para `rejected`, `failed` e `delivered` de push | Ressalva formalizada na seção 8; fecha na fase 2 com o Delivery Tracker | §15; §16 risco 4 |
| 7 | `.araia/stack-profile.yaml` declara `telemetry: none` e `messaging` sem kafka, divergindo do que a fase ainda introduz | Parcialmente resolvida em 2026-08-23: `messaging: [sqs]` aplicado; kafka e telemetria entram quando materializarem; dono: Engenharia | `.araia/stack-profile.yaml` |
| 8 | Nota de infraestrutura de plataforma (outbox, relay, `processed_messages`) precisava constar no documento de padrões | Resolvida em 2026-08-23: nota registrada em [modular-monolith-architecture.md](../architecture/standards/modular-monolith-architecture.md); dono: Arquitetura | Decisão do mapa de módulos (2026-08-23) |
| 9 | Topologia de filas (`core-*`, `dispatch-*`, `contacts-changed`, DLQs) sem entrega Terraform | Pendência de entrega, pré-requisito de AWS pré-prod; dono: Engenharia de Plataforma | §4.2; §14; memorando B5/B6 (2026-08-23) |
| 10 | Transporte de observabilidade do health do `Platform.Worker` em produção (endpoint mínimo, publisher ou probe) sem definição | Decisão em aberto | §12; memorando B5/B6 (2026-08-23) |
| 11 | Ingestão aceita `scheduledAt` sem teto enquanto não existe liberador de deferred, e `expires_at` calculado como aceite + TTL é incoerente para agendamentos | Revisão curta do §7.1; dono: Arquitetura com Engenharia | §7.1; memorando B4 (2026-08-23) |

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
