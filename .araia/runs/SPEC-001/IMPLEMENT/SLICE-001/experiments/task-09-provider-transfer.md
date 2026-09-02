# Comparação dos métodos de transferência ao provedor

## Resultado

| Campo | Resultado |
|---|---|
| Desenho | `ACCEPTED` |
| Promoção produtiva | `streaming`, promovido sob o envelope e o alvo ratificados |
| Candidato | `streaming`, ancorado no teto afim de alocação |
| Braço de contenção | `spool`, mantido na sonda e não construído como caminho produtivo |

O método `streaming` é promovido sob o envelope de produto e o alvo de implantação ratificados. A promoção se apoia no teto afim de alocação, medido no piso e no máximo com separação de 28 vezes: o custo do streaming é constante no anexo, entre 19.149 e 35.642 bytes por envio, enquanto o da bufferização é o próprio anexo, indo de 642.847 a 20.809.486 na mesma faixa.

As duas seções seguintes, sobre a matriz sintética e o limite da comparação, descrevem a sonda anterior à equivalência funcional e ficam preservadas como registro histórico. A medição que sustenta a decisão está na seção da matriz executada sob as entradas ratificadas.

## Matriz sintética anterior, registro histórico

| Perfil não produtivo | Métrica | Buffer | Streaming | Spool |
|---|---|---:|---:|---:|
| 1 MiB, concorrência 1 | p95 | 1,690 ms | 1,068 ms | 21,666 ms |
| 1 MiB, concorrência 1 | alocação/operação | 1.050.538 B | 500 B | 132.608 B |
| 1 MiB, concorrência 1 | vazão | 542,44 MiB/s | 1.024,29 MiB/s | 51,19 MiB/s |
| 4 MiB, concorrência 4 | p95 | 6,506 ms | 4,436 ms | 57,916 ms |
| 4 MiB, concorrência 4 | alocação/operação | 4.198.009 B | 536 B | 132.770 B |
| 4 MiB, concorrência 4 | vazão | 1.802,35 MiB/s | 3.586,55 MiB/s | 357,65 MiB/s |
| 16 MiB, concorrência 2 | p95 | 19,610 ms | 16,048 ms | 208,623 ms |
| 16 MiB, concorrência 2 | alocação/operação | 16.780.542 B | 596 B | 132.800 B |
| 16 MiB, concorrência 2 | vazão | 1.556,66 MiB/s | 2.011,54 MiB/s | 318,95 MiB/s |

Todos os braços produziram o mesmo digest. O braço `spool` terminou cada rodada com zero arquivos e zero raízes residuais. A vazão representa hash e cópia em memória, não a vazão de envio ao provedor.

Artefatos medidos:

- `task-09-small-c1-report.json`, SHA-256 `70E0A30FECE736BBBAAC085E1F61F1FE4A2BB020BE5818DE75C37D3B3691F89E`.
- `task-09-medium-c4-report.json`, SHA-256 `489E3F67D4B84D72FC711298D2C831A1E44287482C9CE63D4B5BA779F5366127`.
- `task-09-large-c2-report.json`, SHA-256 `61116E2E6CC56B2F63B2217B2ADD58ACA2D66AFE28A0A29E01931185156EEDC2`.

Sondas adicionais executadas pelo orquestrador confirmaram a tendência em perfis de 1 MiB com concorrência 4 e 16 MiB com concorrência 4. Elas permanecem evidência de sensibilidade, não linhas de base aprovadas.

## Cancelamento e limpeza

Uma rodada de 128 MiB, 100 operações e concorrência 2 foi cancelada cooperativamente durante o braço `spool`.

Resultado observado:

- `Ctrl+C` propagou `OperationCanceledException` e o processo terminou com código `1`;
- a busca posterior encontrou zero raízes `notification-hub-attachment-transfer-*`;
- no Windows, a remoção da raiz comprova também que nenhum handle do arquivo permaneceu aberto.

A prova cobre cancelamento cooperativo. Ela não cobre encerramento abrupto, perda de processo ou recuperação após reinício. Uma alternativa produtiva com spool exigiria diretório protegido, controle de propriedade, cota e varredura de resíduos na inicialização.

## Limite da comparação sintética, superado

O cenário atual:

- cria todo o payload como `byte[]` antes da medição;
- exclui o custo de leitura S3;
- faz `buffer` copiar para um `MemoryStream`;
- faz `streaming` apenas alimentar `IncrementalHash`;
- faz `spool` gravar bytes crus, reler o arquivo e calcular o hash;
- usa envelope sintético;
- não executa sob a configuração Server GC da API;
- calcula percentis sobre poucas observações.

Os braços não realizam trabalho funcional equivalente. Em especial, a forma Mail Send requer o conteúdo do anexo como string base64 dentro do JSON. Uma representação base64 ocupa `4 × ceil(bytes/3)` antes do restante do envelope. Assim, 4 MiB crus exigem 5.592.408 bytes e 16 MiB crus exigem 22.369.624 bytes somente para a base64. Esse custo não aparece nas medições atuais.

## Condições para promoção, situação final

A decisão produtiva exige, antes da promoção:

1. quantidade e tamanho total aprovados por notificação;
2. concorrência e taxa oferecida por réplica;
3. orçamentos numéricos aprovados para heap, working set, CPU, Gen2, p95, p99, rede e disco;
4. braço streaming funcional com S3, base64 incremental, JSON integral e `HttpContent` cancelável;
5. servidor SendGrid falso que capture o corpo e compare digest, comprimento, nome, tipo e ordem;
6. backpressure e cancelamento injetados na leitura S3, codificação e escrita HTTP;
7. execução sob Server GC;
8. decisão comprovada sobre `Content-Length` ou transferência chunked;
9. ensaio de crash e recuperação se `spool` continuar como alternativa.

Situação final das nove condições: os itens 1 a 3 foram ratificados pelo dono da decisão; os itens 4 a 7 foram implementados e provados por mutação; o item 8 está decidido em comprimento exato antecipado; e o item 9 deixou de ser condição de comparação, porque o `spool` não é candidato à promoção, e passou a ser condição de ativação caso ele venha a ser usado como caminho de contenção.

O portão mede os três métodos com o mesmo trabalho funcional e promoveu a única alternativa cujo custo é constante no tamanho do anexo. `buffer` não serve como alternativa automática: ele reprova o teto afim nos quatro perfis e nas duas concorrências.

## Mesa redonda de envelope, orçamento e método

A mesa está registrada em `task-09-round-table-transfer-budget.md`, com abertura, as duas posições independentes e a síntese. Veredito por decisão:

| Decisão | Veredito | Alternativa líder |
|---|---|---|
| Envelope | `INSUFFICIENT-EVIDENCE` no todo, eixo de tamanho fechado | Teto duro de 22.423.200 bytes de conteúdo cru somado, na leitura conservadora de 30.000.000 bytes com reserva de 100 KiB |
| Orçamento de runtime | `RECOMMEND` a forma, `INSUFFICIENT-EVIDENCE` a constante | Teto afim, com termo constante e coeficiente por tamanho, verificado em dois tamanhos separados por fator cinco |
| `spool` como caminho produtivo | `RECOMMEND` não construir | Consenso das duas partes |
| `spool` como braço da sonda | `RECOMMEND` preservar | Com dissenso do engenheiro registrado |
| `Content-Length` | `RECOMMEND` comprimento exato antecipado | Independe da versão de HTTP negociada |

### O que a mesa mediu e que muda o desenho

O portão vigente possui 36 verificações, das quais 23 não podem reprovar em nenhuma rodada que o cenário consiga produzir. As identidades aritméticas explicam 14, a concorrência observada explica mais 3, e as 6 restantes só aparecem lendo o portão junto com o cenário: a contagem de braços é barrada por dois guardas anteriores, a remoção da raiz temporária nunca reporta falso porque a rotina lança quando o diretório resiste, e os arquivos residuais são verificados em dois braços que não possuem raiz. Sobram 13 verificações capazes, das quais apenas 8 observam comportamento em vez de configuração.

As chamadas de base64 do escritor JSON não passam pelo codificador de escape, então o campo mede exatamente `4 × ceil(n/3) + 2` sob qualquer codificador. A decisão do comprimento não é sobre codificador, é sobre qual chamada de escrita. Escrito como string comum sob o codificador padrão, conteúdo escolhido pelo remetente expande até seis vezes: o padrão `FB EF BE` repetido produz base64 inteiramente escapável, e 3.750.000 bytes crus atingem exatamente o teto de 30.000.000. É negação de serviço acionável por conteúdo de remetente, e desaparece pela escolha da chamada.

O corpus atual não contém nenhum caractere escapável em 5.592.408 caracteres. Sem um perfil adversário, as verificações de teto de corpo, comprimento declarado e aritmética do campo nascem verdes com qualquer das duas chamadas, inclusive a explorável. O perfil adversário é obrigatório.

Um teto de alocação de valor único não sobrevive à evidência: um escritor JSON sobre fluxo sem descarga por segmento aloca 66 MB por operação num braço chamado streaming, contra 118 KB com descarga, produzindo o mesmo corpo e o mesmo digest. Uma razão de invariância também não basta sozinha, porque razão 1,0 aprova uma implementação que aloque 66 MB nos dois tamanhos. Só a forma afim, verificada em dois tamanhos, falsifica as duas.

O número de heaps do Server GC deriva da cota de CPU visível ao runtime. Medindo com dois, quatro e vinte e dois heaps, o percentil 95 varia 1,85 vezes. Portanto a baseline não exige apenas o modo do coletor, exige a contagem de heaps do alvo, que precisa ser pinada porque nenhuma superfície pública devolve a contagem efetiva derivada da máquina.

Com o estimador do próprio portão, o percentil 95 deixa de ser o máximo em vinte amostras e o percentil 99 em cem, mas a dispersão só cai abaixo da tolerância vigente em quinhentas amostras. Estabilizar o percentil 99 exigiria mil, que é exatamente o limite configurável. A grandeza honesta a restringir no lugar é o máximo observado, nomeado como máximo.

## Harness funcional

O modo `provider-transfer` foi acrescentado ao runner, separado do modo sintético para não tocar na linha de base versionada daquele. Ele fecha os itens 4, 5, 6 e 7 das condições de promoção.

O duplo do provedor é um servidor em processo que não materializa o corpo: cada byte alimenta o digest e cada valor de string acima de 4 KiB é decodificado de base64 na passagem, em quartetos com carry, de modo que o volume de JSON a conferir independe do tamanho do anexo. Ele devolve digest, comprimento, nome, tipo, disposição e ordem de cada anexo, mais contagem de chamadas e o comprimento declarado. Sabe simular 400, 429 com cabeçalho de espera, 500, estol até o timeout e queda de conexão. O teto de corpo é o teto documentado do provedor, então uma mensagem acima dele é recusada ali pelo mesmo motivo que seria recusada lá.

A fonte de bytes fica atrás de interface, com fluxo apenas para frente, sem comprimento e sem busca, que é o que uma leitura remota oferece, com tamanho de bloco e latência configuráveis. Não há acoplamento com `AttachmentManagement` nem com o SDK do provedor de nuvem.

Os três braços leem da fonte, codificam base64, montam o JSON completo e enviam por conteúdo HTTP cancelável. O braço de bufferização serializa pelo caminho do serializador, e os outros dois escrevem por um layout recortado. As duas composições são independentes de propósito, para que a igualdade de digest medida pelo duplo seja oráculo real e não uma réplica comparada consigo mesma.

A equivalência funcional é provada pelo destinatário, não pelo remetente. Numa rodada em Release, o corpo mediu 1.398.619 bytes com o mesmo digest nos três braços e o anexo decodificado batendo com a fonte nos três. Um teste separado compara byte a byte o envelope da sonda contra o que o provedor real serializa, o que impede a sonda de medir uma mensagem que este hub não envia.

O cancelamento foi provado nas nove combinações de três braços por três estágios, mais um caso de falha de leitura. Cada caso exige a exceção de cancelamento e então zero fluxos abertos, ao menos um fluxo tendo sido aberto, diretório temporário vazio, remoção bem-sucedida e zero chamadas capturadas pelo duplo.

Seis mutações dirigidas foram aplicadas, medidas e revertidas. Cinco reprovaram os oráculos correspondentes. A sexta, remoção do tratamento de escape no scanner, voltou verde e revelou uma lacuna real: o filtro reconstruía os mesmos bytes lendo a aspa como delimitador ou como conteúdo, então a asserção não falsificava aquele tratamento. Uma amostra em que os dois entendimentos divergem foi acrescentada e a mutação passou a reprovar.

Validação executada: a solução compila sem avisos; 47 de 47 testes novos passam; as suítes de arquitetura, segurança e unidade passam integralmente; o modo novo roda com código zero nas duas codificações de transferência e recusa medir fora do Server GC com código 2.

## Decisões tomadas sob delegação

A leitura conservadora do teto do provedor é adotada, com 30.000.000 bytes de mensagem e reserva de 100 KiB, resultando em 22.423.200 bytes de conteúdo cru somado. É a leitura segura entre as duas possíveis e já é a usada pelo duplo.

O braço `spool` permanece na sonda como caminho de contenção, conforme a síntese, com o dissenso do engenheiro preservado. Ele não será construído como caminho produtivo promovível, o que é consenso das duas partes.

Nenhum método é promovido e nenhuma linha de base é gravada para o modo novo. O runner já recusa gravá-la, com código 2 e mensagem dizendo que os orçamentos não estão ratificados.

O custo de tempo da matriz é aceito: duzentas amostras custam cerca de 5,5 segundos no perfil de 4 MiB e 21 segundos no de 16 MiB, e a matriz inteira fica abaixo de dois minutos.

## Consequência a tratar fora desta tarefa

O Server GC passou a valer para o projeto de desempenho inteiro, e o mesmo executável hospeda cinco linhas de base. Quatro cenários sem relação com anexos passam a ser comparados sob outro coletor. Uma comparação alternada foi executada e o resultado foi inconclusivo, porque a máquina produz valores atípicos de cinco a dez vezes sob os dois coletores e as medianas ficaram comparáveis. Os portões passaram sob os dois modos e nenhuma linha de base foi alterada. Ainda assim, as quatro referências precisam ser regravadas sob o coletor novo antes que os portões delas voltem a ser confiáveis, e isso pertence a quem é dono desses cenários.

## Entradas ratificadas

Ratificadas pelo dono da decisão em 2026-09-02, o que fecha as duas lacunas que impediam a promoção.

| Entrada | Valor ratificado | Consequência |
|---|---|---|
| Anexos por notificação | no máximo 5 | Perfil de fragmentação da matriz usa 5 anexos de 1,4 MiB |
| Tamanho total cru por notificação | 7 MiB, isto é 7.340.032 bytes | Codificado dá 9.786.712 bytes, abaixo dos 10 MB recomendados pelo provedor nas duas leituras, e deixa cerca de 20 MB de folga contra o teto duro |
| Memória do contêiner por réplica | 2 GB | Orçamento do caminho de transferência derivado em 10 por cento, isto é 209.715.200 bytes por réplica |
| Cota de CPU por réplica | 1 vCPU | Contagem de heaps do Server GC pinada em 1 e gravada na linha de base, porque a contagem efetiva não é observável por superfície pública |
| Réplicas | 2 | Concorrência agregada contra o provedor de 16, muito abaixo do limite dele de 10.000 requisições por segundo |
| Envios em voo por réplica | 8, mantendo o valor já presente na configuração | Teto absoluto por envio derivado em 26.214.400 bytes |
| Taxa oferecida | a própria concorrência, em circuito fechado | Não é entrada independente numa sonda de capacidade |

### O que os números ratificados decidem sozinhos

No envelope de 7 MiB, a amplificação medida de 9,33 vezes coloca o braço de bufferização em cerca de 65 MiB por envio em voo. Com 8 em voo, são cerca de 523 MiB por réplica somente para transferir anexos, ou seja um quarto do contêiner de 2 GB e duas vezes e meia o orçamento derivado de 10 por cento. O braço streaming custa cerca de 116 KB por envio e é constante no tamanho, o que dá menos de 1 MB por réplica.

A bufferização deixa de ser uma alternativa cara e passa a ser inviável no alvo escolhido. Isso é afirmação falsificável, e o portão deve reprová-la; se ela passar, a verificação está fraca e não o braço está bom.

### Perfis da matriz sob o envelope ratificado

| Perfil | Anexos | Cru por anexo | Cru total | Papel |
|---|---:|---:|---:|---|
| Piso | 1 | 256 KiB | 262.144 | Termo constante da forma afim |
| Máximo por anexo único | 1 | 7 MiB | 7.340.032 | Coeficiente da forma afim, com separação de 28 vezes contra o piso |
| Fragmentação | 5 | 1,4 MiB | 7.340.032 | Separa custo por item de custo por byte, no mesmo total |
| Adversário | 1 | 7 MiB do padrão `FB EF BE` | 7.340.032 | Único perfil que separa a chamada de escrita correta da explorável |

Concorrência 1 e 8. Mínimo de 200 amostras por braço, e 500 onde o tempo permitir. O percentil 99 não é reportado abaixo de mil amostras; no lugar dele vale o máximo observado, nomeado como máximo.

## Matriz executada sob as entradas ratificadas

Três braços, quatro perfis, duas concorrências, oito células, duzentas amostras por braço em cada célula. Server GC com contagem de heaps pinada em 1, correspondendo ao contêiner de 1 vCPU. O percentil 99 não é reportado; no lugar dele vale o maior valor observado, nomeado como máximo.

| Perfil | Braço | Alocação por envio | Teto afim | p50 | p95 | Máximo | Geração 2 | Pausa de coleta |
|---|---|---:|---|---:|---:|---:|---:|---:|
| Piso, concorrência 8 | buffer | 691.794 | reprova | 5,03 | 16,07 | 31,90 | 7 | 23,15 |
| Piso, concorrência 8 | streaming | 26.166 | passa | 4,45 | 5,35 | 5,94 | 0 | 0,00 |
| Piso, concorrência 8 | spool | 86.772 | passa | 20,71 | 33,64 | 36,61 | 0 | 7,76 |
| Máximo único, concorrência 8 | buffer | 18.952.027 | reprova | 354,99 | 467,41 | 548,03 | 9 | 105,12 |
| Máximo único, concorrência 8 | streaming | 29.408 | passa | 222,35 | 288,53 | 325,00 | 0 | 0,00 |
| Máximo único, concorrência 8 | spool | 96.658 | passa | 487,76 | 585,16 | 662,40 | 0 | 10,75 |
| Fragmentado, concorrência 8 | buffer | 20.809.486 | reprova | 349,67 | 496,28 | 581,11 | 7 | 64,88 |
| Fragmentado, concorrência 8 | streaming | 35.642 | passa | 226,30 | 272,36 | 317,01 | 0 | 0,00 |
| Adversário, concorrência 8 | buffer | 18.952.265 | reprova | 341,21 | 494,75 | 541,79 | 9 | 95,02 |
| Adversário, concorrência 8 | streaming | 31.712 | passa | 237,12 | 352,29 | 397,67 | 0 | 0,00 |

O corpo do perfil adversário mede 9.787.227 bytes, idêntico ao do perfil de máximo único: o comprimento do campo deixou de depender do conteúdo.

### O perfil adversário provou ser obrigatório

Com o conversor trocado pela chamada explorável, que serializa a base64 como string comum sob o codificador padrão, o corpus legível produziu **todas as verificações de corpo verdes**, nenhuma reprovação. O mesmo binário sob o corpus adversário reprovou três verificações, com o corpo medindo 8.389.124 bytes contra 1.398.619 esperados. Sem o perfil adversário, a chamada explorável seria invisível ao portão. A expansão foi medida e é exatamente seis vezes: 32 caracteres de base64 custam 34 bytes pela chamada correta e 194 pela explorável.

### Achado que contradiz a expectativa ratificada

O braço de bufferização **passa** no teto absoluto por envio, medindo 18.952.027 bytes contra os 26.214.400 derivados, isto é 72 por cento do orçamento. A expectativa registrada era que ele reprovasse. O teto não foi ajustado para produzir a reprovação esperada, e a causa foi medida: corrigir a chamada de escrita, que fecha o vetor de expansão por conteúdo de remetente, elimina também a string intermediária de base64 de 19,6 MB, e com isso a amplificação da bufferização cai de 9,33 para 2,58. As duas decisões interagem, e a correção de segurança foi o que tornou o orçamento de memória sobrevivível para a bufferização.

O que separa os braços é o **teto afim**, lido no piso e no máximo, com separação de 28 vezes, e a bufferização o reprova nos quatro perfis e nas duas concorrências. Este é exatamente o caso que a mesa previu que um teto de valor único deixaria passar.

### Duas premissas da mesa foram falsificadas pela medição

Três das sete razões contra linha de base não são passíveis de portão nesta bancada. Em cinco execuções isoladas da mesma célula, a vazão variou 19,6 vezes, o maior valor observado variou 67,7 vezes e o pico de heap variou 8,1 vezes. Uma banda que admita essas execuções não recusa regressão alguma. O pico de working set é estável, com variação de 1,20 vezes, mas foi excluído pelo motivo oposto: execuções saudáveis já alcançam 1,06 do braço de bufferização, portanto ele não separa um candidato que tenha retido a mensagem inteira. Só a razão de alocação sobrevive, com tolerância de 0,75 derivada da dispersão medida de 1,56 e três ordens de grandeza abaixo da assinatura de falha. As três razões que não recebem portão são registradas no relatório como medidas e não graduadas.

A premissa de que nenhuma superfície pública devolve a contagem efetiva de heaps é falsa neste runtime. A configuração do coletor devolve a contagem efetiva: 22 sem pino nesta máquina, 4 sob a variável de ambiente e 1 sob o pino de projeto. O pino continua necessário para reprodutibilidade e é o que dá à verificação um caminho de reprovação alcançável, mas a justificativa registrada na mesa não se sustenta.

O contador de alocação é do processo inteiro. No hospedeiro de testes em paralelo, o braço streaming reportou entre 336 KB e 5,3 MB por envio contra cerca de 20 KB isolado. As classes de teste passaram a rodar sem paralelização.

### Cada verificação nova foi provada por mutação

Dezesseis mutações em tempo de execução, uma por verificação, todas reprovando a verificação correspondente e apenas ela. Entre elas: exceder a quantidade de anexos, exceder o total de bytes crus, reduzir as amostras abaixo do mínimo, desligar o Server GC, mudar a contagem de heaps, a chamada de escrita explorável sob o corpus adversário, reduzir o paralelismo real, e trocar a alocação do candidato pela do braço de bufferização. Cada mutação de código-fonte foi revertida e a reversão verificada por comparação byte a byte contra uma cópia anterior.

Duas verificações foram removidas com evidência. A de braços exigidos na rodada é barrada por dois guardas anteriores e nunca alcança vermelho. A de comprimento declarado contra bytes recebidos é imposta pelo transporte: declarar um byte a menos não produz linha vermelha, produz uma execução sem chamada capturada. A afirmação que ela carregava passou para a verificação de corpo contra a aritmética do campo, que é falsificável sob o perfil adversário.

### Método promovido

**`streaming`**, ancorado no teto afim e não na ordenação de latência.

O teto absoluto por envio não decide, porque a bufferização cabe nele no envelope ratificado. O que decide é a forma do custo: o streaming aloca entre 19.149 e 35.642 bytes por envio ao longo de uma faixa de 28 vezes de conteúdo, portanto seu custo é constante no anexo; a bufferização vai de 642.847 a 20.809.486 na mesma faixa, portanto seu custo é o anexo. No teto do provedor, em vez do envelope de produto, a bufferização custa 40.363.884 bytes por envio, ou 1,54 vezes o orçamento inteiro. O envelope é decisão de produto e pode mudar; a forma do custo não.

O streaming também mantém zero coleções de geração 2 e zero pausa de coleta em todas as vinte e nove execuções, contra 7 a 31 coleções e 20 a 105 milissegundos da bufferização, e lidera em latência e vazão nas oito células.

O `spool` permanece como braço de contenção, conforme ratificado. Ele iguala o streaming em alocação e em geração 2, mas paga de 1,9 a 18 milissegundos de pausa e perde em latência em sete das oito células, mais pesadamente na concorrência 8.

### Linha de base

Gravada em formato 1, com oito células, cada uma a mediana de três execuções isoladas, num total de vinte e quatro invocações de processo, por um comando que não mede nada e nunca compara. Os recibos ficam no registro de gravação, com caminho do relatório, perfil, concorrência, carimbo de tempo e digest. Server GC com contagem de heaps 1. O arquivo guarda razões e configuração, e nenhum teto: os tetos vivem como constante no código do portão, porque o arquivo de referência é reescrito pelo próprio comando de regravação.

## Recibo arquitetural

| Item | Decisão |
|---|---|
| Método promovido para produção | `streaming` |
| Fundamento da promoção | Teto afim de alocação, lido no piso e no máximo com separação de 28 vezes. O custo do streaming é constante no anexo; o da bufferização é o anexo |
| Braço de contenção | `spool`, mantido na sonda, não construído como caminho produtivo |
| Chamada de escrita do base64 | Fixada na forma que não passa pelo codificador de escape, o que fecha o vetor de expansão de seis vezes por conteúdo de remetente |
| `Content-Length` | Exato e antecipado, independente da versão de HTTP negociada |
| Perfil adversário | Obrigatório e implementado. Sem ele a chamada explorável passa com todas as verificações verdes |
| Evidência suficiente para limpeza cooperativa | Sim, provada nas nove combinações de braço e estágio |
| Evidência suficiente para envio real | Sim. Os braços fazem trabalho equivalente, a igualdade é provada pelo destinatário e o orçamento está ratificado |
| Consumidores liberados | Tarefas 29 e 30, e por transitividade as tarefas 32 a 34 |

## Pendências herdadas por outras tarefas

O adaptador de correio de produção ainda constrói o corpo por um caminho que usa o codificador padrão. O módulo não carrega anexos hoje, portanto o vetor não está vivo, mas ele passa a estar no dia em que carregar. As tarefas 29 e 30 devem adotar a mesma chamada de escrita usada pela sonda, e essa é uma condição de aceitação delas, não uma sugestão.

O pino de contagem de heaps é propriedade de projeto e alcança as cinco linhas de base do mesmo executável. A comparação alternada mostrou que o portão de memoização está vermelho sob o pino, sob a contagem alta e também sob o coletor com que a sua referência foi gravada, portanto ele já estava vermelho antes desta mudança. O portão de análise passa nos dois modos anteriores e reprovou uma de duas rodadas sob o pino, ficando no próprio limite. Regravar essas referências pertence aos donos daqueles cenários.
