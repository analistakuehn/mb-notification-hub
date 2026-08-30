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
| `ENG-002` | `HIGH` | **RESOLVIDO** | O check `channel-limits` mede a fonte do template, não a mensagem... |
| `ENG-003` | `MEDIUM` | **RESOLVIDO** | Dois tetos de tamanho para o mesmo artefato, sem relação declarada |
| `ENG-004` | `HIGH` | **RESOLVIDO** | `SetVariablesSchema` promete `Result` e pode lançar `JsonException` |
| `ENG-005` | `MEDIUM` | **RESOLVIDO** | Os cinco modos de recusa do sandbox não produzem sinal observável |
| `ENG-006` | `MEDIUM` | **RESOLVIDO** | O locale governa a seleção de conteúdo mas não a formatação |
| `ENG-007` | `LOW` | **RESOLVIDO** | O documento declara uma regra universal que o código aplica a um... |

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

## `ENG-002` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `HIGH` |
| confiança | alta |
| arquivo | `Domain/TemplateValidation.cs` |
| linha | 411-433 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado, e a ficha envelheceu num ponto: o trecho que ela cita como evidência já não existe, porque a remediação de `SEC-005` fez o check estático somar corpo mais wrapper. O núcleo permaneceu inteiro, a medida era sobre a fonte, e o pior caso é maior do que a ficha calcula: não são 1.000.000 de caracteres, são 3.000.000, porque o teto do motor mede o que o motor emite e a composição NFC roda depois, na política, e alonga até 3 vezes. Medido chamando a política de verdade, entram 1.000.000 e saem 3.000.000 com sucesso, que são 44.777 segmentos. A unidade escolhida NÃO é a que a ficha implica. O documento de desenho do sistema já dizia, desde antes do código existir, que o limite de SMS se conta em segmentos GSM-7 ou UCS-2, e o código nunca implementou, então caractere era o proxy que precisava se justificar e não o contrário. Medido, o mesmo texto de 1596 caracteres custa 11 segmentos sem acento e 24 com acento, e de 60,8% a 79,4% da prosa pt-BR deste repositório cai em UCS-2, pelo til e pela cedilha minúscula, que a tabela básica não tem. Um teto por caractere anularia o incentivo de escrever sem acento, que corta o custo real pela metade. O número não foi escolhido, foi derivado: 10 segmentos, de `floor(1600 / 153)`, que é o maior teto sob o qual um número só governa, porque `153 * 10 = 1530` cabe no limite de corpo assumido e `153 * 11 = 1683` não caberia, e aí voltariam a existir dois tetos sem relação declarada. Fica escrito ao lado da constante que 1600 é inferência sobre o limite do provedor NÃO verificada neste repositório, que a regra é `S = floor(L / 153)`, e que 10 é o teto máximo defensável e não uma política de gasto: quem paga a conta só pode baixá-lo. O efeito medido é de 44.777 segmentos para 10. A recomendação da ficha de rebaixar o check estático a aviso foi RECUSADA, e essa recusa é deliberada: ela contradiz duas regras escritas que declaram a validação integral controle de segurança não reduzido, e a ficha a recomenda sem apresentar um único template legitimamente bloqueado hoje. O check estático continua bloqueante; o que mudou foi a redação dele, que dizia "SMS body template exceeds 1600 characters" e se lia como garantia sobre a mensagem quando garante só sobre a fonte. O teto novo é o quinto passo do sítio único de saída criado para `ENG-001`, depois da normalização, depois do banimento de link e depois da guarda de destino, antes do hash. A posição depois das duas verificações de segurança é escolha declarada e custa 12,3 ms por render recusado: se o tamanho respondesse antes, um operador leria "grande demais" e nunca saberia que havia link de phishing dentro de um SMS de autenticação. A forma mascarada NÃO é medida, com enum próprio que deliberadamente não reaproveita o do banimento, porque as justificativas são opostas: aquele vale porque mascarar só remove link, este porque mascarar pode acrescentar texto, e um código de autenticação de um dígito bastaria para recusar mensagem legal pela sua própria cópia de trilha. A recusa ganhou palavra própria no catálogo fechado, com a linha correspondente no guia do produtor, e na mesma edição entrou a linha que faltava para um motivo que estava no catálogo e fora do guia desde a remediação de `SEC-006`; um portão novo passou a exigir linha no guia para todo membro do catálogo, e ele nasceu vermelho por causa desse motivo antigo. Duas afirmações que a mesa ratificou foram FALSIFICADAS na implementação, as duas na direção permissiva, e as duas eram a mesma causa raiz: dividir um total por uma capacidade em vez de segmentar de verdade. No braço UCS-2, 67 emojis são 3 segmentos e a divisão prevê 2, e a fronteira rápida de 670 admitiria sem contar um corpo de 331 caracteres astrais que custa 11; no braço GSM-7, uma sequência de escape também não pode ser partida, então um segmento carrega 76 caracteres de extensão e não 76,5, e a faixa de 761 a 765 era admitida como 10 ocupando 11. Os dois braços passaram a segmentar percorrendo, sem nenhuma divisão, com a fronteira em 660 e o limitante universal em `ceil(len / 66)`. Fora do escopo, com razão registrada: push, porque a unidade dele é byte e o orçamento do provedor é compartilhado com um payload de dados que a política nunca vê, o que torna o número não derivável no ponto da decisão; e-mail e WhatsApp, sem número; e o `ENG-003`, porque são dois eixos e não um, já que a fonte não é cota do render em nenhum dos dois sentidos. Limite aceito e declarado: a varredura antes de implantar não responde por leitura da fonte, e o endpoint de revalidação lê a fonte, então uma versão publicada continua verde no relatório enquanto o despacho a recusa. O instrumento que resta é o preview por versão publicada, com variáveis representativas. |

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

## `ENG-003` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Domain/TemplateVersion.cs` |
| linha | 17 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado, e o defeito é maior do que a ficha descreve. Ela diz que os dois números não têm relação declarada. O fato medido é que 512.000 é INALCANÇÁVEL por qualquer configuração válida: o teto do motor é `[Range(1, MaxMemoizableSourceChars)]`, e esse máximo é 208.411, imposto na PARTIDA do host, então a faixa morta é de no mínimo 303.589 caracteres em qualquer ambiente e de 380.928 no padrão. Um dos números é 2,46 vezes maior do que o máximo que o outro poderia alcançar. Três fatos que a ficha não registra decidiram o desenho. Primeiro: a seção de configuração que tornaria o teto ajustável por ambiente NÃO EXISTE em arquivo nenhum do repositório, nem em `appsettings`, nem no worker, nem no `compose.yaml`, então a configurabilidade é capacidade nunca exercida e o valor vigente em todo ambiente entregue é 131.072. Segundo: 131.072 tem base medida em três lugares independentes, enquanto 512.000 NÃO TEM ORIGEM em código, comentário, teste, ADR, desenho de sistema ou documento de fase, e essa ausência é ela própria parte da justificativa para removê-lo. Terceiro: `MaxBodyLength` não tinha NENHUM teste, em nenhum dos dois agregados e em nenhum dos quatro campos, então o teto de escrita era código sem oráculo. A correção criou `Domain/TemplateSourceSize`, com `MaxChars = 131_072` e a derivação escrita ao lado, declarada honestamente como ÂNCORA DE MEDIÇÃO e não como derivação aritmética. As duas declarações de `MaxBodyLength`, a de template e a de layout, sumiram, e os oito usos dos agregados mais as quatro regras dos dois validadores passaram a ler o número único. O `[Range]` foi invertido: o superior passou a ser a constante de domínio, então a configuração só pode APERTAR, e o default passou a ser a própria constante, o que zera a faixa morta no padrão. 208.411 foi recusado por razão de natureza e não de custo: é resto de divisão entre cinco constantes de memoização, uma das quais o próprio arquivo declara que se move na próxima atualização do motor, e a hipótese que o produz, dois nós por caractere, é irreal por fator de 25,6, porque a forma mais densa que o teto de tokens admite entrega 0,078 nó por caractere. Um teto de segurança cuja renumeração já está anunciada não é um teto. A mesa recusou levar a leitura de configuração para o validador de escrita, por proporcionalidade e não por erro técnico: depois da inversão a faixa restante é zero no padrão, só existe onde um operador apertou de propósito, e falha fechada; o preço seria um desvio em dezessete validadores, todos hoje sem dependência de construtor. O limite aceito está escrito: se alguém apertar, o autor recebe `200` na escrita e a recusa no `validate`, com o número apertado e correto. Dois testes-gatilho nascem verdes e existem para reabrir essa decisão no dia em que o default deixar de ser a constante ou em que algum `appsettings` entregue apertar de fato. A correção fechou por consequência um defeito que a ficha não previa e que ninguém procurava: o `[Range]` era uma CONDIÇÃO DE BOOT disfarçada. Baixar `MaxResidentBytes` de 64 MiB para 32 MiB, edição de uma linha que um dimensionamento de container justificaria, leva o teto de memoização para 104.205, abaixo do default, e o host DEIXA DE SUBIR sem nenhuma configuração presente. Medido por três agentes independentes, com a sensibilidade completa: 48 MiB sobe, 32 MiB não sobe; `BytesPerNode` a 200 sobe mas passa a recusar configuração antes legal, a 255 não sobe. Nenhum teste pegava essa via. Tirar `MaxMemoizableSourceChars` do atributo elimina a classe inteira, e o laço entre os dois números foi substituído por asserção de TEMPO DE COMPILAÇÃO ao lado do cache, que é estritamente melhor do que a verificação de boot que ela substitui, porque falha no build e não no deploy. Acréscimo do mediador, medido: o limite INFERIOR do `[Range]` passou de `1` para o teto de assunto, porque uma configuração de 500 subia hoje e o assunto também é fonte analisada pelo motor, o que recriaria a mesma faixa morta na outra ponta do intervalo. Uma razão em que os dois participantes concordavam estava errada e foi corrigida antes de virar comentário: a regressão sobre publicado é zero porque o número que governa NÃO SE MOVE, e não porque os caminhos de clone contornem o guarda, já que o rollback reroda a validação. Nenhuma migração de coluna, porque `body` e `body_text` são `text` sem comprimento, e a coluna não deve declarar tamanho por razão de plataforma: `varchar(n)` conta pontos de código e `string.Length` conta unidades UTF-16, então a coluna nunca seria a mais apertada e só serviria para travar mudança de regra atrás de migração. Fora do escopo, com razão registrada: a varredura bruta do catálogo encolhe por fator de 3,9 e não fecha, porque o catálogo não curto-circuita por desenho declarado, com economia medida de 67,7 ms de CPU, 27,84 MB e 3.371 itens de resposta por chamada; um check de variável sensível por OCORRÊNCIA e não por variável, que faz o relatório crescer com o corpo; e três `catch (RegexMatchTimeoutException)` inalcançáveis, que são pior que código morto, porque o comentário do primeiro afirma falha fechada por uma rota que nunca executa. O limite de 256 KB de corpo HTTP, declarado no desenho e não implementado, ganhou errata datada no próprio desenho: impô-lo como está escrito recusaria conteúdo legítimo, porque um corpo pt-BR de 131.072 caracteres serializa em 786.443 bytes, seis por unidade, já três vezes o limite declarado. Regressão declarada: uma sonda existente montava 420.000 caracteres para exercitar um defeito quadrático já corrigido, e o teto novo a impede; ela foi reapontada ao máximo que a produção admite, derivando o comprimento da constante, e a perda de folga está registrada. |

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

## `ENG-004` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM`, elevada a `HIGH` na remediação |
| confiança | média, elevada a alta na remediação |
| arquivo | `Domain/TemplateVersion.cs` |
| linha | 309-315 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-engineer |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado, e a ficha descreve o sintoma mais barato de um defeito de classe. O enquadramento correto não é "o agregado pode lançar": é que a política de recusa por ilegibilidade foi aplicada ao payload do produtor e NÃO ao schema do autor nem à definição de política. O commit `7720da9` fechou um terço deste defeito e esta ficha nomeou um sexto dele. Três premissas caíram por medição, e duas delas eram minhas. PRIMEIRA, e é a que inverte tudo: a rota que a ficha nomeia não é a exposta por transporte, é a MAIS BARATA de disparar. `{"a":"\ud800"}` são catorze bytes ASCII, ligam como objeto, passam a guarda do endpoint, o escape SOBREVIVE ao `GetRawText()`, e a canonização lança. Medido por três agentes independentes, com bytes montados por código; a suposição contrária vinha de sonda cujo escape havia sido colapsado por uma camada intermediária, e o número de comprimento devolvido era o sinal que deveria ter sido lido. SEGUNDA: o tipo lançado NÃO é `JsonException` nem `ArgumentException`, é `InvalidOperationException`, e ele nasce no ESCRITOR, depois de o parse ter tido sucesso. Portanto a recomendação desta ficha, guardar o setter no molde do agregado irmão, capturaria um tipo que não ocorre; ela conserta a aparência e mantém o defeito. TERCEIRA: a publicação NÃO estava protegida. O check do catálogo só faz `JsonDocument.Parse`, que aceita o escape, então em uma posição o catálogo lança e na outra passa verde e a verificação de integridade lança depois. O inventário passou de um sítio para DOZE, todos alcançáveis, e três não estavam em nenhum mapa: as projeções de resposta, que fazem o `GET` dar `500`; o `GetRawText` do próprio endpoint; e a validação da definição de política submetida. O mais grave é que `VariablesSchema.TryParse` é um método cujo nome promete não lançar e que LANÇA, o que significa que o catálogo de validação, que é o portão de publicação do módulo, não podia ser usado como defesa contra esta classe. E o agregado irmão que a ficha cita como bom precedente cai pela porta da frente por duas falhas somadas: o `catch` não nomeia o tipo que ocorre, e o hash roda fora do `try` de qualquer jeito. A correção não inventou guarda: estendeu ao caminho de autoria a política que o módulo já escreveu, com o mecanismo compartilhado que ele já criou. Não existe ponto de estrangulamento único, e essa foi a correção de metáfora que decidiu o desenho: é UMA REGRA sobre TRÊS FRONTEIRAS. A canonização virou total, com veredito de quatro estados em vez de `Result` ou de `bool` com `out`, porque o dialeto do módulo para função pura que pode recusar é veredito, e porque o estado admitido é carregado e não negado, de modo que o valor padrão da estrutura recusa em vez de admitir. Os tipos capturados são dois e nada mais; `ArgumentException` ficou DELIBERADAMENTE de fora, por regra escrita do próprio módulo, porque capturar defesa não medida é como uma medida deixa de poder falhar, e porque essa exceção só ocorre com chamador quebrado, que é exatamente para o que exceção existe. A regra de forma entrou no mesmo veredito, pela frase que o módulo escreveu um dia antes: as duas recusas são descobertas por uma travessia, e separá-las foi o que deixou metade da regra fechar. Deixar de fora permitiria um schema `"texto"` publicar declarando zero variáveis, fazendo toda checagem de nome não declarado passar por vacuidade. A canonização saiu de dentro do cálculo de hash, que voltou a ser totalmente total, com a falibilidade em um lugar só. Custo: ZERO de travessia extra no caminho quente, porque a exceção nasce dentro da escrita que já acontecia, então a guarda envolve a caminhada existente em vez de acrescentar uma. NEUTRALIDADE DE HASH provada por dezesseis vetores capturados antes de qualquer edição, incluindo o teto de 64.000, e conferidos idênticos depois; a cerca tem dente, provado por mutação que a deixou vermelha em exatamente duas entradas. Uma divergência contra o plano foi relatada em vez de escondida: uma das rotas passou a responder recusa do catálogo em vez de recusa de integridade, porque a guarda faz o catálogo recusar mais cedo, e o teste foi reescrito para afirmar o que o código faz, ficando mais forte. Fora do escopo, com razão registrada e gatilho de reabertura: a canonização NÃO é injetora, porque chave duplicada colapsa e `{"a":1,"a":2}` produz o mesmo hash que `{"a":2}`, o que é oráculo que mente, e não entra porque recusar duplicata mudaria a aceitação de endpoint público e poderia transformar versão publicada em bloqueada, colidindo com a neutralidade; as projeções de resposta, cuja recusa correta exige decisão de contrato em cinco lugares; a forma canônica no teto cair no heap de objetos grandes; e a substituição silenciosa de UTF-8 sobre os campos de texto, que corrigir mexeria no hash de toda linha armazenada. Uma correção de registro: das três razões escritas para a coluna ser `text` em vez de `jsonb`, ordem de chave e reescrita de escape não sustentam nada, porque a canonização ordena e resolve escape, provado pelos próprios vetores; sobra a reescrita de literal numérico. A errata está no comentário da migração. |

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

## `ENG-005` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | alta |
| arquivo | `Infrastructure/Integration/PublishedTemplateRenderer.cs` |
| linha | 356-373 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | Fechado PARCIALMENTE, e a parcialidade é por impossibilidade MEDIDA e não por escolha de escopo. A ficha acertou o diagnóstico e errou o inventário nos dois sentidos ao mesmo tempo. Os modos não são cinco, são onze, e a ficha não conta a complexidade de fonte, que tem duas submodalidades e nasceu de remediação posterior, nem o erro de parse, nem o cancelamento do chamador, nem a falha de sistema. E DOIS dos cinco que ela nomeia não são distinguíveis: limite de laço e limite de recursão colapsam na mesma faixa que erro comum de autor, então a recomendação da ficha não é implementável como escrita para eles. Isso não é omissão de quem escreveu o motor: medido no assembly do Scriban 7.2.6, NÃO existe subtipo de exceção para nenhum dos dois, o laço tem TRÊS textos alcançáveis mais um quarto latente, a recursão tem dois, e o parser tem um quinto que COLIDE com o de recursão. Três vias de separação foram testadas e as três caíram por medição. Sobrescrever o gancho de passo de laço não resolve, porque ele dispara ZERO vezes na forma de intervalo e a iteração interna não chama gancho nenhum, então cobriria duas das quatro variantes; e pior, o gancho devolve booleano e devolvê-lo falso produz TRUNCAMENTO SILENCIOSO, quatro caracteres para um laço de cinquenta, sem erro, que é exatamente a corrupção que outro limite do motor existe para impedir. Sobrescrever a avaliação de nó separa o laço por completo, mas a recursão COLIDE com quatro erros de autor comuns, todos produzindo o mesmo tipo de nó, e custa de 2,9% a 7,2% no despacho com risco de o gancho parar de disparar em silêncio numa migração para a variante assíncrona. Casar texto foi recusado porque os alvos são móveis, interpolam o valor de configuração vigente e um deles já colide. Conclusão registrada: em Scriban 7.2.6 o resíduo é IRREDUTÍVEL, e ele fica declarado num valor de enum chamado `Unclassified`, nome deliberado, cujo comentário lista em letra o que ele carrega e por quê, com um teste-sentinela da versão do pacote como gatilho de reabertura. Duas premissas da própria investigação caíram durante a mesa. A colisão com o parser é REAL e confirmada por execução, mas o gatilho não é profundidade, é PILHA RESTANTE: rodando pelo módulo numa thread estreita, com 249 parênteses, o oráculo de recursão fica verde sobre um erro de parse. E a segunda porta do prazo NÃO é inalcançável: medido vinte de vinte, a regex catastrófica se divide entre as duas portas porque os dois números de timeout são o mesmo e a resolução do timer decide, então atribuir o modo só numa perderia metade das recusas de forma não determinística e invisível. Na prova de mutação a perda mostrou-se ainda maior que metade naquele host. O desenho: o motor devolve o modo como enum tipado AO LADO de um `Result` que não muda um caractere, o que preserva a comparação literal do módulo irmão e não toca o achado aberto sobre aquele eixo. Struct por exigência do caminho quente, porque o canal atravessa o render bem-sucedido do despacho. As duas superfícies emitem, cada uma com a identidade que já possui, em arquivos de logger que JÁ EXISTEM: zero arquivos novos, o que é a razão de este remédio não agravar o achado sobre a regra de logger por fatia. Dois eventos, um por superfície, com nível por SUPERFÍCIE e nunca por modo. A razão de recusar um nível mais alto para erro de parte é medida: no despacho, com variável estrita ligada e payload vindo do módulo irmão em tempo de execução, variável ausente cai no resíduo, que é defeito do payload do chamador e não do catálogo, e um alarme que dispara no volume dominante ensina o operador a ignorá-lo. O evento NÃO carrega a mensagem do motor em hipótese nenhuma, e a razão fecha por forma: o mesmo texto, como mensagem de check, já é proibido na trilha por varredura executável, e a varredura de log existente compara NOME de placeholder e não valor, então não o pegaria. Fato novo que reforça: a redação de valores só troca substring exata de escalar com três ou mais caracteres, então valor reformatado pelo motor escapa dela. Custo medido do log: 320 bytes e 392 nanossegundos, contra quase 30 KB e 18,7 microssegundos da própria recusa que ele descreve, ou seja um por cento, e zero no caminho de sucesso. Dois oráculos que já existiam passavam por MOTIVO ERRADO e entraram nesta correção: um casava um fragmento que só aparece numa das quatro variantes de laço, o outro ficava verde sobre erro de parse. Foram reescritos para afirmar o que a decisão afirma, e não para melhorar o casamento, porque melhorar o casamento seria adotar a técnica que a decisão rejeita. O portão de arquitetura de segurança criado na remediação de `ENG-001` FICOU VERMELHO SOZINHO durante esta correção, pelo próprio detector de conjunto vazio, quando os sítios de produção mudaram de nome; a âncora foi ampliada e o alcance provado idêntico antes e depois. Fora do escopo, com razão registrada: sinal na análise de origem, porque o relatório de validação é sinal mais forte e a trilha já o registra; contagem, alarme e limiar, que pertencem à fatia de telemetria e continuam sob a decisão de não haver instrumento de métrica; paridade de oráculo entre as duas superfícies, que é achado aberto de outra lente; e o custo da prévia, que aloca 4,1 vezes e leva 3,1 vezes o do despacho por abrir um contexto por campo, achado de desempenho próprio que NÃO deve ser usado para justificar abrir escopo na prévia, porque a prévia é a única escritora da memoização do host de API. |

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

## `ENG-006` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `MEDIUM` |
| confiança | média |
| arquivo | `Infrastructure/Templating/ScribanTemplateEngine.cs` |
| linha | 75-86 |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-specialist |
| **estado** | **RESOLVIDO** |
| nota de estado | O diagnóstico da ficha está certo e três afirmações dela caíram por medição, sendo que uma delas era a premissa que a própria recomendação mandava preservar. PRIMEIRA: "não há caminho para o autor corrigir isso" é FALSA. Os filtros de formatação de objeto, de número e de data estão no sandbox e os quatro aceitam cultura explícita como argumento; o autor escreve o filtro com o tag de cultura hoje, sem uma linha de código nova. Nem o guia do produtor nem o `AGENTS.md` documentam isso, então o caminho existe e ninguém sabe dele. SEGUNDA, e é a que muda a natureza do trabalho: "como a cultura é invariante e fixa, o hash canônico é determinístico entre hosts, e qualquer correção precisa preservar isso" é FALSA COMO DESCRIÇÃO DO PRESENTE. Medido no mesmo commit, com os dois hosts do projeto: `en-ZA` rende `1 234 567,50` no Windows com ICU 72.1.0.4 e `1,234,567.50` no Ubuntu 24.04 com libicu 74.2, com SHA-256 diferentes. Não há propriedade a preservar, há propriedade a CONSTRUIR. TERCEIRA: um tag bem formado e desconhecido, que a validação do módulo aceita, NÃO formata em invariante; ele segue o locale do SISTEMA OPERACIONAL no Windows e a raiz do ICU no Linux, provado por eliminação contra quatro culturas de thread e contra a raiz. São 555 dos 676 pares de duas letras que resolvem assim, sem lançar, porque `Locale` é forma BCP 47 e nunca lista branca de culturas. A investigação achou, e o mediador reproduziu por execução, um DEFEITO DE SEGURANÇA maior que este achado e que não é dele: o objeto de builtins do sandbox é um só por processo, é empurrado ao fundo da pilha de todo contexto e preservado no reset entre renders, e é MUTÁVEL para membros de dados e membros novos. Um template publicado de uma aplicação grava um valor do destinatário nele e um template de OUTRA aplicação o lê; e sobrescrever o formato de data padrão move toda data implícita de todo render do processo até reiniciar. As funções já eram recusadas; os dados não. É publicável sem sinal nenhum, porque a saída do plantio é vazia e o coletor estático não enxerga atribuição a membro, já que ele só registra quando o alvo é variável simples, então o relatório de publicação sai limpo. O atenuante de "insider sob quatro olhos" foi examinado e RECUSADO: quatro olhos só é controle quando o segundo par de olhos tem o que ver, e aqui não tem; o que o caminho de insider reduz é a população de atacantes, não a probabilidade por tentativa. ENTREGUE NESTA CORREÇÃO: o selo do objeto compartilhado, que é o passo de custo de reversão ZERO e fecha o vazamento. Inventário medido e confirmado de forma independente pelo implementador: profundidade 2, oito grupos, cinco membros de dados, sem terceiro nível. Regressão medida como zero em nove construções legítimas e depois em corpus de setenta expressões, com os dois binários de produção construídos e comparados por sonda de reflexão contra o motor real, não contra réplica. Uma divergência do implementador contra a mesa, medida por três caminhos: o selo da RAIZ não sustenta nada, porque o motor já recusa escrita que resolve na raiz e todo render empurra globais próprios acima dela; quem fecha são os oito selos de grupo. Ele manteve o da raiz porque é grátis e porque uma versão que pare de sombrear a raiz reabriria o buraco em silêncio, e ESCREVEU que é redundante, em vez de deixar o comentário afirmar que as duas metades trabalham. Uma sentinela nasce verde e fixa o inventário, para que uma atualização do motor que traga nono grupo ou terceiro nível não torne o selo incompleto em silêncio. FICA PENDENTE, e o bloqueio é de informação e não de decisão: a mesa decidiu invariância IMPOSTA, com banimento do argumento de cultura, culturas predefinidas ligadas e digest na imagem base, mais ADR próprio. Nada disso foi entregue porque o passo IRREVERSÍVEL do plano, o check de publicação, aciona a regra escrita de que versão publicada recusada pela regra atual perde rollback sem carve-out, e decidir isso sem saber quantas versões alcança é decidir no escuro sobre efeito irreversível. Faltam duas leituras de banco por ambiente, e as consultas ficaram escritas no `AGENTS.md`, validadas contra um Postgres descartável com linhas semeadas para casar e para não casar: quantas versões publicadas usam argumento de cultura nos filtros, e quantas contêm atribuição a membro de builtin. **Se o segundo número for maior que zero, não é correção, é incidente.** Registrado também que `InvariantGlobalization` está PROIBIDO, porque torna a composição de acentos um no-op silencioso e faz a consulta de normalização mentir, atacando o primeiro passo da política de saída; e que os quatro testes que já falham sob essa configuração NÃO devem ser consertados, porque são o oráculo acidental que este repositório tem de graça para a dependência de ICU do caminho de saída. FECHAMENTO EM 2026-08-30. O bloqueio era de informação e a informação chegou: o ambiente foi levantado e os dois censos rodaram contra o banco real. **Os dois devolveram zero, e NÃO é incidente.** Não há uma linha viva em nenhuma tabela de nenhum dos sete esquemas e não há ambiente publicado, então o raio do passo irreversível é zero e o carve-out de rollback não precisou existir. ANTES de aceitar esse zero, as duas consultas foram submetidas a sondas semeadas numa transação revertida, e REPROVARAM EM QUATRO PONTOS, todos corrigidos: (a) só enxergavam aspas simples, e perdiam `math.format "N1" "pt-BR"`, que é a forma canônica do Scriban e era o próprio exemplo que derrubou a premissa desta ficha; (b) exigiam subtag de região, e perdiam tag só de idioma como `'pt'`; (c) filtravam `status = 'published'`, e como o índice único deixa UMA versão publicada por template, ignoravam toda a história restaurável em `superseded`, que `RollbackTemplate` clona exigindo hash idêntico; (d) o quarto furo não foi achado por sonda nenhuma, e só apareceu quando a implementação foi ler a API do motor: a lista procurava `string.to_string`, que NÃO EXISTE no Scriban 7.2.6, e não procurava `date.parse`, `date.parse_to_string` nem `object.format`, que existem e aceitam cultura. Uma sonda confirma o que a lista procura e nunca revela o que a lista esqueceu. As consultas passaram a superestimar de propósito, porque um teto zero prova ausência enquanto um piso zero não prova nada. ENTREGUE: a invariância imposta, em commit único porque as peças só valem juntas. O conjunto real de filtros que aceitam cultura foi MEDIDO na API do motor, e não é três como diziam as consultas nem quatro como dizia o plano: são CINCO membros e SEIS argumentos, porque `date.parse_to_string` carrega cultura de entrada e de saída em posições diferentes, e um banimento que cobrisse só a de saída deixaria a de entrada aberta. Junto: `PredefinedCulturesOnly`, digest nas duas imagens base, check no momento da publicação nos dois caminhos (template e layout), e o ADR-0017. O digest congela `libicu74` 74.2-1ubuntu3.1 em Ubuntu 24.04.4, verificado por dentro da imagem, que é exatamente a ICU da linha Ubuntu da tabela de divergência. RESSALVA REGISTRADA NO ADR EM VEZ DE ESCONDIDA: com o argumento banido, o efeito medido de `PredefinedCulturesOnly` NO CAMINHO DE TEMPLATE é zero; ele continua valendo pelo resto do processo e pela deriva de versão do motor, e o ADR diz isso em vez de deixar implícito que as três peças fecham a mesma porta. Achado de ordem que mudou o desenho: no render, uma fonte com variável não declarada E cultura reprova primeiro pela variável, porque o argumento é avaliado antes da chamada, então a cultura nunca é alcançada; é essa a justificativa concreta do check de publicação, e está afirmada em teste. Portões verificados de forma independente após o commit: build com 0 erros e 0 avisos, ArchTests 9, SecurityArchTests 11, UnitTests 1648 contra base de 1603, sem nenhum teste removido. Falsificabilidade provada por mutação e revertida: tirar o slot 4 de `date.parse_to_string` da tabela deixa 2 vermelhos e 25 verdes; tirar `math.format` deixa 15 vermelhos, todos dele. FICA CONHECIDO E ABERTO POR DESENHO: o check de publicação não vê `grupo = math` seguido de `x | grupo.format`, porque a sintaxe não revela o alvo, e isso está afirmado em teste junto com a recusa do render sobre a mesma fonte, para ficar visível em vez de descrito. As mensagens ao chamador ficaram em inglês, seguindo o dialeto do módulo inteiro e porque um módulo irmão compara a string de erro por igualdade. Os quatro testes que falham sob globalização invariante continuam exatamente como estavam; medido de passagem que o raio real daquele interruptor neste host é 13 testes, não 4. |

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

## `ENG-007` · RESOLVIDO

| Campo | Valor |
|---|---|
| severidade | `LOW` |
| confiança | alta |
| arquivo | `AGENTS.md`, seção "Logging" |
| linha | primeiro item |
| tipo-de-evidência | leitura-de-código |
| introduzido-por-diff | `false` |
| revisores | dotnet-architect |
| **estado** | **RESOLVIDO** |
| nota de estado | O diagnóstico da ficha está certo e a evidência dela envelheceu. Os diretórios que ela nomeia, `Features/Mutations/` e `Features/Queries/`, NÃO EXISTEM mais: o commit `9baafdb` reorganizou em `ClassPolicies` / `Layouts` / `Templates`. Os totais também não batem (18 mais 14 fecham 32, e são 33), e as fatias de validação são três e não duas, mais a de render. Mas o conjunto que ela identifica bate EXATAMENTE: 3 de diff, 6 de leitura por chave, 2 de listagem. A recomendação escrita na própria ficha, porém, não pode ser adotada ao pé da letra: 'efeito governado ou desfecho de negócio' é prosa que só um humano decide, e o dano do achado é justamente o custo de uma regra que só um humano interpreta. MESA REDONDA convocada, e voltou `NO-CONSENSUS` restrito ao predicado de cobertura, com todo o resto em `RECOMMEND`. TRÊS MEDIÇÕES MATARAM o predicado por verbo no escopo do repositório: dos 45 endpoints, 43 aplicam o filtro genérico, e a única fatia sem logger de fatia E sem filtro é justamente a que escreve trilha; GET com efeito colateral persistente já existe hoje fora das duas nomeadas (`GetNotification` grava no Redis, `GetAttemptContent` move o contador que dispara o alarme de volume); e o repositório JÁ PAGOU por um predicado por verbo, porque o teste de segurança exige autorização e rate limiting só para não-GET e as duas rotas de divulgação, as leituras mais sensíveis do sistema, ficaram de fora e receberam rate limiting à mão. Duas medições do mediador fecharam pendências: dentro do módulo os 11 handlers GET recebem SÓ o `DbContext`, sem cache, sem recorder, sem contador, então o contraexemplo derruba o predicado no repositório e não o toca no módulo; e não existe log de acesso no host, então o kill switch manual não deixa traço NENHUM, e não traço sem ator. Mortos por convergência: a regra em prosa (indecidível), o predicado por `IAuditTrail` (explica 12 dos 31 loggers e, literalmente, EXCLUI as duas fatias que motivaram a discussão, porque elas dependem de interface distinta), e código-segue-documento (foi atrás do consumidor e não existe: a lista de alertas do desenho não tem item de leitura de catálogo e a consulta de suporte declarada é a trilha; os 11 loggers emitiriam duplicata do filtro na mesma correlação, porque todo logger existente nomeia veredito que o transporte não sabe e um GET devolve o desfecho no corpo). DESEMPATE DECIDIDO, e a pergunta era de dono e não de medição: o `AGENTS.md` de módulo deve só o dialeto, não um piso de cobertura. Motivo: a regra por verbo confinada ao módulo é verdadeira hoje POR COINCIDÊNCIA da forma atual dele e não por desenho, o contraexemplo já foi medido em dois dos quatro módulos, e ela faria da coisa mais segura a coisa que exige papelada. CUSTO ACEITO E ESCRITO, não apagado: isto fecha o achado DELETANDO a sobre-afirmação em vez de substituí-la por uma regra de cobertura correta, e depois disto nenhum documento responde 'esta fatia deve logar?'. A alternativa rejeitada ficou registrada com motivo e com gatilho de reabertura. ENTREGUE: a seção passa a falar como a dos três módulos irmãos, que já dizem só 'loggers follow the repository dialect' sem afirmação de existência, e ganha portão em `Platform.ArchTests` sobre o pareamento de forma (handler injeta `ILogger` se e somente se existe o arquivo irmão), que vale 46 de 46. O documento de padrões NÃO foi tocado, porque sob esse predicado a frase dele lê como coesão; o implementador corroborou com evidência que eu não tinha, que a mesma frase lista validação estrutural e 29 dos 46 slices não têm validador. TRÊS MUTAÇÕES, e as três ensinaram: apagar um arquivo de logger NÃO chega ao teste, porque o compilador reprova antes, então a mutação teve que ser remodelada para quebrar só o nome; o arquivo órfão COMPILA LIMPO mesmo com avisos como erro, que é exatamente por que a segunda direção precisa existir; e sob âncora podre as duas direções ficam VERDES, que é exatamente por que o detector de descoberta vazia precisa existir. Esse detector não é número literal: compara a caminhada no disco contra os tipos compilados do assembly, nas duas direções mais contagem. O implementador me corrigiu num literal (são 24 `catch` de handler no módulo, não 20; a substância sobrevive inteira) e escreveu a FORMA e não o número no registro, para não envelhecer. DIFERIDOS com a condição que destrava cada um: o portão de `catch` que envolve escrita de trilha, e o portão de cobertura do filtro de requisição, ambos bloqueados pelo kill switch e SEM isenção nomeada por decisão da mesa, porque tabela de isenção que carrega defeito conhecido ensina que o defeito é aceitável. REGISTRADO E NÃO CONSERTADO, por ser outro módulo: a administração do kill switch é controle compensatório nomeado no modelo de ameaça e está sem observabilidade nas TRÊS camadas, e ainda traduz conflito em `Result.Success`, então um conflito no controle compensatório volta como sucesso sem log, sem trilha e sem linha de requisição. Ficou no `AGENTS.md` do módulo `Notifications` com o sequenciamento. Portões verificados de forma independente: build com 0 erros e 0 avisos, ArchTests 12 contra base de 9, SecurityArchTests 11, UnitTests 1648. |

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
