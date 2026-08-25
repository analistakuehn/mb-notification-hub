---
language: pt-BR
---

# Correções aplicadas e invariantes resultantes

[Voltar ao índice](00-index.md)

Cada bloco nomeia a causa, o que passou a ser verdade e onde a mudança vive.
Referências a arquivo apontam para o estado depois desta remediação.

## Gatilhos de fallback

**Tentativa devolvida à fila volta a ser vista.** A varredura por prazo passou a
ler `queued` junto com `sent`
([`OverdueFallbackScan.cs`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Scheduling/OverdueFallbackScan.cs)).
Circuito aberto, throttle e kill switch devolvem a tentativa à fila preservando o
`fallback_deadline`, e antes nenhuma varredura a enxergava: uma indisponibilidade
mais longa que a validade encerrava a notificação sem tentar o segundo canal.
Nenhum dos dois estados carrega o risco que mantém `unknown` fora desse lote,
porque uma tentativa em `queued` nunca chegou a provedor algum e uma em `sent`
foi aceita. A unicidade continua garantida pela reivindicação: o despacho passou
a exigir `plan_advanced_at` e `fallback_requested_at` nulos
([`AttemptDispatchWriter.cs`](../../../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/AttemptDispatchWriter.cs)),
de modo que a tentativa devolvida e o próximo passo nunca saem os dois.

**Veredito inconclusivo responde no prazo do passo.** Um segundo statement pede o
próximo passo quando o `fallback_deadline` de uma tentativa `unknown` vence,
mantendo a carência de 60 s para a tentativa parada dentro de um passo longo.
Antes, um provedor que estourava o timeout logo no início de um passo de trinta
segundos só produzia fallback aos 65 s. Os dois statements não podem ser um só:
um `OR` entre idade e prazo não é buscável em coluna alguma e degradaria a
varredura em leitura de todas as partições, o que os testes de plano medem e
reprovam.

**Par inconsistente é recusado antes de qualquer escrita.**
[`FallbackRequestHandler`](../../../src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs)
resolve notificação e tentativa em consultas independentes e agora valida que a
tentativa pertence à notificação. Sem isso, uma mensagem interna malformada
aplicaria a política de uma notificação ao canal de outra e cruzaria as trilhas.

**Duplicata assumida é registrada como o que é.** Quando o passo avança a partir
de uma tentativa `unknown`, a trilha grava `fallback.requested_from_unknown` na
mesma transação do avanço, declarando o risco assumido. `notification.duplicate`
continua descrevendo mensagem interna repetida, e nunca segundo envio ao cliente,
que é indetectável: um provedor que nunca respondeu não responderá depois.

## Plano de entrega

**O fallback avança sobre o plano da admissão.** `notification.admitted_plan`
congela no despacho o plano que o estágio Policy admitiu, já filtrado por canais
permitidos, canais com conteúdo e canais alcançáveis
([`AdmittedDeliveryPlan.cs`](../../../src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs);
migração `AddNotificationAdmittedPlan`). Rederivar da política publicada corrente
fazia duas coisas erradas ao mesmo tempo: uma republicação, que é como um plano é
ativado e revertido, mudava o comportamento de notificações já admitidas, e o
fallback podia avançar para um canal que a admissão havia recusado. O que
deliberadamente não é congelado é elegibilidade: consentimento e supressão são
relidos na escolha do próximo passo, porque um destino que morreu entre a
admissão e o prazo não pode ser endereçado. Uma linha anterior à coluna lê nulo e
cai na política publicada, para que uma migração não perca um código de
autenticação.

## Webhooks e correlação

**Correlação de rota vale apenas onde a assinatura cobre a URL.**
`IProviderWebhookInterpreter.SignatureCoversRoute` é propriedade do esquema de
assinatura de cada provedor, não configuração: a Twilio assina URL e formulário,
o SendGrid assina instante e corpo. Um par de identificadores na query de um
callback SendGrid é alegação não assinada, e honrá-la permitiria desviar um
callback genuíno para outra tentativa, mudando estado, fallback e supressão. A
resolução do attempt também passou a exigir que o provedor do callback seja o
provedor da tentativa
([`DeliveryStateApplier.cs`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Events/DeliveryStateApplier.cs)).

**A correlação poda partição.** As duas consultas de resolução ganharam limite
sobre `created_at`, a chave de partição, com a janela medida pelo relógio deste
hub e não pelo instante que o provedor declara. Sem o limite, o caminho percorrido
por todo evento de entrega sondava todas as partições existentes.

**A origem é comparada por rede.** A allowlist da aplicação passou a ser lista
CIDR comparada com `IPNetwork.Contains`, com endereço IPv4 mapeado em IPv6 tratado
como IPv4 e recusa no start para faixa que não parseia
([`WebhookRequestGuards.cs`](../../../src/Platform.Api/Modules/Dispatch/Infrastructure/Webhooks/WebhookRequestGuards.cs)).
A comparação anterior era de prefixo textual: `54.172.6` autorizava em silêncio
nove redes que ninguém listou e recusava o mesmo endereço escrito de outra forma
igualmente válida. O documento passou a declarar que a fixação de faixas de
provedor é regra de borda, e que a lista da aplicação é defesa em profundidade
desligada por padrão, porque atrás de um balanceador o endereço observado é o do
balanceador.

**O lote tem teto.** `ProviderWebhookIngestionOptions` fixa um limite de corpo por
rota e um teto de eventos por callback, com recusa inteira e `413` acima do teto.
O custo por evento é uma transação, então sem teto o tempo de resposta da única
superfície pública era escolhido por quem chama, e o provedor reentrega o que
demora. A recusa é inteira porque aceitar parte devolveria `202` sobre evidência
que este hub não guardou.

**Frescor no canal SMS.** A Twilio não envia instante e a assinatura não o cobre,
então a garantia real é a identidade do evento. A retenção da marca de
deduplicação passou a valer 60 dias, igual à janela em que a aplicação ainda
resolve um attempt, de modo que, quando a marca expira, um callback capturado já
não encontra tentativa alguma para descrever. Um teste unitário afirma a relação
entre os dois parâmetros, e o risco residual está declarado na tabela de riscos.

## Supressão

**O relato ao ledger deixou de ser melhor esforço.** O relato acontece depois do
commit por necessidade, porque relatar antes suprimiria um destino com base num
callback que acabou não aplicando nada. Fora da transação, uma falha transitória
do módulo de contatos perdia o sinal para sempre. `delivery_event.suppression_reported_at`
registra a dívida e
[`PendingSuppressionDrain`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Events/PendingSuppressionDrain.cs)
a resolve na cadência do scheduler, com idempotência garantida pela identidade da
linha de evidência. Uma recusa que o ledger declara é carimbada como concluída,
porque nenhuma repetição a muda; só a falha do módulo deixa a dívida em aberto. O
índice parcial que sustenta a varredura tem os três conjuntos escritos
literalmente, para que provar o conjunto vazio custe uma sonda.

**Token FCM separado de supressão de contato.** O documento parou de listar
`UNREGISTERED` no mesmo regime do hard bounce. Um push endereça registro de
dispositivo e não ponto de contato, e a invalidação de token viaja pelo ciclo de
vida de registro, sem `suppression.added` e sem reversão por Platform Admin com
PIM. A consequência está declarada onde o rito mensal a lê.

## Reconciliação

**O lote deixou de ser bloqueado pelo que não pode ser perguntado.** A seleção
exclui, no próprio statement, tentativas de provedor sem lookup posterior e de
notificação já encerrada
([`DeliveryReconciliationScan.cs`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Reconciliation/DeliveryReconciliationScan.cs)).
Essas linhas permanecem elegíveis pela vida da partição e, com ordenação do
silêncio mais antigo primeiro, ocupavam todas as vagas de todas as rodadas: os
canais que o job existe para corrigir nunca eram alcançados. O conjunto de
provedores respondíveis passou a ser fato de composição publicado pelo resolvedor
(`IProviderDeliveryLookupResolver.AnswerableProviderKeys`), porque resolver uma
chave por vez responde tarde demais para um lote limitado.

**O statement virou busca.** A junção carrega a janela de criação, e um índice
parcial sobre `COALESCE(status_changed_at, created_at)` filtrado por
`status IN ('sent','unknown') AND provider_key IS NOT NULL` foi criado na migração
`AddDeliverySuppressionReportState`. Um teste de plano lê o `EXPLAIN` do comando
que o código realmente envia, capturado por interceptor, e prova a falsificação
derrubando o índice.

## Configuração de provedor e resiliência

**Produto Twilio.** O padrão passou a ser `ProgrammableMessaging`, que é o único
ramo que aceita Messaging Service, `StatusCallback` por mensagem e
`ValidityPeriod`. O Verify envia o código e não reporta nada, o que deixaria o
tracker sem evento para encerrar a notificação, com falha silenciosa: o envio
funciona e a confirmação nunca chega. `RequireDeliveryFeedback`, desligada por
padrão, faz o host recusar subir com produto Verify, callback vazio ou sem
Messaging Service, e é a chave que o ambiente de produção liga.

**Orçamento até o SMS de fallback.** Os dois termos configuráveis foram derivados
do aceite de 35 s: intervalo de varredura de 2 s e timeout do provedor de SMS de
2 s, que é o valor que o §11.3 manda para `critical`. A soma no pior caso fica em
34,8 s. Uma asserção de orçamento em teste unitário reprova quando qualquer um dos
dois sobe sozinho, que é a única contraprova executável desse caminho.

**Validação de opções alcança o aninhado.** `NestedOptionsValidation` valida as
entradas por provedor do token bucket e as opções aninhadas de circuit breaker.
Antes, os atributos de faixa em valor de dicionário nunca eram avaliados, e uma
taxa contratada de zero chegava ao script Lua, onde a falha se agrava porque o
limitador degrada para fail-open.

**Polly declarado.** `Polly.Core` ganhou `PackageVersion` no gerenciamento central,
porque três provedores tipam diretamente sobre a exceção de circuito aberto e um
bump de patch do pacote de resiliência trocaria a versão sob esse código sem diff.

**Fuso inválido não lança no estágio Policy.** `QuietHoursRule` resolve o fuso com
`TryFindSystemTimeZoneById` e cai no padrão da plataforma, registrando na evidência
o valor declarado ao lado do resolvido. O contrato do estágio é produzir decisão
auditável, não propagar exceção.

## Medição

**Os dois orçamentos do caminho de entrega deixaram de ser afirmação.** O
projeto de performance ganhou o modo `delivery`, que responde às duas perguntas
que o desenho fazia e nenhum teste media.

A primeira é quanto uma rodada do scheduler leva para achar as tentativas com
prazo vencido. Ela semeia notificações e tentativas no volume pedido, com as
vencidas como minoria rara, porque uma varredura cujas linhas casam com
frequência não prova nada sobre o plano que ela recebe em produção; depois mede
a rodada com percentis e lê o plano de execução junto. É o único termo do prazo
até o SMS de fallback que cresce com a retenção: os saltos de fila, o estágio
Core e a chamada ao provedor são orçamento fixo, e esses já eram guardados pela
asserção de orçamento no teste unitário.

A segunda é quanto custa ingerir um callback, em cada tamanho de lote até o teto
que a rota aceita, com o payload selado uma vez por callback como o handler
sela. A mesma célula roda nas duas formas de transação, uma por evento, que é o
que a produção faz, e uma por lote, que é a alternativa. A diferença entre as
duas linhas é o valor de mudar a forma, em milissegundos, em cada tamanho, o que
transforma a avaliação que o desenho deixou em aberto numa comparação com
número.

Os statements e o envelope são transcritos no projeto de performance em vez de
importados, porque a sonda é deliberadamente não amiga do assembly da API, que é
a mesma decisão que o cenário do relay já carrega. Correção continua sendo
responsabilidade das asserções de plano na suíte de integração, que leem o
comando que o código realmente envia; a sonda mede custo.

O que fica fora das duas, e o registro diz: TLS, verificação de assinatura,
pipeline HTTP e os saltos de fila. Nenhum cresce com volume ou com lote, e
medi-los exige host e cliente, que é o gate de carga contra ambiente real.

## O que a medição descobriu

**A ingestão do webhook é quadrática no tamanho do lote.** Não é achado de
nenhuma das duas revisões: apareceu quando o instrumento novo mediu a série por
tamanho de lote e a série recusou-se a fazer sentido como caminho linear. A
causa está no código e não na bancada, e é derivável sem medir: o handler sela o
corpo inteiro do callback uma vez, e o escritor grava esse mesmo corpo selado em
cada linha de evento, porque a evidência de um evento do lote é o lote. Um
callback de N eventos, cujo corpo cresce com N, grava N cópias de um corpo de
tamanho N.

A conta, a 420 bytes por evento: 1 MiB num lote de cinquenta, 16 MiB em duzentos,
100 MiB em quinhentos, por requisição.

O teto que esta remediação tinha introduzido era de quinhentos eventos, escolhido
antes da medição e sem evidência nenhuma. Ele foi corrigido para duzentos, que é
uma decisão declarada como tal: a aritmética é certa, o teto certo não é, e ele
pertence ao gate de carga com corpos reais. O que removeria a questão em vez de
limitá-la é guardar o corpo uma vez e referenciá-lo das linhas de evento, o que é
mudança do modelo de evidência e está fora do escopo desta remediação.

## Portão executável e provas

**O portão alcança autenticação fora de `critical`.** A fonte do portão passou a
contar também as classes que hospedam modelo publicado com finalidade de
autenticação, porque o resto do desenho trata classe crítica e fluxo de
autenticação como uma unidade. Uma política `transactional` de um passo
hospedando os códigos de login passava sem reprovar.

**Docker deixa de omitir onde a omissão é mentira.** `NOTIFICATIONHUB_REQUIRE_DOCKER`
transforma daemon ausente em reprovação, e um teste guarda a própria condição,
porque uma asserção dentro de um teste omitido é omitida com ele. Sem a variável,
a estação de trabalho continua omitindo com motivo declarado.

**Vetor de assinatura independente.** O teste do SendGrid ganhou chave pública,
corpo, instante e assinatura produzidos fora deste repositório, verificados sem
chamar o auxiliar de assinatura da suíte, com duas falsificações: um byte do corpo
e o instante assinado. Sem ele, uma mudança que tocasse verificador e auxiliar
juntos manteria a suíte verde e recusaria todo callback real.

## Registro

O [documento da fase](../../fases/fase-2-resiliencia-e-sms.md) passou a declarar:
`202 Accepted` na rota e o orçamento de 20 ms como alvo por evento sem medição; os
cinco índices parciais com predicado literal; a forma completa do circuit breaker,
incluindo a precondição de volume; o retry do §8 como dependência de infraestrutura
não entregue; as seis colunas que a fase criou; a extensão da ADR-0011 pela
ADR-0015 e as três partes da ADR-0014; as três seções ausentes do relatório mensal
com suas duas causas distintas; a dependência da fase 1b corrigida para sete
perguntas com a oitava fechada pela F2-3; a entrega da F2-3 no escopo e na tabela
de status; o kill switch automático desligado por padrão e o fail-open do
limitador na tabela de riscos; o caos como pendência atribuída; e os dois limites
do portão executável.
