# Gestão de anexos do Notification Hub: mapa de implementação

**Status**: PROPOSED  
**Spec**: `SPEC-001`  
**Data**: 2026-08-31  
**Fonte**: [01-development-specification.md](01-development-specification.md)

## Sementes

| Semente | Resultado esperado | IDs de requisito | Proprietário ou adapter | Dependências | Onda | Risco ou restrição de rollout |
|---|---|---|---|---|---:|---|
| `SEED-001` | Decisões executáveis registram protocolo de claim, proteção do objeto, política de validação, versão contratual, igualdade do manifesto, roteamento e método de transferência com seus oráculos. | `ER-005`, `ER-006`, `ER-008`, `ER-010`, `ER-016`, `NFR-008` | Produto Notification Hub e `dotnet` | Development Specification, seção 8 | 0 | Nenhuma escolha condicionada por evidência pode ser convertida em código de produção antes do respectivo experimento ou corpus contratual. |
| `SEED-002` | O módulo `AttachmentManagement` possui identidade opaca, estado observável e autorização por principal e `application`, acessível somente por superfícies publicadas. | `CAP-001`, `PAC-001`, `PAC-004`, `ER-001`, `ER-002`, `ER-003`, `NFR-006` | `AttachmentManagement` e adapter `dotnet` | `SEED-001` | 1 | A lacuna de autorização REST por `application` deve fechar antes de qualquer uso cruzado com `Notifications`. |
| `SEED-003` | O conteúdo fornecido fica sob custódia S3 do hub com identidade íntegra imutável, acesso mínimo e proteção contra descarte enquanto ativo. | `CAP-001`, `PAC-011`, `PAC-012`, `ER-002`, `ER-005`, `ER-007`, `ER-013` | `AttachmentManagement` e adapter S3/KMS | `SEED-002`; decisão de identidade e proteção em `SEED-001` | 1 | Reutilizar a Infrastructure de `Audit` ou ler somente por chave violaria propriedade e proteção contra TOCTOU. |
| `SEED-004` | Validação, liberação, revogação e rejeição operam com máquina de estados que bloqueia por padrão em caso de falha e repetição segura. | `CAP-002`, `PAC-002`, `PAC-005`, `PAC-010`, `PAC-013`, `PAC-014`, `ER-004`, `ER-005`, `ER-010`, `ER-013` | `AttachmentManagement` e adapter de validação | `SEED-003`; política de validação em `SEED-001` | 2 | Indisponibilidade ou resultado inconclusivo nunca libera conteúdo. |
| `SEED-005` | Um contrato publicado permite a `Notifications` reivindicar o conjunto integral, obter snapshot imutável e verificar a liberação sem conhecer armazenamento ou persistência interna. | `CAP-003`, `PAC-002`, `PAC-004`, `ER-001`, `ER-003`, `ER-006`, `ER-009` | `AttachmentManagement.Integration` e `Notifications` | `SEED-004`; protocolo de claim em `SEED-001` | 3 | Falha entre claim e aceite não pode produzir notificação aceita sem manifesto integral. |
| `SEED-006` | REST e Kafka aceitam referências opcionais, preservam o caminho sem anexos e aplicam a forma canônica do manifesto à idempotência. | `CAP-003`, `PAC-008`, `PAC-009`, `ER-007`, `ER-008`, `ER-015`, `NFR-007` | `Notifications/Ingress`, REST e Kafka | `SEED-005`; decisões de versão, manifesto e roteamento em `SEED-001` | 3 | V1 só é usada com prova de compatibilidade; caso contrário, versões coexistem. |
| `SEED-007` | A notificação e cada tentativa preservam o snapshot aceito, enquanto mensagens internas carregam somente identificadores e estado mínimo. | `CAP-003`, `CAP-004`, `PAC-003`, `PAC-007`, `ER-007`, `ER-009`, `ER-012`, `ER-014` | `Notifications/Pipeline` e persistência do módulo | `SEED-005`, `SEED-006` | 4 | Retry e fallback não podem consultar metadado mutável nem alterar o conjunto aceito. |
| `SEED-008` | Liberação, identidade e envelope são verificados antes do ponto irreversível de cada submissão. | `CAP-004`, `PAC-006`, `PAC-013`, `ER-010`, `ER-012`, `ER-016`, `NFR-002`, `NFR-008` | `Notifications/Dispatching` e contrato de `AttachmentManagement` | `SEED-004`, `SEED-007`; parâmetros de primeira produção | 4 | Exceder o envelope, revogar a liberação ou deixá-la vencer resulta em zero chamadas ao provedor. |
| `SEED-009` | O contrato neutro de `Dispatch` e o adapter SendGrid submetem o conjunto integral e produzem evidência dos bytes enviados. | `CAP-004`, `PAC-003`, `PAC-006`, `ER-011`, `ER-012`, `ER-014`, `ER-016`, `NFR-001` | `Dispatch.Integration` e adapter SendGrid | `SEED-007`, `SEED-008`; método de transferência em `SEED-001` | 4 | Buffer, streaming ou spool só é promovido após o teste de capacidade; resultado ambíguo não autoriza reenvio cego. |
| `SEED-010` | Roteamento e fallback encerram explicitamente qualquer plano incapaz de preservar o conjunto, sem converter para link ou remover anexos. | `CAP-004`, `PAC-006`, `ER-010`, `ER-012`, `ER-015`, `NFR-003` | `Notifications` | `SEED-006`, `SEED-008`, `SEED-009`; regra de roteamento em `SEED-001` | 4 | O produtor não escolhe o canal vigente; a mudança deve preservar o contrato de produto aprovado. |
| `SEED-011` | Reconciliação, preservação, descarte e evidência operacional convergem sob falhas e permitem investigar o ciclo completo sem conteúdo bruto. | `CAP-005`, `PAC-007`, `PAC-010`, `PAC-011`, `PAC-012`, `ER-007`, `ER-013`, `ER-014`, `NFR-004`, `NFR-005` | `AttachmentManagement`, `Notifications` e contrato publicado de `Audit` | `SEED-004`, `SEED-005`, `SEED-009`, `SEED-010` | 5 | Limpeza de abandonados nunca pode remover dependência ativa; telemetria não pode vazar conteúdo ou capacidade de acesso. |
| `SEED-012` | Migrações aditivas, compatibilidade, habilitação progressiva e rollback preservam clientes antigos e itens já aceitos. | `PAC-008`, `ER-008`, `ER-015`, `NFR-007` | Persistência dos módulos e composição da plataforma | `SEED-006`, `SEED-009`, `SEED-010`, `SEED-011` | 5 | Bloquear novos aceites não pode bloquear leitura, tentativa, reconciliação ou investigação de itens existentes. |

## Pré-requisitos externos

| Semente | Pré-requisito | Responsável | Evidência | Prontidão |
|---|---|---|---|---|
| `SEED-003` | Seleção técnica da identidade imutável, IAM, KMS e proteção S3 por experimento de troca de objeto e acesso cruzado. | `dotnet-architect` | Development Specification, decisão Identidade e proteção do objeto | Gate obrigatório da onda 0 |
| `SEED-004` | Política executável de tipos, conteúdo protegido, antimalware, validade da liberação e tratamento de resultado inconclusivo. | Produto Notification Hub e `dotnet-architect` | `PAC-005`, `PAC-013`, `PAC-014` | Gate obrigatório antes da implementação da liberação |
| `SEED-005` | Protocolo de consistência do claim aprovado mediante injeção de falhas. | `dotnet-architect` | `ER-006` e decisão Consistência do claim | Gate obrigatório antes da integração com `Notifications` |
| `SEED-006` | Semântica do manifesto, comportamento de roteamento e estratégia V1 ou V2 comprovados por corpus e testes de contrato. | Produto Notification Hub e `dotnet-architect` | `PAC-006`, `PAC-008`, `PAC-009` | Gate obrigatório antes de publicar escritores |
| `SEED-008` | Parâmetros de quantidade, tamanho, tipos e envelope efetivo aprovados para a primeira produção. | Produto Notification Hub | `ER-010`, `ER-016`, `NFR-008` e decisão Transferência ao provedor | Gate obrigatório antes de aceitar anexos |
| `SEED-009` | Orçamento de heap, working set, CPU, I/O, concorrência e latência para o cenário aprovado, seguido de comparação entre buffer, streaming e spool. | `dotnet-architect` | `ER-016`, `NFR-008` | Gate obrigatório antes da promoção do adapter |
| `SEED-011` | Regra de descarte compatível com a proteção obrigatória enquanto houver dependência ativa. | Produto Notification Hub | `PAC-011` e `ER-013` | Gate obrigatório antes da limpeza automática |

## Visão de dependências

As arestas canônicas são:

- `SEED-001 -> SEED-002 -> SEED-003 -> SEED-004 -> SEED-005 -> SEED-006`.
- `SEED-005 -> SEED-007` e `SEED-006 -> SEED-007`.
- `SEED-004 -> SEED-008` e `SEED-007 -> SEED-008`.
- `SEED-007 -> SEED-009` e `SEED-008 -> SEED-009`.
- `SEED-006 -> SEED-010`, `SEED-008 -> SEED-010` e `SEED-009 -> SEED-010`.
- `SEED-004 -> SEED-011`, `SEED-005 -> SEED-011`, `SEED-009 -> SEED-011` e `SEED-010 -> SEED-011`.
- `SEED-006 -> SEED-012`, `SEED-009 -> SEED-012`, `SEED-010 -> SEED-012` e `SEED-011 -> SEED-012`.

A cadeia de dependências obrigatória passa pelas decisões da onda 0, custódia, liberação, claim, ingresso, snapshot, preflight, adapter, fallback, operação e rollout. Fitness functions, testes de contrato, segurança, observabilidade e ensaios de falha evoluem junto da semente cujo comportamento protegem. A preparação de fixtures, fake SendGrid, cenários LocalStack e sentinelas de vazamento é candidata à execução paralela, condicionada pelo PLAN à ausência de colisões de arquivos, recursos e ambientes, e não antecipa o aceite dos respectivos requisitos.

## Limite do PLAN

O estágio PLAN é responsável pelos limites de Delivery Slice, estimativas, tarefas concretas, lista final de arquivos, critérios finais de aceitação, fases de TDD, artefatos obrigatórios e Definition of Ready e Definition of Done de cada Delivery Slice.

Este mapa preserva resultados, dependências, ondas, proprietários técnicos e gates. Ele não autoriza implementação nem substitui a decomposição do PLAN.
