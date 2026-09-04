---
language: pt-BR
---

# Tarefa 38: guia do produtor e motivos de recusa

Recibo de execução. Tudo abaixo foi medido nesta máquina, em `master`, sobre a
árvore em `cc69885`. Nenhum commit foi feito.

## 1. A correção de código: duas recusas que eram uma palavra só

O defeito era de vocabulário, não de comportamento. Com a capacidade de anexos
desligada, o registro de um anexo recusava com palavra própria
(`attachment-capability-not-enabled`), mas o vínculo do conjunto no aceite de
uma notificação recusava com `AttachmentClaimStatus.NotClaimable`, que chega ao
produtor como `attachments-not-claimable`. As duas condições pedem ações
opostas: uma pede esperar a habilitação e reenviar o mesmo corpo, a outra pede
não repetir o mesmo conjunto. Produtor e trilha recebiam a segunda quando o
fato era a primeira.

A correção, em sete arquivos de produção:

- `AttachmentClaimStatus` ganhou `CapabilityNotEnabled`, acrescentado ao fim e
  nunca no valor zero. Zero continua sendo `NotClaimable`, que é a resposta que
  não deixa conjunto passar, e o teste que fixa isso
  (`The_answer_nobody_produced_stops_the_set`) segue verde sem alteração.
- `TransactionalAttachmentClaim` devolve o membro novo no portão da capacidade.
  As outras três recusas do mesmo método continuam em `NotClaimable`.
- `RequestNotification.Handler.ResolveAttachmentRefusalAsync` mapeia o membro
  novo para uma recusa com palavra própria, com trilha gravada e barramento em
  silêncio, exatamente como o caminho vizinho. As duas recusas passaram a
  compartilhar um auxiliar (`RecordUnannouncedRejectionAsync`) para que a
  ausência de evento de rejeição seja uma decisão escrita uma vez.
- `IngestionProblems` publica `AttachmentCapabilityNotEnabledType`, com
  `422` no ingresso, e o endpoint e o processador Kafka o encaminham.

**A palavra escolhida é `attachment-capability-not-enabled`, a mesma que a rota
de registro já responde.** Não é acidente e não é constante compartilhada: o
teste de arquitetura de módulos proíbe o módulo de notificações de alcançar o
domínio do módulo de anexos, então são duas constantes com o mesmo literal, e o
comentário de cada uma diz por quê. O precedente é do próprio guia, que já
registra `payload-invalid` e `event-type-unsupported` escritos igual em dois
vocabulários separados. O ganho é para quem integra: uma palavra para procurar,
seja qual for a superfície que respondeu.

**Por que não colide com a função de adequação.** A palavra é código de
transporte e não entra em `NotificationRejectionReasons`. Conferido lendo
`ProducerGuideCatalogTests` antes de escrever: ela compara `All` com a tabela
cujo cabeçalho é `| Motivo | O que significa | O que o produtor faz |`, e o
código novo vive na tabela de condições fora do catálogo. Um teste de unidade
novo fixa essa propriedade
(`The_two_attachment_conditions_of_the_ingestion_stay_out_of_the_catalog_and_apart`),
que antes não existia para nenhuma das duas palavras.

## 2. A mutação que prova a correção

Um eixo por vez, vermelho observado, reversão conferida por `git diff
--numstat`, recompilação provada.

**Mutação 1, o portão do claim.** `TransactionalAttachmentClaim` voltou a
devolver `NotClaimable` no portão da capacidade. Build limpo, e três testes
reprovaram nomeando o dano:

```text
AcceptedAttachmentCapabilityTests.A_request_that_names_no_attachment_is_accepted_while_the_capability_is_off [FAIL]
  refused should contain (case insensitive comparison)
AttachmentCapabilityTests.A_set_nobody_holds_yet_is_not_claimed_while_the_capability_is_off [FAIL]
  refused.Status should be
AttachmentCapabilityTests.Switching_the_capability_off_leaves_every_durable_row_where_it_was [FAIL]
Failed! - Failed: 3, Passed: 7, Skipped: 0, Total: 10
```

Revertida, `git diff --numstat` devolveu `1  1` no arquivo, `grep` confirmou o
membro novo na linha 164, e a reconstrução foi feita com `--no-incremental`
antes de medir de novo. Verde: 31 aprovados, 0 pulados, incluindo os testes
vizinhos que continuam esperando `attachments-not-claimable` para conjunto
genuinamente não vinculável. É essa vizinhança que atribui o vermelho acima ao
portão da capacidade e não ao vínculo inteiro.

**Mutação 2, o caso do Kafka.** O `case` novo do processador passou a
diagnosticar com `AttachmentsNotClaimableType`. O teste de integração novo
reprovou com a mensagem que nomeia exatamente a troca:

```text
KafkaIngressAttachmentManifestTests.A_manifest_published_to_a_deployment_that_takes_no_attachments_is_told_so [FAIL]
  refused should be "attachment-capability-not-enabled" but was "attachments-not-claimable"
```

Revertida, `git diff --numstat` devolveu `12  0` (o bloco novo, sem remoções),
`grep -c AttachmentsNotClaimableType` devolveu `1` (só o caso original), e a
reconstrução foi `--no-incremental`.

O caso do Kafka precisava de teste próprio: sem ele, o desfecho novo cairia no
`default:` do processador, que lança `InvalidOperationException`, e nada além
de uma execução real diria isso. O compilador não pega essa falta.

## 3. O portão do guia, com vermelho medido nos dois sentidos

A afirmação de que a tabela é inventário verificado **nos dois sentidos** não
era verdadeira quando comecei. Medi antes de confiar nela.

**Sentido 1, motivo do catálogo sem linha.** Removi a linha de `expired` da
tabela e rodei:

```text
ProducerGuideCatalogTests.Every_rejection_reason_has_a_row_in_the_producer_guide [FAIL]
  missing should be empty but had 1 item and was ["expired"]
```

**Sentido 2, linha sem motivo correspondente.** Restaurei o arquivo, inseri uma
linha cuja primeira célula é `motivo-que-nao-existe-no-codigo`, palavra que o
código nunca emite, e rodei de novo: **passou**. A função de adequação era unidirecional. Ela compara
`NotificationRejectionReasons.All` contra a tabela e nunca a tabela contra o
catálogo, então uma linha documentando palavra que o código nunca emite passava
em silêncio, e um motivo renomeado deixaria a linha antiga como documentação de
vocabulário que não existe mais.

Fechei a lacuna com um `[Fact]` novo no mesmo arquivo,
`Every_row_of_the_producer_guide_names_a_reason_the_catalog_carries`, com a
mesma guarda de tabela vazia da regra vizinha. Com ele, a mesma linha inventada
reprova:

```text
ProducerGuideCatalogTests.Every_row_of_the_producer_guide_names_a_reason_the_catalog_carries [FAIL]
  uncatalogued should be empty but had 1 item and was ["motivo-que-nao-existe-no-codigo"]
```

Antes de adicioná-lo, conferi que a regra nova passa hoje: catálogo e tabela
têm 23 entradas cada, sem diferença em nenhuma direção e sem linha repetida.
`Platform.ArchTests` passou de 30 para 31 testes por causa dessa regra.

Duas notas sobre o alcance dela: só lê a tabela do catálogo, localizada pelo
cabeçalho, então códigos de transporte documentados na tabela da seção 6.1 não
a violam; e ela não sabe nada sobre o texto das linhas, só sobre a palavra na
primeira célula.

## 4. O que estava obsoleto, e o que passou a dizer

Cada ponto foi conferido contra o código antes de ser reescrito. A data de
última verificação foi refeita, não copiada: passou a 4 de setembro de 2026, no
frontmatter e no cabeçalho.

| Ponto | O que dizia | O que passou a dizer |
|---|---|---|
| Linha `Estado` do cabeçalho | Capacidade publicada no contrato V1, ainda não liberada para integração | A capacidade existe no código, é implantada desligada e depende de duas chaves independentes de ambiente; sem confirmação do time do hub, não envie `attachments` |
| Parágrafo de escopo | O fluxo de anexos permanece fora do onboarding, e a seção de esquema registra o membro para compatibilidade | Continua fora do onboarding e passa a estar descrito, porque quem lê o guia é quem recebe a resposta; aponta para as seções 2.4 e 6.1 |
| Linha do membro `attachments` | Regras de forma e a proibição em negrito | As mesmas regras, mais a origem das referências e a resposta `422 attachment-capability-not-enabled` numa implantação que não aceita anexos novos; a proibição em negrito continua |
| Passo 11 da ordem de checagens | Um único `422 attachments-not-claimable` | Os dois `422` possíveis, com remissão à seção 2.4 para saber qual chega em cada caso |
| Bloco da seção 2.4 | Uma frase sobre a configuração versionada não admitir tipo de conteúdo | As duas chaves separadas, o que cada uma governa, e a afirmação explícita de que ligar a primeira não habilita anexos |
| Nota do teste de habilitação | Não inclua anexos enquanto não houver liberação | O mesmo, mais o que acontece se incluir: `422 attachment-capability-not-enabled` e nenhuma notificação, então o passo não testa nada além da recusa |
| Nota do ambiente local | O compose não completa pré-requisitos de armazenamento e validação | O compose não configura o armazenamento de objetos do módulo, e sem ele o envio de conteúdo responde `503 attachment-store-unavailable` |
| Nota de limite de taxa | Teto de 1.000 por minuto, embora a capacidade não esteja liberada | Teto de 1.000 por minuto por principal autenticado ou por endereço, válido mesmo com a capacidade desligada, porque é freio de borda |
| Tabela da seção 6.1 | Quatro condições fora do catálogo | Cinco, com a linha nova e um parágrafo dizendo por que as ações das duas linhas de anexos são opostas |
| Parágrafo de genericidade | `attachments-not-claimable` é genérico de propósito | O mesmo, preservado, mais por que a palavra nova não o enfraquece |
| Seção 7.1 | Dead letter cita `attachments-not-claimable` como exceção de transporte | Cita as duas, na lista de motivos e na tabela de redação |
| Tabela de evidências | Linha "Anexos ainda não liberados" apontando para `appsettings.json:70` e para `DispatchRequest.cs:37` | Três linhas novas, com âncoras reconferidas: as duas chaves, a recusa nas duas superfícies e o mapa de recusas das rotas de anexos |

Duas afirmações antigas foram conferidas e **mantidas** porque continuam
verdadeiras: a de que um token só com `appid` não serve para as APIs de anexos
(`AttachmentPrincipal.Resolve` resolve `oid`, `sub` ou `NameIdentifier`), e a de
que o compose não provisiona Kafka.

Duas afirmações minhas foram corrigidas antes de fechar, porque eu as escrevi
imprecisas: dizer que a recusa por capacidade "nada leu" sobre o conjunto é
falso. O vínculo trava e resolve a identidade do conjunto **antes** do portão da
capacidade. O texto passou a dizer que nada foi apontado no conjunto, e o
comentário do membro novo do enum e o do desfecho foram corrigidos junto.

## 5. O que a seção 2.4 ganhou, além da revisão

- **A ordem interna do vínculo**, porque ela decide qual palavra chega quando
  as duas condições valem: identidade do conjunto, depois o que a chave de
  idempotência já vincula, e só então a capacidade. Duas consequências
  documentadas: referência inexistente ou estrangeira responde
  `attachments-not-claimable` mesmo com a capacidade desligada; e repetição de
  chave sobre conjunto já vinculado continua respondendo o aceite original
  depois do desligamento.
- **A tabela das recusas das rotas `/v1/attachments`**, com 18 códigos e os
  status que cada um recebe, lida de `ApiResults`. Ela existe por D5: são
  recusas que um produtor observa e não pertencem ao catálogo publicado.
- **Três observações que mudam a leitura de uma resposta dessas**: corpo fora
  das regras de forma no registro responde `400` do framework sem `type` do
  módulo; nas rotas que nomeiam referência, acesso negado e anexo inexistente
  respondem igual (`404`), de propósito, pela mesma razão da genericidade; e
  `attachment-content-refused` é genérico de propósito.

## 6. O que continua sem documentação

Cinco itens, nomeados como estado conhecido e não como ausência:

1. **`attachments-unverified`.** Quando nada pôde ser estabelecido sobre o
   conjunto aceito, o envio é adiado e a mensagem volta para a fila em vez de
   falhar. Não é recusa e não aparece em `attempts[].errorCode`, então um
   produtor não a observa diretamente; o que ele observa é atraso. Ficou de
   fora porque o guia documenta recusas observáveis, e isto não é uma. Se
   virar sintoma recorrente, o lugar é a seção 5.3.
2. **`attachment-reference-invalid`.** O código existe no mapa de respostas do
   módulo e **nenhum caminho de produtor o devolve**: todas as rotas que
   nomeiam uma referência mapeiam referência malformada para
   `attachment-not-found`. Não entrou na tabela porque documentá-lo seria
   documentar resposta que ninguém recebe, que é exatamente o defeito que a
   regra nova do sentido inverso passou a pegar na tabela do catálogo.
3. **`attachment-producer-grant-invalid`.** É recusa da criação de uma
   concessão, ato de implantação, não de produtor. Fora do guia por isso.
4. **A rota `/v1/attachment-operations`.** Existe, é gatilhada pelo papel
   `Notifications.Attachments.Operations` e responde a leitura do ciclo de
   vida. Não é papel de produtor e o guia diz apenas que a distinção fina das
   recusas sai por uma consulta operacional, sem descrever a rota.
5. **A consulta de notificação não devolve o manifesto aceito.** Já estava
   escrito na seção 5.2 e continua verdadeiro; registro aqui porque é a
   pergunta que um produtor de anexos faz primeiro e a resposta é "guarde o
   payload original".

## 7. Execução

| Comando | Resultado |
|---|---|
| `dotnet build MonteBravo.NotificationHub.sln -warnaserror --no-incremental` | 0 erros, 0 avisos |
| `dotnet test tests/Platform.UnitTests` | 2143 aprovados, 0 pulados |
| `dotnet test tests/Platform.ArchTests` | 31 aprovados, 0 pulados |
| `dotnet test tests/Platform.SecurityArchTests` | 14 aprovados, 0 pulados |
| Integração dirigida, `NOTIFICATIONHUB_REQUIRE_DOCKER=1` | 45 aprovados, 0 pulados |
| `python ./.claude/araia/scripts/check-writing-rules.py --mode markdown --strict docs/guia-integracao-produtor.md` | PASS |

A contagem de unidade subiu de 2142 para 2143 e a de arquitetura de 30 para 31,
pelas duas regras acrescentadas. O filtro da integração dirigida cobriu
`AttachmentCapabilityTests`, `AcceptedAttachmentCapabilityTests`,
`AttachmentClaimTests`, `AcceptanceClaimTransactionTests` e
`KafkaIngressAttachmentManifestTests`. Nenhuma execução teve teste pulado, e as
durações por teste no `.trx` confirmam que os casos com Docker rodaram de fato
(o caso novo do Kafka levou 1,7 s; o do ingresso REST fechado, 4,0 s).

**Uma reprovação intermitente observada e não consertada.**
`ScribanSandboxTests.Output_above_the_ceiling_fails_instead_of_being_truncated`
reprovou uma vez na suíte cheia de unidade. A classe isolada passou (11 de 11) e
a suíte cheia repetida passou (2143 de 2143). É a família Scriban já conhecida
por intermitência; nada nesta tarefa toca renderização.

`docker info` respondeu em 0,55 s antes de cada rodada de integração, então
nenhuma reprovação desta sessão é contenção do daemon.

## 8. Arquivos alterados

Produção: `IAttachmentClaim.cs`, `TransactionalAttachmentClaim.cs`,
`IngestionProblems.cs`, `RequestNotification.Response.cs`,
`RequestNotification.Handler.cs`, `RequestNotification.Endpoint.cs`,
`KafkaIngressProcessor.cs`.

Testes: `AttachmentClaimContractTests.cs`, `NotificationRejectionReasonsTests.cs`,
`ProducerGuideCatalogTests.cs`, `AttachmentCapabilityTests.cs`,
`AcceptedAttachmentCapabilityTests.cs`, `KafkaIngressAttachmentManifestTests.cs`.

Documento: `docs/guia-integracao-produtor.md`.

Uma alteração ficou fora do conjunto literal da tarefa e é declarada aqui:
`ProducerGuideCatalogTests` ganhou uma regra. A tarefa dependia da propriedade
de o inventário ser verificado nos dois sentidos, a medição mostrou que não era,
e deixar o portão meio aberto depois de descobrir isso valia menos do que
fechá-lo.
