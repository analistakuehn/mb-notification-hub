---
target: docs/fases/fase-1b-fundacao.md
scope: project
reviewed-on: 2026-08-24T10:31:35-03:00
reviewed-via: dotnet-code-review
severity: high
status: open
---

# Revisão de código de `fase-1b-fundacao.md`

## Resultado

`FINDINGS`

A revisão confirmou 11 achados: quatro de severidade alta, seis de severidade média e um de severidade baixa. Nenhum achado foi atribuído ao diff entre a revisão fixada e seu pai. O diff alterou somente a referência do commit da fatia C4 na linha 172.

## Escopo e identidade da evidência

- Alvo: [`docs/fases/fase-1b-fundacao.md`](../../fases/fase-1b-fundacao.md)
- Revisão-fonte: `3570c5e7ca761911330cace4b7dfdf1a8c5b2dbb`
- Objeto Git do alvo: `31f4e527ca6d9f713b60efb571bec157aa5979ed`
- Pai comparado: `b97bc4431d6924291d1af054cabb295f101cb58c`
- Revisores: `dotnet-architect`, `dotnet-engineer` e `dotnet-specialist`
- Método: inspeção somente leitura do documento, do diff fixado, dos ADRs, do Stack Profile, do design, dos contratos C# e dos testes relacionados
- Execução dinâmica: nenhum build, teste ou benchmark foi executado

## Cobertura das lentes

| Lente | Resultado | Achados |
| --- | --- | ---: |
| [Performance](1-performance.md) | `NO-FINDING` | 0 |
| [Software Engineering](2-software-engineering.md) | `FINDINGS` | 2 |
| [.NET Quality](3-net-quality.md) | `FINDINGS` | 1 |
| [Test](4-test.md) | `FINDINGS` | 1 |
| [Architecture](5-architecture.md) | `FINDINGS` | 5 |
| [Security](6-security.md) | `FINDINGS` | 2 |

## Índice dos achados

| ID | Severidade | Linha | Síntese |
| --- | --- | ---: | --- |
| [ENG-001](2-software-engineering.md#eng-001-estados-de-implementação-incompatíveis) | média | 71 | O documento conserva estados obsoletos para `Platform.Worker`, B3 e B15. |
| [ENG-002](2-software-engineering.md#eng-002-fechamento-da-c4-não-alcança-a-documentação-do-catálogo) | média | 172 | A C4 é dada como concluída, mas o contrato C# ainda inclui falhas no catálogo fechado de rejeições. |
| [STK-001](3-net-quality.md#stk-001-stack-profile-descrito-com-eixo-de-mensageria-obsoleto) | baixa | 219 | O texto afirma `messaging: [sqs]`, enquanto o perfil fixado contém SQS e Kafka. |
| [TST-001](4-test.md#tst-001-portão-operacional-sem-oráculo-executável) | média | 237 | O bloqueio de entrada em produção para `operational` não define verificação executável nem evidência persistida. |
| [ARC-001](5-architecture.md#arc-001-infraestrutura-da-fase-sem-fatia-de-entrega) | alta | 41 | Terraform pertence à fase, mas filas, tópicos, ACLs, identidades, KMS e WORM não têm fatia. |
| [ARC-002](5-architecture.md#arc-002-documento-aceito-depende-de-adrs-ainda-propostas) | média | 81 | O documento trata como aceitas cinco decisões cujo ADR permanece em `Proposta`. |
| [ARC-003](5-architecture.md#arc-003-catálogo-de-saída-anuncia-evento-não-publicado) | média | 100 | `contact_suppressed` aparece como saída publicada, embora a própria fase diga que a supressão não é anunciada. |
| [ARC-004](5-architecture.md#arc-004-entrada-de-contatos-promete-device-tokens-fora-do-contrato) | média | 101 | `contacts.events.v1` é descrito como transporte de tokens de dispositivo, mas o contrato implementado aceita contatos e consentimentos. |
| [ARC-005](5-architecture.md#arc-005-b14-declara-oito-respostas-que-a-fase-deliberadamente-não-fornece) | alta | 166 | B14 e o critério de saída afirmam oito respostas, mas a pergunta sobre entrega é explicitamente omitida. |
| [SEC-001](6-security.md#sec-001-identidade-auto-declarada-permite-impersonação-entre-produtores-kafka) | alta | 297 | Um escritor Kafka pode declarar a identidade lógica de outro produtor e herdar sua autorização. |
| [SEC-002](6-security.md#sec-002-kill-switch-crítico-sem-entrega-planejada) | alta | 249 | O mecanismo de parada de emergência é requisito de segurança, mas não pertence a nenhuma fase. |

## Recibos independentes

- `dotnet-architect`: encontrou deriva de estado, ausência de um portão operacional falseável, dependência de ADRs propostos, lacuna de infraestrutura e dois riscos de segurança.
- `dotnet-engineer`: confirmou a deriva de B15 e do Stack Profile e identificou que a documentação pública do catálogo C# ainda contradiz o fechamento da C4.
- `dotnet-specialist`: confirmou as contradições de B3, B15 e do Stack Profile e identificou três divergências de contrato arquitetural.

Todos os revisores aplicaram as seis lentes. Ausência de achado significa que a evidência estática inspecionada não sustentou um achado naquela lente, não que o sistema inteiro tenha sido provado correto.

## Divergências preservadas

- Em `ENG-002`, o `dotnet-engineer` classificou `introduced-by-diff` como verdadeiro porque a linha 172 passou a registrar um commit concreto. A consolidação define `false`: o pai já marcava a C4 como concluída, com apenas o commit pendente, e o diff não introduziu a divergência de contrato.
- Em `STK-001`, `dotnet-engineer` e `dotnet-specialist` atribuíram severidade baixa. O `dotnet-architect` agrupou a mesma evidência em um achado médio de deriva documental. A consolidação preserva o achado específico como baixo e mantém os demais estados obsoletos em `ENG-001`.

## Pontos cegos

- Infraestrutura externa não reproduzida: RDS Multi-AZ, PgBouncer, Entra ID, ACLs Kafka, Terraform, AWS pré-produção, Object Lock, KMS e telemetria.
- Medições de carga e contenção não reproduzidas; somente os instrumentos, linhas de base e resultados versionados foram inspecionados.
- Nenhum build, teste, benchmark ou chamada a provedor foi executado para evitar alterar o worktree e para respeitar a revisão-fonte fixada.

Este pacote é consultivo. Ele não altera código, não aplica correções, não calcula EQI e não emite veredito de lifecycle.
