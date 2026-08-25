---
language: pt-BR
---

# ADR-0014: Confirmação de entrega e convivência dos gatilhos de fallback

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-24 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | Produto (contrato de saída), SRE |
| **Relacionadas** | ADR-0008 (at-least-once com idempotência), ADR-0011 (plano de entrega da política), ADR-0001 (canal como plugin) |
| **Documento-mãe** | Design de Sistema, §4.2, §5.1, §5.2; Fase 2: decomposição em fatias, D7 |

## Contexto e problema

A fase 2 liga duas coisas ao mesmo tempo: o rastreamento de entrega, que traz confirmação real do provedor, e a varredura de prazo, que dispara o fallback quando a confirmação não chega. As duas mudam o mesmo estado, e o desenho anterior tinha dois defeitos que só aparecem quando elas convivem.

**O primeiro é a unicidade do avanço.** O gatilho reativo (a etapa se esgotou porque o provedor recusou) e o gatilho por prazo (a etapa venceu sem resposta) são duas linhas distintas de outbox, com identidades de mensagem distintas. A deduplicação por mensagem da ADR-0008 protege contra reentrega da *mesma* mensagem e nada mais, então as duas marcas passam e o plano avança duas vezes. O resultado é a duplicata que a ADR-0008 existe para impedir: dois SMS ao mesmo cliente, dois códigos diferentes na mão de quem tenta entrar.

**O segundo é o significado da aceitação do push.** O FCM não reporta entrega, então a aceitação dele era tratada como a entrega da notificação inteira. Isso encerra a notificação em `delivered` no instante em que o provedor aceita a mensagem, e o handler de fallback trata qualquer estado diferente de `dispatched` como duplicata. A consequência é que o cenário central do §5.1, push aceito e sem confirmação em trinta segundos, era descartado em silêncio: a notificação já estava encerrada e o passo de SMS que existia para socorrê-la nunca era pedido. O critério de saída da fase, 100 % das notificações `critical` com fallback efetivo, era inalcançável por construção.

Há um terceiro, menor e da mesma família: o gatilho de fallback era endereçado à fila da classe, enquanto o despacho de um template de finalidade de autenticação já usa a fila de autenticação. Como a banda de drenagem do relay é decidida pelo destino, a segunda metade de um código de acesso drenava atrás do tráfego `critical` comum.

## Fatores de decisão

- **Nunca duplicar para o cliente**, que é a promessa da ADR-0008 e o único requisito não negociável aqui.
- **Nunca abandonar em silêncio**: uma notificação que não chegou tem de chegar ao próximo passo ou terminar em falha declarada.
- **Uma regra, um dono**: a mesma conclusão não pode existir escrita duas vezes, uma no caminho síncrono e outra no caminho de feedback.
- **Sem coordenação distribuída**, coerente com a ADR-0008: o que resolve concorrência é escrita condicional no banco.
- **Contrato de saída explícito**: se o significado de um evento publicado muda, quem consome precisa saber antes de descobrir na produção.

## Opções consideradas

1. **Claim de avanço por etapa no banco, mais aceitação de push declarando entrega apenas na última etapa** (escolhida).
2. Deduplicação por chave de negócio na mensagem: os dois produtores calculariam a mesma identidade de mensagem (`notificationId` mais canal) para colidir em `processed_messages`.
3. Um único produtor de gatilho: eliminar o caminho reativo e deixar a varredura por prazo disparar tudo.
4. Manter a aceitação do push como entrega e disparar o fallback a partir do estado da tentativa, sem olhar o da notificação.

## Decisão

Adotar a opção 1, em três partes.

**Claim de avanço por etapa.** A coluna `notification_attempt.plan_advanced_at` registra o instante em que a etapa avançou. Dentro da transação que enfileira a próxima tentativa, o handler de fallback executa:

```text
UPDATE notifications.notification_attempt
   SET plan_advanced_at = @now
 WHERE notification_id = @notificationId
   AND channel = @failedChannel
   AND plan_advanced_at IS NULL
```

Zero linhas afetadas significa que outro gatilho já comprou o avanço, e o handler devolve `Duplicate` sem efeito. É o mesmo idioma de lock otimista que o módulo já usa na transição de `queued` para `sending`.

O claim é **por etapa e não por tentativa**. O fan-out de push cria irmãos que compartilham um único prazo absoluto, então dois irmãos vencidos pediriam o mesmo avanço; um claim por tentativa deixaria os dois passarem. O `UPDATE` carimba todas as tentativas da etapa e o predicado poda partição pela janela de `created_at` da notificação, senão a escrita varre todas as partições mensais de `notification_attempt`.

O ponto de encontro é o handler, e não cada produtor. Isso é deliberado: já existem três produtores do gatilho (o veredito definitivo do dispatcher, a liberação de um hold de kill switch vencido e, a partir da fatia seguinte, a varredura de prazo), e um quarto produtor futuro herda a garantia sem alterar nada.

**A aceitação do push só declara entrega na última etapa.** Um `fallback_deadline` gravado é a prova de que existe passo posterior, porque o prazo deriva do timeout da etapa e a última etapa do plano não tem timeout. Com passo posterior, a notificação permanece `dispatched` até haver confirmação real ou até o plano concluir. Sem passo posterior, a aceitação continua sendo o desfecho mais forte que este hub vai conhecer sobre a mensagem, e encerra a notificação como antes.

**A confirmação real passa a ter leitor.** Com o rastreamento de entrega no lugar, a tentativa alcança `delivered` por webhook e nada lia esse estado. Agora a mesma escrita que encerra a notificação no caminho síncrono é chamada pelo aplicador de estado: `delivered` encerra a notificação em `delivered` e publica o evento de entrega; `failed` ou `bounced` que esgota a etapa avança o plano exatamente como uma recusa síncrona avançaria, pedindo o próximo passo ou encerrando em falha. A regra não é reescrita dentro do aplicador; ele chama o dono dela.

**Roteamento do gatilho.** O sinal de fluxo de autenticação é materializado em `notification.auth_flow` na aceitação, onde o template publicado já está em mãos, e o gatilho é endereçado a `core-auth` quando ele é verdadeiro. Nenhum produtor precisa consultar o catálogo no caminho quente para saber em que banda drenar.

### Consequências

**Positivas**

- Um único avanço por etapa, qualquer que seja o gatilho e quantos gatilhos existam.
- O fallback por prazo do push passa a ser possível, que é o que o critério de saída da fase exige.
- `araia.notification.delivered.v1` passa a afirmar entrega confirmada em todo canal que reporta uma, em vez de afirmar aceitação em push.
- A segunda metade de um código de autenticação mantém a banda de topo.

**Negativas, e a que precisa viajar com a mudança**

- **Mudança observável de contrato de saída.** `araia.notification.delivered.v1` deixa de ser publicado na aceitação do push quando o plano tem passo posterior. Um consumidor que usava esse evento como confirmação de que o push saiu do hub passa a não recebê-lo nesse caso. O guia de integração do produtor registra a nota; a consulta REST continua respondendo o estado corrente da notificação e das tentativas.
- Uma notificação de push com passo posterior fica em `dispatched` por mais tempo do que antes. Isso é o estado verdadeiro, não uma regressão, mas painel que contava `delivered` como proxy de sucesso de push precisa ser refeito sobre o estado da tentativa.
- Linhas anteriores à migração ficam com `auth_flow = false`. Nenhuma notificação em voo depende disso para correção, apenas para banda de drenagem.
- O claim é uma escrita a mais na transação do fallback, e o gatilho perdedor descobre que perdeu depois de já ter renderizado o próximo passo. É trabalho descartado no caminho raro, em troca de a decisão ficar na transação que produz o efeito.

## Prós e contras das opções

### Opção 1: claim por etapa mais aceitação condicional
- Prós: resolve os dois defeitos com uma escrita condicional; o ponto de encontro é único e produtores futuros herdam a garantia.
- Contras: muda contrato de saída; exige coluna nova e cuidado com poda de partição.

### Opção 2: deduplicação por chave de negócio na mensagem
- Prós: reaproveita `processed_messages` sem coluna nova.
- Contras: obriga os produtores a concordar sobre uma identidade sintética, o que é acoplamento entre componentes que não se conhecem; a marca vale por consumidor e não por etapa, então um consumidor novo reabre o defeito; e a marca é purgada por idade, enquanto a etapa é permanente.

### Opção 3: um único produtor
- Prós: a unicidade some como problema.
- Contras: o caminho reativo é o que dá fallback imediato quando o provedor recusa na hora; jogá-lo fora custaria até um ciclo inteiro de varredura em cima do prazo, no fluxo em que o segundo importa.

### Opção 4: fallback pelo estado da tentativa, ignorando o da notificação
- Prós: não muda o contrato de saída.
- Contras: a notificação continuaria encerrada em `delivered` enquanto o hub dispara o próximo passo, o que é contraditório em auditoria e faz a consulta REST mentir; e deixaria `delivered` significando duas coisas diferentes conforme o canal.

## Como saberemos que foi a decisão certa

- Dois gatilhos concorrentes da mesma etapa produzem exatamente uma tentativa seguinte, provado com duas transações reais e com o predicado do claim removido para ver o teste reprovar.
- Push aceito e sem confirmação dentro do prazo alcança o passo seguinte, com uma única mensagem ao cliente.
- Nenhum relato de duplicata por cliente; `notification.duplicate` e o registro do gatilho perdedor aparecem com frequência compatível com concorrência de infraestrutura.

## Referências

- Design de Sistema, §4.2, §5.1 e §5.2.
- ADR-0008: camadas de idempotência e o que cada uma protege.
- Fase 2: decomposição em fatias, decisão D7 e fatia F2-4.
