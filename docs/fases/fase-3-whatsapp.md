---
language: pt-BR
---

# Design técnico da fase 3: canal WhatsApp

| Campo | Valor |
|---|---|
| **Tipo** | technical-design |
| **Status** | DRAFT |
| **Público** | Engenharia do hub; Produto e Compliance como leitores das regras de canal e consentimento |
| **Propósito** | Ancorar o escopo e o desenho da fase 3 do roadmap para o kickoff da fase |
| **Escopo** | Habilitar WhatsApp como canal de saída do hub via Twilio |
| **Documento-mãe** | [Design de Sistema](../notification-hub-system-design.md), §15 (linha da fase 3) |
| **Decisões governantes** | [ADR-0001](../ADR-0001-canal-e-provedor-como-plugin.md); [ADR-0012](../ADR-0012-contact-consent-fonte-da-verdade.md) |

## Objetivo e contexto

A fase 3 do roadmap (Design de Sistema, §15) habilita o WhatsApp como canal de saída do hub, com cinco entregas: adapter Twilio WhatsApp; submissão de Content template pela API de gestão com sincronização de status da Meta; opt-in; processamento de `SAIR`; template `authentication` para OTP.

Na taxonomia (§3), o WhatsApp é elegível para `critical` com categoria `authentication` e para `transactional` com categoria `utility`. A moldura regulatória (§2.3) impõe as regras do canal: mensagem iniciada pela empresa exige template aprovado pela Meta, submetido via Twilio Content API e categorizado (`authentication` ou `utility`); opt-in explícito documentado; precificação da Meta tratada como configuração; conta WhatsApp Business verificada.

Este documento ancora o escopo da fase e o desenho já registrado nas fontes governantes. O consumidor imediato é o kickoff da fase 3, que fará a decomposição fina em backlog; este documento não a antecipa.

## Arquitetura e desenho por entrega

A fase não introduz decisão arquitetural nova: ela materializa decisões já registradas (ADR-0001 para o adapter; ADR-0012 para consentimento; §4.3 para gestão de templates). Qualquer desvio dessas decisões exige nova ADR; nenhum desvio é introduzido aqui.

### Entrega 1: submissão de Content template e sincronização de status da Meta

- O conteúdo WhatsApp de uma versão de template é `ContentSid` Twilio/Meta com mapeamento de variáveis (§4.3, tabela do modelo de templates).
- A submissão parte da API de gestão pela rota `POST /v1/templates/{key}/versions/{v}/whatsapp-submissions` com `locale` no corpo, prevista no contrato (§7.4). Um job sincroniza `meta_approval_status`; a versão não pode ser publicada até `approved`; rejeições da Meta aparecem na versão com o motivo (§4.3, "WhatsApp").
- A validação automática integral ganha as verificações de WhatsApp (§4.3, tabela de validação): número e ordem de variáveis iguais ao Content template e status Meta `approved`.
- A submissão registra o Content template na Meta e não envia mensagem a destinatário (§10.2, controle A12).
- Estado real no código: a rota ainda não existe no módulo `TemplateManagement`. O inventário de endpoints do módulo cobre autoria, validação, render, publicação, ciclo de vida, layouts e políticas, sem `whatsapp-submissions`; a única referência a WhatsApp é o valor canônico de canal em `src/Platform.Api/Modules/TemplateManagement/Integration/V1/Channel.cs:12`. Busca por `ContentSid`, `submission` e `meta_approval` em `src/` não retorna ocorrências. O endpoint de conteúdo já aceita o canal `whatsapp` porque o conjunto canônico `Channel.All` o inclui (`Channel.cs:18`).

### Entrega 2: adapter Twilio WhatsApp

- Um adapter atrás do contrato único `IChannelProvider` (ADR-0001), com `ProviderKey` `"twilio-whatsapp"`, mesmo adapter base do SMS (§4.3, tabela de dispatchers).
- A hierarquia `RenderedMessage` ganha `WhatsAppMessage(contentSid, contentVariables)` nesta fase, conforme a hierarquia discriminada por canal declarada em §4.3 e na ADR-0001. O envio usa `ContentSid` e `ContentVariables` de template aprovado pela Meta.
- Categoria `authentication` para OTP e `utility` para transacional; o canal disponibiliza o status `read` (§4.3).
- Circuit breaker, rate limit e concorrência configurados por `ProviderKey`, fora do adapter (ADR-0001). A topologia de mensageria já prevê as filas `dispatch-whatsapp-{class}` e `dispatch-whatsapp-auth` (§4.2).

### Entrega 3: opt-in como CONSENT

- O opt-in de WhatsApp é registrado como `CONSENT` com `channel = whatsapp` e campo `source` (app, atendimento, importação), no ledger append-only do módulo Contact & Consent (ADR-0012; §4.3).
- O estágio Policy consome o dado: `ConsentGate` rejeita canais sem opt-in (§4.3, regras da v1 em ordem fixa).
- Nenhum caminho de escrita novo: o opt-in entra pelos caminhos já definidos na ADR-0012 (REST com app role `Contacts.Write` e tópico `contacts.events.v1`), com `audit_event` na mesma transação.

### Entrega 4: processamento de SAIR como transição de consentimento auditada

- O hub trata WhatsApp apenas como saída, com captura de respostas simples `SAIR`/`STOP` (§1.3); o processamento é responsabilidade do Delivery Tracker, com SLA registrado, como materialização do direito de oposição (§10.9).
- A revogação não sobrescreve nada: o ledger de consentimento é append-only, então a transição gera novo registro de `CONSENT` com origem, timestamp e ator, e `audit_event` na mesma transação (ADR-0012).
- A mudança é publicada como evento `araia.notification.consent_changed.v1` (`recipientId`, `channel`, `purpose`, `granted`, `source`) na saída Kafka (§7.3) e invalida os caches locais dos workers via `ConsentChanged` (ADR-0012).

### Entrega 5: template `authentication` para OTP

- O template de OTP usa a categoria `authentication` da Meta, com botão de copiar código (copy code), conforme §4.3 (tabela de dispatchers).
- A classe `critical` elege push, SMS e WhatsApp com categoria `authentication` (§3); mensagens de OTP por SMS ou WhatsApp nunca contêm link (§2.3), e o validador bloqueia links em templates `critical` (§10.8).
- As filas `-auth` dedicadas isolam o OTP sob burst de `critical` (§3, regras derivadas).

## Fora de escopo

- Atendimento conversacional inbound: o hub captura apenas `SAIR`/`STOP` (§1.3).
- Marketing: fora da taxonomia da v1 (§3).
- Failover de provedor dentro do canal: fora da v1 (ADR-0001); o que existe é fallback entre canais.
- Rastreamento de clique e serviço de link assinado (§10.8).
- Envio de teste a destino real pela superfície de gestão (§10.2, controle A12).

## Dados e consentimento

| Dado | Onde vive | O que a fase acrescenta |
|---|---|---|
| Conteúdo WhatsApp da versão | Conteúdo por (canal, locale) da versão do template | `ContentSid` com mapeamento de variáveis (§4.3) |
| Status de aprovação da Meta | Versão do template | `meta_approval_status` sincronizado por job; motivo de rejeição registrado na versão (§4.3, "WhatsApp") |
| Opt-in | Ledger `CONSENT`, append-only | Registros com `channel = whatsapp` e `source` (ADR-0012) |
| Revogação por `SAIR` | Ledger `CONSENT` e `audit_event` | Novo registro de consentimento, nunca sobrescrita (ADR-0012) |

A modelagem física (colunas, índices, migração) fica para o kickoff da fase; este documento não define esquema.

## Segurança e ameaças

- Conta WhatsApp Business verificada; senders registrados por `application`; o número de OTP nunca é usado para outra finalidade (§10.8; §2.3).
- Sem encurtadores; links só em domínio próprio; OTP sem link (§2.3; §10.8).
- A submissão de Content template não é canal de envio: registra o template na Meta e nunca envia mensagem (§10.2, controle A12). A rota nova entra na superfície de gestão, que é superfície privilegiada (§16, risco 12), herdando a autorização por rota e por recurso e a auditoria transacional de §7.4.
- Consentimento demonstrável e direito de oposição: ledger append-only com origem, termo, ator e timestamp; `SAIR`/`STOP` com SLA registrado (§10.9).

## Observabilidade e operação

- O catálogo de auditoria já prevê os eventos `template.whatsapp.submitted` e `template.whatsapp.status_changed` (§9.3).
- Tentativas de entrega registram `ProviderKey` e `ProviderMessageId`; os webhooks da Twilio são correlacionados por eles no Delivery Tracker (ADR-0001; §4.3).
- Modos de falha do provedor: circuit breaker por provedor abre sob taxa de erro; a mensagem volta à fila com visibilidade estendida e o tracker dispara fallback de canal quando o plano permitir (§4.3).
- A vazão é limitada pelo MPS contratado por sender (§16, risco 22); alarme de fila e negociação de MPS com a Twilio são tratamento operacional já registrado no risco.

## Dependências

| Dependência | Por quê | Estado no momento da escrita |
|---|---|---|
| Fase 1b (Contact & Consent v1) | Opt-in e revogação por `SAIR` vivem no ledger `CONSENT` (ADR-0012) | Em andamento |
| Fase 2 (SMS via Twilio, tracker com webhooks) | Mesmo provedor e mesmo adapter base do SMS; webhooks Twilio no tracker (§4.3; §15) | Pendente |
| Aprovação de template pela Meta | Mensagem iniciada pela empresa exige template aprovado (§2.3) | Dependência externa: processo da Meta, com prazo fora do controle do time |

O estado das fases 1b e 2 é o declarado no planejamento corrente do projeto. O código corrobora: `src/Platform.Api/Modules` contém hoje os módulos `Audit`, `SharedKernel` e `TemplateManagement`; os componentes de pipeline, dispatchers e Contact & Consent ainda não existem no repositório.

## Estratégia de testes

- Testes de contrato do adapter com fakes do fornecedor, cobrindo sucesso, rejeição, throttling e erro transitório (ADR-0001).
- Testes das novas verificações de WhatsApp na validação automática integral: variáveis contra o Content template e bloqueio de publicação sem status `approved` (§4.3).
- O gate de publicação permanece: `publish` revalida e devolve o relatório `checks[]` completo (§7.4).

## Rollout e critérios de saída

- Duração registrada no roadmap: 4 semanas (§15). É a única estimativa registrada para a fase; a decomposição fina em backlog e a sequência interna das entregas ficam para o kickoff da fase, e este documento deliberadamente não as define.
- Critério de saída do roadmap (§15): template `utility` e `authentication` aprovados e em produção.
- A migração strangler por template continua valendo (§15): cada template migrado sai do código do produtor só depois de `published` no hub.
- Reversibilidade: rollback de template é republicação com quatro olhos (§4.3); a seleção de provedor por configuração (`PROVIDER_CONFIG`) mantém a troca de provedor barata (ADR-0001).

## Riscos

| Risco | Fonte | Tratamento registrado |
|---|---|---|
| Aprovação de template pela Meta é processo externo, com prazo fora do controle do time | §2.3; §4.3, "WhatsApp" | Tratado como dependência externa da fase; a versão não publica até `approved` e rejeições aparecem na versão com o motivo |
| Mudanças de política e preço da Meta | §16, risco 10 | Custo como configuração; `ContentSid` sincronizado; revisão trimestral |
| Vazão limitada pelo MPS contratado por sender | §16, risco 22 | Pool de senders por `application`; alarme de fila; negociação de MPS com a Twilio |
| Concentração Twilio e SendGrid na mesma empresa | §16, risco 2 | Risco aceito na v1; `IChannelProvider` mantém um segundo provedor barato |

## Referências

- [Design de Sistema](../notification-hub-system-design.md): §1.3, §2.3, §3, §4.2, §4.3, §7.3, §7.4, §9.3, §10.2, §10.8, §10.9, §15, §16.
- [ADR-0001: canal e provedor como plugin](../ADR-0001-canal-e-provedor-como-plugin.md).
- [ADR-0012: Contact & Consent como fonte da verdade](../ADR-0012-contact-consent-fonte-da-verdade.md).
- Código: `src/Platform.Api/Modules/TemplateManagement/Integration/V1/Channel.cs:12`; inventário de rotas do módulo `TemplateManagement` (arquivos `*.Endpoint.cs` em `src/Platform.Api/Modules/TemplateManagement/Features/`).
