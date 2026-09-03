---
language: pt-BR
---

# Recibo de decisão: identidade íntegra imutável do anexo sob custódia

**Tarefa**: 16. **Data**: 2026-09-02. **Veredicto da mesa**: `RECOMMEND`.
**Mediador**: `dotnet-architect`. **Participantes**: `dotnet-engineer` e
`dotnet-specialist`, com posições independentes.
**Dono da decisão**: usuário, que autorizou previamente as decisões desta fatia.

## Decisão

Registro de geração append-only 1:N sob `AttachmentManagement.Infrastructure`,
com a prova dos bytes exclusivamente na linha da geração, congelamento por
linha que nasce completa somado a `AfterSaveBehavior.Throw`, e o gatilho de
rejeição de mutação entrando apenas na Tarefa 35.

Uma linha por geração efetivamente capturada, contendo store lógico, chave,
versão, algoritmo, digest, comprimento e instante da captura. Nenhuma sofre
`UPDATE`. Sem linha de intenção. A liberação, na Tarefa 18, é linha própria, o
que preserva o zero de tipos alterados.

Três decisões de forma que a mesa fixou porque nenhum parecer as fechava:

1. A prova vem exclusivamente da passagem de verificação, ou seja, da leitura
   pela versão fixada logo após a escrita, nunca da contagem feita durante a
   escrita. Um número calculado sobre o que entrou não detecta nada sobre o que
   ficou durável.
2. `MarkReceived` recebe o comprimento verificado, não os bytes contados na
   escrita. A assinatura não muda; muda qual número o chamador passa.
3. A escrita condicional é invariante do adaptador, decidida dentro da operação
   de gravação e não parâmetro da chamada.

## Medições que sustentam a decisão

### Persistência, contra PostgreSQL 17 real

`AfterSaveBehavior.Throw` tem quatro comportamentos, não dois. Entidade
rastreada com valor reatribuído lança dentro de `SaveChanges`; propriedade
sombra por `Entry(e).Property("...")` lança igual; **`Update(entidade
destacada)` com valor adulterado não lança e descarta a alteração em
silêncio**; e `ExecuteUpdate` atravessa a guarda e reescreve o valor durável.

Mensagem literal da exceção, útil como oráculo:

```text
The property '...' is defined as read-only after it has been saved, but its
value has been modified or marked as modified.
```

Consequência direta: um oráculo escrito com `Update(destacada)` passa
igualmente com e sem a proteção, portanto não prova nada. O oráculo tem de
mutar entidade rastreada ou marcar a propriedade como modificada.

`Enum.GetNames<PropertySaveBehavior>()` devolve exatamente `Save, Ignore,
Throw`, e a varredura por reflexão do assembly não encontrou nenhum recurso de
gravação única condicionada ao estado anterior. Não existe "gravar uma vez e
congelar" no EF Core.

`EnsureCreated` e `CreateTablesAsync` criam zero gatilhos, inclusive quando o
modelo declara `HasTrigger`. Nenhuma rota de metadados do EF materializa a
guarda de banco.

Um contexto, um `SaveChanges`, um commit, um rollback: quando o `INSERT` da
linha filha viola índice único, a atualização da linha pai não fica durável.

### Provedor, contra LocalStack 4.4, a mesma imagem da fixture

A escrita condicional não é guarda de imutabilidade. Com um marcador de
exclusão como versão corrente, o objeto não existe para efeito da condição, a
escrita passa e cria segunda geração, e a primeira continua legível. O marcador
é produzido pelo próprio caminho de compensação vigente.

O literal `null` existe como localizador. Em bucket suspenso, a leitura devolve
a cadeia `null` de quatro caracteres, que passa em verificação de vazio e é um
ponteiro que a próxima escrita move: gravado `alpha`, depois `omega`, a leitura
por esse localizador devolveu `omega`.

**Uma única chamada de escrita cuja resposta se perde na rede deixou onze
gerações duráveis, e a aplicação não recebeu nenhuma versão.** Com o limite de
retentativa em cinco foram dezesseis. O mesmo cenário conduzido por cliente HTTP
puro produziu uma, o que atribui a repetição ao SDK e não ao runtime. A escrita
condicional limita a amplificação a uma geração, porque a segunda tentativa
recebe recusa por pré-condição.

Exclusão sem versão não apaga: cria marcador, devolve sucesso, e a geração
permanece durável e legível. Exclusão por versão exata remove, e a leitura
seguinte falha por versão inexistente.

Checksum afirmado pelo provedor chega em leitura e em cabeçalho, e é assertiva
de terceiro. A validação de resposta do SDK detecta corrupção de um byte e é
rede barata de detecção, mas não é prova de que os bytes são os registrados.
**Leitura por faixa não é validada** e entregou bytes alterados sem exceção.

Truncar o corpo lança, tanto com fechamento abrupto quanto com fim limpo, então
não existe leitura parcial silenciosa no consumo integral. Mas verificar e
enviar na mesma passagem entrega todos os bytes ao chamador antes de o veredicto
do digest existir, porque o veredicto de um fluxo só existe depois do último
byte. Preservar a cláusula de zero chamadas ao provedor exige duas passagens
sobre a mesma versão fixada, sem materialização.

## Alternativas rejeitadas

| Alternativa | Motivo |
|---|---|
| Prova copiada para o agregado na liberação | É `UPDATE` em linha preexistente, a forma sem congelamento aplicável, e destrói o zero de tipos alterados que sustenta a própria opção. Nenhum consumidor a exige, e o contrato publicado proíbe digest |
| Linha de intenção antes da escrita | Não habilita nada, porque a chave do objeto já é derivável de estado durável gravado no registro, e o caminho de upload não alcança a escrita sem essa linha carregada. Cobra um segundo commit no caminho feliz de todo upload |
| Colocação um para um | O provedor aceita segunda geração sob a mesma referência, medido. A colocação um para um só tem duas saídas, perder a informação ou sobrescrever a linha, e sobrescrever é o que o congelamento proíbe. Com o gatilho da Tarefa 35 fica sem caminho de retentativa |
| Congelamento só na aceitação da validação | Nenhum oráculo de congelamento dentro da Tarefa 16 |
| Propriedade sombra na tabela do agregado | A invariante não tem onde morar, o congelamento por mapeamento está eliminado sobre linha preexistente, e o gatilho não é observável antes da Tarefa 35 |
| Cadeia opaca dentro do agregado | Toda projeção do agregado vira vetor de vazamento, e a regra de arquitetura não ajuda porque uma cadeia de caracteres não é dependência de namespace |
| Metadado, ETag ou checksum de resposta como digest de registro | Assertiva de terceiro sobre os bytes |
| Preflight em passagem única | Abandona a cláusula de zero chamadas ao provedor |
| Absorver reconciliação de órfão na Tarefa 16 | Expansão de escopo em tarefa que já é a maior da fatia, com donos declarados nas Tarefas 32 e 33 |

## Decisões do dono, resolvidas

O usuário autorizou previamente as decisões desta fatia. Ficam registradas:

1. **Versionamento em produção**: seguir com a implementação. O comportamento de
   falha fechada torna o estado anterior à habilitação seguro, porque sem
   versionamento a resposta não traz versão e o upload recusa. A habilitação
   efetiva pertence à Tarefa 36, que é dona da habilitação progressiva, e entra
   como obrigação de rollout com custo de armazenamento acumulativo e política
   de ciclo de vida.
2. **Expansão de escopo**: confirmada a recusa do mediador. A Tarefa 16 absorve
   apenas a preservação da escrita condicional como invariante do adaptador. O
   mecanismo de reconciliação permanece nas Tarefas 32 e 33.
3. **Estatuto da prova dos bytes na fronteira**: adotada a leitura do mediador.
   A coluna de responsabilidade da tabela de fronteiras não é exclusiva, porque
   cada linha tem coluna de proibições própria e a do domínio não lista digest.
   Retirar a prova do agregado vale por custo de acoplamento, não por
   conformidade.
4. **Errata do passo 5 do experimento da Tarefa 6**: confirmada. Materializar o
   conjunto completo significa resolver todos os membros do conjunto de anexos,
   não bufferizar os bytes. A verificação alimenta o hash sem materializar, em
   passagem separada da passagem de envio. O experimento está fechado e não é
   editado; a errata vive aqui e é entrada da Tarefa 27.
5. **Aceitação da Tarefa 16**: reescrita para declarar o que ela realmente
   prova. Duas cláusulas da redação anterior só são verificáveis nas Tarefas 18
   e 29, e mantê-las produziria portão infalsificável.
6. **Retenção das linhas de geração**: fica para a Tarefa 33.
7. **Linha de intenção**: volta à mesa na Tarefa 32, como otimização de custo
   de varredura, com o custo de segundo commit já medido.

## Experimento de validação

Pré-condição: versionamento habilitado na fixture, e nenhuma instrução de
esquema de guarda executada nela.

| # | Oráculo | Mutação que tem de reprová-lo |
|---|---|---|
| 1 | Falha de commit forçada, seguida de retentativa, produz duas linhas de geração com versões distintas sob a mesma referência, e apenas uma referenciada pelo estado recebido | Trocar a colocação um para muitos por um para um |
| 2 | Recarregar a linha da geração rastreada, reatribuir a versão e persistir lança | Remover o comportamento de somente leitura após gravação |
| 3 | Descarte remove a geração e a leitura seguinte falha por versão inexistente | Descartar sem versão, que deixa marcador onde se esperava exclusão |
| 4 | Bucket suspenso: o localizador capturado é rejeitado e nada é persistido | Capturar o localizador da leitura em vez da escrita, gravando o literal `null` |
| 5 | O digest registrado diverge do afirmado pelo provedor quando eles diferem | Substituir o digest recalculado pelo checksum da resposta |
| 6 | Bucket sem versionamento: upload falha fechado, a linha permanece aguardando upload, nenhuma identidade persistida | Aceitar localizador nulo |
| 7 | O localizador semeado não aparece em fragmento de resposta nem no log capturado | Remover a representação textual sobrescrita do localizador |

Duas exclusões deliberadas. Não são usadas as cinco superfícies da varredura de
sentinelas que nenhum código de anexo alcança hoje, porque afirmar ausência
nelas é asserção constantemente verdadeira. Não há oráculo de gatilho, porque a
fixture não pode executar instrução de esquema que a produção não tenha.

Condição de superdimensionamento preservada: se o oráculo 1 não produzir duas
linhas em nenhuma sequência realista de falha, a colocação um para um bastaria.

## Dissenso preservado

1. **Peso do critério de nova referência.** O especialista declarou que a
   divergência é de peso e não de fato, e que rebaixá-lo empataria a colocação
   um para um. O peso da abertura foi mantido e não revisado sob pressão do
   resultado.
2. **Placares não comparáveis.** Os dois participantes pontuaram em escalas
   diferentes. Ambos elegeram a mesma opção, e os números não foram fundidos.
3. **Mecanismo interno da retentativa do SDK.** Medido o efeito, onze e
   dezesseis gerações, não identificada a causa. Viaja para a Tarefa 32.
4. **Estatuto da prova fora do agregado.** O engenheiro considera que a
   fronteira literal a exige fora; o mediador recusou essa leitura. A emenda
   vale por custo, não por conformidade.

## Ressalva obrigatória

A Tarefa 6 foi fechada por decisão do dono em 2026-09-01 com a matriz
comportamental incompleta, e nenhum dos doze casos negativos do gate foi
executado. Nenhum artefato da Tarefa 16 pode declarar provado: separação de
privilégios entre os papéis de upload, validação e descarte; negação de exclusão
por versão aos papéis normais; incompatibilidade de prefixo, chave gerenciada e
contexto de criptografia; falha fechada diante de chave desabilitada ou negada;
preservação de leitura após rotação; comportamento dos modos de retenção; e
ausência de bytes, nome original e digest nas superfícies comuns de log e na
trilha do provedor.

A fidelidade de chaves gerenciadas no dublê local é nula, porque o experimento
da Tarefa 6 registrou leitura bem-sucedida com a chave desabilitada.

**A decisão protege a identidade dos bytes contra troca acidental e contra
retorno da geração errada. Ela não protege contra um principal com privilégio
amplo, e essa continua sendo a lacuna que só uma execução autorizada em nuvem
real descartável fecha.**

## Errata de 2026-09-02, posterior à implementação

Quatro correções, todas do mediador, provocadas por medição do construtor
durante a implementação. Nenhuma reabre a escolha entre colocação um para um e
um para muitos, nenhuma altera o conjunto autorizado de escrita, e nenhuma
altera as decisões do dono.

1. **O oráculo 1 original afirmava estado inalcançável.** Sob commit único,
   nenhuma das seis sequências realistas de falha produz duas linhas de geração
   sob a mesma referência: o caminho feliz grava uma; commit falho com descarte
   bem-sucedido e retentativa grava uma, porque o rollback levou a primeira
   linha; commit falho com descarte falho deixa zero linhas e uma geração órfã,
   porque a retentativa bate na escrita condicional; corrida entre produtores
   recai nos anteriores; resposta perdida e morte do processo entre a escrita e
   o commit deixam zero linhas por construção. A condição de
   superdimensionamento registrada neste recibo disparou, e o erro é da
   síntese, não da implementação.

   Enunciado novo: falha de commit forçada, seguida de retentativa
   bem-sucedida, deixa exatamente uma linha de geração, a versão registrada é a
   da segunda escrita, e a versão da primeira responde versão inexistente no
   provedor. Falsificadores: dividir a gravação em dois commits, que faz
   aparecer a segunda linha; e descartar sem versão, que deixa a primeira
   versão legível. Acrescenta-se o oráculo 1b: retentativa sem descarte
   bem-sucedido devolve conflito, deixa zero linhas e mantém o anexo aguardando
   upload.

2. **O fundamento da colocação um para muitos mudou.** A justificativa
   registrada era que o provedor aceita segunda geração sob a mesma referência.
   Ele aceita, mas o desenho final torna isso inalcançável, porque o descarte
   por versão exata não cria marcador e era o marcador que reabria a escrita
   condicional. Sob commit único a diferença entre as duas colocações é apenas
   um índice único sobre a chave estrangeira do agregado. Ele não entra pelo
   custo assimétrico do erro: afirmaria no máximo uma geração por anexo para
   sempre, afirmação sobre as Tarefas 18, 19 e 32 que ninguém mediu, e somado
   ao gatilho da Tarefa 35 travaria a referência sem inserção e sem exclusão, o
   que está medido. A invariante já é imposta em código pelo controle de estado
   do chamador.

3. **A objeção à colocação um para um combinada com o gatilho deixa de valer.**
   Ela pressupunha linha sobrevivente a captura falha, que o commit único torna
   impossível.

4. **A justificativa da representação textual sobrescrita está errada no
   mecanismo.** A representação sintetizada de um `record` imprime somente
   membros públicos, e store, chave e versão são internos, portanto o tipo já
   renderizava vazio sem o override. Ele permanece defensável como contrato
   positivo, fixando a renderização vazia contra uma mudança futura de
   acessibilidade, e é assim que deve ser descrito. O falsificador do oráculo 7
   passa a ser registrar a versão numa mensagem do log de upload.

**Forma final de gravação**: commit único. A entidade da geração entra no mesmo
contexto antes da única chamada de persistência. Uma transação, um commit, um
rollback. A divisão em dois commits foi recusada porque abre a janela que
produz a segunda linha, e a segunda linha seria a única coisa a justificar a
forma escolhida, o que fecha a justificativa em círculo; porque torna durável e
legítimo um estado de geração gravada com anexo ainda aguardando upload, que as
Tarefas 17, 18 e 27 passariam a interpretar; e porque não fecha janela alguma,
apenas move a janela de morte para uma posição pior.

## Decisões residuais, resolvidas em 2026-09-02 após a correção

1. **Comportamento de exclusão da chave estrangeira**: mantido `Restrict`. Ele
   não é da mesma classe do índice único recusado. O índice afirmaria, de forma
   irrecuperável sob o gatilho da Tarefa 35, no máximo uma geração por anexo
   para sempre; o comportamento de exclusão apenas impede que um anexo suma
   deixando linha de geração órfã, e hoje nada apaga anexo. Como o módulo ainda
   não tem migração, a restrição não existe no banco, portanto a decisão é
   reversível sem custo. A interação com o gatilho append-only vira entrada
   explícita da Tarefa 35, que é onde ela passa a ser real.

2. **Cláusula que recusa o literal `null` como localizador**: mantida como
   defesa em profundidade, com a atribuição registrada. Ela é hoje inalcançável
   em produção, porque a captura vem da resposta da escrita e essa resposta
   devolve nulo, não o literal. Ela só fica alcançável se a origem da captura
   migrar para a leitura, que é uma alteração de uma linha. O falsificador do
   oráculo 4 é, por isso, um par: cada metade sozinha deixa o teste verde.
   Retirar uma guarda porque ela é inalcançável hoje é exatamente como a guarda
   deixa de existir no dia em que a alcançabilidade muda, e o custo de mantê-la
   é uma comparação.

3. **`Matches` sem chamador de produção**: aceito. O chamador chega com o
   preflight da Tarefa 27, e o método já é exercitado por oráculo de unidade e
   pelo oráculo do digest.
