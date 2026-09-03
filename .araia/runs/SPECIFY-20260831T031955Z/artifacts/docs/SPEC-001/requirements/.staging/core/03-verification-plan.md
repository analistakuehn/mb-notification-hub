# Gestão de anexos do Notification Hub: Plano de verificação

**Status**: PROPOSED  
**Spec**: `SPEC-001`  
**Data**: 2026-08-31  
**Fonte**: [01-development-specification.md](01-development-specification.md)

## Objetivo e escopo

Demonstrar, antes de cada gate aplicável, que a gestão de anexos preserva custódia, isolamento, integridade, liberação, idempotência, compatibilidade e evidência ponta a ponta. O plano cobre `AttachmentManagement`, as mudanças em `Notifications`, o contrato de `Dispatch`, o adaptador SendGrid, os transportes afetados e o caminho brownfield sem anexos.

## Riscos de qualidade

| Risco | Efeito a impedir | Verificação principal |
|---|---|---|
| Estado, objeto ou autorização mudam entre validação, claim e submissão. | Envio de conteúdo revogado, vencido, substituído ou pertencente a outra aplicação. | Barreiras concorrentes, identidade imutável, matriz entre aplicações e contador do provedor. |
| Falha parcial separa claim, aceite, outbox ou tentativa. | Notificação aceita sem claim integral, órfão permanente ou chamada duplicada. | Injeção de falhas, reconciliação idempotente e teste de no máximo uma chamada por tentativa. |
| Base64 e serialização excedem o envelope ou o orçamento de runtime. | Pressão de memória, envio parcial ou descoberta tardia do excesso. | Sonda comparativa de buffer, streaming e spool com captura do payload final. |
| Evolução de contrato altera clientes sem anexos. | Regressão em REST, Kafka, hash, recusas, eventos ou seleção de canal. | Baseline brownfield, vetores dourados, snapshots e consumidores de versão anterior. |
| Conteúdo ou capacidade de acesso vaza por superfície operacional. | Exposição de dados, localização S3 ou credencial reutilizável. | Sentinelas e varredura de brokers, dead-letter, logs, traces, métricas, respostas e auditoria comum. |

## Cobertura e matriz de rastreabilidade

| ID | Requisito ou critério | Observável | Oráculo | Nível de verificação | Verificação rápida | Verificação de limite | Gate | Proprietário |
|---|---|---|---|---|---|---|---|---|
| `VER-001` | `PAC-001`, `ER-002` | A aplicação registra, fornece, acompanha e usa somente a referência liberada. | Estado consultado e resposta pública mostram a transição esperada sem revelar armazenamento. | unitário e integração | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | Host real, PostgreSQL e LocalStack exercitam o ciclo externo. | CI, G5s | `AttachmentManagement` |
| `VER-002` | `PAC-002`, `ER-004`, `NFR-002` | Referência pendente, rejeitada ou inexistente não cria notificação aceita. | Zero linhas aceitas, zero registros na outbox de dispatch e zero chamadas ao fake do SendGrid. | integração | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | `AttachmentManagement` para `Notifications`, com banco real. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-003` | `PAC-003`, `ER-005`, `ER-011`, `NFR-001` | O provedor recebe o conjunto completo e os bytes correspondem à identidade liberada. | Captura do JSON SendGrid, decodificação de cada base64 e igualdade de SHA-256, comprimento, nome e tipo. | integração e contrato | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | S3, conteúdo selado, `Dispatch.Integration` e fake HTTP. | CI, G5s, G6 | `Notifications` e `Dispatch` |
| `VER-004` | `PAC-004`, `ER-003`, `NFR-006` | Nenhuma combinação cruzada consulta, altera ou usa referência de outra aplicação. | Matriz principal A/B, aplicação A/B e referência A/B retorna zero acesso e não revela existência. | integração e segurança | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | REST, Kafka, contrato interno e persistência. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-005` | `PAC-005`, `ER-004`, `NFR-002` | Conteúdo hostil, protegido, não verificável ou inconclusivo nunca é liberado ou enviado. | Estado nunca alcança liberação, claim permanece ausente e o contador do provedor fica em zero. | unitário, integração e segurança | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | Fake de validação, S3 e fake do SendGrid. | CI, G5s, G6 | `AttachmentManagement` |
| `VER-006` | `PAC-006`, `ER-010`, `ER-015`, `NFR-003` | Plano incapaz de preservar anexos termina explicitamente sem envio degradado. | Resultado e evidência usam motivo estável; nenhum outro canal recebe mensagem e nenhum anexo é removido. | integração e contrato | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Pipeline, fallback, contrato de resultado e outbox. | CI, G5s, G6 | `Notifications` |
| `VER-007` | `PAC-007`, `ER-014`, `NFR-005` | Uma consulta autorizada reconstrói identidade, validação, notificação, tentativa e resposta. | Consulta retorna todas as relações e digests necessários, sem conteúdo bruto. | integração | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | `AttachmentManagement`, `Notifications`, `Dispatch` e `Audit`. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-008` | `PAC-008`, `ER-008`, `ER-015`, `NFR-007` | Solicitações sem anexos preservam REST, Kafka, hash, recusas, eventos e seleção de canal. | Suíte brownfield e vetores dourados apresentam zero divergência antes e depois da mudança. | unitário, integração e contrato | `dotnet test MonteBravo.NotificationHub.sln --no-restore` | Clientes e mensagens sem o novo membro atravessam versões mistas. | CI, G5s, G6 | `Notifications` |
| `VER-009` | `PAC-009`, `ER-008` | Mesmo manifesto retorna replay; diferença relevante retorna conflito sem novo efeito. | Matriz cobre ordem, duplicatas e propriedades conforme o corpus aprovado; contadores duráveis permanecem estáveis. | unitário e integração | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | Hash, idempotência, claim e persistência real. | CI, G5s | `Notifications` |
| `VER-010` | `PAC-010`, `ER-006`, `ER-013` | Falhas parciais convergem sem liberar conteúdo indevido nem manter órfão permanente. | Falha injetada em cada efeito produz rollback ou reconciliação idempotente observável. | integração e resiliência | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | PostgreSQL, S3, validação, outbox e reinício do host. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-011` | `PAC-011`, `ER-013` | Limpeza de abandonados não remove anexo vinculado a notificação ativa. | Varredura concorrente preserva objeto e metadado até o último dependente terminal. | unitário e integração | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Worker de manutenção, banco e S3. | CI, G5s, G6 | `AttachmentManagement` |
| `VER-012` | `PAC-012`, `ER-007`, `ER-014`, `NFR-004` | Nenhuma superfície coletada contém conteúdo, localização ou capacidade de acesso. | Sentinelas únicas semeadas no arquivo não aparecem em Kafka, SQS, outbox, dead-letter, logs, traces, métricas, respostas ou auditoria comum. | integração e segurança | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Todos os transportes e coletores do cenário. | CI, G5s, G6 | Todos os contextos afetados |
| `VER-013` | `PAC-013`, `ER-010`, `NFR-002` | Revogação ou vencimento antes da tentativa impede a chamada. | Barreira determinística intercala mudança de estado e início do envio; contador do provedor permanece em zero. | unitário, integração e concorrência | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Estado vivo, snapshot e ponto irreversível da chamada. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-014` | `PAC-014`, `ER-004`, `NFR-002` | Tipo divergente, metadado hostil e estrutura não inspecionável são recusados com segurança. | Fixtures específicas nunca alcançam liberação, e a resposta não ecoa conteúdo ou metadado hostil. | unitário, integração e segurança | `dotnet test tests/Platform.UnitTests/Platform.UnitTests.csproj` | Parser de metadados, validador e superfície pública. | CI, G5s, G6 | `AttachmentManagement` |
| `VER-015` | `ER-001`, `ER-011` | Dependências respeitam propriedade dos contextos e contratos versionados. | NetArchTest reprova acesso a Domain, Infrastructure, EF ou S3 de outro contexto e dependência de `Dispatch` para anexos internos. | função de adequação | `dotnet test tests/Platform.ArchTests/Platform.ArchTests.csproj` | Grafo de assemblies da API e do worker. | CI, G5s, G6 | Adapter `dotnet` |
| `VER-016` | `ER-006` | Claim e aceite permanecem consistentes sob qualquer falha durável exercitada. | Nenhum cenário termina com notificação aceita sem claim integral; nenhum claim órfão permanece após reconciliação. | integração e injeção de falhas | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Claim, notificação, idempotência, outbox, auditoria e commit. | CI, G5s, G6 | `AttachmentManagement` e `Notifications` |
| `VER-017` | `ER-005` | Troca do objeto após validação não altera os bytes enviados. | Upload V1, liberação, substituição sob a mesma chave e tentativa resultam em bloqueio ou leitura da geração V1; nunca em envio de V2. | integração e segurança | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | S3 descartável, identidade íntegra e fake do SendGrid. | CI, G5s, G6 | `AttachmentManagement` |
| `VER-018` | `ER-009`, `ER-012` | Concorrência e reentrega produzem no máximo uma chamada por tentativa, e cada tentativa autorizada preserva o snapshot aceito; submissão ambígua não é repetida cegamente. | Barreiras e o fake HTTP contam chamadas e comparam o manifesto capturado ao snapshot persistido; falha após o início permanece em estado explícito. | integração e resiliência | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Claim otimista, worker, snapshot, nova tentativa e provedor. | CI, G5s, G6 | `Notifications` e `Dispatch` |
| `VER-019` | `ER-015`, `NFR-007` | A versão contratual escolhida preserva consumidores antigos ou coexiste com a versão anterior. | Snapshots de OpenAPI e schema, serializadores e consumidores de versão anterior passam contra o produtor novo. | contrato | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | REST, Kafka, dead-letter, eventos e `Integration.*`. | CI, G5s, G6 | `Notifications` e adapter `dotnet` |
| `VER-020` | `ER-016`, `NFR-008` | Buffer, streaming e spool são comparados com o mesmo corpus e envelope. | Relatório registra payload UTF-8, heap, working set, alocação, GC, CPU, I/O, latência, throughput, backlog, limpeza e igualdade de digest; a opção promovida respeita o orçamento aprovado. | desempenho e capacidade | `dotnet run --project tests/Platform.PerformanceTests/Platform.PerformanceTests.csproj` | S3, serialização, fake HTTP, cancelamento e encerramento abrupto. | G5s, G6, rollout | `dotnet-architect` e `Dispatch` |
| `VER-021` | `ER-015` | Rollout e rollback preservam clientes antigos e anexos já aceitos. | Ensaio com processos de versões diferentes bloqueia novos aceites sem perder leitura, tentativa, reconciliação ou investigação; reversão lógica não apaga dados. | integração e operacional | `dotnet test tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj` | Migrações, controles de habilitação, API e worker. | G6, rollout | Adapter `dotnet` e Produto Notification Hub |

## Estratégia de testes e funções de adequação

| Categoria | Ferramenta e status | Uso nesta SPEC |
|---|---|---|
| Execução e asserções | xUnit e Shouldly, observados | Regras puras, handlers, transições, hash, concorrência e resultados. |
| Dublês de teste | NSubstitute, observado | Relógio, autorização, validação, portas S3/KMS e falhas controladas. |
| Host real | `WebApplicationFactory`, observado | Requisições REST, composição, autenticação, persistência e contratos HTTP. |
| Infraestrutura descartável | Testcontainers PostgreSQL, Redis, LocalStack e Kafka, observados; cenários de anexos previstos | Transações, claims, S3, mensagens, reentrega e falhas de dependência. |
| Contrato de provedor | Servidor HTTP falso e captura do JSON, observado | Forma SendGrid, base64, envelope, respostas 202, 4xx, 429, 5xx, timeout e rede. |
| Arquitetura | NetArchTest e `Platform.ArchTests`, observados | Propriedade dos módulos, dependências publicadas e domínio livre de infraestrutura. |
| Segurança arquitetural | `Platform.SecurityArchTests`, observado em `tests/Platform.SecurityArchTests/Platform.SecurityArchTests.csproj:16-27` | Superfícies proibidas, dados e capacidades em logs, eventos e contratos. |
| Desempenho | Executor em `Platform.PerformanceTests`, observado; cenário de anexos previsto | Comparação reproduzível de buffer, streaming e spool com métricas de runtime. |
| Efetividade | Stryker.NET, previsto pelo baseline do adapter .NET | Mutação das regras de domínio e canonicalização que precisam provar capacidade de falhar. |

Os testes seguem o dialeto xUnit, Shouldly, NSubstitute, `WebApplicationFactory`, Testcontainers e NetArchTest observado em `Directory.Packages.props:32-44`, `tests/Platform.UnitTests/Platform.UnitTests.csproj:31-42`, `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:18-33` e `tests/Platform.ArchTests/Platform.ArchTests.csproj:17-27`. Nomes de testes descrevem comportamento e não carregam IDs de PRD, SPEC ou critérios.

## Ambientes e dados de teste

| Ambiente ou dado | Finalidade | Controle de validade |
|---|---|---|
| Testes unitários em processo | Regras de estado, canonicalização, autorização e resultados puros. | Relógio, identificadores e dependências controlados por dublês. |
| Host com `WebApplicationFactory` | Composição, autenticação, REST, persistência e comportamento ponta a ponta. | Configuração de teste isolada e banco descartável. |
| PostgreSQL, Redis, LocalStack e Kafka em Testcontainers | Transações, claims, S3, mensageria, reentrega e falhas de dependência. | Recursos exclusivos por execução e limpeza ao término. |
| Servidor HTTP falso do SendGrid | Captura do JSON, respostas do provedor, timeout e falha de rede. | Contagem de chamadas e decodificação do payload capturado. |
| Corpus de anexos | Conteúdo válido, hostil, protegido, divergente, não inspecionável e próximo ao envelope aprovado. | Digest, comprimento e resultado esperado versionados com o teste. |
| Sonda de capacidade | Comparação de buffer, streaming e spool sob carga e cancelamento. | Mesmo corpus, envelope e perfil de concorrência para os três braços. |

## Efetividade dos testes

| ID | Alvo | Limiar | Ferramenta | Gate | Orçamento |
|---|---|---|---|---|---|
| `TEST-EFF-001` | Regras de ciclo de vida, identidade e claim do domínio `AttachmentManagement` | mínimo de 75% | Stryker.NET | G5s e G6 | nenhum teto adicional no SPECIFY |
| `TEST-EFF-002` | Regras puras de manifesto e idempotência de `Notifications` | mínimo de 75% | Stryker.NET | G5s e G6 | nenhum teto adicional no SPECIFY |

O limiar segue a linha de base do adapter .NET em `adapters/dotnet/references/templates/quality.template.md:84` e `shared/test-effectiveness-sensor.md:43-50`. PLAN deve projetar cada alvo para as Delivery Slices que tocarem essas regras; IMPLEMENT não pode estreitar o alvo nem reduzir o limiar para liberar o gate.

## Métricas

| Dimensão | Métrica ou evidência | Critério no candidato |
|---|---|---|
| Correção ponta a ponta | Conjunto, digest, comprimento, nome e tipo capturados no wire. | Igualdade integral com o manifesto liberado. |
| Segurança e isolamento | Violações do gate, acessos cruzados e ocorrências de sentinelas. | Zero ocorrência. |
| Idempotência e resiliência | Chamadas ao provedor por tentativa, órfãos e estados sem convergência. | No máximo uma chamada por tentativa e nenhuma inconsistência permanente. |
| Compatibilidade | Diferenças em REST, Kafka, hash, recusas, eventos e seleção de canal sem anexos. | Zero regressão no baseline. |
| Capacidade | Payload UTF-8, heap, working set, alocação, GC, CPU, I/O, latência, throughput, backlog e resíduos. | A alternativa promovida respeita o orçamento aprovado e libera recursos. |
| Evidência operacional | Tentativas aceitas pelo provedor que podem ser reconstruídas. | Cobertura integral. |

## Definição de Pronto global

- Todas as linhas obrigatórias da matriz de cobertura possuem evidência atual e aprovada no gate indicado.
- `dotnet build MonteBravo.NotificationHub.sln --no-restore` conclui sem erro nem aviso.
- Suítes unitárias, de integração, de arquitetura e de segurança arquitetural passam sem cenário obrigatório ignorado.
- Contratos REST, Kafka, eventos e `Integration.*` possuem snapshots ou consumidores de compatibilidade e catálogo estável de recusas.
- Migrações são aditivas, leitores tolerantes precedem escritores e o rollback não apaga objetos, claims ou metadados necessários.
- Falhas de S3, KMS, validação, PostgreSQL, outbox e provedor foram injetadas nos limites em que podem mudar o efeito observável.
- Testes concorrentes cobrem claim, revogação, descarte, reentrega e ponto irreversível de submissão.
- A varredura com sentinelas não encontra conteúdo bruto, localização ou capacidade de acesso nas superfícies proibidas.
- Os alvos de mutação atingem o limiar declarado quando tocados.
- Código, testes, migrações, esquemas e configurações não contêm referências a PRD, SPEC, Delivery Slice ou IDs de aceitação.

## Gates e critérios de primeira produção e rollout

- Produto Notification Hub registra os parâmetros aprovados de quantidade, tamanho, tipos, validade, retenção aplicável e comportamento de roteamento na superfície contratual ou configuração governada correspondente.
- As decisões condicionadas sobre claim, identidade S3, IAM/KMS, validação, manifesto, versão contratual e transferência possuem evidência executável e consequência registrada.
- Custódia, validação, reconciliação e descarte operam antes da habilitação do aceite em `Notifications`.
- REST, Kafka, OpenAPI, esquemas, eventos e consumidores antigos demonstram compatibilidade; quando a prova falha, versões coexistem.
- A sonda de capacidade demonstra que a estratégia de transferência respeita o orçamento aprovado, preserva igualdade byte a byte e limpa recursos após sucesso, falha, cancelamento e encerramento abrupto.
- A matriz de falhas de S3, KMS, validação, banco, outbox e provedor passa com bloqueio em caso de falha nos gates de segurança.
- O ensaio de rollout publica leitores tolerantes antes de escritores e executa processos de versões diferentes sem perda de compatibilidade.
- O ensaio de rollback desabilita novos aceites, mantém itens já aceitos e não remove anexos nem degrada o conjunto.
- A suíte do candidato atende `NFR-001` a `NFR-007`, e todas as tentativas aceitas pelo provedor permanecem reconstruíveis.
- O responsável por Produto Notification Hub aceita o candidato somente após a evidência acima estar associada ao release.

Valores operacionais sem evidência não são persistidos neste plano. Eles entram no gate somente após aprovação e medição no cenário da primeira produção.

## Fontes

- Dialeto de integração e infraestrutura de teste: `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj:17-35`.
- Contrato atual do adaptador SendGrid: `tests/Platform.IntegrationTests/Dispatch/SendGridProviderContractTests.cs:14-117`.
- Funções de adequação entre módulos: `tests/Platform.ArchTests/ArchitectureTests.cs:66-96`.
- Estrutura do executor de desempenho: `tests/Platform.PerformanceTests/Program.cs:86-113`.
- Contrato de produto e critérios de aceitação: [PRD de gestão de anexos](../../../prd-attachment-management.md).
