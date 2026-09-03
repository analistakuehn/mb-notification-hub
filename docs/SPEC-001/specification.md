---
spec-id: "SPEC-001"
title: "Gestão de anexos do Notification Hub"
version: "1.0.0"
lifecycle: "implementing"
created: "2026-08-30"
updated: "2026-08-31"
author: "user"
language: "pt-BR"
adapters:
  - name: "dotnet"
    role: "primary"
initiative-kind: "product"
initiative-kind-source: "default"
mode: "brownfield"
mode-source: "detected"
mode-detection: "dotnet: 1 .sln e 9 .csproj"
pipeline:
  current-stage: "IMPLEMENT"
  collab-mode: "auto"
  completed-stages: ["SPECIFY", "REFINE", "PLAN"]
traceability:
  requirements-dir: "docs/SPEC-001/requirements/"
  backlog-dir: "docs/SPEC-001/backlogs/"
  commits: []
gates: {}
---

# SPEC-001: Gestão de anexos do Notification Hub

## Initiative Brief

### Problema ou oportunidade

O Notification Hub não aceita anexos. Quando uma jornada exige comprovantes ou documentos, a aplicação produtora precisa omitir o arquivo ou realizar a entrega fora da capacidade central. Sem a gestão no hub, o produto não consegue aplicar de modo uniforme isolamento, validação, integridade e evidência ao arquivo associado à notificação, nem relacionar os bytes validados aos bytes submetidos ao provedor.

### Resultados desejados

- Permitir que aplicações autorizadas associem anexos fornecidos por elas a notificações por e-mail sem transportar os bytes na solicitação da notificação.
- Usar somente anexos liberados, íntegros e pertencentes à aplicação solicitante.
- Preservar o conjunto completo durante tentativas, falhas e investigação, sem degradação silenciosa.
- Produzir evidência que relacione o conteúdo validado ao conteúdo submetido ao provedor de e-mail.

### Atores e jornada principal

- **Aplicação produtora**: registra o arquivo, realiza o upload, acompanha a validação e solicita a notificação com uma referência opaca liberada.
- **Destinatário**: recebe a tentativa de entrega por e-mail com o conjunto de anexos solicitado.
- **Produto Notification Hub**: define o comportamento, o canal inicial e os critérios de aceitação.
- **Área operacional autorizada**: investiga estados, falhas e evidências sem depender do conteúdo bruto em logs ou eventos.

A aplicação realiza o upload gerenciado pelo hub e aguarda a liberação. O hub aceita a notificação somente quando todos os anexos estão liberados. Uma tentativa bem-sucedida submete ao provedor exatamente o conjunto aceito; quando isso não for possível, o fluxo termina com resultado explícito e auditável.

### Objetivos

- Disponibilizar upload gerenciado e referência pública opaca.
- Impedir o uso de anexos pendentes, rejeitados, não verificáveis ou pertencentes a outra aplicação.
- Integrar o conjunto de anexos à idempotência, às tentativas, ao fallback e à evidência da notificação.
- Preservar o comportamento vigente de produtores que não usam anexos.

### Não objetivos

- Gerar, converter, editar, assinar ou extrair conteúdo de documentos.
- Aceitar localização arbitrária no S3 ou manter a custódia na infraestrutura do cliente.
- Receber bytes ou base64 no contrato de solicitação da notificação.
- Entregar anexos por SMS, push ou WhatsApp na primeira produção.
- Substituir anexos automaticamente por links ou por mensagens sem arquivos.
- Gerenciar anexos estáticos de templates ou criar um portal público de compartilhamento.
- Definir bucket, chave, URL de upload, IAM, KMS, versionamento, tabelas, eventos internos, scanner ou estratégia de streaming.

### Escopo

#### Incluído

- Registro, upload, acompanhamento, validação e liberação de anexos sob a identidade da aplicação produtora.
- Referência opaca utilizável somente após a liberação.
- Associação idempotente do conjunto liberado à solicitação de notificação.
- Submissão integral por e-mail e falha explícita quando o conjunto não puder ser preservado.
- Preservação enquanto uma notificação ativa depender do anexo.
- Ciclo de vida, recuperação de falhas parciais, descarte seguro e evidência operacional.
- Compatibilidade com o contrato atual para solicitações sem anexos por REST e Kafka.

#### Fora do escopo

- Conteúdo inline no ingresso da notificação.
- Importação de objetos mantidos no S3 da aplicação cliente.
- Outros canais ou conversão para links na primeira produção.
- Escolhas técnicas que pertencem a ADRs, contratos e desenhos técnicos.

### Sucesso e aceitação de produto

Em cada candidato a release, a suíte de aceitação deve demonstrar:

- 100% das jornadas válidas submetendo ao provedor o conjunto correto.
- Nenhuma referência não liberada, vencida, revogada, hostil ou não autorizada alcançando o provedor.
- Nenhuma remoção ou alteração silenciosa do conjunto aceito.
- Nenhum byte, localização S3 ou capacidade de upload expostos em brokers, dead-letter, logs ou auditoria comum.
- Evidência completa para todas as tentativas aceitas pelo provedor.
- Nenhuma violação de isolamento entre aplicações.
- Nenhuma regressão no baseline vigente de solicitações sem anexos.

Os critérios detalhados `PAC-001` a `PAC-014` permanecem canônicos no [PRD de gestão de anexos](../prd-attachment-management.md#critérios-de-aceitação-de-produto).

### Restrições e decisões existentes

- O conteúdo é fornecido pela aplicação produtora; o hub não o gera.
- Amazon S3 é a restrição declarada para custódia sob controle do hub.
- A aplicação realiza um fluxo em várias etapas e aguarda a liberação antes de solicitar a notificação.
- O identificador público é opaco e não revela localização nem credenciais.
- O conteúdo bruto não trafega por Kafka, SQS, outbox, dead-letter, eventos ou logs.
- A primeira produção aceita anexos somente por e-mail.
- Um fallback incompatível encerra com falha explícita e nunca remove anexos silenciosamente.
- O produto prova os bytes submetidos ao provedor, sem afirmar recebimento material pelo destinatário além do que o provedor observa.

### Estado atual

O repositório é brownfield, com uma solução e nove projetos .NET. O [contrato de integração](../guia-integracao-produtor.md#L210) proíbe anexos no barramento, o [comando de ingresso](../../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs#L7) não possui referências de arquivos, o [conteúdo renderizado de e-mail](../../src/Platform.Api/Modules/Dispatch/Integration/V1/RenderedMessage.cs#L23) contém apenas texto e a [forma atual da chamada ao SendGrid](../../src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/SendGridMailRequest.cs#L10) não representa anexos.

O baseline observado cobre REST, Kafka, idempotência, eventos de resultado e seleção de canal. A evolução deve preservar esses comportamentos para solicitações sem anexos e manter coexistência quando uma mudança contratual não for compatível.

### Participantes e aprovação

- **Responsável pelo produto**: Produto Notification Hub.
- **Consumidores diretos**: aplicações produtoras e destinatários de notificações por e-mail.
- **Condição de aprovação**: escopo confirmado pelo responsável do Produto Notification Hub.
- **Condição de aceitação**: critérios e métricas do PRD atendidos no candidato a release.

### Tipo da iniciativa

- **Tipo**: `product`.
- **Origem**: `default`.
- **Justificativa**: a iniciativa entrega uma capacidade delimitada do Notification Hub e não apresenta mandato de governança para um padrão compartilhado entre várias equipes.

### Proveniência das decisões

- **Decidido pelo usuário**: uso de S3 sob custódia do hub, conteúdo fornecido pelo cliente, upload gerenciado, aceite somente após a liberação, primeira produção por e-mail e falha explícita para fallback incompatível.
- **Recomendado pela mesa redonda e aprovado pelo usuário**: `AttachmentManagement` como proprietário da custódia, validação, ciclo de vida e referência opaca. **Trade-off**: a aplicação produtora executa um fluxo em várias etapas. **Divergência preservada**: organização física do armazenamento, IAM, KMS, scanner e estratégia de leitura pertencem aos ADRs e desenhos técnicos. **Confiança**: média. **Condição de revisão**: reavaliar ingresso inline somente se evidências demonstrarem que os produtores não conseguem operar o fluxo aprovado.

### Fontes

- [PRD de gestão de anexos](../prd-attachment-management.md), fonte de intenção, escopo, aceitação e decisões de produto, SHA-256 `26b68f9845e25f89edb2087ee399a16379d9752cd908e8e5358aaf06a5deb1ba` na inicialização.
- [Guia de integração do produtor](../guia-integracao-produtor.md), fonte do contrato e do comportamento brownfield observado.
- [Design de sistema do Notification Hub](../notification-hub-system-design.md), fonte proposta para claim check, contratos e limites arquiteturais.
- [Fronteira do módulo Notifications](../../src/Platform.Api/Modules/Notifications/AGENTS.md#L9) e [fronteira do módulo Dispatch](../../src/Platform.Api/Modules/Dispatch/AGENTS.md#L5).
- [Perfil observado da solução](../../.araia/stack-profile.yaml#L4).
