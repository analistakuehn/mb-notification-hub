---
language: pt-BR
lens: PRF
lens-name: Performance
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 8
---

# Lente `PRF`: Performance

Caminhos quentes, alocações, I/O, concorrência, latência, custo de renderização
e evidência de runtime medida.

Este arquivo cobre dois momentos com autoridades diferentes, e a distinção
importa para ler qualquer afirmação abaixo.

**Na revisão**, a autoridade era somente leitura, o que não autoriza benchmark,
`dotnet-counters` nem `dotnet-trace`. Nenhum dos sete achados originais trazia
medição executada: cada um nomeia o mecanismo e traz o experimento que o
confirmaria, nunca a afirmação de que algo "pode ser lento".

**Na remediação**, foi possível executar. Onde há medição real ela está na nota
de estado do achado, com o número. Onde não há, a nota descreve mudança
estrutural verificada por leitura e pela suíte, e não afirma ganho medido. Um
achado resolvido sem medição continua sem medição: o defeito saiu do código,
o número nunca foi levantado.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `PRF-001` | `HIGH` | **RESOLVIDO** | Render abandonado continua consumindo CPU e um item de trabalho do pool |
| `PRF-002` | `MEDIUM` | **RESOLVIDO** | `Task.Delay` órfã por render, até 10 temporizadores vivos por... |
| `PRF-006` | `LOW` | **RESOLVIDO** | Até 100 construções de `Regex` por validação, único ponto fora do... |
| `PRF-003` | `MEDIUM` | **RESOLVIDO** | Chave de cache montada antes da normalização, e `Clear()` total exposto... |
| `PRF-004` | `MEDIUM` | **RESOLVIDO** | O validador é o único dos três contratos publicados que não usa o cache |
| `PRF-008` | `LOW` | **RESOLVIDO** | As consultas administrativas materializam todo o histórico de versões |
| `PRF-005` | `MEDIUM` | **PENDENTE** | Render duplo para a forma mascarada, correto por desenho e não medido |
| `PRF-007` | `LOW` | **PARCIAL** | `ConcurrentDictionary.Count` toma todos os locks no caminho de miss |

---
## `PRF-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 97-104, 131-141 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | O trabalho abandonado deixou de existir: `Task.Run` mais `Task.WhenAny` foram substituídos por um `CancellationTokenSource` vinculado com `CancelAfter`, e o render para cooperativamente nos checkpoints do engine. Sem tarefa órfã, não há o que limitar, então o limitador de concorrência sugerido tornou-se desnecessário. |

**Render abandonado continua consumindo CPU e um item de trabalho do pool.**

Evidência:

```csharp
// Deadline or caller cancellation: either way the in-flight render
// is discarded before anything propagates, so the engine is asked
// to stop and its eventual failure is never orphaned.
await DiscardInFlightRenderAsync(renderCancellation, renderTask).ConfigureAwait(false);
...
await renderCancellation.CancelAsync().ConfigureAwait(false);
_ = renderTask.ContinueWith(static task => _ = task.Exception, ...);
```

Impacto: o comentário afirma que o render em voo é descartado. O que é
descartado é a `Task`, não o trabalho. O `CancellationToken` do
`TemplateContext` só é observado nos pontos de verificação do Scriban, então uma
única chamada custosa (`string.pad_right`, uma expressão regular via
`regex.match`, uma concatenação grande) roda até o fim numa thread do pool
depois que o chamador já recebeu a falha e liberou a requisição. Com o endpoint
de preview a 1000 requisições por minuto por principal e `QueueLimit = 0`,
renders abandonados acumulam trabalho de CPU sem teto, e cada render consome um
item de trabalho do pool via `Task.Run`, com até 10 por notificação. É o caminho
direto para thread-pool starvation dentro do próprio limite de taxa configurado.

Recomendação: encurtar o intervalo entre verificações do token e tratar o render
como recurso contabilizado, com um limitador de concorrência dedicado (semáforo
com teto derivado do número de núcleos) separado do orçamento de requisições, de
modo que trabalho abandonado não consuma a capacidade do processo.

Verificação: `dotnet-trace` com o provedor de thread pool durante uma rajada de
renders que estouram o deadline. Afirmar que `ThreadPoolWorkerThreadAdjustment`
não cresce e que a fila de trabalho não acumula depois que as respostas
retornaram.

---

## `PRF-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 91-96 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-specialist (convergência independente) |
| **estado** | **RESOLVIDO** |
| nota de estado | O `Task.Delay` saiu do caminho quente junto com o `Task.WhenAny`. |

**`Task.Delay` órfã por render, até 10 temporizadores vivos por notificação.**

Evidência:

```csharp
Task<string> renderTask = Task.Run(() => template.Render(context));
Task first = await Task.WhenAny(
        renderTask,
        Task.Delay(TimeSpan.FromMilliseconds(options.Value.RenderTimeoutMilliseconds), cancellationToken))
    .ConfigureAwait(false);
if (first != renderTask)
```

Impacto: no caminho de sucesso, que é o normal, nada cancela a `Task.Delay`, e o
token passado a ela é o da requisição, não o do deadline. Cada chamada deixa um
`TimerQueueTimer` mais um `CancellationTokenRegistration` vivos por todo o prazo
configurado (2000 ms por padrão), mesmo quando o render terminou em
microssegundos. O caminho quente multiplica: `RenderFormAsync` chama
`RenderAsync` três vezes para subject, body e bodyText, mais até duas vezes para
os wrappers de layout, e a forma mascarada repete tudo, o que chega a dez
chamadas de engine por notificação. A 500 notificações por segundo isso é da
ordem de 10.000 temporizadores vivos em regime, mais uma
`TaskCanceledException` não observada por delay quando a requisição aborta. No
mesmo trecho, `Task.Run` desloca ao pool até o corpo de SMS de poucas dezenas de
caracteres, trocando trabalho de microssegundos por um agendamento.

Recomendação: substituir o par `Task.Run` mais `Task.Delay` por uma fonte de
cancelamento vinculada com prazo, no formato
`CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` com
`CancelAfter(timeout)`, entregando o token ao `TemplateContext` e dispensando o
`Task.WhenAny`. Isso remove o temporizador residual e devolve o render à thread
chamadora, mantendo o prazo de parede que o `RegexTimeOut` já acompanha.

Verificação: benchmark de `RenderAsync` em laço medindo `active-timer-count` de
`System.Runtime` via `dotnet-counters`. O contador deve deixar de crescer com a
taxa de render. Medir também alocações por chamada, com aquecimento descartado
por braço.

---

## `PRF-003` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedCatalog.cs` |
| linha | 26 |
| tipo-de-evidência | leitura-de-código, depois **medição executada** na remediação |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | As duas metades fecharam. A normalização subiu para o topo dos três sítios, então uma identidade publicada tem exatamente uma entrada, qualquer que seja a grafia recebida. A evicção passou a `MemoryCache` com `SizeLimit` e compactação, atrás da mesma fachada. **Medido**: 21,7 milhões de operações por segundo com 0,000 disputas por mil, contra 12 mil operações por segundo e 72.509 disputas da forma anterior na mesma bancada. Uma premissa da recomendação era falsa e está corrigida abaixo. |

**Chave de cache montada antes da normalização, e `Clear()` total exposto ao chamador.**

Evidência:

```csharp
var cacheKey = $"template:{application}:{templateKey}";
if (cache.TryGetPointer(cacheKey, out PublishedTemplateLookup cached))
```

A normalização acontece depois, dentro da consulta, onde `ApplicationName.Create`
e `TemplateKey.Create` aparam a entrada. O mesmo padrão está em
`PublishedCatalog.cs:98` e em `PublishedTemplateRenderer.cs:139`. A política de
evicção é limpeza total, em `PublishedReadCache.cs:51-54`, com
`MaxEntries = 4096`, e só o sucesso é memoizado.

Impacto: `"auth.otp"`, `" auth.otp"` e `"auth.otp "` produzem três entradas
distintas apontando para o mesmo template, e todas contam como sucesso. Um
produtor pode gerar variantes suficientes para cruzar as 4096 entradas e
disparar a limpeza, que não expulsa a entrada mais fria e sim esvazia o cache
inteiro, incluindo os ponteiros quentes de todas as outras aplicações no
processo. O efeito é degradação do caminho quente de ingestão para leitura no
Postgres a cada resolução, e a repetição mantém o cache permanentemente vazio.
Fora do cenário adversarial, o mesmo defeito faz o mesmo template ocupar várias
entradas por variação inócua de formatação, reduzindo a taxa de acerto sem que
ninguém perceba.

Recomendação: normalizar antes de compor a chave, movendo `ApplicationName.Create`
e `TemplateKey.Create` para o início dos três métodos e usando os valores
canônicos, o que também faz o cache recusar entrada inválida sem tocar o banco.
Em paralelo, trocar a limpeza total por evicção de menor uso recente, ou ao
menos por descarte parcial, para que o transbordo deixe de ser um botão de
esvaziar cache exposto ao chamador.

Verificação: teste que consulte a mesma `(application, templateKey)` com e sem
espaços em volta e afirme `PointerLoads == 1` e `PointerHits == 1`. Teste que
insira `MaxEntries + 1` chaves distintas e afirme que as entradas mais recentes
sobrevivem.

### Uma premissa da recomendação é falsa

"O que também faz o cache recusar entrada inválida sem tocar o banco" não
descreve o código. `PublishedTemplateQueries.FindApplicationTemplateAsync:53-63`
já chamava `ApplicationName.Create` e `TemplateKey.Create` **antes** da consulta
EF, e `FindClassPolicyFromStoreAsync:119-132` fazia o mesmo. Entrada inválida
nunca alcançou o banco. O que ela custava era o escopo de injeção, a máquina de
estado assíncrona e duas avaliações de expressão regular, sem nada memoizado.

Normalizar continua certo, por outros três motivos: uma entrada canônica por
identidade publicada, rejeição antes de qualquer trabalho, e a chave deixa de
ser inflável de fora. Nenhum comentário e nenhum teste da correção afirma
consertar acesso indevido ao banco.

### O alcance é maior do que "variação inócua de formatação"

`Trim()` remove qualquer caractere que `char.IsWhiteSpace` aceite, inclusive
tabulação, quebra de linha e espaço não separável, então o espaço de variantes
por chave canônica é ilimitado. Pior: o validador de ingestão
(`RequestNotification.Validator.cs:21,26`) só exige `NotEmpty` e
`MaximumLength`, sem `Trim` e sem o regex canônico, então a string crua do
produtor chegava a virar chave de cache. Cada variante distinta rendia de duas a
três entradas de ponteiro, com os prefixos `template:`, `policy:` e
`render-context:`, então cerca de 1.366 pedidos com variante nova bastavam para
cruzar as 4.096. O enquadramento honesto é raio de explosão entre inquilinos, e
não negação de serviço anônima: o produtor é autenticado e limitado por taxa,
mas o dicionário é único por processo e a limpeza atingia os ponteiros de todas
as aplicações.

### O que a medição escolheu, e o que ela rejeitou

A política de evicção foi decidida com bancada executada, .NET 10.0.10, em 22
núcleos e replicada com 4. Caminho de miss na fronteira do teto:

| Forma | Operações por segundo | Disputas |
|---|---:|---:|
| Atual, `Count` mais `Clear` | 12 K | 72.509 |
| Contador `Interlocked` mais `Clear` | 1.368 K | 369.600 |
| Contador mais despejo parcial, portão não bloqueante | 169 K | 5.154 |
| Portão bloqueante mais cota sem ordenação | 2.744 K | 96.863 |
| `MemoryCache` com `SizeLimit` e compactação | **10.780 K** | **109** |

`ConcurrentDictionary<TKey,TValue>.Count` foi confirmado por desmontagem do IL
de `System.Collections.Concurrent.dll` 10.0.10: ele chama `AcquireAllLocks`, e
`Clear()` também. São 1.408 locks com 4.096 entradas em 22 núcleos, e 512 em 4
núcleos. Custo de uma leitura de `Count`: 15.659 ns, contra 1,3 ns de um
`Volatile.Read`.

`MemoryCache` não custou dependência nova. A biblioteca está no ref pack e no
runtime pack, `Platform.Api` é SDK Web e o Worker já declara
`Microsoft.AspNetCore.App`. Zero `PackageReference`, zero linha em
`Directory.Packages.props`.

Recomendação aplicada: normalização no topo dos três sítios, e duas instâncias
de `MemoryCache` com `SizeLimit` independentes atrás da fachada, preservando
`TryGetPointer`, `SetPointer`, `TryGetImmutable`, `SetImmutable`, `PointerHits`
e `PointerLoads` com a mesma semântica observável.

Verificação: portão de contenção novo em `tests/Platform.PerformanceTests`, com
linha de base versionada, medindo 21,7 milhões de operações por segundo, 0,000
disputas por mil e residente travado em 4.096 de 4.096. Falsificabilidade
exercida em quatro mutações, cada uma revertida: devolver a chave crua reprova o
teste de identidade com espaços, com pilha em
`RelationalConnection.OpenDbConnectionAsync`, provando que a busca chegou ao
banco; devolver `Count` mais `Clear` reprova o teste de travessia do teto;
remover a expiração relativa reprova dois testes de janela; remover o
`SizeLimit` reprova o portão com residente de 4.608 contra teto de 4.096.
Suítes: 997 unitários, 134 de integração do módulo, 13 de arquitetura.

Um desvio honesto contra o teste que a ficha pedia: "as mais recentes
sobrevivem" é falso para exatamente uma entrada. Com `SizeLimit` cheio,
`MemoryCache.Set` descarta **a entrada que chega** e agenda a compactação, em
vez de expulsar uma vítima na hora. O teste afirma o que discrimina de fato: o
residente fica limitado, e quase tudo sobrevive à travessia, contra a única
entrada que a política de limpeza total deixava.

### Correção posterior no portão que esta ficha introduziu

O portão de contenção entregue aqui afirmava o teto **exato** do residente, com
um comentário justificando tolerância zero. A justificativa estava errada, pelo
mesmo mecanismo que o parágrafo acima descreve: a compactação é agendada e não
síncrona, então escritores que passam pela checagem de tamanho antes de ela rodar
são todos admitidos. O portão ficou instável e reprovava de forma intermitente,
por uma ou duas entradas acima do teto, inclusive em árvore sem mudança nenhuma.

O limite passou a ser o teto mais o número de escritores concorrentes, que é o
que limita o excesso transitório. Não é fator de tolerância arbitrário, e não
enfraquece o portão: a falha que ele existe para pegar é a política que **deixa
de limitar**, medida em 11.288.751 entradas contra teto de 4.096, ou seja um
excesso de ordens de grandeza, nunca de duas entradas.

---

## `PRF-004` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedVariablesValidator.cs` |
| linha | 23-24 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | O caminho memoizado saiu de dentro do renderizador para um componente compartilhado, e o validador passou a usá-lo. Os dois colidem na mesma entrada `render-context:{app}:{key}`, que é o que o achado pedia. **Medido** por interceptor de comandos do EF: duas execuções por notificação, contra quatro antes. |

**O validador é o único dos três contratos publicados que não usa o cache.**

Evidência:

```csharp
Result<PublishedTemplateContext> context =
    await dbContext.FindPublishedTemplateAsync(application, templateKey, cancellationToken);
```

Impacto: `IPublishedVariablesValidator` não consulta o `PublishedReadCache`,
embora seus dois irmãos memoizem exatamente o mesmo `PublishedTemplateContext`
sob a chave `render-context:{app}:{key}`. `FindPublishedTemplateAsync` faz duas
consultas (identidade e versão publicada, esta última puxando a coleção owned
`template_content` junto). Como o pipeline de notificação chama catálogo,
validador e renderizador para a mesma `(application, templateKey)`, o validador
adiciona duas viagens ao PostgreSQL por notificação que os outros dois já
evitaram.

Recomendação: usar o caminho de carga memoizado no validador, reaproveitando a
mesma chave de ponteiro.

Verificação: contar comandos executados por notificação com um interceptor de
diagnóstico do EF Core, antes e depois. O total deve cair em duas consultas.

### Como foi fechado

O caminho memoizado existia, mas estava privado dentro de
`PublishedTemplateRenderer.LoadPublishedContextAsync`. Ele saiu para um
componente compartilhado, injetado no renderizador e no validador, em vez de ser
copiado: uma terceira cópia da canonização seria exatamente a dívida que a
correção de `PRF-003` evitou nos outros dois sítios. A chave de ponteiro
continua `render-context:{app}:{key}`, porque o objetivo é justamente que os dois
colidam na mesma entrada.

Verificação executada exatamente como a ficha pedia, com interceptor de comandos
do EF: duas execuções na validação e duas no total depois do render, contra as
quatro de antes. Falsificabilidade exercida em duas mutações, cada uma
revertida: devolver o renderizador à consulta direta reprova o oráculo nomeando
o defeito, com `commands.Executed should be 2 but was 4`; trocar a chave do
componente pelos argumentos crus reprova os dois testes de identidade com
espaços em volta.

Suítes: 1.010 unitários, 138 de integração do módulo, 13 de arquitetura, build
limpo, e o portão de contenção da rodada anterior sem regressão.

### Uma consequência registrada, e não escondida

O validador passa a herdar a janela de obsolescência de 60 segundos dos
ponteiros. Uma identidade cujo contexto publicado já está em memória continua
validando contra a versão memoizada por até 60 segundos após uma nova
publicação, uma depreciação ou uma desativação. Antes, toda chamada de validação
lia o estado do banco no instante da chamada.

Isso **alinha** o validador com o catálogo e o renderizador, que já viviam nessa
janela, e não cria classe de risco nova. Mas é mudança de comportamento
observável, e amplia de dois para três o número de contratos publicados que o
`SEC-011` alcança: enquanto não houver invalidação por transição de ciclo de
vida, a desativação de um template agora também demora até um minuto para
alcançar a validação.

---

## `PRF-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 160-177, 200-246 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Componente do renderizador publicado, ainda não tratado. |

**Render duplo para a forma mascarada, correto por desenho e não medido.**

Evidência:

```csharp
if (!VariableMasking.RequiresMasking(variables, template.SensitiveVariables))
{
    return Result.Success(full);
}
JsonElement? maskedVariables = VariableMasking.MaskSensitiveVariables(variables, template.SensitiveVariables);
return await RenderFormAsync(channel, content, maskedVariables, wrapper, cancellationToken);
```

Impacto: toda notificação com pelo menos uma variável sensível de topo renderiza
o conteúdo duas vezes por inteiro. São até 10 execuções do sandbox por
notificação, cada uma com seu `Task.Run`, seu `TemplateContext` e seu
`ScriptObject` de globais reconstruído a partir do `JsonElement`, que aloca
`ScriptObject` e `ScriptArray` por nó do payload, mais o temporizador órfão de
`PRF-002`. A duplicação é deliberada e correta, porque o mascaramento não pode
vazar por transformação, mas o custo desse caminho quente não está medido em
lugar algum do repositório.

Recomendação: manter a semântica e reduzir o custo fixo por render, reusando um
`TemplateContext` por forma em vez de um por campo, e convertendo o payload uma
única vez em `ScriptObject` reaproveitado nos cinco renders da mesma forma.

Verificação: BenchmarkDotNet sobre `IPublishedTemplateRenderer.RenderAsync` com
`IncludeMaskedForm = true`, layout fixado e payload representativo, reportando
`Allocated` e `Mean` antes e depois.

---

## `PRF-006` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 379-384 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-architect, dotnet-specialist (convergência dos três) |
| **estado** | **RESOLVIDO** |
| nota de estado | As até 100 construções de `Regex` por validação viraram dois `[GeneratedRegex]` estáticos, executados uma vez por campo. **Medido**: a suíte que exercita este caminho, incluindo entradas patológicas de 420.000 e 200.000 caracteres, roda em 229 ms no total. A referência anterior, do próprio relatório, era 3,6 s para 112.000 caracteres com crescimento quadrático. A correção compartilha linhas com `SEC-008`, e foi ela que eliminou o retrocesso; o ganho aqui é consequência, não mérito separado. |

**Até 100 construções de `Regex` por validação, único ponto fora do dialeto `[GeneratedRegex]`.**

Evidência:

```csharp
foreach (var variable in template.SensitiveVariables)
{
    var inUrlPosition = new Regex(
        @"https?://[^\s<>""']*\{\{[^{}]*\b" + Regex.Escape(variable) + @"\b",
        RegexOptions.None,
        TimeSpan.FromSeconds(1));
```

`Template.MaxSensitiveVariables` é 100. Todos os outros seis padrões do módulo
usam `[GeneratedRegex]`.

Impacto: uma instância criada com `new Regex(...)` não entra no cache estático
de padrões do .NET, que só atende os métodos estáticos, e fica em modo
interpretado. Um template com o máximo de variáveis sensíveis paga 100
construções e 100 varreduras a cada chamada de `TemplateValidation.Validate`,
que roda no endpoint de validação, na publicação e no rollback. A construção
está dentro do laço externo e não do interno, o que indica que a intenção já era
içá-la. O impacto absoluto é pequeno numa superfície administrativa de baixo
volume, o que sustenta a severidade, mas o custo é evitável por inteiro e a
quebra do dialeto do próprio arquivo é o sinal mais barato de que a linha passou
sem revisão. O mesmo laço é o caminho de `SEC-008`, e a correção é a mesma.

Recomendação: içar o padrão para um `[GeneratedRegex]` único que capture o nome
da variável como grupo, executá-lo uma vez por campo e comparar os nomes
capturados contra um `HashSet<string>` de variáveis sensíveis. Isso troca 100
construções e 100 varreduras por uma construção estática e uma varredura.

Verificação: `grep -n "new Regex("` sobre o módulo não retorna ocorrência.
Comparar tempo de `TemplateValidation.Validate` com 100 variáveis sensíveis
antes e depois, mantendo os mesmos `checks[]`, o que os testes existentes de
posição de URL já garantem.

---

## `PRF-007` · PARCIAL

| Campo | Valor |
|---|---|
| severidade | `LOW`, e a medição sugere que está subestimada |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanParseCache.cs` |
| linha | 45-48 |
| tipo-de-evidência | leitura-de-código, depois **medição executada** na remediação |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PARCIAL** |
| nota de estado | O mecanismo foi **confirmado** por desmontagem do IL, e a metade em `PublishedReadCache.cs:51-53` e `:75-77` saiu junto com a correção de `PRF-003`. Continua aberto em `ScribanParseCache.cs`, que não foi tocado e não foi medido. A **recomendação como escrita foi refutada por medição** e não deve ser aplicada; a forma correta está abaixo. |

**`ConcurrentDictionary.Count` toma todos os locks no caminho de miss.**

Evidência:

```csharp
if (_templates.Count >= MaxEntries)
{
    _templates.Clear();
}
```

Mesmo padrão em `PublishedReadCache.cs:51-53` e `:75-77`.

Impacto: `ConcurrentDictionary<TKey,TValue>.Count` adquire todos os locks
internos da tabela, serializando os escritores, e é chamado em todo miss de
parse e em todo `SetPointer` ou `SetImmutable`, isto é, exatamente no momento de
maior concorrência: início frio, ou logo após uma limpeza, quando todos os
requests estão em miss simultâneo. Somado à limpeza total, o comportamento é um
efeito manada: o teto dispara, tudo é despejado, e a onda de misses seguinte
volta a tomar todos os locks.

Recomendação: manter um contador aproximado com `Interlocked` em vez de
consultar `Count`, e substituir a limpeza total por despejo parcial ou por um
cache com política.

Verificação: benchmark concorrente do caminho de parse com N threads na
fronteira do teto, medindo `lock-contention-count` via `dotnet-counters`.

### O mecanismo está certo, a recomendação está errada

A medição confirmou o mecanismo e derrubou as duas metades da recomendação.
Números completos na ficha de `PRF-003`.

**Confirmado.** `Count` chama `AcquireAllLocks`, verificado no IL de
`System.Collections.Concurrent.dll` 10.0.10, e `Clear()` também, então a forma
atual paga o custo duas vezes no evento de transbordo. `IsEmpty` ganhou atalho
sem lock; `Count` não. Isolando só o portão, com o teto nunca cruzado, a troca
do `Count` por um contador rende fator de 171 a 2.100 conforme o número de
núcleos. É por esse número que a severidade `LOW` merece reavaliação, que é
chamada do architect e não desta remediação.

**Refutado, primeira metade.** Contador `Interlocked` **sem portão** produziu a
**maior** contenção de todas as formas medidas, 369.600 contra 72.509 da forma
atual, com 25.530 limpezas em 4 segundos contra as cerca de 1.340 esperadas. A
causa: todas as threads observam o cruzamento do teto no mesmo instante e cada
uma chama `Clear()`. Um contador sem portão de despejo é pior que o defeito que
ele conserta.

**Refutado, segunda metade.** Despejo parcial com vítima arbitrária mediu **pior
taxa de acerto** que a limpeza total que substituiria. Em simulação de 1.000.000
de pedidos com 300 chaves quentes, cauda fria de 10 por cento: 125.532 leituras
no banco contra 108.358 da limpeza total. A razão é contraintuitiva e vale
registrar: o despejo parcial roda com frequência muito alta e descarta uma
fração aleatória de cada vez, então corrói o conjunto quente continuamente,
enquanto a limpeza total, por ser rara, deixa o conjunto quente se reconstituir
inteiro entre eventos. "Parcial" só ganha se a vítima for escolhida por idade ou
por uso, e nesse ponto o que se está escrevendo é LRU.

**Forma correta**, já aplicada em `PublishedReadCache` e recomendada para
`ScribanParseCache`: `MemoryCache` de `Microsoft.Extensions.Caching.Memory`, que
compacta primeiro expirados, depois por prioridade, depois por último acesso, com
o contador de tamanho mantido internamente com `Interlocked`. Não custa
dependência nova.

**Escopo remanescente.** `ScribanParseCache` continua com `Count` mais `Clear`.
Deliberadamente **não** se extraiu abstração comum: a chave ali é o texto-fonte
inteiro do template, não um identificador curto, e o portão só é avaliado em
miss de parse, o que é exposição estruturalmente menor. Registrar essa decisão
importa, para que a próxima revisão não relate a duplicação como defeito. Medir
o caminho do Scriban antes de agir: ele não foi medido.

---

## `PRF-008` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Features/Queries/GetTemplate/GetTemplate.Handler.cs` |
| linha | 32-44 |
| tipo-de-evidência | leitura-de-código, depois **medição executada** na remediação |
| introduzido-por-diff | `false` |
| revisores | **nenhum desta revisão**. Levantado pela revisão paralela em `docs/reviews/template-management/codex/`, cujos revisores emitiram `LOW` com dissenso registrado do specialist por ausência de medição |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado em duas etapas. A primeira, por projeção no banco, tirou o custo dominante. A segunda, por mesa técnica com medição contra PostgreSQL real, aplicou janela descendente de 200 com continuação por cursor, em `GetTemplate` e em `GetLayout`. A forma de `versions` não mudou: os campos de truncamento e cursor são aditivos e o array continua crescente. O dissenso do engineer, a favor de aceitar sem teto, está preservado abaixo. |

**As consultas administrativas materializam todo o histórico de versões.**

Este achado está aqui porque a lente `PRF` desta revisão não o encontrou, e um
leitor que abrisse só este arquivo concluiria que a lente cobriu o módulo.
Registrá-lo é o que mantém o documento honesto sobre o próprio alcance.

Evidência:

```csharp
List<TemplateVersion> versions = await dbContext.TemplateVersions
    .AsNoTracking()
    .WhereTemplateKey(templateKey.Value!)
    .OrderBy(version => version.Version)
    .ToListAsync(cancellationToken);
```

`GetLayout.Handler.cs` repete o padrão. Não há limite, cursor nem teto de
versões, e versões superseded são preservadas.

Impacto: o relatório de origem descreve crescimento linear no histórico e para
aí. O custo real é maior, e a verificação durante a correção mostrou por quê:
`Contents` é `OwnsMany`, e o EF carrega coleção owned junto com a entidade. Para
produzir cinco colunas escalares por versão (número, status, hash, autor,
instante), a consulta puxava **todo o conteúdo autorado de todas as versões**,
com `MaxBodyLength` de 512.000 caracteres por entrada de canal e locale. O custo
dominante não era a contagem de linhas, era a carga de conteúdo por linha.

Primeira etapa aplicada: projetar no banco, selecionando apenas as colunas do
resumo. `Canonical()` é extension method sobre enum e não tem tradução SQL, então
a conversão acontece depois de materializar. Zero mudança de contrato.

### O que a medição encontrou, e que a leitura não podia ver

A segunda etapa foi decidida em mesa técnica, contra uma população de 192.641
linhas num PostgreSQL descartável com as migrações de produção aplicadas. Três
resultados mudaram o achado:

**A recomendação de origem, aplicada como escrita, produziria uma resposta
errada.** A numeração de versão é monotônica e o rollback clona para um número
maior, então `draft` e `published` vivem na cauda do histórico. Um `Take` sobre a
ordenação ascendente devolve a cabeça. Medido: `Take(51)` ascendente sobre 10.000
versões devolve as versões 1 a 51, **sem nenhuma publicada**. A janela descendente
devolve 10.000 a 9.950, com a publicada presente. A máquina de estados fecha o
ponto por construção, e não por estatística: acima da versão publicada existe no
máximo uma linha, o rascunho aberto, porque os dois índices únicos parciais
admitem um rascunho e uma publicada por identidade.

**O ganho não é aparar linhas, é trocar a família do plano.** Sem teto, a mesma
consulta produziu três planos diferentes conforme a população: `Index Scan`
ordenado com correlação 0,997; `Sort` sobre `Bitmap Heap Scan` com correlação
-0,134, presente já em N=1; e `Seq Scan` mais `Sort` quando um único template
respondia por 79% da tabela. Com teto, `Limit` sobre `Index Scan` em todos os
cenários e em todas as cardinalidades. Em N=1000, 285 buffers e 0,522 ms sem teto
contra 40 buffers e 0,040 ms com teto.

**O custo de latência não existe na cardinalidade plausível.** O delta fica dentro
do ruído (±0,35 ms) até N=250 e sai da banda em N=500. O que sustenta a correção
não é latência, é a instabilidade de plano acima, mais a travessia do Large Object
Heap na versão 8.193, quando a dobra de capacidade da `List` cruza o limiar de
85.000 bytes e aciona as coletas gen2 medidas.

Recomendação aplicada: janela descendente de 200, revertida em memória antes de
emitir o array, com `versionsTruncated` e `versionsNextCursor` aditivos e o
parâmetro de query opcional `versionsCursor` para a continuação. A página é fixa,
sem parâmetro de tamanho, porque um limite livre na continuação tornaria o teto
contornável. O teto de 200 vem do precedente da casa (`MaxPageSize`), e a medição
apenas delimitou a faixa: excluiu tetos de 500 para cima e não exigiu nada abaixo
de 250.

### Dissenso preservado

O `dotnet-engineer` recomendou **aceitar sem teto**, e a posição continua válida
como escrita: o defeito de fundo é o crescimento não observado, e um teto o
esconde em vez de revelá-lo. A mesa registrou que o repositório não tem
telemetria alguma, então nem o truncamento será observado, nem a condição de
reabertura teria gatilho. O dono da decisão escolheu a janela descendente
sabendo disso.

Risco residual aceito: `versions` deixa de ser garantidamente completo. Para a
frota atual, a mudança é neutra na melhor hipótese e marginalmente negativa na
pior, com cerca de 2,1 KB alocados a mais por request em N=1.

Verificação: SQL emitido confirmado no log do host, com `ORDER BY t.version DESC`
e `LIMIT` parametrizado. Falsificabilidade exercida: trocar `OrderByDescending`
por `OrderBy` reprova quatro testes, entre eles o que afirma a presença da versão
publicada acima da janela. Suítes: 134 testes de integração de
`TemplateManagement`, 987 unitários, 8 de arquitetura e 5 de arquitetura de
segurança, todos passando, com `Skipped: 0` sob Docker obrigatório e build limpo
em `TreatWarningsAsErrors`.

Não medido: `GetLayout`, inferido por identidade estrutural de índice e de
handler; o efeito do Large Object Heap sob concorrência, porque a medição foi
monothread; e a cardinalidade real em produção.

---

## Estado de verificação

Levantado na remediação, depois que a execução passou a ser possível.

| Suíte | Resultado |
|---|---|
| Unitários | 986 de 986 |
| Arquitetura e arquitetura de segurança | 13 de 13 |
| Integração, processo único, `MaxParallelThreads=2` | 558 passando, 2 pulados, 0 falhando |
| Build sob `TreatWarningsAsErrors` | limpo |

Os 2 pulados são `LiveProviderSmokeTests`, que batem em provedores reais e são
pulados por desenho.

A tabela acima é do estado anterior à remediação de `PRF-008`. Depois dela, e
verificado no escopo que a mudança toca: 987 unitários, 134 de integração de
`TemplateManagement`, 8 de arquitetura e 5 de arquitetura de segurança, com
`Skipped: 0` sob Docker obrigatório e build limpo. A suíte de integração completa
não foi reexecutada nessa passagem.

Duas ressalvas sobre o que essa validação prova e o que não prova:

- A suíte **não é benchmark**. Ela prova que o comportamento não regrediu, não
  que o caminho quente ficou mais rápido. Os experimentos descritos em cada
  achado aberto continuam sendo o que confirmaria ganho. A exceção é `PRF-008`,
  cuja remediação teve medição própria, executada fora da suíte e registrada na
  ficha do achado.
- A suíte de integração só completa com paralelismo limitado nesta máquina. Sem
  limite, ela falha em `NamedPipeClientStream.ConnectInternal` sob
  `Docker.DotNet`, saturação de conexões no named pipe do daemon no Windows.
  Nenhuma dessas falhas chega a executar asserção, e nenhuma é do módulo.

## Achados abertos

`PRF-004` é do componente de cache e `PRF-005` é do renderizador publicado.
Nenhum dos dois foi tratado, e ambos seguem sem medição. `PRF-007` está parcial:
o mecanismo foi confirmado e a metade em `PublishedReadCache` saiu com `PRF-003`,
mas `ScribanParseCache` continua com o padrão, e a recomendação original da ficha
não deve ser aplicada como escrita.

O `PRF-004` ficou mais barato de fechar depois desta rodada: ele pede que o
validador reaproveite a mesma chave `render-context:{app}:{key}` que os dois
irmãos memoizam, e essa chave agora é canônica nos três sítios.
