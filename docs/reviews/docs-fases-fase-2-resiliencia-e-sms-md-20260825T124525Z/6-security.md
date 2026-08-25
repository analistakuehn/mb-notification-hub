---
language: pt-BR
---

# Segurança

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## SEC-001: a allowlist de IP dos provedores não pode funcionar na topologia que o próprio documento fixa

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `src/Platform.Api/Modules/Notifications/Infrastructure/Authentication/ProviderSignatureAuthenticationHandler.cs`
- `line`: `60`
- `evidence`: O documento lista `allowlist de IP dos provedores` entre os controles desta fase (linha 123) e afirma na linha 124 que `Os webhooks são a única superfície pública do hub: ALB público somente com WAF, allowlist de IP e TLS`. O handler passa ao verificador `Context.Connection.RemoteIpAddress?.ToString()`, e `grep -rn` por `ForwardedHeaders`, `UseForwardedHeaders`, `KnownProxies` e `X-Forwarded` em `*.cs`, `*.json` e `Dockerfile*` não devolve uma única ocorrência no repositório. Atrás do ALB exigido, esse endereço é o do balanceador. Os dois estados possíveis são ruins: com a lista vazia, que é o valor entregue (`TwilioWebhookOptions.cs:30` registra que vazio desliga a allowlist; idem `SendGridWebhookOptions.cs:47`), o controle não existe; com a lista preenchida com as faixas dos provedores, `IsOriginAllowed` recusa todo callback autêntico, e a recusa é justamente a que dispara alarme de forjaria. O mesmo handler compensa o balanceador para a URL assinada, via `PublicBaseUrl`, e não para o endereço.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: O controle prometido a Compliance e ao escopo de pentest não é aplicável como descrito. Ligá-lo derruba todo o feedback de entrega dos dois provedores e enche o alarme de segurança de falso positivo, o que na prática força mantê-lo desligado; deixá-lo desligado invalida a linha 62 (`delivered ou bounce de origem fora da allowlist não produz efeito e gera alarme de segurança`) e o tratamento do risco 24 na linha 211. Sobram assinatura e WAF, e o WAF pertence à unidade de infraestrutura pendente.
- `recommendation`: Decidir explicitamente onde a origem é fixada. Se na borda, remover a allowlist da lista de controles da aplicação e dizer que ela é regra de ALB ou WAF. Se na aplicação, adicionar `ForwardedHeaders` com `KnownProxies` restrito e ler o endereço do cliente daí, com teste que prove a recusa com e sem proxy.
- `verification`: Subir o host atrás de qualquer proxy local, configurar `AllowedIpPrefixes` com a faixa do provedor e enviar um callback assinado válido. Recusa por origem não permitida confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-specialist`
- `dissent`: O `dotnet-specialist` graduou `MEDIUM`, lendo o defeito como controle entregue inerte por default. A consolidação preserva o `HIGH` do `dotnet-architect`: a ausência total de `ForwardedHeaders` significa que o controle não é apenas desligado, é inaplicável na topologia que o documento exige, e ligá-lo quebra a entrega.

## SEC-002: a replay protection é descrita como composta e no canal SMS a janela nunca engaja

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `47`
- `evidence`: A linha 47 afirma `Replay protection por provider_event_id mais janela de timestamp (§10.2 A5)`, e a linha 123 repete o par entre os controles de A5. Para o SendGrid a janela é obrigatória e o código a impõe, com o motivo registrado no próprio arquivo: como o timestamp está dentro do payload assinado, um callback capturado permanece criptograficamente válido para sempre. Para a Twilio o código declara o contrário: `TwilioWebhookOptions.cs:40` registra que o status callback não envia timestamp e que a janela é ignorada quando o campo está ausente, e `TwilioWebhookInterpreter.cs:67` implementa isso com `if (declaredTimestamp is not null && !IsWithinWindow(...))`. A assinatura HMAC-SHA1 da Twilio cobre URL e parâmetros e não cobre instante. A única defesa restante é a marca de dedupe, apagada por idade: `ProviderEventDedupePurgeOptions.cs:22` fixa `Retention = TimeSpan.FromDays(30)`. A linha 117 descreve `PROVIDER_EVENT_DEDUPE` sem mencionar retenção nem purga.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um callback de SMS capturado permanece válido indefinidamente e volta a ser aceito na fronteira depois de 30 dias, e o documento não permite a ninguém saber disso. O efeito a jusante hoje é limitado pela máquina de estados, que ignora transição inválida, e pela ordem do relato de supressão, mas essa contenção é acidental e não o controle declarado, e o parâmetro que define a garantia real, a retenção, não está registrado em decisão nenhuma.
- `recommendation`: Declarar por provedor o que compõe a replay protection, registrar a retenção de 30 dias como o alcance efetivo da garantia no canal SMS, e avaliar assinar a URL de callback com um parâmetro de instante verificável, que é a única forma de dar frescor a um provedor que não envia timestamp.
- `verification`: Reenviar um callback Twilio assinado após expirar a marca de dedupe, ou com a purga forçada, e observar se a rota volta a aceitá-lo. Aceitação confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`
- `dissent`: O `dotnet-engineer` graduou `LOW`, argumentando que o dano fica confinado à evidência porque a máquina de estados não se move e a supressão só é relatada depois de transição comitada. A consolidação preserva `MEDIUM`: a evidência é o que a rota de reconstrução publica a Compliance, e a contenção citada não é o controle declarado.

## SEC-003: controles apresentados como ativos que a base entrega desligados ou com fail-open não declarado

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Security`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `127`
- `evidence`: A linha 127 afirma `Kill switch por canal: ... acionamento humano, ou automático se o circuito ficar aberto por mais de 10 min (§10.3)`, e a linha 172 credita à fatia concluída `rate limit por provedor, kill switch automático de canal e pool de sender por aplicação`. O código entrega o automático desligado, por decisão registrada em `AutomaticChannelKillSwitchOptions.cs:24`, com dois riscos que o documento não expõe: o circuito é observado por processo enquanto o kill switch é global, então uma instância degradada para o canal da frota inteira; e SMS é o último passo do plano de entrega, então parar esse canal deixa códigos de autenticação esperando até expirar. O limite de 10 min existe (`OpenCircuitWindow`), mas nunca é avaliado com o gate desligado. O limitador de taxa por provedor degrada para fail-open, registrado no próprio logger: com o Redis indisponível o envio segue sem limite, e a compensação declarada é o kill switch manual. A decomposição registra o gate desligado; a fase não.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: O documento é lido por Compliance e por Produto como inventário de controles em vigor. Dois controles de contenção de abuso e de custo estão declarados como ativos quando um está desligado por padrão, com risco aceito que o documento não expõe, e o outro deixa de existir exatamente quando a dependência que ele usa cai. Nenhum dos dois aparece na tabela de riscos da fase.
- `recommendation`: Marcar o kill switch automático como desligado por padrão, com o trade-off registrado e o dono da decisão de ligar. Declarar o fail-open do limitador por provedor com a compensação na tabela de riscos, no mesmo idioma que a ADR-0011 já usa para o fail-open do dedupe.
- `verification`: Subir o host sem configuração adicional e forçar circuito aberto por mais de 10 min: canal seguir ativo confirma o gate desligado. Para o segundo, derrubar o Redis de rate limit e observar se o envio prossegue.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-specialist`
- `dissent`: O `dotnet-specialist` classificou o achado na lente de engenharia. A consolidação atribui a lente de segurança, porque o defeito é de inventário de controles e não de coerência interna.

## Verificado sem achado nesta lente

A rota de webhook é autenticada e limitada como qualquer rota que muda estado, sem carve-out no teste de segurança, e o particionamento do rate limit é por identidade provada. A verificação da Twilio usa comparação de tempo fixo com `CryptographicOperations.FixedTimeEquals`, com HMAC-SHA1 imposto pelo provedor e justificativa registrada; a do SendGrid impõe a janela de timestamp e recusa chave ilegível. O payload bruto é selado antes de virar evidência e nunca é legível na linha nem na resposta. A supressão automática confere com a ADR-0015 e com a linha 62 (e-mail na primeira recusa, demais canais duas ocorrências em sete dias) e é reversível com carimbo e ator, não por apagamento. O evento de saída existe com o nome documentado. O relato de supressão só ocorre depois de a transição comprometer, o que impede supressão por callback não correlacionado. Mensagens de erro de provedor são sanitizadas antes de virar código de erro. SMS de autenticação sem link tem recusa de motivo próprio preservada no caminho de fallback.
