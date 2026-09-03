# Delivery Slice: Gestão de anexos do Notification Hub

## Metadados

| Campo | Valor |
|---|---|
| ID | SLICE-001 |
| Título | Gestão de anexos do Notification Hub |
| Prioridade | P0 |
| Dependências | nenhuma |
| Esforço | 190 SP / 928h |
| Responsável final | Tech Lead |
| Executor responsável | Senior Dev |
| Consultados | Segurança, QA, Operações, Product Owner, Compliance |
| Data | 2026-08-31 |
| Situação | In Progress |
| Tipo | Feature |
| Criticidade | High |
| Adaptador | dotnet |
| Natureza | product |
| Executor | pair |
| Versão do contrato de evidência | v2 |

Esta Delivery Slice concentra, por decisão explícita do responsável pelo backlog, as 12 sementes do mapa de implementação em um único item. A dobra está declarada na seção `## Cobertura do mapa de implementação`, e a ordenação que seria expressa por arestas entre fatias passa a viver na dependência entre tarefas.

## Título

Permitir que aplicações autorizadas associem anexos liberados a notificações por e-mail, com custódia sob o hub, claim indivisível, conjunto preservado até o provedor e evidência reconstruível.

## Problema ou oportunidade

O Notification Hub não aceita anexos. Quando uma jornada exige comprovante ou documento, a aplicação produtora omite o arquivo ou entrega fora da capacidade central, e o produto perde a aplicação uniforme de isolamento, validação, integridade e evidência. O custo de não fazer é duplo: a jornada continua incompleta para o destinatário, e cada aplicação que resolve por fora cria uma custódia própria, sem gate de liberação e sem trilha que relacione os bytes validados aos bytes submetidos ao provedor.

Existe ainda um custo já observável no repositório. O plano de verificação aprovado declara oráculos que pressupõem artefatos de comparação inexistentes, entre eles os vetores dourados do hash de idempotência. Enquanto esses artefatos não existirem, a suíte permanece verde mesmo que a forma canônica mude por inteiro, e a compatibilidade do caminho sem anexos deixa de ser verificável.

## Resultado esperado

- Uma aplicação autorizada registra o arquivo, realiza upload gerenciado pelo hub, acompanha a validação por referência opaca e usa essa referência em uma notificação somente após a liberação.
- Uma solicitação que cite referência pendente, rejeitada, revogada, vencida, inexistente ou pertencente a outra aplicação é recusada antes de existir notificação aceita.
- Uma tentativa bem-sucedida submete ao provedor exatamente o conjunto aceito, e a evidência demonstra correspondência entre os bytes validados e os bytes submetidos.
- Produtores existentes continuam solicitando notificações sem anexos por REST e Kafka, com hash de idempotência, recusas, eventos e seleção de canal idênticos ao comportamento vigente.
- Falha parcial em upload, validação, liberação ou claim converge para estado recuperável ou descarte conhecido, sem jamais produzir anexo utilizável sem validação.
- Uma consulta autorizada reconstrói aplicação, anexo, integridade, validação, notificação, tentativa e resposta do provedor sem depender de conteúdo bruto em log, evento ou auditoria comum.

## Usuários ou atores afetados

- Aplicação produtora, que passa a ter um fluxo em etapas: registrar, enviar, aguardar liberação, solicitar a notificação.
- Destinatário, que recebe a tentativa com o conjunto solicitado ou não recebe nada, sem degradação silenciosa.
- Área operacional autorizada, que investiga estado, falha e evidência sem acesso ao conteúdo bruto.
- Engenharia dos módulos `Notifications`, `Dispatch` e do novo contexto, além de Segurança e Compliance na custódia e na minimização.

## Escopo

- Novo contexto delimitado responsável por referência opaca, custódia, identidade íntegra, validação, liberação, revogação, dependência ativa, recuperação e descarte.
- Contrato publicado e versionado que permite ao módulo de notificações reivindicar o conjunto integral e obter um snapshot imutável.
- Membro opcional de referências no comando de ingresso, presente nos dois transportes, incorporado à forma canônica idempotente com omissão na ausência e no vazio.
- Snapshot imutável do manifesto aceito, persistido junto da notificação e lido por pipeline, dispatching e fallback sem consultar estado mutável.
- Revalidação de liberação, identidade e envelope imediatamente antes do ponto irreversível de cada submissão.
- Representação neutra de provedor e armazenamento no contrato de despacho, e composição do conjunto dentro do adaptador do provedor de e-mail.
- Terminação explícita quando o plano de entrega não puder preservar o conjunto, sem conversão para link e sem remoção de anexos.
- Reconciliação, preservação enquanto houver dependência ativa, descarte seguro e evidência operacional minimizada.
- Migração inicial esmagada, habilitação progressiva e rollback lógico. Não há cliente antigo nem item já aceito a preservar, porque o serviço é novo e não tem nada em produção.
- Artefatos de comparação que hoje faltam para os oráculos do plano de verificação, criados a partir do comportamento vigente antes de qualquer mudança que eles verifiquem.

## Limites de implementação

O executor `dotnet-implementation` pode escrever somente nos caminhos declarados abaixo. Os globs admitem apenas arquivos novos dentro da raiz indicada. Uma superfície de descoberta começa como somente leitura e só entra no conjunto autorizado de escrita quando a regra de promoção produzir evidência registrada no `progress.json` da fatia. Qualquer outro caminho exige novo checkpoint de escopo.

### Arquivos a criar

| Limite permitido | Regra determinística |
|---|---|
| `src/Platform.Api/Modules/AttachmentManagement/**/*.cs` | Criar somente tipos necessários ao contexto, à custódia, à autorização, à validação, ao claim, à reconciliação, ao descarte e à habilitação. Manter o domínio livre de infraestrutura e publicar contratos entre contextos somente em `Integration/V1`. Não criar projeto separado. |
| `src/Platform.Api/Modules/AttachmentManagement/README.md` | Documentar somente as convenções do novo módulo depois que as fronteiras implementadas forem verificadas. |
| `src/Platform.Api/Modules/Notifications/Domain/*Manifest*.cs` | Criar exatamente o tipo imutável adotado para o snapshot aceito, sem identificador de SPEC, Delivery Slice ou critério no nome ou no conteúdo. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/**/*.cs` | Acrescentar o membro de manifesto ao contrato publicado único, reutilizando o caso de uso compartilhado e sem duplicar regra de negócio. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Reads/NotificationEvidenceReader.cs` | Criar somente o leitor autorizado que reconstrói a evidência da notificação, incluindo a projeção minimizada do snapshot aceito. |
| `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationEvidence.cs` | Criar somente o contrato publicado de evidência da notificação, sem conteúdo bruto nem detalhes de armazenamento. |
| `src/Platform.Api/Modules/**/Infrastructure/Persistence/Migrations/*.cs` | Admitir a migração inicial esmagada que a ferramenta gerar por contexto, substituindo a cadeia existente. Não admitir edição manual no par gerado, e não aplicar a cerimônia de tabela viva, que fica suspensa. |
| `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Persistence/Migrations/*.cs` | Admitir somente os arquivos novos que a ferramenta EF gerar nesta tarefa para o schema do novo contexto. Registrar os nomes exatos antes da promoção e não admitir migração destrutiva nem alteração de contexto alheio. |
| `src/Platform.Api/Modules/Dispatch/Integration/V1/*Attachment*.cs` | Criar somente o contrato publicado da representação neutra do conjunto, sem tipo de provedor ou armazenamento. |
| `tests/Platform.UnitTests/Notifications/*PayloadHash*Tests.cs` | Criar ou complementar um único arquivo de vetores dourados que congele o hash sem anexos e cubra manifesto ausente, vazio e preenchido. |
| `tests/**/AttachmentManagement/**/*Tests.cs` | Criar testes apenas no projeto existente selecionado pela regra de descoberta, seguindo xUnit, NSubstitute e Shouldly. |
| `tests/**/*Attachment*Contract*Tests.cs` | Criar somente snapshots ou testes contratuais do corpo HTTP, schema Kafka e contrato publicado alterados. |
| `tests/**/*MixedVersion*Tests.cs` | Criar somente o ensaio de compatibilidade entre produtores e consumidores com e sem o membro opcional. |
| `tests/Platform.UnitTests/Notifications/*Manifest*Tests.cs` | Criar os testes unitários da matriz normativa V1, da imutabilidade e da leitura presente, ausente e ilegível. |
| `tests/Platform.IntegrationTests/Notifications/*Manifest*Tests.cs` | Criar os testes integrados da persistência `jsonb`, da captura do `INSERT`, da ausência da coluna em `UPDATE` posterior e da falha anterior ao SQL quando o snapshot é alterado. |
| `tests/**/*Attachment*Evidence*Tests.cs` | Criar somente os testes da consulta e do contrato de evidência reconstruível, incluindo a projeção minimizada do snapshot aceito. |
| `tests/Platform.PerformanceTests/Scenarios/AttachmentTransferMethodScenario.cs` | Criar somente o cenário comparativo entre buffer, streaming e spool, usando o mesmo corpus e envelope em todos os braços. |
| `tests/Platform.PerformanceTests/Gate/AttachmentTransferMethodBaseline.cs` | Criar somente o modelo versionado da linha de base do cenário de transferência. |
| `tests/Platform.PerformanceTests/Gate/AttachmentTransferMethodGate.cs` | Criar somente o portão relativo que compara braços da mesma rodada e a linha de base aprovada. |
| `tests/Platform.PerformanceTests/baselines/attachment-transfer-method.json` | Registrar somente a linha de base produzida pelo runner aprovado, sem números fabricados ou copiados de outro host. |

### Arquivos a modificar

| Caminho | Limite da alteração |
|---|---|
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs` | Acrescentar membro opcional `init`, sem mudar a posição do construtor primário. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs` | Quando o membro estiver presente, validar referências, unicidade ordinal e itens sem normalizar a entrada. Membro ausente e `null` são legais e significam ausência de anexos; somente lista vazia é recusada. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs` | Inserir o manifesto na ordem canônica aprovada; `null` e coleção vazia significam ausência; preservar os bytes do caminho sem anexos. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs` | Integrar o claim integral somente depois do gate de consistência. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Response.cs` | Acrescentar somente resultados de recusa aprovados. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs` | Preservar o binding V1, recusar especificamente `attachments` e manter a tolerância vigente aos demais membros desconhecidos. |
| `src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs` | Ler o membro novo sem alterar a ordem vigente dos gates. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/KafkaIngressOptions.cs` | Acrescentar somente a configuração necessária ao transporte do membro no tópico único. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/KafkaIngressTopicMap.cs` | Associar tópico, produtor lógico e versão contratual sem enfraquecer a validação dos bindings. |
| `src/Platform.Api/Modules/Notifications/KafkaIngressWorkerRole.cs` | Registrar o consumo do tópico único sem duplicar o caso de uso. |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | Mapear a rota de escrita única que transporta o manifesto. |
| `src/Platform.Api/Program.cs` | Preservar autenticação, limitação de taxa e o documento OpenAPI único, com a nomeação de schema qualificada por módulo. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IngestionWriter.cs` | Integrar o claim na transação compartilhada antes da auditoria, iniciando explicitamente em `READ COMMITTED` e sem segunda conexão. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IIngestionSink.cs` | Alterar somente a costura exigida pelo protocolo de claim transacional aprovado. |
| `src/Platform.Api/Modules/Notifications/Domain/Notification.cs` | Persistir o snapshot aceito segundo o molde do domínio existente. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Configurations/NotificationConfiguration.cs` | Mapear a coluna `jsonb` anulável e configurar `AfterSaveBehavior.Throw` para impedir alteração rastreada depois da criação. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/NotificationsDbContextModelSnapshot.cs` | Regenerar pela ferramenta EF, nunca editar à mão. |
| `src/Platform.Api/Modules/Notifications/Features/Pipeline/NotificationContext.cs` | Acrescentar o slot do snapshot no molde existente. |
| `src/Platform.Api/Modules/Notifications/Features/Dispatching/DispatchMessageProcessor.cs` | Fazer preflight depois do claim e antes da chamada irreversível, preservando redelivery. |
| `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs` | Ler o snapshot exclusivamente da notificação já carregada ao enfileirar a próxima tentativa, sem cópia persistida em tentativa, outbox ou mensagem. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/AttemptDispatchWriter.cs` | Preservar o claim otimista e impedir que o descarte da tentativa alcance o snapshot da notificação, sem persistir nem propagar cópia do manifesto. |
| `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs` | Acrescentar somente motivos aprovados, sem convertê-los em `PayloadInvalid`. |
| `src/Platform.Api/Modules/Dispatch/Integration/V1/DispatchRequest.cs` | Acrescentar membro opcional com default compatível. |
| `src/Platform.Api/Modules/Dispatch/Providers/SendGrid/SendGridMailRequest.cs` | Representar o conjunto somente na forma exigida pelo contrato aprovado. |
| `src/Platform.Api/Modules/Dispatch/Providers/SendGrid/SendGridChannelProvider.cs` | Submeter o conjunto integral pelo método promovido e preservar `text/plain` antes de `text/html`. |
| `src/Platform.Api/appsettings.json` | Acrescentar seção-base sem segredo nem parâmetro produtivo ainda não aprovado. |
| `docs/guia-integracao-produtor.md` | Revisar a proibição vigente, documentar o membro opcional e acrescentar os motivos de recusa aprovados. |
| `tests/Platform.ArchTests/ArchitectureTests.cs` | Acrescentar somente o namespace do provedor de nuvem à regra vigente de domínio livre de tecnologia e sua prova de falsificação. |
| `tests/Platform.PerformanceTests/Program.cs` | Registrar somente o novo modo, a execução do cenário e a chamada do portão de transferência. |
| `tests/Platform.PerformanceTests/ProbeSettings.cs` | Acrescentar somente as opções delimitadas de corpus, envelope, braços e linha de base do novo cenário. |
| `tests/Platform.PerformanceTests/Reporting/ProbeOutcome.cs` | Acrescentar somente os resultados tipados produzidos pelo cenário de transferência. |
| `tests/Platform.PerformanceTests/Reporting/ReportRenderer.cs` | Renderizar somente as grandezas e comparações exigidas pela tarefa 5. |

### Superfícies delimitadas de descoberta

| Superfície inicialmente somente leitura | Regra de promoção |
|---|---|
| `src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/RouteStage.cs`; `src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/ChannelSelectionRule.cs` | Inspecionar somente os dois candidatos. Promover o arquivo que contém a seleção vigente; promover ambos somente se a chamada direta demonstrar que regra e orquestração transportam o resultado novo. |
| `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IngressCommitWriter.cs` | Preservar a confirmação antes da marca de deduplicação. Promover somente se a alteração for indispensável ao protocolo aprovado. |
| `src/Platform.Api/Modules/Notifications/Domain/CanonicalJson.cs`; `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Admission.cs`; `src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs` | Usar como oráculos somente leitura para ordem canônica, precedência de replay e leitura tolerante. |
| `tests/**/{CanonicalJsonTests.cs,RequestNotificationTransactionTests.cs,AppendOrderProbe.cs,RequestNotificationIdempotencyTests.cs,KafkaIngressCheckOrderTests.cs,DispatchClaimRaceTests.cs,SendGridProviderContractTests.cs,AdmittedDeliveryPlanReadTests.cs}` | Resolver cada basename para exatamente um arquivo. Promover somente o teste cujo comportamento mude; manter os demais como oráculos de regressão. |
| `tests/**/*.csproj` | Identificar projetos existentes de unidade, integração, arquitetura, contrato e desempenho. Não criar projeto, alterar pacote ou modificar `.csproj`. |
| `tests/**/*Performance*.csproj`; `benchmarks/**/*.csproj` | Selecionar somente um runner existente para o braço comparativo. Se nenhum existir, bloquear a tarefa 5 e devolver a lacuna ao PLAN. |
| `tests/**/*{OpenApi,Kafka,Schema,Contract,Snapshot}*` | Localizar o snapshot vigente de API e barramento e promover somente o artefato diretamente afetado pelo membro opcional. |

Permanecem fora do limite a solução, `.csproj`, arquivos de pacotes, novos projetos, mediator, telemetria de plataforma, segredos, parâmetros produtivos sem aprovação e qualquer referência de especificação em código, teste, migração, schema ou configuração.

## Fora de escopo

- Gerar, converter, editar, assinar ou extrair conteúdo de documentos.
- Aceitar localização arbitrária no armazenamento de objetos ou manter custódia na infraestrutura do cliente.
- Receber bytes ou base64 no contrato de solicitação da notificação.
- Entregar anexos por SMS, push ou WhatsApp na primeira produção.
- Substituir anexos automaticamente por links ou por mensagem sem arquivos.
- Gerenciar anexos estáticos de template ou criar portal público de compartilhamento.
- Introduzir telemetria de plataforma, que permanece ausente por decisão vigente e não é pré-requisito desta entrega.

## Cobertura do mapa de implementação

Esta seção existe porque a fatia é única. Ela declara a dobra que substitui as arestas entre fatias e permite verificar que nenhuma semente desapareceu.

| Semente | Onda | Coberta por | Natureza da dobra |
|---|---:|---|---|
| `SEED-001` | 0 | Tarefas 6 a 12 | Dobra: as sete decisões condicionadas viram quatro experimentos e três ADRs. A política executável de validação não tem trabalho de engenharia e permanece como gate interno da tarefa 18, sem relação entre SPECs |
| `SEED-002` | 1 | Tarefas 13 a 15 | Dobra direta |
| `SEED-003` | 1 | Tarefas 16 e 17 | Dobra direta, condicionada ao experimento da tarefa 6 |
| `SEED-004` | 2 | Tarefas 18 e 19 | Dobra direta |
| `SEED-005` | 3 | Tarefas 20 e 21 | Dobra direta, condicionada à prova da tarefa 7 |
| `SEED-006` | 3 | Tarefas 22 a 24 | Dobra direta, condicionada ao corpus da tarefa 8 |
| `SEED-007` | 4 | Tarefas 25 e 26 | Dobra direta |
| `SEED-008` | 4 | Tarefa 27 | Dobra direta |
| `SEED-009` | 4 | Tarefas 28 a 30 | Dobra direta, condicionada à sonda da tarefa 9 |
| `SEED-010` | 4 | Tarefa 31 | Dobra direta |
| `SEED-011` | 5 | Tarefas 32 a 34 | Dobra direta |
| `SEED-012` | 5 | Tarefas 35 a 38 | Dobra direta |

As tarefas 1 a 5 não derivam de semente do mapa. Elas vêm do mapa de impacto consolidado do refinamento, que identificou cinco oráculos declarados sem artefato de comparação, e por isso precedem toda tarefa que altere a forma canônica ou um contrato publicado.

## Proveniência

| Especificação de origem | Requisito de engenharia | Artefato derivado |
|---|---|---|
| SPEC-001 | Preservar o monólito modular e isolar o contexto de anexos como proprietário, acessível somente por contratos publicados e versionados | Mapa de impacto consolidado do refinamento, seção de superfícies afetadas |
| SPEC-001 | Reivindicar o conjunto de forma indivisível e tornar claim e aceite atomicamente consistentes no efeito observável | ADR do protocolo de consistência do claim |
| SPEC-001 | Persistir o snapshot imutável do manifesto aceito e sua identidade íntegra para tentativa, retry, fallback e evidência | ADR do local e forma do snapshot do manifesto aceito |
| SPEC-001 | Incorporar referências e propriedades que alteram a entrega à forma canônica idempotente, preservando exatamente o hash vigente quando não há anexos | ADR da entrada do manifesto na forma canônica idempotente |
| SPEC-001 | Revalidar liberação, identidade e envelope imediatamente antes da chamada ao provedor | Mapa de impacto consolidado do refinamento, seção de validação necessária |
| SPEC-001 | Evoluir o contrato de despacho com uma representação neutra de provedor e armazenamento, submetendo o conjunto integral | Contrato publicado do módulo de despacho, versão vigente |

## Gates internos de decisão

Estes itens governam tarefas dentro da única Delivery Slice. Eles não são dependências entre SPECs nem dependências entre Delivery Slices. A ordenação permanece nas decisões e nas dependências entre tarefas.

| Gate | Evidência produzida por | Autoridade decisória | Tarefas bloqueadas até o registro da decisão |
|---|---|---|---|
| Identidade e proteção do objeto sob custódia | Tarefa 6, experimento de troca de objeto e acesso cruzado; fechado com o candidato de identidade provado e os controles ortogonais de IAM, política de chave e SSE-KMS ainda sem prova | `dotnet-architect` | Liberado por decisão do dono; a Tarefa 16 e seus descendentes herdam a ressalva dos controles não provados |
| Política executável de tipos, conteúdo protegido, antimalware e validade da liberação | Decisão externa à engenharia, verificada depois pelos oráculos de conteúdo hostil e resultado inconclusivo | Produto Notification Hub e `dotnet-architect` | Tarefas 18 e 19 e seus descendentes |
| Protocolo de consistência do claim | Tarefa 7, prova sob falhas injetadas; tarefa 10 registra a alternativa promovida | `dotnet-architect` | Tarefa 10 até a evidência; tarefas 20 e 21 e seus descendentes até o registro |
| Semântica do manifesto, roteamento e estratégia de versão contratual | Tarefa 8, corpus contratual; tarefa 12 registra a forma canônica | Produto Notification Hub e `dotnet-architect` | Tarefa 12 até a decisão; tarefas 22, 23, 24 e 31 e seus descendentes aplicáveis |
| Quantidade, tamanho, tipos e envelope efetivo | Decisão externa à engenharia sustentada pelo cenário da primeira produção | Produto Notification Hub | Tarefas 22, 27 e 29 nas partes que materializam validação, preflight ou envelope; habilitação do aceite |
| Orçamento de recursos e método de transferência | Tarefa 9, comparação entre buffer, streaming e spool com o mesmo corpus e envelope | `dotnet-architect` | Tarefas 29 e 30; tarefas 32 a 34 por transitividade |
| Regra de descarte com dependência ativa | Decisão externa à engenharia, verificada depois pelo teste de preservação do último dependente | Produto Notification Hub | Tarefa 33 |

É proibido editar código de produção em uma tarefa consumidora de gate enquanto a evidência aplicável não estiver concluída e a autoridade indicada não tiver registrado a decisão. As tarefas 6 a 9 podem produzir evidência; os outros três gates dependem de decisão de Produto. Artefatos de decisão são produzidos pelo fluxo de trabalho técnico responsável e permanecem fora do conjunto autorizado de escrita do executor de código até aprovação.

## Critérios de aceitação

- AC-1: Dada uma aplicação autorizada, quando ela registra o arquivo, realiza o upload e acompanha o estado por referência opaca, então a referência só se torna utilizável em uma notificação após a liberação, e nenhuma resposta, contrato, erro ou log revela bucket, chave, URL reutilizável ou credencial.
- AC-2: Dada uma solicitação que cite referência pendente, rejeitada ou inexistente, quando ela é submetida, então é recusada antes de existir notificação aceita, sem registro na fila de despacho e sem chamada ao provedor.
- AC-3: Dada uma tentativa bem-sucedida, quando o provedor recebe a submissão, então o conjunto entregue contém todos os anexos solicitados, e o digest, o comprimento, o nome e o tipo de cada um correspondem aos valores liberados.
- AC-4: Dadas duas aplicações distintas, quando uma tenta consultar ou usar referência pertencente à outra, então o acesso é negado em todas as combinações de principal, aplicação e referência, sem permitir enumeração.
- AC-5: Dado um arquivo infectado, não verificável ou com resultado inconclusivo, quando a validação termina, então o anexo nunca é liberado nem enviado, e o estado final é explícito.
- AC-6: Dado um plano de entrega que não preserva o conjunto completo, quando a rota é avaliada, então o fluxo termina com falha explícita e auditável, sem mensagem degradada em outro canal e sem conversão para link.
- AC-7: Dada uma consulta autorizada, quando ela é executada, então a resposta relaciona aplicação, anexo, integridade, validação, notificação, tentativa e provedor sem depender de conteúdo bruto em log ou evento.
- AC-8: Dado um produtor existente que não usa anexos, quando ele solicita uma notificação por REST ou por Kafka, então o hash de idempotência, as recusas, os eventos e a seleção de canal permanecem idênticos aos vetores congelados do comportamento vigente.
- AC-9: Dada a mesma chave idempotente, quando ela é repetida com as mesmas referências e propriedades, então o resultado original é devolvido; quando uma referência ou propriedade difere, então o resultado é conflito, sem novo efeito.
- AC-10: Dada uma falha parcial em upload, validação, liberação ou claim, quando o sistema se recupera, então o estado converge para recuperável ou para descarte conhecido, e em nenhum instante existe anexo utilizável sem validação nem notificação aceita sem claim integral.
- AC-11: Dado um upload abandonado, quando a varredura de descarte é executada, então ela não remove nem torna indisponível um anexo reivindicado por notificação ativa ou por tentativa em envio ou com resultado desconhecido.
- AC-12: Dadas sentinelas únicas semeadas no arquivo, quando as superfícies produzidas pela suíte são inspecionadas, então nenhuma delas aparece em broker, fila, outbox, dead-letter, log, resposta ou auditoria comum.
- AC-13: Dada uma liberação vencida ou revogada antes da tentativa, quando o preflight executa, então a chamada ao provedor não acontece e o resultado é explícito.
- AC-14: Dado conteúdo com tipo divergente, metadado hostil ou estrutura não inspecionável, quando ele é submetido, então é recusado sem liberar o anexo e sem expor o conteúdo na resposta.
- AC-15: Dado o comportamento vigente antes de qualquer alteração, quando os artefatos de comparação ausentes são criados, então cada um reprova uma mutação deliberada do comportamento que protege, e nenhum deles é criado depois da mudança que deveria verificar.

## Contrato de evidência

| AC | Resultado observável | Oráculo | Estratégia | Verificação rápida | Verificação de limite | Risco | Evidência |
|---|---|---|---|---|---|---|---|
| AC-1 | Registro, upload e acompanhamento por referência opaca, com liberação como porta de uso | Estado consultado e resposta pública mostram a transição esperada; varredura da resposta e do log não encontra bucket, chave, URL ou credencial | adaptive | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | Host real com PostgreSQL e LocalStack exercitando o ciclo externo | medium | pending |
| AC-2 | Recusa antes de notificação aceita | Zero linhas aceitas, zero registros na fila de despacho e zero chamadas ao provedor falso | test-first | teste focado de admissão | suíte de integração de ingresso | high: um vazamento aqui produz entrega não autorizada | pending |
| AC-3 | Conjunto integral submetido com identidade preservada | Captura do JSON transmitido, decodificação de cada base64 e igualdade de digest, comprimento, nome e tipo | contract-first | teste de forma do pedido do provedor | suíte de contrato do provedor com servidor falso | critical: é a promessa central do produto ao destinatário | pending |
| AC-4 | Isolamento entre aplicações | Matriz de principal, aplicação e referência produz zero acesso cruzado e nenhuma resposta distingue ausência de negação | test-first | teste de autorização por recurso | suíte de integração com host real | critical: acesso cruzado expõe documento de terceiro | pending |
| AC-5 | Conteúdo hostil ou inconclusivo nunca liberado | Toda transição inválida termina sem referência utilizável, sem claim e sem chamada ao provedor | test-first | teste da máquina de estados | fixtures hostis mais falha do validador | critical: falso negativo entrega conteúdo malicioso | pending |
| AC-6 | Terminação explícita quando o conjunto não pode ser preservado | Resultado de rota é recusa com motivo publicado; nenhuma tentativa em canal alternativo carrega mensagem degradada | test-first | teste do estágio de rota | suíte de fallback com plano multicanal | high: degradação silenciosa quebra contrato de produto | pending |
| AC-7 | Reconstrução autorizada sem conteúdo bruto | Consulta retorna todas as relações e digests necessários, e nenhuma delas contém conteúdo | adaptive | teste focado de projeção de evidência | suíte de integração cruzando os contextos afetados | medium | pending |
| AC-8 | Caminho sem anexos preservado byte a byte | Vetores dourados congelados do comportamento vigente apresentam zero divergência antes e depois | test-first | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | `dotnet test MonteBravo.NotificationHub.sln --no-restore` | critical: regressão aqui quebra todo produtor existente | pending |
| AC-9 | Replay e conflito com manifesto | Mesma chave e mesmo manifesto devolvem o resultado original; diferença relevante devolve conflito e contadores duráveis permanecem estáveis | test-first | teste de unidade do hash canônico | suíte de idempotência com persistência real | high: conflito espúrio bloqueia produtor legítimo | pending |
| AC-10 | Convergência sob falha parcial | Nenhum estado observável apresenta anexo utilizável sem validação ou notificação aceita sem claim integral, em cada ponto de injeção | test-first | teste de transação com dublê que lança | matriz de falhas injetadas, uma por efeito da transação | critical: é a invariante que o requisito de claim indivisível protege | pending |
| AC-11 | Preservação enquanto houver dependência ativa | A varredura de abandonados não remove anexo reivindicado por notificação ativa nem anexo de tentativa em envio ou com resultado desconhecido | test-first | teste focado da varredura | suíte de integração avançando o relógio além do prazo | high: remoção indevida quebra tentativa em curso | pending |
| AC-12 | Ausência de vazamento nas superfícies coletadas | Sentinelas únicas não aparecem em nenhuma superfície inspecionada | adaptive | varredura focada na superfície alterada | varredura completa de todos os transportes e coletores | critical: vazamento de conteúdo ou de capacidade de acesso | pending |
| AC-13 | Preflight antes do ponto irreversível | Vencimento, revogação, divergência, conjunto incompleto ou envelope excedido produz falha explícita e zero chamadas ao provedor falso | test-first | teste focado do preflight | suíte de despacho com reentrega e concorrência | critical: uma chamada indevida é irreversível | pending |
| AC-14 | Recusa de conteúdo divergente ou não inspecionável | Recusa sem liberação e sem eco do conteúdo na resposta | test-first | teste de validação de forma | fixtures hostis na suíte de integração | high: eco de conteúdo hostil na resposta | pending |
| AC-15 | Oráculos com lado anterior real | Cada artefato criado reprova uma mutação deliberada do comportamento que protege | test-first | execução do artefato recém-criado contra o código vigente | mutação deliberada revertida após a prova | critical: sem isso, os demais oráculos não distinguem passar de não medir | pending |

## Obrigações de qualidade

| Origem | Gate | Obrigação | Aplicabilidade | Responsável pela evidência | Sensor / oráculo | Situação | Evidência |
|---|---|---|---|---|---|---|---|
| Ciclo externo por referência opaca | G5 | Estado observável sem revelar armazenamento | applicable | AC-1 | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | pending | pending |
| Recusa antes de notificação aceita | G6 | Zero referência não liberada alcança o provedor | applicable | AC-2 | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | pending | pending |
| Correspondência entre bytes validados e submetidos | G6 | Conjunto integral com identidade preservada | applicable | AC-3 | Suíte de contrato do provedor com captura do payload transmitido | pending | pending |
| Isolamento entre aplicações | G6 | Zero acesso cruzado e nenhuma enumeração | applicable | AC-4 | Suíte de integração com matriz cruzada | pending | pending |
| Fechamento por padrão na validação | G6 | Conteúdo hostil ou inconclusivo nunca liberado | applicable | AC-5 | Fixtures hostis e falha do validador | pending | pending |
| Terminação explícita no roteamento | G6 | Sem degradação silenciosa em outro canal | applicable | AC-6 | Suíte de fallback | pending | pending |
| Reconstrução de evidência | G6 | Tentativas aceitas reconstruíveis sem conteúdo bruto | applicable | AC-7 | Suíte de integração de evidência | pending | pending |
| Preservação do baseline brownfield | G6 | Caminho sem anexos idêntico em REST, Kafka, hash, recusas, eventos e canal | applicable | AC-8 | `dotnet test MonteBravo.NotificationHub.sln --no-restore` | pending | pending |
| Igualdade do manifesto idempotente | G5 | Replay e conflito corretos, contadores duráveis estáveis | applicable | AC-9 | Suíte de idempotência | pending | pending |
| Convergência sob falha parcial | G6 | Nenhum estado inconsistente observável | applicable | AC-10 | Matriz de falhas injetadas | pending | pending |
| Proteção contra descarte | G6 | Dependência ativa nunca removida | applicable | AC-11 | Teste da varredura de abandonados | pending | pending |
| Minimização das superfícies | G6 | Sentinelas ausentes de toda superfície coletada | applicable | AC-12 | Varredura com sentinelas | pending | pending |
| Revalidação antes do ponto irreversível | G6 | Liberação vencida ou revogada impede a chamada | applicable | AC-13 | Suíte de despacho com concorrência | pending | pending |
| Recusa de conteúdo não inspecionável | G6 | Recusa sem liberação e sem eco | applicable | AC-14 | Fixtures hostis | pending | pending |
| Fronteiras entre contextos | G6 | Dependência entre módulos somente sobre a superfície publicada, incluindo ausência de tipos do provedor de nuvem no domínio | applicable | SLICE | `dotnet test tests/Platform.ArchTests/Platform.ArchTests.csproj` | pending | pending |
| Atomicidade entre claim e aceite | G6 | Falha entre claim, aceite, outbox, auditoria e commit não deixa estado inconsistente | applicable | AC-10 | Suíte de transação com dublês | pending | pending |
| Identidade imutável após a liberação | G6 | Substituição ou alteração do objeto impede claim ou envio | applicable | AC-3 | Teste de troca de objeto em ambiente descartável | pending | pending |
| Snapshot estável entre tentativas | G6 | Toda tentativa usa o mesmo conjunto aceito, sem metadado mutável | applicable | AC-3 | Suíte de retry e fallback | pending | pending |
| Compatibilidade de contrato | G6 | Consumidores antigos passam contra o produtor novo | applicable | AC-8 | Snapshot de contrato mais consumidores de versão anterior | pending | pending |
| Escolha do método de transferência | G6 | Braços comparados com o mesmo corpus e envelope; opção promovida respeita o orçamento aprovado | rollout-only | L4-spec | Runner de desempenho com linha de base versionada | rollout-only | Gate de rollout registrado no plano de verificação |
| Rollout e rollback | G6 | A habilitação progressiva implanta os controles desabilitados e a reversão lógica não apaga dados | rollout-only | L4-spec | Ensaio de habilitação e reversão | rollout-only | Gate de rollout registrado no plano de verificação |

## Regras de negócio

- Um anexo só se torna utilizável após liberação. Resultado rejeitado, inconclusivo, indisponível ou não inspecionável nunca alcança liberação.
- O conjunto é indivisível: o claim transacional reivindica todos os anexos ou não altera nenhum.
- A identidade interna de um anexo liberado é imutável e composta por geração do objeto, digest e comprimento.
- O snapshot do manifesto aceito congela identidade e composição, e nunca congela elegibilidade. Liberação, revogação e validade são sempre relidas no preflight.
- Membro de manifesto ausente e membro vazio produzem a mesma forma canônica, para que o caminho sem anexos permaneça único.
- Um claim confirmado conta como dependência ativa, assim como uma tentativa em envio ou com resultado desconhecido.
- O produtor não escolhe o canal. Se o plano vigente não preserva o conjunto, o fluxo termina com falha explícita, sem conversão para link e sem remoção de anexos.
- Quantidade, tamanho, tipos e envelope efetivo são parâmetros aprovados por produto antes de aceitar anexos, e não valores escolhidos pelo implementador.

## Restrições

### Produto

- Mensagens públicas em português brasileiro; identificadores em inglês.
- Todo motivo de recusa novo exige linha correspondente na tabela do guia de integração do produtor, hoje verificada por função de adequação. O guia proíbe anexos de forma explícita e essa proibição é superfície de mudança desta entrega.
- Parâmetros de primeira produção e regra de descarte dependem de aprovação de produto registrada antes das tarefas que os consomem.

### Técnicas

- .NET 10, Minimal APIs, EF Core e PostgreSQL, com o estilo de slices verticais já observado e sem introduzir mediator.
- Monólito modular preservado: dependência entre contextos somente sobre a superfície publicada e versionada; domínio livre de infraestrutura.
- Armazenamento de objetos sob custódia do hub e chaves gerenciadas conforme o perfil de stack manual, sem reutilizar a infraestrutura interna do contexto de auditoria.
- A ordem vigente de escrita na transação de aceite é contrato: a fila de saída é acrescentada antes do registro de auditoria, porque esse registro segura o bloqueio da cadeia de partições até o commit. Qualquer passo novo entra antes desse registro.
- Nenhuma elevação de nível de isolamento no caminho de aceite. A janela entre verificação e uso fecha por atualização condicional com contagem de linhas afetadas.
- Migração inicial esmagada, gerada pela ferramenta por contexto, com snapshot de modelo regenerado e nunca editado à mão. A cerimônia de tabela viva, com tempo limite de bloqueio explícito e exigência de coluna anulável sem default, fica suspensa enquanto não houver dados em produção, e volta a valer no primeiro DDL sobre tabela com dados.
- Sem telemetria de plataforma. Toda afirmação de convergência recai sobre estado durável, jamais sobre contador ou métrica.
- Nomes de teste descrevem comportamento e nunca numeração de critério ou de fatia; artefatos de implementação não citam identificadores de especificação.

### Segurança e conformidade

- Conteúdo, nome, digest, localização e capacidade de acesso permanecem fora de transporte, fila, dead-letter, log, resposta e auditoria comum.
- Autorização por recurso, provando o vínculo entre principal e aplicação, antes de qualquer uso cruzado.
- Resposta de recusa nunca ecoa o conteúdo submetido nem distingue ausência de negação.
- Regra de arquitetura que impede o domínio de qualquer módulo de depender de tipos do provedor de nuvem.

### Operação

- Falha entre claim, aceite e commit precisa reverter a unidade transacional inteira; resultado de commit desconhecido exige verificação idempotente autoritativa em contexto novo, sem repetição cega.
- O horizonte de resolução de uma tentativa com resultado desconhecido é o ciclo de reconciliação vigente, da ordem de um dia, e precisa ser considerado na regra de descarte.
- Habilitação progressiva separa bloqueio de novos aceites do processamento de itens já aceitos.

## Riscos e incertezas

| Risco | Impacto | Tratamento |
|---|---|---|
| Alterar a forma canônica sem vetor dourado congelado torna a regressão indetectável | Todo produtor existente quebra sem que a suíte reprove | Tarefa 1 precede qualquer alteração da forma canônica e prova a reprovação por mutação |
| Promover a compensação posterior deixa janela de aceite durável com claim ainda reservado | Notificação aceita sem anexo íntegro, exatamente o que o requisito proíbe | Tarefa 7 decide entre as alternativas por injeção de falhas antes de qualquer escrita de produção |
| Passo de claim posicionado depois do registro de auditoria alarga um bloqueio global mensal | Degradação de ingresso, pipeline e despacho na frota inteira | Posição contratual antes do registro, com braço adicional na sonda de contenção existente |
| Manifesto vazio tratado como presente produz hash diferente da omissão | Caminho sem anexos deixa de ser único e replay legítimo vira conflito | Regra de omissão na ausência e no vazio, coberta por vetor dourado próprio |
| Varredura de abandonados remove objeto reivindicado | Tentativa em curso perde o anexo | Claim confirmado declarado como dependência ativa antes de habilitar a limpeza |
| Manifesto aceito pelo contrato e descartado em silêncio por uma superfície que não o transporta | Notificação aceita sem os anexos pedidos, com sintaxe válida e efeito diferente do solicitado | Nenhuma superfície publicada aceita corpo que nomeia o manifesto e prossegue sem ele; enquanto não transportar, recusa com motivo declarado |
| Segunda conexão concorrente por aceite esgota o pool | Saturação aparece como tempo esgotado de aquisição, não como lentidão de consulta | Recusar qualquer variante que abra segunda conexão no caminho de aceite |
| Envelope efetivo do provedor excedido após codificação | Submissão recusada tarde, com o conjunto já montado | Cálculo do payload final antes da chamada, com parâmetro aprovado |

## Dependências

- Nenhuma dependência entre Delivery Slices, porque o backlog contém uma única fatia.
- A ordenação que normalmente seria expressa por arestas entre fatias vive na lista `Depends on` de cada tarefa. As tarefas 1 a 5 não dependem de nada e precedem toda tarefa que altere forma canônica ou contrato publicado. As tarefas 6 a 9 produzem a evidência que fecha quatro dos sete gates internos; os outros três dependem de decisão de Produto.

## Artefatos obrigatórios

**Sempre:**
- [x] Context Pack.
- [x] Pull Request com plano de teste embutido e evidência de integração contínua.
- [x] Code Review com revisão de Segurança e de Produto.

**Condicionais aplicáveis a esta entrega:**
- [x] Documentação de convenção no README do módulo novo.
- [x] Aprovação formal de Segurança, pela custódia e pela minimização.
- [x] Aprovação formal de Produto para mensagens públicas e motivos de recusa.
- [x] ADR do protocolo de consistência do claim.
- [x] ADR do local e forma do snapshot do manifesto aceito.
- [x] ADR da entrada do manifesto na forma canônica idempotente.
- [x] Plano de release e runbook, pela habilitação progressiva e pelo rollback.
- [ ] Mapa de descoberta ou Event Storming: não aplicável.
- [ ] Post-mortem: não aplicável.

## Tarefas (Azure DevOps)

| Pontos de história | Estimativa |
|---:|---:|
| 2 | 8h |
| 3 | 16h |
| 5 | 24h |
| 8 | 40h |

**Totais das tarefas da Delivery Slice**: 190 SP | 928h.

### Quadro de tarefas

| # | Nome | Tipo | Responsável | Pontos | Estimativa | Depende de | Estado |
|---:|---|---|---|---:|---:|---|---|
| 1 | Congelar vetores dourados do hash de ingestão | Test | Senior Dev | 3 | 16h | nenhuma | Done |
| 2 | Criar a varredura com sentinelas nas superfícies coletadas | Test | Senior Dev | 5 | 24h | nenhuma | Done |
| 3 | Acrescentar a regra de arquitetura para tipos do provedor de nuvem no domínio | Architecture | Senior Dev | 2 | 8h | nenhuma | Done |
| 4 | Criar o snapshot de contrato para a documentação de API e o schema do barramento | Test | Senior Dev | 3 | 16h | nenhuma | Done |
| 5 | Acrescentar o braço comparativo ao runner de desempenho | Test | Senior Dev | 5 | 24h | nenhuma | Done |
| 6 | Executar o experimento de identidade e proteção do objeto sob custódia | Spike | dotnet-architect | 8 | 40h | nenhuma | Done |
| 7 | Provar o protocolo de consistência do claim sob falhas injetadas | Spike | dotnet-architect | 8 | 40h | nenhuma | Done |
| 8 | Levantar o corpus contratual do manifesto e decidir a versão | Spike | dotnet-architect | 5 | 24h | 1, 4 | Done |
| 9 | Executar a sonda comparativa de transferência ao provedor | Spike | dotnet-architect | 8 | 40h | 5 | Done |
| 10 | Escrever o ADR do protocolo de consistência do claim | Docs | dotnet-architect | 2 | 8h | 7 | Done |
| 11 | Escrever o ADR do local e forma do snapshot do manifesto aceito | Docs | dotnet-architect | 2 | 8h | nenhuma | Done |
| 12 | Escrever o ADR da entrada do manifesto na forma canônica idempotente | Docs | dotnet-architect | 2 | 8h | 8 | Done |
| 13 | Criar o módulo de anexos com contexto de persistência, schema e configuração | Implementation | Senior Dev | 5 | 24h | 3 | Done |
| 14 | Implementar registro, upload gerenciado e referência opaca | Implementation | Senior Dev | 8 | 40h | 13 | Done |
| 15 | Implementar autorização por principal e aplicação | Implementation | Senior Dev | 5 | 24h | 14 | Done |
| 16 | Implementar a custódia com identidade íntegra imutável | Implementation | Senior Dev | 8 | 40h | 6, 14 | Done |
| 17 | Implementar a proteção contra descarte enquanto houver dependência ativa | Implementation | Senior Dev | 5 | 24h | 16 | Done |
| 18 | Implementar a máquina de estados de validação e liberação, fechando por padrão | Implementation | Senior Dev | 8 | 40h | 16 | Done |
| 19 | Implementar revogação, rejeição e repetição segura | Implementation | Senior Dev | 5 | 24h | 18 | Done |
| 20 | Publicar o contrato de claim e snapshot para o módulo de notificações | Implementation | Senior Dev | 5 | 24h | 10, 19 | Done |
| 21 | Implementar o claim integral na transação compartilhada de aceite | Implementation | Senior Dev | 8 | 40h | 20 | Done |
| 22 | Acrescentar o manifesto ao contrato publicado e ao validador | Implementation | Senior Dev | 3 | 16h | 12 | Done |
| 23 | Incorporar o manifesto à forma canônica idempotente | Implementation | Senior Dev | 5 | 24h | 1, 22 | Done |
| 24 | Implementar o leitor do barramento para o membro novo | Implementation | Senior Dev | 3 | 16h | 23 | Done |
| 25 | Persistir o snapshot do manifesto aceito com leitor tolerante | Implementation | Senior Dev | 5 | 24h | 11, 21 | Done |
| 26 | Ler o snapshot por pipeline, despacho e fallback | Implementation | Senior Dev | 5 | 24h | 25 | Done |
| 27 | Implementar o preflight antes do ponto irreversível | Implementation | Senior Dev | 5 | 24h | 26 | Done |
| 28 | Evoluir o contrato de despacho com representação neutra do conjunto | Implementation | Senior Dev | 5 | 24h | 27 | To Do |
| 29 | Implementar a submissão do conjunto integral no adaptador de e-mail | Implementation | Senior Dev | 8 | 40h | 9, 28 | To Do |
| 30 | Produzir a evidência dos bytes submetidos | Implementation | Senior Dev | 3 | 16h | 29 | To Do |
| 31 | Implementar roteamento e fallback com terminação explícita | Implementation | Senior Dev | 5 | 24h | 27 | To Do |
| 32 | Implementar a reconciliação de falhas parciais e a convergência de órfãos | Implementation | Senior Dev | 8 | 40h | 21, 30 | To Do |
| 33 | Implementar o descarte seguro de abandonados | Implementation | Senior Dev | 5 | 24h | 17, 32 | To Do |
| 34 | Implementar a evidência operacional reconstruível | Implementation | Senior Dev | 5 | 24h | 30, 32 | To Do |
| 35 | Esmagar as migrações numa inicial e regenerar o snapshot de modelo | Implementation | Senior Dev | 3 | 16h | 25 | To Do |
| 36 | Implementar a habilitação progressiva e o rollback lógico | Implementation | Senior Dev | 5 | 24h | 24, 31, 34, 35 | To Do |
| 37 | Executar o ensaio de habilitação e reversão | Test | Senior Dev | 5 | 24h | 36, 38 | To Do |
| 38 | Atualizar o guia do produtor e os motivos de recusa | Docs | Senior Dev | 2 | 8h | 24, 31 | To Do |

### Tarefa 1: Congelar vetores dourados do hash de ingestão

- **Descrição**: Cunhar literais de digest para um corpo mínimo e um corpo com todos os membros opcionais, gerados a partir do comportamento vigente, no formato já usado pelos testes de hash canônico de outros módulos. Provar por mutação deliberada que os literais reprovam.
- **Tipo**: Test
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: nenhuma
- **Aceitação**: Uma alteração deliberada na ordem de escrita da forma canônica reprova o teste; revertida a alteração, o teste volta a passar.

### Tarefa 2: Criar a varredura com sentinelas nas superfícies coletadas

- **Descrição**: Semear valores únicos no arquivo e inspecionar broker, fila, outbox, dead-letter, log, resposta e auditoria comum, falhando quando qualquer sentinela aparece. A varredura precisa cobrir as superfícies vigentes antes de existir a primeira superfície nova.
- **Tipo**: Test
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: nenhuma
- **Aceitação**: Uma sentinela injetada deliberadamente em uma das superfícies reprova a varredura; sem a injeção, a varredura passa.

### Tarefa 3: Acrescentar a regra de arquitetura para tipos do provedor de nuvem no domínio

- **Descrição**: Estender a regra vigente que mantém o domínio livre de tecnologia para incluir o namespace do provedor de nuvem, hoje ausente da lista proibida. Provar por mutação.
- **Tipo**: Architecture
- **Responsável**: Senior Dev
- **Pontos de história**: 2
- **Estimativa**: 8h
- **Depende de**: nenhuma
- **Aceitação**: Uma dependência deliberada do domínio para o tipo proibido reprova a função de adequação.

### Tarefa 4: Criar o snapshot de contrato para a documentação de API e o schema do barramento

- **Descrição**: Capturar o corpo do documento de API e a forma do registro do barramento a partir do contrato vigente e compará-los em teste, porque os testes existentes verificam apenas exposição e ambiente, nunca o conteúdo.
- **Tipo**: Test
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: nenhuma
- **Aceitação**: Uma mudança deliberada no contrato reprova a comparação com o snapshot.

### Tarefa 5: Acrescentar o braço comparativo ao runner de desempenho

- **Descrição**: Estender o runner e a pasta de linhas de base existentes com um braço que compara os métodos de transferência sob o mesmo corpus e envelope, registrando as grandezas exigidas pelo plano de verificação.
- **Tipo**: Test
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: nenhuma
- **Aceitação**: O braço executa, grava relatório no formato vigente e o portão relativo compara contra a linha de base versionada.

### Tarefa 6: Executar o experimento de identidade e proteção do objeto sob custódia

- **Descrição**: Em ambiente descartável, comparar os mecanismos candidatos de proteção do objeto, medindo se a troca após a validação é impedida ou detectada, e verificar acesso cruzado e política de chaves.
- **Tipo**: Spike
- **Responsável**: dotnet-architect
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: nenhuma
- **Estado**: Done, por decisão do dono em 2026-09-01, com a matriz comportamental incompleta. Está provado o candidato de identidade dos bytes, promovido em ambiente descartável: S3 Versioning com `VersionId` persistido, SHA-256 recalculado e comprimento, sempre lendo explicitamente a geração liberada. Não está provado o conjunto de controles ortogonais, porque o provisionamento falhou antes do exercício: IAM, política de chave, SSE-KMS, rotação e isolamento entre aplicações seguem inconclusivos, e nenhum dos doze casos negativos do gate foi executado. A Tarefa 16 fica liberada carregando essa ressalva.
- **Evidência atual**: `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-06-object-identity-protection.md` e `task-06-round-table-json-transport.md`. A mesa redonda devolveu `RECOMMEND` com alternativa líder A1 e produziu três artefatos que faltavam desde a primeira rodada: a fronteira de ameaça em forma normativa, o checklist fechado `C01` a `C12` e a tabela de resíduos aceitos `RES-01` a `RES-14`. O `dotnet-specialist`, autor do parecer `BLOCKED` da 24ª revisão, retirou esse parecer no ponto em que ele afirmava fechar uma ameaça, porque a enumeração de principais não encontra ninguém capaz de exercer o mecanismo sem já possuir o token do usuário. A medição fixou que a máscara de acesso, e não o modo de compartilhamento, decide a checagem de compartilhamento; que a nova análise em ancestral é barrada pela não vacuidade do diretório; e que o handle do arquivo, sozinho, recusa renomeação e exclusão de ancestral de um a oito níveis. O checklist foi implementado com 40 linhas no runbook e 476 no ensaio, com prova negativa por 14 mutações em tempo de execução e duas falsificações revertidas com hash idêntico. A 25ª revisão aprovou o par congelado `59c1fe74d8defcbe335e5ad0765301677baa49c3a6ca7f72c5e9c794f8deaf01` e `87d53c2a7ffa08fef693271cf166ebfbbad8eb8dcf42479e6fa2b6d242ef17f2`, com os hashes conferidos na abertura e no encerramento por ambos os revisores.
- **Execução da matriz**: o `Preflight` passou com zero colisões depois de a role temporária de operação ser recriada pelo bootstrap. O `Provision` criou os dois buckets, as duas CMKs com aliases, o trail com log iniciado e as seis roles de dados, e falhou na verificação final porque a allowlist da role de operação não concede `s3:GetBucketObjectLockConfiguration`, ação que o próprio runbook chama. A execução revelou quatro defeitos que a revisão estática não alcança: além da allowlist incompleta, três ocorrências da materialização de coleção nula, em que `@($null)` produz um item nulo e gera argumento vazio, nos dois laços e no guarda de bucket vazio da limpeza de bucket versionado; e uma assimetria na política de chave, porque os aliases são criados pelo perfil federado e a limpeza tenta removê-los pela role de operação, sem `kms:DeleteAlias` concedido. As três correções de coleção nula foram aplicadas para destravar a limpeza, portanto o runbook em disco já não corresponde ao hash que a 25ª revisão aprovou.
- **Limpeza**: concluída. A enumeração final não encontrou nenhuma role, bucket, trail ou alias do prefixo, e as duas CMKs estão em `PendingDeletion` com exclusão em 2026-09-08, dentro do resíduo autorizado. A remoção do trail, do bucket de trilha e da role temporária foi feita fora do runbook, pelo perfil federado, porque o portão de entrega final do CloudTrail se mostrou insatisfazível: uma janela de log de onze minutos produziu um único digest que cobre dez segundos e não referencia arquivo de log, enquanto o portão exige as duas linhas de validação. Esse é o quinto defeito, e ele não deveria valer para provisionamento parcial sem exercício, discernimento que a fase `VerifyCleanup` já possui. Por isso o estado autenticado não alcançou `cleaned-awaiting-verification`, e a ausência de resíduo está comprovada por enumeração direta na conta, não pelo recibo da ferramenta.
- **Correções aplicadas**: o runbook passou a ter SHA-256 `a433365d3bb655130f155dbf5618ceecb9327fd5e359ec1422224d227dc7ed02`. A allowlist declara `s3:GetBucketObjectLockConfiguration`, nome real da ação exigida pela API, no lugar de `s3:GetObjectLockConfiguration`, que não existe como ação IAM. As duas instruções `OperatorAdministration` das políticas de chave concedem `kms:DeleteAlias`, porque a limpeza remove aliases pela role de operação enquanto a criação usa o perfil federado.
- **Decisão pendente**: uma nova execução integral exige três coisas. Condicionar o portão de entrega final à existência de corpus de exercício. Resolver que a fase `Provision` não é retomável, o que obriga a limpar e recriar a cada falha tardia e duplica o resíduo de CMKs por tentativa, acima das duas autorizadas. E uma revisão delimitada que confirme que a superfície do transporte permanece idêntica, já que as cinco correções desta execução afastaram o runbook do par que a 25ª revisão aprovou.
- **Aceitação**: Relatório compara os mecanismos com resultado reproduzível e nomeia o promovido; a troca do objeto após a validação é impedida ou detectada no mecanismo escolhido.

### Tarefa 7: Provar o protocolo de consistência do claim sob falhas injetadas

- **Descrição**: Construir a prova que decide entre as alternativas registradas, injetando falha em cada efeito da transação de aceite e verificando que nenhum estado observável apresenta notificação aceita sem claim integral, e que órfãos convergem.
- **Tipo**: Spike
- **Responsável**: dotnet-architect
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: nenhuma
- **Estado**: Done.
- **Evidência**: `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.md`, desenho `READY`; roteiro de teste reproduzível em `task-07-claim-consistency.sql`.
- **Aceitação**: Cada ponto de injeção produz estado durável consistente; a alternativa promovida é a única que satisfaz a invariante em todos os pontos.

### Tarefa 8: Levantar o corpus contratual do manifesto e decidir a versão

- **Descrição**: Congelar a semântica de ordem, duplicatas e propriedades que alteram o envio, e decidir entre membro opcional na versão vigente e versão coexistente, com prova de tolerância dos consumidores antigos.
- **Tipo**: Spike
- **Responsável**: dotnet-architect
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 1, 4
- **Estado**: Done.
- **Evidência**: `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md`, desenho `READY`; roteiro de teste reproduzível em `task-08-contract-corpus/`.
- **Aceitação**: Corpus publicado com vetores congelados; a estratégia de versão escolhida passa contra consumidores de versão anterior.

### Tarefa 9: Executar a sonda comparativa de transferência ao provedor

- **Descrição**: Comparar os três métodos de transferência sob carga e cancelamento, medindo recursos de runtime, limpeza e igualdade byte a byte, para fechar o gate anterior à promoção do adaptador.
- **Tipo**: Spike
- **Responsável**: dotnet-architect
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 5
- **Estado**: Done. Os três braços fazem trabalho funcional equivalente até o envio, a igualdade é provada pelo destinatário e o método promovido é `streaming`, sob o envelope de produto e o alvo de implantação ratificados pelo dono da decisão.
- **Evidência**: `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-09-provider-transfer.md` e `task-09-round-table-transfer-budget.md`. Matriz de três braços, quatro perfis e duas concorrências, com duzentas amostras por braço em cada célula, sob Server GC com contagem de heaps pinada em 1. Dezesseis mutações em tempo de execução provaram que cada verificação nova reprova, e cada mutação de código foi revertida com verificação byte a byte. Linha de base gravada como mediana de três execuções isoladas por célula, por um comando que não mede e nunca compara.
- **Fundamento da promoção**: o teto absoluto por envio não decide, porque a bufferização cabe nele no envelope ratificado, medindo 18.952.027 bytes contra 26.214.400. O que decide é o teto afim, lido no piso e no máximo com separação de 28 vezes: o streaming aloca entre 19.149 e 35.642 bytes por envio ao longo dessa faixa, portanto seu custo é constante no anexo, enquanto a bufferização vai de 642.847 a 20.809.486, portanto seu custo é o anexo. No teto do provedor a bufferização custa 1,54 vezes o orçamento inteiro. O streaming mantém zero coleções de geração 2 e zero pausa de coleta nas vinte e nove execuções.
- **Achado de segurança**: escrever a base64 como string comum sob o codificador padrão permite que conteúdo escolhido pelo remetente expanda seis vezes, e um anexo de 3,56 MiB estoura o teto de mensagem do provedor. A chamada correta não passa pelo codificador e torna o comprimento independente do conteúdo. O perfil adversário provou ser obrigatório: sob corpus legível, a chamada explorável passa com todas as verificações verdes.
- **Reviravolta registrada**: a expectativa ratificada era que a bufferização reprovasse o teto absoluto. Ela passou, e o teto não foi ajustado. A causa foi medida: corrigir a chamada de escrita elimina também a string intermediária de base64, e a amplificação cai de 9,33 para 2,58. As duas decisões interagem, e a correção de segurança tornou o orçamento sobrevivível para a bufferização.
- **Aceitação**: Os três braços produzem relatório comparável; a opção promovida respeita o orçamento aprovado e limpa todos os recursos.

### Tarefa 10: Escrever o ADR do protocolo de consistência do claim

- **Descrição**: Registrar a decisão promovida pela tarefa 7, com alternativas, consequências, mecanismo de garantia e condição de revisão.
- **Tipo**: Docs
- **Responsável**: dotnet-architect
- **Pontos de história**: 2
- **Estimativa**: 8h
- **Depende de**: 7
- **Estado**: Done.
- **Artefato**: `docs/ADR-0018-claim-atomico-na-transacao-de-aceite.md`, status `ACCEPTED`; backlog consumidor reconciliado com o claim transacional aprovado.
- **Aceitação**: ADR aceito, com a alternativa promovida e a evidência que a sustenta.

### Tarefa 11: Escrever o ADR do local e forma do snapshot do manifesto aceito

- **Descrição**: Registrar onde o snapshot é persistido e por quê, incluindo a separação entre composição congelada e elegibilidade relida.
- **Tipo**: Docs
- **Responsável**: dotnet-architect
- **Pontos de história**: 2
- **Estimativa**: 8h
- **Depende de**: nenhuma
- **Estado**: Done.
- **Artefato**: `docs/ADR-0019-snapshot-do-manifesto-aceito.md`, status `ACCEPTED`; backlog consumidor reconciliado com o local, a forma, a leitura e o rollout do snapshot aprovados.
- **Aceitação**: ADR aceito, com a alternativa promovida e o custo de reversão declarado.

### Tarefa 12: Escrever o ADR da entrada do manifesto na forma canônica idempotente

- **Descrição**: Registrar a posição do membro, a regra de omissão na ausência e no vazio, e a consequência irreversível sobre os digests já gravados.
- **Tipo**: Docs
- **Responsável**: dotnet-architect
- **Pontos de história**: 2
- **Estimativa**: 8h
- **Depende de**: 8
- **Estado**: Done.
- **Artefato**: `docs/ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md`, status `ACCEPTED`, que substitui a ADR-0020; regra canônica aprovada sobre contrato publicado único.
- **Aceitação**: ADR aceito, com a regra de omissão explícita e o corpus como evidência.

### Tarefa 13: Criar o módulo de anexos com contexto de persistência, schema e configuração

- **Descrição**: Registrar o módulo novo pelo mecanismo de descoberta vigente, com contexto de persistência e schema próprios e seção de configuração base, porque a validação na inicialização derruba toda a suíte quando a seção falta.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 3
- **Estado**: Done.
- **Evidência atual**: build da API sem warnings nem erros; testes focados 6/6; testes de arquitetura 30/30; inspeção do contexto pelo EF aprovada; suíte integrada com 662 aprovações, 2 testes ignorados e 23 falhas temporais externas, reproduzidas isoladamente no seed de partições de `Notifications` durante a virada mensal UTC.
- **Exceção aceita**: o usuário aprovou a conclusão em 2026-09-01 com a falha temporal externa isolada e registrada, sem alterar o conjunto autorizado de escrita da entrega nem ocultar o resultado do gate integral.
- **Aceitação**: A aplicação sobe com o módulo registrado; a suíte de integração continua verde; a função de adequação de fronteiras passa sem edição.

### Tarefa 14: Implementar registro, upload gerenciado e referência opaca

- **Descrição**: Expor a superfície externa de registro e upload sob a identidade da aplicação produtora, devolvendo referência pública opaca e estado observável, sem revelar armazenamento ou credencial.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 13
- **Estado**: Done.
- **Direção de implementação**: usar `src/Platform.Api/Modules/TemplateManagement` como referência para organização de vertical slices, validação, tratamento de erros, autorização, limitação de taxa, logging, persistência e testes.
- **Evidência atual**: candidato corrigido e congelado com 43 arquivos e SHA-256 ordinal `5188704cab73f3ce4ed9dbdca6592edcb990b3ae98b902838af2e4b1893e6720`; builds sem warnings nem erros; testes unitários 11/11, módulo 9/9, endpoints 21/21, OpenAPI 2/2, integração completa do módulo 38/38 e arquitetura 30/30; os seis achados da avaliação independente foram resolvidos em rechecks direcionados.
- **Aceitação**: O ciclo externo se completa em host real; nenhuma resposta, erro ou log contém localização, chave ou credencial.

### Tarefa 15: Implementar autorização por principal e aplicação

- **Descrição**: Introduzir autorização por recurso que prove o vínculo entre principal e aplicação, fechando a lacuna do ingresso vigente, que autoriza por classe.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 14
- **Estado**: Done.
- **Direção de implementação**: estender a política nomeada do módulo no padrão de `TemplateManagement`, com autorização por recurso que prove o vínculo entre principal, aplicação e referência sem permitir enumeração. O registry pertence ao schema `attachmentmanagement`, usa a chave `(issuer, claim-kind, principal-id, application)`, não possui cache nem fallback e distingue negação de indisponibilidade. O provisionamento externo dos grants permanece condição de ativação operacional da Tarefa 35, sem bloquear esta implementação.
- **Decisão atual**: desenho aprovado pelo usuário em 2026-09-01 e implementação concluída. Registry legível sem grant, inclusive vazio, nega acesso; falha de consulta ou tabela ausente retorna indisponibilidade. Nenhuma requisição cria grants.
- **Evidência atual**: candidato final congelado com 52 arquivos e SHA-256 ordinal `ac75e5c1768fd2636cd064fddc4e029327e5eaffee49096fcf5234c445b5672d`, confirmado independentemente. Builds da API e integração sem warnings nem erros; unitários 21/21; autorização e registro 15/15; integração completa do módulo 47/47; OpenAPI 6/6; arquitetura 30/30; varreduras de fronteira e higiene sem achados. Os três achados MEDIUM e dois LOW da revisão foram resolvidos; arquiteto e engenheiro retornaram `ACCEPT`, e o especialista retornou `CONFIRMADO`, sem HIGH/MEDIUM novo.
- **Condição de rollout**: ativação depende da migration da Tarefa 35 e do provisionamento externo auditável dos grants; a ausência da tabela permanece fail-closed com `503`.
- **Aceitação**: A matriz cruzada de principal, aplicação e referência produz zero acesso cruzado e nenhuma resposta permite enumeração.

### Tarefa 16: Implementar a custódia com identidade íntegra imutável

- **Descrição**: Fixar após a liberação uma identidade composta por geração do objeto, digest e comprimento, usando o mecanismo de proteção promovido pela tarefa 6, com acesso mínimo.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 6, 14
- **Estado**: Done em 2026-09-02, com a forma decidida pela mesa redonda do mesmo dia e corrigida por ela depois da implementação.
- **Forma decidida**: registro de geração append-only, uma linha por geração capturada, sob a infraestrutura do módulo, contendo store lógico, chave, versão, algoritmo, digest, comprimento e instante. A prova dos bytes vive exclusivamente nessa linha e nunca é copiada para o agregado, o que mantém o agregado e o mapeamento dele intocados. O congelamento é por linha que nasce completa somado ao comportamento de somente leitura após gravação; o gatilho de rejeição de mutação pertence à Tarefa 35. A prova vem da passagem de verificação que relê a versão fixada logo após a escrita, nunca da contagem feita durante a escrita. A escrita condicional passa a ser invariante do adaptador, porque foi medido que ela limita de onze para uma as gerações duráveis produzidas por uma única chamada cuja resposta se perde. O recibo completo está em `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-16-round-table-identity.md`.
- **Ressalva herdada**: IAM, política de chave, criptografia por chave gerenciada, rotação e isolamento entre aplicações seguem inconclusivos. Nenhum artefato desta tarefa pode declará-los provados. A identidade decidida protege contra troca acidental e contra retorno da geração errada, e não protege contra um principal com privilégio amplo.
- **Aceitação**: A captura registra a versão exata, o digest recalculado na releitura e o comprimento verificado, e recusa localizador ausente, vazio ou igual ao literal `null`; a reescrita da identidade registrada é rejeitada na persistência; uma segunda geração escrita sob a mesma referência produz linha própria e não sobrescreve a anterior; o descarte remove a versão exata e não deixa marcador no lugar da exclusão; o localizador não aparece em resposta nem em log. As duas cláusulas que a redação anterior trazia, sobre impedir claim e envio e sobre correspondência do digest na submissão, dependem da máquina de liberação e da chamada ao provedor, e por isso são verificadas nas Tarefas 18 e 29, onde os oráculos existem.

### Tarefa 17: Implementar a proteção contra descarte enquanto houver dependência ativa

- **Descrição**: Impedir a remoção de objeto com dependência viva, incluindo claim confirmado e tentativa em envio ou com resultado desconhecido, conforme a regra qualificada no requisito.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 16
- **Estado**: Done em 2026-09-02.
- **Forma entregue**: registro de dependência do próprio módulo, com uma linha viva por detentor, trava da linha do anexo tomada antes da escrita para fechar a janela entre a decisão do descarte e a remoção dos bytes, e operação de descarte que recusa enquanto houver dependência viva. A vivacidade é a ausência de encerramento, e a razão declarada nunca é consultada para decidir, de modo que uma razão que ninguém listou ainda protege o objeto. Tomar e encerrar são idempotentes, e a assimetria entre os dois é declarada: encerrar não declara nada além do encerramento, portanto repetir não perde informação; tomar declara razão e instante, e a linha viva preserva o par com que a proteção começou, portanto o chamador é avisado de que a declaração dele não foi escrita.
- **Medição para a Tarefa 35**: a inserção da dependência toma bloqueio compartilhado de chave na linha do anexo por causa da chave estrangeira, e esse bloqueio conflita com a trava explícita do descarte. A trava explícita é carregadora apenas na direção da dependência em voo; na direção oposta existe proteção redundante vinda da chave estrangeira.
- **Aceitação**: A varredura não remove anexo com dependência viva em nenhum dos estados listados, com o relógio avançado além do prazo.

### Tarefa 18: Implementar a máquina de estados de validação e liberação, fechando por padrão

- **Descrição**: Aplicar a política executável aprovada, comparando o tipo efetivo e tratando conteúdo protegido e resultado inconclusivo, de modo que indisponibilidade nunca abra o gate.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 16
- **Estado**: Done em 2026-09-02, refeita do zero depois de uma primeira tentativa ter morrido sem nenhum oráculo executado.
- **Forma entregue**: porta de política com implementação padrão que recusa tudo, detecção de tipo por prefixo dos bytes dentro da passagem de verificação que já existe, e liberação como linha própria append-only que nasce completa. A política é avaliada uma única vez, na validação, e um oráculo reprova quando o caminho de upload a consulta. O prazo do inconclusivo é escrito uma vez e não é movido por repetição nem por janela mais larga. Vinte e cinco mutações de runtime, cada uma com vermelho observado e reversão conferida por digest.
- **O que os oráculos não provam**, declarado e não mascarado: conteúdo hostil não está verificado, porque o lado da aprovação é dirigido por dublê e a política que embarca nunca abre um arquivo; e a recusa de conteúdo não inspecionável está provada apenas no sentido de prefixo sem correspondência, de modo que um documento protegido por senha de tipo admitido passa pelo detector. Os dois critérios só ficam verificados quando houver detector e verificador reais atrás do mesmo encaixe.
- **Aceitação**: Fixtures hostis e falha do validador terminam sem referência utilizável; nenhuma transição inválida alcança liberação.

### Tarefa 19: Implementar revogação, rejeição e repetição segura

- **Descrição**: Completar o ciclo de vida com revogação e rejeição idempotentes, garantindo que uma repetição não reabra estado já fechado.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 18
- **Estado**: Done em 2026-09-02.
- **Forma entregue, com o fundamento**: rejeição e revogação ficaram com formas diferentes porque são coisas diferentes. Rejeição é veredito e já existia no agregado, escrita pela máquina de validação; o que faltava era o caminho de produção. Revogação é ato sobre uma concessão já dada, e ganhou estado terminal próprio, alcançável somente a partir de liberado, mais linha append-only própria que nomeia a liberação exata que retirou. Não vira rejeição porque responder recusa de conteúdo para algo que nenhum verificador recusou seria mentira no vocabulário público, e não altera a linha de liberação porque a decisão anterior proíbe alterá-la. A linha nomeia a liberação, e não apenas o anexo, porque uma revalidação explícita futura escreve a segunda concessão e uma revogação que nomeasse só o anexo ficaria sem resposta. Trinta mutações de runtime, cada uma com vermelho observado e reversão conferida byte a byte.
- **Superfície pública**: quatro motivos novos, sendo um único para toda a família de recusa de conteúdo e um único para toda a família de operação que não concluiu. O detalhe fino sai apenas pela leitura autorizada, que é fechada ao produtor, e há oráculo para as duas metades na mesma arrumação.
- **O que os oráculos não provam**, declarado: nada sobre conteúdo hostil ou arquivo protegido por senha, porque a lista de tipos admitidos está vazia e a ressalva anterior continua inteira; nada sobre quem tem autoridade para revogar, já que a rota usa a mesma concessão de produtor e nenhum oráculo diz que essa é a autoridade certa; o motivo público de operação indisponível está no contrato e não é exercitado por oráculo de rota, apenas no nível da operação; não existe oráculo de corrida real entre revogação e validação, e o que está provado é o índice único; e o caso de referência inexistente nas rotas novas não tem oráculo próprio.
- **Aceitação**: Repetir a mesma transição não produz efeito adicional; toda transição inválida termina de forma explícita.

### Tarefa 20: Publicar o contrato de claim e snapshot para o módulo de notificações

- **Descrição**: Expor na superfície publicada e versionada o claim transacional do conjunto integral, o snapshot imutável e a verificação de liberação, sem revelar armazenamento nem persistência interna.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 10, 19
- **Estado**: Done em 2026-09-02.
- **Forma entregue, com o fundamento**: o item publicado carrega referência, identidade de conteúdo, nome, tipo e comprimento. A identidade de conteúdo é um **manipulador opaco que nomeia a geração**, e não um resumo dela: a prova dos bytes continua na linha de geração, o consumidor recebe um ponteiro que só este módulo resolve, e a comparação acontece deste lado da fronteira com um veredito atravessando de volta. É isso que permite congelar a composição fora do módulo mantendo a prova dentro dele. O manipulador nomeia a geração e não a concessão, de propósito, porque amarrá-lo à concessão vigente congelaria elegibilidade. Uma coluna mintada própria foi recusada, com o gatilho de reabertura escrito no tipo: ela passa a ser a resposta no dia em que um manipulador publicado precisar girar sem girar a linha que ele nomeia.
- **Duas escolhas de forma que acompanham**: o veredito de indisponibilidade e o de não reivindicável valem zero, para que um resultado que ninguém produziu, inclusive o de um dublê não configurado, não libere o conjunto; e o desfecho do claim tem apenas duas portas, de modo que não existe construtor para uma recusa que reporte aceitação.
- **Um oráculo próprio nasceu vazio e foi consertado**: as asserções de igualdade ligavam a sobrecarga de coleção da biblioteca de asserções, que percorre elementos e nunca pergunta ao tipo, então o falsificador passava verde. Reescritas para perguntar ao comparador por nome, o falsificador passou a reprovar, e as execuções anteriores foram descartadas.
- **O que os oráculos não provam**, declarado: nada sobre o claim em si, porque as duas interfaces não têm implementação nem registro; nada sobre o manipulador em produção, porque ele não tem chamador; nada sobre vazamento em tempo de execução, já que a regra fecha por forma o conjunto de membros e uma cadeia de caracteres pode carregar qualquer coisa; e nada sobre a taxonomia das recusas, que é vocabulário sem exercício.
- **Aceitação**: O contrato compila na superfície publicada; a função de adequação de fronteiras passa; o inventário de igualdade de contratos é reparado ou registra a quebra.

### Tarefa 21: Implementar o claim integral na transação compartilhada de aceite

- **Descrição**: Implementar o protocolo aceito no caminho de ingresso sobre a mesma `DbTransaction`, a mesma conexão e a mesma base física de notificação, idempotência, outbox e auditoria; reverter e descartar a unidade perdedora antes da consulta idempotente autoritativa.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 20
- **Estado**: Done em 2026-09-03.
- **Forma entregue, com o fundamento**: o texto do upsert saiu do registro de dependências para um irmão que recebe a transação do chamador e monta comando sobre a conexão dela, de modo que existe um único texto para as duas escritas. O claim usa três instruções próprias sobre a mesma conexão, e nenhuma abre conexão, inicia transação ou confirma. A identidade da posse é derivada da aplicação somada à chave idempotente, e não da notificação, porque derivar da notificação quebraria a idempotência: uma retentativa após commit de resultado desconhecido cunharia identificador novo e tomaria segunda posse sobre o mesmo anexo. A ordem de travamento é canônica por referência, e não a ordem do pedido, enquanto o snapshot volta na ordem do pedido.
- **Ordem na transação**: abrir em isolamento explícito, conferir o isolamento efetivo pelo servidor, claim, notificação e chave, fila de saída, auditoria, commit. O passo novo entrou antes do registro de auditoria, como o contrato manda.
- **Ausência de segunda conexão, provada por três medições independentes**: as travas de relação durante o aceite pertencem a um único processo, lido de dentro da própria transação; um host com a configuração do módulo de anexos apontada para uma base inexistente continua aceitando e deixando a posse durável, com prova de que a cadeia é mesmo inutilizável; e o papel de consumo compõe o claim sem nenhuma persistência de anexos, de modo que um repositório próprio faria a resolução de dependências falhar.
- **Matriz de falhas injetadas**: seis pontos, um por efeito da transação. Nos cinco de falha, o estado durável observado é zero notificação, zero chave, zero fila de saída e zero posse. No sexto, commit feito com resposta perdida, a repetição devolve o mesmo identificador pelas duas autoridades, com uma posse viva na versão original.
- **O que os oráculos não provam**, declarado: o claim exige estado liberado e linha de liberação e **não confere vencimento**, portanto um anexo com liberação vencida é reivindicável hoje, e isso é elegibilidade que pertence à verificação da Tarefa 27; a leitura de isolamento da sonda não tem vermelho próprio, porque o que está falsificado é a guarda e não a leitura; a asserção de sessão única é instantânea e não enxergaria uma conexão aberta e devolvida antes da amostra; não existe oráculo de falha durante o commit; nada prova co-localização física dos participantes nem privilégio mínimo do papel; e a contenção da trava não foi medida sob carga.
- **Extensão de escopo registrada**: a composição da superfície de claim foi escrita em `Composition/IntegrationSurfaceSetup.cs`, fora do conjunto declarado, por ser a única casa legal. Um módulo não compõe internos de outro, e o papel de consumo não pode referenciar a infraestrutura de anexos sem reprovar a função de adequação de fronteiras. A adição é de um método e não altera registro existente.
- **Decisão de vocabulário adotada por precedente**: promover a recusa de anexo ao catálogo publicado exigiria linha no guia do produtor, que é propriedade da Tarefa 38. Foi adotado o precedente já escrito no módulo, com código exclusivo de transporte que responde na faixa de erro de negócio, grava trilha e não vira motivo de evento no barramento. Quem promover o código ao catálogo precisa da linha no guia no mesmo commit.
- **Aceitação**: Cada ponto de injeção de falha deixa estado durável consistente; o perdedor idempotente não conserva claim nem lock; a sonda confirma `READ COMMITTED`, a posição anterior à auditoria e a ausência de segunda conexão.

### Tarefa 22: Acrescentar o manifesto ao contrato publicado e ao validador

- **Descrição**: Publicar `attachments` no contrato único, como lista de referências opacas, introduzindo o membro no bloco de opcionais do comando interno sem posição nova no construtor. Membro ausente e `null` significam ausência de anexos; a tolerância vigente aos demais membros desconhecidos é preservada.
- **Estado**: Done em 2026-09-02, reenquadrada pela decisão do dono de que não existe V2 nem nada obsoleto.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: 12
- **Aceitação**: Chamadores existentes compilam sem alteração; o contrato recusa lista vazia, referência em branco e repetição ordinal antes do hash; membro ausente e `null` são aceitos como ausência de anexos; um membro futuro não relacionado continua aceito; existe um único documento OpenAPI publicado, e ele nomeia o manifesto no corpo da ingestão.

### Tarefa 23: Incorporar o manifesto à forma canônica idempotente

- **Descrição**: Escrever o membro na posição ordinal correta, omitindo-o quando ausente e quando vazio, de modo que o caminho sem anexos permaneça idêntico byte a byte.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 1, 22
- **Estado**: Done em 2026-09-02.
- **Defeito fechado**: o contrato publicado nomeava o manifesto e a função de identidade não o conhecia, portanto duas solicitações que diferiam só pelos anexos produziam o mesmo digest e a segunda recebia repetição em vez de conflito. O oráculo que provava o defeito passou a provar o fechamento: o digest do corpo com manifesto deixou de ser o digest do corpo sem manifesto.
- **Vetores dourados**: provados intactos por identidade de blob contra a versão commitada, e não apenas por diferença de estatística. A mutação que escreve o membro sempre deixa os dois vermelhos, o que prova que eles ainda guardam o caminho sem anexos.
- **Risco medido que viaja para quem mexer aqui**: a posição do membro na ordem canônica está protegida por um único oráculo. Sob a mutação que move o bloco de lugar, os digests do corpus continuam verdes, porque foram calculados sobre o corpo mínimo, que não tem o membro vizinho. Quem pega a regressão é o teste que soletra os bytes com o vizinho presente.
- **Aceitação**: Os vetores dourados congelados permanecem idênticos; corpo com manifesto vazio, corpo com `null` e corpo sem o membro produzem o mesmo digest.

### Tarefa 24: Implementar o leitor do barramento para o membro novo

- **Descrição**: Fazer o leitor do barramento transportar `attachments` no tópico único, como lista de referências opacas, preservando a tolerância aos demais membros desconhecidos.
- **Defeito atual que esta tarefa fecha**: o leitor vigente monta o comando lendo nomes um a um e **não lê** `attachments`, portanto um produtor que publicar manifesto recebe aceite de notificação sem anexos, com sintaxe válida e efeito diferente do pedido. Enquanto a superfície não transportar o membro, ela recusa com motivo declarado, e nunca prossegue em silêncio.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: 23
- **Estado**: Done em 2026-09-02.
- **Forma entregue**: dezenove linhas de produção, que leem o membro no vinculador e o repassam ao comando. Nada de opções de consumo, mapa de tópicos ou papel de trabalhador mudou, porque sob a decisão vigente não existe segundo tópico nem segundo tipo de evento, então o manifesto viaja no corpo do tipo já publicado e a topologia não se move. Cinco mutações de runtime, cuja união cobre todos os trinta e dois oráculos; a que reconstrói o estado exato do defeito anterior derrubou doze de treze testes de integração.
- **Provado além do pedido**: o manifesto entra na identidade do pedido também pelo barramento, verificado contra as duas autoridades, o caminho rápido em cache e o registro persistido. Sob a mesma chave, o corpo com manifesto é aceito e o corpo sem ele responde conflito, sobrando exatamente uma notificação.
- **O que os oráculos não provam**, declarado: a cláusula "antes do claim" não é medida diretamente, porque o ingresso ainda não executa claim de anexo, e o que está medido é que o caminho de aceite não rodou; nada persiste o manifesto, então um pedido com manifesto é aceito e nenhum arquivo fica vinculado a nada; recusas posteriores à confiança do produtor retêm o corpo original, que contém as referências, e só a família anterior à confiança está provada livre delas; e não há execução pelo laço real do consumidor.
- **Aceitação**: A correspondência entre tópico e type é obrigatória; solicitação sem anexos e solicitação com lista íntegra são aceitas; lista vazia, referência em branco, repetição, tipo errado ou publicação em tópico incompatível são recusados antes do hash e do claim; nenhum corpo que nomeie o manifesto é aceito sem que o manifesto seja transportado; a ordem vigente dos gates permanece inalterada.

### Tarefa 25: Persistir o snapshot do manifesto aceito com leitor tolerante

- **Descrição**: Gravar o manifesto aceito como documento anulável na linha da notificação, no mesmo `INSERT` do aceite, com leitor que distingue presente, ausente e ilegível. Configurar `AfterSaveBehavior.Throw` para impedir alteração rastreada depois da criação.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 11, 21
- **Estado**: Done em 2026-09-03, refeita do zero depois de uma primeira tentativa ter morrido sem nenhum oráculo executado.
- **Costura escolhida, com o fundamento**: a gravação acontece dentro do escritor de ingestão, entre o claim e a inserção da notificação. É o único instante em que o conjunto aceito e uma notificação ainda não inserida coexistem: o caso de uso monta a notificação antes de qualquer transação existir e não tem contexto nem transação, e o conjunto aceito só nasce depois da abertura da transação, porque o claim roda nela. Devolver o conjunto no desfecho do escritor entregaria tarde e forçaria alteração posterior à inserção, exatamente o que a decisão proíbe.
- **Restrição contrariada com medição, e confirmada**: a instrução era não criar migração. Foi medido que "coluna no modelo sem migração" reprova **toda** a superfície de integração no preparo da fixture, com o aviso de mudanças pendentes do mapeador, e que atualizar apenas o retrato do modelo é pior, porque silencia o aviso e deixa a coluna ausente do banco. A assimetria é real: o módulo de anexos não tem cadeia de migrações e cria o esquema do modelo, enquanto o de notificações tem vinte e sete arquivos e valida pendências. A migração gerada é descartável por construção, porque a Tarefa 35 esmaga a cadeia inteira.
- **Evidência de captura de SQL**: a inserção do aceite nomeia a coluna e um parâmetro carrega o documento, comparado como JSON e não como texto. A transição seguinte emite exatamente uma atualização, capturada literalmente, com dois vizinhos presentes e a coluna do snapshot **ausente**. Os vizinhos estão na asserção de propósito: sem um vizinho que deve mudar, uma captura vazia satisfaria a ausência sendo afirmada.
- **Matriz coberta**: ausência é apenas o valor nulo; presença exige envelope íntegro com itens não vazios, ordem preservada e cadeias devolvidas caractere a caractere. Os três buracos que o contrato publicado não enxerga ficam fechados por três ajustes distintos e com atribuição medida: recusa de membro adicional, exigência de membro obrigatório, que fecha também a grafia em outra caixa, e recusa de membro repetido. A recusa nunca cita o documento, provado com dado sensível plantado.
- **O que os oráculos não provam**, declarado: que nenhuma outra transição emite atualização com a coluna, já que uma transição foi medida e o resto se apoia na guarda de mapeamento; que a atualização em massa e o SQL cru não reescrevem o valor durável, porque já foi medido que atravessam a guarda e o gatilho pertence à Tarefa 35; a recusa de membro repetido através da coluna, porque o tipo do banco colapsa nomes repetidos na escrita; e a presença da coluna em cada partição.
- **Aceitação**: Testes unitários e integrados cobrem a matriz V1 e distinguem documento presente, ausente e ilegível; uma linha anterior à coluna continua avançando; a captura SQL comprova o snapshot no `INSERT` inicial e a ausência do snapshot em `UPDATE` posterior; alterar o snapshot depois da criação falha pela guarda do modelo antes da emissão de SQL.

### Tarefa 26: Ler o snapshot por pipeline, despacho e fallback

- **Estado**: Done em 2026-09-03. Falsificação executada pelo orquestrador com sete mutações de runtime, cada sítio com oráculo próprio e nenhum encobrindo o outro.
- **Descrição**: Fazer pipeline, despacho e fallback lerem o conjunto aceito exclusivamente da linha `notification` já carregada, sem consultar estado mutável do anexo e sem criar cópia persistida em tentativa, outbox ou mensagem. Uma representação transitória pode existir somente para a chamada em curso, e o descarte pós-veredito não pode alcançar o snapshot.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 25
- **Aceitação**: Alterar o estado do anexo entre o aceite e o fallback não muda o conjunto submetido; primeira tentativa, retry, fallback e fan-out não persistem nem transportam uma segunda autoridade do manifesto; o snapshot da notificação sobrevive ao veredito terminal.

### Portão de capacidade, resolvido e parcialmente aplicado em 2026-09-03

O portão de quantidade, tamanho, tipos e envelope foi resolvido por delegação do
dono, e a parte que já estava materializada em código foi corrigida. O envelope
por notificação e o teto por anexo são **7.340.032 bytes**, e a quantidade
máxima é **10**. O envelope é derivado, porque é a base ratificada sob a qual a
sonda de transferência mediu os três braços; o teto por anexo o acompanha porque
o que limita o custo é a soma; a quantidade é escolha de produto e governa
cardinalidade, ou seja, quantas linhas o claim trava e quantas leituras
integrais o preflight faz.

O defeito corrigido: o teto por anexo em código era 30.000.000 bytes, escolhido
pelo implementador e **maior que o teto duro do conjunto inteiro**. Ele saiu do
agregado e passou a vir de configuração validada na partida, onde a ausência ou
a invalidez da seção é **falha de partida** e não recusa silenciosa, porque
envelope ausente não pode significar zero.

Medição que corrigiu a premissa do despacho: não existia oráculo registrando
anexo entre sete mebibytes e trinta megabytes esperando aceitação. O maior
tamanho registrado por qualquer oráculo era de dois mil e quarenta e oito bytes.
Nada foi invalidado pelo aperto.

Lacuna declarada e não promovida: o limite de corpo do upload passou a ler a
configuração, e **nenhum teste em processo distingue isso de uma constante**,
porque o host de teste não aplica o recurso de tamanho máximo, que é do
servidor. A sonda foi escrita, reprovou, e foi removida em vez de promovida.
Fechar isso exige verificação fora do processo.

O envelope somado e a quantidade máxima **não têm consumidor**: quem os aplica é
o preflight e a composição do envio.

### Tarefa 27: Implementar o preflight antes do ponto irreversível

- **Descrição**: Revalidar liberação, identidade e envelope na janela entre a reivindicação da tentativa e a chamada ao provedor, liquidando a tentativa com código estável quando a revalidação reprova.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 26
- **Custo medido da releitura, encaminhado pela revisão da Tarefa 16**: a passagem de verificação não materializa o anexo e o conjunto vivo é constante, mas a taxa de alocação é proporcional ao tamanho. Medido por dois caminhos independentes: 1,446 byte alocado por byte lido em um mebibyte, 1,394 em quatro, e inclinação de 1,377 entre os dois pontos, com o braço contra fluxo local constante nos dois tamanhos, o que prende a inclinação ao fluxo do provedor e não ao laço. A segunda passagem também dobra o tráfego ao provedor no caminho de ingresso, e nenhum orçamento, portão ou limite de concorrência descreve esse eixo, porque o limitador de taxa concede permissões por principal sem ponderar bytes. Decidir o limite de concorrência e o orçamento com esses números na mão pertence a esta tarefa. Nenhum número foi inventado.
- **Errata acolhida em 2026-09-02**: no fluxo candidato da Tarefa 6, materializar o conjunto completo significa resolver todos os membros do conjunto de anexos, não bufferizar os bytes. A verificação alimenta o hash sem materializar, em passagem separada da passagem de envio. Foi medido que verificar e enviar na mesma passagem entrega todos os bytes ao chamador antes de o veredicto do digest existir, o que abandonaria a cláusula de zero chamadas ao provedor. Duas passagens integrais sobre a mesma versão fixada, com alocação constante, é o que preserva essa cláusula. Foi medido também que leitura por faixa não é validada pelo cliente e entrega bytes alterados sem sinal.
- **Estado**: Done em 2026-09-03.
- **Forma entregue, com o fundamento**: a revalidação acontece depois da reivindicação da tentativa, porque liquidar exige uma tentativa que este processo possua, e antes da revelação do destino, porque um envio que não vai acontecer não merece um contato em texto claro na memória. A verificação vive no módulo dono do conjunto e devolve veredito: levar os números da capacidade ao consumidor criaria uma segunda aritmética sobre a mesma regra, e resolver o manipulador de conteúdo do outro lado criaria uma segunda autoridade sobre a identidade. Quatro causas de retenção entram por uma palavra só, porque nada que o chamador faz depende de qual delas fechou.
- **Detalhe de forma que evita um defeito silencioso**: a soma do envelope é contada decrescendo do teto, e não somando. Somar pode estourar o inteiro antes da comparação, e uma soma estourada lê como número pequeno e **libera** o conjunto que deveria recusar.
- **Prova de zero chamadas**: medida pela contagem de requisições de um servidor HTTP real, e não por contador de dublê, com cada zero acompanhado de um um na mesma captura. Sete formas de reprovação cobertas. Indisponibilidade devolve a tentativa à fila sem veredito e sem marca de deduplicação, e a mesma mensagem sai depois do reparo, o que prova que nada foi liquidado.
- **Um oráculo mentia e a falsificação o revelou**: o teste integrado de cardinalidade tinha os dois braços de capacidade com o mesmo envelope apertado, de modo que um conjunto de dois membros violava as duas regras ao mesmo tempo, e a mutação que ignorava a cardinalidade deixava o integrado verde enquanto derrubava o unitário. Os braços foram reescritos para cada um declarar a capacidade inteira, deixando exatamente uma regra capaz de recusar.
- **Treze mutações de runtime, de um eixo só cada**, com portão pelo código de saída do build. Duas correram mal e ficam registradas: uma reversão abortou em silêncio porque a âncora vazia casou milhares de vezes e a mutação seguinte empilhou por cima, com a medição descartada e refeita; e outra morreu no portão do build por processo de teste segurando o binário, que é exatamente o que o portão existe para impedir.
- **O que os oráculos não provam**, declarado: que a revalidação roda antes da revelação do destino, já que só a posição depois da reivindicação tem prova indireta; nada sobre os bytes, porque esta verificação nunca abre conteúdo e divergência aqui é a geração nomeada pela liberação vigente, de modo que um objeto reescrito sob a mesma geração passaria; nenhum número de alocação e tráfego foi medido por código desta tarefa, todos são derivados; e não há oráculo de corrida real entre revogação e revalidação, nem de cancelamento durante a verificação.
- **Decisão pendente do dono, com a pergunta formulada**: não existe orçamento de bytes em lugar nenhum, e o limitador de taxa concede permissões por principal sem ponderar bytes. Derivado do que está medido, o pior caso por instância é 56 mebibytes em voo, 14,7 megabytes de egresso por notificação e cerca de 77 mebibytes de alocação transitória por rodada de oito envios. A pergunta é qual teto de bytes por unidade de tempo, por principal e por instância, a operação aceita gastar no provedor de objetos. Nenhum número entrou em configuração sem essa resposta, porque um limite inventado repetiria o defeito que o portão de capacidade acabou de corrigir.
- **Achado que viaja para a composição do envio**: o tempo limite configurado do provedor de e-mail é de cinco segundos e o envelope aprovado é de sete mebibytes, que codificados passam de nove megabytes na mesma janela.
- **Aceitação**: Vencimento, revogação, divergência, conjunto incompleto ou envelope excedido produz zero chamadas ao provedor falso e resultado explícito.

### Tarefa 28: Evoluir o contrato de despacho com representação neutra do conjunto

- **Descrição**: Acrescentar membro opcional com valor padrão ao pedido publicado e um tipo neutro para o item do conjunto, sem expor tipos do provedor de nuvem nem estado interno.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 27
- **Aceitação**: O contrato não expõe tipo de provedor nem estado interno; o inventário de igualdade de contratos é reparado ou registra a quebra.

### Tarefa 29: Implementar a submissão do conjunto integral no adaptador de e-mail

- **Descrição**: Compor o conjunto na forma exigida pelo provedor no único ponto de montagem do payload, usando o método de transferência promovido pela tarefa 9, com cálculo do envelope efetivo antes da chamada.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 9, 28
- **Aceitação**: A captura do payload transmitido comprova conjunto, nome, tipo, digest e comprimento; exceder o envelope produz falha antes da chamada.

### Tarefa 30: Produzir a evidência dos bytes submetidos

- **Descrição**: Registrar, por tentativa, a testemunha que relaciona o conjunto submetido ao conjunto liberado, no espírito dos digests de conteúdo já existentes, sem reter conteúdo bruto.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: 29
- **Aceitação**: A evidência permite comparar os bytes submetidos aos bytes liberados sem conter o conteúdo.

### Tarefa 31: Implementar roteamento e fallback com terminação explícita

- **Descrição**: Fazer o estágio de rota recusar de forma explícita todo plano incapaz de preservar o conjunto, com motivo publicado, sem conversão para link e sem remoção de anexos.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 27
- **Aceitação**: Um plano cujo canal elegível não preserva o conjunto termina com recusa auditável e nenhuma tentativa degradada em outro canal.

### Tarefa 32: Implementar a reconciliação de falhas parciais e a convergência de órfãos

- **Descrição**: Materializar em coluna com índice parcial somente o passivo de falhas externas ao commit atômico, como falhas de validação ou submissão e evidência pendente, para manter o controle da repetição no banco, não na fila. O claim não depende de reconciliação assíncrona.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 8
- **Estimativa**: 40h
- **Depende de**: 21, 30
- **Entrada obrigatória da Tarefa 16**: existem gerações duráveis que a aplicação nunca aprende. Foi medido que uma única chamada de escrita cuja resposta se perde na rede deixou onze gerações duráveis sem devolver versão alguma, e dezesseis com o limite de retentativa em cinco, enquanto um cliente HTTP puro no mesmo cenário produziu uma. A repetição é do cliente do provedor, e o mecanismo interno não foi identificado. A escrita condicional limita a amplificação a uma geração e traduz o cenário em conflito de upload, que é a assinatura observável do órfão. A chave do objeto é sempre derivável do registro, então a reconciliação por prefixo dispensa registro novo. Uma linha de intenção anterior à escrita volta a ser opção aqui, como otimização de custo de varredura, ao preço de um segundo commit no caminho feliz.
- **Aceitação**: Todo passivo externo ao commit converge em uma rodada; a matriz confirma que não existe órfão de claim; o plano de consulta usa índice parcial e não varre partição.

### Tarefa 33: Implementar o descarte seguro de abandonados

- **Descrição**: Aplicar a regra de descarte aprovada, respeitando a proteção obrigatória enquanto houver dependência ativa, incluindo os estados qualificados no requisito.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 17, 32
- **Entrada obrigatória da Tarefa 16**: o descarte é por versão exata. Foi medido que a exclusão sem versão não apaga nada, cria marcador e devolve sucesso, deixando a geração durável e legível. Marcadores de exclusão são entrada distinta na enumeração, e o indicador que os identifica chega nulo para versão normal, não falso. A política de expurgo das linhas de geração pertence a esta tarefa.
- **Aceitação**: A varredura remove apenas abandonados sem dependência viva, e nunca um anexo vinculado a notificação ativa.

### Tarefa 34: Implementar a evidência operacional reconstruível

- **Descrição**: Implementar `NotificationEvidenceReader` e publicar `NotificationEvidence` para a consulta autorizada que relaciona aplicação, anexo, integridade, validação, snapshot aceito da notificação, tentativa e resposta do provedor, de forma minimizada e sem conteúdo bruto.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 30, 32
- **Aceitação**: Testes do leitor e do contrato comprovam que toda tentativa aceita pelo provedor é reconstruível a partir da autoridade da notificação, incluindo identidade e composição do snapshot aceito; nenhuma evidência comum contém conteúdo bruto nem detalhe de armazenamento.

### Tarefa 35: Escrever as migrações aditivas e regenerar o snapshot de modelo

- **Descrição**: Esmagar a cadeia de migrações existente numa migração inicial por contexto, gerada pela ferramenta, cobrindo o schema completo já com anexos e com a coluna do manifesto. Regenerar o snapshot de modelo pela ferramenta, sem edição manual.
- **Decisão do dono em 2026-09-02**: como o serviço é novo e não tem nada em produção, a cadeia vigente de setenta e quatro arquivos em seis contextos é substituída. Nota técnica: a ferramenta gera migração por contexto, portanto serão seis iniciais, uma por contexto, e não um arquivo único.
- **Cerimônia suspensa**: tempo limite de bloqueio explícito, ensaio de contenção no pai e numa partição, e a exigência de coluna anulável sem default existem para alterar tabela viva. Ficam suspensas e voltam a valer no primeiro DDL sobre tabela com dados em produção. Continuam valendo o snapshot gerado pela ferramenta e a presença da coluna no pai e em todas as partições.
- **Evidência que o esmagamento apaga**: duas migrações citadas por caminho e linha na decisão do snapshot deixam de existir. O precedente de coluna anulável em `jsonb` continua verificável pela configuração; o padrão manual de tempo limite de bloqueio perde lastro documental.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 3
- **Estimativa**: 16h
- **Depende de**: 25
- **Entrada obrigatória da Tarefa 16**: a tabela de gerações recebe o gatilho de rejeição de mutação no dialeto já usado pelo projeto, rejeitando alteração e exclusão inteiras, sem cláusula condicional. Foi medido que a forma condicional por coluna rejeita também alteração de coluna não relacionada e reprovaria a transição de estado do anexo. Foi medido que a criação de esquema pela ferramenta de contexto cria zero gatilhos, mesmo com o gatilho declarado no modelo, portanto nenhum oráculo anterior a esta tarefa pode se apoiar nele, e a fixture não executa instrução de esquema que a produção não tenha. A chave estrangeira da tabela de gerações usa comportamento restritivo de exclusão, decidido de forma reversível porque o módulo ainda não tem migração; ao materializá-la junto com o gatilho, verifique que a combinação não torne o anexo indelével de forma indesejada, já que o gatilho recusa exclusão na tabela filha.
- **Aceitação**: A aplicação migra sem recusar por mudança pendente de modelo; a coluna existe no pai e em todas as partições existentes; a migração é apenas de catálogo, sem reescrita do heap; o ensaio de bloqueio no pai e em uma partição comprova o limite local de três segundos na mesma transação.

### Tarefa 36: Implementar a habilitação progressiva e o rollback lógico

- **Descrição**: Separar o bloqueio de novos aceites do processamento dos itens já aceitos, publicando leitores tolerantes antes dos escritores e preservando dados na reversão.
- **Tipo**: Implementation
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 24, 31, 34, 35
- **Obrigação de rollout acrescentada em 2026-09-02**: habilitar o versionamento no bucket de produção é pré-requisito da funcionalidade e não tem decisão de infraestrutura registrada. Enquanto ele não existir, o upload falha fechado, porque a resposta de escrita não traz versão e a captura recusa. A habilitação traz custo de armazenamento acumulativo e exige política de ciclo de vida.
- **Aceitação**: Os controles são implantados desabilitados; o caminho sem anexos e os itens já aceitos continuam processáveis; desabilitar novos aceites mantém leitura, tentativa, reconciliação e investigação dos itens existentes; a reversão lógica não apaga dados; nenhuma habilitação operacional ocorre antes do ensaio da Tarefa 37 e da publicação da documentação do produtor.

### Tarefa 37: Executar o ensaio de habilitação e reversão

- **Descrição**: Exercitar a matriz de rollout contra a mesma base: leitor antigo com schema novo e SQL `NULL`; leitor novo com linha nula produzida pelo writer antigo; leitor novo com writer novo; e leitor antigo com writer novo e documento não nulo como combinação proibida. Verificar também que o caminho sem anexos permanece idêntico, que nenhum replay legítimo vira conflito e que a versão V1 continua recusando anexos.
- **Tipo**: Test
- **Responsável**: Senior Dev
- **Pontos de história**: 5
- **Estimativa**: 24h
- **Depende de**: 36, 38
- **Aceitação**: As três combinações compatíveis passam, a combinação proibida é bloqueada antes da habilitação operacional, a versão V1 continua recusando anexos, o caminho sem anexos permanece idêntico e nenhum replay legítimo devolve conflito.

### Tarefa 38: Atualizar o guia do produtor e os motivos de recusa

- **Descrição**: Revisar a proibição explícita de anexos no registro do barramento e acrescentar uma linha na tabela de motivos para cada recusa nova, porque essa tabela é verificada por função de adequação.
- **Tipo**: Docs
- **Responsável**: Senior Dev
- **Pontos de história**: 2
- **Estimativa**: 8h
- **Depende de**: 24, 31
- **Aceitação**: A função de adequação do catálogo do guia passa; a proibição vigente foi revista para refletir a capacidade.

## Prompt semente para IA

```txt
Context:
Monólito modular .NET 10 com módulos de notificações, despacho, auditoria, consentimento de
contato, compliance e gestão de templates. Persistência em PostgreSQL por schema, mensageria por
fila e barramento, custódia de objetos e chaves gerenciadas já disponíveis no host. Sem telemetria
de plataforma.

Goal:
Produzir o Context Pack e o plano técnico para acrescentar gestão de anexos ao hub, com contexto
delimitado próprio, contrato publicado de claim, snapshot imutável do manifesto aceito e submissão
integral ao provedor de e-mail.

Constraints:
Sem mediator. Dependência entre contextos somente sobre a superfície publicada e versionada. Domínio
livre de infraestrutura e de tipos do provedor de nuvem. A fila de saída é acrescentada antes do
registro de auditoria na transação de aceite. Sem elevação de nível de isolamento no caminho de
aceite. Migração inicial esmagada e snapshot de modelo regenerado pela ferramenta.
Mensagens públicas em português brasileiro, identificadores em inglês. Nomes de teste descrevem
comportamento, nunca numeração. Artefatos de implementação não citam identificadores de
especificação.

Acceptance criteria:
Referência opaca só utilizável após liberação; recusa antes de notificação aceita para referência
inválida; conjunto integral submetido com identidade preservada; isolamento entre aplicações;
conteúdo hostil nunca liberado; terminação explícita quando o conjunto não pode ser preservado;
evidência reconstruível sem conteúdo bruto; caminho sem anexos idêntico byte a byte; replay e
conflito corretos; convergência sob falha parcial; dependência ativa nunca descartada; nenhuma
sentinela nas superfícies coletadas; preflight antes do ponto irreversível; recusa de conteúdo não
inspecionável; oráculos criados antes das mudanças que verificam.

Risks:
Alterar a forma canônica sem vetor dourado congelado torna a regressão indetectável. Compensação
posterior deixa janela de aceite sem claim integral. Passo de claim depois do registro de auditoria
alarga um bloqueio global. Manifesto vazio tratado como presente quebra a unicidade do caminho sem
anexos.

Expected output:
Plano em tarefas com dependências, exemplo de configuração, lista de testes de unidade, integração,
contrato, arquitetura e desempenho, funções de adequação, e README de convenções do módulo novo.
```

## Definição de pronto

- [x] Problema claro.
- [x] Resultado esperado descrito.
- [x] Critérios de aceitação verificáveis.
- [x] Escopo e fora de escopo explícitos.
- [x] Riscos conhecidos.
- [x] Tarefas decompostas com responsáveis, pontos de história e horas correspondentes.
- [x] Artefatos obrigatórios identificados.
- [x] Responsável definido.

## Definição de concluído

- [ ] Toda linha do Contrato de evidência está verificada no limite, com avaliação independente confirmada.
- [ ] Toda linha aplicável de Obrigações de qualidade está verificada, e toda disposição permanece sustentada por evidência.
- [ ] Critérios de aceitação atendidos.
- [ ] Testes de unidade cobrindo a forma canônica, a máquina de estados, a autorização e os leitores tolerantes.
- [ ] Testes de integração cobrindo ciclo externo, falhas injetadas, corrida de chave idempotente, reentrega, preflight e submissão ponta a ponta.
- [ ] Funções de adequação rejeitando dependência entre contextos fora da superfície publicada, tipo do provedor de nuvem no domínio, motivo de recusa sem linha no guia e quebra não registrada de contrato publicado.
- [ ] Aprovações formais de Segurança e de Produto registradas no Pull Request.
- [ ] README de convenções do módulo novo publicado.
- [ ] Pull Request aprovado.
- [ ] Pipeline verde, com build sem aviso, dependências sem vulnerabilidade alta e varredura de segredos sem achado.
- [ ] Habilitação progressiva e rollback exercitados em homologação.

## Critério de encerramento

A Delivery Slice está pronta para engenharia porque tem problema claro, resultado observável, escopo explícito, critérios de aceitação verificáveis, contrato completo de evidência e de obrigações de qualidade, regras de negócio, restrições derivadas do refinamento com citação de comportamento vigente, riscos com tratamento, quebra de tarefas com responsáveis, pontos de história e horas correspondentes, e uma definição de concluído que cobre teste, segurança, produto e operação. A dobra das 12 sementes está declarada, e a ordenação que normalmente viveria em arestas entre fatias está preservada na dependência entre tarefas.
