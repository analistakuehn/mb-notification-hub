---
language: pt-BR
lens: SEC
lens-name: Segurança
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 2
---

# Lente `SEC`: Segurança

Proteção de entrada e dados, caminhos de abuso, antifraude, auditabilidade e
retenção.

Autorização nomeada e rate limiting já são impostos pelos testes de arquitetura
de segurança. Os achados tratam de controles de conteúdo e dados que não
alcançam o resultado persistido ou renderizado.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `SEC-001` | `HIGH` | **RESOLVIDO** | A validação de URLs pode ser contornada no conteúdo renderizado |
| `SEC-002` | `HIGH` | **PENDENTE** | Motivos livres podem persistir dados sensíveis na auditoria imutável |

---
## `SEC-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/RenderedDestinationPolicy.cs` |
| linha | `14-61` |
| tipo-de-evidência | teste-executado e leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect, dotnet-engineer e dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado com uma política compartilhada aplicada ao conteúdo final. O preview e o render publicado agora validam depois da interpolação, do layout e da normalização, antes da resposta e do hash, incluindo as formas full e masked. A política usa `System.Uri` e `IdnHost`, trata caixa mista, IPv6, IDN, userinfo, href, src, srcset, CSS url() e meta refresh, e falha fechada para entradas ilegíveis. A autoria exige uma variável global inteira com format url ou uri para destinos dinâmicos. Os erros retornam somente host canônico seguro ou marcador fixo, sem query, token, CPF ou userinfo. A validação executada passou 122/122 testes unitários afetados, 39/39 integrações sem skips, 1333/1333 testes unitários completos e build sem avisos ou erros; a avaliação independente confirmou o fechamento. |

**A validação de URLs pode ser contornada no conteúdo renderizado.**

Evidência:

    foreach (VariableDeclaration declaration in
             declarations.Where(declaration => declaration.IsUrl))
    {
        ...
        if (!IsAllowedUrl(template, value))
        {
            return ...;
        }
    }

A allowlist cobre somente variáveis declaradas com format url ou uri. Uma
variável string pode renderizar uma URL externa sem passar pelo laço. O
resultado final depois da interpolação e do layout não é examinado para todos
os canais. A expressão regular de links literais usa https sem comparação
independente de caixa.

Um diagnóstico executado confirmou que HTTPS://evil.example não casa com a
expressão regular, embora Uri.TryCreate reconheça uma URL HTTPS absoluta com
host evil.example.

Impacto: templates podem entregar links de phishing ou exfiltração fora da
allowlist, inclusive em classes que proíbem links.

Recomendação: extrair e validar todos os destinos depois da interpolação e da
aplicação do layout, usando semântica de URI sem distinção de caixa. Exigir
também tipo URL para variáveis usadas como destino.

Verificação: testar URL em variável string, esquema HTTPS em caixa alta ou
mista, href, src, layout, domínio proibido e domínio autorizado. A falha não
deve expor query string ou dado pessoal.

---

## `SEC-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Features/Mutations/DisableTemplate/DisableTemplate.Handler.cs` |
| linha | `44-53` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect e dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Converge com `SEC-014` do relatório paralelo em `../claude/`. |

**Motivos livres podem persistir dados sensíveis na auditoria imutável.**

Evidência:

    var entry = new AuditEntry
    {
        ...
        DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason }),
        OccurredAt = timeProvider.GetUtcNow(),
    };

Os validadores limitam somente presença e tamanho. DeprecateTemplate,
DisableLayout e DeprecateLayout repetem o fluxo. O destino é uma trilha
append-only com exportação WORM.

Impacto: CPF, email, token ou outro dado sensível inserido pelo operador pode
entrar em retenção imutável, sem caminho normal de exclusão.

Recomendação: persistir um reasonCode de vocabulário controlado. Se uma
narrativa for indispensável, mantê-la em armazenamento com retenção própria e
gravar na auditoria somente código e referência não pessoal.

Verificação: enviar motivos contendo CPF, email e token e confirmar que nenhum
valor bruto aparece nos detalhes, no texto canônico ou na exportação, mantendo
um código auditável.
