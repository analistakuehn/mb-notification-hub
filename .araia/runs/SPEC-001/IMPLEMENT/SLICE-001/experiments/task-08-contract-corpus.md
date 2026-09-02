# Corpus contratual do manifesto e decisão de versão

**Tarefa**: 8, levantar o corpus contratual do manifesto e decidir a versão  
**Data**: 2026-08-31  
**Resultado**: `DESIGN: READY`  
**Responsável**: `dotnet-architect`

## Resultado

Solicitações com anexos usarão contratos públicos V2 coexistentes no REST e no Kafka. Os contratos V1 continuam aceitando somente notificações sem anexos. A adição de `attachments` ao V1 foi rejeitada porque clientes antigos aceitam o membro desconhecido e o descartam silenciosamente, preservando a sintaxe e alterando o efeito.

O ingresso V2 transporta apenas referências opacas. Nome, media type, comprimento e identidade do conteúdo pertencem ao snapshot imutável retornado pelo claim. Uma referência pública permanece vinculada ao mesmo snapshot liberado; qualquer alteração dessas propriedades exige nova referência.

## Forma promovida

```json
{
  "attachments": ["att_alpha", "att_beta"]
}
```

O ingresso não carrega bytes, nome, media type, digest, chave, URL, `VersionId` nem informação de armazenamento.

## Semântica canônica

- Membro ausente, `null` e `[]` representam manifesto vazio e são omitidos da forma canônica.
- A ordem é significativa e preservada.
- Duplicatas são inválidas e recusadas antes do claim e do aceite.
- Cada referência é comparada ordinalmente, sem ordenação, deduplicação ou normalização.
- `attachments`, quando não vazio, entra depois de `application` na ordem fixa da forma canônica.
- Nome, media type, comprimento e identidade de conteúdo vêm somente do snapshot liberado.
- Alterar qualquer propriedade liberada exige nova referência opaca.

## Vetores congelados

| Vetor | Resultado |
|---|---|
| Manifesto ausente | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: null` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| `attachments: []` | `ae72ea096493d48cfcd34578542f2249b677872c5834778919df27af34cdbdeb` |
| Corpo completo vigente, sem anexos | `135fb9992e7260f847834935d5dff24a98664975989a3dd57962082b11f6557c` |
| `att_alpha`, `att_beta` | `5b707f0391c59c39cc4e0547f9f118d25a93d6bc4cbbe796191be44f0b4d8199` |
| Ordem invertida | `97b013c55139f5fcd30a3d8685a3c326cde8984fcb232d74bd85552dc1fc789d` |
| Segunda referência alterada | `71bb9b9799e73874e8eee914f91a2d0e1e9a69307437725073c02b7cb653c0dd` |
| Referência duplicada | recusada |

## Decisão por superfície

### REST

- Preservar `POST /v1/notifications` e o documento OpenAPI V1 sem alterações.
- Criar `POST /v2/notifications` para o contrato com `attachments`.
- Publicar um documento OpenAPI V2 separado.
- Fazer o V1 novo recusar especificamente `attachments`, sem fechar genericamente a tolerância a membros futuros.
- Habilitar produtores V2 somente depois que todos os nós estiverem atualizados.

O aceite REST com anexos permanece desabilitado até o write set autorizar a rota V2, seu mapeamento e o documento OpenAPI V2.

### Kafka

- Preservar o tópico `notifications.requested.v1` e o type `araia.notification.requested.v1`.
- Criar o tópico `notifications.requested.v2` e o type `araia.notification.requested.v2`.
- Fazer o hub novo consumir V1 e V2.
- Exigir correspondência entre tópico e type.
- Recusar `attachments` no V1.
- Proibir produtores novos de publicar anexos em V1.

### `AttachmentManagement.Integration.V1`

V1 é adequada porque a superfície é nova. O claim retorna uma lista ordenada e imutável com:

- referência pública opaca;
- identidade de conteúdo opaca;
- nome liberado;
- media type liberado;
- comprimento liberado.

O contrato não expõe digest, chave, `VersionId`, bucket, CMK, URL, bytes nem tipos AWS. O snapshot não é reescrito por revogação; a elegibilidade é relida de modo fail-closed antes da tentativa.

### `Dispatch.Integration.V1`

`DispatchRequest` recebe uma alteração aditiva em V1:

- membro opcional de anexos neutros, com default ausente;
- chamadas antigas continuam compatíveis em código-fonte;
- o contrato permanece in-process e é recompilado no mesmo assembly;
- adaptador incapaz de preservar o conjunto recusa explicitamente a requisição com anexos.

Revisar para V2 se `Dispatch` atravessar processo, deploy independente ou serialização.

## Prova de compatibilidade

O roteiro de teste autocontido está em `task-08-contract-corpus/` e executa:

```text
dotnet run --project .araia/runs/SPEC-001/IMPLEMENT/SLICE-001/experiments/task-08-contract-corpus/ContractCorpus.csproj --no-restore
```

Resultado: código de saída `0`, com todas as assertions aprovadas.

| Arquivo | SHA-256 |
|---|---|
| `Program.cs` | `BAABB7CEDA288DACA930F72BE6D6FA030E0B38E556F98949AFBC566C9419C2AC` |
| `ContractCorpus.csproj` | `CDD6F0B4E4B9C72196431282FC7F42508E52E0F6A10B88A948A157BBDABD220B` |
| `packages.lock.json` | `2241481B7E74DCDCD600303146B730ECC12CBD8E49AA55755EF95EE499FE229A` |

Observações decisivas:

- produtor REST V1 para servidor novo: aceito sem anexos;
- payload novo para contrato REST antigo: aceito com descarte silencioso de `attachments`;
- binder Kafka V1 antigo com `attachments`: aceito com descarte silencioso;
- router novo: V1 antigo aceito, V1 com `attachments` recusado, V2 com anexos aceito e type desconhecido recusado;
- alteração de nome, media type ou identidade do conteúdo muda o fingerprint do snapshot.

Validações complementares:

```text
RequestPayloadHashTests: 10 aprovados; 0 falhas; 0 ignorados
AttachmentIngressContractTests: 5 aprovados; 0 falhas; 0 ignorados
```

O SHA-256 congelado do OpenAPI V1 permanece `8dc9d320d2914a9703b63bae5dc9f46d18d22e853f4854ae729646f65bf567c7`.

## Rollout

1. Publicar `AttachmentManagement.Integration.V1` e leitores internos sem habilitar produtores.
2. Publicar REST V2, OpenAPI V2 e consumer Kafka V2, mantendo V1.
3. Publicar a alteração aditiva de `DispatchRequest` e a recusa fail-closed dos adaptadores incompatíveis.
4. Confirmar que todos os nós suportam V2.
5. Habilitar produtores em `/v2/notifications` ou `notifications.requested.v2`.
6. Manter V1 enquanto houver produtores antigos.

## Rollback

- Desabilitar novos ingressos V2.
- Manter leitores V2 até drenar notificações aceitas.
- Preservar claims e snapshots existentes.
- Continuar aceitando V1 sem anexos.
- Nunca reprocessar uma solicitação V2 como V1.
- Remover leitores V2 somente quando não houver estado com anexos pendente.

## Condições de revisão

Reabrir a decisão se a referência puder ser vinculada a outro nome, tipo ou conteúdo; se produtores puderem escolher propriedades de apresentação; se `Dispatch` cruzar processo ou deploy; se o versionamento REST não puder publicar documento V2 separado; ou se outro canal passar a preservar anexos.

## Próximos artefatos

A Tarefa 12 registra a forma canônica em ADR. As Tarefas 22 a 24 implementam o contrato e exigem a expansão de write set da superfície REST V2 antes de qualquer escrita fora dos caminhos atualmente autorizados.
