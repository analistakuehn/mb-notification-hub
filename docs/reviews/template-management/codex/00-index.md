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

Revisão independente de seis lentes conduzida pelo skill dotnet-code-review
com as três personas do adaptador dotnet em contextos isolados e paralelos.

## Identidade

| Campo | Valor |
|---|---|
| Revisão de origem | cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2, branch master |
| Escopo | módulo completo, não um diff |
| Veredito | FINDINGS |
| Achados brutos | 22 |
| Achados consolidados | 14 |

Por ser uma revisão de módulo, e não de mudança, o campo introduced-by-diff é
false em todos os achados. O módulo inteiro constitui a linha de base.

## Escopo revisado

| Área | Arquivos rastreados | Arquivos substantivos |
|---|---:|---:|
| src/Platform.Api/Modules/TemplateManagement | 251 | 247 |
| tests/Platform.UnitTests/TemplateManagement | 32 | 32 |
| tests/Platform.IntegrationTests/TemplateManagement | 27 | 27 |
| Total | 310 | 306 |

O hash do conjunto substantivo ordenado é
a91bf9c6866815dbee7e91a2364e62a66f890f23b75e0baad078ead0eeac7254.
Mudanças concorrentes posteriores ao congelamento do snapshot não integraram a
revisão.

## Cobertura de lentes

As seis lentes foram avaliadas pelos três revisores. O dotnet-specialist
registrou NO-FINDING em PRF e ENG por ausência de evidência adicional
suficiente nessas duas lentes.

| Lente | Arquivo | architect | engineer | specialist | Consolidado |
|---|---|---:|---:|---:|---:|
| `PRF` Performance | [1-performance.md](1-performance.md) | 1 | 1 | `NO-FINDING` | 1 |
| `ENG` Software Engineering | [2-software-engineering.md](2-software-engineering.md) | 1 | 3 | `NO-FINDING` | 3 |
| `STK` .NET Quality | [3-net-quality.md](3-net-quality.md) | 1 | 1 | 2 | 3 |
| `TST` Test | [4-test.md](4-test.md) | 1 | 1 | 1 | 2 |
| `ARC` Architecture | [5-architecture.md](5-architecture.md) | 2 | 1 | 1 | 3 |
| `SEC` Security | [6-security.md](6-security.md) | 2 | 2 | 1 | 2 |
| **Total** | | **8** | **9** | **5** | **14** |

## Distribuição por severidade

| Severidade | Quantidade |
|---|---:|
| CRITICAL | 0 |
| HIGH | 4 |
| MEDIUM | 7 |
| LOW | 3 |

## Estado dos achados

Levantado após a remediação conduzida a partir deste relatório e do paralelo em
`../claude/`. O estado de cada achado está também na sua ficha, na linha
**estado**. Os achados em si não foram alterados: evidência, impacto e
recomendação seguem como escritos na revisão.

| Estado | Quantidade | Significado |
|---|---:|---|
| `PENDENTE` | 12 | não tratado, e o achado segue válido como escrito |
| `RESOLVIDO` | 2 | corrigido no código e verificado por teste |
| `PARCIAL` | 0 | parte corrigida, com o residual nomeado na ficha |
| **Total** | **14** | |

| Id | Estado | O que mudou |
|---|---|---|
| `STK-002` | `RESOLVIDO` | `include` e `include_join` saíram do builtin do sandbox e a lista de isenções passou a ser derivada dessa superfície, então a análise os reporta como variável não declarada e a publicação é reprovada. É a primeira das duas alternativas que o achado oferece. |
| `PRF-001` | `RESOLVIDO` | projeção no banco eliminou o custo dominante, que era maior do que o achado descreve. Depois, o teto de linhas entrou como janela **descendente** de 200 com continuação por cursor, decidido com medição. A ordenação crescente que a ficha descreve teria devolvido só versões `superseded`. |

## O que esta revisão viu e a paralela não

Duas revisões independentes rodaram sobre a mesma revisão fonte: esta, com 14
achados consolidados, e a de `../claude/`, com 53. A sobreposição é baixa, e
isso é o dado mais útil deste painel.

**Sete dos catorze achados aqui não têm correspondente na revisão paralela**,
apesar de ela ter quase quatro vezes mais achados:

| Id | Severidade | O que só esta revisão encontrou |
|---|---|---|
| `ARC-002` | `HIGH` | versões aprovadas sem a proteção append-only que a decisão aceita exige |
| `SEC-001` | `HIGH` | a expressão de link literal não é insensível a caixa: `HTTPS://` não casa |
| `ENG-001` | `MEDIUM` | a migração de jsonb para text pode invalidar hashes já aprovados |
| `ENG-003` | `LOW` | o catálogo histórico pode devolver uma versão draft |
| `STK-001` | `MEDIUM` | conversão numérica para `double` perde precisão e aceita não finitos |
| `STK-003` | `LOW` | `If-Match` aceita ETag fraco e não interpreta listas |
| `ARC-003` | `MEDIUM` | o DTO publicado expõe coleção concretamente mutável |
| `TST-002` | `MEDIUM` | a cadeia de upgrade da migração canônica não é exercitada |

Vale registrar o que isso significa, porque é contraintuitivo: **nenhuma das
duas revisões é completa**, e a maior não contém a menor. Tratar qualquer uma
como cobertura do módulo seria erro. Os `HIGH` acima, em especial `ARC-002`,
não aparecem em lugar nenhum do relatório paralelo.

Em sentido inverso, a revisão paralela encontrou o único `CRITICAL` do conjunto,
mascaramento de variável sensível que não alcança nome aninhado, que não aparece
aqui.

## Convergências

Cinco achados descrevem o mesmo defeito nas duas revisões, o que eleva a
confiança neles: `ENG-002` (pré-visualização divergente), `TST-001` e `ARC-001`
(cache publicada sem invalidação) e `SEC-002` (motivo livre na trilha
imutável). Nenhum foi corrigido.

## Advertência de navegação

Os campos `linha` apontam para a revisão `cc754e5`. Os arquivos tocados pela
remediação (`ScribanTemplateEngine.cs`, `TemplateValidation.cs`,
`GetTemplate.Handler.cs`, `GetLayout.Handler.cs`) mudaram de tamanho, então
nesses casos o número é histórico. O trecho de evidência citado continua sendo
o localizador confiável.

## Achados que dominam o relatório

Quatro achados concentram o risco material:

1. ARC-001: ponteiros publicados permanecem obsoletos por até 60 segundos,
   inclusive depois de desativação ou depreciação.
2. ARC-002: versões aprovadas não possuem a proteção append-only exigida pela
   decisão aceita.
3. SEC-001: a validação de URLs pode ser contornada no conteúdo final.
4. SEC-002: texto livre pode persistir dados sensíveis na auditoria imutável.

## Agrupamentos de correção sugeridos

1. **Coerência de leitura publicada**: ARC-001 e TST-001.
2. **Integridade persistida e upgrade**: ARC-002, ENG-001 e TST-002.
3. **Pipeline de templates**: ENG-002, STK-001 e STK-002.
4. **Proteção de conteúdo e auditoria**: SEC-001 e SEC-002.
5. **Contratos e superfícies administrativas**: PRF-001, ENG-003, STK-003 e
   ARC-003.

## Dissensos preservados

| Id | Divergência | Retido | Razão |
|---|---|---|---|
| PRF-001 | architect e engineer emitiram LOW; specialist registrou NO-FINDING | LOW, confiança média | o mecanismo de crescimento é demonstrável, mas não houve benchmark, perfil ou telemetria para quantificar o efeito |

## Evidência de validação

| Validação | Resultado |
|---|---|
| build em Release | 0 avisos e 0 erros |
| Testes unitários do módulo | 290 de 290 |
| Testes de integração do módulo | 120 de 120 |
| Testes de arquitetura | 8 de 8 |
| Testes de arquitetura de segurança | 5 de 5 |

Os testes relevantes inspecionados possuem oráculos independentes. Os achados
TST tratam de cenários ausentes, não de asserções tautológicas adicionais.

## Hipóteses verificadas

- O contrato histórico permite identidades depreciadas ou desabilitadas, mas
  restringe versões a published ou superseded. ENG-003 trata somente da falta
  desse filtro de estado.
- A decisão aceita ADR-0006 nomeia template_version e class_policy_version
  entre as tabelas append-only. As migrações do módulo protegem apenas
  audit_event e approval.
- O parser de URI reconhece HTTPS em caixa alta, enquanto a expressão regular
  usada na validação literal não reconhece o mesmo esquema.
- A cache publicada é local ao processo, possui janela de 60 segundos e não
  expõe mecanismo de invalidação.

## Limites desta revisão

Não foram executados teste de carga, perfil de produção, ensaio
multi-instância, adulteração SQL, upgrade destrutivo, SAST, DAST, varredura de
CVE ou SBOM, teste de penetração ou exportação WORM real.

PRF-001 permanece LOW e com confiança média porque o relatório demonstra o
crescimento da consulta, mas não mede latência, alocação ou cardinalidade de
produção.

## O que este relatório não é

Os achados são evidência consultiva. Este documento não calcula EQI, não aprova
gate, não edita o código-fonte, não publica comentários e não altera o estado
do ciclo de vida.
