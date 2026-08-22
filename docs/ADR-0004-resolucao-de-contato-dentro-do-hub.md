# ADR-0004: Resolução de contato dentro do hub (produtor envia apenas `recipient_id`)

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Segurança da Informação, DPO |
| **Consultados** | Engenharia dos domínios produtores, Compliance |
| **Relacionadas** | ADR-0006 (auditoria), ADR-0010 (Kafka), ADR-0012 (fonte da verdade e contratos de escrita de Contact & Consent) |
| **Documento-mãe** | Design de Sistema, §4.3 "Contact & Consent", §4.4 "Fronteira de PII", §10.2 ameaças A3/A7 |

## Contexto e problema

Para entregar uma notificação é preciso conhecer o e-mail, o telefone ou o *device token* do cliente, além do seu consentimento por finalidade. Há duas formas de o hub obter isso: o produtor envia os contatos junto com a solicitação, ou envia apenas um identificador opaco (`recipient_id`) e o hub resolve os contatos internamente.

Se cada produtor enviar contatos, a PII passa a existir em N serviços, nos seus logs, nos seus traces, no barramento Kafka (retido por dias e lido por vários consumidores) e em qualquer fila intermediária. O consentimento também passaria a ser responsabilidade de cada produtor, ou de ninguém.

## Fatores de decisão

- **Minimização (LGPD art. 6º, III)**: dado pessoal deve existir no menor número possível de lugares.
- **Auditoria de consentimento**: um único ponto que decide "podia enviar?" e registra por quê.
- **Superfície de vazamento**: logs, traces, filas e tópicos de terceiros fora do controle do hub.
- **Latência de `critical`**: a resolução não pode adicionar centenas de milissegundos ao OTP.
- **Disponibilidade**: a resolução de contato e consentimento não pode derrubar OTP.

## Opções consideradas

1. **Produtor envia `recipient_id`; hub resolve contatos e consentimento internamente** (escolhida).
2. Produtor envia contatos e o hub usa o que recebe.
3. Híbrido: produtor envia `recipient_id` e, opcionalmente, contatos como *override*.

## Decisão

Adotar a opção 1.

- O contrato de solicitação (REST e Kafka) aceita apenas `recipientId` (ULID opaco). Qualquer campo que pareça contato é rejeitado com `422`.
- O estágio *Resolve* do Core consulta o módulo **Contact & Consent**, módulo interno do hub (mesmo processo, mesmo Postgres; não existe serviço remoto separado na v1), e obtém pontos de contato verificados e consentimentos vigentes. A PII existe a partir daí: em memória no pipeline e cifrada em `NOTIFICATION_ATTEMPT`. A fonte da verdade (tabelas do hub) e os contratos de escrita (REST e Kafka) são registrados na ADR-0012, decisão complementar a esta.
- Cache em Redis (valores cifrados com data key, TTL 24 h), invalidado por `ContactChanged`/`ConsentChanged`, eventos emitidos pelo próprio módulo via outbox e consumidos pelos caches locais dos workers. O modo degradado é cache *stale-while-revalidate* sobre a consulta local: `critical` usa o último valor conhecido; demais classes `Defer`.
- Logs, traces e métricas carregam `recipient_id`, nunca o contato (Serilog *destructuring policy* + processador OTel).
- Eventos de saída (`notifications.events.v1`) nunca carregam contato.

### Consequências

**Positivas**
- Um único lugar para consentimento, supressão e verificação de contato, e uma única trilha.
- Produtores ficam mais simples e deixam de ser operadores de dado de contato para fins de notificação.
- O barramento Kafka não transporta PII de contato.
- O `recipient_id` opaco elimina o uso de CPF/e-mail como chave em qualquer integração.

**Negativas**
- A resolução é uma consulta adicional no caminho de cada notificação; mitigada por cache e *stale-while-revalidate* para `critical`.
- Se o dado de contato estiver desatualizado no módulo Contact & Consent, a notificação não chega; mas isso é verdade hoje, apenas espalhado; o hub passa a medir (`notifications_rejected_total{reason=no-valid-contact}`).
- O hub passa a ser fonte da verdade de contato e consentimento para fins de notificação, com ingestão dedicada a manter (fonte da verdade e contratos de escrita na ADR-0012).

## Prós e contras das opções

### Opção 1 — `recipient_id` + resolução interna
- Prós: minimização, auditoria única, barramento sem PII, identificador opaco.
- Contras: o hub assume a guarda e a ingestão dos dados de contato; exige cache.

### Opção 2 — Produtor envia contatos
- Prós: hub sem dependência; latência mínima.
- Contras: PII em N serviços e no Kafka; consentimento sem dono; impossível provar ao regulador que "só enviamos para contato verificado com consentimento".

### Opção 3 — Híbrido com override
- Prós: flexibilidade para casos de borda (ex.: notificar um e-mail ainda não cadastrado).
- Contras: o override vira o caminho fácil e reabre todos os problemas da opção 2; caso de borda real (contato não cadastrado) deve ser resolvido no cadastro, não no hub.

## Como saberemos que foi a decisão certa

- Nenhum log, trace ou tópico Kafka contém e-mail/telefone (verificado por teste automatizado com payloads sintéticos).
- `GET /v1/audit/notifications/{id}` responde "o cliente tinha consentimento/canal válido?" sem consultar nenhum outro sistema.
- p95 do estágio *Resolve* ≤ 20 ms com cache quente.

## Referências

- LGPD, art. 6º (princípios) e art. 46 (segurança).
- Design de Sistema — §4.4, §10.2 (A3, A7), §11.3.
