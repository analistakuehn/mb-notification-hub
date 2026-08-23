---
language: pt-BR
---

# Requisitos de engenharia: Fase 0 (Governança)

| Campo | Valor |
|---|---|
| **Tipo** | Requisitos de engenharia |
| **Status** | PROPOSED |
| **Data** | 2026-08-23 |
| **Dono** | Arquitetura (papel decisor nas ADRs 0005 e 0006, que governam esta fase) |
| **Público** | Engenharia, Produto, Compliance, Jurídico, Segurança da Informação |
| **Documento-mãe** | [Design de Sistema, §15 Roadmap](../notification-hub-system-design.md) |
| **Fontes** | Design de Sistema (§2.3, §3, §9, §10.4, §14, §15, §16); [ADR-0005](../ADR-0005-templates-como-dados-geridos-pelo-hub.md); [ADR-0006](../ADR-0006-auditoria-append-only-hash-chain-worm.md); [ADR-0012](../ADR-0012-contact-consent-fonte-da-verdade.md); [registro de ADRs](../README.md); código em `src/Platform.Api` |

## Objetivo

Traduzir a linha da fase 0 do roadmap (§15 do Design de Sistema) em requisitos verificáveis de engenharia. A fase 0 constrói a fundação de governança do Notification Hub: identidade e papéis no Entra provisionados por Terraform, inventário e classificação dos textos hoje embutidos nos serviços, definição de retenção com Jurídico e a infraestrutura de prova imutável (bucket WORM e chaves KMS). O critério de saída do roadmap é: inventário completo e cada template existente com owner, classe e base legal definidos.

### Contexto observado

- Os textos de notificação vivem hoje embutidos no código dos serviços produtores, o estado que a [ADR-0005](../ADR-0005-templates-como-dados-geridos-pelo-hub.md) descreve como modelo (a) e decide eliminar: nenhum template pode existir em código.
- A superfície de gestão da fase 1a já está implementada no repositório: rotas `/v1/templates`, `/v1/layouts` e `/v1/applications/{application}/classes/{class}/policy` (`src/Platform.Api/Modules/TemplateManagement/TemplateManagementModule.cs:64`, `:81`, `:95`), com criação, edição de rascunho, validação, render de teste, publish, deprecate, disable, rollback e diff.
- O quatro olhos é avaliado no recurso, pelo domínio: o código de erro `four-eyes-violation` (`src/Platform.Api/Modules/TemplateManagement/Domain/ErrorCodes.cs:20`) é aplicado em `TemplateVersion.cs:255`, `LayoutVersion.cs:273` e `ClassPolicyVersion.cs:188`.
- A autorização por rota usa os papéis `Templates.Author` e `Templates.Publish` (`src/Platform.Api/Modules/TemplateManagement/Infrastructure/Authorization/AuthorizationSetup.cs:13`, `:15`).
- A autenticação atual é JWT bearer com chave simétrica de desenvolvimento (issuer `notification-hub-dev-only`); o host se recusa a subir com essa chave fora de Development (`src/Platform.Api/Program.cs:45-56`) e a configuração prevê que o provedor de identidade de produção substitua a seção Bearer (`src/Platform.Api/Program.cs:12-15`). A integração com o Entra está pendente.
- Não existe endpoint de import em lote na superfície de gestão e não existe código Terraform no repositório (buscas por endpoints de lote em `Features/` e por arquivos `.tf` sem resultados).

### Escopo

Entregas da fase 0 conforme §15: identidade no Entra via Terraform, inventário com import como `draft`, classificação com Produto e Compliance, definição de retenção com Jurídico, bucket WORM e chaves KMS. A fase corre em paralelo com a fase 1 e dura de 3 a 4 semanas (§15).

### Não objetivos

- Não define os valores de retenção: a fase produz essa definição com Jurídico e a registra em ADR própria (prevista no [registro de ADRs](../README.md)).
- Não implementa ingestão, pipeline, hash chain nem export WORM: essas entregas pertencem à fase 1b (§15).
- Não publica template nenhum: o import termina em `draft`; publicar segue o fluxo de quatro olhos da fase 1a.
- Não decide aprovação dupla por classe: é ponto de extensão da ADR-0005, ativável quando Compliance exigir.
- Não atribui pessoas nem datas de calendário: o roadmap fornece somente a duração e o paralelismo.

## Arquitetura alvo

A fase 0 entrega três superfícies de fundação, todas fora do caminho de execução do hub:

1. **Identidade**: app registrations, app roles e grupos no Entra, provisionados pelo módulo Terraform `entra` com o provider `azuread` (§14, §15), refletindo a tabela de papéis e segregação de funções do §9.1 (produtor por app registration; autor por grupo de time com `Templates.Author`; publicador com `Templates.Publish`; Platform Admin em grupo restrito com PIM; Auditor com `Notifications.Audit`).
2. **Catálogo semeado**: os textos inventariados entram no catálogo do hub como versões `draft`, pela superfície de gestão já implementada. Sem endpoint de lote, o "import em lote" é operacional: scripts que chamam os endpoints unitários existentes (`POST /v1/templates`, `POST /v1/templates/{key}/versions`, `PUT /v1/templates/{key}/versions/{version}/content/{channel}/{locale}`), no espírito da coleção de chamadas de apoio à autoria prevista na fase 1a (§15).
3. **Prova imutável**: bucket S3 com Object Lock em modo Compliance e chaves KMS (uma CMK por `application` para dados e uma CMK para assinatura de manifests WORM), provisionados no módulo Terraform `data` (§10.4, §14, ADR-0006).

## Responsabilidades e entregas

| Entrega | Requisito verificável | Fonte |
|---|---|---|
| E1: identidade no Entra via Terraform | Módulo `entra` versionado no repositório e aplicado via PR; app roles publicadas com grafia idêntica à que a API exige (`Templates.Author`, `Templates.Publish`) e `Notifications.Audit`; grupos e PIM conforme §9.1 | §15, §14, §9.1, §9.7; `AuthorizationSetup.cs:13-15` |
| E2: inventário e import como `draft` | Cada texto inventariado tem template e versão `draft` correspondentes no catálogo, com origem registrada (serviço e caminho no repositório produtor) e volume mensal medido por serviço | §15; §16 risco 25; ADR-0005 |
| E3: classificação com Produto e Compliance | Nenhum item do inventário sem classe (§3), base legal (§2.3) e owner; fronteira `operational` vs `transactional` decidida item a item | §15; §16 risco 11; `Template.cs:83-99` |
| E4: retenção com Jurídico | ADR de retenção por classe registrada, com valor explícito por classe e decisão de Jurídico e Compliance | §15, §9.6; §16 risco 1; [registro de ADRs](../README.md) |
| E5: bucket WORM e chaves KMS | Bucket com Object Lock em modo Compliance ativo e retenção igual ao valor da ADR de E4; CMKs por `application` e de assinatura com política que nega `Decrypt` fora do workload correspondente; ambiente de não produção com retenção curta | §15, §10.4; ADR-0006 |

### E1: identidade no Entra via Terraform

App registrations para os serviços produtores, app roles e grupos, espelhando §9.1. Os nomes das app roles precisam bater byte a byte com o que o código valida: a API lê papéis da claim `role` do token (`Program.cs:25`) e exige `Templates.Author` para autoria e leitura de catálogo e `Templates.Publish` para transições de ciclo de vida (`AuthorizationSetup.cs`). O onboarding de produtor segue §9.7: app registration, app roles mínimas e registro em `PRODUCER_REGISTRY`, tudo via Terraform e PR.

### E2: inventário e import como `draft`

Inventariar os textos embutidos nos serviços produtores, extrair conteúdo e metadados e importar cada item como rascunho pela API de gestão. O inventário registra, por item: serviço de origem, caminho no repositório, canais usados e volume mensal medido, porque as estimativas de capacidade do design não têm base medida e o inventário é a fonte prevista para medi-la (§16 risco 25). O import termina em `draft`; nenhuma publicação ocorre nesta fase.

### E3: classificação com Produto e Compliance

Cada item do inventário recebe classe (`critical`, `transactional` ou `operational`, conforme a taxonomia do §3), base legal LGPD (§2.3) e owner. A classificação precede o import de cada item: a API rejeita a criação de template sem `ownerTeam`, `legalBasis` e classe (`src/Platform.Api/Modules/TemplateManagement/Domain/Template.cs:83-99`), portanto E2 depende de E3 item a item. A fronteira entre `operational` e `transactional` é decidida no cadastro por Produto e Compliance; o hub aplica, não decide (§16 risco 11).

### E4: retenção com Jurídico

Definir a retenção por classe (§9.6) e registrá-la como ADR própria, já prevista no [registro de ADRs](../README.md). A ADR confirma ou substitui a premissa do design (mínimo de 5 anos para `critical` e `transactional`, §9.6). O design suporta qualquer valor por classe (§16 risco 1), então a decisão não bloqueia o desenho da fase 1b, mas bloqueia a configuração definitiva de retenção do bucket em E5.

### E5: bucket WORM e chaves KMS

Bucket S3 com Object Lock em modo Compliance e retenção igual ao prazo legal definido em E4 (ADR-0006): nem a conta root apaga antes do prazo (§9.4). Chaves KMS conforme §10.4: uma CMK por `application` para dados (envelope encryption) e uma CMK para assinatura de manifests WORM, com políticas de chave que negam `Decrypt` a qualquer principal que não seja o workload correspondente e uso logado no CloudTrail. Como o modo Compliance é irreversível, o ciclo de export é validado antes em bucket de não produção com retenção curta (ADR-0006).

## Dependências e desbloqueios

**Dependências de entrada**: nenhuma fase anterior. A fase 0 é a primeira do roadmap e corre em paralelo com a fase 1 (§15).

**Dependências internas**: E3 precede E2 item a item (a API exige classe, owner e base legal na criação); E4 precede a configuração definitiva de retenção em E5.

**O que a fase destrava**:

- **Troca do JWT de desenvolvimento por Entra**: o host hoje valida tokens assinados com chave simétrica de desenvolvimento e recusa boot fora de Development (`Program.cs:45-56`). E1 entrega as app registrations, app roles e grupos que a configuração de produção consome ao substituir a seção Bearer (`Program.cs:12-15`; §14 prevê `Microsoft.Identity.Web`).
- **Gates de WORM da fase 1b**: a 1b entrega `audit_event` com hash chain e export WORM (§15); o job de export e a assinatura de manifests exigem o bucket e as CMKs de E5 (ADR-0006).
- **Migração strangler**: cada template migrado na 1b só sai do código do produtor depois de `published` no hub (§15); publicar exige o rascunho de E2 e os metadados de E3, e o critério de saída da fase 0 (owner, classe e base legal por template) é pré-condição dessa migração.
- **Base legal na auditoria e no consentimento**: a base legal registrada em E3 é o que permite responder "sob qual base legal?" na reconstrução (§9.5) e alimenta a decisão de envio junto ao consentimento que a 1b implementa (ADR-0012).

## Requisitos não funcionais

| Atributo | Requisito | Fonte |
|---|---|---|
| Prazo | 3 a 4 semanas, em paralelo com a fase 1 | §15 |
| Completude | 100 % dos textos inventariados com owner, classe e base legal antes do fim da fase | §15 (critério de saída) |
| Segregação de funções | Nenhuma publicação fora do quatro olhos; o bloqueio já existe por construção no recurso | §9.1; `TemplateVersion.cs:255` |
| Imutabilidade | Object Lock em modo Compliance: remoção impossível antes do prazo, para qualquer principal | §9.4; ADR-0006 |
| Reversibilidade controlada | Ciclo WORM validado em bucket de retenção curta antes do modo Compliance definitivo | ADR-0006 |
| Rastreabilidade de mudança | Identidade e infraestrutura mudam somente via Terraform e PR revisado | §9.7 |
| Auditabilidade do import | Toda criação e edição de rascunho gera trilha na superfície da 1a | §15 (fase 1a); migração `20260822225539_TemplateLifecycleAudit` |

## Interfaces e superfícies de integração

- **Superfície de gestão (consumida, não criada)**: `/v1/templates`, `/v1/layouts` e a rota de políticas por classe (`TemplateManagementModule.cs:64-95`). O import usa os endpoints unitários de criação e edição de rascunho; a validação usada é a mesma do runtime (ADR-0005).
- **Entra**: os papéis chegam como app roles na claim `role` do JWT (`Program.cs:25`); E1 publica os nomes exatos que a autorização exige.
- **Terraform**: módulos `entra` (app registrations, app roles, grupos) e `data` (S3 WORM, KMS) conforme a stack de referência (§14); ambos inexistentes no repositório hoje.
- **Artefatos de saída**: inventário classificado (origem, volume, classe, base legal, owner por item) e ADR de retenção por classe.

## Segurança e privacidade

- **Sem dados pessoais no inventário**: o objeto da fase são textos de template e metadados, nunca dados de destinatários. Variáveis sensíveis são declaradas por template (`Template.cs:68`) e não recebem valores reais nesta fase; o render de teste usa variáveis de exemplo (ADR-0005).
- **Base legal por classe**: exigência LGPD registrada no catálogo (§2.3); é o campo que E3 preenche e que a auditoria consome (§9.5).
- **Rastreabilidade e terceiros**: a Resolução CMN 4.893/2021 exige rastreabilidade e controle de acesso (§2.3); E1 e E5 são os fundamentos dessa exigência no hub.
- **Chaves e segredos**: políticas de CMK negam `Decrypt` fora do workload; uso de chave no CloudTrail; sem segredo em código, imagem ou Terraform state em claro (§10.4).
- **Autenticação transitória**: enquanto a troca por Entra não ocorre, a chave de desenvolvimento só assina tokens em Development, por guarda de boot (`Program.cs:45-56`).

## Qualidade e verificação

Critérios de saída do roadmap (§15): inventário completo; cada template existente com owner, classe e base legal definidos.

Verificações da fase:

1. **Linha de base do inventário**: busca automatizada por padrões de texto de notificação nos repositórios produtores; é o mesmo mecanismo que depois verifica o critério "zero templates em código" da ADR-0005.
2. **Completude da classificação**: consulta ao catálogo (`GET /v1/templates`) confere owner, classe e base legal por item; a API já impede criação sem esses campos (`Template.cs:83-99`), então a verificação residual é a conciliação inventário versus catálogo.
3. **Identidade**: papéis publicados no Entra conferidos contra os nomes exigidos pelo código (`AuthorizationSetup.cs:13-15`); mudanças só via PR (§9.7).
4. **WORM**: ciclo de escrita e tentativa de remoção validado em bucket de retenção curta antes da configuração definitiva (ADR-0006).
5. **Quatro olhos preservado**: nenhuma publicação nesta fase; qualquer tentativa de publish pelo autor é bloqueada por construção (`TemplateVersion.cs:255`).

## Riscos e premissas

| Item | Tratamento | Fonte |
|---|---|---|
| Retenção depende de Jurídico e bloqueia a retenção definitiva do bucket | ADR de retenção prevista; o design suporta qualquer valor por classe | §16 risco 1; [registro de ADRs](../README.md) |
| Object Lock em modo Compliance é irreversível: erro de retenção é permanente | Validação do ciclo antes do lock; bucket de não produção com retenção curta | ADR-0006 |
| Fronteira `operational` vs `transactional` é cinzenta | Decisão item a item por Produto e Compliance no cadastro; o hub aplica | §16 risco 11 |
| Volumes sem base medida até o fim do inventário | O inventário mede o volume real por serviço; o teste de carga da 1b usa o dobro do medido | §16 risco 25 |
| Premissa: retenção mínima de 5 anos para `critical` e `transactional` | Confirmada ou substituída pela ADR de retenção de E4 | §9.6 |
| Autoria só por API limita autores não técnicos durante o import | Scripts e coleção de chamadas de apoio (fase 1a); consequência aceita do corte do Studio | §16 risco 13; ADR-0005 |

## Rastreabilidade

O documento upstream é o Design de Sistema (não há PRD separado); as ADRs 0005, 0006 e 0012 registram as decisões que esta fase materializa, todas com status `Proposta` no registro.

| Item | Origem (upstream) | Consumidores (downstream) |
|---|---|---|
| E1 identidade | §15; §9.1; §14 | Troca da autenticação do host (`Program.cs:12-15`); onboarding de produtores (§9.7) |
| E2 inventário e import | §15; ADR-0005 | Migração strangler da 1b (§15); modelo de capacidade (§16 risco 25) |
| E3 classificação | §15; §2.3; §3 | Publicação com quatro olhos (1a); reconstrução de auditoria (§9.5); consentimento (ADR-0012) |
| E4 retenção | §15; §9.6; §16 risco 1 | E5; ciclo de partições e export (§9.6); ADR de retenção prevista |
| E5 WORM e KMS | §15; §10.4; ADR-0006 | Export WORM e assinatura de manifests da 1b (§15) |
| Critérios de saída | §15 | Gate de início da migração de templates na 1b |

## Referências

- [Design de Sistema](../notification-hub-system-design.md): §2.3, §3, §9.1, §9.4 a §9.7, §10.4, §14, §15, §16.
- [ADR-0005: templates como dados geridos pelo hub](../ADR-0005-templates-como-dados-geridos-pelo-hub.md).
- [ADR-0006: auditoria append-only com hash chain e export WORM](../ADR-0006-auditoria-append-only-hash-chain-worm.md).
- [ADR-0012: Contact & Consent como fonte da verdade](../ADR-0012-contact-consent-fonte-da-verdade.md).
- [Registro de ADRs e próximas ADRs previstas](../README.md).
- Código: `src/Platform.Api/Program.cs`, `src/Platform.Api/Modules/TemplateManagement/` (autorização, domínio, endpoints e migrações).
