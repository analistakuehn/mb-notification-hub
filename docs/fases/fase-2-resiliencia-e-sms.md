---
language: pt-BR
---

# Fase 2: Resiliência e SMS

**Tipo**: design técnico (technical-design)
**Status**: ACCEPTED, em implementação
**Audiência**: engenharia do Notification Hub, Compliance e Produto (participantes do rito mensal, §9.7 do design de sistema)
**Propósito**: fixar o desenho técnico das entregas da fase 2 do roadmap do Notification Hub, com fronteiras, dependências e critérios de saída
**Fontes**: [Design de Sistema](../notification-hub-system-design.md) (§2.1, §2.3, §3, §4.2, §4.3, §5.1, §5.2, §7.3, §8, §9.3, §9.6, §9.7, §10.2, §10.3, §11.3, §15, §16); [ADR-0008](../ADR-0008-at-least-once-com-idempotencia.md); [ADR-0011](../ADR-0011-politica-como-configuracao-de-classe.md); contratos publicados em [`ClassPolicyDefinition.cs`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs); decisão de fronteira registrada pelo arquiteto no fechamento da fase 1b (mapa de módulos)

Convenção de evidência: referências a `§` apontam para o design de sistema; as ADRs citadas estão em status Proposta e este design as segue sem desvio; fatos observados na base de código citam `arquivo:linha`; a decisão de fronteira da 1b é decisão do arquiteto responsável, registrada no contexto do fechamento daquela fase.

## Objetivo e contexto

A fase 1b entrega a fundação: ingestão REST e Kafka, outbox, Core pipeline, Contact & Consent v1, auditoria com hash chain, canais e-mail (SendGrid) e push (FCM), classes `critical` e `transactional` (§15). A fase 2 fecha o ciclo de resiliência da entrega: adiciona o canal SMS (Twilio), executa de ponta a ponta o fallback declarativo entre canais, materializa o Delivery Tracker com webhooks assinados, o scheduler DB-backed, a supressão automática reversível e a reconciliação por canal, ativa a classe `operational` com janela de silêncio e produz o primeiro relatório mensal de evidências para Compliance (§15, linha da fase 2).

O objetivo mensurável é o critério de saída do roadmap: 100 % das notificações `critical` com fallback, zero envio após o TTL e o primeiro relatório mensal entregue a Compliance (§15).

Duração: 4 a 6 semanas, conforme o roadmap (§15). Este documento fixa o desenho técnico e as fronteiras das entregas; a decomposição fina em fatias de implementação acontece no kickoff da fase, deliberadamente fora deste documento.

## Escopo por entrega

### Adapter SMS (Twilio) atrás de `IChannelProvider`

- Implementação de `IChannelProvider` para SMS sobre Twilio Messaging (§4.3): Messaging Service por `application`, com sender pool de número longo ou short code BR; `StatusCallback` por mensagem apontando para `/webhooks/twilio`; `ValidityPeriod` igual ao TTL restante da notificação.
- A hierarquia `RenderedMessage` já discrimina `SmsMessage(body)` por canal (§4.3); o adapter consome essa forma, sem contrato novo.
- SMS é reservado à classe `critical`; liberar para outras classes exige mudança de política aprovada (§3).
- Dispatcher SMS com filas próprias por classe e finalidade (`dispatch-sms-critical`, `dispatch-sms-auth`), preservando o bulkhead de filas por classe e canal (§3, §4.2, §5.1).
- Encoding específico do canal na renderização: remoção de caracteres de controle e de quebras de linha, normalização NFC (§10.2 A2); SMS de OTP nunca contém link (§2.3).
- Circuit breaker Polly por provedor (abre com 50 % de erro em 30 s; aberto, a mensagem volta à fila com visibilidade estendida e o tracker aciona fallback de canal se o plano permitir) e rate limit por token bucket em Redis nos limites contratados (§4.3).
- As filas novas herdam o contrato de confiabilidade do §8: retry com backoff (`critical`: até 3 em 60 s), DLQ por fila com alarme pager para `*-critical` e `*-auth` e redrive por ferramenta interna auditada, TTL rígido verificado em cada ponto de decisão.

### Fallback declarativo

- O plano de entrega é configuração de classe: lista ordenada `deliveryPlan` com canal e timeout por passo (exemplo do vocabulário v1: push com timeout de 30 s, depois SMS), conforme a ADR-0011. O contrato tipado já está publicado: `DeliveryPlanStep(Channel Channel, TimeSpan? Timeout)` em [`ClassPolicyDefinition.cs:11`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs) e o campo `DeliveryPlan` na definição de política (`ClassPolicyDefinition.cs:31`).
- Execução (§5.1, §5.2): o Core grava o attempt com `fallback_deadline` calculado no enfileiramento (`queued`), nunca no `sent`; o scheduler detecta o deadline vencido sem `delivered`; o Delivery Tracker grava a mensagem `FallbackRequested` via outbox; o relay roteia para a fila `core-*` correspondente; o Core reavalia o TTL: vencido, a notificação termina em `expired` sem consumir SMS; válido, insere o attempt do próximo passo do plano. Nunca há chamada direta do Tracker ao Core (§5.1).
- Attempt `unknown` de fluxo `critical` ou de autenticação por mais de 60 s dispara fallback imediato, sem esperar a reconciliação diária; o risco de duplicata é aceito e documentado, preferível a OTP perdido (§5.2; ADR-0008).
- Fan-out de push: o fallback só dispara se todos os attempts de push da notificação falharem (§4.3).
- Critério de saída associado: 100 % de `critical` com fallback (§15).

### Delivery Tracker: webhooks assinados com replay protection

- Endpoints `/webhooks/twilio` (valida `X-Twilio-Signature`) e `/webhooks/sendgrid` (valida a assinatura ECDSA do Event Webhook); allowlist de IP quando disponível; WAF na frente, por serem a única superfície pública do hub (§4.3; §10.2 A9; §16 risco 24).
- Idempotência por UK `(provider, provider_event_id)` na tabela `PROVIDER_EVENT_DEDUPE`; payload bruto armazenado como evidência (§4.3; ADR-0008).
- Replay protection por `provider_event_id` mais janela de timestamp (§10.2 A5).
- Handler mínimo e síncrono: validar assinatura, `INSERT delivery_event` idempotente, enfileirar; resposta `200` em menos de 20 ms; máquina de estados e fallback processados de forma assíncrona (§11.3).
- Correlação por canal: e-mail via `custom_args.notification_id` e `attempt_id` no SendGrid; SMS via `StatusCallback` por mensagem na Twilio (§4.3).
- Transições alimentadas por webhook: `sent → delivered → read` e `sending → failed`/`bounced` (§5.2).

### Scheduler DB-backed

- Worker que a cada 5 s busca attempts com `fallback_deadline < now()` e sem `delivered`, e notificações com `release_at <= now()`, e grava a próxima ação via outbox; simples, sem estado fora do banco, auditável (§4.3).
- Varredura com `LIMIT` e `SKIP LOCKED`, sobre índices parciais `WHERE status = 'sent' AND fallback_deadline IS NOT NULL` e em `release_at` (§11.3). Verificação de fonte: §11.3 especifica `SKIP LOCKED` para a varredura do scheduler; a forma literal `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 100` aparece no caminho quente do Outbox Relay, não no scheduler.
- Justificativa registrada no design: `DelaySeconds` do SQS é limitado a 15 min; agendamento distante e janela de silêncio usam `release_at` liberado pelo scheduler, de forma uniforme, auditável e sem EventBridge (§4.2; §16 risco 5).

### Supressão automática, reversível e auditada

- Requisito: supressão automática por hard bounce, número inválido e token FCM `UNREGISTERED` (RF-10, §2.1).
- Ponto de aplicação: regra do estágio Policy, junto de consentimento, dedupe e janela de silêncio; a decisão é auditável regra a regra (§4.3).
- Somente código de hard bounce específico suprime; para SMS, só após 2 ocorrências em 7 dias; `delivered` ou `bounce` de origem fora da allowlist não produz efeito e gera alarme de segurança, porque um bounce forjado suprimiria o contato de um cliente (§10.2 A5).
- Reversível e auditada: `suppression.added` (automática ou manual) e `suppression.removed`, com ator registrado; supressão manual é atribuição de Platform Admin com PIM (§9.3; §9.1).
- Supressão gerida pelo hub, não pelos suppression groups do SendGrid (§4.3).
- Publicação do evento `araia.notification.contact_suppressed.v1` com `recipientId`, `channel` e `reason` em `notifications.events.v1` (§7.3).
- Decisões sobre supressões manuais são revisadas no rito mensal (§9.7).

### Reconciliação por canal

- Job diário para attempts `sent`/`unknown` sem evento há mais de 6 h; corrige o estado (`unknown → sent`/`failed`) e registra `audit_event` (§8; §5.2).
- Limites reais por provedor, assumidos sem disfarce (§8; ADR-0008):
  - E-mail: SendGrid Email Activity API por `custom_args`; histórico além de poucos dias exige add-on pago; a contratação é decisão externa, registrada como dependência desta fase.
  - SMS e WhatsApp: a Twilio não oferece busca por metadado customizado; correlação best effort por `To` mais janela temporal.
  - Push: o FCM não oferece lookup posterior; `unknown` de push resolve apenas por fallback ou TTL.
- A reconciliação complementa, e não substitui, o fallback imediato para `unknown` acima de 60 s em `critical`/autenticação (§5.2; ADR-0008).

### Classe `operational` com janela de silêncio

- Ativação da classe `operational`: canais e-mail e push, fallback de 1 h, opt-out parcial, janela de silêncio de 21h às 8h no fuso de `RECIPIENT_PROFILE`, base legal de legítimo interesse (§3).
- Regra `QuietHours` do estágio Policy: defer para classes que não sejam `critical`/autenticação, no fuso de `RECIPIENT_PROFILE` (ADR-0011); o resultado `Defer` grava `release_at` e o scheduler libera a notificação (§4.3; §4.2).
- Contrato tipado já publicado: `QuietHoursWindow(TimeOnly From, TimeOnly To)` e o campo opcional `QuietHours` da política ([`ClassPolicyDefinition.cs:14`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs) e `ClassPolicyDefinition.cs:40`).
- A fronteira entre `operational` e `transactional` é definida no cadastro do template por Produto e Compliance na fase 0; o hub aplica a classificação, não a decide (§16 risco 11).

### Relatório mensal de evidências

- Job mensal gera e guarda no mesmo bucket WORM: volumes por classe e canal, rejeições por motivo, taxa de entrega e bounce, DLQs, falhas de provedor, mudanças de catálogo, política e configuração com aprovadores, ativações de PIM e resultado das verificações de hash chain (§9.6).
- O relatório alimenta o rito mensal de Engenharia, Compliance e Produto (§9.7).
- Critério de saída associado: primeiro relatório mensal entregue a Compliance (§15).

## Fora de escopo

- WhatsApp: adapter, submissão de Content template, sincronização de status Meta, opt-in e processamento de `SAIR` pertencem à fase 3 (§15).
- Segundo provedor por canal e failover de provedor: risco aceito na v1; avaliação de SES como failover de e-mail é v2 (§16 riscos 2 e 3).
- Template Studio e demais evoluções com gatilho (aprovação dupla por classe, envio de teste, promoção por bundle, `review_due`) (§15).
- Classe `marketing` (fora de escopo da v1, §3).
- Nível 2 de política, com condições por expressão (§16 risco 20; ADR-0011).

## Dependências

- **Fase 1b completa**, com seus critérios de saída atingidos (§15): OTP de login e confirmação de câmbio migrados, `kyc.document.approved` chegando pelo barramento, consulta de auditoria respondendo às 8 perguntas do §9.5 e verificação de hash chain em execução.
- **Fase 1a**: a política de classe é publicada pela API de gestão com quatro olhos (RF-14, §2.1); `deliveryPlan` e `quietHours` já fazem parte do vocabulário v1 dessa configuração (ADR-0011; `ClassPolicyDefinition.cs:23`).
- **Externas herdadas**, registradas como dependências e não resolvidas por esta fase:
  - Verificação do sender ID alfanumérico no Brasil nas country guidelines da Twilio (§2.3; §16 risco 9). Enquanto pendente, o desenho usa número longo ou short code BR no Messaging Service.
  - Decisão de contratação do add-on pago Email Activity do SendGrid, que condiciona o alcance histórico da reconciliação de e-mail (§8; ADR-0008).

## Arquitetura e fronteiras de módulo

- O hub permanece um monólito modular e a fase 2 não cria módulo novo por padrão. Decisão de fronteira do arquiteto no fechamento da fase 1b: o Delivery Tracking não nasceu como módulo próprio na 1b; a extração para módulo próprio na fase 2 acontece somente com evidência concreta de atrito de fronteira; sem essa evidência, o tracker permanece dentro do módulo Notifications. A mesma decisão determina que QuietHours entra em vigor nesta fase, junto com a classe `operational`.
- Dependências entre módulos acontecem exclusivamente pelos namespaces `Modules.<outro>.Integration.V1`, regra de arquitetura vigente na base (`src/Platform.Api/Modules/TemplateManagement/AGENTS.md:105`). O consumo de `ClassPolicyDefinition`, `DeliveryPlanStep`, `QuietHoursWindow` e `Channel` pelo pipeline segue esse contrato publicado.
- Fluxo de fallback, sem chamada direta entre componentes (§5.1): webhook ou scheduler detecta a condição; o Tracker grava `FallbackRequested` via outbox; o Outbox Relay roteia para a fila `core-*`; o Core decide (TTL, próximo passo do plano) e enfileira o attempt seguinte para o dispatcher do canal.
- O adapter SMS entra como mais um plugin atrás de `IChannelProvider`, sem mudança na topologia de filas por classe e canal (§4.3; §4.2).

## Dados e persistência

- Máquina de estados por attempt conforme a tabela canônica do §5.2, incluindo `sending` e `unknown`; `fallback_deadline` gravado no enfileiramento do attempt (`queued`), não no `sent` (§5.1; §5.2; ADR-0008).
- `release_at` na notificação sustenta deferimento por janela de silêncio e agendamento além de 15 min (§4.2).
- `PROVIDER_EVENT_DEDUPE(provider, provider_event_id)` com UK em tabela não particionada; `delivery_event` particionada mensalmente, com payload bruto do webhook como evidência (§4.3; §11.3).
- Índices que sustentam o scheduler e a reconciliação: parcial `(status, fallback_deadline)`, parcial `(release_at)` e `(provider_message_id)` (§11.3).
- Supressão com trilha de auditoria própria (`suppression.added`, `suppression.removed`) e evento de saída `araia.notification.contact_suppressed.v1` (§9.3; §7.3).

## Segurança e ameaças

- Ameaça A5, webhook forjado ou repetido: `delivered` falso suprime o fallback e `bounce` falso suprime o contato de um cliente. Controles desta fase: assinatura obrigatória (Twilio HMAC, SendGrid ECDSA), allowlist de IP dos provedores, WAF com regra de taxa, replay por `provider_event_id` mais janela de timestamp, supressão só por hard bounce específico (SMS: 2 ocorrências em 7 dias), supressão reversível e auditada, alarme de segurança para `delivered` fora da allowlist (§10.2 A5).
- Os webhooks são a única superfície pública do hub: ALB público somente com WAF, allowlist de IP e TLS; pentest específico previsto (§10.2 A9; §16 risco 24).
- Ameaça A4, interceptação de OTP por SMS: push permanece canal primário; SMS entra como fallback com rate limit por destinatário; TTL curto; SMS de OTP sem link (§10.2 A4; §2.3).
- Prevenção a phishing no canal SMS: sem encurtadores, links só em domínio próprio, remetente registrado por número longo ou short code BR (§2.3).
- Kill switch por canal: o dispatcher SMS pausa e o fallback de canal continua se o plano permitir; acionamento humano, ou automático se o circuito ficar aberto por mais de 10 min (§10.3).

## Observabilidade e operações

- Webhook responde em menos de 20 ms e o processamento pesado é assíncrono; provedores reenviam quando o webhook demora, o que degrada ainda mais (§11.3).
- Alarmes: DLQ de `*-critical` e `*-auth` aciona pager (§8); profundidade da fila `dispatch-sms-critical` tem alarme próprio (§16 risco 22); `delivered` de origem fora da allowlist gera alarme de segurança (§10.2 A5).
- A reconciliação corrige estado e registra `audit_event`; reentrega interna aparece na auditoria como `notification.duplicate`, nunca como segundo envio ao cliente (§8; ADR-0008).
- Operação recorrente: rito mensal revisa o relatório de evidências, supressões manuais, mudanças de política pendentes e custos por aplicação (§9.7).

## Implantação, rollout e rollback

- Ordem de ativação derivada das dependências do fluxo (§5.1): primeiro Delivery Tracker (webhooks) e scheduler, porque o fallback depende do deadline varrido pelo scheduler e da mensagem `FallbackRequested` do tracker; depois o adapter SMS; por fim a ativação do fallback e da classe `operational` por política.
- Ativação por configuração, não por deploy: incluir SMS no `deliveryPlan` e ativar `quietHours` são publicações de nova versão da política de classe pela API de gestão, com quatro olhos (RF-14; ADR-0011). Rollback é republicação da versão anterior pelo mesmo caminho, com trilha completa (§9.7).
- Mitigação imediata em incidente: kill switch por canal SMS (§10.3); `ValidityPeriod` na Twilio atua como segunda barreira de TTL mesmo com o hub degradado (§8).
- A decomposição das entregas em fatias numeradas de implementação acontece no kickoff da fase, com este design como insumo.

## Estratégia de testes

- Verificação dos critérios de saída: cenário fim a fim do §5.1 (push sem `delivered` em 30 s dispara SMS) para 100 % das `critical`; zero envio após TTL, verificado em cada ponto de decisão e coberto pela segunda barreira `ValidityPeriod` (§8); geração e entrega do primeiro relatório mensal (§9.6; §15).
- Máquina de estados por attempt: cobertura das transições canônicas do §5.2, incluindo `sending → unknown` e o fallback imediato para `unknown` acima de 60 s em `critical`/autenticação.
- Webhooks: assinatura inválida rejeitada, replay de `provider_event_id` sem efeito duplicado, origem fora da allowlist sem efeito e com alarme (§10.2 A5).
- Supressão: hard bounce específico suprime; SMS exige 2 ocorrências em 7 dias; reversão gera `suppression.removed` auditado (§10.2 A5; §9.3).
- Janela de silêncio: `operational` dentro da janela recebe `Defer` com `release_at` no fuso de `RECIPIENT_PROFILE` e é liberada pelo scheduler; `critical` não é afetada (§3; ADR-0011).
- Caos, herdado da ADR-0008: matar pods durante burst, rebalance sob carga e failover do banco, com zero duplicata ao cliente e zero perda.
- Sender BR: teste por operadora, conforme a mitigação do risco 9 (§16).

## Alternativas e decisões deliberadamente adiadas

- **Scheduler DB-backed versus EventBridge ou `DelaySeconds`**: decisão do design de sistema, mantida aqui: `DelaySeconds` não passa de 15 min e o scheduler sobre o banco é uniforme e auditável (§4.2; §16 risco 5). Trade-off aceito: a varredura de 5 s adiciona até 5 s à liberação, contra um menor timeout de fallback de 30 s no plano de `critical` (§3).
- **Fallback imediato para `unknown` acima de 60 s**: aceita duplicata rara em troca de nunca perder OTP; a reentrega é auditada como `notification.duplicate` (ADR-0008).
- **Extração do Delivery Tracking como módulo próprio**: adiada; acontece nesta fase somente com evidência concreta de atrito de fronteira, senão o tracker permanece no módulo Notifications (decisão de fronteira do arquiteto na 1b).
- **Contratação do add-on Email Activity do SendGrid**: não decidida aqui; registrada como dependência externa com impacto direto no alcance da reconciliação de e-mail (§8; ADR-0008).
- **Forma final do sender por operadora**: não decidida aqui; aguarda a verificação nas country guidelines da Twilio e o teste por operadora (§2.3; §16 risco 9).
- **Rodízio 3:1 entre `transactional` e `operational` no consumo de filas**: não introduzido nesta fase. O §4.2 marca a ativação da classe `operational` como gatilho de reavaliação, e a reavaliação foi feita agora, na fatia F2-12, com este resultado: fica o que já existe. O comportamento atual não é rodízio nenhum, é prioridade estrita na alocação de vaga de processamento, e a evidência está no código: cada fila entra no consumidor com o posto fixo da sua banda (`auth` 0, `critical` 1, `transactional` 2, `operational` 3, em `OutboxBand`), e quando uma vaga é liberada ela vai para o pretendente de menor posto, o que o teste unitário `A_freed_slot_goes_to_the_highest_priority_waiter_not_the_first_in_line` afirma. Cada fila faz long polling próprio, então uma classe nunca deixa de ser lida; o que ela pode não conseguir é vaga, enquanto houver pretendente de banda superior. O risco declarado é esse: sob pressão sustentada das bandas superiores, `operational` fica sem vaga por tempo indeterminado. Trocar prioridade estrita por rodízio com peso é decisão de calibração e exige medição, não julgamento: sem número de starvation observado em carga real não há como escolher o peso, e um rodízio mal calibrado devolve vaga a `operational` na frente de `critical`. A medição pertence ao gate de carga (§11.6), o outro gatilho que o §4.2 já nomeia, e a mudança só entra depois dela.
- Nenhum desvio das ADRs relacionadas (0001, 0008, 0011): o adapter SMS é plugin, a entrega permanece at-least-once com idempotência em camadas e a política segue como configuração de classe.

## Status de implementação

A decomposição em fatias prometida no fim da seção de implantação foi produzida e aceita em [fase-2-decomposicao.md](fase-2-decomposicao.md), que fixa as sete decisões de fronteira que este design deixou para o kickoff e carrega a tabela de status por fatia. O acompanhamento por fatia vive lá; este documento registra apenas o estado das entregas.

| Entrega deste design | Estado em 2026-08-25 |
|---|---|
| Delivery Tracker com webhooks assinados e replay protection | Concluída (fatias F2-1 e F2-2, commit `7af6e32`) |
| Fallback declarativo, unicidade do avanço de plano | Concluída (fatia F2-4, commit `7af6e32`) |
| Scheduler DB-backed | Concluída (fatia F2-5, commit `7af6e32`) |
| Adapter SMS (Twilio) | Concluída (fatias F2-7 e F2-8, commits `b2a885e` e `8132cbf`), incluindo rate limit por provedor, kill switch automático de canal e pool de sender por aplicação |
| Supressão automática, reversível e auditada | Concluída (fatia F2-6, commit `47ab335`) |
| Reconciliação por canal | Concluída (fatia F2-9, commit `6850637`) |
| Classe `operational` com janela de silêncio | Concluída (fatia F2-12) |
| Relatório mensal de evidências | Concluída (fatia F2-10, commit `e74fdfa`) |
| Cenário de ponta a ponta do §5.1 (push aceito sem evento, prazo vencido, SMS, webhook encerrando) | Concluída (fatia F2-11) |

Duas correções que a implementação obrigou e que este design não previa, ambas registradas na decomposição e a segunda também em [ADR-0014](../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md):

1. **Os dois gatilhos de fallback precisavam de unicidade no banco.** A deduplicação por mensagem não cobre dois produtores distintos, e o gatilho reativo e o gatilho por prazo geram identificadores de mensagem distintos: sem a correção, o cenário do §5.1 entregaria dois SMS ao mesmo cliente, contra a ADR-0008. A unicidade passou a ser claim de estado por passo do plano.
2. **A aceitação do push encerrava a notificação e matava o fallback.** Enquanto o push aceito pelo FCM declarasse a notificação entregue, o cenário do §5.1 seria descartado em silêncio e o critério de saída de 100 % de `critical` com fallback seria inatingível. A aceitação passou a declarar entrega somente no passo sem prazo, isto é, no último passo do plano. Consequência observável: `araia.notification.delivered.v1` deixa de ser emitido na aceitação do push quando o plano tem passo posterior, o que exige comunicação aos produtores antes do deploy.

## Critérios de saída

Conforme a linha da fase 2 do roadmap (§15):

1. 100 % das notificações `critical` com fallback.
2. Zero envio após o TTL.
3. Primeiro relatório mensal de evidências entregue a Compliance.

## Riscos

| Risco | Fonte | Tratamento nesta fase |
|---|---|---|
| Operadoras brasileiras podem não entregar sender ID alfanumérico; verificação pendente nas country guidelines da Twilio | §2.3; §16 risco 9 | Número longo ou short code BR no Messaging Service; teste por operadora; a verificação permanece como dependência externa |
| Vazão de SMS limitada pelo MPS contratado por sender | §16 risco 22 | SMS restrito a `critical`; pool de senders por `application`; alarme da fila `dispatch-sms-critical`; negociar MPS com base no burst de referência |
| Webhooks são a única superfície pública do hub | §16 risco 24; §10.2 A5 | WAF, allowlist de IP, assinatura, replay protection, supressão corroborada; pentest específico |
| Concentração Twilio e SendGrid na mesma empresa; indisponibilidade correlacionada | §16 riscos 2 e 3 | Risco aceito na v1; `IChannelProvider` preserva a troca de provedor; fallback de canal e circuit breaker reduzem o impacto |
| FCM sem confirmação de entrega real | §16 risco 4 | `delivered` de push significa aceito pelo FCM; o fallback de 30 s compensa para `critical` |
| Duplicata ao cliente no fallback imediato de attempt `unknown` | §5.2; ADR-0008 | Risco aceito e documentado, preferível a OTP perdido; reentrega auditada como `notification.duplicate` |
| Reconciliação com lacunas de lookup por provedor | §8; ADR-0008 | Fallback imediato para `unknown` acima de 60 s em `critical`/autenticação; decisão do add-on Email Activity tratada como dependência externa |

## Referências

- [Notification Hub, Design de Sistema](../notification-hub-system-design.md): §2.1, §2.3, §3, §4.2, §4.3, §5.1, §5.2, §7.3, §8, §9.3, §9.6, §9.7, §10.2, §10.3, §11.3, §15, §16.
- [ADR-0008: Entrega at-least-once com idempotência](../ADR-0008-at-least-once-com-idempotencia.md).
- [ADR-0011: Política como configuração de classe](../ADR-0011-politica-como-configuracao-de-classe.md).
- [`ClassPolicyDefinition.cs`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs): contratos `DeliveryPlanStep`, `QuietHoursWindow` e `ClassPolicyDefinition` publicados em `Integration/V1`.
- [`AGENTS.md` do módulo TemplateManagement](../../src/Platform.Api/Modules/TemplateManagement/AGENTS.md): regra de dependência entre módulos via `Modules.<outro>.Integration.V1`.
