---
language: pt-BR
---

# Tarefa 34: evidência operacional reconstruível

Registro da construção, das medições e do que ficou sem medida. Executado em
2026-09-03 sobre `303517f`, sem commit.

## A forma entregue

A evidência de notificação já existia e já era servida pela superfície de
divulgação da conformidade. O que esta tarefa acrescentou foi a dimensão de
anexo, em quatro peças, cada uma no módulo que tem autoridade sobre o que ela
afirma.

| Peça | Onde | O que afirma |
|---|---|---|
| `AttachmentEvidence` e `IAttachmentEvidence` | `Modules/AttachmentManagement/Integration/V1/IAttachmentEvidence.cs` | o que o módulo dono ainda prova sobre o conteúdo de um manipulador aceito: algoritmo, resumo criptográfico, comprimento medido, tipo detectado, instante da captura, estado, detalhe da validação e a concessão que nomeou aquela geração |
| `RecordedAttachmentEvidence` | `Modules/AttachmentManagement/Infrastructure/Reads/RecordedAttachmentEvidence.cs` | resolve o manipulador contra a linha de geração e a linha do anexo, sem rastreamento e sem escrita |
| `AcceptedAttachmentEvidence`, mais dois membros em `NotificationEvidence` | `Modules/Notifications/Integration/V1/NotificationEvidence.cs` | a composição congelada pelo aceite, membro a membro, com a metade registrada aninhada e anulável |
| `AcceptedAttachmentEvidenceProjection` | `Modules/Notifications/Infrastructure/Reads/AcceptedAttachmentEvidenceProjection.cs` | a função pura que transforma os três desfechos da leitura do documento em duas afirmações distintas |

O leitor `NotificationEvidenceReader` passou a projetar a coluna do manifesto,
a ler o documento uma única vez, a perguntar ao módulo dono apenas pelos
manipuladores que aquele documento congelou, e a devolver a projeção. O bloco
`attachments` entra na resposta pela `GetNotificationEvidence.Mapping.cs`, dentro
de `state`.

### Por que a autoridade continua sendo a notificação

A composição, a ordem, o nome, o tipo e o comprimento saem do documento da
linha, que é o que o produtor foi informado ter sido aceito. O módulo de anexos
responde uma pergunta só, sobre os bytes que cada manipulador congelado nomeia.
Nada na leitura alcança o estado atual do módulo dono para descobrir *quais*
anexos a notificação tinha, porque isso seria uma segunda resposta, livre para
discordar da primeira.

A junção é o manipulador opaco e não existe segunda chave. A referência do
anexo viaja nas duas metades de propósito: a de cima é o que o aceite congelou,
a de baixo é a resposta do próprio módulo dono para o manipulador, e uma
discordância entre elas é exatamente o que um auditor precisa ver em vez de
herdar.

## O que viaja e o que deliberadamente não viaja

| Viaja | Por quê |
|---|---|
| resumo criptográfico, algoritmo e comprimento medido | é a única afirmação que diz *quais* bytes saíram; sem ela um anexo aceito é um nome de arquivo e uma promessa |
| nome do arquivo, tipo declarado e comprimento liberado | é o que o auditor precisa saber, e esta é a única superfície onde eles devem aparecer |
| estado, detalhe da validação e tipo detectado | é a dimensão de validação: qual verificação recusou, e o que os bytes iniciais foram reconhecidos como |
| instante da concessão, da retirada e o motivo declarado | é o ciclo de vida da liberação daquela geração |
| aplicação do anexo | fecha a relação entre aplicação, anexo e notificação, e uma divergência fica visível |

| Não viaja | Por quê |
|---|---|
| loja, chave e geração do provedor | é capacidade de alcançar bytes, não prova de quais bytes eram; uma superfície de evidência que os carregasse viraria uma segunda porta para o conteúdo |
| identificador de conteúdo do agregado | a chave do objeto é derivada dele, então publicá-lo é publicar a chave em outra grafia |
| bytes do conteúdo, em qualquer forma | a evidência prova, não entrega; o conteúdo renderizado já sai por uma leitura dedicada, uma tentativa por vez, e o anexo não tem equivalente autorizado |
| prazo de validade da liberação | é comparação lida no instante do envio contra valor configurado; publicar o prazo armazenado ou o computado convidaria o auditor a concluir "venceu" ou "não venceu" a partir de um número que não é o que o envio comparou. A leitura operacional do ciclo de vida continua sendo dona dessa pergunta |
| documento cru do manifesto quando ele não lê | a recusa nomeia a forma do defeito, do vocabulário fechado do módulo dono, e nunca cita o documento; referência, nome e tipo são dado do produtor |

O contrato publicado do despacho, `AcceptedAttachment`, continua sem o resumo
criptográfico. A assimetria é a decisão: aquele valor viaja com todo despacho,
toda mensagem e toda linha de log que o renderize, enquanto este é alcançado por
uma leitura autorizada e auditada. Pela mesma razão `AttachmentEvidence`
sobrescreve a renderização de texto: um registro imprime todos os membros
públicos que tem, e uma interpolação num log publicaria o resumo na forma
copiável que a fatia recusou.

## Ausência afirmada, e como ela ficou distinguível

Três desfechos entram e três saem, sem colapso:

| Documento da linha | Resposta | O que ela afirma |
|---|---|---|
| ausente | `accepted` presente e vazio | a notificação não nomeou anexo nenhum |
| lido inteiro | `accepted` com os membros, na ordem congelada | esta é a composição aceita |
| ilegível | `accepted` ausente, `unreadable` com a palavra da recusa | ninguém consegue nomear o que ela carregava |

O par vazio contra ausente é medido num teste só, sobre duas notificações do
mesmo arranjo, porque separados cada um passaria sobre uma resposta cuja forma
não afirma nada.

## Por que o oráculo de ausência não é vazio

Afirmar que algo não aparece é a asserção mais fácil de escrever de forma vazia.
O oráculo aqui é uma varredura de texto sobre o corpo cru servido pela rota, e
ele foi construído para não poder passar num vácuo:

1. **Todo valor procurado é real e é desta notificação.** Loja, chave, geração
   do provedor e identificador de conteúdo são lidos das linhas duráveis destes
   anexos exatos, dentro do próprio teste. Os bytes são lidos da custódia, pela
   loja do módulo, antes de qualquer remoção. Não há literal inventado na lista.
2. **Cada zero anda ao lado de um um, na mesma varredura e sobre o mesmo texto.**
   A mesma função afirma presença de referência, manipulador, nome do arquivo e
   resumo criptográfico de cada membro, e ausência das coordenadas e dos bytes.
   Um corpo vazio, um corpo de outra notificação ou uma leitura que devolvesse
   erro reprovam pela metade de presença antes de a metade de ausência dizer
   qualquer coisa.
3. **A ausência foi falsificada em execução.** A mutação M2 põe a chave do
   objeto num membro que nenhum outro oráculo afirma, e a varredura reprova
   nomeando o achado: "a evidência não carrega a chave do objeto". Nenhuma outra
   asserção do caso reprovou junto, então o vermelho é atribuível à varredura.
4. **A superfície do tipo é fechada por inventário, além da varredura de texto.**
   Um teste de unidade nomeia os treze membros publicados de `AttachmentEvidence`
   e nove membros do bloco serializado, em ambas as direções, porque a
   coordenada que ninguém pensaria em procurar chegaria exatamente assim, como
   um membro novo que ninguém escreveu teste para.

## Tabela de mutações

Oito mutações de runtime, um eixo cada, no código de produção, todas com o
portão pelo código de saída do build antes de qualquer execução de teste, todas
vermelhas, nenhuma verde. Reversão conferida por `sha256sum` contra os resumos
tomados antes da série, e recompilação forçada porque a restauração por cópia
carimba o arquivo com o instante da reversão.

| # | Arquivo e eixo | Vermelho observado |
|---|---|---|
| M1 | projeção: ilegível devolve lista vazia em vez de lista nenhuma | unitário `A_document_nobody_can_read_answers_no_set_at_all_and_names_the_defect` ("should be null but was []") e integração `A_snapshot_nobody_can_read...`, com a mensagem "um documento ilegível não pode chegar ao auditor como conjunto nenhum de anexos" |
| M2 | leitor do módulo dono: o tipo detectado passa a carregar a chave do objeto | integração, os dois casos que varrem o corpo, com "a evidência não carrega a chave do objeto" |
| M3 | projeção: junção pela referência em vez do manipulador | unitário `The_set_carries_the_frozen_composition...` e integração, casos 1 e 3, pela metade registrada ausente |
| M4 | contrato: remoção da renderização redigida | unitário `The_rendering_carries_the_handle...`, que imprimiu o resumo criptográfico inteiro na renderização padrão |
| M5 | leitor do módulo dono: anexos descartados saem da junção | apenas o caso 3, a reconstrução depois do recolhimento; casos 1 e 2 verdes |
| M6 | projeção: ausência devolve lista nenhuma em vez de lista vazia | unitário e integração, caso 2, pela `accepted` que sumiu de uma notificação sem anexos |
| M7 | leitor do módulo dono: resumo criptográfico constante | integração, casos 1 e 3, na comparação contra a linha de geração |
| M8 | mapeamento da conformidade: a recusa não é copiada para a resposta | unitário `A_set_nobody_can_read...` e integração, caso 2 |

M5 é a mutação que atribui o oráculo da tarefa anterior: só o caso que roda a
varredura de abandono reprova, e os outros dois seguem verdes, então aquele caso
mede o que diz medir e não a junção em geral.

## A reconstrução depois do recolhimento dos bytes

O caso `The_attempt_stays_reconstructible_after_the_sweep_took_the_bytes` roda a
varredura de abandono real, sobre a loja real, e reconstrói depois:

1. notificação aceita sobre um anexo liberado com bytes na custódia, despachada
   e enviada contra um provedor que aceita, devolvendo identidade de mensagem;
2. a custódia é lida antes de qualquer remoção, e a abertura responde `Opened`,
   porque um zero depois só mede remoção se houve um um antes;
3. a liberação é retirada pela operação do próprio módulo, datada vinte anos
   atrás, e todas as dependências vivas são encerradas pelo registro do módulo;
4. a rodada é composta como o papel de manutenção compõe, com janelas de dez
   anos nos quatro estados, de modo que nenhuma linha vizinha deste ambiente
   possa estar vencida. `Discarded` igual a 1 é o arame de contaminação: um
   segundo descarte significaria que a rodada alcançou linha de vizinho;
5. depois da rodada, o estado do anexo é `discarded` e a abertura da custódia
   responde `Missing`;
6. a evidência é lida de novo: a tentativa continua `sent` com a identidade do
   provedor, o membro continua nomeando arquivo e comprimento, e a metade
   registrada continua respondendo com o mesmo resumo criptográfico, o mesmo
   comprimento medido, o estado `discarded` e o instante e o motivo da retirada.

O registro sobrevive aos bytes porque a varredura não expurga linha nenhuma, e é
o estado que diz que o conteúdo saiu. A mesma varredura de ausência roda sobre
este corpo, com as coordenadas e os bytes capturados antes da remoção.

## Medições executadas

Todas na árvore final, depois da última reversão e com recompilação forçada.

| Comando | Resultado |
|---|---|
| `dotnet build MonteBravo.NotificationHub.sln -warnaserror` | 0 avisos, 0 erros |
| `dotnet test tests/Platform.UnitTests` | 2136 aprovados, 0 reprovados, 0 pulados |
| `dotnet test tests/Platform.ArchTests` | 30 aprovados |
| `dotnet test tests/Platform.SecurityArchTests` | 14 aprovados |
| integração, filtro `AcceptedAttachmentEvidenceTests` | 3 aprovados, 0 pulados |
| integração, filtro `AcceptedAttachment` | 36 aprovados, 0 pulados |
| integração, filtro `NotificationHub.IntegrationTests.Compliance` | 31 aprovados, 0 pulados |
| integração, filtro `AttachmentManagementModuleTests` e `SentinelSurfaceScanTests` | 13 aprovados, 0 pulados |
| integração, filtro `OpenApiDocument` e `AttachmentContractSurfaceTests` | 9 aprovados, 0 pulados |

Toda execução de integração rodou com `NOTIFICATIONHUB_REQUIRE_DOCKER=1`, e o
número de pulados foi conferido em cada uma, porque sonda de Docker que expira
vira aprovação silenciosa. A suíte de unidade eram 2123 antes; os 13 novos são
os oráculos desta tarefa.

Uma execução dirigida reprovou os três casos em 818 ms com a mesma assinatura,
`System.InvalidOperationException : Could not find resource 'PostgreSqlContainer'`,
que é a fixture voltando sem subir contêiner porque a sonda de Docker do processo
de teste não respondeu. A repetição imediata, com a sonda respondendo em um
segundo, deu os três aprovados em 36 segundos de relógio. A reprovação é do
ambiente e não do código, e ela é o desenho funcionando: com a variável exigida,
uma sonda que expira vira vermelho em vez de virar suíte verde sem execução.

O inventário do contrato publicado do módulo de anexos,
`tests/Platform.UnitTests/AttachmentManagement/AttachmentClaimContractTests.cs`,
foi atualizado no mesmo passo: ele compara tipos e membros publicados nas duas
direções, e publicar um contrato sem escrever a linha é exatamente o que ele
existe para impedir. As duas reprovações que ele produziu antes da atualização
estão registradas aqui como a prova de que ele mede a superfície nova.

A varredura de sentinela não foi tocada e não recebeu a evidência no seu
inventário de superfícies. Ela cobre transporte e observabilidade e trata nome
de arquivo como sentinela de vazamento; a evidência é o único lugar onde nome,
tipo e comprimento devem aparecer, então ela ganhou oráculo próprio em vez de
uma isenção naquela varredura.

## O que os oráculos não provam

- **Indisponibilidade do módulo dono.** O leitor não captura falha da leitura de
  anexos, de propósito, para que uma loja inalcançável nunca seja servida como
  "este módulo não registra mais estes membros". Isso é construção, não medida:
  nenhum teste exercita um `IAttachmentEvidence` que falha, e o comportamento
  observável de uma falha real, um erro na rota, não foi verificado.
- **Concessão superseded.** A regra de responder pela concessão que nomeou a
  geração aceita, e nunca pela última concessão do anexo, está escrita na
  consulta e coberta pela projeção unitária, mas nenhum caso arranja uma
  revalidação e lê a evidência depois. A suíte de pré-voo tem o arranjo; esta
  tarefa não o usou.
- **A varredura de ausência é textual.** Ela pega coordenada que viaje literal.
  Uma que viajasse transformada, codificada, partida em pedaços ou reduzida a
  um resumo, passaria. O mesmo vale para os bytes: são procurados em hexadecimal,
  em base64 e em prefixos dos dois, e uma cópia reenquadrada escaparia.
- **O resumo criptográfico é tão imutável quanto a linha que o guarda.** O
  módulo dono documenta que não revisa linha alguma e que o mapeamento recusa
  duas das quatro formas de revisão, deixando de fora a atualização de instância
  destacada e a atualização por conjunto. A evidência herda esse limite: a prova
  de quais bytes saíram é a projeção de uma linha que uma instrução por conjunto
  ainda reescreve, e nada aqui assina essa projeção. O bloco `state` da resposta
  não é coberto pela cadeia de resumos da trilha, e a dimensão de anexo entrou
  ali junto com o restante.
- **Custo e volume.** A leitura acrescenta duas consultas por notificação, e
  nada foi medido sobre uma notificação com conjunto grande nem sobre a rota sob
  carga.
- **Ambiente.** A rodada de abandono foi medida contra o dublê local de
  armazenamento, no ambiente desta coleção, e nunca contra o provedor real. O
  arame de contaminação afirma um descarte exato, então a medida vale para esta
  execução; ela não diz nada sobre a varredura rodando ao lado de outro tráfego.
- **Autorização.** Os casos leem a rota como auditor autorizado. Que a rota
  recuse quem não é auditor é regra da suíte de conformidade, que continua verde,
  e não foi remedida aqui.
