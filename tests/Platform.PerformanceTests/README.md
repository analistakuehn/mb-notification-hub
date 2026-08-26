---
language: pt-BR
---

# Sonda de contenção da cadeia de auditoria

Instrumento de medição, não suíte de teste. Ele existe para responder uma
pergunta que nenhum teste da suíte responde: quanto o lock consultivo por
partição da cadeia de auditoria custa por append, como esse custo cresce com o
tamanho da partição, e se as mitigações baratas bastam antes de mudar a forma
da cadeia.

A sonda é .NET puro, sem ferramenta de carga de terceiros. A razão é o
experimento: isolar a contenção exige controlar a partição de destino de cada
append, escrevendo com `occurred_at` em meses distintos, e nenhum driver HTTP
consegue isso porque a API carimba o instante. A medição precisa ser
in-process contra o PostgreSQL real.

## Comando

Rodada completa contra um contêiner descartável:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode full
```

Só a reivindicação do outbox, sem nada da trilha:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode relay
```

Os dois orçamentos do caminho de entrega, que o desenho da fase 2 declara e que
nenhum teste media:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode delivery
```

Ele responde duas perguntas. A primeira é quanto uma rodada do scheduler leva
para achar as tentativas com prazo vencido, sobre uma tabela do tamanho pedido e
com as vencidas como minoria rara: é o único termo do prazo até o SMS de
fallback que cresce com a retenção, porque os saltos de fila e a chamada ao
provedor são orçamento fixo. A segunda é quanto custa ingerir um callback de
provedor em cada tamanho de lote, nas duas formas de transação, uma por evento e
uma por lote, que é a comparação de que a decisão de mudar a forma depende.

O modo `delivery` roda apenas contra o contêiner descartável, e nenhuma flag abre
essa porta. Os outros modos escrevem linhas que ficam paradas; este escreve
linhas no outbox endereçadas ao rastreador de entrega, e linha de outbox não é
inerte: um relay apontado para aquele banco publica. A falha não seria tabela
suja, seria evento de entrega sintético entrando num hub real.

O que fica de fora nas duas: TLS, verificação de assinatura, pipeline HTTP e os
saltos de fila. Nenhum deles cresce com o volume nem com o lote, e medi-los exige
host e cliente, que é o gate de carga contra ambiente real.

A memoização de leitura publicada sob falta concorrente, com o orçamento dela
cheio:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode memoization
```

O modo `memoization` não toca banco nenhum e não sobe contêiner: é a única
rodada que roda em qualquer máquina. Ele responde duas perguntas que só existem
com o orçamento cheio, que é o único estado em que a política de evicção roda.
A primeira é quanto custa uma operação no caminho de falta quando o processo
inteiro pede ao mesmo tempo, com a disputa de lock que essa operação compra. A
segunda é se o residente passa do teto declarado sob escrita concorrente, e essa
não tem tolerância nem referência: o teto é a promessa da própria política, e um
residente acima dele é memória que o processo nunca devolve, com todo teste em
processo passando enquanto ela cresce.

O espaço de chaves fica logo acima do teto de propósito, para que quase toda
operação erre, escreva e encontre o portão de admissão. O braço `M1` mede vazão
e disputa sem ninguém olhando; o braço `M2` repete a carga com um observador do
residente, separado porque os locks do observador entrariam na contagem de
disputa do primeiro. Uma passagem descartada antecede os dois.

O custo de uma forma renderizada, que é a unidade que o renderizador publicado
monta por notificação:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode render
```

O modo `render` também não toca banco. Uma forma são cinco renders: assunto,
corpo e variante em texto sobre o payload do chamador, mais o layout fixado em
volta dos dois corpos. O braço `F1` renderiza a forma no contexto que os campos
compartilham, e o braço `F0` renderiza a mesma forma com um contexto por render,
que é como o preview de uma versão continua renderizando. O segundo braço não é
enfeite: ele é a referência medida na mesma rodada, então a comparação entre os
dois não depende de máquina nenhuma.

O portão lê bytes por forma, e só. Bytes por operação são determinísticos para um
runtime dado, o que é o que torna honesta uma referência versionada; o tempo por
forma é reportado e não entra em portão nenhum, porque ele pertence ao host que
mediu. A segunda checagem exige que a forma compartilhada custe uma fração da
forma com um contexto por render, e é ela que reprova, sozinha, se o
compartilhamento for desfeito.

O que uma busca custa na memoização de parse, com o catálogo publicado já
inteiro na memória:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode parse
```

O modo `parse` também não toca banco. O braço `S1` mantém 205 formas quentes,
cinco fontes cada, e as lê com uma thread por núcleo. Ele não tem cauda fria de
propósito: toda fonte que ele pede foi parseada na passagem descartada, então o
que sobra para medir é o custo da busca em si e se o catálogo continua lá no
fim. A segunda resposta é a que se move com a política de evicção. Uma política
que limita a memoização contando entradas derruba um catálogo desse tamanho
enquanto ele está sendo lido, e o braço passa a pagar parse por fonte que já
tinha parseado, de novo e de novo.

Duas das quatro checagens não têm referência nem tolerância. A primeira é
reparse: o braço oferece bem menos do que o orçamento cabe, então uma fonte
parseada durante ele é uma fonte que a política jogou fora enquanto era lida. A
segunda é o catálogo no fim da rodada, caractere a caractere. Zero não é limiar
a calibrar nem número a afrouxar: o custo de errar aqui se mede em passagens
inteiras, nunca numa busca ou duas.

A referência é a mediana de três passagens, e não de uma. A dispersão entre duas
passagens honestas deste braço chega a um quarto do valor, que é metade da
tolerância, então uma referência de passagem única mede a sorte daquela
passagem. O reparse foge da mediana de propósito e é somado sobre as três: uma
passagem que teve de parsear uma fonte que já tinha é falha, tenha ou não sido a
do meio.

Rodada de guarda por pull request, comparada contra a linha de base versionada:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode smoke
```

O modo `smoke` termina com código de saída 1 quando uma métrica de guarda
regride além da tolerância, e 0 quando passa. É esse código que um pipeline lê.

Regravar a linha de base, depois de uma mudança deliberada de forma do append:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode smoke \
  --update-baseline --baseline tests/Platform.PerformanceTests/baselines/audit-chain-contention.json
```

Contra um banco existente, por exemplo pré-produção:

```bash
dotnet run --project tests/Platform.PerformanceTests -c Release -- --mode full \
  --connection-string "Host=...;Database=...;Username=...;Password=..." --allow-trail-writes
```

`--allow-trail-writes` é obrigatório fora do contêiner descartável e não tem
default. A sonda grava na trilha, a trilha é append-only por construção, e nada
do que ela escrever poderá ser apagado depois.

## Opções

| Opção | Default | Efeito |
|---|---|---|
| `--mode` | `full` | `full` roda o desenho inteiro; `smoke` roda a rodada de guarda; `relay` roda só a reivindicação do outbox; `delivery` roda os dois orçamentos do caminho de entrega; `memoization` roda a memoização de leitura publicada, sem banco; `render` roda o custo de uma forma renderizada, sem banco; `parse` roda a memoização de parse com o catálogo quente, sem banco |
| `--connection-string` | contêiner | Aponta para um banco existente |
| `--allow-trail-writes` | desligado | Autoriza escrita na trilha de um banco informado |
| `--appenders` | 4 | Appenders concorrentes por braço |
| `--volumes` | `10000,500000,2000000` | Linhas pré-carregadas na partição corrente |
| `--arms` | `A1,A2,A3,A4,A5` | Braços a rodar |
| `--arm-seconds` | 20 (smoke: 5) | Orçamento de tempo por braço |
| `--max-appends` | 4000 | Orçamento de transações por braço |
| `--sustained-rate` | 900 | Taxa oferecida da célula de malha aberta |
| `--relay-backlog` | 1000000 | Linhas pendentes no outbox para o plano do relay |
| `--delivery-volumes` | `50000,500000` | Notificações semeadas antes de medir a rodada do scheduler |
| `--callback-batches` | `1,10,50,200,500` | Tamanhos de lote em que o custo do callback é medido |
| `--delivery-repeats` | 30 | Callbacks por célula e rodadas por statement |
| `--purge-backlog` | 1000000 | Marcas de dedupe para o braço de interferência |
| `--tolerance` | 0,55 | Regressão relativa tolerada na métrica normalizada |
| `--volume-drift` | 2,00 | Crescimento tolerado da posse entre os dois volumes de guarda (teto 3,0) |
| `--gate-arm` | `A5` | Braço que o portão lê |
| `--guard-repeats` | 3 | Rodadas do braço de guarda antes de tomar a mediana |
| `--memoization-workers` | núcleos | Threads que leem uma memoização em processo ao mesmo tempo |
| `--render-forms` | 2000 | Formas que cada braço do modo `render` mede |
| `--parse-forms` | 205 | Formas que o modo `parse` mantém quentes, cinco fontes cada |
| `--report` | ausente | Caminho do relatório em JSON |

## O desenho

Cinco braços, sempre na mesma taxa, no mesmo banco e no mesmo pool, variando
uma dimensão de cada vez:

- **A1, controle**: appenders em partições mensais distintas. O lock nunca
  disputa. Dá o custo de banco por append. As partições de controle são
  carregadas no mesmo volume da corrente, senão o delta contra A2 misturaria
  contenção com custo de varredura.
- **A2, tratamento**: os mesmos appenders na partição corrente. Serialização
  plena.
- **A3, mistura real**: o perfil de append por operação medido do código, três
  appends por notificação mais as duas operações que apendem fora da vida de
  uma notificação, na forma de quatro round trips que o appender tinha antes da
  correção. Ele fica no desenho como a régua do que a correção comprou.
- **A4, superfície de auditoria**: A3 mais o fluxo de reconstrução, que é o
  pior caso do lote: dois elos numa transação sem nenhum trabalho de negócio
  para amortizar a posse.
- **A5, forma de produção**: A3 com o colapso de round trips que se provou
  seguro, que é de quatro para três: lock e `nextval` juntos, leitura do elo
  anterior em statement próprio, insert. Dobrar a leitura do elo anterior para
  dentro do statement do lock bifurca a cadeia, e isso é medido, não teórico. O
  índice de cauda parcial em `(seq DESC)` deixou de ser exclusividade deste
  braço: ele está no schema, criado na partição-mãe pela migração, e portanto
  todos os braços da rodada o encontram.

Três sinais precisam se corroborar antes de atribuir um delta ao lock: o delta
A1 contra A2 na mesma taxa e no mesmo volume, a instrumentação separando espera
de posse, e a amostragem de `pg_stat_activity` por tipo de evento de espera.

## O que a sonda mede por append

Cada append é dividido em quatro fases: setup (conexão, transação e statements
de negócio), espera pelo lock, trabalho pré-commit sob o lock, e commit. A
posse é a soma das duas últimas, e é ela que define o teto da partição, porque
`pg_advisory_xact_lock` segura até o COMMIT terminar.

O relatório publica uma tabela de sensibilidade do teto por partição para
latências de commit de 0,5, 1, 2 e 4 ms. O commit é o termo que o Docker local
não reproduz: em RDS Multi-AZ ele inclui replicação síncrona ao standby. A
tabela mantém o trabalho pré-commit medido e substitui apenas o commit, o que
torna a leitura decidível com um único número de infraestrutura pendente.

## Dois portões, papéis distintos

**Por pull request**, o modo `smoke` roda o braço mitigado em dois volumes e
mede, na mesma rodada, o custo de uma ida trivial ao banco. Reprova por três
sinais:

1. **Posse normalizada**: posse p50 dividida pela ida trivial, com 55 % de
   tolerância contra a linha de base. Diz quantas idas ao banco o append segura
   o lock, que é a forma do append e não a velocidade da máquina.
2. **Crescimento com o volume**: posse no volume maior sobre a posse no volume
   menor, com teto de 3,0. É razão dentro da rodada, imune ao host, e é o
   sinal que pega a regressão que de fato importa: índice de cauda derrubado,
   invalidado ou deixado de fora por migração futura.
3. **Guarda absoluta frouxa**, uma ordem de grandeza acima da base, só para o
   caso de a normalização se comportar mal.

Nenhuma métrica absoluta apertada entra aqui de propósito. A versão anterior do
portão media posse absoluta e a mediana do mesmo código variou 30 % entre
rodadas na mesma máquina, que era a própria tolerância: o portão media o host.
Afrouxar para 40 % compraria silêncio, não sinal. Com a normalização medida sob
a mesma concorrência dos braços e amostrada nas duas pontas da rodada, a
dispersão entre rodadas caiu de 12,4 % para 4,4 %.

**Onde mora um limiar.** Na região vazia entre a distribuição saudável medida e
a distribuição de falha medida, nunca na borda do ruído. Limiar derivado só do
ruído fabrica reprovação sem regressão e acaba silenciado. Foi o que aconteceu
com o teto de crescimento em 1,25: duas rodadas do mesmo código e do mesmo
schema deram 0,917 e 1,272. O saudável chega perto de 1,33, a assinatura de
falha com índice ausente é da ordem de 55 vezes, e entre 1,3 e 55 há duas
décadas vazias; por isso o teto passou a 3,0. A tolerância da posse normalizada
segue a mesma regra e deixou de ser palpite: dispersão medida de uma razão
desta bancada, cerca de 27 %, vezes dois.

Antes de mexer em limiar, mexa nas duas alavancas que valem mais. **Separação
de volume**: manter o braço baixo pequeno e subir o alto até onde o tempo de
execução permitir, porque a razão saudável fica perto de 1 qualquer que seja a
separação e a razão quebrada cresce com ela. **Amostragem**: mais transações
por braço e mediana de três passagens, porque meio milissegundo de jitter sobre
dois milissegundos de posse é ruído de host, e ruído de host cede a amostragem.

O divisor precisa ser medido como os braços são medidos. Numa conexão só, em
laço apertado, ele reporta a sorte de escalonamento daquele instante: medido
assim, variou 49 % entre duas rodadas cujas posses variaram 4 %, e dividir
piorou a métrica em vez de melhorar.

A linha de base é gravada como **mediana de três rodadas**. Referência tomada
de uma rodada azarada deixa a próxima rodada honesta sem margem nenhuma.

**Pré-produção sob demanda**: metas absolutas, a mesma sonda mais a campanha de
carga com provedores substituídos por fake. É o portão de go-live, e não roda
por pull request.

### Quem é o dono do índice durante o braço mitigado

O relatório declara a origem do índice de cauda. A sonda verifica pelo plano de
execução se o schema já responde à consulta de cauda sem varredura sequencial;
se responde, ela não cria índice nenhum e o sinal de crescimento com o volume
passa a vigiar o índice de produção. Enquanto o schema não tiver o índice, a
sonda cria o dela durante o braço, e nessa condição o sinal vigia o índice da
própria sonda, não o de produção. Isso está escrito no relatório de cada
rodada porque muda o que o portão consegue detectar.

Desde que a migração do índice entrou, a detecção resolve para "índice já
presente no schema" e o portão vigia produção. A frase continua condicional de
propósito: ela é verificação de plano, não afirmação de estado, e uma migração
futura que derrube ou invalide o índice reaparece no relatório como origem
trocada antes de reaparecer como regressão.

### Planos dos caminhos que percorrem a partição por seq

O índice de cauda é parcial, com predicado `hash IS NOT NULL`, e um índice
parcial só atende statement que carregue o predicado dele. A rodada completa
lê o plano dos três statements que a trilha executa por `seq`, a cauda dentro
do lock, a faixa que verificação e export compartilham e o maior `seq` da
partição, e publica execução, buffers e se o plano varre a partição. Quem se
beneficia do índice passa a ser leitura de plano por rodada, não afirmação
herdada de documento.

### Reivindicação do outbox por banda

O modo `relay` semeia um backlog de linhas pendentes na mistura que os
produtores escrevem, com a banda de autenticação rara de propósito, e mede cada
banda em quatro braços: o índice como o schema o deixa, o mesmo índice
derrubado, uma ordem de colunas alternativa e uma coluna comum escrita em vez
da coluna gerada. Publica, por braço e por banda, o plano, quantas linhas o
filtro descarta para encher um lote, se varre a tabela, se ordena em disco, e o
tempo por lote de cem.

Duas escolhas de método valem mais que os números. O tempo por lote é medido
com reivindicação, carimbo e commit, como o relay faz, e não com transação
descartada: uma medição que sempre relê a mesma cabeça do backlog em cache não
diz nada sobre um relay que avança. E o braço que derruba o índice existe para
que a leitura boa não seja afirmação sem contraprova; é a mesma razão pela qual
o teste de plano da suíte de integração afirma os dois sentidos.

O braço da coluna comum roda por último de propósito: preenchê-la reescreve
toda a linha do backlog, e uma tabela carregando um milhão de tuplas mortas
cobraria do braço seguinte o trabalho deste.

## Por que a sonda reimplementa a aritmética da cadeia

Os tipos de produção da cadeia são internos ao assembly da API e este projeto
está fora da lista de amigos dele. Ampliar a visibilidade de produção para
rodar uma medição seria mudança de produção, e a fatia da medição não faz uma.
Além disso, nenhuma costura de tempo dentro do appender separaria espera de
posse. As formas em `Infrastructure/AuditChainArithmetic.cs` e o SQL em
`Contention/AuditAppender.cs` espelham o appender byte a byte: ordem de campos,
precisão do carimbo, preimagem da âncora, espaço de chave do lock e statements,
inclusive a leitura do nível de isolamento que viaja na projeção do statement
do lock e a recusa de qualquer nível que não seja READ COMMITTED. O único termo
que a sonda acrescenta é o instante da concessão do lock, que existe para
separar espera de posse e que o appender não tem por que pagar.
Uma mudança lá que este projeto não acompanhe aparece como medição que deixa de
corresponder à produção, e o cenário de verificação da cadeia é o oráculo que
denuncia: ele reconstrói a partição inteira e recusa uma cadeia que bifurcou.

## Por que a sonda alcança a memoização por reflexão

Vale aqui a mesma restrição da seção acima: a memoização de leitura publicada é
interna ao assembly da API e este projeto está fora da lista de amigos dele. A
saída é outra porque o risco é outro. A cadeia de auditoria tem forma estável e
um oráculo que denuncia divergência, então reimplementá-la custa pouco; uma
política de evicção reimplementada mediria uma cópia, e a pergunta toda é como
o tipo publicado se comporta no teto. `Contention/PublishedReadCacheHandle.cs` e
`Infrastructure/ScribanParseCacheHandle.cs` ligam cada membro uma vez em
delegate, de modo que o braço paga uma chamada de delegate por operação e
nenhuma reflexão, e recusam a rodada com mensagem clara se o tipo deixar de
expor o que elas ligam. Conceder visibilidade de internos a
este projeto trocaria o arquivo inteiro por um `using`, e essa é uma decisão de
produção que a fatia da medição não toma sozinha.
