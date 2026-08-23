---
language: pt-BR
---

# ADR-0008: Entrega at-least-once com idempotência

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | SRE |
| **Relacionadas** | ADR-0002 (SQS), ADR-0010 (Kafka), ADR-0006 (auditoria) |
| **Documento-mãe** | Design de Sistema, §4.2, §5.2, §7.1, §8 |

## Contexto e problema

Uma notificação atravessa vários saltos: produtor → (REST ou Kafka) → ingestão → outbox → SQS → Core → outbox → SQS → dispatcher → provedor → webhook → tracker. Cada salto pode falhar depois de ter produzido efeito e antes de confirmar (rede, rebalance, pod morto). Há duas garantias possíveis: tentar eliminar qualquer reentrega (exactly-once) ou aceitar reentrega interna e garantir que o **cliente** nunca receba duplicata.

O dano de duplicata para o cliente é real (dois OTPs diferentes confundem; dois SMS custam) e o dano de perda é pior (OTP que não chega). A questão é onde colocar a complexidade.

## Fatores de decisão

- **Nunca perder** uma notificação aceita (`202`).
- **Nunca duplicar** para o cliente.
- **Simplicidade operacional**: sem coordenação distribuída entre banco, SQS, Kafka e provedores.
- **Auditoria**: reentregas internas devem ser visíveis (`notification.duplicate`) sem poluir o resultado.
- **Desempenho** no caminho quente.

## Opções consideradas

1. **At-least-once em todos os saltos + idempotência em cada consumidor + lock otimista antes do provedor** (escolhida).
2. Exactly-once via transações distribuídas (2PC) entre banco e broker.
3. Exactly-once do Kafka (transações) estendido ao resto do fluxo.
4. At-most-once (descartar em dúvida).

## Decisão

Adotar a opção 1. A unicidade para o cliente é garantida por **três chaves em camadas**:

| Camada | Chave | Protege contra |
|---|---|---|
| Produtor | `(application, idempotency_key)`: UK da tabela dedicada `IDEMPOTENCY_KEY`, que guarda `payload_hash` para responder `409` à mesma chave com payload diferente; *fast path* `SET NX` no Redis (`idem:{application}:{key}`), gravado somente após o commit | Retry do produtor (REST) ou reenvio legítimo (Kafka) |
| Transporte | `processed_messages(message_id, consumer)` (`messageId` do envelope interno para origens SQS ou `(topic, partition, offset)` do Kafka), verificado **na mesma transação** do efeito | Redelivery do broker, rebalance, pod morto após efeito e antes do ack |
| Entrega | `UPDATE notification_attempt SET status='sending' WHERE id=? AND status='queued'` antes de chamar o provedor; só quem vence o update chama | Dois dispatchers com a mesma mensagem |

Complementos:
- Outbox transacional em quem publica (produtores e hub); o relay pode publicar duas vezes, o consumidor dedupe.
- Nota de refinamento (2026-08-23): para origens SQS, o dedupe usa o `messageId` do envelope interno (gerado na escrita do outbox, estável entre republicações do relay) mais a chave de negócio; a republicação pelo relay gera novo `MessageId` de transporte no SQS, que por isso não serve como identidade.
- Commit manual no Kafka após a transação; `CooperativeSticky` e *static membership* reduzem reprocessamento, não o eliminam; por isso a camada de transporte existe.
- Webhooks: idempotência por `(provider, provider_event_id)`, UK da tabela dedicada `PROVIDER_EVENT_DEDUPE`.
- Reentrega detectada gera `notification.duplicate` na auditoria e métrica; nunca gera segundo envio.
- A máquina de estados canônica dos attempts (incluindo `sending` e `unknown`, com gatilhos e componentes responsáveis) está em §5.2 do documento-mãe; o `fallback_deadline` é gravado no enfileiramento do attempt (`queued`), não no `sent`.
- Caso residual aceito: o provedor aceitou a mensagem e o hub caiu antes de gravar `sent`; ao reprocessar, o lock otimista está em `sending` (não `queued`) → o dispatcher não reenvia e marca `unknown`.
- Attempt `unknown` de fluxo `critical` ou de autenticação por mais de 60 s: o Delivery Tracker dispara fallback imediato (`FallbackRequested`), sem esperar a reconciliação diária; o risco de duplicata é aceito e documentado (preferível a OTP perdido).
- A reconciliação de `unknown` opera nos limites reais de cada provedor: e-mail via SendGrid Email Activity API por `custom_args` (histórico além de poucos dias exige add-on pago; o custo é registrado como decisão de contratação); SMS/WhatsApp na Twilio sem busca por metadado customizado, com correlação best effort por `To` + janela temporal; push sem lookup posterior no FCM, `unknown` de push resolve apenas por fallback/TTL.

### Consequências

**Positivas**
- Sem coordenação distribuída; cada componente é simples e testável isoladamente.
- Falhas de infraestrutura viram reprocessamento, não perda nem duplicata.
- Auditoria mostra reentregas sem contaminar resultados.

**Negativas**
- Cada consumidor carrega a verificação de `processed_messages` (um `INSERT` com índice único por transação). Custo baixo, mas é disciplina obrigatória, coberta por teste de contrato do `SqsConsumer<T>`.
- Janela residual descrita acima depende da reconciliação, que tem limites reais por provedor (lookup só onde o provedor suporta); mitigada pelo fallback imediato para `unknown` acima de 60 s em `critical`/auth.

## Prós e contras das opções

### Opção 1 — At-least-once + idempotência
- Prós: simples, robusto, auditável.
- Contras: disciplina em cada consumidor; caso residual via reconciliação.

### Opção 2 — 2PC
- Prós: garantia formal entre banco e broker.
- Contras: SQS não participa de transações distribuídas; provedores externos tampouco; complexidade e latência altas; ganho inexistente na borda que importa (o provedor).

### Opção 3 — Transações Kafka
- Prós: exactly-once entre tópicos Kafka.
- Contras: não cobre Postgres, SQS nem provedores; o fluxo sai do Kafka no primeiro salto.

### Opção 4 — At-most-once
- Prós: nunca duplica.
- Contras: perde OTP; inaceitável.

## Como saberemos que foi a decisão certa

- Teste de caos (§11.6: matar pods durante burst, rebalance sob carga, failover do banco) com zero duplicatas ao cliente e zero perda.
- `notification.duplicate` aparece na auditoria com frequência compatível com falhas de infraestrutura, nunca com envios duplicados reportados por clientes.

## Referências

- Design de Sistema — §4.2, §5.2, §8, §11.6, §16 risco 17.
