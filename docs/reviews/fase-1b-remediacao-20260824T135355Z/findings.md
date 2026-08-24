# Achados consolidados da remediação da fase 1B

Este documento registra os achados confirmados nas três rodadas independentes
de revisão. O estado `resolvido` indica que a causa foi corrigida e recebeu
verificação específica. A confirmação global permanece registrada no índice e
no recibo da verificação final deste pacote após a remediação.

## Achados da revisão original

| ID | Severidade | Lente | Síntese | Estado |
| --- | --- | --- | --- | --- |
| `ENG-001` | média | Engenharia de software | O documento mantinha estados de implementação incompatíveis para o worker e para as fatias B3 e B15. | resolvido |
| `ENG-002` | média | Engenharia de software | A documentação pública do catálogo ainda incluía motivos de falha no conjunto fechado de rejeições. | resolvido |
| `STK-001` | baixa | Qualidade .NET | O Stack Profile omitia Kafka no eixo de mensageria. | resolvido |
| `TST-001` | média | Testes | O gate operacional não possuía oráculo executável nem recibo persistido. | resolvido |
| `ARC-001` | alta | Arquitetura | A infraestrutura da fase não pertencia a uma fatia de entrega bloqueante. | resolvido |
| `ARC-002` | média | Arquitetura | O documento aceito dependia de ADRs ainda marcadas como propostas. | resolvido |
| `ARC-003` | média | Arquitetura | O catálogo de saída anunciava `contact_suppressed`, que não era publicado. | resolvido |
| `ARC-004` | média | Arquitetura | A entrada de contatos prometia tokens de dispositivo fora do contrato implementado. | resolvido |
| `ARC-005` | alta | Arquitetura | A fatia B14 declarava oito respostas, embora a evidência sustentasse sete respostas e uma lacuna de entrega. | resolvido |
| `SEC-001` | alta | Segurança | A identidade Kafka podia ser autodeclarada e permitir que um producer se passasse por outro. | resolvido |
| `SEC-002` | alta | Segurança | O kill switch crítico não possuía entrega planejada. | resolvido |

O pacote detalhado da rodada original está em
[`../docs-fases-fase-1b-fundacao-md-20260824T133135Z/`](../docs-fases-fase-1b-fundacao-md-20260824T133135Z/00-index.md).

## Achados da primeira verificação após a remediação

| ID | Severidade | Lente | Síntese | Estado |
| --- | --- | --- | --- | --- |
| `PRF-001` | alta | Performance | O releaser selecionava os primeiros 100 holds antes de verificar a elegibilidade, permitindo bloqueio head-of-line. | resolvido |
| `ENG-003` | alta | Engenharia de software | A unicidade vitalícia combinada com `ON CONFLICT DO NOTHING` impedia reabrir o hold em um novo ciclo de bloqueio. | resolvido |
| `ENG-004` | baixa | Engenharia de software | O contrato HTTP documentava dois tipos exclusivos, embora três tipos fossem exclusivos do transporte. | resolvido |
| `STK-002` | baixa | Qualidade .NET | O handler de solicitação excedia o limite arquitetural de sete dependências. | resolvido |
| `STK-003` | média | Qualidade .NET | O cache do kill switch e o registro de producers usavam relógio civil para controlar validade. | resolvido |
| `TST-002` | média | Testes | Os testes de hold protegiam o oráculo anterior e não cobriam reabertura, concorrência e bloqueio head-of-line. | resolvido |
| `ARC-006` | alta | Arquitetura | O limite final de dispatch verificava somente o canal e podia ignorar o bloqueio da aplicação. | resolvido |
| `SEC-003` | alta | Segurança | Respostas 200 malformadas do Microsoft Graph podiam ser interpretadas como ausência de atribuições. | resolvido |
| `SEC-004` | média | Segurança | O corpo administrativo `{}` era interpretado como desativação do kill switch. | resolvido |
| `SEC-005` | alta | Segurança | A DLT copiava payload não confiável em recusas anteriores ao estabelecimento de confiança do producer. | resolvido |

## Achado da validação pós-correção

| ID | Severidade | Lente | Síntese | Estado |
| --- | --- | --- | --- | --- |
| `TST-003` | média | Testes | O teste de retenção compartilhava a fixture de Core com um teste de kill switch e podia processar o envelope criptográfico sintético deixado por esse teste. | resolvido |

A reprodução mínima executou o teste contaminador antes do varredor e confirmou
a mesma exceção da suíte completa. `RenderedContentRetentionTests` passou a usar
uma fixture exclusiva da classe, sem desabilitar o paralelismo global. O grupo
interferente passou com 2 de 2 testes, a classe passou com 5 de 5 testes e a
suíte completa passou com 411 aprovações, 2 omissões condicionais e nenhuma
falha.

## Achados da verificação completa

| ID consolidado | Severidade | Lente | Síntese | Estado |
| --- | --- | --- | --- | --- |
| `R3-PRF-001` | média | Performance | Falhas de atualização do registro de producers não eram compartilhadas e podiam causar consultas serializadas repetidas. | resolvido |
| `R3-ENG-001` | alta | Engenharia de software | A chave de idempotência Kafka aceitava espaços e mais de 200 caracteres, permitindo envenenar a partição. | resolvido |
| `R3-ENG-002` | alta | Engenharia de software | Campos opcionais Kafka malformados eram convertidos em ausência, inclusive agendamento inválido transformado em envio imediato. | resolvido |
| `R3-ENG-003` | média | Engenharia de software | Quando o Redis não encontrava o valor, o kill switch podia bloquear um replay já aceito antes da consulta idempotente ao PostgreSQL. | resolvido |
| `R3-STK-001` | alta | Qualidade .NET | O cache do kill switch podia devolver o primeiro snapshot depois que a própria carga já consumisse o TTL. | resolvido |
| `R3-STK-002` | alta | Qualidade .NET | O registro de producers desconsiderava a duração da consulta na idade absoluta de autorização. | resolvido |
| `R3-STK-003` | baixa | Qualidade .NET | Um método de dispatch e um método auxiliar do Kafka possuíam oito parâmetros. | resolvido |
| `R3-STK-004` | baixa | Qualidade .NET | Tipos de `System.Globalization` e `System.Text.Json` apareciam totalmente qualificados em corpos C#. | resolvido |
| `R3-TST-001` | média | Testes | O teste da DLT pré-confiança não espalhava sentinelas por key, headers e todos os campos não confiáveis. | resolvido |
| `R3-TST-002` | baixa | Testes | Um teste afirmava incorretamente que `producer-disabled` era inalcançável. | resolvido |
| `R3-TST-003` | baixa | Testes | Um teste com duas instâncias locais se apresentava como prova de caches em processos independentes. | resolvido |
| `R3-ARC-001` | média | Arquitetura | `ProducerDisabled` no REST não produzia a mesma trilha e o mesmo evento de rejeição do caminho Kafka. | resolvido |
| `R3-ARC-002` | alta | Arquitetura | Um hold inválido ou órfão podia bloquear indefinidamente a retomada dos holds posteriores. | resolvido |
| `R3-ARC-003` | média | Arquitetura | Os contratos documentavam dois tipos HTTP exclusivos, embora a implementação possuísse três. | resolvido |
| `R3-SEC-001` | alta | Segurança | A DLT pré-confiança ainda copiava key, `traceparent` e campos controlados pelo producer. | resolvido |
| `R3-SEC-002` | alta | Segurança | O gate Graph não comprovava a identidade do service principal nem exigia a role canônica antes de aprovar zero atribuições. | resolvido |
| `R3-SEC-003` | alta | Segurança | O dispatch não vinculava `attemptId` ao `notificationId` antes de processar o envelope. | resolvido |

Os três revisores confirmaram a resolução dos 17 critérios sobre o manifesto
final de 104 arquivos. O recibo detalhado está em
[`final-recheck.md`](final-recheck.md).

## Divergências de classificação preservadas

- O bloqueio head-of-line foi classificado como Performance na consolidação e
  como Engenharia de software pelo especialista. A severidade alta foi mantida.
- A falha de reabertura do hold foi classificada como Engenharia de software na
  consolidação e como Arquitetura pelo especialista. A severidade alta foi
  mantida.
- Os três revisores concordaram com as evidências e com a necessidade de
  correção, mesmo quando atribuíram lentes diferentes.

## Gates externos

Os itens abaixo continuam sendo condições operacionais, não achados de código
em aberto:

- recibo real de ACL exclusiva por tópico e de drift da infraestrutura;
- execução da ferramenta de go-live contra PostgreSQL e Microsoft Graph reais;
- comprovação de propagação do kill switch em múltiplas instâncias até
  `t0 + 10 s`;
- smoke tests dos provedores reais, condicionados a credenciais e autorização
  explícita para efeitos externos.
