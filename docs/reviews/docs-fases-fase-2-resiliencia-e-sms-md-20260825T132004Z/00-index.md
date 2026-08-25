---
language: pt-BR
target: docs/fases/fase-2-resiliencia-e-sms.md
scope: bounded-file
reviewed-on: 2026-08-25T13:20:04Z
reviewed-via: dotnet-code-review
severity: high
status: open
---

# Revisão de código de `fase-2-resiliencia-e-sms.md`

## Resultado

`FINDINGS`

A revisão consolidou 11 achados: três de severidade alta, sete de severidade média e um de severidade baixa. As seis lentes foram cobertas pelos três revisores obrigatórios em contextos independentes: `dotnet-architect`, `dotnet-engineer` e `dotnet-specialist`.

## Escopo e identidade da evidência

- Alvo: [`docs/fases/fase-2-resiliencia-e-sms.md`](../../fases/fase-2-resiliencia-e-sms.md)
- Revisão-fonte: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- Objeto Git do alvo: `37bc47f8d2f32579d2c740e06b10861054513c7e`
- SHA-256 do conteúdo: `7856D7930244FAC4FC738A0B0650DE50E46FFF1C691D2AF8518B9EE16178E0CE`
- Stack Profile: `.NET 10`, nullable habilitado, monólito modular, EF Core, SQS, Kafka, Redis, S3 e JWT Bearer
- Decisões consultadas: design de sistema, ADR-0008, ADR-0011, ADR-0014 e decomposição da fase 2
- Limiar: todos os achados sustentados, de `LOW` a `CRITICAL`
- Autoridade: somente leitura sobre o alvo e as evidências do commit
- Diff: não houve diff; todos os campos `introduced-by-diff` são `false`
- Pacote anterior: não foi aberto até os três comprovantes novos retornarem e a consolidação independente ser fechada

A cópia local da capacidade não continha `references/review-lenses.md`. A revisão carregou a cópia canônica equivalente em `~/.araia/framework/adapters/dotnet/skills/dotnet-code-review/references/review-lenses.md`, além da regra compartilhada e do `code-style.md` do adaptador.

## Cobertura das lentes

| Lente | Resultado | Achados |
|---|---|---:|
| [Performance](1-performance.md) | `FINDINGS` | 1 |
| [Engenharia de software](2-software-engineering.md) | `FINDINGS` | 2 |
| [Qualidade do .NET](3-net-quality.md) | `FINDINGS` | 2 |
| [Teste](4-test.md) | `FINDINGS` | 1 |
| [Arquitetura](5-architecture.md) | `FINDINGS` | 2 |
| [Segurança](6-security.md) | `FINDINGS` | 3 |

## Índice dos achados

| ID | Severidade | Linha | Síntese |
|---|---|---:|---|
| [ENG-001](2-software-engineering.md#eng-001-o-circuito-aberto-não-produz-o-fallback-de-canal-declarado) | alta | 32 | Circuito aberto devolve a tentativa a `queued`, estado que nenhuma varredura de fallback alcança. |
| [ARC-001](5-architecture.md#arc-001-o-handler-de-fallback-aceita-uma-tentativa-de-outra-notificação) | alta | 38 | O handler combina `notificationId` e `failedAttemptId` sem validar pertencimento. |
| [SEC-003](6-security.md#sec-003-o-relatório-mensal-concluído-omite-as-ativações-de-pim-prometidas) | alta | 86 | O relatório marcado como concluído omite PIM, além de DLQs e falhas de provedor. |
| [PRF-001](1-performance.md#prf-001-o-limite-de-20-ms-não-tem-medição-e-o-custo-cresce-por-evento) | média | 48 | O caminho síncrono cifra o payload e confirma uma transação por evento sem teste de latência. |
| [ENG-002](2-software-engineering.md#eng-002-a-duplicata-aceita-no-fallback-de-unknown-não-é-observável-como-notificationduplicate) | média | 214 | `notification.duplicate` não prova a duplicata real que o risco aceita. |
| [STK-001](3-net-quality.md#stk-001-a-validação-de-opções-não-alcança-os-limites-aninhados-do-token-bucket) | média | 32 | Valores inválidos do rate limit podem chegar ao Redis e degradar para fail-open. |
| [TST-001](4-test.md#tst-001-a-assinatura-sendgrid-é-testada-com-vetor-gerado-pela-própria-implementação) | média | 203 | O teste prova consistência interna, não interoperabilidade com o provedor. |
| [ARC-002](5-architecture.md#arc-002-a-supressão-automática-pode-ser-perdida-depois-do-commit) | média | 173 | A supressão é best effort, sem outbox ou retentativa após o evento ser marcado como aplicado. |
| [SEC-001](6-security.md#sec-001-a-correlação-sendgrid-pode-usar-identificadores-de-query-não-assinados) | média | 49 | Um callback SendGrid assinado sem `custom_args` pode usar correlação de query fora da assinatura. |
| [SEC-002](6-security.md#sec-002-o-callback-twilio-não-possui-a-janela-temporal-de-replay-declarada) | média | 47 | A Twilio não envia timestamp e a marca de dedupe expira em 30 dias. |
| [STK-002](3-net-quality.md#stk-002-o-endpoint-publica-202-e-o-documento-declara-200) | baixa | 48 | O contrato HTTP documentado diverge do endpoint e dos testes. |

## Comprovantes independentes

- `dotnet-architect`: cobriu as seis lentes; originou `PRF-001`, `STK-002`, `ARC-001`, `ARC-002`, `SEC-001`, `SEC-002` e `SEC-003`; registrou `NO-FINDING` em STK e TST no comprovante individual.
- `dotnet-engineer`: cobriu as seis lentes; originou `PRF-001`, `ENG-002`, `TST-001`, `SEC-002` e `SEC-003`; registrou `NO-FINDING` em STK e ARC.
- `dotnet-specialist`: cobriu as seis lentes; originou `ENG-001`, `STK-001`, `SEC-002` e `SEC-003`; tratou a ausência de medição de PRF como ponto cego e a lacuna de teste do circuito aberto como parte da verificação de `ENG-001`.

Todos confirmaram a revisão-fonte, o blob e o SHA-256 do alvo. Nenhum revisor abriu o pacote anterior antes de retornar.

## Divergências preservadas

- Em `PRF-001`, o `dotnet-specialist` registrou a ausência de medição como ponto cego, sem achado. A consolidação manteve `MEDIUM` porque o documento apresenta o limite de 20 ms como propriedade alcançada e o custo cresce por evento.
- Em `ENG-001`, o `dotnet-specialist` também classificou a ausência do cenário ponta a ponta como achado de teste. A consolidação incorporou essa lacuna à verificação do defeito para evitar duplicação por causa e localização.
- Em `STK-002`, o comprovante classificou o ponto em engenharia. A consolidação escolheu a lente da stack para manter identidade comparável com o pacote anterior.
- Em `SEC-003`, os revisores divergiram entre Segurança, Engenharia e Arquitetura e entre `HIGH` e `MEDIUM`. A consolidação preservou a maior severidade sustentada e a lente do impacto sobre evidência de acesso privilegiado.

## Comparação com a revisão anterior

Pacote comparado: [`docs-fases-fase-2-resiliencia-e-sms-md-20260825T124525Z`](../docs-fases-fase-2-resiliencia-e-sms-md-20260825T124525Z/00-index.md).

As duas revisões usam a mesma revisão-fonte e o mesmo blob. O pacote anterior registrou 31 achados: nove altos, vinte médios e dois baixos. O novo registrou 11: três altos, sete médios e um baixo.

### Diferenças de método

| Aspecto | Revisão nova | Revisão anterior |
|---|---|---|
| Unidade revisada | Um arquivo delimitado | Documento, código relacionado, migrações, configuração e testes |
| Âncora dos achados | Sempre o Markdown alvo | Também arquivos `.cs`, `appsettings.json` e `Directory.Packages.props` |
| Diff | Não houve diff | O último commit do documento atribuiu sete achados ao diff |
| Execução dinâmica | Não executada para preservar o snapshot e a árvore local | Build executado e reprovado; testes não executados nesse snapshot |

### Causas reproduzidas nas duas revisões

| Revisão nova | Revisão anterior | Resultado |
|---|---|---|
| `PRF-001` | `PRF-001` | Orçamento de 20 ms sem medição e com custo por evento. |
| `ENG-001` | `ENG-004` | Requeue produz estado que o tracker não varre para fallback. |
| `STK-002` | `STK-001` | Documento declara HTTP 200 e endpoint retorna 202. |
| `SEC-002` | `SEC-002` | Twilio sem timestamp e dedupe com retenção finita. |
| `SEC-003` | `ENG-006` | PIM é a terceira seção ausente do relatório; a nova revisão elevou a severidade para alta. |

### Achados novos

`ENG-002`, `STK-001`, `TST-001`, `ARC-001`, `ARC-002` e `SEC-001` não aparecem no pacote anterior.

### Achados anteriores não reproduzidos

| Lente anterior | IDs |
|---|---|
| Performance | `PRF-002` a `PRF-006` |
| Engenharia | `ENG-001`, `ENG-002`, `ENG-003`, `ENG-005`, `ENG-007` |
| Qualidade do .NET | `STK-002` a `STK-006` |
| Teste | `TST-001` a `TST-005` |
| Arquitetura | `ARC-001` a `ARC-004` |
| Segurança | `SEC-001`, `SEC-003` |

Não reproduzido não significa refutado. Esses 26 itens não receberam nova verificação dirigida e continuam como evidência consultiva do pacote anterior. O pacote novo não os encerra nem substitui.

## Pontos cegos

- Nenhum teste, build, benchmark ou comando que gerasse `bin`, `obj` ou estado externo foi executado nesta revisão.
- A árvore local continha uma alteração não relacionada em `DeclareContactPoints.Handler.cs`; ela não foi usada como evidência.
- Não foram validados WAF, cabeçalhos encaminhados, listas de permissões reais, credenciais, remetente BR, Messaging Service, bucket WORM, fonte PIM, política publicada, métricas de produção, pentest ou entrega a Compliance.
- Não havia benchmark, trace ou telemetria versionada para webhook, scheduler sob pico de carga, starvation de `operational` ou comportamento sob carga.

Este pacote é consultivo. Ele não calcula EQI, não aprova gate, não altera o alvo, não comenta em provedor e não muda estado de lifecycle.
