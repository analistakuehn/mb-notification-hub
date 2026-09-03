# Inspeção mecânica da solução para gestão de anexos

**Resultado**: PASS  
**SPEC**: `SPEC-001`  
**Escopo**: solução .NET, módulos afetados, contratos atuais e capacidades técnicas reutilizáveis  
**Método**: inspeção somente leitura, sem build, restore ou alteração do Stack Profile

## Inventário

- A solução `MonteBravo.NotificationHub.sln` contém os hosts `Platform.Api` e `Platform.Worker`, cinco projetos de validação, um projeto de probes e `Platform.GoLiveChecks` (`MonteBravo.NotificationHub.sln:6`, `MonteBravo.NotificationHub.sln:8`, `MonteBravo.NotificationHub.sln:10`, `MonteBravo.NotificationHub.sln:12`, `MonteBravo.NotificationHub.sln:14`, `MonteBravo.NotificationHub.sln:16`, `MonteBravo.NotificationHub.sln:20`, `MonteBravo.NotificationHub.sln:22`, `MonteBravo.NotificationHub.sln:26`).
- Todos os projetos observados usam `net10.0`; `Directory.Build.props` habilita `LangVersion=latest`, nullable e implicit usings (`Directory.Build.props:4`, `Directory.Build.props:5`, `Directory.Build.props:6`).
- O repositório usa Central Package Management (`Directory.Packages.props:4`).
- O host de produção permanece um monólito modular. Os módulos observados sob `src/Platform.Api/Modules/` são `Audit`, `Compliance`, `ContactConsent`, `Dispatch`, `Notifications`, `SharedKernel` e `TemplateManagement`.

## Stack observado

| Eixo | Evidência |
|---|---|
| API | Minimal APIs registradas por descoberta de módulos em `src/Platform.Api/Program.cs:38` e `src/Platform.Api/Program.cs:101` |
| Persistência | EF Core e PostgreSQL em `src/Platform.Api/Platform.Api.csproj:33` a `src/Platform.Api/Platform.Api.csproj:36` |
| Mensageria | SQS e Kafka em `src/Platform.Api/Platform.Api.csproj:40` e `src/Platform.Api/Platform.Api.csproj:41` |
| Cache | Redis em `src/Platform.Api/Platform.Api.csproj:50` |
| Armazenamento | AWS SDK for S3 em `src/Platform.Api/Platform.Api.csproj:45` |
| Gestão de chaves | AWS SDK for KMS em `src/Platform.Api/Platform.Api.csproj:46` |
| Autenticação | JWT bearer em `src/Platform.Api/Platform.Api.csproj:14` |
| Resiliência | HTTP resilience em `src/Platform.Api/Platform.Api.csproj:15` |
| Validação | FluentValidation em `src/Platform.Api/Platform.Api.csproj:12` e `src/Platform.Api/Platform.Api.csproj:13` |
| Testes de integração | LocalStack e Kafka em `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:32` a `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:34` |

## Fronteiras atuais

- `Notifications` possui o ciclo de vida da notificação, ingestão REST e Kafka, idempotência, pipeline, tentativas, fallback e rastreamento de entrega (`src/Platform.Api/Modules/Notifications/AGENTS.md:9` a `src/Platform.Api/Modules/Notifications/AGENTS.md:22`).
- `Notifications` deve consumir módulos irmãos somente pelos contratos publicados e não pode acessar o armazenamento interno de outro contexto (`src/Platform.Api/Modules/Notifications/AGENTS.md:27` a `src/Platform.Api/Modules/Notifications/AGENTS.md:36`).
- `Dispatch` possui os adaptadores de provedor e a tradução de um `DispatchRequest` em uma chamada ao provedor, sem possuir estado de tentativa, fallback ou auditoria (`src/Platform.Api/Modules/Dispatch/AGENTS.md:5` a `src/Platform.Api/Modules/Dispatch/AGENTS.md:24`, `src/Platform.Api/Modules/Dispatch/AGENTS.md:41` a `src/Platform.Api/Modules/Dispatch/AGENTS.md:43`).
- O módulo `Audit` já usa S3 para exportação WORM. A implementação é interna ao contexto, usa `IAmazonS3`, `PutObject`, `GetObject` e Object Lock (`src/Platform.Api/Modules/Audit/Infrastructure/Worm/S3WormObjectStore.cs:17` a `src/Platform.Api/Modules/Audit/Infrastructure/Worm/S3WormObjectStore.cs:80`). Esse código comprova disponibilidade do SDK e de um padrão de configuração, mas não constitui um contrato reutilizável por outro módulo.

## Lacuna da capacidade

- Não existe módulo `AttachmentManagement` no diretório de módulos observado.
- O comando atual de ingresso contém aplicação, destinatário, classe, template e TTL, mas nenhuma referência de anexo (`src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs:7` a `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs:36`).
- O hash idempotente atual cobre a forma vigente da requisição e não inclui anexos (`src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs:13` a `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.PayloadHash.cs:26`).
- A forma publicada de e-mail contém assunto, preheader, HTML e texto, sem anexos (`src/Platform.Api/Modules/Dispatch/Integration/V1/RenderedMessage.cs:23` a `src/Platform.Api/Modules/Dispatch/Integration/V1/RenderedMessage.cs:27`).
- O contrato da chamada ao SendGrid não contém a coleção `attachments` (`src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/SendGridMailRequest.cs:10` a `src/Platform.Api/Modules/Dispatch/Infrastructure/Providers/SendGrid/SendGridMailRequest.cs:15`).
- O guia do produtor limita o registro Kafka a 256 KB e proíbe anexos e dados de contato no barramento (`docs/guia-integracao-produtor.md:215`).

## Perfil preservado e divergência observada

O Stack Profile manual declara `net10.0`, monólito modular, ausência de mediator, EF, SQS, Kafka, Redis, S3, KMS e JWT (`.araia/stack-profile.yaml:4` a `.araia/stack-profile.yaml:32`). Esses eixos possuem evidência correspondente no repositório e permanecem válidos como declaração local.

O eixo `messaging-consumer-pattern: none` em `.araia/stack-profile.yaml:24` diverge do uso direto de Kafka e SQS observado nos pacotes e no módulo `Notifications`. A inspeção registra a divergência sem alterar o perfil, conforme a decisão do usuário.

## Superfícies técnicas afetadas

1. Novo contexto proprietário da custódia, validação, referência opaca e ciclo de vida dos anexos.
2. Contrato publicado desse contexto para consulta e claim de um conjunto liberado por `Notifications`.
3. Forma de ingresso e hash idempotente de `Notifications`, preservando compatibilidade para solicitações sem anexos.
4. Conteúdo selado da tentativa e contrato publicado para `Dispatch`.
5. Adaptador SendGrid, com submissão integral do conjunto e evidência do conteúdo enviado.
6. Auditoria e operação por contratos publicados, sem acesso cruzado a armazenamento interno.
7. Testes unitários, de arquitetura, segurança, integração, performance e probes da solução.

## Recibo

`SOLUTION_INSPECTION: PASS`

O inventário fornece evidência suficiente para a interpretação arquitetural brownfield. Nenhum arquivo de produção, configuração ou Stack Profile foi alterado.
