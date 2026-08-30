# ADR-0017: Formatação de saída invariante e imposta

| | |
|---|---|
| **Status** | Aceita |
| **Data** | 2026-08-30 |
| **Decisores** | Arquitetura, Segurança da Informação, Engenharia |
| **Consultados** | Produto, Compliance |
| **Relacionadas** | ADR-0013 (Scriban como engine de templates), ADR-0005 (templates como dados) |
| **Documento-mãe** | Design de Sistema, §4.3 "Template Management" |

## Contexto e problema

A ADR-0013 adotou o Scriban e descreveu o sandbox pelo que entra em cada render.
Ela não disse nada sobre a cultura sob a qual o texto sai, e essa omissão
carregava uma premissa que nunca foi medida: a de que a formatação é invariante,
e portanto o mesmo template com os mesmos dados produz o mesmo texto e o mesmo
`content_hash` em qualquer host.

A premissa é falsa, e foi medida nas duas pontas do projeto no mesmo commit. O
filtro de formatação aceita um argumento de cultura, o autor do template escreve
esse argumento sem precisar de uma linha de código nova, e o resultado depende da
biblioteca ICU do host que renderizou:

| Host | ICU | Saída de `1234567.5` sob `en-ZA` | SHA-256 do UTF-8 |
|---|---|---|---|
| Windows 11 | `icu.dll` 72.1.0.4 | `1 234 567,50` | `cf3a9964…` |
| Ubuntu 24.04 | `libicu74` 74.2-1ubuntu3.1 | `1,234,567.50` | `29870715…` |

O separador de milhar do lado Windows é espaço inquebrável (U+00A0) e não espaço
comum, detalhe que decide se uma remedição reproduz ou não.

Pior que a divergência entre hosts é o caso da cultura desconhecida. A validação
do módulo aceita um `Locale` pela forma BCP 47 e nunca por lista de culturas
conhecidas, então um tag bem formado que ninguém definiu chega ao runtime. Medido
neste host, `qq-QQ` não resolve para a cultura invariante: ele resolve para uma
cultura fabricada com LCID 4096 que segue o locale do sistema operacional, e
`1234567.5` sob esse tag sai como `1.234.567,50`, que é o locale da máquina e não
o invariante que qualquer leitor supõe. Nada em lugar nenhum relata que isso
aconteceu.

Não há, portanto, propriedade a preservar. Há propriedade a construir.

## Fatores de decisão

- **Determinismo de saída**: o mesmo template com os mesmos dados tem que produzir
  o mesmo texto em qualquer host, porque o `content_hash` e a evidência de
  auditoria são construídos sobre esse texto.
- **Recusar em vez de ignorar**: um render que descartasse a cultura em silêncio
  responderia um pedido do autor com outra coisa, em texto que nenhum passo
  seguinte compara com nada.
- **O autor descobre cedo**: quem escreve o template não é desenvolvedor e não lê
  log de worker; o lugar onde ele encontra o erro é a publicação.
- **A composição de acentos não pode ser sacrificada**: ela é o primeiro passo da
  política de saída e o hash descreve o texto depois dela.

## Opções consideradas

1. **Banir o argumento de cultura, fixar as culturas predefinidas e fixar a
   imagem base por digest** (escolhida).
2. `InvariantGlobalization=true` no runtime.
3. Documentar o argumento de cultura no guia do produtor e deixar o autor
   escolher.
4. Normalizar a saída depois do render, convertendo o texto formatado.

## Decisão

A formatação de saída passa a ser **invariante e imposta**, por três peças que só
valem juntas. Uma peça sozinha muda o texto que sai sem fechar a propriedade que
as três fecham, e por isso elas entram no mesmo commit.

### Peça 1: o argumento de cultura é banido, e a recusa é classificada

O sandbox passa a embrulhar cada membro de builtin que aceita cultura. Um
template que passa cultura falha o render sob um modo de recusa próprio,
`CultureArgument`, e não sob o modo residual que não nomeia limite nenhum.

O conjunto de membros foi **medido** contra o Scriban 7.2.6 fixado, percorrendo o
objeto de builtins inteiro pela informação de parâmetro que o próprio motor
publica, e não herdado de documento nenhum. São cinco membros e seis argumentos:

| Membro | Argumento | Posição |
|---|---|---|
| `date.parse` | `culture` | 2 |
| `date.parse_to_string` | `output_culture` | 2 |
| `date.parse_to_string` | `input_culture` | 4 |
| `date.to_string` | `culture` | 2 |
| `math.format` | `culture` | 2 |
| `object.format` | `culture` | 2 |

Duas correções que essa medição obrigou, e que ficam registradas porque quem
vier depois vai reencontrar as duas afirmações antigas. Primeira: `string.to_string`
**não existe** neste motor, e as consultas de censo que o nomeavam procuravam um
filtro que nunca esteve lá. Segunda: os membros não são quatro, são cinco, e
`date.parse_to_string` carrega dois argumentos de cultura, um para a cultura com
que lê o texto e outro para a cultura com que escreve o resultado; um banimento
que cobrisse só o de saída deixaria o de entrada aberto.

O embrulho fica entre o motor e o builtin, e vê os argumentos depois que o motor
os ligou. É por isso que ele é completo: medido contra o motor fixado, o
encadeamento com barra, a chamada posicional, a chamada com parênteses, o
argumento nomeado, a cultura que chega por variável, o indexador com literal e o
apelido do grupo por variável chegam todos ao mesmo ponto, como um único vetor
posicional com a cultura no lugar declarado. Uma checagem escrita sobre o texto
da fonte, ou sobre a árvore sintática, fecha as primeiras formas e não fecha as
últimas.

O argumento omitido e o argumento escrito como `null` chegam iguais e significam
a mesma coisa, então nenhum dos dois é recusado. A cadeia vazia é recusada como
qualquer outra: ela seleciona a cultura invariante, que é a que o sistema usaria
de todo jeito, mas o que está banido é o autor decidir a cultura, e não uma
cultura específica vencer.

### Peça 2: `PredefinedCulturesOnly=true`

Vai em `Directory.Build.props`, portanto vale para todos os projetos. Medido: com
a propriedade ausente, `CultureInfo.GetCultureInfo("qq-QQ")` devolve uma cultura
fabricada com LCID 4096 que formata sob o locale do sistema operacional; com ela,
a mesma chamada lança `CultureNotFoundException`. As culturas reais continuam
resolvendo e continuam vindo da ICU: `pt-BR` resolve, e o separador decimal dela
continua sendo vírgula.

Com a peça 1 no lugar, nenhum template alcança essa fabricação, e é honesto dizer
que o efeito medido desta peça **no caminho de template é zero**. Ela fica por
dois motivos que não são o caminho de template. O primeiro é o resto do processo:
qualquer código que resolva cultura por nome passa a lançar em vez de inventar. O
segundo é a deriva de versão do motor: se uma versão nova trouxer um sexto membro
com argumento de cultura, o banimento não o conhece, e esta peça converte pelo
menos o tag desconhecido em exceção em vez de saída dependente do host.

### Peça 3: a imagem base é fixada por digest

`src/Platform.Api/Dockerfile` e `src/Platform.Worker/Dockerfile` deixam de
apontar para a tag e passam a apontar para o digest:

- `mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94`
- `mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c`

Verificado por dentro da imagem de runtime fixada: Ubuntu 24.04.4 LTS com
`libicu74` na versão `74.2-1ubuntu3.1`, que é exatamente a ICU da linha Ubuntu da
tabela de divergência acima. O digest congela a ICU que produziu aquela saída
medida. Mover qualquer uma das duas linhas é decisão sobre saída e não tarefa de
manutenção: remeça a tabela antes de mudar, e registre o digest novo junto com a
versão de ICU que ele carrega.

### Peça 4: o autor descobre na publicação

O catálogo de validação ganha o check `output-culture`, que reprova a versão
nomeando o campo. Ele roda nos **dois** caminhos, template e layout, porque um
layout renderiza no mesmo motor e é recusado pelo mesmo banimento; um layout
deixado de fora publicaria limpo e quebraria todo template que o fixa.

O check lê a árvore sintática, então ele é deliberadamente mais fraco que o
banimento: ele resolve a chamada cujo alvo a sintaxe nomeia, o que cobre a grafia
`grupo.membro` e o indexador com literal, e não segue um grupo que chegou por
variável. O que ele compra com essa fraqueza é o momento. As duas metades dizem a
**mesma frase**, escrita uma vez só, porque um autor que encontra o erro na
publicação e um autor que o encontra no render precisam ser informados da mesma
coisa com as mesmas palavras.

## `InvariantGlobalization` está proibido

Ele parece resolver o mesmo problema por um interruptor e não resolve. Sob
globalização invariante a composição de acentos vira no-op silencioso, a consulta
de normalização passa a mentir, e o primeiro passo da política de saída passa a
operar sobre um texto que ele acredita ter normalizado. É trocar um defeito
visível por um invisível, exatamente no passo cujo resultado o hash descreve.

Os testes que já falham sob essa configuração **ficam falhando**. Eles são o
único oráculo que este repositório tem, de graça, para a dependência de ICU do
caminho de saída, e deixá-los verdes apagaria esse sinal. Os quatro do caminho de
saída são:

- `SmsContentNormalizerTests.A_decomposed_accent_becomes_the_composed_form`
- `RenderedOutputPolicyTests.Composition_shortens_the_text_before_the_ceiling_measures_it`
- `RenderedOutputPolicyTests.Composition_expands_the_text_before_the_ceiling_measures_it`
- `RenderedOutputPolicyTests.The_hash_describes_the_normalized_text_and_not_the_untouched_render`

Medido nesta correção, o raio é maior que quatro: ligar `InvariantGlobalization`
neste host reprova treze testes, sendo os quatro acima, cinco de `QuietHoursRule`
que dependem de resolução de fuso horário, dois de projeção de evidência de
política, e dois novos que afirmam a configuração de propósito. Os quatro acima
continuam sendo os que falam da saída; o resto é a medida de quanto mais o
interruptor levaria junto.

A proibição deixa de depender de quem leu este documento: `RuntimeGlobalizationTests`
afirma em código que o modo invariante está desligado, e afirma junto a
consequência observável, para que a asserção sobre o interruptor não seja a única
coisa entre o repositório e ele.

## Consequências

**Positivas**

- O texto que sai de um template deixa de depender da ICU do host, e o
  `content_hash` volta a poder ser comparado entre hosts.
- Uma cultura desconhecida deixa de virar saída silenciosa e vira exceção.
- O autor descobre o problema publicando, com o campo nomeado, e não quando a
  mensagem sai.
- A superfície de cultura do motor passa a ser afirmada por teste, então uma
  versão nova do Scriban que traga um sexto membro fica vermelha em vez de abrir
  a porta em silêncio.

**Negativas**

- **Uma versão publicada que passe cultura passa a falhar o render.** A troca é
  correta, porque a alternativa é texto dependente do host, mas ela não é
  invisível. O censo por ambiente foi feito em 2026-08-30 e devolveu zero, então
  o raio deste passo irreversível é zero hoje; num ambiente com catálogo vivo,
  esse censo é pré-requisito da implantação e não formalidade.
- **Perda de recurso para o autor**: quem quisesse formatar um número no padrão
  de um país específico não tem mais como. Se essa necessidade aparecer, ela
  volta como decisão de produto sobre um conjunto fechado de culturas, resolvido
  por dado da notificação e nunca por argumento no texto do template.
- **A imagem base deixa de ganhar correção de segurança sozinha.** O digest é
  fixo, então subir a base vira passo explícito. É o custo direto de congelar a
  ICU, e a contrapartida é que a atualização passa a ser observável.

## Prós e contras das opções

### Opção 1: banir, fixar culturas e fixar digest (escolhida)

- Bom: fecha a decisão de cultura no lugar onde ela é tomada, recusa em vez de
  ignorar, e congela a biblioteca que decide o resultado.
- Ruim: são três mudanças em três camadas diferentes, e nenhuma delas sozinha
  entrega a propriedade.

### Opção 2: `InvariantGlobalization=true`

- Bom: um interruptor, sem código.
- Ruim: quebra a composição de acentos em silêncio no primeiro passo da política
  de saída, e apaga o oráculo de ICU do repositório. Recusada.

### Opção 3: documentar e deixar o autor escolher

- Bom: custo zero, e o recurso continua disponível.
- Ruim: mantém o texto dependente do host e o hash não comparável, e transfere
  para o autor uma decisão cuja consequência ele não tem como observar. Recusada.

### Opção 4: normalizar depois do render

- Bom: não mexe no sandbox.
- Ruim: converter um número já formatado de volta exige adivinhar sob qual
  cultura ele foi escrito, o que é a mesma ambiguidade um passo adiante, e não
  ajuda em nada no caso da data por extenso. Recusada.

## Como saberemos que foi a decisão certa

- Nenhuma versão publicada é recusada por `output-culture` depois da primeira
  semana, o que indica que o check informa antes de a publicação acontecer.
- O `content_hash` de uma mesma versão renderizada nos dois hosts do projeto
  coincide.
- A sentinela de superfície de cultura fica vermelha na próxima atualização do
  Scriban que mexa nesses membros, em vez de a porta reabrir em silêncio.

## Referências

- ADR-0013, e a errata de 2026-08-30 que selou o objeto de builtins compartilhado.
- `src/Platform.Api/Modules/TemplateManagement/AGENTS.md`, seção "Formatação de saída".
- Scriban 7.2.6, `Scriban.Functions.DateTimeFunctions`, `MathFunctions` e `ObjectFunctions`.
