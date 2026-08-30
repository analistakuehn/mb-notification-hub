# ADR-0013: Scriban como engine de templates

| | |
|---|---|
| **Status** | Proposta (com errata de 2026-08-30) |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Segurança da Informação |
| **Consultados** | Produto, Compliance |
| **Relacionadas** | ADR-0005 (templates como dados) |
| **Documento-mãe** | Design de Sistema, §4.3 "Template Management" |

## Contexto e problema

A ADR-0005 definiu templates como dados geridos pelo hub: o texto é gerido via API pelos seus donos (Produto e Compliance), validado no `validate` e no `publish` e renderizado pelo runtime com a mesma engine. Isso torna a engine de templates uma fronteira de segurança: quem escreve o template é usuário privilegiado, mas não é desenvolvedor, e o conteúdo renderiza no caminho quente do pipeline com as variáveis da notificação. Um template não pode alcançar tipos .NET (SSTI), não pode travar o worker (laço infinito, recursão descontrolada) e precisa de uma sintaxe que Produto e Compliance escrevam sem depender de engenharia.

## Fatores de decisão

- **Sandbox nativo**: o template só enxerga dados; nenhum tipo .NET exposto.
- **Limites de execução nativos**: laço (`LoopLimit`) e recursão contidos pela própria engine.
- **Sintaxe acessível a não desenvolvedores**: os donos do texto são Produto e Compliance (ADR-0005).
- **Performance**: o render roda no caminho quente do Core e precisa caber no orçamento do estágio.

## Opções consideradas

1. **Scriban** (escolhida).
2. Fluid (dialeto Liquid).
3. Handlebars.Net.
4. Razor.

## Decisão

Adotar o Scriban como engine única de templates, na validação (ADR-0005) e no runtime.

- **Sandbox**: o render recebe somente um `ScriptObject` populado com dados (variáveis da notificação e campos permitidos do contexto). Nenhum tipo .NET é exposto ao template.
- **Limites nativos**: `LoopLimit` e limite de recursão configurados na engine.
- **Timeout de parede**: não é nativo do Scriban e é imposto externamente: o render executa em task com timeout e, estourado o prazo, o resultado é descartado. Complemento na validação: limite de tamanho de template.
- **Pendência registrada**: esta ADR não fixa a versão do pacote Scriban nem os valores numéricos dos limites; ambos serão verificados e fixados na aceitação da ADR (o limite de tamanho de fonte foi fixado pela errata de 2026-08-30).

### Errata de 2026-08-30: o teto de tamanho de fonte é um número só, e ele é 131.072

O item de decisão acima registra como pendência que "esta ADR não fixa a versão do pacote Scriban nem os valores numéricos dos limites". Esta errata fecha metade dessa pendência, a do limite de tamanho de fonte, e deixa a versão do pacote e os demais limites onde estavam.

O que existia eram dois números no mesmo eixo. Os agregados de template e de layout carregavam cada um `MaxBodyLength = 512.000`, aplicado ao corpo e ao corpo em texto, enquanto o motor recusava a fonte por `MaxTemplateSizeChars`, cujo padrão é 131.072. A faixa entre os dois era morta: a escrita era aceita e a recusa vinha depois, na análise, com a fonte já gravada. O limite passa a ser um só, em `Domain/TemplateSourceSize`, lido pelos dois agregados, pelos dois validadores de autoria e pelo padrão da configuração. As duas declarações de `MaxBodyLength` foram removidas, e o compilador provou que não restou leitor.

**O número é âncora de medição e não derivação aritmética.** Três leituras independentes o sustentam. A fonte legítima mais rica já sondada tem 128 KB de HTML de marketing, com 200 interpolações e 2781 tokens, de modo que o teto passa longe do conteúdo real. Na mesma contagem de caracteres, texto puro analisa em 0,6 ms e um único encadeamento de acessos a membro analisa em 92 ms, o que mostra que o custo de uma fonte é governado pela forma dela e nunca pelo tamanho: tamanho é a alavanca errada para comprar custo de parse, e quem limita esse custo são os tetos de token. E 131.072 é o tamanho em torno do qual o módulo foi dimensionado quando essas leituras foram tomadas.

**Por que não 208.411.** Aquele é o maior valor que o orçamento de memoização admitiria, e foi recusado. Ele é resto de uma divisão entre cinco constantes da memoização, uma das quais a própria memoização declara que se move na próxima atualização do motor, e a hipótese que o produz, dois nós analisados por caractere de fonte, é irreal por fator de 25,6 contra os 0,078 nós por caractere que a forma mais densa admitida pelo teto de tokens entrega. Um teto de segurança cuja renumeração já está anunciada não é um teto.

**A amarra com a memoização passou de partida para compilação.** Antes, o único laço entre o teto da fonte e o orçamento de memoização era o limite superior do `[Range]` da configuração, verificado quando o host subia. Ele passou a ser uma asserção de tempo de compilação ao lado da própria memoização, que declara a folga entre os dois como constante sem sinal: um teto que a memoização não consegue prometer guardar não compila. A substituição é estritamente melhor, porque a falha acontece no build e não no deploy, e o modo de falha que ela impede é o que a memoização declara inaceitável, uma fonte recusada na chegada sem aviso e reanalisada em toda chamada, que se lê como renderizador lento e nunca como configuração errada.

Isso fecha, por consequência, um defeito de partida que nenhum teste cobria. Baixar `MaxResidentBytes` de 64 MiB para 32 MiB leva `MaxMemoizableSourceChars` a 104.205, abaixo do padrão de 131.072, e o host deixava de subir sem nenhuma configuração presente. A sensibilidade é estreita nos dois eixos: 48 MiB dá 156.308 e sobe, 32 MiB dá 104.205 e não sobe; `BytesPerNode` a 200 dá 166.936 e sobe, mas passa a recusar configuração antes legal, e a 255 dá 131.071 e não sobe. Com a asserção no lugar, qualquer uma dessas edições falha o build de quem a faz, em vez de falhar a partida de quem recebe o artefato.

**O intervalo da configuração ganhou piso, e o piso é medido.** O assunto é fonte analisada pelo motor, então uma configuração abaixo do assunto mais longo que uma versão pode carregar recria no eixo do assunto exatamente a faixa morta que esta errata fecha no eixo do corpo. Medido com o teto em 500 e um assunto de 700 caracteres, a escrita é aceita e a análise recusa com "The template has 700 characters and exceeds the 500 character limit", mensagem que ainda chama de template o que é o assunto. O intervalo passa a ir do teto de assunto ao teto de fonte, e o padrão passa a ser o próprio teto de fonte. Duas configurações que subiam o host antes desta errata deixam de subir: 200.000 e 500.

O teto de assunto continua constante própria e não entra no teto de fonte: 998 vem da linha de cabeçalho de e-mail do RFC 5322 e da coluna `character varying(998)` que o guarda, nunca do custo de analisar um assunto. O teto de schema de variáveis fica de fora pela razão oposta, porque o schema nunca chega ao motor.

**Limite aceito, registrado aqui e não descoberto depois.** Os validadores de autoria não leem a configuração, e fazê-los ler foi considerado e recusado. A consequência é que um operador que aperte `MaxTemplateSizeChars` abaixo do teto de fonte faz o autor receber `200` na escrita de um corpo entre os dois números e a recusa no `validate`, com o número apertado, que é o correto de reportar. A premissa da recusa é que o padrão entregue seja o próprio teto de fonte, e ela é fixada por teste, junto com uma varredura dos arquivos de configuração que os dois hosts entregam. No dia em que qualquer um dos dois ficar vermelho, o que se reabre é a recusa.

### Errata de 2026-08-30: o sandbox tem estado compartilhado entre renders, e ele passa a ser selado

A decisão acima descreve o sandbox por aquilo que entra em cada render, "o
render recebe somente um `ScriptObject` populado com dados", e não diz nada
sobre o que **sobra** de um render para o próximo. Essa omissão não era neutra:
existia estado compartilhado por todo o processo, e ele era gravável a partir do
texto de um template publicado. Esta errata registra o defeito e fecha a
omissão. A decisão de adotar o Scriban não muda.

**O que estava aberto.** A superfície de builtins que o sandbox expõe é
construída uma vez, guardada em campo estático e passada a todo contexto de
execução. O construtor do motor a empurra para o fundo da pilha de globais, e o
reset que roda ao fim de cada render a preserva por desenho. As funções dessa
superfície já eram protegidas pelo próprio motor, que recusa `{{ math.abs = 0 }}`
por membro somente leitura. Os membros de dados e os membros novos não eram
protegidos por ninguém. Medido, com a saída literal:

```text
plantar object.vazado : ''                 (saída vazia, invisível)
ler     object.vazado : '111.222.333-44'
data antes            : '30 Aug 2026'
set date.format       : ''
data depois           : '2026/08/30'
```

Em texto: um template publicado de uma aplicação grava um valor do destinatário
num objeto estático do processo, e um template de **outra** aplicação o lê. E
sobrescrever o formato de data padrão move toda data implícita de todo render
seguinte do processo, até reiniciar.

**Por que a revisão humana não tinha sinal.** A coleta de variáveis usadas só
registra gravação quando o alvo é variável simples, e o alvo aqui é expressão de
membro. O que resta é o nome do grupo, `object`, que a mesma coleta remove por
ser builtin. O relatório de publicação de uma versão dessas sai limpo.

**O remédio, e a superfície que ele cobre.** `IsReadOnly` na raiz e em cada grupo
aninhado, aplicado como último passo da construção da superfície, depois das
remoções de membro que ela já faz, porque uma superfície selada recusaria as
próprias remoções. A superfície foi inventariada contra o Scriban 7.2.6 e o selo a
cobre inteira nesta versão: profundidade 2, oito grupos (`array`, `date`,
`html`, `math`, `object`, `regex`, `string`, `timespan`), cinco membros de dados
(`blank`, `empty`, `date.default_format`, `date.format`, `timespan.zero`) e 125
funções. Não existe terceiro nível. Com o selo, as duas gravações acima passam a
ser recusadas e a data depois volta a ser `30 Aug 2026`.

**Selar a raiz é redundante hoje, e fica.** Medido: com os oito grupos selados e
a raiz aberta, nenhum dos dois vazamentos reaparece, porque o motor já recusa por
conta própria a escrita que resolve na raiz e porque todo render empurra globais
próprios acima dela. Quem fecha o defeito são os oito selos de grupo, e isso foi
provado removendo o selo de um grupo por vez. A raiz continua selada por dois
motivos: a superfície passa a carregar uma regra só em vez de duas, e uma versão
do motor que pare de sombrear a raiz reabriria o buraco sem sinal nenhum.

**Custo de regressão medido em zero.** Nove construções legítimas foram
comparadas antes e depois contra o mesmo binário de produção e a saída é idêntica
byte a byte: `string.upcase`, `math.format` sem cultura, laço `for`,
`date.to_string` com padrão e sem cultura, encadeamento `array.sort` com
`array.join`, atribuição a variável local, `capture`, `func`, e variável local
não vazando de um render para o outro. Um corpus mais largo, de 70 expressões
cobrindo os oito grupos e os filtros com argumento de cultura, também não move
nenhum caractere.

**A pendência de versão do pacote ganha um segundo gatilho.** A decisão acima
registra que esta ADR não fixa a versão do pacote Scriban. O selo percorre um
nível abaixo da raiz, que é toda a superfície deste motor, então uma versão que
aninhe um terceiro nível, ou que traga um nono grupo, deixaria o que ela trouxe
gravável e compartilhado, e todo teste de vazamento continuaria verde por nomear
membro que o selo já alcança. O inventário acima passa a ser afirmado por teste,
que é o que fica vermelho nesse dia.

**Consequência de implantação, registrada e não descoberta depois.** Uma versão
publicada que hoje escreva num membro de builtin renderiza com sucesso e vaza;
com o selo ela passa a falhar o render. A troca é correta, porque recusa é
melhor que vazamento silencioso, e não é invisível: o censo das versões
publicadas que contêm essa gravação é leitura de banco por ambiente, ela não
tinha sido feita quando esta errata foi escrita, e um resultado maior que zero
não é lista de trabalho, é incidente.

### Consequências

**Positivas**
- SSTI mitigada por construção: sem tipos .NET no escopo do template, a classe de ataque perde a superfície principal.
- Template patológico (laço infinito, recursão profunda) é contido pela própria engine, sem infraestrutura adicional.
- Sintaxe de chaves duplas com condicionais e filtros, legível para Produto e Compliance.

**Negativas**
- **Lock-in de sintaxe nos templates governados**: todo o catálogo fica escrito em Scriban; trocar de engine exigiria migrar e reaprovar cada versão publicada (a aprovação é sobre `content_hash`, ADR-0005), custo que cresce com o catálogo.
- **Timeout de parede não nativo**: o controle de tempo total é responsabilidade do hub (render em task com timeout e descarte do resultado). O descarte não interrompe a execução em andamento; quem garante terminação são os limites nativos e o limite de tamanho de template.

## Prós e contras das opções

### Opção 1: Scriban
- Prós: sandbox por exposição explícita de dados via `ScriptObject`; `LoopLimit` e limite de recursão nativos; sintaxe simples para não desenvolvedores; desempenho compatível com o caminho quente, a confirmar no teste de carga (critério abaixo).
- Contras: timeout de parede externo; lock-in de sintaxe.

### Opção 2: Fluid (dialeto Liquid)
- Prós: dialeto Liquid difundido (autores vindos de plataformas de e-commerce o reconhecem); modelo de acesso restrito por registro explícito de tipos.
- Contras: linguagem deliberadamente mais limitada; transformações além do conjunto Liquid tendem a virar filtro registrado em código, deslocando apresentação para deploy, exatamente o que a ADR-0005 quer evitar.

### Opção 3: Handlebars.Net
- Prós: sintaxe mustache amplamente conhecida; o estilo logic-less reduz o que um autor pode errar.
- Contras: logic-less empurra condicionais e formatações para helpers registrados em código (mesmo problema da opção 2, agravado); controles equivalentes a limite de laço e de recursão teriam de ser construídos fora da engine.

### Opção 4: Razor
- Prós: expressividade máxima; familiar a qualquer desenvolvedor .NET.
- Contras: o template é C# com acesso ao runtime .NET, SSTI por construção quando o autor não é tecnicamente confiável; sandbox exigiria isolamento de processo; sintaxe hostil a não desenvolvedores; compilação por template. Eliminada pelos dois primeiros fatores de decisão.

## Como saberemos que foi a decisão certa

- O teste de SSTI do catálogo de segurança passa: payloads de template maliciosos não alcançam tipos .NET nem derrubam o worker (laço e recursão interrompidos pelos limites da engine).
- p95 de render dentro do orçamento do estágio, medido no teste de carga.
- Autores publicam template novo sem deploy e sem intervenção de Engenharia (proxy do fator sintaxe, observado na retrospectiva de fase).

## Referências

- Design de Sistema, §4.3 "Template Management".
- ADR-0005 (templates, layouts e políticas como dados geridos pelo hub).
