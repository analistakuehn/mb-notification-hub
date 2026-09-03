---
language: pt-BR
---

# Tarefa 31: roteamento e fallback com terminação explícita

Registro da construção, das medições e do que ficou sem medida. Executado em
2026-09-03 sobre a árvore limpa em `5e5981e`, sem commit.

## A forma entregue

### Quem responde se o canal transporta o conjunto

`IChannelProvider` ganhou `CarriesAttachments`. É membro obrigatório da
interface e não uma propriedade com valor padrão, e essa escolha é a que
carrega peso: um adaptador novo não compila sem decidir, e nenhum caminho
consegue herdar uma resposta que ninguém escreveu. SendGrid responde
verdadeiro, Twilio e FCM respondem falso, e os dois decoradores repassam a
resposta do adaptador que embrulham.

O fundamento é que a resposta é propriedade da chamada que o adaptador monta.
Uma tabela por canal guardada em qualquer outro lugar seria uma segunda
afirmação sobre esse comportamento, correta no dia em que fosse escrita e
silenciosamente errada no dia em que um canal apontasse para outro adaptador.

### A pergunta publicada

`IChannelAttachmentSupport`, em `Modules/Dispatch/Integration/V1/`, responde
por canal sobre o `IChannelProviderResolver` que já existia. A implementação
não guarda resposta nenhuma: ela encaminha a pergunta ao objeto que compõe a
chamada, então quem planeja e quem envia leem o mesmo adaptador. Falha de
resolução atravessa como falha de integração e nunca como um não.

Consequência de composição, assumida e declarada: o papel `core` passou a
compor `AddDispatchProviderSurface`, isto é, hospeda os mesmos adaptadores do
papel `dispatcher` para fazer uma pergunta e nenhum envio. Nenhuma chave nova
de configuração é necessária, porque o `appsettings` do worker já traz a seção
`Modules:Dispatch` e as três seções de provedor validam com seus valores
padrão. O custo real é outro: uma instância que rode apenas o papel `core`
agora exige a cadeia de conexão do Dispatch para subir.

### O estágio de rota recusa

`RouteStage` pergunta somente quando a linha da notificação carrega conjunto
aceito, e recusa com `StageOutcome.Reject` quando o canal do primeiro passo
não transporta. O plano não é filtrado, reordenado nem reescrito, nenhum anexo
é removido e nada vira link. Falha de resolução lança, exatamente como o
despacho já trata a mesma classe de defeito, porque uma recusa ali diria ao
produtor que a notificação foi negada por uma falha que é nossa.

### O motivo, e onde ele foi inventariado

`NotificationRejectionReasons.AttachmentsNotCarriedByChannel`, com o valor
`attachments-not-carried-by-channel`. A palavra não nomeia canal nenhum, o que
tem oráculo próprio. Serviço novo e nada em produção: não existe V2 de
contrato, membro obsoleto nem migração de compatibilidade.

Inventários de contrato publicado atualizados:

1. o próprio catálogo e o conjunto `All`;
2. a tabela de motivos do guia de integração do produtor, que o teste de
   arquitetura compara contra `All`;
3. a lista, no mesmo guia, dos motivos que não aparecem como status HTTP
   porque são decididos depois do aceite;
4. a frase da seção 2.4 do guia que afirmava que o contrato de despacho ainda
   não transporta anexos ao provedor, que esta tarefa tornou falsa.

### O fallback termina

`FallbackRequestHandler` liquida em `PipelineResult.Failed` com a mesma
palavra quando o passo seguinte do plano não transporta o conjunto, auditado,
sem enfileirar tentativa e sem caminhar adiante. O fundamento está escrito no
código: `NextUsableStep` pula passo inelegível porque elegibilidade é sobre o
destinatário, e transportar o conjunto é sobre a mensagem, então pular seria
deixar a presença de um anexo reordenar um plano publicado sem ele.

### A ligação e a guarda de última linha

`AttachmentPreflight.VerifyAsync` passou a devolver desfecho e conjunto
juntos, e o `DispatchMessageProcessor` submete esse objeto em
`DispatchRequest.Attachments`. É o mesmo objeto que o preflight verificou, e
não uma segunda leitura do documento: ler duas vezes abriria janela para a
linha mudar entre a verificação e a submissão.

Além disso, quando existe conjunto e o provedor resolvido responde que não
transporta, o processador falha a tentativa com código estável, sem chamar. A
guarda faz a propriedade valer sobre o objeto que faz a chamada, e não apenas
sobre quem planejou. O código é a mesma palavra do catálogo, alcançada por
constante em vez de reescrita, para que quem lê uma tentativa falhada ao lado
de uma notificação recusada não tenha que aprender dois nomes para um fato.

## Suítes executadas sobre a árvore limpa

| Suíte | Resultado |
|---|---|
| `dotnet build -warnaserror` | sucesso, zero avisos |
| `Platform.UnitTests` | 2083 aprovados, 0 reprovados (eram 2067; 16 novos) |
| `Platform.ArchTests` | 30 aprovados |
| `Platform.SecurityArchTests` | 14 aprovados |
| `Platform.IntegrationTests` | 983 no total, 971 aprovados, 2 pulados, 10 reprovados |

Os 2 pulados são os dois testes de fumaça contra provedor real, pulados por
desenho. Os 10 reprovados são anteriores a esta tarefa e estão medidos abaixo.

## Mutações de falsificação

Uma mutação por medição, sempre no código de produção, sempre com portão pelo
código de saída do build, sempre revertida por cópia byte a byte tirada antes
de tocar o arquivo. Nenhuma reversão usou `git checkout`, que restauraria o
arquivo commitado e destruiria a mudança sendo medida.

| Eixo mutado | Onde | Oráculos derrubados | Resultado |
|---|---|---|---|
| O adaptador de e-mail responde que não transporta | `SendGridChannelProvider` | 4: composição real de DI, o envio que carrega o conjunto, o fim do fallback e a guarda de última linha | vermelho |
| O decorador de taxa responde por si em vez de repassar | `RateLimitedChannelProvider` | 2: a teoria unitária do decorador no caso verdadeiro e a composição real de DI | vermelho |
| O decorador de concorrência inventa o outro valor | `ConcurrencyLimitedChannelProvider` | 2: a teoria unitária no caso falso e a composição real de DI, que passou a ver Twilio como transportador | vermelho |
| A rota nunca reconhece que a linha carrega conjunto | `RouteStage.CarriesASet` | 5: quatro dos cinco testes de estágio e a recusa de ponta a ponta antes de qualquer tentativa | vermelho |
| O fallback nunca reconhece que a linha carrega conjunto | `FallbackRequestHandler` | 4: o fim do fallback, os dois testes de autoridade única e o fallback do documento ilegível | vermelho |
| O fallback termina com a palavra errada | `FallbackRequestHandler` | 3: as três asserções de motivo publicado no evento de falha | vermelho |
| O processador não liga o conjunto ao pedido | `DispatchMessageProcessor` | 1: o envio que põe cada membro na chamada ao provedor | vermelho |
| A guarda de última linha perde a negação | `DispatchMessageProcessor` | 9: a guarda dedicada, o envio com conjunto e toda a suíte de preflight | vermelho |
| O preflight devolve uma cópia igual em vez do objeto verificado | `AttachmentPreflight` | 1: a identidade do conjunto entregue ao chamador | vermelho |

**Nenhuma mutação voltou verde.** A que mais importava era a sétima: sem o
oráculo de ponta a ponta que lê o corpo capturado no servidor falso, ligar o
conjunto ao envio teria ficado sem falsificador nenhum, e as recusas medidas
seriam regras sobre um caminho que não carrega nada.

A oitava e a nona merecem leitura conjunta. A oitava derruba nove testes e a
nona derruba um só, e as duas são igualmente boas: a nona fala de identidade
de objeto, e uma cópia igual passa por qualquer oráculo que compare conteúdo.
É exatamente o defeito que ela existe para pegar.

## O vermelho falso, e o que ele ensina

Nenhum oráculo voltou verde, mas um voltou vermelho sem motivo, e o incidente
vale mais registro que os nove acertos.

Depois da última reversão, com o arquivo restaurado byte a byte e o build
reportando sucesso, a suíte unitária acusou **exatamente** o teste que a
última mutação derruba. A causa: a reversão restaura a cópia preservando o
carimbo de tempo original do arquivo, que passa a ser mais **antigo** que o
binário compilado sob a mutação. O MSBuild compara carimbos, conclui que a
saída está em dia e **não recompila**, e o build informa sucesso sem ter feito
nada. A execução seguinte mediu o binário mutado.

A regra da casa manda não usar `--no-build` depois de reverter, e ela foi
seguida: houve build. A forma forte da regra é outra: **o código de saída do
build não prova recompilação**. O que prova é o carimbo do binário de saída
ser posterior ao da fonte revertida. O sintoma que denunciou o caso foi a
coincidência exata entre o único teste vermelho e o alvo da última mutação.

Nenhuma das nove medições foi afetada, porque cada aplicação de mutação
escreve o arquivo com carimbo novo e força a compilação. O que ficou
comprometido foi apenas a verificação final da árvore limpa, refeita depois de
tocar os arquivos revertidos e reconstruir de verdade: 2083 aprovados.

## As dez reprovações pré-existentes, medidas e não argumentadas

As dez reprovações da suíte de integração foram reproduzidas em worktree
separado no commit `5e5981e`, sem nenhuma linha desta tarefa:

- nove em `DeliveryReconciliationTests`, todas com a mesma assinatura,
  `23514: no partition of relation "notification" found for row`. A fixture
  fixa o relógio em 2026-08-25 e semeia linhas nessa data, e a data corrente
  da execução é 2026-09-03. Reproduzido no worktree: 9 de 9 reprovadas;
- uma em `AuditReconstructionTests`, com a mesma assinatura em ambos os lados:
  a republicação da versão 2 é recusada com `422` porque descarta a variável
  sensível declarada pela versão em vigor. Reproduzido no worktree: 1 de 10
  reprovada, a mesma.

## Achado fora do escopo, corrigido pelo orquestrador

`.gitignore:6` contém `[Rr]elease/`, e esse padrão ignora um diretório de
código de produção: `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Release/`,
com `RecordedAttachmentReleaseCheck.cs` e o companheiro de log. Os dois
arquivos existem na árvore de trabalho e **não estão no git**. A consequência
foi medida por acidente: o worktree criado para o A/B não compilou, com
`CS0234` sobre o namespace `Infrastructure.Release`, e só compilou depois de
copiar os dois arquivos ignorados para dentro dele. Um clone limpo não
constrói este repositório.

O construtor não corrigiu, e fez bem em parar: a saída é decisão de
configuração do repositório. O orquestrador corrigiu, depois de medir qual
das saídas era a certa.

As duas linhas `[Dd]ebug/` e `[Rr]elease/` foram **removidas**, e não negadas
caso a caso. A medição que decidiu isso: toda pasta chamada `Debug` ou
`Release` neste repositório está dentro de `bin/` ou de `obj/`, que as duas
linhas acima delas já ignoram, com uma única exceção, que é justamente o
diretório de código de produção. Os dois padrões portanto não protegiam nada e
só faziam dano. Removidas, o `git status` passa a enxergar os dois arquivos
perdidos e **zero** saída de build vaza, o que foi conferido contando as
entradas sob `bin/` e `obj/` antes de confiar.

Uma negação teria consertado este caso e deixado a armadilha armada para o
próximo diretório que alguém chamasse de `Release`.

## O que os oráculos não provam

- **Nada sobre provedor real.** Os dois testes de fumaça seguem pulados por
  desenho, e todo o resto mede a chamada composta contra um servidor falso em
  processo. Que o destinatário receba o arquivo continua sem medida.
- **Nada sobre os bytes exatos no oráculo de ponta a ponta.** Ele compara, por
  membro, nome de arquivo, tipo de mídia e comprimento do conteúdo
  decodificado. A identidade dos bytes é medida em outro lugar, pela
  testemunha que o adaptador registra na mesma passagem que os escreve, e a
  afirmação completa é a composição das duas.
- **O documento ilegível não é pergunta do estágio de rota.** Existe teste que
  fixa essa fronteira, e ele fixa a fronteira em vez de fechá-la: o que impede
  uma notificação de sair sobre um conjunto que ninguém consegue nomear é o
  despacho, que recusa antes do claim. Uma linha corrompida hoje gasta uma
  tentativa enfileirada antes de encontrar essa recusa.
- **A recusa do fallback não diz nada sobre a elegibilidade do passo
  seguinte.** A ordem das perguntas coloca consentimento e supressão antes,
  então um passo inelegível já teria terminado com outro motivo.
- **A guarda de última linha é medida por substituição de adaptador no
  container**, e não por uma reconfiguração real de provedor em voo. O que se
  prova é que a guarda decide sobre o objeto que faz a chamada; a corrida
  entre uma mudança de configuração e um envio em andamento continua sem
  medida.
- **A regra do decorador cobre os dois decoradores que existem hoje.** Um
  terceiro decorador que esquecesse de repassar seria pego, mas por um caminho
  indireto: o teste de composição afirma que o container entrega
  `RateLimitedChannelProvider`, e um invólucro novo por fora falharia ali
  antes de falhar por causa da resposta.
- **Nada mede o custo do papel `core` hospedar adaptadores.** Nem memória, nem
  tempo de boot, nem o que acontece quando as três seções de provedor estão
  ausentes em uma instância que só roteia.
- **O motivo é medido no evento publicado e na trilha de auditoria.** A
  consulta REST do histórico com esse motivo não foi exercitada.
- **A cobertura de fan-out sobre notificação com conjunto foi perdida, não
  transferida.** Está detalhado abaixo.

## Decisões que tomei e não estavam previstas

1. **Documento ilegível não é pergunta do estágio de rota.** A alternativa era
   lançar ali também. Preferi a menor mudança coerente: o caminho que ainda
   pode alcançar um provedor já recusa antes do claim, então a propriedade de
   aceitação não depende disso. A fronteira está declarada em teste e acima.
2. **O ponto de composição do papel `core`.** A decisão de forma dizia que o
   Dispatch publica a pergunta; escolher que o papel `core` compõe a
   superfície inteira de provedores para fazê-la foi minha, e o custo está
   declarado.
3. **Uma palavra só para o catálogo e para o código de erro da tentativa.** O
   processador poderia ter vocabulário próprio para a guarda. Reusei a
   constante do catálogo, com o motivo escrito no código.
4. **Dois testes vizinhos tiveram o alvo mudado, porque a terminação do
   fallback tornou falsa a expectativa deles.** `Revoking_an_attachment_...`
   afirmava que o fallback seguia para push; agora afirma que a notificação
   termina com a palavra do canal e **não** com um motivo de elegibilidade, o
   que preserva a afirmação original de que a revogação não é pergunta do
   fallback. `No_attempt_no_outbox_row_...` percorria três tentativas
   incluindo o fan-out de push; agora percorre até onde uma notificação com
   conjunto chega, que é a tentativa de e-mail e a conclusão do fallback.
   **Isso é redução de cobertura**, não transferência: as superfícies que um
   passo posterior escreveria ficaram fora do alcance dessa regra, porque uma
   notificação com conjunto não as alcança mais.
5. **Os arranjos passaram a semear objeto real na custódia.** A fixture do
   pipeline ganhou balde versionado no LocalStack e as configurações de
   armazenamento nos dois lados, e o semeador ganhou uma variante que escreve
   os bytes pela porta do módulo dono e fixa a geração na versão que a escrita
   devolveu, com o digest da geração calculado sobre esses bytes. A variante
   sem conteúdo continua existindo e é a que as outras suítes usam.
6. **Dois reparos de auxiliar de teste.** A leitura sem consumo de uma fila
   quebrava com fila vazia, porque a resposta traz lista ausente e não lista
   vazia; e o corpo da solicitação passou a omitir o membro de anexos quando
   não há nenhum, para permitir arranjar o vizinho sem anexos que dá sentido a
   cada zero medido.
7. **O `AGENTS.md` do módulo Notifications ganhou o parágrafo do estágio
   Route**, porque o papel `core` passou a compor uma superfície de outro
   contexto e isso é limite de módulo.
