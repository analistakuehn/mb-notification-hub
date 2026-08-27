---
language: pt-BR
lens: SEC
lens-name: Security
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 14
---

# Lente `SEC`: Security

Autenticação, autorização, proteção de entrada e dados, segredos, cadeia de
suprimentos, caminhos de abuso e auditabilidade.

Autorização nomeada e rate limiting estão presentes nos 32 endpoints e já são
impostos deterministicamente pelo teste de arquitetura de segurança, portanto não
aparecem aqui. Os achados desta lente tratam de controles que existem mas não
alcançam o que deveriam, do sandbox de templates, e de dado sensível que atravessa
fronteiras que o próprio módulo declarou fechadas.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `SEC-002` | `HIGH` | **RESOLVIDO** | O sandbox não remove os builtins que reavaliam string como código |
| `SEC-003` | `HIGH` | **RESOLVIDO** | A mensagem de exceção do Scriban chega ao `detail` da resposta com o... |
| `SEC-008` | `HIGH` | **RESOLVIDO** | Retrocesso quadrático no check de posição de URL, escapando como... |
| `SEC-009` | `HIGH` | **RESOLVIDO** | Timeout de expressão regular escapa do contrato publicado e produz... |
| `SEC-010` | `HIGH` | **RESOLVIDO** | Alocação por largura contorna os dois tetos de saída |
| `SEC-001` | `CRITICAL` | **RESOLVIDO** | O mascaramento só alcança o primeiro nível, e nada liga o nome sensível... |
| `SEC-004` | `HIGH` | **RESOLVIDO** | O allowlist de domínios é, na prática, opcional para quem escreve o... |
| `SEC-005` | `HIGH` | **PENDENTE** | O catálogo de validação de layout tem dois checks, e o layout envolve... |
| `SEC-006` | `HIGH` | **PENDENTE** | `Layout.Status` nunca é consultado no render: desativar um layout não... |
| `SEC-007` | `HIGH` | **PENDENTE** | `purpose` não é canonizado, e uma variação de caixa desliga o controle... |
| `SEC-011` | `MEDIUM` | **PENDENTE** | Nenhuma transição de ciclo de vida invalida o cache, nem no processo... |
| `SEC-012` | `MEDIUM` | **PENDENTE** | A superfície de governança não impõe posse por aplicação; o contrato de... |
| `SEC-013` | `MEDIUM` | **PENDENTE** | Cache de parse limitado por contagem, com o texto fonte como chave... |
| `SEC-014` | `MEDIUM` | **PENDENTE** | A trilha grava relatório completo na publicação e um booleano no... |

---
## `SEC-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `CRITICAL` |
| confiança | alta |
| arquivo | `Domain/VariableMasking.cs` |
| linha | 19-20, 33-40 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist (`CRITICAL`), dotnet-architect (`HIGH`), dotnet-engineer (`MEDIUM`) |
| dissenso | severidade divergente entre os três, retido `CRITICAL` |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado nas duas metades. A primeira já bloqueava a publicação quando um nome sensível não corresponde ao schema. A segunda trocou o mascaramento de topo por uma regra estrutural única em `SharedKernel/SensitiveValueMask.cs`, que os dois mascaradores passam a ler: nome sem ponto casa a chave em qualquer profundidade, inclusive dentro de array; nome com ponto é caminho absoluto, resolvido só por caminho e nunca por chave literal; prefixo próprio que cai em nó não objeto é recusado e mascarado fechado. A travessia lê `JsonElement` e escreve por `Utf8JsonWriter`, sem `JsonNode` em ponto nenhum, e é FUNDIDA: decide e copia na mesma passagem, porque um "nada a mascarar" calculado à parte da máscara que rodou devolve a forma completa como mascarada. O portão de publicação passou a resolver em qualquer profundidade e recusa alto através de `additionalProperties`, `$ref`, `oneOf` e `allOf`. Duas divergências apareceram contra a previsão, ambas na direção de o defeito ser pior do que esta ficha registrava: uma chave literal de topo que soletra o caminho era mascarada no lugar do caminho real, e payload com chave duplicada derrubava a ingestão com `500` em qualquer template com variável sensível. As duas foram fechadas junto. O risco residual que esta ficha registrava caiu por construção: a regra lê o payload e não a declaração, então template já publicado para de vazar no deploy, sem escrita de dado. O que permanece aberto é de outra natureza, não é vazamento novo, e são dois itens: as linhas já gravadas antes desta correção, que o dono da decisão manteve fora do escopo; e o transporte da recusa, porque `RefusedName` é produzido e hoje descartado pelos dois consumidores, de modo que a falha fechada é o que impede o vazamento e o autor não recebe sinal de que o caminho declarado está errado. |

**O mascaramento só alcança o primeiro nível, e nada liga o nome sensível ao schema.**

Evidência:

```csharp
public static bool RequiresMasking(JsonElement? variables, IReadOnlyList<string> sensitiveVariables)
    => variables is { ValueKind: JsonValueKind.Object } payload
        && sensitiveVariables.Any(name => payload.TryGetProperty(name, out _));
...
JsonObject root = JsonNode.Parse(variables!.Value.GetRawText())!.AsObject();
foreach (var name in sensitiveVariables)
{
    if (root.ContainsKey(name)) { root[name] = Mask(root[name]); }
}
```

O consumo fecha o circuito em `Infrastructure/Integration/PublishedTemplateRenderer.cs:169-171`:

```csharp
if (!VariableMasking.RequiresMasking(variables, template.SensitiveVariables))
{
    return Result.Success(full);
}
```

Impacto: com variável sensível declarada como `cpf`, corpo `{{ customer.cpf }}` e
payload `{"customer":{"cpf":"..."}}`, a verificação devolve `false` e o
renderizador entrega a forma **completa** como se fosse a mascarada, hash
incluso. O mesmo acontece com divergência de caixa, erro de digitação ou variável
renomeada em versão posterior, porque a lista sensível preserva caixa e só é
aparada. O dado pessoal em claro é persistido como forma de trilha numa tabela
WORM append-only, com um hash canônico que declara ser o da forma mascarada. O
`AGENTS.md` fixa o inverso: o mascaramento substitui valores sensíveis antes do
render mascarado, "so the stored form proves that a value was sent, never which
one". Não há check que falhe, não há log, não há aviso: a publicação passa limpa e
o vazamento é permanente num armazenamento que ninguém pode corrigir depois.

Há ainda uma contradição interna que confirma o defeito: o check de posição de URL
usa uma expressão com fronteira de palavra que **casa** o nome aninhado, ou seja,
o módulo afirma naquele ponto que o nome ali é o dado sensível, enquanto o
mascaramento afirma que não é. Combinado com `ARC-005`, também não existe caminho
de API para consertar o nome depois de descoberto.

Recomendação: duas medidas, que juntas fecham o caso. Acrescentar ao catálogo
integral um check bloqueante que reprove a publicação quando algum nome declarado
sensível não corresponder a uma propriedade de topo do `variables-schema` da
versão. Ele pertence ao catálogo integral porque a regra é "só publica quem passa
em tudo, agora", e porque publicação e rollback já reexecutam esse catálogo. E
estender a busca e o mascaramento a caminho com pontos, o que exige relaxar o
padrão de nome de variável e é decisão de contrato.

Verificação: teste com variável sensível declarada e payload aninhado, com a forma
mascarada solicitada, afirmando que o corpo mascarado não contém o valor e que o
hash mascarado difere do completo. Somar teste de publicação que reprova o
template com sensível não declarado no topo. Ambos devem falhar hoje.

---

## `SEC-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 28-29, 75-87 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Confirmado por sonda antes da correção, na forma mais forte: `payload | object.eval_template` executou o valor de uma variável como template. O builtin do sandbox passou a ser derivado do default por clone profundo, sem `object.eval`, `object.eval_template`, `include` e `include_join`. |

**O sandbox não remove os builtins que reavaliam string como código.**

Evidência:

```csharp
var context = new TemplateContext { ... MemberFilter = static _ => false, ... };
context.PushGlobal(BuildGlobals(variables));
```

O objeto builtin permanece no escopo global (Scriban `TemplateContext.cs:208`
empurra o builtin, e `Functions/BuiltinFunctions.cs:40` registra `object`), e
`Functions/ObjectFunctions.cs:72` e `:127` expõem `Eval` e `EvalTemplate`, que
fazem `Template.Parse` de uma string em tempo de render.

Impacto: `MemberFilter` bloqueia apenas acesso reflexivo a tipos .NET; não remove
os builtins. Um pipe que passe o valor de uma variável por `object.eval_template`
transforma dado fornecido pelo módulo chamador em código de template executado.
Toda a governança do módulo (`variables-declared`, `url-allowlist`,
`sensitive-variables`, `authentication-sms-links`) inspeciona a fonte estática, e
um template publicado com esse pipe passa todos os checks enquanto o template
efetivo só existe em runtime. `object.eval` tem o mesmo efeito para expressões.

Recomendação: construir um `ScriptObject` builtin restrito e passá-lo ao
construtor do `TemplateContext`, removendo no mínimo `object.eval`,
`object.eval_template`, `include` e `include_join`. Alinhar a lista de globais
conhecidos ao conjunto restrito resultante, o que também resolve `STK-003`.

Verificação: teste de que os dois builtins de reavaliação retornam falha do
sandbox, e teste de regressão que enumere os membros visíveis ao template e falhe
quando um novo aparecer.

---

## `SEC-003` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 117-119 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Confirmado, com correção de evidência. O caminho citado no achado, operador binário, NÃO vaza: devolve mensagem que nomeia o tipo. Das oito formas sondadas, apenas `math.abs` vaza, com `The value {valor} is not a number`. A correção redige os valores do payload das mensagens do engine antes de saírem, preservando o vocabulário que o autor precisa. |

**A mensagem de exceção do Scriban chega ao `detail` da resposta com o valor real.**

Evidência:

```csharp
return Result.ValidationError<string>(exception.InnerException is RegexMatchTimeoutException
    ? TimeLimitMessage()
    : exception.Message);
```

Em Scriban `Syntax/Expressions/ScriptBinaryExpression.cs:247`, a mensagem
interpola os operandos:

```csharp
throw new ScriptRuntimeException(span, $"The operator `{op.ToText()}` is not supported between `{leftValue}` and `{rightValue}`");
```

Impacto: a mensagem é devolvida verbatim, embrulhada pelo renderizador com o nome
do campo, decodificada na borda HTTP e escrita no `detail` da resposta RFC 9457.
Várias mensagens de runtime do Scriban interpolam o valor real (expressão binária,
expressão unária, funções matemáticas). No caminho de despacho as variáveis são
dados reais de cliente, então uma operação de tipo incompatível sobre uma variável
produz uma mensagem contendo dado pessoal. O módulo protege esse mesmo eixo em
outro ponto, onde declara por escrito que o valor nunca viaja no erro, e o teste
de arquitetura que cobre nomes de dado pessoal olha templates de logger, não
strings de erro.

Recomendação: nunca propagar a mensagem de exceção do Scriban para fora do engine
no caminho publicado. Devolver um código estável mais a posição (linha e coluna) e
registrar a mensagem completa apenas em log de diagnóstico com escopo restrito, ou
sanitizar por lista de permissão.

Verificação: renderizar um template com operação de tipo incompatível sobre uma
variável com valor sentinela e afirmar que o valor não aparece nem no `detail` do
problema nem na string de erro.

---

## `SEC-004` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 219, 578, 389 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado nas duas metades, e a correção divergiu da recomendação num ponto que a medição obrigou. Metade um: o check de publicação trocou o detector de link literal por extração de host, e passou a reprovar host relativo de protocolo, host nu com caminho e host anunciado por `www.`. Apareceu um achado colateral que esta ficha não registrava: `LiteralLink` rodava sem `IgnoreCase`, então `HTTPS://EVIL.COM/x` passava limpo. Metade dois: a verificação de domínio passou a alcançar todo valor string do payload, em qualquer profundidade e dentro de array, como a regra de mascaramento já fazia, porque uma varredura rasa reabriria o mesmo furo pela mesma porta. A regra virou única em `Domain/LinkDomainPolicy.cs` e as três cópias de `IsAllowedUrl` deixaram de existir. A divergência: aplicar o detector amplo tal e qual, como a recomendação pede, foi medido em 10 falsos positivos sobre 17 não-links, ou 59%, todos da forma número pontuado seguido de barra, que em pt-BR é CNPJ, nota fiscal, número de processo, cláusula de contrato e endereço com número. Num ponto que bloqueia despacho isso é indisponibilidade, não conservadorismo, então o detector do allowlist ficou separado do detector de SMS de autenticação, que segue largo de propósito, e passou a exigir TLD alfabético mais sufixo plausível: 0 falso positivo e 0 link perdido no mesmo corpus. O custo medido da varredura é de cerca de 5 microssegundos por kB de texto. Duas decisões do dono ficaram registradas: a regra bloqueia sempre, inclusive com allowlist vazio, o que é quebra de contrato consciente para template já publicado que hoje envia link de rastreio por variável de tipo string; e a recusa nomeia o host, e apenas o host, porque sem ele o produtor não descobre o que corrigir. Duas integrações do banimento de link em SMS de autenticação passaram a morrer no portão novo e foram reparadas para declarar o domínio que injetam, de modo que a recusa volte a ser provada no portão que elas se propõem a provar. O que permanece aberto é de outra natureza, não é vazamento novo, e são três itens: o teto de bytes de payload só existe no validador do endpoint de preview, então o caminho de ingestão e o render entre módulos não têm o custo da varredura limitado por construção; placeholder como prefixo de host nu, na forma `{{ host }}.com/x`, escapa dos dois detectores; e host formado pela concatenação de duas variáveis só é alcançável por varredura pós render, que depende de `SEC-005`. |

**O allowlist de domínios é, na prática, opcional para quem escreve o schema.**

Evidência, o detector estreito usado pelo check e o filtro de render por formato:

```csharp
foreach (Match match in LiteralLink().Matches(text))
...
[GeneratedRegex(@"https?://([^\s/:?#<>""']+)")]
private static partial Regex LiteralLink();
...
foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.IsUrl))
```

Impacto: o check tem duas aberturas. Primeira, só reconhece link literal com
esquema explícito: um `href` relativo de protocolo, ou com host nu seguido de
caminho, é clicável num corpo HTML de e-mail e nunca é inspecionado, enquanto o
detector amplo existe no mesmo arquivo e é usado apenas para SMS de autenticação.
Segunda, em tempo de render só são conferidas variáveis declaradas com formato de
URL: qualquer variável de tipo string carregando um endereço atravessa sem
verificação, e o Scriban não escapa nada.

Recomendação: usar o detector amplo também no check de allowlist, reprovando host
relativo de protocolo e host nu com caminho. Em tempo de render, aplicar a
verificação de domínio a todo valor string que case com o detector amplo, não
apenas às variáveis declaradas como URL.

Verificação: publicar um template não crítico com âncora relativa de protocolo e
afirmar o check reprovado. Renderizar com uma variável declarada como string
carregando domínio fora do allowlist e afirmar recusa.

---

## `SEC-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/LayoutValidation.cs` |
| linha | 23-26 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Catálogo de layout, ainda não tratado. |

**O catálogo de validação de layout tem dois checks, e o layout envolve toda notificação que o fixa.**

Evidência:

```csharp
List<ValidationCheck> checks = [];
AddCompilationChecks(checks, analyses);
AddContentPlaceholderChecks(checks, version, analyses);
return new ValidationReport(checks);
```

Impacto: não há allowlist de URL, não há proibição de link em SMS de autenticação
e não há limite de canal. Os domínios permitidos do template nunca alcançam o
conteúdo do layout. Duas consequências concretas: um layout pode injetar link para
domínio arbitrário em todo e-mail de todos os templates que o fixam, com publicação
aprovada; e um layout com conteúdo SMS contendo algo clicável faz o renderizador
recusar 100% das renderizações de um template de autenticação em despacho, apesar
de a publicação ter passado. O `AGENTS.md` afirma que aprovar um template também
aprova a exata versão de layout que ele renderiza dentro, e nenhum check sustenta
essa afirmação.

Recomendação: aplicar ao conteúdo do layout o mesmo catálogo de links, e no check
de referência de layout do template validar o conteúdo do layout fixado contra os
domínios permitidos daquele template e contra a proibição de link em SMS de
autenticação.

Verificação: fixar um layout com domínio fora do allowlist e afirmar que a
publicação do template é bloqueada. Fixar layout SMS com link em template de
autenticação e afirmar bloqueio na publicação, não no despacho.

---

## `SEC-006` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 294-313 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Componente do renderizador publicado, ainda não tratado. |

**`Layout.Status` nunca é consultado no render: desativar um layout não impede nada.**

Evidência: a resolução do wrapper carrega a versão fixada e a identidade apenas
para obter o locale padrão, e memoriza o resultado como entrada imutável:

```csharp
LayoutVersion? pinned = await dbContext.LayoutVersions
    .AsNoTracking()
    .WhereLayoutKey(key)
    .FirstOrDefaultAsync(candidate => candidate.Version == pinnedNumber, cancellationToken);
...
Layout? layout = await dbContext.Layouts.AsNoTracking().WhereKey(key).FirstOrDefaultAsync(cancellationToken);
pinnedLayout = new PinnedLayout(pinned, layout?.DefaultLocale);
cache.SetImmutable(cacheKey, pinnedLayout);
```

A varredura no módulo mostra que `Layout.Status` só é consultado na listagem de
layouts.

Impacto: a desativação de layout é controle de governança com transição auditada,
mas nenhum caminho de leitura ou render consulta o status. Desabilitar um layout
comprometido não impede uma única notificação. Pior: a entrada vai para o cache
imutável, que nunca expira, então nem uma alteração no banco reverte o
comportamento dentro do processo.

Recomendação: consultar o status da identidade do layout no caminho de render e
recusar quando desativado. Guardar o status dentro da entrada imutável invalidaria
a premissa de imutabilidade, então o status pertence a uma entrada de ponteiro com
janela, não à entrada imutável.

Verificação: publicar template com layout fixado, desabilitar o layout, renderizar
pelo contrato publicado e afirmar recusa. Repetir após um render prévio que já
povoou o cache imutável.

---

## `SEC-007` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 326 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Canonização de `purpose`, ainda não tratada. |

**`purpose` não é canonizado, e uma variação de caixa desliga o controle em silêncio.**

Evidência:

```csharp
if (!string.Equals(template.Purpose, AuthenticationPurpose, StringComparison.Ordinal)) return;
```

com a constante em minúsculas, e o mesmo eixo no renderizador. O campo que
alimenta a comparação não é canonizado: o agregado apenas apara o valor, e o
validador de entrada só exige não vazio com comprimento máximo.

Impacto: um template criado com o propósito em outra caixa é aceito e persistido
tal e qual, e a partir daí o controle de maior aposta do módulo deixa de existir
em silêncio. A verificação de link em SMS de autenticação retorna na primeira
linha, então a publicação não roda a proibição, e a recusa em tempo de render
também não dispara. O relatório devolvido ao autor sequer contém a entrada
correspondente, de modo que nada sinaliza a ausência do controle. O raio de
alcance passa a fronteira: o módulo `Notifications` compara o mesmo campo com a
mesma semântica ordinal, então a mesma diferença de caixa também tira a
notificação da faixa de autenticação. O próprio arquivo declara a assimetria de
custo: um falso negativo entrega ao atacante a única mensagem que as pessoas foram
treinadas a confiar e a atender imediatamente.

Recomendação: canonizar o propósito no agregado, normalizando para minúsculas
invariantes, de forma que o valor armazenado seja o único que qualquer comparação
ordinal possa encontrar. Alternativa mais forte: transformar o propósito em
vocabulário fechado como a classe de notificação, com validação por `Result`, o
que remove a classe inteira de erro em vez de só esta instância. Se a canonização
for adotada, prever a normalização dos valores já persistidos.

Verificação: criar um template com o propósito em caixa mista e conteúdo SMS com
link, e publicar. Antes da correção a publicação passa e o relatório não traz a
verificação; depois, ela aparece reprovada e a publicação é bloqueada. Cobrir com
teoria sobre as variações de caixa, conforme `TST-009`.

---

## `SEC-008` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 381-389 |
| tipo-de-evidência | leitura-de-código, medição |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect (`MEDIUM`, com medição), dotnet-specialist (`HIGH`, sem medição) |
| dissenso | severidade divergente, retido `HIGH` |
| **estado** | **RESOLVIDO** |
| nota de estado | A expressão que ia do esquema até o nome da variável foi substituída por varredura linear de placeholders com busca para trás limitada, e o timeout deixou de escapar. Medido: validação de 420.000 caracteres densos em esquemas em menos de 2 segundos, contra 3,6 segundos para 112.000 antes. |

**Retrocesso quadrático no check de posição de URL, escapando como exceção não tratada.**

Evidência:

```csharp
var inUrlPosition = new Regex(
    @"https?://[^\s<>""']*\{\{[^{}]*\b" + Regex.Escape(variable) + @"\b",
    RegexOptions.None,
    TimeSpan.FromSeconds(1));
```

Medição executada pelo revisor architect, entrada com esquema repetido:

| Tamanho da entrada | Tempo de busca |
|---|---|
| 7.000 | 14,60 ms |
| 14.000 | 57,34 ms |
| 28.000 | 224,91 ms |
| 56.000 | 918,41 ms |
| 112.000 | 3.678,06 ms |

Comportamento quadrático confirmado. O teto do texto que chega aqui é
`MaxBodyLength`, 512.000 caracteres.

Impacto: a classe é gulosa sobre um conjunto que inclui a chave de abertura,
seguida da própria chave, então um corpo com muitas ocorrências de esquema e
nenhuma abertura faz o motor tentar cada posição inicial e retroceder o sufixo
inteiro. O timeout de 1 segundo aborta, mas a exceção resultante não é capturada
em nenhum ponto do caminho de validação, publicação ou rollback, então vira `500`
e quebra o eixo de erro único do módulo. Um principal autenticado com papel de
autoria grava um corpo grande num template que declare ao menos uma variável
sensível (nenhum outro pré-requisito) e chama o endpoint de validação: cada
chamada queima 1 segundo de CPU e termina em erro do servidor. O orçamento de
rate limit é generoso por decisão declarada, cerca de 16 requisições por segundo
por principal, cada uma saturando um núcleo. O custo multiplica ainda por número
de variáveis sensíveis, entradas de conteúdo e campos. Não é escalada de
privilégio, é indisponibilidade a partir de um papel que a segregação de funções
trata como semiconfiável, e o mesmo processo serve a superfície de ingestão de
notificações.

Recomendação: três correções. Reescrever o padrão sem retrocesso ambíguo (grupo
atômico, ou localizar as aberturas e olhar para trás por janela limitada, ou
`RegexOptions.NonBacktracking`) e içá-lo para `[GeneratedRegex]` estático,
conforme `PRF-006`. Aplicar teto de tamanho de entrada antes de qualquer varredura
de conteúdo, alinhado ao teto do motor, reportando o estouro como check reprovado.
Envolver as varreduras em captura de timeout que produza check reprovado, para que
nenhum estouro escape como exceção.

Verificação: validação sobre um corpo de 512.000 caracteres com esquema repetido
responde `200` com relatório em menos de 200 ms, e nenhum `500` no log.

---

## `SEC-009` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 189 |
| tipo-de-evidência | leitura-de-código, medição |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect (`HIGH`), dotnet-specialist (`MEDIUM`) |
| dissenso | severidade divergente, retido `HIGH` |
| **estado** | **RESOLVIDO** |
| nota de estado | `ContainsLinkLikeText` virou total, falhando fechado no timeout, e `LinkLike` passou a `NonBacktracking`. O predicado roda no despacho, dentro de um contrato cujo consumidor não trata exceção, então o escape virava mensagem venenosa. |

**Timeout de expressão regular escapa do contrato publicado e produz mensagem venenosa.**

Evidência:

```csharp
&& (TemplateValidation.ContainsLinkLikeText(form.Body)
    || TemplateValidation.ContainsLinkLikeText(form.Subject)
    || TemplateValidation.ContainsLinkLikeText(form.BodyText));
```

O padrão executado tem timeout de 1 segundo, e a chamada está fora de qualquer
`try`. Medição executada pelo revisor architect com entrada de rótulos repetidos:
1.041 ms para 16.000 caracteres, também quadrático. O teto do texto renderizado é
1.000.000 de caracteres.

Impacto: a exceção atravessa `IPublishedTemplateRenderer.RenderAsync` como exceção
e não como `Result`, violando a regra do módulo no ponto exato em que o contrato
publicado entra no pipeline do módulo irmão. Como o pipeline não trata exceções
por decisão declarada (a mensagem retorna à fila com backoff e só a política de
redrive alcança a DLQ), a notificação vira mensagem venenosa determinística: o
conteúdo renderizado é o mesmo a cada tentativa, cada tentativa queima 1 segundo
de CPU de um worker, e a mensagem só para ao fim da política de redrive. Uma
variável de produtor com valor longo o bastante em template SMS de autenticação
basta, e o caminho não tem corte de tamanho antes da varredura: o normalizador não
trunca, e o limite de SMS é apenas um check de validação, não uma barreira de
renderização. Na direção oposta, o achado `SEC-010` mostra que uma falha
operacional grave chega ao chamador classificada como erro de validação: o eixo de
erro está furado nos dois sentidos.

Recomendação: tornar o predicado total, capturando o timeout e devolvendo
`true` (falha fechada, coerente com a doc do próprio método, em que um falso
positivo custa um código de autenticação e um falso negativo entrega a mensagem
mais confiável ao atacante), de modo que o resultado continue sendo recusa por
`Result` e nunca exceção. Eliminar o retrocesso do padrão e cortar a entrada por
teto explícito antes da varredura. Distinguir falha operacional de falha de
validação no tratamento do engine, deixando a exceção de memória propagar como
falha de sistema.

Verificação: renderizar um SMS de autenticação com variável de 1.000.000 de
caracteres no formato patológico e afirmar `Result` de falha com o motivo de link,
sem exceção, em menos de 50 ms após a troca do padrão.

---

## `SEC-010` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | média |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 88, 207-232 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Confirmado por sonda: 200.000 caracteres alocados com teto de saída em 1.000. `string.pad_left` e `string.pad_right` sairam do builtin, e falha operacional deixou de ser classificada como erro de validação. |

**Alocação por largura contorna os dois tetos de saída.**

Evidência: a barreira de saída limita o que é escrito, `var output = new
BoundedScriptOutput(options.Value.MaxOutputChars);`. Em Scriban
`Functions/StringFunctions.cs:1246-1248`:

```csharp
public static string PadRight(string? text, int width)
{
    return (text ?? string.Empty).PadRight(width);
}
```

Impacto: a barreira própria e o limite interno do Scriban limitam a saída, mas
nenhum dos dois limita valores intermediários. Um preenchimento à direita com
largura de centenas de milhões aloca uma string de ordem de centenas de megabytes
numa única chamada que não passa pela escrita e não observa o token de
cancelamento. O deadline de parede não interrompe a alocação, conforme `PRF-001`.
Se estourar, a exceção de memória é embrulhada pelo Scriban e o módulo a devolve
como erro de validação: um pico de memória é reportado ao autor como `400`, sem
alarme operacional. O gatilho está disponível a qualquer portador do papel de
autoria pelo endpoint de preview. Confiança média porque o mecanismo foi lido na
fonte do pacote, sem execução.

Recomendação: remover ou embrulhar as funções de alocação por largura no objeto
builtin restrito de `SEC-002`, ou impor teto de largura. Tratar a exceção de
memória como falha operacional, nunca como erro de validação.

Verificação: renderizar a chamada patológica sob `dotnet-counters` observando
`gc-heap-size` e afirmar recusa antes de qualquer alocação relevante.

---

## `SEC-011` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedReadCache.cs` |
| linha | 15, 48-57 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-specialist (convergência independente) |
| **estado** | **PENDENTE** |
| nota de estado | Componente de cache, ainda não tratado. |

**Nenhuma transição de ciclo de vida invalida o cache, nem no processo que a executou.**

Evidência:

```csharp
internal static readonly TimeSpan PointerLifetime = TimeSpan.FromSeconds(60);
...
internal void SetPointer<T>(string key, T value) where T : class
{
    if (_pointers.Count >= MaxEntries) { _pointers.Clear(); }
    _pointers[key] = new PointerEntry(value, timeProvider.GetUtcNow() + PointerLifetime);
}
```

A classe não expõe método de invalidação, e a varredura mostra que nenhum handler
de mutação toca o cache. O módulo irmão já tem o padrão, com um handler
administrativo que invalida o próprio cache explicitamente.

Impacto: a desativação de template é o efeito terminal que existe para parar
tráfego, e o teste de integração o exercita com a razão "conteúdo incorreto em
produção". Ainda assim, se o catálogo ou o renderizador tiverem sido chamados para
aquele template nos 60 segundos anteriores, o ponteiro memorizado continua
respondendo publicado e o render continua produzindo mensagem, por processo e por
até um minuto inteiro depois do commit. O mesmo vale para depreciação e para uma
publicação corretiva. O `AGENTS.md` aceita a janela para convergência de
publicação, mas a desativação é interruptor de emergência, não publicação, e a
decisão de aceitar um minuto de tráfego depois de um comando de parada é de
negócio, não de implementação, e não está registrada em lugar algum.

Recomendação: expor invalidação por chave ou por prefixo e chamá-la nos handlers
de desativação, depreciação, publicação, rollback e publicação de política, depois
do commit. Para múltiplas réplicas, publicar evento de invalidação pelo Redis já
presente no perfil. Registrar no `AGENTS.md` que a janela remanescente é apenas a
de propagação entre processos.

Verificação: em teste de integração, consultar o catálogo para popular o ponteiro,
desativar, e consultar de novo no mesmo escopo, afirmando rejeição. Hoje o teste
falha, porque a primeira consulta é o passo que hoje não existe (`TST-006`).

---

## `SEC-012` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | média |
| arquivo | `Infrastructure/Authorization/AuthorizationSetup.cs` |
| linha | 19 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-architect (convergência independente) |
| **estado** | **PENDENTE** |
| nota de estado | Decisão de escopo por aplicação, pendente. |

**A superfície de governança não impõe posse por aplicação; o contrato de leitura impõe.**

Evidência:

```csharp
services.AddAuthorizationBuilder()
    .AddPolicy(AuthorPolicyName, policy => policy.RequireRole(AuthorRole))
    .AddPolicy(PublisherPolicyName, policy => policy.RequireRole(PublisherRole));
```

Nenhuma das duas políticas consulta a aplicação. A resolução de ator extrai apenas
identificadores de sujeito, nunca uma claim de aplicação. A leitura por chave não
filtra por aplicação, e a listagem trata a aplicação como filtro opcional do
chamador. Do outro lado da mesma fronteira, o contrato publicado impõe posse,
recusando quando a aplicação do template difere da solicitada.

Impacto: a assimetria é interna ao módulo. O runtime impede que a aplicação A use
o template da aplicação B, mas a API de governança permite que A o destrua.
Qualquer principal com o papel de publicação pode desativar ou depreciar o
template de produção de outro time, efeito terminal que interrompe tráfego, e a
trilha registra apenas quem o fez, não que faltava autoridade. Qualquer principal
com o papel de autoria lê e edita rascunhos de todas as aplicações, e edita o
rascunho de política de classe de qualquer par de aplicação e classe, o que inclui
campos de decisão de Compliance. Confiança média por um motivo específico: nenhuma
decisão aceita exige o escopo. O desenho descreve o autor como grupo por time de
produto, ou seja, a intenção é segregar, mas o modelo declarado é autorização por
rota, com autorização por recurso apenas para os quatro olhos. É lacuna de
alocação de NFR de segurança que nenhum documento cobre, não violação de regra
escrita.

Recomendação: decidir explicitamente e registrar. Se a segregação por time deve
valer no hub, o papel precisa carregar o escopo (uma claim de aplicações
permitidas lida na resolução de ator), e a aplicação do corpo passa a ser
verificada contra ela em criação, listagem e governança de política. Se o escopo
fica no provedor de identidade e no processo, isso precisa estar escrito como
risco aceito, porque hoje o leitor do desenho conclui o contrário do que o código
faz.

Verificação: teste de integração com token de autor de uma aplicação tentando
criar template sob outra, afirmando `403`. Ou, na decisão oposta, uma linha no
documento de desenho declarando o escopo como responsabilidade do provedor de
identidade, e o achado fechado como aceito.

---

## `SEC-013` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Templating/ScribanParseCache.cs` |
| linha | 16, 42-51 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer, dotnet-specialist (convergência independente) |
| **estado** | **PENDENTE** |
| nota de estado | Componente de cache, ainda não tratado. |

**Cache de parse limitado por contagem, com o texto fonte como chave, alimentado por rascunhos.**

Evidência:

```csharp
internal const int MaxEntries = 1024;
...
var parsed = Template.Parse(source);
if (!parsed.HasErrors)
{
    if (_templates.Count >= MaxEntries) { _templates.Clear(); }
    _templates.TryAdd(source, parsed);
}
```

A chave é o texto integral da fonte, cujo teto é 131.072 caracteres, num singleton
cujas entradas nunca expiram.

Impacto: o teto é 1024 fontes de até 131.072 caracteres, cerca de 268 MB só em
chaves, mais as árvores sintáticas associadas, que costumam superar a fonte. O
cache admite conteúdo de rascunho, porque o preview renderiza qualquer versão pelo
mesmo engine, então um autor autenticado, com orçamento de 1000 requisições por
minuto, empurra fontes distintas de tamanho máximo para dentro do dicionário. A
recuperação é abrupta em vez de gradual: ao cruzar o teto o cache inteiro é
descartado, e as fontes publicadas quentes que servem o despacho voltam a pagar
parse.

Recomendação: separar as duas populações, memoizando apenas fontes publicadas e
imutáveis e deixando o preview parsear sem cachear, o que também remove o vetor de
entrada controlada pelo autor. Se a memoização de preview for desejada, limitar
por bytes acumulados e não por contagem, usar chave de tamanho fixo (o hash
canônico da fonte, que o módulo já calcula) e trocar a limpeza total por despejo do
menos usado.

Verificação: teste que injeta mais fontes que o teto e afirma o comportamento de
despejo escolhido, mais medição de memória residente do processo após N previews
de fonte máxima, comparada com o mesmo cenário sem memorização de rascunho.

---

## `SEC-014` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Features/Mutations/PublishTemplateVersion/PublishTemplateVersion.Handler.cs` |
| linha | 169-185 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect (`MEDIUM`), dotnet-specialist (`LOW`) |
| dissenso | severidade divergente, retido `MEDIUM` |
| **estado** | **PENDENTE** |
| nota de estado | Detalhes da trilha, ainda não tratados. |

**A trilha grava relatório completo na publicação e um booleano no rollback, com nomes de variáveis e texto livre.**

Evidência, a publicação serializa o relatório inteiro:

```csharp
validation = new
{
    passed = report.Passed,
    checks = report.Checks.Select(check => new
    {
        name = check.Name,
        status = check.Status,
        message = check.Message,
        location = check.Location,
    }).ToList(),
},
```

Para o mesmo ato governado por outro caminho, o rollback grava apenas
`validation = new { passed = report.Passed }`. As mensagens que entram no primeiro
caso citam nomes de variáveis e hosts extraídos do conteúdo. E a desativação de
layout grava a razão como texto livre do chamador, limitada apenas a 500
caracteres. O `AGENTS.md` fixa que os detalhes carregam evidência JSON compacta e
"Never personal data, variables, or rendered content".

Impacto: dois problemas. Assimetria de evidência, porque publicar e reverter são
atos governados da mesma natureza, ambos gravam aprovação mais evento na mesma
transação, e deixam registros que respondem coisas diferentes à mesma pergunta: um
auditor que compare os dois recebe o relatório completo num caso e um booleano no
outro. E conteúdo, porque o relatório completo de um template com muitas variáveis
são vários kilobytes por publicação, gravados para sempre numa tabela append-only,
particionada e encadeada por hash, cujo custo de verificação e exportação cresce
com o volume, e as mensagens carregam nomes de variáveis e hosts derivados do
conteúdo, que é a fronteira que o documento diz não atravessar. As duas outras
publicações repetem a forma completa, então a assimetria é publicação contra
rollback, não caso isolado.

Recomendação: padronizar os detalhes na forma compacta em todos os efeitos
governados: hash de conteúdo, aprovado, contagem de reprovados e, no máximo, os
nomes dos checks reprovados. O relatório completo continua disponível pelo
endpoint de validação, que é a superfície feita para ele. A razão passa a ser
código de vocabulário fechado em vez de texto livre. Se a decisão for o oposto, ela
vale para os cinco caminhos e o `AGENTS.md` precisa dizer o que "compact" permite
exatamente.

Verificação: teste que publique e depois reverta o mesmo template e afirme a mesma
forma de detalhes nos dois eventos, mais uma assertiva de tamanho máximo do
documento e de ausência de texto livre e de nome de variável.
