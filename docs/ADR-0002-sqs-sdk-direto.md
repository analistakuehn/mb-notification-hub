# ADR-0002: SQS com SDK direto da AWS

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | SRE |
| **Relacionadas** | ADR-0008 (at-least-once), ADR-0010 (Kafka/SQS), ADR-0003 (pipeline) |
| **Documento-mãe** | Design de Sistema, §4.2 "Topologia de mensageria", §8 |

## Contexto e problema

Dentro do hub, os workers (ingress, relay, core, quatro dispatchers, tracker) se comunicam por filas. A decisão de usar Amazon SQS está tomada; a questão é se acessá-lo através de um framework de mensageria (MassTransit, Rebus, NServiceBus) ou diretamente pelo `AWSSDK.SQS`.

Um framework traria prontos: outbox transacional, retry com backoff, *delayed messages*, DLQ, sagas, serialização e convenções de tópico. Em troca, impõe uma dependência grande, semântica própria sobre o SQS e convenções que o time precisaria aprender e respeitar.

## Fatores de decisão

- **Controle e legibilidade**: o time precisa entender por inteiro o comportamento de retry, idempotência e prioridade; são requisitos de auditoria, não detalhes.
- **Prioridade por fila**: SQS não tem prioridade; precisamos de *weighted polling* entre filas de classe, que frameworks não modelam bem.
- **Auditoria de cada salto**: cada mensagem processada deve gerar `audit_event` na mesma transação do efeito.
- **Tamanho da dependência** vs. tamanho do problema: não há sagas, não há roteamento complexo, não há múltiplos transportes.
- **Custo de manutenção**: atualizações de framework, quebras de versão, *lock-in* em convenções.

## Opções consideradas

1. **`AWSSDK.SQS` direto, envolvido num `SqsConsumer<T>` interno** (escolhida).
2. MassTransit sobre SQS/SNS.
3. Rebus ou NServiceBus.

## Decisão

Adotar a opção 1. O hub mantém um componente interno pequeno (~400 linhas) que encapsula:

- **Consumo**: long polling (20 s), `ReceiveMessage` em lotes de 10, concorrência configurável por fila, *weighted polling* entre filas (`critical` sempre que houver mensagem; `transactional`/`operational` em rodízio 3:1).
- **Retry**: em erro transitório, `ChangeMessageVisibility` com backoff exponencial e jitter; em erro permanente, `DeleteMessage` + registro da falha (não vai para DLQ).
- **DLQ**: redrive policy do próprio SQS (`maxReceiveCount`) para o inesperado.
- **Outbox**: `Outbox Relay Worker` lê `outbox` com `FOR UPDATE SKIP LOCKED`, publica (`SendMessageBatch` ou producer Kafka) e marca como enviado.
- **Idempotência**: tabela `processed_messages(message_id, consumer)` verificada na transação do efeito (ADR-0008).
- **Agendamento**: `DelaySeconds` (≤ 15 min) para curto prazo; scheduler DB-backed para o resto.
- **Serialização**: `System.Text.Json` com *source generators*; envelope próprio com `messageId`, `type`, `schemaVersion`, `traceparent`.

### Consequências

**Positivas**
- Zero mágica: todo comportamento de entrega é código do time, testável em testes de integração com LocalStack (SQS emulado), no mesmo padrão dos demais testes de integração (Testcontainers para Postgres, Redis e Kafka).
- Prioridade por fila e backpressure são explícitos.
- Auditoria por salto é natural, porque o consumidor controla a transação.
- Uma dependência a menos para acompanhar em CVEs e *breaking changes*.

**Negativas**
- Assumimos a implementação e os testes de outbox, retry, idempotência e scheduling. (Esses testes seriam necessários de qualquer forma para auditar o comportamento.)
- Sem sagas; não precisamos: o estado de uma notificação mora no banco, não numa saga.
- Risco de reinventar mal: mitigado por manter o componente pequeno, com revisão de SRE e testes de caos (§11.6).

## Prós e contras das opções

### Opção 1 — SDK direto
- Prós: controle total, pequeno, sem lock-in, prioridade e auditoria explícitas.
- Contras: código próprio a manter.

### Opção 2 — MassTransit
- Prós: outbox, retry, scheduling e DLQ prontos; comunidade grande.
- Contras: dependência pesada; usa SNS/SQS com convenções próprias de tópicos que não coincidem com a topologia desejada; prioridade entre filas exige contornar o framework; auditoria por salto requer *filters* customizados; curva de aprendizado para um problema pequeno.

### Opção 3 — Rebus / NServiceBus
- Prós: semelhantes à opção 2, com menos ou mais recursos.
- Contras: NServiceBus é licenciado; Rebus tem comunidade menor; mesmos problemas de convenção e controle.

## Como saberemos que foi a decisão certa

- O `SqsConsumer<T>` permanece abaixo de ~500 linhas após a fase 2.
- Os cenários de caos de mensageria de §11.6 têm cobertura nos testes de integração do consumidor.
- Os testes de caos de §11.6 (provedor fora, failover do banco, rebalance) passam sem alterar o consumidor.

## Referências

- Design de Sistema — §4.2, §8, §11.3.
- ADR-0010 para a divisão Kafka (integração) / SQS (interno).
