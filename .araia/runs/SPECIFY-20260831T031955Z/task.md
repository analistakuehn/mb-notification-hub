# Missão do estágio SPECIFY

## Contexto imutável

| Campo | Valor |
|---|---|
| SPEC | `SPEC-001` |
| Manifesto | `docs/SPEC-001/manifest.md` |
| Fonte aprovada | `docs/SPEC-001/specification.md` |
| Workflow | `araia:SPECIFY` |
| Adapter | `dotnet` |
| Modo | `brownfield` |
| Perfil solicitado | `standard` |
| Colaboração | `auto` |
| Idioma | `pt-BR` |
| Gate | `G2` |

## Plano de contribuições

1. `dotnet-discovery`, fase `evidence`, com inspeção somente leitura. Preservar `.araia/stack-profile.yaml` porque contém `# manually-edited: true` e a decisão do usuário proibiu a sobrescrita.
2. `dotnet-stack-profile`, fase `prepare`, usando o perfil manual existente e registrando divergências contra evidências observadas.
3. `dotnet-requirements-template`, fase `prepare`.
4. `dotnet-specification`, fase `execute`, com `dotnet-architect`, `dotnet-engineer` e `dotnet-specialist` em paralelo.
5. `dotnet-unknowns-validator`, fase `verify`.

## Saídas candidatas

- `docs/SPEC-001/analyses/*.md`.
- `docs/SPEC-001/requirements/core/01-development-specification.md`.
- `docs/SPEC-001/requirements/core/02-implementation-map.md`.
- `docs/SPEC-001/requirements/core/03-verification-plan.md`.
- Até dois SVGs em `docs/SPEC-001/requirements/diagrams/`, somente quando o protocolo visual justificar.

## Restrições

- Consumir o Initiative Brief aprovado sem novo questionário concorrente.
- Produzir exatamente os três artefatos centrais. Famílias condicionais sem opt-in permanecem inline quando seus gatilhos forem acionados.
- Omitir informações sem suporte. A invocação não recebeu `--resolve-unknowns` nem `--include`.
- Separar fatos brownfield de intenção de produto e citar afirmações do estado atual com `arquivo:linha` ou evidência de comando.
- Manter todo conteúdo narrativo em português brasileiro.
- Avaliar performance, capacidade, Event Storming, bounded contexts e necessidade de visual antes do checkpoint de conteúdo.
- Não promover artefatos nem atualizar o gate antes das aprovações obrigatórias.
