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
| `ARC-001` | `MEDIUM` | **RESOLVIDO** | O módulo registra duas abstrações transversais que ninguém consome |
| `ARC-002` | `MEDIUM` | **RESOLVIDO** | Erro fora da codificação `DomainError` atravessando um contrato... |
| `ARC-003` | `MEDIUM` | **RESOLVIDO** | O vocabulário canônico publicado não faz round trip pelo serializador... |
| `ARC-004` | `MEDIUM` | **RESOLVIDO** | A superfície pública real tem quatro contratos, e o documento declara... |
| `ARC-005` | `MEDIUM` | **RESOLVIDO** | `sensitiveVariables` vive na identidade imutável, e o schema que a... |
| `ARC-006` | `MEDIUM` | **RESOLVIDO** | O frescor não é parte do contrato de leitura publicada, é propriedade... |
| `ARC-007` | `LOW` | **RESOLVIDO** | `null` carrega dois significados incompatíveis numa superfície de... |
| `ARC-008` | `LOW` | **PENDENTE** | Os cinco efeitos de layout não preenchem `Application` na trilha |

---
## `ARC-001` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Caching/RedisSetup.cs` |
| linha | 17 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | **A evidência desta ficha se sustentou inteira**, o que nesta revisão é exceção e não regra. Confirmado por medição: nenhum tipo do repositório resolvia `IConnectionMultiplexer` nem `IDistributedCache`, e as únicas ocorrências da primeira fora do arquivo removido eram TRÊS COMENTÁRIOS DE DOCUMENTAÇÃO nos módulos irmãos dizendo que deliberadamente não resolvem a do container. `IDistributedCache` não aparecia em `src/` nem em `tests/`. O cache real do módulo é `MemoryCache`, em `Infrastructure/Integration/PublishedReadCache.cs`, e nenhum ADR ou documento amarra este módulo ao Redis: os dois hits em `docs/` são a barreira de dedupe do módulo `Notifications`, onde `templateKey` é só parte da chave. ACRESCENTADO À FICHA, e alivia o diagnóstico dela: o `Platform.Worker` NÃO é afetado, porque só registra o papel configurado e o `TemplateManagement` não tem papel de worker, então a ausência da seção no `appsettings` dele sempre foi inofensiva. O acoplamento de boot estava confinado à API, que tem a seção, e portanto era latente e não defeito vivo. ACRESCENTADO TAMBÉM, e é a forma concreta desse acoplamento: as fixtures de `Platform.IntegrationTests` injetavam a cadeia de conexão de Redis **só para o host conseguir subir**, e isso saiu junto. PROVA DE BOOT MEDIDA, e não argumentada, que é a verificação que a própria ficha pede: com a seção removida dos dois `appsettings`, o host respondeu `Now listening on` e `Application started`. As falhas de Postgres no log são ambiente (a cadeia de desenvolvimento não declara senha e o contêiner exige) e acontecem em serviços de fundo DEPOIS do start, nunca na validação de opções, que roda antes. ENTREGUE: `RedisSetup.cs`, `RedisOptions.cs` e o diretório `Caching/` inteiro removidos, a chamada tirada do módulo, a seção retirada dos dois `appsettings`, das fixtures de integração e da variável de ambiente deste módulo no `compose.yaml`. O **serviço** `redis` do compose FICA, e as duas variáveis dos irmãos também, porque três módulos usam Redis de verdade. O pacote `Microsoft.Extensions.Caching.StackExchangeRedis` saiu de `Directory.Packages.props` e do `.csproj`, porque só ele fornecia `AddStackExchangeRedisCache`; **`StackExchange.Redis` fica**, porque os irmãos o usam direto. Saiu também a seção `Redis cache` do `README.md`, que documentava exatamente as duas abstrações removidas e era texto de scaffold: mantê-la seria o README afirmando o que o código não faz mais. `.araia/stack-profile.yaml` continua correto com `cache: [redis]`, porque a afirmação é da solução e não deste módulo. PORTÃO MEDIDO E RECUSADO, com o motivo escrito no `AGENTS.md` do módulo: a própria ficha observa que o teste de arquitetura por namespace não enxerga acoplamento na coleção de serviços, o que convida a um portão. Medido, ele **não é viável como regra mecânica**: `TimeProvider.System` é registrado por vários módulos (`AuditModule`, `PartitionManagerSetup`, `ChainVerificationSetup` e o próprio `TemplateManagementModule`) e é abstração de framework legítima, e some-se `IValidator` por varredura de assembly, `AddDbContext`, health checks e rate limiters. A distinção entre abstração de framework que um módulo pode registrar e infraestrutura transversal de terceiro que ele deve envolver é semântica e não mecânica, então o portão exigiria lista de exceção grande e sem princípio, que é pior que portão nenhum. O que segura a linha é a regra escrita mais revisão humana, e isso ficou dito em vez de implícito. A regra registrada para o futuro: se memoização distribuída for desejada, entra por decisão aceita e com wrapper próprio do módulo, no padrão que os três irmãos documentam (multiplexer preguiçoso, `AbortOnConnectFail` forçado a `false`, falha na primeira operação), **nunca por registro de abstração global**. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests 15, SecurityArchTests 14, UnitTests 1657, todos iguais à base. |

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

## `ARC-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 100 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | **A FICHA ESTÁ ESCRITA COMO RISCO PREVENTIVO E NÃO É: a degradação já acontece em produção.** Provado por execução, dirigindo o renderizador real: `ErrorCodes.UrlDomainNotAllowed` atravessa a fronteira, não é membro de `NotificationRejectionReasons`, e cai no braço `_ =>`, chegando ao produtor como `template-render-failed`. São 11 códigos nomeados alcançáveis a partir de `RenderAsync` e o consumidor discrimina 3, então **sete recusas nomeadas degradam hoje**, todas morando em `Domain/ErrorCodes.cs`. Uma recusa de governança de destino de link chega ao produtor como 'seu template quebrou'. E o implementador mediu depois que **sete é PISO e não censo**: há ao menos um oitavo código não publicado (`template-version-not-found`), e ainda `template-disabled` e `template-deprecated`, que **são publicados** e mesmo assim colapsam, porque saem formatados. O `<remarks>` do portão declara que o inventário é piso e que código fora da lista é NÃO REGISTRADO, jamais provado inexistente. UM OITAVO CASO QUE MATA A SAÍDA FÁCIL, achado pelo mediador: `template-not-found` **está** no catálogo publicado, **é** emitível pelo render, e **mesmo assim não é discriminado**, porque o mapa tem três braços e um padrão. **Pertencer ao catálogo não é condição suficiente para discriminação correta**, então 'basta publicar os códigos' está refutado. SEVERIDADE: a mesa mediu que `MEDIUM` não se sustenta e classificou como **`HIGH`**, e o fato que decide é que a degradação alcança **artefato de conformidade**: a leitura periódica agrupa a trilha pelo campo de motivo e o relatório arquivado copia o nome do grupo literalmente, então um motivo degradado entra no arquivo mensal **como categoria errada, e o arquivo não se corrige**. A ressalva honesta que impede ir além: **nenhuma mensagem indevida é enviada**, a recusa continua recusando; perde-se identidade, evidência e alarme, nunca contenção. O reajuste fica registrado aqui e no corpo dos commits, **nunca por reescrita do campo de severidade da ficha**. SÃO DOIS MODOS DE FALHA, e a ficha nomeia um. Além de **embrulhar o valor** (trocar a forma no sítio de chamada: **0 mortes** em 1657 unitários, 15 arch e 14 security; só Docker pegava), há **apagar o braço** do `switch` do consumidor: também **0 mortes**, e o pareamento por valor que já existia **continuava passando**, porque ele prova que a palavra é a mesma dos dois lados e **não que o mapa a usa**. `ReasonForFailedRender` tinha **zero referências em `tests/`**. MEDIÇÕES QUE CORRIGIRAM A NORMALIZAÇÃO: a recusa de tamanho **era** presa, mas no nível da POLÍTICA, onde o teste passa a forma explícita, e ninguém prendia qual forma o SÍTIO DE CHAMADA escolhe (por isso uma mutação matava 5 testes e a outra zero); eram **dois** alarmes sem teste, não um; a atribuição do teste de arquitetura estava errada, e um remendo mirado nele cairia no lugar errado; e o baseline de `A` estava incompleto em duas direções, com um 13º sítio (a política é compartilhada com o preview) e um terceiro consumidor que colapsa toda recusa **incondicionalmente, sem olhar o erro**. A união no eixo de sucesso foi **rejeitada pelos dois participantes**, e a razão decisiva não é custo: ela **não fecha o achado sozinha**, porque o mapa viraria `switch` sobre enum com `_ => throw`, e membro novo compila limpo e explode em execução; trocaria degradação silenciosa por falha em produção e continuaria sem ser impedida no build, logo precisaria do portão derivador de qualquer jeito, custando **54 sítios** medidos, e apagaria o ramo do alarme antifraude com a suíte verde. ENTREGUE, em quatro commits: rede para os dois alarmes ANTES de tudo (porque apagar a chamada do antifraude tinha zero mortes e o passo seguinte refatora a função que o contém); a constante de segurança subiu para a superfície publicada, no padrão que a linha 81 do MESMO arquivo já usava, o que era assimetria não intencional e não decisão; portão de forma sobre o **sítio de chamada** e não sobre a política, em 441 ms na suíte unitária, o que **derrubou a restrição de que ele viveria atrás de Docker**; e portão derivador por reflexão. **O PORTÃO FOI REENUNCIADO DE PROPÓSITO**: ele prova coerência entre catálogo publicado e o mapa do consumidor e **NÃO prova que o renderizador só emite código publicado**, porque as recusas do vocabulário interno passam por baixo dele inteiras. Um portão que se anunciasse como a segunda coisa seria pior que nenhum, porque desarmaria a próxima revisão. Nove mutações, cada uma com o vermelho nascendo na asserção e não no compilador. RECUSADO E REGISTRADO: as duas formas de derivação que os participantes propuseram (marcador de catálogo e catálogo único) **derivam do lado errado**, do que o produtor DECLAROU publicar, enquanto o defeito está no que ele **EMITE**, e a distância é de sete códigos mais o oitavo caso. A derivação certa tem domínio de emissão, contradomínio do mapa e um terceiro conjunto pequeno de colapsos deliberados com razão registrada; **ninguém a mediu** e ela é trabalho próprio. FICA DECLARADAMENTE ABERTO, e o fechamento não pode ser lido como progresso nisso: **o núcleo impede degradações novas e NÃO conserta as sete que já degradam**, que continuam chegando ao produtor como falha genérica. Consertá-las é decidir, uma recusa por vez, quais viram membro do catálogo do consumidor e quais são colapso deliberado, e isso é decisão de produto sobre vocabulário publicado de evento. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests **23** contra base de 17, SecurityArchTests 14, UnitTests **1661** contra base de 1657. |

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

## `ARC-003` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Integration/V1/Channel.cs` |
| linha | 14 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | **A FICHA MIRA PARCIALMENTE O ALVO ERRADO, e isso foi medido.** O parágrafo de impacto dela descreve **completude de vocabulário** ('acrescentar um canal, ou mudar a grafia, não gera sinal'); a recomendação resolve **round trip**. São problemas diferentes. Medido: acrescentar `Channel.Telegram` a `All` dava **0 erros, 0 avisos, 0 vermelhos**, e **continuaria dando 0 com o conversor aterrissado**, porque um conversor serializa o vocabulário e não afirma nada sobre a completude dele. Mudar a grafia dava 6 vermelhos, **todos no dono e no worker, nenhum no consumidor**. Ou seja: **o buraco maior da ficha não é fechado por nada que a ficha recomenda**, e quem o fecha custa um arquivo de teste e zero produção. Depois da correção, a mesma mutação do canal novo dá **5 vermelhos**. **A ALTERNATIVA 'EQUIVALENTE E MAIS BARATA' DA FICHA ESTÁ DUPLAMENTE MORTA.** O `readonly record struct` é regressão de invariante confirmada com mecanismo: `Result<T>` é ele próprio um struct desse tipo com `T` sem restrição e fabrica falha como `new Result<T>(false, default, ...)`, então toda falha de `Channel.Create` materializaria `Value == null` passando em `is not null` nos gates de consentimento e supressão. E o `record` de referência **entrega ZERO da causa**, porque `record class` **não desserializa** (`NotSupportedException`); ele ainda reprova por desenho o portão de identidade escrito no `STK-005` e compra **falso verde** no teste de igualdade, porque `<Clone>$` é público. **A VERIFICAÇÃO QUE A FICHA PROPÕE TAMBÉM ESTÁ MORTA**: pede igualdade estrutural de `ClassPolicyDefinition`, que uma correção anterior desta revisão tornou `sealed class` sem igualdade, por decisão documentada. O oráculo novo é **re-serialização byte a byte**, e ele é **necessário e insuficiente**: `TryParseDuration` aceita exclusivamente a forma `<inteiro>s` e o serializador produz `00:10:00`, então o teste ficaria verde sobre documento que o parser canônico do módulo **recusa**. Fechado com segundo braço, e o braço **prova a recusa**. **SEGUNDO EIXO, QUE A FICHA NÃO REGISTRA E A RECOMENDAÇÃO DELA NÃO RESOLVE:** `AdmittedDeliveryPlan.Read` colapsava três causas no mesmo `null`, o chamador único lia isso como 'sem plano' e caía no plano publicado **agora**. Medido sobre as funções puras do próprio handler: plano admitido levava a `email`, documento ilegível levava a `push`, que é literalmente o que o comentário logo acima declara que não pode acontecer. `Read` tinha **1 chamador e ZERO testes**. E consertar o round trip **não** conserta isso: um conversor que lance cai no mesmo `catch` e devolve o mesmo `null`. **DUAS DIVERGÊNCIAS DECLARADAS DA RECOMENDAÇÃO**, escritas nos commits e não em silêncio: o conversor ficou **`internal`** e não publicado, porque público acrescentaria um tipo à superfície que **nenhum teste de arquitetura percebe**; e o tipo auxiliar do consumidor **FICOU**, contra a instrução de removê-lo, porque é a fronteira que desacopla a forma da coluna da evolução do contrato. O implementador refinou a justificativa e o refinamento é meu erro corrigido: com o conversor no lugar, serializar direto produziria **a mesma coluna byte a byte hoje**; o que a projeção protege é o **futuro**, a partir do primeiro membro opcional novo. Pelo mesmo motivo, **o conversor NÃO toca a forma da coluna**, ao contrário do que eu afirmei. **O DESEMPATE DA MESA FOI MEDIDO E O MEDIADOR ESTAVA CERTO.** O engenheiro dizia que a divergência de comparador se conserta com uma linha. Medido no caso de configuração mista: com o reparo ingênuo, **não há exceção**, a contagem fica em 1, o guard de 'nenhuma fila' **não dispara**, e o dispatcher **sobe saudável drenando só um canal, ignorando em silêncio o que o operador configurou**. Hoje aquilo falhava **fechado com diagnóstico falso** (dizia faltar adapter que existe); o reparo ingênuo o faria falhar **aberto e em silêncio**. Corrigido normalizando uma vez na porta tolerante e comparando canônico contra canônico nas duas comparações. **ENTREGUE em sete commits**: a distinção dos três estados na leitura, com testemunha carregando a **palavra crua** e não o erro formatado (que vazaria o separador de unidade `0x1F` e o codec de erro de um módulo para o log de outro); o teste que **nomeia o dano** em vez de fixar `null` por três razões, que seria relatório constante; o conversor com os dois braços de oráculo e a forma armazenada fixada literalmente; as três constantes derivadas do vocabulário; os dois testes de vocabulário; e a normalização do canal configurado. **RECUSADO E REGISTRADO**: derivar o subconjunto do módulo irmão falha em duas ondas (`CS9135` em produção porque `const` participa de padrão constante, e `CS0182` em teste por `[InlineData]`), e ele exclui um canal **de propósito e documentado**; e o `switch` de três braços não tem o quarto porque **não existe provider nem mensagem** para ele, então o `throw` é a fronteira honesta da fase. **FICA ABERTO E MEDIDO**: uma das três constantes **não tem oráculo em suíte nenhuma** (mutá-la mata zero testes), registrado como lacuna; as duas formas de fio para duração **já coexistiam antes** e não são regressão do conversor; e tratar o estado ilegível como falha operacional é decisão separada, **não tomada**. Achado colateral que não é deste trabalho: `ContentHashNeutralityTests` **já era instável antes**, provado por A/B pareado (dispersão 2112 na base contra 242 depois, com **duas de cinco corridas** acima do teto na base), ou seja **o teto está calibrado abaixo da dispersão real da máquina**. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests 23, SecurityArchTests 14, UnitTests **1694** contra base de 1661. |

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

## `ARC-004` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Integration/V1/IHistoricalCatalog.cs` |
| linha | 13 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | A evidência se sustentou e a ficha SUBCONTA: são **cinco** interfaces públicas em `Integration/V1`, não quatro. A quinta é `IPolicyRule<in TContext>`, e ela tem natureza diferente, o que é justamente o que decide o desenho do portão. `IPolicyRule` é **contrato invertido**: o módulo dono declara, e quem implementa são cinco regras do `Notifications`, executadas pelo `PolicyStage`. O implementador refinou isso e o refinamento fortalece a exclusão: o `Notifications` registra as **classes concretas**, e a **interface não é registrada por ninguém**, nem pelo dono nem pelo consumidor. POR ISSO A VERIFICAÇÃO QUE A FICHA PROPÕE ESTÁ ERRADA COMO ENUNCIADA: contar interfaces públicas e comparar com o documento exigiria listar `IPolicyRule` como contrato de leitura, o que seria falso, e a correção passaria a mentir de outro jeito. O predicado adotado é derivável e exclui o invertido POR CONSTRUÇÃO: o conjunto de interfaces de `Integration/V1` que o próprio módulo REGISTRA tem que ser igual ao que a seção enumera, e o lado do código é medido do **container real** (`new ServiceCollection()` mais `ConfigureServices`), não de lista literal, para que a regra não possa concordar consigo mesma. Medido: o módulo registra exatamente quatro, nas linhas 68, 69, 70 e 80, e o documento listava três; a que faltava era exatamente `IHistoricalCatalog`, registrado pelo módulo e consumido por `Compliance/.../GetNotificationEvidence`. ENTREGUE: a seção ganhou a entrada com o DTO, a frase que separa os dois catálogos (o publicado responde 'o que sairia agora', o histórico 'o que saiu naquele momento') e a regra de que a leitura histórica não é memoizada nem como ponteiro corrente, confirmada no código (o `HistoricalCatalog` recebe só o `DbContext`, usa `AsNoTracking` e não toca o carregador nem o cache). Mais o portão em `Platform.ArchTests`, com igualdade exata nos dois sentidos e tripwire de vazio ancorado no cabeçalho. UMA EDIÇÃO QUE NINGUÉM PEDIU E QUE A PRÓPRIA CORREÇÃO TORNOU NECESSÁRIA: o documento afirmava `Only published state is visible: drafts and superseded versions stay internal`, e com o contrato histórico declarado essa frase virou FALSA, porque ele devolve versão superseded. Sem reescrevê-la, a correção teria trocado uma omissão por uma **contradição**. QUATRO MUTAÇÕES, e a quarta foi acrescentada pelo implementador contra o meu roteiro, com razão: as três que eu pedi nunca exercitavam a asserção do segundo sentido, porque a mutação do cabeçalho matava aquele teste no tripwire e deixava `unregistered.ShouldBeEmpty` sem prova de que pode falhar; ele trocou o registro da interface pelo da classe concreta e obteve o vermelho que faltava. CORRIGIDO TAMBÉM UM DEFEITO INTRODUZIDO POR ESTA REVISÃO: a linha que obriga a atualizar o documento na mesma mudança que altera contratos públicos era a última do arquivo e virou meio dele, porque correções anteriores apenderam cerca de 380 linhas depois; instrução de fechamento no meio lê como se valesse só para o que vem antes. Movida para o fim, texto preservado byte a byte por igualdade exata. O implementador ainda achou e corrigiu um defeito PRÓPRIO antes de comitar: o script de movimentação usou uma API que no Windows converte toda quebra de linha para a forma do Windows, e o arquivo inteiro virou CRLF contra o `.gitattributes`, sem que `git status` nem `git diff --stat` mostrassem nada. FICA CONHECIDO E NÃO CORRIGIDO, por ser mudança de comportamento de contrato e fora deste escopo: o XML doc de `IHistoricalCatalog` promete `published or superseded`, mas `FindTemplateVersionAsync` filtra apenas por chave e número de versão, **sem status**, então **draft é devolvível pela superfície de auditoria**. O DTO concorda com o código contra a interface, documentando `published, superseded or draft`. Isso contradiz a própria frase que justifica a existência do contrato, que diz que separar os catálogos é o que impede uma superfície de auditoria de citar uma versão que ninguém usou. Foi por isso que a exceção escrita no `AGENTS.md` não enumera status: qualquer enumeração seria falsa contra um dos dois lados. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests **17** contra base de 15, SecurityArchTests 14, UnitTests 1657. |

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

## `ARC-005` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Domain/Template.cs` |
| linha | 35, 68 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | **O MECANISMO DA FICHA É FALSO E A ESCALAÇÃO DELA ESTÁ OBSOLETA, mas o DANO que ela descreve é real e alcançável por um caminho que nem ela nem eu tínhamos.** Falso: existe reconciliação (`TemplateValidation.AddSensitiveVariableDeclarationChecks`) que reprova com *'would never be masked'*, e check reprovado **bloqueia a publicação**; então renomear NÃO desliga o mascaramento em silêncio, e a verificação que a própria ficha propõe **já passava**. Obsoleta: a nota de estado invoca o risco residual de `SEC-001`, que está **RESOLVIDO** com aquele risco caído **por construção**. **O QUE ESTÁ CERTO, PROVADO POR SONDA EXECUTADA:** existe um escape, e ele é **OBRIGATÓRIO E NÃO TOLERADO**. Declarar o nome antigo morto no schema (fora de `required`) e ler o nome novo no conteúdo publica **VERDE**, aprovado por quatro olhos, e a forma armazenada sai com o dado pessoal **completo em claro**. **Sem** o nome morto, a publicação é bloqueada; e como a lista não tem mutador, essa é a única publicação possível de uma versão que pare de usar o nome antigo. **A mensagem de recusa empurra o autor de boa fé para o escape.** Medições que fecham: `ContentHash` **IDÊNTICO** para `["cpf"]` e `[]`, ou seja a lista **nunca foi objeto da aprovação** que a própria ADR define; a checagem é **satisfazível de modo vazio**, provado nu; nome ausente devolve recusa nula **por desenho**, então transportar a recusa é **inerte** aqui; e a trilha grava **nomes de checagem e descarta mensagens**, então o escape deixa um aviso **indistinguível de um schema preparado para locale futuro**. **SEVERIDADE REVISADA PARA `HIGH`** (dano é dado pessoal completo em claro numa trilha append-only incorrigível, alcançável no caminho **honesto** e não no adversarial, sem sinal em runtime e sem sinal durável discriminante). O limite honesto que impede `CRITICAL`: exige um evento de renome, o valor para em loja interna e não em terceiro, e o caminho **declarado** segue mascarando. Registro por commit e errata, **nunca por reescrita da ficha**. **OS DOIS PARTICIPANTES SE CONTRADISSERAM NUM FATO DECISIVO**, e a contradição foi resolvida: eles mediram **payloads diferentes**. A máscara casa **chaves do payload** contra a lista de nomes; no cenário de renome o produtor manda o nome novo, e o outro arranjo media o estado **anterior** ao renome, onde o mascaramento funciona como pretendido. A refutação caiu por evidência. **ALTERNATIVAS MORTAS POR MEDIÇÃO**: manter na identidade com correção governada exigiria tornar anuláveis os campos que sustentam **os dois leitores de evidência de conformidade**, ou seja **tiraria força probatória de toda aprovação do sistema** para acomodar um sujeito sem versão e sem hash; e a variante intermediária, feita de forma aditiva, dá **0 erros e 1694 verdes com comportamento INALTERADO**, ou seja **campo decorativo dentro do hash**, e feita funcional vira a opção vencedora mais uma segunda coluna, com a reconciliação a matando por dentro (identidade como piso faz a lista **nunca encolher**, e a pergunta 'por qual ato uma lista errada fica corrigível' fica sem resposta). **ENTREGUE em quatro commits**: a lista foi para a **versão** e entrou no hash canônico **SEMPRE PRESENTE**, sem recorte condicional, porque o motivo do precedente (preservar bytes históricos) **não se aplicava** e recortar faria vazio colidir com ausente; quatro olhos passa a valer **por construção**, porque o mutador registra editor e quem editou não publica; as duas leituras de achatamento passaram a ler da versão, **sem o que a divulgação de conformidade passaria a responder sobre hoje**; guarda de não regressão que recusa versão que larga variável em vigor; checagem `sensitive-variables-unused` em `Warning`, **obrigatória** porque a trilha grava nomes e não mensagens, e sem ela o escape sairia **aprovado com aparência de rigor, pior que hoje**; e errata em três linhas da ADR, sendo que a do meio **não muda e é ela que transforma o buraco em VIOLAÇÃO DE CONTRATO**, porque a ADR promete aprovação sobre o hash e a lista estava fora dele. **SEIS AFIRMAÇÕES DA MESA CAÍRAM NA MEDIÇÃO**, e duas importam: **existem bytes históricos de hash, e são de teste** (14 literais fixados, todos mudaram), com uma consequência que ninguém nomeou, registrada na doc da migração: **qualquer linha de versão gravada por deploy anterior passa a reprovar a verificação de hash, e publicação e rollback recusam sobre esse relatório**; e a guarda precisava de **três** chamadores e não um, porque rollback é publicação em todo sentido e a validação responderia verde sobre o que a publicação recusa. Mais: eram **6 sítios de fábrica e não 5**, e **o compilador não pegou o sexto**, exatamente o modo silencioso previsto; e a satisfação vazia eram **5 testes e não 1**, fechados por asserção de arranjo que leva a mutação de 4 para **9 de 12** reprovações. Duas condutas que merecem registro: uma mutação com código inalcançável foi **reprovada pelo compilador** e o executor rodou **binário velho devolvendo verde falso**, e ele reescreveu a mutação; e ele tentou recuperar folga no teto de alocação, **mediu que a diferença ficava dentro do ruído, e reverteu a otimização E o comentário que alegava a recuperação**. **FICA DECLARADAMENTE ABERTO, e o fechamento NÃO deve ser lido como progresso nisso**: a lista **OMISSA** não é fechada por alternativa nenhuma, e fechar por detecção de conteúdo está recusado por medição anterior. **O achado passa de 'declaração de ator único, nunca aprovada' para 'declaração aprovada, POSSIVELMENTE INCOMPLETA': é progresso e não é fechamento.** Também abertos: conluio entre autor e publicador, que nada cobre; postura de transporte como decisão própria, com o defeito a responder primeiro de que postura imutável fixada na criação é **ato de ator único sem quatro olhos**, ou seja a trava original movida de uma lista para um booleano; promoção da checagem nova a `Failed`, pendente de medir o preparo legítimo; três documentos-mãe que passaram a divergir; e a suíte de integração, que **não rodou**. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests 23, SecurityArchTests 14, UnitTests **1703** contra base de 1694. |

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

## `ARC-006` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `TemplateManagementModule.cs` |
| linha | 63-67 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | **A observação da ficha está certa e o enunciado dela é impreciso como decisão**: medido, o que estava em jogo eram TRÊS perguntas independentes, e tratá-las como uma só é o que tornava as alternativas incomparáveis. Encapsulamento (a queixa literal, que **não muda comportamento nenhum**), contrato (o consumidor precisa saber quão velha é a resposta?) e controle de parada (existe algo atrás disso que precise parar tráfego mais rápido?). **AS DUAS RECOMENDAÇÕES DA FICHA ESTÃO MORTAS POR MEDIÇÃO.** A primeira, expor o instante: a entrada é escrita com expiração absoluta e nada mais, e o carimbo teria de viajar dentro do valor, logo `0 <= now - LoadedAt <= 60s` **sempre, por construção**, e **o campo tem alcance igual à constante que o guia já publica**. Pior, **todo limiar sobre ele é morto (maior ou igual a 60s) ou NOCIVO (menor que 60s)**, porque abaixo de 60s o ramo dispara e converte artefato de memoização em rejeição de negócio, fazendo a taxa de rejeição depender da taxa de acerto do cache. Some-se que a trilha já ancora por versão e hash, e que o gate desembrulha o carimbo uma linha depois de lê-lo e ainda compõe com outra família, recaindo na proibição de carimbar instante único em resposta composta. A segunda, o Redis: reintroduz cerca de 55 linhas que o `ARC-001` acabou de remover deste módulo, exige decisão aceita e wrapper próprio por regra escrita, e **não move o pior caso**, porque sob canal mudo o contrato volta a ser a janela. **O MECANISMO DA FICHA SUPERESTIMA NUM PONTO**: ela diz que a garantia vale 'se ninguém tiver estourado o teto', sugerindo afrouxamento. Medido, o teto **reforça** o limite, porque despejo produz releitura, com o qualificador de que **reescrever chave existente no teto não é recusado**; só a admissão de chave nova é. **ENTREGUE em cinco commits**, e a ORDEM foi parte da decisão: a testemunha de convergência entre processos entrou **PRIMEIRO**, antes do encapsulamento que ela testemunha, porque escrever a testemunha depois do ato é ordem errada; depois a cerca de ponteiro fechada num mecanismo único, que é ganho de **correção e não de legibilidade**, porque o protocolo estava manual em quatro sítios e **ler a cerca depois do `await` compila, passa na suíte inteira e reintroduz o read-old-write-late por uma janela cheia**, sem que nenhum teste pegue o quinto leitor errado; depois dois portões de inventário **complementares, nenhum contendo o outro** (um pega família de chave nova, o outro pega tipo novo tocando o construtor, que é o veículo provável porque família nova é ato visível); e por fim a extração do leitor compartilhado, feita **depois** de existirem duas cópias reais, e não antes por suposição. **O RESÍDUO FICOU ESCRITO NA MESMA FRASE QUE ANUNCIA OS PORTÕES**, e não numa seção de limitações: membro novo em objeto já memoizado e consumidor novo do carregador existente continuam sendo julgamento humano, porque **um portão que fecha metade dos vazamentos e é anunciado como fechando o risco é pior que portão nenhum**. **ACHADO MAIOR QUE A FICHA, e ele invalidou uma frase publicada que eu mesmo havia citado como fronteira**: a cerca de invalidação é **inerte em todo worker**, porque os 11 sítios vivem em 7 handlers compostos só pelo host de API. Mas a atribuição causal também foi corrigida: a cerca fecha a corrida **intraprocesso** entre carga em voo e invalidação **local**, e o worker não tem invalidação local, então **não há o que cercar e nenhum item do espaço vivo conserta isso**. O limite honesto é **60 segundos mais a leitura já em voo no commit, e esse excedente não tem teto** (mediana 0,228 ms, p90 0,406 ms, **30 segundos sob inanição do pool**). E a mesma frase **tratava leitura e tráfego como a mesma coisa**: entre a decisão de render e o envio existe a fila de dispatch, que estes ponteiros não governam. Corrigido no mesmo lote, em `b86ec3a`, com a **aceitação de risco mantida no limite honesto** e **encurtar a janela RECUSADO**, porque é a alavanca errada por medição, já que a fila drena de qualquer jeito. O caminho para parada rápida por template ficou **nomeado e não escolhido**, porque muda de módulo. **ACHADO DE CAPACIDADE, registrado**: o orçamento de 4096 entradas é **um só** para todas as famílias, um template quente ocupa **duas**, e o joelho fica **abaixo de 2.048** e não em 4.096; 7% a mais de conjunto compra **15 vezes** mais consultas, e **nada testemunha a travessia**. A prosa do cache que fazia o número 4.096 circular foi corrigida. **REGISTRADO E DELIBERADAMENTE NÃO CORRIGIDO**: o cache do framework responde com entrada que reprovou na expiração enquanto ela está sendo substituída (medido 12.237 vezes, com dois controles isolando o mecanismo), o que falsifica a frase absoluta e **não move** o limite; contorná-lo custaria camada própria sobre o tipo de framework. **UMA LIÇÃO DE MÉTODO que vale além desta ficha**: eu classifiquei três testes como contenção com base em corrida por **filtro de classe**, que **não é isolamento**; corrigi para 'reprova isolado' medindo por nome; e o implementador mediu **3 de 3 verdes, uma ordem de grandeza mais rápido**. Três leituras, nenhuma causa estabelecida: o honesto é que **nem 'passa isolado' nem 'reprova isolado' é confiável**, e nenhum dos três deve absolver ou condenar mudança sem corrida por nome, repetida, com dispersão. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests **27** contra base de 23, SecurityArchTests 14, UnitTests 1703, mais a coleção de integração do módulo em 210 com a testemunha nova dentro. |

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

## `ARC-007` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/HistoricalCatalog.cs` |
| linha | 80 |
| tipo-de-evidência | contrato |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | **A ficha fala em DOIS significados colapsados; hoje são TRÊS**, e o terceiro entrou por uma correção anterior desta mesma revisão, a que tirou rascunho da leitura histórica. Ela registrou na época que o caso novo entrava **sob a ambiguidade que esta ficha fecha**, e é o que se fecha agora. Os três caminhos que devolvem `null` em `FindPinnedLayoutAsync`: a versão **não fixou layout**; o pino **não resolve mais**; e o layout fixado **nunca saiu de rascunho**. O XML doc de `HistoricalTemplateVersion.Layout` prometia **um só**. **MEDIÇÃO QUE DECIDIU A FORMA: os casos 2 e 3 são ANOMALIAS, não estados legítimos.** Transições de layout são `Draft -> Published -> Superseded` sem volta; `AddLayoutReferenceChecks` reprova pino que não resolva para layout publicado e a publicação só segue com o relatório aprovado; e as doze fatias de `Features/Layouts/` não têm exclusão. **Logo um estado legítimo e duas anomalias compartilhavam a mesma representação, e o documento nomeava só o legítimo.** O consumidor propaga direto: o `null` virava bloco de layout ausente na evidência, e quem lê conformidade concluía 'saiu sem wrapper' quando o estado real podia ser 'o wrapper existia e o hash é desconhecido'. A ficha chama isso de resposta **errada, não parcial**, e está certa. **A ALTERNATIVA 'DEVOLVER FALHA' DA FICHA ESTÁ FALSIFICADA** e foi recusada por medição: o consumidor faz `IsSuccess ? ... : null`, então falhar por causa do layout **apagaria o bloco de template INTEIRO** da evidência, trocando uma omissão por uma maior, que é o mesmo colapso corrigido noutro ponto desta revisão. **ENTREGUE, de forma aditiva e não por união**, porque é `LOW` e o tipo é `sealed class` com propriedades `init`: um irmão que carrega o **pino cru como a versão o declarou**, presente sempre que a versão fixou algo. Ausente com `Layout` ausente é o único caso legítimo; presente com `Layout` ausente é retenção. **A anomalia passou a ser visível NA EVIDÊNCIA**, e não só num log que quem lê conformidade nunca vê, e o consumidor foi atualizado para expor o pino retido, senão a distinção morreria na fronteira. Corrigida também uma **assimetria criada pela correção anterior**: o caso de rascunho tinha testemunha e o caso de pino não resolvido não tinha, sendo anomalias da mesma família; ele ganhou a sua, em `Error`, com justificativa própria de que **das duas é a pior**, porque no rascunho a linha ainda existe e o hash é recuperável à mão, e aqui a linha sumiu. **UMA AFIRMAÇÃO MINHA CAIU, e a correção AUMENTA o achado em vez de reduzi-lo.** Eu disse que a chave estrangeira era restritiva. Existe um `Restrict`, mas é o de `layout_version` para `layout`, que protege a identidade: **o pino não tem chave estrangeira nenhuma**, são duas colunas soltas sem relação declarada. O que sustenta 'pino que não resolve é anomalia' é a **ausência de rota que apague a linha**, não integridade referencial, e um `DELETE` cru passa sem obstáculo. **O caso 2 é mais alcançável do que a ficha e eu supúnhamos**, o que aumenta o valor da testemunha. Quatro mutações, cada uma isolando uma asserção e nascendo no teste e não no compilador; a primeira serviu também de prova de que a integração roda de verdade, porque a duração curta levantou dúvida e o vermelho nomeando o valor plantado a fechou. **FICA DECLARADO E NÃO É LACUNA:** a evidência distingue legítimo de retido, e **não** distingue uma anomalia da outra entre si; quem precisa da diferença lê os dois eventos de log. **E fica registrado o que não foi possível:** não há caminho unitário para os três caminhos na origem, porque a leitura histórica vai direto ao banco sem semente de cache e o projeto unitário não referencia provedor em memória, então eles são cobertos por integração com o estado anômalo escrito por SQL cru, já que nenhuma rota do módulo o produz. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests 27, SecurityArchTests 14, UnitTests **1707** contra base de 1703, mais o filtro estreito de integração em 5. |

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
