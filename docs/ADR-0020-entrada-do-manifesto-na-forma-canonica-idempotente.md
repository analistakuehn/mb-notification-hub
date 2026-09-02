---
language: pt-BR
---

# ADR-0020: Entrada do manifesto na forma canônica idempotente

**Status**: ACCEPTED

| Campo | Valor |
|---|---|
| **Data** | 2026-08-31 |
| **Responsável** | Produto Notification Hub e `dotnet-architect` |
| **Audiência** | Arquitetura e engenharia dos módulos `Notifications` e `AttachmentManagement`, responsáveis pelos contratos REST e Kafka e responsáveis pelo rollout |
| **Aprovação** | Usuário, por decisão explícita no ponto de controle de implementação de 2026-08-31 |
| **Escopo da decisão** | Representação das referências de anexos na forma canônica usada pelo hash de idempotência do ingresso |
| **Relacionadas** | [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md); [ADR-0019: Snapshot do manifesto aceito na notificação](ADR-0019-snapshot-do-manifesto-aceito.md) |
| **Fontes** | [Especificação de desenvolvimento](SPEC-001/requirements/core/01-development-specification.md); [refinamento consolidado](SPEC-001/refinements/00-refinement-consolidated.md); [corpus contratual do manifesto](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md); [Delivery Slice](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md) |
| **Código afetado** | Contratos públicos V2 de ingresso; validação do comando; `RequestNotification.PayloadHash`; testes de contrato, hash e idempotência |

## Resumo executivo

Incluir `attachments` na forma canônica do hash de idempotência. O membro é escrito imediatamente depois de `application` somente quando contém ao menos uma referência. Membro ausente, JSON `null` e lista vazia são omitidos e, portanto, preservam byte a byte o caminho vigente sem anexos.

O ingresso V2 transporta uma lista ordenada de referências opacas. A ordem é significativa. Cada referência é comparada ordinalmente e escrita como string por `Utf8JsonWriter`, com a política vigente `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. O ingresso não ordena, não deduplica e não normaliza as referências. Duplicatas são recusadas antes do hash, do claim e do aceite.

REST e Kafka V1 continuam sem anexos. As superfícies V2 exigem o membro novo com uma lista não vazia. Uma repetição com a mesma chave e a mesma forma canônica devolve o resultado original; uma diferença relevante produz conflito sem novo efeito de aceite. A trilha vigente de replay, recusa e conflito permanece inalterada.

## Contexto

### Fatos observados

- O hash vigente é SHA-256 em hexadecimal minúsculo sobre um objeto JSON compacto. Seus membros são escritos à mão em ordem fixa, e os opcionais ausentes são omitidos ([`RequestNotification.PayloadHash.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs):13-87).
- O writer vigente usa `Utf8JsonWriter` com `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. Arrays preservam a ordem recebida porque cada valor é escrito na sequência de enumeração ([`RequestNotification.PayloadHash.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs):34-53).
- A canonicalização recursiva de `metadata` e `variables` ordena nomes de membros com comparação ordinal, preserva a ordem de arrays e mantém a última ocorrência de uma chave repetida. Essa regra é independente da lista de referências de anexos ([`CanonicalJson.cs`](../src/Platform.Api/Modules/Notifications/Domain/CanonicalJson.cs):7-25,50-118).
- Os testes vigentes congelam os hashes do corpo mínimo e do corpo com todos os opcionais, além de comprovar que a ordem de `channelsHint` participa do payload ([`RequestPayloadHashTests.cs`](../tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs):22-57,106-115).
- A avaliação independente confirmou os dois vetores brownfield, executou dez testes com sucesso e comprovou por mutação que inverter a ordem de membros reprova somente os dois vetores dourados ([avaliação do baseline](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/evaluations/task-01-ac-15.json)).
- O corpus contratual demonstrou que consumidores REST e Kafka V1 antigos descartam silenciosamente `attachments`. Por isso, uma adição em V1 preservaria a sintaxe, mas poderia alterar o efeito sem que o consumidor percebesse ([corpus contratual, Resultado e Prova de compatibilidade](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md)).
- O mesmo corpus congelou ausência, `null`, vazio, ordem, troca de referência e duplicata. Ele também fixou que nome, tipo de mídia, comprimento e identidade do conteúdo pertencem ao snapshot liberado, não ao ingresso ([corpus contratual, Semântica canônica e Vetores congelados](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md)).
- O fluxo vigente registra uma trilha para payload inválido, replay e conflito. Replay mantém o barramento silencioso; conflito pode registrar o evento de rejeição. Esses registros não são efeitos de aceite ([`RequestNotification.Handler.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs):85-105,246-289; [`IIngestionSink.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IIngestionSink.cs):23-53).

### Intenção declarada

`ER-008` exige incorporar referências e propriedades que alteram a entrega à forma canônica, preservar exatamente o hash vigente quando não há anexos e produzir replay ou conflito sem efeito conforme a igualdade do manifesto. O refinamento identificou duas proteções necessárias: congelar o baseline antes da mudança e tratar membro ausente e lista vazia como a mesma forma canônica ([especificação, `ER-008` e Igualdade do manifesto idempotente](SPEC-001/requirements/core/01-development-specification.md); [refinamento, `RF-001`, `RF-003` e seção 4.3](SPEC-001/refinements/00-refinement-consolidated.md#43-raio-de-impacto-e-preservação-do-caminho-sem-anexos)).

### Objetivos

- Manter uma única autoridade de igualdade para o payload aceito, expressa pelo hash canônico já persistido.
- Preservar byte a byte os hashes de solicitações sem anexos.
- Fazer ordem e identidade de cada referência participarem da igualdade idempotente.
- Impedir que normalização implícita transforme duas entregas distintas na mesma solicitação.
- Evitar que propriedades liberadas e imutáveis sejam repetidas no ingresso.

### Fora do escopo

- Definir quantidade máxima, tamanho, tipos permitidos ou envelope da primeira produção.
- Definir custódia, validação, claim, formato persistido do snapshot ou transferência ao provedor.
- Alterar a canonicalização de `metadata`, `variables`, `channelsHint`, datas ou qualquer outro membro vigente.
- Recalcular ou preencher novamente digests já persistidos.
- Adicionar anexos aos contratos REST ou Kafka V1.

### Direcionadores da decisão

1. Toda propriedade do ingresso capaz de selecionar outro conjunto para entrega precisa participar da igualdade idempotente.
2. O caminho sem anexos precisa manter os bytes canônicos e os digests congelados.
3. V1 não pode aceitar silenciosamente um membro cujo efeito consumidores antigos descartam.
4. A referência opaca precisa identificar de forma estável o snapshot liberado.
5. Replay e conflito precisam continuar resolvidos por uma única forma canônica, sem uma segunda autoridade de comparação.

## Decisão

Adotar a alternativa A: incluir `attachments` na forma canônica do hash, omitindo o membro quando ausente, `null` ou vazio.

### Contrato normativo do ingresso

1. REST V2 e Kafka V2 transportam `attachments` como uma lista não vazia e ordenada de strings, cada uma contendo uma referência pública opaca. Membro ausente, JSON `null` e lista vazia são recusados pelo contrato V2 antes do hash, do claim e do aceite.
2. REST V1 e Kafka V1 continuam sem anexos. O V1 recusa especificamente a presença de `attachments`; não reinterpreta, ignora ou encaminha esse membro. A tolerância vigente a outros membros desconhecidos permanece inalterada.
3. O V2 valida a unicidade ordinal das referências antes de calcular o hash, executar o claim ou iniciar o aceite. Uma duplicata recusa a solicitação e não produz digest nem novo efeito de aceite. A trilha de recusa vigente permanece.
4. A comparação entre referências usa semântica ordinal. Nenhuma referência é convertida para outra caixa, aparada, normalizada, ordenada ou deduplicada.
5. A ordem da lista é parte do payload. Inverter duas referências produz outra forma canônica.
6. Quando a lista contém ao menos uma referência, o writer escreve `attachments` imediatamente depois de `application` e antes de `channelsHint`.
7. O writer abre o array com `Utf8JsonWriter`, percorre a lista na ordem recebida e escreve cada referência com `WriteStringValue`. O writer conserva `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, a política atual do hash.
8. Na função canônica, quando `attachments` está ausente, é JSON `null` ou contém uma lista vazia, o writer não escreve o nome do membro nem um array vazio. Esses vetores protegem a compatibilidade da função e do caminho sem anexos, embora o contrato público V2 os recuse antes do cálculo.
9. A ordem completa passa a ser: `application`, `attachments` quando não vazio, `channelsHint`, `class`, `correlationId`, `metadata`, `recipientId`, `scheduledAt`, `templateKey`, `ttlSeconds` e `variables`.
10. Todos os demais membros preservam posição, condição de presença, normalização e regra de escrita vigentes.

### Propriedades determinadas pela referência

Nome, tipo de mídia, comprimento e identidade do conteúdo não entram diretamente no contrato de ingresso nem na forma canônica do pedido. A referência opaca determina essas propriedades no snapshot imutável liberado pelo claim, cuja autoridade e ciclo de vida pertencem à [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md).

Uma referência pública permanece vinculada ao mesmo snapshot liberado. Alterar nome, tipo de mídia, comprimento ou identidade do conteúdo exige nova referência. Essa regra faz a troca participar do hash pela nova referência, sem duplicar propriedades mutáveis no payload de ingresso.

### Igualdade, replay e conflito

- Mesma chave idempotente e igualdade de toda a forma canônica, incluindo a mesma sequência ordinal de referências, devolvem o resultado original.
- Mudar uma referência, sua posição ou qualquer outro membro já participante do hash produz forma diferente e conflito, sem novo claim, notificação ou outbox de aceite. A trilha de rejeição e auditoria vigente continua sendo registrada.
- Ausência, `null` e lista vazia são o mesmo manifesto vazio para o hash. Essa equivalência não autoriza o contrato V1 a receber o membro.
- Duplicata é erro de validação anterior à idempotência. Ela não é ordenada, removida nem convertida em replay.

### Matriz normativa de vetores

Os valores abaixo pertencem ao corpus reproduzível. Os três primeiros usam o corpo mínimo vigente; o corpo completo conserva todos os opcionais já existentes.

| Vetor | Resultado normativo |
|---|---|
| Manifesto ausente | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: null` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: []` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| Corpo completo vigente, sem anexos | `135fb9992e7260f847834935d5dff24a98664975989a3dd57962082b11f6557c` |
| `attachments: ["att_alpha", "att_beta"]` | `5b707f0391c59c39cc4e0547f9f118d25a93d6bc4cbbe796191be44f0b4d8199` |
| `attachments: ["att_beta", "att_alpha"]` | `97b013c55139f5fcd30a3d8685a3c326cde8984fcb232d74bd85552dc1fc789d` |
| `attachments: ["att_alpha", "att_gamma"]` | `71bb9b9799e73874e8eee914f91a2d0e1e9a69307437725073c02b7cb653c0dd` |
| `attachments: ["att_alpha", "att_alpha"]` | Recusa antes do hash, do claim e do aceite |

### Invariantes e mecanismos de garantia

| Invariante | Mecanismo de garantia |
|---|---|
| Solicitações sem anexos preservam bytes e digest | testes com os dois vetores brownfield literais antes e depois da mudança; mutação deliberada da ordem precisa reprovar os vetores |
| Ausente, `null` e vazio possuem uma única forma canônica | teste parametrizado exige o digest mínimo literal para os três vetores |
| Uma lista não vazia entra depois de `application` | vetor literal do corpus e teste de mutação da posição do membro |
| Ordem é significativa | vetores `att_alpha, att_beta` e ordem invertida exigem digests literais distintos |
| Trocar uma referência altera a igualdade | vetor com `att_gamma` exige digest literal distinto; teste integrado exige conflito sem novo efeito de aceite e preserva a trilha de rejeição |
| Duplicata nunca chega à idempotência | teste da ordem dos gates comprova recusa antes do hash; claim, notificação e outbox de aceite permanecem ausentes, e a trilha segue o contrato vigente |
| Comparação é ordinal e sem normalização | casos com diferença de caixa, espaço e sequência permanecem distintos; o validador usa comparação ordinal sem reescrever entrada |
| Demais membros permanecem inalterados | suíte vigente de `RequestPayloadHashTests` continua verde, incluindo opcionais, ordem de `channelsHint` e normalização temporal |
| V1 permanece sem anexos sem fechar sua evolução | testes de contrato REST e Kafka recusam especificamente `attachments` em V1, preservam os snapshots V1 e continuam aceitando um membro desconhecido não relacionado |
| V2 nunca é reinterpretado como V1 | roteamento exige correspondência entre versão da superfície e versão do contrato; ensaio de rollback comprova que V2 desabilitado não cai no handler V1 |
| Replay não cria novo efeito de aceite | suíte integrada repete chave e forma iguais e comprova retorno original sem novo claim, notificação ou outbox de aceite; a trilha de replay permanece |
| Diferença relevante produz conflito sem efeito de aceite | suíte integrada altera referência e ordem separadamente e comprova conflito sem novo claim, notificação ou outbox de aceite; a trilha de rejeição permanece |

## Alternativas consideradas

Uma matriz ponderada criaria precisão falsa. `ER-008` e o corpus fornecem critérios eliminatórios e vetores literais, então a comparação é comportamental.

| Critério | Alternativa A: incluir na forma canônica | Alternativa B: hash intacto e igualdade separada |
|---|---|---|
| Hash sem anexos | preservado pela omissão | preservado |
| Diferença entre conjuntos | representada no digest persistido | depende de uma segunda comparação persistida ou reconstruída |
| Autoridade de igualdade | uma forma canônica | hash mais verificação paralela |
| Replay e caminho rápido | reutilizam o contrato vigente do hash | precisam coordenar dois resultados para não devolver replay indevido |
| Custo de mudança | altera a semântica dos novos digests V2 com anexos | adiciona modelo, persistência e verificação de igualdade fora do hash |
| Reversão após aceite V2 | lógica, com preservação dos digests | lógica, com preservação da segunda autoridade |

### Alternativa A: incluir `attachments` na forma canônica

Preserva o mecanismo vigente, faz toda diferença relevante participar do digest e mantém uma única autoridade idempotente. A omissão de ausência, `null` e vazio protege o baseline brownfield. Foi promovida porque os vetores reproduzíveis demonstram simultaneamente compatibilidade sem anexos e distinção de ordem ou referência.

### Alternativa B: manter o hash intacto e verificar o manifesto separadamente

Evitaria alterar o digest calculado para novos pedidos com anexos e poderia isolar a evolução do manifesto. Foi rejeitada porque separaria a igualdade entre o hash persistido e outra verificação. Cada replay, corrida, cache e caminho de conflito teria de coordenar ambas as autoridades de forma atômica. O custo e a superfície de falha adicionais não oferecem vantagem diante de uma forma canônica que já preserva exatamente o caminho sem anexos.

## Consequências

### Positivas

- A identidade idempotente representa o conjunto e a ordem solicitados.
- O caminho sem anexos mantém os dois digests brownfield e os bytes canônicos vigentes.
- Replay, corrida e conflito continuam dependentes de uma única comparação.
- O ingresso permanece mínimo e não repete nome, tipo, comprimento nem identidade do conteúdo.
- A escrita percorre a lista uma vez e não introduz ordenação ou canonicalização recursiva para referências.

### Negativas e contrapartidas aceitas

- Depois do primeiro aceite V2 com anexos, a semântica do digest passa a depender da versão do contrato e da presença do manifesto.
- V1 e V2 precisam coexistir durante o rollout e enquanto houver produtores antigos.
- A ordem significativa pode gerar conflito quando um produtor envia o mesmo conjunto em outra sequência. Esse comportamento é deliberado porque a ordem compõe a solicitação.
- Produtores precisam tratar duplicatas como erro de contrato, sem correção automática pelo hub.
- Uma futura propriedade de entrega determinada pelo produtor exigirá decidir explicitamente se entra na forma canônica.

### Custos operacionais e de engenharia

- Manter rotas, documento OpenAPI, tópico e tipo de evento V2 em paralelo com V1.
- Executar testes de contrato e de versões mistas antes de habilitar o writer V2.
- Preservar leitores V2 e estado aceito durante rollback e drenagem.
- Tratar qualquer alteração da forma, ordem ou política de escaping como mudança do contrato persistido, não como refatoração local.

## Rollout, irreversibilidade e rollback

### Irreversibilidade

Cada digest persistido continua válido para o registro e para a semântica contratual sob a qual foi aceito. Digests gravados com a forma anterior não são recalculados, preenchidos novamente nem reinterpretados pela forma V2. A implantação não executa backfill de `payload_hash`.

Depois do primeiro aceite V2 com anexos, remover o membro do cálculo não restaura o estado anterior. A base passa a conter registros aceitos sob contratos coexistentes, e a versão da superfície continua determinando como cada solicitação pode ser interpretada. Uma solicitação V2 jamais é reprocessada como V1, inclusive durante rollback.

### Sequência de rollout

1. Manter congelados os vetores brownfield e os vetores do corpus, com prova de mutação ativa.
2. Publicar leitores V2 no REST e no Kafka, o roteamento por versão e os leitores internos necessários, mantendo ingressos V2 desabilitados.
3. Publicar o suporte aditivo dos consumidores internos e confirmar que todos os nós reconhecem V2. V1 continua sem anexos.
4. Implantar o writer da forma canônica e a validação de duplicatas ainda com o ingresso V2 desabilitado.
5. Executar a matriz de contratos, hash, idempotência e versões mistas. A combinação de writer V2 com leitor incompatível é proibida.
6. Habilitar produtores V2 progressivamente. Não alterar rotas, tópicos ou tipos V1.
7. Manter V1 disponível enquanto existirem produtores antigos.

### Rollback lógico

1. Desabilitar novos ingressos REST e Kafka V2.
2. Continuar aceitando V1 sem anexos.
3. Manter leitores V2, snapshots, claims e processamento dos itens já aceitos até a drenagem completa.
4. Preservar digests e demais dados. Não executar recalculação, backfill, migração descendente nem conversão de V2 para V1.
5. Remover leitores V2 somente depois de demonstrar que não existe estado com anexos pendente de processamento, retry, fallback, reconciliação ou investigação.

## Riscos e mitigação

| Risco | Consequência | Mitigação |
|---|---|---|
| Writer V2 habilitado antes de todos os leitores | replay legítimo pode virar conflito ou um nó pode ignorar o manifesto | leitores antes do writer, gate de versões mistas e habilitação do ingresso somente após compatibilidade integral |
| Lista vazia escrita como membro presente | quebra dos vetores brownfield e duas formas para o caminho sem anexos | condição normativa `Count > 0` e vetor literal para ausência, `null` e vazio |
| Ordenação ou deduplicação acidental | duas solicitações distintas podem compartilhar identidade | vetores de ordem, troca e duplicata, mais mutação deliberada |
| Mudança da política de escaping | alteração ampla de digests sem mudança aparente de negócio | fixação explícita de `UnsafeRelaxedJsonEscaping` e vetores literais |
| Propriedade liberada mudar sob a mesma referência | hash permanece igual para uma entrega diferente | referência imutável vinculada ao snapshot; qualquer alteração exige nova referência |
| Rollback remover leitores cedo demais | itens aceitos deixam de ser processáveis ou investigáveis | rollback lógico com drenagem comprovada antes da remoção |

## Trabalho futuro

- Materializar o membro opcional e a validação de duplicatas no comando V2.
- Incorporar o bloco de escrita ao hash exatamente na posição e sob as condições definidas neste ADR.
- Implementar os leitores e roteadores REST e Kafka V2, mantendo a recusa explícita em V1.
- Ampliar a suíte integrada de idempotência para replay, conflito e corrida com manifesto.
- Executar o ensaio de versões mistas antes da habilitação operacional.

Este trabalho não altera a decisão. Mudança da forma canônica, da regra de referência imutável ou da separação V1 e V2 exige revisão arquitetural.

## Condições de revisão

Reabrir a decisão se ocorrer qualquer uma destas condições:

- uma referência puder ser vinculada a outro nome, tipo de mídia, comprimento ou identidade de conteúdo;
- produtores puderem escolher outra propriedade de apresentação ou entrega que não seja determinada pela referência;
- a ordem deixar de representar uma diferença relevante de entrega;
- a estratégia de versão não puder manter REST ou Kafka V2 separados de V1;
- outro canal passar a preservar anexos com semântica de composição diferente;
- o mecanismo de idempotência deixar de usar o hash canônico como autoridade;
- uma mudança na política de serialização, escaping ou runtime exigir alterar os bytes canônicos.

## Evidência

| Afirmação | Evidência |
|---|---|
| `ER-008` exige manifesto no hash, replay ou conflito e preservação do caminho sem anexos | [Especificação de desenvolvimento](SPEC-001/requirements/core/01-development-specification.md), `ER-008` e decisão Igualdade do manifesto idempotente |
| O refinamento exige omissão para ausente e vazio e posiciona o membro depois de `application` | [Refinamento consolidado](SPEC-001/refinements/00-refinement-consolidated.md#43-raio-de-impacto-e-preservação-do-caminho-sem-anexos), `FT-10`, `RF-001` e `RF-003` |
| Os digests brownfield foram congelados e submetidos a teste de mutação | [Avaliação independente do baseline](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/evaluations/task-01-ac-15.json) e [`RequestPayloadHashTests.cs`](../tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs):22-57 |
| O writer atual fixa ordem, `Utf8JsonWriter` e `UnsafeRelaxedJsonEscaping` | [`RequestNotification.PayloadHash.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs):13-87 |
| Arrays preservam ordem e objetos aninhados usam comparação ordinal | [`CanonicalJson.cs`](../src/Platform.Api/Modules/Notifications/Domain/CanonicalJson.cs):7-25,50-118 |
| O corpus fixa V2, referências opacas, omissão, ordem, unicidade e os digests novos | [Corpus contratual do manifesto](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md) |
| A Tarefa 12 exige posição, omissão e consequência sobre digests persistidos | [Delivery Slice, Tarefa 12](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md#tarefa-12-escrever-o-adr-da-entrada-do-manifesto-na-forma-canônica-idempotente) |
| Claim e snapshot possuem decisões próprias e não são redefinidos aqui | [ADR-0018](ADR-0018-claim-atomico-na-transacao-de-aceite.md) e [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md) |
| O Stack Profile mantém .NET 10, monólito modular, System.Text.Json e ausência de mediator | [Stack Profile](../.araia/stack-profile.yaml) |

## Referências

- [Especificação de desenvolvimento, `ER-008` e decisão Igualdade do manifesto idempotente](SPEC-001/requirements/core/01-development-specification.md).
- [Refinamento consolidado, `FT-10`, `RF-001`, `RF-003`, seção 4.3 e decisões que exigem ADR](SPEC-001/refinements/00-refinement-consolidated.md).
- [Corpus contratual do manifesto e decisão de versão](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md).
- [Avaliação independente dos vetores brownfield](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/evaluations/task-01-ac-15.json).
- [Delivery Slice, Tarefa 12](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md#tarefa-12-escrever-o-adr-da-entrada-do-manifesto-na-forma-canônica-idempotente).
- [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md).
- [ADR-0019: Snapshot do manifesto aceito na notificação](ADR-0019-snapshot-do-manifesto-aceito.md).
- [`RequestPayloadHashTests`](../tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs).
- [`RequestNotification.PayloadHash`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs).
- [`CanonicalJson`](../src/Platform.Api/Modules/Notifications/Domain/CanonicalJson.cs).
- [Stack Profile](../.araia/stack-profile.yaml).
