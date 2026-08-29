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
| `SEC-002` | `HIGH` | **RESOLVIDO** | Motivos livres podem persistir dados sensíveis na auditoria imutável |

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

## `SEC-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Features/Mutations/DisableTemplate/DisableTemplate.Handler.cs` |
| linha | `44-53` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect e dotnet-engineer |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado tirando as palavras da trilha, e não tentando adivinhar o que elas contêm. A trilha grava a razão canônica mais `noteRef`, identificador aleatório da linha guardada em `lifecycle_note`, tabela deste contexto que aceita exclusão; o texto deixa de existir em `details`, no texto canônico e no NDJSON exportado. **A detecção de dado pessoal na borda foi recusada por medição, não por gosto.** Sobre texto operacional em pt-BR o dígito verificador divide o falso positivo por cem mas não o elimina, ficando entre 1% e 12% conforme o comprimento da corrida numérica, com recusa medida de `versao 10.0.26200.1234567890` e de sha de commit; no sentido oposto, 15 de 16 grafias do mesmo identificador escapam da expressão canônica, o padrão usual não compila no dialeto `NonBacktracking` da casa porque usa lookaround, e `\d` em .NET casa dígito árabe-índico que um dígito verificador escrito com `c - '0'` rejeita calado, de modo que o detector nasceria cego com teste verde. Pesou também que a alternativa da borda não quebrava nenhuma asserção da suíte, ou seja nasceria sem contenção. **`noteRef` é `Guid` aleatório e nunca digest**: medidos 4.997.550 hashes SHA-256 por segundo em um thread, o digest de um identificador de nove dígitos cai em 9 segundos sem GPU, e um digest ainda amarraria para sempre duas linhas que carregassem a mesma frase, que é justamente o vínculo que o esquecimento existe para quebrar. **O apagamento é ato e não ausência**: apaga a linha e grava a ação com o mesmo `noteRef`, na mesma transação e na forma do módulo, porque sem esse evento "nunca houve nota" e "houve e foi apagada" ficam indistinguíveis, e lacuna silenciosa é a assinatura de adulteração. Não tem superfície HTTP, e isso é deliberado: nenhum contexto do sistema expõe endpoint de esquecimento, e o primeiro é capacidade com revisão própria, não carona numa mudança de armazenamento. A retenção é somente sob pedido, sem expurgo por idade, o que também evitou subir um job de fundo em toda réplica da API e em dez fixtures de integração num módulo que não tem worker role. **Três eras passam a ser legíveis da própria linha, sem relógio**: sem `note` e sem `noteRef` é a prosa no campo de razão; com `note`, as palavras estão na trilha e não saem de lá; com `noteRef`, são apagáveis. A era do meio é aquela em que um pedido de esquecimento não pode ser atendido por inteiro. Contenção nova em `tests/Platform.SecurityArchTests` reprova produtor de detalhes que nomeie o texto de uma nota, e uma sonda de mutação mostrou que a primeira versão da regra escapava por acesso condicional, o que também endureceu a regra irmã de mensagem de check, que tinha o mesmo ponto cego. **Refutações.** O eixo de exposição que a ficha supõe não existe: a nota nunca chegou ao relatório mensal arquivado nem ao endpoint de evidência, porque a leitura periódica agrupa só por `reason` e a composição mensal não copia `details`; o que sustentava a severidade era irreversibilidade, com Object Lock em modo Compliance por cinco anos e exportação diária ligada por padrão, ou seja janela de horas para corrigir. Também não confere a frase de que os validadores limitavam presença e tamanho: a razão já era vocabulário fechado desde `SEC-014`, e só a nota era irrestrita, de modo que a primeira metade da recomendação já estava executada quando a ficha foi lida. O caminho citado no cabeçalho era o correto na revisão desta revisão; `9baafdb` o moveu para `Features/Templates/`. Validação executada: build sem avisos nem erros, 1342 testes unitários, 8 de arquitetura, 7 de segurança e 646 de integração com 2 pulados por desenho e nenhuma falha. **Permanece aberto**: o apagamento não tem gatilho em produção, e `RemoveSuppression` do ContactConsent grava justificativa livre, obrigatória e ao lado do identificador do titular na mesma trilha, que é a mesma classe de defeito por outra porta, com dono e migração próprios, e fora desta ficha. |

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
