---
language: pt-BR
---

# Arquitetura

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## ARC-001: duas ADRs aceitas que governam esta fase estão fora das listas de fontes, e o documento nega o desvio de uma terceira

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `161`
- `evidence`: A linha 161 afirma `Nenhum desvio das ADRs relacionadas (0001, 0008, 0011): o adapter SMS é plugin, a entrega permanece at-least-once com idempotência em camadas e a política segue como configuração de classe`. A [ADR-0015](../../ADR-0015-regra-de-supressao-no-estagio-policy.md), que decide o ponto de aplicação da supressão descrita nas linhas 60 a 66, registra o oposto na primeira consequência negativa: a lista de regras da v1 deixa de ser a lista da ADR-0011, e este é o primeiro uso do nível 3, consumindo parte do orçamento que aquela ADR estabeleceu para si mesma. A ADR-0015 não aparece uma única vez no artefato. A [ADR-0014](../../ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md) aparece só no corpo, na linha 179, e não em `Fontes` nem em `Referências`; a ADR-0001 é citada apenas como `0001` na linha 161 e não consta de nenhuma das duas listas. A linha 179 diz `a segunda também em ADR-0014`, mas a ADR-0014 decide três partes, e a terceira, o roteamento do gatilho para `core-auth`, está implementada (`DispatchMessages.cs:45` e `NotificationPlanOutcome.cs:126` passam `notification.AuthFlow`) e não registrada aqui.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: O leitor que segue as listas de fontes não chega às duas decisões que mais afetam o comportamento desta fase, e recebe uma afirmação de conformidade com a ADR-0011 que a ADR-0015 já contradisse por escrito. Isso corrói a função de registro do documento: a próxima fase decide sobre um mapa de ADRs incompleto, e o contador de orçamento de regras que a ADR-0015 abriu não aparece em lugar nenhum.
- `recommendation`: Incluir ADR-0001, ADR-0014 e ADR-0015 em `Fontes` e `Referências`; reescrever a linha 161 para declarar a extensão da ADR-0011 com a justificativa da ADR-0015 em vez de negá-la; corrigir a atribuição da linha 179 e registrar a terceira parte.
- `verification`: Abrir a ADR-0015 e ler a primeira consequência negativa. Se ela declara desvio da ADR-0011, a linha 161 está errada.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`

## ARC-002: o fallback rederiva o plano da política publicada corrente, não do plano sob o qual a notificação foi admitida

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs`
- `line`: `106`
- `evidence`: O handler faz `Result<PublishedClassPolicy> policy = await catalog.FindClassPolicyAsync(notification.Application, notification.Class, cancellationToken)` e em seguida `DeliveryPlanStep? nextStep = NextStep(policy.Value!.Definition.DeliveryPlan, failedAttempt.Channel)`, ou seja o plano bruto publicado. Na admissão o plano usado é o filtrado: `ChannelSelectionRule.cs:62` grava `context.DeliveryPlan = survivingPlan`, computado por `policy.DeliveryPlan.Where(...)` sobre `RemainingChannels`, canais com conteúdo e canais alcançáveis, e é dele que sai o prazo (`RouteStage.cs:24` e `:39`). Esse plano filtrado vive apenas no contexto em memória e nunca é persistido. `Notification.PolicyVersion` existe e é gravado (`Domain/Notification.cs:73`, `NotificationConfiguration.cs:51`), mas `IPublishedCatalog.FindClassPolicyAsync` não recebe versão. O handler reconfere alcance (`contactPointId is null`) e conteúdo (falha de render) e não reconfere `ChannelsAllowed`, `ConsentGate` nem `SuppressionGate`.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Duas consequências concretas. Num plano de três passos, que é o formato que a fase 3 introduz ao somar WhatsApp, um canal recusado na admissão por falta de opt-in ou por supressão total dos endereços continua sendo o próximo passo escolhido pelo fallback, e a trilha da notificação conterá uma avaliação de política dizendo que aquele canal estava suprimido junto de uma tentativa de envio por ele: é o controle de reputação de remetente que a ADR-0015 existe para instalar, furado no caminho que esta fase adiciona. Segunda, a linha 139 define ativação e rollback como republicação de versão da política; com o plano lido da versão corrente, uma republicação altera o plano de notificações em voo, e o rollback declarado como seguro muda o comportamento de mensagens já admitidas.
- `recommendation`: Persistir o plano admitido, ou ler a política pela `policy_version` já gravada, e fazer o handler avançar sobre esse plano. Se a decisão for manter a leitura corrente, reexecutar as regras de elegibilidade de canal antes de enfileirar o próximo passo e registrar a escolha no documento.
- `verification`: Teste de integração com plano publicado de push com 30 s, e-mail e SMS, endereços de e-mail do destinatário todos suprimidos, push aceito sem evento e prazo vencido. Tentativa seguinte de e-mail confirma o achado. Para a segunda consequência, republicar a política entre a admissão e o vencimento do prazo e observar qual plano o handler usa.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## ARC-003: o documento fixa Programmable Messaging e a configuração entregue seleciona Twilio Verify com callback vazio

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `src/Platform.Api/appsettings.json`
- `line`: `88`
- `evidence`: A linha 27 do documento fixa `Implementação de IChannelProvider para SMS sobre Twilio Messaging (§4.3): Messaging Service por application, com sender pool de número longo ou short code BR; StatusCallback por mensagem apontando para /webhooks/twilio; ValidityPeriod igual ao TTL restante da notificação`. O código traz `public TwilioSmsProduct Product { get; init; } = TwilioSmsProduct.Verify;` em `TwilioOptions.cs:48`, e o `appsettings.json` base traz `"Product": "Verify"` com `"MessagingServiceSid": ""` e `"StatusCallbackUrl": ""`. No ramo Verify a requisição montada é `https://verify.twilio.com/v2/Services/{ServiceSid}/Verifications` com apenas `To`, `Channel` e `CustomCode` (`TwilioChannelProvider.cs:106`): sem `StatusCallback`, sem `ValidityPeriod`, sem `MessagingServiceSid`, que só entram no ramo `ProgrammableMessaging`. Mesmo no ramo correto o callback só é anexado quando há URL configurada (`:176`).
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Na configuração entregue o SMS não produz retorno de entrega nenhum, então o encerramento por webhook assinado que a linha 195 apresenta como provado não se reproduz em produção, o `ValidityPeriod` que a linha 140 chama de segunda barreira de TTL não existe, e o pool de sender por aplicação que a linha 172 declara concluído não é usado. A seção de estado nomeia como pendente apenas o sender BR e o Messaging Service, então a unidade de infraestrutura derivada deste documento não recebe duas configurações obrigatórias, e a falha é silenciosa: o envio funciona, a confirmação nunca chega.
- `recommendation`: Registrar no documento que o produto Twilio é decisão de configuração com dois contratos de entrega distintos, declarar `Product = ProgrammableMessaging` e `StatusCallbackUrl` como itens da unidade de infraestrutura ao lado do Messaging Service, e considerar validação de inicialização que recuse subir o papel de SMS em produção com `Verify` ou com callback vazio.
- `verification`: Subir o host com o `appsettings.json` entregue, despachar um SMS e observar a URL chamada. Um POST para `verify.twilio.com/.../Verifications` confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`
- `dissent`: O `dotnet-specialist` descreveu `ValidityPeriod` e `StatusCallback` como o caminho ativo, tendo lido o adapter e não a configuração. A consolidação executou a verificação e confirmou o achado do `dotnet-architect`: não é divergência de leitura, é lacuna de escopo do recibo do especialista.

## ARC-004: a seção de dados e persistência não lista nenhuma coluna que a fase criou

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `113`
- `evidence`: A seção enumera `fallback_deadline`, `release_at`, `PROVIDER_EVENT_DEDUPE`, `delivery_event` e três índices. A fase adicionou, além disso, `notification_attempt.plan_advanced_at` (o claim da ADR-0014), `notification_attempt.fallback_requested_at`, `notification_attempt.status_changed_at` e `notification.auth_flow`, visíveis em `20260825010029_AddSchedulerScanState.cs:70`, `:82`, `:90` e `:96` e em `NotificationAttemptConfiguration.cs:75`, cujos índices parciais filtram `plan_advanced_at IS NULL AND fallback_requested_at IS NULL`. A própria linha 181 descreve a correção como `claim de estado por passo do plano` sem nomear onde ela vive.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Quem planeja a fase 3 ou uma migração lê a seção de dados como o inventário do modelo e não encontra o mecanismo que impede SMS duplicado, nem a coluna que impede a varredura de reemitir gatilho, nem a janela de reemissão associada. É o tipo de omissão que faz uma fatia posterior reintroduzir o defeito que a ADR-0014 fechou.
- `recommendation`: Completar a seção com as quatro colunas, o predicado literal dos índices parciais que dependem delas e a janela de reemissão de gatilho.
- `verification`: Comparar a seção com o diff das migrações do módulo entre a fundação e o fechamento da fase. Qualquer coluna nova ausente da seção confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## Verificado sem achado nesta lente

A decisão de fronteira de manter o Delivery Tracking dentro do módulo Notifications continua sustentada e não há atrito de fronteira observável que a contradiga: a slice existe com as quatro sub-slices previstas (`Features/DeliveryTracking/{Webhooks,Events,Scheduling,Reconciliation}`), o tracker consome os módulos vizinhos só por superfície publicada, e a fase não criou módulo novo. A regra de dependência por `Modules.<outro>.Integration.V1` que a linha 109 cita confere com `TemplateManagement/AGENTS.md` e está codificada sem carve-out em `Platform.ArchTests/ArchitectureTests.cs`. O fluxo de fallback sem chamada direta entre componentes, descrito nas linhas 38 e 110, confere no trecho verificado. A topologia de filas da linha 30 confere com `DispatcherWorkerRole.cs:107`. O ponto de aplicação da supressão é de fato regra do estágio Policy, ao lado de consentimento, dedupe e janela de silêncio. Nada da lista de `Fora de escopo` apareceu no código desta fase. A afirmação de verificação de fonte da linha 55 sobre `SKIP LOCKED` é exata: o §11.3 especifica a varredura do scheduler com `LIMIT` e `SKIP LOCKED`, e a forma literal com `LIMIT 100` está no bloco do Outbox Relay, como o documento diz.
