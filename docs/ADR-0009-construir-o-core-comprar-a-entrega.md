# ADR-0009: Construir o core, comprar só a entrega

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Liderança de Engenharia, Compliance |
| **Consultados** | Produto, Financeiro (custo), Jurídico (terceiros) |
| **Relacionadas** | ADR-0001 (provedores), ADR-0005 (templates), ADR-0006 (auditoria) |
| **Documento-mãe** | Design de Sistema, §1, §9, §14 |

## Contexto e problema

Existem plataformas prontas de notificação, SaaS (Courier, Knock, OneSignal, Braze, Customer.io) e open source (Novu), que cobrem boa parte do escopo: multicanal, templates, preferências, tracking, fallback. A pergunta é se o hub deve ser uma dessas plataformas (com adaptações) ou um sistema próprio que usa fornecedores apenas para a entrega física das mensagens.

O diferencial do caso é regulatório: a parte que importa para o BCB e para a LGPD não é "enviar e-mail", é **decidir se podia enviar, com qual texto aprovado por quem, e provar isso por anos**.

## Fatores de decisão

- **Controle sobre a parte regulada**: política, consentimento, aprovação de texto, auditoria e reconstrução.
- **Gestão de terceiros (Res. CMN 4.893/2021)**: cada SaaS na cadeia é um terceiro relevante com contrato, localização de dados e plano de saída.
- **Minimização (LGPD)**: um SaaS de notificação precisaria receber contatos e conteúdo, exatamente o que a ADR-0004 confina ao hub.
- **Custo total**: licença por mensagem/usuário vs. equipe para construir e manter.
- **Velocidade de entrega** da v1.
- **Aderência à stack** (.NET 10, AWS, Entra, Kafka corporativo).

## Opções consideradas

1. **Construir o core (ingestão, política, consentimento, templates, auditoria, roteamento); comprar só a entrega (SendGrid, Twilio, FCM)** (escolhida).
2. SaaS completo de notificação como o hub.
3. Novu (open source) self-hosted como base, customizado.
4. Construir tudo, inclusive entrega (SMTP próprio, integrações diretas com operadoras).

## Decisão

Adotar a opção 1.

- O hub é software próprio em .NET 10 sobre a infraestrutura existente (EKS, RDS, SQS, Kafka corporativo, Entra, Terraform).
- Fornecedores de entrega ficam atrás de `IChannelProvider` (ADR-0001) e são os únicos terceiros que tocam mensagem e contato, já sob contrato com cláusulas de segurança e LGPD.
- Nenhuma plataforma de orquestração ou de templates de terceiros. Reavaliação prevista apenas se, quando uma UI de gestão de templates entrar no roadmap, o custo de construí-la e mantê-la se mostrar maior que integrar um registry open source; e mesmo assim só para a UI, nunca para política, auditoria ou consentimento.

### Consequências

**Positivas**
- Conformidade demonstrável sem depender de SaaS estrangeiro: o regulador audita o nosso banco, a nossa trilha, o nosso bucket WORM.
- PII confinada (ADR-0004); o barramento e os fornecedores recebem o mínimo.
- Integração natural com Entra, Kafka corporativo, padrões de observabilidade e Terraform já existentes.
- Custo variável limitado à entrega física.

**Negativas**
- Mais código para construir e manter: core, gestão de templates, auditoria. Estimado em ~5 meses de equipe dedicada para as fases 0 a 3.
- Funcionalidades que um SaaS traria prontas (analytics de engajamento, editor visual, A/B) ficam de fora, coerente com o escopo sem marketing.
- Risco de subestimar a UI de gestão quando entrar no roadmap; mitigado pelo fato de a v1 operar exclusivamente via API, com o OpenAPI como contrato (ADR-0005, ADR-0007).

## Prós e contras das opções

### Opção 1 — Core próprio + entrega comprada
- Prós: controle da parte regulada; minimização; aderência à stack.
- Contras: mais código.

### Opção 2 — SaaS completo
- Prós: rápido para começar; UI pronta; analytics.
- Contras: contatos, consentimento e conteúdo em terceiro (muitas vezes fora do Brasil); aprovação e auditoria nos termos do fornecedor; exportar a trilha para o regulador depende do SaaS; lock-in; custo por volume; mais um terceiro relevante na 4.893.

### Opção 3 — Novu self-hosted
- Prós: open source; evita custo por mensagem; UI pronta.
- Contras: stack Node/Mongo fora do padrão; modelo de aprovação e auditoria insuficiente para o requisito (§9), seria preciso modificar o produto; dependência de roadmap externo; curva de customização semelhante a construir.

### Opção 4 — Construir inclusive a entrega
- Prós: nenhum terceiro.
- Contras: reputação de e-mail, relação com operadoras e WhatsApp Business exigem escala e expertise que não são o negócio da ARAIA.

## Como saberemos que foi a decisão certa

- Fases 0 a 3 entregues dentro de ±30 % da estimativa.
- Auditoria interna ou do regulador atendida integralmente com artefatos do hub, sem solicitar nada a fornecedores além do contrato.
- Custo por notificação (infra + equipe rateada) medido trimestralmente e comparado a cotações atualizadas de pelo menos dois SaaS em volume equivalente; a baseline de cotações datada é anexada à ADR na aceitação.

## Referências

- Design de Sistema — §1, §9, §15 roadmap.
- Res. CMN 4.893/2021 — contratação de serviços relevantes de processamento e armazenamento de dados.
