# Parecer do `dotnet-architect`

Parecer somente leitura para subsidiar a Development Specification, o Implementation Map e o Verification Plan. Não aprova gate, estágio, arquitetura final ou ciclo de vida.

## 1. Base factual

- A solução usa .NET 10, Minimal APIs, EF Core, PostgreSQL, SQS, Kafka, Redis, S3, KMS e JWT em um monólito modular com slices verticais (`.araia/stack-profile.yaml:4-35`; `.araia/runs/SPECIFY-20260831T031955Z/artifacts/docs/SPEC-001/analyses/.staging/01-solution-inspection.md:10-28`).
- `Notifications` possui ingestão, idempotência, ciclo de vida, tentativas, fallback e rastreamento. Contextos irmãos só podem ser consumidos por contratos publicados, sem acesso a dados ou tipos internos (`src/Platform.Api/Modules/Notifications/AGENTS.md:9-43`).
- `Dispatch` possui seleção e tradução para provedores. Ele não possui tentativa, fallback, auditoria ou dados de outro contexto (`src/Platform.Api/Modules/Dispatch/AGENTS.md:5-25`, `:39-43`).
- As fitness functions aceitam dependências entre módulos somente por `Integration.V1` e proíbem dependência de Infrastructure e do worker host para módulos (`tests/Platform.ArchTests/ArchitectureTests.cs:23-24`, `:66-96`, `:137-159`, `:201-245`).
- O ingresso atual, o hash idempotente, `EmailMessage` e a forma SendGrid não representam anexos (`src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs:7-36`; `RequestNotification.PayloadHash.cs:13-86`; `src/Platform.Api/Modules/Dispatch/Integration/V1/RenderedMessage.cs:23-27`; `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/SendGridMailRequest.cs:10-15`).
- Kafka limita cada registro a 256 KB e não aceita anexos ou dados de contato (`docs/guia-integracao-produtor.md:210-216`).

## 2. Decisões já aceitas

- `AttachmentManagement` possui custódia, validação, referência opaca e ciclo de vida dos anexos (`docs/SPEC-001/specification.md:133-136`).
- O ingresso usa upload gerenciado pelo hub, seguido de referência opaca; S3 fica sob custódia do hub; a notificação só é aceita depois da liberação (`docs/prd-attachment-management.md:97-107`).
- A primeira produção usa anexos somente em e-mail. Um fallback incompatível termina com falha explícita, sem remoção de arquivo ou conversão para link (`docs/prd-attachment-management.md:101-105`, `:146-154`).
- Bytes, localização S3 e capacidades de upload não podem aparecer em brokers, outbox, dead-letter, eventos, logs ou auditoria comum (`docs/prd-attachment-management.md:156-164`).
- Solicitações sem anexos devem preservar REST, Kafka, idempotência, recusas, eventos de resultado e seleção de canal (`docs/prd-attachment-management.md:165-170`).

## 3. Arquitetura-alvo recomendada

Manter o monólito modular. A evidência não sustenta extração de serviço, mudança de arquitetura ou novo runtime. O novo contexto deve seguir o dialeto observado, com slices verticais, ausência de mediator e persistência própria no eixo EF Core/PostgreSQL.

| Contexto | Responsabilidade recomendada | Limite obrigatório |
|---|---|---|
| `AttachmentManagement` | Identidade opaca, objeto S3, metadados, integridade, validação, liberação, expiração, revogação, descarte e claims que protegem dependências ativas | Não expor bucket, chave, URL reutilizável, contexto EF, entidades ou adaptador S3 |
| `Notifications` | Aceite da notificação, idempotência, snapshot do manifesto aceito, tentativas, fallback e evidência da tentativa | Não acessar S3, tabelas ou tipos internos de `AttachmentManagement` |
| `Dispatch` | Receber forma neutra de provedor e traduzir uma tentativa em uma chamada ao SendGrid | Não consultar `AttachmentManagement`, S3, claims ou estado de tentativa |
| Infrastructure da plataforma | Outbox, consumo e recursos compartilhados já publicados | Mensagens carregam referências e evidência mínima, nunca conteúdo |

Direção recomendada:

1. A aplicação produtora usa a superfície externa de `AttachmentManagement` para registro, upload e acompanhamento.
2. `Notifications` usa um contrato publicado e versionado de `AttachmentManagement` para reivindicar todo o conjunto de modo indivisível, obter o manifesto imutável e acessar o conteúdo sem conhecer o armazenamento.
3. `Notifications` continua como único orquestrador da tentativa e consumidor de `Dispatch.Integration`.
4. `Dispatch` recebe anexos em forma neutra de provedor, sem dependência reversa para `AttachmentManagement`.

O uso já existente de S3 no módulo `Audit` comprova disponibilidade do SDK e de configuração, mas não fornece contrato reutilizável. Reutilizar sua Infrastructure violaria a propriedade dos contextos (`01-solution-inspection.md:32-35`).

## 4. Invariantes de domínio

1. Uma referência pública identifica uma versão imutável do conteúdo e não revela localização ou capacidade de acesso.
2. Somente conteúdo liberado pode ser reivindicado. Estados pendente, rejeitado, inconclusivo, vencido ou revogado fecham o gate.
3. A liberação exige integridade comprovada, tipo efetivo compatível, validações de segurança concluídas e elegibilidade para o envelope efetivo de entrega (`docs/prd-attachment-management.md:126-135`).
4. O claim de uma notificação é indivisível: todas as referências pertencem à mesma `application`, estão liberadas e são autorizadas, ou nenhuma é associada.
5. Uma notificação aceita possui um snapshot estável do manifesto. Replay devolve o resultado original, sem reavaliar o estado atual nem criar nova entrega (`docs/prd-attachment-management.md:139-144`).
6. Referências e propriedades que alterem a entrega integram a identidade idempotente. Ausência de anexos preserva exatamente o hash atual (`RequestNotification.PayloadHash.cs:13-32`).
7. Cada tentativa verifica novamente a validade da liberação antes da chamada ao provedor. Revogação ou vencimento produz resultado explícito, sem chamada (`docs/prd-attachment-management.md:148-154`).
8. Os bytes submetidos devem corresponder à identidade íntegra validada. Nome de exibição, tipo e conjunto aceito não podem ser modificados pelo adaptador.
9. Descarte é proibido enquanto houver dependência ativa. Falhas parciais convergem para estado recuperável ou descarte conhecido (`docs/prd-attachment-management.md:156-163`, `:184-189`).
10. Nenhum caminho de fallback pode remover anexos, convertê-los em links ou escolher silenciosamente um canal incompatível.

## 5. Requisitos de engenharia candidatos

| ID sugerido | Requisito | Critério verificável |
|---|---|---|
| `ER-001` | Preservar o monólito modular e criar `AttachmentManagement` como contexto proprietário independente | Fitness functions impedem acesso cruzado a Domain, Infrastructure, persistência e armazenamento |
| `ER-002` | Publicar contratos versionados para o ciclo externo e para consumo por `Notifications` | Contract tests comprovam que somente DTOs imutáveis e resultados estáveis atravessam a fronteira |
| `ER-003` | Autorizar registro, consulta, claim e uso pela combinação entre principal e `application` | Matriz entre aplicações demonstra zero consulta, inferência ou uso cruzado |
| `ER-004` | Implementar máquina de estados que falhe fechada durante validação inconclusiva ou indisponível | Testes de transição demonstram que nenhum estado inválido alcança `Released` |
| `ER-005` | Tornar objeto e identidade íntegra imutáveis após liberação | Troca ou alteração do objeto invalida o fluxo antes do claim ou da tentativa |
| `ER-006` | Reivindicar o conjunto de forma indivisível e impedir aceite sem claim durável | Injeção de falhas entre claim, aceite, outbox e commit não produz notificação aceita sem claim válido |
| `ER-007` | Transportar somente referências opacas nos ingressos e na mensageria | Varredura da suíte encontra zero bytes, base64, localização S3 ou capacidade de upload nessas superfícies |
| `ER-008` | Incorporar ao hash as referências e propriedades de entrega conforme uma canonicalização publicada | Golden tests preservam o hash sem anexos; matriz de replay e conflito cobre ordem, duplicatas e propriedades decididas |
| `ER-009` | Persistir o manifesto aceito e selar o conteúdo necessário à tentativa | Retry e fallback usam o mesmo conjunto, nomes, tipos e identidades íntegras |
| `ER-010` | Evoluir o contrato de `Dispatch` sem transferir custódia ou estado de tentativa | Testes de arquitetura mantêm `Dispatch` sem dependência de S3, persistência ou Infrastructure de anexos |
| `ER-011` | Submeter o conjunto integral em uma única tentativa e falhar antes da chamada quando ele não couber | Fake do provedor demonstra igualdade do conjunto e ausência de chamadas parciais |
| `ER-012` | Preservar anexos e claims enquanto qualquer notificação ativa depender deles | Testes concorrentes de descarte, retry e reconciliação demonstram que conteúdo ativo permanece acessível |
| `ER-013` | Produzir evidência que relacione referência, digest, validação, tentativa, payload submetido e resposta do provedor | Toda tentativa aceita no provedor é reconstruída sem consultar conteúdo bruto em logs |
| `ER-014` | Preservar o contrato sem anexos e manter versões coexistentes quando a mudança não for aditiva | Suíte vigente de REST, Kafka, idempotência, dead-letter e eventos apresenta zero regressões |
| `ER-015` | Separar bloqueio de novos aceites do processamento de notificações já aceitas | Ensaio de rollback interrompe novos aceites e conclui ou falha explicitamente os itens existentes sem degradação |

Os testes de implementação devem ter nomes baseados em comportamento. A associação aos `ER-*` pertence à matriz de rastreabilidade do Verification Plan.

## 6. Sementes do Implementation Map

| Onda | Resultado | Sementes | Dependências |
|---|---|---|---|
| 0 | Decisões executáveis | Descoberta de domínio, protocolo de claim, estratégia de versão, modelo de proteção, contrato preliminar e baseline de capacidade | Intenção aceita e evidência brownfield |
| 1 | Registro e custódia | Fronteira do módulo, identidade opaca, metadados próprios, ingresso de upload, S3 encapsulado e consulta de estado | Decisões de upload, IAM, KMS e imutabilidade |
| 2 | Validação e liberação | Máquina de estados, integridade, classificação de tipo, scanner, liberação fechada, revogação, expiração e reconciliação | Onda 1; política de segurança decidida |
| 3 | Associação e aceite | Claim indivisível, vínculo por `application`, snapshot do manifesto, ingresso REST/Kafka, hash e compatibilidade sem anexos | Onda 2; ADR de consistência; RFC de contrato |
| 4 | Submissão por e-mail | Contrato neutro de anexos, conteúdo selado da tentativa, adaptação SendGrid, validação pré-envio e evidência do payload | Onda 3; decisão de transferência e envelope |
| 5 | Operação e rollout | Preservação, descarte, investigação, métricas, probes, controles de habilitação e ensaios de rollback | Ondas 2 e 4 |

A ordem preserva as dependências de capacidades já aceitas no PRD (`docs/prd-attachment-management.md:244-254`). Contratos, segurança e verificações podem evoluir junto de cada onda, mas o aceite em `Notifications` não deve ser habilitado antes da custódia, validação e recuperação estarem operacionais.

## 7. Plano de verificação arquitetural e de rollout

| Dimensão | Verificação obrigatória |
|---|---|
| Arquitetura | Dependência entre contextos somente pela versão publicada; `Dispatch` sem acesso a anexos internos; contextos e host sem dependências reversas; propriedade exclusiva de schema, contexto EF e armazenamento |
| Contratos | Compatibilidade de OpenAPI, REST, Kafka, dead-letter, eventos e consumidores antigos; execução com versões antiga e nova coexistentes; atualização da fitness function hoje fixada em `Integration.V1` se houver V2 |
| Domínio | Transições de estado, claim de conjunto, replay, conflito, revogação, expiração, descarte, claims órfãos e concorrência entre claim, revogação e limpeza |
| Integração | PostgreSQL real, S3/KMS em ambiente controlado, scanner substituível e fake SendGrid que capture a requisição e confira o conteúdo reconstruído |
| Segurança | Autorização por recurso e `application`, referência não enumerável, troca de objeto, tipo divergente, arquivo hostil, scanner indisponível, metadado hostil e varredura de vazamento |
| Confiabilidade | Falhas injetadas após cada efeito durável; reprocessamento não duplica liberação, associação ou tentativa; indisponibilidade nunca avança estado incorretamente |
| Capacidade | Ensaio com o envelope máximo aprovado, quantidade aprovada de anexos, cancelamento e pressão de memória. A estratégia de buffer ou streaming só pode ser aceita depois desse baseline |
| Rollout | Migrações aditivas antes do código que grava; leitura tolerante antes da escrita; upload e validação antes do aceite; habilitação progressiva; execução com processos de versões diferentes |
| Rollback | Desabilitar novos aceites sem desabilitar leitura e processamento de itens existentes; preservar objetos e claims; nunca reenviar sem anexos; reversão lógica sem apagar dados |

Metas sustentadas para cada candidato a release:

- 100% das jornadas válidas com o conjunto correto.
- Zero violações do gate, degradações silenciosas, vazamentos, violações de isolamento e regressões sem anexos.
- 100% das tentativas aceitas pelo provedor reconstruíveis (`docs/prd-attachment-management.md:203-215`).

Essas metas são critérios de suíte, não SLOs de produção. A evidência não fornece latência de upload ou validação, throughput, quantidade e tamanho de anexos, orçamento de memória, disponibilidade, RPO, RTO, retenção ou custo (`02-architecture-discovery.md:74-78`). Esses valores são condição de validação antes da escolha de streaming, scanner, topologia de workers e habilitação produtiva.

## 8. Migração, rollout e rollback

1. Introduzir estado, armazenamento e validação com migrações aditivas, sem backfill para notificações antigas.
2. Manter ausência de anexos no caminho vigente, inclusive a forma atual do hash.
3. Publicar primeiro leitores tolerantes e versões coexistentes; só depois permitir que produtores enviem referências.
4. Habilitar upload e validação antes do aceite de notificações com anexos.
5. Separar o controle que impede novos aceites daquele que mantém processamento, retry, fallback e investigação dos itens já aceitos.
6. Em rollback, manter objetos e claims até todos os dependentes alcançarem estado terminal. Não reverter migrations por exclusão nem remover capacidade de leitura.
7. Se a submissão for suspensa, manter solicitações sem anexos e falhar explicitamente as tentativas com anexos.

Essa estratégia preserva a recomendação já registrada em `02-architecture-discovery.md:52-59`.

## 9. Aplicabilidade dos artefatos

| Artefato | Aplicabilidade | Trigger e justificativa |
|---|---|---|
| ADR | `separate` | Consistência do claim, proteção S3, modelo de upload, scanner, imutabilidade e evolução de contrato têm consequências duráveis, operacionais ou difíceis de reverter |
| RFC | `separate` | REST e Kafka são contratos de produtores externos; versão e coexistência exigem consulta e validação entre consumidores |
| Ata | `inline` | Este painel não realizou decisão humana. Preservar fatos, decisões aceitas e dissensos no registro consolidado basta; eventual decisão humana deve alimentar ADR ou RFC |
| Desenho técnico | `separate` | Máquina de estados, sequências, reconciliação, descarte, obtenção de conteúdo e evidência atravessam várias ondas e modos de falha |
| Contratos | `separate` | OpenAPI, schema Kafka, catálogo de recusas e contratos publicados entre módulos precisam ser versionados e verificáveis por máquina |
| Descoberta de domínio | `separate` | O bounded context está aceito, mas agregados, eventos, políticas, semântica do manifesto e classificação do subdomínio ainda não estão definidos |
| Capacidade e desempenho | `deferred` | Faltam envelope, volumes e orçamento de memória autorizados. A postergação bloqueia a decisão buffer versus streaming e o rollout, não a autoria da especificação |
| Privacidade e segurança | `separate` | O fluxo processa conteúdo potencialmente sensível e exige ameaça, autorização por recurso, minimização, proteção, retenção e descarte definidos por autoridade competente |

## 10. Dissensos e decisões não fechadas

1. **Consistência do claim**: transação compartilhada por contrato publicado reduz a janela de inconsistência, mas aumenta o acoplamento; reserva idempotente com compensação preserva autonomia, mas exige reconciliação de claims órfãos.
2. **Versão contratual**: membro opcional em V1 reduz superfícies simultâneas, mas precisa de prova de compatibilidade; V2 coexistente reduz risco de quebra e aumenta operação. A fitness function atual reconhece apenas `Integration.V1`.
3. **Identidade do manifesto**: ordem, duplicatas, nome de exibição, tipo e demais propriedades que compõem igualdade idempotente ainda precisam ser decididos.
4. **Transferência do conteúdo**: buffer simplifica o adaptador; streaming reduz pressão de memória e aumenta complexidade de ciclo de vida, cancelamento, hash e retry. A evidência quantitativa ainda não permite escolher.
5. **Proteção e validação**: modelo de upload, organização S3, IAM, KMS, versionamento, imutabilidade, scanner e validade da liberação não estão decididos pelo PRD (`docs/prd-attachment-management.md:53-62`).
6. **Autorização por aplicação**: os papéis REST atuais autorizam por classe, não demonstram vínculo do principal com `application` (`docs/guia-integracao-produtor.md:44-53`). A nova capacidade exige autorização por recurso e aplicação.
7. **Canal e fallback**: o produtor não escolhe o canal; a política atual escolhe canal e ordem (`docs/guia-integracao-produtor.md:333-344`). Deve-se decidir como um manifesto de anexos restringe o plano a e-mail sem degradação silenciosa.
8. **Stack Profile**: `messaging-consumer-pattern: none` diverge do uso direto de Kafka e SQS; `telemetry: none` não decide como atender às métricas operacionais. Preservar a divergência, sem corrigir automaticamente o perfil (`.araia/stack-profile.yaml:23-30`; `03-stack-profile-preparation.md:17-19`).

`DOTNET_ARCHITECT_SPECIFICATION: PASS`

Justificativa objetiva: a evidência sustenta a arquitetura-alvo, os limites, as invariantes, os requisitos candidatos, as ondas e as verificações. As escolhas ainda abertas estão explicitamente isoladas em ADRs, RFCs, desenhos e condições de validação, sem decisão inventada.
