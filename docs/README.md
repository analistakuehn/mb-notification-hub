---
language: pt-BR
---

# Notification Hub: registro de decisões de arquitetura (ADRs)

Formato: MADR (Markdown Architectural Decision Records), em pt-BR. Uma decisão por arquivo, numeração sequencial, nunca reescrita após aceita: mudanças viram nova ADR que substitui a anterior.

Documento-mãe: *Notification Hub, design de sistema*.

| ADR | Título | Status | Substitui / Relacionadas |
|---|---|---|---|
| [0001](ADR-0001-canal-e-provedor-como-plugin.md) | Canal e provedor como plugin | Proposta | 0009, 0010, 0011 |
| [0002](ADR-0002-sqs-sdk-direto.md) | SQS com SDK direto da AWS | Proposta | 0003, 0008, 0010 |
| [0003](ADR-0003-pipeline-de-estagios.md) | Pipeline de estágios com resultado explícito | Proposta | 0002, 0006, 0011 |
| [0004](ADR-0004-resolucao-de-contato-dentro-do-hub.md) | Resolução de contato dentro do hub | Proposta | 0006, 0010 |
| [0005](ADR-0005-templates-como-dados-geridos-pelo-hub.md) | Templates, layouts e políticas como dados geridos pelo hub | Proposta | 0006, 0007, 0011 |
| [0006](ADR-0006-auditoria-append-only-hash-chain-worm.md) | Auditoria em banco, append-only, com hash chain e export WORM | Proposta | 0003, 0004, 0005, 0007 |
| [0007](ADR-0007-superficie-http-unica-minimal-apis.md) | Uma única superfície HTTP: minimal APIs REST | Proposta | 0005, 0006, 0010 |
| [0008](ADR-0008-at-least-once-com-idempotencia.md) | Entrega at-least-once com idempotência | Proposta | 0002, 0006, 0010 |
| [0009](ADR-0009-construir-o-core-comprar-a-entrega.md) | Construir o core, comprar só a entrega | Proposta | 0001, 0005, 0006 |
| [0010](ADR-0010-kafka-integracao-sqs-filas-internas.md) | Kafka para integração, SQS para filas de trabalho internas | Proposta | 0002, 0004, 0008 |
| [0011](ADR-0011-politica-como-configuracao-de-classe.md) | Política como configuração de classe | Proposta | 0003, 0005, 0006 |
| [0012](ADR-0012-contact-consent-fonte-da-verdade.md) | Contact & Consent: hub como fonte da verdade com ingestão dedicada | Proposta | 0004, 0006, 0010 |
| [0013](ADR-0013-scriban-engine-de-templates.md) | Scriban como engine de templates | Proposta | 0005 |
| [0014](ADR-0014-confirmacao-de-entrega-e-gatilhos-de-fallback.md) | Confirmação de entrega e convivência dos gatilhos de fallback | Proposta | 0008, 0011 |
| [0015](ADR-0015-regra-de-supressao-no-estagio-policy.md) | Regra de supressão no estágio Policy | Proposta | 0011, 0012 |
| [0016](ADR-0016-corpo-do-callback-armazenado-uma-vez.md) | Corpo do callback armazenado uma vez e referenciado pela evidência | Aceita | 0006, 0014 |
| [0017](ADR-0017-formatacao-de-saida-invariante-e-imposta.md) | Formatação de saída invariante e imposta | Aceita | 0005, 0013 |

## Convenções

- **Status**: Proposta → Aceita → (Substituída por ADR-NNNN | Depreciada).
- A seção *Como saberemos que foi a decisão certa* define critérios verificáveis; devem ser revisitados na retrospectiva de cada fase do roadmap.
- Decisões que exigem deploy por escolha (ex.: tipo de regra novo na ADR-0011) geram ADR curta própria quando acontecerem.

## Documentos de fase

Planos completos das fases pendentes do roadmap (§15 do documento-mãe), com evidência citada e estado real do repositório na data de autoria. A fase 1a foi concluída e vive no histórico do repositório.

- [Fase 0: Governança](fases/fase-0-governanca.md)
- [Fase 1b: Fundação](fases/fase-1b-fundacao.md)
- [Fase 2: Resiliência e SMS](fases/fase-2-resiliencia-e-sms.md), decomposta em fatias de implementação em [Fase 2: decomposição](fases/fase-2-decomposicao.md), que carrega o status por fatia
- [Fase 3: WhatsApp](fases/fase-3-whatsapp.md)

## Guias

Documentos voltados a públicos externos ao time do hub, escritos a partir do comportamento implementado e não do desenho pretendido.

- [Guia de integração do produtor](guia-integracao-produtor.md): para times que vão pedir notificações ao hub por REST ou pelo barramento.

## Próximas ADRs previstas

- Cluster Kafka: MSK ou outro (provider Terraform, mecanismo de ACL/IAM, existência de Schema Registry).
- Retenção de auditoria por classe (depende de Jurídico/Compliance).
- Failover de provedor por canal (quando houver segundo provedor contratado).
- Nível 2 de política (condições por expressão), quando o critério de "duas ocorrências" for atingido.
- Serviço de link assinado `/l/{token}` (quando rastreamento de clique ou URL opaca for requisito), com modelo de ameaças próprio (enumeração de token, open redirect, PII em telemetria).
- Envelope de cifra para variáveis sensíveis no barramento (formato, distribuição de chave pública, rotação KMS).
