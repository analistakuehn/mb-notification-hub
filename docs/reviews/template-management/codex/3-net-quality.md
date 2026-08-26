---
language: pt-BR
lens: STK
lens-name: .NET Quality
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 3
---

# Lente `STK`: .NET Quality

Idiomas .NET, fluxo de tipos, semântica de framework, compatibilidade de
protocolo e comportamento da biblioteca Scriban fixada pelo projeto.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `STK-002` | `MEDIUM` | **RESOLVIDO** | `include` passa pela análise, mas falha em toda renderização |
| `STK-001` | `MEDIUM` | **PENDENTE** | A conversão numérica para double perde precisão e aceita não finitos |
| `STK-003` | `LOW` | **PENDENTE** | If-Match aceita ETag fraco e não interpreta listas de tags |

---
## `STK-001` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | `189-192` |
| tipo-de-evidência | teste-executado |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect e dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. A conversão numérica continua caindo em `double`, com perda de precisão e aceitação de não finitos. |

**A conversão numérica para double perde precisão e aceita não finitos.**

Evidência:

    case JsonValueKind.Number:
        return element.TryGetInt64(out var integer) ? integer : element.GetDouble();

Um diagnóstico executado com o mesmo fluxo converteu
0.10000000000000001 em 0.1 e 1e400 em Infinity. A validação do payload aceita
ambos porque verifica apenas JsonValueKind.Number.

Impacto: valores aceitos podem ser arredondados ou renderizados como não
finitos, fazendo a mensagem divergir do payload original.

Recomendação: definir o domínio numérico suportado e preservar o literal ou
usar tipos exatos, rejeitando valores não representáveis antes da renderização.

Verificação: cobrir alta precisão, inteiro além de Int64 e expoente extremo.
Exigir preservação definida ou erro controlado, nunca arredondamento silencioso
ou Infinity.

---

## `STK-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | `27-50` |
| tipo-de-evidência | teste-executado |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado pela primeira das duas alternativas que o achado oferece: bloquear na análise. `include` e `include_join` foram removidos do builtin do sandbox, e a lista de isenções passou a ser derivada dessa superfície, então a análise agora os reporta como variável não declarada e `variables-declared` reprova a publicação. Verificado por teoria em `ScribanSandboxTests` cobrindo os dois nomes. A segunda alternativa, fornecer um loader restrito, não foi tomada e continua disponível caso a inclusão venha a ser desejada. |

**`include` passa pela análise, mas falha em toda renderização.**

Evidência:

    private static readonly string[] BuiltinGlobals =
        ["array", "blank", "date", "empty", "html", "include", "math",
         "object", "regex", "string", "timespan"];

    var template = Template.Parse(source, sourcePath);
    ...
    return new TemplateSourceAnalysis(true, null, collector.UsedVariables());

O TemplateContext usado na renderização não configura TemplateLoader. Um
diagnóstico executado com Scriban 7.2.6 confirmou parsing sem erros para uma
expressão include e exceção Unable to include durante o render.

Impacto: uma versão pode passar pela validação e pela aprovação, ser publicada
e falhar em todas as notificações que usam include.

Recomendação: bloquear include durante a análise ou fornecer um loader restrito
a fontes imutáveis e autorizadas, com resolução validada antes da publicação.

Verificação: exigir bloqueio de publicação para inclusão ausente. Na alternativa
com loader, provar que inclusão autorizada funciona e que nomes ausentes ou não
permitidos são recusados antes da publicação.

---

## `STK-003` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Infrastructure/Http/EntityTags.cs` |
| linha | `45-53` |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. `If-Match` continua aceitando validador fraco e tratando o cabeçalho como tag única. |

**If-Match aceita ETag fraco e não interpreta listas de tags.**

Evidência:

    var value = ifMatch.Trim();
    if (value.StartsWith("W/", StringComparison.Ordinal))
    {
        value = value[2..];
    }

    return value.Trim('"');

A normalização remove o prefixo de fraqueza e compara o resultado como se fosse
um ETag forte. O método também trata todo o valor de If-Match como uma única
tag.

Impacto: a API aceita um validador fraco onde deveria falhar e rejeita listas
válidas que contenham o ETag corrente.

Recomendação: analisar o cabeçalho como lista, rejeitar tags fracas e
malformadas e aceitar quando qualquer tag forte coincide com a corrente.

Verificação: provar que uma tag fraca falha, que uma lista com a tag forte
corrente passa e que valores malformados não são normalizados silenciosamente.
