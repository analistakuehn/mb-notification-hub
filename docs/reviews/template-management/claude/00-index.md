---
language: pt-BR
type: code-review
adapter: dotnet
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
date: 2026-08-25
verdict: FINDINGS
---

# Revisão de código: módulo TemplateManagement

Revisão independente de seis lentes conduzida pelo skill `dotnet-code-review`
com as três personas do adaptador `dotnet` em contextos isolados e paralelos.

## Identidade

| Campo | Valor |
|---|---|
| Revisão fonte | `cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2` (branch `master`, árvore limpa) |
| Escopo | módulo completo, não um diff |
| Veredito | `FINDINGS` |
| Achados brutos | 68 |
| Achados consolidados | 52 |

Por ser revisão de módulo e não de mudança, o campo `introduced-by-diff` é
`false` em todos os achados: o módulo inteiro é a linha de base, não um delta.

## Escopo revisado

| Área | Arquivos | Linhas |
|---|---|---|
| `Domain/` | 31 | 4.118 |
| `Features/` (18 mutations, 14 queries) | 148 | 6.417 |
| `Infrastructure/` | 52 | 7.243 |
| `Integration/V1/` | 13 | 524 |
| Testes unitários e de integração | 59 | 7.570 |

Fora de escopo: arquivos gerados pelo EF (`*.Designer.cs` e o snapshot do
modelo), módulos irmãos (lidos apenas como contexto de contrato), `bin/`,
`obj/` e worktrees.

## Cobertura de lentes

Todas as seis lentes foram avaliadas pelos três revisores. Nenhum `NO-FINDING`
foi necessário: cada revisor produziu achado sustentado em cada lente.

| Lente | Arquivo | architect | engineer | specialist | Consolidado |
|---|---|---|---|---|---|
| `PRF` Performance | [1-performance.md](1-performance.md) | 2 | 3 | 6 | 7 |
| `ENG` Software Engineering | [2-software-engineering.md](2-software-engineering.md) | 3 | 3 | 5 | 7 |
| `STK` .NET Quality | [3-net-quality.md](3-net-quality.md) | 1 | 2 | 5 | 6 |
| `TST` Test | [4-test.md](4-test.md) | 2 | 5 | 5 | 10 |
| `ARC` Architecture | [5-architecture.md](5-architecture.md) | 7 | 1 | 2 | 8 |
| `SEC` Security | [6-security.md](6-security.md) | 3 | 2 | 11 | 14 |
| **Total** | | **18** | **16** | **34** | **52** |

## Distribuição por severidade

| Severidade | Quantidade |
|---|---|
| `CRITICAL` | 1 |
| `HIGH` | 16 |
| `MEDIUM` | 25 |
| `LOW` | 10 |

## O achado que domina o relatório

`SEC-001` é o único `CRITICAL` e foi levantado de forma independente pelos três
revisores, com severidades `CRITICAL`, `HIGH` e `MEDIUM`. O mascaramento de
variável sensível só alcança propriedades de primeiro nível do payload, e
nenhum check liga o nome declarado sensível ao schema de variáveis da versão.
A consequência é dado pessoal em claro gravado na trilha WORM append-only como
prova de "que um valor foi enviado, nunca qual", sem sinal em nenhuma camada.

Fechado. O mascaramento passou a ser uma regra estrutural única sobre o payload,
em `SharedKernel/SensitiveValueMask.cs`, lida pelos dois mascaradores. Ela
alcança qualquer profundidade e elemento de array, resolve caminho com ponto só
por caminho e nunca por chave literal, e recusa alto quando um prefixo próprio
cai em nó que não é objeto. Como a regra lê o payload e não a declaração,
template já publicado deixou de vazar no deploy, sem escrita de dado. A
investigação achou dois defeitos que esta seção não registrava, e os dois eram
piores do que o descrito: uma chave literal de topo que soletrava o caminho era
mascarada no lugar do caminho real, e payload com chave duplicada derrubava a
ingestão com `500` em qualquer template com variável sensível.

## Estado dos achados

Levantado após a remediação. O estado de cada achado está também na sua própria
ficha, na linha **estado**; esta tabela existe para que a pergunta "o que falta"
tenha uma resposta em um lugar só.

| Estado | Quantidade | Significado |
|---|---:|---|
| `RESOLVIDO` | 25 | corrigido no código e verificado por suíte |
| `PENDENTE` | 23 | não tratado, e o achado segue válido como escrito |
| `PARCIAL` | 2 | parte corrigida, com o risco residual nomeado na ficha |
| `ADIADO` | 2 | decisão deliberada de não corrigir, com a razão registrada |
| `OBSOLETO` | 1 | a premissa caiu; **não** aplique a recomendação como escrita |
| **Total** | **53** | 52 da revisão original, mais `PRF-008` |

### Resolvidos

| Id | Lente | O que fechou |
|---|---|---|
| `SEC-002` | Security | builtins que reavaliam string como código removidos do sandbox |
| `SEC-003` | Security | valores do payload redigidos das mensagens do engine |
| `SEC-008` | Security | retrocesso quadrático eliminado no check de posição de URL |
| `SEC-009` | Security | detector de link virou total e não backtracking |
| `SEC-010` | Security | alocação por largura removida do builtin |
| `STK-001` | .NET Quality | `LimitToString` derivado da opção; fim do truncamento silencioso |
| `STK-003` | .NET Quality | lista de isenções derivada da superfície do sandbox |
| `STK-004` | .NET Quality | fonte de cancelamento não sobrevive mais ao método |
| `STK-006` | .NET Quality | `catch` inalcançável removido |
| `PRF-001` | Performance | trabalho abandonado deixou de existir |
| `PRF-002` | Performance | temporizador órfão saiu do caminho quente |
| `PRF-006` | Performance | expressões regulares içadas para `[GeneratedRegex]` estáticos |
| `PRF-008` | Performance | janela descendente de 200 com continuação por cursor, decidida com medição |
| `PRF-003` | Performance | chave canônica nos três sítios e evicção por `MemoryCache`, escolhida com medição |
| `PRF-004` | Performance | o validador passou a compartilhar o contexto memoizado, de quatro consultas para duas |
| `PRF-005` | Performance | um `TemplateContext` por forma no lugar de um por render, menos 71,2% de alocação |
| `PRF-007` | Performance | o teto por contagem, que não limitava memória, virou orçamento em caracteres |
| `TST-002` | Test | `ScribanSandboxTests` cobre a família de fuga que faltava |
| `SEC-001` | Security | regra estrutural única em `SensitiveValueMask`, lida pelos dois mascaradores; alcança qualquer profundidade e array |
| `SEC-004` | Security | regra única em `LinkDomainPolicy`: o check de publicação extrai host em vez de casar link literal, e o render confere todo valor string em qualquer profundidade |
| `SEC-005` | Security | o texto do layout fixado passou a responder ao allowlist e ao banimento de link em SMS de autenticação, e os tetos de canal passaram a somar wrapper mais corpo |
| `SEC-006` | Security | a identidade do layout saiu da entrada imutável para uma de ponteiro, e o status passou a recusar no render publicado, no de autoria e na publicação de quem o fixa |
| `SEC-007` | Security | o propósito passou a ser canonizado na única porta de escrita, com migração idempotente dos já persistidos, e os seis sítios de comparação leem um predicado único |
| `SEC-011` | Security | as transições de ciclo de vida passaram a invalidar o ponteiro no processo que as commitou, com cerca de geração, e a janela remanescente entre processos ficou registrada como limite aceito |
| `SEC-013` | Security | só fonte publicada é memoizada, o orçamento passou a contar a árvore sintática em bytes, e a recusa por capacidade passou a despejar de verdade em vez de congelar a loja |
| `SEC-014` | Security | os onze caminhos de trilha passaram à mesma forma compacta, sem mensagem de check, e a razão de ciclo de vida virou código canônico com nota livre opcional |

### Parciais, adiado e obsoleto

| Id | Estado | O que exige atenção |
|---|---|---|
| `TST-001` | `PARCIAL` | o defeito ficou bloqueado na publicação, mas o oráculo de mascaramento sobre payload aninhado continua não existindo. |
| `STK-002` | `ADIADO` | ligar o modo estrito trocaria entrega degradada por entrega zero em mensagem de autenticação. A metade correta é detectar em publicação. |
| `SEC-012` | `ADIADO` | o achado é real e foi medido maior do que a ficha descreve, mas nada era implementável: a claim que ligaria um principal a uma aplicação não existe, nem o provedor que a emitiria. Aceito e registrado em quatro lugares, amarrado à pendência 25 da fase 1b, que é a mesma decisão nas outras duas superfícies. |
| `TST-003` | `OBSOLETO` | a recomendação manda observar trabalho abandonado que não existe mais. Aplicá-la como escrita é trabalho perdido. |

Além desses, duas recomendações envelheceram **dentro** de achados resolvidos, e
estão sinalizadas na ficha de cada um: a redução do `[Range]` em `STK-001` e o
ponto de vigilância sobre callback no token em `STK-004`.

### Pendentes, por lente

| Lente | Pendentes | Ids |
|---|---:|---|
| Architecture | 8 | `ARC-001` a `ARC-008` |
| Software Engineering | 7 | `ENG-001` a `ENG-007` |
| Test | 7 | `TST-004` a `TST-010` |
| .NET Quality | 1 | `STK-005` |

O maior `HIGH` de conteúdo ainda intocado é `ENG-002` (limite de canal medido
sobre a fonte). A lente `SEC` não tem mais nenhum `HIGH` pendente.

### Uma advertência de navegação

Os campos `linha` de cada ficha apontam para a revisão `cc754e5`. Os arquivos
tocados pela remediação (`ScribanTemplateEngine.cs`, `TemplateValidation.cs`,
`GetTemplate.Handler.cs`, `GetLayout.Handler.cs`, mais os `Response` e `Endpoint`
das duas consultas de detalhe, `PublishedReadCache.cs`, `PublishedCatalog.cs`,
`VariablesPayloadValidation.cs`, `RenderTemplateVersion.Handler.cs`,
`LayoutValidation.cs`, `LayoutReference.cs`, `LayoutStatus.cs`, `Template.cs` e
`PublishedTemplateRenderer.cs`) mudaram de tamanho, então
nesses casos o número da linha é histórico e não serve para navegar no código
atual. O trecho de evidência citado continua sendo o localizador confiável.


## Agrupamentos de correção sugeridos

Os achados se concentram em cinco frentes. A ordem abaixo reflete caminho de
exploração mais curto primeiro, não severidade isolada.

1. **Sandbox do Scriban**: `SEC-002`, `SEC-003`, `SEC-010`, `STK-001`,
   `STK-002`, `STK-003`, `PRF-001`, `TST-002`, `TST-003`. Evidência lida na
   fonte do pacote Scriban 7.2.6.
2. **Mascaramento e variáveis sensíveis**: `SEC-001`, `ARC-005`, `TST-001`,
   `TST-008`.
3. **Controles de conteúdo com furo**: `SEC-004`, `SEC-005`, `SEC-006`,
   `SEC-007`, `ENG-002`, `TST-005`, `TST-009`.
4. **ReDoS e eixo de erro único**: `SEC-008`, `SEC-009`, `PRF-006`, `ENG-004`.
5. **Cache de leitura publicada**: `SEC-011`, `SEC-013`, `ARC-006`, `TST-006`,
   `TST-007`. O `PRF-003` fechou e levou junto a metade de `PRF-007` que vivia
   neste componente; o `PRF-004` fechou em seguida. O `SEC-011`, que pede
   invalidação por transição de ciclo de vida, agora tem uma fachada com ponto
   de decisão único onde acrescentá-la, e ficou **mais urgente**: com o
   validador memoizando, são três contratos publicados na janela de 60
   segundos, e não mais dois.

## Dissensos preservados

A consolidação retém a maior severidade sustentada e preserva a divergência.

| Id | Divergência | Retido | Razão |
|---|---|---|---|
| `SEC-001` | specialist `CRITICAL`, architect `HIGH`, engineer `MEDIUM` | `CRITICAL` | três evidências independentes da mesma causa raiz |
| `SEC-008` | architect `MEDIUM` (com medição), specialist `HIGH` (sem medição) | `HIGH` | a medição do architect sustenta a extrapolação ao teto real de 512.000 caracteres |
| `SEC-009` | architect `HIGH`, specialist `MEDIUM` | `HIGH` | o architect demonstrou o caminho até a DLQ pelo pipeline consumidor |
| `ENG-001` | specialist `HIGH`, engineer `MEDIUM` | `HIGH` | ambos demonstraram divergência já materializada, não risco futuro |
| `SEC-014` | architect `MEDIUM`, specialist `LOW` | `MEDIUM` | o architect demonstrou também a assimetria entre publicação e rollback |

## Hipóteses verificadas e descartadas

Registradas para que uma revisão seguinte não gaste tempo nelas.

- Concorrência otimista correta e coerente: `xmin` como `IsRowVersion` nas
  identidades, `etag` como token nas versões, índices únicos parciais como
  backstop no banco, com tratamento explícito de `UniqueViolation`.
- Trilha de auditoria na mesma transação: conforme em todos os onze handlers de
  efeito governado, por leitura.
- `AddColumn<uint>("xmin", type: "xid")` na migration é benigno: o gerador do
  Npgsql ignora operações sobre colunas de sistema.
- Hash canônico determinístico entre hosts: o Scriban formata em cultura
  invariante por padrão.
- Sem colisão de chave de cache entre aplicações: nem `ApplicationName` nem
  `TemplateKey` admitem o separador.
- Entidades sem tracking guardadas em singleton são seguras: coleções owned,
  sem proxies de carregamento tardio, instâncias nunca mutadas.
- `timestamptz` correto em todas as colunas temporais.
- Paginação keyset com cursor opaco, sem N+1, sem `Skip`.
- Autorização e rate limiting presentes nos 32 endpoints e já impostos
  deterministicamente pelo teste de arquitetura de segurança.
- Build limpo em `net10.0` sob `TreatWarningsAsErrors`, confirmado
  independentemente por dois revisores. As duas supressões de analisador
  existentes estão justificadas.
- A migration de particionamento não perde colunas de cadeia de hash: elas não
  existiam nesta tabela naquele ponto do histórico.

## Limites desta revisão

- Todos os achados `PRF` do especialista, mais `SEC-008` e `SEC-010`, nomeiam o
  mecanismo a partir de leitura de código e da fonte do Scriban, **sem medição
  executada**: a autoridade foi somente leitura. Cada achado traz o experimento
  que o confirmaria. A única medição real do conjunto é a do architect sobre os
  dois padrões de expressão regular, reproduzida em `SEC-008`.
- `SEC-012` tem confiança média porque nenhuma decisão aceita exige escopo por
  aplicação: é lacuna de alocação de NFR de segurança, não violação de regra
  escrita.
- Os módulos consumidores foram lidos apenas como contexto de contrato. Se um
  consumidor já filtra links ou limita comprimento de SMS, a severidade de
  `SEC-004` e `ENG-002` cai. Nada no contrato publicado deste módulo delega
  essas responsabilidades.

## O que este relatório não é

Achados são evidência advisory. Este documento não calcula EQI, não aprova
gate, não edita fonte, não posta comentários e não altera estado de ciclo de
vida. Ele foi gravado diretamente, fora do registro do orquestrador, portanto
não possui identidade de relatório rastreável nem habilita os comandos de
comentário e publicação.
