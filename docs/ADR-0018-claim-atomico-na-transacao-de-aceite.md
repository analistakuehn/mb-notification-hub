---
language: pt-BR
---

# ADR-0018: Claim atômico na transação de aceite

**Status**: ACCEPTED

| Campo | Valor |
|---|---|
| **Data** | 2026-08-31 |
| **Responsável** | Arquitetura do Notification Hub |
| **Audiência** | Arquitetura e Engenharia de Plataforma |
| **Aprovação** | Usuário, por decisão explícita no ponto de controle de implementação de 2026-08-31 |
| **Escopo da decisão** | Aceite de notificações com anexos |
| **Relacionadas** | [ADR-0006: Auditoria em banco, append-only, com hash chain e export WORM](ADR-0006-auditoria-append-only-hash-chain-worm.md); [ADR-0008: Entrega at-least-once com idempotência](ADR-0008-at-least-once-com-idempotencia.md) |
| **Fontes** | [Experimento de consistência](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.md); [Delivery Slice](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md) |
| **Código afetado** | `AttachmentManagement.Integration.V1`; writer de ingresso de `Notifications`; testes de transação e arquitetura |

## Errata de 2026-09-02: cerimônia de migração

A decisão continua vigente por inteiro, e nada neste documento dependia de versão de contrato de ingresso. O `V1` de `AttachmentManagement.Integration.V1` é a versão do contrato publicado entre módulos, não a versão da superfície pública de ingresso, e permanece como está.

Um único ponto envelheceu. O passo 2 do rollout prescreve publicar migrações aditivas, e a reversibilidade afirma que a reversão não desfaz migrações. Em 2026-09-02, o dono do produto decidiu esmagar todas as migrações em uma inicial, porque o serviço é novo e não tem nada em produção. Para este módulo, que ainda não possui migração alguma, aditivo e inicial descrevem o mesmo ato: o schema do claim nasce na migração inicial. A consequência detalhada, incluindo o que fica suspenso e o que continua valendo, está na errata de 2026-09-02 da [ADR-0019](ADR-0019-snapshot-do-manifesto-aceito.md).

## Resumo executivo

Adotar o claim integral na mesma transação PostgreSQL que confirma notificação, snapshot, idempotência, outbox e auditoria. O módulo `Notifications` inicia e confirma a transação. O módulo `AttachmentManagement` participa por um contrato publicado que recebe a `DbTransaction`, executa seus próprios comandos sobre a conexão existente e devolve um snapshot neutro e imutável.

A decisão elimina a janela durável entre claim e aceite que apareceu nas alternativas com compensação ou confirmação assíncrona. Em contrapartida, ela torna a co-localização no mesmo PostgreSQL físico e o isolamento `READ COMMITTED` restrições arquiteturais explícitas.

## Contexto

O caminho vigente já confirma notificação, chave de idempotência, mensagem de outbox e auditoria na mesma transação. Outbox e auditoria recebem a transação crua do chamador, sem compartilhar `DbContext` ou entidades. A auditoria exige `READ COMMITTED`, pois níveis com snapshot anterior ao advisory lock podem bifurcar a cadeia.

O aceite com anexos acrescenta outra invariante: nenhum estado durável observável pode conter notificação aceita sem o claim integral do conjunto. Um experimento descartável em PostgreSQL 17.10 executou 23 observações nos pontos de falha da fronteira transacional. A transação compartilhada reverteu todos os efeitos antes do commit e preservou todos eles depois de um commit bem-sucedido. As alternativas com reserva deixaram estados proibidos.

O harness representa o claim por uma única linha. Ele comprova a fronteira transacional e falsifica as janelas duráveis das outras alternativas, mas não comprova ainda pertencimento, múltiplas referências, concorrência, replay nem a contagem condicional do contrato produtivo. Essas propriedades pertencem à matriz contra a implementação real.

### Fatos observados

- A transação compartilhada reverteu claim, notificação, idempotência, outbox e auditoria em cada falha anterior ao commit.
- O estado permaneceu integral depois de um commit bem-sucedido no harness.
- A compensação posterior deixou aceite durável sem claim confirmado e ainda pôde remover a dependência restante.
- A confirmação pela outbox preservou o estado imediato, mas uma varredura por TTL removeu a reserva antes do consumo tardio, que atualizou zero linhas.
- O teste de integração vigente comprova que uma falha no append da auditoria reverte notificação, idempotência e outbox.
- A configuração-base aponta `Notifications`, Audit e outbox para a mesma base (`src/Platform.Api/appsettings.json:9-20,108-112`), mas cada ambiente ainda precisa validar essa condição na inicialização.

### Objetivos

- Impedir notificação aceita sem claim integral em qualquer ponto de falha durável.
- Preservar a indivisibilidade do conjunto: todas as referências são reivindicadas ou nenhuma é alterada.
- Resolver replay e resultado de commit desconhecido pela idempotência, sem repetir efeitos cegamente.
- Manter a auditoria como último efeito antes do commit.
- Preservar a fronteira do monólito modular por contrato publicado e versionado.

### Fora do escopo

- Custódia e validação do conteúdo.
- Forma canônica do manifesto.
- Transferência do conteúdo ao provedor.
- Quantidade, tamanho, tipos e orçamento operacional de anexos.
- Extração de `AttachmentManagement` para outro repositório de dados ou serviço.

### Direcionadores da decisão

1. A invariante de claim integral precisa valer no commit, não depois de reconciliação.
2. O sistema já usa contratos transacionais neutros para outbox e auditoria.
3. A ordem vigente reduz a posse do advisory lock da auditoria.
4. Uma segunda conexão aumentaria consumo do pool e impediria atomicidade local.
5. A fronteira entre módulos não pode expor contexto EF, entidade ou detalhe do schema.

## Decisão

O caminho de aceite com anexos deve executar o claim integral por meio de `AttachmentManagement.Integration.V1`, dentro da mesma `DbTransaction`, conexão aberta e base física PostgreSQL usadas para persistir a notificação, o snapshot, a chave de idempotência, a outbox de aceite e a auditoria.

A transação deve ser iniciada explicitamente em `READ COMMITTED`. Antes do claim, o proprietário da transação deve conferir também o isolamento efetivo informado pelo PostgreSQL. A guarda da auditoria permanece como defesa adicional. A ordem é:

1. abrir a transação em `READ COMMITTED`;
2. conferir o isolamento efetivo;
3. executar o claim indivisível;
4. persistir notificação, snapshot e idempotência;
5. acrescentar a outbox de aceite;
6. acrescentar a auditoria;
7. confirmar imediatamente.

`Notifications` permanece proprietário da transação, do commit, do rollback e do descarte. `AttachmentManagement` permanece proprietário do SQL e do schema do claim. A implementação usa `transaction.Connection`, associa cada `DbCommand` à transação recebida, parametriza valores e qualifica os nomes de schema.

É proibido abrir segunda conexão, iniciar transação independente, usar `TransactionScope` ou DTC, compensar o claim depois de um aceite confirmado ou concluir o claim por mensagem assíncrona nesse caminho.

Como o módulo chamado usa a conexão do chamador, ele também usa a identidade PostgreSQL dessa conexão. A role do writer deve receber apenas os privilégios necessários às operações publicadas de claim no schema de anexos. O rollout deve provar que essa role executa o claim e não executa operações administrativas ou mutações fora da superfície publicada.

Se a persistência da chave idempotente perder uma corrida depois do claim, o writer deve reverter e descartar a transação antes de consultar o registro vencedor. Nenhum claim ou lock do perdedor pode permanecer durante essa consulta. O replay só é aceito quando o hash armazenado corresponde à forma canônica que inclui o manifesto de anexos definida pela decisão que possui essa canonicalização.

Este ADR não autoriza retry automático. Um deadlock conhecido deve encerrar e descartar toda a unidade transacional. Uma política futura de retry precisa definir limite, backoff, reconstrução do `DbContext` e testes antes de ser habilitada. Uma falha durante `CommitAsync` também descarta transação e contexto; um contexto novo consulta a autoridade idempotente. Se essa verificação não concluir o resultado, o aceite permanece com resultado desconhecido e não é repetido cegamente.

### Contrato publicado

| Elemento | Forma decidida |
|---|---|
| Entrada | `DbTransaction`, chave idempotente do claim, aplicação, identificador da notificação e referências opacas em ordem canônica |
| Sucesso | snapshot neutro e imutável do conjunto integral |
| Recusas | referência indisponível, pertencimento inválido, conjunto não liberado ou conflito idempotente |
| Atomicidade | conjunto completo ou nenhuma alteração |
| Idempotência | mesma chave e mesmo conjunto devolvem o mesmo claim; conjunto diferente produz conflito |
| Encapsulamento | nenhum `DbContext`, entidade EF, migração, tipo PostgreSQL ou detalhe de armazenamento atravessa a fronteira |

## Invariantes e mecanismos de garantia

| Invariante | Mecanismo de garantia |
|---|---|
| Não existe notificação aceita sem claim integral | matriz de integração com falha após claim, persistência, outbox, auditoria e commit |
| O conjunto é atômico | SQL condicional sobre o conjunto canônico recebido, contagem de linhas afetadas, conferência do conjunto devolvido e rollback quando qualquer referência falhar |
| Todos os efeitos usam uma transação e conexão | contrato recebe a `DbTransaction`; testes recusam segunda conexão e transação independente |
| Todos os participantes usam a mesma base física | validação fail-fast na inicialização e teste com destinos divergentes |
| O isolamento é `READ COMMITTED` | início explícito e verificação prévia do isolamento efetivo antes do claim; guarda da auditoria como defesa adicional |
| A auditoria é o último efeito antes do commit | teste da ordem claim, persistência, outbox, auditoria e commit |
| A fronteira permanece publicada | função de adequação permite somente `AttachmentManagement.Integration.V1` e recusa referências à Infrastructure, EF e tipos PostgreSQL |
| O perdedor da corrida idempotente não conserva claim | rollback e descarte antes da consulta autoritativa; teste concorrente inspeciona estado e locks |
| Locks seguem ordem determinística | aquisição por chave estável na ordem canônica; teste com conjuntos sobrepostos em ordens de entrada opostas |
| A role respeita o menor privilégio | teste permite as operações publicadas do claim e recusa administração ou mutação alheia no schema |
| Commit desconhecido não repete cegamente | verificação autoritativa em contexto novo; resultado inconclusivo permanece desconhecido |
| Deadlock não produz efeito parcial | descarte integral da unidade; nenhum retry automático sem política própria aprovada |

## Alternativas consideradas

Não se aplica uma matriz ponderada. A invariante eliminou alternativas por falha comportamental reproduzida, sem depender de pontuação subjetiva.

### Claim na transação compartilhada

Elimina a janela entre claim e aceite sem depender de mensagem, TTL ou reconciliação. Foi a única alternativa que preservou a invariante em todos os pontos exercitados e, por isso, foi promovida.

### Reserva e compensação posterior

Preservaria maior autonomia entre repositórios e permitiria convergir uma reserva órfã. Foi rejeitada porque o commit produziu aceite durável sem claim confirmado e a compensação removeu a dependência restante.

### Reserva e confirmação pela outbox

Manteria uma confirmação durável junto do aceite e toleraria a reentrega do consumidor. Foi rejeitada na forma testada porque a corrida entre TTL e consumo removeu a reserva antes da confirmação.

### Transação distribuída

Poderia coordenar repositórios fisicamente separados. Foi rejeitada porque introduz coordenação distribuída desnecessária enquanto os participantes compartilham a mesma base e não cobre o provedor externo.

## Consequências

### Positivas

- Remove a janela de aceite durável sem claim integral.
- Dispensa consumidor de confirmação, TTL e reconciliação para concluir o claim.
- Reutiliza o dialeto transacional já adotado por outbox e auditoria.
- Mantém entidades, contexto EF e detalhes PostgreSQL fora do contrato publicado.
- Faz qualquer recusa ou falha no claim abortar os demais efeitos do aceite.

### Negativas e contrapartidas aceitas

- Torna a co-localização física no PostgreSQL uma restrição arquitetural.
- Introduz acoplamento técnico explícito a `DbTransaction` na fronteira publicada.
- Acrescenta SQL e locks do claim ao caminho quente do aceite.
- Uma falha do módulo de anexos aborta o aceite inteiro.
- Extrair `AttachmentManagement` para outra base ou serviço exige novo protocolo e nova decisão.
- O risco de contenção e deadlock cresce quando um conjunto contém várias referências.

### Mitigações

- Bloquear referências em ordem canônica.
- Executar o claim antes da auditoria e confirmar imediatamente depois dela.
- Usar SQL parametrizado, qualificado por schema e com predicado de estado.
- Abortar e descartar integralmente a unidade depois de deadlock; qualquer retry depende de política própria aprovada.
- Validar a topologia física na inicialização.
- Repetir a matriz de falhas contra a implementação real antes da habilitação.
- Provar o conjunto mínimo de privilégios da role usada pelo writer.

## Rollout e reversibilidade

1. Reconciliar, pelo fluxo de trabalho autorizado, as tarefas consumidoras que ainda prescrevem reserva, promoção por consumo, compensação e expiração como mecanismo do claim.
2. Publicar `AttachmentManagement.Integration.V1`, migrações aditivas, privilégios mínimos e validação de co-localização antes do primeiro writer.
3. Implantar leitores e estruturas persistentes sem habilitar novos aceites com anexos.
4. Integrar o claim ao writer vigente na ordem normativa.
5. Executar a matriz de falhas contra o código real, incluindo conjunto multirreferência, pertencimento, corrida de idempotência, deadlock e falha durante `CommitAsync`.
6. Executar as funções de adequação da fronteira e da ordem transacional.
7. Habilitar novos aceites progressivamente.

Antes do primeiro aceite com anexos, a reversão pode desabilitar a capacidade e remover a costura ainda não utilizada. Depois do primeiro aceite, o rollback é lógico: bloquear novos aceites com anexos, continuar processando claims confirmados, preservar schema e dados e remover a chamada transacional somente quando não existirem aceites dependentes. A reversão não apaga dados nem desfaz migrações.

## Riscos

- A divergência de configuração pode separar silenciosamente os participantes. A validação de inicialização precisa impedir essa topologia.
- A contenção do claim pode ampliar a duração da transação e a posse dos locks.
- Uma ordem de locks inconsistente pode introduzir deadlocks.
- Um resultado de commit desconhecido pode induzir repetição indevida sem consulta idempotente.
- Mudanças no SQL ou schema de `AttachmentManagement` podem quebrar o comportamento sem quebrar compilação.
- Um retry incorreto pode conservar claim ou locks do perdedor da corrida.
- Privilégios amplos na conexão chamadora podem enfraquecer o isolamento operacional entre módulos.

## Evidências

| Afirmação | Evidência |
|---|---|
| O writer vigente compartilha uma única transação entre persistência, outbox, auditoria e commit | `src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IngestionWriter.cs:51-57` |
| Auditoria e outbox já publicam contratos que recebem a transação do chamador | `src/Platform.Api/Modules/Audit/Integration/V1/IAuditTrail.cs:12-20`; `src/Platform.Api/Infrastructure/Messaging/IOutboxWriter.cs:57-63` |
| A auditoria recusa isolamento incompatível com sua cadeia | `src/Platform.Api/Modules/Audit/Infrastructure/AuditTrail/TransactionalAuditTrail.cs:150-164` |
| Falha da auditoria reverte notificação, idempotência e outbox | `tests/Platform.IntegrationTests/Notifications/RequestNotificationTransactionTests.cs:16-56` |
| A configuração-base co-localiza Notifications, Audit e outbox | `src/Platform.Api/appsettings.json:9-20,108-112` |
| A matriz descartável comparou a fronteira transacional das três alternativas em 23 observações | [Experimento de consistência](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.md) |

## Condições de revisão

Reabrir a decisão se ocorrer qualquer uma destas condições:

- `AttachmentManagement`, Notifications, Audit ou outbox migrar para outra base física;
- o nível de isolamento do caminho de aceite mudar;
- a autonomia física do repositório de anexos se tornar requisito;
- PostgreSQL deixar de ser o mecanismo transacional comum;
- o contrato publicado não puder mais receber a transação do chamador;
- evidência de carga demonstrar contenção ou deadlocks incompatíveis com o ingresso;
- a semântica do advisory lock da auditoria ou sua posição na transação mudar;
- a alternativa assíncrona receber fencing de expiração e uma nova prova das corridas entre confirmação, expiração, revogação e descarte.

## Referências

- [Experimento da consistência entre claim e aceite](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.md).
- [Roteiro SQL reproduzível](../.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.sql).
- [Delivery Slice](SPEC-001/backlogs/SLICE-001-gestao-de-anexos-do-notification-hub.md).
- [ADR-0006: Auditoria em banco, append-only, com hash chain e export WORM](ADR-0006-auditoria-append-only-hash-chain-worm.md).
- [ADR-0008: Entrega at-least-once com idempotência](ADR-0008-at-least-once-com-idempotencia.md).
- [`IngestionWriter`](../src/Platform.Api/Modules/Notifications/Infrastructure/Persistence/IngestionWriter.cs).
- [`IAuditTrail`](../src/Platform.Api/Modules/Audit/Integration/V1/IAuditTrail.cs).
- [`TransactionalAuditTrail`](../src/Platform.Api/Modules/Audit/Infrastructure/AuditTrail/TransactionalAuditTrail.cs).
- [`IOutboxWriter`](../src/Platform.Api/Infrastructure/Messaging/IOutboxWriter.cs).
- [Teste de rollback do aceite](../tests/Platform.IntegrationTests/Notifications/RequestNotificationTransactionTests.cs).
- [Stack Profile](../.araia/stack-profile.yaml).
