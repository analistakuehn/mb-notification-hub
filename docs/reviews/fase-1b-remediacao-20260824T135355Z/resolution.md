# Resolução dos achados da fase 1B

## Revisão original

- `ENG-001`, `STK-001`, `ARC-002`, `ARC-003`, `ARC-004` e `ARC-005`: o
  [documento da fase](../../fases/fase-1b-fundacao.md) passou a refletir o
  estado implementado, o Stack Profile com SQS e Kafka, os estados das ADRs, os
  contratos reais de entrada e saída e as sete respostas de auditoria com a
  lacuna explícita de comprovação de entrega.
- `ENG-002`: a documentação de
  [`NotificationRejectionReasons`](../../../src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs)
  limita o conjunto fechado às rejeições. Testes de contrato preservam a
  cardinalidade aberta dos motivos de falha.
- `TST-001`: o projeto
  [`Platform.GoLiveChecks`](../../../tools/Platform.GoLiveChecks/) executa as
  verificações de PostgreSQL e Microsoft Graph, percorre páginas de resultados e produz
  recibo sem expor o token. Entradas ausentes, respostas malformadas e falhas de
  consulta bloqueiam a aprovação.
- `ARC-001`: a fatia I1 representa a entrega de Terraform, filas, tópicos,
  ACLs, identidades, KMS, WORM e observabilidade como dependência bloqueante da
  entrada em produção.
- `SEC-001`: o consumer deriva a identidade autorizável exclusivamente do
  tópico dedicado recebido, cuja relação com o producer lógico é bijetiva. O
  `source`, o payload e os cabeçalhos permanecem diagnósticos não confiáveis.
- `SEC-002`: o kill switch canônico cobre producer, aplicação e canal, com
  administração auditada, cache de cinco segundos em modo fechado, holds
  duráveis e retomada transacional.

## Primeira verificação após a remediação

- `PRF-001`: a consulta do releaser aplica elegibilidade antes do limite de
  cem itens e usa ordenação estável por expiração e identificador. Um teste com
  101 holds bloqueados comprova que um candidato elegível posterior não fica
  oculto.
- `ENG-003` e `TST-002`: um novo ciclo bloqueado reabre atomicamente o hold
  liberado; o replay do mesmo ciclo permanece idempotente. A expiração de Core
  e Fallback alcança o estado terminal antes do gate. Os testes cobrem
  reabertura, concorrência entre releasers, unicidade da retomada e bloqueio
  head-of-line.
- `ENG-004`: a documentação e os testes de `IngestionProblems` reconhecem os
  três tipos exclusivos do transporte HTTP.
- `STK-002`: a admissão da solicitação foi extraída para
  [`RequestNotification.Admission.cs`](../../../src/Platform.Api/Modules/Notifications/Features/Mutations/RequestNotification/RequestNotification.Admission.cs),
  mantendo o handler dentro do limite de sete dependências e preservando o
  replay idempotente antes do kill switch.
- `STK-003`: `KillSwitchCache` e `CachedProducerRegistry` calculam validade com
  timestamp monotônico. UTC permanece restrito a diagnóstico e persistência.
- `ARC-006`: o dispatch verifica aplicação e canal antes do claim e repete as
  duas verificações imediatamente antes da chamada ao provedor. Uma parada após
  o claim reverte a tentativa e abre o hold na mesma transação.
- `SEC-003`: o leitor do Microsoft Graph valida estritamente `appRoles`,
  `value`, `appRoleId` e paginação. JSON malformado em resposta 200 falha em
  modo fechado.
- `SEC-004`: o endpoint administrativo exige o campo `active`; `{}` responde
  400 sem mutação de estado nem acréscimo de auditoria.
- `SEC-005`: recusas anteriores ao estabelecimento de confiança do producer
  constroem a DLT com base em uma lista de campos permitidos e sinalizam a sanitização. O envelope original,
  segredos, PII e valores não confiáveis não são copiados para corpo nem
  cabeçalhos.

## Validação pós-correção

- `TST-003`: `RenderedContentRetentionTests` deixou de compartilhar a
  `CorePipelineFixture` com o teste de kill switch que persistia um envelope
  sintético de um byte. A classe usa uma fixture própria, preserva o
  paralelismo do restante da suíte e impede que o varredor global processe dados
  residuais de outro caso.

## Verificação completa

- `R3-PRF-001`, `R3-STK-001` e `R3-STK-002`: os dois caches medem desde o
  início da carga com relógio monotônico, recusam resultados que consumiram a
  janela absoluta e compartilham falha com backoff de um segundo. Trinta e dois
  chamadores concorrentes produzem uma tentativa por janela sem ampliar a
  autoridade.
- `R3-ENG-001` e `R3-ENG-002`: o binder Kafka aplica o limite canônico de 200
  caracteres à chave de idempotência e usa leitura em três estados para separar
  ausência, `null` permitido e formato inválido. Erros permanentes alcançam a
  DLT, confirmam processamento e não bloqueiam a mensagem seguinte.
- `R3-ENG-003`: quando o Redis não encontra o valor, a admissão consulta o
  registro idempotente do PostgreSQL antes do kill switch. O replay idêntico
  retorna 200; uma divergência retorna 409, e somente trabalho novo permanece
  bloqueado.
- `R3-ARC-001`: `ProducerDisabled` no REST registra exatamente uma trilha e um
  evento de rejeição, sem criar notificação nem registro idempotente.
- `R3-ARC-002`: holds inválidos e órfãos são marcados de forma persistente com
  um estado terminal, sem mensagem de retomada. O lote continua. Falhas
  transitórias permitem nova tentativa, e releasers concorrentes publicam uma
  única retomada válida.
- `R3-ARC-003`: `AGENTS.md` e o documento da fase enumeram os três tipos HTTP
  exclusivos: `idempotency-key-required`, `principal-rate-limited` e
  `kill-switch-unavailable`.
- `R3-SEC-001` e `R3-TST-001`: a DLT pré-confiança reconstrói key, corpo e
  headers com base em uma lista de campos permitidos. Os testes distribuem
  sentinelas em
  todas as superfícies e exigem ausência de dados controlados pelo producer.
- `R3-SEC-002`: o gate Graph compara `id`, `appId` e
  `appOwnerOrganizationId`, exige uma única role canônica e grava no recibo as
  identidades verificadas sem token.
- `R3-SEC-003`: o dispatch carrega a tentativa pelo par `attemptId` e
  `notificationId` antes de qualquer gate ou efeito. Envelope cruzado não chama
  provider nem altera estado, outbox, auditoria, deduplicação ou hold.
- `R3-STK-003`, `R3-STK-004`, `R3-TST-002` e `R3-TST-003`: as assinaturas
  excedentes foram reduzidas, nomes totalmente qualificados foram substituídos
  por imports e os dois testes passaram a declarar somente as propriedades que
  realmente comprovam.

## Invariantes documentadas

O arquivo
[`Modules/Notifications/AGENTS.md`](../../../src/Platform.Api/Modules/Notifications/AGENTS.md)
registra as decisões que precisam sobreviver às próximas alterações: relógio
monotônico, dupla verificação de aplicação e canal, ciclo de vida dos holds,
ordenação do releaser, transição para o estado terminal antes do gate, sanitização da DLT e
validação fechada do Microsoft Graph.

## Limites da resolução

A remediação entrega código, testes, contratos e ferramentas verificáveis no
repositório. Os recibos de ACL e drift, a consulta contra o ambiente real, a
propagação em múltiplas instâncias e os smoke tests dos provedores dependem de
infraestrutura e credenciais externas. Eles permanecem gates explícitos da
entrada em produção e não são substituídos por testes locais.
