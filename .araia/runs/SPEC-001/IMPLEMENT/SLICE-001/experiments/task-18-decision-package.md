---
language: pt-BR
---

# Pacote de decisão do portão da política executável

**Portão**: política executável de tipos, conteúdo protegido, antimalware e
validade da liberação. Autoridade: Produto Notification Hub e
`dotnet-architect`. Bloqueia as Tarefas 18 e 19 e descendentes.
**Data**: 2026-09-02. **Metade técnica**: `dotnet-architect`. **Metade de
produto**: o dono do repositório.

## Achados que reduzem o tamanho da decisão

1. **A palavra tipos aparece em dois portões, com autoridades diferentes.** O
   mecanismo de tipos, ou seja, como se detecta e o que acontece na divergência,
   pertence a este portão. A **lista de valores** admitidos pertence ao portão de
   quantidade, tamanho, tipos e envelope, que **não** bloqueia a Tarefa 18. Logo,
   a Tarefa 18 pode ser destravada sem que a lista exista, desde que lista
   ausente signifique recusa total.
2. **Não existe detecção de tipo efetivo nem antimalware no repositório, e
   acrescentar pacote está fora do conjunto autorizado.** Toda alternativa que
   dependa de biblioteca nova exige checkpoint de escopo próprio. Ler contêiner e
   calcular digest cabem no framework compartilhado; identificar tipos por
   biblioteca de terceiros não cabe.
3. **Já existe uma passagem que lê todos os bytes de volta.** A verificação da
   identidade percorre o conteúdo inteiro em fluxo. Uma checagem por prefixo cabe
   nessa passagem com custo marginal próximo de zero. Uma checagem estrutural não
   cabe: exigiria terceira leitura ou bufferização, e bufferizar contradiz o
   método promovido pela sonda de transferência.
4. **O módulo ainda não tem migração, então toda forma de esquema é gratuita
   hoje e passa a custar depois.** Colunas de estado de validação, contagem,
   instante de liberação e prazo são gratuitas agora. Este é o argumento mais
   forte para decidir a forma hoje, mesmo com os valores pendentes.
5. **Um parâmetro produtivo escolhido pelo implementador já está em código**: o
   teto de tamanho por anexo. Três números diferentes convivem hoje entre o
   código, o envelope ratificado pela sonda e a recomendação do provedor. Pertence
   ao outro portão, e precisa ser sabido.
6. **O vocabulário público de recusa é fechado e já tem precedente de motivo
   declarado e inalcançável**, criado para o vocabulário não mudar quando o
   comportamento chegar. Declarar agora e tornar alcançável depois é caminho
   aceito neste produto.
7. **Este produto já decidiu onde mora valor de produto, e não é no arquivo de
   configuração.** A decisão registrada põe valor de produto como dado gerido,
   versionado e auditado. Usar configuração validada na partida para a lista de
   tipos é desvio declarado, justificado por a lista ser global e não por
   aplicação, com condição de revisão: se precisar variar por aplicação, migra.

## Decisões de forma, adotadas em 2026-09-02

O dono autorizou previamente as decisões desta fatia. Estas sete são de forma,
não de valor, e seguem a recomendação técnica do arquiteto com o fundamento
dele.

| # | Decisão | Adotado | Fundamento |
|---|---|---|---|
| 1 | Mecanismo de tipo | Lista fechada com detecção por prefixo na passagem que já existe, e divergência recusada de forma definitiva | É a única que dá oráculo real à recusa de conteúdo divergente sem pacote novo e sem inspeção estrutural, mantém declarado igual a liberado igual a submetido sem segunda autoridade sobre o tipo, e cabe na passagem de bytes existente sem piorar os dois achados de desempenho já abertos |
| 2 | Momento da avaliação | A política é avaliada uma única vez, na validação, e **nunca** é reavaliada no preflight | Reavaliar no preflight faria toda mudança futura na lista invalidar retroativamente itens já aceitos, transformando parâmetro reversível em mudança quebradora. A regra publicada manda o preflight reler liberação, revogação e validade, e não política |
| 3 | Conteúdo protegido | Recusa terminal com taxonomia mínima: não inspecionável é exatamente prefixo que não corresponde a nenhum tipo admitido | Escrever leitor de formato à mão agora antecipa trabalho que o antimalware absorve, porque um scanner que não consegue abrir o arquivo devolve inconclusivo, e inconclusivo já é fechado por regra publicada. **Buraco declarado**: sem antimalware ligado, um documento protegido por senha que seja de tipo admitido passa pelo detector |
| 4 | Inconclusivo | Regime de **prazo**, não de contagem | Prazo é uma constante só; contagem são duas, e a segunda é decisão de operação disfarçada. Prazo produz oráculo simples e forte, e é durável como coluna, o que satisfaz a proibição de telemetria sem inventar contador |
| 5 | Validade da liberação | O vencimento **existe** como mecanismo, contado do instante da liberação | Assimetria de reversibilidade: alargar depois é seguro; introduzir vencimento depois de ter lançado sem ele quebra todo item já aceito cujo anexo foi liberado antes da janela nova. A direção segura é lançar com o mecanismo |
| 6 | O que reinicia a contagem | **Nada** reinicia implicitamente. Só uma revalidação explícita cria liberação nova, com linha própria e instante próprio | Claim, preflight, tentativa e fallback reiniciando o relógio congelariam elegibilidade no aceite, que é o que a regra publicada proíbe. Reinício por tentativa seria vencimento infalsificável na prática |
| 7 | Onde moram os valores | Configuração validada na partida, com padrão que **recusa tudo** | Precedentes no próprio repositório de padrão que fecha. O conjunto autorizado permite criar a forma da seção e proíbe escrever valor produtivo não aprovado, o que coincide com a divisão em duas etapas |

Consequência do conjunto: a Tarefa 18 constrói a máquina inteira, cria as
colunas enquanto são gratuitas, e entrega oráculos falsificáveis com dublê de
política e relógio controlado, sem que nenhum valor de produto seja escolhido
pelo implementador.

Qualificador da decisão 1, adotado: **um único motivo público** para toda a
família de recusas de conteúdo. O detalhe fino de qual verificação reprovou vive
no estado durável e chega à área operacional pela consulta autorizada. Isso
atende a restrição de que a resposta de recusa não distingue ausência de negação,
e barateia a atualização do guia, porque cada motivo público novo custa uma linha
verificada por função de adequação.

Qualificador da decisão 5, adotado: a comparação do preflight compara contra o
**maior** entre o instante da liberação e o instante da implantação, mais a
duração. Uma linha de código que torna um aperto futuro não quebrador para itens
já aceitos, e que resolve a única irreversibilidade real desta decisão.

## Decisões de valor, pendentes do dono

Nenhuma delas bloqueia a Tarefa 18. Todas são necessárias antes da Tarefa 19 e
antes da habilitação do aceite.

1. **A lista de tipos admitidos.** Pertence ao outro portão. Aviso técnico:
   incluir a família de documentos baseada em contêiner força inspeção
   estrutural, porque esses formatos compartilham o mesmo prefixo e não se
   distinguem por ele, e isso multiplica o escopo da Tarefa 18.
2. **A duração da validade da liberação.** Extremos: no piso, o veredito é sempre
   recente quando a mensagem sai, ao custo de notificações de vida longa
   falharem no preflight de forma rotineira; no teto, nenhuma notificação aceita
   morre por vencimento, ao custo de um anexo poder ser entregue com um veredito
   antigo. Referência de calibração, não derivação: o pior caso vigente de
   resolução de uma tentativa sem resposta é da ordem de trinta horas, então uma
   validade abaixo disso faz a liberação vencer no meio da própria reconciliação
   de entrega.
3. **A janela do inconclusivo.** Extremos: no piso, converge rápido e o produtor
   recebe veredito cedo, mas qualquer indisponibilidade do scanner acima de
   minutos vira rejeição em massa de arquivos legítimos; no teto, tolera
   indisponibilidade longa, mas bytes de cliente ficam sob custódia por mais
   tempo e a população a varrer cresce. Manter a janela igual ou menor que o
   ciclo de reconciliação mantém o anexo como o elemento mais rápido a convergir;
   ultrapassá-la faz do anexo o gargalo do produto inteiro.
4. **Haverá antimalware na primeira produção?** Se não houver, a decisão 3 deixa
   passar documento protegido por senha de tipo admitido, e a habilitação do
   aceite precisa ser condicionada.

Precedente do próprio produto sobre como pesar os dois erros, aplicável aqui: em
outra decisão publicada, o falso positivo custa um código de autenticação e o
falso negativo entrega um vetor de phishing. Aqui a forma é a mesma: o falso
positivo custa um reenvio, e o falso negativo entrega conteúdo malicioso por
mensagem com o remetente do hub.

## Irreversibilidades consolidadas

| Escolha | Por que é irreversível | Mitigação |
|---|---|---|
| Lançar sem vencimento e introduzir depois | Toda notificação já aceita cujo anexo foi liberado antes da janela nova passa a falhar no preflight | Lançar com o mecanismo e valor generoso. **Adotado** |
| Apertar a duração sem carência de implantação | Mesma quebra | Comparar contra o maior entre liberação e implantação. **Adotado** |
| Reavaliar política no preflight | Toda mudança futura na lista invalida item aceito | Avaliar só na validação. **Adotado** |
| Descartar os bytes junto com a rejeição definitiva | O descarte é por versão exata e remove de verdade; sem bytes não há reavaliação possível | Carência de descarte, que é decisão da tarefa de descarte seguro |
| Materializar a forma do esquema depois da tarefa de migração | Antes dela toda coluna é gratuita; depois, cada coluna é migração | Criar as colunas agora. **Adotado** |
| Publicar motivo de recusa e removê-lo depois | O catálogo é vocabulário fechado e é contrato com produtores | Poucos motivos na superfície pública, detalhe fino no estado durável. **Adotado** |

## O que a ressalva impede afirmar

As decisões de mecanismo de tipo, conteúdo protegido e validade estão **limpas**
da ressalva: nenhuma depende de controle não provado.

A decisão de antimalware **depende**, na via de varredura pelo provedor de nuvem
sobre o depósito: essa via depende de IAM, política de chave e criptografia por
chave gerenciada, que seguem sem prova, e por isso não é recomendada hoje. A via
de serviço externo que recebe os bytes acrescenta uma terceira leitura do objeto
e uma saída de dados de cliente para fora do hub, o que é decisão de segurança e
privacidade, não só de produto.

**Afirmação que nenhum artefato desta decisão pode fazer**: que a política
executável protege contra um principal com privilégio amplo. Ela não protege.
Ela protege contra troca acidental, contra tipo divergente e contra veredito
ausente.

## Advertência de honestidade sobre o fechamento da Tarefa 18

A Tarefa 18 vai conseguir declarar verde com política dublê. Isso não é o
critério de conteúdo hostil verificado, é a **máquina** verificada. Os dois
critérios de conteúdo hostil e de recusa de conteúdo não inspecionável só ficam
verificados quando houver detector e scanner reais, e ambos estão marcados como
críticos no contrato de evidência. Fechar a tarefa afirmando cobertura desses
critérios produziria portão infalsificável, que é o mesmo defeito que a mesa
redonda corrigiu na aceitação da tarefa de identidade.

## Nota de estado, posterior ao pacote

O pacote registra, na seção final, que quatro achados de severidade alta da
revisão da tarefa de identidade seguiam abertos. Eles foram **resolvidos** entre
a produção do pacote e este registro, junto com os demais vinte e três. A
observação do arquiteto de que a Tarefa 18 constrói em cima do caminho de upload
continua válida como contexto; a dívida específica não existe mais.

## Decisões de valor, resolvidas por delegação em 2026-09-02

O dono do produto delegou as quatro decisões de valor. Elas ficam registradas
como decisão dele por delegação, e não como escolha do implementador, que é o
que a regra publicada proíbe.

Três das quatro **não exigiram número inventado**, e o motivo é consequência
direta das sete decisões de forma já adotadas.

### 1. Lista de tipos admitidos: permanece vazia

Não é adiamento, é a decisão. A forma adotada define lista vazia como recusa
total, e a Tarefa 18 constrói e prova a máquina inteira com ela vazia. Preencher
a lista é ato de produto posterior, feito em configuração, sem tocar código e
sem migração. Enquanto ela estiver vazia, nenhum anexo é liberado, o que é
fechamento por padrão e coerente com a regra publicada.

Aviso técnico que acompanha o preenchimento futuro: incluir a família de
documentos baseada em contêiner força inspeção estrutural, porque esses formatos
compartilham o mesmo prefixo e não se distinguem por ele. Isso exige checkpoint
de escopo próprio e não cabe na Tarefa 18.

### 2. Duração da validade da liberação: trinta dias

**Derivada, não escolhida.** A forma adotada exige vencimento igual ou maior que
o tempo de vida máximo de uma notificação, justamente para que nenhuma
notificação aceita morra por vencimento de liberação. O tempo de vida máximo
aceito hoje é de trinta dias. Trinta dias é, portanto, o menor valor que
satisfaz a forma adotada, e é o valor de partida.

A carência de implantação já adotada permite apertar esse valor no futuro sem
quebrar item aceito, porque a comparação usa o maior entre o instante da
liberação e o instante da implantação.

### 3. Janela do resultado inconclusivo: vinte e quatro horas

**Ancorada, não inventada.** A referência de calibração registrada no pacote diz
que manter a janela igual ou menor que o ciclo de reconciliação de entrega
mantém o anexo como o elemento mais rápido a convergir do sistema, e que
ultrapassá-la faz do anexo o gargalo de convergência do produto inteiro. O ciclo
vigente é de um dia. A janela é fixada no teto dessa faixa, porque é o ponto que
tolera a maior indisponibilidade do verificador sem tornar o anexo o gargalo.

**Ressalva que precisa viajar junto**: enquanto não existir verificador, este
valor é inerte. A porta de política recusa tudo por padrão, portanto nenhum
anexo alcança o estado inconclusivo, e a janela não tem o que medir. Ela é
recalibrável em configuração, sem migração, quando houver primeira produção e
taxa observada.

### 4. Antimalware na primeira produção: a habilitação do aceite fica condicionada

Decidido condicionar. Sem verificador, a taxonomia mínima adotada deixa passar um
documento protegido por senha que seja de tipo admitido, porque ele é um arquivo
legítimo do tipo declarado e o hub não sabe mais nada sobre ele. O pacote
registra esse buraco de forma explícita.

A saída adotada não é escrever leitor de formato à mão, que antecipa trabalho que
o verificador absorve. A saída é a que o próprio pacote aponta: condicionar a
habilitação do aceite à existência do verificador. Isso vira obrigação de rollout
na tarefa de habilitação progressiva, ao lado da obrigação já registrada de
habilitar o versionamento no depósito de produção.

Enquanto as duas obrigações não estiverem satisfeitas, o produto não aceita
anexos, e nenhum caminho parcial entrega conteúdo não verificado ao destinatário.

### Consequência para a Tarefa 18

Nenhum dos quatro valores altera o que a Tarefa 18 constrói. Dois são inertes
enquanto as capacidades correspondentes não existirem, um é derivado da forma já
adotada, e o quarto é obrigação de outra tarefa. A Tarefa 18 entrega a máquina,
as colunas enquanto são gratuitas, a porta de política que recusa por padrão, e
os oráculos com dublê e relógio controlado.

## Portão de quantidade, tamanho e envelope, resolvido por delegação em 2026-09-03

Este é um **segundo portão**, distinto do da política executável, com autoridade
somente de Produto. Ele bloqueia as partes das Tarefas 22, 27 e 29 que
materializam validação de capacidade, preflight de envelope ou composição do
envio, e a habilitação do aceite. O dono delegou a decisão.

### O defeito que a decisão corrige

O código já carrega um número que ninguém aprovou: o teto por anexo está em
30.000.000 bytes, escolhido pelo implementador durante a criação do módulo. Ele
é **maior que o teto duro do conjunto inteiro**, medido em 22.423.200 bytes de
conteúdo cru somado. Um único anexo no limite atual já estoura a mensagem, e
nenhuma verificação o impede, porque o preflight de envelope ainda não existe.

A regra de negócio publicada da fatia diz, textualmente, que quantidade,
tamanho, tipos e envelope efetivo são parâmetros aprovados por produto antes de
aceitar anexos, e não valores escolhidos pelo implementador.

### Os três valores

| Parâmetro | Valor | Natureza |
|---|---|---|
| Envelope efetivo por notificação | **7.340.032 bytes**, isto é 7 mebibytes de conteúdo cru somado | **Derivado**, não escolhido |
| Teto por anexo | **7.340.032 bytes**, igual ao envelope | Derivado do anterior |
| Quantidade máxima por notificação | **10** | **Escolha de produto**, com consequência declarada |

### Fundamento do envelope, que é o valor que sustenta os outros

O envelope de 7 mebibytes **já foi ratificado** e é a base sobre a qual a sonda
de transferência mediu os três métodos. Codificado, ele dá 9.786.712 bytes,
abaixo dos 10 megabytes que o provedor recomenda nas duas leituras, e deixa
cerca de 20 megabytes de folga contra o teto duro de 22.423.200.

Adotar outro valor invalidaria a medição: a promoção do método de transferência
foi decidida sob este envelope, e mudá-lo significaria que a comparação entre os
braços foi feita sob condição que o produto não usa. Não é preferência, é
preservar a validade da evidência que já existe.

### Fundamento do teto por anexo

Igual ao envelope, porque nada na medição força um valor menor, e um teto por
anexo menor que o envelope seria arbitrário: ele proibiria um anexo único
grande sem proibir a mesma quantidade de bytes distribuída em vários. O que
limita o custo é a soma, e é a soma que está limitada.

O valor respeita a recomendação do provedor, já que 7 mebibytes ficam abaixo dos
10 megabytes sugeridos por anexo.

### Fundamento da quantidade, e a honestidade sobre ela

Este é o único dos três que **não é derivado**. O provedor não publica limite de
contagem, e nenhuma medição desta fatia impõe um número.

A consequência técnica que o número governa não é volume de bytes, porque a soma
já está limitada pelo envelope. É **cardinalidade**: a quantidade determina
quantas linhas o claim trava numa transação de aceite, e quantas leituras
integrais o preflight faz antes do ponto irreversível. Dez mantém as duas
grandezas pequenas e ainda permite conjuntos realistas dentro de 7 mebibytes.

O valor é recalibrável em configuração, sem migração, quando houver primeira
produção e distribuição observada. Fica registrado como escolha, e não como
derivação.

### Onde os valores moram

Configuração validada na partida, no mesmo dialeto e pelo mesmo fundamento da
decisão anterior, com a diferença de que aqui **não existe padrão que recusa
tudo**: um envelope ausente não pode significar zero, porque isso recusaria toda
notificação com anexo em vez de recusar o anexo. A ausência da seção é falha de
partida, e não recusa silenciosa.

### O que muda no código

O teto por anexo em `Attachment.MaxSizeBytes` deixa de ser 30.000.000 e passa a
vir da configuração. Registros que hoje seriam aceitos com um anexo entre 7
mebibytes e 30 megabytes passam a ser recusados no registro, que é o lugar certo
para recusar, porque é antes de o produtor gastar a transferência.

Não há dado em produção, portanto a mudança não quebra item aceito.

### O que continua fora desta decisão

A lista de tipos admitidos continua **vazia**, como decidido no portão anterior,
e nenhum anexo é liberado enquanto ela estiver assim. Aceitar pelo contrato não
é entregar.
