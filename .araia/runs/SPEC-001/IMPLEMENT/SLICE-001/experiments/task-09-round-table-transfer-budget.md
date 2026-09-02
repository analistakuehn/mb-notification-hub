# Mesa redonda: envelope, orçamento de runtime e alternativas da transferência ao provedor

**Tarefa**: 9 (comparação dos métodos de transferência ao provedor)  
**Convocada em**: 2026-09-02T01:33:43Z  
**Mediador**: `dotnet-architect`  
**Participantes obrigatórios**: `dotnet-engineer`, `dotnet-specialist`  
**Dono da decisão**: usuário (delegação explícita nesta sessão: "tome as melhores decisões por mim; em caso de dúvidas convoque uma mesa redonda; considere tudo previamente aprovado")

## Brief

### Pergunta de decisão

Qual envelope, quais orçamentos numéricos de runtime e qual conjunto de alternativas a sonda comparativa deve usar para que a promoção de um método de transferência satisfaça `NFR-008`, sabendo que o orçamento de runtime aprovado é uma entrada que a especificação exige e não fixa?

### Contexto imutável

- `task-09-provider-transfer.md` fecha a rodada anterior com desenho `PARTIAL`, candidato `streaming` não promovido e nove condições para promoção. O motivo do bloqueio é duplo: trabalho não equivalente ao envio real e ausência de envelope, volume e orçamento numérico aprovados.
- `docs/SPEC-001/requirements/core/01-development-specification.md`, linha 194, define `NFR-008`: nenhuma estratégia de transferência é promovida sem baseline reproduzível, igualdade byte a byte e orçamento de runtime aprovado. A coluna de fundamentação registra que a evidência não sustenta escolha antecipada entre `buffer`, `streaming` e `spool`.
- `docs/SPEC-001/requirements/core/03-verification-plan.md`, linha 45, define `VER-020`: os três braços comparados com o mesmo corpus e envelope, relatório com payload UTF-8, heap, working set, alocação, GC, CPU, I/O, latência, throughput, backlog, limpeza e igualdade de digest, e a opção promovida respeitando o orçamento aprovado. Proprietários: `dotnet-architect` e `Dispatch`.
- A sonda vive em `tests/Platform.PerformanceTests/Scenarios/AttachmentTransferMethodScenario.cs` (385 linhas), `Gate/AttachmentTransferMethodGate.cs` (118 linhas) e `baselines/attachment-transfer-method.json`. O portão é relativo: compara razões contra o braço `buffer` de uma baseline versionada, com verificações exatas de configuração, limpeza, concorrência observada e igualdade de digest.
- `src/Platform.Api/Platform.Api.csproj`, linha 7, e `src/Platform.Worker/Platform.Worker.csproj`, linha 7, ligam `ServerGarbageCollection`. O projeto de desempenho não declara a propriedade.
- Fato externo já verificado nesta sessão, na documentação oficial do provedor: a mensagem total do Mail Send v3, incluindo cabeçalhos, corpo e anexos, deve ficar abaixo de 30 MB; o provedor recomenda anexos de no máximo 10 MB; o total de destinatários não passa de 1.000; argumentos personalizados somam menos de 10.000 bytes; o endpoint aceita até 10.000 requisições por segundo.
- Aritmética da base64, conferida nesta sessão: `4 × ceil(n/3)` produz 5.592.408 bytes para 4 MiB crus, 13.981.016 bytes para 10 MiB crus e 22.369.624 bytes para 16 MiB crus.

### Alternativas comparáveis por decisão

**D1. Envelope medido pela sonda.** O teto do provedor é fato externo; a escolha é o enquadramento em perfis.

- `E1`. Perfil ancorado na recomendação do provedor: um anexo por notificação no tamanho recomendado, medido em concorrência baixa e alta.
- `E2`. Perfil ancorado no teto duro: total de anexos cujo conteúdo codificado aproxima o corpo do limite da mensagem, com folga declarada para o restante do envelope.
- `E3`. Perfil de fragmentação: vários anexos pequenos somando o mesmo total, que exercita custo por item em vez de custo por byte.
- `E4`. Perfil produtivo declarado: quantidade e tamanho vindos de uma decisão de produto. Indisponível hoje (ver `L2`).
- Qualquer combinação exige decidir também a taxa oferecida por réplica, que hoje não existe como grandeza na sonda: o cenário mede um lote fechado de operações, não uma chegada aberta.

**D2. Orçamento de runtime.**

- `B1`. Somente relativo, ampliado: manter o portão de razões e estender as verificações às grandezas hoje registradas e não verificadas (p95, p99, CPU, coleções por geração, heap, working set). Não fixa número absoluto e ainda assim detecta regressão.
- `B2`. Absoluto derivado do alvo de implantação: limite de memória do contêiner, cota de CPU, réplicas e concorrência em voo por réplica produzem tetos por operação. Depende de entrada inexistente (ver `L1`).
- `B3`. Orçamento como função, com constante pendente: fixar agora a forma (por exemplo, pico de memória por envio em voo como múltiplo do tamanho cru do anexo, e memória por réplica como esse múltiplo vezes a concorrência admitida) e materializar a constante quando o alvo de implantação existir.
- `B4`. Absoluto derivado apenas do que o provedor fixa: teto de bytes do corpo, teto de destinatários e teto de argumentos personalizados. É o único subconjunto absoluto derivável hoje sem inventar entrada.

**D3. O braço `spool` permanece como alternativa?**

- `C1`. Descartar agora: matriz de dois braços, sem ensaio de crash e recuperação, sem diretório protegido, cota, controle de propriedade e varredura de resíduos na inicialização. Exige registrar a condição objetiva que reabriria o braço.
- `C2`. Manter apenas no perfil onde poderia vencer: maior total e maior concorrência, aceitando o custo do ensaio de durabilidade só nesse perfil.
- `C3`. Reclassificar como caminho de contenção sob pressão de memória, não como candidato à promoção. O ensaio de durabilidade passa a ser condição de ativação, não de comparação.

**D4. `Content-Length` ou transferência chunked no corpo do Mail Send.**

- `L-A`. Comprimento exato antecipado: medir a parte não anexada do envelope pelo mecanismo já existente e somar o comprimento aritmético do campo do anexo, sem materializar o conteúdo.
- `L-B`. Chunked: dispensa o cálculo, transfere a incerteza para o servidor e para intermediários, e depende da versão HTTP negociada.
- `L-C`. Bufferizar o corpo inteiro para obter o comprimento: elimina a aritmética e reintroduz exatamente o custo de memória que o braço `streaming` existe para evitar.
- `L-D`. Decidir por evidência de protocolo: verificar o que o endpoint aceita e negocia antes de escolher. Depende de entrada não verificada nesta sessão (ver `L3`).

### Critérios de decisão com precedência

1. Eliminatório. Nenhum perfil ou desenho pode produzir corpo acima do teto de mensagem do provedor, contado após a codificação e com o envelope incluído.
2. Eliminatório. A igualdade byte a byte exigida por `NFR-008` precisa ser provada sobre o corpo submetido, capturado no limite observável, e não sobre bytes anteriores à serialização.
3. Eliminatório. Toda grandeza verificada pelo portão precisa ser falsificável. Verificação cujo valor medido deriva da própria configuração não é evidência (ver `F3`).
4. Ordenador. Honestidade da derivação do orçamento: número derivado de entrada declarada vence número escolhido.
5. Ordenador. Custo da matriz (braços vezes perfis vezes ensaios) contra uma única rodada de revisão.
6. Ordenador. Proximidade entre o trabalho da sonda e o caminho produtivo.

Os critérios 1 a 3 eliminam; 4 a 6 ordenam o que sobrar.

### Restrições desqualificantes

- Nenhuma chamada AWS, nenhum acesso de rede, nenhum recurso cobrável nesta mesa e no desenho que ela produzir.
- Nenhuma telemetria em `src/`. A verificação por padrão sobre `OpenTelemetry`, `new Meter` e `System.Diagnostics.Metrics` em `src/` não retornou ocorrência. Um orçamento de runtime não pode ser instrumentado abrindo essa porta.
- Texto em pt-BR com diacríticos; identificadores em inglês.
- O cenário exige hoje exatamente os três braços, e o portão verifica `braços medidos` igual a 3. Remover um braço invalida a baseline versionada, cuja leitura falha quando o braço exigido não existe nela.
- O mesmo comando que compara grava a referência: `--update-baseline` reescreve `baselines/attachment-transfer-method.json`. Um teto absoluto guardado nesse arquivo passa a ser autocertificado.
- A tolerância é um único parâmetro compartilhado por todos os portões da sonda.

### Decisões já aceitas, que não se reabrem

- O portão é relativo contra o braço `buffer` de uma baseline versionada, com verificações de limpeza e igualdade de digest.
- `streaming` não está promovido, e `buffer` não serve como fallback automático sem orçamento aprovado.
- As nove condições de promoção listadas em `task-09-provider-transfer.md` continuam sendo o contrato de promoção.
- O conteúdo do anexo trafega como string base64 dentro do JSON do Mail Send. É forma do provedor, não escolha da mesa.
- A evidência de limpeza cooperativa do `spool` já é suficiente para o que ela cobre, e não cobre encerramento abrupto.

### Forma de retorno solicitada

Para cada decisão de `D1` a `D4`: `RECOMMEND`, `NO-CONSENSUS` ou `INSUFFICIENT-EVIDENCE`, com alternativa líder, oráculo que a torna falsificável, custo estimado em linhas e rodadas, resíduo a registrar e dissenso preservado. Quando a resposta for `INSUFFICIENT-EVIDENCE`, nomear a entrada que falta e quem a possui.

## Abertura do mediador (`dotnet-architect`)

Fontes lidas: brief, `task-09-provider-transfer.md`, `NFR-008`, `VER-020`, cenário, portão, baseline, `ProbeSettings.cs`, o bloco de execução em `Program.cs`, os dois `.csproj` produtivos e o de desempenho. Nada foi alterado; nenhuma chamada de rede.

### Fatos condicionantes

- `F1`. O portão restringe hoje duas grandezas contínuas: razão de alocação contra `buffer`, com limite superior, e razão de vazão contra `buffer`, com limite inferior. p50, p95, p99, CPU, heap, working set e coleções por geração são gravados no relatório e na baseline e não são lidos por nenhuma verificação. O "orçamento de runtime aprovado" que `NFR-008` exige não tem, hoje, ponto algum de aplicação.
- `F2`. Das seis verificações de razão, duas são identidades: para o braço `buffer`, referência e medida são a razão de `buffer` contra si mesmo, ou seja, 1 dos dois lados. Elas passam sempre.
- `F3`. Cinco verificações não podem reprovar, porque o valor medido deriva da configuração e não de observação. `LogicalFileReadBytes` e `LogicalFileWrittenBytes` recebem o produto `bytesPerOperation × operations` calculado no próprio cenário, e o portão compara esse produto com ele mesmo. `TemporaryFilesCreated` é incrementado uma vez por operação sempre que existe raiz de spool, e o portão o compara com o número de operações. As verificações `bytes por operação` e `operações` repetem a mesma forma. Nenhuma delas carrega informação sobre I/O de disco.
- `F4`. A verificação de concorrência aceita `PeakConcurrency` maior que zero e menor ou igual à configurada, de modo que uma rodada que nunca alcançou paralelismo passa. Além disso, o paralelismo é `Parallel.For` sobre trabalho síncrono limitado por CPU, enquanto o caminho produtivo é limitado por I/O (leitura S3 e escrita HTTP). O eixo medido não é o eixo que o orçamento vai restringir.
- `F5`. A baseline tem 12 operações por braço, e o percentil usa índice `ceil(p × n) - 1`. Com 12 amostras, p95 e p99 são a mesma observação, o máximo. Sob essa função, p95 só deixa de ser o máximo a partir de 20 amostras, e p99 a partir de 100. Qualquer teto absoluto escrito como p95 ou p99 nessa contagem é teto sobre o pior de doze.
- `F6`. A tolerância padrão de 0,55 é derivada e documentada para outro cenário, o da trilha de auditoria, a partir da dispersão medida daquela razão e da distância até a assinatura de falha daquela métrica. O portão da transferência consome o mesmo parâmetro sem derivação própria. É evidência herdada.
- `F7`. O projeto de desempenho não declara `ServerGarbageCollection`, e não existe `runtimeconfig.template.json` em nenhum lugar do repositório. Como a sonda é o processo de entrada, a baseline foi medida sob Workstation GC enquanto API e Worker rodam sob Server GC. Alocação por operação é indiferente ao modo; heap, coleções por geração, working set e latência sob concorrência não são.
- `F8`. As outras quatro baselines estão listadas no bloco `None` do `.csproj`, com o comentário que explica por que não são copiadas para a saída; `attachment-transfer-method.json` não está. Pelos globs padrão do SDK o arquivo continua incluído, então a divergência é documental e não funcional, mas recai justamente sobre o arquivo que o portão lê e reescreve.
- `F9`. A igualdade de digest prova que os três braços resumiram os mesmos bytes de entrada. Ela não prova igualdade do corpo submetido, porque nenhum braço produz base64, JSON ou `HttpContent`. A prova que `NFR-008` pede é sobre o que sai.
- `F10`. O cancelamento é entre iterações: `ParallelOptions.CancellationToken` interrompe o agendamento, e a operação em voo não recebe token nenhum. A evidência de limpeza da rodada anterior tem granularidade de operação inteira.
- `F11`. Com as opções padrão do `System.Text.Json`, o codificador escapa o sinal de adição como `+` e a barra como `/`. A prova está na própria baseline versionada, cujo campo de data grava `2026-08-31T20:13:55.5647834+00:00`. Um corpo serializado com o codificador padrão expande o conteúdo base64 além da razão de 4 para 3, e por uma quantidade que depende do conteúdo, já que os dois caracteres afetados pertencem ao alfabeto base64. O projeto já pinou `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` nas formas canônicas de `Audit`, `Notifications` e `TemplateManagement` e em `SharedKernel`. A decisão `D4` depende de qual codificador o corpo do Mail Send usa, e essa é uma decisão de arquitetura, não um detalhe de serialização.
- `F12`. `src/Platform.Api/Modules/SharedKernel/CompactJsonSize.cs` já mede o tamanho compacto em UTF-8 em uma única travessia, sem materializar o payload e com a política de escape pinada. O mecanismo que `L-A` pressupõe para a parte não anexada do envelope já existe no repositório.
- `F13`. O cenário usa blocos de 65.536 bytes, que não é múltiplo de 3. Uma codificação base64 incremental exige blocos múltiplos de 3 para não emitir preenchimento no meio do fluxo; 65.532 e 49.152 servem. O braço `streaming` funcional não reaproveita o tamanho de bloco atual sem esse ajuste.
- `F14`. A tabela de `NFR-001` a `NFR-008` não contém nenhum alvo de latência, vazão ou memória. O orçamento não está guardado em outro requisito: ele não existe no documento.
- `F15`. Os limites do provedor têm duas ambiguidades de unidade que mudam o resultado da conta. "30 MB" pode ser 30.000.000 ou 31.457.280 bytes, e a recomendação de "10 MB" não diz se é medida no conteúdo cru ou no codificado. Sob a leitura conservadora, reservando 100 KiB para o restante do envelope, o total cru cabe em cerca de 22.423.200 bytes, aproximadamente 21,4 MiB; sob a leitura binária, cerca de 23.516.160 bytes. Nas duas leituras, um anexo de 16 MiB cabe sozinho e dois não cabem, e o perfil de 16 MiB já usado pela sonda fica acima da faixa recomendada pelo provedor.
- `F16`. O parâmetro de corpus aceita até 536.870.912 bytes, mais de dezessete vezes o teto de mensagem do provedor. A faixa admitida pela sonda não é limitada pelo envelope que ela pretende representar.

### Pergunta de decisão normalizada

Qual conjunto de perfis de envelope, qual forma de orçamento de runtime (relativo ampliado, absoluto derivado de alvo declarado, ou função com constante pendente), qual matriz de braços e qual regime de comprimento do corpo tornam a sonda comparativa capaz de reprovar uma promoção errada, de modo que `VER-020` verifique conformidade contra um orçamento escrito, em vez de contra razões que hoje não cobrem nenhuma das grandezas nomeadas por `NFR-008`?

### Perguntas a cada participante

`dotnet-engineer`:

- `E1`. Dados `F1`, `F2` e `F3`, qual é o conjunto mínimo de verificações que precisa ser acrescentado ou substituído para que o portão possa reprovar, e quais das verificações atuais devem ser removidas por não carregarem informação?
- `E2`. Dado `F5`, qual contagem de operações por braço e por perfil torna p95 e p99 grandezas distintas do máximo, e qual é o custo de tempo dessa contagem na matriz completa?
- `E3`. Dado `F7`, o Server GC entra por propriedade no `.csproj` do projeto de desempenho, por `runtimeconfig.template.json` ou por variável de ambiente na invocação, e qual das formas mantém a baseline reproduzível fora da máquina de quem a gravou?
- `E4`. Dados `F11`, `F12` e `F13`, quanto código separa o braço `streaming` atual de um braço funcional com leitura S3, base64 incremental, envelope JSON integral e `HttpContent` cancelável, e qual parte disso é reaproveitável de `CompactJsonSize`?
- `E5`. Dada a alternativa `C1` em `D3`, qual é o efeito concreto de remover um braço sobre o cenário, o portão e a baseline versionada, e qual sequência evita uma rodada em que o portão não consegue nem carregar a referência?
- `E6`. Em `D4`, o comprimento exato antecipado é obtível sem materializar o anexo, com o codificador pinado, e qual é o erro máximo da conta quando o envelope contém texto de usuário que escapa?

`dotnet-specialist`:

- `S1`. Dado `F4`, a concorrência limitada por CPU do cenário atual pode ordenar os braços de forma diferente da concorrência limitada por I/O do caminho produtivo? Se pode, qual desenho de sonda mede o eixo certo sem rede.
- `S2`. Dado `F7`, quais das grandezas exigidas por `VER-020` mudam de valor entre Workstation GC e Server GC nesta carga, e quais são invariantes ao modo? A resposta decide se a baseline atual serve de referência ou precisa ser regravada.
- `S3`. Em `D2`, qual é a menor derivação honesta de um teto absoluto de memória por envio em voo a partir de grandezas hoje observáveis, e onde exatamente essa derivação passa a exigir o alvo de implantação?
- `S4`. Dado `F9`, qual oráculo prova igualdade byte a byte do corpo submetido entre braços que constroem esse corpo por caminhos diferentes, e o que esse oráculo precisa capturar além do digest?
- `S5`. Em `D3`, o `spool` tem alguma propriedade mensurável que `buffer` e `streaming` não têm nesta carga, considerando que o payload já está residente em memória antes da medição? Se a resposta for não, dizer se isso é limitação do desenho da sonda ou do próprio braço.

Aos dois:

- `R1`. Qual das quatro decisões pode ser fechada agora com a evidência disponível, e qual precisa ser devolvida como `INSUFFICIENT-EVIDENCE` com a entrada nomeada?
- `R2`. Qual é a condição objetiva, escrita, que reabre cada decisão fechada nesta mesa?

### Lacunas de evidência

- `L1`. Não existe alvo de implantação declarado: limite de memória do contêiner, cota de CPU, número de réplicas, concorrência em voo por réplica e taxa oferecida. Sem ele, a parte absoluta de `D2` não é derivável para heap, working set, CPU e disco. A entrada pertence ao usuário, como dono da decisão, e ao dono da infraestrutura ainda não escrita em Terraform. Bloqueia `B2` inteiramente e fixa a constante de `B3`.
- `L2`. Não existe quantidade nem tamanho de anexos aprovados por notificação. `F14` confirma que a especificação não guarda esse número em outro requisito. A entrada pertence ao produto. Bloqueia `E4` e deixa `E1` a `E3` como enquadramentos derivados apenas do teto do provedor.
- `L3`. A versão HTTP negociada com o endpoint do provedor, e a exigência ou não de `Content-Length`, não foram verificadas nesta sessão. Pelo padrão, a codificação chunked existe em HTTP/1.1 e não existe em HTTP/2, onde a questão vira presença ou ausência do comprimento declarado. Sem essa verificação, `L-B` e `L-D` não são comparáveis a `L-A`. A entrada está na documentação do provedor e não exige chamada cobrável.
- `L4`. As duas ambiguidades de unidade de `F15` continuam abertas. Enquanto continuarem, todo perfil precisa ser escrito na leitura conservadora, e a diferença entre as leituras precisa ser registrada como folga, nunca como capacidade.
- `L5`. Nenhuma medição existente inclui leitura S3, base64, JSON e escrita HTTP. A ordenação atual entre os braços não prediz a ordenação produtiva, e o `spool` é o braço mais penalizado por essa ausência, porque paga disco sem exercitar a razão pela qual existiria.
- `L6`. Não há medição sob Server GC. Enquanto não houver, qualquer teto absoluto de heap, working set ou coleção por geração seria calibrado no modo errado.

`L1` e `L2` impedem a síntese da parte absoluta de `D2` e da forma final de `D1`. `L3` condiciona `D4`. `L5` e `L6` condicionam a validade da baseline atual como referência, não a estrutura da decisão. `D3` é a única das quatro decidível hoje por argumento de custo e de desenho, desde que a mesa registre a condição objetiva de reabertura.

### O que esta abertura não decide

Nenhum vencedor, nenhuma promoção, nenhum número de orçamento. A abertura fixa a pergunta, as alternativas comparáveis, a precedência dos critérios, as restrições que desqualificam e as entradas que faltam. As posições dos participantes vêm a seguir, e a síntese só é possível depois delas.

## Posição do engenheiro (`dotnet-engineer`)

Medições próprias em projeto descartável fora do repositório, .NET 10.0.302, x64, 22 núcleos, Release, Workstation GC salvo onde indicado, cada número reproduzido em pelo menos duas execuções. O repositório não foi alterado.

| Id | Medida | Resultado |
|---|---|---|
| `M1` | `Utf8JsonWriter.WriteBase64StringSegment` e `WriteStringValueSegment` existem na BCL do .NET 10 | presentes |
| `M2` | Codificação em blocos de 65.536, 65.532, 49.152 e 1.000 bytes sobre 4 MiB mais 7 bytes, contra a forma inteira | idêntica byte a byte nos quatro tamanhos |
| `M3` | `FlushAsync` entre segmentos, escrevendo em `Stream` | o fluxo cresce 65.536 bytes por bloco, saída idêntica à forma inteira |
| `M4` | Alocação total de uma codificação completa, por tamanho de bloco | 49.152 devolve 65.722 B; 65.536 devolve 240.538 B; 81.920 devolve 174.970 B; 786.432 devolve 1.114.322 B mais uma coleção de geração 2 |
| `M5` | Invariância da alocação ao tamanho do anexo, bloco 49.152 | 4 MiB e 16 MiB alocam o mesmo valor, 65.722 B |
| `M6` | Fronteira do Large Object Heap para o buffer de saída | 84.972 B fica na geração 1; 84.976 B vai para a geração 2. Bloco de entrada 49.152 mantém a saída fora do LOH; 63.732, 65.532, 65.536 e 81.920 põem a saída no LOH |
| `M7` | Campo do anexo sob `UnsafeRelaxedJsonEscaping` | exatamente `4 × ceil(n/3) + 2` |
| `M8` | Mesmo campo sob o codificador padrão | 17.606 contra 16.386. Somente o `+` escapa, a 6 bytes por ocorrência; a barra não escapa em nenhum codificador |
| `M9` | Pior expansão por caractere sob o codificador pinado | 6 bytes por 1 em caractere de controle; 12 bytes por 4 em par substituto; 2 por 1 em aspas e barra invertida |
| `M10` | Passe de contagem sobre envelope realista de 2.420 bytes com 50 destinatários | 5,44 e 5,51 microssegundos, 16.568 B alocados |
| `M11` | `ServerGarbageCollection` verdadeiro no arquivo de projeto | emite a chave no `runtimeconfig.json` e a leitura em execução devolve verdadeiro |

`M8` corrige o fato `F11` da abertura: a barra não é escapada. A expansão sob o codificador padrão vem só do `+`, e mede 7,4 por cento em base64 aleatória. `M1` a `M3` refutam `F13` no caminho que importa, porque `WriteBase64StringSegment` absorve o resto entre segmentos e produz saída idêntica com qualquer tamanho de bloco, inclusive 1.000. O múltiplo de 3 só obriga quem chamar a codificação de baixo nível à mão. Isso apaga cerca de 120 linhas de codificador manual.

**`D1`**: `RECOMMEND` para os perfis da sonda, `INSUFFICIENT-EVIDENCE` para o envelope produtivo. Perfis propostos: 1 MiB como piso da razão, 7 MiB como único perfil abaixo da recomendação do provedor nas duas leituras, 16 MiB como teto declarado como esforço, e 64 anexos de 256 KiB para separar custo por item de custo por byte. Concorrência 1 e 8, e não uma só, para que a reta de memória por réplica seja medida e não asserida. Regra eliminatória: nenhum perfil produz corpo acima de 30.000.000 bytes, contado depois da codificação pelo passe de contagem, nunca por aritmética à parte.

**`D2`**: `RECOMMEND` para o portão relativo ampliado mais três constantes absolutas já deriváveis, `INSUFFICIENT-EVIDENCE` para os tetos de heap, working set, CPU e disco por réplica. As três constantes não descrevem capacidade do ambiente, descrevem a propriedade definidora do braço. Primeira, invariância da alocação entre o menor e o maior perfil, com teto de 2,0 contra assinatura de falha de 15,97 medida no braço de bufferização. Segunda, alocação por operação do candidato até 200.000 B contra 65.722 B medidos. Terceira, coleções de geração 2 por operação iguais a zero, que é o detector direto de regressão para o LOH. Restrição estrutural: todo teto absoluto vive como constante no código do portão, nunca como campo do arquivo de baseline, porque a opção de regravar baseline reescreve o mesmo arquivo que o portão lê e um teto guardado ali se autocertifica.

**`D3`**: `RECOMMEND` descartar o `spool` agora, com condição de reabertura escrita. A única propriedade que justificaria disco é limitar memória sob pressão, e `M5` a retira: o braço streaming funcional aloca o mesmo valor a 4 MiB e a 16 MiB. Manter o braço promovível custa cerca de 250 linhas em código de produção e 450 em teste, mais superfície operacional permanente de diretório com controle de propriedade, cota, varredura de resíduos na inicialização e ensaio de crash. Como a baseline precisa ser regravada de qualquer modo, o custo marginal de remover um braço é praticamente zero e o de mantê-lo é setecentas linhas que apodrecem por ficarem inativas. Reabre se o envio exigir repetição do corpo numa retentativa e a fonte não puder ser relida; hoje as duas metades falham, porque a fonte é um objeto no armazenamento e uma nova leitura é repetível.

**`D4`**: `RECOMMEND` comprimento exato antecipado. `M10` responde a pergunta de custo: o passe de contagem custa 5,44 microssegundos e aloca 16.568 bytes, contra um corpo de 22,4 MB que leva dezenas de milissegundos na rede, ou seja 0,02 por cento da operação. A forma é escrever o envelope uma vez com o valor do anexo vazio contra um sorvedouro descartante e somar a aritmética do campo. O erro é zero por construção, porque a conta não estima o envelope, ela o conta com o mesmo escritor e o mesmo codificador que vão emiti-lo. A invariante a impor é uma única função de escrita do envelope chamada duas vezes com sorvedouros diferentes, e o oráculo que pega a divergência é comparar o comprimento declarado com a contagem de bytes que o servidor falso recebeu.

**Portão**: contou 36 verificações, das quais 14 não podem reprovar. Propõe remover essas 14, substituir a de concorrência observada por igualdade exata com a configurada, manter nove reais e acrescentar seis, entre elas o digest do corpo recebido pelo servidor falso, a igualdade entre comprimento declarado e bytes recebidos, e o modo de GC da rodada igual ao gravado na baseline.

**Blocagem**: 49.152 bytes, ou 48 KiB. O critério não é o múltiplo de 3, é a fronteira do LOH medida em `M6`. A saída dá exatamente 64 KiB, entrada e saída ficam as duas fora do LOH, e 49.152 é o maior tamanho da família com essa propriedade. Aloca 3,7 vezes menos que o bloco atual. O tamanho de bloco não muda tempo mensurável; muda alocação de forma determinística.

**Baseline**: não é reaproveitável. A alocação do braço streaming atual é 590 bytes porque ele só alimenta um hash incremental, contra 65.722 do braço funcional, duas ordens de grandeza. A vazão registrada é hash sobre memória residente. O digest é sobre o corpus de entrada, não sobre o corpo submetido. O dispositivo anticegueira já existe: a leitura recusa com exceção qualquer baseline cujo formato não seja o esperado, então basta subir a versão do formato para que a referência antiga produza falha dura e jamais um verde.

**Modos de falha da própria recomendação**: a medição de invariância foi feita sem rede e sem armazenamento, então um cliente HTTP real e o cliente do provedor de nuvem podem acrescentar alocação proporcional ao tamanho; as medições são sob Workstation GC, e a verificação de geração 2 igual a zero pode virar vacuamente verdadeira sob Server GC, portanto precisa ser provada por mutação; o par de concorrência assume linearidade; descartar o `spool` é irreversível na prática, porque reabrir significa escrever as setecentas linhas do zero; e o teto de invariância foi derivado de duas observações, que não são uma distribuição.

## Posição do especialista (`dotnet-specialist`)

Congelou um snapshot dos arquivos em reescrita antes de qualquer leitura e construiu uma sonda independente fora do repositório com três braços que produzem byte a byte o mesmo corpo do provedor e o mesmo digest sobre esse corpo. A equivalência funcional é verificada em toda rodada, não suposta. Host único, 22 núcleos, .NET 10.0.10.

**Estatística de medição.** Coletou mil latências reais por modo de GC e reamostrou duas mil subamostras de cada tamanho aplicando o estimador do próprio portão. O percentil 95 deixa de ser o máximo em vinte amostras e o percentil 99 deixa de ser o máximo em cem, confirmado em cem por cento e zero por cento das subamostras, sem exceção. Mas o índice ser distinto do máximo não basta: para que o ruído amostral caiba na banda de tolerância vigente, a dispersão precisa ficar abaixo de 1,55 vezes, e isso só acontece em quinhentas amostras para o percentil 95. Em duzentas a dispersão ainda é 1,92 vezes, de modo que uma rodada honesta pode reprovar e outra rodada honesta da mesma configuração pode passar. Conclusão: duzentas amostras no mínimo para que o percentil 95 seja estatística e não máximo disfarçado, quinhentas para que um teto sobre ele seja mais apertado que o próprio ruído, e nenhum teto sobre o percentil 99, porque estabilizá-lo exigiria mil amostras, que é exatamente o limite configurável. A grandeza honesta a restringir no lugar é o máximo observado. O custo em tempo não é objeção: duzentas amostras custam cerca de 5,5 segundos no perfil de 4 MiB e 21 segundos no de 16 MiB, e a matriz inteira fica abaixo de dois minutos.

**Server GC contra Workstation.** A razão de alocação é rigorosamente invariante ao modo, com variação de zero por cento medida. O comprimento do corpo e o digest são invariantes por construção. Já as coleções de geração 2 variam 23 por cento para mais em um braço e 54 por cento para menos em outro, o percentil 50 sobe 69 por cento, o percentil 95 sobe 248 por cento e a vazão cai 36 por cento. E o achado decisivo: o número de heaps do Server GC deriva do limite de CPU visível ao runtime. Medindo com dois, quatro e vinte e dois heaps, o percentil 95 do braço de bufferização varia 1,85 vezes. Portanto o alvo de implantação não é lacuna burocrática, a topologia do coletor é derivada dele, e sem ele nenhum teto absoluto de latência ou de memória é calibrável. A baseline atual serve exatamente na medida em que o portão é insensível; no instante em que ganhar poder de reprovar, precisa ser regravada sob a contagem de heaps do alvo.

**Large Object Heap.** Um anexo de 63.750 bytes crus já produz base64 no LOH, e de 31.875 bytes se materializado como string. Não existe perfil em discussão que não seja carga de LOH. Em todas as 24 linhas medidas, sem exceção, as contagens das três gerações são iguais: não há uma única coleção de geração 0 nesta carga, e toda coleção observada é completa, disparada pelo orçamento do LOH. Logo as contagens de geração 0 e 1 não carregam informação independente, são o número de geração 2 escrito três vezes. O braço de bufferização força cerca de 0,55 coleção completa por operação a 4 MiB, e o streaming elimina isso por inteiro: pausa de coleta de zero milissegundo em todas as rodadas, contra 9,1 a 28,8 milissegundos por 24 operações. A fragmentação medida chegou a 83.895.256 bytes e o working set cresceu 802 MiB em oito operações sequenciais no tamanho de teto do provedor. Nenhuma grandeza do portão captura esse efeito hoje.

**Amplificação de memória.** Alocação por operação dividida pelo tamanho cru: o braço de bufferização mede 9,335, 9,333 e 9,334 em três tamanhos que cobrem uma faixa de cinco vezes, portanto é constante; o streaming cresce 144 bytes ao longo da mesma faixa, portanto é constante no tamanho, não uma razão pequena.

**Armadilha do escritor JSON.** Os 16 a 66 MB por operação que o braço de spool alocava não vinham do disco, vinham do escritor sobre fluxo, que acumula a saída pendente em um buffer que cresce por duplicação e que, acima de um megabyte, deixa de vir do pool e passa a ser alocação nova a cada passo. Chamar descarga por segmento derruba a alocação de 66.403.386 para 118.019 bytes, com o mesmo corpo e o mesmo digest. É a armadilha exata que faz um braço chamado streaming alocar como um braço de bufferização sem que nada no relatório denuncie, e um teto de alocação medido em um único tamanho passaria essa implementação sem reclamar.

**Comprimento do corpo.** Aqui o especialista vai além do engenheiro. As chamadas de base64 do escritor não passam pelo codificador: o campo mede exatamente `4 × ceil(n/3) + 2` sob o codificador padrão e sob o relaxado, para conteúdo de texto, aleatório e adversário, com diferença zero em oito de oito linhas. Portanto a decisão não é sobre qual codificador, é sobre qual chamada de escrita. Se o base64 for escrito como string comum sob o codificador padrão, o pior caso mede seis vezes o comprimento, e um anexo de apenas 3.737.200 bytes de conteúdo escolhido pelo remetente estoura o teto de mensagem do provedor. É um vetor de negação de serviço acionável por conteúdo de usuário, e ele desaparece escolhendo a chamada certa. A parte não anexada do envelope é rigorosamente aditiva: medida em três tamanhos, a diferença é constante em 315 bytes.

**`D1`**: `INSUFFICIENT-EVIDENCE` no todo, com o eixo de tamanho fechado. O teto duro de conteúdo cru somado é 22.423.200 bytes na leitura conservadora, e a sonda foi medida exatamente nesse ponto de fronteira. A reserva de 100 KiB é justificada, porque mil destinatários mais os argumentos personalizados somam cerca de 50 KB. Um anexo de 16 MiB cabe sozinho e dois não cabem. Não derivável: a quantidade de anexos por notificação, porque o provedor limita apenas o total e não publica limite de contagem, entrada que pertence ao produto; e o perfil de concorrência, entrada que pertence ao dono da decisão e ao dono da infraestrutura, agravada porque a cota de CPU determina também a topologia do coletor.

**`D2`**: `RECOMMEND` a forma, `INSUFFICIENT-EVIDENCE` a constante. A menor derivação honesta é afim: alocação por envio menor ou igual a um termo constante mais um coeficiente vezes o tamanho cru, e memória por réplica igual a isso vezes a concorrência em voo mais o regime. Os dois parâmetros já estão medidos e são invariantes ao modo de GC e ao tamanho: cerca de 116 KB e coeficiente zero para streaming, cerca de 118 KB e coeficiente zero para spool com descarga por segmento, e coeficiente 9,33 para bufferização. Com o teto do provedor, a bufferização exige 199,6 MiB por envio em voo, número medido e não extrapolado. A derivação passa a exigir o alvo de implantação exatamente na concorrência e no limite de memória, e em nada antes disso. Recebem teto absoluto agora: alocação afim verificada em pelo menos dois tamanhos, igualdade exata do comprimento do corpo, pausa de coleta por operação, e coleções de geração 2 sozinhas. Precisam ser trocadas antes de qualquer leitura: os deltas de heap e de working set, porque foram medidos negativos em cinco rodadas, chegando a menos 444 megabytes, e uma grandeza que assume valor negativo não pode carregar limite superior. O substituto é pico amostrado durante o braço.

**`D3`**: `RECOMMEND` reclassificar o `spool` como caminho de contenção, e não descartá-lo. Medido com os três braços produzindo o mesmo corpo, o spool bem implementado iguala o streaming em alocação, em coleções e em pausa de coleta, e perde em latência por fator de 6,5 a 10,7 vezes. Portanto é estritamente dominado nesta carga, e isso é estrutural, porque origem e destino já são fluxos. A ressalva honesta é que o spool dá repetibilidade do corpo, propriedade que nenhuma sonda sem rede enxerga, mas que também não lhe é exclusiva, porque reemitir a leitura no armazenamento a obtém sem diretório protegido, sem cota e sem varredura de resíduos. A reclassificação move o ensaio de crash de condição de comparação para condição de ativação e preserva os três braços, o que preserva a baseline versionada e evita a sequência de rodada quebrada. Descartar paga a invalidação da baseline para chegar onde a reclassificação chega de graça.

**`D4`**: `RECOMMEND` comprimento exato, e refuta a dependência da versão de HTTP. Um comprimento declarado é framing válido em HTTP/1.1 e cabeçalho válido em HTTP/2, então a alternativa líder não depende de descobrir o que o endpoint negocia. Condição inegociável: fixar a chamada de escrita, não apenas o codificador.

**Portão**: contou 36 verificações e concluiu que 17 são incapazes de reprovar, ou seja 47 por cento, três formas a mais do que a abertura listou. Acrescentou que o relatório afirma cumprir o item de backlog do plano de verificação com um valor que é constante e igual ao número de operações, portanto não é observação.

**Modos de falha da própria recomendação**: host único, sem Linux, sem cotas de contêiner, e a penalidade do Server GC varia 1,85 vezes só mexendo na contagem de heaps; os braços medidos são os do especialista e não os do repositório, de modo que o valor 9,33 é da implementação e o que é robusto é a ordenação e a invariância; o teto de alocação recomendado pode consagrar uma implementação errada se gravado de uma única medição, e foi assim que a armadilha do escritor foi descoberta, por acidente; a dispersão de percentis é otimista, porque foi reamostrada dentro de uma única execução; a leitura de pausa de coleta é do processo inteiro e quebra em silêncio se os braços rodarem em paralelo; e nenhum número inclui armazenamento nem rede.

## Síntese do mediador (`dotnet-architect`)

Fontes desta etapa, lidas agora e não herdadas das posições: o portão em `tests/Platform.PerformanceTests/Gate/AttachmentTransferMethodGate.cs` (`sha256 ed9028cd...`), o cenário em `Scenarios/AttachmentTransferMethodScenario.cs` (`sha256 410da967...`), o carregador em `Gate/AttachmentTransferMethodBaseline.cs`, a baseline versionada, os padrões em `ProbeSettings.cs`, o despacho em `Program.cs`, o arquivo de projeto da sonda e os dois arquivos em reescrita nesta hora, `Gate/ProviderTransferInvariants.cs` (`sha256 52dbf1c8...`) e `Reporting/ProbeOutcome.cs` (`sha256 9ffc14c4...`), lidos como instantâneo e não como estado final. Nada foi alterado em `tests/`. Nenhuma chamada de rede. Cinco números foram recalculados por mim antes de qualquer veredito, e dois deles mudam recomendações das duas posições.

### Fato que chegou depois das duas posições

`F7` está vencido. O arquivo `tests/Platform.PerformanceTests/Platform.PerformanceTests.csproj` na árvore de trabalho declara `ServerGarbageCollection` verdadeiro, com comentário próprio, e `git show HEAD` do mesmo arquivo não contém a propriedade. A mudança não está confirmada em commit e pertence à sessão que reescreve o harness. Três consequências que nenhuma das duas posições pôde pesar:

- A baseline versionada foi gravada em 2026-08-31T20:13:55, antes da mudança, portanto sob Workstation GC. A próxima rodada de comparação da transferência roda sob Server GC contra uma referência de outro coletor, e nenhuma verificação hoje repara nisso. Pelas medições do especialista, o percentil 95 sobe 248 por cento e a vazão cai 36 por cento entre os modos.
- A propriedade é de projeto, não de cenário. O mesmo executável hospeda os modos `Delivery`, `Memoization`, `Render`, `Parse`, `AttachmentTransfer`, `ProviderTransfer`, `Smoke`, `Relay` e `Full`. As outras quatro baselines versionadas, `audit-chain-contention`, `published-read-memoization`, `published-render-cost` e `scriban-parse-memoization`, foram gravadas sob Workstation GC e passam a ser comparadas sob Server GC sem que nada as avise. O raio de alcance da decisão saiu do escopo desta mesa.
- Enquanto a propriedade não estiver no commit junto com as baselines regravadas, um checkout de `HEAD` mede sob Workstation e esta árvore mede sob Server. É exatamente a pergunta `E3`, e a resposta é de sequenciamento: propriedade e referências regravadas viajam no mesmo commit, ou a referência e o coletor divergem por checkout.

Isto não é censura à mudança: ela é o que as duas posições recomendaram. É o preço que a recomendação carrega e que nenhuma das duas listou.

### 1. Veredito por decisão

| Decisão | Veredito | Alternativa líder |
|---|---|---|
| `D1` | `RECOMMEND` para os perfis da sonda; `INSUFFICIENT-EVIDENCE` para o envelope produtivo e para o ponto de operação da concorrência | `E1` com `E3`: 1 MiB, 7 MiB, 16 MiB e 64 anexos de 256 KiB, mais um perfil adversário de conteúdo, em concorrência 1 e 8 |
| `D2` | `RECOMMEND` para a forma; `INSUFFICIENT-EVIDENCE` para toda constante de ambiente e também para o termo constante do teto de alocação | `B1` somado a `B3`, com o subconjunto de `B4` como única parte absoluta derivável hoje |
| `D3a`, spool como candidato à promoção e como caminho produtivo | `RECOMMEND` não construir | consenso das duas posições, com a condição de reabertura do engenheiro adotada literalmente |
| `D3b`, braço spool na sonda comparativa | `RECOMMEND` com dissenso preservado e gatilho falsificável | `C3`, reclassificar sem remover |
| `D4` | `RECOMMEND` | `L-A`, com a condição inegociável de fixar a chamada de escrita e com corpus adversário que torne a verificação falsificável |

**`D1`.** O engenheiro respondeu `RECOMMEND` e o especialista respondeu `INSUFFICIENT-EVIDENCE`, e os dois estão certos porque respondem sobre objetos diferentes. O eixo de tamanho está fechado pelo teto do provedor e não depende de nenhuma entrada externa: os dois derivam o mesmo teto de conteúdo cru somado, os dois concluem que um anexo de 16 MiB cabe sozinho e dois não cabem. Conferi a escolha de 7 MiB: `4 × ceil(7.340.032/3)` dá 9.786.712 bytes, abaixo de 10.000.000 e de 10.485.760, portanto 7 MiB é o único perfil redondo que fica sob a recomendação do provedor nas duas leituras de unidade e nos dois pontos de medição, cru e codificado. O que não é derivável é a afirmação de que esses perfis representam produção, e essa afirmação ninguém precisa fazer para medir. Acrescento um quinto perfil que não é escolha de tamanho e sim de conteúdo, pela razão do item 2.2.

**`D2`.** As duas posições convergem na forma e divergem só na aparência. A primeira constante do engenheiro, invariância da alocação entre o menor e o maior perfil com teto de 2,0, já é a forma afim escrita como razão. O que não sobrevive é o segundo número dele, 200.000 B por operação, pela razão do item 2.3. Recebem valor hoje apenas as grandezas que descrevem a forma do braço e não o tamanho do ambiente.

**`D3`.** Separo a decisão em duas, porque as duas posições precificaram objetos diferentes sem perceber. Ver o item 3 do desafio delimitado e a seção 6.

**`D4`.** Consenso na alternativa, com a leitura do especialista prevalecendo sobre a natureza da decisão. `L3` deixa de bloquear `D4`: um comprimento declarado é framing válido em HTTP/1.1 e cabeçalho válido em HTTP/2, então a alternativa líder é invariante ao que o endpoint negocia. `L3` continua aberta para outros fins e não bloqueia mais nada nesta mesa.

### 2. Desafio delimitado

#### 2.1 A contagem do portão: nem 14 nem 17, são 23

Os dois acertaram o total, 36, e os dois subcontaram os incapazes porque aplicaram um critério estreito demais. O critério correto é este: uma verificação é incapaz quando a via vermelha é inalcançável em qualquer rodada que o cenário atual consiga produzir, isto é, quando falsificá-la exige editar o cenário e não medir outra coisa. Sob esse critério a contagem é 23 incapazes e 13 capazes, e as 13 se dividem em 5 detectores de deriva de configuração e 8 observações de comportamento.

| Família | Instâncias | Incapaz | Onde a via vermelha morre |
|---|---|---|---|
| `braços medidos` igual a 3 | 1 | sim | `ProbeSettings.cs` linhas 537 a 540 e `EnsureCompleteArmSet` no cenário linhas 375 a 384 lançam antes; o portão nunca vê contagem diferente |
| `bytes UTF-8 do payload`, `bytes do envelope`, `operações por braço`, `concorrência configurada`, `digest do corpus` | 5 | não | comparam a rodada com a baseline; falsificadas por rodar configuração diferente da referência |
| `{braço}: bytes por operação` | 3 | sim | `bytesPerOperation` é `payload.Length + envelope.Length` no cenário linha 189, e o esperado é a mesma soma; identidade |
| `{braço}: operações` | 3 | sim | os dois lados são o mesmo parâmetro `operations` |
| `{braço}: concorrência configurada` | 3 | sim | os dois lados são o mesmo parâmetro `concurrency` |
| `{braço}: concorrência observada` | 3 | sim | `peak` é pelo menos 1 com uma operação, e `MaxDegreeOfParallelism` impede ultrapassar o configurado; o predicado é tautologia |
| `{braço}: igualdade de digest` | 3 | não | falsificada por um braço que resuma bytes diferentes, por exemplo mexendo no fatiamento de `Chunks` |
| `{braço}: arquivos temporários residuais` | 3 | 2 de 3 | sem raiz de spool o valor é a constante 0 do cenário, linhas 178 a 180; só o braço `spool` enumera o disco de verdade |
| `{braço}: raiz temporária removida` | 3 | sim, as três | `RemoveSpoolRoot` lança quando o diretório resiste, cenário linhas 232 a 235, então o valor falso é inalcançável: a falha vira exceção e nunca vermelho |
| `{braço}: razão de alocação contra buffer` | 3 | 1 de 3 | no braço `buffer` referência e medida são `buffer` contra si mesmo, 1 dos dois lados |
| `{braço}: razão de vazão contra buffer` | 3 | 1 de 3 | mesma identidade |
| `spool: bytes lógicos lidos` e `escritos` | 2 | sim | `transferredBytes` é `bytesPerOperation × operations`, cenário linhas 190 e 217 a 218, comparado consigo mesmo. Confirmado na baseline versionada: 50.343.936 é exatamente `(4.194.304 + 1.024) × 12` |
| `spool: temporários exercitados` | 1 | sim | incrementado uma vez por operação sempre que existe raiz; o valor ou é `operations` ou a rodada morre por exceção |

Reconciliação com as duas contagens. As 14 do engenheiro são exatamente as identidades aritméticas escritas dentro do portão: 3 mais 3 mais 3, mais as 2 do braço `buffer`, mais as 3 do rodapé do spool. As 17 do especialista são essas 14 mais as 3 de concorrência observada. As 6 que faltam nas duas contagens são de um tipo que só aparece lendo o cenário junto com o portão, e não o portão sozinho: `braços medidos`, garantida por duas validações anteriores; `raiz temporária removida` nos três braços, cuja via falsa foi substituída por exceção; e `arquivos temporários residuais` nos dois braços que não têm raiz. É diferença de método, não de atenção: quem lê só a expressão do portão enxerga identidade, quem lê o cenário enxerga também inalcançabilidade.

Consequência prática: 23 verificações desaparecem, e das 13 que sobram apenas 8 observam comportamento, das quais 4 são as razões dos braços `streaming` e `spool`. É esse o tamanho real da rede que hoje protege `NFR-008`.

#### 2.2 `D4` é escolha de chamada de escrita, e o corpus atual não consegue falsificá-la

Prevalece a leitura do especialista, e as duas medições não se contradizem: elas medem chamadas diferentes. A medida `M7` do engenheiro, `4 × ceil(n/3) + 2` sob o codificador relaxado, é a chamada `WriteBase64String`. A medida `M8`, 17.606 contra 16.386, é o mesmo conteúdo escrito como string comum sob o codificador padrão. O especialista mediu que as chamadas de base64 do escritor não passam pelo codificador em 8 de 8 linhas, o que explica os dois resultados de uma vez: o comprimento do campo não depende do codificador, depende de qual chamada emite o campo. Logo `D4` não é decisão de serialização, é decisão de arquitetura sobre uma chamada, e pinar `UnsafeRelaxedJsonEscaping` não protege nada aqui.

Confirmei a aritmética do vetor de negação de serviço por conta própria. O padrão de 3 bytes `FB EF BE` repetido produz base64 formada só pelo caractere de adição, e cada ocorrência escapa para 6 bytes, portanto o campo mede 8 vezes o conteúdo cru: 3.750.000 bytes crus dão exatamente 30.000.000 bytes de campo. Os 3.737.200 do especialista são esse número com a reserva de envelope descontada. O vetor é real, é acionável por conteúdo de remetente e não exige nenhuma anomalia de protocolo.

E aqui está o achado que muda o que o portão precisa verificar. O corpus determinístico de hoje não contém um único caractere escapável. Medi a base64 do gerador do cenário, linhas 333 a 343, sobre 4 MiB: 5.592.408 caracteres, zero ocorrências do sinal de adição e zero da barra. Sob a chamada errada, com o corpus atual, a expansão é exatamente 0 por cento. Ou seja: a igualdade entre comprimento declarado e bytes recebidos, o teto de corpo e a aritmética do campo, as três, nascem verdes com qualquer das duas chamadas enquanto o corpus for o padrão ASCII repetido. Sem um perfil adversário no corpus, a correção de `D4` entra no portão já pertencendo às 23. O oráculo de `D4` é, portanto, um par: fixar a chamada, e medir o campo sobre conteúdo cuja base64 seja inteiramente escapável.

#### 2.3 O teto de valor único não sobrevive, e a razão sozinha também não

Não sobrevive. Um teto de alocação por operação em valor único não distingue constante no tamanho de proporcional ao tamanho, porque um ponto não tem inclinação. A armadilha do escritor JSON é a demonstração: 66.403.386 bytes por operação caindo para 118.019 com descarga por segmento, mesmo corpo e mesmo digest, e o especialista descobriu isso por acidente, o que é a prova de que a grandeza não estava sob vigilância.

A mesma simetria vale contra a proposta do especialista lida isoladamente: uma verificação apenas de razão entre dois tamanhos aprova sem reclamar uma implementação que aloque 66 MB nos dois tamanhos, porque a razão dela é 1,0. A forma que sobrevive é a união das duas: termo constante com teto absoluto e coeficiente por byte próximo de zero, verificado em pelo menos dois tamanhos separados por fator cinco ou mais. É a forma afim do especialista com o teto de invariância do engenheiro ocupando o segundo parâmetro dela.

O que não pode ser escrito hoje é o número do termo constante, por um motivo que nenhuma das duas posições isolou: existem três medições de alocação do streaming em circulação e elas medem unidades de trabalho diferentes. São 590 bytes por operação na baseline versionada do repositório, onde o braço só alimenta um hash sobre memória residente; 65.722 bytes na medição do codificador isolado do engenheiro; e cerca de 116 KB no corpo completo do especialista. Nenhuma das três é o braço funcional deste repositório, que ainda não existe. Pela mesma razão, a amplificação 9,33 e a assinatura de falha 15,97 pertencem ao corpo funcional com base64 e JSON, e não ao braço de bufferização do repositório, cuja amplificação medi em 4.198.243 sobre 4.195.328, ou seja 1,0007. Escrever qualquer desses números como teto hoje é consagrar a implementação de outra pessoa.

#### 2.4 Server GC não é uma configuração, é uma família

O achado do especialista promove `L1` de lacuna administrativa a dependência técnica, e o efeito sobre a proposta de gravar sob Server GC é este: gravar sob Server GC é necessário e não é suficiente. A contagem de heaps deriva da cota de CPU visível ao runtime e move o percentil 95 em 1,85 vezes entre 2 e 22 heaps, que é mais do que a maior parte das regressões que o portão deveria pegar. Uma baseline que diga apenas Server GC descreve uma família de configurações, não uma configuração.

Portanto sim, a baseline passa a exigir também a contagem de heaps, com três exigências adicionais. Primeira, a contagem precisa ser pinada e não observada: verifiquei no pacote de referência instalado, 10.0.10, que `GCSettings.IsServerGC`, `GC.GetConfigurationVariables` e `GC.GetGCMemoryInfo` existem, mas nenhuma dessas superfícies devolve a contagem efetiva quando ela é derivada da máquina, então só o pino torna a referência reproduzível fora do host que a gravou. Segunda, o valor do pino depende de `L1`, porque o pino honesto é a cota do alvo; enquanto `L1` não existir, o pino é provisório e a baseline precisa declarar no próprio arquivo que é provisória. Terceira, modo e pino entram como campos gravados e como verificação de igualdade, senão a divergência volta a ser silenciosa, que é exatamente o estado em que a árvore de trabalho está agora.

O que atravessa a mudança de modo sem alteração, e por isso pode ser fixado hoje: a alocação por operação e sua razão, com variação de zero por cento medida pelo especialista; o comprimento do corpo; a igualdade de digest do corpo; e a igualdade entre comprimento declarado e bytes recebidos. Tudo o mais que `VER-020` nomeia, latência, heap, working set, coleções e CPU, fica sem calibração possível até `L1`.

### 3. Conjunto fechado de verificações do portão

Dezoito verificações no lugar de trinta e seis. Cada uma tem falsificador nomeado. Onde a verificação já existe no harness em reescrita, digo onde, para que a síntese não mande fazer o que já está sendo feito.

#### Grupo A. Absolutas e independentes do alvo de implantação

Valem sem referência gravada e sem alvo declarado. Uma rodada isolada consegue reprová-las.

1. **Teto de corpo.** O corpo submetido de cada perfil, contado depois da codificação pelo mesmo escritor que o emite, fica abaixo de 30.000.000 bytes. Falsificada por: um perfil cujo corpo contado alcance o teto. Já existe em `ProviderTransferInvariants.cs`, linhas 39 a 43.
2. **Comprimento declarado igual a bytes recebidos.** O comprimento declarado no corpo é igual à contagem de bytes que o servidor falso recebeu, por operação. Falsificada por: duas funções de escrita diferentes entre a passada de contagem e a passada de emissão. Não existe hoje: `ContentLengthDeclared` e `CapturedBodyBytes` são gravados no registro do braço e nenhuma invariante os confronta.
3. **Comprimento do campo do anexo.** O campo mede exatamente `4 × ceil(n/3) + 2`, verificado também no perfil adversário. Falsificada por: chamada de escrita que roteie a base64 pelo codificador, que multiplica o campo por até seis. A aritmética já existe, linhas 32 a 36; o perfil adversário não existe, e sem ele a verificação é vazia.
4. **Corpo idêntico entre os braços.** Cada braço entrega um único digest distinto na rodada, e os três braços entregam o mesmo. Falsificada por: qualquer braço que construa corpo diferente. Já existe, linhas 28 e 61. Absorve as três verificações atuais de digest do corpus, que provavam igualdade da entrada e não da saída, que é o que `NFR-008` pede.
5. **Ida e volta do anexo.** O servidor falso decodifica o campo, o digest dos bytes decodificados é igual ao da fonte, e nome, tipo, disposição, ordem e contagem conferem. Falsificada por: truncamento, preenchimento no meio do fluxo, ordem trocada. Já existe, linhas 82 a 106.
6. **Forma afim da alocação.** Alocação por operação do candidato medida em dois tamanhos separados por fator cinco ou mais, com razão entre as duas medidas no máximo 2,0. Falsificada por: implementação cuja alocação acompanhe o tamanho, que é exatamente a armadilha do escritor sem descarga por segmento. O termo constante entra quando existir, e enquanto não existir o relatório declara que só a parte de razão está ativa.
7. **Coleções de geração 2 por operação iguais a zero no candidato**, acompanhada de prova por mutação de que a verificação fica vermelha sob o modo de GC da rodada. Falsificada por: qualquer materialização do corpo no LOH. Sem a prova de mutação a verificação não entra, porque é a candidata natural a virar vacuamente verdadeira. As contagens de geração 0 e 1 saem do portão e ficam no relatório: na baseline versionada as três gerações são iguais em todos os braços, 0,25 e 0,25 e 0,25 no `buffer` e zero nos outros dois.
8. **Pausa de coleta por operação igual a zero no candidato**, lida no processo inteiro. Falsificada por: uma coleção completa durante o braço. Válida somente enquanto os braços forem medidos em sequência, o que o cenário faz hoje, linhas 43 a 61: isso passa a ser invariante a preservar e não coincidência.
9. **Limpeza sem resíduo.** Nenhum arquivo temporário residual e nenhuma raiz residual ao fim de cada braço, reportados como verificação vermelha e não como exceção. Falsificada por: um arquivo vazado. Já existe na forma correta no harness em reescrita, linhas 108 a 115, e é a correção das três verificações mortas de hoje.
10. **Concorrência observada igual à configurada, exatamente.** Falsificada por: rodada que nunca alcançou o paralelismo pedido. Substitui a tautologia atual.
11. **Contagem de amostras por braço e por perfil de pelo menos 200.** Falsificada por: rodada configurada abaixo disso. Nenhum teto é escrito sobre o percentil 99, porque estabilizá-lo exigiria mil amostras, que é o limite configurável.

#### Grupo B. Razão ou igualdade contra a baseline versionada

Exigem referência gravada. As igualdades de configuração estão aqui porque só existem para tornar a razão comparável.

1. **Igualdade de configuração.** Bytes de payload, bytes de envelope, operações por braço, concorrência configurada, identificador do perfil e digest do corpus, todos iguais aos da referência. Falsificada por: comparar uma rodada com referência de outra configuração. Consolida cinco verificações atuais e dispensa as três por braço, que eram identidades.
2. **Igualdade de coletor.** Modo de GC e contagem pinada de heaps iguais aos gravados. Falsificada por: gravar sob um coletor e comparar sob outro, que é o estado da árvore de trabalho agora, ou comparar entre contagens de heaps diferentes, cujo efeito medido no percentil 95 é de 1,85 vezes.
3. **Versão de formato da referência.** Igual à esperada. Falsificada por: uma referência gravada na forma antiga. Já existe e reprova com exceção, linhas 72 a 75 do carregador, e é o dispositivo anticegueira de toda esta mudança.
4. **Razão de alocação contra `buffer`**, no máximo a razão de referência ampliada pela tolerância, somente para `streaming` e `spool`. Falsificada por: regressão de alocação no candidato. A identidade do `buffer` contra si mesmo sai.
5. **Razão de vazão contra `buffer`**, no mínimo a razão de referência reduzida pela tolerância, mesma restrição de braços.
6. **Razão do máximo observado de latência contra `buffer`**, com tolerância derivada desta métrica. Falsificada por: regressão de latência. Usa o máximo e não p95 nem p99: com 200 amostras a dispersão do p95 ainda é de 1,92 vezes, de modo que duas rodadas honestas da mesma configuração discordariam entre si.
7. **Razão de pico amostrado de heap e de working set contra `buffer`.** Falsificada por: candidato cujo custo residente acompanhe a bufferização. Exige trocar antes os campos de delta por picos amostrados: o delta de working set do braço `streaming` na baseline versionada é negativo, menos 24.576 bytes, e grandeza que assume valor negativo não sustenta limite superior.

#### Saem do portão

As 23 incapazes, nas famílias da tabela 2.1. As três de digest do corpus por braço não saem por incapacidade e sim por absorção em A4 e A5, que provam mais. As contagens de geração 0 e 1 saem por redundância medida. Nenhuma verificação capaz de hoje perde cobertura: as cinco de cabeçalho viram B1, as três de digest viram A4 e A5, a de resíduo do `spool` vira A9 e as quatro razões viram B4 e B5.

#### Não entram hoje

Todo teto absoluto de latência, heap, working set, CPU, coleções por réplica e memória por réplica, além do termo constante de A6. Não é omissão, é `L1` e a inexistência do braço funcional neste repositório.

### 4. Parâmetros

#### Deriváveis hoje, com número e fonte

| Parâmetro | Valor | Fonte |
|---|---|---|
| Teto de corpo do provedor | 30.000.000 bytes, leitura conservadora | documentação do provedor, contexto imutável do brief |
| Conteúdo cru somado, com reserva de 100 KiB | 22.423.200 bytes | especialista, concordante com `F15` |
| Único perfil redondo sob a recomendação nas duas leituras | 7 MiB cru, campo de 9.786.712 bytes | aritmética conferida por mim nesta síntese |
| Comprimento do campo do anexo | `4 × ceil(n/3) + 2` | `M7` e as oito linhas do especialista, condicionado à chamada de escrita |
| Pior caso sob a chamada errada | oito vezes o conteúdo cru; 3.750.000 bytes crus dão 30.000.000 | conferido por mim |
| Caracteres escapáveis no corpus atual | zero em 5.592.408 | medido por mim sobre o gerador do cenário, linhas 333 a 343 |
| Blocagem | 49.152 bytes, maior da família que mantém entrada e saída fora do LOH | `M6` |
| Coeficiente de alocação por byte do candidato | aproximadamente zero | duas medições independentes, escopos diferentes, concordantes na forma |
| Teto de razão de invariância entre menor e maior perfil | 2,0, contra assinatura de falha de 15,97 | engenheiro, com a ressalva de que 15,97 pertence ao corpo funcional |
| Amostras por braço | 200 no mínimo, 500 para teto próprio sobre p95, nenhum sobre p99 | especialista, duas mil subamostras |
| Coleções por geração | iguais nas três gerações em todos os braços | baseline versionada, conferida por mim |
| Identidade de I/O lógico do `spool` | 50.343.936 igual a `(4.194.304 + 1.024) × 12` | baseline e cenário, conferido por mim |
| Amplificação do braço `buffer` deste repositório | 1,0007 | baseline, conferida por mim, separa o braço atual do corpo funcional |
| Superfícies de GC disponíveis | `GCSettings.IsServerGC`, `GC.GetConfigurationVariables`, `GC.GetGCMemoryInfo` | pacote de referência 10.0.10 instalado, conferido por mim |

#### Pendentes de trabalho próprio, sem entrada externa

| Parâmetro | Por que ainda não existe |
|---|---|
| Termo constante do teto de alocação | exige o braço funcional existir neste repositório; hoje há 590 B, 65.722 B e cerca de 116 KB, que medem unidades de trabalho diferentes |
| Tolerância desta métrica | a de hoje, 0,55, é herdada do cenário da trilha de auditoria e compartilhada por todos os portões da sonda |
| Método de amostragem de pico de heap e de working set | não existe; é o substituto obrigatório dos deltas |
| Custo marginal do braço `spool` funcional | ninguém contou as linhas do braço que produz corpo byte a byte igual |

### 5. Entradas que faltam, com dono

| Entrada | Dono | O que ela destrava |
|---|---|---|
| `L1`, alvo de implantação: limite de memória do contêiner, cota de CPU, réplicas, concorrência em voo por réplica e taxa oferecida | usuário, como dono da decisão, e o dono da infraestrutura ainda não escrita em Terraform | todo teto absoluto de ambiente, o pino definitivo de heaps e a baseline definitiva |
| `L2`, quantidade e tamanho de anexos por notificação | produto, que nesta delegação é o usuário | `E4` e qualquer afirmação de que os perfis representam produção |
| `L4`, as duas ambiguidades de unidade | documentação do provedor, sem chamada cobrável | a diferença entre 30.000.000 e 31.457.280; enquanto aberta, a leitura conservadora é obrigatória |
| Custo marginal do braço `spool` funcional em linhas | a sessão que reescreve o harness agora | `D3b` |
| `L3`, versão de HTTP negociada | documentação do provedor | nada mais nesta mesa; deixou de bloquear `D4` |

### 6. Dissenso preservado

**`dotnet-engineer` mantém `C1`, descartar o `spool` agora.** Preservado nominalmente. O argumento que sobrevive é o da superfície permanente que apodrece por ficar inativa. O argumento que não decide é o do custo marginal: ele está certo de que a baseline é regravada de qualquer modo, e é exatamente por isso que o argumento não separa as duas alternativas.

**`dotnet-specialist` mantém `C3`, reclassificar sem remover.** Um dos argumentos dele fica riscado aqui: "descartar paga a invalidação da baseline para chegar onde a reclassificação chega de graça" perde o objeto, porque as duas posições concordam que a baseline é regravada de qualquer modo, o engenheiro dizendo que ela não é reaproveitável e o especialista dizendo que ela precisa ser regravada sob a contagem de heaps do alvo. A invalidação já está paga por `D2`.

**Por que a mesa resolve `D3a` e não fabrica consenso em `D3b`.** As duas posições precificaram objetos diferentes sem perceber. As setecentas linhas do engenheiro incluem código produtivo, ensaio de crash, diretório protegido, cota, controle de propriedade e varredura de resíduos, isto é, o `spool` promovível, que `D3a` já elimina por consenso das duas partes. O custo "de graça" do especialista aponta o braço que já existe na sonda atual, que não é o braço funcional. O custo marginal do braço funcional, que é o objeto real de `D3b`, ninguém mediu.

**O que resolve `D3b` a favor de `C3`**: a assimetria de reversibilidade, nas palavras do próprio engenheiro, "descartar o `spool` é irreversível na prática, porque reabrir significa escrever as setecentas linhas do zero", somada ao critério ordenador 5 com os tempos do especialista, matriz abaixo de dois minutos e um terceiro braço acrescentando metade disso. Sob dominância técnica que as duas posições medem igual e sob baseline regravada nos dois caminhos, a alternativa reversível vence enquanto o custo de mantê-la for pequeno e limitado.

**Gatilho falsificável que vira `D3b` para `C1`**: se o braço `spool` do harness funcional exigir diretório protegido, cota, controle de propriedade ou varredura de resíduos para produzir corpo byte a byte igual, o custo deixa de ser marginal e a recomendação passa a ser descartar. Quem pode medir é a sessão que escreve o harness, contando linhas.

**Condição de reabertura de `D3a`**, adotada literalmente do engenheiro porque o especialista chegou à mesma: reabre se o envio exigir repetição do corpo numa retentativa e a fonte não puder ser relida. Hoje as duas metades falham, porque a fonte é um objeto no armazenamento e uma nova leitura é repetível.

**Dissenso menor preservado**: o engenheiro considera a verificação de geração 2 igual a zero suscetível de virar vacuamente verdadeira sob Server GC. Não escolhi entre as posições: incorporei a prova por mutação como parte da verificação A7, de modo que a objeção dele vira condição de entrada.

### 7. Sequência de execução recomendada

Pode ser feito antes das entradas que faltam, nesta ordem:

1. Subir a versão de formato da baseline antes de acrescentar qualquer verificação. Acrescentar verificação antes disso permite uma rodada comparando contra a referência velha, e o carregador só reprova o que não reconhece.
2. Fixar a chamada de escrita e acrescentar o perfil adversário de conteúdo. Precede tudo no eixo de corpo, porque sem ele as verificações A1, A2 e A3 nascem verdes.
3. Concluir o harness funcional em curso e acrescentar a única invariante do grupo A que não está nele, a A2.
4. Remover as 23 incapazes e instalar A4, A5, A9, A10 e A11.
5. Decidir o escopo do coletor. A propriedade está no arquivo de projeto e atinge as cinco baselines do mesmo executável. Ou ela viaja no mesmo commit que regrava as cinco, ou o coletor passa a ser escopado por rodada. O estado atual da árvore de trabalho não é uma terceira opção.
6. Gravar modo e pino provisório de heaps na baseline, rotulá-la como provisória no próprio arquivo e ligar B2.
7. Subir as amostras para 200, trocar delta por pico amostrado, derivar a tolerância desta métrica e ligar B6 e B7.
8. Blocagem em 49.152, A6 na parte de razão, A7 com prova por mutação e A8 preservando a medição sequencial dos braços.

Não pode ser feito antes:

- Nenhum teto absoluto de latência, heap, working set, CPU ou memória por réplica. Depende de `L1`, e o pino definitivo de heaps é parte de `L1`.
- A baseline definitiva. A do passo 6 é explicitamente provisória, e comparar contra ela não autoriza conclusão sobre produção.
- O termo constante de A6. Depende do braço funcional existir aqui, não de entrada externa.
- O perfil produtivo e qualquer afirmação de representatividade. Depende de `L2`.
- A promoção de qualquer braço. Depende de `L1` e `L2` juntos, porque `NFR-008` exige orçamento aprovado, e nada nesta mesa aprova orçamento.

### 8. Entrega ao dono da decisão

Exigem decisão humana, e nenhuma delas foi tomada aqui:

1. `L1`, cinco números: limite de memória do contêiner, cota de CPU, réplicas, concorrência em voo por réplica e taxa oferecida.
2. `L2`, dois números: quantidade e tamanho de anexos por notificação.
3. `L4`: aceitar a leitura conservadora como política permanente, ou autorizar a verificação na documentação do provedor.
4. `D3b`: ratificar a recomendação de manter o braço reclassificado, ou acolher o dissenso do engenheiro e descartar. Aviso de irreversibilidade: descartar exige reescrever do zero, e manter é reversível a qualquer momento.
5. Escopo do coletor. Esta escolha tem efeito fora desta mesa: as quatro outras baselines versionadas foram gravadas sob Workstation GC e passam a ser comparadas sob Server GC sem que nada as avise.
6. Aceitar que nenhuma promoção ocorre nesta rodada e que a baseline intermediária é provisória e rotulada como tal.
7. Confirmar que o custo de tempo da matriz ampliada é aceitável: abaixo de dois minutos por rodada com 200 amostras, mais o perfil adversário.

O que esta síntese não faz: aprovar orçamento de runtime. `NFR-008` continua sem número aprovado, e nenhum dos itens acima o substitui.
