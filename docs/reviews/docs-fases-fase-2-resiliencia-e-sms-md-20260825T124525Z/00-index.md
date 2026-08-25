---
language: pt-BR
target: docs/fases/fase-2-resiliencia-e-sms.md
scope: project
reviewed-on: 2026-08-25T09:45:25-03:00
reviewed-via: dotnet-code-review
severity: high
status: open
---

# Revisão de código de `fase-2-resiliencia-e-sms.md`

## Resultado

`FINDINGS`

A revisão confirmou 31 achados: nove de severidade alta, vinte de severidade média e dois de severidade baixa. Sete achados são atribuídos ao diff que fechou a tabela de status, porque a seção `Estado em 2026-08-25` foi introduzida por ele.

## Escopo e identidade da evidência

- Alvo: [`docs/fases/fase-2-resiliencia-e-sms.md`](../../fases/fase-2-resiliencia-e-sms.md)
- Revisão-fonte: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- Objeto Git do alvo: `37bc47f8d2f32579d2c740e06b10861054513c7e`
- Último diff do alvo: `4d36b157b84e4b659570189a17153f0be4603ddd`
- Revisores: `dotnet-architect`, `dotnet-engineer` e `dotnet-specialist`
- Método: inspeção somente leitura do documento, da decomposição, das ADRs, do design de sistema, dos contratos C#, das migrações, da configuração de ambiente e dos testes relacionados
- Execução dinâmica: `dotnet build` foi executado na revisão-fonte e reprovou, o que sustenta TST-001. Por consequência, nenhum teste pôde ser executado na revisão-fonte, e todo julgamento sobre cobertura é leitura do teste e do código sob teste. Nenhum benchmark, medição de plano de consulta ou chamada a provedor foi executado.

O escopo é um documento de design, não um diff de código-fonte .NET. As seis lentes foram aplicadas ao desenho e às afirmações falsificáveis do documento, confrontadas contra o código na revisão-fonte. Este recorte foi escolhido pela consolidação e está registrado aqui porque altera o que `introduced-by-diff` significa: o campo indica se o diff mais recente do documento introduziu a afirmação defeituosa, não se introduziu o defeito no código.

## Cobertura das lentes

| Lente | Resultado | Achados |
| --- | --- | ---: |
| [Performance](1-performance.md) | `FINDINGS` | 6 |
| [Software Engineering](2-software-engineering.md) | `FINDINGS` | 7 |
| [.NET Quality](3-net-quality.md) | `FINDINGS` | 6 |
| [Test](4-test.md) | `FINDINGS` | 5 |
| [Architecture](5-architecture.md) | `FINDINGS` | 4 |
| [Security](6-security.md) | `FINDINGS` | 3 |

Nenhuma lente ficou sem cobertura em nenhum dos três recibos. Ausência de achado numa lente significa que a evidência inspecionada não sustentou um achado, não que a parte correspondente esteja provada correta.

## Índice dos achados

| ID | Severidade | Linha | Síntese |
| --- | --- | ---: | --- |
| [TST-001](4-test.md#tst-001-na-revisão-sob-review-a-solução-não-compila-então-nada-do-que-o-documento-chama-de-validado-é-verificável) | alta | 203 | A solução não compila na revisão-fonte, então concluídas e validadas descreve outra revisão. |
| [ARC-002](5-architecture.md#arc-002-o-fallback-rederiva-o-plano-da-política-publicada-corrente-não-do-plano-sob-o-qual-a-notificação-foi-admitida) | alta | 38 | O fallback avança sobre o plano bruto publicado e ignora os filtros de consentimento e supressão da admissão. |
| [ARC-003](5-architecture.md#arc-003-o-documento-fixa-programmable-messaging-e-a-configuração-entregue-seleciona-twilio-verify-com-callback-vazio) | alta | 27 | A configuração entregue seleciona Twilio Verify, sem `StatusCallback` nem `ValidityPeriod`. |
| [ENG-001](2-software-engineering.md#eng-001-a-tabela-nomeia-onze-fatias-o-texto-afirma-doze-e-a-f2-3-não-existe-no-documento) | alta | 203 | A tabela nomeia onze fatias, o texto afirma doze, e a entrega da F2-3 não aparece. |
| [ENG-004](2-software-engineering.md#eng-004-a-varredura-por-prazo-alcança-dois-estados-e-o-documento-promete-os-demais) | alta | 32 | Tentativa devolvida a `queued` com prazo vencido não é vista por varredura nenhuma. |
| [PRF-003](1-performance.md#prf-003-o-prazo-de-60-s-do-veredito-inconclusivo-substitui-o-timeout-de-30-s-do-plano) | alta | 39 | Em veredito inconclusivo o fallback sai em 65 s, não nos 30 s que o plano promete. |
| [PRF-004](1-performance.md#prf-004-a-aritmética-do-fallback-não-fecha-contra-o-aceite-de-35-s-do-116) | alta | 155 | O orçamento somado do fallback passa do aceite de 35 s do gate de carga. |
| [PRF-005](1-performance.md#prf-005-a-reconciliação-está-funcional-em-teste-e-inerte-em-escala) | alta | 174 | 200 attempts por dia, ocupados permanentemente por push sem lookup, sem índice que sirva. |
| [SEC-001](6-security.md#sec-001-a-allowlist-de-ip-dos-provedores-não-pode-funcionar-na-topologia-que-o-próprio-documento-fixa) | alta | 123 | Sem `ForwardedHeaders`, a allowlist lê o endereço do balanceador: desligada não existe, ligada quebra a entrega. |
| [ARC-001](5-architecture.md#arc-001-duas-adrs-aceitas-que-governam-esta-fase-estão-fora-das-listas-de-fontes-e-o-documento-nega-o-desvio-de-uma-terceira) | média | 161 | ADR-0014 e ADR-0015 fora das fontes, e a afirmação de zero desvio contradiz a ADR-0015. |
| [ARC-004](5-architecture.md#arc-004-a-seção-de-dados-e-persistência-não-lista-nenhuma-coluna-que-a-fase-criou) | média | 113 | A seção de dados omite `plan_advanced_at`, que é o mecanismo que impede SMS duplicado. |
| [SEC-002](6-security.md#sec-002-a-replay-protection-é-descrita-como-composta-e-no-canal-sms-a-janela-nunca-engaja) | média | 47 | No canal SMS a janela de timestamp nunca engaja, e a marca de dedupe expira em 30 dias. |
| [SEC-003](6-security.md#sec-003-controles-apresentados-como-ativos-que-a-base-entrega-desligados-ou-com-fail-open-não-declarado) | média | 127 | Kill switch automático desligado por padrão e limitador de taxa com fail-open não declarado. |
| [PRF-001](1-performance.md#prf-001-orçamento-de-20-ms-declarado-como-propriedade-sem-percentil-e-sem-medição) | média | 48 | 20 ms sem percentil, sem gate e sem teto de lote, com uma transação por evento. |
| [PRF-002](1-performance.md#prf-002-índices-parciais-descritos-não-existem-na-forma-descrita) | média | 55 | Os predicados parciais citados não são os do banco, e dois índices usados não são mencionados. |
| [PRF-006](1-performance.md#prf-006-a-correlação-de-callback-não-carrega-a-chave-de-partição) | média | 365 | A correlação do attempt sonda toda partição, no caminho de todo evento de entrega. |
| [ENG-002](2-software-engineering.md#eng-002-a-dependência-declarada-atribui-à-fase-1b-uma-capacidade-que-esta-fase-produziu) | média | 100 | A dependência afirma oito respostas do §9.5 pela fase 1b; a 1b declara sete e uma lacuna. |
| [ENG-003](2-software-engineering.md#eng-003-o-corpo-do-documento-ainda-descreve-a-semântica-anterior-à-segunda-correção) | média | 40 | O corpo mantém a regra de irmãos e a semântica de `delivered` que a correção 2 derrubou. |
| [ENG-005](2-software-engineering.md#eng-005-a-supressão-por-token-fcm-não-passa-pelo-ledger-que-o-documento-promete) | média | 60 | `UNREGISTERED` invalida token e nunca produz `suppression.added` nem reversão auditada. |
| [ENG-006](2-software-engineering.md#eng-006-a-seção-de-honestidade-do-relatório-mensal-declara-duas-lacunas-onde-o-código-tem-três) | média | 201 | O relatório omite três seções, e a terceira não é resolvível pela unidade de infraestrutura. |
| [ENG-007](2-software-engineering.md#eng-007-o-contrato-de-retry-herdado-do-8-não-é-expressável-na-configuração-entregue) | média | 33 | O backoff é único por papel e o `maxReceiveCount` não existe no repositório. |
| [STK-002](3-net-quality.md#stk-002-a-allowlist-de-origem-compara-prefixo-textual-em-vez-de-rede-ip) | média | 36 | A allowlist compara prefixo de string, então autoriza faixas vizinhas e recusa IPv6 mapeado. |
| [STK-003](3-net-quality.md#stk-003-findsystemtimezonebyid-sobre-dado-ingerido-sem-guarda-no-estágio-policy) | média | 50 | Fuso inválido lança exceção no estágio Policy da classe que esta fase ativa. |
| [STK-004](3-net-quality.md#stk-004-a-semântica-declarada-do-circuit-breaker-omite-a-precondição-que-decide-se-ele-abre) | média | 32 | `MinimumThroughput = 10` não está descrito, e o canal SMS raramente o alcança. |
| [STK-005](3-net-quality.md#stk-005-polly-é-consumido-como-dependência-direta-de-compilação-sem-declaração-de-pacote) | média | 5 | Polly é tipado por três provedores sem `PackageVersion` no gerenciamento central. |
| [STK-006](3-net-quality.md#stk-006-o-timeout-é-propriedade-por-provedor-e-o-113-o-exige-por-classe) | média | 149 | O timeout é por provedor, então o canal SMS roda nos 5 s de `demais` e não nos 2 s de `critical`. |
| [TST-002](4-test.md#tst-002-o-item-de-caos-da-estratégia-de-testes-não-tem-artefato-e-não-consta-como-lacuna) | média | 150 | O item de caos não tem artefato no repositório e não é declarado pendente. |
| [TST-004](4-test.md#tst-004-a-prova-do-critério-de-saída-é-condicional-a-docker-e-o-documento-a-apresenta-como-incondicional) | média | 195 | A prova do critério 1 é ignorada sem Docker e a suíte segue verde. |
| [TST-005](4-test.md#tst-005-nenhum-oráculo-mede-latência-de-fallback) | média | 190 | Nenhum teste mede tempo de fallback, então os três achados de prazo não têm contraprova. |
| [STK-001](3-net-quality.md#stk-001-código-http-declarado-difere-do-publicado) | baixa | 48 | O documento fixa `200`; o endpoint responde `202`. |
| [TST-003](4-test.md#tst-003-o-portão-executável-não-alcança-fluxo-de-autenticação-fora-da-classe-critical) | baixa | 195 | O portão filtra só `critical`, então autenticação em `transactional` passa. |

## Recibos independentes

- `dotnet-architect`: encontrou a rederivação do plano no fallback, a divergência entre o produto Twilio documentado e o configurado, a allowlist inaplicável na topologia, o inventário de ADRs incompleto, a omissão das colunas novas, a supressão de push fora do ledger e o build reprovado.
- `dotnet-engineer`: encontrou a lacuna de varredura nos estados não terminais, a semântica pós correção ausente do corpo, a dependência mal atribuída à fase 1b, o predicado de índice divergente e o limite não declarado do portão executável.
- `dotnet-specialist`: encontrou a substituição do prazo de 30 s pelo de 60 s, o estouro do orçamento de 35 s, a reconciliação inerte em escala, a correlação sem poda de partição, a precondição omitida do circuit breaker, Polly sem declaração de pacote, o timeout por provedor e a terceira omissão do relatório mensal.

Os três aplicaram as seis lentes e nenhum viu o recibo dos outros antes de todos retornarem.

## Divergências preservadas

- Em `SEC-001`, o `dotnet-specialist` graduou média por ler o controle como inerte por default. A consolidação preserva a alta do `dotnet-architect`: sem `ForwardedHeaders` o controle é inaplicável, não apenas desligado, e ligá-lo quebra a entrega dos dois provedores.
- Em `ENG-001`, as três graduações foram alta, média e baixa. A consolidação preserva a alta, porque o defeito é omissão de entrega e não apenas de contagem.
- Em `SEC-002`, o `dotnet-engineer` graduou baixa, argumentando que o dano fica confinado à evidência. A consolidação preserva média, porque a evidência é o que a reconstrução publica a Compliance e a contenção citada é acidental.
- Em `STK-003`, o `dotnet-architect` graduou baixa por confiar nos validadores de ingestão. A consolidação preserva a média do `dotnet-specialist`.
- Em `ARC-003`, o `dotnet-specialist` descreveu o caminho de Programmable Messaging como ativo, tendo lido o adapter e não a configuração de ambiente. A consolidação executou a verificação e confirmou o achado do `dotnet-architect`.
- Em `TST-001`, o `dotnet-specialist` presumiu em pontos cegos que a revisão compila. A consolidação executou o build e confirmou o achado do `dotnet-architect`.
- Três achados foram atribuídos a lentes diferentes por revisores diferentes (`STK-001`, `PRF-002` e `SEC-003`). A consolidação escolheu a lente do defeito dominante e registrou a divergência em cada arquivo.

## Pontos cegos

- Nenhum teste executado na revisão-fonte, por consequência de `TST-001`. Todo julgamento de cobertura é leitura.
- Nenhuma medição. Os achados de plano de consulta e de orçamento de tempo são análise de forma do código e do SQL, não número. `EXPLAIN (ANALYZE)` sobre base particionada real pode estreitar `PRF-002`, `PRF-005` e `PRF-006`.
- A unidade de infraestrutura declarativa não está neste revision: nenhum `.tf`, nenhuma definição de fila, nenhum WAF, nenhuma faixa de origem de borda, nenhum bucket WORM, nenhuma fonte de métricas operacionais. Tudo que o documento delega a ela ficou fora de verificação nos dois sentidos.
- O instante zero do aceite de 35 s do §11.6 não está definido no design de sistema, o que limita a confiança de `PRF-004`.
- As assinaturas ECDSA do SendGrid e HMAC da Twilio foram lidas no esquema, não confrontadas contra payloads reais de provedor.
- O alcance histórico real da reconciliação por provedor depende de contrato externo, incluindo o add-on Email Activity, que o próprio documento registra como dependência não resolvida.
- Existe uma worktree em `.claude/worktrees/` com cópias dos mesmos arquivos. Toda citação de arquivo e linha deste pacote é do caminho principal do repositório.

## Nota sobre remediação posterior a esta revisão

Fora da autoridade desta revisão e depois de ela fechar, o bloqueador de `TST-001` foi reparado a pedido: a diretiva `using` desnecessária foi removida de `DeclareContactPoints.Handler.cs` e a suíte passou a executar, com 1494 testes aprovados e 2 ignorados (936 unitários, 8 de arquitetura, 5 de arquitetura de segurança e 545 de integração). Isso não altera o achado, que descreve o estado da revisão-fonte, e não valida nenhum dos outros 30: os demais permanecem abertos.

Este pacote é consultivo. Ele não aplica correções ao alvo, não calcula EQI e não emite veredito de lifecycle.
