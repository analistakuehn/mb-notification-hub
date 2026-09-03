# Interpretação arquitetural brownfield

## Evidência e estado observado

`SOLUTION_INSPECTION: PASS`. A inspeção mecânica foi executada sem build, restore ou alteração do Stack Profile e declarou evidência suficiente para interpretação arquitetural (`.araia/runs/SPECIFY-20260831T031955Z/artifacts/docs/SPEC-001/analyses/.staging/01-solution-inspection.md:3-6`, `:62-66`).

### Fatos observados

- A solução usa .NET 10, Minimal APIs, EF Core com PostgreSQL, SQS, Kafka, Redis, S3, KMS e JWT em um monólito modular (`01-solution-inspection.md:10-28`; `.araia/stack-profile.yaml:4-32`).
- O Stack Profile está marcado como editado manualmente e deve permanecer preservado (`.araia/stack-profile.yaml:1-3`). O eixo `messaging-consumer-pattern: none` diverge do uso direto de Kafka e SQS, divergência já registrada sem sobrescrita automática (`01-solution-inspection.md:46-50`).
- `Notifications` possui o ciclo de vida da notificação, ingestão, pipeline, tentativa, fallback e rastreamento. Ele consome módulos irmãos somente por `Integration.V1` e não pode acessar dados ou tipos internos de outro contexto (`src/Platform.Api/Modules/Notifications/AGENTS.md:9-43`).
- `Notifications` possui `notification`, `notification_attempt`, `policy_evaluation`, `delivery_event`, `idempotency_key` e demais estados de execução. A outbox e `processed_messages` pertencem à infraestrutura da plataforma (`src/Platform.Api/Modules/Notifications/AGENTS.md:60-66`).
- `Dispatch` possui seleção e adaptadores de provedor. Ele não possui estado de tentativa, fallback ou auditoria e traduz um `DispatchRequest` em uma chamada ao provedor (`src/Platform.Api/Modules/Dispatch/AGENTS.md:5-25`, `:39-43`).
- As fitness functions permitem dependência entre módulos apenas pela superfície `Integration.V1`; infraestrutura e host worker não podem depender de módulos (`tests/Platform.ArchTests/ArchitectureTests.cs:66-96`, `:137-159`, `:201-245`).
- A solicitação atual não representa anexos (`RequestNotification.Command.cs:7-36`). O hash idempotente cobre uma forma JSON fixa sem referências de arquivo (`RequestNotification.PayloadHash.cs:13-26`). `EmailMessage` possui somente assunto, preheader, HTML e texto (`RenderedMessage.cs:23-27`). A forma enviada ao SendGrid não possui `attachments` (`src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/SendGridMailRequest.cs:10-15`).
- O barramento limita registros a 256 KB e proíbe anexos e dados de contato (`docs/guia-integracao-produtor.md:210-216`). Portanto, bytes, base64, chaves S3 e capacidades de upload não podem entrar em Kafka, SQS, outbox ou dead-letter.
- O caminho de `SendGridMailRequest.cs` informado no despacho não existe sem o segmento `Infrastructure`; o arquivo citado acima é a superfície real.

## Intenção aceita

- `AttachmentManagement` é o proprietário aceito da custódia, validação, referência opaca e ciclo de vida dos anexos (`docs/SPEC-001/specification.md:133-136`).
- O produtor realiza upload gerenciado, aguarda liberação e envia somente referências opacas. A custódia usa S3 sob controle do hub; conteúdo inline e S3 arbitrário do cliente estão fora do escopo (`docs/prd-attachment-management.md:97-107`).
- `Notifications` só pode aceitar anexos liberados, íntegros, pertencentes à mesma `application` e usados por principal autorizado. Referências e propriedades de entrega integram a idempotência (`docs/prd-attachment-management.md:137-144`).
- A entrega deve preservar todo o conjunto, comprovar a relação entre bytes validados e submetidos e falhar explicitamente, sem remover arquivos ou converter para link (`docs/prd-attachment-management.md:146-154`).
- O conteúdo deve permanecer imutável, protegido e indisponível ao provedor quando a liberação estiver vencida, revogada ou inconclusiva (`docs/prd-attachment-management.md:174-182`).
- Falhas parciais devem convergir para estado recuperável ou descarte conhecido, sem duplicação nem avanço incorreto (`docs/prd-attachment-management.md:184-189`).
- A compatibilidade do fluxo sem anexos abrange REST, Kafka, idempotência, recusas, eventos e seleção de canal (`docs/prd-attachment-management.md:165-170`).

O PRD não decide bucket, chave, URL de upload, IAM, KMS, versionamento, tabelas, eventos internos, scanner ou estratégia de streaming (`docs/prd-attachment-management.md:53-62`). Essas escolhas continuam técnicas.

## Limites e direção recomendados

1. `AttachmentManagement` deve possuir objetos S3, metadados internos, integridade, validação, liberação, revogação, descarte e vínculos que impedem descarte.
2. A aplicação produtora deve usar a superfície externa de `AttachmentManagement` para registro, upload e acompanhamento.
3. `Notifications` deve consumir um contrato versionado de `AttachmentManagement` para validar e reivindicar atomicamente o manifesto liberado. Ele deve armazenar somente o snapshot necessário à idempotência, tentativa e evidência, sem conhecer bucket ou chave.
4. `Notifications` continua como único orquestrador da tentativa e consumidor de `Dispatch.Integration.V1`.
5. `Dispatch` deve receber conteúdo de anexo em forma neutra de provedor e continuar sem acessar tabelas, S3 ou estado de `AttachmentManagement`.

Essa direção preserva `Notifications -> AttachmentManagement.Integration.*` e `Notifications -> Dispatch.Integration.*`, sem criar dependência entre `Dispatch` e o armazenamento de anexos.

## Decisões técnicas candidatas

| Decisão necessária | Alternativas e trade-off | Validação necessária |
|---|---|---|
| Claim e consistência | Transação compartilhada por contrato publicado, semelhante ao acréscimo transacional de auditoria (`Notifications/AGENTS.md:68-77`), oferece atomicidade e maior acoplamento. Reserva idempotente seguida de compensação preserva autonomia, mas exige reconciliação de claims órfãos. | Falhas injetadas em cada ponto entre claim, aceite, outbox e commit; nenhuma notificação aceita sem claim válido. |
| Contrato público | Campo opcional em V1 preserva produtores antigos se todos os serializadores e schemas tolerarem a adição. V2 coexistente reduz risco contratual e amplia custo operacional. | Contract tests para REST e Kafka, clientes antigos, OpenAPI, schema, dead-letter e eventos. |
| Semântica idempotente | Definir ordem, duplicatas, nome, tipo e demais propriedades do manifesto. A ausência de anexos deve produzir exatamente o hash atual. | Golden tests do hash atual e matriz de replay e conflito com referências e propriedades. |
| Transferência ao provedor | Buffer simplifica o adaptador e pressiona memória; streaming limita memória e aumenta complexidade de ciclo de vida, hash e retentativa. | Carga com envelope máximo aprovado, limites de memória, cancelamento, falha parcial e igualdade byte a byte. |
| Evolução de `Dispatch` | Alterar `EmailMessage` ou `DispatchRequest` em V1 pode manter compatibilidade de fonte por membro opcional, mas muda o contrato publicado. Criar V2 exige adaptar a fitness function, hoje fixada em `Integration.V1`. | Compilação de todos os consumidores, contract tests e fitness functions para a versão escolhida. |
| Proteção e validação | Modelo de upload, imutabilidade S3, versionamento, IAM, KMS, scanner, tipos permitidos e validade da liberação ainda precisam de ADR. | Troca de objeto após validação, arquivo hostil, tipo divergente, conteúdo protegido, scanner indisponível e isolamento entre aplicações. |

## Compatibilidade, migração e rollback

- Introduzir armazenamento, metadados e validação antes de habilitar o aceite em `Notifications`.
- Preservar `null` ou ausência de anexos como o caminho atual, sem backfill e sem alteração do hash vigente.
- Comprovar compatibilidade aditiva de REST e Kafka; se qualquer consumidor ou schema não tolerar o campo, manter V1 e V2 coexistentes, como já previsto para evolução do evento (`docs/guia-integracao-produtor.md:182-186`).
- Separar o controle de rollout em dois gates: impedir novos aceites com anexos e manter o processamento dos já aceitos. Um rollback não pode remover anexos, desativar sua leitura nem convertê-los em links.
- Preservar objetos e claims até todas as notificações dependentes alcançarem estado terminal. Migrações devem ser aditivas; sua reversão lógica não deve apagar dados.
- Se o envio com anexos for suspenso, o fluxo deve manter solicitações sem anexos e produzir falha explícita para as aceitas com anexos, sem degradação silenciosa.

## Riscos principais

- Corrida entre liberação, claim, revogação e descarte.
- Falta de evidência de que os papéis REST atuais, definidos por classe, vinculam um principal à `application`; o novo limite exige autorização por aplicação (`docs/guia-integracao-produtor.md:46-53`; `docs/prd-attachment-management.md:176-177`).
- Alteração acidental do hash para produtores sem anexos.
- Exposição de nome, tipo, chave S3, URL assinada ou capacidade de upload em logs, traces, brokers ou dead-letter.
- Estouro do envelope real do provedor, incluindo corpo, metadados e codificação, com rejeição tardia.
- Pressão de memória e disponibilidade caso o adaptador materialize arquivos inteiros.
- Divergência entre bytes validados, bytes lidos do S3 e bytes codificados para o SendGrid.
- Claims ou uploads órfãos após falhas parciais.
- Seleção de canal ou fallback incompatível com anexos, pois o produtor não escolhe o canal no contrato atual.
- Custos e capacidade de S3, KMS, scanner, tráfego e retenção sem orçamento sustentado pela evidência.

## NFRs sustentados e lacunas mensuráveis

O PRD sustenta metas de aceitação de 100% para jornadas válidas e reconstrução de tentativas, e zero para violações do gate, degradação silenciosa, vazamento, quebra de isolamento e regressão sem anexos (`docs/prd-attachment-management.md:203-215`). Também exige métricas operacionais de volume, rejeição, backlog, uploads abandonados, falhas e descarte (`:197-201`).

A evidência não sustenta metas quantitativas de latência de upload ou validação, throughput, tamanho e quantidade de anexos, memória, disponibilidade, RPO, RTO, retenção legal ou custo. Esses valores devem ser definidos antes da escolha definitiva de streaming, scanner, topologia de workers e capacidade de armazenamento.

`DISCOVERY: PASS`

Justificativa: a evidência identifica com precisão o sistema atual, a lacuna, os proprietários, a direção das dependências, os contratos afetados, os controles exigidos, os riscos e as decisões necessárias. As lacunas restantes pertencem ao desenho de sistema e aos ADRs; este recibo não aprova estágio, gate, arquitetura final nem ciclo de vida.
