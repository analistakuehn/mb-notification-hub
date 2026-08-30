---
language: pt-BR
lens: ENG
lens-name: Software Engineering
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 7
---

# Lente `ENG`: Software Engineering

Correção, coesão, acoplamento, manutenibilidade, tratamento de erro,
observabilidade e escopo de mudança.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `ENG-001` | `HIGH` | **RESOLVIDO** | O slice de preview reimplementa o renderizador publicado e já divergiu... |
| `ENG-002` | `HIGH` | **PENDENTE** | O check `channel-limits` mede a fonte do template, não a mensagem... |
| `ENG-003` | `MEDIUM` | **PENDENTE** | Dois tetos de tamanho para o mesmo artefato, sem relação declarada |
| `ENG-004` | `MEDIUM` | **PENDENTE** | `SetVariablesSchema` promete `Result` e pode lançar `JsonException` |
| `ENG-005` | `MEDIUM` | **PENDENTE** | Os cinco modos de recusa do sandbox não produzem sinal observável |
| `ENG-006` | `MEDIUM` | **PENDENTE** | O locale governa a seleção de conteúdo mas não a formatação |
| `ENG-007` | `LOW` | **PENDENTE** | O documento declara uma regra universal que o código aplica a um... |

---
## `ENG-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Features/Queries/RenderTemplateVersion/RenderTemplateVersion.Handler.cs` |
| linha | 170-176 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist (`HIGH`), dotnet-engineer (`MEDIUM`) |
| dissenso | severidade divergente, retido `HIGH` |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado pela extração da política de saída, e a ficha envelheceu em três pontos que a correção teve de corrigir antes de agir. Duas das três divergências que ela descreve já não existiam: `3c0ee19` e `26f89b3` levaram a normalização de SMS e a guarda de destino para dentro do preview. A terceira, o teto de payload, nunca foi ausência: o preview já o aplicava no validador, com o mesmo número do módulo, e o que havia era diferença de porta e de forma de recusa, que fica registrada como deliberada. A quarta divergência que a investigação levantou, a de o preview embrulhar o layout por uma volta em JSON, foi MEDIDA e refutada nos três eixos que importariam: texto entregue, redação de erro e variável não declarada são equivalentes, porque a volta pelo serializador não sobrevive ao `GetString()` e os dois caminhos rodam com variável estrita. O que restava vivo era uma coisa só, e era a mais grave: o preview não recusava link dentro de SMS de autenticação, e a rede estática não cobre esse caso por construção, porque o check de publicação lê a fonte do conteúdo, a fonte do wrapper e as variáveis DECLARADAS como URL, e um link que chega no valor de uma variável não declarada é invisível para os três. Nenhuma superfície mostrava a recusa ao autor: ela só aparecia no despacho. A correção criou `Domain/RenderedOutputPolicy.Apply`, sítio único chamado pelas duas superfícies, com ordem fixa de quatro passos: normalizar por canal, banir link em SMS de autenticação sobre o texto já normalizado, guardar o destino, hashear o que restou. `SmsContentNormalizer` mudou de `Infrastructure/Templating/` para `Domain/` sem alteração de comportamento, porque `Domain` não pode depender de `Infrastructure`, e `EnforceUrlVariables`, que era cópia byte a byte, virou `Domain/VariablesDestinationPolicy`. As diferenças deliberadas sobreviveram como parâmetro explícito e nunca como omissão: `RefusalShape` porque a forma da recusa é contrato de consumidor, já que o pipeline consumidor compara o texto inteiro do erro por igualdade e qualquer formatação colapsaria a recusa de segurança em falha de render; e `AuthenticationLinkBan` porque a forma mascarada não deve pagar um segundo passe de detector, dado que mascarar remove link e nunca cria. O alarme ficou fora da política, porque `Domain` não loga e o evento carrega aplicação, chave e versão. O preview passou a calcular o hash e deliberadamente NÃO o expõe: medido, o hash canônico fecha em UTF-8 e é cego para a única divergência de conteúdo que a investigação conseguiu provar, então um hash exposto diria "igual" sobre texto desigual, e a resposta do preview nem sequer carrega a versão que respondeu. O detector de arquitetura de segurança foi reancorado, porque medido ele NÃO afrouxaria em silêncio, ficaria vermelho: as duas regexes antigas param de casar a guarda nos dois orquestradores, que continuam casando o wrapper. Uma regra virou quatro, duas delas com cardinalidade, e cada uma pega um adversário que as outras não pegam. Uma consequência da ordem fixa não estava prevista e está registrada: em SMS de autenticação com link para host fora da allowlist, a recusa passou de domínio não permitido, que o consumidor mapeia para falha genérica, para link banido, que ele reconhece como recusa de segurança. É a direção correta, e agora tem portão. Fica deliberadamente aberta a duplicação de ORQUESTRAÇÃO, que a recomendação desta ficha pede e que a mesa recusou com razão registrada: unificar as duas resoluções de layout foi decidido contra por escrito na remediação de `SEC-006`, porque os dois lados têm donos de cache diferentes, e a leitura fresca do preview existe para que o autor que acabou de desabilitar um layout veja a recusa agora e não dentro da janela de 60 segundos. Também ficam de fora, cada um com motivo próprio: levar o contexto por forma ao preview e corrigir a procedência do layout, bloqueados pela pré-condição de o preview não conferir se a versão de layout fixada está publicada, sendo rascunho de layout mutável; e unificar a forma da recusa do teto de payload, que mudaria o corpo do `400` de um endpoint existente. A investigação achou, de passagem e por medição, um defeito que não é desta ficha e não entrou nesta correção: um payload com escape de substituto solitário, que é JSON sintaticamente legal, liga em `JsonElement` sem erro e faz `VariablesPayloadSize` lançar fora do eixo `Result`, atingindo o preview e a ingestão de notificação na primeira instrução de cada um. |

**O slice de preview reimplementa o renderizador publicado e já divergiu em três pontos.**

Evidência, o retorno do preview sem normalização e sem guarda de link:

```csharp
return Result.Success(new Response(
    content.Channel.Value,
    requested.Value,
    resolved.Value,
    subject.Value,
    wrappedBody,
    wrappedBodyText));
```

Confronto com o renderizador de produção, `PublishedTemplateRenderer.cs:255-264`:

```csharp
if (channel == Channel.Sms)
{
    normalizedSubject = ...SmsContentNormalizer.Normalize(normalizedSubject);
    normalizedBody = SmsContentNormalizer.Normalize(normalizedBody);
```

e `PublishedTemplateRenderer.cs:93-102`:

```csharp
if (CarriesAuthenticationSmsLink(template, channel.Value!, full.Value!))
{
    logger.AuthenticationSmsLinkRefused(...);
    return Result.ValidationError<PublishedTemplateRender>(TemplateValidation.AuthenticationSmsLinkCode);
}
```

`SmsContentNormalizer` aparece em um único chamador, e `ContainsLinkLikeText`
também. Os dois arquivos carregam ainda cópias byte a byte de
`EnforceUrlVariables`, `IsAllowedUrl`, `WrapInLayoutAsync`, `RenderFieldAsync` e
da resolução de layout.

Impacto: duplicação com divergência já materializada, em três direções, todas na
direção que importa. Primeira: para SMS, o preview devolve o texto bruto e a
produção devolve o texto normalizado (NFC, controles removidos, quebras de linha
viradas em espaço). O teste `SmsRenderNormalizationContractTests` fixa a
diferença exata: a fonte com caractere de largura zero e quebra de linha produz
texto limpo em produção e texto sujo no preview. O autor usa o preview
justamente para conferir conteúdo e contagem de segmentos de SMS, e recebe uma
resposta que a produção não vai enviar. Segunda, mais grave: um render de
autenticação em que o link chega pelo valor de uma variável é recusado em
produção e renderizado sem objeção no preview, ou seja, o autor vê a forma de
phishing aprovada na ferramenta de conferência e só descobre a recusa quando o
despacho falha. Terceira: o preview não calcula hash. Corrigir o comportamento
hoje exige duas edições coordenadas, e nada no código ou nos testes obriga a
segunda.

Recomendação: extrair o núcleo compartilhado (resolução de locale, resolução de
wrapper de layout, render por campo, aplicação do wrapper, verificação de URL,
normalização por canal e a guarda de link em SMS de autenticação) para um único
colaborador de infraestrutura consumido pelas duas superfícies, cada uma
acrescentando somente o que lhe é próprio: memoização e forma mascarada de um
lado, leitura de rascunho do outro. Diferenças que devam permanecer viram
parâmetro explícito, nunca omissão.

Verificação: replicar o teste de normalização contra o endpoint de preview com a
mesma fonte, afirmando que o corpo retornado é o normalizado. Acrescentar um
caso de preview com propósito de autenticação, canal SMS e variável cujo valor
traz um encurtador de URL, afirmando recusa. Ambos devem falhar antes da
correção.

---

## `ENG-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 411-433 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O check `channel-limits` mede a fonte do template, não a mensagem renderizada.**

Evidência:

```csharp
if (content.Channel == Channel.Sms && content.Body.Length > SmsMaxBodyChars)
...
if (content.Channel == Channel.Push && content.Body.Length > PushMaxBodyChars)
```

Impacto: um corpo SMS de 100 caracteres com `{{ x }}` produz uma mensagem de
qualquer tamanho até o teto de saída do sandbox, 1.000.000 de caracteres por
padrão, e nem o renderizador publicado nem o normalizador de SMS conferem
comprimento após o render. Consequência concreta: um SMS pode sair com milhares
de segmentos, cobrados por segmento e por destinatário, sem que nenhum controle
do módulo tenha se oposto. O wrapper de layout agrava o caso, porque também não
passa por `channel-limits`, conforme `SEC-005`.

Recomendação: acrescentar verificação de comprimento por canal sobre o texto
final normalizado no caminho de render, recusando como falha de validação, e
manter o check estático apenas como aviso de autoria.

Verificação: renderizar um template SMS cujo payload produza 5.000 caracteres e
afirmar recusa com código estável. Afirmar também que o layout fixado entra na
contagem.

---

## `ENG-003` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Domain/TemplateVersion.cs` |
| linha | 17 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**Dois tetos de tamanho para o mesmo artefato, sem relação declarada.**

Evidência: `public const int MaxBodyLength = 512_000;`, aplicado em `SetContent`
e replicado no validador de entrada. O teto do motor que precisa compilar esse
mesmo texto é outro, em `Infrastructure/Templating/TemplatingOptions.cs:21`:

```csharp
public int MaxTemplateSizeChars { get; init; } = 131_072;
```

Impacto: o mesmo artefato tem dois tetos definidos em camadas diferentes, com
relação de quase quatro vezes entre eles e nenhuma ligação declarada. Um autor
grava com sucesso um corpo entre 131.073 e 512.000 caracteres, recebe `200`, e
só descobre no `validate` ou no `publish` que a versão nunca poderá compilar,
por um check `compilation` reprovado cuja mensagem fala de um limite que ele
nunca viu na escrita. Pior, `MaxTemplateSizeChars` é configurável por ambiente
enquanto `MaxBodyLength` é constante de compilação, então a distância entre os
dois pode mudar sem deploy e sem que o caminho de escrita saiba. É também a
faixa que alimenta `SEC-008`, porque as varreduras do catálogo operam sobre o
texto bruto, sem o teto do motor.

Recomendação: derivar um teto do outro. O caminho mais simples é `SetContent`
recusar acima de `MaxTemplateSizeChars`, o que faz o limite aparecer na escrita,
no ponto em que o autor pode agir. Se a configurabilidade por ambiente precisar
ser preservada, o teto configurado passa a ser lido no caminho de escrita e
`MaxBodyLength` vira o teto absoluto de armazenamento. Em qualquer forma, um
único número governa e o outro é derivado.

Verificação: escrita de conteúdo com 200.000 caracteres responde `400` com o
mesmo limite que o check `compilation` reportaria. Teste que afirme a relação
entre os dois tetos.

---

## `ENG-004` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | média |
| arquivo | `Domain/TemplateVersion.cs` |
| linha | 309-315 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. Vizinho de `SEC-001`, que passou a bloquear na publicação, mas o agregado continua podendo lançar `JsonException` fora do eixo `Result`. |

**`SetVariablesSchema` promete `Result` e pode lançar `JsonException`.**

Evidência:

```csharp
if (string.IsNullOrWhiteSpace(schemaJson) || schemaJson.Length > MaxSchemaLength)
{
    return ValidationFailure(...);
}
VariablesSchemaJson = schemaJson;
RegisterEdit(editor);
```

`RegisterEdit` chama `ComputeContentHash()`, que chama `CanonicalHash.OfVersion`,
que executa `CanonicalJson.Normalize`, cujo corpo abre com
`using var document = JsonDocument.Parse(json);` sem `try`. O agregado irmão faz
o oposto: `ClassPolicyVersion.ApplyDefinition` envolve o parse em
`try`/`catch (JsonException)` e devolve `ValidationFailure` antes de calcular o
hash.

Impacto: viola o eixo de erro único que o `AGENTS.md` declara não negociável
("Return `Result<T>` for expected outcomes; reserve exceptions for unexpected
system failures"). O mesmo vale para `VerifyContentHash()`, que roda no caminho
de publicação e recomputaria o hash a partir de uma coluna que deixou de ser
JSON válido. Pela porta HTTP a exposição hoje é nula, porque o endpoint liga
`JsonElement` e recusa o que não é objeto, o que sustenta a severidade em
`MEDIUM`: a proteção é do transporte, não do agregado. O que resta é um
invariante mantido fora do tipo que deveria mantê-lo, e a próxima entrada não
HTTP (importação, migração de dados, novo slice) transforma o mesmo caminho em
`500`. A assimetria com `ClassPolicyVersion`, que resolve exatamente esse
problema dentro do agregado, é o que torna a lacuna difícil de justificar como
intencional.

Recomendação: mover a validação de JSON para dentro de
`TemplateVersion.SetVariablesSchema`, no mesmo formato que `ApplyDefinition` já
usa, devolvendo `ValidationFailure` em `JsonException` antes de tocar o estado.
Alternativa equivalente: tornar `CanonicalJson.Normalize` total com uma
sobrecarga `TryNormalize` e traduzir a falha em `Result` nos dois chamadores.

Verificação: teste unitário chamando `SetVariablesSchema` com JSON malformado e
afirmando `IsFailure` com código `invalid-request`. Hoje o teste lança
`JsonException` em vez de falhar a asserção, o que já é o sinal.

---

## `ENG-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 356-373 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. A superfície mudou de forma parcial: as mensagens do engine agora são redigidas antes de sair (`SEC-003`), mas continua não havendo log em nenhum dos cinco modos de recusa. |

**Os cinco modos de recusa do sandbox não produzem sinal observável.**

Evidência:

```csharp
Result<string> rendered = await engine.RenderAsync(source, variables, cancellationToken);
return rendered.IsFailure
    ? Result.ValidationError<string?>(DomainError.Format(
        ErrorCodes.TemplateRenderFailed,
        $"Field '{field}': {rendered.Error}"))
    : Result.Success<string?>(rendered.Value);
```

O arquivo de logger correspondente declara exatamente um método: o alarme de
link em SMS de autenticação.

Impacto: o sandbox tem cinco modos de recusa (tamanho de fonte, deadline de
parede, teto de saída, limite de laço, limite de recursão) e nenhum deles produz
sinal. A falha vira uma string de erro que sobe pelo `Result` e some. Não há
como responder, em produção, a "quantos despachos estão morrendo no teto de
saída" nem a "o deadline de 2000 ms está apertado demais para o template X", que
são exatamente as perguntas que a configuração por ambiente de
`TemplatingOptions` existe para responder. O perfil de stack declara
`telemetry: none`, o que explica a ausência de métrica, mas não a ausência de
log estruturado no dialeto source-generated que o módulo já usa em toda parte.

Recomendação: acrescentar um logger dedicado ao renderizador com um evento por
modo de recusa, carregando chave do template, versão, canal, locale e o modo,
sem conteúdo renderizado nem variáveis.

Verificação: forçar cada um dos cinco modos e afirmar a emissão do `EventId`
correspondente com os campos de domínio, e a ausência de conteúdo.

---

## `ENG-006` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | média |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 75-86 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O locale governa a seleção de conteúdo mas não a formatação.**

Evidência: o `TemplateContext` é construído com `LoopLimit`, `RecursiveLimit`,
`StrictVariables`, `MemberFilter`, `CancellationToken` e `RegexTimeOut`, e nunca
recebe cultura. Em Scriban 7.2.6, `TemplateContext.cs:214`:

```csharp
public CultureInfo CurrentCulture => _cultures.Count == 0 ? CultureInfo.InvariantCulture : _cultures.Peek();
```

Impacto: o módulo resolve um `Locale` por render, tanto para o template quanto
para o layout, e nunca o entrega ao engine. O Scriban formata números e datas em
cultura invariante, então uma notificação `pt-BR` com um valor 1234.5 sai como
`1234.5` e não como `1.234,5`, e uma data depende de o autor lembrar do formato
explícito. Não há caminho para o autor corrigir isso. O lado positivo,
verificado: como a cultura é invariante e fixa, o hash canônico é determinístico
entre hosts, e qualquer correção precisa preservar essa propriedade.

Recomendação: empurrar a cultura do locale resolvido no contexto do render, e
cobrir explicitamente que o hash canônico passa a depender do locale resolvido,
que já é parte do contexto auditado. Se a decisão de produto for formatação
invariante deliberada, o achado cai para nota de documentação, mas a ausência de
qualquer registro dessa decisão nos ADRs é ela própria a lacuna.

Verificação: renderizar o mesmo payload numérico em `pt-BR` e `en-US` e afirmar
formatações distintas e hashes distintos, mais um teste de determinismo
repetindo o mesmo locale.

---

## `ENG-007` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `AGENTS.md`, seção "Logging" |
| linha | primeiro item |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O documento declara uma regra universal que o código aplica a um subconjunto.**

Evidência: o `AGENTS.md` afirma sem exceção que "Each use case ships a dedicated
`<UseCase>.Handler.Logger.cs` file". O inventário mostra 18 de 18 fatias de
`Features/Mutations/` com o arquivo, e apenas 3 de 14 fatias de
`Features/Queries/`: as de render e as duas de validação. Sem o arquivo ficam as
três de diff, as seis de leitura por chave e as duas de listagem.

Impacto: a regra é escrita como universal e o código a aplica a um subconjunto
que ninguém declarou. O impacto operacional é baixo, porque todos os endpoints
aplicam o filtro de log de requisição e a observabilidade mínima está coberta. A
consequência real é sobre o `AGENTS.md` como contexto confiável de agente, que o
`CLAUDE.md` da raiz equipara em peso a uma política de segurança: um autor ou
agente que siga o documento literalmente acrescenta 11 arquivos de logger que
ninguém pediu, e um revisor que o siga marca as 11 fatias como violação. A regra
atual não distingue leitura de escrita, que é a distinção que o código já tomou.

Recomendação: alinhar o documento ao desenho, exigindo o arquivo de logger em
toda fatia que produza efeito governado ou que precise registrar um desfecho de
negócio (mutações, mais validação e render), e declarando que consultas simples
ficam cobertas pelo filtro genérico. Se a intenção original era mesmo universal,
o caminho é o inverso e as 11 fatias ganham seus loggers. O que não pode
continuar é o documento afirmar uma coisa e o módulo praticar outra.

Verificação: a contagem de fatias sem arquivo de logger bate com a exceção que o
documento declarar. Um script que compare o conjunto de fatias contra a regra
escrita.
