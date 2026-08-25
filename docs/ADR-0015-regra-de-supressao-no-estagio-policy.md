---
language: pt-BR
---

# ADR-0015: Regra de supressão no estágio Policy

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-25 |
| **Decisores** | Arquitetura, Produto, Compliance |
| **Consultados** | Engenharia de Plataforma, SRE |
| **Relacionadas** | ADR-0011 (política como configuração de classe), ADR-0012 (ContactConsent como fonte da verdade), ADR-0003 (pipeline de estágios), ADR-0004 (resolução de contato dentro do hub) |
| **Documento-mãe** | Design de Sistema, §4.3, §6, §10.2 A5; Fase 2: decomposição em fatias, D3 e fatia F2-6 |

## Contexto e problema

A ADR-0011 fixou cinco regras para o estágio *Policy* da v1 e fechou a lista: consentimento, janela de silêncio, deduplicação, limite por destinatário e seleção de canal. O §4.3 do design de sistema, escrito depois, lista a supressão entre as regras desse mesmo estágio. As duas afirmações não podem valer ao mesmo tempo, e a fase 2 é onde a diferença deixa de ser textual: com o rastreamento de entrega no lugar, o hub passa a saber que um provedor recusou um destino de forma definitiva, e precisa decidir o que fazer com esse conhecimento na próxima notificação.

A própria ADR-0011 previu esse caso. O nível 3 do roteiro dela diz que tipos de regra novos entram conforme evidência, cada um como um `IPolicyRule` e com ADR curta. Esta é essa ADR.

O que está em jogo não é apenas organização de código. Continuar escrevendo para um endereço que o provedor declarou inexistente gasta reputação de envio a cada mensagem, e reputação de envio é o que faz as mensagens dos outros destinatários chegarem. Do outro lado, deixar de escrever para alguém é retirar um canal de uma pessoa real, o que em fluxo de autenticação é a diferença entre um incômodo e um cliente trancado para fora. A regra precisa das duas coisas: recusar o que não chega e recusar o mínimo possível.

## Fatores de decisão

- **Uma decisão auditável regra a regra**, no mesmo formato das demais, porque a pergunta "por que esse canal" continua sendo respondida pela avaliação da política.
- **Motivo estável no catálogo publicado**: quem produz notificação precisa distinguir "sem consentimento" de "canal suprimido" sem ler texto livre.
- **Nenhuma ida a mais ao banco no caminho quente**, coerente com a ADR-0004: a resolução do destinatário já acontece uma vez por notificação.
- **A decisão de suprimir pertence a quem tem o histórico**, coerente com a ADR-0012: o acúmulo de sinais é dado de contato.
- **Reversível e atribuível**: uma decisão automática sobre falar ou não falar com uma pessoa precisa ter quem a desfaça e trilha de quem desfez.

## Opções consideradas

1. **Regra nova `SuppressionGate` no estágio Policy, entre `ConsentGate` e `QuietHours`, lendo a supressão pelo snapshot já resolvido** (escolhida).
2. Filtrar destinos suprimidos dentro do estágio Resolve, junto com a resolução de contato.
3. Recusar no despacho, no instante de revelar o valor do contato.
4. Estender a regra de consentimento para também olhar supressão.
5. Deixar o próprio provedor recusar, sem regra alguma no hub.

## Decisão

Adotar a opção 1.

**A regra e sua posição.** `SuppressionGate` é a segunda regra da lista ordenada, depois de `ConsentGate` e antes de `QuietHours`. Depois do consentimento porque um destinatário que nunca autorizou o canal é recusado por um motivo mais forte, e é esse o motivo que a trilha deve registrar. Antes da janela de silêncio porque adiar uma notificação por horas para recusá-la de manhã é trabalho que ninguém pediu, e porque uma notificação adiada ocupa estado, índice e varredura até a liberação.

**O que a regra filtra.** Ela recebe o conjunto de canais remanescente e retira os canais cujos endereços ativos estão todos suprimidos. Zero canais restantes recusa a notificação com o motivo canônico `channel-suppressed`, novo membro de `NotificationRejectionReasons`. Como todas as demais regras, ela grava evidência JSON própria: o conjunto que recebeu, o que caiu e o que sobreviveu.

**Um canal cai apenas quando todos os seus endereços ativos estão suprimidos.** Um destinatário que mantém dois endereços de e-mail e teve um deles recusado continua alcançável no outro, e derrubar o canal por causa do endereço morto transformaria uma proteção do destinatário em falha de entrega contra ele. A consequência aceita está registrada nas consequências negativas.

**A leitura entra pelo snapshot já publicado.** `RecipientSnapshot` ganha o membro `Suppressions`, e não uma superfície V2 nem uma leitura própria. O estágio Resolve já carrega o snapshot uma vez por notificação; uma leitura separada acrescentaria ida ao banco no caminho quente e, pior, permitiria decidir sobre um estado diferente daquele que foi resolvido, que é exatamente o tipo de incoerência que a política existe para não ter.

**A decisão de suprimir não é desta regra.** O acúmulo por canal, e-mail na primeira ocorrência definitiva e os demais canais na segunda dentro de sete dias, vive dentro do ContactConsent, porque só ele tem o histórico de sinais e exportá-lo seria exportar dado de contato. Esta regra lê um estado; ela não o produz.

**Reversão.** A supressão é sempre removível por ato humano registrado, por rota REST do próprio ContactConsent, com papel de autorização e limite de taxa próprios, e a remoção grava `suppression.removed` com o ator. A linha é carimbada, nunca apagada: a pergunta que um auditor faz depois é por que uma mensagem não foi enviada em determinado dia, e a resposta precisa sobreviver à reversão.

**Fronteira dado x código, na régua da ADR-0011.** Esta é uma mudança de código, com deploy, porque é tipo de regra novo. Os limiares do acúmulo também são código, e deliberadamente: eles não são preferência de classe, são consequência do comportamento dos provedores e da proteção de reputação, e mudá-los altera quem o hub deixa de alcançar. Se aparecer demanda concreta duas vezes para variá-los por aplicação, eles sobem para o vocabulário da política pela mesma régua que a ADR-0011 fixou.

### Consequências

**Positivas**

- Uma recusa definitiva do provedor deixa de virar reincidência: a próxima notificação para aquele destino é recusada antes de gastar renderização, despacho e reputação.
- A auditoria responde "por que esse canal" com uma linha de avaliação a mais, no formato que o Compliance já lê.
- O produtor recebe um motivo estável e distinto, em vez de descobrir a supressão como falha de entrega genérica.
- O caminho quente não ganha nenhuma consulta: a supressão viaja no snapshot que já era carregado.

**Negativas**

- **A lista de regras da v1 deixa de ser a lista da ADR-0011.** Este é o primeiro uso do nível 3 e ele consome parte do orçamento que aquela ADR estabeleceu para si mesma: zero ou um tipo de regra novo em seis meses. O contador começa aqui.
- **Um endereço suprimido ainda pode ser endereçado quando o destinatário mantém outro endereço ativo no mesmo canal.** A regra decide por canal, e a escolha do endereço acontece no estágio Route, que hoje ordena por verificação e por identificador sem olhar supressão. O caso é raro, porque a declaração de contatos remove o endereço antigo quando o conjunto muda, mas ele existe e o fechamento pertence à seleção de endereço, não a esta regra.
- **Uma supressão indevida cala o canal até um humano agir.** É o preço de a reversão ser humana, e é o lado certo do risco: o inverso seria uma reversão automática desfazendo a proteção que o provedor pediu.
- Regra nova é mais uma linha de `policy_evaluation` por notificação, com o custo de escrita correspondente.

## Prós e contras das opções

### Opção 1: regra no estágio Policy, lendo o snapshot
- Prós: mesma forma das outras regras; auditável regra a regra; motivo estável; sem consulta extra; a ordem expressa a prioridade entre as recusas.
- Contras: consome o orçamento de regras novas da ADR-0011; a decisão continua sendo por canal.

### Opção 2: filtrar no estágio Resolve
- Prós: o snapshot está em mãos ali mesmo.
- Contras: o Resolve responde quem é o destinatário, não o que a política permite; a recusa perderia a linha de avaliação e a pergunta "por que esse canal" voltaria a ser respondida por texto livre; e a ordem entre as recusas deixaria de ser explícita.

### Opção 3: recusar no despacho
- Prós: é o último instante possível, com a informação mais fresca.
- Contras: paga renderização, seleção de provedor e uma tentativa gravada para recusar o que já se sabia; e a recusa viraria falha de entrega, apagando a distinção entre "não enviamos" e "não chegou".

### Opção 4: estender a regra de consentimento
- Prós: nenhuma regra nova, nenhum consumo do orçamento da ADR-0011.
- Contras: junta duas bases jurídicas e operacionais distintas sob um motivo só; a evidência da regra passaria a misturar consentimento com falha de provedor; e a auditoria perderia a resposta que mais importa aqui, que é qual das duas coisas barrou a mensagem.

### Opção 5: nenhuma regra, deixar o provedor recusar
- Prós: custo zero de implementação.
- Contras: é exatamente o comportamento que destrói reputação de envio, e o §10.2 A5 existe por causa dele; além disso a recusa do provedor é cobrada e contabilizada contra o remetente.

## Como saberemos que foi a decisão certa

- Um bounce definitivo de e-mail suprime na primeira ocorrência e a notificação seguinte para aquele canal é recusada no estágio Policy com `channel-suppressed`, provado por teste de integração de ponta a ponta.
- Um contato suprimido deixa de ser elegível de imediato, e não quando o snapshot cacheado expira.
- Nenhuma remoção manual acontece sem ator e justificativa na trilha.
- Se aparecer um segundo tipo de regra novo dentro de seis meses, o vocabulário da v1 estava errado e a ADR-0011 pede revisão, não mais uma exceção.

## Referências

- Design de Sistema, §4.3 (regras do estágio Policy), §6 (`SUPPRESSION` ancorada em `CONTACT_POINT`) e §10.2 A5 (acúmulo por canal).
- ADR-0011: lista de regras da v1, pontos de extensão e o roteiro de níveis.
- ADR-0012: o ContactConsent como fonte da verdade de contato e consentimento.
- Fase 2: decomposição em fatias, decisão D3 e fatia F2-6.
