---
language: pt-BR
---

# Revisão consolidada da Tarefa 16

**Veredicto**: `FINDINGS`. **Data**: 2026-09-02.
**Revisores**: `dotnet-architect`, `dotnet-engineer`, `dotnet-specialist`, em
contextos independentes e sem ver o recibo um do outro. Cobertura das seis
lentes completa nos três recibos.
**Revisão de origem**: árvore não commitada sobre `1a81063`. O envelope
declarava `b9d4d27`; os três revisores detectaram a divergência de forma
independente e confirmaram que nenhum dos nove commits intermediários toca os
arquivos em escopo.

## Aviso de fronteira

A árvore se moveu durante a revisão. O construtor da Tarefa 17 reescreveu
`IAttachmentObjectStore.cs`, `S3AttachmentObjectStore.cs` e
`UnavailableAttachmentObjectStore.cs`, os três em escopo, e criou
`AttachmentObjectDiscard.cs`, fazendo o descarte passar a devolver desfecho.
Isso resolveu parte de um achado antes mesmo da consolidação. O engenheiro e o
especialista reconferiram suas citações contra o estado posterior; o arquiteto
fechou antes. Um achado pode ter sido resolvido depois do recibo que o levantou.

## Achados consolidados

### `STK-001` Tempo esgotado escapa de toda cláusula de captura das três operações

**Severidade**: `HIGH`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: `dotnet-specialist` (medido) e `dotnet-engineer` (medido por
reflexão, mesma raiz).
**Local**: `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Storage/S3AttachmentObjectStore.cs:49-69`, `:88-108`, `:130-145`.

Medido contra a montagem recompilada, com três formas de falha de rede: conexão
recusada e host inexistente devolvem indisponível nas três operações; **porta
que aceita a conexão e não responde propaga `TimeoutException` nas três**.
Medido por reflexão nos dois recibos: `AmazonS3Exception` e
`AmazonClientException` são irmãs e não parentes, portanto qualquer
`AmazonServiceException` que não seja de S3 também escapa, incluindo as que a
cadeia de credenciais lança ao assumir papel, cenário que a produção exercita e
a fixture não, porque ela usa credencial básica.

Três consequências, por leitura do código corrente:

1. A escrita é chamada **fora** do bloco protegido do handler. Tempo esgotado
   ali é exceção não tratada: quinhentos, sem compensação, sem localizador e sem
   registro, enquanto o endpoint anuncia indisponibilidade. Os bytes podem estar
   duráveis.
2. O descarte lançando de dentro do bloco de captura genérico **destrói a
   exceção original**, e a causa real nunca chega ao chamador nem ao log.
3. O descarte lançando de dentro do bloco de concorrência converte um conflito
   controlado em quinhentos.

O tipo do desfecho de descarte documenta que "não há terceira resposta de
propósito"; a medição mostra que existe uma quarta, que é não responder.

**Verificação**: apontar o endereço de serviço para um destino que aceite a
conexão e não responda, e chamar as três operações. Cai se todas devolverem
indisponível.

### `ENG-001` O desfecho do descarte é ignorado nos quatro pontos de compensação

**Severidade**: `HIGH`. **Confiança**: alta.
**Revisores**: os três, de forma independente.
**Local**: `.../Features/Attachments/UploadAttachment/UploadAttachment.Handler.cs:76`, `:91`, `:119`, `:131`.

O store passou a responder se removeu ou não, e a documentação da própria
interface diz que quem reporta os bytes como removidos tem de ter sido informado
disso. Os quatro pontos descartam a resposta com espera sem atribuição, e o
compilador não emite nada. O handler tem dez retornos de falha e uma única
chamada de log em caminho de falha.

Quando o descarte falha, os bytes continuam duráveis, o anexo continua
aguardando upload, e a escrita condicional recusa toda retentativa, sem que o
processo deixe rastro do par chave e versão que vazou. A chave é derivável do
identificador de conteúdo, então o órfão é reencontrável; a versão não é.

**Verificação**: trocar as quatro chamadas por descarte explícito do valor e
rodar a suíte filtrada. Tudo passa, o que prova que nenhum oráculo observa o
valor.

### `ENG-002` Captura sem identidade produz órfão garantido, mudo, e com código de erro que descreve outra coisa

**Severidade**: `HIGH`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: os três.
**Local**: `.../UploadAttachment/UploadAttachment.Handler.cs:56-65`.

Medido contra o dublê local: em bucket sem versionamento a escrita devolveu
captura sem identidade, com localizador nulo, e **o objeto ficou durável e
legível**. O ramo devolve falha de integração por indisponibilidade de
armazenamento, sem chamar descarte e sem emitir linha de log. Não existe
localizador para descartar depois, e não existe registro de que bytes ficaram
lá. O provedor estava perfeitamente disponível.

Isto não repete alternativa rejeitada: o recibo remete o mecanismo de
reconciliação às Tarefas 32 e 33, e não autoriza que o caminho de recusa produza
órfão mudo. É também achado de proteção de dados: bytes que o sistema declara
ter recusado permanecem duráveis sem registro que os nomeie, portanto nenhuma
rotina de retenção ou de exclusão a pedido os alcança por registro.

**Verificação**: rodar o oráculo de falha fechada de store nunca versionado e
listar o bucket criado pelo teste. Cai se vier vazio.

### `SEC-001` Uma credencial única acumula gravação, leitura por versão e exclusão por versão

**Severidade**: `HIGH`. **Confiança**: alta.
**Revisor**: `dotnet-architect`, com concordância do `dotnet-engineer` em
severidade média sob a mesma raiz.
**Local**: `.../Infrastructure/Storage/AttachmentObjectStoreOptions.cs:13-14`,
`.../Storage/AttachmentObjectStoreSetup.cs:17-39`,
`.../Storage/S3AttachmentObjectStore.cs:120-127`.

Esta afirmação depende de itens da ressalva da Tarefa 6, que não foram
executados: separação de privilégios entre os papéis de upload, validação e
descarte, e negação de exclusão por versão aos papéis normais. O achado não é
que o controle falhou; é que o código o torna inimplementável sem mexer no que a
Tarefa 16 acabou de consolidar.

A credencial do processo que aceita bytes de produtor carrega a capacidade de
exclusão permanente de versão, que é exatamente a capacidade que derrota a
imutabilidade que o registro afirma. O recibo já fixa que a escrita condicional
não é guarda de imutabilidade, então contra o principal do próprio processo não
sobra guarda nenhuma.

Combinado com `ENG-001`, o modo de falha é o pior possível: sob a separação de
papéis pretendida, toda compensação é recusada pelo provedor, o store devolve
indisponível, o handler ignora, nada é registrado, e cada upload que falha
depois da escrita deixa conteúdo de cliente durável e órfão.

**Verificação**: enumerar as ações que a credencial em execução precisa. Hoje
são gravação, leitura, leitura por versão e exclusão por versão num único
principal.

### `STK-002` A igualdade sintetizada da prova compara referência de memória, não conteúdo

**Severidade**: `MEDIUM`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: `dotnet-specialist` e `dotnet-engineer`, os dois por medição
independente.
**Local**: `.../Infrastructure/Storage/AttachmentContentProof.cs:10`, `:24`, `:41-44`.

O tipo é `record`, ou seja, o idioma promete igualdade por valor, mas o membro
que carrega a identidade é uma região de memória, cuja igualdade é por segmento.
Medido sobre dois digests byte a byte idênticos em vetores distintos: a
comparação por operador devolve falso, a comparação por método sintetizado
devolve falso, os códigos de espalhamento diferem, e um conjunto guarda os dois
como distintos. Só o método próprio de comparação devolve verdadeiro.

O tipo expõe duas operações de igualdade com significados incompatíveis: a
correta e de tempo fixo, e a que o compilador entrega de graça e responde
errado. A decisão residual 3 do recibo registra que o método próprio ainda não
tem chamador de produção, o que significa que o primeiro chamador a chegar, o
preflight da Tarefa 27, tem a mesma chance de alcançar o operador.

**Verificação**: comparar por operador duas provas construídas sobre um vetor e
sobre sua cópia. Cai se devolver verdadeiro.

### `SEC-002` Texto claro do anexo volta ao pool compartilhado do processo

**Severidade**: `MEDIUM`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: os três.
**Local**: `.../Infrastructure/Storage/AttachmentContentVerification.cs:46`.

A devolução ao pool não limpa o vetor. Medido: depois de uma verificação sobre
conteúdo com sentinela, o locatário seguinte alugou 131.072 bytes e a sentinela
estava legível no deslocamento 100.000. A janela é o vetor inteiro de 131.072
bytes, e não os 81.920 pedidos, porque o laço lê no vetor completo e a última
leitura só sobrescreve o prefixo.

Bytes de anexo de um produtor ficam residentes num pool compartilhado por todo o
processo e aparecem literalmente em qualquer despejo de memória, muito depois de
a requisição terminar. O módulo é justamente o de custódia de conteúdo de
cliente, e o arquivo vizinho já demonstra intenção de higiene criptográfica.
Esta afirmação não depende de nenhum item da ressalva da Tarefa 6.

**Verificação**: rodar a verificação sobre conteúdo com sentinela, alugar em
seguida e procurar a sentinela. Cai se não for encontrada.

### `ARC-001` A reidratação do localizador não passa pela guarda que o tipo declara ser única

**Severidade**: `MEDIUM`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: `dotnet-architect` e `dotnet-specialist`.
**Local**: `.../Infrastructure/Storage/AttachmentObjectLocator.cs:6-11`, `:67-69`;
`.../Infrastructure/Persistence/AttachmentObjectGeneration.cs:61-62`.

A documentação do tipo afirma que toda rejeição vive no único ponto de
construção e que nenhum chamador consegue montar um localizador que aponte para
o que estiver corrente. O construtor de confiança, três linhas abaixo, não
rejeita nada, e é o caminho de produção de reidratação a partir da linha
durável. A coluna de versão tem apenas comprimento máximo, sem restrição de não
vazio, enquanto a coluna menos consequente ganhou restrição de verificação.

Medido, e esta é evidência nova que a errata 2 do recibo não considerou: com
versão vazia, o descarte cria marcador e mantém a geração original durável; o
marcador reabre a escrita condicional; e aparece uma segunda geração durável sob
a mesma chave. A errata conclui que isso é inalcançável porque o descarte por
versão exata não cria marcador. A premissa é verdadeira e a conclusão só vale
enquanto nenhuma versão vazia entrar no descarte, e a reidratação é exatamente a
porta sem guarda. A decisão residual 2 mantém a cláusula do literal como defesa
em profundidade sem aplicá-la onde ela se torna alcançável.

**Verificação**: gravar uma linha de geração com versão vazia, reidratar e
passar o resultado ao descarte contra bucket versionado, depois listar as
versões. Cai se nada for criado ou se a reidratação recusar.

### `PRF-001` A verificação aloca proporcionalmente ao tamanho do anexo, e a inclinação não estava medida

**Severidade**: `MEDIUM`. **Confiança**: alta. **Evidência**: executada.
**Revisor**: `dotnet-specialist`.
**Local**: `.../Infrastructure/Storage/AttachmentContentVerification.cs:31-47`.

A passagem não materializa o anexo e o conjunto vivo é constante, o que confirma
a intenção da decisão. Mas a taxa de alocação é proporcional ao tamanho. Medido
contra fluxo síncrono: 888 bytes no total, idênticos para 1 MiB, 8 MiB e 30 MB.
Medido contra o fluxo real do provedor: 1.511.416 bytes para 1 MiB e
**41.366.208 bytes para 30 MB**, ou seja **1,377 byte alocado por byte de
anexo**.

Mecanismo identificado e medido: a leitura devolvida pelo cliente é um envoltório
que não sobrescreve a sobrecarga assíncrona sobre região de memória. Duas
hipóteses de correção foram medidas e refutadas: trocar para a sobrecarga de
vetor dá o mesmo número, e desligar a validação de checksum de resposta também.

No teto de tamanho admitido, cada upload gera cerca de 41 MB de lixo transiente
dentro do caminho da requisição. Dez uploads simultâneos no teto são cerca de
410 MB de rotatividade que o teto afim da Tarefa 9 não descreve.

**Verificação**: gravar um objeto no teto, aquecer, coletar, e medir o total
alocado em volta de uma segunda verificação. Cai se ficar abaixo de um mebibyte.

### `PRF-002` A verificação dobra o tráfego ao provedor no caminho quente, sem orçamento nem limite de concorrência

**Severidade**: `MEDIUM`. **Confiança**: alta no mecanismo.
**Revisores**: `dotnet-specialist` (medido) e `dotnet-architect` (derivado).
**Local**: `.../UploadAttachment/UploadAttachment.Handler.cs:72-73`.

Consequência direta da decisão aceita 4, e não contestação a ela. Medido: 282 ms
a 432 ms para 30 MB sobre laço local, com o provedor no mesmo host; em rede real
o tempo é dominado pela latência. O achado é que a mesa fixou a decisão sem
medir a grandeza, e não existe orçamento, portão nem limite de concorrência para
o dobro de tráfego que ela introduz. O limitador de taxa vigente concede
permissões por principal sem ponderação por bytes, e os portões de desempenho
existentes graduam a transferência ao provedor de e-mail no envio, não o
ingresso.

**Verificação**: cronometrar o manejo com um objeto no teto, com e sem a
passagem de verificação.

### `TST-001` O oráculo de congelamento cobre a coluna menos consequente das oito

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisores**: os três.
**Local**: `tests/Platform.IntegrationTests/AttachmentManagement/AttachmentObjectGenerationTests.cs:177-199`
contra `.../Configurations/AttachmentObjectGenerationConfiguration.cs:54-63`.

O congelamento cobre oito colunas; o único oráculo muta uma, a de versão da
linha. Remover qualquer um dos outros sete nomes da chamada de congelamento
deixa a suíte inteira verde, e entre eles estão o digest e o comprimento, que são
a prova que a tarefa existe para proteger. Nenhum teste lê os metadados do
modelo para conferir completude.

**Verificação**: remover o nome do digest da chamada de congelamento e rodar a
suíte filtrada. Cai se ficar vermelho.

### `ARC-002` Nada impede copiar a prova para o agregado, que é a alternativa rejeitada pela decisão

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisores**: `dotnet-architect` e `dotnet-engineer`.
**Local**: ausência de guarda em `Domain/Attachment.cs`,
`Configurations/AttachmentConfiguration.cs` e nos testes de arquitetura.

A decisão 2 é contrato, e hoje nada executável a sustenta. O módulo inteiro não
tem representação nos testes de arquitetura, e nenhum teste de modelo afirma que
a tabela do agregado não tem coluna de digest. A lista de proibidos do oráculo de
não vazamento nomeia bucket, chaves, endpoint, chave do objeto, identificador de
versão, identificador de conteúdo, conteúdo, nome e tipo, e **não nomeia o
digest**. A superfície de resposta está protegida; a persistência não.

Acrescentar o digest ao agregado mais o mapeamento, ou à resposta do upload,
passa por todos os portões atuais.

**Verificação**: acrescentar a propriedade e o mapeamento e rodar arquitetura,
unidade e integração. Cai se algum ficar vermelho.

### `ENG-003` Os comentários de persistência afirmam imutabilidade que o mapeamento não impõe

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisores**: `dotnet-architect` e `dotnet-specialist`.
**Local**: `.../Configurations/AttachmentObjectGenerationConfiguration.cs:51-53`
e `.../Persistence/AttachmentObjectGeneration.cs:5-10`.

Os comentários afirmam que qualquer tentativa de revisar coluna de identidade é
recusada na persistência e que a linha nunca é revisada. O recibo mediu contra
banco real que o comportamento tem quatro formas, e que em duas nada é recusado:
a atualização de entidade destacada não lança e descarta em silêncio, e a
atualização em massa atravessa a guarda e reescreve o valor durável. A guarda
durável chega só com o gatilho da Tarefa 35, e nenhum dos dois comentários diz
isso.

Este é o critério de honestidade do envelope: afirmação forte apoiada em
silêncio. A errata do recibo corrigiu a justificativa da representação textual e
passou por cima destes dois.

**Verificação**: recarregar linha destacada, adulterar o digest, atualizar e
persistir. Cai se lançar.

### `TST-002` O oráculo de falha fechada não olha para os bytes, e a fixture não permite olhar

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisores**: `dotnet-architect` e `dotnet-specialist`.
**Local**: `tests/.../AttachmentObjectGenerationTests.cs:286-328` com
`tests/.../AttachmentManagementApiFixture.cs:102-128`.

Os dois oráculos de falha fechada verificam estado, código, instante e ausência
de linhas de geração, e não verificam que a escrita já colocou objeto durável.
E não teriam como: o auxiliar de listagem por versão está preso ao bucket
constante da fixture, enquanto os testes criam bucket ad hoc. O órfão medido em
`ENG-002` é estruturalmente inobservável por esse oráculo.

O nome do teste é honesto sobre a tabela e silencioso sobre o objeto, e o leitor
conclui que nada aconteceu.

**Verificação**: listar o bucket ad hoc ao final do oráculo. Cai se vier vazio.

### `TST-003` O oráculo do token de compensação afirma o mecanismo e reprovaria a correção

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisor**: `dotnet-engineer`.
**Local**: `tests/.../UploadAttachmentEndpointTests.cs:270` e `:332`.

A asserção de que o token não pode ser cancelado afirma o mecanismo, que é ser
exatamente o token vazio, e não a propriedade que importa, que é não estar
ligado ao token do chamador. Um token com prazo, que é a correção necessária
para `PRF-003`, pode ser cancelado, portanto o oráculo atual **reprova a
correção** e tranca a compensação sem limite de tempo.

**Verificação**: trocar o token vazio por um com prazo e rodar os dois testes.
Eles ficam vermelhos sem que nenhum comportamento observável tenha piorado.

### `PRF-003` A compensação não tem limite de tempo algum

**Severidade**: `MEDIUM`. **Confiança**: média.
**Revisor**: `dotnet-engineer`.
**Local**: `.../UploadAttachment/UploadAttachment.Handler.cs:76`, `:91`, `:119`,
`:131` com `.../Storage/AttachmentObjectStoreSetup.cs:44-65`.

A compensação é desligada do token do chamador de propósito, e isso está certo,
mas o efeito colateral não está fechado: o token vazio não tem limite, e o
cliente é construído sem tempo limite e sem política de retentativa. O único
limite passa a ser o padrão da biblioteca, justamente na situação em que ele é
acionado, que é a de provedor lento ou parado. Cada upload que falha depois da
escrita ocupa uma vaga de requisição pelo orçamento inteiro, e o endpoint está
sob limitador de taxa, então vagas presas viram recusa de tráfego legítimo.

**Verificação**: apontar o store para endereço que aceita a conexão e nunca
responde, provocar o caminho de verificação sem prova, cancelar o cliente e
medir a duração no servidor.

### `ENG-004` Compensação dentro do bloco cujo tratamento também compensa

**Severidade**: `MEDIUM`. **Confiança**: alta.
**Revisor**: `dotnet-engineer`, com a mesma consequência derivada pelo
`dotnet-specialist` em `STK-001`.
**Local**: `.../UploadAttachment/UploadAttachment.Handler.cs:75-93` contra
`:125-135`.

As compensações dos dois primeiros pontos estão dentro do bloco cujo tratamento
genérico também compensa. Se o descarte lançar, o tratamento roda, consulta o
estado durável, descarta de novo e relança: uma falha limpa de quatrocentos vira
quinhentos, com duas tentativas de exclusão. Nos outros dois pontos a chamada
está dentro do tratamento, então uma exceção ali substitui a original e a causa
primária desaparece. Que o descarte pode lançar está medido em `STK-001`.

**Verificação**: injetar store cujo descarte lança, chamar o caminho de tamanho
divergente e observar o código de status.

### `ENG-005` Ausência e indisponibilidade colapsam no mesmo nulo

**Severidade**: `MEDIUM`. **Confiança**: média.
**Revisor**: `dotnet-specialist`.
**Local**: `.../Infrastructure/Storage/AttachmentContentVerification.cs:19`,
`:25-28` e `.../UploadAttachment/UploadAttachment.Handler.cs:74-78`.

A verificação devolve nulo tanto para geração ausente quanto para
indisponibilidade, e o handler mapeia ambos para falha de integração por
indisponibilidade. Ausência logo após escrita condicional bem-sucedida sobre
versão fixada não é indisponibilidade: é evento de integridade. O repositório
tem precedente de tratar nulo com significados incompatíveis como defeito, nos
commits `6ff3deb` e `27b8b09`.

**Verificação**: apagar a versão fixada entre a escrita e a verificação e
observar a distinção na resposta e no log. Cai se ela existir.

### `ENG-006` O evento de sucesso da captura não carrega correlator algum

**Severidade**: `LOW`. **Confiança**: alta. **Evidência**: executada.
**Revisores**: `dotnet-architect` e `dotnet-specialist`.
**Local**: `.../UploadAttachment/UploadAttachment.Handler.Logger.cs:24-31`.

Medido executando o método gerado contra um provedor capturador: a mensagem
promete nomear uma geração e entrega um literal constante, porque a
representação textual do localizador é sempre a cadeia redigida. O evento
vizinho carrega a referência do anexo. Uma investigação não consegue ligar o
evento a um anexo. No sentido oposto, o aviso de reconciliação afirma que o
objeto foi preservado e não nomeia nada, e um teste congela essa ausência.

**Verificação**: capturar o evento e afirmar que ele contém a referência do
anexo. Cai se já contiver.

### `TST-004` Duas asserções que não podem falhar

**Severidade**: `LOW`. **Confiança**: alta.
**Revisores**: `dotnet-engineer` e `dotnet-specialist`.
**Local**: `tests/.../AttachmentObjectGenerationTests.cs:272-273` e
`tests/Platform.UnitTests/AttachmentManagement/AttachmentContentProofTests.cs:46-53`.

A primeira compara representações hexadecimais de 32 e de 64 caracteres, que
são de comprimentos diferentes por construção. A segunda afirma que uma cadeia
de dez caracteres não contém a representação de 32 bytes. Nenhuma das duas
discrimina. A força real do primeiro teste está na recomputação independente,
que é falsificável.

Registro em favor da implementação: a afirmação central da decisão 4, de que a
prova vem da releitura e não da contagem da escrita, **está** falsificada por
oráculo executado, num teste em que o dublê guarda menos bytes do que recebeu.

### `ENG-007` Ramo inalcançável e assimétrico

**Severidade**: `LOW`. **Confiança**: alta.
**Revisores**: `dotnet-engineer` e `dotnet-specialist`.
**Local**: `.../UploadAttachment/UploadAttachment.Handler.cs:95-98`.

O ramo é inalcançável, porque a guarda anterior já retornou e nada entre as duas
linhas recarrega o estado da instância rastreada. Além de morto, é o único ramo
posterior à captura sem compensação, divergindo do irmão imediato. No dia em que
o estado passar a ser relido dentro da transação, ele cria órfão por construção.

### `STK-003` Corpo entregue a menos vira indisponibilidade de armazenamento

**Severidade**: `LOW`. **Confiança**: alta. **Evidência**: executada.
**Revisor**: `dotnet-specialist`.
**Local**: `.../Infrastructure/Storage/S3AttachmentObjectStore.cs:66-69`.

Medido: a exceção que o servidor lança quando o cliente entrega menos corpo do
que declarou deriva da exceção de entrada e saída, portanto cai na cláusula de
captura e vira indisponibilidade de armazenamento, para uma falha que é de
classe de requisição.

### `STK-004` Desvios da tabela de `var` do adaptador

**Severidade**: `LOW`. **Confiança**: alta.
**Revisores**: `dotnet-architect` e `dotnet-specialist`, com o
`dotnet-engineer` divergindo.
**Local**: `.../Storage/AttachmentContentVerification.cs:31`,
`.../Storage/S3AttachmentObjectStore.cs:18`,
`.../UploadAttachment/UploadAttachment.Handler.cs:153`.

Dois revisores citam a tabela do adaptador, que prescreve tipo explícito quando
o lado direito é chamada de método cujo nome não embute o tipo, e apontam que os
mesmos arquivos já seguem a regra em linhas vizinhas. Ver dissenso abaixo.

### `PRF-004` Contagem morta no caminho quente

**Severidade**: `LOW`. **Confiança**: alta.
**Revisor**: `dotnet-specialist`.
**Local**: `.../Infrastructure/Storage/S3AttachmentObjectStore.cs:19`.

O envoltório de leitura continua necessário, porque declarar comprimento sobre
fluxo não pesquisável é o que sustenta a escrita, mas a contagem deixou de ter
leitor depois que a decisão 4 moveu a prova para a releitura. O nome do tipo
anuncia uma medição que ninguém consome.

## Dissenso preservado

1. **Tabela de `var`** (`STK-004`). O arquiteto e o especialista citam a tabela
   do adaptador e apontam desvio em três posições. O engenheiro afirma que o
   dialeto vigente é o do arquivo de configuração de editor do projeto, que
   habilita `var` para tipos internos, e não vê achado. A tabela do adaptador é
   autoritativa salvo quando a configuração local recebe precedência explícita
   por regra. Fica registrado sem decisão.
2. **Severidade de `SEC-001`**. O arquiteto grada alto; o engenheiro grada médio
   e diz que vira alto no dia em que a separação de papéis for aplicada. Ambos
   marcam a dependência da ressalva não provada.
3. **Domicílio de `ENG-001`**. O especialista observa que o construtor da Tarefa
   17 já reescreveu o contrato do store e que a leitura do desfecho pode ter dono
   naquela tarefa; nesse caso o achado deve ser encaminhado, e não reaberto aqui.

## Pontos verificados sem achado, para o registro

- As superfícies de vazamento do localizador foram examinadas pelos três. A
  representação textual devolve constante, medido inclusive através do gerador
  de mensagens de log; a resposta pública expõe somente referência e estado; a
  linha da geração não é projetada para resposta alguma; e o oráculo de não
  vazamento usa sondas plantadas reais e exige a presença da linha do localizador
  entre os eventos capturados, o que o torna falsificável.
- A propagação do token na verificação está correta, medida com cancelamento
  antes da chamada e no meio de trinta megabytes: lança, devolve o vetor ao pool
  e descarta a leitura.
- Os sete pontos normativos do recibo foram conferidos um a um pelo especialista
  e conferem, incluindo a ausência deliberada de índice único e o comportamento
  restritivo de exclusão da decisão residual 1.
- A varredura de referências de especificação nos arquivos em escopo está limpa.
- O caminho de autorização recusa referência de outra aplicação sem ler o corpo
  da requisição, com oráculo que conta leituras.

## Lacunas declaradas pelos revisores

- Nenhum dos três conseguiu executar as suítes do repositório: a árvore estava
  sendo editada pelo construtor da Tarefa 17 e chegou a não compilar. As
  medições feitas foram fora da árvore ou contra montagem recompilada à parte.
  Os números de validação do envelope permanecem como entrada, não
  reverificados.
- Toda medição contra provedor usou o dublê local, o mesmo do recibo, o que
  torna os resultados comparáveis e continua sendo dublê.
- Os doze casos negativos da ressalva da Tarefa 6 continuam abertos. Os três
  revisores marcaram explicitamente quais afirmações dependem deles.
- O módulo não tem migração, então nenhuma afirmação de esquema desta revisão
  foi confirmada contra a criação de esquema de produção.

## Resolução, 2026-09-02

O dono mandou resolver todos. Os 23 foram tratados. Validação após a correção:
build com aviso tratado como erro em zero avisos; unidade 1792 de 1792 sem
pulos, contra 1759 antes; integração do módulo 74 de 74 sem pulos, contra uma
linha de base de 66 de 66 medida na mesma sessão antes da primeira edição;
arquitetura 30 de 30; arquitetura de segurança 14 de 14. Dezesseis mutações de
runtime, cada uma aplicada, medida, revertida por comparação byte a byte contra
cópia intocada e recompilada antes da medição seguinte.

**Erro do brief, registrado**: o brief de correção agrupou 21 dos 23 achados e
deixou `SEC-002` de fora. O construtor detectou a omissão contra esta revisão,
que é a fonte da verdade, e corrigiu o achado assim mesmo. A contagem de 21 que
circulou antes está errada; são 23.

| Achado | Resolução |
|---|---|
| `STK-001` | Corrigido. Predicado único de falha de armazenamento nas três operações, cobrindo exceção de serviço, tempo esgotado, requisição, entrada e saída, e cancelamento que não veio do chamador. Oráculo novo de unidade com seis classes de falha por três operações, mais um contra ouvinte que aceita e não responde |
| `ENG-001` | Corrigido. Os quatro pontos viraram um, que lê o desfecho e registra quando a remoção não é confirmada. Limitação declarada: o evento nomeia a referência e não o par chave e versão, porque registrar a versão é o falsificador do oráculo de não vazamento; a versão continua não derivável e a lacuna segue com as tarefas de reconciliação |
| `ENG-002` | Corrigido. Ramo próprio, com evento e código de erro separados da indisponibilidade real. Não há descarte porque não há geração para nomear, e isso está escrito no código |
| `SEC-001` | Encaminhado com a costura entregue. As ações que a credencial precisa hoje estão escritas junto ao adaptador, e o adaptador passou a receber um cliente próprio para remoção, hoje a mesma instância. Custo: um parâmetro de construtor. A interface e os chamadores não mudaram. Dono: `dotnet-architect` com o dono de infraestrutura, sob o gate de identidade e proteção do objeto sob custódia |
| `STK-002` | Corrigido. Igualdade e código de espalhamento sobrescritos, delegando à comparação de tempo fixo. O tipo passou a ter uma comparação só |
| `SEC-002` | Corrigido. Devolução ao pool com limpeza, e o comentário registra que a janela é o vetor inteiro e não o tamanho pedido. Oráculo com sentinela e varredura dos aluguéis seguintes |
| `ARC-001` | Corrigido. A reidratação passa pela mesma validação e falha alto, e a coluna de versão ganhou restrição de não vazio. Oráculos de unidade e de integração, este último afirmando a violação de restrição pelo nome |
| `PRF-001` | Encaminhado com números remedidos. A inclinação foi reproduzida por caminho independente: 1,446 para um mebibyte e 1,394 para quatro, com inclinação de 1,377 byte alocado por byte lido entre os dois pontos, e o braço contra fluxo local constante nos dois tamanhos, o que prende a inclinação ao fluxo do provedor e não ao laço. Escrito no código, com a extrapolação ao teto marcada como extrapolação |
| `PRF-002` | Encaminhado. Escrito ao lado da chamada: a segunda passagem dobra o tráfego e nenhum orçamento, portão ou limite de concorrência a descreve. Nenhum número inventado. Dono: o construtor do preflight |
| `TST-001` | Corrigido pela segunda opção: asserção sobre os metadados do modelo de que toda propriedade fora da chave tem o comportamento de somente leitura, mais a contagem exata como arame de contaminação. O falsificador deixa o oráculo novo vermelho e o antigo verde, que é literalmente o achado |
| `ARC-002` | Corrigido. Conjunto de colunas do agregado congelado literalmente, mais a afirmação de que nenhuma tabela fora da linha de geração mapeia digest, por tipo, por nome e por nome de coluna. O digest entrou na lista de proibidos em hexadecimal maiúsculo, minúsculo e base64 |
| `ENG-003` | Corrigido. Os dois comentários nomeiam as quatro formas medidas, incluindo as duas que a persistência não recusa, e dizem que a guarda que as cobre mora no banco e ainda não existe. Sem oráculo, porque é texto, e declarado como tal |
| `TST-002` | Corrigido. Auxiliares parametrizados por bucket, e a asserção passou a afirmar o que fica: exatamente uma versão, não marcador, sob a chave derivada, com conteúdo legível. O falsificador é a correção plausível de apagar sem versão o objeto que não pôde ser nomeado |
| `TST-003` | Corrigido. As duas asserções passaram a afirmar a propriedade, com o token do chamador cancelado antes de asserir, em vez do mecanismo |
| `PRF-003` | Corrigido. Orçamento de compensação com prazo próprio, e tempo limite, tempo de conexão, limite de retentativa e modo fixados na construção do cliente, com o motivo escrito |
| `ENG-004` | Corrigido. A compensação é decidida dentro e executada fora do bloco protegido, com relançamento que preserva a exceção original e captura própria na compensação. O oráculo afirma o código de status e a contagem de descartes, porque o descarte duplo era a outra metade do defeito |
| `ENG-005` | Corrigido. A leitura devolve estado em vez de nulo, e ausência logo após escrita confirmada virou evento de integridade com código próprio |
| `ENG-006` | Corrigido. Regra decidida uma vez: a referência opaca é publicável em log, a coordenada de armazenamento não. Aplicada nos dois eventos, e a asserção que congelava a contradição foi invertida |
| `ENG-007` | Alinhado ao irmão, com o motivo escrito. Declarado sem falsificador de runtime possível e não promovido a oráculo |
| `STK-003` | Corrigido, e a evidência do achado estava incompleta. Medido que o cliente rebobina e reenvia o fluxo, então um total corrido esconde a falta e o contador precisa reiniciar no reposicionamento. A exceção real é de requisição, e não de entrada e saída como o achado dizia |
| `STK-004` | **Improcedente por medição.** A tabela do adaptador cede explicitamente à configuração local do editor, que declara a preferência oposta como aviso, com aviso tratado como erro. Aplicar a prescrição da tabela nas três posições reprova o build. Nada mudou nessas linhas |
| `PRF-004` | Corrigido. O envoltório foi renomeado pelo que faz, e a contagem ganhou leitor, porque é ela que sustenta `STK-003` |
| `TST-004` | Corrigido. As asserções que não podiam falhar foram removidas junto com a consulta que as alimentava, e o teste foi renomeado para o que ele prova. Na unidade, a inclusão dentro de uma cadeia de dez caracteres virou igualdade exata da renderização |

### Achados que a correção produziu, e o que foi feito com eles

1. **Uma guarda de outro módulo é acoplada por nome nu.** Um teste de contenção varre todo o código pelo literal de um nome de método e exige zero chamadores. O nome escolhido na correção colidiu. A guarda dos outros módulos não foi enfraquecida; o método novo foi renomeado. A fragilidade permanece e vai disparar de novo.
2. **Uma falha intermitente fora do módulo era da correção, e não do ambiente.** Três testes de banda de alocação e de tempo falharam uma vez cada em oito execuções, nunca o mesmo. Comparação pareada isolou a causa nos testes novos, por pressão de agendamento e promoção de camada, e não por contaminação de medida. A pegada foi reduzida e o braço voltou a zero falhas em oito.
3. **Duas mutações de falsificação foram descartadas por serem pegas pelo compilador** antes do teste rodar, e as execuções contaminadas por elas rodaram com binário velho e reportaram verde. Foram refeitas em forma que compila, e as execuções contaminadas não contam.
4. **Onze textos públicos de detalhe foram convertidos de inglês para português.** O mapa existe só neste módulo, portanto a conversão é autocontida e segue a restrição publicada da fatia de que mensagens públicas são em português brasileiro.
5. **Uma falha no meio do fluxo da passagem de verificação escapa da captura do adaptador**, porque a abertura é coberta e a leitura do corpo não. É anterior à correção e não está entre os 23. Registrado, não tratado.
