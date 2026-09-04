---
language: pt-BR
---

# Tarefa 32: reconciliação de falhas parciais e convergência de órfãos

Registro da construção, das medições e do que ficou sem medida. Executado em
2026-09-03 sobre a árvore limpa em `4268956`, sem commit.

## A forma entregue

### A coluna, anulável e de vocabulário fechado

`attachmentmanagement.attachment` ganhou `reconciliation_liability`, mapeada de
`Attachment.ReconciliationLiability`, anulável e com largura tirada de
`AttachmentLiabilities.MaxLength`. O vocabulário tem duas palavras e cada uma
nomeia um reparo, não um incidente:

| Palavra | O que ela diz | Qual reparo |
|---|---|---|
| `custody-unreclaimed` | a chave derivada guarda bytes que o registro de gerações não explica | listar a chave, remover o que o registro não reivindica, devolver a chave |
| `verdict-open` | um veredito não concluiu e o anexo espera o prazo dele | encerrar a espera pela operação que é dona da máquina de estados |

O fundamento de ser palavra e não indicador booleano é o que a decisão de forma
já dizia, e agora tem consequência mecânica: a rodada decide o reparo lendo a
coluna, sem voltar ao armazenamento, ao registro e ao relógio para descobrir
qual executar. O fundamento de ser uma coluna só é que as duas palavras vivem
em estados disjuntos: custódia só é devida enquanto os bytes nunca chegaram, e
veredito só é devido depois que chegaram. Nenhuma linha pode dever as duas.

O fundamento de a coluna morar no agregado, e não numa linha própria, é que um
anexo que deve custódia é justamente aquele que não tem geração registrada,
então uma linha própria teria de ser inventada para a dívida. E linha inventada
antes da escrita é ela mesma uma escrita fora da transação, que foi a razão de
a linha de intenção ter sido recusada. O preço dessa escolha foi medido e está
na seção sobre a versão de linha, mais abaixo.

### O índice parcial

```text
CREATE INDEX ix_attachment_reconciliation_liability
    ON attachmentmanagement.attachment
 USING btree (created_at)
 WHERE (reconciliation_liability IS NOT NULL)
```

O filtro é o que dá valor ao índice: quase nenhum anexo deve reparo, então a
estrutura guarda a exceção. A chave é o instante de criação e não a palavra,
porque a rodada não busca por palavra: ela lê o que estiver pendente, do mais
antigo para o mais novo, e uma chave sobre a palavra devolveria linhas que
ainda teriam de ser ordenadas. Com essa chave o índice responde a seleção e a
ordem juntas, e o plano executado mostra isso sem nó de ordenação.

O módulo de anexos não tem cadeia de migrações e cria o esquema a partir do
modelo, então a coluna e o índice nascem por `CreateTablesAsync`. Nenhum
arquivo de migração foi escrito, e a materialização definitiva pertence à
tarefa que esmaga a cadeia. Nada foi tocado do lado de notificações, portanto
não houve aviso de modelo pendente.

### Quem escreve e quem limpa

São dois escritores, e a diferença entre eles é deliberada.

O agregado é dono de `verdict-open`. `HoldInconclusive` grava a palavra na
mesma transação que cria a espera; `Release` e `Reject` a apagam na mesma
transação que encerra a espera. Não há segundo commit em caminho nenhum.

O caminho de upload é dono de `custody-unreclaimed` e não pode usar o agregado,
porque escreve exatamente quando a própria transação falhou e não sobrou
agregado para salvar. Ele anota a linha por instrução própria em
`AttachmentLiabilityLedger`, num contexto novo, com prazo próprio de cinco
segundos, e nunca vira a resposta ao chamador. As duas instruções carregam no
predicado o valor que esperam: o registro só escreve sobre coluna vazia, e a
limpeza só escreve sobre a palavra exata que a rodada se propôs a reparar.

A anotação acontece em exatamente dois pontos, e a escolha desses dois é a
medição mais importante desta entrega.

### O ponto em que a anotação é segura, e o em que ela causa dano

A anotação acontece quando o armazenamento aceitou a escrita e não nomeou
geração, e quando a remoção compensatória não foi confirmada, seja porque o
armazenamento recusou, seja porque a chamada lançou. Nos dois casos foi a
escrita **desta** requisição que colocou os bytes, e a escrita condicional
torna isso exclusivo: nenhuma outra requisição pode ter colocado bytes sob essa
chave, portanto nenhuma pode estar prestes a registrar uma geração.

A anotação **não** acontece no conflito de upload, e essa é a reversão de uma
decisão que já estava escrita e implementada. Ela foi desfeita por medição, e o
que a derrubou está descrito na seção de reprovações reais: o agregado carrega
versão de linha, então qualquer escrita confirmada nessa linha entre a leitura
e a gravação de uma requisição em voo faz a gravação dela falhar. Anotar a
partir do perdedor do conflito transforma um upload concorrente que já tinha
armazenado seus bytes numa recusa que ainda por cima os remove.

A consequência é declarada e não maquiada: o órfão que a aplicação nunca
aprendeu, produzido por amplificação de retentativa, **não entra na fila de
reparo sozinho**. A varredura sabe repará-lo, e isso está medido; o que não
existe é um gatilho seguro que o registre a partir da requisição que encontra o
conflito.

A anotação também não acontece quando o armazenamento fica inalcançável. É a
falha comum e não estabelece nada sobre bytes duráveis; anotá-la encheria o
índice com um diário de falhas de transporte.

### A porta de inventário

`IAttachmentObjectInventory` é porta própria e não método novo em
`IAttachmentObjectStore`. As duas fazem promessas opostas: a custódia nomeia a
geração exata em toda chamada após a escrita, para que nenhum chamador alcance
o que a chave aponta agora; o inventário existe justamente para encontrar as
gerações que ninguém consegue nomear. Colocá-lo no contrato de custódia daria
enumeração ao caminho que aceita bytes de produtor.

A implementação em `S3AttachmentObjectStore` enumera por prefixo e filtra as
respostas de volta para a chave exata, porque prefixo que termina onde uma
chave termina ainda casa com toda chave mais longa que comece com ele. Marcador
de exclusão fica de fora: não é byte, e removê-lo descobriria a geração
embaixo em vez de encerrar coisa alguma. Página truncada, laço interrompido e
geração que o provedor nomeou de forma que o módulo não consegue fixar viram a
mesma resposta, indisponível, porque quem chama decide o que remover pelo que
falta na resposta.

A composição deriva o inventário da custódia que a composição já resolveu, em
vez de construir um segundo cliente. Custódia que não enumera não vira
inventário vazio, e sim inventário indisponível.

### A chave derivada, com uma definição só

`AttachmentObjectKeys.For` é a única derivação, e a escrita e a varredura
chamam a mesma função. Duas grafias da mesma regra deixariam a segunda
procurando num lugar onde a primeira nunca escreveu, e a busca voltaria vazia
relatando que nada é devido. Como as duas leem a mesma função, o arranjo do
teste soletra a chave por conta própria e a compara com o que a produção
deriva, para que a mutação da derivação seja visível.

### A rodada

`AttachmentReconciliationScan` lê o lote pela consulta abaixo, executa o reparo
que a palavra nomeia e para quando não entende a palavra.

```csharp
.Where(attachment => attachment.ReconciliationLiability != null
    && (attachment.ReconciliationLiability != AttachmentLiabilities.VerdictOpen
        || (attachment.InconclusiveUntil != null
            && attachment.InconclusiveUntil <= now)))
.OrderBy(attachment => attachment.CreatedAt)
.Take(batchSize)
```

O teste de vazio vem primeiro e sozinho porque é sobre ele que o índice parcial
é construído. O prazo do veredito entra na seleção e não no laço, para que o
lote se encha de trabalho e não de linhas que ainda não venceram.

A recuperação de custódia para antes de remover qualquer coisa quando o
inventário não veio completo, e para de novo quando o armazenamento não
confirma uma remoção. Contar remoção não confirmada como feita é o único erro
que essa rodada não recupera rodando de novo: a linha sairia da fila com os
bytes ainda ocupando a chave.

O encerramento da espera chama `AttachmentValidation.ValidateAsync` em vez de
escrever a transição. O prazo é lido lá, antes de qualquer veredito ser pedido,
e a recusa que ele escreve é a mesma que um produtor obteria perguntando de
novo. Uma rodada com transição própria seria uma segunda máquina de estados
sobre os mesmos anexos, livre para concluir o que a primeira nunca concluiria.

### Onde a rodada é hospedada

`AttachmentMaintenanceWorkerRole`, papel `attachment-maintenance`, descoberto
pelo catálogo do worker. A rodada é registrada onde o módulo é composto; o
agendador, não. Trabalho que remove bytes duráveis não é trabalho que possa
rodar uma vez por réplica de host que serve requisição, e os reparos precisam
acontecer independentemente de alguém estar enviando, porque as linhas
reparadas são exatamente aquelas cujo próximo envio está sendo recusado. Um
teste de composição prova que o papel resolve a rodada inteira e que, sem
armazenamento configurado, ele compõe custódia e inventário que não afirmam
nada em vez de um cliente apontado para o que a cadeia de credenciais achar.

## O passivo que não foi materializado, e por quê

**Evidência de submissão pendente.** Fica fora, e a decisão é de fronteira. O
veredito da testemunha nasce no módulo de despacho, depois que os bytes já
saíram, e não existe reparo: não há chamada a reter nem veredito de provedor a
revisar. Materializá-lo nesta coluna faria um segundo módulo escrever a linha
deste, e a fila de reparo passaria a guardar itens que ninguém pode reparar.

**Conflito de upload.** Fica fora por medição, não por argumento, e a medição
está na seção de reprovações reais. O reparo continua existindo e continua
medido; o que não existe é gatilho seguro para registrá-lo.

**Escrita que o armazenamento não alcançou.** Fica fora de propósito. É a falha
comum e não estabelece nada sobre bytes duráveis.

**Falha de validação com veredito conclusivo.** Não é passivo: a recusa é
estado final, escrita na transação que a decidiu, e não há reparo pendente.

## A matriz de injeção de falha

Cada linha injeta uma falha real contra o armazenamento em que o módulo roda,
com bytes de verdade sob a chave que o registro deriva. O estado durável foi
lido de volta do banco e do armazenamento em cada ponto.

| Ponto de injeção | Estado durável logo após a falha | Depois de uma rodada |
|---|---|---|
| Escrita aceita e geração não nomeada | 1 geração sob a chave, anexo em `awaiting-upload`, passivo `custody-unreclaimed` | 0 gerações sob a chave, passivo vazio, novo envio aceito e anexo em `received` |
| Persistência falha antes do commit e a remoção compensatória é recusada | 1 geração sob a chave, anexo em `awaiting-upload`, passivo `custody-unreclaimed`, uma chamada de remoção contada | 0 gerações, passivo vazio, novo envio aceito |
| Geração que a aplicação nunca aprendeu, sob a chave derivada, com o passivo registrado | 1 geração sob a chave, envio recusado com 409, passivo `custody-unreclaimed` | 0 gerações, passivo vazio, novo envio aceito |
| A mesma geração órfã, sem passivo registrado, com o envio encontrando o conflito | 1 geração sob a chave, 409 ao produtor, **passivo vazio** | inalterado; a rodada não a vê |
| Inventário indisponível durante a rodada | 1 geração sob a chave, passivo `custody-unreclaimed` | inalterado; a rodada seguinte, com inventário disponível, leva a 0 gerações e passivo vazio |
| Remoção recusada pelo armazenamento durante a rodada | 1 geração sob a chave, passivo `custody-unreclaimed` | inalterado; a rodada seguinte, com armazenamento que confirma, leva a 0 gerações e passivo vazio |
| Chave vizinha que apenas começa com a derivada | 1 geração sob a chave derivada e 1 sob a vizinha | 0 sob a derivada e 1 sob a vizinha, preservada |
| Veredito que não conclui | anexo em `validation-inconclusive`, prazo gravado, passivo `verdict-open` | antes do prazo: inalterado, nenhuma espera encerrada. Depois do prazo: anexo em `rejected` com detalhe `inconclusive-window-elapsed` e passivo vazio |
| Envio repetido sobre anexo liberado cujos bytes já estão registrados | 409 ao produtor, anexo em `released`, passivo vazio | não entra na fila; 1 geração sob a chave permanece |
| Palavra fora do vocabulário gravada na coluna | passivo com a palavra desconhecida, 1 geração sob a chave | inalterado, e a rodada contabiliza a linha como sem solução |

Convergência em uma rodada, medida e não afirmada: em cada linha de custódia a
varredura rodou **uma vez** e o par um/zero foi lido no mesmo arranjo, sobre o
mesmo anexo. Uma geração sob a chave antes, nenhuma depois. Passivo gravado
antes, vazio depois. Envio recusado antes, aceito depois. Um zero sozinho não
foi aceito como prova em ponto nenhum.

Os oráculos são por anexo e não por contagem sobre a tabela. A suíte divide o
banco com os demais testes de anexo, então contar pendências mediria os
vizinhos tanto quanto o caso; o que cada teste arranja é um anexo, e todo zero
que ele afirma vem acompanhado do um que o precedeu naquele mesmo anexo.

## O claim não depende da reconciliação

O oráculo é comportamental e tem três metades, porque só as três juntas
sustentam a independência: o host que serve requisição não compõe agendador
nenhum, verificado sobre os serviços hospedados resolvidos; um passivo gravado
em outro anexo continua pendente depois do claim, o que mostra que nenhuma
rodada correu durante ele; e o claim conclui com o conjunto inteiro e a
identidade de conteúdo esperada.

A mutação M13 registra o agendador no módulo e derruba a primeira metade, o que
mostra que a afirmação sobre composição não é decorativa.

## O plano de consulta, executado

Base de quarenta mil anexos, com uma minoria devendo reparo, num contêiner
próprio. O comando foi capturado do próprio pipeline de consulta por
interceptador, e não transcrito, e o `EXPLAIN` foi executado sobre ele com os
mesmos parâmetros ligados.

Comando capturado:

```sql
SELECT a.id, a.reference, a.content_id, a.reconciliation_liability
FROM attachmentmanagement.attachment AS a
WHERE a.reconciliation_liability IS NOT NULL AND (a.reconciliation_liability <> 'verdict-open' OR (a.inconclusive_until IS NOT NULL AND a.inconclusive_until <= @now))
ORDER BY a.created_at
LIMIT @p
```

Plano com o índice:

```text
Limit  (cost=0.14..12.43 rows=1 width=92)
  ->  Index Scan using ix_attachment_reconciliation_liability on attachment a  (cost=0.14..12.43 rows=1 width=92)
        Filter: (((reconciliation_liability)::text <> 'verdict-open'::text) OR ((inconclusive_until IS NOT NULL) AND (inconclusive_until <= '2026-09-03 22:35:24.441352+00'::timestamp with time zone)))
```

Plano com o mesmo comando depois de derrubar o índice:

```text
Limit  (cost=1531.01..1531.02 rows=1 width=92)
  ->  Sort  (cost=1531.01..1531.02 rows=1 width=92)
        Sort Key: created_at
        ->  Seq Scan on attachment a  (cost=0.00..1531.00 rows=1 width=92)
              Filter: ((reconciliation_liability IS NOT NULL) AND (((reconciliation_liability)::text <> 'verdict-open'::text) OR ((inconclusive_until IS NOT NULL) AND (inconclusive_until <= '2026-09-03 22:35:24.441352+00'::timestamp with time zone))))
```

O índice foi recriado a partir da definição que o próprio catálogo devolveu, e
o plano voltou a ser o primeiro. O piso da afirmação está medido: sem o índice
o mesmo comando varre a tabela e ordena por cima, com custo estimado cento e
vinte e três vezes maior.

Sobre "não varre partição": a tabela `attachmentmanagement.attachment` não é
particionada, então essa metade da aceitação não tem sujeito neste módulo. O
que foi medido, e é o que a metade pretendia impedir, é a ausência de `Seq
Scan` sobre a tabela e a ausência de nó de ordenação. Isso está dito em vez de
apresentado como prova de algo que não existe aqui.

## A tabela de mutações

Toda mutação é de um eixo só, no código de produção, aplicada por substituição
textual com âncora única, executada, revertida por substituição inversa e
conferida por comparação de resumo criptográfico de cada arquivo mutado contra
o resumo anterior à campanha. O portão foi o código de saída do build: nenhuma
mutação chegou a teste sem compilar. Nenhuma reversão preservou carimbo de
tempo, porque toda escrita foi feita pelo próprio processo, e cada reversão foi
seguida de build completo.

| Mutação | Eixo | Vermelho observado |
|---|---|---|
| M1 | remove o filtro do índice, deixando-o total | 3 testes: o inventário de índice no modelo e os dois de plano |
| M2 | `HoldInconclusive` não grava o passivo | `A_verdict_that_did_not_conclude_records_the_repair_and_both_ways_out_take_it_back` e `A_wait_is_closed_by_the_first_round_after_its_deadline_and_by_none_before_it` |
| M3 | `Reject` não apaga o passivo | `A_wait_is_closed_by_the_first_round_after_its_deadline_and_by_none_before_it` |
| M4 | o upload não anota o passivo quando a geração não é nomeada | `Bytes_kept_without_a_named_generation_are_reclaimed_and_the_key_accepts_a_retry` |
| M4b | o upload volta a anotar o passivo no conflito | `A_conflict_leaves_the_row_unannotated_because_a_concurrent_upload_may_win` e `A_repeat_over_bytes_the_record_already_accounts_for_owes_nothing`; e o teste de uploads concorrentes, que é onde o dano aparece, em 3 de 4 execuções |
| M5 | compensação não confirmada passa a responder como confirmada | `A_removal_the_store_never_confirmed_is_reclaimed_by_one_round` |
| M6 | inventário indisponível segue como se fosse listagem vazia | `An_inventory_the_store_could_not_complete_leaves_the_repair_outstanding` |
| M7 | remoção não confirmada na rodada é contada como feita | `A_removal_the_store_refused_leaves_the_repair_outstanding` |
| M8 | a seleção perde o prazo do veredito | `A_wait_is_closed_by_the_first_round_after_its_deadline_and_by_none_before_it` |
| M9 | a rodada limpa a palavra que não entende | `A_repair_this_round_does_not_understand_is_left_exactly_as_it_is` |
| M10 | a derivação da chave muda de pasta | 6 testes de reconciliação |
| M11 | a listagem perde a igualdade de chave exata | `A_key_that_only_starts_with_the_derived_one_keeps_its_bytes` |
| M13 | o agendador é registrado no módulo | `A_claim_concludes_with_no_round_composed_and_none_ever_run` |

Treze mutações, treze vermelhos, nenhuma voltando verde. Depois da última
reversão, o `git diff` completo foi lido linha a linha e varrido por literais
de mutação; nenhum resíduo ficou na árvore.

Uma reversão foi **recusada pela ferramenta** e o registro importa: a âncora de
M2 era a linha de retorno da transição, que aparece três vezes no arquivo. A
ferramenta se recusou a reverter em vez de escolher uma ocorrência, a reversão
foi refeita com âncora que inclui o comentário acima dela, e o resumo
criptográfico do arquivo voltou a bater com o anterior à campanha. Sem essa
recusa, a mutação seguinte teria empilhado sobre uma reversão parcial.

Uma guarda foi **removida por não ter falsificador**: a anotação perguntava se
já existia geração registrada antes de escrever. Com o conflito fora da lista
de gatilhos, os dois gatilhos restantes implicam por construção que não existe
geração alguma, e a pergunta passou a ser um ramo que nenhuma mutação de
runtime consegue derrubar. Foi retirada em vez de mantida como decoração; o
predicado do próprio livro-razão, que só escreve sobre coluna vazia, continua
impedindo sobrescrita.

## O que voltou verde, e o que reprovou de verdade

Nenhuma mutação voltou verde. Duas reprovações reais apareceram, e nenhuma
delas era mutação.

**A primeira, e a mais desconfortável.** A suíte de integração completa acusou
`UploadAttachmentEndpointTests.Concurrent_uploads_store_once_and_return_one_explicit_conflict`,
que esperava uma resposta `200` e uma `409` e recebeu duas `409`. Reproduzida
isolada em três execuções de três, e verde em três de três com a anotação do
conflito retirada, o que atribui a causa sem ambiguidade.

O mecanismo: o agregado carrega versão de linha. O perdedor do conflito abria
contexto próprio e gravava o passivo na linha do anexo; essa escrita confirmada
mudava a versão da linha entre a leitura e a gravação do vencedor, e a gravação
do vencedor passava a não casar nenhuma linha. O vencedor então respondia
conflito e **compensava**, isto é, removia os bytes que já tinha armazenado com
sucesso. Um envio legítimo era perdido por causa de um envio concorrente que
apenas tinha chegado atrasado.

A consequência geral vale além deste caso e foi registrada como fronteira da
forma escolhida: **enquanto o passivo mora na linha do agregado, nenhum
caminho concorrente com um upload em voo do mesmo anexo pode anotá-la.** Os
dois gatilhos que sobraram são seguros porque a escrita condicional os torna
exclusivos com qualquer upload que ainda possa vencer.

O reparo foi retirar o conflito da lista de gatilhos, e não afrouxar o teste
concorrente. Um oráculo dedicado foi acrescentado para segurar a forma no
lugar, e a mutação M4b, que devolve o gatilho, derruba os dois.

**A segunda, de arranjo de teste.** O auxiliar `VersionsOfAsync` listava por
prefixo e contava como do anexo a chave vizinha que apenas começa com a
derivada. O defeito era do auxiliar e não do produto, que havia preservado o
vizinho corretamente; o auxiliar passou a comparar a chave por igualdade, que é
a mesma confusão que a guarda de produção existe para evitar. Depois do
conserto, a mutação M11 continua derrubando esse teste.

## O conserto de arranjo da suíte de reconciliação de entrega

As nove reprovações de `DeliveryReconciliationTests` foram reproduzidas antes
de qualquer mudança: nove reprovadas, zero aprovadas, todas com
`23514: no partition of relation "notification" found for row`, e todas
morrendo dentro de `SeedAttemptAsync`, isto é, no arranjo, antes de qualquer
código de produção rodar.

A causa é assimetria de relógio. As migrações provisionam partições a partir do
relógio de parede de quem as executa, o mês corrente e dois adiante, que é o
que uma implantação precisa. A fixture lê um relógio próprio, fixo em
2026-08-25, para alcançar a janela de obsolescência movendo o relógio em vez de
esperar seis horas, e semeia as linhas nesse instante. Os dois concordam apenas
enquanto o instante fixo cai no mês em que a suíte roda, e a partir de
2026-09-01 pararam de concordar.

O conserto é do arranjo: a fixture provisiona, depois das migrações, as
partições mensais das tabelas particionadas de notificações e da trilha de
auditoria, para o mês do próprio relógio e os dois vizinhos. A lista de tabelas
vem da constante de produção `NotificationsPartitionManagerService.PartitionedTables`,
e não de uma cópia, para que uma tabela particionada nova não deixe o arranjo
para trás em silêncio. Nenhuma asserção foi afrouxada e nenhuma linha de
produção foi tocada. O comportamento de produção continua correto: o
provisionador nunca cria mês passado, e não deve criar.

Resultado depois do conserto: catorze aprovadas e zero reprovadas no filtro
`Notifications.Reconciliation`, que cobre as nove de entrega e as cinco de
plano.

## O que os oráculos não provam, declarado

- **O órfão de amplificação não entra sozinho na fila.** A rodada sabe repará-lo
  e isso está medido, mas nenhum gatilho seguro o registra. Bytes que ninguém
  jamais repete, e bytes cuja chave é encontrada por um conflito, ficam órfãos
  até a varredura de abandono, que pertence a outra tarefa. É a maior lacuna
  desta entrega e ela é consequência direta da forma exigida.
- **Nada sobre o provedor real.** Toda a custódia medida aqui é LocalStack. O
  comportamento de listagem por versão, de marcador de exclusão e de remoção
  por geração exata foi medido contra o dublê local e não contra o provedor.
- **A amplificação de retentativa não foi reproduzida.** O órfão foi arranjado
  plantando bytes sob a chave derivada, que é a forma durável do fenômeno, e
  não fazendo o cliente do provedor amplificar uma escrita. O que está medido é
  o reparo do resíduo, não a produção dele.
- **Não existe varredura de tabela inteira por prefixo.** A convergência por
  prefixo é por anexo e disparada pela palavra na linha. A leitura ampla de
  "todo anexo que nunca recebeu bytes" não foi implementada, porque custaria uma
  chamada de listagem por linha por rodada, e porque a política de descarte de
  abandonados pertence a outra tarefa.
- **A anotação do passivo é ela mesma uma escrita fora da transação.** Se ela
  falhar, a linha de log é tudo o que resta e nada mais descobre aqueles bytes.
  É a mesma classe de problema que a linha de intenção teria, com a diferença
  de que aqui ela não taxa o caminho feliz.
- **Duas guardas do livro-razão não têm falsificador.** O predicado que impede
  o registro de sobrescrever um reparo já anotado, e o que impede a limpeza de
  apagar um reparo diferente do que a rodada executou, não têm oráculo de
  runtime nesta entrega. Ambos exigem uma corrida entre dois escritores sobre a
  mesma linha, e ela não foi arranjada.
- **A limpeza por instrução de conjunto atravessa qualquer congelamento.** A
  coluna não é congelada, e não pode ser, porque a própria rodada precisa
  limpá-la; e mesmo se fosse, já está medido nesta fatia que atualização
  baseada em conjunto atravessa a guarda de mapeamento. Nada no banco impede
  hoje que uma instrução crua escreva uma palavra qualquer nessa coluna. O que
  a rodada faz nesse caso está medido: ela não entende e não age.
- **A paginação da listagem não foi exercitada.** Toda chave medida guarda uma
  ou duas gerações, e o laço de páginas nunca deu a segunda volta.
- **Concorrência entre a rodada e um envio sobre o mesmo anexo não foi
  medida.** A rodada não toma a trava de linha do anexo. Pelo que a primeira
  reprovação real ensinou, a limpeza do passivo feita pela rodada também muda a
  versão da linha e pode derrubar a gravação de um upload em voo do mesmo
  anexo. Essa corrida não foi arranjada e não está medida.
- **A contagem da rodada é sobre a tabela inteira.** As asserções de contagem
  usam piso e não igualdade, porque a suíte divide o banco com os vizinhos. As
  afirmações exatas são todas por anexo.
- **O agendador nunca rodou.** Todas as medições chamam a rodada diretamente. O
  serviço de fundo tem seu intervalo e sua habilitação cobertos apenas pelo
  teste de composição do papel, que prova registro e resolução, não execução.

## Execução

Todas as suítes foram executadas sobre a árvore final, depois de um build
completo sem incremental que provou a recompilação.

| Suíte | Resultado |
|---|---|
| `dotnet build MonteBravo.NotificationHub.sln -warnaserror --no-incremental` | 0 aviso, 0 erro |
| `Platform.UnitTests` | 2087 aprovadas, 0 reprovadas |
| `Platform.ArchTests` | 30 aprovadas, 0 reprovadas |
| `Platform.SecurityArchTests` | 14 aprovadas, 0 reprovadas |
| `Platform.IntegrationTests`, completa, com paralelismo de coleção em um | 994 aprovadas, 1 reprovada, 2 puladas, de 997 |

A única reprovação é
`AuditReconstructionTests.Publishing_a_newer_version_does_not_move_the_answer_of_the_older_notification`,
que já era conhecida antes desta tarefa e não toca anexo nenhum: ela morre na
publicação de uma versão de modelo recusada pela verificação de variáveis
sensíveis retidas. As duas puladas são os dois testes de fumaça contra provedor
real, pulados por desenho. As nove reprovações de reconciliação de entrega
deixaram de existir com o conserto de arranjo.

Uma observação sobre estabilidade, medida e não deduzida: executada com o
filtro estreito de anexos e paralelismo padrão, três testes de sentinela
reprovaram todos com a mesma assinatura, `System.TimeoutException`, e voltaram
a aprovar em execução isolada com paralelismo um. Isso é contenção de contêiner
desta máquina e não defeito de produto, e está classificado por assinatura em
vez de por contagem.
