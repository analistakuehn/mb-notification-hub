---
language: pt-BR
---

# Inventário consolidado dos achados da fase 2

[Voltar ao índice](00-index.md)

As duas revisões da fase 2 usam a mesma revisão-fonte e o mesmo objeto do
documento alvo. O pacote de escopo delimitado registrou 11 achados e o pacote de
escopo de projeto registrou 31. Cinco causas aparecem nos dois, com
identificadores diferentes, e estão fundidas aqui pela causa e não pelo
identificador. O total de causas distintas é 37.

Convenção das colunas: `origem` nomeia o pacote e o identificador de lá;
`natureza` distingue o que foi corrigido em código do que foi corrigido no
registro; `estado` é o resultado desta remediação.

## Causas presentes nos dois pacotes

| Causa | Delimitado | Projeto | Severidade preservada | Natureza | Estado |
|---|---|---|---|---|---|
| Varredura por prazo não alcança tentativa devolvida à fila | `ENG-001` | `ENG-004` | alta | código e registro | `RESOLVED` |
| Relatório mensal omite três seções e o documento declara duas | `SEC-003` | `ENG-006` | alta | registro | `RESOLVED` |
| Orçamento de 20 ms do webhook sem percentil, sem teto e sem medição | `PRF-001` | `PRF-001` | média | código, medição e registro | `RESOLVED` |
| Callback Twilio sem prova de frescor e dedupe com retenção finita | `SEC-002` | `SEC-002` | média | código e registro | `RESOLVED` com risco residual |
| Documento declara HTTP 200 e a rota publica 202 | `STK-002` | `STK-001` | baixa | registro | `RESOLVED` |

## Causas exclusivas do pacote delimitado

| Causa | Origem | Severidade | Natureza | Estado |
|---|---|---|---|---|
| Handler de fallback aceita tentativa de outra notificação | `ARC-001` | alta | código | `RESOLVED` |
| Duplicata aceita no fallback de `unknown` não é observável | `ENG-002` | média | código e registro | `RESOLVED` |
| Validação de opções não alcança os limites aninhados | `STK-001` | média | código | `RESOLVED` |
| Assinatura SendGrid testada com vetor da própria implementação | `TST-001` | média | teste | `RESOLVED` |
| Supressão automática pode ser perdida depois do commit | `ARC-002` | média | código | `RESOLVED` |
| Correlação SendGrid pode vir de query não assinada | `SEC-001` | média | código e registro | `RESOLVED` |

## Causas exclusivas do pacote de projeto

| Causa | Origem | Severidade | Natureza | Estado |
|---|---|---|---|---|
| Solução não compilava na revisão-fonte | `TST-001` | alta | código | `RESOLVED` antes desta remediação |
| Fallback rederiva o plano da política publicada corrente | `ARC-002` | alta | código | `RESOLVED` |
| Documento fixa Programmable Messaging e a configuração seleciona Verify | `ARC-003` | alta | código e registro | `RESOLVED` |
| Tabela nomeia onze fatias e o texto afirma doze | `ENG-001` | alta | registro | `RESOLVED` |
| Prazo de 60 s do veredito inconclusivo substitui o timeout do passo | `PRF-003` | alta | código e registro | `RESOLVED` |
| Aritmética do fallback não fecha contra o aceite de 35 s | `PRF-004` | alta | código, teste e registro | `RESOLVED` |
| Reconciliação funcional em teste e inerte em escala | `PRF-005` | alta | código, teste e registro | `RESOLVED` |
| Allowlist de IP inaplicável na topologia exigida | `SEC-001` | alta | código e registro | `RESOLVED` |
| Duas ADRs aceitas fora das fontes e desvio negado | `ARC-001` | média | registro | `RESOLVED` |
| Seção de dados não lista nenhuma coluna que a fase criou | `ARC-004` | média | registro | `RESOLVED` |
| Controles apresentados como ativos entregues desligados ou com fail-open | `SEC-003` | média | registro | `RESOLVED` |
| Índices parciais descritos não existem na forma descrita | `PRF-002` | média | registro | `RESOLVED` |
| Correlação de callback não carrega a chave de partição | `PRF-006` | média | código | `RESOLVED` |
| Dependência atribui à fase 1b uma capacidade desta fase | `ENG-002` | média | registro | `RESOLVED` |
| Corpo do documento descreve a semântica anterior à correção 2 | `ENG-003` | média | registro | `RESOLVED` |
| Supressão por token FCM não passa pelo ledger prometido | `ENG-005` | média | registro | `RESOLVED` |
| Contrato de retry do §8 não é expressável na configuração entregue | `ENG-007` | média | registro | `RESOLVED` com dependência externa |
| Allowlist de origem compara prefixo textual em vez de rede | `STK-002` | média | código | `RESOLVED` |
| `FindSystemTimeZoneById` sobre dado ingerido, sem guarda | `STK-003` | média | código | `RESOLVED` |
| Semântica do circuit breaker omite a precondição de volume | `STK-004` | média | registro | `RESOLVED` |
| Polly consumido por tipo sem declaração de pacote | `STK-005` | média | código | `RESOLVED` |
| Timeout é por provedor e o §11.3 o exige por classe | `STK-006` | média | código e registro | `RESOLVED` por uso, desvio declarado |
| Item de caos sem artefato e sem declaração de lacuna | `TST-002` | média | registro | `RESOLVED` como pendência declarada |
| Prova do critério de saída condicional a Docker | `TST-004` | média | código e registro | `RESOLVED` |
| Nenhum oráculo mede latência de fallback | `TST-005` | média | teste, medição e registro | `RESOLVED` |
| Portão executável não alcança autenticação fora de `critical` | `TST-003` | baixa | código e registro | `RESOLVED` |

## Achados não reproduzidos

Nenhum. As 26 causas exclusivas do pacote de projeto foram tratadas junto das 11
do pacote delimitado, e nenhuma foi encerrada por não reprodução.

## Severidades preservadas

As severidades acima são as da consolidação de cada pacote, incluindo as
divergências que aqueles pacotes preservaram. Esta remediação não regradua
nenhuma delas: corrigir uma causa não muda a severidade que ela tinha.
