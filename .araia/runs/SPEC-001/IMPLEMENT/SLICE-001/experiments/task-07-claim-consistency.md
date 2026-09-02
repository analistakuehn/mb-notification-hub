# Experimento da consistência entre claim e aceite

**Tarefa**: 7, provar o protocolo de consistência do claim sob falhas injetadas  
**Data**: 2026-08-31  
**Resultado**: `DESIGN: READY`  
**Responsável**: `dotnet-architect`

## Resultado

A transação compartilhada por contrato publicado foi promovida para esta Delivery Slice. Ela foi a única alternativa que preservou a invariante em todos os pontos exercitados sem depender da entrega futura de mensagem, prazo de reserva ou reconciliação temporal.

O claim integral deve participar da mesma `DbTransaction` que confirma notificação, idempotência, outbox e auditoria. A restrição exige que `AttachmentManagement`, `Notifications`, outbox e Audit permaneçam na mesma base física PostgreSQL. Separar esses repositórios de dados invalida a decisão e exige reabrir o desenho.

## Evidência reproduzível

O roteiro de teste autocontido está em `task-07-claim-consistency.sql`.

| Propriedade | Valor |
|---|---|
| PostgreSQL | `17.10` |
| Imagem | `postgres:17-alpine` |
| Observações | 23 |
| Resultado | código de saída `0`; todas as assertions passaram |
| Varredura | 12 reservas removidas; zero órfãs expiradas restantes |
| Confirmação tardia de C | zero linhas atualizadas |
| SHA-256 do roteiro de teste | `7CD4F6BBDBCF5125F631FE5814DE3017A4DDFEC3613D32B89301A1495E650246` |
| Tamanho | 16.326 bytes |
| Limpeza | container descartável removido |

Comandos de reprodução:

```powershell
docker run --name task7-claim-consistency-pg -d `
  -e POSTGRES_PASSWORD=test `
  -e POSTGRES_DB=task7_claim_consistency `
  postgres:17-alpine

Get-Content -Raw `
  '.araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-07-claim-consistency.sql' |
  docker exec -i task7-claim-consistency-pg `
    psql -U postgres -d task7_claim_consistency -P pager=off

docker rm -f task7-claim-consistency-pg
```

A linha de base do sistema existente também passou:

```text
dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~RequestNotificationTransactionTests"
```

Resultado: um teste aprovado. A falha induzida no acréscimo à auditoria reverteu notificação, idempotência e outbox, e a nova tentativa bem-sucedida foi aceita como primeira tentativa.

## Alternativas

| Alternativa | Resultado observado | Decisão |
|---|---|---|
| A. Claim na transação compartilhada | Falhas após claim, notificação, idempotência, outbox e auditoria reverteram tudo. Perda do ACK após o commit preservou claim e aceite integrais. | Promovida |
| B. Reserva e compensação posterior | O commit deixou aceite durável com reserva não confirmada. A compensação removeu a única dependência restante. | Rejeitada |
| C. Reserva e confirmação pela outbox | Os pontos imediatos foram seguros, mas o sweep removeu a reserva quando a confirmação atrasou além do TTL. O consumer tardio executou `UPDATE 0`. | Refutada na forma testada |

## Matriz de falhas

| Alternativa | Falha após | Estado durável | Veredito |
|---|---|---|---|
| A | claim | nenhuma linha | Seguro |
| A | notificação | nenhuma linha | Seguro |
| A | idempotência | nenhuma linha | Seguro |
| A | outbox de aceite | nenhuma linha | Seguro |
| A | auditoria | nenhuma linha | Seguro |
| A | commit, antes do ACK | claim, notificação, idempotência, outbox e auditoria íntegros | Seguro |
| B | reserva ou efeito anterior ao commit | somente reserva | Órfão convergível |
| B | commit, antes da confirmação | aceite durável e reserva não confirmada | Violação |
| B | compensação após o commit | aceite sem reserva | Violação |
| C | reserva ou efeito interno anterior ao commit | somente reserva | Órfão convergível |
| C | commit, antes do consumer | aceite, reserva e confirmação durável | Seguro enquanto a reserva existir |
| C | consumo repetido | claim confirmado e uma marca de deduplicação | Seguro |
| C | confirmação atrasada além do TTL | aceite e confirmação duráveis, sem reserva | Violação |

## Contrato promovido

A superfície deve permanecer em `AttachmentManagement.Integration.V1` e expor somente:

| Elemento | Contrato |
|---|---|
| Entrada | `DbTransaction`, chave idempotente do claim, `application`, identificador da notificação e referências opacas em ordem canônica |
| Sucesso | snapshot neutro e imutável do conjunto integral |
| Recusas | reserva incompatível, referência indisponível, pertencimento inválido ou conjunto não liberado |
| Atomicidade | conjunto completo ou nenhuma alteração |
| Idempotência | mesma chave e mesmo conjunto retornam o mesmo claim; conjunto diferente retorna conflito |
| Encapsulamento | nenhum `DbContext`, entidade EF ou tipo PostgreSQL atravessa o contrato |

`AttachmentManagement` continua proprietário do SQL e de seu esquema. A implementação usa a conexão da transação recebida, comandos parametrizados e nomes qualificados por esquema. `Notifications` não referencia entidades, migrações nem o contexto EF do módulo.

## Ordem obrigatória

1. Abrir a transação em `READ COMMITTED`.
2. Executar o claim indivisível.
3. Persistir notificação, snapshot e idempotência.
4. Acrescentar a outbox de aceite.
5. Acrescentar a auditoria.
6. Confirmar imediatamente.

A auditoria permanece como último efeito anterior ao commit porque mantém o advisory lock até o término da transação. A implementação não deve usar segunda conexão, `TransactionScope` nem DTC.

## Garantias de implementação

- Validar na inicialização que `AttachmentManagement` participa da mesma base física do caminho de escrita de `Notifications`, outbox e Audit.
- Executar o claim integral com predicado de estado e conferir o número de linhas afetadas.
- Bloquear os anexos em ordem canônica para reduzir deadlocks.
- Resolver resultado de commit desconhecido pela chave idempotente, sem repetir cegamente.
- Repetir a unidade transacional inteira após deadlock elegível.
- Provar que o perdedor da corrida de idempotência não conserva claim nem bloqueios.
- Tratar a separação física do repositório de dados como condição de revisão arquitetural.

## Rollout e rollback

1. Publicar o contrato transacional e as migrações aditivas antes do primeiro componente de escrita.
2. Acrescentar o claim ao componente de escrita vigente antes de habilitar produtores.
3. Repetir a matriz contra a implementação real.
4. Habilitar novos aceites progressivamente.

No rollback lógico, bloquear novos aceites com anexos, continuar processando claims já confirmados e preservar esquema e dados. Remover a chamada transacional somente depois de não existirem aceites dependentes.

## Condição de revisão

Reabrir a decisão se qualquer módulo participante migrar para outra base física, se o nível de isolamento mudar ou se a autonomia dos repositórios de dados se tornar requisito. Nesse cenário, a alternativa C precisa primeiro receber um protocolo de expiração com fencing e uma nova prova de corrida entre confirmação, expiração, revogação e descarte.

## Próximo artefato

A Tarefa 10 deve registrar esta decisão e suas condições em ADR. Este experimento não altera lifecycle, manifesto nem código de produção.
