---
language: pt-BR
lens: ARC
lens-name: Architecture
scope: TemplateManagement
source-revision: cc754e5a56dcbe1cae9cf05cf9303f7b0eb189d2
findings: 8
---

# Lente `ARC`: Architecture

Fronteiras, direção de dependência, contratos, consistência, alocação de NFR e
decisões aceitas.

O que já é imposto deterministicamente pelos testes de arquitetura e de
arquitetura de segurança não aparece aqui como achado. Os achados desta lente
tratam de fronteiras que os testes por namespace não enxergam, de contratos
publicados cuja semântica não corresponde ao que documentam, e de decisões
estruturais que nenhum documento registra.

---

## Achados desta lente

Ordenados por estado: o que ainda exige ação fica no fim.

| Achado | Severidade | Estado | Assunto |
|---|---|---|---|
| `ARC-001` | `MEDIUM` | **PENDENTE** | O módulo registra duas abstrações transversais que ninguém consome |
| `ARC-002` | `MEDIUM` | **PENDENTE** | Erro fora da codificação `DomainError` atravessando um contrato... |
| `ARC-003` | `MEDIUM` | **PENDENTE** | O vocabulário canônico publicado não faz round trip pelo serializador... |
| `ARC-004` | `MEDIUM` | **PENDENTE** | A superfície pública real tem quatro contratos, e o documento declara... |
| `ARC-005` | `MEDIUM` | **PENDENTE** | `sensitiveVariables` vive na identidade imutável, e o schema que a... |
| `ARC-006` | `MEDIUM` | **PENDENTE** | O frescor não é parte do contrato de leitura publicada, é propriedade... |
| `ARC-007` | `LOW` | **PENDENTE** | `null` carrega dois significados incompatíveis numa superfície de... |
| `ARC-008` | `LOW` | **PENDENTE** | Os cinco efeitos de layout não preenchem `Application` na trilha |

---
## `ARC-001` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Caching/RedisSetup.cs` |
| linha | 17 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O módulo registra duas abstrações transversais que ninguém consome.**

Evidência:

```csharp
services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    RedisOptions options = sp.GetRequiredService<IOptions<RedisOptions>>().Value;
    return ConnectionMultiplexer.Connect(options.ConnectionString);
});

services.AddStackExchangeRedisCache(redisOptions => { ... });
```

A opção é `[Required]` com validação na inicialização. A busca por consumidores
em todo o `src/` retorna apenas três módulos irmãos, e cada um registra a própria
conexão justamente para não usar esta, com um deles declarando por escrito que
nunca resolve o multiplexer do container porque outro módulo registra o seu.
Nenhum tipo do repositório resolve `IDistributedCache` nem `IConnectionMultiplexer`.

Impacto: três consequências. Acoplamento de boot, porque a validação na
inicialização faz o host inteiro recusar subir sem a cadeia de conexão, para uma
dependência que nada consome. Fronteira, porque este é o único módulo que
registra no container duas abstrações transversais que não pertencem ao seu
contexto, e o teste de arquitetura por namespace não enxerga esse acoplamento,
que acontece na coleção de serviços e não em referências de tipo. E armadilha de
disponibilidade, porque a conexão não força `AbortOnConnectFail = false`,
exatamente o cuidado que os três módulos irmãos documentaram: no dia em que
alguém resolver esse singleton, um Redis inacessível vira exceção de resolução em
vez de falha no ponto de uso.

Recomendação: remover o registro de Redis do módulo junto com os dois arquivos de
configuração e a seção correspondente. O cache real publicado deste módulo é o de
memória. Se a memoização distribuída for desejada no futuro, ela entra por decisão
aceita e com um wrapper próprio no padrão dos três módulos irmãos, nunca por
registro de abstração global.

Verificação: a busca por `IDistributedCache` e `IConnectionMultiplexer` não
retorna ocorrência no módulo. O host sobe sem a seção de configuração, e a suíte
de integração continua verde.

---

## `ARC-002` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 100 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. O código de recusa continua atravessando o contrato publicado fora da codificação `DomainError`, e o consumidor continua comparando a string literalmente. |

**Erro fora da codificação `DomainError` atravessando um contrato publicado.**

Evidência:

```csharp
return Result.ValidationError<PublishedTemplateRender>(
    TemplateValidation.AuthenticationSmsLinkCode);
```

O consumidor, em `Modules/Notifications/Features/Pipeline/Stages/RenderStage.cs:68`,
compara a string literalmente:

```csharp
context.LastReason = string.Equals(
    render.Error, ReasonAuthenticationSmsLink, StringComparison.Ordinal)
```

O `AGENTS.md` fixa o contrário: a string é decodificada uma vez, na borda HTTP, e
handlers e código de domínio nunca a interpretam de volta.

Impacto: este é o único ponto do módulo que devolve uma string de erro fora da
codificação `DomainError`, e é justamente o que atravessa um contrato publicado.
O módulo irmão volta a interpretar a string por comparação literal, o oposto da
regra. Pior: se alguém alinhar esta linha à convenção do próprio módulo,
envolvendo o código no formato padrão, a comparação do consumidor deixa de casar
em silêncio, e a recusa de segurança degrada para o motivo genérico de falha de
render. Um controle antifraude perde sua identidade na trilha por uma mudança que
parece limpeza de estilo. Além disso, se essa string chegar ao mapeamento de
problema HTTP, o decodificador cai no ramo de campo único e produz um tipo
genérico com o código real no detalhe, invertendo código e detalhe na resposta
RFC 9457.

Recomendação: promover o motivo a dado tipado no contrato em vez de mantê-lo como
string de erro. A forma mínima é acrescentar um caso ao resultado do contrato, no
mesmo padrão de união que a rejeição de catálogo já usa, o que é exatamente a
regra "quando o caso de uso produz um resultado composto, modele como valor de
sucesso". Enquanto isso não acontecer, cobrir o acoplamento com um teste que
falhe se a string mudar.

Verificação: teste no módulo consumidor que force a recusa e assegure o motivo
sem depender de igualdade sobre a string de erro. Nenhuma chamada de erro de
validação no renderizador com argumento que não passe pelo formatador padrão.

---

## `ARC-003` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Integration/V1/Channel.cs` |
| linha | 14 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O vocabulário canônico publicado não faz round trip pelo serializador do projeto.**

Evidência: `private Channel(string value) => Value = value;`, consumido por
`public sealed record DeliveryPlanStep(Channel Channel, TimeSpan? Timeout);`. O
consumidor é obrigado a contornar, em
`Modules/Notifications/Domain/AdmittedDeliveryPlan.cs`:

```csharp
public static string Serialize(IReadOnlyList<DeliveryPlanStep> plan)
    => JsonSerializer.Serialize(
        plan.Select(step => new StoredStep(step.Channel.Value, step.Timeout)),
        SerializerOptions);
...
private sealed record StoredStep(string? Channel, TimeSpan? Timeout);
```

O perfil de stack declara `serialization: system-text-json`.

Impacto: o módulo publica o vocabulário canônico de canal em um tipo com
construtor privado, sem conversor e sem construtor sem parâmetros. Todo consumidor
que precise persistir ou transportar os tipos do contrato tem que escrever e
manter a própria codificação do vocabulário deste módulo, e já existe uma, fora
do módulo dono e fora dos seus testes. É exatamente o risco que o próprio módulo
declara querer evitar para o hash de conteúdo, aplicado ao vocabulário em vez de
ao hash: acrescentar um canal, ou mudar a grafia do valor, não gera nenhum sinal
de compilação nem de teste na forma armazenada do consumidor.

Recomendação: publicar em `Integration/V1` um `JsonConverter<Channel>` que
serialize como o valor canônico e desserialize pela fábrica validante,
registrando-o com atributo no próprio tipo. Alternativa equivalente e mais
barata: trocar `Channel` por um `readonly record struct` sobre a string canônica
com fábrica validante, o que também resolve `STK-005`. Feito isso, a codificação
volta para o módulo dono.

Verificação: teste no módulo dono que serialize e desserialize uma definição
completa e compare por igualdade estrutural. O tipo auxiliar do consumidor deixa
de ser necessário.

---

## `ARC-004` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Integration/V1/IHistoricalCatalog.cs` |
| linha | 13 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**A superfície pública real tem quatro contratos, e o documento declara três de forma exclusiva.**

Evidência: `IHistoricalCatalog` é registrado pelo módulo e consumido pelo módulo
`Compliance`. O `AGENTS.md`, na seção de contratos de leitura publicados, enumera
de forma exclusiva: módulos irmãos leem este contexto "exclusively" por três
contratos, e a busca por `Historical` no documento não retorna ocorrência. A
última linha do próprio documento obriga a atualizá-lo na mesma mudança que
altera contratos públicos.

Impacto: a redação é exclusiva e a lista está incompleta. O `CLAUDE.md` da raiz
classifica o `AGENTS.md` como contexto confiável de agente com peso de política de
segurança; um autor ou agente que siga o documento literalmente conclui que este
contrato é superfície indevida e o remove ou o contorna, quebrando a reconstrução
de evidência do módulo consumidor. Some-se a isso que a distinção entre os dois
catálogos é a regra mais sutil da fronteira, documentada apenas no XML doc do
arquivo (o catálogo publicado responde "o que sairia agora", o histórico responde
"o que saiu naquele momento", e misturá-los é como uma superfície de auditoria
começa a citar uma versão que ninguém usou), e não no documento que governa o
módulo.

Recomendação: acrescentar o contrato histórico e seu DTO à seção de contratos de
leitura publicados, com a frase que separa os dois catálogos e a regra de que a
leitura histórica nunca é memoizada como ponteiro corrente.

Verificação: a contagem de interfaces públicas em `Integration/V1` bate com a
enumeração do documento.

---

## `ARC-005` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Domain/Template.cs` |
| linha | 35, 68 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado, e ganhou peso: é exatamente o que impede corrigir por API o risco residual de `SEC-001` nos templates já publicados. |

**`sensitiveVariables` vive na identidade imutável, e o schema que a declara vive na versão mutável.**

Evidência: a lista é preenchida no construtor privado e exposta apenas como
leitura. A busca por métodos públicos que retornam `Result` no agregado retorna
somente depreciação, desativação e a verificação de aceitação de publicação:
nenhum método altera a lista, e o inventário dos 32 endpoints não contém
atualização parcial ou total do template. Do outro lado, o schema de variáveis é
por versão e editável através de um endpoint dedicado. O DTO histórico confirma a
assimetria como decisão: a base legal, o propósito, o time dono, a classe e as
variáveis sensíveis pertencem à identidade, que o módulo cria uma vez e nunca
edita.

Impacto: a lista de variáveis sensíveis vive na identidade imutável, o schema que
declara essas mesmas variáveis vive na versão mutável, e nada reconcilia os dois.
Duas consequências operacionais. Um erro de digitação na criação é permanente:
não há caminho de API para corrigi-lo, e a única saída é desabilitar o template e
recriá-lo sob outra chave, o que quebra a referência que os produtores já usam. E
a evolução normal torna a lista obsoleta: renomear uma variável numa versão nova,
operação legítima e disponível, desliga o mascaramento daquela variável para
sempre, sem nenhum sinal. Este é o mecanismo que torna `SEC-001` irrecuperável.

Recomendação: escolher um dos dois eixos e documentá-lo. Mover as variáveis
sensíveis para a versão, onde passam a fazer parte do hash canônico e portanto do
que a aprovação de quatro olhos cobre, o que fecha o buraco de aprovação (hoje um
dado que decide o que é armazenado em claro não é coberto por aprovação alguma).
Ou mantê-las na identidade e abrir um caminho de correção governado, com quatro
olhos e trilha própria sob uma ação nova no vocabulário de auditoria. A primeira
opção fecha o buraco; a segunda é a de menor migração.

Verificação: teste que renomeie a variável sensível numa versão nova e afirme que
ou a publicação é bloqueada por check, ou o mascaramento continua valendo. A
decisão registrada em ADR e refletida no `AGENTS.md`.

---

## `ARC-006` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `TemplateManagementModule.cs` |
| linha | 63-67 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**O frescor não é parte do contrato de leitura publicada, é propriedade emergente do cache.**

Evidência:

```csharp
// Published read contracts consumed in-process by sibling modules.
services.AddSingleton<PublishedReadCache>();
services.AddScoped<IPublishedCatalog, PublishedCatalog>();
services.AddScoped<IPublishedVariablesValidator, PublishedVariablesValidator>();
services.AddScoped<IPublishedTemplateRenderer, PublishedTemplateRenderer>();
```

Impacto: o cache de leitura publicada é singleton por processo, sem canal de
invalidação, num sistema que o perfil de stack declara com Redis disponível e com
workers separados do host de API. O contrato não oferece garantia de frescor além
de "até 60 segundos, por réplica, se ninguém tiver estourado o teto de entradas".
Os módulos consumidores não têm como distinguir dado corrente de dado envelhecido
nem como forçar convergência, e o módulo não expõe nada para isso. O `AGENTS.md`
aceita a janela como decisão, mas a decisão não está encapsulada num mecanismo:
está espalhada entre uma constante estática, um teto de entradas e uma limpeza
total.

Recomendação: modelar o frescor como parte do contrato, expondo o instante de
leitura junto do valor, ou publicando invalidação pelo Redis já presente no
perfil, para que a janela deixe de ser propriedade emergente do cache.

Verificação: teste de dois processos que publicam e leem, afirmando o limite de
convergência declarado.

---

## `ARC-007` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/HistoricalCatalog.cs` |
| linha | 80 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**`null` carrega dois significados incompatíveis numa superfície de evidência.**

Evidência:

```csharp
// A pin that no longer resolves is itself evidence: the answer omits
// the layout instead of inventing a hash for it.
return layout is null
    ? null
```

O contrato correspondente documenta apenas um dos dois: o layout que a versão
fixou, "absent when it pinned none".

Impacto: `null` significa ao mesmo tempo "a versão não fixou layout algum" e "a
versão fixou um layout que não resolve mais", e a documentação afirma apenas o
primeiro. O consumidor é a reconstrução de evidência do módulo `Compliance`, cuja
pergunta é exatamente o que saiu: ler a documentação e concluir "esta notificação
foi enviada sem wrapper" quando o estado real é "o wrapper existia e o hash dele
é desconhecido" é resposta errada, não parcial, exatamente o erro que o XML doc do
contrato diz querer evitar. Severidade `LOW` porque o caminho está hoje fechado
por dados: a chave estrangeira usa comportamento restritivo na exclusão e não
existe endpoint de exclusão de versão de layout, então o pin só deixa de resolver
por intervenção fora de banda. O achado é a divergência entre contrato documentado
e implementado, esperando o dia em que essa intervenção aconteça.

Recomendação: tornar o terceiro estado explícito, com uma união de fixado, não
fixado e não resolvido, ou com um campo irmão que declare o pin bruto lido da
versão. Alternativa aceitável, se a decisão for que o caso é impossível: devolver
falha em vez de `null` silencioso, e alinhar a documentação.

Verificação: teste que remova a linha da versão de layout fora do caminho de API e
afirme que a resposta de evidência distingue o caso de ausência de layout. O XML
doc do campo passa a descrever os três estados.

---

## `ARC-008` · PENDENTE

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Features/Mutations/PublishLayoutVersion/PublishLayoutVersion.Handler.cs` |
| linha | 118 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **PENDENTE** |
| nota de estado | Não tratado. |

**Os cinco efeitos de layout não preenchem `Application` na trilha.**

Evidência: a entrada de trilha da publicação de layout declara tipo de ator, ator
e ação, e não `Application`. O contrato admite a omissão, porque o campo é
opcional. A busca por atribuição de aplicação nos handlers de mutação retorna
somente os efeitos de template e de política de classe; nenhum dos cinco efeitos
de layout aparece. A causa raiz é o agregado: `Layout` expõe chave, time dono,
locale padrão e status, sem aplicação, ao passo que `Template` a tem.

Impacto: o layout é recurso compartilhado entre aplicações, já que fixar uma
referência de layout não exige relação alguma entre a aplicação do template e o
layout. O `AGENTS.md` afirma que o pin entra no hash canônico da versão, ou seja,
o layout faz parte do que foi aprovado e do que a pessoa recebe. Com aplicação
nula na trilha, um auditor que pergunte "o que mudou para a aplicação X"
filtrando por aplicação não enxerga as transições de layout que alteraram a
moldura renderizada dos templates de X. Severidade `LOW` porque o efeito não é de
runtime: depreciar ou desativar um layout não quebra render existente, já que a
resolução do wrapper carrega a versão fixada sem consultar o status, o que é
coerente com as três condições que o documento define para o check de referência.
O que se perde é atribuição, não disponibilidade.

Recomendação: decidir na arquitetura se layout é recurso global por desenho. Se
for, registrar no `AGENTS.md` que a trilha de layout é intencionalmente global e
que leituras por aplicação não a incluem, para que o leitor não infira o contrário
a partir do restante do módulo. Se não for, acrescentar aplicação ao agregado,
o que é decisão de fronteira.

Verificação: consultar a trilha filtrando por aplicação no intervalo de uma
publicação de layout referenciado por um template dessa aplicação, e confirmar se
o evento aparece. Hoje não aparece.
