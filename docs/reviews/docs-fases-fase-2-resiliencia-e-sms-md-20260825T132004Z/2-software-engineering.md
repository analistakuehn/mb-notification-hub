---
language: pt-BR
---

# Engenharia de software

[Voltar ao índice](00-index.md)

## Resultado

`FINDINGS`

## ENG-001: o circuito aberto não produz o fallback de canal declarado

- `severity`: `HIGH`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `32`
- `evidence`: A linha 32 afirma que, com o circuito aberto, a mensagem volta à fila e o tracker aciona fallback. `DispatchMessageProcessor.Decide` converte `circuit-open` em `Requeue`, e `SettleVerdictAsync` devolve o attempt a `queued`. `OverdueFallbackScan` seleciona apenas `sent` ou `unknown`; o gate do kill switch também posterga o attempt atual sem avançar o plano. O teste existente confirma somente o requeue.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: Se a indisponibilidade do canal primário durar até o TTL, uma notificação `critical` pode expirar sem tentar SMS, contrariando a garantia de fallback e o critério de saída.
- `recommendation`: Produzir `FallbackRequested` para `circuit-open` quando houver próximo passo ou tornar a varredura elegível para attempts `queued` cujo provedor não recebeu chamada. Preservar o claim único por etapa.
- `verification`: Executar um cenário push para SMS com o provedor push em circuito aberto e duas réplicas do scheduler. O resultado deve conter exatamente um attempt SMS e nenhum envio push.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-specialist`
- `dissent`: O `dotnet-specialist` também classificou a ausência do cenário ponta a ponta como finding de teste. A consolidação incorporou essa lacuna à verificação deste achado para não duplicar a mesma causa e localização.

## ENG-002: a duplicata aceita no fallback de `unknown` não é observável como `notification.duplicate`

- `severity`: `MEDIUM`
- `confidence`: `HIGH`
- `lens`: `Software Engineering`
- `file`: `docs/fases/fase-2-resiliencia-e-sms.md`
- `line`: `214`
- `evidence`: A linha 214 afirma que a duplicata ao cliente decorrente do fallback de `unknown` é auditada como `notification.duplicate`. `FallbackRequestHandler` grava esse evento quando um gatilho chega depois de a notificação já ter encerrado. O fallback normal a partir de `unknown` não registra a duplicata real; para FCM não existe lookup posterior que confirme se a primeira mensagem chegou.
- `evidence-kind`: `derived`
- `introduced-by-diff`: `false`
- `impact`: A trilha não sustenta a afirmação de que a duplicata aceita será identificada, o que prejudica métricas de risco, investigação e prestação de contas.
- `recommendation`: Descrever com precisão o que é observável e registrar um evento como `fallback.requested_from_unknown`, sem usar `notification.duplicate` como prova de uma entrega duplicada não observada.
- `verification`: Forçar um attempt `unknown` por mais de 60 s, confirmar o fallback e inspecionar a trilha. Ela deve registrar o risco assumido sem alegar detecção da duplicata efetiva.
- `source-revision`: `6744e61e47fdfb0b0e709f0bfb616de1dd999420`
- `reviewers`: `dotnet-engineer`
