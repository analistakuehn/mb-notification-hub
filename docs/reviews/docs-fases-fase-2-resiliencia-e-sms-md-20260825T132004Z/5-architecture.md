---
language: pt-BR
---

# Arquitetura

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## ARC-001: o handler de fallback aceita uma tentativa de outra notificação

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `38`
- `evidence`: `FallbackRequestHandler.ProcessAsync` carrega `notificationId` e `failedAttemptId` em consultas independentes. Não valida que `failedAttempt.NotificationId` corresponda à notificação carregada. O próximo passo usa a política da notificação e o canal da tentativa, permitindo uma combinação cruzada.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Uma mensagem interna inconsistente pode avançar ou encerrar outra notificação, criar tentativa indevida e registrar auditoria cruzada.
- `recommendation`: Resolver a tentativa por chave composta de tentativa e notificação. Rejeitar pares inconsistentes e validar que a tentativa pertence ao passo vigente do plano.
- `verification`: Publicar um gatilho com IDs pertencentes a notificações diferentes. O processamento deve produzir zero alterações, zero outbox e zero auditorias para ambas.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## ARC-002: a supressão automática pode ser perdida depois do commit

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Architecture`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `173`
- `evidence`: O documento marca a supressão automática como concluída. `DeliveryStateApplier` confirma a transação e marca o evento como aplicado antes de chamar `ReportSuppressionAsync`. A chamada opera em regime de melhor esforço, absorve falhas e exceções e não possui outbox nem retentativa. Como o evento já foi aplicado e deduplicado, uma reentrega não refaz a supressão.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Uma falha transitória do módulo ContactConsent pode perder permanentemente um hard bounce ou destino inválido, permitindo novos envios a um contato que deveria ser suprimido.
- `recommendation`: Publicar o sinal por outbox durável ou persistir um estado pendente de supressão com retentativa idempotente.
- `verification`: Injetar falha transitória no ContactConsent, recuperar o consumidor e comprovar que a supressão ocorre exatamente uma vez.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-architect`

## Verificado sem achado adicional nesta lente

As decisões D1 a D7, ADR-0008, ADR-0011, ADR-0014, as fronteiras publicadas em `Integration/V1` e os testes arquiteturais não sustentaram outro achado dentro do recorte delimitado.
