---
language: pt-BR
---

# ADR-0021: Manifesto de anexos na forma canônica do ingresso publicado

**Status**: ACCEPTED

| Campo | Valor |
|---|---|
| **Data** | 2026-09-02 |
| **Responsável** | `dotnet-architect` |
| **Audiência** | Arquitetura e engenharia dos módulos `Notifications` e `AttachmentManagement`, responsáveis pelo contrato REST e pelo contrato do barramento, responsáveis pelo rollout |
| **Aprovação** | Dono do produto, por decisão de escopo declarada em 2026-09-02 |
| **Escopo da decisão** | Representação das referências de anexos na forma canônica usada pelo hash de idempotência do ingresso, e a superfície pública que a transporta |
| **Substitui** | [ADR-0020: Entrada do manifesto na forma canônica idempotente](ADR-0020-entrada-do-manifesto-na-forma-canonica-idempotente.md), cujo enquadramento de versão foi invalidado pela decisão de escopo |
| **Relacionadas** | [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md); [ADR-0019: Snapshot do manifesto aceito](ADR-0019-snapshot-do-manifesto-aceito.md); [ADR-0008: Entrega at-least-once com idempotência](ADR-0008-at-least-once-com-idempotencia.md) |
| **Fontes** | [Especificação de desenvolvimento](SPEC-001/requirements/core/01-development-specification.md); [refinamento consolidado](SPEC-001/refinements/00-refinement-consolidated.md); [corpus contratual do manifesto](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md); [Delivery Slice](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md) |
| **Código afetado** | Contrato público de ingresso; comando e validador; `RequestNotification.PayloadHash`; `IngressRequestBinder`; testes de contrato, hash e idempotência |

## Resumo executivo

Incluir `attachments` na forma canônica do hash de idempotência. O membro é escrito imediatamente depois de `application` somente quando a lista existe e contém ao menos uma referência. Membro ausente, JSON `null` e lista vazia não produzem membro algum, portanto o caminho sem anexos preserva byte a byte os digests vigentes.

O manifesto viaja no contrato publicado V1, a única versão que existe. Membro ausente e JSON `null` são legais e significam ausência de anexos. Somente lista vazia, referência que não nomeia nada e repetição ordinal são recusadas, antes do hash e antes de qualquer efeito durável. A ordem é significativa, a comparação é ordinal, e o ingresso não ordena, não deduplica e não normaliza referência alguma.

Não existe V2, não existe documento de máquina paralelo, não existe tópico ou tipo de evento paralelo, e nada foi marcado como obsoleto. Esta ADR substitui a ADR-0020, cujas cláusulas de coexistência de versões, roteamento por versão e recusa de membro ausente descreviam um mundo que a decisão de escopo do dono do produto encerrou.

## Contexto

### Decisão de escopo do dono do produto, 2026-09-02

O dono do produto declarou, literalmente:

> O serviço é novo e não tem nada em produção. Não existe V2 e não existe nada obsoleto. O mesmo vale para as migrations.

Perguntado sobre o histórico de migrações, decidiu também esmagar todas as migrações em uma inicial, em vez de manter cadeia com migração aditiva. A consequência dessa segunda decisão sobre a persistência do snapshot está registrada na errata da [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md), que é onde o implementador da migração vai tropeçar nela.

Três premissas da ADR-0020 caem com essa declaração:

1. **Consumidores antigos em produção**, que sustentavam o argumento de que uma adição em V1 preservaria a sintaxe e alteraria o efeito sem que ninguém percebesse. Não existe consumidor antigo, porque não existe implantação anterior.
2. **Coexistência de versões durante o rollout**, que sustentava a sequência de sete passos, o ensaio de versões mistas e o rollback lógico por versão. Não há o que coexistir.
3. **Digests persistidos sob contrato anterior**, que sustentavam a seção de irreversibilidade. Não existe digest persistido em produção; os dois digests brownfield existem como vetores congelados em teste, não como dado.

### Fatos observados

- O comando de ingresso carrega o manifesto como lista opcional de strings ([`RequestNotification.Command.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs):45).
- O validador recusa exatamente três formas: lista vazia, referência que não nomeia nada e repetição ordinal. Membro ausente e `null` não encontram regra alguma ([`RequestNotification.Validator.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs):125-176).
- A rota que carrega o manifesto é a rota em que toda notificação é solicitada, `/v1/notifications` ([`RequestNotification.Endpoint.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs):22,84).
- O documento de máquina publicado nomeia `attachments` como lista de strings opacas, sem descrever membros do item. O documento declara uma única versão de rota, e `/openapi/v2.json` responde 404 ([`AttachmentContractSurfaceTests.cs`](../tests/Platform.IntegrationTests/AttachmentContractSurfaceTests.cs):34-96).
- A validação precede o cálculo do hash no mesmo caminho, portanto nenhuma das três recusas alcança o digest ([`RequestNotification.Handler.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs):87,105).
- **A função canônica ainda não conhece o membro.** O writer escreve `application`, `channelsHint`, `class`, `correlationId`, `metadata`, `recipientId`, `scheduledAt`, `templateKey`, `ttlSeconds` e `variables`, e nada mais ([`RequestNotification.PayloadHash.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs):34-84).
- **O binder do barramento não lê o membro.** Ele monta o comando a partir de nomes procurados um a um, e `attachments` não está entre eles, portanto um corpo publicado com manifesto vincula sem manifesto ([`KafkaIngressProcessor.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs):305-364).
- O corpus contratual comprovou as duas direções de vinculação: um corpo antigo, sem o membro, vincula no contrato novo com manifesto nulo; e um corpo novo, com o membro, vincula em um contrato que não o declara, que o descarta em silêncio ([corpus, `Program.cs`](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus/Program.cs):60-77).
- Os digests do corpus foram calculados sobre o corpo mínimo, exceto o vetor do corpo completo, e a rota que transporta o corpo não participa do cálculo ([corpus, `Program.cs`](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus/Program.cs):29-51).
- Os dois digests brownfield estão congelados em teste ([`RequestPayloadHashTests.cs`](../tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs):22-57).

### Lacuna aberta hoje entre o contrato e a identidade

O contrato publicado nomeia `attachments`, e a autoridade de identidade o ignora. Hoje, duas solicitações que diferem somente pelo manifesto produzem o mesmo digest, portanto a segunda é tratada como repetição da primeira, e não como outra solicitação. Essa lacuna nasceu de a superfície pública ter chegado antes da forma canônica, e é exatamente o que a tarefa de forma canônica fecha. Enquanto ela existir, o membro é publicado e inerte para a idempotência.

A mesma lacuna, na sua versão pior, existe no barramento: um produtor que publicar um manifesto recebe aceite de uma notificação sem anexos, com sintaxe válida e efeito diferente do pedido.

### Intenção declarada

`ER-008` exige incorporar referências e propriedades que alteram a entrega à forma canônica, preservar exatamente o hash vigente quando não há anexos e produzir repetição ou conflito sem efeito conforme a igualdade do manifesto. O refinamento identificou duas proteções necessárias: congelar o baseline antes da mudança e tratar membro ausente e lista vazia como a mesma forma canônica ([especificação, `ER-008`](SPEC-001/requirements/core/01-development-specification.md); [refinamento, `RF-001` e `RF-003`](SPEC-001/refinements/00-refinement-consolidated.md#43-raio-de-impacto-e-preservação-do-caminho-sem-anexos)).

### Objetivos

- Manter uma única autoridade de igualdade para o payload aceito, expressa pelo hash canônico já persistido.
- Preservar byte a byte os digests de solicitações sem anexos.
- Fazer ordem e identidade de cada referência participarem da igualdade idempotente.
- Impedir que normalização implícita transforme duas entregas distintas na mesma solicitação.
- Impedir que qualquer superfície publicada aceite um manifesto e prossiga como se não o tivesse recebido.
- Evitar que propriedades liberadas e imutáveis sejam repetidas no ingresso.

### Fora do escopo

- Definir quantidade máxima, tamanho máximo, tipos admitidos ou envelope da primeira produção. Esses valores pertencem ao portão de quantidade, tamanho, tipos e envelope.
- Definir custódia, validação, liberação, claim, formato persistido do snapshot ou transferência ao provedor.
- Alterar a canonicalização de `metadata`, `variables`, `channelsHint`, datas ou qualquer outro membro vigente.
- Definir a cadeia de migrações, que segue a decisão de esmagamento registrada na errata da ADR-0019.

### Direcionadores da decisão

1. Toda propriedade do ingresso capaz de selecionar outro conjunto para entrega precisa participar da igualdade idempotente.
2. O caminho sem anexos precisa manter os bytes canônicos e os digests congelados.
3. Um produtor que nunca ouviu falar do membro precisa continuar funcionando sem alteração, e é isso que torna a adição compatível.
4. Uma superfície que aceita o membro e o descarta produz efeito diferente do pedido com sintaxe válida, que é a falha mais cara desta capacidade.
5. A referência opaca precisa identificar de forma estável o snapshot liberado.
6. Repetição e conflito precisam continuar resolvidos por uma única forma canônica, sem uma segunda autoridade de comparação.

## Decisão

Incluir `attachments` na forma canônica do hash, omitindo o membro quando ausente, `null` ou vazio, sobre o contrato publicado V1, que é o único que existe.

### Contrato normativo do ingresso

1. O manifesto viaja no contrato publicado V1. Não existe segunda rota, segundo documento de máquina, segundo tópico ou segundo tipo de evento para esta capacidade, e nenhuma superfície vigente é marcada como obsoleta.
2. `attachments` é opcional. Membro ausente e JSON `null` são legais, significam ausência de anexos e não encontram regra alguma. Nenhum dos dois é recusado.
3. São recusadas, antes do hash e de qualquer efeito durável, exatamente três formas: lista vazia, referência que não nomeia nada e repetição ordinal de uma referência. A lista vazia é recusada porque é um produtor pedindo anexos sem nomear nenhum, que é diferente de um produtor que não pediu anexos.
4. Cada recusa responde pela lista inteira, e não por posição, para que um manifesto enorme seja recusado por uma frase e não por uma recusa do tamanho do pedido.
5. A comparação entre referências usa semântica ordinal. Nenhuma referência é convertida para outra caixa, aparada, normalizada, ordenada ou deduplicada.
6. A ordem da lista é parte do payload. Inverter duas referências produz outra forma canônica.
7. Quando a lista existe e contém ao menos uma referência, o writer escreve `attachments` imediatamente depois de `application` e antes de `channelsHint`.
8. O writer abre o array com `Utf8JsonWriter`, percorre a lista na ordem recebida e escreve cada referência como valor de string. O writer conserva `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, a política atual do hash.
9. Quando o membro está ausente, é JSON `null` ou contém lista vazia, o writer não escreve o nome do membro nem um array vazio.
10. A ordem completa passa a ser: `application`, `attachments` quando não vazio, `channelsHint`, `class`, `correlationId`, `metadata`, `recipientId`, `scheduledAt`, `templateKey`, `ttlSeconds` e `variables`.
11. Todos os demais membros preservam posição, condição de presença, normalização e regra de escrita vigentes. `locale` continua deliberadamente fora do payload de idempotência.
12. **Nenhuma superfície publicada pode aceitar um corpo que nomeia `attachments` e prosseguir como se não o tivesse recebido.** Uma superfície que ainda não transporta o membro precisa recusar o corpo que o nomeia, pelo caminho de recusa que ela já possui e com motivo declarado, em vez de vincular descartando o membro. Enquanto o binder do barramento não transportar o manifesto, ele está fora de conformidade com esta cláusula, e essa é a condição que a tarefa do leitor do barramento fecha.

### Ausente, `null` e vazio deixaram de ser detalhe defensivo

Na ADR-0020, os três vetores eram protegidos por uma regra de função enquanto o contrato os recusava. Agora, dois dos três são alcançáveis pelo contrato, e a igualdade entre eles é comportamento de produção observável:

| Vetor | Alcançável pelo contrato | Papel da equivalência |
|---|---|---|
| Membro ausente | sim | um produtor que não conhece o membro precisa produzir o mesmo digest de sempre |
| `attachments: null` | sim | uma biblioteca cliente que serializa opcional como `null` precisa repetir, e não conflitar, com o mesmo pedido enviado sem o membro |
| `attachments: []` | não | vetor de função: protege a forma canônica contra um chamador interno que não passe pelo validador |

Duas requisições com a mesma chave idempotente, uma omitindo o membro e outra enviando `null`, precisam devolver repetição, nunca conflito. Isso não é mais uma defesa: é o contrato.

### Propriedades determinadas pela referência

Nome, tipo de mídia, comprimento e identidade do conteúdo não entram no contrato de ingresso nem na forma canônica do pedido. A referência opaca determina essas propriedades no snapshot imutável liberado pelo claim, cuja autoridade e ciclo de vida pertencem à [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md).

Uma referência pública permanece vinculada ao mesmo snapshot liberado. Alterar nome, tipo de mídia, comprimento ou identidade do conteúdo exige nova referência. Essa regra faz a troca participar do hash pela nova referência, sem duplicar propriedade mutável no payload de ingresso.

### Igualdade, repetição e conflito

- Mesma chave idempotente e igualdade de toda a forma canônica, incluindo a mesma sequência ordinal de referências, devolvem o resultado original.
- Mudar uma referência, sua posição ou qualquer outro membro já participante do hash produz forma diferente e conflito, sem novo claim, notificação ou outbox de aceite. A trilha de rejeição e auditoria vigente continua sendo registrada.
- Ausência, `null` e lista vazia são o mesmo manifesto vazio para o hash.
- Lista vazia, referência em branco e duplicata são erro de validação anterior à idempotência. Nenhuma delas é ordenada, removida nem convertida em repetição.
- O digest é função pura do comando vinculado e é calculado antes de qualquer efeito durável. Uma solicitação cujo claim falhe aborta a transação inteira e não deixa digest, chave nem notificação persistidos, conforme a ADR-0018.

### Matriz normativa de vetores

Os valores abaixo pertencem ao corpus reproduzível e foram calculados sobre o corpo mínimo, exceto a linha do corpo completo. A reformulação de versão não toca em byte algum do corpo, portanto os digests da ADR-0020 permanecem válidos sem recálculo.

| Vetor | Resultado normativo |
|---|---|
| Manifesto ausente | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: null` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: []` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb`, inalcançável pelo contrato |
| Corpo completo vigente, sem anexos | `135fb9992e7260f847834935d5dff24a98664975989a3dd57962082b11f6557c` |
| `attachments: ["att_alpha", "att_beta"]` | `5b707f0391c59c39cc4e0547f9f118d25a93d6bc4cbbe796191be44f0b4d8199` |
| `attachments: ["att_beta", "att_alpha"]` | `97b013c55139f5fcd30a3d8685a3c326cde8984fcb232d74bd85552dc1fc789d` |
| `attachments: ["att_alpha", "att_gamma"]` | `71bb9b9799e73874e8eee914f91a2d0e1e9a69307437725073c02b7cb653c0dd` |
| `attachments: ["att_alpha", "att_alpha"]` | recusa antes do hash, do claim e do aceite |

### Invariantes e mecanismos de garantia

| Invariante | Mecanismo de garantia |
|---|---|
| Solicitações sem anexos preservam bytes e digest | os dois vetores brownfield literais permanecem congelados antes e depois da mudança; mutação deliberada da ordem precisa reprovar os dois |
| Ausente, `null` e vazio possuem uma única forma canônica | teste parametrizado exige o digest mínimo literal para os três vetores; teste integrado repete a mesma chave com membro ausente e com `null` e exige repetição, não conflito |
| Uma lista não vazia entra depois de `application` | vetor literal do corpus e teste de mutação da posição do membro |
| Ordem é significativa | vetores `att_alpha, att_beta` e ordem invertida exigem digests literais distintos |
| Trocar uma referência altera a igualdade | vetor com `att_gamma` exige digest literal distinto; teste integrado exige conflito sem novo efeito de aceite |
| Lista vazia, referência em branco e duplicata nunca chegam à idempotência | teste da ordem dos gates comprova recusa antes do hash; claim, notificação e outbox de aceite permanecem ausentes |
| Comparação é ordinal e sem normalização | casos com diferença de caixa e de espaço permanecem duas referências distintas |
| Um produtor que não conhece o membro continua funcionando | corpo sem o membro vincula com manifesto nulo e produz o digest congelado; teste integrado aceita a solicitação sem alteração |
| Existe uma única superfície de escrita | o documento publicado declara uma única versão de rota e nenhum segundo documento é servido |
| Nenhuma superfície aceita o manifesto e o descarta | teste do binder do barramento: um corpo que nomeia o manifesto ou é transportado integralmente ou é recusado com motivo declarado, e nunca vincula sem o membro |
| Demais membros permanecem inalterados | a suíte vigente do hash continua verde, incluindo opcionais, ordem de `channelsHint` e normalização temporal |

## Alternativas consideradas

Uma matriz ponderada criaria precisão falsa. Os critérios são eliminatórios e comportamentais.

### Forma canônica: incluir o manifesto ou manter uma segunda autoridade

| Critério | Incluir na forma canônica | Hash intacto e igualdade separada |
|---|---|---|
| Hash sem anexos | preservado pela omissão | preservado |
| Diferença entre conjuntos | representada no digest persistido | depende de segunda comparação persistida ou reconstruída |
| Autoridade de igualdade | uma forma canônica | hash mais verificação paralela |
| Repetição e caminho rápido | reutilizam o contrato vigente do hash | precisam coordenar dois resultados para não devolver repetição indevida |
| Custo de mudança | altera a semântica dos digests com manifesto | adiciona modelo, persistência e verificação de igualdade fora do hash |

Incluir na forma canônica foi promovida: preserva o mecanismo vigente, faz toda diferença relevante participar do digest e mantém uma única autoridade idempotente. A alternativa da igualdade separada foi rejeitada porque separaria a igualdade entre o hash persistido e outra verificação, e cada repetição, corrida, cache e caminho de conflito teria de coordenar as duas autoridades de forma atômica.

### Versão do contrato: encerrada pelo produto, não pela engenharia

Em 2026-08-31, o corpus contratual promoveu contratos V2 coexistentes. O fundamento era um fato medido: um corpo com `attachments` vincula em um contrato que não o declara e o membro é descartado em silêncio, o que preserva a sintaxe e altera o efeito. Esse fato continua verdadeiro e continua sendo o risco de implantação registrado mais abaixo.

O que caiu não foi o fato, foi a premissa que o transformava em argumento de versão: só existe consumidor antigo se existir implantação anterior. Com o serviço sem nada em produção, criar V2 pagaria coexistência de rota, documento, tópico, tipo de evento, roteamento, ensaio de versões mistas e rollback por versão para proteger um produtor que não existe.

O documento-mãe estabelece que mudanças incompatíveis criam versão nova. Acrescentar um membro opcional cujo valor ausente significa exatamente o comportamento anterior é mudança compatível: nenhum produtor precisa mudar, e o digest de quem não usa o membro não se altera. Portanto a adição em V1 não abre exceção à regra de versionamento, ela se classifica dentro dela.

## Consequências

### Positivas

- A identidade idempotente representa o conjunto e a ordem solicitados.
- O caminho sem anexos mantém os dois digests brownfield e os bytes canônicos vigentes.
- Repetição, corrida e conflito continuam dependentes de uma única comparação.
- Existe uma superfície de escrita, um documento de máquina e um catálogo de recusa, e um membro acrescentado a essa superfície alcança todos os produtores de uma vez.
- Some o custo permanente de coexistência: nenhuma rota, tópico, tipo de evento, roteador por versão ou ensaio de versões mistas precisa ser mantido.
- O ingresso permanece mínimo e não repete nome, tipo, comprimento nem identidade do conteúdo.

### Negativas e contrapartidas aceitas

- A partir do primeiro produtor em produção, `attachments` é membro público publicado de V1. Removê-lo ou alterar sua semântica passa a ser mudança quebradora, e aí sim exigiria versão nova pela regra do documento-mãe.
- O hub nunca distingue um produtor que não quis anexos de um produtor que não conhece o membro. As duas intenções produzem a mesma notificação e o mesmo digest. Esse é o preço da compatibilidade, e é ele que faz a adição não quebrar ninguém.
- A ordem significativa pode gerar conflito quando um produtor envia o mesmo conjunto em outra sequência. O comportamento é deliberado, porque a ordem compõe a solicitação.
- Produtores precisam tratar lista vazia e duplicata como erro de contrato, sem correção automática pelo hub.
- O descarte silencioso do membro por um processo que não o conheça continua possível na primeira implantação que o publica. O que fecha essa janela é ordenação de implantação, não versão de contrato.
- Uma futura propriedade de entrega escolhida pelo produtor exigirá decidir explicitamente se entra na forma canônica.

### Custos operacionais e de engenharia

- Manter os vetores literais congelados e a prova de mutação ativa, porque eles são a única defesa contra uma mudança acidental de forma, ordem ou política de escaping.
- Tratar qualquer alteração da forma, da ordem ou da política de escaping como mudança do contrato persistido, não como refatoração local.
- Acrescentar ao catálogo de motivos de recusa uma linha por recusa nova, porque esse catálogo é verificado por função de adequação.

## Rollout e reversibilidade

### Reversibilidade

Enquanto não existir aceite com anexos em produção, remover o membro do cálculo restaura exatamente o estado anterior, porque não existe digest com manifesto persistido. Essa janela de reversão barata é consequência direta de o serviço não ter nada em produção, e ela fecha no primeiro aceite com anexos.

Depois desse ponto, a reversão é lógica: bloquear novos aceites com anexos, continuar processando os itens já aceitos, preservar digests e dados, e não executar recálculo nem backfill de `payload_hash`.

### Sequência

1. Manter congelados os dois vetores brownfield e os vetores do corpus, com prova de mutação ativa.
2. Fechar a lacuna entre o contrato publicado e a autoridade de identidade, incorporando o membro à forma canônica na posição e sob as condições definidas acima. Enquanto essa etapa não estiver concluída, o contrato promete um membro que a idempotência ignora.
3. Fazer o binder do barramento transportar o manifesto, ou recusar com motivo declarado o corpo que o nomeia. Nenhuma habilitação pode ocorrer com o binder descartando o membro em silêncio.
4. Ampliar a suíte integrada de idempotência para repetição, conflito e corrida com manifesto, incluindo o par membro ausente contra `null` sob a mesma chave.
5. Garantir, na implantação que publica o membro, que nenhuma instância comece a aceitar produção com manifesto antes que todas as instâncias o transportem. Essa é a única ordenação que substitui o antigo ensaio de versões mistas.
6. Habilitar o aceite com anexos somente depois de satisfeitas as obrigações já registradas em outro lugar: a existência do verificador de conteúdo e o versionamento habilitado no depósito de produção. Enquanto a lista de tipos admitidos permanecer vazia, nenhum anexo é liberado, portanto nenhuma solicitação com manifesto sobrevive ao claim, mesmo sendo aceita pelo contrato.

Não existe ensaio de versões mistas de contrato, porque não existem duas versões.

## Riscos e mitigação

| Risco | Consequência | Mitigação |
|---|---|---|
| O contrato publica o membro e a identidade o ignora | duas solicitações que diferem só pelo manifesto são tratadas como a mesma, e a segunda recebe repetição | fechar a forma canônica antes de qualquer habilitação; o risco é o estado vigente, não uma hipótese |
| O barramento vincula o corpo e descarta o membro | o produtor recebe aceite de uma notificação sem os anexos que pediu, com sintaxe válida | transportar ou recusar com motivo declarado; teste que proíbe vincular sem o membro |
| Instância antiga na primeira implantação que publica o membro | mesmo efeito do item anterior, com janela curta | ordenação de implantação: todas as instâncias transportam o membro antes que qualquer produtor o envie |
| Lista vazia escrita como membro presente | quebra dos vetores brownfield e duas formas para o caminho sem anexos | condição normativa de lista não vazia e vetor literal para os três vetores equivalentes |
| Ordenação ou deduplicação acidental | duas solicitações distintas passam a compartilhar identidade | vetores de ordem, troca e duplicata, mais mutação deliberada |
| Mudança da política de escaping | alteração ampla de digests sem mudança aparente de negócio | fixação explícita de `UnsafeRelaxedJsonEscaping` e vetores literais |
| Propriedade liberada mudar sob a mesma referência | o hash permanece igual para uma entrega diferente | referência imutável vinculada ao snapshot; qualquer alteração exige nova referência |

## Trabalho futuro

- Incorporar o bloco de escrita ao hash exatamente na posição e sob as condições definidas nesta ADR.
- Fazer o binder do barramento transportar o manifesto no tipo de evento vigente, ou recusar o corpo que o nomeia enquanto não o transportar.
- Ampliar a suíte integrada de idempotência para repetição, conflito e corrida com manifesto.
- Atualizar o guia do produtor e o catálogo de motivos de recusa para descrever o membro, as três recusas e a legalidade de ausente e `null`.

Este trabalho não altera a decisão. Mudança da forma canônica, da regra de referência imutável ou da unicidade da superfície publicada exige revisão arquitetural.

## Condições de revisão

Reabrir a decisão se ocorrer qualquer uma destas condições:

- uma referência puder ser vinculada a outro nome, tipo de mídia, comprimento ou identidade de conteúdo;
- produtores puderem escolher outra propriedade de apresentação ou entrega que não seja determinada pela referência;
- a ordem deixar de representar uma diferença relevante de entrega;
- o serviço passar a ter produtor em produção, momento em que a janela de reversão barata fecha e qualquer mudança incompatível no membro passa a exigir versão nova;
- outro canal passar a preservar anexos com semântica de composição diferente;
- o mecanismo de idempotência deixar de usar o hash canônico como autoridade;
- uma mudança na política de serialização, escaping ou runtime exigir alterar os bytes canônicos.

## Evidência

| Afirmação | Evidência |
|---|---|
| O comando de ingresso carrega o manifesto como lista opcional | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs:45` |
| O validador recusa lista vazia, referência em branco e duplicata, e não recusa ausente nem `null` | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs:125-176`; `tests/Platform.UnitTests/Notifications/AttachmentManifestContractTests.cs:137-217` |
| A rota que carrega o manifesto é a rota vigente de ingresso | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs:22,84`; `tests/Platform.IntegrationTests/Notifications/AttachmentContractIngressTests.cs:19-43` |
| O documento publicado nomeia o manifesto, declara uma única versão e não há segundo documento | `tests/Platform.IntegrationTests/AttachmentContractSurfaceTests.cs:34-96` |
| A validação precede o cálculo do hash | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs:87,105` |
| A função canônica ainda não escreve o membro | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs:34-84` |
| O binder do barramento não lê o membro | `src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs:305-364` |
| Um corpo sem o membro vincula com manifesto nulo, e um corpo com o membro é descartado por um contrato que não o declara | `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus/Program.cs:60-77` |
| Os digests do corpus foram calculados sobre o corpo mínimo, exceto o do corpo completo | `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus/Program.cs:29-51` |
| Os dois digests brownfield estão congelados em teste | `tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs:22-57` |
| O documento-mãe define que mudança incompatível cria versão nova | `docs/notification-hub-system-design.md:881` |
| A habilitação do aceite está condicionada ao verificador de conteúdo, e a lista de tipos admitidos permanece vazia | [pacote de decisão do portão da política executável](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-18-decision-package.md) |
| Claim e snapshot possuem decisões próprias e não são redefinidos aqui | [ADR-0018](ADR-0018-claim-atomico-na-transacao-de-aceite.md) e [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md) |
| O Stack Profile mantém .NET 10, monólito modular, System.Text.Json e ausência de mediator | [Stack Profile](../.araia/stack-profile.yaml) |

## Referências

- [ADR-0020: Entrada do manifesto na forma canônica idempotente](ADR-0020-entrada-do-manifesto-na-forma-canonica-idempotente.md), substituída por esta decisão.
- [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md).
- [ADR-0019: Snapshot do manifesto aceito na notificação](ADR-0019-snapshot-do-manifesto-aceito.md).
- [Especificação de desenvolvimento, `ER-008`](SPEC-001/requirements/core/01-development-specification.md).
- [Refinamento consolidado, `RF-001`, `RF-003` e seção 4.3](SPEC-001/refinements/00-refinement-consolidated.md).
- [Corpus contratual do manifesto](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md).
- [Pacote de decisão do portão da política executável](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-18-decision-package.md).
- [`RequestNotification.PayloadHash`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs).
- [`RequestNotification.Validator`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs).
- [`KafkaIngressProcessor`](../src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs).
- [`RequestPayloadHashTests`](../tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs).
- [Notification Hub, design de sistema](notification-hub-system-design.md).
- [Stack Profile](../.araia/stack-profile.yaml).
