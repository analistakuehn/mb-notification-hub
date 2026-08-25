---
language: pt-BR
---

# Desempenho

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## PRF-001: o limite de 20 ms não tem medição e o custo cresce por evento

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Performance`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `48`
- `evidence`: A linha 48 declara resposta em menos de 20 ms e a linha 131 repete o número como fato operacional. Na versão analisada, `ReceiveProviderWebhook.Handler` cifra o payload e percorre os eventos sequencialmente; cada `DeliveryEventWriter.RecordAsync` abre e confirma sua própria transação. Os testes de webhook validam estado e persistência, mas `tests/Platform.PerformanceTests` não contém cenário de webhook nem limite de lote que sustente o orçamento.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Um lote SendGrid maior pode ultrapassar o orçamento, provocar reentregas e ampliar contenção no único endpoint público do hub.
- `recommendation`: Definir percentil, perfil de carga e teto de eventos por callback. Medir o caminho HTTP completo com criptografia e PostgreSQL representativos. Avaliar uma transação por lote ou enfileiramento do lote verificado.
- `verification`: Medir P95 e P99 com lotes até o teto aceito e concorrência representativa. O critério precisa declarar qual percentil deve permanecer abaixo de 20 ms.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`, `dotnet-engineer`
- `dissent`: O `dotnet-specialist` registrou o ponto como lacuna de medição em `BLIND_SPOTS`, sem achado. A consolidação manteve o achado porque o documento apresenta o número como propriedade alcançada e o caminho cresce linearmente por evento.

## Pontos cegos da lente

Não havia benchmark, trace, counter ou telemetria versionada para validar o limite. A revisão não executou medição de carga.
