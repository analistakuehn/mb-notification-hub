---
language: pt-BR
lens: STK
lens-name: .NET Quality
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 6
---

# Lente `STK`: .NET Quality

Idiomas .NET, fluxo de tipos e nulabilidade, semântica async, uso de framework e
API, analisadores e compatibilidade de toolchain.

A evidência de vários achados desta lente vem da leitura da fonte do pacote
Scriban 7.2.6, a versão fixada em `Directory.Packages.props`. O build está limpo
em `net10.0` sob `TreatWarningsAsErrors` com `AnalysisLevel latest-recommended`,
portanto nenhum achado aqui decorre de analisador suprimido: as duas supressões
existentes no módulo estão justificadas e corretas.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `STK-001` | `HIGH` | **RESOLVIDO** | `LimitToString` do Scriban não é ajustado, e o `[Range]` aceita valores... |
| `STK-003` | `LOW` | **RESOLVIDO** | Lista de builtins mantida à mão e já fora de sincronia com o pacote... |
| `STK-004` | `LOW` | **RESOLVIDO** | `CancellationTokenSource` descartada enquanto o render abandonado ainda... |
| `STK-006` | `LOW` | **RESOLVIDO** | Bloco `catch` provavelmente inalcançável dá a impressão de duas rotas... |
| `STK-002` | `MEDIUM` | **ADIADO** | `EnableRelaxedMemberAccess` no padrão `true`: membro inexistente... |
| `STK-005` | `LOW` | **PENDENTE** | `record` público de contrato sem igualdade estrutural |

---
## `STK-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/TemplatingOptions.cs` |
| linha | 31-32 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | `LimitToString` passou a ser fixado em `MaxOutputChars + 1`, de propósito acima do teto do sink, para que o sink falhe em vez de o engine truncar. Confirmado por sonda antes da correção: 1.048.579 caracteres com cauda em reticências.  **Recomendação parcialmente obsoleta**: a segunda metade, reduzir o limite superior do `[Range]`, deixou de fazer sentido. Com `LimitToString` derivado da opção, todo valor do intervalo passou a ser efetivamente suportado.|

**`LimitToString` do Scriban não é ajustado, e o `[Range]` aceita valores inalcançáveis.**

Evidência:

```csharp
[Range(1, 16_000_000)]
public int MaxOutputChars { get; init; } = 1_000_000;
```

Em Scriban `TemplateContext.cs:151`, `LimitToString = 1048576;`, e em
`TemplateContext.cs:729-748` todo `Write` passa por `WriteOutputChunk`, que
trunca nesse limite e chama `WriteOutputLimitEllipsis()`, escrevendo `"..."`.

Impacto: o engine nunca ajusta `LimitToString`, que fica no padrão de 1.048.576
caracteres. Como o padrão do módulo (1.000.000) fica logo abaixo, a barreira
própria dispara primeiro e o comportamento hoje está correto. Mas o `[Range]`
aceita até 16.000.000: qualquer valor configurado acima de 1.048.576 é
inalcançável, e o efeito não é "limite maior", é truncamento silencioso do
Scriban com reticências anexadas. A saída truncada segue o fluxo normal, é
normalizada, tem hash canônico calculado sobre ela e é auditada como se fosse a
mensagem completa. Uma alteração de configuração que passa na validação de
opções passa a corromper conteúdo em silêncio.

Recomendação: definir `LimitToString` a partir de `MaxOutputChars` no
`TemplateContext`, mantendo a barreira própria como o mecanismo que falha em vez
de truncar, e reduzir o limite superior do `[Range]` ao valor efetivamente
suportado.

Verificação: configurar `MaxOutputChars = 2_000_000`, renderizar saída de 1,5
milhão de caracteres e afirmar que o resultado tem o tamanho pedido e não termina
em reticências, e que acima do teto a falha é explícita.

---

## `STK-002` · ADIADO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 297-300 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **ADIADO** |
| nota de estado | Deliberado, com razão registrada. Ligar o modo estrito em runtime não muda template que hoje funciona: quebra o que já está quebrado produzindo buraco. Só que piora o modo de falha onde mais custa, porque um SMS de autenticação com o nome faltando ainda entrega o código, e com render falhando a pessoa não recebe nada. Trocar entrega degradada por entrega zero em mensagem de autenticação é regressão. A metade correta é detectar em publicação, estendendo o coletor a registrar caminho de membro para que `variables-declared` o confronte com o schema, e ela pertence ao catálogo de validação. |

**`EnableRelaxedMemberAccess` no padrão `true`: membro inexistente renderiza vazio.**

Evidência, o coletor de análise que só registra a raiz:

```csharp
public override void Visit(ScriptMemberExpression node)
    // Only the target counts: 'user.name' reads the variable 'user',
    // and 'name' is a member of it, not a template variable.
    => node.Target?.Accept(this);
```

Em Scriban `TemplateContext.cs:144`, `EnableRelaxedMemberAccess = true;`, não
sobrescrito pelo módulo. O comportamento é confirmado pelo próprio teste do
repositório, que espera acesso a membro inexistente render como string vazia com
sucesso.

Impacto: `StrictVariables = true` só protege variáveis raiz. Membros aninhados
são silenciosos nos dois momentos. Na publicação, o coletor não os registra,
então o check `variables-declared` não os enxerga. No despacho, o acesso relaxado
faz um nome de membro digitado errado renderizar vazio em vez de falhar. O
resultado é uma notificação real enviada ao cliente com um buraco no lugar do
nome, sem erro em nenhuma camada. O `variables-schema` do módulo já carrega
estrutura suficiente para conferir o primeiro nível de membro.

Recomendação: definir `EnableRelaxedMemberAccess = false` no `TemplateContext`
para que acesso a membro inexistente falhe o render, e estender o coletor a
registrar o caminho de membro, para que `variables-declared` possa confrontá-lo
com o schema.

Verificação: renderizar um acesso a membro inexistente sobre um payload que
declare o membro correto e afirmar falha nomeando o membro. Validar a mesma
versão e afirmar check `variables-declared` reprovado.

---

## `STK-003` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 28-29 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | A lista de isenções passou a ser derivada da própria superfície do sandbox. |

**Lista de builtins mantida à mão e já fora de sincronia com o pacote fixado.**

Evidência:

```csharp
private static readonly string[] BuiltinGlobals =
    ["array", "blank", "date", "empty", "html", "include", "math", "object", "regex", "string", "timespan"];
```

Em Scriban `Functions/BuiltinFunctions.cs:32-43`, o conjunto registrado inclui
também `include_join`.

Impacto: a lista é um espelho mantido à mão do conjunto de builtins do Scriban e
já diverge da versão fixada. Um template que referencie o builtin ausente faz o
check `variables-declared` reprovar com "Variable is used but not declared",
bloqueando a publicação por um falso positivo cuja causa não é visível ao autor.
O acoplamento também é frágil: uma atualização de patch do Scriban que acrescente
um builtin reintroduz a divergência.

Recomendação: derivar a lista do objeto builtin efetivamente empurrado no
contexto, em vez de escrevê-la. Melhor ainda, derivá-la do objeto builtin
restrito recomendado em `SEC-002`, o que torna as duas superfícies consistentes
por construção.

Verificação: teste que compara a lista com as chaves do objeto builtin
efetivamente empurrado e falha em qualquer divergência.

---

## `STK-004` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | média |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 74, 101-104 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-specialist (convergência independente) |
| **estado** | **RESOLVIDO** |
| nota de estado | Não há mais `CancellationTokenSource` sob render vivo; a fonte vinculada morre com o método, que agora é síncrono.  **Ponto de vigilância obsoleto**: a dúvida registrada, se o Scriban registra callback no token, ficou sem objeto. Não há mais render abandonado consultando o token.|

**`CancellationTokenSource` descartada enquanto o render abandonado ainda a consulta.**

Evidência:

```csharp
using var renderCancellation = new CancellationTokenSource();
...
await DiscardInFlightRenderAsync(renderCancellation, renderTask).ConfigureAwait(false);
cancellationToken.ThrowIfCancellationRequested();
return Result.ValidationError<string>(TimeLimitMessage());
```

Impacto: o `using` descarta a fonte no retorno, enquanto a tarefa abandonada
segue rodando e continua consultando aquele token pelos pontos de verificação do
Scriban. Hoje é benigno, porque `ThrowIfCancellationRequested` e
`IsCancellationRequested` são seguros após descarte, e o cancelamento é
sinalizado antes. Passa a não ser no instante em que qualquer código do caminho
registrar um callback no token, porque `Register` lança `ObjectDisposedException`
sobre a fonte descartada. Essa exceção seria observada pela continuação já
existente e portanto não derruba o processo, mas mascara a causa real do descarte
em qualquer diagnóstico futuro. É armadilha latente exatamente no ponto que o
comentário do código descreve como seguro. O que falta para elevar a confiança:
confirmar que nenhuma versão futura do Scriban registra callback no
`CancellationToken` do contexto, o que é verdade em 7.2.6.

Recomendação: não usar `using` aqui. Descartar a fonte na continuação anexada,
quando a tarefa abandonada realmente termina, e no caminho de sucesso após o
`await`.

Verificação: teste que provoca o deadline e, na continuação, afirma que o token
permanece utilizável até o término do render abandonado.

---

## `STK-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | média |
| arquivo | `Integration/V1/ClassPolicyDefinition.cs` |
| linha | 23 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Contrato `Integration/V1`, ainda não tratado. |

**`record` público de contrato sem igualdade estrutural.**

Evidência:

```csharp
public sealed record ClassPolicyDefinition
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<Channel> ChannelsAllowed { get; init; }
    public required IReadOnlyList<DeliveryPlanStep> DeliveryPlan { get; init; }
```

`Channel` é `sealed class` com `Value`, fábricas estáticas e `ToString()`
sobrescrito, mas sem `Equals` nem `GetHashCode`, ou seja, igualdade de
referência.

Impacto: a igualdade que o compilador gera para o `record` compara as duas listas
por referência, porque `IReadOnlyList<T>` não tem igualdade estrutural. Duas
definições lidas do mesmo documento JSON nunca são iguais por `==`. Isso
contraria a expectativa que a escolha de `record` cria em quem consome o
contrato: um consumidor que use igualdade para responder "a política mudou"
recebe sempre "sim". `DeliveryPlanStep` escapa por acidente, porque as instâncias
de `Channel` são internadas nas estáticas e a igualdade de referência coincide
com a de valor, propriedade que se mantém apenas enquanto o construtor
permanecer privado. Confiança média porque nenhum consumidor atual compara
definições por igualdade: o achado é sobre um contrato público cujo tipo
escolhido promete uma semântica que ele não entrega.

Recomendação: se a igualdade estrutural importa para o contrato, tornar `Channel`
um `readonly record struct` sobre o valor canônico, o que também resolve
`ARC-003`, e trocar as listas por um tipo com igualdade estrutural. Se não
importa, documentar no XML doc do tipo que a comparação deve ser feita pelo hash
de conteúdo publicado, que é o identificador que o desenho já elegeu para "é a
mesma definição".

Verificação: teste que leia duas vezes o mesmo documento e afirme o
comportamento decidido para `==`, ou o XML doc apontando explicitamente para o
hash como critério de identidade.

---

## `STK-006` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | baixa |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 121-124 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | O `catch` inalcançável foi removido e a classificação ficou explícita em um único ponto. |

**Bloco `catch` provavelmente inalcançável dá a impressão de duas rotas de tratamento.**

Evidência:

```csharp
catch (RegexMatchTimeoutException)
{
    return Result.ValidationError<string>(TimeLimitMessage());
}
```

Em Scriban `TemplateContext.cs:915-918`:

```csharp
catch (Exception ex) when (!(ex is ScriptRuntimeException))
{
    var toThrow = new ScriptRuntimeException(scriptNode.Span, ex.Message, ex);
```

Impacto: toda exceção lançada durante a avaliação de um nó é embrulhada em
`ScriptRuntimeException`, e o `catch` anterior já trata o caso pela
`InnerException`. Este segundo `catch` parece inalcançável, o que dá a impressão
de haver duas rotas de tratamento quando há uma. O risco é o inverso do usual: um
leitor confia no bloco morto e não percebe que qualquer exceção não prevista,
incluindo a de memória descrita em `SEC-010`, cai no `catch` de
`ScriptRuntimeException` e vira erro de validação. Confiança baixa porque falta
cobertura de execução provando que o bloco nunca é atingido.

Recomendação: remover o `catch` inalcançável e tornar explícita a classificação
por `InnerException` no único `catch` restante, distinguindo timeout de expressão
regular, teto de saída, falha de script e falha operacional.

Verificação: instrumentar cobertura sobre a suíte do engine e afirmar zero
acertos no bloco antes da remoção.
