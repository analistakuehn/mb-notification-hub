---
language: pt-BR
---

# Fase 2: Resiliência e SMS

**Tipo**: design técnico (technical-design)
**Status**: ACCEPTED, em implementação
**Audiência**: engenharia do Notification Hub, Compliance e Produto (participantes do rito mensal, §9.7 do design de sistema)
**Propósito**: fixar o desenho técnico das entregas da fase 2 do roadmap do Notification Hub, com fronteiras, dependências e critérios de saída
**Fontes**: [Design de Sistema](../notification-hub-system-design.md) (§2.1, §2.3, §3, §4.2, §4.3, §5.1, §5.2, §7.3, §8, §9.3, §9.5, §9.6, §9.7, §10.2, §10.3, §11.1, §11.2, §11.3, §11.6, §11.7, §15, §16); [ADR-0001](../ADR-0001-canal-e-provedor-como-plugin.md); [ADR-0008](../ADR-0008-at-least-once-com-idempotencia.md); [ADR-0011](../ADR-0011-politica-como-configuracao-de-classe.md); [ADR-0014](../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md); [ADR-0015](../ADR-0015-regra-de-supressao-no-estagio-policy.md); contratos publicados em [`ClassPolicyDefinition.cs`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs); decisão de fronteira registrada pelo arquiteto no fechamento da fase 1b (mapa de módulos)

Convenção de evidência: referências a `§` apontam para o design de sistema; as ADRs citadas estão em status Proposta e este design as segue sem desvio; fatos observados na base de código citam `arquivo:linha`; a decisão de fronteira da 1b é decisão do arquiteto responsável, registrada no contexto do fechamento daquela fase.

## Objetivo e contexto

A fase 1b entrega a fundação: ingestão REST e Kafka, outbox, Core pipeline, Contact & Consent v1, auditoria com hash chain, canais e-mail (SendGrid) e push (FCM), classes `critical` e `transactional` (§15). A fase 2 fecha o ciclo de resiliência da entrega: adiciona o canal SMS (Twilio), executa de ponta a ponta o fallback declarativo entre canais, materializa o Delivery Tracker com webhooks assinados, o scheduler DB-backed, a supressão automática reversível e a reconciliação por canal, ativa a classe `operational` com janela de silêncio e produz o primeiro relatório mensal de evidências para Compliance (§15, linha da fase 2).

O objetivo mensurável é o critério de saída do roadmap: 100 % das notificações `critical` com fallback, zero envio após o TTL e o primeiro relatório mensal entregue a Compliance (§15).

Duração: 4 a 6 semanas, conforme o roadmap (§15). Este documento fixa o desenho técnico e as fronteiras das entregas; a decomposição fina em fatias de implementação acontece no kickoff da fase, deliberadamente fora deste documento.

## Escopo por entrega

### Adapter SMS (Twilio) atrás de `IChannelProvider`

- Implementação de `IChannelProvider` para SMS sobre Twilio Messaging (§4.3): Messaging Service por `application`, com sender pool de número longo ou short code BR; `StatusCallback` por mensagem apontando para `/webhooks/twilio`; `ValidityPeriod` igual ao TTL restante da notificação.
- O produto Twilio é decisão de configuração e os dois produtos carregam contratos de entrega diferentes. Só o Programmable Messaging aceita Messaging Service, `StatusCallback` por mensagem e `ValidityPeriod`; o Verify envia o código e não reporta nada de volta, o que deixa o Delivery Tracker sem evento algum para encerrar a notificação. `ProgrammableMessaging` é o valor entregue por padrão. O `MessagingServiceSid` e a `StatusCallbackUrl` continuam vazios na configuração base e são itens obrigatórios da unidade de infraestrutura I2, ao lado do sender BR: sem eles o envio funciona e a confirmação nunca chega. O ambiente que precisa de retorno de entrega liga `RequireDeliveryFeedback`, e com essa chave o host recusa subir com produto Verify, com callback vazio ou sem Messaging Service. A chave é desligada por padrão porque uma estação local com um único número verificado e sem endereço público é configuração legítima, e um guarda que reprova essa configuração é o primeiro que alguém desliga.
- A hierarquia `RenderedMessage` já discrimina `SmsMessage(body)` por canal (§4.3); o adapter consome essa forma, sem contrato novo.
- SMS é reservado à classe `critical`; liberar para outras classes exige mudança de política aprovada (§3).
- Dispatcher SMS com filas próprias por classe e finalidade (`dispatch-sms-critical`, `dispatch-sms-auth`), preservando o bulkhead de filas por classe e canal (§3, §4.2, §5.1).
- Encoding específico do canal na renderização: remoção de caracteres de controle e de quebras de linha, normalização NFC (§10.2 A2); SMS de OTP nunca contém link (§2.3).
- Circuit breaker Polly por provedor, com a forma completa declarada porque as três primeiras propriedades sozinhas descrevem outro comportamento: abre com 50 % de erro numa janela de 30 s, e só quando essa janela tiver ao menos dez chamadas (`MinimumThroughput`), permanecendo aberto por 15 s antes da meia abertura. O limite de volume importa neste canal: o SMS atende apenas `critical` como fallback, então dez chamadas em trinta segundos é um patamar que o próprio desenho torna raro, e abaixo dele o circuito não abre. Rate limit por token bucket em Redis nos limites contratados (§4.3); a validação de opções recusa no start valores de limite fora de faixa, inclusive nas entradas aninhadas por provedor.
- Com o circuito aberto a mensagem volta à fila com visibilidade estendida e a tentativa volta a `queued` preservando o `fallback_deadline`. A varredura por prazo lê `queued` junto com `sent` exatamente por isso: uma tentativa devolvida à fila nunca foi entregue a provedor nenhum, então pedir o próximo passo não pode duplicar entrega, e sem essa leitura uma indisponibilidade mais longa que a validade encerraria a notificação sem nunca tentar o segundo canal. A reivindicação do despacho passou a exigir que o plano não tenha avançado, para que a tentativa devolvida e o próximo passo não saiam os dois.
- As filas novas herdam o contrato de confiabilidade do §8: DLQ por fila com alarme pager para `*-critical` e `*-auth`, redrive por ferramenta interna auditada e TTL rígido verificado em cada ponto de decisão.
- O retry com backoff do §8 é dependência de infraestrutura não entregue nesta fase, e o documento registra a diferença em vez de creditar a garantia à camada errada. O backoff que existe no repositório é único por papel de consumidor (base de 5 s dobrando até o teto), não por classe, então a progressão de `critical` não é expressável na configuração atual. O teto de três recepções é o `maxReceiveCount` da redrive policy do SQS, que pertence à unidade I2 e não existe neste repositório. O que segura o resultado hoje é o TTL rígido por ponto de decisão, que é outro mecanismo.

### Fallback declarativo

- O plano de entrega é configuração de classe: lista ordenada `deliveryPlan` com canal e timeout por passo (exemplo do vocabulário v1: push com timeout de 30 s, depois SMS), conforme a ADR-0011. O contrato tipado já está publicado: `DeliveryPlanStep(Channel Channel, TimeSpan? Timeout)` em [`ClassPolicyDefinition.cs:11`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs) e o campo `DeliveryPlan` na definição de política (`ClassPolicyDefinition.cs:31`).
- Execução (§5.1, §5.2): o Core grava o attempt com `fallback_deadline` calculado no enfileiramento (`queued`), nunca no `sent`; o scheduler detecta o deadline vencido sem `delivered`; o Delivery Tracker grava a mensagem `FallbackRequested` via outbox; o relay roteia para a fila `core-*` correspondente; o Core reavalia o TTL: vencido, a notificação termina em `expired` sem consumir SMS; válido, insere o attempt do próximo passo do plano. Nunca há chamada direta do Tracker ao Core (§5.1).
- Attempt `unknown` de fluxo `critical` ou de autenticação dispara fallback por dois instantes independentes, e não apenas pela idade: o prazo do próprio passo do plano, quando ele vence, e uma carência de 60 s para a tentativa que continua inconclusiva dentro de um passo longo. Os dois são necessários porque um provedor que estoura o timeout logo no início de um passo de trinta segundos deixa a tentativa parada em `unknown` em poucos segundos, e esperar a carência inteira gastaria a carência em cima do passo em vez de dentro dele, respondendo em 65 s um prazo prometido para 30 s. O risco de duplicata é aceito e documentado, preferível a OTP perdido (§5.2; ADR-0008).
- Fan-out de push: a regra de irmãos é propriedade do gatilho reativo, não do gatilho por prazo. Um veredito definitivo de provedor só avança o plano quando nenhum irmão de push continua vivo (§4.3); a varredura por prazo não tem condição de irmão alguma, e não pode ter, porque um push aceito pelo FCM nunca falha e a leitura universal da regra tornaria o critério de saída inalcançável. É a correção 2 registrada no fim deste documento, e o corpo aqui já a descreve.
- Critério de saída associado: 100 % de `critical` com fallback (§15).

### Delivery Tracker: webhooks assinados com replay protection

- Endpoints `/webhooks/twilio` (valida `X-Twilio-Signature`) e `/webhooks/sendgrid` (valida a assinatura ECDSA do Event Webhook); WAF na frente, por serem a única superfície pública do hub (§4.3; §10.2 A9; §16 risco 24).
- A allowlist de faixas de provedor é regra de borda, no ALB ou no WAF, e não controle da aplicação. O motivo é a própria topologia que este documento exige: atrás de um balanceador, o endereço que a aplicação enxerga é o do balanceador, então preencher a lista dentro do hub recusaria todo callback autêntico e ainda geraria alarme de forjaria para cada um. A aplicação mantém uma allowlist desligada por padrão, que existe como defesa em profundidade para um host exposto diretamente; ela passou a ser expressa em notação CIDR e comparada por rede, porque a comparação anterior era de prefixo textual e um prefixo como `54.172.6` autorizava em silêncio de `54.172.60.x` a `54.172.69.x`, além de recusar o mesmo endereço escrito na forma IPv6 mapeada. Uma faixa que não parseia reprova no start.
- Idempotência por UK `(provider, provider_event_id)` na tabela `PROVIDER_EVENT_DEDUPE`; payload bruto armazenado como evidência (§4.3; ADR-0008).
- Replay protection por `provider_event_id` mais janela de timestamp (§10.2 A5), com a limitação declarada por provedor porque ela não é simétrica. O SendGrid assina o instante junto com o corpo e a janela é imposta; a Twilio não envia instante algum no callback de status, e a assinatura HMAC cobre a URL e os parâmetros, nunca o momento. No canal SMS a garantia real é a identidade do evento, e o horizonte dela é a retenção da marca de deduplicação, que passou a valer 60 dias por decisão: é a mesma janela em que a aplicação ainda consegue resolver um attempt, de modo que, quando a marca expira, um callback capturado já não encontra tentativa alguma para descrever. O risco residual, um callback assinado que permanece criptograficamente válido para sempre, fica registrado aqui e não é fechado por esta fase.
- Handler mínimo e síncrono: validar assinatura, `INSERT delivery_event` idempotente, enfileirar; a resposta é `202 Accepted`, que é o código que a rota publica e o que os testes de contrato exigem, e expressa melhor o que acontece: o evento foi aceito para processamento assíncrono. A máquina de estados e o fallback rodam fora da requisição (§11.3).
- O orçamento de 20 ms é por evento e não por callback, declarado como percentil e não como propriedade alcançada. O caminho síncrono cifra o payload uma vez por callback e depois abre e confirma uma transação por evento, então o tempo de resposta é linear no tamanho do lote. A fase entrega o teto que torna esse orçamento uma promessa fechada, um limite de corpo por rota e um teto de eventos por callback, ambos configuráveis, com recusa inteira e `413` acima do teto.
- **A medição achou uma propriedade que a revisão não nomeou: a escrita da ingestão é quadrática no tamanho do lote, não linear.** Cada evento é gravado com os bytes selados do callback inteiro como sua evidência, porque a evidência de um evento do lote é o lote. Um callback de N eventos, cujo corpo cresce com N, grava N cópias de um corpo de tamanho N. A cerca de 420 bytes por evento isso é 1 MiB num lote de cinquenta, 16 MiB em duzentos e 100 MiB em quinhentos, numa única requisição da única rota pública do hub. O teto entregue foi fixado em 200 por causa dessa conta e não por medição: mantém o pior caso na casa das dezenas de megabytes em vez de centenas. O valor certo pertence ao gate de carga com corpos reais de provedor. O que removeria a questão em vez de limitá-la é guardar o corpo uma vez e referenciá-lo nas linhas de evento, que é mudança do modelo de evidência e não deste parâmetro; fica registrada aqui como consequência conhecida e fora do escopo desta fase.
- E entrega a medição do caminho de escrita, no modo `delivery` do projeto de performance: custo de um callback por tamanho de lote até o teto, com o payload selado uma vez por callback como o handler faz, contra PostgreSQL real, com percentis e custo derivado por evento. A mesma célula roda nas duas formas de transação, uma por evento, que é o que a produção faz, e uma por lote, que é a alternativa; a diferença entre as duas linhas é o que a mudança de forma valeria, em milissegundos, em cada tamanho. Isso responde a avaliação que o desenho deixou em aberto com número em vez de julgamento. O que continua fora é TLS, assinatura e pipeline HTTP, que não crescem com o lote.
- Correlação por canal: e-mail via `custom_args.notification_id` e `attempt_id` no SendGrid; SMS via `StatusCallback` por mensagem na Twilio (§4.3).
- Correlação vinda da rota só vale para o provedor cuja assinatura cobre a URL. A Twilio assina a URL completa junto com o formulário, então os identificadores que o hub anexou ao endereço entregue estão dentro da assinatura; o SendGrid assina o instante e o corpo e nada diz sobre o endereço, então um par de identificadores na query é uma alegação não assinada sobre qual tentativa um callback autêntico descreve. Aceitá-la permitiria desviar um callback genuíno para outra tentativa e mudar estado, fallback e supressão. A propriedade é do esquema de assinatura de cada provedor, não configuração. A resolução do attempt também passou a exigir que o provedor do callback seja o provedor da tentativa.
- Transições alimentadas por webhook: `sent → delivered → read` e `sending → failed`/`bounced` (§5.2).

### Scheduler DB-backed

- Worker que a cada 2 s busca attempts com `fallback_deadline < now()` e sem `delivered`, e notificações com `release_at <= now()`, e grava a próxima ação via outbox; simples, sem estado fora do banco, auditável (§4.3). O intervalo é derivado do orçamento somado do fallback e não escolhido isoladamente; a conta está na seção de alternativas.
- Varredura com `LIMIT` e `SKIP LOCKED`, sobre índices parciais cujos predicados são transcritos aqui na forma vigente, porque um índice parcial só responde a um statement cujas quals impliquem o filtro e um leitor que recrie a forma errada derruba o plano em silêncio:
  - `ix_notification_attempt_fallback_due`, colunas `(status, fallback_deadline)`, filtro `fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL AND fallback_requested_at IS NULL`;
  - `ix_notification_attempt_unknown_due`, coluna `(status_changed_at)`, filtro `status = 'unknown' AND fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL AND fallback_requested_at IS NULL`;
  - `ix_notification_attempt_fallback_inflight`, coluna `(fallback_requested_at)`, filtro `fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL AND fallback_requested_at IS NOT NULL`;
  - `ix_notification_release_due`, coluna `(release_at)`, filtro `status = 'deferred'`;
  - `ix_notification_attempt_reconciliation_due`, sobre a expressão `COALESCE(status_changed_at, created_at)`, filtro `status IN ('sent', 'unknown') AND provider_key IS NOT NULL`, que é o índice da reconciliação.
- Verificação de fonte: §11.3 especifica `SKIP LOCKED` para a varredura do scheduler; a forma literal `SELECT ... FOR UPDATE SKIP LOCKED LIMIT 100` aparece no caminho quente do Outbox Relay, não no scheduler. Testes de plano leem o `EXPLAIN` de cada statement contra a base real e reprovam quando os predicados da consulta deixam de implicar o filtro.
- Justificativa registrada no design: `DelaySeconds` do SQS é limitado a 15 min; agendamento distante e janela de silêncio usam `release_at` liberado pelo scheduler, de forma uniforme, auditável e sem EventBridge (§4.2; §16 risco 5).

### Supressão automática, reversível e auditada

- Requisito: supressão automática por hard bounce e número inválido (RF-10, §2.1). O token FCM `UNREGISTERED` é caso à parte e está separado aqui de propósito: ele não é supressão de contato e não passa pelo ledger. Um push endereça um registro de dispositivo, não um ponto de contato, e o que o código faz com `UNREGISTERED` é invalidar o token pelo ciclo de vida de registro do lado do despacho. Consequência: nenhum `suppression.added` é gravado para push, e a reversão pelo caminho de Platform Admin com PIM não se aplica a esse canal, o que a trilha revisada no rito mensal precisa refletir.
- Ponto de aplicação: regra do estágio Policy, junto de consentimento, dedupe e janela de silêncio; a decisão é auditável regra a regra (§4.3; ADR-0015).
- O relato ao ledger de contatos acontece depois do commit da transição, porque relatar antes suprimiria um destino com base num callback que acabou não aplicando nada. Fora da transação, uma falha transitória do módulo de contatos perdia o sinal para sempre: o evento já está aplicado e deduplicado, e nenhuma reentrega o revisita. A dívida passou a ser um carimbo na própria evidência, e uma varredura do scheduler relata o que ficou devendo, com idempotência garantida pela identidade da linha de evidência.
- Somente código de hard bounce específico suprime; para SMS, só após 2 ocorrências em 7 dias. O que impede um bounce forjado de suprimir o contato de um cliente é a assinatura do provedor, que a rota exige sempre; a recusa por origem é controle adicional e só existe onde a allowlist da aplicação estiver ligada, o que não é a postura entregue (§10.2 A5).
- Reversível e auditada: `suppression.added` (automática ou manual) e `suppression.removed`, com ator registrado; supressão manual é atribuição de Platform Admin com PIM (§9.3; §9.1).
- Supressão gerida pelo hub, não pelos suppression groups do SendGrid (§4.3).
- Publicação do evento `araia.notification.contact_suppressed.v1` com `recipientId`, `channel` e `reason` em `notifications.events.v1` (§7.3).
- Decisões sobre supressões manuais são revisadas no rito mensal (§9.7).

### Reconciliação por canal

- Job diário para attempts `sent`/`unknown` sem evento há mais de 6 h; corrige o estado (`unknown` para `sent` ou `failed`) e registra `audit_event` (§8; §5.2).
- A seleção do lote exclui, no próprio statement, as tentativas que nenhuma pergunta pode resolver: as de provedor sem lookup posterior e as de notificação já encerrada. Sem essa exclusão o lote não drenava, porque essas linhas permanecem elegíveis pela vida da partição e, com ordenação do silêncio mais antigo primeiro, ocupavam todas as vagas de todas as rodadas; os canais que o job existe para corrigir nunca eram alcançados. A janela de criação entra na junção para podar partição, e o predicado casa literalmente com o índice parcial da reconciliação.
- Limites reais por provedor, assumidos sem disfarce (§8; ADR-0008):
  - E-mail: SendGrid Email Activity API por `custom_args`; histórico além de poucos dias exige add-on pago; a contratação é decisão externa, registrada como dependência desta fase.
  - SMS e WhatsApp: a Twilio não oferece busca por metadado customizado; correlação best effort por `To` mais janela temporal.
  - Push: o FCM não oferece lookup posterior; `unknown` de push resolve apenas por fallback ou TTL.
- A reconciliação complementa, e não substitui, o fallback de `unknown` em `critical`/autenticação, que é acionado pelo prazo do passo e pela carência de 60 s (§5.2; ADR-0008).

### Classe `operational` com janela de silêncio

- Ativação da classe `operational`: canais e-mail e push, fallback de 1 h, opt-out parcial, janela de silêncio de 21h às 8h no fuso de `RECIPIENT_PROFILE`, base legal de legítimo interesse (§3).
- Regra `QuietHours` do estágio Policy: defer para classes que não sejam `critical`/autenticação, no fuso de `RECIPIENT_PROFILE` (ADR-0011); o resultado `Defer` grava `release_at` e o scheduler libera a notificação (§4.3; §4.2).
- Um fuso declarado que o runtime não resolve cai no fuso padrão da plataforma, e a substituição aparece na evidência da regra ao lado do valor declarado. O contrato do estágio é produzir decisão auditável, não propagar exceção: um identificador inválido gravado antes do validador atual, ou uma imagem sem base de fusos, faria a avaliação lançar em toda notificação daquele destinatário, justamente na classe que esta fase ativa.
- Contrato tipado já publicado: `QuietHoursWindow(TimeOnly From, TimeOnly To)` e o campo opcional `QuietHours` da política ([`ClassPolicyDefinition.cs:14`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs) e `ClassPolicyDefinition.cs:40`).
- A fronteira entre `operational` e `transactional` é definida no cadastro do template por Produto e Compliance na fase 0; o hub aplica a classificação, não a decide (§16 risco 11).

### Relatório mensal de evidências

- Job mensal gera e guarda no mesmo bucket WORM: volumes por classe e canal, rejeições por motivo, taxa de entrega e bounce, DLQs, falhas de provedor, mudanças de catálogo, política e configuração com aprovadores, ativações de PIM e resultado das verificações de hash chain (§9.6).
- Três dessas seções não são produzíveis hoje e o relatório sai parcial, com as ausências nomeadas nele: fila morta e falhas de provedor dependem da fonte de métricas operacionais da unidade I2, e as ativações de PIM dependem de integração com o provedor de identidade. A terceira é diferente em natureza e por isso está separada: a elevação de privilégio acontece fora deste hub, então nenhuma fonte interna pode afirmá-la e a unidade I2 não a resolve.
- O relatório alimenta o rito mensal de Engenharia, Compliance e Produto (§9.7).
- Critério de saída associado: primeiro relatório mensal entregue a Compliance (§15).

### Confirmação de entrega na consulta de auditoria

- `GET /v1/audit/notifications/{id}` passa a responder se o provedor confirmou a entrega, fechando a oitava pergunta do §9.5 que a fase 1b declarou como lacuna. É entrega desta fase e não da anterior, e está aqui porque a dependência declarada mais abaixo creditava a capacidade à 1b.
- `NotificationAttemptEvidence` ganha `DeliveredAt` e `DeliveryEvents`, e `DeliveryEventEvidence` publica `ProviderKey`, `ProviderEventId`, `Kind`, `OccurredAt` e `ErrorCode`. Nenhum payload bruto e nenhum dado de contato sai na resposta.
- Muda o significado da resposta: até esta fase `status`, `sentAt` e `providerMessageId` afirmavam aceitação pelo provedor e nunca entrega, e lista vazia passou a ser afirmação legítima porque a tabela existe.

## Fora de escopo

- WhatsApp: adapter, submissão de Content template, sincronização de status Meta, opt-in e processamento de `SAIR` pertencem à fase 3 (§15).
- Segundo provedor por canal e failover de provedor: risco aceito na v1; avaliação de SES como failover de e-mail é v2 (§16 riscos 2 e 3).
- Template Studio e demais evoluções com gatilho (aprovação dupla por classe, envio de teste, promoção por bundle, `review_due`) (§15).
- Classe `marketing` (fora de escopo da v1, §3).
- Nível 2 de política, com condições por expressão (§16 risco 20; ADR-0011).

## Dependências

- **Fase 1b completa**, com seus critérios de saída atingidos (§15): OTP de login e confirmação de câmbio migrados, `kyc.document.approved` chegando pelo barramento, consulta de auditoria respondendo a sete das oito perguntas do §9.5 e verificação de hash chain em execução. A oitava, a confirmação de entrega ao destinatário, era lacuna declarada da 1b e foi fechada nesta fase, pela fatia F2-3.
- **Fase 1a**: a política de classe é publicada pela API de gestão com quatro olhos (RF-14, §2.1); `deliveryPlan` e `quietHours` já fazem parte do vocabulário v1 dessa configuração (ADR-0011; `ClassPolicyDefinition.cs:23`).
- **Externas herdadas**, registradas como dependências e não resolvidas por esta fase:
  - Verificação do sender ID alfanumérico no Brasil nas country guidelines da Twilio (§2.3; §16 risco 9). Enquanto pendente, o desenho usa número longo ou short code BR no Messaging Service.
  - Decisão de contratação do add-on pago Email Activity do SendGrid, que condiciona o alcance histórico da reconciliação de e-mail (§8; ADR-0008).

## Arquitetura e fronteiras de módulo

- O hub permanece um monólito modular e a fase 2 não cria módulo novo por padrão. Decisão de fronteira do arquiteto no fechamento da fase 1b: o Delivery Tracking não nasceu como módulo próprio na 1b; a extração para módulo próprio na fase 2 acontece somente com evidência concreta de atrito de fronteira; sem essa evidência, o tracker permanece dentro do módulo Notifications. A mesma decisão determina que QuietHours entra em vigor nesta fase, junto com a classe `operational`.
- Dependências entre módulos acontecem exclusivamente pelos namespaces `Modules.<outro>.Integration.V1`, regra de arquitetura vigente na base (`src/Platform.Api/Modules/TemplateManagement/AGENTS.md:105`). O consumo de `ClassPolicyDefinition`, `DeliveryPlanStep`, `QuietHoursWindow` e `Channel` pelo pipeline segue esse contrato publicado.
- Fluxo de fallback, sem chamada direta entre componentes (§5.1): webhook ou scheduler detecta a condição; o Tracker grava `FallbackRequested` via outbox; o Outbox Relay roteia para a fila `core-*`; o Core decide (TTL, próximo passo do plano) e enfileira o attempt seguinte para o dispatcher do canal.
- O adapter SMS entra como mais um plugin atrás de `IChannelProvider`, sem mudança na topologia de filas por classe e canal (§4.3; §4.2; ADR-0001).
- O gatilho de fallback é roteado para a fila de autenticação quando a notificação é de fluxo de autenticação, qualquer que seja a classe, que é a terceira parte decidida pela ADR-0014.
- O handler do fallback resolve a notificação e a tentativa em consultas independentes e valida que a tentativa pertence à notificação antes de qualquer escrita: um par inconsistente aplicaria a política de uma notificação ao canal de outra e cruzaria as duas trilhas.

## Dados e persistência

- Máquina de estados por attempt conforme a tabela canônica do §5.2, incluindo `sending` e `unknown`; `fallback_deadline` gravado no enfileiramento do attempt (`queued`), não no `sent` (§5.1; §5.2; ADR-0008).
- `release_at` na notificação sustenta deferimento por janela de silêncio e agendamento além de 15 min (§4.2).
- `PROVIDER_EVENT_DEDUPE(provider, provider_event_id)` com UK em tabela não particionada, purgada por idade com retenção de 60 dias, que é o horizonte da proteção contra replay no canal SMS e não uma decisão de armazenamento; `delivery_event` particionada mensalmente, com payload bruto do webhook como evidência (§4.3; §11.3).
- Colunas que esta fase acrescentou e que não estavam inventariadas aqui:
  - `notification_attempt.plan_advanced_at`, o claim de avanço de passo da ADR-0014, que é o mecanismo que impede dois SMS para o mesmo cliente;
  - `notification_attempt.fallback_requested_at`, que impede a varredura de reemitir gatilho para uma tentativa cujo pedido ainda está em voo, com janela de reemissão configurável;
  - `notification_attempt.status_changed_at`, que responde há quanto tempo a tentativa está onde está e é o que a varredura de vereditos inconclusivos lê;
  - `notification.auth_flow`, que roteia o fluxo de autenticação para as filas próprias qualquer que seja a classe;
  - `notification.admitted_plan`, o plano de entrega sob o qual a notificação foi admitida, congelado no despacho;
  - `delivery_event.suppression_signal` e `delivery_event.suppression_reported_at`, a classificação que o lado do despacho fez e a marca de que o relato ao ledger de contatos foi concluído.
- Índices que sustentam o scheduler e a reconciliação: os cinco listados na seção do scheduler, com predicado literal, mais `(provider_message_id)` filtrado por `provider_message_id IS NOT NULL` e o parcial de supressão pendente sobre `delivery_event` (§11.3).
- Supressão com trilha de auditoria própria (`suppression.added`, `suppression.removed`) e evento de saída `araia.notification.contact_suppressed.v1` (§9.3; §7.3).

## Segurança e ameaças

- Ameaça A5, webhook forjado ou repetido: `delivered` falso suprime o fallback e `bounce` falso suprime o contato de um cliente. Controles desta fase, com o dono de cada um nomeado: assinatura obrigatória na aplicação (Twilio HMAC sobre URL e formulário, SendGrid ECDSA sobre instante e corpo); correlação de rota aceita apenas do provedor cuja assinatura cobre a URL; replay por `provider_event_id`, com janela de instante onde o provedor a fornece e retenção da marca como horizonte onde não fornece; supressão só por hard bounce específico (SMS: 2 ocorrências em 7 dias), reversível e auditada. Na borda, e não na aplicação: WAF com regra de taxa e allowlist de faixas de provedor. O alarme de segurança por origem não permitida só existe onde a allowlist da aplicação estiver ligada, o que não é a postura entregue.
- Os webhooks são a única superfície pública do hub: ALB público somente com WAF, allowlist de IP e TLS, todos na borda; pentest específico previsto (§10.2 A9; §16 risco 24). A aplicação não lê cabeçalhos encaminhados e isso é decisão, não omissão: confiar num cabeçalho de endereço exige uma lista de proxies conhecidos que só a unidade I2 pode fixar, e ler o cabeçalho sem essa lista transformaria o controle em algo que o próprio chamador escolhe.
- Ameaça A4, interceptação de OTP por SMS: push permanece canal primário; SMS entra como fallback com rate limit por destinatário; TTL curto; SMS de OTP sem link (§10.2 A4; §2.3).
- Prevenção a phishing no canal SMS: sem encurtadores, links só em domínio próprio, remetente registrado por número longo ou short code BR (§2.3).
- Kill switch por canal: o dispatcher SMS pausa e o fallback de canal continua se o plano permitir; acionamento humano, ou automático se o circuito ficar aberto por mais de 10 min (§10.3). O gate automático é entregue desligado por padrão, com o trade-off registrado na tabela de riscos: o circuito é observado por processo enquanto o kill switch é global, então uma instância degradada pararia o canal da frota inteira, e SMS é o último passo do plano, então parar esse canal deixa códigos de autenticação esperando até expirar. Ligá-lo é decisão de operação, não de deploy.
- O limitador de taxa por provedor degrada para fail-open: com o Redis indisponível o envio segue sem limite, e a compensação declarada é o kill switch manual. O comportamento está na tabela de riscos, no mesmo idioma que a ADR-0011 usa para o fail-open do dedupe.

## Observabilidade e operações

- O webhook responde `202 Accepted` e o processamento pesado é assíncrono; provedores reenviam quando o webhook demora, o que degrada ainda mais (§11.3). O alvo de 20 ms é por evento e ainda não tem medição: o percentil, o perfil de carga e o teto de eventos por callback são o que o gate de carga do §11.6 precisa fixar, e o teto já está implementado como recusa acima do limite.
- Alarmes: DLQ de `*-critical` e `*-auth` aciona pager (§8); profundidade da fila `dispatch-sms-critical` tem alarme próprio (§16 risco 22); callback com assinatura inválida gera registro próprio, separável de recusa por origem, porque assinatura inválida também é o sintoma cotidiano de segredo rotacionado e origem não permitida é tentativa de forjaria (§10.2 A5). O alarme por origem depende da allowlist da aplicação estar ligada; na postura entregue, a recusa por faixa acontece na borda e o alarme correspondente é do WAF.
- A reconciliação corrige estado e registra `audit_event`; reentrega interna aparece na auditoria como `notification.duplicate`, nunca como segundo envio ao cliente (§8; ADR-0008). `notification.duplicate` descreve mensagem interna repetida e nada afirma sobre o cliente; a duplicata aceita do fallback a partir de `unknown` é outra coisa e tem entrada própria, `fallback.requested_from_unknown`, que registra o risco assumido e nunca alega detecção de uma entrega duplicada. A detecção não é possível: um provedor que nunca respondeu não vai responder depois.
- Operação recorrente: rito mensal revisa o relatório de evidências, supressões manuais, mudanças de política pendentes e custos por aplicação (§9.7).

## Implantação, rollout e rollback

- Ordem de ativação derivada das dependências do fluxo (§5.1): primeiro Delivery Tracker (webhooks) e scheduler, porque o fallback depende do deadline varrido pelo scheduler e da mensagem `FallbackRequested` do tracker; depois o adapter SMS; por fim a ativação do fallback e da classe `operational` por política.
- Ativação por configuração, não por deploy: incluir SMS no `deliveryPlan` e ativar `quietHours` são publicações de nova versão da política de classe pela API de gestão, com quatro olhos (RF-14; ADR-0011). Rollback é republicação da versão anterior pelo mesmo caminho, com trilha completa (§9.7).
- Mitigação imediata em incidente: kill switch por canal SMS (§10.3); `ValidityPeriod` na Twilio atua como segunda barreira de TTL mesmo com o hub degradado (§8), e essa barreira só existe no ramo Programmable Messaging, o que reforça a configuração obrigatória da unidade I2.
- A decomposição das entregas em fatias numeradas de implementação acontece no kickoff da fase, com este design como insumo.

## Estratégia de testes

- Verificação dos critérios de saída: cenário fim a fim do §5.1 (push sem `delivered` em 30 s dispara SMS) para 100 % das `critical`; zero envio após TTL, verificado em cada ponto de decisão e coberto pela segunda barreira `ValidityPeriod` (§8); geração e entrega do primeiro relatório mensal (§9.6; §15).
- Precondição de ambiente das provas acima: elas rodam com banco, cache e filas em contêiner, e sem daemon de contêiner disponível são omitidas, o que numa suíte é indistinguível de aprovadas. A execução que grada uma entrega declara a variável `NOTIFICATIONHUB_REQUIRE_DOCKER` e, com ela, um daemon ausente reprova em vez de omitir. Sem a variável, a estação de trabalho continua omitindo com motivo declarado.
- Máquina de estados por attempt: cobertura das transições canônicas do §5.2, incluindo `sending → unknown` e o fallback de `unknown` em `critical`/autenticação pelos dois instantes, o prazo do passo e a carência de 60 s.
- Webhooks: assinatura inválida rejeitada, replay de `provider_event_id` sem efeito duplicado, origem fora da allowlist configurada sem efeito e com alarme, correlação de query recusada para o provedor cuja assinatura não cobre a URL, e lote acima do teto recusado inteiro (§10.2 A5).
- Supressão: hard bounce específico suprime; SMS exige 2 ocorrências em 7 dias; reversão gera `suppression.removed` auditado (§10.2 A5; §9.3).
- Janela de silêncio: `operational` dentro da janela recebe `Defer` com `release_at` no fuso de `RECIPIENT_PROFILE` e é liberada pelo scheduler; `critical` não é afetada (§3; ADR-0011).
- Caos, herdado da ADR-0008: matar pods durante burst, rebalance sob carga e failover do banco, com zero duplicata ao cliente e zero perda. **Pendente**: não existe artefato de caos neste repositório, nem runbook, nem recibo de execução. O item pertence ao gate de carga do §11.6 e à unidade I2, que provisiona o ambiente onde derrubar processo e provocar failover é possível. Enquanto não existir, a promessa de zero duplicata sob falha de infraestrutura é desenho e não medição.
- Latência de fallback, medida em dois níveis. O cenário fim a fim aciona cada estágio à mão sobre relógio controlado, então ele prova composição e não tempo; por cima dele há uma asserção sobre o orçamento somado, que reprova quando um dos termos configuráveis torna o aceite aritmeticamente impossível. A medição de tempo decorrido vive no projeto de performance, no modo `delivery`: ele semeia notificações e tentativas no volume pedido, com as vencidas como minoria rara, e mede a rodada do scheduler que acha essas tentativas, com percentis e plano de execução. É o único termo do prazo que cresce com a retenção; os saltos de fila e a chamada ao provedor são orçamento fixo e ficam declarados como tal.
- O que o modo `delivery` deliberadamente não mede: TLS, verificação de assinatura, pipeline HTTP e os saltos de fila. Nenhum cresce com volume ou com lote, e medi-los exige host e cliente, que é o gate de carga do §11.6 contra ambiente real. O aceite em percentil sobre carga real continua sendo decisão daquele gate; o que a fase entrega é o instrumento e a série contra a qual ele decide.
- Vetor fixo de assinatura de provedor: o teste do SendGrid inclui chave pública, corpo, instante e assinatura produzidos fora deste repositório, verificados sem chamar o auxiliar de assinatura da própria suíte. Sem ele, uma mudança que tocasse o verificador e o auxiliar juntos manteria a suíte verde e recusaria todo callback real.
- Sender BR: teste por operadora, conforme a mitigação do risco 9 (§16).

## Alternativas e decisões deliberadamente adiadas

- **Scheduler DB-backed versus EventBridge ou `DelaySeconds`**: decisão do design de sistema, mantida aqui: `DelaySeconds` não passa de 15 min e o scheduler sobre o banco é uniforme e auditável (§4.2; §16 risco 5). Trade-off aceito, agora com a conta somada em vez de estimada: o §11.6 aceita até 35 s entre o push degradado e o SMS que o substitui, e o orçamento é a soma do prazo do passo (30 s no plano de `critical`), mais um intervalo de varredura, mais os dois saltos de fila e o estágio Core que o §11.2 orça em 300 ms, 300 ms e 200 ms, mais a chamada ao provedor. Com o intervalo em 5 s e o timeout do canal SMS em 5 s a soma passava de 40 s, e nenhum teste media isso. Os dois termos configuráveis foram derivados do aceite: intervalo de varredura em 2 s e timeout do provedor de SMS em 2 s, que é também o valor que o §11.3 manda para `critical`. A soma fica em 34,8 s no pior caso e uma asserção de orçamento reprova se qualquer um dos dois subir sozinho.
- **Fallback a partir de veredito inconclusivo**: aceita duplicata rara em troca de nunca perder OTP, e é acionado pelo prazo do passo e pela carência de 60 s, o que é a mesma decisão medida por dois instantes e não duas decisões (ADR-0008). O que a trilha registra é o risco assumido, com a entrada `fallback.requested_from_unknown` no avanço. A duplicata efetiva não é observável e o documento não alega que seja: `notification.duplicate` descreve mensagem interna repetida.
- **Extração do Delivery Tracking como módulo próprio**: adiada; acontece nesta fase somente com evidência concreta de atrito de fronteira, senão o tracker permanece no módulo Notifications (decisão de fronteira do arquiteto na 1b).
- **Contratação do add-on Email Activity do SendGrid**: não decidida aqui; registrada como dependência externa com impacto direto no alcance da reconciliação de e-mail (§8; ADR-0008).
- **Forma final do sender por operadora**: não decidida aqui; aguarda a verificação nas country guidelines da Twilio e o teste por operadora (§2.3; §16 risco 9).
- **Rodízio 3:1 entre `transactional` e `operational` no consumo de filas**: não introduzido nesta fase. O §4.2 marca a ativação da classe `operational` como gatilho de reavaliação, e a reavaliação foi feita agora, na fatia F2-12, com este resultado: fica o que já existe. O comportamento atual não é rodízio nenhum, é prioridade estrita na alocação de vaga de processamento, e a evidência está no código: cada fila entra no consumidor com o posto fixo da sua banda (`auth` 0, `critical` 1, `transactional` 2, `operational` 3, em `OutboxBand`), e quando uma vaga é liberada ela vai para o pretendente de menor posto, o que o teste unitário `A_freed_slot_goes_to_the_highest_priority_waiter_not_the_first_in_line` afirma. Cada fila faz long polling próprio, então uma classe nunca deixa de ser lida; o que ela pode não conseguir é vaga, enquanto houver pretendente de banda superior. O risco declarado é esse: sob pressão sustentada das bandas superiores, `operational` fica sem vaga por tempo indeterminado. Trocar prioridade estrita por rodízio com peso é decisão de calibração e exige medição, não julgamento: sem número de starvation observado em carga real não há como escolher o peso, e um rodízio mal calibrado devolve vaga a `operational` na frente de `critical`. A medição pertence ao gate de carga (§11.6), o outro gatilho que o §4.2 já nomeia, e a mudança só entra depois dela.
- **Timeout de provedor é propriedade por provedor, e o §11.3 o pede por classe.** O desvio fica registrado em vez de disfarçado: o pipeline de resiliência é composto uma vez por provedor e nunca por fila, então a classe não chega a esse ponto de decisão. A lacuna é fechada pelo uso e não pela forma: o SMS atende apenas `critical` como fallback, então o valor do canal foi ajustado para o orçamento de `critical`. Tornar o timeout por classe exigiria mudar a forma das opções de provedor, o que não é escopo desta fase.
- Desvios das ADRs relacionadas, declarados: o adapter SMS continua plugin (ADR-0001), a entrega continua at-least-once com idempotência em camadas (ADR-0008) e a política continua configuração de classe (ADR-0011). O que mudou é a lista de regras do estágio Policy: a [ADR-0015](../ADR-0015-regra-de-supressao-no-estagio-policy.md) acrescenta a supressão como sexta regra, que é o primeiro uso do nível 3 previsto pela própria ADR-0011 e consome parte do orçamento que ela estabeleceu para si mesma. O contador começa aqui. A [ADR-0014](../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md) decide três partes, e as três estão implementadas: o claim de avanço por passo, a semântica de aceitação do push e o roteamento do gatilho para a fila de autenticação.

## Status de implementação

A decomposição em fatias prometida no fim da seção de implantação foi produzida e aceita em [fase-2-decomposicao.md](fase-2-decomposicao.md), que fixa as sete decisões de fronteira que este design deixou para o kickoff e carrega a tabela de status por fatia. O acompanhamento por fatia vive lá; este documento registra apenas o estado das entregas.

| Entrega deste design | Estado em 2026-08-25 |
|---|---|
| Delivery Tracker com webhooks assinados e replay protection | Concluída (fatias F2-1 e F2-2, commit `7af6e32`) |
| Fallback declarativo, unicidade do avanço de plano | Concluída (fatia F2-4, commit `7af6e32`) |
| Scheduler DB-backed | Concluída (fatia F2-5, commit `7af6e32`) |
| Adapter SMS (Twilio) | Concluída (fatias F2-7 e F2-8, commits `b2a885e` e `8132cbf`), incluindo rate limit por provedor com fail-open declarado, kill switch automático de canal entregue desligado por padrão e pool de sender por aplicação, que depende do Messaging Service da unidade I2 |
| Supressão automática, reversível e auditada | Concluída (fatia F2-6, commit `47ab335`) |
| Reconciliação por canal | Concluída (fatia F2-9, commit `6850637`) |
| Classe `operational` com janela de silêncio | Concluída (fatia F2-12, commit `26f72df`) |
| Relatório mensal de evidências | Job concluído (fatia F2-10, commit `e74fdfa`); o relatório que ele produz é **parcial** e assim se declara, com `deadLetterQueues`, `providerFailures` e `privilegedAccessActivations` ausentes. Não trate a linha como pacote mensal completo |
| Confirmação de entrega na consulta de auditoria (oitava pergunta do §9.5) | Concluída (fatia F2-3, commit `9c4cbb4`) |
| Cenário de ponta a ponta do §5.1 (push aceito sem evento, prazo vencido, SMS, webhook encerrando) | Concluída (fatia F2-11, commit `26f72df`) |

Duas correções que a implementação obrigou e que este design não previa, ambas registradas na decomposição e a segunda também em [ADR-0014](../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md):

1. **Os dois gatilhos de fallback precisavam de unicidade no banco.** A deduplicação por mensagem não cobre dois produtores distintos, e o gatilho reativo e o gatilho por prazo geram identificadores de mensagem distintos: sem a correção, o cenário do §5.1 entregaria dois SMS ao mesmo cliente, contra a ADR-0008. A unicidade passou a ser claim de estado por passo do plano.
2. **A aceitação do push encerrava a notificação e matava o fallback.** Enquanto o push aceito pelo FCM declarasse a notificação entregue, o cenário do §5.1 seria descartado em silêncio e o critério de saída de 100 % de `critical` com fallback seria inatingível. A aceitação passou a declarar entrega somente no passo sem prazo, isto é, no último passo do plano. Consequência observável: `araia.notification.delivered.v1` deixa de ser emitido na aceitação do push quando o plano tem passo posterior, o que exige comunicação aos produtores antes do deploy.

## Remediação das revisões

Duas revisões independentes deste documento registraram 42 achados, que
consolidam em 37 causas distintas. Todas foram corrigidas, e o registro do que
mudou vive em
[pacote de remediação de 2026-08-25](../reviews/fase-2-remediacao-20260825T182631Z/00-index.md).
Este documento já descreve o estado resultante; o pacote guarda a rastreabilidade
de cada causa até a correção e nomeia o que continua sem prova local.

Nove das correções alteram comportamento observável e três alteram valores de
configuração entregues, o que torna o pacote leitura obrigatória para quem
compara este design com uma implantação anterior a 2026-08-25.

## Critérios de saída

Conforme a linha da fase 2 do roadmap (§15):

1. 100 % das notificações `critical` com fallback.
2. Zero envio após o TTL.
3. Primeiro relatório mensal de evidências entregue a Compliance.

### Estado em 2026-08-25, separando o que o código prova do que depende de ato humano

**1. Fallback em `critical`: o caminho está provado, a cobertura de 100 % não é afirmável por código.**
O código prova duas coisas. O caminho existe de ponta a ponta, num único teste com banco, cache, filas e provedor falso: push aceito sem evento de entrega, prazo de trinta segundos vencido, varredura reivindicando exatamente uma tentativa, TTL reavaliado, SMS enviado com a URL de retorno que o próprio hub montou, e webhook assinado encerrando a notificação. E o portão executável reprova enquanto existir política `critical` publicada cujo plano não tenha passo posterior. O portão tem dois limites honestos, e não um. Ele mede plano publicado, não alcance em tempo de execução, então um plano de dois passos cujo segundo canal não tenha conteúdo publicado ou destinatário alcançável passa no portão sem entregar fallback. E ele contava apenas a classe `critical`, o que deixava passar uma política `transactional` de um passo hospedando template de finalidade de autenticação, embora o resto do desenho trate classe crítica e fluxo de autenticação como uma unidade; a fonte do portão passou a alcançar também as classes que hospedam template de autenticação publicado. A prova de ponta a ponta é condicional a ambiente: ela precisa de banco, cache e filas em contêiner, e a execução que grada a entrega precisa declarar `NOTIFICATIONHUB_REQUIRE_DOCKER` para que um daemon ausente reprove em vez de omitir. A publicação da política de ativação com quatro olhos é ato humano, a execução do portão com recibo em ambiente real é ato de SRE, e o SMS só sai em produção com o sender BR, o Messaging Service e a URL de callback da unidade I2, com o produto Twilio em Programmable Messaging.

**2. Zero envio após o TTL: provado no ponto de decisão, não como propriedade global.**
Validade vencida no instante do pedido de fallback termina a notificação em `expired` sem consumir SMS, provado pela contagem de requisições no provedor falso, sem tentativa nova e sem trilha de enfileiramento. O ponto de decisão do dispatcher já era coberto, e o `ValidityPeriod` enviado à Twilio atua como segunda barreira, que é comportamento de provedor e não de teste. Não estão provados: o comportamento sob carga, e a corrida entre o vencimento e uma chamada de provedor já em voo.

**3. Primeiro relatório mensal entregue: depende inteiramente de ato humano e de infraestrutura.**
O job de composição e o arquivamento imutável estão implementados e verdes. A entrega a Compliance é rito com janela mensal real, bucket WORM e chaves reais, e nada em código pode alegá-la. Além disso, o relatório sai parcial em três seções, e as causas são duas e não uma: fila morta e falhas de provedor permanecem ausentes até existir a fonte de métricas operacionais, que pertence à unidade I2; as ativações de PIM permanecem ausentes porque a elevação de privilégio acontece no provedor de identidade, fora deste hub, então nenhuma fonte interna pode afirmá-las e a unidade I2 não resolve essa. Compliance recebe um pacote que nomeia as três ausências, e não um pacote completo.

Conclusão: **as doze fatias de código da fase, de F2-1 a F2-12, estão concluídas e validadas**, e a tabela acima agora nomeia as doze, inclusive a F2-3, cuja entrega faltava tanto no escopo quanto no acompanhamento. Os três critérios de saída dependem, nesta ordem, da publicação de política com quatro olhos, da entrega declarativa da unidade I2 e do primeiro ciclo mensal real.

## Riscos

| Risco | Fonte | Tratamento nesta fase |
|---|---|---|
| Operadoras brasileiras podem não entregar sender ID alfanumérico; verificação pendente nas country guidelines da Twilio | §2.3; §16 risco 9 | Número longo ou short code BR no Messaging Service; teste por operadora; a verificação permanece como dependência externa |
| Vazão de SMS limitada pelo MPS contratado por sender | §16 risco 22 | SMS restrito a `critical`; pool de senders por `application`; alarme da fila `dispatch-sms-critical`; negociar MPS com base no burst de referência |
| Webhooks são a única superfície pública do hub | §16 risco 24; §10.2 A5 | Na aplicação: assinatura obrigatória, replay protection, supressão corroborada, teto de corpo e teto de eventos por callback. Na borda: WAF com regra de taxa e allowlist de faixas de provedor, porque o endereço que a aplicação enxerga atrás do balanceador é o do balanceador. Pentest específico previsto |
| Concentração Twilio e SendGrid na mesma empresa; indisponibilidade correlacionada | §16 riscos 2 e 3 | Risco aceito na v1; `IChannelProvider` preserva a troca de provedor; fallback de canal e circuit breaker reduzem o impacto |
| FCM sem confirmação de entrega real | §16 risco 4 | Aceitação pelo FCM não declara entrega quando o passo tem prazo: a notificação só é declarada entregue na aceitação do último passo do plano, que é a correção 2 registrada acima. O fallback por prazo compensa para `critical`, e `araia.notification.delivered.v1` deixa de ser emitido na aceitação do push quando há passo posterior |
| Duplicata ao cliente no fallback imediato de attempt `unknown` | §5.2; ADR-0008 | Risco aceito e documentado, preferível a OTP perdido; a trilha grava `fallback.requested_from_unknown` no avanço, registrando o risco assumido. A duplicata em si não é observável, porque um provedor que nunca respondeu não responderá depois, e `notification.duplicate` descreve mensagem interna repetida, não segundo envio ao cliente |
| Reconciliação com lacunas de lookup por provedor | §8; ADR-0008 | Fallback por prazo e por carência em `critical`/autenticação; a seleção do lote exclui os provedores sem lookup para que as demais linhas sejam alcançadas; decisão do add-on Email Activity tratada como dependência externa |
| Kill switch automático de canal entregue desligado por padrão | §10.3 | O circuito é observado por processo e o kill switch é global, então uma instância degradada pararia o canal da frota; SMS é o último passo do plano, e pará-lo deixa código de autenticação esperando até expirar. Ligar é decisão de operação, com dono nomeado no rito mensal |
| Limitador de taxa por provedor degrada para fail-open | §4.3; ADR-0011 | Com o Redis indisponível o envio segue sem limite; a compensação é o kill switch manual por canal, e o comportamento fica declarado aqui em vez de implícito |
| Callback Twilio sem prova de frescor | §10.2 A5 | O provedor não envia instante e a assinatura não o cobre; a garantia real é a identidade do evento, com retenção de 60 dias alinhada à janela em que um attempt ainda é resolvível. Risco residual aceito nesta fase |
| Testes de caos sem artefato | ADR-0008 | Item declarado pendente, atribuído ao gate de carga do §11.6 e ao ambiente da unidade I2; a promessa de zero duplicata sob falha de infraestrutura permanece desenho e não medição |
| Orçamento de 20 ms do webhook sem medição | §11.3; §11.6 | Teto de corpo e teto de eventos por callback implementados, com recusa acima do limite; percentil e perfil de carga pertencem ao gate de carga |

## Referências

- [Notification Hub, Design de Sistema](../notification-hub-system-design.md): §2.1, §2.3, §3, §4.2, §4.3, §5.1, §5.2, §7.3, §8, §9.3, §9.6, §9.7, §10.2, §10.3, §11.3, §15, §16.
- [ADR-0008: Entrega at-least-once com idempotência](../ADR-0008-at-least-once-com-idempotencia.md).
- [ADR-0001: Canal e provedor como plugin](../ADR-0001-canal-e-provedor-como-plugin.md).
- [ADR-0011: Política como configuração de classe](../ADR-0011-politica-como-configuracao-de-classe.md).
- [ADR-0014: Confirmação de entrega e gatilhos de fallback](../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md).
- [ADR-0015: Regra de supressão no estágio Policy](../ADR-0015-regra-de-supressao-no-estagio-policy.md).
- [`ClassPolicyDefinition.cs`](../../src/Platform.Api/Modules/TemplateManagement/Integration/V1/ClassPolicyDefinition.cs): contratos `DeliveryPlanStep`, `QuietHoursWindow` e `ClassPolicyDefinition` publicados em `Integration/V1`.
- [`AGENTS.md` do módulo TemplateManagement](../../src/Platform.Api/Modules/TemplateManagement/AGENTS.md): regra de dependência entre módulos via `Modules.<outro>.Integration.V1`.
