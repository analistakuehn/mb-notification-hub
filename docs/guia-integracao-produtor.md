---
language: pt-BR
---

# Guia de integração do produtor

Este guia é para o time que vai **pedir notificações ao hub**. Ele descreve o
contrato observável: rotas, corpos, cabeçalhos, status, eventos de saída e o
que cada resposta afirma. Tudo aqui foi escrito a partir do comportamento
implementado, não do desenho pretendido, e o guia diz explicitamente o que a
versão atual **não** faz.

O contrato de máquina é o documento OpenAPI publicado pela própria API, em
`GET /openapi/v1.json`. Este guia explica o que o OpenAPI não consegue dizer:
ordem das checagens, semântica de cada desfecho e o que cada afirmação vale.

## 1. O que o hub faz, em uma frase

O produtor pede uma notificação por chave de template e identificador de
destinatário. O hub decide canal, versão de template e ordem de tentativa a
partir da política publicada, resolve o contato e o consentimento, renderiza e
entrega pelo provedor. O produtor **não** envia texto, não envia endereço de
e-mail, não envia telefone e não escolhe canal.

Classes entregues nesta versão: `critical` e `transactional`. O vocabulário
aceito pela ingestão também inclui `operational`, mas a versão atual não
entrega nenhum caminho pensado para essa classe (sem janela de silêncio
efetiva, sem liberação de adiamento). Trate `operational` como indisponível, e
saiba o motivo exato: nenhum componente desta versão lê o instante de
liberação, então uma notificação que a política adiar fica parada
indefinidamente, sem envio, sem falha e sem alarme. A contenção é
administrativa e dupla: o papel `Notifications.Send.Operational` não é
concedido e nenhum template dessa classe é publicado.

Canais entregues nesta versão: `email` e `push`. Uma política publicada que
liste `sms` ou `whatsapp` faz a notificação terminar em falha, porque não
existe adaptador hospedado para esses canais.

## 2. Como pedir uma notificação

### 2.1 REST: `POST /v1/notifications`

Autenticação por token Bearer. A rota exige pelo menos um dos papéis de envio,
e a classe pedida no corpo é conferida contra o papel correspondente:

| Classe no corpo | Papel exigido no token |
|---|---|
| `critical` | `Notifications.Send.Critical` |
| `transactional` | `Notifications.Send.Transactional` |
| `operational` | `Notifications.Send.Operational` |

Campos do corpo:

| Campo | Obrigatório | Observação |
|---|---|---|
| `application` | sim | Até 100 caracteres. Identifica a aplicação dona do template. |
| `recipientId` | sim | Até 100 caracteres. Identificador opaco do destinatário, nunca CPF, e-mail ou telefone. |
| `class` | sim | `critical`, `transactional` ou `operational`. |
| `templateKey` | sim | Até 200 caracteres. |
| `locale` | não | Até 20 caracteres. Aceito, **sem efeito** e fora do hash de idempotência: veja a seção 4. |
| `ttlSeconds` | sim | Inteiro maior que zero e no máximo 2.592.000 (30 dias). |
| `variables` | não | Objeto JSON. Ausente ou `null` significa nenhuma variável. |
| `channelsHint` | não | Lista de strings de até 20 caracteres cada. Aceita e ignorada: veja a seção 4. |
| `correlationId` | não | Até 200 caracteres. É por ele que a consulta agrupa uma transação de negócio. |
| `metadata` | não | Objeto JSON. Não é persistido, mas entra no hash de idempotência. |
| `scheduledAt` | não | ISO 8601. Aceito e **sem efeito** nesta versão: veja a seção 4. |

O cabeçalho `Idempotency-Key` é obrigatório, com no máximo 200 caracteres.

```http
POST /v1/notifications HTTP/1.1
Authorization: Bearer eyJhbGciOi...
Idempotency-Key: login-otp:cus_01J5X9ZK8QF3V7NB2E4D6TAHMC:2026-08-24T14:03:11Z
Content-Type: application/json

{
  "application": "araia-cambio",
  "recipientId": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
  "class": "critical",
  "templateKey": "auth.otp.login",
  "locale": "pt-BR",
  "variables": { "code": "482913", "expiresInMinutes": 5 },
  "ttlSeconds": 300,
  "correlationId": "trace-7c1e4b90"
}
```

```http
HTTP/1.1 202 Accepted
Location: /v1/notifications/ntf_01K3XQ8V2M4T6Y9BCDEFGHJKMN
Content-Type: application/json

{
  "notificationId": "ntf_01K3XQ8V2M4T6Y9BCDEFGHJKMN",
  "status": "accepted"
}
```

O `202` afirma exatamente uma coisa: a solicitação foi aceita, registrada e
enfileirada. Ele **não** afirma que existe destinatário, que existe contato,
que existe consentimento nem que a mensagem será entregue. Todas essas
perguntas são respondidas depois, pelos eventos de saída e pela consulta.

O identificador público tem a forma `ntf_` seguida de 26 caracteres do
alfabeto Crockford base32 em maiúsculas, sem as letras I, L, O e U.

**Ordem das checagens, do primeiro corte ao aceite.** A ordem é contrato: ela
decide qual resposta o produtor recebe quando mais de uma condição falha ao
mesmo tempo.

1. Token ausente ou inválido: `401`.
2. Token sem nenhum papel de envio: `403` com o corpo de erro padrão do
   framework, sem `type` do catálogo.
3. Teto bruto de requisições por principal estourado: `429` com o corpo de erro
   padrão do framework e **sem** `Retry-After` (seção 8).
4. `Idempotency-Key` ausente, em branco ou com mais de 200 caracteres:
   `400` com `type` `idempotency-key-required`.
5. Corpo malformado ou fora das regras de forma: `400` com `type`
   `payload-invalid` e a lista de erros por campo em `errors` (seção 6).
6. Papel do token não cobre a classe pedida, ou o token não carrega identidade
   estável: `403` com `type` `class-not-allowed-for-principal`.
7. Chave de idempotência já conhecida: `200` no replay, `409` no conflito
   (seção 3).
8. Limite de negócio estourado: `429` com `type` `recipient-rate-limited` ou
   `principal-rate-limited`, conforme a dimensão, e cabeçalho `Retry-After`
   (seção 8).
9. Template recusa a solicitação: `422` com o motivo do catálogo no `type`
   (seção 5).
10. Aceite: `202`.

Ponto que vale saber: a idempotência é avaliada **antes** do limite de taxa,
então um replay legítimo nunca gasta orçamento do destinatário. E a
autorização é avaliada **antes** do catálogo, então um principal não
autorizado nunca descobre quais templates existem pela diferença entre dois
motivos de recusa.

Outro ponto, que muda o que você vê quando erra duas coisas ao mesmo tempo: a
chave de idempotência é conferida **antes** da forma do corpo. Um corpo
inválido enviado sem `Idempotency-Key` responde `idempotency-key-required`, e
não `payload-invalid`. Isso é deliberado: a recusa por forma grava trilha, e a
trilha precisa da chave para identificar a entidade que ela registra. Corrija a
chave primeiro e reenvie para ver o relatório de campos.

### 2.2 Kafka: tópico `notifications.requested.v1`

Envelope CloudEvents 1.0 em modo estruturado, JSON. A chave do registro é o
`recipientId`, o que preserva ordem por cliente na entrada.

```json
{
  "specversion": "1.0",
  "id": "evt_01J5XB7QK2N4P6R8STUVWXYZ01",
  "source": "urn:araia:kyc-service",
  "type": "araia.notification.requested.v1",
  "time": "2026-08-24T14:03:11Z",
  "subject": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
  "datacontenttype": "application/json",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "data": {
    "application": "araia-cambio",
    "recipientId": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
    "idempotencyKey": "kyc-doc-approved:doc_8a1f",
    "class": "transactional",
    "templateKey": "kyc.document.approved",
    "locale": "pt-BR",
    "variables": { "documentType": "CNH" },
    "ttlSeconds": 86400,
    "correlationId": "trace-9b2d7c10"
  }
}
```

O `data` carrega os mesmos campos do corpo REST, com uma diferença: a chave de
idempotência viaja **dentro do corpo**, no campo `idempotencyKey`, porque não
existe cabeçalho HTTP para ela. Um evento sem `idempotencyKey` não é sequer
vinculado ao comando: vai direto para a dead letter com motivo
`payload-invalid`.

O `type` do envelope é a **versão do esquema**, e o hub o confere antes de olhar
o corpo. Um envelope com `type` diferente de `araia.notification.requested.v1`
vai para a dead letter com o motivo `event-type-unsupported`, mesmo que o
`data` esteja perfeito. Quando uma versão nova existir, o nome dela é que muda,
e o hub consome as duas durante a transição.

Cabeçalhos que o hub lê:

| Cabeçalho | Uso |
|---|---|
| `producer` | Nome lógico do produtor. É a identidade conferida contra o registro de produtores. Na ausência dele, o hub usa o `source` do CloudEvent. |
| `traceparent` | Contexto de rastreio, usado quando o envelope não traz o atributo `traceparent`. |

Autorização em duas camadas. A primeira é a ACL de escrita do broker. A segunda
é o registro de produtores dentro do hub, que autoriza a tripla identidade do
produtor, `application` e classe. Identidade fora do registro, ou pedindo
classe que o registro não concede, resulta em dead letter com motivo
`producer-not-authorized`.

As duas camadas fazem coisas diferentes, e vale entender qual faz qual. O
cabeçalho `producer` é escrito por você, e um consumidor Kafka não enxerga
identidade autenticada de quem publicou. Quem autentica de fato é a ACL de
escrita do broker; o registro de produtores é autorização declarativa e
auditável sobre um nome declarado. Consequência prática para o seu time: o
nome lógico do produtor é um compromisso operacional, não uma credencial.
Nunca publique com o nome de outro time, e trate a ACL de escrita no tópico de
entrada como a concessão sensível que ela é.

Duas regras operacionais do produtor no barramento:

- Publique pelo **seu próprio outbox**, na transação do evento de negócio.
  Publicar direto do handler reintroduz exatamente a perda que o outbox existe
  para evitar.
- Mantenha o registro dentro de 256 KB. Sem anexos e sem dado de contato: o
  hub só aceita `recipientId`.

O tópico de entrada retém 24 horas. Se a ingestão do hub ficar parada mais que
isso, os registros mais antigos se perdem, e a recuperação é o produtor
reemitir.

### 2.3 A regra que separa os dois caminhos

**Template que declara variáveis sensíveis só aceita solicitação por REST.**
Um evento no barramento para um template assim é recusado com o motivo
`sensitive-variables-on-bus`, vai para a dead letter e gera registro de
auditoria.

A razão é o meio, não a mensagem: um tópico Kafka é lido por qualquer
consumidor com ACL de leitura e retém as mensagens por dias, enquanto uma
chamada síncrona entrega o valor a um destinatário só e não o persiste em
lugar nenhum do transporte. Um código de uso único parado num tópico por 24
horas é uma exposição que nada compensa.

Duas consequências que o produtor precisa entender:

1. **A recusa depende apenas da declaração do template, nunca do payload.** Se
   o template declara variável sensível, todo evento para ele é recusado, mesmo
   que aquele evento não traga a variável. É isso que torna a regra decidível
   antes de publicar: basta saber se o template declara.
2. **A checagem roda antes da validação de variáveis.** Um evento recusado por
   essa regra nunca tem o corpo inspecionado contra o esquema, porque a
   validação produziria um relatório sobre exatamente o payload que não deve
   ser lido.

Na prática: OTP e alertas de segurança com segredo continuam em REST, que de
todo modo já precisa da resposta síncrona. O barramento serve o resto:
confirmação de operação, documento aprovado, status de pedido.

## 3. Idempotência

**Escopo da chave**: o par `(application, idempotencyKey)`. Duas aplicações
podem usar a mesma chave sem colidir; a mesma aplicação, não.

**Janela**: 24 horas. Depois disso o registro é purgado e a mesma chave passa a
criar uma notificação nova, de propósito.

**Replay com o mesmo corpo**: `200` com o **mesmo** `notificationId` do aceite
original. Nada é reprocessado, nenhum evento novo é publicado no barramento.

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "notificationId": "ntf_01K3XQ8V2M4T6Y9BCDEFGHJKMN",
  "status": "accepted"
}
```

**Mesma chave com corpo diferente**: `409`.

```http
HTTP/1.1 409 Conflict
Content-Type: application/problem+json

{
  "type": "idempotency-key-conflict",
  "title": "idempotency-key-conflict",
  "status": 409,
  "detail": "A mesma chave de idempotência já foi usada com um corpo diferente."
}
```

No caminho Kafka, o mesmo conflito vira dead letter com motivo
`idempotency-key-conflict`. Nos dois caminhos o conflito gera registro de
auditoria e evento de rejeição.

**Como o hub compara os corpos.** Ele calcula um SHA-256 sobre uma forma
canônica do corpo: membros em ordem fixa, ausentes omitidos, `variables` e
`metadata` canonicalizados recursivamente com chaves em ordem, `scheduledAt`
normalizado para UTC. Consequências práticas:

- Dois corpos que diferem só na ordem das propriedades, no espaçamento ou no
  fuso de um mesmo instante **têm o mesmo hash** e são o mesmo replay.
- `channelsHint` e `metadata` **entram no hash**, mesmo sendo ignorados pelo
  roteamento. Mudar qualquer um deles e repetir a chave produz `409`, não um
  replay.
- `locale` **não entra no hash**. Duas tentativas com a mesma chave que
  diferem só no locale, inclusive uma delas sem o campo, resolvem como replay.
  Ele é a única exceção entre os campos sem efeito, e por um motivo: um campo
  que não alcança decisão nenhuma do hub não identifica a notificação, e fazer
  a retentativa que corrigiu o locale colidir com a tentativa original seria
  quebrar exatamente o caminho que a idempotência existe para proteger.

**Derive a chave do evento de negócio, nunca gere aleatória.** Uma chave
aleatória por tentativa não protege nada: cada retentativa do seu cliente HTTP
manda uma chave nova e cria uma notificação nova, que é precisamente a
duplicata que a idempotência existe para impedir. A chave precisa ser função
determinística do fato de negócio que motivou a notificação:

```text
Bom:  kyc-doc-approved:doc_8a1f
Bom:  login-otp:cus_01J5X9...:2026-08-24T14:03:11Z
Bom:  cambio-confirmado:ord_7c1e
Ruim: 3f9a1c2e-...-aleatório-por-tentativa
```

Se dois fatos de negócio distintos puderem gerar a mesma chave, inclua na
chave o que os distingue. Se o mesmo fato puder ser reprocessado
legitimamente mais de uma vez em 24 horas (por exemplo, reenvio de OTP a
pedido do cliente), inclua na chave o que caracteriza a nova tentativa, como o
instante da solicitação do cliente.

**A idempotência não é a única barreira contra duplicata.** A política de
classe tem uma janela de deduplicação própria, sobre a tripla
`(application, templateKey, recipientId)`. Duas solicitações com chaves de
idempotência diferentes, para o mesmo template e o mesmo destinatário, dentro
dessa janela, resultam na segunda recusada com o motivo `duplicate-window`.
Essa recusa é assíncrona: chega pelo evento de rejeição e pela consulta, nunca
como status HTTP.

## 4. O que o hub decide e o produtor não

Três decisões pertencem à política publicada e ao catálogo, não à solicitação:

- **Canal**: quais canais são elegíveis para a classe, e a ordem em que são
  tentados.
- **Versão do template**: o hub usa sempre a versão publicada no momento em
  que renderiza. Se uma publicação acontecer entre a aceitação e o
  processamento, a notificação registra a versão que realmente foi renderizada.
- **Ordem de fallback**: derivada do plano de entrega da política, filtrado
  pelos canais com conteúdo publicado e pelos canais em que o destinatário é
  alcançável.

Três campos da solicitação são aceitos e não têm efeito nesta versão. O hub os
valida e depois os descarta. Dois deles, `channelsHint` e `scheduledAt`, entram
no hash de idempotência; `locale` não entra, e a seção 3 explica por quê.

**`channelsHint`**: aceito e ignorado. A ordem efetiva é a do plano da
política. O motivo é que o hint não é persistido na aceitação, então a regra de
seleção de canal roda sem ele. Nenhuma reordenação por solicitação existe hoje.
*Critério de retorno*: o primeiro produtor com necessidade demonstrada de
reordenar preferência por solicitação. Quando isso acontecer, o hint volta como
reordenação **dentro** dos canais já permitidos, jamais como adição de canal.

**`locale`**: opcional, não persistido e fora do hash de idempotência. O locale
de renderização vem do perfil do destinatário ou do padrão do template. Omita o
campo sem receio, e se enviar não espere que ele mude o idioma da mensagem.
Para influenciar o idioma, ajuste a preferência do destinatário pela rota de
contatos (seção 7).

**`scheduledAt`**: aceito, armazenado e sem efeito. A notificação é enfileirada
imediatamente e processada assim que o pipeline a pegar. Além disso, o prazo de
expiração é calculado como instante de aceite mais `ttlSeconds`, sem considerar
o agendamento. Não existe liberador de agendamento nesta versão. *Critério de
retorno*: a entrega do liberador de adiamento, que é o mesmo componente de que
a janela de silêncio depende.

Consequência direta: **não use o hub para agendar**. Peça a notificação no
instante em que ela deve sair.

## 5. Como saber o que aconteceu

Existem dois caminhos, e eles respondem perguntas diferentes: os eventos de
saída avisam quando algo terminal acontece; a consulta responde sobre uma
notificação específica quando você quiser.

### 5.1 Eventos de saída, no tópico `notifications.events.v1`

Envelope CloudEvents 1.0, chave do registro igual ao `recipientId`, cabeçalho
`eventType` com o tipo do evento para filtrar sem abrir o corpo. Nenhum evento
carrega conteúdo renderizado nem dado de contato.

| Tipo | Quando é publicado | O que afirma |
|---|---|---|
| `araia.notification.rejected.v1` | A ingestão ou a política recusou | O hub recusou a solicitação, pelo motivo do catálogo canônico |
| `araia.notification.failed.v1` | O plano de entrega se esgotou, ou a notificação expirou | A entrega não aconteceu |
| `araia.notification.delivered.v1` | Um envio por **push** foi aceito pelo provedor | Apenas push, e apenas aceitação pelo provedor |
| `araia.notification.consent_changed.v1` | O ledger de consentimento registrou uma mudança | Estado de consentimento por finalidade e canal |

```json
{
  "specversion": "1.0",
  "id": "01931f7c8a4b7e2d9c5f0a1b2c3d4e5f",
  "source": "urn:araia:notification-hub",
  "type": "araia.notification.rejected.v1",
  "time": "2026-08-24T14:03:12Z",
  "subject": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
  "datacontenttype": "application/json",
  "data": {
    "idempotencyKey": "kyc-doc-approved:doc_8a1f",
    "reason": "no-consent",
    "class": "transactional",
    "templateKey": "kyc.document.approved",
    "correlationId": "trace-9b2d7c10"
  }
}
```

**O `notificationId` do evento de rejeição é opcional e está ausente nas
recusas de ingestão**, porque nesse ponto ainda não existe notificação
registrada. A correlação disponível ali é a `idempotencyKey`, que é sua. Nas
recusas do pipeline o `notificationId` está presente.

```json
{
  "type": "araia.notification.failed.v1",
  "data": {
    "notificationId": "0193...",
    "lastChannel": "email",
    "reason": "http-400",
    "correlationId": "trace-9b2d7c10"
  }
}
```

**Dois vocabulários que nunca se misturam.** O `reason` de `rejected` pertence
ao catálogo fechado da seção 6. O `reason` de `failed` é **vocabulário
aberto**: ele carrega o código que o provedor devolveu, um motivo de alvo
inutilizável do hub como `no-active-device-token`, ou `expired` quando a
notificação venceu antes de chegar a um canal. Valores novos aparecem sem
mudança de esquema, porque quem os cunha é o provedor. Não valide
`failed.reason` contra o catálogo, e agrupe por família de código em painel e
alarme, nunca por enumeração fechada.

Dois casos em que **nenhum evento é publicado**, e você precisa saber deles:

- Corpo malformado sem `recipientId`. O contrato de saída chaveia todo evento
  pelo sujeito, e não há sujeito. A recusa existe na trilha e, no caminho
  Kafka, na dead letter.
- Estouro do limite por principal. Sob a pressão que o controle existe para
  absorver, um evento por requisição recusada seria a própria tempestade.

### 5.2 Consulta REST

Papel `Notifications.Read`. É papel de atendimento e ferramenta interna: os
papéis de envio não dão leitura.

| Rota | Responde |
|---|---|
| `GET /v1/notifications/{id}` | Estado agregado de uma notificação, avaliações de política e tentativas |
| `GET /v1/recipients/{recipientId}/notifications` | Histórico de um destinatário |
| `GET /v1/notifications?correlationId=` | Todas as notificações de uma transação de negócio |

Só busca por identidade exata. Não existe busca por prefixo, curinga, listagem
sem sujeito nem listagem por `application` sozinha. Identificador malformado é
`400` com `type` `invalid-request`; identificador bem formado e inexistente é
`404` com corpo padrão, sem eco do valor recebido.

**A consulta lê a réplica.** Logo após o `202`, uma leitura imediata pode
devolver `404` ou um estado anterior ao mais recente.

**Janela obrigatória nas rotas de lista.** `to` assume agora, `from` assume `to`
menos **90 dias**, e o intervalo máximo é **180 dias**. Janela invertida ou
maior que o máximo é `400`, nunca cortada em silêncio. A janela efetiva volta
na resposta, para você distinguir histórico vazio de janela que não cobria as
linhas.

**Paginação**: `limit` assume 50, teto 200, fora da faixa é `400`. O cursor é
opaco e carrega só a posição, então repita a mesma janela da primeira página
em toda página seguinte; cursor apontando para fora da janela pedida é `400`
com `type` `invalid-cursor`.

```http
GET /v1/recipients/cus_01J5X9ZK8QF3V7NB2E4D6TAHMC/notifications?class=critical&limit=2 HTTP/1.1
Authorization: Bearer eyJhbGciOi...
```

```json
{
  "items": [
    {
      "id": "ntf_01K3XQ8V2M4T6Y9BCDEFGHJKMN",
      "application": "araia-cambio",
      "recipientId": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
      "class": "critical",
      "status": "delivered",
      "templateKey": "auth.otp.login",
      "templateVersion": 7,
      "createdAt": "2026-08-24T14:03:11.482913+00:00",
      "correlationId": "trace-7c1e4b90"
    }
  ],
  "nextCursor": "MjAyNi0wOC0yNFQxNDowMzoxMS40ODI5MTNafG50Zl8wMUszWFE4VjJNNFQ2WTlCQ0RFRkdISktNTg",
  "window": {
    "from": "2026-05-26T14:10:00+00:00",
    "to": "2026-08-24T14:10:00+00:00"
  }
}
```

A leitura de uma notificação traz o estado agregado, as avaliações de política
e as tentativas:

```json
{
  "id": "ntf_01K3XQ8V2M4T6Y9BCDEFGHJKMN",
  "application": "araia-cambio",
  "class": "critical",
  "status": "delivered",
  "templateKey": "auth.otp.login",
  "templateVersion": 7,
  "requestedBy": "a1b2c3d4-0000-0000-0000-000000000001",
  "createdAt": "2026-08-24T14:03:11.482913+00:00",
  "expiresAt": "2026-08-24T14:08:11.482913+00:00",
  "correlationId": "trace-7c1e4b90",
  "policyVersion": 3,
  "policyEvaluations": [
    { "rule": "ConsentGate", "result": "allow", "evaluatedAt": "2026-08-24T14:03:12+00:00" },
    { "rule": "ChannelSelection", "result": "filter", "evaluatedAt": "2026-08-24T14:03:12+00:00" }
  ],
  "attempts": [
    {
      "sequence": 1,
      "channel": "push",
      "status": "sent",
      "contentHashFull": "9f2c...",
      "contentHashMasked": "41ab...",
      "createdAt": "2026-08-24T14:03:12+00:00",
      "providerKey": "fcm",
      "providerMessageId": "projects/araia/messages/0:1756...",
      "sentAt": "2026-08-24T14:03:12.910000+00:00",
      "target": { "kind": "device", "deviceTokenId": "3f9a...", "platform": "android" }
    }
  ]
}
```

Estados possíveis da notificação: `accepted`, `dispatched`, `delivered`,
`rejected`, `failed`, `expired` e `deferred`. Estados possíveis da tentativa:
`queued`, `sending`, `sent`, `failed` e `unknown`.

A consulta não devolve conteúdo renderizado em forma alguma, nem as variáveis.
Da tentativa saem apenas os dois hashes do conteúdo. Em canal de contato o alvo
sai com o valor **mascarado**; em push sai a plataforma e o identificador do
registro de dispositivo, nunca o token.

### 5.3 O que esta versão não sabe

Esta é a parte que mais gera engano, então ela está escrita sem rodeio.

**Confirmação de entrega pelo provedor não existe.** O hub não coleta eventos
de retorno de provedor nesta versão. Não há webhook, não há reconciliação e não
há tabela de eventos de entrega. A resposta da consulta **não declara** membro
de evento de entrega: ele não vem como lista vazia, porque lista vazia
afirmaria que não houve evento, e a versão atual não pode afirmar isso.

**`sent` afirma aceitação pelo provedor, nunca entrega.** Os campos `sentAt` e
`providerMessageId` de uma tentativa dizem que o provedor assumiu
responsabilidade pela mensagem. Não dizem que ela chegou ao aparelho, à caixa
de entrada nem aos olhos do cliente.

**`delivered` existe apenas para push.** Para push, aceitação pelo provedor é o
sinal de entrega que o desenho define, então a notificação transita para
`delivered` e o evento `araia.notification.delivered.v1` é publicado. Para
e-mail isso não acontece: a notificação permanece em `dispatched` e a tentativa
em `sent`, e nenhum evento de entrega é publicado. Um e-mail bem-sucedido
**nunca** produz `delivered` nesta versão.

**Tentativa em `unknown` não progride.** Quando o provedor responde com
timeout ou erro de servidor, sem veredito conclusivo, a tentativa fica em
`unknown` e permanece assim: sem reconciliação, nada a resolve nesta versão.
Trate `unknown` como indeterminado, não como falha e não como sucesso.

**Supressão de contato é detectada e registrada internamente, não anunciada.**
O evento `araia.notification.contact_suppressed.v1` não é publicado nesta
versão. Não é que o hub ignore o fato: um token de push revogado pelo provedor
já é registrado na trilha, na mesma transação do veredito do envio, e o
dispositivo deixa de ser alcançável a partir dali. O que falta é o anúncio no
barramento, mais os gatilhos que dependem de retorno de provedor, bounce de
e-mail e número inválido. A entrega do evento é de versão futura, na mesma
ressalva do `delivered` de e-mail.

**Não existe stream de mudanças de status.** Não há assinatura por evento
enviado pelo servidor. O que existe é o tópico de saída e a consulta.

## 6. Motivos de rejeição

Primeiro a distinção que organiza tudo:

- **Rejeição de negócio** é um desfecho válido do hub. Ele funcionou, avaliou e
  concluiu que a notificação não deve sair. Chega como `422` ou `429`
  `recipient-rate-limited` na ingestão REST, ou como evento `rejected` e status
  `rejected` na consulta (no pipeline). Não é erro, não deve virar alerta de
  falha do seu serviço e, na maioria dos casos, não deve ser retentada da mesma
  forma.
- **Erro de protocolo** é problema da sua requisição ou do seu token: `400`,
  `401`, `403`, `429` `principal-rate-limited` e `5xx`. Aqui sim há algo a
  corrigir no cliente, ou a retentar.

Repare que a linha divisória é o que **você faz**, e não onde o valor mora: o
catálogo tem membros dos dois lados. `payload-invalid` é do catálogo e é erro
de protocolo, porque quem corrige é o cliente; `recipient-rate-limited` é do
catálogo e é rejeição de negócio, porque o hub decidiu proteger o cliente.

O catálogo canônico de motivos vale para o `reason` de `rejected` e para o
`type` do problema em todas as recusas de negócio e de forma da ingestão. É um
vocabulário fechado, e fora dele existem exatamente dois `type` só de
protocolo, listados no fim desta seção.

| Motivo | O que significa | O que o produtor faz |
|---|---|---|
| `template-not-found` | A aplicação não tem template publicado com essa chave | Confira `application` e `templateKey`. Template criado mas nunca publicado também cai aqui |
| `template-deprecated` | O template não aceita mais solicitações novas | Migre para a chave sucessora com o time dono do template |
| `template-disabled` | O template foi desligado | Pare de solicitar e procure o time dono do template |
| `template-class-mismatch` | O template pertence a outra classe | Corrija a `class` da solicitação para a classe do template |
| `template-variables-invalid` | As variáveis não passam no esquema publicado | Corrija o payload usando os `checks` da resposta. Não retente o mesmo corpo |
| `template-render-failed` | A renderização do conteúdo publicado falhou | Não é corrigível pelo produtor. Acione o time dono do template |
| `producer-not-authorized` | A identidade do produtor está fora do registro, ou pede classe que o registro não concede | Peça o registro ou o ajuste de concessão. Só ocorre no caminho Kafka |
| `class-not-allowed-for-principal` | O token não carrega o papel da classe pedida, ou não carrega identidade estável | Peça a atribuição do papel ao seu principal. Só ocorre no caminho REST |
| `sensitive-variables-on-bus` | O template declara variáveis sensíveis e a solicitação veio pelo barramento | Migre a solicitação desse template para REST |
| `no-valid-contact` | Nenhum canal sobreviveu ao cruzamento entre plano da política, canais com conteúdo publicado e canais em que o destinatário é alcançável | Não retente igual. Verifique o cadastro do destinatário; se ele estiver correto, a causa é conteúdo publicado faltando para o canal, e é assunto do time do template |
| `no-consent` | O destinatário não consentiu com a finalidade em nenhum canal elegível | Não retente. Colete o consentimento pelo caminho de cadastro |
| `recipient-rate-limited` | O orçamento por destinatário daquela classe se esgotou | Não retente em laço. Respeite o intervalo e reavalie se o volume por cliente está correto |
| `duplicate-window` | Uma notificação equivalente está dentro da janela de deduplicação da política | Provavelmente é duplicata legítima detectada. Se não for, revise a chave de negócio que está gerando repetição |
| `payload-invalid` | O corpo é estruturalmente inválido, ou falha nas regras de forma | Corrija o corpo usando o dicionário `errors` da resposta. É o `type` do `400` no REST e, no Kafka, o motivo da dead letter para envelope ilegível ou sem `idempotencyKey` |
| `event-type-unsupported` | O `type` do envelope não é o que este tópico consome | Publique com `araia.notification.requested.v1`. Só ocorre no caminho Kafka. Não confunda com `payload-invalid`: aqui o corpo pode estar perfeito e a versão do envelope é que está errada |
| `idempotency-key-conflict` | A mesma chave chegou com corpo diferente | Escolha: se o corpo novo é o correto, use uma chave nova; se o antigo é o correto, pare de reenviar |
| `expired` | O TTL venceu antes de a notificação alcançar um canal | Solicite de novo se o fato de negócio ainda vale. Reavalie se o `ttlSeconds` é curto demais |
| `producer-disabled` | Declarado no vocabulário e **inalcançável nesta versão** | Nada. Ele existe para que o vocabulário não mude quando o desligamento de produtor chegar |

Exemplo de `422` com o relatório de verificações:

```http
HTTP/1.1 422 Unprocessable Content
Content-Type: application/problem+json

{
  "type": "template-variables-invalid",
  "title": "template-variables-invalid",
  "status": 422,
  "detail": "As variáveis da solicitação não passam no esquema publicado do template.",
  "checks": [
    {
      "name": "variables.required",
      "status": "failed",
      "message": "A variável obrigatória 'code' não foi informada.",
      "location": "variables.code"
    }
  ]
}
```

As mensagens de verificação nomeiam a variável e **nunca** carregam o valor
dela.

Três motivos do catálogo **não** aparecem como status HTTP, porque são
decididos depois do aceite, no pipeline: `no-valid-contact`, `no-consent`,
`duplicate-window`, além de `template-render-failed` e `expired`. Eles chegam
pelo evento `rejected` e pela consulta.

**O erro de forma no REST usa o catálogo, e mantém o relatório por campo.** Um
corpo que falha na validação recebe `payload-invalid` no `type` e a mesma lista
de erros por campo em `errors`:

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "payload-invalid",
  "title": "payload-invalid",
  "status": 400,
  "detail": "O corpo da solicitação não passa nas regras de forma da ingestão.",
  "errors": {
    "TtlSeconds": ["'Ttl Seconds' must be greater than '0'."]
  }
}
```

As chaves de `errors` são os nomes das propriedades do corpo em PascalCase, uma
entrada por regra que falhou. Use `type` para decidir o que fazer e `errors`
para saber onde corrigir.

Essa recusa gera trilha e evento de rejeição nos dois transportes, com o mesmo
motivo: o hub trata a falha de forma pelo que ela é, e não pelo transporte que
a carregou. A única exceção continua sendo o corpo malformado **sem
`recipientId`**, que não gera evento por falta de sujeito para chavear (seção
5.1).

Dois `type` de problema que existem no REST e não pertencem ao catálogo, porque
são condições de protocolo que nunca viajam no barramento:
`idempotency-key-required` e `principal-rate-limited`. O conjunto é fechado
nesses dois.

## 7. Dead letter

Só o caminho Kafka tem dead letter. No REST a recusa é a própria resposta.

### 7.1 Dead letter de notificações: `notifications.requested.dlt`

Vai para lá todo registro **permanentemente** inválido: envelope ilegível,
`type` de envelope não suportado, evento sem `idempotencyKey`, produtor não
autorizado, recusa do catálogo, conflito de idempotência, estouro do orçamento
por destinatário e recusa por variável sensível. Falha transitória nunca vai para a dead letter: nesse caso o
consumidor para de ler a partição e aplica contrapressão, sem avançar o offset.

Cabeçalhos de diagnóstico do registro:

| Cabeçalho | Conteúdo |
|---|---|
| `reason` | Motivo do catálogo canônico |
| `sourceTopic` | Tópico de origem |
| `sourcePartition` | Partição de origem |
| `sourceOffset` | Offset de origem |
| `occurredAt` | Instante em que a recusa foi registrada |
| `redacted` | `true` quando o corpo publicado não é cópia fiel do original |
| `producer` | Nome lógico do produtor, quando conhecido |
| `application` | Aplicação declarada, quando o corpo pôde ser lido |
| `class` | Classe declarada, quando o corpo pôde ser lido |
| `idempotencyKey` | Chave declarada, quando o corpo pôde ser lido |
| `traceparent` | Contexto de rastreio, quando presente |

As coordenadas de origem são o que transforma "o produtor diz que não pediu"
numa afirmação conferível: elas apontam para o registro exato que o broker
ainda guarda, dentro da retenção de 24 horas do tópico de entrada.

**Primeira regra que o produtor precisa conhecer: a recusa por variável
sensível redige o corpo.** Para o motivo `sensitive-variables-on-bus`, e só
para ele, o corpo publicado na dead letter substitui o objeto `variables` pela
**lista de nomes** das variáveis sensíveis declaradas pelo template. Valores
nunca viajam, e o cabeçalho `redacted` vem `true` para que ninguém confunda o
registro com cópia fiel.

```json
{
  "specversion": "1.0",
  "id": "evt_01J5XB7QK2N4P6R8STUVWXYZ01",
  "source": "urn:araia:kyc-service",
  "type": "araia.notification.requested.v1",
  "subject": "cus_01J5X9ZK8QF3V7NB2E4D6TAHMC",
  "data": {
    "application": "araia-cambio",
    "templateKey": "auth.otp.login",
    "variables": ["code"]
  }
}
```

A razão é aritmética simples de retenção: o tópico de entrada retém 24 horas e
a dead letter retém 14 dias. Copiar o corpo original ali moveria o segredo para
um tópico que o guarda quatorze vezes mais, ou seja, o controle se derrotaria
pela própria mitigação. Corpo que não puder ser interpretado perde a seção
`data` inteira: na dúvida sobre onde estão os valores, nada vai.

Nos demais motivos o corpo original é preservado e `redacted` vem `false`,
então o reprocessamento auditado continua possível.

### 7.2 Dead letter de contatos: `contacts.events.dlt`

Este par de tópicos pertence ao time do cadastro, não ao produtor de
notificações, mas a regra vale a pena conhecer porque ela é oposta à anterior.

**Segunda regra: a dead letter de contatos não tem reprocessamento.** Ali a
redação é incondicional, e o corpo publicado nunca é o original. Todo registro
daquele tópico carrega e-mail ou telefone em claro por construção, então o que
é publicado é um **resumo reconstruído por lista de permissão**: tipo do
evento, origem, identificador do CloudEvent, contagem de pontos de contato e o
canal de cada um por posição, e as entradas de consentimento com finalidade,
canal, concessão, origem e versão do termo. Valor de contato nunca viaja, e o
hash dele também não, porque seria um pseudônimo estável e correlacionável, que
continua sendo dado pessoal.

Como o registro não é cópia fiel, **não existe redrive**. E não precisa
existir: a semântica dessa entrada é declarativa, então a correção é o cadastro
**reemitir o estado desejado**, que é idempotente por construção. O diagnóstico
sai do motivo, das coordenadas e do identificador do CloudEvent, e o corpo
original continua alcançável no tópico de entrada dentro das 24 horas de
retenção.

Motivos próprios dessa ingestão, que não se misturam com o catálogo de
notificações: `source-not-authorized`, `payload-invalid`,
`event-type-unsupported`, `recipient-unknown` e `no-contact-point-for-channel`.
Dois deles se escrevem igual em ambos os vocabulários, `payload-invalid` e
`event-type-unsupported`, e significam a mesma coisa em cada transporte, corpo
inválido e tipo de envelope não consumido. Ainda assim são vocabulários
separados: não valide um motivo de contato contra o catálogo de notificações
nem o contrário, porque os dois conjuntos evoluem por decisões diferentes.

## 8. Checklist de integração

Antes da primeira notificação, providencie:

**Identidade e papéis**

- Um principal de client credentials para o seu serviço.
- O papel de envio da classe que você vai pedir: `Notifications.Send.Critical`
  ou `Notifications.Send.Transactional`. O papel é por classe, então um serviço
  que pede as duas precisa das duas.
- O token precisa carregar uma identidade estável (`appid`, `oid` ou `sub`).
  Sem ela a ingestão responde `403`, porque não há o que gravar como
  solicitante na trilha.
- Se o seu time também vai **ler** notificações, peça `Notifications.Read`
  separadamente. Os papéis de envio não dão leitura, e hoje quem porta a
  leitura enxerga notificação de qualquer aplicação, então trate a concessão
  como decisão de segurança.
- O papel `Notifications.Audit` é de Compliance e Auditoria Interna. Ele abre
  conteúdo renderizado na forma mascarada e trilha completa, cada chamada
  gravando um registro de divulgação. Produtor não recebe esse papel.

**Registro de produtor, apenas no caminho Kafka**

- ACL de escrita no tópico de entrada para o seu principal do broker.
- Registro da tripla identidade do produtor, `application` e classes
  permitidas. Sem ele, todo evento seu vai para a dead letter com
  `producer-not-authorized`.
- Defina e combine o valor do cabeçalho `producer`: é ele que o hub confere
  contra o registro.

**Template publicado**

- O template precisa existir para a sua `application`, estar publicado e
  pertencer à classe que você vai pedir.
- A publicação exige quatro olhos: quem publica não pode ser quem criou ou
  editou a versão. Planeje isso no cronograma, porque não é um passo que uma
  pessoa faz sozinha.
- Combine com o time dono do template quais variáveis são sensíveis. Essa
  decisão determina se você pode usar o barramento (seção 2.3).
- Confira que existe conteúdo publicado para os canais do plano da política.
  Falta de conteúdo por canal aparece depois como `no-valid-contact`, que é
  fácil de confundir com problema de cadastro.

**Contatos e consentimento carregados**

- Pontos de contato do destinatário, pelo sistema de cadastro. As rotas exigem
  o papel `Contacts.Write`: `PUT /v1/recipients/{id}/contact-points`,
  `PUT /v1/recipients/{id}/consents` e `POST /v1/recipients/{id}/devices`.
- A declaração de pontos de contato é **conjunto completo**: o que a declaração
  omite passa a marcado como removido. Canais aceitos: `email`, `sms` e
  `whatsapp`.
- Consentimento é declarado por par de finalidade e canal, com origem (`app`,
  `atendimento` ou `importacao`) e versão do termo. Um consentimento que nomeia
  canal sem ponto de contato ativo é recusado com `422` e
  `no-contact-point-for-channel`.
- Token de push é registrado pelo **aplicativo**, nunca pelo sistema de
  cadastro, e só por REST. Plataformas aceitas: `ios`, `android` e `web`.
  Sem token registrado, push não é canal alcançável para aquele destinatário.

**Teste de ponta a ponta antes de ligar**

- Peça uma notificação real para um destinatário de teste com cadastro
  completo, e confirme o `202`.
- Repita a mesma requisição com a mesma chave e confirme o `200` com o mesmo
  identificador.
- Repita com corpo diferente e confirme o `409`.
- Consulte a notificação e confirme o estado e a tentativa.
- Assine o tópico de saída e confirme que você recebe os eventos.

## 9. Limites e comportamento sob pressão

Dois níveis de limite, com comportamentos diferentes.

**Teto bruto por principal, na borda HTTP.** Janela fixa de um minuto, contada
por principal autenticado, ou por endereço de origem quando não há principal.
Ingestão: 2.000 requisições por minuto. Consulta: 120 por minuto. Escrita de
contatos: 600 por minuto. O estouro devolve `429` com o corpo de erro padrão do
framework, sem `type` do catálogo e **sem `Retry-After`**. É um freio contra
abuso automatizado, não o limite de negócio: quando ele dispara, o hub não diz
quanto esperar, então o cliente precisa de recuo próprio.

**Limites de negócio, na ingestão.** Duas dimensões, ambas por classe canônica,
configuráveis por ambiente. A configuração vigente no repositório é:

| Dimensão | Chave do contador | Configuração vigente |
|---|---|---|
| Por principal | principal e classe | `critical` e `transactional`: 50 por segundo. `operational`: 20 por segundo |
| Por destinatário | aplicação, destinatário e classe | `critical`: no máximo 5 em 10 minutos **e** 20 em 24 horas. Sem limite configurado nas demais classes |

As janelas por destinatário são cumulativas: todas precisam passar. Uma classe
sem entrada configurada não tem limite naquela dimensão.

O estouro de limite de negócio no REST devolve `429` com o `type` da dimensão
que recusou. Por destinatário:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 384
Content-Type: application/problem+json

{
  "type": "recipient-rate-limited",
  "title": "recipient-rate-limited",
  "status": 429,
  "detail": "O orçamento de notificações deste destinatário na classe pedida se esgotou; não retente em laço."
}
```

Por principal:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 12
Content-Type: application/problem+json

{
  "type": "principal-rate-limited",
  "title": "principal-rate-limited",
  "status": 429,
  "detail": "O limite de solicitações do seu principal foi atingido; reduza a vazão e tente novamente após o intervalo indicado."
}
```

O `Retry-After` é o tempo restante da janela esgotada, em segundos. Quando mais
de uma janela por destinatário estoura, o valor é o da mais longa. Respeite-o:
retentar antes só consome o contador de novo.

**A diferença entre os dois transportes.** O limite por destinatário vale nos
**dois** caminhos e o orçamento é **compartilhado**: a chave do contador não
tem dimensão de transporte, então trocar de transporte não dobra o orçamento de
um cliente. Já o limite por principal **só rejeita no REST**. No Kafka ele é
contado e observado com registro próprio, mas **não rejeita**: não existe
chamador síncrono para receber um `429`, e transformar a rajada em rejeição
apenas moveria o volume para a dead letter. A parada real de um produtor
abusivo no barramento é a ACL de escrita do broker.

Consequência prática para quem publica no barramento: **você não recebe sinal
de contrapressão**. Se o seu serviço passar do orçamento por principal, os
eventos continuam sendo processados e nada te avisa. O controle de vazão do
lado do produtor é responsabilidade sua.

**Como o `429` se distingue no REST, e por que isso importa para o seu
cliente.** O `type` nomeia a dimensão, e as duas pedem comportamentos opostos:

| `type` | O que significa | O que o produtor faz |
|---|---|---|
| `recipient-rate-limited` | O orçamento daquele destinatário na classe pedida se esgotou | **Não retente esta solicitação.** O cliente está protegido de propósito. Reavalie se o volume por cliente está correto |
| `principal-rate-limited` | O seu próprio orçamento de requisições se esgotou | **Desacelere e retente** após o `Retry-After`. A solicitação continua legítima |

Trate os dois de forma diferente na sua política de retentativa: retentar em
laço um `recipient-rate-limited` só queima orçamento e não entrega nada,
enquanto desistir de um `principal-rate-limited` perde uma notificação que o
hub aceitaria alguns segundos depois.

Os dois também se distinguem no tópico de saída: o estouro por destinatário
publica `araia.notification.rejected.v1` com motivo `recipient-rate-limited`;
o estouro por principal não publica nada, porque um evento por requisição
recusada seria a própria tempestade que o controle existe para conter.

**Comportamento em degradação.** Os dois controles apoiados em armazenamento
externo (a via rápida de idempotência e o limitador de taxa) **falham abertos**
com alarme: em indisponibilidade, a ingestão continua aceitando. A idempotência
não se perde nisso, porque a autoridade dela é a restrição de unicidade no
banco, e não o cache. Já o limite de taxa realmente deixa de ser aplicado
enquanto durar a indisponibilidade, e a compensação é operacional. A barreira
de duplicata da política tem a mesma postura: em indisponibilidade ela permite
passar, e registra na evidência que passou aberta.
