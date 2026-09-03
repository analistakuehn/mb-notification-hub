---
language: pt-BR
---

# ADR-0019: Snapshot do manifesto aceito na notificação

**Status**: ACCEPTED

A decisão continua vigente. O sequenciamento de rollout e a cerimônia de migração foram corrigidos pela errata de 2026-09-02, logo abaixo do quadro de metadados.

| Campo | Valor |
|---|---|
| **Data** | 2026-08-31 |
| **Responsável** | `dotnet-architect` |
| **Audiência** | Arquitetura e engenharia dos módulos `Notifications` e `AttachmentManagement` |
| **Aprovação** | Usuário, por decisão explícita no ponto de controle de implementação de 2026-08-31 |
| **Escopo da decisão** | Local, forma persistida, leitura e ciclo de vida do snapshot do manifesto aceito |
| **Relacionadas** | [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md) |
| **Fontes** | [Especificação de desenvolvimento](SPEC-001/requirements/core/01-development-specification.md); [refinamento da fronteira](SPEC-001/refinements/00-refinement-consolidated.md); [corpus contratual](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md) |
| **Código afetado** | Linha `notifications.notification`; domínio e persistência de `Notifications`; pipeline, despacho e fallback; `NotificationEvidenceReader`, contrato `NotificationEvidence` e testes de evidência |

## Errata de 2026-09-02: serviço sem produção, sem V2 e com migração inicial única

Em 2026-09-02, o dono do produto declarou que o serviço é novo, não tem nada em produção, não existe V2 e não existe nada obsoleto, e que o mesmo vale para as migrações. Perguntado, decidiu esmagar todas as migrações em uma inicial, em vez de manter cadeia com migração aditiva.

O corpo abaixo não foi reescrito. **A decisão continua vigente por inteiro**: local, forma persistida, schema V1 do documento, matriz de leitura com presente, ausente e ilegível, composição congelada com elegibilidade relida no preflight, escrita única no `INSERT` do aceite, imutabilidade após a criação e proibição de cópia em `notification_attempt`. Nada disso depende de versão de contrato nem de cadeia de migrações.

### O que ficou errado, e como se lê agora

| Trecho do corpo | Estado vigente |
|---|---|
| Rollout, passo 8: habilitar progressivamente somente os ingressos V2 com anexos | Não existe V2. O manifesto viaja no contrato publicado vigente, conforme a [ADR-0021](ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md) |
| Rollout, passo 6: manter REST e Kafka V1 recusando anexos | O contrato vigente carrega o manifesto. A regra que sobrevive é outra: nenhuma superfície pode aceitar um corpo que nomeia o manifesto e prosseguir sem ele |
| Reconciliação da Tarefa 36: cobrindo ingresso V2 | Leia como o ingresso vigente, único |
| Matriz de combinações e a categoria leitor antigo | Não existe leitor antigo, porque não existe implantação anterior. A combinação proibida deixa de ser fase de rollout e passa a ser regra de ordenação dentro de uma implantação: nenhuma instância escreve documento não nulo antes que todas leiam |
| Migração aditiva, coluna acrescentada e preservação de linhas anteriores | A coluna nasce junto com a tabela, na migração inicial única. Não existem linhas anteriores a preservar |

### Cerimônia de migração, corrigida

A cerimônia prescrita no passo 1 do rollout e na reconciliação da Tarefa 35 existe para `ALTER TABLE` sobre pai particionado com dados e tráfego. A migração inicial única cria o schema em base vazia, portanto:

- **Suspenso**: a edição manual autorizada de `SET LOCAL lock_timeout = '3s'` imediatamente antes do DDL, e o ensaio de contenção com transação bloqueadora no pai e em uma partição. Criar tabela em base vazia não disputa bloqueio com ninguém.
- **Suspenso**: o teste sobre linhas nulas anteriores à coluna. A categoria não existe.
- **Preservado**: a coluna é anulável e criada sem valor padrão. Uma notificação sem anexos persiste SQL `NULL`, e ausência precisa continuar distinguível de documento ilegível.
- **Preservado**: o snapshot do modelo continua gerado pela ferramenta, sem edição manual, e a aplicação precisa migrar sem recusar por mudança pendente de modelo.
- **Preservado**: a coluna precisa existir no pai e em todas as partições, o que a migração inicial passa a garantir por construção em vez de por alcance de `ALTER TABLE`.
- **Volta a valer integralmente** no primeiro DDL executado sobre tabela com dados em produção. A suspensão vale enquanto o schema for criado do zero, não para sempre.

### Evidência que o esmagamento apaga

Este documento cita, como evidência, as migrações `20260825145151_AddNotificationAdmittedPlan` e `20260825223842_StoreCallbackPayloadOnce`, por caminho e linha. O esmagamento remove esses arquivos, e as citações deixam de resolver. A afirmação que elas sustentavam sobre o precedente de `jsonb` anulável no nível da notificação continua verificável pela configuração EF e pela migração inicial. A afirmação sobre o padrão manual de `lock_timeout` como SQL explícito perde sua evidência no repositório, e precisará ser restabelecida se a regra voltar a ser necessária.

## Resumo executivo

Persistir um único snapshot neutro e imutável do manifesto aceito em uma coluna `jsonb` dedicada, aditiva, anulável e sem valor padrão na linha particionada `notifications.notification`. O mesmo `INSERT` que aceita a notificação deve gravar o snapshot dentro da transação definida pela ADR-0018.

O snapshot congela identidade e composição do conjunto aceito. Ele não congela elegibilidade. Liberação, revogação, validade, integridade vigente e envelope são relidos com falha fechada no preflight imediatamente anterior a cada chamada ao provedor.

Pipeline, despacho, retry, fallback e evidência devem ler o único snapshot da notificação. `notification_attempt` não recebe cópia do conjunto.

## Contexto

`ER-009` exige que toda tentativa use o mesmo conjunto, nomes, tipos e identidades aceitos sem consultar metadado mutável. `ER-010` exige reler liberação, identidade e envelope imediatamente antes da chamada ao provedor. As duas regras se complementam: o aceite congela o que foi aceito, enquanto o preflight decide se o conjunto ainda pode ser usado.

O módulo já possui um precedente direto. `AdmittedDeliveryPlan` representa um snapshot imutável no nível da notificação, mapeado em `jsonb` anulável. Seu leitor distingue documento presente, ausente e ilegível, e o fallback relê elegibilidade em vez de congelá-la no documento. A migração desse precedente acrescentou uma coluna anulável sem valor padrão, preservando as linhas anteriores.

O precedente transfere a forma, mas não o comportamento de erro por inteiro. Um plano de entrega ilegível atualmente pode continuar sobre a política publicada com testemunha operacional. Um manifesto de anexos ilegível não pode consultar ou reconstruir silenciosamente o conjunto a partir do estado atual: isso substituiria a composição aceita e quebraria a correspondência entre aceite, tentativa e evidência.

O refinamento também constatou que pipeline, despacho e fallback já carregam a linha `notification` por identificador. Portanto, manter o manifesto nessa linha não exige uma consulta adicional nos caminhos observados. A tabela `notification` e a tabela de tentativas seguem um padrão particionado por mês. Alinhar uma tabela filha a esse padrão acrescentaria administração de partições; afastá-la do padrão exigiria uma exceção operacional justificada.

### Fatos observados

- `notifications.notification` já possui o snapshot `admitted_plan` como `jsonb` anulável.
- O leitor de `admitted_plan` diferencia presente, ausente e ilegível.
- O plano armazenado congela composição e ordem, mas a elegibilidade é relida no fallback.
- O `PipelineCommitWriter` grava o plano admitido na linha da notificação e cria a primeira tentativa sem copiar esse plano para a tentativa.
- O `FallbackRequestHandler` carrega a notificação e cria a tentativa seguinte a partir do estado já associado a ela.
- O leitor e o contrato atuais de evidência ainda não projetam o manifesto da notificação.

### Decisões vigentes de entrada

- A especificação atribui a `Notifications` o snapshot imutável usado por tentativa, retry, fallback e evidência, e separa esse snapshot da elegibilidade relida no preflight.
- O corpus contratual fixa que o snapshot de anexos contém referências opacas em ordem, identidade opaca de conteúdo, nome liberado, tipo de mídia liberado e comprimento liberado.
- A ADR-0018 determina que claim, snapshot, notificação, idempotência, outbox e auditoria compartilhem a mesma transação PostgreSQL.

### Objetivos

- Preservar para todas as tentativas exatamente a identidade e a composição aceitas.
- Manter uma única autoridade persistida do manifesto dentro de `Notifications`.
- Preservar linhas legadas e notificações sem anexos por meio de valor nulo.
- Distinguir ausência legítima de documento ilegível.
- Impedir que revogação ou vencimento sejam ocultados por um snapshot antigo.
- Permitir rollout aditivo com leitores implantados antes dos escritores.

### Fora do escopo

- Semântica da forma canônica e do hash idempotente.
- Política de validação, liberação, revogação ou validade.
- Estratégia de transferência ao provedor.
- Cardinalidade máxima, tamanho máximo ou orçamento de TOAST do manifesto.
- Forma física do estado proprietário de `AttachmentManagement`.
- Nome do tipo de domínio ou da coluna, que pertence à implementação dentro desta decisão.

### Direcionadores da decisão

1. Toda tentativa, retry, fallback e evidência precisa observar o mesmo conjunto aceito.
2. Elegibilidade é mutável e precisa ser relida imediatamente antes do efeito irreversível.
3. Os consumidores observados já carregam a linha da notificação.
4. A migração precisa preservar linhas anteriores e o caminho sem anexos.
5. O conjunto não pode ser perdido quando uma tentativa alcança estado terminal ou descarta conteúdo transitório.
6. O modelo particionado torna uma nova tabela e suas junções uma obrigação operacional adicional.

## Decisão

### Local e forma persistida

`Notifications` deve persistir o snapshot do manifesto aceito em uma única coluna `jsonb` dedicada na linha `notifications.notification`. A coluna deve ser:

- aditiva;
- anulável;
- criada sem valor padrão;
- mapeada no modelo EF de `Notification`;
- incluída no snapshot do modelo gerado pela ferramenta;
- escrita no mesmo `INSERT` que registra a notificação aceita.

O snapshot não deve ser criado nem atualizado por pipeline, despacho, retry ou fallback. Depois do aceite, esses caminhos somente o leem.

O documento persistido usa um envelope durável e neutro com `schemaVersion: 1` e `items`. `items` contém, na ordem aceita, para cada item:

- `reference`: referência pública opaca;
- `contentIdentity`: identidade opaca e íntegra do conteúdo;
- `name`: nome liberado;
- `mediaType`: tipo de mídia liberado;
- `length`: comprimento liberado.

O schema persistido V1 é normativo:

- a raiz é um objeto JSON com exatamente os membros `schemaVersion` e `items`, com nomes sensíveis a maiúsculas e minúsculas;
- `schemaVersion` é um número JSON inteiro igual a `1` e não aceita `null`;
- `items` é um array não vazio, cuja ordem é parte do snapshot;
- cada item é um objeto com exatamente `reference`, `contentIdentity`, `name`, `mediaType` e `length`;
- `reference`, `contentIdentity`, `name` e `mediaType` são strings JSON não vazias, não aceitam `null` e são preservadas sem trim, normalização de caixa ou normalização Unicode pelo leitor;
- duas referências iguais por comparação ordinal tornam o documento ilegível;
- `length` é um número JSON inteiro entre `0` e `9223372036854775807`, inclusive, e não aceita `null`;
- a ordem dos membros de um objeto é irrelevante, mas a grafia e a caixa dos nomes são exatas;
- qualquer membro adicional no envelope ou em um item torna o documento ilegível.

O limite de `length` corresponde a `System.Int64`, tipo já usado pelo contrato publicado de arquivo de evidência para comprimento. Ele impede que leitores escolham faixas numéricas diferentes sem prescrever o tamanho máximo de anexo aceito pelo produto.

O documento não contém bytes, base64, digest público, bucket, chave, `VersionId`, CMK, URL, credencial, estado interno de armazenamento ou tipo AWS.

### Momento da escrita

Pela ADR-0018, o claim transacional deverá devolver o snapshot integral. `Notifications` deve associá-lo à nova notificação e persistir ambos dentro da unidade transacional aprovada. Para uma solicitação com anexos, falha ao serializar ou persistir o snapshot aborta o aceite inteiro.

Uma solicitação sem anexos persiste valor nulo. O writer não grava documento vazio. Linhas criadas antes da coluna também permanecem nulas.

### Semântica de leitura

O leitor deve produzir três resultados distinguíveis:

| Resultado | Significado | Comportamento |
|---|---|---|
| Presente | Documento íntegro e reconhecido | Devolver a representação neutra e imutável na ordem persistida |
| Ausente | Valor SQL `NULL`: linha legada ou notificação aceita sem anexos | Continuar pelo caminho sem anexos, sem consulta a `AttachmentManagement` para reconstruir composição |
| Ilegível | A coluna contém documento que o leitor não reconhece integralmente | Registrar anomalia e falhar fechado em qualquer uso de anexos; nunca substituir pelo estado atual |

| Entrada persistida | Resultado normativo |
|---|---|
| SQL `NULL` | Ausente |
| Envelope integral com `schemaVersion: 1`, `items` não vazio e itens íntegros | Presente |
| JSON `null` | Ilegível |
| Array na raiz, array vazio ou objeto vazio | Ilegível |
| `schemaVersion` ausente ou versão desconhecida | Ilegível |
| Membro desconhecido no envelope ou em um item | Ilegível |
| Campo obrigatório ausente ou tipo incorreto | Ilegível |
| Item inválido, referência duplicada ou lista `items` vazia | Ilegível |

O leitor deve rejeitar versões e membros desconhecidos até que uma versão compatível tenha sido implantada pelo fluxo de implantação dos leitores antes dos componentes de escrita. O tipo `jsonb` impõe JSON válido na entrada, conforme a [documentação do PostgreSQL](https://www.postgresql.org/docs/17/datatype-json.html). Portanto, texto vazio e JSON sintaticamente malformado devem falhar no `INSERT`; esta decisão exige que o erro reverta a transação de aceite definida pela ADR-0018. O parser direto deve cobrir essa rejeição separadamente; testes integrados devem persistir JSON sintaticamente válido, porém estruturalmente inválido.

Vetores mínimos do schema V1:

| Vetor | Classificação |
|---|---|
| `{"schemaVersion":1,"items":[{"reference":"att_alpha","contentIdentity":"content_alpha","name":"arquivo.pdf","mediaType":"application/pdf","length":0}]}` | Presente |
| Mesmo documento com dois itens distintos, na ordem do claim | Presente, com a ordem preservada |
| Raiz em array, JSON `null` ou objeto vazio | Ilegível |
| `schemaVersion` ausente, `null`, string, fracionário ou diferente de `1` | Ilegível |
| `items` ausente, `null`, objeto ou array vazio | Ilegível |
| Nome de membro com caixa diferente ou membro adicional | Ilegível |
| Qualquer string obrigatória ausente, `null`, vazia ou com tipo diferente de string | Ilegível |
| `length` negativo, fracionário, string ou maior que `9223372036854775807` | Ilegível |
| Duas ocorrências ordinais da mesma `reference` | Ilegível |

O leitor não deve converter documento ilegível em ausência, conjunto vazio ou leitura atual do módulo proprietário.

### Composição congelada e elegibilidade relida

O snapshot congela:

- quais referências compõem o conjunto;
- a ordem do conjunto;
- a identidade de conteúdo de cada item;
- nome, tipo de mídia e comprimento liberados no aceite.

O snapshot não congela:

- liberação vigente;
- revogação;
- validade;
- disponibilidade ou divergência de identidade;
- envelope efetivo da tentativa.

Imediatamente antes de cada chamada ao provedor, o preflight deve reler essas condições com falha fechada. Uma reprovação termina antes da chamada e não altera o snapshot aceito.

### Uso pelos consumidores

Pipeline, despacho, retry, fallback e evidência devem obter o manifesto da linha `notification` já carregada. A primeira tentativa e as tentativas seguintes continuam referenciando a notificação, sem copiar o conjunto para `notification_attempt`.

Mensagens de outbox e filas continuam transportando apenas identificadores e estado mínimo. A representação necessária ao adaptador de entrega é derivada do snapshot da notificação para a tentativa em execução, sem criar uma segunda autoridade persistida.

Um veredito terminal pode descartar conteúdo transitório da tentativa, mas não pode remover, limpar ou reescrever o snapshot da notificação.

### Reconciliação aplicada na aprovação

- A Tarefa 25 e seu write set devem autorizar os testes unitários e integrados da matriz V1, a captura do `INSERT`, a ausência da coluna em `UPDATE` posterior e a guarda negativa de imutabilidade. O mapeamento EF deve configurar `AfterSaveBehavior.Throw` para a propriedade persistida; um teste deve tentar alterá-la após a criação e comprovar falha antes da emissão de SQL.
- A Tarefa 26 e seu write set devem ordenar que pipeline, despacho e fallback leiam exclusivamente a linha `notification`. Qualquer instrução para copiar o snapshot deve ser removida: não se permite cópia persistida em `notification_attempt`, outbox ou mensagem. Uma representação transitória pode ser derivada da notificação somente para a chamada em curso.
- A Tarefa 34 e seu write set devem incluir `NotificationEvidenceReader`, o contrato `NotificationEvidence` e seus testes. Sem essa cobertura, `ER-009` não é atendido.
- A Tarefa 35 e seu write set devem autorizar a única edição manual necessária na migração gerada: inserir `SET LOCAL lock_timeout = '3s'` na mesma transação, imediatamente antes do DDL. A migração precedente `AddNotificationAdmittedPlan` não contém esse comando, enquanto `StoreCallbackPayloadOnce` registra o padrão manual; por isso, o plano deve autorizar a inserção revisada. O snapshot do modelo continua gerado pela ferramenta e não pode ser editado manualmente. A aceitação deve conferir a coluna no pai e em todas as partições existentes e ensaiar o limite contra bloqueio no pai e em uma partição.
- A Tarefa 36 deve depender das Tarefas 24, 31, 34 e 35, cobrindo ingresso V2, terminação segura de roteamento, leitores operacionais e de evidência e migração. Sua aceitação limita-se a implantar os controles ainda desabilitados e a comprovar que o caminho sem anexos e os itens já aceitos continuam processáveis.
- A Tarefa 37 deve verificar as quatro combinações da matriz de rollout e declarar leitor antigo com writer novo e documento não nulo como combinação proibida, não como caso de compatibilidade esperado. Nenhuma habilitação operacional pode ocorrer antes de a Tarefa 37 concluir o ensaio e de a documentação do produtor estar publicada.
- As seis reconciliações acima foram aplicadas ao backlog no mesmo ponto de controle em que o usuário aprovou este ADR.

## Invariantes e mecanismos de garantia

| Invariante | Mecanismo de garantia |
|---|---|
| Uma notificação com anexos possui um snapshot integral ou não é aceita | teste transacional injeta falha na serialização e persistência; nenhuma linha ou claim sobrevive |
| O snapshot integra o `INSERT` inicial e é escrito uma única vez | teste captura o SQL e comprova a coluna no `INSERT` da notificação; inspeção de comandos posteriores comprova que nenhum `UPDATE` inclui a coluna; `AfterSaveBehavior.Throw` bloqueia alteração rastreada depois da criação, e um teste negativo confirma a falha antes de qualquer comando SQL |
| Linhas legadas e notificações sem anexos permanecem válidas | migração anulável sem valor padrão e testes sobre linhas nulas anteriores e novas |
| Ausência e ilegibilidade permanecem distinguíveis | teste unitário do leitor cobre a matriz normativa completa; teste direto do parser cobre JSON malformado; teste integrado no PostgreSQL cobre documentos JSON válidos, porém estruturalmente inválidos, e confirma que JSON malformado falha no `INSERT` |
| Documento ilegível nunca vira composição atual | teste altera o estado em `AttachmentManagement` e confirma falha fechada sem substituição |
| Todas as tentativas usam o mesmo conjunto e ordem | testes de retry e fallback comparam o conjunto submetido ao snapshot persistido |
| `notification_attempt` não duplica o conjunto | teste de modelo e migração comprova ausência de coluna ou entidade dedicada; inspeção do estado persistido após primeira tentativa, retry, fallback e fan-out comprova que nenhum campo existente recebeu uma representação do manifesto |
| Elegibilidade não é congelada | teste revoga, vence ou altera a identidade depois do aceite e comprova zero chamada ao provedor |
| O snapshot sobrevive ao veredito terminal | teste conclui a tentativa e reconstrói o manifesto pela notificação |
| O rollout admite somente combinações compatíveis | matriz de compatibilidade e ensaio cobrem leitor antigo com schema novo e valor nulo, leitor novo com linha nula e leitor novo com writer novo; leitor antigo com writer novo e documento não nulo é combinação proibida |

## Alternativas consideradas

Não se aplica uma matriz ponderada. As alternativas acrescentam superfícies persistidas sem entregar vantagem sustentada pelos acessos observados.

### Coluna `jsonb` na notificação

Mantém uma autoridade por notificação, acompanha o acesso já praticado pelos consumidores e usa o precedente `admitted_plan`. Foi promovida.

### Tabela filha particionada

Separaria os itens em linhas próprias e permitiria consultas e estado por item. Foi rejeitada porque introduziria outra superfície persistida para o conjunto, além de escrita e junção adicionais, enquanto os consumidores já carregam a notificação. Particionamento e relacionamento com a tabela pai exigiriam decisões operacionais adicionais sem benefício demonstrado para os acessos observados.

Essa alternativa deve ser reconsiderada se o manifesto passar a exigir estado mutável por item ou consultas independentes por anexo.

### Cópia por tentativa

Tornaria cada tentativa autocontida. Foi rejeitada porque cria várias autoridades para um conjunto que precisa permanecer único, amplia cada fan-out e fallback e submete a cópia ao ciclo de vida destrutivo da tentativa. Uma omissão em qualquer caminho produziria tentativas com composições diferentes.

## Consequências

### Positivas

- Todas as tentativas compartilham uma única composição aceita.
- Pipeline, despacho e fallback reutilizam a linha que já carregam.
- O snapshot permanece disponível depois do veredito terminal da tentativa.
- A ausência de documento preserva linhas legadas e notificações sem anexos.
- O preflight continua apto a bloquear revogação, vencimento ou divergência posterior ao aceite.

### Negativas e contrapartidas aceitas

- O documento aumenta a linha particionada `notification` no caminho de escrita e leitura.
- Um documento ilegível bloqueia o uso de anexos e exige investigação operacional.
- O formato persistido passa a exigir evolução compatível do leitor.
- Mover o manifesto depois do primeiro aceite exige migração de dados e coexistência de leitores; o backfill por partição é uma estratégia operacional.
- A ausência de orçamento aprovado impede afirmar se o documento permanecerá fora do TOAST ou qual será seu custo de I/O.

### Mitigações

- Manter no snapshot somente dados necessários à identidade e à composição.
- Validar integralmente o documento antes de expô-lo aos consumidores.
- Implantar leitores tolerantes antes do primeiro writer.
- Manter o snapshot fora de `notification_attempt`, outbox e mensagens.
- Medir tamanho e comportamento de TOAST somente quando cardinalidade e envelope tiverem parâmetros aprovados.

## Rollout e reversibilidade

1. Gerar pela ferramenta a migração aditiva e o snapshot do modelo, com coluna `jsonb` anulável e sem valor padrão. Na mesma transação da migração, executar a edição manual autorizada `SET LOCAL lock_timeout = '3s'` imediatamente antes do DDL; o precedente `AddNotificationAdmittedPlan` demonstra que o comando não integra o artefato EF análogo observado. Inspecionar o SQL, confirmar o alcance sobre o pai e cada partição existente e ensaiar contenção com uma transação bloqueadora no pai e em uma partição.
2. Implantar leitores que diferenciem presente, ausente e ilegível, mantendo a escrita desabilitada.
3. Confirmar que API, worker, despacho, fallback e evidência toleram a coluna nula e que nenhum deles substitui documento ilegível por estado atual.
4. Implantar o writer que grava o snapshot no mesmo aceite transacional, ainda sem habilitar produtores com anexos.
5. Executar a matriz de compatibilidade, testes de retry, fallback, fan-out, veredito terminal e preflight. Tratar leitor antigo com writer novo e documento não nulo como combinação proibida.
6. Confirmar que cada instância da API e dos workers e cada consumidor dos caminhos Pipeline, Dispatching, Fallback e Evidence anuncia suporte ao documento não nulo; manter REST e Kafka V1 recusando anexos e drenar consumidores incompatíveis.
7. Confirmar que os limites aprovados de cardinalidade, tamanho e envelope foram publicados e são impostos antes do aceite.
8. Depois da conclusão da Tarefa 37 e da publicação da documentação do produtor, habilitar progressivamente somente os ingressos V2 com anexos.

| Combinação | Situação |
|---|---|
| Leitor antigo, schema novo e coluna SQL `NULL` | Segura durante a fase sem writer |
| Leitor novo e linha legada ou sem anexos com SQL `NULL` | Segura |
| Leitor novo e writer novo | Segura após os gates do rollout |
| Leitor antigo e writer novo com documento não nulo | Proibida |

Antes do primeiro aceite com anexos, a reversão pode retirar o writer e manter a coluna vazia. Depois do primeiro aceite, o rollback é lógico: bloquear novos aceites com anexos, manter leitores e processamento dos itens existentes e preservar coluna e dados. A reversão não deve executar a migração descendente nem converter solicitações com anexos para o caminho sem anexos.

Mover o snapshot depois do primeiro aceite exige criar o novo destino de modo aditivo, fazer backfill por partição como estratégia operacional, manter simultaneamente o leitor da coluna e o leitor do novo destino, comparar a equivalência do corpus retido e somente então trocar a autoridade. O destino anterior permanece até o backfill integral, a troca de leitores operacionais e históricos e o cumprimento da retenção aplicável. Este é o custo de reversão aceito, não uma fase deste rollout.

## Riscos

- Um writer habilitado antes dos leitores pode deixar processos antigos incapazes de preservar o conjunto.
- Um leitor permissivo pode converter corrupção em envio com composição diferente.
- Um caminho de retry ou fallback pode introduzir uma cópia divergente se não usar a notificação como autoridade.
- Uma migração sobre o pai particionado adquire bloqueio e pode interferir no ingresso se não tiver limite operacional.
- Uma migração sem `SET LOCAL lock_timeout` antes do DDL, sem transação ou sem ensaio de contenção pode aguardar bloqueio além do limite operacional.
- Um snapshot usado como prova de elegibilidade pode permitir envio depois de revogação ou vencimento.
- O tamanho do documento e seu comportamento de TOAST permanecem sem medição até existirem cardinalidade e envelope aprovados.

## Condições de revisão

Reabrir a decisão se ocorrer qualquer uma destas condições:

- o manifesto passar a exigir estado mutável por item;
- consumidores deixarem de carregar a notificação por identificador;
- consultas independentes por anexo se tornarem parte do caminho crítico;
- a estratégia de particionamento de `notification` mudar;
- outro canal com semântica diferente de composição entrar na primeira produção;
- o formato do snapshot não puder evoluir com leitura compatível;
- medição sustentada demonstrar que a coluna prejudica o orçamento aprovado de armazenamento ou I/O.

## Evidências

| Afirmação | Evidência |
|---|---|
| `ER-009` exige um snapshot estável para tentativa, retry, fallback e evidência | `docs/SPEC-001/requirements/core/01-development-specification.md:140` |
| `ER-010` separa a composição aceita da elegibilidade relida no preflight | `docs/SPEC-001/requirements/core/01-development-specification.md:141` |
| O precedente usa `jsonb` anulável no nível da notificação | `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Configurations/NotificationConfiguration.cs:54-56` |
| O leitor precedente distingue presente, ausente e ilegível | `src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs:48-108` |
| O precedente congela composição, mas não elegibilidade | `src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs:8-27`; `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs:140-201` |
| O pipeline grava o snapshot precedente na notificação e não na tentativa | `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/PipelineCommitWriter.cs:134-154` |
| A migração precedente acrescenta `jsonb` anulável sem valor padrão e documenta o bloqueio do pai particionado | `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/20260825145151_AddNotificationAdmittedPlan.cs:38-58` |
| A migração precedente `AddNotificationAdmittedPlan` não contém `lock_timeout`, e o repositório registra o comando como SQL explícito | `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/20260825145151_AddNotificationAdmittedPlan.cs:50-58`; `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/20260825223842_StoreCallbackPayloadOnce.cs:29-33` |
| O tipo `jsonb` impõe JSON válido na entrada | [PostgreSQL 17: tipos JSON](https://www.postgresql.org/docs/17/datatype-json.html) |
| `ALTER TABLE` usa `ACCESS EXCLUSIVE` por padrão e, sem `ONLY`, alcança tabelas descendentes | [PostgreSQL 17: `ALTER TABLE`](https://www.postgresql.org/docs/17/sql-altertable.html) |
| `SET LOCAL` vale somente durante a transação corrente | [PostgreSQL 17: `SET`](https://www.postgresql.org/docs/17/sql-set.html) |
| O contrato publicado de arquivo de evidência representa comprimento com `System.Int64` | `src/Platform.Api/Modules/Audit/Integration/V1/IEvidenceArchive.cs:38-46` |
| O fallback carrega a notificação e cria a tentativa seguinte sem armazenar o plano na tentativa | `src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs:90-96,433-448` |
| O leitor de evidência atual ainda não projeta o manifesto | `src/Platform.Api/Modules/Notifications/Infrastructure/Reads/NotificationEvidenceReader.cs:40-57,102-120`; `src/Platform.Api/Modules/Notifications/Integration/V1/NotificationEvidence.cs` |
| O refinamento promove a linha da notificação e rejeita tabela filha e cópia por tentativa | `docs/SPEC-001/refinements/00-refinement-consolidated.md:152-160` |
| O modelo particionado amplia o custo de uma tabela filha | `docs/SPEC-001/refinements/00-refinement-consolidated.md:65,160` |
| O corpus fixa a forma neutra e ordenada do snapshot | `.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md:12,23-32,68-78` |
| A Tarefa 8 produziu o corpus contratual que antecede esta decisão | `docs/SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md:463-472` |
| A ADR-0018 inclui o snapshot na transação de aceite | `docs/ADR-0018-claim-atomico-na-transacao-de-aceite.md:69-78` |

## Referências

- [Especificação de desenvolvimento, `ER-009`, `ER-010` e `ER-015`](SPEC-001/requirements/core/01-development-specification.md).
- [Refinamento, seção 4.2 e fatos `FT-14` a `FT-16`, `FT-39`, `FT-40`, `RF-015` e `RF-016`](SPEC-001/refinements/00-refinement-consolidated.md#42-onde-persistir-o-snapshot-e-como-os-consumidores-o-leem).
- [Corpus contratual do manifesto](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus.md).
- [Tarefa 8: levantamento do corpus contratual](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md#tarefa-8-levantar-o-corpus-contratual-do-manifesto-e-decidir-a-versão).
- [ADR-0018: Claim atômico na transação de aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md).
- [`AdmittedDeliveryPlan`](../src/Platform.Api/Modules/Notifications/Domain/AdmittedDeliveryPlan.cs).
- [`NotificationConfiguration`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Configurations/NotificationConfiguration.cs).
- [Migração `AddNotificationAdmittedPlan`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/20260825145151_AddNotificationAdmittedPlan.cs).
- [Migração `StoreCallbackPayloadOnce`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/Migrations/20260825223842_StoreCallbackPayloadOnce.cs).
- [`PipelineCommitWriter`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/PipelineCommitWriter.cs).
- [`FallbackRequestHandler`](../src/Platform.Api/Modules/Notifications/Features/Fallback/FallbackRequestHandler.cs).
- [`NotificationEvidenceReader`](../src/Platform.Api/Modules/Notifications/Infrastructure/Reads/NotificationEvidenceReader.cs) e [`NotificationEvidence`](../src/Platform.Api/Modules/Notifications/Integration/V1/NotificationEvidence.cs).
- [Stack Profile](../.araia/stack-profile.yaml).
- [PostgreSQL 17: tipos JSON](https://www.postgresql.org/docs/17/datatype-json.html).
- [PostgreSQL 17: `ALTER TABLE`](https://www.postgresql.org/docs/17/sql-altertable.html).
- [PostgreSQL 17: `SET`](https://www.postgresql.org/docs/17/sql-set.html).
