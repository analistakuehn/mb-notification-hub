# Missão do estágio REFINE

## Contexto imutável

| Campo | Valor |
|---|---|
| SPEC | `SPEC-001` |
| Manifesto | `docs/SPEC-001/manifest.md` |
| Fonte aprovada | `docs/SPEC-001/specification.md` |
| Requisitos aprovados | `docs/SPEC-001/requirements/core/` |
| Workflow | `araia:REFINE` |
| Hash do plano | `sha256:d634c9f1d35554a2d91df4ee51a9d456ee6a0a8f00b475518db4b0bcb4fd5ee6` |
| Adapter | `dotnet` (primary) |
| Modo | `brownfield` |
| Perfil de entrega | `standard` (`resolved`) |
| Colaboração | `auto` |
| Idioma | `pt-BR` |
| Stack Profile | `.araia/stack-profile.yaml` (`# manually-edited: true`) |
| Gate | `G3` |
| Início | `2026-08-31T09:59:08-03:00` |

## Cenário concreto

Quando `Notifications` aceita uma solicitação com um conjunto de anexos
liberados, o claim indivisível em `AttachmentManagement` (`ER-006`) e o
snapshot imutável do manifesto aceito (`ER-009`) precisam ser atomicamente
consistentes com a invariante transacional vigente da ingestão, que confirma
`notification`, `idempotency_key`, `platform.outbox` e o `audit_event` em uma
única transação (`src/Platform.Api/Modules/Notifications/AGENTS.md:68-77`), e
precisam sobreviver a tentativa, retry e fallback sem consultar metadado
mutável.

O cenário cobre `SEED-005` e `SEED-007` do mapa de implementação aprovado.
Ambos carregam gate obrigatório antes da integração com `Notifications`.

### Perguntas que o mapa de impacto deve responder

1. Qual alternativa de `ER-006`, transação compartilhada por contrato ou
   reserva idempotente com compensação, é compatível com o `IngestionWriter`
   atual sem acoplar `AttachmentManagement` à `DbTransaction` de
   `Notifications`.
2. Onde o snapshot de `ER-009` é persistido e como `Features/Pipeline`,
   `Features/Dispatching` e `Features/Fallback` o leem sem tocar estado mutável
   do anexo.
3. O raio de impacto em `RequestNotification.Command`,
   `RequestNotification.PayloadHash`, `IngestionWriter` e nas tabelas do
   módulo, com o caminho sem anexos preservado exatamente (`ER-008`).
4. Quais falhas injetadas provam a convergência de órfãos (`PAC-002`,
   `PAC-010`) e quais testes e fitness functions as cobrem.
5. Quais decisões exigem ADR próprio em vez de permanecerem `inline`.

## Plano de contribuições

1. `dotnet-impact-analysis-profile`, fase `evidence`, perfil obrigatório em
   `adapters/dotnet/references/contributions/impact-analysis.md`. Vincula
   `dotnet-architect` para limite, contrato, migração e NFR; `dotnet-engineer`
   para fonte, teste, pacote, configuração e validação; `dotnet-specialist`
   somente quando runtime, SDK, compilador, build ou internals do framework
   fizerem parte do cenário.
2. `dotnet-stack-profile`, fase `prepare`, consumindo o perfil manual existente
   sem sobrescrevê-lo.
3. `dotnet-system-design`, fase `execute`, com
   `--from-stage-context --scenario docs/SPEC-001/specification.md`.
4. `dotnet-refine-unknowns-validator`, fase `verify`,
   `scripts/check-document-unknowns.mjs` contra o mapa consolidado.

## Saídas candidatas

- `docs/SPEC-001/refinements/00-refinement-consolidated.md`.
- Relatórios por lente em `docs/SPEC-001/refinements/{NN}-{slug}.md`, somente
  quando a execução produzir lentes separáveis.
- Até dois SVGs em `docs/SPEC-001/refinements/diagrams/`, gerados em
  `.staging/diagrams/` e promovidos antes da limpeza.

## Enriquecimento visual

Limite rígido de dois SVGs novos. Candidatos preferenciais: mapa de impacto da
mudança e comparação entre estado atual e estado-alvo do limite transacional.
Reusar por link relativo o fluxo crítico já publicado em
`docs/SPEC-001/requirements/diagrams/attachment-management-critical-flow.svg`
em vez de redesenhá-lo. Nenhum SVG órfão, nenhuma referência quebrada, nenhuma
visão duplicada.

## Restrições

- O estágio é somente leitura sobre `src/` e `tests/`. Nenhum arquivo de
  implementação é criado ou alterado.
- Requisitos aprovados não são editados silenciosamente. Cada achado declara
  `no requirements change` ou nomeia a atualização necessária antes do `G3`.
- Separar fatos observados, inferências, recomendações, riscos, contratos
  afetados, validação e decisões que exigem ADR.
- Citar cada afirmação sobre o estado atual com `arquivo:linha` ou evidência de
  comando.
- Omitir informação sem suporte. A invocação não recebeu `--resolve-unknowns`.
- Classificar cada achado como `ALIGNED`, `GAP`, `RISK` ou `OPPORTUNITY`.
- Todo risco `HIGH` precisa de plano de mitigação; todo achado `CRITICAL`
  precisa de resolução ou reconhecimento explícito antes do `G3`.
- Manter todo o conteúdo narrativo em português brasileiro.
- Não promover artefatos nem avaliar o gate antes da aprovação de conteúdo. O
  runner é o único a avaliar `G3` e mutar o estado do estágio.
