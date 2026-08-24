# Módulo Notifications

## Limite

- Mantenha um único bounded context neste módulo: o ciclo de vida da
  notificação, da ingestão ao dispatch. Esta unidade entrega a ingestão REST, a
  entrada pelo barramento que consome os tópicos dedicados dos producers
  declarados em `Modules:Notifications:KafkaIngress:Bindings`, o pipeline Core
  que consome as queues `core-*`, a slice de dispatch (os consumers
  `dispatch-*`, a máquina de estados das tentativas a partir de `queued`, o
  fan-out de push e o handler de fallback), os eventos de resultado de saída em
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
  de contato e token, ciclo de vida do token do dispositivo),
  `Modules.Dispatch.Integration.V1` (providers de canais e sua resolução) e
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
| `src/Platform.Api/Modules/Notifications/Features/` | slices verticais deste contexto: o pipeline Core em `Features/Pipeline/`, o consumer de dispatch em `Features/Dispatching/`, o handler de fallback em `Features/Fallback/`, a administração e os gates do kill switch em `Features/KillSwitch/` e as slices de consulta em `Features/Queries/` |
| `src/Platform.Api/Modules/Notifications/Infrastructure/` | persistência (schema `notifications`, contextos de escrita e somente leitura), controles Redis, gate de template, privacidade, cache do kill switch e ciclo de vida dos holds, gerenciador de partições, job de purge, writer de commit do pipeline, writer de dispatch de tentativas, poison sinks, auxiliares de transporte para consultas e leitor de histórico |
| `src/Platform.Api/Modules/Notifications/NotificationsModule.cs` | registro de serviços e mapeamento de endpoints deste contexto |
| `src/Platform.Api/Modules/Notifications/Integration/V1/` | contrato publicado deste contexto: o catálogo canônico de motivos de rejeição |
| `src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs` | composição da função de worker `core`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs` | composição da função de worker `dispatcher`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/KafkaIngressWorkerRole.cs` | composição da função de worker `kafka-ingress`, descoberta pelo host de workers |
| `src/Platform.Api/Modules/Notifications/NotificationsMaintenanceWorkerRole.cs` | composição da função de worker `notifications-maintenance`, descoberta pelo host de workers |

Estado sob responsabilidade: `notification`, `notification_attempt` e
`policy_evaluation` (tabelas-pai particionadas mensalmente), `idempotency_key`,
`producer_registry`, `kill_switch` e `kill_switch_hold`. A `outbox` e
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

- `Features/Mutations/RequestNotification/` é neutro em relação ao transporte.
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
- O gate operacional de rollout permanece externo: em um ambiente
  representativo com várias instâncias, a ativação de cada escopo deve resultar
  em zero novos efeitos protegidos após `t0 + 10 s`. Uma ACL exclusiva de writer
  do Kafka por tópico de producer e o comprovante de ACL/drift do ambiente real
  permanecem controles independentes e bloqueantes da Infrastructure. A
  consulta estrita ao Microsoft Graph, inclusive a validação fechada de host,
  payload e paginação, pertence à ferramenta `tools/Platform.GoLiveChecks`; ela
  comprova a ausência de atribuições operacionais sem expor o token em log ou
  recibo.

## Eventos de resultado de saída

- `Infrastructure/Events/NotificationEvents.cs` constrói as linhas do
  CloudEvents em `notifications.events.v1`, lendo o nome do tópico e a URN de
  origem do hub na superfície de mensageria da plataforma em vez de declará-los
  aqui: o barramento de saída é um contrato de transporte, e ContactConsent
  publica `consent_changed` no mesmo tópico. O módulo é responsável pelos tipos
  de evento e pelas formas dos payloads: `rejected` na ingestão e no pipeline,
  `failed` quando o plano se esgota e na expiração e `delivered` na aceitação do
  push. A aceitação não anuncia nada (o producer já tem seu 202), assim como uma
  rejeição pelo orçamento do principal, pois um evento por requisição recusada
  é exatamente a tempestade que o controle existe para impedir.
- Cada evento é escrito pela outbox dentro da transação do efeito que relata e
  **antes** do acréscimo por `IAuditTrail`, pois esse acréscimo mantém o bloqueio
  da cadeia de partições até o término da transação, e qualquer item colocado
  na queue depois dele amplia a janela de espera da ingestão concorrente. Isso
  se aplica a `IngestionWriter`, `PipelineCommitWriter` e
  `AttemptDispatchWriter`.
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
  `Features/Mutations/RequestNotification/RequestNotification.PayloadHash.cs`.

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
  `Features/Pipeline/Rules/`, na ordem fixa da v1: `ConsentGate`, `QuietHours`,
  `DedupeWindow` e `ChannelSelection`. Cada regra registra uma linha
  `policy_evaluation` com evidência JSON compacta; motivos canônicos de rejeição:
  `no-consent`, `duplicate-window` e `no-valid-contact`. Uma proteção rígida no
  código impede o adiamento de fluxos críticos e de autenticação.
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
- Resultados: `Accepted` leva a `sent` (em push, o primeiro item irmão aceito
  também leva a notificação a `delivered`); `Rejected` leva a `failed` e, quando
  a falha esgota a etapa (em push, nenhum item irmão teve sucesso e todos os
  outros já falharam), avança o plano na mesma transação: uma etapa com prazo
  emite `FallbackRequested` para `core-{class}` junto com a trilha
  `fallback.triggered`, e a última etapa falha a notificação; `Throttled` e um
  circuito aberto revertem para `queued` e adiam a mensagem respeitando
  `RetryAfter`; qualquer outro erro transitório estaciona a tentativa em
  `unknown`, que não avança nesta fase.
- O handler de fallback é executado dentro da função core: ele verifica o TTL
  (a expiração encerra a notificação em `expired`), encontra no plano publicado
  a etapa posterior ao canal que falhou, renderiza o próximo canal e coloca a
  próxima tentativa na queue com a invariante transacional do pipeline. Uma
  próxima etapa sem conteúdo, contato ou entrada no plano falha a notificação
  com um motivo estável.
- PII somente no momento do envio: a renderização selada é aberta em memória, o
  endereço de e-mail vem de `RevealContactValueAsync`, e o token de push vem de
  `RevealDeviceTokenAsync`, ambos transitórios. Os resultados FCM `UNREGISTERED`
  e `INVALID_ARGUMENT` relatam o token inativo pelo contrato de ciclo de vida do
  ContactConsent após a confirmação do resultado; o relato é best effort e
  idempotente no lado responsável.

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
  portanto `deliveryEvents` e o comprovante de leitura ficam ausentes, não
  vazios. `attempts[].deliveredAt` nunca é gravado nesta fase, e a regra de
  omissão o mantém fora.
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

## Particionamento

- `notification` (por `created_at`), `notification_attempt` (por `created_at`) e
  `policy_evaluation` (por `evaluated_at`) são particionadas por mês; cada
  migração de criação provisiona as partições iniciais, e o agendador do módulo
  (`Modules:Notifications:PartitionManager`) mantém meses futuros provisionados
  para as três tabelas por meio do provisionador da plataforma.
  Verificações de integridade: `notifications-partitions`,
  `notifications-attempt-partitions` e
  `notifications-policy-evaluation-partitions`.
- Nunca revogue escritas nas partições deste módulo: a revogação de escrita é
  uma semântica de fechamento exclusiva da trilha de Audit.
- `idempotency_key` permanece fora do particionamento para que sua chave única
  possa existir.

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
`Integration/V1` de Audit é uma decisão pendente entre módulos.

## Eixo de erros

- Os handlers retornam `Result<T>`; cada resultado da ingestão, inclusive as
  rejeições, é um dado dentro da união de respostas
  (`Features/Mutations/RequestNotification/RequestNotification.Response.cs`), e
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
