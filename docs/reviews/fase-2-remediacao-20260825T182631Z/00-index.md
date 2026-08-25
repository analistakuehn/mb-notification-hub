---
language: pt-BR
target: docs/fases/fase-2-resiliencia-e-sms.md
scope: project
remediated-on: 2026-08-25T18:26:31Z
remediated-via: dotnet-code-review --fix
severity: high
status: resolved
---

# Remediação das revisões da fase 2

## Resultado

`RESOLVED`

As duas revisões da fase 2 registraram 42 achados, que consolidam em 37 causas
distintas: 11 do pacote de escopo delimitado, 31 do pacote de escopo de projeto,
com 5 causas presentes nos dois sob identificadores diferentes. Todas as 37
receberam correção. Nenhuma foi encerrada por não reprodução e nenhuma foi
regraduada.

## Artefatos

| Artefato | Conteúdo |
|---|---|
| [`findings.md`](findings.md) | inventário consolidado das 37 causas, com origem, natureza e estado |
| [`resolution.md`](resolution.md) | o que passou a ser verdade e onde cada mudança vive |
| [`validation.md`](validation.md) | comandos executados, contagens e o que continua sem prova local |

## Pacotes de origem

- [`docs-fases-fase-2-resiliencia-e-sms-md-20260825T124525Z`](../docs-fases-fase-2-resiliencia-e-sms-md-20260825T124525Z/00-index.md),
  escopo de projeto, 31 achados.
- [`docs-fases-fase-2-resiliencia-e-sms-md-20260825T132004Z`](../docs-fases-fase-2-resiliencia-e-sms-md-20260825T132004Z/00-index.md),
  escopo delimitado, 11 achados.

Os dois permanecem como estão. Um pacote de revisão descreve o estado da
revisão-fonte que ele examinou, e corrigir a causa não reescreve o que foi
observado; este pacote é que registra o desfecho.

## O que mudou de comportamento

Nove correções alteram comportamento observável, e não apenas o registro:

1. A varredura por prazo alcança tentativas devolvidas à fila, e a reivindicação
   do despacho passou a ser condicional ao plano não ter avançado.
2. Um veredito inconclusivo produz fallback no prazo do próprio passo, além da
   carência de 60 s.
3. O fallback avança sobre o plano sob o qual a notificação foi admitida, e não
   sobre a política publicada no instante do prazo.
4. Um gatilho cuja tentativa pertence a outra notificação é recusado sem efeito.
5. Correlação vinda da rota só é honrada onde a assinatura do provedor cobre a
   URL, e a resolução do attempt passou a fixar o provedor.
6. A rota de webhook recusa lote acima do teto de eventos e corpo acima do teto
   de bytes.
7. A seleção da reconciliação exclui provedores sem lookup posterior e
   notificações já encerradas.
8. O produto Twilio padrão é Programmable Messaging, e um ambiente pode exigir
   contrato de retorno de entrega no start.
9. Um sinal de supressão que não chegou ao ledger de contatos é reapresentado
   pela varredura em vez de se perder.

A medição também produziu um achado que nenhuma das duas revisões nomeou: a
escrita da ingestão é quadrática no tamanho do lote, porque cada linha de evento
guarda os bytes do callback inteiro. Está descrito em
[`resolution.md`](resolution.md); o teto de eventos por callback foi fixado a
partir dessa conta e o modelo de evidência fica registrado como pergunta aberta.

Três mudam valores de configuração entregues: intervalo de varredura de 5 s para
2 s, timeout do provedor de SMS de 5 s para 2 s e retenção da marca de
deduplicação de 30 para 60 dias. As três são derivadas de um aceite ou de uma
janela declarada, e não de preferência.

## Migração de banco

`AddDeliverySuppressionReportState` acrescenta `delivery_event.suppression_reported_at`,
o índice parcial que sustenta a varredura de supressões pendentes e o índice de
expressão que a reconciliação passou a exigir. A migração
`AddNotificationAdmittedPlan`, produzida antes desta rodada e ainda não
publicada, acrescenta `notification.admitted_plan`. As duas são operações de
catálogo no pai: não reescrevem partição, e o bloqueio exclusivo sobre o pai
continua valendo, com o mesmo limite de tempo de bloqueio de toda migração desta
classe.

## Limite operacional

Este pacote não calcula EQI, não aprova gate e não muda estado de lifecycle. O
que continua fora de prova local está nomeado em [`validation.md`](validation.md).
