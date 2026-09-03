# Gestão de anexos do Notification Hub: mapa de impacto consolidado

**Status**: APPROVED
**Spec**: `SPEC-001`
**Estágio**: REFINE
**Data**: 2026-08-31
**Sementes cobertas**: `SEED-005`, `SEED-007`
**Fontes**: [01-development-specification.md](../requirements/core/01-development-specification.md), [02-implementation-map.md](../requirements/core/02-implementation-map.md), [03-verification-plan.md](../requirements/core/03-verification-plan.md)

## 1. Cenário e limites da análise

Quando `Notifications` aceita uma solicitação que carrega um conjunto de anexos já liberados, duas exigências precisam coexistir com a invariante transacional vigente da ingestão:

- `ER-006`: o claim do conjunto é indivisível e atomicamente consistente com o aceite. Falha entre claim, notificação, idempotência, outbox, auditoria e commit nunca deixa notificação aceita sem claim integral.
- `ER-009`: um snapshot imutável do manifesto aceito alimenta toda tentativa, retry e fallback, sem consultar metadado mutável.

A invariante vigente confirma quatro escritas em uma transação ou nenhuma: a linha `notification`, a linha `idempotency_key`, a mensagem em `platform.outbox` e o `audit_event` acrescentado por `IAuditTrail` com a `DbTransaction` bruta ([IngestionWriter.cs:47-57](../../../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IngestionWriter.cs)).

Fora do recorte: escolha entre buffer, streaming e spool (`ER-016`, gate de `SEED-009`), política executável de validação (gate de `SEED-004`), parâmetros de envelope da primeira produção (gate de `SEED-008`) e regra de roteamento (gate de `SEED-006`). Este mapa não os antecipa.

## 2. Fatos observados

Cada linha foi lida no repositório nesta invocação. Afirmação sem citação está na seção 9, não aqui.

| # | Fato | Evidência |
|---|---|---|
| `FT-01` | A aceitação abre a transação depois de as entidades entrarem no rastreador, grava, acrescenta outbox, acrescenta auditoria e confirma imediatamente. | `Infrastructure/Persistence/IngestionWriter.cs:47-57` |
| `FT-02` | O commit segue o append de auditoria de imediato porque esse append segura o bloqueio da cadeia de partições até o fim da transação. | `Infrastructure/Persistence/IngestionWriter.cs:30-33,94-100` |
| `FT-03` | A ordem "outbox antes de auditoria" é decisão de latência declarada e repetida em todos os writers do módulo. | `IngestionWriter.cs:94-100`; `IIngestionSink.cs:88-93`; `IngressCommitWriter.cs:14-18` |
| `FT-04` | Um contrato publicado de contexto irmão já recebe a `DbTransaction` bruta do chamador, nunca um contexto ou uma entidade. | `Modules/Audit/Integration/V1/IAuditTrail.cs:12-20` |
| `FT-05` | O append de auditoria toma `pg_advisory_xact_lock` com chave derivada de ano e mês, liberado apenas no fim da transação do chamador. | `Modules/Audit/Infrastructure/AuditTrail/TransactionalAuditTrail.cs:54-62,102-103` |
| `FT-06` | Esse append recusa nível de isolamento mais forte que READ COMMITTED duas vezes: o declarado e o reportado pelo servidor. | `TransactionalAuditTrail.cs:150-163,193-202` |
| `FT-07` | A costura de escrita da ingestão aceita exatamente uma `OutboxAppend` e uma `AuditEntry`. | `Infrastructure/Persistence/IIngestionSink.cs:16-21` |
| `FT-08` | No caminho Kafka a aceitação confirma em uma transação e a marca de deduplicação confirma em outra, aberta depois. | `Infrastructure/Persistence/IngressCommitWriter.cs:38-50` |
| `FT-09` | A violação de unicidade da chave idempotente é resolvida por exceção dentro da transação, com remoção das entidades rastreadas. | `IngestionWriter.cs:60-73` |
| `FT-10` | A forma canônica do hash escreve membros em ordem ordinal fixa, escrita à mão, e omite opcionais ausentes. | `Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs:34-86` |
| `FT-11` | `channelsHint` não nulo porém vazio escreve `channelsHint: []`, porque o teste de presença é apenas `is not null`. | `RequestNotification.PayloadHash.cs:44-53` |
| `FT-12` | Os testes vigentes do hash de ingestão são todos relacionais e não existe nenhum literal de 64 hexadecimais em `tests/Platform.UnitTests/Notifications/` nem em `tests/Platform.IntegrationTests/Notifications/`. | `tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs:22-118`; varredura da suíte |
| `FT-13` | Vetores congelados em literal existem para outros hashes canônicos do repositório, em `Audit`, `Compliance` e `TemplateManagement`. | `tests/Platform.UnitTests/TemplateManagement/CanonicalHashTests.cs:47`; `tests/Platform.UnitTests/Audit/AuditChainTests.cs:51` |
| `FT-14` | Existe precedente exato de snapshot imutável no nível da notificação: `admitted_plan`, coluna `jsonb` anulável. | `Domain/AdmittedDeliveryPlan.cs:29-45`; `Infrastructure/Persistence/Configurations/NotificationConfiguration.cs:54-56` |
| `FT-15` | O leitor desse snapshot distingue três desfechos: presente, ausente e ilegível. | `Domain/AdmittedDeliveryPlan.cs:63-108` |
| `FT-16` | O snapshot congela composição e ordem, e deliberadamente não congela elegibilidade, que é relida no passo seguinte. | `Domain/AdmittedDeliveryPlan.cs:21-27`; `Features/Fallback/FallbackRequestHandler.cs:140-151` |
| `FT-17` | A mensagem de fila da aceitação carrega apenas `notificationId`, e o processor relê a linha `notification` pelo id. | `RequestNotification.Handler.cs:432-441`; `Features/Pipeline/CoreMessageProcessor.cs:68-69` |
| `FT-18` | Pipeline, Dispatching e Fallback já carregam a linha `notification` por id em seus próprios caminhos. | `CoreMessageProcessor.cs:69`; `DispatchMessageProcessor.cs:130-132`; `FallbackRequestHandler.cs:90-91` |
| `FT-19` | Um veredito terminal reescreve o envelope selado e descarta a forma completa do conteúdo de tentativa. | `Infrastructure/Persistence/AttemptDispatchWriter.cs:39-46,426-447` |
| `FT-20` | O claim da tentativa é `UPDATE` condicional guardado por quatro predicados, e a verdade é a contagem de linhas afetadas. | `AttemptDispatchWriter.cs:86-102` |
| `FT-21` | A marca de deduplicação do consumidor de dispatch confirma com o veredito, nunca com o claim. | `AttemptDispatchWriter.cs:29-37,193,244,299,356` |
| `FT-22` | Uma reentrega resolve pelo estado armazenado antes de qualquer claim ou chamada ao provedor. | `Features/Dispatching/DispatchMessageProcessor.cs:134-141` |
| `FT-23` | Timeout ambíguo vira `DispatchVerdict.Unknown` e estaciona a tentativa; só circuito aberto e throttle devolvem para `queued`. | `DispatchMessageProcessor.cs:285-294,378` |
| `FT-24` | A reconciliação de entrega roda a cada 24 horas com `StaleAfter` de 6 horas. | `src/Platform.Api/appsettings.json:45-51` |
| `FT-25` | Reserva idempotente por identidade externa já existe: `INSERT ... ON CONFLICT DO NOTHING` com rollback quando a contagem é zero. | `Infrastructure/Persistence/DeliveryEventWriter.cs:131-147` |
| `FT-26` | Compensação com passivo em coluna, índice parcial e varredura periódica já existe para efeito que atravessa módulo. | `PendingSuppressionDrain.cs:19-43,91-126`; `Configurations/DeliveryEventConfiguration.cs:78-87` |
| `FT-27` | Consumidor por módulo com deduplicação em `platform.processed_messages` é padrão estabelecido. | `Modules/ContactConsent/Infrastructure/Consuming/ContactsChangedProcessor.cs:21`; `DispatchMessageProcessor.cs:56` |
| `FT-28` | A recepção de callback do provedor recusa deliberadamente o append de auditoria na sua transação, para não serializar o callback contra a ingestão. | `Infrastructure/Persistence/DeliveryEventWriter.cs:48-55` |
| `FT-29` | As seis connection strings do host são textualmente idênticas e apontam para `Database=NotificationHub_TemplateManagement` em `localhost` com `Username=postgres`. | `src/Platform.Api/appsettings.json:12,20,73,80,111,124` |
| `FT-30` | Cada módulo registra o próprio `DbContext` com `AddDbContext`, sem pooling, e fixa um schema próprio. | `NotificationsEfSetup.cs:17-33`; `NotificationsDbContext.cs:60`; `AuditDbContext.cs:24` |
| `FT-31` | Não existe em `src/` nem em `tests/` qualquer uso de `TransactionScope`, `System.Transactions`, `Enlist` ou `UseTransaction`. | Varredura em `src` e `tests` |
| `FT-32` | Nenhuma connection string declara `Maximum Pool Size`, `Pooling`, `Enlist` ou `Multiplexing`. | `src/Platform.Api/appsettings.json:12,20,73,80,111,124` |
| `FT-33` | O comando de ingresso não possui membro de anexos, e o precedente de lista opcional é `ChannelsHint`. | `RequestNotification.Command.cs:7-37` |
| `FT-34` | O binder Kafka possui leitores para string, inteiro, objeto, array de strings e `DateTimeOffset`, e nenhum para array de objetos. | `Features/Ingress/KafkaIngressProcessor.cs:367-478` |
| `FT-35` | `EmailMessage` contém apenas assunto, preheader, HTML e texto, e a hierarquia `RenderedMessage` é fechada. | `Modules/Dispatch/Integration/V1/RenderedMessage.cs:11-16,23-27` |
| `FT-36` | `DispatchRequest` já evoluiu de forma aditiva três vezes por membros opcionais com valor padrão. | `Modules/Dispatch/Integration/V1/DispatchRequest.cs:37-41` |
| `FT-37` | A forma de fio do SendGrid não possui membro de anexos, e `BuildRequest` é o único ponto de composição. | `SendGridMailRequest.cs:10-15`; `SendGridChannelProvider.cs:68-95` |
| `FT-38` | O adaptador nunca reescreve conteúdo, porque o hash auditado descreve os bytes exatos entregues ao provedor. | `Modules/Dispatch/AGENTS.md:79-86` |
| `FT-39` | `notification` e `notification_attempt` são pais particionados por mês, com chave primária composta, e as irmãs não declaram chave estrangeira para `notification`. | `Configurations/NotificationConfiguration.cs:17-20`; `Migrations/20260823122306_CreateCorePipelineState.cs:41-55` |
| `FT-40` | O padrão de migração aditiva é coluna `jsonb` anulável sem default, com `SET LOCAL lock_timeout = '3s'` e justificativa de operação apenas de catálogo. | `Migrations/20260825145151_AddNotificationAdmittedPlan.cs:38-58`; `Migrations/20260825223842_StoreCallbackPayloadOnce.cs:29-33` |
| `FT-41` | O teste de fronteiras descobre módulos por namespace, sem lista fixa, e admite dependência entre contextos apenas sobre `Integration.V1`. | `tests/Platform.ArchTests/ArchitectureTests.cs:23,66-93` |
| `FT-42` | Existe precedente de injeção de falha na trilha de auditoria provando rollback de notificação, registro e outbox. | `tests/Platform.IntegrationTests/Notifications/RequestNotificationTransactionTests.cs:15-48` |
| `FT-43` | Existe sonda de contenção do bloqueio de cadeia com desenho fatorial, baseline versionado e portão relativo. | `tests/Platform.PerformanceTests/Contention/ContentionArm.cs:59-113`; `tests/Platform.PerformanceTests/Gate/ContentionBaseline.cs:145-172` |
| `FT-44` | A linha de base medida com quatro appenders registra `NormalizedHold` de 5,0553. | `tests/Platform.PerformanceTests/baselines/audit-chain-contention.json` |
| `FT-45` | O caminho rápido de idempotência memoriza `(notificationId, payloadHash)` no Redis e é escrito depois do commit. | `Infrastructure/Idempotency/IdempotencyFastPath.cs:54-64`; `RequestNotification.Handler.cs:226-230` |
| `FT-46` | `AWSSDK.S3`, `AWSSDK.KeyManagementService`, `Testcontainers.LocalStack` e `StackExchange.Redis` já estão no gerenciamento central de pacotes. | `Directory.Packages.props:41-47,68-75` |
| `FT-47` | Existe armazenamento S3 com Object Lock, digest SHA-256 em metadado e leitura por chave, dentro de `Audit`. | `Modules/Audit/Infrastructure/Worm/S3WormObjectStore.cs:17-81` |
| `FT-48` | O REST autoriza por classe a partir dos app roles, sem provar vínculo do principal à `application`. | `Infrastructure/Authorization/ProducerAuthorization.cs:36-53` |
| `FT-49` | O guia do produtor publica hoje a proibição explícita de anexos no registro Kafka, com limite de 256 KB. | `docs/guia-integracao-produtor.md:215-216` |
| `FT-50` | Não existe telemetria em `src/`: a varredura por `OpenTelemetry`, `new Meter(` e `CreateCounter` retorna vazio. | Varredura em `src/`; `.araia/stack-profile.yaml:30` |
| `FT-51` | Já existe cota explícita para entrada de cardinalidade escolhida por terceiro: o callback de provedor declara `MaxBodyBytes` de 1048576 e `MaxEventsPerCallback` de 200. | `src/Platform.Api/appsettings.json:30-33` |
| `FT-52` | O gerenciamento central fixa versões transitivas, e por isso `Npgsql` está resolvido em 10.0.3 mesmo sendo transitivo, com lock file de restore em toda a solução. | `Directory.Packages.props:4-5`; `src/Platform.Api/packages.lock.json:382-385`; `Directory.Build.props:11` |
| `FT-53` | Appends concorrentes serializam sob o bloqueio advisory sem bifurcar a cadeia, e a ordem de sequência prova que o valor é reservado com o bloqueio já obtido. | `tests/Platform.IntegrationTests/Audit/AuditChainIntegrationTests.cs:41,56-79` |
| `FT-54` | Nenhuma função de adequação vigente barra a transação compartilhada, porque `DbTransaction` não é tipo de módulo e o contrato moraria em `Integration.V1`. | `tests/Platform.ArchTests/ArchitectureTests.cs:96-133` |
| `FT-55` | A regra que mantém o `Domain` livre de tecnologia lista seis namespaces proibidos e `Amazon` não está entre eles. | `tests/Platform.ArchTests/ArchitectureTests.cs:33-40` |
| `FT-56` | Os dois testes de OpenAPI verificam apenas exposição e comportamento por ambiente, e nenhum deles lê o corpo do documento. | `tests/Platform.IntegrationTests/OpenApiDocumentExposureTests.cs:27-50`; `tests/Platform.IntegrationTests/OpenApiDocumentEnvironmentTests.cs:32-45` |
| `FT-57` | O inventário de contratos publicados compara nos dois sentidos e reprova tanto por quebra nova quanto por reparo não registrado. | `tests/Platform.ArchTests/PublishedContractEqualityTests.cs:62-78,163-164` |
| `FT-58` | Todo motivo de rejeição publicado precisa de linha na tabela do guia do produtor, verificada por teste. | `tests/Platform.ArchTests/ProducerGuideCatalogTests.cs:25-30` |
| `FT-59` | O writer de dead-letter reemite o corpo original quando o motivo não está na lista de redação. | `Infrastructure/Consuming/IngressDeadLetterWriter.cs:100-107,173-176` |
| `FT-60` | O fan-out de push copia conteúdo selado, hashes e prazo campo a campo para cada irmão criado. | `AttemptDispatchWriter.cs:146-158` |
| `FT-61` | O dialeto de opções usa `Bind` mais `ValidateDataAnnotations` mais `ValidateOnStart`, e `appsettings.json` declara uma seção sob `Modules` por módulo existente. | `NotificationsModule.cs:38-54`; `src/Platform.Api/appsettings.json` |
| `FT-62` | Fixtures de integração já sobem LocalStack e expõem `IAmazonS3` e KMS, e existe cifra de envelope com braço local e braço KMS. | `tests/Platform.IntegrationTests/Notifications/CorePipelineFixture.cs:87,101`; `src/Platform.Api/Infrastructure/Cryptography/` |

## 3. Achados

| ID | Achado | Classificação | Severidade |
|---|---|---|---|
| `RF-001` | `VER-008` exige que "vetores dourados apresentem zero divergência antes e depois da mudança", mas nenhum vetor dourado do hash de ingestão existe no repositório. A suíte atual permaneceria verde se toda a forma canônica mudasse. | `GAP` | `CRITICAL` |
| `RF-002` | A alternativa registrada "reserva idempotente com compensação", lida ao pé da letra, admite uma janela em que a notificação aceita já é durável e o claim ainda é uma reserva que uma compensação posterior desfaz. Esse é exatamente o desfecho que `ER-006` proíbe. | `RISK` | `CRITICAL` |
| `RF-003` | Se o membro do manifesto seguir o teste de presença de `ChannelsHint`, um produtor que enviar `attachments: []` produzirá hash diferente de um produtor que omitir o membro, e o caminho sem anexos deixa de ser único. | `RISK` | `HIGH` |
| `RF-004` | Um passo de claim posicionado depois do append de auditoria entra na posse de um bloqueio advisory global do mês corrente e degrada ingestão, pipeline, dispatch e os demais módulos da frota. | `RISK` | `HIGH` |
| `RF-005` | `ER-013` exige impedir descarte enquanto houver dependência ativa, mas não qualifica se uma reserva conta como dependência ativa. Sem essa qualificação, a varredura de abandonados pode remover objeto reservado. | `GAP` | `HIGH` |
| `RF-006` | O caminho rápido memoriza `(notificationId, payloadHash)`. Em implantação progressiva com instâncias computando hashes diferentes para o mesmo corpo, um replay legítimo vira conflito. | `RISK` | `HIGH` |
| `RF-007` | Uma variante de claim que abra segunda conexão concorrente por aceite consome um pool Npgsql único e sem tamanho declarado, e pode produzir espera circular. A saturação aparece como timeout de aquisição, não como lentidão de consulta. | `RISK` | `HIGH` |
| `RF-008` | A viabilidade da transação compartilhada depende de os módulos partilharem a mesma instância física. As seis connection strings apontam para o mesmo database em `localhost`, que é configuração de desenvolvimento, e nada no repositório prova a topologia de produção. | `GAP` | `MEDIUM` |
| `RF-009` | Uma tentativa estacionada em `unknown` mantém dependência ativa sobre o conjunto por até o ciclo de reconciliação, cujo horizonte é de ordem de um dia. | `RISK` | `MEDIUM` |
| `RF-010` | O manifesto não pode viajar dentro do envelope de conteúdo renderizado, porque um veredito terminal descarta a forma completa desse envelope e `ER-014` exige reconstrução depois do desfecho. | `RISK` | `MEDIUM` |
| `RF-011` | O binder Kafka não possui leitor para array de objetos. Um manifesto que não seja lista de strings opacas exige um leitor novo, com a disciplina dos existentes. | `GAP` | `MEDIUM` |
| `RF-012` | Nenhuma fonte aprovada publica cardinalidade máxima nem tamanho máximo do manifesto. Sem esse teto, o custo do snapshot, do hash canônico e do claim indivisível fica sem cota superior. | `GAP` | `MEDIUM` |
| `RF-013` | Não existe telemetria no processo. Qualquer critério de convergência redigido como observação em produção não tem instrumento; a afirmação precisa recair sobre estado durável. | `RISK` | `MEDIUM` |
| `RF-014` | Uma variante de transação compartilhada herda a recusa de isolamento mais forte que READ COMMITTED, imposta por um contrato de terceiro, e a recusa acontece depois de o bloqueio já ter sido tomado. | `RISK` | `MEDIUM` |
| `RF-015` | O precedente `admitted_plan` responde `ER-009` na íntegra: coluna `jsonb` anulável na notificação, migração aditiva apenas de catálogo, leitor de três desfechos e a separação entre composição congelada e elegibilidade relida. | `ALIGNED` | não se aplica |
| `RF-016` | Os três consumidores exigidos já carregam a linha `notification` por id, então o manifesto no nível da notificação custa zero consulta adicional e zero chamada entre módulos no ponto de envio. | `ALIGNED` | não se aplica |
| `RF-017` | A mensagem de fila da aceitação carrega apenas `notificationId`, o que já satisfaz `ER-007` nessa aresta sem nenhuma mudança. | `ALIGNED` | não se aplica |
| `RF-018` | A garantia de no máximo uma chamada ao provedor por tentativa (`ER-012`) já existe em quatro camadas e não muda com anexos; a revalidação de `ER-010` cabe na janela entre o claim e a chamada, onde já vivem a checagem de validade e a segunda avaliação de kill switch. | `ALIGNED` | não se aplica |
| `RF-019` | A função de adequação descobre módulos por namespace e já cobriria o novo contexto sem edição, admitindo dependência apenas sobre `Integration.V1`. | `ALIGNED` | não se aplica |
| `RF-020` | Todas as primitivas de reserva idempotente, dedupe transacional, outbox e compensação com passivo persistido já existem implementadas no repositório. A alternativa de reserva não é a opção nova: é o dialeto vigente para efeitos que atravessam módulo. | `ALIGNED` | não se aplica |
| `RF-021` | Pacotes de S3, KMS, LocalStack e Redis já estão no gerenciamento central, e existe precedente de armazenamento S3 com Object Lock e digest em metadado. | `ALIGNED` | não se aplica |
| `RF-022` | Guardar na tentativa apenas um digest do manifesto, no espírito dos dois digests de conteúdo já existentes, daria à captura de fio de `VER-003` uma testemunha por tentativa sem duplicar o conjunto. | `OPPORTUNITY` | não se aplica |
| `RF-023` | A sonda de contenção existente aceita um braço adicional sem reescrita, o que transforma a posição do claim em questão medida em vez de argumentada. | `OPPORTUNITY` | não se aplica |
| `RF-024` | As funções de adequação vigentes permitem a transação compartilhada e são cegas ao seu custo: SQL literal no módulo dono, isolamento travado por contrato de terceiro e escrita por conexão alheia não são medidos por nenhuma regra. Ausência de reprovação automática não é aval de projeto. | `RISK` | `MEDIUM` |
| `RF-025` | Existe precedente de cota explícita para entrada de cardinalidade escolhida por terceiro, no callback de provedor. O caminho novo não herda nada disso. | `OPPORTUNITY` | não se aplica |
| `RF-026` | `RF-001` não é um caso isolado. O Plano de Verificação declara ao menos cinco oráculos cujo artefato de comparação não existe no repositório. Cada um deles precisa ser criado **antes** da mudança que deveria verificar, o que é uma restrição de ordenação para o PLAN, não uma tarefa de teste no fim da fatia. | `GAP` | `CRITICAL` |
| `RF-027` | Uma migração sem o `ModelSnapshot` regenerado faz `Migrate()` recusar por mudança pendente de modelo. O arquivo de migração não pode ser escrito à mão. | `RISK` | `MEDIUM` |
| `RF-028` | Um módulo novo com `ValidateOnStart` e sem seção base em `appsettings.json` derruba toda fixture de `WebApplicationFactory`, e não apenas os testes do módulo. | `RISK` | `MEDIUM` |
| `RF-029` | Um record publicado novo com membro de coleção reprova o inventário de igualdade de contratos até ser reparado ou registrado com razão. É reprovação esperada, não regressão. | `RISK` | `MEDIUM` |
| `RF-030` | O dead-letter reemite o corpo original para motivos fora da lista de redação. A superfície só é segura enquanto a referência de anexo for opaca por contrato. | `RISK` | `MEDIUM` |
| `RF-031` | O fan-out de push copia campo a campo. Se o manifesto for por tentativa, um campo esquecido deixa irmãos sem conjunto, e nada no compilador detecta a omissão. | `RISK` | `MEDIUM` |
| `RF-032` | Todo motivo de rejeição novo derivado de anexo reprova o catálogo do guia do produtor até o guia ganhar a linha correspondente. O guia hoje proíbe anexos de forma explícita. | `GAP` | `MEDIUM` |

## 4. Análise das cinco perguntas

### 4.1 Qual alternativa de consistência do claim é compatível

Nenhuma das duas alternativas registradas, como estão escritas.

A transação compartilhada por contrato é tecnicamente viável e não viola `ER-001`: o precedente já roda, porque `IAuditTrail.AppendAsync` mora em `Integration.V1` e recebe uma `DbTransaction`, que é tipo do BCL e não tipo interno de EF (`FT-04`), e a implementação escreve o schema do próprio dono sobre a conexão do chamador (`FT-05`). O que ela viola é a restrição do recorte, porque acopla `AttachmentManagement` à transação aberta por `Notifications`. Além disso herda três consequências duráveis: SQL literal no módulo dono, isolamento travado em READ COMMITTED por contrato de terceiro (`FT-06`, `RF-014`) e dependência de instância física compartilhada, que hoje não está provada (`RF-008`).

A reserva com compensação posterior, ao pé da letra, é o desfecho que `ER-006` proíbe (`RF-002`).

A variante compatível é uma terceira, derivada da segunda: **reserva idempotente pré-transacional com confirmação carregada pela própria transação de aceite, tendo a expiração da reserva como única compensação**. Na ordem dos efeitos:

1. Antes de abrir a transação de aceite, `Notifications` reserva o conjunto completo pelo contrato publicado, de forma idempotente na chave de idempotência. A resposta é o snapshot imutável do conjunto inteiro ou uma recusa. Recusa parcial não existe. Uma reserva fixa identidade e bloqueia descarte, e não torna o anexo enviável.
2. A transação vigente confirma seis escritas em vez de quatro: as quatro atuais, mais o snapshot do manifesto no mesmo INSERT da linha `notification`, mais uma segunda mensagem de outbox confirmando o claim, acrescentada sobre a mesma `DbTransaction` pelo contrato de plataforma que já recebe transação.
3. `AttachmentManagement` consome essa mensagem em fila própria com deduplicação em `processed_messages`, no padrão já estabelecido (`FT-27`), e promove reserva para claim de forma idempotente.
4. Uma reserva que nunca recebe confirmação expira e é varrida. A varredura é segura porque confirmação e aceitação partilham o mesmo commit, o que torna "aceito sem confirmação enfileirada" impossível por construção.

O órfão possível deixa de ser "aceito sem claim" e passa a ser "reservado sem aceite", que é precisamente o órfão que `PAC-010` manda convergir e que nunca produziu anexo utilizável. Todas as primitivas necessárias já existem (`RF-020`).

O diagrama abaixo mostra a ordenação e onde cada falha injetada cai. Note que os três pontos de falha ficam ou antes do `BEGIN`, e então a reserva expira, ou dentro do bloco, e então nada persiste: não há posição em que uma notificação aceita sobreviva sem a confirmação enfileirada.

![Sequência do aceite com anexos: a reserva idempotente acontece fora da transação e devolve o snapshot do manifesto; a transação confirma a notificação com o manifesto, a chave de idempotência, a confirmação do claim, a mensagem de aceite e o audit_event, nessa ordem; o consumo posterior promove a reserva para claim. Três pontos de falha marcam que o único órfão possível é reserva sem aceite.](diagrams/claim-accept-transaction-ordering.svg)

Restrição dura de posição: o append de confirmação precisa vir **antes** do append de auditoria, pela mesma razão de latência já escrita para o append de outbox (`FT-03`, `RF-004`).

### 4.2 Onde persistir o snapshot e como os consumidores o leem

Na linha `notification`, como coluna `jsonb` aditiva e anulável, escrita no mesmo INSERT do aceite, com leitor tolerante de três desfechos. Não na tentativa.

O precedente é literal e do mesmo módulo (`FT-14`, `FT-15`, `FT-16`, `RF-015`), e os três consumidores exigidos já carregam a linha (`FT-18`, `RF-016`), o que satisfaz "sem consultar metadado mutável" com custo zero.

A regra transferível do precedente decide o desenho: o snapshot congela **identidade e composição**, nunca **elegibilidade**. Liberação, revogação e validade continuam relidas ao vivo no preflight de `ER-010`, exatamente como o fallback relê consentimento e supressão. Congelar o estado de liberação dentro do snapshot derrotaria `PAC-013`.

O modelo alternativo, cópia por tentativa, importa um ciclo de vida que termina em destruição (`FT-19`, `RF-010`) e contradiz a própria invariante de conjunto único e imutável. Uma tabela filha exigiria replicar particionamento e ser juntada sem chave estrangeira (`FT-39`), acrescentando escrita à transação de aceite e leitura a cada consumidor, e só se paga se o manifesto precisar de estado mutável por item, que é o oposto do que `ER-009` pede.

### 4.3 Raio de impacto e preservação do caminho sem anexos

Quatro superfícies mudam, e a preservação de `ER-008` depende de uma única regra: **o membro do manifesto é omitido da forma canônica quando ausente ou vazio**.

- `RequestNotification.Command`: membro opcional novo, `init`, ausente por padrão, no bloco dos opcionais, sem posição nova no construtor primário (`FT-33`).
- `RequestNotification.PayloadHash`: bloco condicional entre `application` e `channelsHint`, porque a ordem ordinal codificada à mão é justamente essa. A condição de presença precisa ser falsa também para lista vazia, e não apenas para `null` (`FT-11`, `RF-003`).
- `IngestionWriter` e `IIngestionSink`: a costura passa a aceitar mais de uma mensagem de outbox (`FT-07`), e o ramo de compensação da violação de unicidade precisa resolver a reserva do perdedor de forma idempotente, porque quem perde a corrida já reservou (`FT-09`).
- Persistência: uma coluna `jsonb` anulável em `notifications.notification`, no formato aditivo já praticado (`FT-40`). A tabela `idempotency_key` não muda, porque `payload_hash` já carrega o manifesto assim que ele entra na forma canônica.

O caminho Kafka acrescenta duas exigências próprias: um leitor para o novo membro no binder (`FT-34`, `RF-011`) e a revisão do guia do produtor, que hoje proíbe anexos de forma explícita (`FT-49`).

#### Superfícies afetadas

Inventário do levantamento de engenharia. `criar` e `modificar` descrevem a natureza esperada da mudança, não autorizam implementação: o PLAN é quem fixa limites de Delivery Slice e lista final de arquivos.

| Caminho | Natureza | Classificação | Observação |
|---|---|---|---|
| `.../Ingress/RequestNotification/RequestNotification.Command.cs` | modificar | `ALIGNED` | Membro `init` anulável no bloco de opcionais; `ChannelsHint` é o precedente |
| `.../RequestNotification.Validator.cs` | modificar | `ALIGNED` | Teto de coleção mais regra por elemento condicionada, padrão em `:100-123` |
| `.../RequestNotification.PayloadHash.cs` | modificar | `RISK` | Inserção entre `:43` e `:44`; lista vazia precisa contar como ausência |
| `.../RequestNotification.Handler.cs` | modificar | `GAP` | O ponto do claim depende da decisão condicionada de `SEED-005` |
| `.../RequestNotification.Response.cs` e `.Endpoint.cs` | modificar | `ALIGNED` | Um `Outcome` novo por recusa; o binding do corpo é automático |
| `.../Features/Ingress/KafkaIngressProcessor.cs` | modificar | `GAP` | Não há leitor de array de objetos no binder |
| `.../Infrastructure/Persistence/IngestionWriter.cs` | modificar | `RISK` | Detém a transação; a confirmação entra antes do append de auditoria |
| `.../Infrastructure/Persistence/IIngestionSink.cs` | modificar | `RISK` | A assinatura muda e as duas posturas de sink seguem |
| `.../Infrastructure/Persistence/IngressCommitWriter.cs` | somente leitura | `RISK` | A aceitação já confirma antes da marca de deduplicação |
| `.../Domain/Notification.cs` e arquivo novo do manifesto | modificar e criar | `ALIGNED` | Molde de `AdmittedPlanJson` e `AdmittedDeliveryPlan` |
| `.../Configurations/NotificationConfiguration.cs` | modificar | `ALIGNED` | Coluna `jsonb` no molde de `:54-56` |
| `.../Persistence/Migrations/` (migração nova) | criar | `ALIGNED` | Coluna anulável sem default, com `lock_timeout` |
| `.../Migrations/NotificationsDbContextModelSnapshot.cs` | modificar | `RISK` | Sem regeneração, `Migrate()` recusa por mudança pendente de modelo |
| `.../Features/Pipeline/NotificationContext.cs` | modificar | `ALIGNED` | Slot novo no molde dos existentes |
| `.../Pipeline/Stages/RouteStage.cs` ou `Rules/ChannelSelectionRule.cs` | modificar | `GAP` | Regra de roteamento condicionada ao contrato de produto |
| `.../Features/Dispatching/DispatchMessageProcessor.cs` | modificar | `RISK` | Revalidação entre o claim e a chamada; errar o ponto reabre TOCTOU |
| `.../Features/Fallback/FallbackRequestHandler.cs` | modificar | `ALIGNED` | Copiar o snapshot em `QueueNextAttemptAsync` |
| `.../Infrastructure/Persistence/AttemptDispatchWriter.cs` | modificar | `RISK` | O descarte não pode alcançar o manifesto; o fan-out copia campo a campo |
| `.../Notifications/Integration/V1/NotificationRejectionReasons.cs` | modificar | `ALIGNED` | Motivo novo por decisão nova, sem colapsar em `PayloadInvalid` |
| `.../Modules/Dispatch/Integration/V1/DispatchRequest.cs` | modificar | `ALIGNED` | Membro opcional com default, precedente de três adições |
| `.../Modules/Dispatch/Integration/V1/` (contrato novo do conjunto) | criar | `RISK` | Coleção em record publicado dispara o inventário de igualdade |
| `.../Providers/SendGrid/SendGridMailRequest.cs` | modificar | `ALIGNED` | Membro com omissão quando nulo |
| `.../Providers/SendGrid/SendGridChannelProvider.cs` | modificar | `GAP` | A estratégia de preenchimento depende da medição de `SEED-009` |
| `src/Platform.Api/Modules/AttachmentManagement/` | criar | `GAP` | Módulo inexistente; a função de adequação o cobre sem edição |
| `src/Platform.Api/appsettings.json` | modificar | `RISK` | Sem seção base, `ValidateOnStart` derruba toda fixture |
| `docs/guia-integracao-produtor.md` | modificar | `RISK` | Proíbe anexos hoje, e a tabela de motivos é verificada por teste |

#### Superfícies que não podem mudar de comportamento

| Caminho | Invariante preservada | Teste que a protege hoje |
|---|---|---|
| `RequestNotification.PayloadHash.cs:34-87` | Hash byte a byte idêntico sem anexos | **Nenhum**: apenas relações, sem vetor congelado (`RF-001`) |
| `Domain/CanonicalJson.cs:50-161` | Ordem UTF-16, duplicata colapsada na última ocorrência | `tests/Platform.UnitTests/Notifications/CanonicalJsonTests.cs` |
| `IngestionWriter.cs:47-57` | Quatro escritas em uma transação ou nenhuma | `RequestNotificationTransactionTests.cs:15-48` |
| `IngestionWriter.cs:55-56` | Outbox antes de auditoria, commit logo em seguida | `AppendOrderProbe.cs` |
| `IngestionWriter.cs:60-73` | Violação de unicidade vira `ExistingRegistration` | `RequestNotificationIdempotencyTests.cs:80-120` |
| `RequestNotification.Admission.cs:93-140` | Replay resolvido antes do kill switch e do orçamento | `RequestNotificationIdempotencyTests.cs:44-77` |
| `KafkaIngressProcessor.cs:58-143` | Ordem dos gates: tipo, binding, validação, kill switch, autorização | `KafkaIngressCheckOrderTests.cs` |
| `AttemptDispatchWriter.cs:86-104` | Claim otimista por status, no máximo um envio por tentativa | `DispatchClaimRaceTests.cs` |
| `DispatchMessageProcessor.cs:134-141` | Redelivery após claim resolve pelo status, nunca reenvia | `DispatchClaimRaceTests.cs` |
| `SendGridChannelProvider.cs:88-94` | `text/plain` antes de `text/html` | `SendGridProviderContractTests.cs:39-40` |
| `AdmittedDeliveryPlan.cs:63-108` | Três estados de leitura; linha anterior à coluna continua avançando | `AdmittedDeliveryPlanReadTests.cs` |

A primeira linha é a mais importante da tabela: é a única invariante do conjunto que hoje **não tem** teste que a proteja.

### 4.4 Falhas injetadas que provam convergência

Sete pontos, seis dos quais já existem como costura no código atual.

| Ponto de injeção | Onde | O que prova |
|---|---|---|
| Dublê de `IAuditTrail` que lança | `Modules/Audit/Integration/V1/IAuditTrail.cs:20`, no padrão de `RequestNotificationTransactionTests.cs:15-48` | Falha no último append antes do commit: nada da aceitação persiste, a reserva fica órfã e expira |
| Dublê de `IOutboxWriter` que lança no segundo append e não no primeiro | `Infrastructure/Messaging/IOutboxWriter.cs` | Único ponto que discrimina confirmação de claim perdida de aceitação perdida. Não existe cenário equivalente hoje |
| Falha em `IIngestionSink.PersistAcceptedAsync` depois de a reserva retornar | `Infrastructure/Persistence/IIngestionSink.cs:16` | Reserva sem aceitação converge por expiração, sem anexo utilizável em nenhum instante |
| Corrida de duas requisições com a mesma chave idempotente | `IngestionWriter.cs:60-73` | O perdedor não deixa segunda reserva nem revoga a do vencedor; o replay devolve o mesmo manifesto |
| Encerrar o host entre os dois commits do caminho Kafka | `IngressCommitWriter.cs:38-50` | A reentrega resolve como replay e não reserva nem reivindica de novo |
| Falhar a memorização no caminho rápido depois do commit | `RequestNotification.Handler.cs:226-230` | O Postgres continua autoridade e o replay ainda responde com o mesmo manifesto |
| Reentregar a mensagem de dispatch entre o claim e o veredito | `AttemptDispatchWriter.cs:86-102`; desfecho em `DispatchMessageProcessor.cs:134-141` | No máximo uma chamada ao provedor por tentativa |

Restrição que condiciona todos: sem telemetria no processo (`FT-50`), a convergência precisa ser afirmada sobre estado durável, nunca sobre contador (`RF-013`).

### 4.5 Decisões que exigem ADR próprio

Três, pelo critério de irreversibilidade e custo crescente com o volume já produzido. Ver seção 8.

### 4.6 Oráculos declarados sem artefato de comparação

Este é o achado de maior consequência da análise, e ele não estava no recorte original. `RF-001` apareceu ao verificar `ER-008` e revelou um padrão: o Plano de Verificação aprovado declara oráculos que pressupõem artefatos de comparação inexistentes no repositório. Um oráculo sem lado anterior não reprova nada.

| Oráculo | O que o plano exige | O que existe hoje | Evidência |
|---|---|---|---|
| `VER-008` | "Vetores dourados apresentam zero divergência antes e depois da mudança" | Nenhum literal de 64 hexadecimais em `Notifications`; os oito casos são relacionais e comparam duas execuções do código atual | `tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs:22-118` |
| `VER-012` | "Sentinelas únicas semeadas no arquivo não aparecem em Kafka, SQS, outbox, dead-letter, logs, traces, métricas, respostas ou auditoria comum" | Nenhuma varredura com sentinelas na suíte | ausência em `tests/Platform.IntegrationTests` |
| `VER-015` | "NetArchTest reprova acesso a Domain, Infrastructure, EF ou S3 de outro contexto" | A regra que mantém o `Domain` livre de tecnologia lista seis namespaces e `Amazon` não está entre eles, então a parte de S3 do oráculo não é executável | `tests/Platform.ArchTests/ArchitectureTests.cs:33-40` |
| `VER-019` | "Snapshots de OpenAPI e schema passam contra o produtor novo" | Os dois testes de OpenAPI verificam apenas exposição e ambiente, e nenhum lê o corpo do documento | `OpenApiDocumentExposureTests.cs:27-50`; `OpenApiDocumentEnvironmentTests.cs:32-45` |
| `VER-020` | "Relatório registra payload, heap, working set, alocação, GC, CPU, I/O, latência, throughput, backlog, limpeza e igualdade de digest" | O runner de desempenho, o formato de relatório e a pasta de baselines existem, mas não há braço comparativo entre buffer, streaming e spool | `tests/Platform.PerformanceTests/Program.cs:81-113` |

A consequência é de ordenação, não de esforço. Cada um desses artefatos precisa ser criado **a partir do comportamento vigente e antes** da mudança que deveria verificar. Criá-lo depois congela o comportamento novo como se fosse a linha de base, e o oráculo ratifica exatamente aquilo que deveria questionar. Para `VER-008` isso é irreversível na prática, porque depois da alteração da forma canônica o "antes" não é reconstruível.

`VER-020` é o caso mais brando: o runner e o portão já existem e o que falta é um braço, o que a sonda de contenção mostra ser extensível sem reescrita (`RF-023`).

## 5. Riscos e mitigações

| Risco | Severidade | Mitigação |
|---|---|---|
| Adicionar o manifesto ao hash sem baseline congelado torna a regressão de `ER-008` indetectável (`RF-001`) | `CRITICAL` | Congelar vetores dourados literais do hash de ingestão a partir de `HEAD` antes de qualquer alteração da forma canônica, no formato já usado em `tests/Platform.UnitTests/TemplateManagement/CanonicalHashTests.cs:47` |
| Aceite confirmado com claim apenas reservado (`RF-002`) | `CRITICAL` | Promover a variante da seção 4.1: confirmação acrescentada na mesma transação do aceite, expiração como única compensação |
| Lista vazia produz hash diferente de membro ausente (`RF-003`) | `HIGH` | Fixar a regra de omissão na ausência e no vazio como parte da decisão "Igualdade do manifesto idempotente", com corpus contratual antes de `SEED-006` |
| Claim depois do append de auditoria estende a posse do bloqueio global do mês (`RF-004`) | `HIGH` | Fixar por contrato a posição do claim antes do append, pela mesma regra já escrita para a outbox, e acrescentar um braço à sonda de contenção existente |
| Varredura de abandonados apaga objeto reservado (`RF-005`) | `HIGH` | Qualificar reserva como dependência ativa em `ER-013` antes de habilitar limpeza automática, que já é gate de `SEED-011` |
| Implantação progressiva transforma replay legítimo em conflito (`RF-006`) | `HIGH` | Regra de omissão como invariante dura, mais o ensaio de versões mistas de `VER-021` antes de publicar escritores |
| Segunda conexão concorrente por aceite esgota o pool único (`RF-007`) | `HIGH` | Recusar qualquer variante que abra segunda conexão; se a transação compartilhada for promovida, ela precisa correr sobre a conexão do chamador |
| Topologia física do banco não provada (`RF-008`) | `MEDIUM` | Decidir a topologia antes de admitir a transação compartilhada como alternativa viável do gate de `SEED-005` |
| Tentativa em `unknown` mantém dependência ativa por até um dia (`RF-009`) | `MEDIUM` | Incluir `sending` e `unknown` na definição de dependência ativa; revisar o intervalo de reconciliação se o descarte exigir horizonte menor |
| Manifesto no envelope de conteúdo seria descartado após o veredito (`RF-010`) | `MEDIUM` | Coluna própria no nível da notificação; na tentativa, no máximo um digest |
| Binder Kafka sem leitor de array de objetos (`RF-011`) | `MEDIUM` | Leitor novo com a disciplina dos existentes, ou manifesto como lista de strings opacas, decidido junto da versão contratual de `SEED-006` |
| Manifesto sem cota superior de cardinalidade e tamanho (`RF-012`) | `MEDIUM` | Publicar o teto como parâmetro aprovado antes de aceitar anexos, que já é gate de `SEED-008`, e impô-lo no validador no molde do teto de `ChannelsHint`. O repositório já sabe limitar entrada de cardinalidade escolhida por terceiro: `ProviderWebhookIngestion` declara duas cotas (`FT-51`, `RF-025`) |
| Funções de adequação permitem a transação compartilhada sem medir seu custo (`RF-024`) | `MEDIUM` | Tratar a escolha como ADR explícito e não delegá-la à ausência de reprovação automática; nenhuma regra vigente reprovaria o caminho mais caro |
| Cinco oráculos declarados sem artefato de comparação (`RF-026`) | `CRITICAL` | Criar cada artefato a partir do comportamento vigente antes da mudança que ele verifica, e tratar essa precedência como aresta de dependência no PLAN, não como tarefa de teste no fim da fatia |
| Migração escrita sem regenerar o `ModelSnapshot` (`RF-027`) | `MEDIUM` | Gerar a migração pela ferramenta, nunca editar apenas o arquivo de migração |
| Módulo novo sem seção base derruba toda a suíte de integração (`RF-028`) | `MEDIUM` | Acrescentar a seção com valores neutros no mesmo commit do registro do módulo |
| Record publicado novo com coleção reprova o inventário de igualdade (`RF-029`) | `MEDIUM` | Decidir entre igualdade por conteúdo e registro da quebra na mesma mudança; a reprovação é o mecanismo funcionando |
| Dead-letter reemite o corpo original fora da lista de redação (`RF-030`) | `MEDIUM` | Confirmar a opacidade da referência por contrato e cobrir a superfície pela varredura de sentinelas de `VER-012` |
| Fan-out de push copia campo a campo (`RF-031`) | `MEDIUM` | Se o manifesto for por tentativa, cobrir o fan-out com asserção por irmão. A coluna no nível da notificação elimina o risco na origem |
| Motivo de rejeição novo sem linha no guia do produtor (`RF-032`) | `MEDIUM` | Atualizar o guia no mesmo commit do motivo novo, incluindo a revisão da proibição vigente de anexos |
| Convergência afirmada sobre instrumento inexistente (`RF-013`) | `MEDIUM` | Oráculos de `PAC-010` definidos sobre estado durável |
| Isolamento travado por contrato de terceiro, com recusa após o bloqueio já tomado (`RF-014`) | `MEDIUM` | Fechar a janela TOCTOU por `UPDATE ... WHERE` com contagem de linhas, nunca por elevação de nível de isolamento |

Nenhum achado `CRITICAL` permanece sem mitigação nomeada, e nenhum risco `HIGH` permanece sem plano.

## 6. Checklist de atualização de requisitos

As linhas `RUC-01` a `RUC-05`, `RUC-08` e `RUC-09` foram aplicadas aos requisitos aprovados nesta invocação, após aprovação explícita do usuário no checkpoint do G3, e estão marcadas como `aplicado`. A análise em si permaneceu somente leitura: nenhuma edição ocorreu antes dessa aprovação. `RUC-06` e `RUC-07` recaem sobre documentos de fronteira e de produto, não sobre requisitos, e seguem abertos para a fatia que os tocar.

| # | Alvo | Ação | Origem |
|---|---|---|---|
| `RUC-01` (aplicado) | `03-verification-plan.md`, `VER-008` e `VER-009` | Declarar o congelamento do baseline de vetores dourados a partir de `HEAD` como pré-condição do oráculo. Sem isso, o critério "zero divergência antes e depois" é inverificável, porque o "antes" não existe. | `RF-001` |
| `RUC-08` (aplicado) | `03-verification-plan.md`, `VER-012`, `VER-015`, `VER-019` e `VER-020` | Declarar, em cada linha, que o artefato de comparação é criado a partir do comportamento vigente antes da mudança verificada. Para `VER-015`, registrar que a regra vigente não cobre `Amazon` e precisa passar por prova de mutação. | `RF-026` |
| `RUC-09` (aplicado) | `01-development-specification.md`, seção 6, "Stack e restrições" | Registrar que a proibição explícita de anexos no guia do produtor é superfície de mudança da capacidade, e não apenas o limite de 256 KB. | `RF-032` |
| `RUC-02` (aplicado) | `01-development-specification.md`, seção 8, decisão "Consistência do claim" | Acrescentar a terceira alternativa às "Alternativas consideradas". As duas registradas hoje não são promovíveis como escritas, e o gate de `SEED-005` só poderia ser fechado promovendo algo que o registro não contém. | `RF-002` |
| `RUC-03` (aplicado) | `01-development-specification.md`, `ER-013` | Qualificar se uma reserva conta como dependência ativa, ou fazer o pré-requisito externo de `SEED-011` nomeá-la explicitamente. | `RF-005` |
| `RUC-04` (aplicado) | `01-development-specification.md`, seção 8, decisão "Igualdade do manifesto idempotente" | Registrar que lista vazia e membro ausente precisam produzir a mesma forma canônica, como eixo do corpus contratual. | `RF-003` |
| `RUC-05` (aplicado) | `01-development-specification.md`, `ER-010` ou seção 9 | Registrar que uma tentativa em `sending` ou `unknown` mantém dependência ativa, com o horizonte de resolução da reconciliação. | `RF-009` |
| `RUC-06` (aberto) | `src/Platform.Api/Modules/Notifications/AGENTS.md:68-77` | Atualizar a invariante transacional de quatro para seis escritas se a variante recomendada for promovida. Documento de fronteira do módulo, não requisito: `no requirements change`. | seção 4.1 |
| `RUC-07` (aberto) | `docs/guia-integracao-produtor.md:215-216` | Revisar a proibição explícita de anexos no registro Kafka. Documento de produto, não requisito: `no requirements change`. | `FT-49` |

`ER-006`, `ER-008`, `ER-009` e `ER-012` não exigem mudança de texto: `no requirements change`.

## 7. Validação necessária

| O que provar | Como | Onde |
|---|---|---|
| O hash de um corpo sem anexos é idêntico antes e depois da mudança | Congelar literais de 64 hexadecimais para um corpo mínimo e um corpo com todos os opcionais, gerados em `HEAD`, e reexecutar após a alteração | `tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs` |
| Lista vazia e membro ausente produzem o mesmo hash | Caso de unidade dedicado, comparando os dois corpos | mesmo arquivo |
| Falha em cada efeito da transação de aceite não deixa notificação aceita sem claim | Dublês que lançam em `IAuditTrail`, no primeiro e no segundo append de outbox, e no `SaveChangesAsync` | `tests/Platform.IntegrationTests`, no padrão de `RequestNotificationTransactionTests.cs:15-48` |
| Reserva sem aceitação converge e nunca produz anexo enviável | Injetar falha após a reserva e antes do commit, avançar o relógio além do prazo e verificar estado durável com o contador do provedor falso em zero | `tests/Platform.IntegrationTests`, com PostgreSQL e LocalStack |
| Reentrega no caminho Kafka entre os dois commits não duplica reserva nem claim | Encerrar o host entre os commits de `IngressCommitWriter.cs:38-50` e reentregar | `tests/Platform.IntegrationTests` |
| Corrida de chave idempotente devolve replay com manifesto igual e conflito sem efeito com manifesto diferente | Duas requisições concorrentes atravessando `IngestionWriter.cs:60-73` | `tests/Platform.IntegrationTests/Notifications/` |
| Claims concorrentes do mesmo conjunto produzem exatamente um vencedor | Formato de `DispatchClaimRaceTests.cs:15-56`, com dois escopos e `Task.WhenAll` | `tests/Platform.IntegrationTests` |
| Retry e fallback submetem exatamente o conjunto aceito | Alterar o estado do anexo entre o aceite e o fallback e comparar o conjunto capturado no fio com o snapshot persistido | `tests/Platform.IntegrationTests`, com fake HTTP do SendGrid |
| O claim antes do append não move a posse do bloqueio de cadeia | Braço adicional em `ContentionArms.Build` com a mistura real acrescida do passo de claim | `tests/Platform.PerformanceTests`, contra a linha de base 5,0553 |
| O mesmo passo depois do append degrada de forma mensurável | Braço gêmeo, idêntico exceto pela ordem | mesmo runner. Se não houver degradação, a regra de ordenação perde fundamento e precisa ser revista, não copiada |
| Um claim que eleve o nível de isolamento é recusado antes de qualquer efeito | Abrir a transação em `RepeatableRead` e chamar o caminho de aceite | `tests/Platform.IntegrationTests` |
| A variante compartilhada não abre segunda conexão | Contagem de conexões do pool durante o aceite | `tests/Platform.IntegrationTests` |
| A dependência do novo módulo permanece dentro de `Integration.V1` | Regra vigente, sem edição | `tests/Platform.ArchTests/ArchitectureTests.cs:66-93` |
| O `Domain` de qualquer módulo não depende de `Amazon.*` | Acrescentar o namespace à lista proibida e provar por mutação que reprova | `tests/Platform.ArchTests/ArchitectureTests.cs:33-40` |

### Impacto em testes

`imutável` significa que o teste não pode mudar de resultado; se ele mudar, a alteração é regressão até prova em contrário.

| Caminho de teste | Ação | Justificativa |
|---|---|---|
| `tests/Platform.UnitTests/Notifications/RequestPayloadHashTests.cs` | caso novo, **antes** da mudança | Congelar vetores literais do estado vigente e só então acrescentar ordem, duplicata, lista vazia e presença do manifesto |
| `tests/Platform.UnitTests/Notifications/CanonicalJsonTests.cs` | imutável | A canonicalização recursiva não muda |
| `tests/Platform.UnitTests/Notifications/Ingress/IngressRequestBinderTests.cs` | caso novo | Leitor novo: ausente, `null`, tipo errado, elemento inválido |
| `tests/Platform.IntegrationTests/Notifications/RequestNotificationIdempotencyTests.cs` | caso novo | Matriz de replay e conflito com manifesto |
| `tests/Platform.IntegrationTests/Notifications/RequestNotificationTransactionTests.cs` | caso novo | Uma falha injetada por efeito, incluindo o claim |
| `tests/Platform.IntegrationTests/Notifications/AppendOrderProbe.cs` | ajuste | A ordem interna ganha um passo: a confirmação precede o append de auditoria |
| `tests/Platform.IntegrationTests/Ingress/KafkaIngressCheckOrderTests.cs` | imutável | A ordem dos gates não pode mudar |
| `tests/Platform.IntegrationTests/Dispatching/RenderedContentRetentionTests.cs` | ajuste | Afirmar que o descarte da forma completa não alcança o manifesto |
| `tests/Platform.IntegrationTests/Dispatching/DispatchPushFanOutTests.cs` | ajuste condicional | Só se o snapshot for por tentativa |
| `tests/Platform.IntegrationTests/Dispatch/SendGridProviderContractTests.cs` | ajuste e caso novo | O `DispatchRequest` compartilhado muda de forma mesmo com valor padrão |
| `tests/Platform.ArchTests/ArchitectureTests.cs` | caso novo | Regra de `Amazon.*` no `Domain` e regra que impede `Dispatch` de alcançar o módulo novo |
| `tests/Platform.ArchTests/PublishedContractEqualityTests.cs` | ajuste | Reprovação esperada até reparo ou registro da quebra |
| `tests/Platform.ArchTests/ProducerGuideCatalogTests.cs` | imutável | Verde apenas se cada motivo novo ganhar linha no guia |
| varredura com sentinelas (arquivo novo) | criar | `VER-012` não tem artefato hoje |
| snapshot de OpenAPI e schema (arquivo novo) | criar, **antes** da mudança de contrato | `VER-019` não tem lado anterior |
| braço comparativo em `tests/Platform.PerformanceTests/` | criar | `VER-020` não tem braço; o runner e o portão já existem |

## 8. Decisões que exigem ADR

| Decisão | Por que um ADR | Alternativas | Condição de revisão |
|---|---|---|---|
| Protocolo de consistência do claim | Altera a costura de escrita do módulo, coloca contrato de outro contexto no caminho de aceitação e fixa a garantia de que produtores passam a depender. Reverter exige migrar reservas em voo, com custo crescente no volume aceito. | Transação compartilhada por contrato; reserva com compensação posterior; reserva com confirmação carregada pela transação de aceite | A topologia física do banco do novo módulo mudar, ou o limite transacional da ingestão mudar |
| Local e forma do snapshot do manifesto aceito | Decisão de esquema sobre tabela pai particionada lida por três consumidores independentes. Mover depois exige backfill por partição e leitor tolerante a duas formas simultâneas. A decisão equivalente no mesmo módulo foi registrada com justificativa própria na migração. | Coluna `jsonb` na notificação; tabela filha particionada; cópia por tentativa | O manifesto passar a exigir estado mutável por item, ou um canal adicional entrar na primeira produção |
| Entrada do manifesto na forma canônica idempotente | O hash é contrato publicado com produtores. Após a mudança, todo `payload_hash` gravado pertence à semântica anterior e não existe rollback que preserve as duas. É a decisão menos reversível do recorte e hoje não tem baseline que a proteja. | Incluir o manifesto no hash com omissão na ausência e no vazio; manter o hash intacto e resolver igualdade em verificação separada | Uma propriedade de entrega for adicionada, ou uma versão contratual for promovida |

Permanecem legitimamente `inline`, por serem aditivas ou trocáveis sem migração: o prazo da reserva, o digest do manifesto na tentativa, o nome do tipo de evento de confirmação e a ordem relativa dos dois appends de outbox.

## 9. Inferências, premissas e lacunas de evidência

### Inferências

- Uma reserva precisa contar como dependência ativa. Nada no repositório nem na especificação decide isso.
- A mensagem de confirmação do claim, contendo apenas identificadores, satisfaz `ER-007`. A verificação pertence à varredura de sentinelas de `VER-012` e não pode ser presumida.
- A coluna `jsonb` do manifesto cabe no orçamento de linha sem pressionar o TOAST, por analogia com `admitted_plan`. Não medido.
- O membro omitido na ausência preserva byte a byte os hashes existentes. A leitura do escritor canônico sustenta a conclusão, mas sem vetores congelados isso é raciocínio sobre o código, não medição.

### Premissas

- `AttachmentManagement` será provisionado sobre a mesma instância física de PostgreSQL. Se falsa, a transação compartilhada deixa de existir como alternativa.
- O relay da outbox entrega a confirmação ao consumidor dentro de um atraso menor que o prazo da reserva. Nenhum número existe hoje para sustentar a comparação.

### Lacunas de evidência

| Lacuna | Consequência |
|---|---|
| Topologia física do banco do novo módulo não decidida em nenhuma fonte aprovada | Decide se a transação compartilhada é sequer possível |
| Privilégios de role por schema não observáveis: as strings usam um único usuário | A escrita entre schemas por transação bruta pode exigir concessão que não está declarada |
| Cardinalidade e tamanho máximo do manifesto não publicados | Custo do snapshot, do hash e do claim indivisível sem cota superior |
| Forma do estado de `AttachmentManagement` inexistente | Impede afirmar qual predicado fecharia a janela TOCTOU entre verificação de liberação e claim |
| A sonda de contenção não foi executada nesta invocação, por ser somente leitura | Toda projeção sobre o efeito do claim na posse do bloqueio é hipótese, não achado |
| Baseline de latência do relay até o consumo inexistente, e sem telemetria que a produza | Impede dimensionar o prazo da reserva |
| Vetores dourados do hash de ingestão inexistentes | O "antes" de `VER-008` não é reconstruível após a mudança sem congelamento prévio |
| Validade da liberação, prazo admissível e regra de descarte pendentes nos gates de `SEED-004`, `SEED-008` e `SEED-011` | Condicionam parâmetros do protocolo recomendado |
| `tests/Platform.SecurityArchTests/SecurityArchitectureTests.cs` não foi inventariado por completo | Pode haver regra adicional disparada pelo membro novo do endpoint |
| `tests/Platform.PerformanceTests/Scenarios/` e `Gate/` não foram inventariados | Não se sabe quanto do runner é reutilizável para `VER-020` |
| Custo de canonicalização de um manifesto no hash não medido | O número conhecido para metadado não transfere para um formato inexistente |
| Envelope efetivo do provedor depois de base64 e JSON não medido nem declarado em nenhum arquivo | Condiciona `ER-010` e `ER-016`, fora deste recorte |
| Nenhum pacote de validação de conteúdo, antimalware ou detecção de tipo efetivo existe no gerenciamento central | A seleção está condicionada à política executável do gate de `SEED-004` |

## 10. Receipts das contribuições

| Contribuição | Fase | Status | Observação |
|---|---|---|---|
| `dotnet-impact-analysis-profile` | `evidence` | `READY` | Vinculou arquitetura e engenharia; a especialidade foi ativada por evidência de internals de EF Core, transação e outbox |
| `dotnet-stack-profile` | `prepare` | `READY` | Perfil manual consumido sem sobrescrita |
| `dotnet-system-design` | `execute` | `PARTIAL` | Três lentes em paralelo. Arquitetura `PARTIAL`, porque duas entradas de gate abertas impedem fechar a decisão de `SEED-005`; engenharia `READY`; especialidade `READY` |
| `dotnet-refine-unknowns-validator` | `verify` | `PASS` | Ver seção 11 |

Uma nota de processo que afeta a leitura dos receipts: o relatório da lente de engenharia foi truncado pela camada de persistência e precisou ser reemitido em duas partes. Um pedido de reemissão foi endereçado por engano à lente de especialidade, que recusou corretamente por estar fora da sua fronteira de autoridade e devolveu apenas o que era seu. Nenhum conteúdo foi reconstruído por inferência para cobrir a falha.

As afirmações mais determinantes foram reverificadas de forma independente pelo orquestrador nesta invocação: ausência de literal de 64 hexadecimais nos testes de `Notifications`, escrita de `channelsHint: []` para lista não nula e vazia, mapeamento da coluna `admitted_plan`, identidade textual das seis connection strings, ausência de `Amazon` na lista de namespaces proibidos ao `Domain`, precedente de cota do callback de provedor, fixação transitiva de `Npgsql`, textos de `VER-008`, `VER-009`, `VER-012`, `VER-015`, `VER-019` e `VER-020`, texto de `ER-013` e os parâmetros de reconciliação.


## 11. Validação determinística

| Verificação | Comando | Resultado |
|---|---|---|
| Informação sem suporte persistida | `check-document-unknowns.mjs docs/SPEC-001/refinements/` | `PASS` |
| Regras de escrita pt-BR | `check-writing-rules.py --mode markdown --strict` | `PASS` |
| XML do SVG | `xml.dom.minidom.parse` | `PASS` |
| SVG órfão | Comparação entre o diretório `diagrams/` e as referências deste documento | `PASS`, um arquivo e uma referência |
| Limite visual do estágio | Protocolo de enriquecimento visual, máximo de dois SVGs novos | `PASS`, um SVG novo |
| Conformidade de estilo do SVG | Raiz apenas com `viewBox`, sem largura ou altura fixas, sem travessão | `PASS` |

O validador de informação sem suporte reprovou duas vezes antes de passar. Ambas as ocorrências eram falso positivo: um dos padrões de sentinela do script casa com uma locução verbal comum do português, formada pela preposição mais o verbo confirmar, que ali não marcava informação pendente. As frases foram reescritas para liberar a verificação, e a limitação fica registrada porque é da mesma classe do falso positivo com o marcador de tarefa em maiúsculas que o próprio script já documenta em comentário.
