# ADR-0010: Kafka para integração, SQS para filas de trabalho internas

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma, Arquitetura Corporativa (dona do barramento) |
| **Consultados** | Segurança da Informação, SRE, times produtores |
| **Relacionadas** | ADR-0002 (SQS), ADR-0004 (sem PII no barramento), ADR-0008 (idempotência) |
| **Documento-mãe** | Design de Sistema, §4.2, §7.2, §7.3, §10.2, §16 riscos 15–19 |

## Contexto e problema

A organização já centraliza mensagens e eventos num barramento Kafka; os domínios produzem e consomem ali. Foi solicitado que o hub também possa ser acionado por esse barramento. Isso levanta duas decisões: (1) o Kafka substitui ou convive com o SNS de entrada/saída previsto até então; (2) o Kafka deve também substituir as filas SQS internas entre os workers do hub.

Kafka e SQS têm semânticas diferentes. Kafka é um log particionado: ordem por partição, retenção por tempo, consumidores com offset, sem *delay* por mensagem, sem DLQ nativa por mensagem, paralelismo limitado por partições. SQS é uma fila: *visibility timeout*, `DelaySeconds`, redrive para DLQ por mensagem, retry individual, escala por profundidade sem reparticionar.

## Fatores de decisão

- **Aderência ao padrão corporativo**: produtores já estão no Kafka; não devem precisar de um segundo mecanismo.
- **Semântica de fila** para o trabalho interno: *delay*, retry individual, DLQ por mensagem, prioridade por fila, escala por profundidade.
- **Superfície**: evitar dois caminhos assíncronos para a mesma coisa.
- **Segurança**: um tópico corporativo é lido por vários consumidores e retido por dias; o que pode viajar nele é diferente do que viaja numa fila privada.
- **Autorização**: no REST há token Entra; no Kafka há principal do broker.

## Opções consideradas

1. **Kafka na borda (entrada e saída), SQS dentro; SNS removido** (escolhida).
2. Só Kafka, inclusive filas internas.
3. Só SQS/SNS, com um *bridge* Kafka ↔ SNS.
4. Kafka e SNS convivendo como entradas alternativas.

## Decisão

Adotar a opção 1.

**Borda (Kafka).**
- Entrada `notifications.requested.v1`: envelope CloudEvents 1.0 com o mesmo `data` do REST; key = `recipientId`; `idempotencyKey` obrigatório; Schema Registry se existir, senão JSON Schema versionado no hub.
- Saída `notifications.events.v1`: `rejected`, `delivered`, `failed`, `contact_suppressed`, `consent_changed`; sem conteúdo e sem contato.
- Dead-letter `notifications.requested.dlt` apenas para erro permanente, com headers de diagnóstico.
- **Kafka Ingress Worker**: `Confluent.Kafka`, commit manual após a transação, `processed_messages` por `(topic, partition, offset)`, `CooperativeSticky` + *static membership*, erro transitório pausa a partição sem commit, escala por consumer lag (KEDA) limitada ao número de partições.
- **Autorização** em duas camadas: ACL do Kafka (quem escreve no tópico) + `PRODUCER_REGISTRY` no hub (principal Kafka → `application`s e classes permitidas, espelho das app roles do Entra). Forma canônica: dados declarativos no repositório de IaC (aplicados via Terraform), materializados na tabela Postgres homônima por job de deploy; o hub lê somente a tabela, com cache de 60 s. Fora do registro → `.dlt` + `NotificationRejected(producer-not-authorized)`.
- **Variáveis sensíveis**: na v1, templates cuja versão publicada declara `sensitive_variables` só aceitam solicitação via REST, e a publicação recusa versão que largue uma variável em vigor, então a restrição não desliga depois de ligada. Evento Kafka com variável sensível → `.dlt` + rejeição (`sensitive-variables-on-bus`) + `audit_event`. O envelope de cifra para variáveis sensíveis no barramento fica fora da v1 (ADR futura prevista). Retenção do tópico de entrada em 24 h; ACL de leitura restrita ao consumer group do hub.

**Interno (SQS).** Filas `core-{class}` e `dispatch-{channel}-{class}` como na ADR-0002; Outbox Relay publica nas filas SQS e no tópico Kafka de saída.

**SNS** sai da topologia. Produtor sem acesso ao Kafka usa REST.

### Consequências

**Positivas**
- Produtores integram pelo padrão que já usam; eventos de resultado ficam disponíveis a qualquer domínio.
- Cada sistema de mensageria faz o que faz bem; nenhuma semântica de fila é reimplementada sobre um log.
- Um único caminho assíncrono; menos superfície a auditar.
- Regra explícita para segredos no barramento, com rejeição e trilha.

**Negativas**
- Dois clientes de mensageria no hub (`Confluent.Kafka` e `AWSSDK.SQS`), cada um com seu consumidor e seus testes.
- `PRODUCER_REGISTRY` é um segundo lugar de autorização a manter em sincronia com as app roles do Entra (a forma canônica materializa o repositório de IaC na tabela por job de deploy; o teste de drift diário compara repositório e tabela).
- Custo multi-time: cada produtor implementa outbox próprio ou CDC + envelope CloudEvents para publicar no tópico de entrada.
- A retenção de 24 h do tópico de entrada apaga mensagens não consumidas: indisponibilidade prolongada do ingress causa perda real. Controles: alarme de partição pausada por mais de 5 min (page) e objetivo de restauração do ingress ≤ 1 h, muito menor que a retenção.
- Ordem por `recipientId` só na entrada; o hub não garante ordem ponta a ponta (documentado; fila FIFO dedicada se um fluxo exigir, com as ressalvas de FIFO: sem `DelaySeconds` por mensagem, limite de mensagens em voo por fila, throughput limitado por grupo, consumo serializado por `MessageGroupId`).
- Paralelismo do ingress limitado a partições; aumentar partições quebra ordem por key (operação planejada).

## Prós e contras das opções

### Opção 1 — Kafka na borda, SQS dentro
- Prós: aderência, semântica correta em cada lado, superfície única.
- Contras: dois clientes.

### Opção 2 — Só Kafka
- Prós: um cliente; padrão corporativo em tudo.
- Contras: reimplementar *delay*, retry individual, DLQ por mensagem e prioridade sobre um log; cada classe × canal viraria tópico + consumer group, multiplicando partições para obter paralelismo que o SQS dá de graça; escala interna acoplada a reparticionamento.

### Opção 3 — Só SQS/SNS + bridge
- Prós: um ecossistema AWS.
- Contras: produtores fora do padrão corporativo; componente de bridge a operar e auditar; latência e ponto de falha adicionais.

### Opção 4 — Kafka e SNS convivendo
- Prós: flexibilidade.
- Contras: dois caminhos assíncronos para a mesma entrada, duas autorizações, duas auditorias; nenhum produtor real precisa do SNS.

## Como saberemos que foi a decisão certa

- Fase 1b: `cambio.order.confirmed` e `kyc.document.approved` chegam pelo barramento sem mudança de padrão nos produtores.
- Teste de caos (rebalance sob carga, pod morto) sem duplicata ao cliente.
- Nenhum evento com variável sensível em claro aceito do Kafka (teste automatizado com payload sintético → `.dlt`).
- `kafka_consumer_lag` estável no burst de referência com partições dimensionadas para 3× o pico.

## Referências

- Design de Sistema — §4.2, §7.2, §7.3, §8, §10.2 (A1, A7), §11.4, §16 riscos 15–19.
- CloudEvents 1.0 specification.
