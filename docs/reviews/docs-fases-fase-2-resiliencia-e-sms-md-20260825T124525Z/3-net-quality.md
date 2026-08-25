---
language: pt-BR
---

# Qualidade do .NET

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## STK-001: código HTTP declarado difere do publicado

- `severity`: `LOW`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `48`
- `evidence`: A linha 48 fixa `resposta 200 em menos de 20 ms` e a linha 131 repete o código. O endpoint devolve `Results.Accepted()` em [`ReceiveProviderWebhook.Endpoint.cs:53`](../../../src/Platform.Api/Modules/Notifications/Features/DeliveryTracking/Webhooks/ReceiveProviderWebhook.Endpoint.cs), e o teste de ponta a ponta confere `HttpStatusCode.Accepted` em `PushToSmsFallbackTests.cs:258`.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Pequeno em produção, porque os dois provedores aceitam qualquer 2xx, mas o documento é o desenho de registro da única superfície pública do hub e é a fonte de um teste de contrato, de um monitor sintético ou de uma regra de WAF. O `202` também carrega a semântica de recebimento sem aplicação que o resto da seção descreve.
- `recommendation`: Corrigir para `202 Accepted` nas linhas 48 e 131.
- `verification`: Um POST assinado válido contra a rota responde `202`.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`
- `dissent`: `dotnet-architect` e `dotnet-specialist` classificaram o achado na lente de engenharia. A consolidação atribui a lente da stack, onde o `dotnet-engineer` o colocou, porque o defeito é de contrato de API e não de coerência do documento.

## STK-002: a allowlist de origem compara prefixo textual em vez de rede IP

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `src/Platform.Api/Modules/Dispatch/Infrastructure/Webhooks/WebhookRequestGuards.cs`
- `line`: `36`
- `evidence`: A comparação é `if (prefix.Length > 0 && remoteIpAddress.StartsWith(prefix, StringComparison.Ordinal))`, com a configuração documentada em `TwilioWebhookOptions.cs:28` como prefixos textuais de endereço, por exemplo `54.172.60.`. O projeto tem alvo `net10.0`, onde `System.Net.IPNetwork` e `IPAddress.TryParse` estão disponíveis.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Raiz distinta de SEC-001, que trata de qual endereço é lido. Aqui o defeito é a comparação: um prefixo `54.172.6` autoriza silenciosamente de `54.172.60.x` a `54.172.69.x`; um par IPv6 mapeado (`::ffff:54.172.60.1`) nunca casa e é recusado como forjado; e qualquer variação de forma textual do endereço quebra a decisão.
- `recommendation`: Tipar a configuração como lista de CIDR, comparar com `IPNetwork.Contains(IPAddress)` e recusar na inicialização um valor que não parseia, em vez de aceitar qualquer string.
- `verification`: Teste de unidade com allowlist `["54.172.6"]` e origem `54.172.69.7`. Se passar, a comparação não é de rede.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## STK-003: `FindSystemTimeZoneById` sobre dado ingerido, sem guarda, no estágio Policy

- `severity`: `MEDIUM`
- `confidence`: `MEDIUM`
- `lens`: `.NET Quality`
- `file`: `src/Platform.Api/Modules/Notifications/Features/Pipeline/Rules/QuietHoursRule.cs`
- `line`: `50`
- `evidence`: A regra faz `var timezone = TimeZoneInfo.FindSystemTimeZoneById(recipient.Timezone);` direto sobre o snapshot do destinatário, que é dado ingerido. O método lança `TimeZoneNotFoundException` ou `InvalidTimeZoneException` para identificador desconhecido, e o caminho não tem guarda. O mesmo repositório usa a forma sem exceção onde valida entrada: `DeclareContactPoints.Validator.cs:74` faz `TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out _)`. Consultas malsucedidas não entram no cache estático de `TimeZoneInfo`, então um identificador inválido paga sondagem e exceção a cada notificação. Nota menor no mesmo arquivo: a linha 109 monta `new DateTimeOffset(releaseLocal, timezone.GetUtcOffset(releaseLocal))` sobre horário local de `Kind` indefinido, o que é ambíguo em transição de horário de verão.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: A exposição hoje é baixa, porque os dois caminhos de entrada de contato passam pelo mesmo validador e a imagem do worker traz ICU. Permanece latente para linhas gravadas antes do validador, para mudança de base de imagem e para `InvariantGlobalization`, e o efeito é exceção no estágio Policy de toda notificação `operational` daquele destinatário, exatamente a classe que esta fase ativa. O contrato do estágio é produzir decisão auditável, não propagar exceção.
- `recommendation`: Usar `TryFindSystemTimeZoneById` e recusar com evidência na avaliação da regra, ou cair no fuso padrão do perfil.
- `verification`: Submeter uma `operational` cujo destinatário tenha `Timezone` inexistente e observar o desfecho: mensagem em retry ou DLQ confirma o comportamento.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-specialist`
- `dissent`: O `dotnet-architect` graduou `LOW`, argumentando que os dois caminhos de ingestão já validam o fuso. A consolidação preserva o `MEDIUM` do `dotnet-specialist`, porque o dado é externo ao módulo que o consome e a classe afetada é a que esta fase ativa.

## STK-004: a semântica declarada do circuit breaker omite a precondição que decide se ele abre

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `32`
- `evidence`: A linha 32 afirma `Circuit breaker Polly por provedor (abre com 50 % de erro em 30 s ...)`. A forma está expressa em `DispatchProviderSetup.cs:138` (`builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { FailureRatio, SamplingDuration, MinimumThroughput, BreakDuration })`) com os defaults de `ProviderCircuitBreakerOptions`: `FailureRatio = 0.5` e `SamplingDurationSeconds = 30`, ambos conforme o documento. O que o documento não diz é `MinimumThroughput = 10`: no breaker v8 o circuito só abre se a janela de amostragem tiver ao menos esse número de chamadas. Some-se `BreakDurationSeconds = 15`, enquanto o kill switch da linha 127 exige circuito aberto por mais de 10 min.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: A linha 29 restringe SMS à classe `critical` como fallback, então o canal onde o breaker mais importa é justamente o de menor probabilidade de acumular dez chamadas em trinta segundos. E com o circuito fechando para meia abertura a cada 15 s, a janela de 10 min do kill switch só é atingida por reaberturas encadeadas, que qualquer chamada passante encerra. Um leitor conclui que o canal SMS tem proteção automática de circuito e de kill switch; na configuração entregue as duas dependem de volume que o próprio desenho impede.
- `recommendation`: Acrescentar `MinimumThroughput` e `BreakDuration` à descrição e calibrar os dois contra o volume real de SMS.
- `verification`: Falhar nove chamadas SMS consecutivas em trinta segundos e observar que o circuito não abre.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## STK-005: Polly é consumido como dependência direta de compilação sem declaração de pacote

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `Directory.Packages.props`
- `line`: `5`
- `evidence`: Não existe `PackageVersion` de `Polly` em `*.csproj` nem em `*.props` (grep executado, zero resultados), e `Platform.Api.csproj` declara apenas `Microsoft.Extensions.Http.Resilience`. Ainda assim o código faz `using Polly.CircuitBreaker;` em `TwilioChannelProvider.cs:9`, `SendGridChannelProvider.cs:7` e `FcmChannelProvider.cs:7`, tipando diretamente sobre a exceção de circuito aberto. `Directory.Packages.props:5` liga `CentralPackageTransitivePinningEnabled`, que só fixa transitivos com entrada correspondente, e Polly não tem entrada.
- `evidence-kind`: `executed`
- `introduced-by-diff`: `false`
- `impact`: Um bump de patch do pacote de resiliência pode trocar a versão de Polly sob código que a consome por tipo, sem diff e sem que `TreatWarningsAsErrors` perceba. O documento nomeia o circuit breaker Polly como o mecanismo desta fase sem que o repositório declare o pacote de que ele depende.
- `recommendation`: Acrescentar `PackageVersion` explícito de `Polly.Core` ao gerenciamento central de pacotes.
- `verification`: `dotnet list package --include-transitive` filtrado por Polly, comparado com `Directory.Packages.props`.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## STK-006: o timeout é propriedade por provedor e o §11.3 o exige por classe

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/Twilio/TwilioOptions.cs`
- `line`: `149`
- `evidence`: `TwilioOptions.TimeoutSeconds = 5` é propriedade por provedor, e `FcmOptions` e `SendGridOptions` fazem igual. O §11.3 do design de sistema manda timeout curto de 2 s para `critical` e 5 s para as demais. A classe não chega a esse ponto de decisão, porque `DispatchProviderSetup` compõe o pipeline por provedor e não por fila. Como a linha 29 do documento restringe SMS a `critical`, o canal inteiro roda no valor de `demais`. Nota de contexto: não há pacote `Twilio` no revision, e `ValidityPeriod` e `StatusCallback`, que as linhas 27 e 140 citam como se fossem superfície de SDK, são campos de formulário montados à mão em `TwilioChannelProvider.BuildMessageForm`.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: A regra por classe do design não é expressável sem mudar a forma das opções, e o documento não registra o desvio. O valor entregue também alimenta PRF-004: os 5 s do canal SMS são parte do estouro do aceite de 35 s do §11.6.
- `recommendation`: Registrar no documento que o timeout é por provedor e ajustar o valor do canal SMS ao orçamento de `critical`.
- `verification`: Procurar qualquer binding de opções de provedor que discrimine `critical`. A ausência confirma o achado.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## Verificado sem achado nesta lente

A correspondência entre a forma de configuração em camelCase e os tipos C# é literal e está correta: `ClassPolicyValidation.cs:234` e `:378` leem `"deliveryPlan"` e `"quietHours"` por nome, e os tipos são `DeliveryPlanStep(Channel Channel, TimeSpan? Timeout)` e `QuietHoursWindow(TimeOnly From, TimeOnly To)` em `ClassPolicyDefinition.cs:11` e `:14`, com os campos em `:31` e `:40`. `RenderedMessage` discrimina `SmsMessage(string Body)` como o documento afirma (`Dispatch/Integration/V1/RenderedMessage.cs:30`), e `git log -S` confirma que o contrato veio da fundação e não desta fase. O token bucket em Redis é atômico por script Lua (`ProviderRateLimiter.cs`), a normalização NFC do canal SMS existe (`SmsContentNormalizer.cs:62`), e os workers usam `PeriodicTimer` como o §11.7 exige.
