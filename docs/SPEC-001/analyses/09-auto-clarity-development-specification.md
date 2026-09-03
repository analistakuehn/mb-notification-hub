# Auto-Clarity: especificação de desenvolvimento

**Modo**: estrito  
**Alvo**: artefato de pipeline  
**Resultado inicial**: não conforme

## Achados

| Severidade | Local | Achado | Disposição |
|---|---|---|---|
| BLOCKER | `01-development-specification.md:226` | O registro atribui aceitação de rollout e rollback ao `dotnet-architect`, mas o parecer foi consultivo e não aprovou ciclo de vida. | Remover a aceitação e manter a decisão condicionada à validação executável. |
| BLOCKER | `01-development-specification.md:143,222,237` | Alegações brownfield sobre dispatch, S3 e submissão ambígua não citam caminhos completos e alcançáveis. | Acrescentar evidência `arquivo:linha` e preservar a limitação do padrão S3 atual. |
| WARN | `01-development-specification.md:53-55,82-84` | O mapa de rastreabilidade omite relações declaradas entre `CAP-003` e `PAC-002`, `CAP-004` e `PAC-013`, `CAP-005` e `PAC-011`. | Alinhar o mapa às declarações das capacidades. |
| WARN | `01-development-specification.md:112,221,233,235` | Algumas evidências usam apenas o nome do arquivo. | Substituir por caminhos relativos completos ao repositório. |

O verificador confirmou que os links resolvem no destino final previsto e que as decisões condicionadas possuem responsável, evidência e condição de revisão. Nenhum arquivo foi alterado pelo verificador.
