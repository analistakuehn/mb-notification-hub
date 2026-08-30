---
language: pt-BR
---

# Módulo Notifications

## Limite

- Mantenha um único bounded context neste módulo: o ciclo de vida da
  notificação, da ingestão ao dispatch. Esta unidade entrega a ingestão REST, a
  entrada pelo barramento que consome os tópicos dedicados dos producers
  declarados em `Modules:Notifications:KafkaIngress:Bindings`, o pipeline Core
  que consome as queues `core-*`, a slice de dispatch (os consumers
  `dispatch-*`, a máquina de estados das tentativas a partir de `queued`, o
  fan-out de push e o handler de fallback), o rastreamento de entrega em
  `Features/DeliveryTracking/` (a rota `POST /webhooks/{provider}`, a
  deduplicação e a evidência do feedback do provedor, e a aplicação assíncrona
  da máquina de estados do attempt), os eventos de resultado de saída em
  `notifications.events.v1` e a API de consulta somente leitura protegida por
  `Notifications.Read`. A API de auditoria (`/v1/audit/*`) permanece fora desta
  unidade: o conteúdo renderizado e o contato completo saem por ela, nunca pela
  superfície de consulta.
- Mantenha as invariantes nas entidades e funções puras em
  `src/Platform.Api/Modules/Notifications/Domain/`.
- Mantenha a orquestração dos casos de uso nas slices em
  `src/Platform.Api/Modules/Notifications/Features/`.
- Leia contextos irmãos exclusivamente por seus contratos publicados:
  `Modules.TemplateManagement.Integration.V1` (catálogo publicado, validação de
  variáveis, renderer e contrato de regra de política),
  `Modules.ContactConsent.Integration.V1` (diretório de destinatários, revelação
  de contato e token, ciclo de vida do token do dispositivo, ledger de
  supressão),
  `Modules.Dispatch.Integration.V1` (providers de canais e sua resolução, mais
  a verificação de assinatura e a normalização de feedback de provedor) e
  `Modules.Audit.Integration.V1` (acréscimo transacional de auditoria). Nunca
  acesse o armazenamento de dados nem os tipos internos de outro contexto.
- A Infrastructure da plataforma é uma dependência, não um contexto irmão: o
  writer da outbox (`NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter`),
  a superfície de consumo do SQS
  (`NotificationHub.Api.Infrastructure.Messaging.Consuming`), a cifra de
  envelope (`NotificationHub.Api.Infrastructure.Cryptography.IEnvelopeCipher`)
  e o provisionamento de partições ficam fora de `Modules.*` e nunca
  referenciam de volta os tipos do módulo.

## Superfícies sob responsabilidade

| Caminho | Responsabilidade |
|---|---|
| `src/Platform.Api/Modules/Notifications/Domain/` | entidades de notificação, tentativa, avaliação de política e idempotência, vocabulário de classes, JSON canônico, mascaramento de variáveis e forma pública do id |
| `src/Platform.Api/Modules/Notifications/Features/` | slices verticais deste contexto: o pipeline Core em `Features/Pipeline/`, o consumer de dispatch em `Features/Dispatching/`, o handler de fallback em `Features/Fallback/`, a administração e os gates do kill switch em `Features/KillSwitch/`, o rastreamento de entrega em `Features/DeliveryTracking/` (recepção em `Webhooks/`, aplicação em `Events/`, varreduras em `Scheduling/` e reconciliação em `Reconciliation/`) e as slices de consulta em `Features/History/` |
| `src/Platform.Api/Modules/Notifications/Infrastructure/` | persistência (schema `notifications`, contextos de escrita e somente leitura), controles Redis, gate de template, privacidade, cache do kill switch e ciclo de vida dos holds, gerenciador de partições, jobs de purge, writer de commit do pipeline, writer de dispatch de tentativas, writer da evidência de entrega, esquema de autenticação por assinatura de provedor, poison sinks, auxiliares de transporte para consultas e leitor de histórico |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | registro de serviços e mapeamento de endpoints deste contexto |
| `src/Platform.Api/Modules/Notifications/Integration/V1/` | contrato publicado deste contexto: o catálogo canônico de motivos de rejeição |
| `src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs` | composição da função de worker `core`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs` | composição da função de worker `dispatcher`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/KafkaIngressWorkerRole.cs` | composição da função de worker `kafka-ingress`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/NotificationsMaintenanceWorkerRole.cs` | composição da função de worker `notifications-maintenance`, descoberta pelo host de workers: varredura de conteúdo renderizado, backfill e reconciliação diária de entrega |
| `src/Platform.Api/Modules/Notifications/DeliveryTrackerWorkerRole.cs` | composição da função de worker `delivery-tracker`, descoberta pelo host de workers |

Estado sob responsabilidade: `notification`, `notification_attempt`,
`policy_evaluation` e `delivery_event` (tabelas-pai particionadas mensalmente),
`idempotency_key`, `producer_registry`, `provider_event_dedupe`, `kill_switch`
e `kill_switch_hold`. A `outbox` e
`processed_messages` da plataforma pertencem à Infrastructure de mensageria;
este módulo apenas escreve por meio dos contratos correspondentes, em sua
própria transação.

## Invariante transacional da ingestão

A aceitação de uma requisição confirma quatro escritas em uma única transação
de banco de dados ou nenhuma: a linha `notification`, a linha `idempotency_key`,
a mensagem em `platform.outbox` e o `audit_event` acrescentado por `IAuditTrail`
com a `DbTransaction` bruta. O acréscimo de auditoria mantém o bloqueio da cadeia
de partições até o término da transação, portanto o commit ocorre imediatamente
depois (`Infrastructure/Persistence/IngestionWriter.cs`). Uma rejeição ou
duplicidade não produz efeito de negócio e registra sua trilha em uma transação
curta própria.

## Um caso de uso de ingestão, dois transportes

- `Features/Ingress/RequestNotification/` é neutro em relação ao transporte.
  Ele recebe uma decisão de autorização já tomada, a origem da requisição e
  a chave de idempotência; responde com dados. Toda rejeição é um resultado
  legítimo, portanto a rota a mapeia para um problema RFC 9457, enquanto a
  entrada pelo barramento a mapeia para um registro de dead-letter, sem que
  nenhuma das duas reimplemente uma regra. A validação da forma é executada
  primeiro dentro do caso de uso, de modo que uma requisição ilegível receba a
  resposta correspondente mesmo quando o producer também falharia na
  autorização. A rota **não** contém filtro de validação: responder antes do
  caso de uso rejeitaria o mesmo defeito com o corpo do framework aqui e com
  `payload-invalid` no barramento, além de deixar a recusa síncrona sem trilha e
  sem evento de rejeição. O 400 publicado mantém o dicionário `errors` por campo
  e recebe o código do catálogo como `type`. A consequência aceita é a ordem:
  um corpo malformado sem `Idempotency-Key` recebe primeiro a resposta sobre a
  chave ausente, pois a trilha precisa dela para identificar a entidade que
  registra.
- A autorização é resolvida por transporte antes da execução do caso de uso:
  `RestProducerAuthorizer` sobre os app roles do Entra (motivo
  `class-not-allowed-for-principal`) e `KafkaProducerAuthorizer` sobre
  `producer_registry` para o producer lógico derivado do tópico consumido
  (motivo `producer-not-authorized`).
- A trilha de um resultado sem efeito de negócio é escrita por
  `IIngestionSink`. O fluxo síncrono a confirma imediatamente; o fluxo do
  barramento a retém até a existência do registro de dead-letter e então a
  confirma com a marca de deduplicação.

## Registro de producers

- `producer_registry(principal, application, class, updated_at)` concede a um
  principal do barramento uma classe de uma aplicação. Não há coluna de
  habilitação, propositalmente: uma linha desativada seria uma alavanca lenta
  fingindo ser uma parada de emergência, e o corte de um producer é feito pelo
  kill switch em conjunto com a ACL do broker.
- A forma canônica consiste em dados declarativos do repositório de
  Infrastructure, materializados por um job de deploy; o hub apenas os lê por
  meio de um snapshot cuja idade absoluta nunca ultrapassa sessenta segundos.
  A idade e a validade do snapshot são calculadas exclusivamente com timestamp
  monotônico; o instante UTC serve apenas para diagnóstico ou persistência e
  nunca como base do TTL.
  Um TTL de cache menor atualiza antes; um valor maior não pode estender a
  autorização além de sessenta segundos. Uma falha de atualização nesse limite
  fecha o gate do consumer. Não há carga inicial de configuração: uma segunda
  fonte de autorização fora da trilha auditável permitiria que uma concessão não
  revisada chegasse à produção.
- Um registro vazio fecha o gate do consumer: a função `kafka-ingress` não
  realiza a subscrição e relata estado não saudável. Uma tabela vazia é
  indistinguível de uma materialização que nunca foi executada e, com um dia de
  retenção do tópico, um deploy fora de ordem enviaria um dia de tráfego legítimo
  ao tópico de dead-letter enquanto todas as probes relatariam sucesso.

## Entrada pelo barramento

- `Features/Ingress/` consome cada tópico dedicado configurado em um único
  consumer group, um registro por vez, com offsets confirmados por lote de poll
  e semântica at-least-once resolvida por `platform.processed_messages` com a
  chave `{topic}:{partition}:{offset}`.
- Cada tópico de entrada se vincula a exatamente um producer lógico, e cada
  producer lógico se vincula a exatamente um tópico. A ACL do broker é o único
  limite de autenticação do writer; o hub deriva o producer autoritativo apenas
  do tópico consumido. O `source` do CloudEvents, os campos do payload e cabeçalhos
  como `producer` são diagnósticos não confiáveis e nunca autorizam. O producer
  mapeado orienta a verificação do registro, `RequestedBy`, o ator da auditoria
  e os diagnósticos de dead-letter. Tópicos desconhecidos falham em modo fechado
  antes de qualquer efeito.
- A ordem das verificações do Kafka é parte do contrato: vínculo do tópico,
  envelope e tamanho, tipo do envelope, validação da forma, kill switch do
  producer, registro do producer, idempotência, orçamento do destinatário,
  catálogo publicado, restrição de variáveis sensíveis, schema das variáveis e
  persistência. O tipo do envelope é verificado antes do binding do corpo, pois
  esse tipo é a versão do schema, e uma versão posterior poderia fazer o binding
  por mera coincidência dos nomes dos campos; a recusa é
  `event-type-unsupported`, membro próprio do catálogo, para que o producer
  diferencie "seu corpo está errado" de "sua versão não é a usada por este
  tópico". O registro é verificado antes do catálogo para que uma recusa nunca
  revele quais templates existem; a restrição de variáveis sensíveis é
  verificada antes da validação do schema porque essa validação relata achados
  exatamente sobre o payload que não deve ser inspecionado; a idempotência é
  verificada antes do orçamento para que um replay legítimo nunca o consuma.
- **Um corpo que analisa mas não transcreve é recusado inteiro, antes de
  qualquer campo.** Um escape pode nomear um substituto que o corpo nunca
  pareia, e isso é texto JSON legal: o leitor aceita, o valor liga sem
  reclamar, e só a reescrita para UTF-8 descobre que o escape não nomeia
  caractere. Ler qualquer campo é o que reescreve, e a busca por um nome
  desescapa as chaves candidatas para compará-las, então qual leitura tropeça
  primeiro depende dos nomes procurados e do comprimento deles. Medido, duas
  escapadas em uma chave já quebram a busca de um nome de nove caracteres, e
  por isso a recusa cobre o corpo inteiro e nunca a lista de campos de hoje,
  que reabriria no dia em que um campo fosse acrescentado ou renomeado. São
  dois pontos, e os dois são deliberados: o envelope, no analisador de
  CloudEvents, que responde `cloudevent-unreadable-text`, e o `data`, no
  binder, que responde `payload-invalid`. O envelope precisa do seu próprio
  ponto porque é lido fora da retentativa que o consumidor envolve em volta do
  processador, de modo que um lançamento ali derruba o serviço consumidor
  inteiro em vez de um registro. A medida que responde é a mesma dos tetos de
  tamanho, e ela nunca lança: nada aqui relê o que é um substituto, porque o
  runtime já é dono dessa regra e uma segunda leitura dela pode discordar da
  que de fato transcreve.
- **Uma falha determinística nunca chega ao transporte como exceção.** O
  consumidor classifica toda exceção como transitória: tenta quatro vezes com
  backoff e então pausa a partição sem confirmar o offset, que é retomada e
  encontra o mesmo registro. A política é permanente vai para a dead-letter,
  transitório pausa a partição, e uma falha determinística que lança é
  classificada ao contrário do que ela é, o que trava a partição
  indefinidamente em vez de deixar um registro de dead-letter que alguém possa
  ler. Quem acrescentar leitura de payload no caminho do barramento devolve
  recusa, nunca lança. O ponto de classificação continua o que é, e a
  assimetria fica registrada aqui: nada hoje impede uma exceção nova de ser
  lida como transitória.
- Um erro permanente registra primeiro o registro de dead-letter, depois
  confirma a trilha e a marca de deduplicação e, por fim, o offset. Uma marca
  escrita primeiro faria o replay de uma falha ignorar um registro que nunca foi
  registrado.
- Para `payload-invalid`, `event-type-unsupported`, `producer-disabled` e
  `producer-not-authorized`, a DLT reconstrói o corpo com base em uma lista de
  campos de diagnóstico permitidos; não copia o envelope original nem qualquer segredo, PII
  ou valor proveniente da entrada ainda não confiável. O motivo
  `sensitive-variables-on-bus` mantém sua sanitização específica: a restrição
  depende apenas da declaração de variáveis sensíveis pelo template, nunca da
  presença delas no payload, e substitui `data.variables` pelos nomes
  declarados. Se o corpo não puder ser interpretado com segurança, nenhum dado
  dele é preservado. Um cabeçalho anuncia qualquer uma das formas de sanitização: o
  tópico de entrada mantém os registros por um dia, enquanto a DLT os mantém
  por duas semanas, portanto o controle nunca pode copiar o segredo para uma
  retenção quatorze vezes maior.

## Kill switch

- `kill_switch` é o estado canônico no PostgreSQL para os escopos `producer`,
  `application` e `channel`. A rota administrativa é
  `PUT /v1/notifications/kill-switch/{scope}/{key}`. Ela exige
  `Platform.Admin`, obtido operacionalmente por meio do PIM, identifica o ator
  por `oid` e depois por `sub` e confirma a transição de estado e a entrada de
  auditoria `kill_switch.changed` na mesma transação. Um estado repetido é um
  no-op.
- `KillSwitchCache` carrega um único snapshot para chamadores concorrentes
  (single-flight), o disponibiliza por no máximo cinco segundos e falha em modo
  fechado após uma falha de carregamento ou atualização. A janela de validade é
  medida por timestamp monotônico; o vencimento em UTC exposto pelo status é
  apenas diagnóstico, enquanto UTC permanece reservado aos instantes
  persistidos.
  `notifications-kill-switch` relata a integridade do snapshot. Cada processo
  possui seu próprio cache; não infira propagação entre instâncias com base em
  um teste de unidade local.
- Cada gate é executado antes do efeito que protege. O Kafka verifica o switch
  do producer após validar a forma e antes do registro de producers. O REST
  permite primeiro a resolução de um replay idempotente seguro e depois
  verifica o switch do producer antes do rate limit ou da aceitação. Core e
  Fallback levam o trabalho expirado ao estado terminal antes de avaliar o gate
  da aplicação; somente o trabalho ainda válido pode ser colocado em hold. O
  dispatch avalia Application e Channel, nessa ordem, antes da resolução do
  provider e do claim, e avalia ambos novamente imediatamente antes da chamada
  ao provider. O hold registra exatamente o escopo e a chave que bloquearam o
  trabalho; uma parada observada após o claim reverte a tentativa para `queued`
  na mesma transação que abre o hold. Um snapshot indisponível não cria hold: o
  REST retorna indisponibilidade, o Kafka tenta novamente, e Core ou dispatch
  adiam sem aceitação, avanço ou chamada ao provider.
- Uma aplicação ou um canal bloqueado grava um `kill_switch_hold` durável cuja
  chave é composta pelo tipo e pelo id do trabalho. O payload é uma verificação
  de claim com os identificadores da notificação e, quando necessário, da
  tentativa; ele nunca contém conteúdo renderizado, dados de contato, PII ou
  token de dispositivo. O destino e a expiração originais são mantidos para que
  o roteamento não seja reconstruído. A chave única `(work_kind, work_id)`
  mantém no máximo um hold ativo por trabalho. Um novo ciclo bloqueado reabre
  atomicamente o hold já liberado, atualiza o escopo que bloqueou e incrementa a
  versão; o replay do mesmo ciclo enquanto o hold permanece ativo é idempotente.
- `KillSwitchHoldReleaseService` faz uma varredura por segundo em lotes de 100.
  A consulta aplica a elegibilidade, switch inativo ou hold expirado, antes de
  selecionar os 100 candidatos, para que holds bloqueados não ocupem o lote nem
  causem head-of-line blocking. Cada candidato recebe um claim concorrente por
  atualização condicional de um hold ainda não liberado, e `released_at` é
  confirmado junto com a linha de retomada na outbox em uma transação. Assim,
  releasers concorrentes acrescentam uma única mensagem de retomada; uma falha
  na outbox deixa o hold sem liberação. Holds expirados de Core e Fallback
  alcançam o estado terminal antes de qualquer novo gate; um hold expirado de
  dispatch retoma pelo caminho de fallback e nunca chama o provider bloqueado.
- **A parada automática de canal nasce desligada.** Com
  `Modules:Notifications:AutomaticChannelKillSwitch:Enabled`, o papel
  `dispatcher` transforma um circuito de provedor aberto por mais de dez
  minutos seguidos na ativação do kill switch de canal, com ator de sistema e a
  mesma trilha `kill_switch.changed` da transição humana, mais o motivo
  `provider-circuit-open` nos detalhes. O gate existe porque a observação do
  circuito é por processo e a parada é global: uma única instância degradada
  pararia o canal para toda a frota. Com SMS como último passo do plano, parar o
  canal deixa código de autenticação esperando até vencer. A volta é sempre
  humana, pela rota administrativa; nada aqui reativa canal, porque a condição
  que disparou a parada não diz nada sobre ser seguro voltar.
- O gate operacional de rollout permanece externo: em um ambiente
  representativo com várias instâncias, a ativação de cada escopo deve resultar
  em zero novos efeitos protegidos após `t0 + 10 s`. Uma ACL exclusiva de writer
  do Kafka por tópico de producer e o comprovante de ACL/drift do ambiente real
  permanecem controles independentes e bloqueantes da Infrastructure. A
  consulta estrita ao Microsoft Graph, inclusive a validação fechada de host,
  payload e paginação, pertence à ferramenta `tools/Platform.GoLiveChecks`; ela
  comprova a ausência de atribuições operacionais sem expor o token em log ou
  recibo.
- **A administração do kill switch está sem observabilidade nas três camadas, e
  isto é dívida registrada, não desenho.** O desenho do sistema nomeia o kill
  switch manual como compensação do fail open do rate limit: é o controle que
  resta de pé quando o outro cede. Hoje a fatia não tem
  `KillSwitchAdministration.Handler.Logger.cs` nem `ILogger` injetado, enquanto
  a parada automática irmã emite evento em `Critical` com mensagem que termina
  mandando o operador ao caminho manual; o endpoint não aplica
  `.WithRequestLogging()`, e é uma das duas rotas do host fora do filtro; e o
  host não tem log de acesso, porque `Program.cs` não usa `UseHttpLogging`,
  `W3CLogging` nem equivalente. O traço de quem parou ou religou um escopo é
  zero, e não traço sem ator.
- Junte-se a isso que os dois `catch` do handler traduzem
  `DbUpdateConcurrencyException` e violação de unicidade em
  `Result.Success(... Conflict: true)` depois do rollback. Um conflito no
  controle compensatório volta ao chamador como sucesso, sem log, sem trilha e
  sem linha de requisição.
- Sequenciamento para fechar isto: primeiro o logger de fatia, depois o filtro
  no endpoint, depois a decisão explícita sobre os dois `catch`. Só então valem
  os portões que dependem deles, o de evento de log em `catch` que envolve
  escrita de trilha e o de cobertura do filtro em todo endpoint mapeado. Escritos
  antes, os dois nascem com esta fatia como violação e forçariam uma isenção
  nomeada.

## Eventos de resultado de saída

- `Infrastructure/Events/NotificationEvents.cs` constrói as linhas do
  CloudEvents em `notifications.events.v1`, lendo o nome do tópico e a URN de
  origem do hub na superfície de mensageria da plataforma em vez de declará-los
  aqui: o barramento de saída é um contrato de transporte, e ContactConsent
  publica `consent_changed` no mesmo tópico. O módulo é responsável pelos tipos
  de evento e pelas formas dos payloads: `rejected` na ingestão e no pipeline,
  `failed` quando o plano se esgota e na expiração e `delivered` quando a
  entrega é confirmada. A aceitação não anuncia nada (o producer já tem seu
  202), assim como uma rejeição pelo orçamento do principal, pois um evento por
  requisição recusada é exatamente a tempestade que o controle existe para
  impedir.
- **`delivered` afirma entrega confirmada, e não aceitação.** A única exceção
  declarada é o push na última etapa do plano: aquele provedor não reporta nada
  depois de aceitar, e ali nenhuma etapa posterior poderia socorrer a mensagem,
  então a aceitação é o desfecho mais forte que este hub vai conhecer. Um
  `fallback_deadline` gravado é a prova de que existe etapa posterior. Encerrar
  a notificação antes disso mataria o fallback por prazo, porque o handler de
  fallback trata qualquer estado diferente de `dispatched` como duplicata.
- Cada evento é escrito pela outbox dentro da transação do efeito que relata e
  **antes** do acréscimo por `IAuditTrail`, pois esse acréscimo mantém o bloqueio
  da cadeia de partições até o término da transação, e qualquer item colocado
  na queue depois dele amplia a janela de espera da ingestão concorrente. Isso
  se aplica a `IngestionWriter`, `PipelineCommitWriter`, `AttemptDispatchWriter`
  e `NotificationPlanOutcome`.
- A faixa de um evento de saída é a classe de sua notificação, nunca `auth`: a
  faixa `auth` protege a latência de entrega de um código de autenticação, e um
  evento de resultado não é uma entrega.
- O motivo de uma rejeição sempre pertence a
  `Integration/V1/NotificationRejectionReasons`; o motivo de uma falha pertence
  ao vocabulário de erros de entrega, que é uma decisão pendente.

## Contrato de idempotência

- Escopo `(application, idempotency_key)`; a autoridade é a chave primária de
  `idempotency_key`, nunca o caminho rápido do Redis.
- Um replay dentro de 24 h com o mesmo hash canônico do payload responde 200 com
  o id original da notificação; a mesma chave com um hash diferente responde
  409.
- A entrada do Redis (`idem:{application}:{key}`, TTL de 24 h) é escrita somente
  após o commit; uma entrada ausente ou malformada é um miss, e o banco de dados
  decide.
- O job de purge (`Modules:Notifications:IdempotencyPurge`) remove registros com
  mais de 24 h, de modo que um replay além da janela cria deliberadamente uma
  nova notificação.
- O hash canônico do payload está documentado em
  `Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs`.

## Pipeline Core

- Um único `NotificationContext` mutável atravessa a lista ordenada de estágios
  Validate, Resolve, Policy, Render e Route; ao final, o commit escreve tudo o
  que a execução produziu em uma única transação de banco de dados: a transição
  da notificação, a primeira tentativa (`queued`, com `fallback_deadline`
  gravado no momento do enfileiramento), as linhas `policy_evaluation`, a mensagem na
  outbox para `dispatch-{channel}-{class}` ou `dispatch-{channel}-auth`
  (verificação de claim `{notificationId, attemptId}`), o `audit_event` por
  `IAuditTrail` e a marca de deduplicação do consumer em
  `platform.processed_messages`
  (`Infrastructure/Persistence/PipelineCommitWriter.cs`).
- Uma rejeição de negócio é um resultado explícito de estágio com motivo
  estável, nunca uma exceção; uma exceção inesperada se propaga, a mensagem
  retorna à queue com backoff e somente a política de redrive chega à DLQ. A
  ausência de uma política de classe publicada é uma falha operacional, não uma
  rejeição.
- Estados de notificação escritos pelo commit: `dispatched` (tentativa nº 1
  queued), `rejected` (decisão de política ou validação), `expired` (TTL
  esgotado) e `deferred` (`release_at` definido, execução estacionada; o
  releaser chega em uma slice posterior). `variables_enc` é eliminado em
  `rejected` e `expired`; `deferred` o mantém selado porque o pipeline é retomado
  a partir desse ponto.
- Estágio Policy: implementações de `IPolicyRule<NotificationContext>` em
  `Features/Pipeline/Rules/`, na ordem fixa `ConsentGate`, `SuppressionGate`,
  `QuietHours`, `DedupeWindow` e `ChannelSelection`. Cada regra registra uma
  linha `policy_evaluation` com evidência JSON compacta; motivos canônicos de
  rejeição: `no-consent`, `channel-suppressed`, `duplicate-window` e
  `no-valid-contact`. Uma proteção rígida no código impede o adiamento de
  fluxos críticos e de autenticação.
- `SuppressionGate` retira os canais cujos endereços ativos estão todos
  suprimidos, lendo `RecipientSnapshot.Suppressions`, que o estágio Resolve já
  carregou. A posição é decisão de política: depois do consentimento, porque
  quem nunca autorizou o canal é recusado por um motivo mais forte, e antes da
  janela de silêncio, porque adiar por horas para recusar de manhã é trabalho
  que ninguém pediu. Um canal só cai quando todos os seus endereços ativos
  estão suprimidos; um destinatário com um segundo endereço vivo no mesmo canal
  continua alcançável. A ordem das regras vive em `CoreWorkerRole.RulesInOrder`
  e mudá-la é decisão de política, não refatoração. Toda chave de evidência
  nova precisa entrar na lista de divulgação de
  `Infrastructure/Auditing/PolicyEvidenceProjection.cs`, que um teste de
  completude cobra.
- Estágio Render: o conteúdo chega pronto do catálogo publicado, e este módulo
  não o reescreve. Duas consequências vêm de lá e aparecem aqui. A primeira é
  que o corpo de SMS chega normalizado (forma composta, sem caracteres de
  controle e sem quebras de linha) e os dois hashes auditados descrevem essa
  forma, que é exatamente a que o provider recebe. A segunda é que um render
  de SMS de finalidade de autenticação que produza um link é recusado pelo
  renderer, e o estágio o traduz para o motivo canônico
  `authentication-sms-link`, separado de `template-render-failed`: o template
  está íntegro e quem recusou foi uma regra de segurança. O alarme de segurança
  é registrado onde a deteção acontece, no módulo dono do render.
- A barreira de deduplicação é o `SET NX` do Redis com chave
  `(application, templateKey, recipientId)`, a janela da política como TTL e o
  id da notificação como valor, de modo que uma reentrega reconheça a própria
  marca. Uma falha do Redis opera em modo fail open e registra esse estado na
  evidência.
- A função de worker `core` consome concorrentemente as quatro queues `core-*`,
  com slots de processamento priorizados como `auth > critical > transactional >
  operational`; uma restrição opcional de faixa espelha o relay
  (`Modules:Notifications:CoreWorker:Bands`, vazio = todas).

## Slice de dispatch

- A função de worker `dispatcher` consome a combinação dos canais e faixas
  configurados nas queues `dispatch-{channel}-{band}`, com a mesma prioridade
  de slots da função core (`auth > critical > transactional >
  operational`). A
  composição hospeda e-mail (SendGrid), SMS (Twilio) e push (FCM); um canal
  configurado sem um adaptador hospedado recusa a inicialização.
- A posse de uma tentativa usa bloqueio otimista: `queued -> sending` por
  `UPDATE ... WHERE status = 'queued'`, com a gravação de `provider_key`. Cada
  transição posterior é protegida pelo estado armazenado esperado. A marca de
  deduplicação do consumer é confirmada com o resultado, nunca com o claim: um
  envio não é idempotente, portanto uma reentrega é resolvida com base no estado
  armazenado e nunca volta a alcançar o provider. Uma falha entre o claim e o
  resultado estaciona a tentativa em `sending` para a reconciliação em uma fase
  posterior.
- O fan-out de push se expande no momento do claim, dentro da transação do
  claim: a tentativa reservada recebe o token ativo mais recente, e um item
  irmão por token restante (cinco no total, no máximo, por `last_seen_at`) é
  inserido já queued, copiando o conteúdo selado, os hashes e o
  `fallback_deadline` absoluto da etapa; cada item é anunciado na mesma queue
  pela outbox. `sequence` é a ordem monotônica de criação por notificação. A
  ausência de tokens ativos no claim falha a tentativa com
  `no-active-device-token`.
- Resultados: `Accepted` leva a `sent` (em push **sem prazo de fallback**, ou
  seja na última etapa do plano, também leva a notificação a `delivered`);
  `Rejected` leva a `failed` e, quando a falha esgota a etapa (em push, nenhum
  item irmão teve sucesso e todos os outros já falharam ou sofreram bounce),
  avança o plano na mesma transação: uma etapa com prazo emite
  `FallbackRequested` junto com a trilha `fallback.triggered`, e a última etapa
  falha a notificação; `Throttled` e um circuito aberto revertem para `queued` e
  adiam a mensagem respeitando `RetryAfter`; qualquer outro erro transitório
  estaciona a tentativa em `unknown`, que não avança nesta fase.
- O destino de um `FallbackRequested` é `core-auth` quando `notification
  .auth_flow` é verdadeiro e `core-{class}` no restante, porque a banda de
  drenagem do relay é decidida pelo destino e a segunda metade de um código de
  autenticação precisa manter a banda que a primeira teve. O sinal é gravado na
  aceitação, onde o template publicado já está em mãos, para que nenhum produtor
  consulte o catálogo no caminho quente.
- Os desfechos terminais do plano vivem em `NotificationPlanOutcome`, e não em
  cada caminho que os alcança: se a etapa se esgotou, o pedido do próximo passo,
  o encerramento em `delivered` e o encerramento em `failed`. O veredito
  síncrono do dispatcher e o feedback assíncrono do provedor chamam esse mesmo
  código, para que a mesma conclusão nunca exista escrita duas vezes.
- O handler de fallback é executado dentro da função core: ele verifica o TTL
  (a expiração encerra a notificação em `expired`), encontra no plano publicado
  a etapa posterior ao canal que falhou, renderiza o próximo canal e coloca a
  próxima tentativa na queue com a invariante transacional do pipeline. Uma
  próxima etapa sem conteúdo, contato ou entrada no plano falha a notificação
  com um motivo estável. A recusa de segurança do SMS de autenticação preserva o
  motivo `authentication-sms-link`, a mesma distinção que o estágio de render
  faz na ingestão: dobrá-la em falha de template registraria como defeito de
  conteúdo exatamente o caso para o qual a regra existe.
- **O avanço do plano é reivindicado no banco, e não deduplicado por mensagem.**
  Existem vários produtores do mesmo gatilho (veredito definitivo do dispatcher,
  liberação de hold de kill switch vencido e, adiante, varredura de prazo) e
  cada um escreve uma linha de outbox com identidade de mensagem própria, então
  as marcas em `processed_messages` passam todas. Dentro da transação que
  enfileira a próxima tentativa, o handler carimba `plan_advanced_at` em toda
  tentativa da etapa com `UPDATE ... WHERE notification_id = ? AND channel = ?
  AND plan_advanced_at IS NULL`; zero linhas afetadas devolve `Duplicate` sem
  efeito. O claim é **por etapa e não por tentativa**, porque o fan-out de push
  cria irmãos que compartilham um único prazo absoluto. O predicado poda
  partição pela janela de `created_at` da notificação, senão a escrita varre
  todas as partições de `notification_attempt`. Produtor novo de gatilho não
  precisa de nada: o ponto de encontro é o handler.
- **A validade restante decide se o envio ainda vale.** Depois do claim e antes
  de revelar o destino, o dispatcher mede `expires_at - agora` e a entrega ao
  adaptador em `DispatchRequest.Validity`, para que o provider saiba por quanto
  tempo ainda faz sentido manter a mensagem na fila dele. Validade esgotada não
  chama o provider: a tentativa é encerrada como `failed` com o código
  `notification-expired`, sem consumir mensagem, e o plano avança pelo mesmo
  caminho de qualquer falha definitiva. A medição vem depois do claim porque a
  resposta precisa descrever o instante da chamada, e antes da revelação porque
  uma mensagem que ninguém vai mais ler não justifica um contato em claro na
  memória. Na última etapa do plano esse encerramento chega como `failed` com
  esse motivo, e não como `expired`: quem escreve `expired` depois do dispatch é
  o handler de fallback, que é o único a ver o plano inteiro.
- **O limite de taxa por provedor vem depois da guarda de validade.** A
  superfície de providers gasta o orçamento contratado do provedor dentro da
  chamada de envio, ou seja, depois do claim, depois da guarda de validade e
  depois da segunda avaliação do kill switch. A ordem é a economia do
  orçamento: orçamento gasto com uma mensagem que a validade já descartou é
  orçamento que faltou para a próxima que ainda valia. Uma mensagem barrada
  volta a `queued` e é adiada pelo mesmo caminho do `Throttled` do provedor,
  com o motivo `rate-limited` em vez de `provider-throttled`, porque o
  congestionamento é nosso e não do provedor. Barrada nunca vira falha
  definitiva: o plano avançaria por fila nossa, e não por recusa de quem
  entrega.
- **A aplicação viaja no envio.** `DispatchRequest.Application` carrega a
  aplicação da notificação para providers cuja identidade de envio é alocada por
  aplicação, como um pool de remetentes por marca. É repasse puro: não entra no
  conteúdo renderizado nem nos hashes auditados.
- **O circuito aberto é observado depois da liquidação do veredito.** Cada
  veredito alimenta a observação do circuito daquele canal, e apenas o circuito
  aberto conta: qualquer outro desfecho prova que o breaker deixou a chamada
  passar. Uma tentativa encerrada por validade vencida não chega a essa
  observação, porque ela termina antes do envio; contá-la pararia o canal pelo
  prazo do cliente e não pela degradação do provedor. A observação roda depois
  da liquidação, e nunca dentro dela, para que uma decisão de canal não desfaça
  a transição da tentativa.
- PII somente no momento do envio: a renderização selada é aberta em memória, o
  endereço de e-mail vem de `RevealContactValueAsync`, e o token de push vem de
  `RevealDeviceTokenAsync`, ambos transitórios. Os resultados FCM `UNREGISTERED`
  e `INVALID_ARGUMENT` relatam o token inativo pelo contrato de ciclo de vida do
  ContactConsent após a confirmação do resultado; o relato é best effort e
  idempotente no lado responsável.

## Rastreamento de entrega

- O webhook tem duas metades e elas vivem em módulos diferentes. O
  conhecimento de provedor (assinatura, janela de timestamp, allowlist de
  origem, extração do identificador de evento, tradução do vocabulário e sinal
  de supressão) pertence ao Dispatch e chega por
  `Modules.Dispatch.Integration.V1`. O conhecimento de notificação (rota,
  autenticação, deduplicação, evidência, correlação e máquina de estados) vive
  em `Features/DeliveryTracking/`.
- A rota `POST /webhooks/{provider}` é autenticada, nunca anônima. A assinatura
  do provedor entra como esquema de autenticação (`ProviderSignature`): o
  handler do esquema lê o corpo cru com buffer e rebobina, chama `Verify` pelo
  resolver, publica um principal com a claim `provider_key` e deixa o callback
  provado em `HttpContext.Items`. O corpo é verificado uma única vez, e o
  endpoint declara `RequireAuthorization` e `RequireRateLimiting` como qualquer
  outra rota que muda estado. Uma recusa de origem
  (`origin-not-allowed`) gera evento de log de segurança próprio, distinto do
  de assinatura inválida: o primeiro é sinal de forjação, o segundo é também o
  sintoma corriqueiro de um segredo rotacionado.
- **A URL assinada vem de uma base pública configurável**
  (`Modules:Notifications:ProviderWebhooks:PublicBaseUrl`), caindo para a URL
  da requisição quando a base não está configurada. A assinatura da Twilio
  cobre a URL completa com query string, e atrás de balanceador o endereço que
  este processo observa é o interno enquanto o provedor assinou o público. Sem
  a base configurável, toda assinatura válida seria recusada em produção e
  ainda assim passaria em teste.
- **Invariante transacional da recepção**: por evento, uma única transação ou
  nenhuma: a marca em `provider_event_dedupe`, a linha `delivery_payload` com o
  corpo selado quando este é o primeiro evento a reivindicar o lote, a linha
  `delivery_event` que a referencia por `payload_id`, e a mensagem
  `delivery.event_received` na outbox para `delivery-events`. Uma chave já
  existente é ignorada sem erro.
- **O corpo é selado uma vez e gravado uma vez por callback.** Replicá-lo em
  cada evento do lote tornava a escrita quadrática no tamanho do lote, que é
  variável escolhida pelo provedor. O que preserva a propriedade de que a
  evidência de um evento é o lote inteiro é a ordem, e não uma transação
  compartilhada: a linha de payload confirma no máximo junto do primeiro evento
  que a referencia, o que mantém uma transação por evento. Lote inteiramente
  reentregue não reivindica nada e não grava nada.
- **O instante da recepção é do callback, não do evento**, e é isso que mantém o
  lote inteiro dentro da mesma partição mensal nas duas tabelas.
- **`delivery_payload.source` diz o que são os bytes**: `webhook` para o corpo
  que o provedor assinou, `reconciliation` para o evento canônico que este hub
  serializou depois de perguntar. **Nenhum `IAuditTrail.AppendAsync`
  entra nessa transação**: o acréscimo segura o bloqueio da cadeia até o fim da
  transação e serializaria o callback contra a ingestão, e quem decide o ritmo
  do callback é o provedor. A trilha é escrita pelo consumidor assíncrono.
- Lista vazia de eventos é sucesso sem escrita. Um lote de eventos de
  engajamento é tráfego comum, e responder erro compraria reentrega infinita de
  um callback que nunca teve nada para este hub.
- O payload do provedor carrega contato em claro, então é selado com a cifra de
  envelope em escopo próprio do tracker
  (`notifications-delivery-evidence`), e não no escopo da aplicação: a
  aplicação dona da notificação pode ainda não ser conhecida no instante do
  insert, e a consulta que a resolveria estouraria o orçamento de latência do
  callback. Um lote sela uma vez e compartilha o texto cifrado entre suas
  linhas.
- **Correlação**: o evento canônico traz a correlação quando o provedor a
  ecoa. Quando não traz, a rota aceita `notificationId` e `attemptId` como
  parâmetros de query e preenche a correlação com eles. Correlação é
  conhecimento deste módulo, e o endereço de callback é deste módulo; o
  contrato do Dispatch permanece sem a URL.
- A aplicação roda no papel `delivery-tracker`, que consome `delivery-events`,
  carrega a evidência, resolve o attempt (pela correlação e, na falta dela,
  pelo `provider_message_id` com o índice parcial) e aplica a transição
  carimbando `applied_at`, a correlação resolvida e a trilha na mesma
  transação. `DeliveryStateApplier.ApplyAsync` é o único escritor dessa metade
  da máquina de estados; a reconciliação de fase posterior chama o mesmo
  aplicador com o mesmo `ProviderDeliveryEvent`, para que as duas fontes não
  virem duas máquinas.
- Transições alimentadas por feedback, em `DeliveryStateMachine`: `sent ->
  delivered` (carimba `delivered_at` com o instante do provedor), `delivered ->
  read`, `sending -> failed`, `sending -> bounced`, `sent -> bounced`, `sent ->
  failed` e, a partir do estado estacionado, `unknown -> sent`, `unknown ->
  delivered`, `unknown -> read`, `unknown -> failed` e `unknown -> bounced`.
  Qualquer outro par é registrado e ignorado, sem erro: o feedback nunca anda
  para trás. O estado estacionado é a exceção que confirma a regra, e é o que
  torna a reconciliação capaz de corrigir alguma coisa: um veredito
  inconclusivo é este hub admitindo que não sabe o que o provedor fez, não um
  fato sobre a mensagem, então nada anda para trás a partir dele. O carimbo de
  entrega é escrito onde ainda estiver vazio, inclusive na leitura, porque uma
  abertura pode ser a única prova de entrega que uma tentativa estacionada
  jamais recebe.
- O que a transição significa para a notificação **não** é decidido no
  aplicador. Ele chama `NotificationPlanOutcome` dentro da própria transação:
  `delivered` encerra a notificação em `delivered` e publica o evento de
  entrega; `failed` ou `bounced` que esgota a etapa avança o plano exatamente
  como o veredito síncrono avançaria. Reescrever a regra ali criaria duas
  máquinas para a mesma conclusão, que é o defeito que a máquina de estados
  única existe para evitar.
- Um evento cujo attempt ainda não existe fica armazenado e não aplicado, sem
  erro. O provedor pode entregar o callback antes de a transação do envio
  confirmar, então a mensagem volta por uma janela limitada
  (`Modules:Notifications:DeliveryTracking`) e depois é descartada com registro,
  em vez de circular pela retenção inteira da fila.
- `provider_event_dedupe` é purgada por idade
  (`Modules:Notifications:ProviderEventDedupePurge`, trinta dias por padrão) no
  próprio papel; a evidência não é tocada pela purga.
- **O sinal de supressão é gravado, nunca reclassificado aqui.**
  `delivery_event.suppression_signal` guarda a classificação que os adaptadores
  de provedor fizeram na ingestão, na grafia durável (`none`, `hard-bounce`,
  `invalid-destination`). Quais códigos são definitivos é vocabulário
  configurável do provedor, e uma segunda leitura desse vocabulário deste lado
  seria um segundo classificador livre para divergir. Linha anterior à coluna
  lê `none`, que é o que a ausência já significava.
- **Quem relata o destino recusado é o aplicador**, depois de confirmar a
  transição e nunca antes, por
  `ContactConsent.Integration.V1.ISuppressionLedger`. O relato vive junto do
  escritor único da máquina de estados porque a regra é exatamente essa
  ordem, e uma regra que cada chamador do aplicador precisa lembrar é uma
  regra que um dia viaja sem o chamador: foi o que aconteceu quando a
  reconciliação passou a chamar o aplicador direto. O aplicador também é o
  único lugar que já tem as duas metades do alvo em mãos, o ponto de contato
  da tentativa e o destinatário da notificação, então relatar dali não custa
  leitura alguma. Regime best effort e idempotente, o mesmo do token de
  dispositivo: a transição já foi commitada e uma reentrega resolve como
  duplicata, então uma falha do relato fica registrada. O `sourceEventId` é o
  id da linha de evidência, e feedback sem evidência não relata nada: o ledger
  identifica a recusa por essa linha, e um identificador cunhado na hora
  contaria a mesma recusa duas vezes, o que num canal que suprime na segunda
  tira um destino alcançável de quem foi recusado uma vez. O instante relatado
  é o da aplicação, sempre deste hub e nunca o que o provedor declara: a janela
  de acúmulo do ledger não pode ser aberta de fora. Um attempt sem ponto de
  contato (push) não relata nada, porque token morto viaja pelo contrato de
  ciclo de vida do dispositivo.

## Scheduler do papel `delivery-tracker`

- `Features/DeliveryTracking/Scheduling/` varre o banco a cada
  `Modules:Notifications:SchedulerScan:Interval` (dois segundos por padrão) e
  grava a próxima ação na outbox. A rodada faz cinco perguntas: prazo de
  fallback vencido sobre tentativa em `queued` ou `sent`, prazo vencido sobre
  tentativa em `unknown`, veredito inconclusivo prolongado, `release_at`
  vencido e sinal de supressão que o aplicador não conseguiu entregar ao ledger
  de contatos. O tamanho do lote, a tolerância do veredito inconclusivo e a
  janela de reemissão são configuração; nenhum deles é contrato.
- **O intervalo não é livre.** Ele soma o próprio tamanho ao tempo até o SMS de
  fallback, junto do prazo do passo, dos dois saltos de fila, do estágio Core e
  do timeout do provedor. O padrão é derivado do aceite dessa soma, e uma
  asserção de orçamento em teste unitário reprova quando o intervalo ou o
  timeout do provedor de SMS sobe sozinho.
- **`queued` entra na varredura de prazo.** Circuito aberto, throttle e canal
  pausado devolvem a tentativa à fila com o prazo intacto, e ler apenas `sent`
  deixava essa tentativa invisível para toda varredura: uma indisponibilidade
  mais longa que a validade encerrava a notificação sem tentar o segundo canal.
  Nem `queued` nem `sent` carregam a dúvida que mantém `unknown` num lote
  separado, porque uma nunca chegou a provedor algum e a outra foi aceita.
  Reivindicar `queued` é o que torna condicional o claim do despacho:
  `AttemptDispatchWriter` passou a exigir `plan_advanced_at` e
  `fallback_requested_at` nulos, senão a tentativa devolvida e o próximo passo
  sairiam os dois.
- **O `unknown` tem dois instantes e dois statements.** O prazo do passo e a
  idade governam as mesmas linhas, e não cabem no mesmo comando: um `OR` entre
  os dois não é buscável em coluna alguma e degrada a rodada em leitura de todas
  as partições, o que as asserções de plano medem. O statement de prazo roda
  antes do de idade, para que uma tentativa vencida nos dois seja pedida uma vez
  só.
- **O scheduler não reivindica o avanço do plano.** Ele só pede. O ponto de
  encontro de todos os gatilhos de um passo continua sendo
  `FallbackRequestHandler`, e uma varredura que carimbasse `plan_advanced_at`
  faria o handler ler o passo como já avançado e descartar exatamente o
  gatilho que ela acabou de escrever.
- **Nada de estado fora do banco.** Cada rodada lê o que precisa saber no
  momento em que roda, então o papel roda com mais de uma réplica e uma
  réplica pode morrer no meio de uma rodada sem levar consigo trabalho que só
  ela conhecia.
- Os três statements de fallback reivindicam por `FOR UPDATE SKIP LOCKED`:
  elas precisam ler os candidatos e juntar as notificações antes de escrever
  qualquer coisa, e um lote de pedidos não acrescenta trilha, então segurar o
  lote inteiro em uma transação não faz ninguém esperar. A varredura de
  liberação faz o oposto e por isso mesmo: ela termina em acréscimo de
  auditoria, que segura o bloqueio da cadeia até o fim da transação, então
  reivindica uma notificação por transação.
- **Pedir não deixa trilha.** O pedido é uma linha de fila, a decisão é do
  handler, e o handler já registra tanto o gatilho quanto a tentativa
  enfileirada. Uma entrada de trilha por pedido tomaria o bloqueio da cadeia
  uma vez por rodada, pela mesma razão que mantém o acréscimo fora da
  transação do callback. A liberação é diferente em natureza: ela muda o
  estado de uma notificação, e mudança de estado sem trilha é mudança que
  ninguém reconstrói.
- **A liberação transita `deferred` para `accepted` dentro da transação do
  claim.** `CoreMessageProcessor` lê qualquer estado diferente de `accepted`
  como reentrega e responde com trilha de duplicata e nenhum efeito, então uma
  liberação que apenas enfileirasse deixaria a notificação parada para sempre
  parecendo, por toda métrica de fila, um scheduler funcionando. A expiração
  não é redecidida ali: quem resolve TTL vencido é o estágio do pipeline para
  onde a notificação está voltando.
- `notification_attempt.status_changed_at` é carimbada por **todo** escritor de
  transição (`AttemptDispatchWriter` e `DeliveryStateApplier`) e responde há
  quanto tempo a tentativa está onde está. Linha anterior à coluna fica nula e
  nunca casa com predicado de idade, o que é leitura desejada: varredura não
  age sobre idade que ninguém consegue calcular. Esse passivo pertence à
  reconciliação.
- `notification_attempt.fallback_requested_at` é o que impede a varredura de
  prazo de escrever um gatilho por rodada para a mesma tentativa. Ela entra no
  predicado do índice parcial, então a tentativa com pedido em voo sai do
  índice; e ela é janela, não bandeira permanente, para que um gatilho que
  nunca chegue ao handler não estacione o passo para sempre. Quem garante
  unicidade continua sendo o claim do handler, nunca a contagem de pedidos.
- O `unknown` prolongado só gera fallback em `critical` e em fluxo de
  autenticação, e a elegibilidade é predicado da consulta, não filtro em
  código, para que a tentativa inelegível nunca ocupe vaga do lote. Ele exige
  prazo gravado, que é a prova de que existe passo posterior: um último passo
  sem resposta fica para a reconciliação em vez de virar notificação falha.
- **Predicado literal.** Toda varredura escreve o predicado do índice
  parcial que a atende palavra por palavra na própria consulta. Índice
  parcial só atende consulta cujas cláusulas o planejador consegue provar que
  implicam o predicado dele, e predicado escrito como parâmetro não se prova.
  Trocar qualquer um deles por bind transforma a rodada em varredura
  sequencial de todas as partições, em silêncio.
- Integridade própria: `notifications-scheduler-scan` responde se as rodadas
  continuam acontecendo. É a única pergunta que um scheduler parado responde
  errado sozinho, porque o processo continua de pé, consumindo a fila e
  relatando sucesso em todas as outras probes. A idade da linha vencida mais
  antiga sai em log estruturado a cada rodada que encontra trabalho; o alarme
  sobre ela pertence à Infrastructure.


## Reconciliação do papel `notifications-maintenance`

- `Features/DeliveryTracking/Reconciliation/` roda uma vez por
  `Modules:Notifications:DeliveryReconciliation:Interval` (um dia por padrão) e
  pergunta ao provedor o que aconteceu com as tentativas que ele aceitou, ou
  deixou sem veredito, e nunca mais reportou. É correção de retaguarda: o
  fallback já rodou, o prazo já venceu, e o que sobrou é uma tentativa cujo
  registro está errado, não uma mensagem que alguém espera.
- **Elegibilidade**: `sent` ou `unknown`, com `provider_key` carimbado, paradas
  há mais de `StaleAfter` (seis horas por padrão). A idade cai para
  `created_at` quando `status_changed_at` é nula, que é o que toda linha
  anterior à coluna carrega. O scheduler não pode fazer essa substituição,
  porque agir cedo lá custa uma segunda mensagem a uma pessoa; aqui o mesmo
  erro custa uma leitura no provedor, e recusar-se a fazê-la deixaria
  justamente essas linhas inalcançáveis para sempre. Uma tentativa encerrada
  pela própria validade não entra: ela nunca chegou ao provedor e não carrega
  identidade de mensagem, e é o par de status que a mantém de fora, não uma
  exceção escrita à parte.
- **A resposta entra pela mesma porta que o callback**: uma linha
  `delivery_event` sob a mesma identidade de evento do provedor, com o evento
  canônico selado como payload, e uma aplicação pelo mesmo
  `DeliveryStateApplier`. A deduplicação é compartilhada de propósito: um
  evento que o callback já gravou é recusado aqui, e um evento gravado aqui é
  recusado ao callback que chegar depois. Sem isso, uma recusa vista pelas duas
  metades contaria duas vezes no ledger de contatos. A mensagem de fila também
  é escrita, e é respondida como duplicata no caminho feliz: ela é a única cura
  para uma rodada que morra entre o commit da evidência e a própria aplicação.
- **O destino é transitório e só existe quando é a única rota.** Uma mensagem
  com identidade no provedor é consultada por ela, e nada é revelado; sem
  identidade, o valor sai de `RevealContactValueAsync` no instante da consulta,
  vai para o adaptador e morre com a consulta. A regra é escrita sobre a
  tentativa e não sobre o provedor, porque quais chaves de busca uma plataforma
  oferece é conhecimento de provedor e vive do outro lado do contrato; o preço
  é uma revelação supérflua para o provedor que busca por metadado e cujo envio
  não deixou identidade, e o valor dessa revelação é descartado sem uso.
- **Provedor sem consulta posterior não é chamado.** O resolver do Dispatch
  recusa, a tentativa permanece onde está e o registro é de log, não de trilha:
  as mesmas linhas voltam a cada rodada pela vida da partição, e um acréscimo
  de trilha segura o bloqueio da cadeia da partição mensal, então um acréscimo
  por linha inconsultável por dia taxaria a ingestão do hub inteiro para
  repetir um fato que não muda.
- **A consulta não alimenta o observador de circuito do canal.** Aquela janela
  mede há quanto tempo os envios estão falhando e sua consequência é parar o
  canal para todo mundo. Uma consulta é leitura, feita por um job de lote,
  sobre uma mensagem enviada horas antes: contar o timeout dela como veredito
  de envio faria um minuto ruim de uma API de relatório parar um canal que
  entrega perfeitamente. Também não há lacuna: uma tentativa barrada pelo
  limitador de taxa nunca chegou ao provedor e continua `queued`, fora dos dois
  status que esta varredura lê.
- **A retirada do passivo de índice roda na mesma rodada.**
  `SettleTerminalAsync` encerra a notificação sem reivindicar avanço de plano,
  porque não há avanço a reivindicar, e a tentativa fica com prazo carimbado e
  claim vazio: exatamente o predicado dos três índices parciais do scheduler. A
  varredura de prazo já mantém notificação encerrada fora do lote pelo join,
  então nada é pedido duas vezes; o que cresce é o trabalho embaixo, uma
  entrada lida e descartada por rodada por linha, para sempre. Carimbar
  `plan_advanced_at` nessas linhas não muda comportamento algum (o handler
  responde duplicata antes de chegar ao claim, e um plano encerrado não avança)
  e muda só quais linhas os índices guardam. Medição sobre 40 mil tentativas:
  a varredura de prazo lia 897 entradas por rodada, 799 delas de notificações
  encerradas, e passou a ler 98, que é o trabalho real.
- O alcance histórico da consulta de e-mail é decisão comercial e vive em
  configuração do Dispatch, não aqui. Enquanto o add-on de atividade não for
  contratado, uma tentativa de e-mail mais velha que o alcance é recusada com
  `history-exhausted` e permanece sem desfecho, com registro.

## Superfície de consulta

- Três rotas de leitura sob `Notifications.Read`: `GET /v1/notifications/{id}`,
  `GET /v1/recipients/{recipientId}/notifications` e
  `GET /v1/notifications?correlationId=`. A função protege a rota; não há escopo
  por aplicação nesta fase porque nada vincula um principal de leitura a uma
  aplicação. A contenção está em outro ponto e faz parte do contrato: somente
  identidade exata (sem prefixo, curinga, listagem sem sujeito ou rota que
  liste apenas por `application`), um id malformado respondendo 400 e um id
  desconhecido bem-formado respondendo 404 com um corpo que nunca repete o
  valor, uma política própria de rate limit (`notifications-query`, separada da
  política de ingestão dimensionada por producer) e um log de acesso estruturado
  que carrega principal, rota e sujeito.
- **Nenhum `audit_event` por leitura.** Acrescentar uma linha de trilha por
  consulta serializaria cada leitura com a ingestão no advisory lock da cadeia,
  e `audit.read` pertence a `/v1/audit/*`, que é por onde o conteúdo e o contato
  completo realmente saem do hub.
- As leituras são executadas em `NotificationsReadDbContext`: o mesmo modelo
  sobre `Modules:Notifications:Persistence:Ef:ReadConnectionString`, com
  fallback para a conexão de escrita quando ausente, sem rastreamento e com todos os
  pontos de entrada de `SaveChanges` lançando exceção. As migrações e a fábrica
  usada em tempo de design nunca o acessam.
- A paginação usa keyset decrescente sobre `(created_at, id)` por meio de uma
  comparação row-value do PostgreSQL, com um cursor opaco (base64url do instante
  em ISO 8601 UTC até o microssegundo, junto com o id público `ntf_`). Quatro
  números fazem parte do contrato, não da configuração: tamanho de página 50
  por padrão, no máximo 200, janela padrão de 90 dias e limite de 180 dias. A
  janela efetiva é repetida na resposta; um cursor cuja posição esteja fora da
  janela solicitada é recusado como `invalid-cursor`.
- A forma da resposta segue três regras. Os membros que sempre existem estão
  sempre presentes, inclusive arrays vazios. Os membros cujo valor está ausente
  são omitidos. Os membros cuja fonte não existe nesta fase não são declarados,
  portanto o comprovante de leitura fica ausente, não vazio. `deliveryEvents`
  deixou de cair nessa regra quando a tabela de eventos de entrega passou a
  existir: ele é sempre declarado, e lista vazia afirma que o provedor não
  relatou nada para aquela tentativa. `attempts[].deliveredAt` é gravado por
  confirmação do provedor e omitido enquanto nenhuma tiver sido aplicada.
- **Nunca saem por aqui**: conteúdo renderizado em qualquer forma (somente
  `content_hash_full` e `content_hash_masked` trafegam) e `variables_masked`, que
  ainda é dado de negócio e pertence à superfície de auditoria. As projeções de
  consulta selecionam coluna por coluna para que nenhuma refatoração posterior
  alcance qualquer um deles.
- O destino da tentativa é o ponto de contato mascarado pelo ContactConsent por
  meio de `IRecipientDirectory.MaskContactPointsAsync`, acompanhado da indicação
  de que o ponto ainda está ativo. Uma tentativa de push não tem ponto de
  contato: ela expõe a plataforma e o id de registro do dispositivo, nunca o
  token.

## Variáveis e PII

- `variables_masked` (jsonb, obrigatório) armazena o objeto canônico de
  variáveis com cada variável listada em `SensitiveVariables` do template
  mascarada como `***`; essa é a única projeção em texto simples armazenada.
- `variables_enc` (bytea, anulável) armazena a forma canônica criptografada em
  envelope do objeto **completo** de variáveis, selada pela cifra de envelope da
  plataforma com a chave de dados da aplicação.
- **Responsabilidade pelo purge**: o commit do pipeline Core elimina
  `variables_enc` nos estados terminais alcançáveis `rejected` e `expired`, na
  mesma transação da transição. Os estados terminais do lado do dispatch
  (`delivered`, `failed`) fazem o purge em uma fase posterior; `deferred` mantém
  o texto cifrado porque o pipeline é retomado desse ponto.
- Nenhuma verificação da existência do destinatário ocorre na ingestão
  (antienumeração): a API responde 202 independentemente da existência do
  destinatário.

## O conteúdo renderizado existe em duas fases

- `rendered_content_enc` (bytea) é selado por
  `Infrastructure/Privacy/RenderedContentEnvelope.cs`, o único responsável por
  essa forma. O conteúdo a enviar ocupa o nível superior do envelope; a forma
  mascarada o acompanha em um membro `masked`, somente quando as duas formas são
  diferentes. Nenhum outro componente interpreta ou escreve esses bytes.
- **O resultado terminal de um envio é a transição.** Na mesma instrução que
  escreve `sent`, `failed` ou `unknown`, o envelope é reescrito somente com a
  forma mascarada: o conteúdo completo perde sua finalidade assim que o
  provider aceita ou recusa a mensagem; uma etapa de fallback renderiza e sela
  seu próprio conteúdo em vez de reutilizar aquele que falhou, e a reconciliação
  consulta o provider pelo id da mensagem sem nunca reenviar o conteúdo.
  Throttling e circuito aberto não são resultados e nunca causam transição.
- **A função `notifications-maintenance` é a salvaguarda final.** Uma tentativa
  que nunca alcança um resultado (queued ou sending, com a notificação
  expirada além da tolerância configurada) é resolvida por
  `RenderedContentSweep`; o conteúdo selado antes da existência do envelope com
  duas formas é resolvido por `RenderedContentBackfill`, que renderiza novamente
  o template publicado com `variables_masked` e substitui o conteúdo somente
  quando o hash recalculado corresponde ao `content_hash_masked` armazenado. Uma
  linha sem correspondência permanece intocada e aparece em um log estruturado
  de revisão.
- **SMS gravado antes da normalização não casa mais, e isso é o backfill
  funcionando.** A normalização de encoding do SMS mudou os bytes que o render
  produz, então uma tentativa de SMS selada antes dela tem
  `content_hash_masked` calculado sobre a forma antiga. O `RenderedContentBackfill`
  renderiza de novo, obtém a forma normalizada, o hash não corresponde, e a
  linha permanece **intocada**, com `hash-mismatch` no log estruturado de
  revisão. Esse é exatamente o comportamento que o duplo hash existe para
  garantir: o backfill só substitui conteúdo quando prova que está olhando para
  a mesma mensagem, e uma divergência é motivo para não tocar, nunca para
  reescrever. Não é defeito, não deve ser "consertado" afrouxando a comparação,
  e o volume dessas linhas é finito: são as tentativas de SMS anteriores à
  mudança. Quem for reconciliá-las precisa saber que a diferença é de
  normalização, e não de conteúdo.
- Os dois hashes nunca mudam: `content_hash_full` permanece como âncora para
  confrontar evidências externas, e `content_hash_masked` é o valor que a
  superfície de auditoria verifica em relação à forma durável.

## Rate limit

- Duas dimensões baseadas em Redis, ambas com chave por classe canônica: por
  principal do producer e por destinatário
  (`Modules:Notifications:RateLimits`); exceder um limite responde 429 com
  `Retry-After` e o `type` do problema correspondente à dimensão que recusou, e
  a dimensão do destinatário também registra
  `notification.rejected_at_ingress` com o motivo `recipient-rate-limited`.
- Toda falha do Redis opera em modo fail open com um log de alarme: a
  disponibilidade prevalece, e o kill switch de producer implementado é a
  compensação. No Kafka, a revogação da ACL do broker permanece como parada
  rígida independente. A política nomeada do ASP.NET no endpoint é apenas uma
  proteção rudimentar dentro do processo.
- A rota de webhook tem política própria (`notifications-provider-webhook`),
  particionada pelo provedor provado pela assinatura e, quando ele não existe,
  pelo provedor endereçado na rota. O teto é generoso de propósito: quem decide
  o ritmo do callback é o provedor, e um callback recusado volta em retentativa.
  A partição por provedor impede que a tempestade de eventos de um provedor
  afame o feedback de outro.

## Particionamento

- `notification` (por `created_at`), `notification_attempt` (por `created_at`),
  `policy_evaluation` (por `evaluated_at`), `delivery_event` e
  `delivery_payload` (as duas por `received_at`) são particionadas por mês; cada migração de criação provisiona
  as partições iniciais, e o agendador do módulo
  (`Modules:Notifications:PartitionManager`) mantém meses futuros provisionados
  para as quatro tabelas por meio do provisionador da plataforma.
  Verificações de integridade: `notifications-partitions`,
  `notifications-attempt-partitions`,
  `notifications-policy-evaluation-partitions` e
  `notifications-delivery-event-partitions`.
- `delivery_event` particiona por `received_at`, o instante em que este hub
  recebeu o callback, e nunca por `occurred_at`: o provedor data o próprio
  evento e pode datá-lo para trás, o que colocaria a linha fora de toda
  partição provisionada e faria falhar o insert de um callback que este hub não
  tem o direito de recusar.
- Nunca revogue escritas nas partições deste módulo: a revogação de escrita é
  uma semântica de fechamento exclusiva da trilha de Audit.
- `idempotency_key` e `provider_event_dedupe` permanecem fora do
  particionamento para que suas chaves únicas possam existir. No caso do livro
  de deduplicação isso é o ponto: um callback reentregue dias depois precisa
  colidir com a primeira entrega, e uma chave única sobre tabela particionada
  teria de carregar a coluna de partição.

## Vocabulário de auditoria

As ações de ingestão seguem o vocabulário com pontos da plataforma:
`notification.accepted` (os detalhes carregam `source = rest`),
`notification.duplicate` e `notification.rejected_at_ingress` (os detalhes
carregam o motivo estável). O tipo do ator é `producer`, com a identidade do
token (`appid`/`oid`) como id do ator. O pipeline acrescenta
`notification.dispatched`, `notification.rejected`, `notification.deferred`,
`notification.expired`, `notification.duplicate` e `message.discarded`, com o
tipo de ator `system` e o id de ator `core-worker`
(`Infrastructure/Auditing/PipelineAuditVocabulary.cs`). O lado do dispatch
acrescenta `fallback.triggered`, `notification.delivered`,
`notification.failed` e `fallback.attempt_queued`, com o id de ator `dispatcher`
para as decisões do próprio dispatcher e `core-worker` para o handler de
fallback (`Infrastructure/Auditing/DispatchingAuditVocabulary.cs`). As
constantes permanecem locais ao módulo; promovê-las para o vocabulário
`Integration/V1` de Audit é uma decisão pendente entre módulos. O rastreamento
de entrega acrescenta `delivery.event_applied`, com tipo de ator `system` e id
de ator `delivery-tracker`: uma única ação com a transição nos detalhes, porque
quem lê uma trilha de evidência pergunta o que o provedor relatou e o que mudou,
e as duas respostas pertencem ao mesmo registro.

## Eixo de erros

- Os handlers retornam `Result<T>`; cada resultado da ingestão, inclusive as
  rejeições, é um dado dentro da união de respostas
  (`Features/Ingress/RequestNotification/RequestNotification.Response.cs`), e
  o endpoint mapeia cada caso para problemas RFC 9457
  (`Infrastructure/Http/IngestionProblems.cs`). Os valores de `type` dos
  problemas são códigos estáveis: `idempotency-key-conflict`,
  `class-not-allowed-for-principal`, `recipient-rate-limited`, `payload-invalid`,
  `template-not-found`, `template-class-mismatch`,
  `template-variables-invalid`, além dos motivos de catálogo
  `template-deprecated` e `template-disabled`. Exatamente três códigos são
  condições de protocolo da rota e permanecem fora do catálogo, pois nenhum
  deles é transportado como `reason` de um evento de rejeição:
  `idempotency-key-required`, `principal-rate-limited` e
  `kill-switch-unavailable`.
- **O 429 identifica a dimensão.** O orçamento do destinatário responde
  `recipient-rate-limited`, e o orçamento do principal responde
  `principal-rate-limited`, pois os dois pedem comportamentos opostos ao
  producer: um orçamento de destinatário esgotado significa que o cliente está
  protegido e que a requisição não deve ser repetida; um orçamento de principal
  esgotado significa reduzir o ritmo e tentar novamente. Somente a dimensão do
  destinatário registra uma trilha e publica um evento de rejeição.
- A superfície de consulta tem seus próprios três códigos estáveis
  (`Infrastructure/Http/QueryProblems.cs`): `invalid-request`, `invalid-cursor`
  e `notification-not-found`. O cursor recebe um código próprio porque um
  cliente que repete a tentativa sem critério precisa saber se deve descartar o
  parâmetro ou a posição.

## Segurança e testes

- A rota exige uma função de envio; a verificação no nível da classe é executada
  sobre o recurso no caso de uso porque a classe chega no corpo.
- A rota de webhook é autenticada pelo esquema `ProviderSignature` e por
  nenhuma outra identidade: o token de portador que autentica o resto do host
  nunca satisfaz o gate dela, e a assinatura do provedor nunca satisfaz o gate
  das demais.
- A administração do kill switch exige `Platform.Admin` e um `oid` ou `sub`
  estável; o acesso operacional a essa função passa pelo PIM. Nunca separe a
  escrita do estado de seu acréscimo de auditoria.
- Nunca faça binding de corpos HTTP com tipos de Domain; nunca registre em log
  variáveis, dados de contato do destinatário, tokens ou conteúdo renderizado.
  Registre somente identificadores.
- Comece com um teste de comportamento que falhe; mantenha a invariante
  transacional, o contrato de idempotência, o comportamento fail open do rate
  limit e o comportamento fail closed do kill switch cobertos por testes de
  integração. Testes de cache local não substituem o comprovante de rollout com
  várias instâncias para `t0 + 10 s`.

Atualize este arquivo na mesma alteração que modificar o limite do módulo, a
invariante transacional, o contrato de idempotência ou as regras de PII.
