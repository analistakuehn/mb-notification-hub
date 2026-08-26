---
language: pt-BR
---

# ADR-0007: Uma única superfície HTTP com minimal APIs REST para ingestão, consulta, auditoria e gestão

| | |
|---|---|
| **Status** | Proposta (com errata de 2026-08-26) |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | Segurança da Informação |
| **Relacionadas** | ADR-0005 (templates como dados), ADR-0006 (`audit.read`), ADR-0010 (Kafka como segunda entrada) |
| **Documento-mãe** | Design de Sistema, §4.3 "Management & Query API", §7 |

## Contexto e problema

O hub expõe quatro famílias de operações HTTP: ingestão de solicitações (produtores, máquina a máquina), consulta de status e histórico (atendimento, BI), auditoria (Compliance) e gestão de templates/layouts/políticas (clientes administrativos). Uma versão anterior do design dividia isso em duas tecnologias (REST para comando, GraphQL para leitura e gestão), com o argumento de seleção de campos e autorização por campo. O time decidiu consolidar em uma única superfície.

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
- **OpenAPI** gerado em build, versionado por rota (`/v1`), com testes de contrato no pipeline (ver a errata de 2026-08-26).

### Errata de 2026-08-26: o documento OpenAPI é servido em runtime, e não gerado em build

O item de decisão acima prescreve "OpenAPI gerado em build, versionado por rota (`/v1`), com testes de contrato no pipeline". Das três cláusulas, só a do versionamento por rota foi implementada. Não existe geração em build: nenhum projeto referencia `Microsoft.Extensions.ApiDescription.Server`, nenhum `openapi.json` está versionado no repositório e nenhum teste lê o documento. Não existem testes de contrato no pipeline, e hoje não podem existir, porque não existe pipeline: `.github/` contém apenas o modelo de pull request.

O que existe é a própria API gerando e servindo o documento em `GET /openapi/v1.json`, por `Microsoft.AspNetCore.OpenApi`, em todos os ambientes. Esta errata alinha o documento ao código, e não o contrário.

**A rota em runtime não é acidente de implementação, é requisito de dois documentos publicados.** O guia de integração do produtor abre nomeando `GET /openapi/v1.json` como o contrato de máquina do hub. E a decisão de 2026-08-24 de não produzir a biblioteca .NET compartilhada se apoia explicitamente nessa rota: o argumento registrado é que um cliente gerado a partir do documento publicado cobre tipos, rotas e status sem código escrito à mão. Gerar em build não substituiria a rota, acrescentaria um segundo lugar onde o mesmo contrato mora, livre para divergir do primeiro. Restringir a rota a Development quebraria os dois documentos de uma vez.

**Postura de exposição, fixada aqui.** Servir em todos os ambientes torna a proteção da rota parte da decisão, e não detalhe do host. O documento descreve a superfície administrativa inteira, incluindo autoria e publicação de templates, e por isso nunca responde a chamador anônimo: a resposta é `401`. A rota declara `RequireAuthorization()` e um teto de 60 requisições por minuto por principal, ainda que a política de fallback do host já cobrisse a autorização e nada mais dispute o orçamento. A razão é que `MapOpenApi` não traz metadado de autorização próprio, então sem a declaração explícita a proteção da rota ficaria inteiramente apoiada numa política declarada em outro ponto do arquivo, e viraria pública no dia em que essa política fosse relaxada ou o pacote passasse a marcar a rota como anônima. Todas as demais rotas da API declaram as duas coisas. Dois testes de integração fixam as duas metades, com o host em Development e em Production: chamador anônimo recebe `401`, chamador autenticado recebe o documento.

**Propriedade aceita, não achado.** Qualquer principal autenticado lê o documento inteiro, inclusive um produtor que só tem papel de ingestão. Isso é intencional: o documento publica rotas e esquemas, cada rota aplica a própria política, e conhecer o mapa não concede acesso a nada. O contrário, um documento por audiência, exigiria recortes por papel que o guia do produtor não pede.

**Critério de retorno da geração em build.** A geração em build volta ao escopo quando existir pipeline de CI, porque é ela que dá lugar à cláusula que falta, o teste de contrato que reprova a mudança incompatível em `/v1`. Até lá, um `openapi.json` versionado seria uma segunda cópia do contrato sem nada que a mantenha igual à primeira, e o critério correspondente em "Como saberemos que foi a decisão certa" permanece não atendido.

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

### Opção 1: REST único
- Prós: simplicidade, auditoria explícita, ferramental padrão.
- Contras: sem seleção de campos.

### Opção 2: REST + GraphQL
- Prós: flexibilidade de consulta; autorização por campo.
- Contras: duas stacks e dois modelos de autorização; dado auditável acessível por seleção de campo exige *middleware* por resolver; cache HTTP e WAF menos eficazes; decisão explícita do time de remover.

### Opção 3: gRPC + REST
- Prós: desempenho e tipagem para máquina a máquina.
- Contras: a ingestão já é trivial (uma transação); Kafka cobre o assíncrono; gRPC adicionaria uma terceira superfície sem ganho mensurável.

## Como saberemos que foi a decisão certa

- 100 % dos acessos a conteúdo renderizado/contato aparecem como `audit.read` (verificado por teste que cobre todas as rotas).
- Clientes administrativos consomem exclusivamente o contrato OpenAPI, sem chamadas fora do contrato.
- Testes de contrato quebram o build em qualquer mudança incompatível em `/v1` (critério não atendido; ver a errata de 2026-08-26).

## Referências

- Design de Sistema, §7.1, §7.4, §9.1, §10.5.
- RFC 9457 (Problem Details), RFC 7232 (`ETag`/`If-Match`).
