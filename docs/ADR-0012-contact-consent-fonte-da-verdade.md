---
language: pt-BR
---

# ADR-0012: Contact & Consent: hub como fonte da verdade com ingestão dedicada

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Segurança da Informação, DPO |
| **Consultados** | Engenharia do sistema de cadastro, Compliance |
| **Relacionadas** | ADR-0004 (resolução de contato), ADR-0006 (auditoria), ADR-0010 (Kafka) |
| **Documento-mãe** | Design de Sistema, §4.3 "Contact & Consent" |

## Contexto e problema

A ADR-0004 definiu que o produtor envia apenas `recipient_id` e o hub resolve contato e consentimento internamente, mas deixou em aberto onde esse dado vive e como chega até o hub. Sem essa definição, faltam respostas para perguntas operacionais concretas: quem guarda o fuso horário que alimenta `quietHours`; como device tokens são registrados e invalidados; por onde entra o opt-in de WhatsApp; e qual é o modo degradado quando o dado não está disponível. Além disso, a linguagem anterior ("Contact & Consent Service") sugeria uma dependência remota, com timeout de rede, que nunca foi a intenção da v1.

## Fatores de decisão

- **Disponibilidade do OTP**: a resolução no caminho quente não pode depender da disponibilidade de outro sistema.
- **Latência do estágio *Resolve***: dentro do orçamento de §11.2.
- **Minimização e auditoria LGPD** (continuidade da ADR-0004): um único ponto que decide "podia enviar?" e registra por quê, na mesma transação.
- **Atualidade do dado**: device tokens expiram e são invalidados pelo provedor; consentimento muda por origens diferentes (app, atendimento, importação).
- **Custo operacional**: sincronização com o cadastro e propriedade do dado precisam de dono explícito.

## Opções consideradas

1. **Módulo interno do hub como fonte da verdade, com ingestão dedicada** (escolhida).
2. Consulta síncrona ao cadastro a cada envio.
3. Réplica local alimentada por CDC do banco do cadastro.
4. Serviço separado de Contact & Consent.

## Decisão

Adotar a opção 1.

**Topologia.** Contact & Consent é módulo interno do hub: mesmo processo, mesmo Postgres. Não existe serviço remoto separado na v1. O modo degradado é cache *stale-while-revalidate* sobre consulta local; não há timeout de rede envolvido na resolução.

**Modelo.** A fonte da verdade são as tabelas do hub:

- `RECIPIENT_PROFILE(recipient_id PK, timezone IANA, locale, created_at, updated_at)`: fuso ausente assume `America/Sao_Paulo`; este é o dado que alimenta `quietHours`.
- `CONTACT_POINT` e `CONSENT`: pontos de contato verificados e consentimentos vigentes por canal e finalidade.
- `DEVICE_TOKEN(id, recipient_id, token, platform, app_version, registered_at, last_seen_at, invalidated_at)`: invalidação quando o FCM responde `UNREGISTERED` ou `INVALID_ARGUMENT`.

**Ingestão (caminhos de escrita).**

- REST: `PUT /v1/recipients/{id}/contact-points`, `PUT /v1/recipients/{id}/consents` e `POST /v1/recipients/{id}/devices` (registro de device token). Autorização por app role dedicada `Contacts.Write` (roles de client credentials, o mecanismo de autorização do hub); toda escrita gera `audit_event` na mesma transação (ADR-0006).
- Kafka: tópico `contacts.events.v1` (CloudEvents, emissor: sistema de cadastro), no mesmo padrão at-least-once do ingress, com dedupe em `processed_messages` (ADR-0010).
- Opt-in de WhatsApp: registrado como `CONSENT` com `channel=whatsapp` e campo `source` (app, atendimento, importação).

**Invalidação de cache.** Os eventos `ContactChanged` e `ConsentChanged` são emitidos pelo próprio módulo via outbox e consumidos pelos caches locais dos workers.

### Consequências

**Positivas**
- O caminho quente do OTP não depende de nenhum sistema externo: a resolução é uma consulta local com cache.
- O ponto único de decisão e trilha da ADR-0004 ganha materialização concreta: dado, decisão e `audit_event` no mesmo banco e na mesma transação.
- `quietHours` passa a ter fonte definida de fuso (perfil do destinatário, default `America/Sao_Paulo`).
- O ciclo de vida do push fecha: registro do device token pelo app e invalidação pelos códigos do FCM vivem no mesmo modelo.

**Negativas**
- O hub assume a guarda de dado cadastral e a responsabilidade LGPD associada (retenção, controle de acesso, resposta a incidentes sobre contato e consentimento).
- A sincronização com o sistema de cadastro vira responsabilidade operacional do hub: atraso ou falha na ingestão de `contacts.events.v1` produz dado desatualizado e exige monitoramento próprio.
- O escopo do banco do hub cresce: tabelas cadastrais passam a conviver com as operacionais e entram no perímetro de backup, DR e controle de acesso.

## Prós e contras das opções

### Opção 1: módulo interno com ingestão dedicada
- Prós: sem dependência remota no caminho quente; latência de consulta local; escrita, decisão e auditoria na mesma transação.
- Contras: o hub vira dono de dado cadastral; a sincronização com o cadastro é operação contínua, não um contrato pontual.

### Opção 2: consulta síncrona ao cadastro a cada envio
- Prós: nenhuma cópia de dado; sempre o valor corrente do cadastro.
- Contras: latência e disponibilidade do cadastro entram no caminho do OTP; o modo degradado voltaria a ser cache sobre chamada remota, com timeout de rede; reconstruir "podia enviar?" em auditoria dependeria do histórico de um sistema externo.

### Opção 3: réplica local via CDC do banco do cadastro
- Prós: leitura local sem novo contrato de API com o cadastro.
- Contras: acopla o hub ao esquema interno do banco do cadastro (mudança de esquema quebra a réplica sem aviso); parte do dado não nasce no cadastro (device token registrado pelo app; opt-in de WhatsApp com `source` atendimento ou importação), então o CDC não cobre todos os caminhos de escrita; a escrita não passa pela transação de auditoria do hub.

### Opção 4: serviço separado de Contact & Consent
- Prós: dono explícito e reutilização por outros sistemas desde o início.
- Contras: reintroduz no caminho quente a dependência remota que esta decisão elimina; mais um serviço para operar na v1; é prematuro: a fronteira de módulo preserva a extração futura, sem pagar o custo agora (evolução já apontada na ADR-0004, que exige dono).

## Como saberemos que foi a decisão certa

- Zero notificações da fase 1b rejeitadas com `no-valid-contact` para destinatário que possui contato válido no cadastro (confronto por amostragem entre as rejeições e a base do cadastro).
- Latência do estágio *Resolve* dentro do orçamento de §11.2, verificada no teste de carga.
- A auditoria de consentimento passa em amostragem trimestral: cada envio amostrado tem `CONSENT` vigente registrado antes do envio, com origem (`source`) rastreável.

## Referências

- Design de Sistema, §4.3 "Contact & Consent", §11.2.
- ADR-0004 (resolução de contato dentro do hub), ADR-0006 (auditoria), ADR-0010 (Kafka).
