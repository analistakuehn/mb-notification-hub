---
language: pt-BR
document_type: contract
status: verified
last_verified: 2026-09-04
---

# Guia de integração com o `mb-notification-hub`

| Metadado | Valor |
|---|---|
| Público | Times de engenharia que produzem notificações por REST ou Kafka |
| Responsável | Time de engenharia do `mb-notification-hub` |
| Estado | Vigente para solicitações sem anexos. A capacidade de anexos existe no código, é implantada desligada e depende de duas chaves independentes de ambiente; enquanto o time do hub não confirmar a habilitação, não envie o membro `attachments` |
| Última verificação | 4 de setembro de 2026, contra código, configuração e testes do repositório |
| Versões cobertas | HTTP `/v1`, CloudEvent de entrada `araia.notification.requested.v1` e eventos de saída `.v1` |

## Objetivo, público e escopo

Use este guia para **pedir notificações ao hub**, implementar idempotência,
tratar respostas e dead letters, consumir eventos de resultado e consultar o
estado de uma notificação. Ele descreve o contrato observável: rotas, corpos,
cabeçalhos, status e o significado de cada desfecho.

O guia cobre o ingresso de notificações, os pré-requisitos que um produtor
precisa cumprir, o acompanhamento do resultado e a recuperação que cabe ao
cliente. Não cobre administração de templates, operação do pipeline, gestão de
provedores, auditoria de Compliance nem as APIs de cadastro de contatos.

O fluxo de anexos permanece fora do onboarding enquanto a implantação não
habilitar a capacidade, e mesmo assim está descrito aqui. A razão é simples:
quem lê este guia é quem recebe a resposta, e uma recusa que um produtor
consegue observar sem conseguir procurar o significado é um diagnóstico
perdido. A seção 2.4 descreve a capacidade, as duas chaves que a governam e
todas as recusas que ela produz; a seção 6.1 lista as que chegam pelo ingresso
de notificações.

### Autoridade das fontes

O documento OpenAPI publicado pela própria API, em `GET /openapi/v1.json`, é a
superfície de descoberta do schema HTTP. A rota existe em todos os ambientes e
exige o mesmo token Bearer das demais, sem papel específico: uma chamada
anônima recebe `401`. Os endpoints de notificação ainda não declaram no OpenAPI
todos os corpos e status de saída. Use este guia para ordem das checagens,
semântica dos desfechos e comportamento de retry.

O repositório ainda não publica AsyncAPI nem JSON Schema para Kafka. Até esse
artefato existir, o contrato do barramento é o envelope e as regras documentadas
aqui, verificados contra o binder e os testes de contrato. Não infira garantias
de ACL, retenção ou quantidade de partições a partir dos exemplos: confirme a
configuração do ambiente com o time de Plataforma.

## Início rápido

1. Escolha REST quando precisar de resposta síncrona ou quando o template
   declarar variáveis sensíveis. Escolha Kafka para fatos de negócio publicados
   por outbox.
2. Obtenha o papel de envio da classe e um template publicado para a mesma
   `application`. No Kafka, obtenha também o tópico exclusivo atribuído ao seu
   produtor lógico e a autorização da tripla produtor, aplicação e classe.
3. Derive uma chave de idempotência estável do fato de negócio.
4. Envie apenas `recipientId`, `templateKey`, variáveis e contexto. Não envie
   e-mail, telefone, conteúdo renderizado nem preferência de canal como ordem.
5. No REST, trate `202` como persistência do aceite, não como entrega. No Kafka,
   o ack do broker também não é aceite do hub.
6. Consuma `notifications.events.v1`, acompanhe a dead letter quando usar Kafka
   e mantenha a consulta REST como caminho de diagnóstico.

## Segurança e autorização

No REST, use OAuth 2.0 client credentials e envie o token no cabeçalho
`Authorization: Bearer`. O token precisa conter uma identidade estável
(`appid`, `oid`, `sub` ou `NameIdentifier` mapeado) e o papel exato da classe
solicitada. Os papéis de envio não concedem leitura; consultas exigem
`Notifications.Read`.

No Kafka, a ACL do broker controla quem publica, mas a identidade lógica que o
hub usa vem **exclusivamente do tópico dedicado**. `source`, headers e campos do
payload não autenticam o produtor. Nunca coloque CPF, e-mail, telefone, token ou
segredo em `recipientId`, `metadata`, headers ou chaves Kafka. Templates que
declaram variáveis sensíveis só podem ser solicitados por REST.

## 1. O que o hub faz, em uma frase

O produtor pede uma notificação por chave de template e identificador de
destinatário. O hub decide canal, versão de template e ordem de tentativa a
partir da política publicada, resolve o contato e o consentimento, renderiza e
entrega pelo provedor. O produtor **não** envia texto, não envia endereço de
e-mail, não envia telefone e não escolhe canal.

Classes entregues nesta versão: `critical`, `transactional` e `operational`. A
classe `operational` tem janela de silêncio, e é a única que tem: uma
notificação pedida dentro da janela não é recusada nem perdida, ela é adiada
até o fim da janela no fuso do destinatário e sai sozinha depois disso, sem que
o produtor peça de novo. Conte com esse atraso ao escolher a classe: uma
mensagem que não pode esperar o amanhecer não é `operational`. A classe
`critical` e os templates de finalidade `authentication` nunca são adiados pela
janela, seja qual for a política publicada.

Canais entregues nesta versão: `email`, `push` e `sms`. O `sms` é reservado à
classe `critical`; liberá-lo para outra classe é mudança de política aprovada,
não decisão de solicitação. O vocabulário também conhece `whatsapp`, mas nenhum
adaptador hospedado o entrega: uma tentativa roteada para esse canal fica em
fila sem consumidor até o prazo do passo, quando existe, levar o plano ao canal
seguinte. A seção 4.1 explica de onde vem o canal de cada tentativa.

## 2. Como pedir uma notificação

### 2.1 REST: schema e validação de `POST /v1/notifications`

#### Schema da solicitação

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
| `locale` | não | Até 20 caracteres. Aceito, **sem efeito** e fora do hash de idempotência: veja a seção 4.2. |
| `ttlSeconds` | sim | Inteiro maior que zero e no máximo 2.592.000 (30 dias). |
| `variables` | não | Objeto JSON. Ausente ou `null` significa nenhuma variável. No máximo 262.144 bytes na forma compacta em UTF-8. |
| `channelsHint` | não | Lista de no máximo 4 strings não vazias, de até 20 caracteres cada. A implementação não confere catálogo nem unicidade. Aceita e ignorada: veja a seção 4.2. |
| `correlationId` | não | Até 200 caracteres. É por ele que a consulta agrupa uma transação de negócio. |
| `metadata` | não | Objeto JSON. Não é persistido, mas entra no hash de idempotência. No máximo 32.768 bytes na forma compacta em UTF-8, teto menor que o de `variables` porque o campo não é renderizado nem consultado, e mesmo assim é canonicalizado a cada requisição e a cada replay. |
| `scheduledAt` | não | ISO 8601. Aceito e **sem efeito** nesta versão: veja a seção 4.2. |
| `attachments` | não | Lista ordenada de referências opacas obtidas nas rotas `/v1/attachments`. Ausente ou `null` significa sem anexos. Lista vazia, referência em branco ou duplicata ordinal produz `400 payload-invalid`. Numa implantação que não aceita anexos novos, uma lista bem formada produz `422 attachment-capability-not-enabled` e nenhuma notificação. **Não use enquanto o time do hub não confirmar a habilitação da capacidade no ambiente.** |

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

O `202` afirma exatamente uma coisa: a solicitação foi aceita e a transação
local persistiu notificação, idempotência, auditoria e outbox. A publicação na
fila interna acontece depois. O status **não** afirma que existe destinatário,
que existe contato, que existe consentimento nem que a mensagem será entregue.
Essas perguntas são respondidas depois, pelos eventos de saída e pela consulta.

O identificador público tem a forma `ntf_` seguida de 26 caracteres do
alfabeto Crockford base32 em maiúsculas, sem as letras I, L, O e U.

**Ordem das checagens, do primeiro corte ao aceite.** A ordem é contrato: ela
decide qual resposta o produtor recebe quando mais de uma condição falha ao
mesmo tempo.

1. Token ausente ou inválido: `401`.
2. Token sem nenhum papel de envio: `403` com o corpo de erro padrão do
   framework, sem `type` do catálogo.
3. Teto bruto de requisições por principal estourado: `429` com o corpo de erro
   padrão do framework e **sem** `Retry-After` (seção 9).
4. `Idempotency-Key` ausente, em branco ou com mais de 200 caracteres:
   `400` com `type` `idempotency-key-required`.
5. Corpo malformado ou fora das regras de forma: `400` com `type`
   `payload-invalid` e a lista de erros por campo em `errors` (seção 6).
6. Papel do token não cobre a classe pedida, ou o token não carrega identidade
   estável: `403` com `type` `class-not-allowed-for-principal`.
7. Chave de idempotência já conhecida: `200` no replay, `409` no conflito
   (seção 3).
8. Produtor bloqueado pelo controle de emergência: `403` com `type`
   `producer-disabled`; autoridade desse controle indisponível: `503` com
   `type` `kill-switch-unavailable`.
9. Limite de negócio estourado: `429` com `type` `recipient-rate-limited` ou
   `principal-rate-limited`, conforme a dimensão, e cabeçalho `Retry-After`
   (seção 9).
10. Template recusa a solicitação: `422` com o motivo do catálogo no `type`
   (seção 6).
11. A solicitação nomeia anexos e o conjunto não foi vinculado: `422` com
    `type` `attachments-not-claimable`, ou `422` com `type`
    `attachment-capability-not-enabled` quando a implantação não aceita anexos
    novos. A seção 2.4 diz qual dos dois você recebe em cada caso.
12. Aceite: `202`.

Ponto que vale saber no REST: a idempotência autoritativa é avaliada **antes**
do limite de taxa, então um replay legítimo não gasta orçamento do
destinatário. No Kafka, essa precedência depende de acerto no fast path Redis;
uma falta no cache pode fazer o replay alcançar o limite antes da unicidade no
banco. Em ambos os caminhos, a autorização é avaliada antes do catálogo, então
um principal não autorizado não descobre quais templates existem pela diferença
entre motivos de recusa.

Outro ponto, que muda o que você vê quando erra duas coisas ao mesmo tempo: a
chave de idempotência é conferida **antes** da forma do corpo. Um corpo
inválido enviado sem `Idempotency-Key` responde `idempotency-key-required`, e
não `payload-invalid`. Isso é deliberado: a recusa por forma grava trilha, e a
trilha precisa da chave para identificar a entidade que ela registra. Corrija a
chave primeiro e reenvie para ver o relatório de campos.

### 2.2 Kafka: tópico dedicado ao produtor

Não existe um tópico único de ingresso. Cada produtor recebe um tópico
exclusivo, associado a um nome lógico na configuração do worker. O repositório
traz `notifications.requested.kyc.v1` e
`notifications.requested.billing.v1` como exemplos de bindings, mas use apenas
o tópico provisionado para o seu serviço.

Publique um envelope CloudEvents 1.0 em modo estruturado, JSON. Use
`recipientId` como chave do registro para manter a afinidade de partição na
entrada. O hub não compara essa chave com `data.recipientId`, e o processamento
interno não oferece ordenação ponta a ponta. Produtores e consumidores precisam
tolerar reordenação e duplicatas.

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

O `data` carrega os mesmos campos do corpo REST, inclusive o membro opcional
`attachments`, com uma diferença: a chave de idempotência viaja **dentro do
corpo**, no campo `idempotencyKey`, porque não existe cabeçalho HTTP para ela.
Um evento sem `idempotencyKey` vai para a dead letter com motivo
`payload-invalid`.

O `type` do envelope é a **versão do esquema**, e o hub o confere antes de olhar
o corpo. Um envelope com `type` diferente de `araia.notification.requested.v1`
vai para a dead letter com o motivo `event-type-unsupported`, mesmo que o
`data` esteja perfeito. A implementação atual aceita apenas esse tipo V1; não
há coexistência executável de V1 e V2.

O ingresso não define header Kafka obrigatório. O `source` continua obrigatório
como atributo CloudEvents, mas não autentica nem autoriza o produtor. Não envie
header `producer`: a identidade lógica vem exclusivamente do binding entre o
tópico consumido e o produtor. O header `traceparent` só possui fallback
comprovado no diagnóstico da dead letter; não dependa dele para propagação no
caminho aceito.

A autorização possui duas camadas. A Plataforma concede escrita no tópico ao
principal do broker. O hub deriva o produtor lógico daquele tópico e confere a
tripla exata produtor, `application` e classe no registro interno. Tripla fora
do registro resulta em dead letter com motivo `producer-not-authorized`.

Regras operacionais do produtor no barramento:

- Publique pelo **seu próprio outbox**, na transação do evento de negócio.
  Publicar direto do handler reintroduz exatamente a perda que o outbox existe
  para evitar.
- Mantenha o registro completo dentro de 262.144 bytes, limite bruto da
  configuração versionada. Não envie dado de contato: o hub aceita apenas
  `recipientId`.
- Trate o ack do broker como confirmação de publicação, não como aceite do hub.
  Observe a dead letter, os eventos de resultado e, quando autorizado, a
  consulta REST.
- Confirme com a Plataforma a ACL, a retenção e a quantidade de partições do
  ambiente. Esses recursos não são provisionados neste repositório.

O consumidor tenta novamente exceções transitórias e pausa a partição quando
esgota as tentativas locais. A implementação atual não comprova o
reposicionamento no offset recusado depois da retomada. Portanto, não use essa
pausa como garantia de reprocessamento. Preserve o fato no outbox, acompanhe a
ausência de desfecho pela chave de idempotência e acione o time do hub antes de
reemitir uma sequência potencialmente incompleta.

### 2.3 A regra que separa os dois caminhos

**Template que declara variáveis sensíveis só aceita solicitação por REST.**
Um evento no barramento para um template assim é recusado com o motivo
`sensitive-variables-on-bus`, vai para a dead letter e gera registro de
auditoria.

A razão é o meio, não a mensagem: um tópico Kafka pode ser lido por qualquer
consumidor com ACL e persiste registros conforme a retenção do ambiente,
enquanto uma chamada síncrona entrega o valor a um destinatário só. Um código
de uso único persistido no barramento amplia a exposição sem benefício.

Duas consequências que o produtor precisa entender:

1. **A recusa depende apenas da declaração do template, nunca do payload.** Se
   o template declara variável sensível, qualquer evento para ele é recusado,
   mesmo que aquele evento não traga a variável. É isso que torna a regra
   decidível antes de publicar: basta saber se o template declara.
2. **A checagem roda antes da validação de variáveis.** Um evento recusado por
   essa regra nunca tem o corpo inspecionado contra o esquema, porque a
   validação produziria um relatório sobre exatamente o payload que não deve
   ser lido.

Na prática: OTP e alertas de segurança com segredo continuam em REST, que de
qualquer forma já precisa da resposta síncrona. O barramento serve o resto:
confirmação de operação, documento aprovado, status de pedido.

### 2.4 Anexos: capacidade implantada desligada

`attachments` pertence ao contrato V1 de REST e Kafka desde a publicação do
membro, para que clientes sem ele continuem compatíveis. A lista carrega
referências opacas, não bytes. Ausente e `null` significam sem anexos. Quando
presente, a lista precisa conter ao menos uma referência não branca, sem
repetição ordinal. O hub preserva ordem, caixa e grafia, e esses três aspectos
participam da identidade idempotente.

**O que a capacidade faz.** Ela existe inteira no código. As rotas
`/v1/attachments` registram um anexo, recebem o conteúdo, pedem a validação e
revogam a liberação. O aceite de uma notificação vincula o conjunto inteiro na
mesma transação que grava notificação, idempotência, trilha e outbox, e o
conjunto vinculado é congelado ali: é ele que viaja com a notificação, não a
lista que a solicitação enviou. Antes de cada envio, o hub reconfere a
liberação de cada anexo e a capacidade agregada do conjunto.

**Isso não significa que o envio com anexos esteja liberado.** A capacidade é
implantada desligada, e são duas chaves independentes de ambiente:

- **A seção de capacidade do módulo** governa o aceite de coisa nova. Com ela
  desligada, nenhuma referência é cunhada pelo registro e nenhum conjunto novo
  é vinculado por uma aceitação. Ela não é consultada por nada que trabalhe
  sobre anexo já existente, então ligar ou desligar não congela notificação em
  andamento nem invalida conjunto já aceito.
- **A lista de tipos de conteúdo admitidos**, vazia na configuração versionada,
  governa a liberação. Um conteúdo cujo tipo não está na lista é recusado na
  validação, o anexo nunca alcança o estado liberado, e anexo não liberado não
  é vinculado a notificação nenhuma.

Ligar a primeira **não** habilita anexos. Com a lista de tipos vazia nenhum
anexo chega a liberado, e conjunto não liberado não passa do aceite. Habilitar
de verdade é decisão de ambiente que passa pelas duas chaves, e o time do hub
confirma quando estiver feita.

Até essa confirmação:

- omita `attachments` ou envie `null`;
- não trate as rotas `/v1/attachments` como capacidade pronta para produção;
- não inclua anexos no teste de habilitação da sua integração;
- não use os valores configurados de quantidade máxima ou envelope agregado
  como limites contratuais. Eles não são conferidos no aceite: o hub mede o
  conjunto aceito e reconfere a liberação de cada anexo imediatamente antes de
  cada envio, e um conjunto que não passa faz a tentativa falhar sem chamada ao
  provedor, com `attachments-over-capacity` ou `attachments-withheld` em
  `attempts[].errorCode`;
- saiba que o transporte do conjunto é propriedade do canal: um plano cujo canal
  não transporta anexos recusa a notificação com
  `attachments-not-carried-by-channel`, e o mesmo motivo encerra a notificação
  quando é o passo seguinte do plano que não transporta. Nenhum canal alternativo
  é tentado e nenhum anexo é convertido em link.

**O que você recebe se enviar mesmo assim.** Uma lista malformada é
`400 payload-invalid`, como qualquer defeito de forma. Uma lista bem formada
alcança o vínculo, e o vínculo responde com uma de duas palavras:

| Resposta | Quando | O que o produtor faz |
|---|---|---|
| `422 attachment-capability-not-enabled` | A implantação não aceita anexos novos. Nada foi apontado no conjunto que você nomeou | Espere a confirmação da habilitação. A mesma solicitação é aceita depois, sem alterar o corpo |
| `422 attachments-not-claimable` | O conjunto não pode ser vinculado a esta solicitação | Não retente o mesmo conjunto. Enquanto anexos não estiverem liberados, omita o membro |

A ordem entre as duas é contrato, porque decide qual você recebe quando as duas
condições valem ao mesmo tempo. O vínculo confere primeiro a identidade do
conjunto, depois o que a sua chave de idempotência já vincula, e só então a
capacidade da implantação. Consequências:

- uma referência inexistente ou de outra aplicação responde
  `attachments-not-claimable` mesmo numa implantação que não aceita anexos
  novos, porque a identidade é conferida antes;
- uma repetição da mesma chave sobre um conjunto que ela já vinculou continua
  respondendo o aceite original depois de a capacidade ser desligada. Desligar
  bloqueia aceite novo e não transforma retentativa de aceite antigo em
  rejeição.

Nenhuma das duas gera evento `araia.notification.rejected.v1`. As duas gravam
registro de auditoria, e no Kafka as duas vão para a dead letter com o mesmo
motivo que o REST devolve no `type`.

**`attachments-not-claimable` é deliberadamente genérico, e continua sendo.**
Ele não revela qual referência falhou nem se ela era inexistente, estrangeira,
não liberada ou revogada. A propriedade é de segurança: um produtor capaz de
distinguir esses casos descobriria por tentativa quais referências existem e
quais pertencem a outra aplicação. A palavra nova não abre essa porta, porque
capacidade desligada é fato sobre a implantação, igual para todo chamador e
para toda referência: saber que ela está desligada não ensina nada sobre anexo
de terceiro. Para diagnóstico use a resposta REST ou as coordenadas da dead
letter com o time do hub.

**O vínculo é integral.** As referências precisam pertencer à mesma
`application` e estar liberadas. Se uma delas falhar, nenhuma é vinculada e não
há aceite parcial.

**As recusas das rotas `/v1/attachments`.** Elas formam o vocabulário do fluxo
de anexos e não se misturam com o catálogo de motivos de notificação da seção
6. Cada uma chega como problema RFC 9457 com o código no `type` e no `title`.
Essas rotas exigem token autenticado com identidade estável e uma concessão por
aplicação registrada para o seu principal; não existe papel de aplicação que as
abra.

| Código | Status | O que significa |
|---|---|---|
| `attachment-capability-not-enabled` | `409` | A implantação não aceita anexos novos. Nada do arquivo foi olhado |
| `attachment-metadata-invalid` | `400` | Nome, tipo declarado ou tamanho não passam nas regras de registro |
| `attachment-size-mismatch` | `400` | O conteúdo enviado não tem o tamanho registrado para o anexo |
| `attachment-access-denied` | `403` | O principal não tem concessão para a aplicação declarada no registro |
| `attachment-not-found` | `404` | Não há anexo alcançável por este principal com essa referência |
| `attachment-already-received` | `409` | O conteúdo desse anexo já foi recebido |
| `attachment-upload-conflict` | `409` | O envio não pôde ser concluído. Consulte o anexo antes de repetir |
| `attachment-content-refused` | `409` | O conteúdo não foi liberado. Uma palavra só para toda a família de recusas de conteúdo |
| `attachment-content-missing` | `409` | Pediram validação de um anexo cujo conteúdo nunca chegou |
| `attachment-not-released` | `409` | Pediram revogação de um anexo sem liberação vigente |
| `attachment-revoked` | `409` | A liberação foi retirada |
| `attachment-discarded` | `409` | O conteúdo foi descartado. Registre um anexo novo |
| `attachment-authorization-unavailable` | `503` | A autoridade das concessões não respondeu |
| `attachment-store-unavailable` | `503` | O armazenamento não respondeu |
| `attachment-store-unidentified-generation` | `503` | O armazenamento aceitou os bytes sem identificar a geração gravada |
| `attachment-generation-unreadable` | `503` | A geração recém-gravada não pôde ser lida de volta |
| `attachment-lifecycle-unavailable` | `503` | A transição de ciclo de vida não avançou, e nada foi gravado |
| `attachment-operation-failed` | `500` | Falha que o módulo não classificou |

Três observações que mudam o que você conclui de uma resposta dessas:

- **Um corpo fora das regras de forma no registro responde `400` com o
  dicionário `errors` do framework e sem `type` do módulo.** O código
  `attachment-metadata-invalid` é a recusa das regras de domínio, que ficam
  atrás da validação de forma.
- **Nas rotas que nomeiam uma referência, negação de acesso e anexo
  inexistente respondem igual: `404`.** É deliberado, pela mesma razão da
  genericidade acima: distinguir os dois transformaria a rota num detector de
  referências alheias. Só o registro, que declara a aplicação no corpo,
  responde `403`.
- **`attachment-content-refused` também é genérico de propósito.** Arquivo
  irreconhecível, declaração que os bytes contradizem, tipo que ninguém
  admitiu e veredito que não concluiu saem todos com essa palavra. Qual
  checagem recusou fica no registro durável e sai pela consulta operacional,
  que não é papel de produtor: um produtor que lesse essa distinção saberia
  qual checagem contornar.

O teto bruto de requisições dessa superfície está na seção 9.

## 3. Idempotência

**Escopo da chave**: o par `(application, idempotencyKey)`. Duas aplicações
podem usar a mesma chave sem colidir; a mesma aplicação, não.

**Retenção**: o registro se torna elegível para remoção depois de 24 horas, e o
purge padrão roda a cada hora. Falhas do job podem prolongar esse período. Não
trate 24 horas como corte exato nem reutilize deliberadamente uma chave antiga;
prefira uma chave nova para um fato de negócio novo.

**Replay REST com o mesmo corpo**: `200` com o **mesmo** `notificationId` do
aceite original. Nada é reprocessado, nenhum evento novo é publicado no
barramento.

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
- `attachments` entra no hash como sequência. Trocar a ordem, a caixa, a grafia
  ou uma referência e repetir a chave produz `409`. Ausência e `null` preservam
  a forma de uma solicitação sem anexos.
- `locale` **não entra no hash**. Duas tentativas com a mesma chave que
  diferem só no locale, inclusive uma delas sem o campo, resolvem como replay.
  Ele é a única exceção entre os campos sem efeito, e por um motivo: um campo
  que não alcança decisão nenhuma do hub não identifica a notificação, e fazer
  a retentativa que corrigiu o locale colidir com a tentativa original seria
  quebrar exatamente o caminho que a idempotência existe para proteger.

No Kafka, a mesma chave continua sendo a barreira de negócio, mas não existe
resposta síncrona. Além disso, a precedência do replay sobre o rate limit depende
do fast path Redis: numa falta de cache, o limite pode ser avaliado antes de a
unicidade persistida resolver a duplicata. Mantenha vazão controlada e observe
a dead letter mesmo ao reemitir com a mesma chave.

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

### 4.1 De onde vem o canal

A solicitação não tem campo de canal, e `channelsHint` não conta como um
(seção 4.2). O canal de cada tentativa nasce da **política de classe**
publicada para o par (`application`, `class`). É um documento JSON que o time
dono da aplicação mantém e publica pela rota administrativa
`/v1/applications/{application}/classes/{class}/policy`, fora do alcance do
produtor. Dois campos dele respondem à pergunta:

| Campo da política | O que faz | O que a publicação exige |
|---|---|---|
| `channelsAllowed` | Conjunto de canais elegíveis para a classe. Restringe, não ordena | Lista não vazia, sem repetição, só com canais do vocabulário |
| `deliveryPlan` | Lista **ordenada** de passos `{ "channel", "timeout" }`. É ela que decide por qual canal a notificação sai primeiro e para qual cai em seguida | Cada passo nomeia um canal de `channelsAllowed`, uma única vez; `timeout` é opcional, no formato `<segundos>s`, entre `1s` e `86400s` |

```json
{
  "schemaVersion": 1,
  "channelsAllowed": ["push", "email", "sms"],
  "deliveryPlan": [
    { "channel": "push", "timeout": "30s" },
    { "channel": "email", "timeout": "120s" },
    { "channel": "sms" }
  ],
  "defaultTtl": "300s",
  "dedupeWindow": "60s"
}
```

Um canal que está em `channelsAllowed` mas não aparece em `deliveryPlan` nunca
é tentado: o plano é derivado da lista ordenada, e o conjunto elegível só
restringe.

**Do plano publicado ao canal da primeira tentativa.** O estágio Policy parte
de `channelsAllowed` e roda cinco regras em ordem fixa. As quatro primeiras
podem retirar canais ou recusar a notificação; a última, `ChannelSelection`,
cruza o que sobrou com o plano, o conteúdo e o cadastro, e devolve o plano que
vai valer:

1. `ConsentGate`: só age quando a política declara `consentPurpose`. Retira os
   canais sem consentimento concedido para essa finalidade; se nenhum sobra,
   recusa com `no-consent`.
2. `SuppressionGate`: retira os canais cujos endereços do destinatário estão
   todos suprimidos; se nenhum sobra, recusa com `channel-suppressed`.
3. `QuietHours`: adia a notificação, sem mexer nos canais. Nunca age em
   `critical` nem em template de finalidade `authentication`.
4. `DedupeWindow`: recusa a repetição com `duplicate-window`, sem mexer nos
   canais.
5. `ChannelSelection`: mantém apenas os passos de `deliveryPlan` cujo canal
   sobreviveu às regras anteriores, tem **conteúdo publicado na versão vigente
   do template** e é **alcançável para o destinatário**, o que significa um
   ponto de contato ativo daquele canal ou, para `push`, ao menos um token de
   dispositivo registrado. Se nenhum passo sobra, recusa com `no-valid-contact`.

O que sobra preserva a ordem de `deliveryPlan` e é o **plano admitido**,
gravado com a notificação. O estágio Route toma o primeiro passo desse plano:
o canal vira o canal da tentativa 1, o ponto de contato escolhido é o daquele
canal (verificado antes de não verificado), e o `timeout` do passo vira o
`fallbackDeadline` da tentativa, que a consulta devolve em
`attempts[].fallbackDeadline`.

**Do primeiro canal aos seguintes.** Quando a tentativa falha no envio ou no
retorno do provedor, ou quando o prazo do passo vence sem veredito, o hub pede
o próximo passo do plano admitido. Três regras valem nesse avanço:

- O plano não é relido da política vigente. Republicar ou reverter a política
  muda notificações futuras, nunca as que já foram admitidas.
- Consentimento e supressão são relidos na hora. Um passo que ficou inelegível
  entre a admissão e o prazo é pulado, e o plano segue para o seguinte.
- Um passo sem `timeout` encerra o plano quando falha, mesmo que existam
  passos depois dele, porque sem prazo não há como cobrar o passo seguinte.
  Quem publica a política dá `timeout` a todo passo que não for o último.

Um canal parado pelo controle de emergência, por aplicação ou por canal, não
faz a notificação trocar de canal: a tentativa fica retida num registro durável
e volta à fila quando um operador reativa o canal. Se o TTL vencer enquanto ela
espera, a notificação expira em vez de sair.

**O que o produtor precisa saber para não se surpreender.**

- O vocabulário de canais é fechado: `email`, `sms`, `push` e `whatsapp`. O
  último existe no vocabulário e no cadastro de contatos, mas não tem adaptador
  hospedado (seção 1).
- `sms` reservado a `critical` é regra de aprovação da política, não de código.
  A validação da política não confere classe contra canal; o que impede `sms`
  numa política `transactional` é o processo de publicação.
- A política não é legível com papel de produtor: a rota de leitura exige
  `Templates.Author`. Pergunte ao time dono da aplicação qual é o
  `deliveryPlan` da sua classe antes de ligar a integração (seção 8).
- O canal usado aparece só no resultado: `attempts[].channel` na consulta,
  `lastChannel` no evento `failed` e `channel` no evento `delivered`. A
  avaliação `ChannelSelection` aparece em `policyEvaluations[]` com resultado
  `filter`, sem a lista de canais, que fica na trilha de auditoria.

### 4.2 Campos aceitos sem efeito

Três campos da solicitação são aceitos e não têm efeito nesta versão. O hub os
valida; `channelsHint` e `locale` não são persistidos, enquanto `scheduledAt` é
armazenado sem dirigir o pipeline. `channelsHint` e `scheduledAt` entram no hash
de idempotência; `locale` não entra, e a seção 3 explica por quê.

**`channelsHint`**: aceito e ignorado. A ordem efetiva é a do plano da
política. O motivo é que o hint não é persistido na aceitação, então a regra de
seleção de canal roda sem ele. Nenhuma reordenação por solicitação existe hoje.
A validação atual limita quantidade, tamanho e itens vazios, mas não confere
nomes contra o catálogo nem recusa duplicatas. Não use essa tolerância para
enviar valores próprios: ela não cria preferência e ainda altera o hash
idempotente.

**`locale`**: opcional, não persistido e fora do hash de idempotência. O locale
de renderização vem do perfil do destinatário ou do padrão do template. Omita o
campo sem receio, e se enviar não espere que ele mude o idioma da mensagem.
Para influenciar o idioma, ajuste a preferência do destinatário pela rota de
contatos administrada pelo sistema de cadastro.

**`scheduledAt`**: aceito, armazenado e sem efeito. A notificação é enfileirada
imediatamente e processada assim que o pipeline a pegar. Além disso, o prazo de
expiração é calculado como instante de aceite mais `ttlSeconds`, sem considerar
o agendamento. O liberador de adiamento passou a existir com a janela de
silêncio, e é ele que devolve ao pipeline o que a política adiou; o que não
existe é quem transforme o seu `scheduledAt` no instante de liberação que esse
liberador lê. *Critério de retorno*: um produtor com necessidade demonstrada de
agendar, que é o que justificaria escrever esse instante a partir da
solicitação.

Consequência direta: **não use o hub para agendar**. Peça a notificação no
instante em que ela deve sair.

### 4.3 O que o SMS faz com o seu texto

Três comportamentos do canal SMS mudam o que chega ao aparelho, e nenhum deles
é configurável por solicitação.

- **O texto é normalizado na renderização.** Acentos vão para a forma composta,
  caracteres de controle são removidos e quebras de linha viram um espaço.
  Motivo: a forma decomposta gasta mais caracteres no ar e muda a codificação
  que a operadora escolhe; caractere de controle invisível dentro de mensagem
  de autenticação é recurso de falsificação, não conteúdo; e quebra de linha é
  reembrulhada pela operadora, o que torna a contagem de segmentos
  imprevisível. O valor que você envia em uma variável passa pela mesma regra,
  então não conte com espaçamento exato nem com quebras de linha em SMS.
- **SMS de autenticação não carrega link.** Um template de finalidade
  `authentication` com link no conteúdo não publica, e uma renderização que
  produza um endereço (inclusive vindo de valor de variável, e inclusive
  encurtado, sem `https://`) é recusada com o motivo `authentication-sms-link`.
  A decisão é assumida: o falso positivo custa um código de autenticação, e o
  falso negativo entrega um vetor de phishing dentro da mensagem que as pessoas
  são treinadas a obedecer na hora.
- **O `ttlSeconds` chega até a operadora.** O tempo que resta de validade viaja
  na chamada ao provedor como prazo máximo de permanência na fila dele, e uma
  notificação cuja validade venceu antes do envio não vira mensagem alguma: o
  hub encerra a tentativa sem gastar SMS. Um `ttlSeconds` curto demais para um
  código de autenticação transforma atraso de fila em notificação perdida, e um
  longo demais entrega um código depois de ele deixar de valer.

## 5. Observabilidade e acompanhamento do resultado

Existem dois caminhos, e eles respondem perguntas diferentes: os eventos de
saída avisam quando algo terminal acontece; a consulta responde sobre uma
notificação específica quando você quiser.

### 5.1 Eventos de saída, no tópico `notifications.events.v1`

Envelope CloudEvents 1.0, chave do registro igual ao `recipientId`, cabeçalho
`eventType` com o tipo do evento para filtrar sem abrir o corpo. Nenhum evento
carrega conteúdo renderizado nem dado de contato.

A publicação usa outbox e entrega **ao menos uma vez**. Uma queda entre publicar
e marcar a linha pode republicar o mesmo envelope. Deduplicate pelo `id` estável
do CloudEvent e não dependa de ordem entre eventos. `correlationId` acompanha os
eventos de rejeição, falha e entrega quando foi informado na solicitação.

| Tipo | Quando é publicado | O que afirma |
|---|---|---|
| `araia.notification.rejected.v1` | A ingestão ou a política recusou | O hub recusou a solicitação, pelo motivo do catálogo canônico |
| `araia.notification.failed.v1` | O plano de entrega se esgotou, o passo seguinte não pôde ser usado, ou a notificação expirou | A entrega não aconteceu |
| `araia.notification.delivered.v1` | O provedor confirmou a entrega, ou um push foi aceito na **última** etapa do plano | Entrega confirmada; em push sem etapa posterior, aceitação pelo provedor |
| `araia.notification.consent_changed.v1` | O ledger de consentimento registrou uma mudança | Estado de consentimento por finalidade e canal |
| `araia.notification.contact_suppressed.v1` | Um provedor recusou o destino de forma definitiva e o hub parou de endereçar o canal | O canal daquele destinatário deixa de ser elegível até remoção manual |

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
    "notificationId": "01931f7c-8a4b-7e2d-9c5f-0a1b2c3d4e5f",
    "lastChannel": "email",
    "reason": "http-400",
    "correlationId": "trace-9b2d7c10"
  }
}
```

Quando um evento carrega `notificationId`, o valor é um UUID. A consulta por ID
aceita apenas a forma pública `ntf_` devolvida pelo REST, e a API não publica uma
conversão entre as duas representações. Preserve o ID público recebido no
aceite. No caminho Kafka, use `correlationId` ou a chave de idempotência para
correlacionar o resultado; não envie o UUID do evento para
`GET /v1/notifications/{id}`.

**Dois vocabulários que nunca se misturam.** O `reason` de `rejected` pertence
ao catálogo fechado da seção 6. O `reason` de `failed` é **vocabulário
aberto**: ele carrega o código que o provedor devolveu, um motivo do próprio
hub como `no-active-device-token` para um alvo inutilizável ou `plan-exhausted`
para um plano sem passo seguinte, ou `expired` quando a notificação venceu,
antes de chegar a um canal ou no meio do plano. Valores novos aparecem sem
mudança de esquema, porque quem os cunha é o provedor. Não valide
`failed.reason` contra o catálogo, e agrupe por família de código em painel e
alarme, nunca por enumeração fechada.

Dois casos em que **nenhum evento é publicado**, e você precisa saber deles:

- Corpo malformado sem `recipientId`. O contrato de saída chaveia cada evento
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

**A consulta pode ler uma réplica.** Quando o ambiente configurar uma conexão
de leitura, logo após o `202` uma consulta pode devolver `404` ou um estado
anterior ao mais recente. Sem conexão de réplica, a implementação usa o banco de
escrita. Clientes devem tolerar consistência eventual porque a projeção do
pipeline continua assíncrona em ambos os casos.

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
`queued`, `sending`, `sent`, `failed`, `unknown`, `delivered`, `read` e
`bounced`. Os três últimos só aparecem com retorno de provedor.

A consulta não devolve conteúdo renderizado em forma alguma, nem as variáveis.
Da tentativa saem apenas os dois hashes do conteúdo. Em canal de contato o alvo
sai com o valor **mascarado**; em push sai a plataforma e o identificador do
registro de dispositivo, nunca o token.

A resposta também não devolve o manifesto aceito de anexos. Preserve o payload
original e a chave de idempotência no seu domínio se precisar reconstruir a
solicitação.

### 5.3 O que esta versão não sabe

Esta é a parte que mais gera engano, então ela está escrita sem rodeio.

**O hub já coleta retorno de provedor, e a consulta ainda não o mostra.** O
retorno chega por webhook, é guardado como evidência e move a tentativa. O que
a consulta devolve continua sendo o estado agregado: a resposta **não declara**
membro de evento de entrega, e ele não vem como lista vazia, porque lista vazia
afirmaria que não houve evento. Quando o provedor não chama de volta, um job
diário consulta o provedor sobre tentativa parada há mais de seis horas e aplica
a resposta pelo mesmo caminho do webhook. Onde o provedor não oferece consulta
posterior, como no push, a lacuna permanece por limitação da plataforma.

**`sent` afirma aceitação pelo provedor, nunca entrega.** Os campos `sentAt` e
`providerMessageId` de uma tentativa dizem que o provedor assumiu
responsabilidade pela mensagem. Não dizem que ela chegou ao aparelho, à caixa
de entrada nem aos olhos do cliente.

**`delivered` afirma entrega confirmada, e mudou de significado.** Uma
notificação alcança `delivered` quando o provedor confirma a entrega da
tentativa. Em push existe uma exceção declarada: o provedor de push não confirma
nada depois de aceitar, então a aceitação vale como entrega **somente quando a
tentativa é a última etapa do plano**, onde nenhuma etapa posterior poderia
socorrê-la.

**A mudança que quebra suposição antiga.** Um push aceito numa etapa que **tem**
etapa posterior não produz mais `delivered` e não publica mais
`araia.notification.delivered.v1` no instante da aceitação. A notificação
permanece em `dispatched` e a tentativa fica em `sent`, à espera de confirmação
real ou da conclusão do plano. Se o seu consumidor usava esse evento como
"o push saiu do hub", troque a leitura: o que afirma que o hub entregou ao
provedor é a tentativa em `sent`, na consulta, e o que afirma entrega é o
evento. Um e-mail continua **não** produzindo `delivered` por aceitação; ele só
alcança `delivered` com confirmação do provedor.

**Tentativa em `unknown` é indeterminada, e pode ser resolvida depois.** Quando
o provedor responde com timeout ou erro de servidor, sem veredito conclusivo, a
tentativa fica em `unknown`. Três coisas podem tirá-la de lá: um retorno tardio
do provedor por webhook, a reconciliação diária, ou, em fluxo `critical` e de
autenticação, o fallback imediato que o hub dispara depois de sessenta segundos
sem veredito, preferindo o risco raro de duplicata ao risco de código de uso
único perdido. Enquanto `unknown` durar, trate como indeterminado: não é falha e
não é sucesso.

**Não existe stream de mudanças de status.** Não há assinatura por evento
enviado pelo servidor. O que existe é o tópico de saída e a consulta.

**Não existe cancelamento.** A API não publica rota de cancelamento e o domínio
não possui estado `cancelled` ou `canceled`. Depois do `202`, revogar uma
referência de anexo também não cancela a notificação.

### 5.4 Supressão de contato

Quando um provedor recusa um destino de forma definitiva, o hub para de
endereçar aquele contato. A regra é por canal e não é a mesma em todos eles:
e-mail suprime na primeira recusa definitiva, porque uma caixa que o provedor
declara inexistente não volta a existir e cada mensagem seguinte gasta
reputação de envio; os demais canais exigem duas recusas dentro de sete dias,
porque um número pode ser recusado por condição temporária e retirar um canal
alcançável custa mais ao destinatário do que a mensagem extra.

O que o produtor observa:

- `araia.notification.contact_suppressed.v1` no tópico de saída, com
  `recipientId`, `channel` e `reason`, gerado uma vez por decisão lógica. A
  entrega física pode repetir pelo contrato ao menos uma vez. Ele é o aviso de
  que aquele canal daquele destinatário parou de funcionar, e serve para o
  domínio pedir um contato novo pelo caminho de cadastro.
- `araia.notification.rejected.v1` com motivo `channel-suppressed` na próxima
  solicitação cujos canais elegíveis estejam todos suprimidos.

A supressão é reversível, e a reversão é ato humano registrado: um operador com
o papel próprio remove a supressão com justificativa, e a trilha guarda quem
removeu. Não há reversão automática, e não há como um produtor pedir uma.

### 5.5 Correlação e sinais operacionais

Use três identificadores com finalidades diferentes:

| Identificador | Responsável | Uso |
|---|---|---|
| `Idempotency-Key` ou `data.idempotencyKey` | Produtor | Identidade estável do fato de negócio e recuperação de replay |
| `correlationId` | Produtor | Agrupamento de notificações de uma mesma transação na consulta e nos eventos |
| `notificationId` | Hub | Consulta de uma aceitação específica |

No Kafka, conserve também tópico, partição, offset e `event.id`. A trilha do hub
registra essas coordenadas, e a dead letter as devolve nos headers. O serviço
emite logs estruturados de aceite, replay, dead letter, pausa e retomada, mas não
publica uma métrica própria de consumer lag. O time produtor deve monitorar:

- taxa de respostas REST por status e `type`;
- ausência prolongada de resultado para uma chave aceita;
- volume e idade da dead letter, deduplicados pelas coordenadas de origem;
- duplicatas e atraso no consumo de `notifications.events.v1`.

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
vocabulário fechado. Algumas condições de protocolo e de capacidade ficam fora
dele; a matriz no fim desta seção mostra como cada transporte as expõe.

| Motivo | O que significa | O que o produtor faz |
|---|---|---|
| `template-not-found` | A aplicação não tem template publicado com essa chave | Confira `application` e `templateKey`. Template criado mas nunca publicado também cai aqui |
| `template-deprecated` | O template não aceita mais solicitações novas | Migre para a chave sucessora com o time dono do template |
| `template-disabled` | O template foi desligado | Pare de solicitar e procure o time dono do template |
| `template-class-mismatch` | O template pertence a outra classe | Corrija a `class` da solicitação para a classe do template |
| `template-variables-invalid` | As variáveis não passam no esquema publicado | Corrija o payload usando os `checks` da resposta. Não retente o mesmo corpo |
| `template-render-failed` | A renderização do conteúdo publicado falhou | Não é corrigível pelo produtor. Acione o time dono do template |
| `authentication-sms-link` | O SMS renderizado de um template de autenticação contém um link | Corrija o valor da variável que produziu o endereço. Um código de autenticação por SMS não carrega link nesta plataforma, e a recusa vale também quando o link chega por valor de variável |
| `layout-disabled` | O layout que a versão publicada fixa está desativado, então a mensagem não tem moldura aprovada | Não é corrigível pelo produtor. Acione o time dono do template, que precisa republicar a versão apontando para um layout ativo |
| `rendered-content-too-large` | A mensagem renderizada ultrapassa o limite do canal. Em SMS o limite é contado em segmentos, e o mesmo texto custa mais que o dobro de segmentos quando carrega acento, porque a operadora troca a codificação | Reduza o valor da variável que fez o texto crescer. Renderize a versão pelo preview com as mesmas variáveis para conferir antes de solicitar |
| `attachments-not-carried-by-channel` | A notificação foi aceita com anexos e o canal que o plano usa não transporta anexos na chamada ao provedor. A notificação termina aí: nenhum anexo é removido, nada vira link e nenhum outro canal é tentado, porque transportar o conjunto é propriedade da mensagem e não do destinatário | Não retente igual. Solicite sem o membro `attachments`, ou trate com o time do hub um plano de entrega cujo canal transporte anexos |
| `producer-not-authorized` | A identidade do produtor está fora do registro, ou pede classe que o registro não concede | Peça o registro ou o ajuste de concessão. Só ocorre no caminho Kafka |
| `class-not-allowed-for-principal` | O token não carrega o papel da classe pedida, ou não carrega identidade estável | Peça a atribuição do papel ao seu principal. Só ocorre no caminho REST |
| `sensitive-variables-on-bus` | O template declara variáveis sensíveis e a solicitação veio pelo barramento | Migre a solicitação desse template para REST |
| `no-valid-contact` | Nenhum canal sobreviveu ao cruzamento entre plano da política, canais com conteúdo publicado e canais em que o destinatário é alcançável | Não retente igual. Verifique o cadastro do destinatário; se ele estiver correto, a causa é conteúdo publicado faltando para o canal, e é assunto do time do template |
| `no-consent` | O destinatário não consentiu com a finalidade em nenhum canal elegível | Não retente. Colete o consentimento pelo caminho de cadastro |
| `channel-suppressed` | Todos os canais elegíveis estão suprimidos: o provedor recusou o destino de forma definitiva e o hub parou de endereçá-lo | Não retente. O destino não vai passar a funcionar por insistência, e insistir gasta reputação de envio de todos os outros destinatários. A saída é o destinatário declarar um contato novo ou um operador reverter a supressão com justificativa |
| `recipient-rate-limited` | O orçamento por destinatário daquela classe se esgotou | Não retente em laço. Respeite o intervalo e reavalie se o volume por cliente está correto |
| `duplicate-window` | Uma notificação equivalente está dentro da janela de deduplicação da política | Provavelmente é duplicata legítima detectada. Se não for, revise a chave de negócio que está gerando repetição |
| `payload-invalid` | O corpo é estruturalmente inválido, ou falha nas regras de forma | Corrija o corpo usando o dicionário `errors` da resposta. É o `type` do `400` no REST e, no Kafka, o motivo da dead letter para envelope ilegível ou sem `idempotencyKey` |
| `event-type-unsupported` | O `type` do envelope não é o que este tópico consome | Publique com `araia.notification.requested.v1`. Só ocorre no caminho Kafka. Não confunda com `payload-invalid`: aqui o corpo pode estar perfeito e a versão do envelope é que está errada |
| `idempotency-key-conflict` | A mesma chave chegou com corpo diferente | Escolha: se o corpo novo é o correto, use uma chave nova; se o antigo é o correto, pare de reenviar |
| `expired` | O TTL venceu antes de a notificação alcançar um canal | Solicite de novo se o fato de negócio ainda vale. Reavalie se o `ttlSeconds` é curto demais |
| `producer-disabled` | O controle de emergência bloqueou o produtor | Pare de publicar e acione o time do hub. Não faça retry automático enquanto o bloqueio permanecer. No REST retorna `403`; no Kafka gera rejeição e dead letter |

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

Alguns motivos do catálogo **não** aparecem como status HTTP, porque são
decididos depois do aceite, no pipeline: `no-valid-contact`, `no-consent`,
`channel-suppressed`, `duplicate-window`, `authentication-sms-link`,
`layout-disabled`, `rendered-content-too-large`,
`attachments-not-carried-by-channel`, além de `template-render-failed`. Eles chegam pelo evento `rejected` e pela consulta.
`expired` segue outro caminho: aparece como `reason` do evento
`araia.notification.failed.v1` e como estado `expired` na consulta.

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

### 6.1 Condições fora do catálogo canônico

Estas condições não podem aparecer como `reason` de
`araia.notification.rejected.v1`:

| Condição | REST | Kafka | Ação do produtor |
|---|---|---|---|
| `idempotency-key-required` | `400` | Não existe; ausência em `data` vira `payload-invalid` na dead letter | Corrija ou gere a chave determinística antes de reenviar |
| `principal-rate-limited` | `429` com `Retry-After` | A dimensão é observada, sem rejeição | Reduza a vazão e, no REST, retente após o intervalo |
| `kill-switch-unavailable` | `503` | Pausa transitória, sem dead letter | REST: retente com recuo exponencial e jitter. Kafka: não republique em laço; acompanhe a recuperação do hub |
| `attachments-not-claimable` | `422` | Dead letter com o mesmo motivo | Não retente o mesmo conjunto. Enquanto anexos não forem liberados, omita o membro |
| `attachment-capability-not-enabled` | `422` | Dead letter com o mesmo motivo | Espere a confirmação da habilitação. A mesma solicitação é aceita depois, sem alterar o corpo |

As duas últimas linhas são o fluxo de anexos, e a diferença entre elas importa
porque as ações são opostas: uma diz que este conjunto não pode ser vinculado e
repetir não muda nada, a outra diz que a implantação não aceita anexos novos e
nada foi apontado no conjunto. A seção 2.4 explica qual delas você recebe
quando as duas condições valem ao mesmo tempo.

`attachments-not-claimable` é deliberadamente genérico: não revela qual
referência falhou nem se ela era inexistente, estrangeira, não liberada ou
revogada. Essa propriedade continua valendo, e a palavra de capacidade não a
enfraquece, porque capacidade desligada é fato sobre a implantação, igual para
todo chamador e para toda referência. Nenhuma das duas produz evento de
rejeição. Use a resposta REST ou as coordenadas da dead letter para o
diagnóstico com o time do hub.

O mesmo código `attachment-capability-not-enabled` responde, com `409`, no
registro de um anexo pela rota `/v1/attachments`. É a mesma palavra de
propósito: o fato é o mesmo nas duas superfícies, e o status difere porque as
superfícies são diferentes.

## 7. Dead letter

Só o caminho Kafka tem dead letter. No REST a recusa é a própria resposta.

### 7.1 Dead letter de notificações: `notifications.requested.dlt`

Vai para lá qualquer registro **permanentemente** inválido: envelope ilegível,
`type` de envelope não suportado, evento sem `idempotencyKey`, produtor não
autorizado ou bloqueado, recusa do catálogo, conflito de idempotência, estouro
do orçamento por destinatário, manifesto de anexos não vinculável, manifesto
publicado para uma implantação que não aceita anexos novos e recusa por
variável sensível. Falha transitória não vai para a dead letter; a implementação
tenta pausar a partição, com a limitação de reposicionamento descrita na seção
2.2.

Cabeçalhos de diagnóstico do registro:

| Cabeçalho | Conteúdo |
|---|---|
| `reason` | Motivo da recusa; normalmente canônico, com exceções de transporte como `attachments-not-claimable` e `attachment-capability-not-enabled` |
| `sourceTopic` | Tópico de origem |
| `sourcePartition` | Partição de origem |
| `sourceOffset` | Offset de origem |
| `occurredAt` | Instante em que a recusa foi registrada |
| `redacted` | `true` quando o corpo publicado não é cópia fiel do original |
| `producer` | Nome lógico do produtor, quando conhecido |
| `application` | Aplicação declarada, somente quando a política de redação permite |
| `class` | Classe declarada, somente quando a política de redação permite |
| `idempotencyKey` | Chave declarada, somente quando a política de redação permite |
| `traceparent` | Contexto de rastreio, somente quando a política de redação permite |

As coordenadas de origem são o que transforma "o produtor diz que não pediu"
numa afirmação conferível: elas apontam para o registro exato que o broker
ainda guarda, se ele estiver dentro da retenção configurada no ambiente.

**A dead letter não é sempre uma cópia do evento.** A política depende de quanto
o hub já pôde confiar no produtor e no payload:

| Motivos | Corpo na dead letter | Headers de contexto |
|---|---|---|
| `payload-invalid`, `event-type-unsupported`, `producer-disabled`, `producer-not-authorized` | Resumo reconstruído por lista de permissão, sem o corpo original | Omite `application`, `class`, `idempotencyKey` e `traceparent`; usa o produtor lógico como key |
| `sensitive-variables-on-bus` | Preserva o envelope, mas troca `data.variables` pelos nomes declarados como sensíveis | Mantém o contexto permitido e marca `redacted=true` |
| Demais recusas após confiança, inclusive as duas do fluxo de anexos | Preserva o corpo original | Marca `redacted=false` |

Para `sensitive-variables-on-bus`, valores nunca viajam. O cabeçalho `redacted`
vem `true` para que ninguém confunda o registro com cópia fiel.

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

A razão é impedir que uma recusa copie material ainda não confiável ou sensível
para outro tópico. Corpo que não puder ser interpretado perde a seção `data`
inteira: na dúvida sobre onde estão os valores, nada vai.

A DLT também é entregue **ao menos uma vez**. Uma falha entre a publicação e a
gravação da marca de transporte pode duplicar o registro. Deduplicate pela
tupla `(sourceTopic, sourcePartition, sourceOffset)`. Só considere redrive
quando `redacted=false`; um resumo ou corpo parcialmente redigido não recompõe
o evento original. Confirme a retenção real da entrada e da DLT com a Plataforma.

### 7.2 Dead letter de contatos: `contacts.events.dlt`

Este par de tópicos pertence ao time do cadastro, não ao produtor de
notificações, mas a regra vale a pena conhecer porque ela é oposta à anterior.

**Segunda regra: a dead letter de contatos não tem reprocessamento.** Ali a
redação é incondicional, e o corpo publicado nunca é o original. Cada registro
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
sai do motivo, das coordenadas e do identificador do CloudEvent. O corpo original
só permanece alcançável enquanto estiver dentro da retenção configurada para o
tópico de entrada.

Motivos próprios dessa ingestão, que não se misturam com o catálogo de
notificações: `source-not-authorized`, `payload-invalid`,
`event-type-unsupported`, `recipient-unknown` e `no-contact-point-for-channel`.
Dois deles se escrevem igual em ambos os vocabulários, `payload-invalid` e
`event-type-unsupported`, e significam a mesma coisa em cada transporte, corpo
inválido e tipo de envelope não consumido. Ainda assim são vocabulários
separados: não valide um motivo de contato contra o catálogo de notificações
nem o contrário, porque os dois conjuntos evoluem por decisões diferentes.

## 8. Validação da integração e checklist

Antes da primeira notificação, providencie:

**Identidade e papéis**

- Um principal de client credentials para o seu serviço.
- O papel de envio da classe que você vai pedir: `Notifications.Send.Critical`,
  `Notifications.Send.Transactional` ou `Notifications.Send.Operational`. O
  papel é por classe, então um serviço que pede mais de uma precisa de cada
  concessão correspondente.
- O token precisa carregar uma identidade estável (`appid`, `oid`, `sub` ou
  `NameIdentifier` mapeado). Sem ela a ingestão responde `403`, porque não há o
  que gravar como solicitante na trilha.
- Se o seu time também vai **ler** notificações, peça `Notifications.Read`
  separadamente. Os papéis de envio não dão leitura, e hoje quem porta a
  leitura enxerga notificação de qualquer aplicação, então trate a concessão
  como decisão de segurança.
- O papel `Notifications.Audit` é de Compliance e Auditoria Interna. Ele abre
  conteúdo renderizado na forma mascarada e trilha completa, cada chamada
  gravando um registro de divulgação. Produtor não recebe esse papel.

**Registro de produtor, apenas no caminho Kafka**

- ACL de escrita no tópico exclusivo atribuído ao seu principal do broker.
- Binding daquele tópico para um único produtor lógico no worker.
- Registro da tripla produtor lógico, `application` e classes
  permitidas. Sem ele, qualquer evento seu vai para a dead letter com
  `producer-not-authorized`.
- Não envie header `producer`. O tópico consumido é a única autoridade para o
  nome lógico usado pelo hub.
- Confirme com a Plataforma a retenção, as partições e as ACLs do ambiente.

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
- Peça ao time dono da aplicação o `deliveryPlan` publicado para a sua classe,
  com o `timeout` de cada passo. A rota de leitura da política exige
  `Templates.Author`, que não é papel de produtor, e sem essa informação você
  não sabe por qual canal a notificação sai nem quanto tempo ela espera antes
  de cair para o seguinte (seção 4.1).

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
- Reentregue um evento de saída com o mesmo `id` e confirme que seu consumidor
  não repete o efeito.
- No Kafka, force um payload inválido, localize a dead letter pelas coordenadas
  de origem e confirme que seu diagnóstico tolera corpo redigido.

**Anexos**

- Não inclua `attachments` no teste de habilitação enquanto o time do hub não
  confirmar a habilitação de ponta a ponta no ambiente. Numa implantação que
  não aceita anexos novos, a solicitação responde
  `422 attachment-capability-not-enabled` e nenhuma notificação é criada, então
  o passo não testa nada além da própria recusa.
- Um token que envia notificações apenas com `appid` não é suficiente para a
  autorização atual das APIs de anexos, que resolve `oid`, `sub` ou
  `NameIdentifier` e exige concessão exata por aplicação.
- O ambiente local iniciado pelo compose não provisiona Kafka nem configura o
  armazenamento de objetos do módulo de anexos. Sem armazenamento configurado,
  o envio de conteúdo responde `503 attachment-store-unavailable`. Use-o para
  exercitar o caminho REST sem anexos.

## 9. Limites e comportamento sob pressão

Dois níveis de limite, com comportamentos diferentes.

**Teto bruto por principal, na borda HTTP.** Janela fixa de um minuto, contada
por principal autenticado, ou por endereço de origem quando não há principal.
Ingestão: 2.000 requisições por minuto. Consulta: 120 por minuto. Escrita de
contatos: 600 por minuto. O estouro devolve `429` com o corpo de erro padrão do
framework, sem `type` do catálogo e **sem `Retry-After`**. É um freio contra
abuso automatizado, não o limite de negócio: quando ele dispara, o hub não diz
quanto esperar, então o cliente precisa de recuo próprio.

O documento OpenAPI tem teto próprio de 60 requisições por minuto. A superfície
de gestão de anexos possui teto de 1.000 por minuto, contado por principal
autenticado ou por endereço de origem quando não há principal. Ele vale mesmo
numa implantação que não aceita anexos novos, porque é um freio de borda e não
uma consequência da capacidade estar ligada.

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

O controle de emergência segue a postura oposta. Se o produtor estiver
bloqueado, o REST responde `403 producer-disabled`; se a autoridade do controle
estiver indisponível, o REST responde `503 kill-switch-unavailable` e o Kafka
pausa o consumo sem publicar dead letter.

## 10. Compatibilidade, versionamento e descontinuação

As versões aparecem na rota HTTP (`/v1`) e no tipo CloudEvents
(`araia.notification.requested.v1` e eventos de saída `.v1`). A implementação
aceita apenas o tipo de entrada V1 exato.

Uma adição opcional pode permanecer em V1 quando a ausência preserva o
comportamento anterior. `attachments` seguiu essa regra: ausência e `null`
continuam significando uma solicitação sem anexos. Depois de publicado, remover
o membro ou alterar esse significado passa a ser mudança incompatível.

Mudanças incompatíveis exigem nova versão. O runtime atual não comprova
coexistência automática de versões, e não existe janela fixa de depreciação no
contrato. Portanto, não assuma que V1 e V2 serão consumidas em paralelo. Uma
transição precisa publicar previamente o novo esquema, a estratégia de rollout,
o período de convivência e o critério de retirada.

Para reduzir acoplamento, clientes REST e consumidores Kafka devem ignorar
propriedades adicionais que não usam, preservar a versão que produzem e testar
seus serializadores contra os exemplos e o OpenAPI. O repositório ainda não
oferece um schema de máquina para Kafka; trate qualquer mudança no envelope,
nos campos obrigatórios, na key, nos headers ou nos motivos como revisão de
contrato.

## 11. Referências e evidências

As referências abaixo sustentam as regras que mais afetam uma integração. Os
links apontam para o arquivo, e a coluna de evidência registra as linhas
verificadas nesta revisão.

| Tema | Evidência |
|---|---|
| OpenAPI autenticado e rate limit | [`src/Platform.Api/Program.cs`](../src/Platform.Api/Program.cs), `src/Platform.Api/Program.cs:87`; [`OpenApiRateLimitingSetup.cs`](../src/Platform.Api/Infrastructure/RateLimiting/OpenApiRateLimitingSetup.cs), `src/Platform.Api/Infrastructure/RateLimiting/OpenApiRateLimitingSetup.cs:26` |
| Rota REST, autorização e respostas | [`RequestNotification.Endpoint.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs:21` |
| Esquema e validação da solicitação | [`RequestNotification.Command.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs:7`; [`RequestNotification.Validator.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs:33` |
| Ordem de admissão, kill switch e aceite | [`RequestNotification.Admission.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Admission.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Admission.cs:84`; [`RequestNotification.Handler.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Handler.cs:79` |
| Idempotência e manifesto de anexos | [`RequestNotification.PayloadHash.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs:12`; [`ADR-0021`](ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md), `docs/ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md:93` |
| As duas chaves da capacidade de anexos | [`appsettings.json`](../src/Platform.Api/appsettings.json), `src/Platform.Api/appsettings.json:72`, `src/Platform.Api/appsettings.json:85`; [`AttachmentCapability.cs`](../src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Capability/AttachmentCapability.cs), `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Capability/AttachmentCapability.cs:30`; [`AdmittedTypeContentPolicy.cs`](../src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Validation/AdmittedTypeContentPolicy.cs), `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Validation/AdmittedTypeContentPolicy.cs:58` |
| Recusa por capacidade desligada, nas duas superfícies | [`RegisterAttachment.Handler.cs`](../src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/RegisterAttachment/RegisterAttachment.Handler.cs), `src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/RegisterAttachment/RegisterAttachment.Handler.cs:30`; [`TransactionalAttachmentClaim.cs`](../src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Persistence/TransactionalAttachmentClaim.cs), `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Persistence/TransactionalAttachmentClaim.cs:161`; [`IngestionProblems.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Http/IngestionProblems.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Http/IngestionProblems.cs:55` |
| Recusas das rotas de anexos | [`ApiResults.cs`](../src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Http/ApiResults.cs), `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Http/ApiResults.cs:11` |
| Política de classe, seleção de canal e plano admitido | [`ClassPolicyValidation.cs`](../src/Platform.Api/Modules/TemplateManagement/Domain/ClassPolicyValidation.cs), `src/Platform.Api/Modules/TemplateManagement/Domain/ClassPolicyValidation.cs:277`; [`PolicyStage.cs`](../src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/PolicyStage.cs), `src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/PolicyStage.cs:38`; [`CoreWorkerRole.cs`](../src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs), `src/Platform.Api/Modules/Notifications/CoreWorkerRole.cs:102`; [`ChannelSelectionRule.cs`](../src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/ChannelSelectionRule.cs), `src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/ChannelSelectionRule.cs:41`; [`AdmittedDeliveryPlan.cs`](../src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs), `src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs:29`; [`RouteStage.cs`](../src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/RouteStage.cs), `src/Platform.Api/Modules/Notifications/Features/Pipeline/Stages/RouteStage.cs:25` |
| Fallback, avanço do plano e evento de fim | [`NotificationAttempt.cs`](../src/Platform.Api/Modules/Notifications/Domain/NotificationAttempt.cs), `src/Platform.Api/Modules/Notifications/Domain/NotificationAttempt.cs:185`; [`NotificationPlanOutcome.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/NotificationPlanOutcome.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/NotificationPlanOutcome.cs:117`; [`FallbackRequestHandler.cs`](../src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs), `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs:305`; [`FallbackRequestHandler.cs`](../src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs), `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs:526`; [`DispatchMessageProcessor.cs`](../src/Platform.Api/Modules/Notifications/Features/Dispatching/DispatchMessageProcessor.cs), `src/Platform.Api/Modules/Notifications/Features/Dispatching/DispatchMessageProcessor.cs:331` |
| Leitura da política e canais hospedados | [`GetClassPolicy.Endpoint.cs`](../src/Platform.Api/Modules/TemplateManagement/Features/ClassPolicies/GetClassPolicy/GetClassPolicy.Endpoint.cs), `src/Platform.Api/Modules/TemplateManagement/Features/ClassPolicies/GetClassPolicy/GetClassPolicy.Endpoint.cs:13`; [`DispatcherWorkerRole.cs`](../src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs), `src/Platform.Api/Modules/Notifications/DispatcherWorkerRole.cs:57` |
| Revalidação de anexos antes do envio | [`AttachmentPreflight.cs`](../src/Platform.Api/Modules/Notifications/Features/Dispatching/AttachmentPreflight.cs), `src/Platform.Api/Modules/Notifications/Features/Dispatching/AttachmentPreflight.cs:72`; [`DispatchMessageProcessor.cs`](../src/Platform.Api/Modules/Notifications/Features/Dispatching/DispatchMessageProcessor.cs), `src/Platform.Api/Modules/Notifications/Features/Dispatching/DispatchMessageProcessor.cs:268`; [`AcceptedSetEnvelopeCheck.cs`](../src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Capacity/AcceptedSetEnvelopeCheck.cs), `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Capacity/AcceptedSetEnvelopeCheck.cs:21` |
| Identidade por tópico e bindings Kafka | [`KafkaIngressTopicMap.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/KafkaIngressTopicMap.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/KafkaIngressTopicMap.cs:90`; [`Platform.Worker/appsettings.json`](../src/Platform.Worker/appsettings.json), `src/Platform.Worker/appsettings.json:76` |
| Binder, desfechos e settlement Kafka | [`KafkaIngressProcessor.cs`](../src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs), `src/Platform.Api/Modules/Notifications/Features/Ingress/KafkaIngressProcessor.cs:49` |
| Retry, pausa e commit Kafka | [`KafkaConsumerService.cs`](../src/Platform.Api/Infrastructure/Messaging/Consuming/KafkaConsumerService.cs), `src/Platform.Api/Infrastructure/Messaging/Consuming/KafkaConsumerService.cs:122` |
| Redação e diagnóstico da dead letter | [`IngressDeadLetterWriter.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/IngressDeadLetterWriter.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Consuming/IngressDeadLetterWriter.cs:60` |
| Catálogo canônico e problemas HTTP | [`NotificationRejectionReasons.cs`](../src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs), `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationRejectionReasons.cs:14`; [`IngestionProblems.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Http/IngestionProblems.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Http/IngestionProblems.cs:13` |
| Consultas e paginação | [`NotificationQueryContract.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Http/NotificationQueryContract.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Http/NotificationQueryContract.cs:9`; [`NotificationsEfOptions.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/NotificationsEfOptions.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/NotificationsEfOptions.cs:18` |
| Entrega ao menos uma vez dos eventos | [`IOutboxPendingStore.cs`](../src/Platform.Api/Infrastructure/Messaging/Relay/IOutboxPendingStore.cs), `src/Platform.Api/Infrastructure/Messaging/Relay/IOutboxPendingStore.cs:17`; [`CloudEventOutbox.cs`](../src/Platform.Api/Infrastructure/Messaging/CloudEventOutbox.cs), `src/Platform.Api/Infrastructure/Messaging/CloudEventOutbox.cs:48` |
| Formato de IDs e evento de falha | [`NotificationId.cs`](../src/Platform.Api/Modules/Notifications/Domain/NotificationId.cs), `src/Platform.Api/Modules/Notifications/Domain/NotificationId.cs:22`; [`NotificationEvents.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Events/NotificationEvents.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Events/NotificationEvents.cs:36` |
| Normalização e validade de SMS | [`SmsContentNormalizer.cs`](../src/Platform.Api/Modules/TemplateManagement/Domain/SmsContentNormalizer.cs), `src/Platform.Api/Modules/TemplateManagement/Domain/SmsContentNormalizer.cs:9`; [`DispatchSmsValidityTests.cs`](../tests/Platform.IntegrationTests/Dispatching/DispatchSmsValidityTests.cs), `tests/Platform.IntegrationTests/Dispatching/DispatchSmsValidityTests.cs:20` |
| Feedback de provedor e reconciliação | [`DeliveryReconciliationOptions.cs`](../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Reconciliation/DeliveryReconciliationOptions.cs), `src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Reconciliation/DeliveryReconciliationOptions.cs:14`; [`GetNotification.Response.cs`](../src/Platform.Api/Modules/Notifications/Features/History/GetNotification/GetNotification.Response.cs), `src/Platform.Api/Modules/Notifications/Features/History/GetNotification/GetNotification.Response.cs:7` |
| Redação da dead letter de contatos | [`ContactIngestionDeadLetterWriter.cs`](../src/Platform.Api/Modules/ContactConsent/Infrastructure/Consuming/ContactIngestionDeadLetterWriter.cs), `src/Platform.Api/Modules/ContactConsent/Infrastructure/Consuming/ContactIngestionDeadLetterWriter.cs:44`; [`ContactIngestionRejectionReasons.cs`](../src/Platform.Api/Modules/ContactConsent/Integration/V1/ContactIngestionRejectionReasons.cs), `src/Platform.Api/Modules/ContactConsent/Integration/V1/ContactIngestionRejectionReasons.cs:12` |
| Limites e degradação dos controles Redis | [`appsettings.json`](../src/Platform.Api/appsettings.json), `src/Platform.Api/appsettings.json:56`; [`IngestionRateLimiter.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/RateLimiting/IngestionRateLimiter.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/RateLimiting/IngestionRateLimiter.cs:59`; [`IdempotencyFastPath.cs`](../src/Platform.Api/Modules/Notifications/Infrastructure/Idempotency/IdempotencyFastPath.cs), `src/Platform.Api/Modules/Notifications/Infrastructure/Idempotency/IdempotencyFastPath.cs:24` |
| Bootstrap local | [`README.md`](../README.md), `README.md:22` |
