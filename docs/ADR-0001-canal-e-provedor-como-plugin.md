---
language: pt-BR
---

# ADR-0001: Canal e provedor como plugin

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Engenharia de Plataforma |
| **Consultados** | Compliance (gestão de terceiros), Produto |
| **Relacionadas** | ADR-0009 (construir vs. comprar), ADR-0010 (Kafka/SQS), ADR-0011 (política e fallback entre canais) |
| **Documento-mãe** | Notification Hub — Design de Sistema, §4.3 "Dispatchers", §8, §16 risco 2 |

## Contexto e problema

O hub entrega mensagens por quatro canais (e-mail, SMS, push, WhatsApp) através de fornecedores externos. Fornecedores mudam de preço, reputação, cobertura de operadora e disponibilidade; a Meta altera regras e precificação do WhatsApp Business com frequência; a Twilio e o SendGrid pertencem à mesma empresa, o que cria risco de indisponibilidade correlacionada em três dos quatro canais.

Na v1 há **um provedor por canal**: SendGrid (e-mail), Twilio (SMS e WhatsApp), FCM (push). A pergunta é como estruturar o código e a configuração para que a troca ou a adição de um provedor seja uma mudança barata, reversível e auditável, sem construir, já na v1, um mecanismo de failover que ainda não tem demanda.

## Fatores de decisão

- **Reversibilidade**: trocar de fornecedor deve ser decisão comercial, não projeto de engenharia.
- **Gestão de terceiros (Res. CMN 4.893/2021)**: cada provedor precisa estar inventariado, com contrato, localização de dados e plano de contingência.
- **Isolamento de falhas**: problema em um provedor não pode contaminar os outros canais.
- **Auditoria**: a tentativa de entrega precisa registrar qual provedor foi usado e o que ele respondeu.
- **Simplicidade na v1**: não pagar agora por failover multi-provedor.

## Opções consideradas

1. **Port único `IChannelProvider` com adapters por provedor, seleção por configuração** (escolhida).
2. Chamadas diretas aos SDKs dos provedores dentro dos dispatchers, sem abstração.
3. Abstração por canal (`IEmailSender`, `ISmsSender`, ...) com um provedor fixo por canal em código.
4. Plataforma de entrega de terceiros (ex.: gateway unificado) na frente dos provedores.

## Decisão

Adotar a opção 1. Um único contrato:

```csharp
public interface IChannelProvider
{
    Channel Channel { get; }
    string ProviderKey { get; }          // "sendgrid", "twilio-sms", "twilio-whatsapp", "fcm"
    Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken ct);
}

public sealed record DispatchRequest(DeliveryTarget Target, RenderedMessage Message);

public sealed record ProviderResult(
    ProviderOutcome Outcome,             // Accepted | Rejected | Throttled | TransientError
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage,                // texto do provedor após sanitização, sem dados pessoais
    TimeSpan? RetryAfter);
```

A fonte normativa do contrato é `Modules/Dispatch/Integration/V1`; os trechos aqui são ilustrativos. O destino viaja em `DeliveryTarget`, separado do conteúdo renderizado: o modelo de dados guarda contato e conteúdo em colunas distintas do attempt, e a fronteira de PII impede endereço ou token dentro do conteúdo auditado por hash.

`RenderedMessage` é uma hierarquia discriminada por canal; cada adapter recebe o tipo do seu canal:

```csharp
public abstract record RenderedMessage;

public sealed record EmailMessage(string Subject, string Preheader, string HtmlBody, string TextBody) : RenderedMessage;
public sealed record SmsMessage(string Body) : RenderedMessage;
public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> DataPayload) : RenderedMessage;
public sealed record WhatsAppMessage(string ContentSid, IReadOnlyDictionary<string, string> ContentVariables) : RenderedMessage;
```

- Cada provedor é um adapter fino que traduz `DispatchRequest` → chamada ao fornecedor → `ProviderResult` normalizado. O adapter não conhece política, fallback, retry nem auditoria. Mapeamento canônico de limitação: 429 e códigos de quota viram `Throttled`, com `RetryAfter` propagado quando o provedor nomear a espera; rejeição permanente fica reservada a erros que invalidam a mensagem ou o destino.
- A seleção do provedor por canal e `application` vem de `PROVIDER_CONFIG` (gerida por Terraform, auditada), com campo `priority` já previsto para failover futuro.
- Circuit breaker, rate limit e concorrência são configurados **por `ProviderKey`**, fora do adapter.
- O `ProviderKey` e o `ProviderMessageId` são gravados em `NOTIFICATION_ATTEMPT`; webhooks são correlacionados por eles.
- Na v1, failover entre provedores do mesmo canal **não** é implementado; o que existe é fallback entre canais (ADR-0011). A estrutura permite adicionar failover sem mudar o contrato.

### Consequências

**Positivas**
- Trocar Twilio por outro fornecedor de SMS = um adapter novo + uma linha em `PROVIDER_CONFIG`; nenhum produtor, template ou política muda.
- Inventário de terceiros sai da configuração, não de uma planilha.
- Testes de contrato por adapter (respostas de sucesso, rejeição, throttling, erro transitório) com *fakes* dos fornecedores.

**Negativas**
- Um adapter a mais por provedor, com a obrigação de manter paridade de semântica (`Rejected` vs. `TransientError`) entre fornecedores diferentes.
- O menor denominador comum do contrato pode esconder recursos específicos de um provedor (ex.: *scheduling* nativo da Twilio). Aceito: esses recursos ficam fora do contrato até que haja caso real.
- Concentração Twilio/SendGrid permanece um risco aceito na v1 (§16, risco 2).

## Prós e contras das opções

### Opção 1 — Port único + adapters + configuração
- Prós: reversibilidade; testabilidade; inventário; failover adicionável.
- Contras: camada a mais; semântica normalizada.

### Opção 2 — SDKs diretos nos dispatchers
- Prós: menos código hoje.
- Contras: cada troca de fornecedor é refatoração; impossível testar sem o fornecedor; lógica de retry e auditoria se mistura com detalhes do SDK.

### Opção 3 — Abstração por canal com provedor fixo
- Prós: interfaces mais ricas por canal.
- Contras: quatro contratos para manter; failover exige mudar cada um; o "fixo em código" transforma decisão comercial em deploy.

### Opção 4 — Gateway unificado de terceiros
- Prós: um único SDK.
- Contras: mais um terceiro na cadeia regulatória; custo por mensagem; a camada de política e auditoria (a parte regulada) continuaria sendo nossa de qualquer forma (ADR-0009).

## Como saberemos que foi a decisão certa

- A primeira troca ou adição de provedor leva menos de uma sprint e não toca produtores, templates nem políticas.
- Nenhum incidente de provedor derruba mais de um canal.

## Referências

- Design de Sistema — §4.3 Dispatchers, §8 Confiabilidade, §16 riscos 2 e 3.
- Res. CMN 4.893/2021 — requisitos de contratação de serviços relevantes de terceiros.
