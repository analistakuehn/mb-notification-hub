---
language: pt-BR
---

# Tarefa 37: ensaio de habilitação e reversão

Recibo de execução. Todas as afirmações abaixo foram medidas nesta máquina, em
`master`, a partir da árvore limpa em `561fad2`. Nenhum commit foi feito e
nenhuma linha de produção foi alterada de forma permanente.

## 1. A cláusula vencida, declarada nula

A redação original da aceitação exige verificar que "a versão V1 continua
recusando anexos". **A cláusula é nula.** Ela é anterior à decisão ratificada de
que não existe V2: hoje há uma versão de contrato só, num tópico único e numa
rota única, e essa versão **aceita** anexos. Inventar uma V1 que recusa, para
satisfazer a frase, seria construir um artefato só para o oráculo medir.

No lugar dela foram medidas duas propriedades que protegem o mesmo bem, que é o
produtor não receber promessa de entrega com arquivo que não viajou:

1. **Nenhum corpo que nomeie o manifesto é aceito sem que o manifesto seja
   transportado.** Medido em
   `A_body_that_names_a_manifest_is_accepted_only_where_the_manifest_is_carried`.
2. **O caminho sem anexos permanece idêntico.** Medido em
   `A_request_that_names_no_attachment_hashes_as_the_form_without_the_member`,
   contra vetores dourados.

## 2. A matriz, braço por braço

Os arquivos do ensaio:

- `tests/Platform.IntegrationTests/Notifications/AttachmentMixedVersionReaderTests.cs`,
  com banco próprio, no ponto onde a diferença entre gerações é real: as
  instruções que cada uma envia.
- `tests/Platform.IntegrationTests/Notifications/AcceptedAttachments/AttachmentMixedVersionRolloutTests.cs`,
  sobre o host de verdade, com as duas composições da chave.

| # | Braço | O que mediu | Evidência de que o arranjo produziu a condição | Falsificador | Vermelho medido em |
|---|---|---|---|---|---|
| 1 | Leitor antigo, schema novo, `NULL` no banco | `A_reader_that_never_selects_the_column_advances_a_row_the_schema_left_empty`: o modelo sem a coluna carrega a linha, transiciona para `dispatched` e grava, e nenhuma instrução dele nomeia a coluna | a coluna existe em `information_schema.columns`; `accepted_attachments IS NULL` lido por SQL cru; e o modelo **deste build**, na mesma consulta, **nomeia** a coluna | F1: remover o `Ignore` do modelo dublê | `stale.Reads` |
| 2 | Leitor novo, linha nula do escritor antigo | `A_row_a_writer_that_never_sets_the_column_left_reads_as_no_attachment`: o `INSERT` do modelo sem a coluna não a nomeia, e a leitura devolve ausência, não ilegibilidade; `RefuseUnreadable` não lança e a linha avança | o texto do `INSERT` capturado, mais `IS NULL` por SQL cru | F2: `Read(null)` devolve `Unreadable` | `ShouldBeOfType<Absent>` |
| 3 | Leitor novo, escritor novo | `A_row_written_with_a_document_reads_whole_here_and_reads_as_no_attachment_without_the_column`, primeira metade: o `INSERT` nomeia a coluna e a leitura devolve o conjunto inteiro, membro a membro e na ordem | `IS NULL` devolve falso por SQL cru | F3: `Serialize` inverte a ordem dos itens | `whole.ShouldBe(Set(...))` |
| 4 | Leitor antigo, escritor novo, documento não nulo | duas leituras da **mesma linha no mesmo instante**: este build lê o conjunto inteiro, o modelo sem a coluna lê ausência, e `RefuseUnreadable` não lança sobre ela | a metade do braço 3, na mesma execução | F1: remover o `Ignore` | `blind.AcceptedAttachmentsJson` |
| 4b | A combinação proibida bloqueada | `A_body_that_names_a_manifest_is_accepted_only_where_the_manifest_is_carried`: com a chave desligada, a referência que a requisição nomeou **não aparece em documento nenhum da tabela**; com a chave ligada, aparece em exatamente um | a contagem 1 do braço ligado, na mesma varredura | FB1: neutralizar o portão de capacidade no claim | contagem 0 vira 1 |

O braço 4 é o **dano**, não a proteção: ele mostra que um leitor que nunca
seleciona a coluna não distingue uma notificação aceita sobre dois arquivos de
uma aceita sobre nenhum, e portanto a levaria ao provedor sem anexo e a
liquidaria como entregue. O braço 4b é a **proteção**, e é a implicação que a
decisão de forma mandou medir em vez de afirmar: com a capacidade desligada
nenhum escritor produz documento não nulo, logo a combinação quatro não pode
surgir. A outra metade, implantar leitores antes de escritores, é ordem de
operações e vive no procedimento, não em código.

A varredura do braço 4b é sobre a tabela inteira e procura a referência dentro
do texto do documento, com `position` e não com padrão, porque uma referência
carrega sublinhado e sublinhado em padrão casa qualquer caractere. O braço
ligado está na mesma medição de propósito: sem ele, o zero seria a resposta de
uma varredura que nunca encontra nada.

## 3. O limite exato da simulação de leitor e escritor antigos

Nenhum binário anterior foi executado. **O que a simulação é**: um `DbContext`
derivado que ignora a propriedade do snapshot, com chave de cache de modelo por
tipo de contexto, apontado para o mesmo banco migrado pelo modelo vigente. Isso
faz o `SELECT` dele omitir a coluna, o `INSERT` dele omitir a coluna, e a
entidade que ele materializa carregar nulo ali.

**O que a simulação cobre**, e foi medido:

- que uma consulta que não nomeia a coluna carrega a linha e a transiciona;
- que uma inserção que não nomeia a coluna deixa `NULL` no banco;
- que o leitor vigente, sobre uma linha assim, responde ausência e não recusa;
- que o leitor sem a coluna, sobre uma linha **com** documento, responde
  ausência, que é a resposta que deixa a notificação seguir.

**O que a simulação não cobre**, sem eufemismo:

- ela compartilha deste build o validador, a forma canônica, o claim, o tipo de
  entidade, o mapeamento das demais colunas e a máquina de estados. Nada aqui
  diz o que código que não existe mais fazia;
- ela não exercita a serialização de mensagem, o contrato HTTP nem o schema do
  barramento de uma versão anterior;
- ela não diz nada sobre duas réplicas rodando ao mesmo tempo. Cada host lê a
  própria configuração, e a ordenação entre eles não foi medida;
- o "escritor antigo" grava uma notificação que já nasce em `accepted`; ele não
  atravessa o ingresso, a admissão, o hash nem a auditoria.

Uma afirmação de compatibilidade que se apoiasse nesse dublê sem dizer isso
valeria menos que nenhuma.

## 4. O caminho sem anexos: medição de divergência

A comparação é entre o digest que o **host** grava na linha de idempotência e o
SHA-256 dos **bytes canônicos escritos como texto no teste**, sem membro de
manifesto. Comparar contra a mesma função que o produz concordaria com qualquer
mudança dela, inclusive a que acrescenta membro vazio e transforma toda
retentativa de todo produtor existente em conflito.

Medido nos dois hosts, o que aceita anexos e o que não aceita, sobre o mesmo
banco e o mesmo arranjo: **zero divergências**. Na mesma medição, a coluna do
snapshot fica nula nas duas linhas, isto é, a identidade do caminho não é só o
digest dele.

**Idade do corpus**, declarada:

| Vetor | Onde | Anterior ou posterior |
|---|---|---|
| digest do corpo mínimo, `ae72ea09…` | `RequestPayloadHashTests.The_minimal_request_body_has_a_stable_digest` | **anterior**, congelado em `60c6530` (2026-09-02 09:58), antes de `ba8c10a`, que admitiu o manifesto no contrato publicado |
| digest do corpo com todos os membros opcionais, `135fb999…` | `RequestPayloadHashTests.A_request_body_with_every_optional_member_has_a_stable_digest` | **anterior**, mesmo commit |
| ausente, `null` e lista vazia são a mesma requisição | `AttachmentManifestPayloadHashTests` | posterior |
| bytes canônicos sem o membro, comparados contra o host | `AttachmentMixedVersionRolloutTests` | **posterior**, nasceu nesta tarefa |

Os dois vetores anteriores foram executados sob o falsificador FB3 e ficaram
**vermelhos**, o que os qualifica como âncoras vivas e não como literais que
ninguém confere. Sob FB3 caíram cinco casos de unidade: os dois anteriores mais
os três do teoria de ausência, vazio e nulo.

## 5. Replay legítimo

`A_retry_of_an_accepted_set_is_answered_again_by_a_host_that_takes_no_new_attachments`:
uma requisição com conjunto é aceita no host que aceita anexos e **repetida com
o mesmo corpo e a mesma chave no host que não aceita**. A resposta é `200` com o
mesmo identificador, que é a resposta de repetição e não a de aceite, e não é
recusa. Na mesma medição: o documento na linha é idêntico byte a byte antes e
depois, a retenção do anexo continua sendo uma só, e existe uma notificação só
para aquele destinatário.

Esse é o acidente que uma reversão convida. O produtor está repetindo uma
requisição com a qual o hub já concordou, e a chave foi desligada depois dessa
concordância. Recusar essa retentativa diria ao produtor que uma entrega
prometida não vai acontecer, e nenhuma política de repetição se recupera dessa
resposta.

Falsificador FB2: desabilitar o ramo de replay no ingresso. Vermelho em
`again.StatusCode`. O falsificador do portão no claim (M3 da tarefa anterior)
**não** derruba este caso, e isso é informação: o replay é resolvido pela
admissão, antes do claim, portanto o portão de capacidade não é alcançado por
uma retentativa.

## 6. Tabela de mutações

Toda mutação passou pelo portão do código de saída do build antes de qualquer
execução, e foi revertida por cópia do original com conferência de `sha256sum`
mais `git status --porcelain src/` vazio.

| ID | Eixo mutado | Onde | Braço que ficou vermelho | Asserção que falhou |
|---|---|---|---|---|
| F1 | modelo dublê passa a conhecer a coluna | arranjo do teste | 1, 2 e 4 | `stale.Reads`; `stale.Writes[0]`; `blind.AcceptedAttachmentsJson` |
| F2 | `Read(null)` devolve ilegível | `AcceptedAttachmentManifest` | 2 e 4 | `ShouldBeOfType<Absent>` |
| F3 | `Serialize` inverte a ordem dos itens | `AcceptedAttachmentManifest` | 3 | `whole.ShouldBe(Set(...))` |
| FB1 | portão de capacidade neutralizado no claim | `TransactionalAttachmentClaim` | 4b | contagem de documentos que nomeiam a referência recusada |
| FB2 | ramo de replay desabilitado no ingresso | `RequestNotification.Handler` | replay | `again.StatusCode` |
| FB3 | membro `attachments` escrito sempre, mesmo vazio | `RequestNotification.PayloadHash` | caminho sem anexos | lista de divergências, com os dois hosts nomeados; e cinco casos de unidade |

Nenhuma mutação voltou verde. F1 é mutação de arranjo e está marcada como tal:
ela prova que o dublê é dublê, não que a produção resiste a algo.

## 7. Execução e evidência

```
dotnet build MonteBravo.NotificationHub.sln -warnaserror --no-incremental
  -> Build succeeded. 0 Warning(s) 0 Error(s)
     (carimbos das DLLs conferidos depois da compilação)

dotnet test Platform.UnitTests         -> Passed: 2143, Failed: 0, Skipped: 0
dotnet test Platform.ArchTests         -> Passed:   31, Failed: 0, Skipped: 0
dotnet test Platform.SecurityArchTests -> Passed:   14, Failed: 0, Skipped: 0

NOTIFICATIONHUB_REQUIRE_DOCKER=1, integração dirigida:
  ~AttachmentMixedVersionReaderTests            -> Passed:  3, Failed: 0, Skipped: 0
  ~AttachmentMixedVersionRolloutTests           -> Passed:  3, Failed: 0, Skipped: 0
  ~AcceptedAttachments (coleção inteira)        -> Passed: 38, Failed: 0, Skipped: 0  (1 m 7 s)
  vizinhos de persistência e de replay          -> Passed: 40, Failed: 0, Skipped: 0  (2 m 17 s)
     AcceptedAttachmentSnapshotTests, AcceptedAttachmentIngressSnapshotTests,
     AttachmentManifestIdempotencyTests, RequestNotificationIdempotencyTests,
     AcceptanceClaimTransactionTests
  resto da coleção de planos de consulta        -> Passed: 15, Failed: 0, Skipped: 0  (2 m 22 s)
```

`Skipped` foi zero em todas as rodadas de integração, o que separa este
resultado de uma sonda de Docker que expira e vira skip silencioso. `docker
info` respondeu em 6 s antes da primeira rodada. Os `.trx` ficaram em
`tests/Platform.IntegrationTests/TestResults/t37-*.trx` e
`tests/Platform.UnitTests/TestResults/t37-unit3.trx`.

**Uma reprovação intermitente, registrada porque aconteceu.** A primeira das
cinco rodadas da suíte de unidade devolveu `Failed: 1, Passed: 2142`, e o nome
do caso **não foi capturado**, porque a saída estava truncada e só sobrou o
quadro de pilha da invocação por reflexão. As quatro rodadas seguintes
devolveram 2143 aprovados, e a família Scriban rodou isolada com 99 aprovados.
Esta tarefa não escreveu nem alterou nenhum arquivo do projeto de unidade, o que
limita a explicação a intermitência; ainda assim, a reprovação não está
atribuída e é isso que fica escrito.

## 8. O que os oráculos não provam

- **Não provam compatibilidade com um binário anterior.** O leitor e o escritor
  antigos são o mesmo build com uma propriedade ignorada. A seção 3 lista o que
  isso cobre e o que não cobre.
- **Não provam nada sobre duas réplicas simultâneas.** Cada host lê a própria
  configuração; o ensaio compõe dois hosts sobre um banco, mas nunca os faz
  disputar a mesma linha ao mesmo tempo.
- **Não provam a ordem de implantação.** Que leitores devem ir antes de
  escritores é procedimento. Nenhum portão neste repositório impede a ordem
  inversa, e o braço 4 mostra exatamente o que ela custaria.
- **Não provam o caminho sem anexos no barramento.** A medição de divergência é
  sobre o ingresso REST. O tópico único não foi exercido nesta tarefa, e o
  digest que ele grava não foi comparado com vetor dourado nenhum aqui.
- **Não provam que a forma canônica inteira está congelada.** Os vetores
  anteriores cobrem o corpo mínimo e o corpo com todos os membros opcionais. Um
  corpo com `metadata` aninhado profundo, com chave em plano suplementar ou com
  `scheduledAt` em fuso não canônico não tem vetor anterior.
- **Não provam que a varredura do braço 4b enxerga toda escrita possível.** Ela
  procura a referência no texto do documento da tabela de notificações. Um
  caminho que gravasse o conjunto em outro lugar, ou sob outra grafia da
  referência, passaria por ela.
- **Não provam que o host de produção sobe com a chave desligada.** Provam que o
  binder e o tipo respondem fechado sem seção e que os dois hosts do ensaio
  responderam ao valor declarado. Qualquer camada de configuração acima disso
  não foi inspecionada.
- **Não provam reversão de algo já entregue.** O replay medido é anterior ao
  despacho. Nada aqui diz o que acontece com notificação já submetida ao
  provedor quando a chave é desligada.
- **Não provam a suíte de integração inteira.** Ela continua inutilizável nesta
  máquina por contenção do daemon. O fechamento do raio foi feito por vizinhança
  declarada: a coleção que a classe nova passa a integrar, a coleção serializada
  de planos de consulta, e as suítes de persistência e de replay do manifesto.
- **F1 não é falsificação de produção.** Ela derruba três braços mexendo no
  arranjo, e o que ela prova é que o dublê não é um contexto que consulta nada.

## 9. Defeito encontrado

Nenhum. O ensaio não revelou defeito de produção. A única correção foi de
arranjo: a expectativa de que a retentativa devolvesse `202`. O endpoint devolve
`200` para requisição já decidida e `202` para requisição sendo decidida agora,
e o teste passou a nomear a resposta certa. Nenhuma linha de produção foi
alterada por esta tarefa.

## 10. Pendências que continuam precisando de decisão humana

As três da tarefa anterior continuam abertas e nenhuma foi resolvida aqui:
versionamento no bucket de produção, política de ciclo de vida para gerações não
correntes, e a habilitação operacional em si, que é ato de operação. O ensaio
que a aceitação exigia como pré-condição está executado.
