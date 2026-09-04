---
language: pt-BR
---

# Tarefa 33: descarte seguro de abandonados

Registro da construção, das medições e do que ficou sem medida. Executado em
2026-09-03 sobre `bf772e9`, sem commit.

## A regra de abandono que foi implementada

Abandono é estado somado a prazo, e o prazo conta do último evento que ainda
podia mudar aquele estado. A regra mora no agregado, em
`Attachment.DiscardableFrom`, que devolve o instante em que o conteúdo deixa de
ser guardado, ou nada quando nada ali está abandonado.

| Estado | De onde vem a data | Coluna | Prazo aplicado |
|---|---|---|---|
| `awaiting-upload` | o registro, que é o último evento que podia mudar o estado, porque o próximo é o upload e ele tira o anexo daqui | `created_at` | `UnstartedUpload` |
| `received` | a chegada dos bytes | `received_at` | `UnvalidatedContent` |
| `rejected` | a recusa | `ended_at`, coluna nova | `RefusedContent` |
| `revoked` | a revogação | `ended_at`, coluna nova | `WithdrawnRelease` |

Contar de `created_at` nos três últimos é o defeito que a regra existe para
evitar, e ele está medido dos dois lados: nos testes de unidade o registro é um
ano mais velho que o ato que termina o anexo, e na integração o arranjo inteiro
vive em 2020 com o ato em 2021, de modo que uma rodada em `Registered + 30 dias`
não pode remover nada e a rodada no prazo do estado remove.

### A coluna nova, e por que ela precisou existir

`ended_at` foi acrescentada ao agregado e é escrita por `Reject` e por `Revoke`,
que são os dois atos que nada reabre. A justificativa é que nenhum dos dois
instantes existia em lugar algum:

- a recusa escreve estado e detalhe e mais nada, e não tem linha própria;
- a revogação escreve uma linha que data a **concessão** que foi retirada, e
  não o anexo, além de exigir junção por candidato numa varredura;
- `received_at` não serve como substituto, porque a validação é pedida pelo
  produtor quando ele quiser: o intervalo entre o recebimento e a recusa é
  ilimitado, e um prazo contado do recebimento encerraria um anexo recusado há
  um minuto.

O instante da revogação é lido uma vez e escrito duas, no agregado e na linha
da revogação, na mesma transação, para que o registro do ato e o relógio da
retenção não possam divergir.

### Os três estados que ficaram de fora, e o motivo de cada um

- **`released`**: é o que todo o fluxo existe para produzir, e o vencimento
  dele é calculado no momento da comparação, a partir de uma validade lida da
  configuração e de um instante de vigência. Descartar por esse vencimento
  destruiria bytes que a próxima mudança daquele valor tornaria utilizáveis de
  novo. Está medido: uma liberação com anos de idade sobrevive a uma rodada que
  no mesmo instante descarta um anexo recusado.
- **`validation-inconclusive`**: já tem dono e prazo próprios, e a rodada de
  reconciliação a encerra numa recusa, que é onde este relógio começa. Incluí-la
  seria uma segunda máquina de estados sobre os mesmos anexos.
- **`discarded`**: não tem mais conteúdo a tirar.

A lista publicada `AttachmentStates.Discardable` é o mesmo conjunto, e ela não é
uma cópia: um teste percorre os sete estados, pergunta ao agregado quais têm
prazo, e compara com a lista.

## O estado `discarded`, que a tarefa não pediu e o desenho exigiu

A varredura escreve `discarded` no anexo depois que a loja confirma que nada
restou sob a chave. Ele não é enfeite, e as três razões são independentes:

1. **Sem marca durável, a varredura nunca avança.** O lote é limitado e a ordem
   é do mais antigo para o mais novo; um anexo que continua qualificado depois
   de perder os bytes volta a ocupar o lote em toda rodada, e o que chegou
   depois nunca é alcançado.
2. **A remoção dos bytes reabre a porta do upload.** O que impedia repetir o
   envio sobre um anexo já resolvido era a escrita condicional encontrando a
   chave ocupada. Tirar os bytes libera a chave, e sem uma recusa por estado o
   upload passaria e devolveria ao fluxo um registro que já havia terminado.
   Medido: com a guarda, `409` com `attachment-discarded` e nada gravado; sem
   ela, o teste reprova.
3. **O produto já previa o estado.** O escopo da primeira produção lista os
   estados observáveis incluindo o descarte.

Custo acolhido e declarado: a superfície pública ganhou um estado e um código de
erro, `attachment-discarded`, respondido pelo upload e pela validação. O
`SettledAnswerAsync` da validação tinha um braço escrito para o dia em que
existisse um quarto estado resolvido, e este é ele.

## Quais prazos são derivação e quais são escolha minha

| Valor | Natureza | De onde vem |
|---|---|---|
| Piso de 30 horas para qualquer prazo | **derivado** | uma tentativa sem desfecho fica presa até a entrega resolvê-la, e a entrega espera o corte de 6 horas e depois roda uma vez por dia: `StaleAfter + Interval` da seção `Modules:Notifications:DeliveryReconciliation` |
| `UnstartedUpload` = 7 dias | **escolha minha** | anexo registrado cujo envio nunca começou; descartar cedo mata a chance de o produtor voltar e enviar |
| `UnvalidatedContent` = 7 dias | **escolha minha** | pedir a validação é passo do produtor e pode vir a qualquer momento |
| `RefusedContent` = 3 dias | **escolha minha** | conteúdo recusado é o mais provável de ser hostil, e o que sobrevive à remoção é o digest e o detalhe, não os bytes |
| `WithdrawnRelease` = 7 dias | **escolha minha** | conteúdo que foi legítimo e teve a aprovação retirada |
| `Interval` = 1 hora e `BatchSize` = 100 | **escolha minha** | limitam a rodada e não decidem o que é abandono |

Nenhum dos quatro prazos tem padrão que feche. Zero é a marca de valor que
ninguém definiu, a guarda de partida recusa a seção ausente e recusa qualquer
prazo abaixo do piso, e o agregado recusa de novo: prazo zero devolve "não
descartável" em vez de "descartável agora".

O piso é um número neste módulo e não uma leitura da configuração alheia,
porque contexto não lê seção de contexto vizinho. O que o mantém honesto é um
teste que lê o `appsettings.json` que o host publica, confirma `1.00:00:00` e
`06:00:00` na seção da entrega e exige que o piso cubra a soma. Se aquele ciclo
crescer, o teste reprova.

## A varredura, e o que ela toma emprestado

Por candidato, numa transação só e com a linha travada desde antes da decisão
até depois das remoções:

1. trava a linha do anexo;
2. relê o estado e pergunta de novo ao agregado se ainda está abandonado;
3. chama `AttachmentDisposal.DiscardHeldAsync`, que lê as dependências e recusa
   enquanto houver uma viva, e remove por versão exata cada geração registrada;
4. lista a chave derivada pelo inventário e remove o que sobrou, que é
   exatamente o que o registro não explica;
5. só então escreve `discarded` e limpa o passivo de custódia, e comita.

A recusa por dependência continua sendo do descarte. `AttachmentDisposal` ganhou
uma segunda entrada para quem já detém a linha e a transação, e nenhum argumento
dela desliga a leitura das dependências. A razão de a decisão e a remoção
precisarem da mesma trava está medida: um candidato escolhido numa leitura
passada pode ter recebido o upload no intervalo, e uma varredura que confiasse na
própria seleção removeria os bytes desse upload.

## Matriz de preservação

Quatro estados por quatro razões, com o relógio um ano além do prazo do estado,
mais o par positivo em cada caso. Dezesseis casos, todos executados.

| Estado | `claim-confirmed` | `attempt-sending` | `attempt-unknown` | razão não listada |
|---|---|---|---|---|
| `awaiting-upload` | preservado | preservado | preservado | preservado |
| `received` | preservado | preservado | preservado | preservado |
| `rejected` | preservado | preservado | preservado | preservado |
| `revoked` | preservado | preservado | preservado | preservado |

Preservado quer dizer: a rodada devolveu `Preserved`, a chave continua com
exatamente uma geração durável, o estado é o mesmo de antes, e a linha do
registro continua lá. Em todos os dezesseis, encerrar a dependência e rodar de
novo remove o conteúdo e escreve `discarded`, que é o um que acompanha cada zero:
sem ele a tabela acima também valeria para uma varredura que nunca remove nada.

A razão não listada está na matriz de propósito: o que torna uma dependência
viva é a ausência de encerramento, nunca a razão, então nenhum filtro futuro por
razão passa despercebido.

## Tabela de mutações

Uma mutação de runtime por eixo, no código de produção, com vermelho observado.
Toda reversão foi conferida por digest do arquivo, idêntica ao original, com
recompilação forçada e código de saída zero depois de cada uma.

| # | Alvo | Mutação | Vermelho observado |
|---|---|---|---|
| 1 | `AttachmentDisposal` | `if (live > 0)` vira `if (live < 0)` | 16 de 16 casos da matriz de preservação |
| 2 | `Attachment.DiscardableFrom` | `received` passa a contar de `CreatedAt` | 2 casos de unidade, entre eles o que separa registro de recebimento |
| 3 | `Attachment.Deadline` | devolve o início sem somar a janela | 7 casos de unidade |
| 4 | `AttachmentAbandonmentScan` | a remoção do que o registro não explica passa a percorrer nada | o caso da geração que ninguém registrou |
| 5 | `AttachmentAbandonmentScan` | a guarda do inventário é invertida | o caso do inventário incompleto |
| 6 | `Attachment.Discard` | não escreve o estado | 8 casos de unidade |
| 7 | `UploadAttachment.Handler` | a guarda do estado descartado é removida | o upload sobre conteúdo descartado |
| 8 | `Attachment.Reject` | não escreve `ended_at` | 7 casos de unidade |
| 9 | `AttachmentAbandonmentScan` | a releitura do estado sob a trava é removida | o candidato que deixou de estar abandonado |
| 10 | `Attachment.Deadline` | aceita janela zero | 4 casos de unidade |
| 11 | `AttachmentAbandonmentScan` | passa a expurgar as linhas de geração | 3 casos, e o de `revoked` morre com `23503` |

Nenhuma mutação voltou verde. A décima primeira é a que sustenta a política de
expurgo e está detalhada abaixo.

## Política de expurgo das linhas de geração: nenhuma linha é expurgada

A proposta herdada era expurgar a linha de geração de anexo que nunca teve
dependência alguma. Ela foi medida e recusada, com três razões, a primeira
delas executada:

1. **O banco recusa exatamente essa população.** `attachment_release` tem chave
   estrangeira para `attachment_object_generation` com comportamento restritivo.
   Um anexo liberado e depois revogado nunca teve dependência alguma, portanto
   cai na regra proposta, e a exclusão da linha reprova com
   `23503: update or delete on table "attachment_object_generation" violates
   foreign key constraint ... on table "attachment_release"`. A mutação 11
   reproduz isso dentro da varredura. Estreitar a regra para "linha que nenhuma
   liberação nomeia" deixaria de fora justamente os anexos que chegaram a ser
   liberados, e sobraria o conjunto mais barato de todos.
2. **A linha é o único registro do que esteve armazenado.** Ela guarda
   algoritmo, digest, comprimento e tipo detectado, medidos sobre aqueles bytes.
   Depois que o conteúdo sai, ela é a única resposta a "o que este anexo
   guardou". A própria regra herdada de nunca expurgar linha cuja remoção não
   foi confirmada é o mesmo princípio, um passo antes.
3. **A tabela está marcada para receber gatilho de rejeição de mutação**,
   recusando alteração e exclusão inteiras, sem cláusula condicional, na tarefa
   das migrações. Um expurgo entregue aqui seria uma instrução que o banco passa
   a recusar no dia em que aquela migração entrar.

O que substitui o expurgo é o estado: `discarded` diz que o conteúdo saiu, tira
o anexo da seleção e do índice parcial, e deixa o registro intacto. O oráculo
que segura isso conta as linhas exatamente, e não percorre o que sobrou: lido
como percurso, ele passaria sobre um registro esvaziado.

## A expectativa da decisão D4 se confirmou

A varredura enumera por prefixo com o inventário, e não apenas as gerações
registradas, e é por isso que o órfão de amplificação finalmente é recolhido.
Medido num anexo em `awaiting-upload` cuja chave guarda bytes que nada no
registro nomeia: zero gerações registradas, uma geração durável antes, nenhuma
depois, e o anexo passa a `discarded`. O segundo braço mede o mesmo com o
passivo `custody-unreclaimed` escrito na linha, e a varredura o retira, porque
foi ela quem executou aquele reparo.

A razão pela qual isso é seguro aqui e não era na requisição que encontra o
conflito também se confirmou pelo desenho: a linha é tomada antes da decisão e
solta depois das remoções, então não existe upload concorrente do mesmo anexo
para quebrar. O caso do candidato que deixou de estar abandonado mede a metade
que importa dessa afirmação.

## Efeito sobre a versão de linha, medido

A varredura escreve na linha do agregado, e a Tarefa 32 mostrou que isso pode
quebrar um upload concorrente. Três coisas contêm o risco aqui, e as duas
primeiras estão executadas:

- a escrita acontece sob a trava da linha e depois da releitura do estado, de
  modo que um upload já confirmado é visto e o anexo é deixado em paz;
- um upload que chegue depois do descarte é recusado pelo estado, com `409`, e
  não grava nada;
- em produção, a única escrita de dependência é o claim, e o claim exige o
  estado liberado, que não é candidato. Nenhum candidato pode ganhar uma
  dependência nova enquanto a varredura trabalha.

A suíte inteira do módulo de anexos e a dos vizinhos que revogam continuam
verdes, incluindo os casos de upload concorrente e de trava herdados da
Tarefa 32.

## Plano de consulta

O comando é capturado do próprio pipeline por interceptador e o `EXPLAIN` corre
sobre ele. Com o índice parcial `ix_attachment_abandonment`, varredura de índice
sem nó de ordenação e sem varredura sequencial da tabela; derrubando o índice, a
mesma instrução passa a varrer a tabela, e recriá-lo pela definição que o
catálogo devolveu restaura o plano. O filtro é lido do catálogo e contém
`awaiting-upload` e `revoked` e não contém `discarded` nem `released`, que é o
que faz o anexo sair da estrutura para sempre quando o conteúdo é removido.

Achado de mapeamento, encontrado por um teste que já existia: dois índices sobre
a mesma propriedade são um índice só para o construtor de modelo. A primeira
versão desta declaração reescreveu em silêncio o filtro e o nome do índice da
reconciliação, e o único sinal foi o teste de mapeamento. O índice novo passou a
ser declarado com nome próprio.

## Execução

| Portão | Resultado |
|---|---|
| `dotnet build MonteBravo.NotificationHub.sln -warnaserror` | 0 erros, 0 avisos |
| `Platform.UnitTests` | 2123 aprovados, 0 reprovados, 0 pulados (base de 2087, mais 36 novos) |
| `Platform.ArchTests` | 30 aprovados |
| `Platform.SecurityArchTests` | 14 aprovados |
| Integração dirigida a `IntegrationTests.AttachmentManagement` | 193 aprovados, 0 pulados |
| Integração dirigida a composição de worker, anexos aceitos e ingresso de contrato | 48 aprovados, 0 pulados |

Todas as rodadas de integração com `NOTIFICATIONHUB_REQUIRE_DOCKER=1` e com
`Skipped` conferido em zero. A suíte cheia não foi executada, pela contenção de
Docker declarada na tarefa.

## O que os oráculos não provam

- **Nada sobre o esquema de produção.** O módulo não tem cadeia de migração: a
  coluna `ended_at` e o índice `ix_attachment_abandonment` só existem onde o
  esquema nasce do modelo. A migração pertence à tarefa das migrações, e é lá que o
  gatilho de rejeição de mutação e esta coluna se encontram.
- **Nada sobre corrida real entre upload e varredura.** A guarda que relê o
  estado sob a trava é medida por candidato montado à mão, com o upload já
  confirmado, e não por duas execuções concorrentes de verdade. O que está
  medido é que a varredura recusa a leitura vencida, não o entrelaçamento.
- **O braço `Discard` da varredura não tem falsificador de runtime.** Ele fica,
  com o comentário dizendo isso, porque o dia em que o método reler o anexo é o
  dia em que um estado que deixou de estar abandonado poderia ser escrito como
  descartado.
- **A resposta de uma revogação repetida muda para anexo descartado**, de
  `already-revoked` para `not-released`, porque o estado deixou de ser
  `revoked`. Isso não tem teste e está declarado, não medido.
- **O agregado continua aceitando `MarkReceived` a partir de estados
  terminais.** Quem recusa o upload sobre conteúdo descartado é o handler, no
  mesmo lugar onde já morava a recusa de anexo recebido. O buraco equivalente
  para `rejected` e `revoked` é anterior a esta tarefa e continua fechado apenas
  pela chave ocupada.
- **Conteúdo liberado e nunca reivindicado não é recolhido por nada.** Ele fica
  para sempre, e sair disso exige uma decisão de produto sobre expiração de
  liberação, além de um fato durável de vencimento que hoje não existe.
- **O instante do descarte não é gravado.** O estado diz que aconteceu, o log
  nomeia a referência e as contagens, e nenhuma coluna data o ato. A leitura de
  ciclo de vida também não ganhou membro para ele.
- **Nada foi medido sobre custo sob carga real**, nem sobre a rodada com backlog
  grande, nem contra provedor real: as medições correram sobre LocalStack e um
  Postgres de contêiner.
- **Uma exceção inesperada aborta a rodada inteira**, e não apenas o candidato,
  porque não existe captura por candidato. É o mesmo comportamento da rodada de
  reconciliação, e não foi medido com falha injetada fora dos caminhos já
  cobertos.
- **O gatilho planejado para a tabela de gerações não foi executado por mim.**
  A terceira razão da política de expurgo se apoia na decisão registrada da
  tarefa das migrações, não em medição própria. As duas primeiras razões, essas
  sim, estão medidas.
- **A documentação do produtor não foi atualizada** com o estado novo nem com o
  código de erro novo.
