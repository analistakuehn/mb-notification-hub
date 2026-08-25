---
language: pt-BR
---

# Qualidade do .NET

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## STK-001: a validação de opções não alcança os limites aninhados do token bucket

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `32`
- `evidence`: O token bucket depende de `ProviderRateLimitOptions.PerProvider`. Os atributos `[Range]` estão em `ProviderRateLimit.PermitsPerSecond` e `BurstSeconds`, mas a coleção aninhada não declara validação recursiva. O registro usa `ValidateDataAnnotations()` apenas no objeto externo. Valores aninhados inválidos podem alcançar o script Lua; `ProviderRateLimiter` absorve falha operacional e segue em fail-open.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Uma configuração inválida pode desativar o limite contratado no runtime, exceder MPS, aumentar throttling e atrasar SMS.
- `recommendation`: Validar explicitamente cada entrada de `PerProvider`, incluindo chave, `PermitsPerSecond` e `BurstSeconds`, e falhar no startup. Aplicar validação equivalente às opções aninhadas do circuit breaker.
- `verification`: Construir o host com limites iguais a zero e comprovar falha em `ValidateOnStart`; uma configuração válida deve inicializar normalmente.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`

## STK-002: o endpoint publica 202 e o documento declara 200

- `severity`: `LOW`
- `confidence`: `HIGH`
- `lens`: `.NET Quality`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `48`
- `evidence`: A linha 48 fixa resposta HTTP 200. `ReceiveProviderWebhook.Endpoint` devolve `Results.Accepted()`, código 202, e os testes esperam `HttpStatusCode.Accepted`.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Monitores, testes externos ou regras de borda derivados do documento podem exigir o código incorreto. O 202 também expressa melhor que o evento foi aceito para processamento assíncrono.
- `recommendation`: Corrigir o documento para `202 Accepted` ou escolher deliberadamente 200 e alinhar endpoint e testes.
- `verification`: Fixar o código escolhido em teste de contrato da rota e na documentação operacional.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`
- `dissent`: O comprovante classificou o ponto em engenharia. A consolidação o colocou em `STK`, como contrato da API .NET, para manter identidade comparável com o pacote anterior.
