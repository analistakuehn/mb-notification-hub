---
language: pt-BR
document_type: technical-design
status: proposed
date: 2026-09-04
---

# Proposta de pacote NuGet para envio de notificações

| Campo | Definição |
|---|---|
| Pacote proposto | `MonteBravo.NotificationHub.Client` |
| Público | Times .NET que produzem notificações e engenharia do Notification Hub |
| Responsabilidade proposta | Engenharia do Notification Hub mantém o pacote e sua compatibilidade com a API |
| Estado | Proposta técnica para avaliação e implementação |
| Origem | Solicitação do usuário, em 4 de setembro de 2026, de um pacote autossuficiente para abstrair o envio |
| Base técnica | Contratos e código do repositório, inspecionados em 4 de setembro de 2026 |

## 1. Objetivo e proposta

Criar um único pacote NuGet que permita a uma aplicação .NET solicitar uma notificação com uma chamada, fornecendo destinatário, template, classe e dados de negócio. O pacote deve entregar a implementação completa de integração: configuração, autenticação, contratos públicos, serialização, comunicação HTTP, validação local, idempotência, retentativas seguras, preparação de anexos, interpretação de respostas e consulta do resultado.

**Autossuficiente significa que o consumidor instala o pacote, configura sua identidade e o endereço do hub e usa o cliente.** O consumidor não precisa implementar um provedor de token, montar cabeçalhos, escrever um cliente HTTP, serializar o protocolo, integrar armazenamento de anexos ou conhecer os provedores de entrega. As dependências técnicas necessárias são declaradas pelo próprio NuGet e restauradas transitivamente.

O pacote usa o Notification Hub como serviço. O ambiente precisa disponibilizar o hub, a identidade do produtor, as permissões, os templates e os contatos necessários. Credenciais, infraestrutura e cadastros não são recursos que uma biblioteca possa provisionar durante sua instalação.

Os comportamentos do SDK descritos a seguir são **propostos**. Os nomes de tipos, métodos, projetos e opções ilustram a API a implementar; este documento não anuncia um pacote já disponível.

## 2. Contexto e escopo

Hoje o produtor integra diretamente com o protocolo descrito no [guia de integração](guia-integracao-produtor.md). Centralizar essa integração no pacote reduz a repetição de autenticação, montagem de solicitações, tratamento de erros e recuperação de chamadas sem resposta entre as aplicações consumidoras.

| Comportamento observado | Consequência para o pacote | Evidência |
|---|---|---|
| O ingresso REST exige `Idempotency-Key` e devolve `202` no aceite ou `200` no replay | Preservar a identidade da solicitação e distinguir aceite de replay | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs:12`, `:62` |
| O hub resolve contato, template e canal | A API do pacote recebe `recipientId` e `templateKey`; a política permanece no hub | `docs/guia-integracao-produtor.md:83`, `:570` |
| As classes são `critical`, `transactional` e `operational` | Expor classes tipadas com os mesmos valores no JSON | `docs/guia-integracao-produtor.md:115` |
| Envio e consulta exigem permissões distintas | O envio funciona sem consulta automática | `docs/guia-integracao-produtor.md:69`, `:823` |
| Há rotas de registro, upload, validação, consulta e revogação de anexos | Orquestrar o fluxo pelo próprio hub, sem SDK de armazenamento no produtor | `src/Platform.Api/Modules/AttachmentManagement/AttachmentManagementModule.cs:86` |
| A configuração versionada desabilita novos anexos e mantém a lista de tipos admitidos vazia | Condicionar a liberação desse cenário à habilitação do ambiente | `src/Platform.Api/appsettings.json:72`, `:85` |
| O ingresso já distingue a capacidade de anexos desabilitada | Tratar `attachment-capability-not-enabled` como recusa que exige ação operacional | `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs:77`; `src/Platform.Api/Modules/Notifications/Infrastructure/Http/IngestionProblems.cs:55` |

O guia é a referência de integração. Esta proposta também verifica no código do checkout a recusa de capacidade desabilitada e as operações necessárias à preparação de anexos.

### Capacidades incluídas

- Solicitar notificações com variáveis de template, correlação e referências de anexos existentes.
- Preparar arquivos locais ou streams como anexos e solicitar a notificação em uma única operação de conveniência.
- Obter e renovar tokens de aplicação, aplicar autenticação e interpretar falhas de autorização.
- Validar a forma da solicitação, preservar sua identidade e tratar falhas transitórias.
- Consultar uma notificação e, mediante chamada explícita, aguardar seu resultado com prazo limitado.
- Consultar histórico por destinatário ou correlação, preservando filtros e paginação.
- Expor diagnósticos seguros, documentação de uso e exemplos verificáveis.

### Fora de escopo

O pacote não hospeda o hub nem seus workers. Cadastro de contatos, consentimentos, publicação de templates, administração de políticas, filas internas, armazenamento, auditoria de Compliance e credenciais de SendGrid, Twilio ou outros provedores continuam sob responsabilidade dos serviços correspondentes.

O ingresso Kafka permanece disponível para integrações que já o utilizam, mas não é dependência deste pacote. REST atende ao objetivo de uma integração completa com resposta de aceite e também ao envio de templates com variáveis sensíveis. A restrição desses templates ao REST está documentada em `docs/guia-integracao-produtor.md:308`.

## 3. Arquitetura e responsabilidades

A aplicação chama `INotificationClient`. A implementação do pacote obtém o token, prepara a solicitação, executa as operações REST e traduz a resposta. Após o aceite, o hub executa seu pipeline de entrega de forma assíncrona.

| Responsabilidade | Aplicação consumidora | Pacote NuGet | Notification Hub |
|---|---|---|---|
| Fato de negócio e destinatário | Define quando enviar e informa o identificador opaco | Valida a entrada | Resolve contato e elegibilidade |
| Template, classe e variáveis | Informa a intenção e os dados | Serializa e valida a forma | Valida o catálogo, aplica políticas e renderiza |
| Identidade e acesso | Configura credenciais e permissões provisionadas | Obtém, reutiliza e renova tokens | Autentica e autoriza cada operação |
| Idempotência | Fornece chave estável do fato | Conserva chave, corpo e referências nas tentativas | Decide aceite, replay e conflito |
| Anexos | Fornece arquivos ou referências | Registra, transfere, solicita validação e monta o manifesto | Valida conteúdo, libera, vincula e transporta |
| Resultado | Decide a reação do negócio | Devolve recibo e oferece consultas | Mantém estado, tentativas e evidência de entrega |
| Recuperação após queda do produtor | Persiste o trabalho quando sua transação exige isso | Oferece solicitação preparada serializável e reenvio seguro | Mantém a idempotência durante sua retenção |

### Composição e dependências

Propor um projeto empacotável em `src/MonteBravo.NotificationHub.Client/`, com contratos públicos e implementação interna no mesmo pacote. Organizar internamente as responsabilidades em configuração, autenticação, notificações, anexos, transporte e diagnósticos. Criar projetos próprios de testes e um exemplo de consumidor, sem transformar os módulos do servidor em bibliotecas públicas.

| Dependência proposta | Finalidade |
|---|---|
| Bibliotecas do .NET, incluindo `System.Text.Json` | HTTP, streams, JSON, relógio e cancelamento |
| `Microsoft.Extensions.Http` | Registro do cliente e gerenciamento dos handlers HTTP |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | Configuração tipada e validação na inicialização |
| `Microsoft.Extensions.Logging.Abstractions` | Integração com o logging escolhido pela aplicação |
| `Microsoft.Extensions.Http.Resilience` | Estratégias de resiliência configuradas por operação |
| `Microsoft.Extensions.Hosting` | Suporte ao exemplo de console com Generic Host após instalar somente o pacote proposto |

Usar `IHttpClientFactory` para administrar conexões e o tempo de vida dos handlers. O cliente público deve ser seguro para chamadas concorrentes e não capturar um cliente HTTP tipado permanentemente dentro de um singleton. A implementação pode criar clientes nomeados por operação usando a factory. [Documentação do `IHttpClientFactory`](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory).

O pacote não depende de `Platform.Api`, `Platform.Worker`, `SharedKernel`, EF Core, Redis, AWS, Kafka nem SDKs de entrega. A dependência com o servidor é o contrato HTTP versionado. Não adicionar dependência de `Microsoft.AspNetCore.App`: o mesmo pacote deve funcionar em uma API, um worker ou uma aplicação de console.

Propor inicialmente `net10.0`, alinhado ao projeto observado em `src/Platform.Api/Platform.Api.csproj:4`. Essa escolha restringe o consumo a aplicações compatíveis; a validação de adoção deve verificar o framework de cada consumidor antes de liberar a versão. Suporte a outro framework exige target e testes próprios, sem deduzir compatibilidade a partir do runtime do servidor.

## 4. Contrato público e experiência de uso

### Operações propostas

| Operação | Resultado e responsabilidade |
|---|---|
| `SendAsync(request, cancellationToken)` | Valida, prepara anexos quando houver, autentica e solicita o aceite. Retorna `NotificationSendResult` |
| `SendAsync(request, context, cancellationToken)` | Executa o mesmo fluxo e atualiza um `NotificationOperationContext` fornecido pelo consumidor, acessível mesmo se a chamada for cancelada |
| `PrepareAsync(request, cancellationToken)` | Executa a preparação e devolve `PreparedNotification` imutável, sem solicitar a notificação |
| `SendPreparedAsync(prepared, cancellationToken)` | Envia exatamente a solicitação preparada, sem registrar anexos novamente |
| `GetAsync(notificationId, cancellationToken)` | Consulta o estado pelo identificador público recebido no aceite |
| `WaitForOutcomeAsync(notificationId, options, cancellationToken)` | Consulta até obter resultado terminal ou encerrar o prazo informado |
| `ListByRecipientAsync` e `ListByCorrelationAsync` | Encapsulam filtros, janela temporal e cursor do histórico |

Um cliente auxiliar de anexos, entregue no mesmo pacote, oferece preparação, consulta e revogação explícitas para aplicações que precisam controlar essas etapas. O fluxo usual usa somente `INotificationClient`. Pontos de extensão para token e transporte são opcionais e devem acompanhar implementações prontas.

### Dados de entrada

| Propriedade proposta | Regra |
|---|---|
| `Application` | Configurada por cliente e fixada na solicitação preparada |
| `RecipientId` | Identificador opaco obrigatório; não é endereço de entrega |
| `Class` | `Critical`, `Transactional` ou `Operational`, serializados com o vocabulário do hub |
| `TemplateKey` | Chave obrigatória do template publicado |
| `TimeToLive` | Duração obrigatória, positiva, em segundos inteiros; conversão explícita para `ttlSeconds` |
| `IdempotencyKey` | Chave obrigatória e estável derivada pelo produtor do fato de negócio |
| `Variables` | Objeto JSON ou objeto .NET serializável como objeto JSON; copiar seu conteúdo na preparação |
| `CorrelationId` | Identificador opcional da transação de negócio, preservado nas retentativas |
| `Metadata` | Contexto opcional; advertir na documentação que participa da identidade e não é persistido pelo ingresso |
| `Attachments` | Sequência de fontes de arquivo ou referências existentes, preservando a ordem declarada |

A validação local replica limites de forma comprovados do contrato: aplicação e destinatário com até 100 caracteres, template com até 200, chave e correlação com até 200, TTL de até 2.592.000 segundos, variáveis com até 262.144 bytes e metadados com até 32.768 bytes em JSON compacto UTF-8. O servidor continua autoritativo para existência de template, esquema das variáveis, autorização, disponibilidade e limites de negócio. [Esquema vigente](guia-integracao-produtor.md#schema-da-solicitação), `src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs:19`.

Não oferecer atalhos `SendEmailAsync`, `SendSmsAsync` ou parâmetros de conteúdo renderizado, pois a decisão de canal pertence ao hub. Omitir `locale`, `channelsHint` e `scheduledAt` da API simplificada: são aceitos pelo ingresso, mas não produzem os efeitos sugeridos por seus nomes. [Campos sem efeito](guia-integracao-produtor.md#42-campos-aceitos-sem-efeito).

### Exemplo de integração proposta

Os exemplos abaixo representam o uso pretendido após a implementação. O template, o destinatário e as permissões devem existir no ambiente de teste.

```bash
dotnet add package MonteBravo.NotificationHub.Client
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonteBravo.NotificationHub.Client;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNotificationHub(
    builder.Configuration.GetSection("NotificationHub"));

using var host = builder.Build();
await host.StartAsync();

var client = host.Services.GetRequiredService<INotificationClient>();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

var result = await client.SendAsync(new NotificationRequest
{
    RecipientId = "cus_exemplo_001",
    Class = NotificationClass.Transactional,
    TemplateKey = "operacao.confirmada",
    TimeToLive = TimeSpan.FromHours(1),
    IdempotencyKey = "operacao.confirmada:operacao_123",
    CorrelationId = "operacao_123",
    Variables = new { operationNumber = "123" }
}, lifetime.ApplicationStopping);

if (result is NotificationSendResult.Accepted accepted)
{
    Console.WriteLine(accepted.Receipt.NotificationId);
}

await host.StopAsync();
```

A seção `NotificationHub` deve conter `BaseAddress`, `Application` e as opções `Authentication.TokenEndpoint`, `Authentication.ClientId`, `Authentication.Scope`, `Authentication.ClientSecret` e `Authentication.ClientAuthenticationMethod`. Endereço, identidade e escopo vêm do provisionamento. Carregar o segredo por variável de ambiente ou provedor de configuração seguro, como `NotificationHub__Authentication__ClientSecret`; nunca incluí-lo no exemplo versionado.

Para enviar um arquivo, a mesma solicitação recebe `Attachments = [NotificationAttachment.FromFile(filePath, contentType: "application/pdf")]`. O SDK resolve nome e tamanho do arquivo e executa a preparação com o tipo declarado. Fontes baseadas em stream informam nome, tipo, tamanho e uma factory assíncrona de abertura. Os testes do pacote devem compilar esses exemplos contra o `.nupkg` produzido.

## 5. Segurança, autenticação e configuração

Entregar uma implementação completa de OAuth 2.0 client credentials, com autenticação do cliente por HTTP Basic ou, quando exigido pelo provedor, segredo no corpo. Obter o token por HTTPS, registrar sua expiração, reutilizá-lo em memória e obter outro antes de expirar. Esse fluxo não depende de login interativo nem de refresh token. Permitir a substituição opcional por um provedor de token da aplicação para identidades que usem outro mecanismo. O pacote não pressupõe Microsoft Entra ou outro emissor específico. [OAuth 2.0, seções 2.3.1 e 4.4](https://datatracker.ietf.org/doc/html/rfc6749#section-4.4).

Isolar o cache por identidade, endpoint e escopo. Coordenar renovações concorrentes para evitar uma chamada de autenticação por envio; uma falha de renovação não pode reutilizar token expirado. Uma resposta `401` permite uma única renovação e repetição segura, dentro do limite total da operação. `403` exige correção de permissão ou ação operacional.

| Configuração | Comportamento proposto |
|---|---|
| Endereço e autenticação | Validar URI, campos obrigatórios e modalidade de autenticação suportada ao inicializar o cliente |
| Aplicação | Obrigatória e isolada por instância configurada; não modificar cabeçalhos globais entre envios |
| Timeout e retentativas | Opções positivas, com limites por tentativa e por operação |
| Preparo e espera de resultado | Prazos próprios, distintos do TTL da notificação e do timeout do POST |
| Certificados e redirecionamentos | Validar TLS normalmente e bloquear redirecionamentos automáticos dos clientes autenticados |
| Credenciais | Ler pela configuração da aplicação; invalidar o cache quando a identidade configurada for substituída |

Exigir HTTPS, admitindo HTTP somente mediante opção explícita de desenvolvimento para loopback. O destinatário não controla a URL de destino. Não implementar download de anexos a partir de URLs arbitrárias; trabalhar com arquivos locais, streams fornecidos pela aplicação e referências emitidas pelo hub.

O pacote não inclui valores de variáveis, bytes de anexos, nomes de arquivos, credenciais ou tokens em logs, métricas e exceções. Sanitizar detalhes de erros HTTP antes de incorporá-los aos diagnósticos. Restringir leitura de corpos de erro para não materializar respostas sem limite.

O envio exige o papel da classe solicitada. A consulta exige `Notifications.Read` e deve ser habilitada deliberadamente pelo consumidor. Essa permissão permite atualmente leitura entre aplicações, conforme `docs/guia-integracao-produtor.md:1291`; o SDK não concede esse papel nem o exige no registro básico do cliente. A gestão de anexos também depende da autorização de produtor para a aplicação. Nessa superfície, a identidade usa `oid`, `sub` ou `NameIdentifier`; um token que identifica o produtor somente por `appid` não basta. Evidências: `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Authorization/AuthorizationSetup.cs:31` e `src/Platform.Api/Modules/AttachmentManagement/Infrastructure/Authorization/AttachmentPrincipal.cs:14`.

## 6. Idempotência, erros e resiliência

### Identidade da solicitação

Manter a chave estável no escopo de `application`. O pacote nunca gera uma chave nova para corrigir timeout, `409` ou retentativa. Ao iniciar o envio, copiar variáveis, metadados e a sequência de anexos para que alterações posteriores nos objetos do consumidor não mudem o corpo entre tentativas. Reutilizar os mesmos bytes serializados nos reenvios de uma solicitação preparada.

O hash autoritativo permanece no servidor. O SDK não reproduz o algoritmo de canonicalização nem normaliza referências opacas. Deve preservar ordem, caixa e grafia; omitir `attachments` quando não houver anexos e rejeitar uma lista explicitamente vazia, itens em branco ou duplicatas. Essas regras respeitam a [decisão sobre a identidade do manifesto](ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md) e o contrato do ingresso.

### Resultado da chamada

`NotificationSendResult` deve distinguir aceite confirmado, recusa e resultado indeterminado. O recibo de aceite contém `NotificationId`, `IsReplay`, a chave usada e a correlação. `202` e `200` confirmam o aceite da mesma solicitação; nenhum deles confirma entrega ao destinatário.

Nas falhas, devolver código estável, status HTTP quando existir, operação afetada, `RetryAfter` quando fornecido, erros de campo sanitizados e a certeza sobre a submissão: não enviada, recusada ou indeterminada. Um timeout após transmissão não autoriza afirmar que o hub deixou de aceitar a solicitação. Se uma tentativa anterior ficou sem resposta, uma falha posterior de autenticação ou transporte também não elimina essa incerteza.

Não obrigar o consumidor a capturar exceções para recusas de negócio. Reservar exceções para configuração inválida, uso incorreto da API e cancelamento solicitado pelo chamador. Preservar `CancellationToken` em autenticação, transferências, esperas e chamadas HTTP. Cancelar a espera local não cancela uma notificação já aceita.

Para recuperar um envio de conveniência cancelado, o consumidor pode criar um `NotificationOperationContext` e passá-lo à sobrecarga correspondente. Antes do POST, o SDK publica nesse contexto o `PreparedNotification` imutável; durante o preparo, atualiza o progresso conhecido. Após `OperationCanceledException`, o contexto continua acessível e permite `SendPreparedAsync` com a mesma identidade. O contexto deve ser exclusivo por operação, expor snapshots seguros para leitura concorrente e não imprimir o payload em `ToString` ou logs. Se o preparo não terminou, ele não autoriza a submissão. A sobrecarga sem contexto não promete recuperar seu preparo interno após cancelamento. Para sobreviver à queda do processo, usar o fluxo de persistência da seção 7.

| Resposta ou condição | Tratamento do SDK |
|---|---|
| `202` / `200` | Devolver aceite com `IsReplay` correspondente |
| `400` | Devolver erro de contrato ou validação; não repetir automaticamente |
| `401` | Renovar uma vez e repetir somente a operação cuja repetição seja segura |
| `403` | Devolver recusa de acesso ou produtor desabilitado; não insistir |
| `409 idempotency-key-conflict` | Devolver conflito; não alterar chave nem payload |
| `422` | Devolver o motivo, incluindo `attachments-not-claimable` e `attachment-capability-not-enabled`; não insistir |
| `429 recipient-rate-limited` | Devolver limite de negócio, preservando `Retry-After`; não fazer retentativa automática |
| `429 principal-rate-limited` | Respeitar `Retry-After` e repetir dentro do orçamento da operação |
| `429` sem motivo reconhecido | Devolver limitação com metadados disponíveis; evitar classificar como limite do destinatário ou repetir às cegas |
| Falha de rede, timeout, `408`, `500`, `502`, `503` ou `504` | Repetir somente operações seguras; ao esgotar o orçamento, preservar a certeza real sobre o envio |
| Corpo vazio, HTML ou erro sem `type` | Devolver erro de protocolo ou infraestrutura com status preservado e conteúdo sanitizado |

Aplicar backoff exponencial com jitter. Propor para o POST de notificação e consultas um orçamento total inicial de 30 segundos, com até 10 segundos por tentativa e no máximo duas retentativas. A renovação por `401` também consome esse orçamento e esse limite. Esses valores são configurações iniciais de cliente, não SLOs de entrega. Se `Retry-After` exceder o tempo restante, devolver o erro com a indicação de espera, sem antecipar o reenvio.

Configurar resiliência por operação e usar uma única cadeia, sem hedging. O handler padrão do .NET repete inclusive métodos de escrita; portanto, sua configuração genérica não é adequada para todas as rotas deste SDK. Autorizar retentativa do POST de notificação somente com chave e corpo preservados. Registro e transferência de anexos seguem as regras da próxima seção. [Resiliência HTTP no .NET](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience).

## 7. Anexos, dados e persistência

### Fluxo completo de preparação

Para cada arquivo, o SDK executa as operações abaixo, preservando a ordem da solicitação:

1. Validar os metadados e a disponibilidade da fonte, sem colocar o arquivo inteiro em memória.
2. Registrar metadados por `POST /v1/attachments` e guardar a referência recebida.
3. Transferir o conteúdo por `PUT /v1/attachments/{reference}/content`, como stream, com tamanho conhecido.
4. Solicitar validação por `POST /v1/attachments/{reference}/validation`.
5. Prosseguir apenas quando a resposta indicar liberação. Se o resultado for inconclusivo, consultar o estado e repetir a solicitação de validação de forma limitada, dentro do prazo de preparação.
6. Montar o manifesto com todas as referências liberadas e somente então enviar `POST /v1/notifications`.

As rotas estão registradas em `src/Platform.Api/Modules/AttachmentManagement/AttachmentManagementModule.cs:86`. O upload recebe corpo binário, conforme `src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/UploadAttachment/UploadAttachment.Endpoint.cs:17`. A validação distingue liberação, inconclusão e recusa, conforme `src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/ValidateAttachment/ValidateAttachment.Handler.cs:43`.

Fontes de stream abertas pelo SDK são encerradas pelo SDK. Fontes e referências do consumidor permanecem sob sua responsabilidade. Limitar concorrência e buffers; comparar a quantidade transferida com o tamanho informado. A verificação de conteúdo e a autorização pertencem ao hub, mesmo quando o SDK executa verificações locais.

**Falhar em qualquer anexo interrompe o envio da notificação inteira.** Nunca remover o anexo, substituir por link ou escolher outro canal para fazer a solicitação passar. Claim integral e snapshot do manifesto são garantias do servidor, preservadas pela [decisão de claim atômico](ADR-0018-claim-atomico-na-transacao-de-aceite.md) e pela [decisão de snapshot](ADR-0019-snapshot-do-manifesto-aceito.md).

### Recuperação e limites transacionais

O registro de anexo cria uma referência e não recebe chave de idempotência no contrato observado. Uma resposta perdida nessa etapa não pode ser resolvida repetindo o registro automaticamente: devolver falha de preparação com o progresso conhecido. Não inventar uma rota de busca por nome de arquivo. A aplicação pode iniciar outro preparo deliberadamente; o recurso órfão fica sujeito à retenção do hub. Evidência: `src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/RegisterAttachment/RegisterAttachment.Command.cs:5` e `src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/RegisterAttachment/RegisterAttachment.Handler.cs:53`.

No upload interrompido, consultar a referência conhecida e continuar apenas quando seu estado permitir uma transição comprovada pelo contrato. Não repetir um stream consumido nem assumir que um `PUT` torna a operação idempotente. Se o estado não permitir concluir com segurança, devolver falha de preparação. Validação pode ser repetida sobre a mesma referência conforme o comportamento de resultado já decidido do handler.

`PreparedNotification` contém versão do formato, aplicação, chave, correlação, corpo de envio e referências na ordem original. Não contém tokens, streams ou bytes de anexos. Entregar serialização e desserialização desse objeto no pacote. No caminho com recuperação durável, o consumidor chama `PrepareAsync`, persiste o resultado e depois chama `SendPreparedAsync`. Assim, uma retomada do POST não cria referências novas nem transforma um replay em conflito.

O pacote não cria banco de dados, arquivo de checkpoint ou outbox oculto. O envio de conveniência mantém o preparo em memória e devolve o progresso conhecido em falhas, mas não garante recuperação após queda do processo. Quando o negócio exigir atomicidade entre sua transação e a intenção de notificar, o produtor usa seu armazenamento transacional e seu outbox. O pacote executa a integração a partir desse registro; não consegue participar automaticamente de uma transação que desconhece.

Solicitações preparadas podem conter variáveis sensíveis. Sua persistência exige a mesma proteção e retenção do dado de origem, sem segredos de autenticação. Não conservar tokens ou respostas brutas em checkpoints.

A idempotência do hub possui retenção: o registro se torna elegível para remoção após 24 horas, conforme `docs/guia-integracao-produtor.md:480`. Registrar o instante da primeira tentativa e o prazo máximo de recuperação em estado operacional separado do `PreparedNotification`, sem alterar seu payload imutável. No fluxo durável, o produtor persiste esse estado antes da primeira transmissão. Não oferecer reenvio indefinido como garantia contra duplicatas: após ultrapassar a janela segura, encaminhar a recuperação ao processo de negócio. A validade dos anexos também precisa permitir o novo aceite.

### Condição de liberação dos anexos

A implementação do pacote deve contemplar o fluxo completo. Sua liberação para consumo com anexos exige que o ambiente habilite a capacidade, configure os tipos de conteúdo e comprove validação, armazenamento e entrega do conjunto. Enquanto isso, o SDK deve apresentar a recusa de capacidade e preservar os arquivos solicitados. Não divulgar o suporte completo a anexos como operacional apenas porque o membro existe no JSON.

## 8. Consulta e observabilidade

`GetAsync` preserva o identificador público `ntf_...` recebido no aceite. Não converter IDs de eventos Kafka em IDs HTTP por inferência. As representações diferem no contrato atual, conforme `docs/guia-integracao-produtor.md:798`.

`WaitForOutcomeAsync` é opcional, exige permissão de leitura e recebe prazo explícito. Deve tolerar leitura eventualmente desatualizada e `404` temporário após aceite, espaçar consultas e terminar sem bloquear uma thread. Expirar essa espera significa que o cliente não observou um resultado no prazo; não significa falha de entrega.

Estados futuros e motivos de falha não reconhecidos precisam continuar disponíveis como valores brutos, sem quebrar a desserialização nem ser classificados automaticamente como terminais. O estado de uma tentativa não substitui o estado agregado da notificação. A confirmação `delivered` segue a semântica do hub, inclusive a aceitação pelo provedor em push na última etapa do plano; não comprova leitura humana. [Acompanhamento do resultado](guia-integracao-produtor.md#5-observabilidade-e-acompanhamento-do-resultado).

Nas listagens, preservar a janela temporal resolvida na primeira página, os filtros e o cursor opaco até o fim da navegação. Não permitir listagem irrestrita inventada pelo cliente. [Consulta REST](guia-integracao-produtor.md#52-consulta-rest).

Instrumentar operações com `ActivitySource`, `Meter` e `ILogger`, sem impor exportador ou backend de observabilidade ao consumidor. Medir duração de autenticação, preparação e submissão, quantidade de tentativas, aceites, replays, recusas e resultados indeterminados. Usar rótulos de baixa cardinalidade; destinatário, chave idempotente, nome de arquivo e valores de variáveis não viram dimensões de métricas.

Propagar contexto de trace por cabeçalhos e preservar `CorrelationId` como identidade de negócio independente do trace. Não recalcular a correlação a cada tentativa nem incluí-la automaticamente a partir do trace em um corpo que já foi preparado.

## 9. Alternativas e consequências

| Alternativa | Benefício | Custo e recomendação |
|---|---|---|
| Pacote REST completo | Uma instalação e um fluxo de integração, incluindo autenticação e anexos | Mantém dependências transitivas de infraestrutura HTTP. Recomendada para atender à autossuficiência |
| Apenas contratos ou cliente gerado do OpenAPI | Menor manutenção manual do protocolo | O consumidor ainda implementa token, idempotência, recuperação e anexos. Não atende sozinho ao objetivo; geração pode apoiar a implementação interna |
| Pacotes separados de abstrações, autenticação e transporte | Permite combinações independentes | Aumenta composição e incompatibilidades de versões na integração inicial. Reavaliar quando houver consumidores que precisem usar as partes isoladamente |
| Kafka como transporte padrão do pacote | Integra com produtores que já usam outbox e barramento | Exige acesso ao broker, acompanhamento assíncrono e não atende templates sensíveis. Manter como integração independente |
| Envio direto a provedores dentro do NuGet | Permite entregar sem chamar o hub | Distribui credenciais, políticas e lógica de canal aos consumidores. Altera a arquitetura e multiplica a manutenção |

A consequência da opção recomendada é depender da disponibilidade do hub e do provedor de identidade para iniciar uma solicitação. O produtor continua responsável pelo fato de negócio e pela durabilidade anterior ao aceite; o hub continua responsável pela execução posterior. O benefício é concentrar a complexidade de integração numa biblioteca mantida junto ao contrato do serviço.

## 10. Qualidade e critérios de aceitação

| Cenário verificável | Resultado exigido |
|---|---|
| Consumidor novo, somente com o pacote e configuração válida | Autentica e solicita uma notificação sem implementar interfaces de infraestrutura |
| Console, worker e API no framework declarado | Restauram o `.nupkg`, compilam os exemplos e registram o cliente |
| Configuração inválida | Falha na inicialização com mensagem clara e sem expor segredo |
| Token expirando sob chamadas concorrentes | Coordena renovação e evita usar token vencido |
| Aceite seguido de resposta perdida | Repete a mesma chave e o mesmo corpo e reconhece o replay com o ID original |
| Conflito, recusa de negócio ou limite do destinatário | Preserva o motivo e não inicia retentativa automática |
| `Retry-After`, cancelamento ou prazo total atingido | Respeita o tempo de espera e encerra dentro do orçamento |
| Cancelamento após iniciar o POST com anexos | O contexto fornecido conserva o preparo; a retomada usa a mesma chave e referências, sem expô-las na exceção |
| Envio com arquivos, em ambiente habilitado | Registra, transfere, valida e envia todas as referências sem código HTTP no consumidor |
| Capacidade desabilitada, anexo recusado ou preparo inconclusivo | Retorna falha contextualizada e não envia notificação sem os anexos |
| Queda após persistir `PreparedNotification` | Retoma o POST com as mesmas referências e identidade, dentro da janela segura |
| Notificação aceita, mas ainda invisível na réplica | A espera tolera ausência transitória e não reenvia a notificação |
| Corpo de erro inesperado ou valor futuro no resultado | Preserva diagnóstico seguro e não quebra por enumeração fechada |
| Logs e traces capturados nos cenários de erro | Não contêm credenciais, variáveis, conteúdo ou nomes de anexos |
| Inspeção do pacote e suas dependências | Não inclui servidor, segredos, SDKs de provedores nem dependências internas do hub |

Testar regras de serialização, snapshots, retentativa, tempo e cancelamento com transporte controlado e relógio substituível. Executar testes de contrato contra a API real em ambiente de integração, incluindo `202`, `200`, `409`, recusas e anexos. Verificar o fluxo de entrega separadamente do aceite. Esses são critérios para a implementação futura; a criação deste documento não executa nem comprova tais testes.

## 11. Implantação, compatibilidade e suporte

| Etapa | Entrega e condição de avanço |
|---|---|
| Contrato público | Consolidar nomes, certeza do resultado, configuração, framework e semântica de retomada com os consumidores |
| Cliente completo | Implementar autenticação, envio, consultas, erros, resiliência e diagnósticos com testes de contrato |
| Anexos e recuperação | Implementar preparo, serialização, recuperação e cenários de falha; validar em ambiente habilitado |
| Piloto | Publicar versão de pré-lançamento em feed autorizado e migrar um consumidor, preservando chaves de negócio |
| Versão estável | Liberar após os critérios de aceitação, documentação e compatibilidade passarem no pacote efetivamente restaurado |

Usar versionamento semântico e tratar a API pública do NuGet e o protocolo `/v1` como versões distintas. Correções preservam contrato; mudanças aditivas preservam consumidores existentes; remoções, novos requisitos obrigatórios e mudanças de significado exigem versão principal. Manter fixtures de contrato e verificação de compatibilidade da API pública no processo de release.

Distribuir README, documentação XML, notas de versão, metadados de repositório e símbolos adequados à política de distribuição. Gerar o pacote em CI, verificar dependências e conteúdo e publicar no feed autorizado, com versão imutável. Essas práticas seguem a [orientação de autoria de pacotes NuGet](https://learn.microsoft.com/en-us/nuget/create-packages/package-authoring-best-practices).

A migração substitui a integração manual pelo cliente, conservando aplicação, templates, identidade e chaves de negócio. Fazer rollout gradual por consumidor. Para rollback, fixar a versão anterior compatível ou reativar sua integração anterior; não executar as duas rotas simultaneamente para o mesmo trabalho, não trocar a chave e não retirar anexos de uma solicitação preparada.

A equipe do hub deve manter a matriz de compatibilidade com a API, as notas de alteração e os exemplos. O consumidor fornece versão do pacote, operação, horário e identificadores de correlação permitidos para diagnóstico. Recusas de negócio, falhas de integração e incerteza de submissão precisam continuar distinguíveis no atendimento.

## 12. Riscos e controles

| Risco | Controle proposto |
|---|---|
| Interpretar autossuficiência como envio sem infraestrutura ou cadastro | Definir explicitamente os pré-requisitos e comprovar a integração inicial com um consumidor novo |
| Prometer entrega ao retornar do POST | Nomear o resultado como recibo de aceite e separar consulta de entrega |
| Duplicar notificações após timeout ou reinício | Preservar chave e solicitação preparada; limitar recuperação à retenção suportada |
| Recriar anexos numa retentativa | Separar preparo de submissão e conservar o manifesto original |
| Divulgar suporte a anexos antes da habilitação | Condicionar esse cenário à validação do ambiente e tratar a recusa de capacidade |
| Divergir do contrato do hub | Testes de contrato, API pública versionada e release coordenado |
| Aumentar dependências ou restringir consumidores | Inspecionar o `.nupkg` e verificar frameworks reais antes da adoção |
| Expor dados em diagnóstico ou checkpoint | Redação de logs por lista permitida e proteção de solicitações persistidas |

## 13. Referências

- [Guia de integração do produtor](guia-integracao-produtor.md), fonte do comportamento publicado de ingresso e acompanhamento.
- [Comando de solicitação](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Command.cs), [endpoint](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Endpoint.cs) e [validador](../src/Platform.Api/Modules/Notifications/Features/Ingress/RequestNotification/RequestNotification.Validator.cs).
- [Rotas de anexos](../src/Platform.Api/Modules/AttachmentManagement/AttachmentManagementModule.cs), [registro](../src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/RegisterAttachment/RegisterAttachment.Handler.cs), [validação](../src/Platform.Api/Modules/AttachmentManagement/Features/Attachments/ValidateAttachment/ValidateAttachment.Handler.cs) e [configuração versionada](../src/Platform.Api/appsettings.json).
- [Claim atômico no aceite](ADR-0018-claim-atomico-na-transacao-de-aceite.md), [snapshot do manifesto](ADR-0019-snapshot-do-manifesto-aceito.md) e [identidade canônica dos anexos](ADR-0021-manifesto-de-anexos-na-forma-canonica-do-ingresso-publicado.md).
- [Stack Profile do projeto](../.araia/stack-profile.yaml), para alinhamento da implementação proposta com o ambiente observado.
