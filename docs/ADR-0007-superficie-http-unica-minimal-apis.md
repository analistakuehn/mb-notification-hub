# ADR-0007: Uma única superfície HTTP — minimal APIs REST para ingestão, consulta, auditoria e gestão

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | Segurança da Informação |
| **Relacionadas** | ADR-0005 (templates como dados), ADR-0006 (`audit.read`), ADR-0010 (Kafka como segunda entrada) |
| **Documento-mãe** | Design de Sistema, §4.3 "Management & Query API", §7 |

## Contexto e problema

O hub expõe quatro famílias de operações HTTP: ingestão de solicitações (produtores, máquina a máquina), consulta de status e histórico (atendimento, BI), auditoria (Compliance) e gestão de templates/layouts/políticas (clientes administrativos). Uma versão anterior do design dividia isso em duas tecnologias — REST para comando, GraphQL para leitura e gestão — com o argumento de seleção de campos e autorização por campo. O time decidiu consolidar em uma única superfície.

## Fatores de decisão

- **Uma stack, um modelo de autorização, um pipeline de testes de contrato.**
- **Dado auditável não pode vazar por seleção de campo**: ler conteúdo renderizado ou contato precisa ser um ato explícito e registrado.
- **Consumidores conhecidos**: clientes administrativos, atendimento, Compliance, BI; não há necessidade de agregação ad hoc por terceiros.
- **Semântica HTTP**: idempotência por `Idempotency-Key`, `ETag`/`If-Match`, cache, Problem Details, OpenAPI como contrato.
- **Contrato único**: clientes administrativos consomem exclusivamente o contrato OpenAPI.

## Opções consideradas

1. **Minimal APIs REST para tudo, com OpenAPI, autorização por rota e por recurso** (escolhida).
2. REST para comando + GraphQL para consulta/gestão (estado anterior).
3. gRPC para máquina a máquina + REST para humanos.

## Decisão

Adotar a opção 1.

- **Grupos de rotas** por política de autorização: `/v1/notifications` (produtores: `Notifications.Send.{Class}`), `/v1/audit/*` (`Notifications.Audit`), `/v1/templates/*`, `/v1/layouts/*`, `/v1/applications/{app}/classes/{class}/policy` (`Templates.Author|Publish`), `/webhooks/*` (assinatura do provedor).
- **Autorização por recurso além da rota**: quem publica não é o autor da versão. O hub nega `publish` a quem editou a versão, mesmo com o papel `Templates.Publish`; é o mecanismo dos quatro olhos no publish.
- **Endpoints dedicados para dado auditável** (`/v1/audit/notifications/{id}/attempts/{seq}/content`): cada chamada gera `audit.read`. Não há campo opcional que exponha conteúdo ou contato em outra rota.
- **Transições de estado como `POST`** em sub-recursos (`/publish`, `/deprecate`, `/disable`), com `/validate` e `/render` como operações `POST` sem transição; rollback é a republicação de uma versão anterior, sem rota dedicada; conteúdo de rascunho como `PUT` idempotente com `ETag`/`If-Match` (`412` em conflito).
- **Erros** em RFC 9457, incluindo `409 invalid-state-transition` com as transições permitidas e relatório de validação completo (`checks[]`) em `publish`/`validate`.
- **Paginação por cursor**; **SSE** para o único caso de tempo real (acompanhamento de status), opcional na v1.
- **OpenAPI** gerado em build, versionado por rota (`/v1`), com testes de contrato no pipeline.

### Consequências

**Positivas**
- Um só modelo mental para produtores, clientes administrativos e auditores.
- `audit.read` impossível de contornar: o endpoint é a fronteira.
- Ferramental padrão (WAF, cache, OpenAPI, testes de contrato) sem camada adicional.
- Menos superfície de ataque e de manutenção.

**Negativas**
- Perde-se seleção de campos e agregação ad hoc. Aceito: os recursos são desenhados para os consumidores conhecidos; novos casos viram novos endpoints.
- Clientes administrativos fazem mais chamadas para compor uma visão (ex.: template + versões + relatório de validação). Mitigado por recursos compostos onde o padrão de uso é estável.

## Prós e contras das opções

### Opção 1 — REST único
- Prós: simplicidade, auditoria explícita, ferramental padrão.
- Contras: sem seleção de campos.

### Opção 2 — REST + GraphQL
- Prós: flexibilidade de consulta; autorização por campo.
- Contras: duas stacks e dois modelos de autorização; dado auditável acessível por seleção de campo exige *middleware* por resolver; cache HTTP e WAF menos eficazes; decisão explícita do time de remover.

### Opção 3 — gRPC + REST
- Prós: desempenho e tipagem para máquina a máquina.
- Contras: a ingestão já é trivial (uma transação); Kafka cobre o assíncrono; gRPC adicionaria uma terceira superfície sem ganho mensurável.

## Como saberemos que foi a decisão certa

- 100 % dos acessos a conteúdo renderizado/contato aparecem como `audit.read` (verificado por teste que cobre todas as rotas).
- Clientes administrativos consomem exclusivamente o contrato OpenAPI, sem chamadas fora do contrato.
- Testes de contrato quebram o build em qualquer mudança incompatível em `/v1`.

## Referências

- Design de Sistema — §7.1, §7.4, §9.1, §10.5.
- RFC 9457 (Problem Details), RFC 7232 (`ETag`/`If-Match`).
