---
language: pt-BR
---

# Tarefa 36: habilitação progressiva e rollback lógico

Recibo de execução. Todas as afirmações abaixo foram medidas nesta máquina, em
`master`, a partir da árvore em `31a90fd`. Nenhum commit foi feito.

## 1. A forma do controle, e por que não é o kill switch

O controle é uma seção de configuração própria do módulo de anexos,
`Modules:AttachmentManagement:Capability`, com um único membro booleano
`AcceptsNewAttachments`. O membro não tem inicializador: quem responde por uma
implantação que nunca nomeia a seção é o valor padrão do tipo, e esse valor é o
fechado. O tipo, o portão que o lê e a composição ficam em
`src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Capability/`.

Não é o kill switch, e a razão é o padrão de ausência. O kill switch é parada de
emergência com escopos de produtor, aplicação e canal, e ausência de linha
significa **permitido**. Aqui ausência significa **desabilitado**, porque o
controle é implantado desligado. Na mesma tabela, a mesma ausência teria
consequências opostas conforme o significado, e um operador que apagasse uma
linha habilitaria a funcionalidade sem querer. As duas palavras também se leem
diferente num incidente: "bloqueado" é emergência decidida agora, "não
habilitado" é estado de implantação que ninguém decidiu hoje.

A seção não tem guarda de partida, e a ausência da guarda é a decisão. As seções
vizinhas (`Capacity`, `Retention`) recusam valor não declarado porque zero seria
decisão de produto tomada por omissão; nesta, a omissão **é** a decisão, e é a
segura. Em ambos os casos a ausência é o valor seguro, e seguro é o oposto em
cada uma.

`src/Platform.Api/appsettings.json` passa a declarar a seção com `false`. O
`appsettings.json` do worker não a declara, e continua desabilitado pelo padrão
do tipo.

## 2. Os dois pontos de bloqueio

| Ponto | Arquivo | Resposta com a capacidade desligada |
|---|---|---|
| Registro de anexo novo | `RegisterAttachment.Handler.HandleAsync` | 409 com `attachment-capability-not-enabled`, nenhuma linha gravada, evento 2500 no log |
| Claim de conjunto novo | `TransactionalAttachmentClaim.ClaimAsync` | `AttachmentClaimStatus.NotClaimable`, nenhuma dependência gravada, evento 2501 |

O portão do registro fica antes de qualquer julgamento de metadados, para que um
produtor nunca ouça que o arquivo está errado quando a verdade é que nada seria
aceito.

O portão do claim fica **depois** do ramo que responde a um claim já realizado e
**antes** da primeira linha que a chamada gravaria. Conjunto já retido é aceite
concluído; recusá-lo transformaria toda retentativa de notificação aceita em
rejeição no dia em que a capacidade fosse desligada. Conjunto que ninguém retém
ainda é aceite novo, e é ele que a porta fecha, tenham os anexos sido liberados
antes ou não.

Nenhum motivo novo entrou em `NotificationRejectionReasons`, e o
`docs/guia-integracao-produtor.md` não foi tocado. O claim recusado continua
saindo pelo código exclusivo de transporte já existente,
`attachments-not-claimable` (422), que grava trilha e não vira evento no
barramento. O `attachment-capability-not-enabled` é da mesma família: exclusivo
de transporte, na faixa de erro de negócio do módulo (`BusinessRule` para 409),
com linha em `ApiResults` e sem eco no barramento.

Consequência declarada sem eufemismo: no ingresso, o produtor e a trilha **não
distinguem** "capacidade não habilitada" de "anexo não reivindicável". As duas
recusas saem com a mesma palavra. A distinção existe no log do claim (evento
2501) e na superfície do próprio módulo (409). Promover a distinção ao ingresso
exigiria membro novo no enum publicado e código novo em `IngestionProblems`,
que é superfície de outro módulo.

## 3. Guarda de "nenhum a mais"

`AttachmentCapabilityCallSiteTests` lê o assembly compilado e exige que apenas
dois tipos recebam o portão ou a seção que ele liga, nomeados e não contados:
`RegisterAttachment+Handler` e `TransactionalAttachmentClaim`. Um terceiro
leitor em leitura, tentativa, reparo, varredura ou investigação transformaria o
desligamento em congelamento, e é isso que a regra existe para impedir.

## 4. Os cinco caminhos que o portão não alcança, com o par medido

Cada linha tem o par exigido: funciona com a chave desligada, e a mutação que faz
o portão alcançá-lo derruba o teste.

| Caminho | Oráculo com a chave desligada | Mutação que derruba |
|---|---|---|
| Despacho de tentativa já aceita | `An_attempt_of_an_accepted_notification_still_carries_the_whole_set_while_the_capability_is_off`: o provedor recebe os dois membros e a tentativa fica `Sent` | M5: portão dentro de `AcceptedSetEnvelopeCheck.Measure`. Vermelho em `provider.Requests.TryDequeue` |
| Fallback | `The_fallback_still_decides_over_the_frozen_set_while_the_capability_is_off`: lê o documento congelado, encerra com `attachments-not-carried-by-channel`, e a vizinha sem conjunto caminha para push | M6: portão no início de `FallbackRequestHandler.ProcessAsync`. Vermelho em `RunFallbackAsync` |
| Rodada de reconciliação | `The_reconciliation_round_still_settles_an_outstanding_repair_while_the_capability_is_off`: `Examined >= 1`, passivo zerado, anexo vira `rejected` | M7: retorno vazio em `AttachmentReconciliationScan.RunAsync`. Vermelho em `round.Examined` |
| Varredura de abandono | `The_abandonment_sweep_still_discards_at_the_deadline_while_the_capability_is_off`: `Discarded >= 1`, estado `discarded`, geração removida do armazenamento | M8: retorno vazio em `AttachmentAbandonmentScan.RunAsync`. Vermelho em `swept.Discarded` |
| Leitura de evidência | `The_authorized_readings_still_answer_for_what_exists_while_the_capability_is_off`: `/v1/attachment-operations/{ref}` devolve 200, e `IAttachmentEvidence` devolve referência, estado e comprimento | M9a: dicionário vazio em `RecordedAttachmentEvidence`, vermelho em `evidence.Keys`. M9b: `NotFound` em `GetAttachmentLifecycle.Handler`, vermelho em `lifecycle.StatusCode` |

O caminho sem anexos tem oráculo próprio, no mesmo host fechado:
`A_request_that_names_no_attachment_is_accepted_while_the_capability_is_off`
recebe 202, e o braço vizinho, mesmo host e mesmo produtor, com um conjunto
liberado, recebe 422. Os dois braços diferem somente no conjunto que a
requisição nomeia.

Duas observações que valem registro sobre esses pares. Primeira: M5 e M6 leem
`IOptions<AttachmentCapabilityOptions>` em composições que **não ligam** a seção,
portanto o valor que a mutação lê é o padrão do tipo. Os testes desses dois
caminhos declaram `AcceptsNewAttachments=false` explicitamente, então as duas
leituras concordam em `false` e a atribuição do vermelho continua válida.
Segunda: M7, M8, M9a e M9b rodam dentro do host da API, onde a seção está ligada
e o teste a declara em `false`, portanto a mutação lê o valor configurado.

## 5. A reversão preserva os dados, lida de volta do banco

`Switching_the_capability_off_leaves_every_durable_row_where_it_was` aceita um
conjunto com a capacidade ligada, depois liga um host que não aceita nada novo e
**exerce as duas portas fechadas** nele (registro recusado com 409, claim
recusado), e só então lê as linhas de volta:

- a linha do anexo, com estado e identificador de conteúdo;
- a contagem de linhas de geração;
- a contagem de liberações;
- a contagem de dependências vivas e a contagem total de dependências.

O oráculo compara um registro inteiro antes e depois, e não a ausência de um
comando de exclusão. Um teste que só afirmasse "nenhum delete foi emitido"
passaria por cima de uma implementação que apagasse as linhas por outro meio.

No lado da notificação, `An_attempt_of_an_accepted_notification_...` lê o
documento `jsonb` do manifesto antes e depois do despacho sob a chave desligada e
exige bytes idênticos.

## 6. Upload falha fechado sem versionamento, medido

Medido nesta rodada, com `NOTIFICATIONHUB_REQUIRE_DOCKER=1`:

```
AttachmentObjectGenerationTests.A_store_that_never_versioned_is_refused_and_records_no_identity
AttachmentObjectGenerationTests.A_store_with_suspended_versioning_is_refused_and_records_no_identity
Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

Os dois casos criam um bucket sem versionamento, e um com versionamento
suspenso, apontam o host para ele e exercem o upload. O resultado medido: `503`
com `attachment-store-unidentified-generation`, sem
`attachment-store-unavailable`; o anexo permanece em `awaiting-upload`; nenhuma
linha de geração é escrita. O comportamento é correto e já estava construído: a
resposta de escrita não traz versão, `AttachmentObjectLocator.Create` recusa, e a
captura devolve `Unidentified`.

Custo declarado, e não é pequeno:

- **Bytes órfãos por falha.** Os mesmos testes leem o armazenamento depois e
  encontram **uma** geração durável com o conteúdo enviado, sob a chave derivada,
  que o módulo não consegue remover: remoção sem geração nomeada coloca um
  marcador de exclusão e deixa os bytes legíveis. Cada tentativa de upload contra
  um bucket sem versionamento deixa esse resíduo.
- **Custo acumulativo de armazenamento.** Com versionamento habilitado, toda
  escrita repetida sob a mesma chave cria uma geração nova e nenhuma some
  sozinha. Habilitar versionamento no bucket de produção exige política de ciclo
  de vida para gerações não correntes e para marcadores de exclusão, sem a qual o
  custo cresce monotonicamente.
- **Não há decisão de infraestrutura registrada** para esse pré-requisito.
  Nenhum Terraform foi escrito e nenhuma infraestrutura foi tocada nesta tarefa.

Ordem de operações que isso impõe: com a capacidade desligada o upload nem chega
a ser tentado, porque o registro é recusado antes. O pré-requisito de
versionamento só se torna alcançável depois de habilitar, o que reforça que
habilitar sem o bucket versionado entrega 503 ao produtor e acumula órfãos.

## 7. Leitores tolerantes antes dos escritores, confirmado por medição

A propriedade já era verdadeira e foi confirmada executando, e não lendo, os
oráculos que a exercem. Nenhum conserto foi necessário.

| Situação | Onde foi medida | Resultado |
|---|---|---|
| Linha de notificação sem manifesto (`NULL`) | `AcceptedAttachmentCapabilityTests.A_request_that_names_no_attachment_is_accepted_...` e o braço vizinho sem conjunto do teste de fallback | Aceita, despacha e caminha no plano normalmente |
| Documento presente e ilegível | `AcceptedAttachmentPreflightTests.An_unreadable_record_holds_the_attempt_and_calls_no_provider`, suíte existente, verde nesta rodada | Segura a tentativa em vez de perdê-la |
| Handle que o módulo não resolve | `IAttachmentEvidence` responde por ausência da chave, exercido pelo lado positivo em `The_authorized_readings_still_answer_...` | Ausente em vez de presente e vazio |

A matriz completa de implantação pertence à tarefa seguinte. O que está declarado
aqui é que a propriedade vale e foi executada.

## 8. Tabela de mutações

Toda mutação foi construída com portão pelo código de saída do build antes de
qualquer execução, e revertida com conferência por `git diff --numstat` mais
varredura por literal residual (`grep` por `mutationM|MutationM|MUTATION_M`,
resultado 0).

| ID | Eixo mutado | Teste que ficou vermelho | Asserção que falhou |
|---|---|---|---|
| M1 | Portão do registro neutralizado | `Registering_a_new_attachment_is_refused_...` e `Switching_the_capability_off_...` | `refused.StatusCode`, `registration.StatusCode` |
| M2 | Portão do claim neutralizado | `A_set_nobody_holds_yet_is_not_claimed_...`, `Switching_the_capability_off_...`, `A_request_that_names_no_attachment_...` | `refused.Status`, `carrying.StatusCode` |
| M3 | Portão do claim movido para **antes** do ramo de repetição | `A_claim_that_already_happened_is_answered_again_...` | `repeated.Status` |
| M4 | `AcceptsNewAttachments` com inicializador `= true` | 3 dos 5 casos de `AttachmentCapabilityOptionsTests` | seção ausente, seção sem valor, objeto novo |
| M5 | Portão dentro do preflight de envelope | `An_attempt_of_an_accepted_notification_...` | `provider.Requests.TryDequeue` |
| M6 | Portão no início do fallback | `The_fallback_still_decides_over_the_frozen_set_...` | `RunFallbackAsync` |
| M7 | Rodada de reconciliação curto-circuitada | `The_reconciliation_round_still_settles_...` | `round.Examined` |
| M8 | Varredura de abandono curto-circuitada | `The_abandonment_sweep_still_discards_...` | `swept.Discarded` |
| M9a | Leitura de evidência curto-circuitada | `The_authorized_readings_still_answer_...` | `evidence.Keys` |
| M9b | Leitura de ciclo de vida curto-circuitada | `The_authorized_readings_still_answer_...` | `lifecycle.StatusCode` |

Nenhuma mutação voltou verde.

Duas armadilhas caíram no caminho e ficam registradas porque valem mais que o
resultado. Na primeira tentativa de M5 o build **falhou** por nome de membro de
enum inexistente, e o `dotnet test --no-build` seguinte rodou o binário antigo e
reportou verde: exatamente o falso positivo que o portão pelo código de saída
existe para pegar. Na segunda, M4 derrubou três dos cinco casos e **não**
derrubou `The_configuration_the_host_ships_takes_no_new_attachments`, e isso está
certo: esse caso mede o arquivo `appsettings.json`, que declara `false`
explicitamente, e não o padrão do tipo. Os dois oráculos medem coisas diferentes
de propósito.

## 9. Falsificação não planejada, e a mais útil

A linha `AcceptsNewAttachments=true` acrescentada às fixtures não é decorativa, e
isso foi medido sem querer. Antes de tratar `NotificationsApiFixture`, a rodada
dirigida devolveu **11 reprovações** em `AcceptanceClaimTransactionTests`, todas
com `422 UnprocessableEntity` onde a suíte esperava `202 Accepted`. Depois de
declarar a chave na fixture, as mesmas 22 execuções passaram. O mesmo tratamento
foi necessário em `KafkaIngressFixture`, nos dois pontos onde ela compõe
configuração: o host web e o provider do papel de ingresso por barramento.

O fechamento do raio de alcance foi feito por duas vias que se conferem: a guarda
de metadados diz que somente dois tipos consultam o portão, e uma varredura da
árvore de testes por `/v1/attachments`, `IAttachmentClaim`, `ClaimableAttachments`
e `"attachments"` em corpo de requisição listou todos os candidatos. Todos foram
executados.

## 10. Como isso se compõe com o desligamento de fato que já existia

Existe hoje um segundo desligamento, e ele **não** é substituído por este. A
lista de tipos de conteúdo admitidos em
`Modules:AttachmentManagement:Validation:AdmittedContentTypes` está vazia, e com
ela nenhum anexo alcança o estado `released`.

Os dois se compõem em **estágios diferentes** e produzem observáveis diferentes:

| Estágio | Controle | Com o controle fechado |
|---|---|---|
| Registro | Capacidade | 409 `attachment-capability-not-enabled`; nenhuma referência é cunhada |
| Upload | nenhum | segue |
| Validação | Lista de tipos admitidos | 422 `attachment-content-refused`; a referência existe, o conteúdo está na custódia, e a liberação nunca acontece |

A lista vazia é da **validação** e não do **aceite**: com ela sozinha, um produtor
registra, gasta a transferência e só então descobre que nada será liberado, e o
sistema acumula anexos em `received` que a varredura de abandono depois descarta.
Com a capacidade desligada, nada disso começa. Medidos nesta rodada: o 409 vem do
teste novo; o 422 de conteúdo recusado vem de
`AttachmentLifecycleEndpointTests.Asking_for_a_verdict_that_refuses_says_one_word_and_repeating_it_says_it_again`,
verde na rodada de 203 casos.

Habilitar a capacidade **não** habilita anexos: sem tipo admitido, o conjunto
nunca fica reivindicável. São duas chaves e as duas precisam girar.

## 11. Execução e evidência

```
dotnet build MonteBravo.NotificationHub.sln -warnaserror --no-incremental
  -> Build succeeded. 0 Warning(s) 0 Error(s)

dotnet test Platform.UnitTests         -> Passed: 2142, Failed: 0, Skipped: 0
dotnet test Platform.ArchTests         -> Passed:   30, Failed: 0, Skipped: 0
dotnet test Platform.SecurityArchTests -> Passed:   14, Failed: 0, Skipped: 0

NOTIFICATIONHUB_REQUIRE_DOCKER=1, integração dirigida:
  ~IntegrationTests.AttachmentManagement | ~AcceptedAttachments
      -> Passed: 238, Failed: 0, Skipped: 0   (3 m 2 s)
  ~AcceptanceClaimTransactionTests | ~AcceptedAttachmentIngressSnapshotTests
  | ~AttachmentContractIngressTests | ~AttachmentManifestIdempotencyTests
      -> Passed:  22, Failed: 0, Skipped: 0
  ~Ingress.Kafka
      -> Passed:  53, Failed: 0, Skipped: 0   (2 m 8 s)
  ~WorkerHostCompositionTests | ~SendGridAttachmentSubmissionTests
  | ~ProviderTransfer | ~AttachmentContractSurfaceTests | ~OpenApiDocument
      -> Passed: 105, Failed: 0, Skipped: 0
  ~AttachmentObjectGenerationTests, versionamento ausente e suspenso
      -> Passed:   2, Failed: 0, Skipped: 0
```

`Skipped` foi zero em todas as rodadas de integração, o que separa este resultado
de uma sonda de Docker que expira e vira skip silencioso. Os `.trx` ficaram em
`tests/Platform.IntegrationTests/TestResults/t36-*.trx`.

O total de unidade subiu de 2136 para 2142: seis casos novos, cinco em
`AttachmentCapabilityOptionsTests` e um em `AttachmentCapabilityCallSiteTests`.

## 12. Contrato publicado que mudou

`POST /v1/attachments` passa a declarar `409` no documento OpenAPI, porque a rota
agora responde com esse status. Isso quebrou três casos de
`AttachmentIngressContractTests`, e os três foram tratados como o contrato manda:
a asserção semântica passou a nomear o `409`, e só então o digest foi
recongelado de `8c47c746...` para `51ea71b2...`. O digest nunca foi atualizado
sozinho.

Nenhuma linha entrou em `NotificationRejectionReasons` e nenhuma linha entrou em
`docs/guia-integracao-produtor.md`, portanto a função de adequação
`Every_rejection_reason_has_a_row_in_the_producer_guide` continua verde sem que a
tarefa seguinte tenha sido antecipada.

## 13. O que os oráculos não provam

- **Não provam que o host de produção sobe fechado.** Provam que o binder e o
  tipo respondem fechado sem seção, e que o `appsettings.json` do repositório diz
  `false`. Qualquer camada de configuração acima dele, variável de ambiente,
  gerenciador de segredos ou mapa de configuração do orquestrador, pode dizer
  outra coisa, e nada aqui a inspeciona.
- **Não provam que o produtor distingue as duas recusas no ingresso.** Ele não
  distingue: capacidade desligada e conjunto não reivindicável saem com a mesma
  palavra `attachments-not-claimable`. A distinção existe apenas no log e na
  superfície do próprio módulo.
- **Não provam nada sobre reverter algo que já saiu.** A reversão medida preserva
  manifesto, gerações, dependências e liberações. Nada foi medido sobre
  notificação já entregue ao provedor.
- **Não provam que a validação de conteúdo continua funcionando com a chave
  desligada.** O teste de leituras autorizadas cobre a leitura de ciclo de vida e
  a evidência; pedir um veredito para um anexo existente com a capacidade
  desligada não foi exercido. Pela guarda de metadados o portão não alcança esse
  caminho, mas isso é argumento estrutural e não medição.
- **Não provam o comportamento da suíte de integração inteira.** Ela é
  inutilizável nesta máquina: a mesma árvore já devolveu 476, 15, 221, 2, 634,
  232 e 108 reprovações com `System.TimeoutException` em
  `NamedPipeClientStream.ConnectInternal`. O fechamento do raio de alcance foi
  feito pela guarda de metadados mais varredura da árvore de testes, e todos os
  candidatos encontrados foram executados. Um host de teste que alcance uma das
  duas portas por um caminho que a varredura não nomeia ficaria de fora.
- **Não provam ordenação entre réplicas.** Se um host antigo e um host novo
  rodarem lado a lado com valores diferentes da chave, o comportamento é por
  réplica. A matriz de implantação pertence à tarefa seguinte.
- **A varredura de abandono do teste roda sobre a base compartilhada.** Ela
  afirma somente sobre a própria referência, mas remove conteúdo de linhas
  vizinhas antigas que já tenham vencido o prazo. A coleção é serializada, então
  nenhuma suíte vizinha é interrompida no meio; ainda assim, o efeito colateral
  está declarado.
- **Não houve medição de infraestrutura.** Nenhum Terraform, nenhum bucket de
  produção, nenhuma política de ciclo de vida.

## 14. Pendências que precisam de decisão humana

1. **Versionamento no bucket de produção** continua sem decisão de
   infraestrutura registrada, e é pré-requisito. Sem ele, habilitar entrega 503 ao
   produtor e deixa bytes órfãos por tentativa.
2. **Política de ciclo de vida** para gerações não correntes e marcadores de
   exclusão, sem a qual o custo de armazenamento cresce monotonicamente depois de
   habilitar.
3. **Nenhuma habilitação operacional** deve ocorrer antes do ensaio da tarefa
   seguinte e da publicação da documentação do produtor. O código está pronto e
   implantado desligado; girar a chave é ato operacional e não está nesta tarefa.
4. **Extensão de escopo a registrar**: seis arquivos fora da tabela de limites da
   fatia foram tocados, todos por necessidade estrutural.
   `src/Platform.Api/Modules/Notifications/KafkaIngressWorkerRole.cs`, uma linha
   com argumento novo, está na tabela de modificáveis;
   `src/Platform.Api/Composition/IntegrationSurfaceSetup.cs` já tem extensão
   aprovada de 2026-09-03. As quatro fixtures `AttachmentManagementApiFixture`,
   `CorePipelineFixture`, `NotificationsApiFixture` e `KafkaIngressFixture` não
   estão cobertas pelo glob `tests/**/AttachmentManagement/**/*Tests.cs` e seguem
   o precedente da extensão aprovada de 2026-09-02 para a fixture do módulo. O
   motivo é medido: sem a chave declarada nelas, 11 casos existentes reprovam.
