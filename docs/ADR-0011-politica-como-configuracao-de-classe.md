# ADR-0011: Política como configuração de classe

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Produto, Compliance |
| **Consultados** | Engenharia de Plataforma |
| **Relacionadas** | ADR-0003 (estágio Policy), ADR-0005 (workflow de aprovação), ADR-0006 (auditoria) |
| **Documento-mãe** | Design de Sistema, §3, §4.3 "Políticas — configuração de classe", §7.4, §9.2 |

## Contexto e problema

Uma política é o conjunto de regras que o estágio *Policy* aplica a toda solicitação de uma classe, independentemente do template: quais canais podem ser usados, em que ordem, quanto esperar antes do fallback, qual TTL padrão, como deduplicar, se há janela de silêncio, qual consentimento consultar. É transversal (uma regra vale para dezenas de templates), é decisão de Produto/Compliance (custo, fraude, base legal LGPD) e precisa ficar registrada na notificação (`policy_version`) para a auditoria responder "por que esse canal".

A discussão percorreu os dois extremos: tudo em código (mudar um timeout é deploy e passa por quem não decide) e engine de regras genérica (zero deploy, mas validação, teste e auditoria muito piores, e uma linguagem editável por UI numa instituição regulada). A pergunta decisiva foi: **toda política nova exige código e deploy?** A resposta ("depende de ser valor de regra conhecida ou tipo de regra novo") definiu o desenho.

## Fatores de decisão

- **Quem decide deve poder mudar** (valores) sem engenharia.
- **Auditoria legível**: "por que SMS" respondido em termos que Compliance entende.
- **Simplicidade na v1**: não construir simulador, expressões ou engine sem demanda.
- **Evolução sem redesenho**: quando a demanda vier, adicionar código ao estágio Policy e à API, não migrar modelo ou auditoria.
- **Fronteira explícita** entre dado e código, decidida agora.

## Opções consideradas

1. **Configuração de classe mínima (seis campos) + cinco pontos de extensão + roteiro em níveis** (escolhida).
2. Vocabulário maior desde a v1, com condições por expressão e `simulate`.
3. Regras em código (constantes/config por classe no hub).
4. Engine de regras genérica ou DSL própria.
5. Override de política por template.

## Decisão

Adotar a opção 1.

**v1: o que existe.** Um registro por `(application, class)`, seis campos tipados, editado via API REST e publicado com o mesmo fluxo essencial dos templates (validação automática e quatro olhos: quem publica não é o autor da versão), tudo auditado:

```json
{
  "schemaVersion": 1,
  "channelsAllowed": ["push", "sms", "whatsapp"],
  "deliveryPlan": [ { "channel": "push", "timeout": "30s" }, { "channel": "sms" } ],
  "defaultTtl": "300s",
  "dedupeWindow": "60s",
  "quietHours": null,
  "consentPurpose": null
}
```

Opt-out deriva da classe em código. Sem condições, sem override por template, sem `simulate`.

**Regras da v1, em ordem fixa.** O estágio *Policy* aplica cinco regras concretas: 1. `ConsentGate` (rejeita canais sem opt-in; marketing exige opt-in explícito); 2. `QuietHours` (defer para classes que não sejam `critical`/auth, no fuso de `RECIPIENT_PROFILE`); 3. `DedupeWindow`; 4. `RecipientRateLimit`; 5. `ChannelSelection` (aplica `deliveryPlan` + `channelsHint`).

**Composição.** Cada regra recebe o conjunto de canais remanescente; `FilterChannels` é interseção; o primeiro `Reject` ou `Defer` encerra o pipeline de política; o resultado de cada regra é auditado. `channelsHint` reordena a preferência dentro dos canais permitidos pela política, nunca adiciona canal, e é registrado em auditoria. A barreira atômica do `DedupeWindow` é Redis `SET NX` com TTL da janela sobre `(application, templateKey, recipientId)`; em falha do Redis, fail-open (duplicata possível, risco aceito e auditado).

**Pontos de extensão fixados agora** (baratos hoje, caros depois):
1. `policy_version` na notificação e `POLICY_EVALUATION` regra a regra.
2. Estágio Policy como lista ordenada de `IPolicyRule`: regra nova é uma classe registrada na lista.
3. Definição JSON com `schemaVersion` e leitor tolerante a campos adicionais.
4. Passos do `deliveryPlan` como objetos (um `when` entra como propriedade opcional).
5. Versionamento e aprovação reaproveitados do workflow de templates.

**Roteiro.** Nível 2: `rejectWhen[]`/`deferWhen[]` e `when` nos passos como condições por expressão (Scriban em sandbox) sobre contexto tipado e versionado; `simulate` e casos de teste de política. Nível 3: tipos de regra novos conforme evidência, cada um como `IPolicyRule` com ADR curta. Fora de plano: override por template, engine genérica.

**Fronteira dado × código.** Mudar valor de qualquer dos seis campos: nunca é deploy. Tipo de regra novo, campo novo no schema, avaliador de expressão, classe nova: deploy, por decisão. Critério para subir de nível: necessidade concreta que apareceu **duas vezes**.

### Consequências

**Positivas**
- v1 entrega com o mínimo e sem dívida estrutural.
- Mudanças frequentes (timeouts, canais, janelas) são dado aprovado, com trilha.
- A auditoria já responde "por que esse canal" com `policy_version` + avaliação regra a regra.
- Subir de nível é adicionar código isolado, não redesenhar.

**Negativas**
- Até o nível 2, qualquer "e se" que não caiba nos seis campos vira PR. Adequado enquanto não houver evidência de frequência.
- Risco de os seis campos serem insuficientes cedo; mitigado por validar o vocabulário com Produto/Compliance antes da fase 1a.
- Ao entrar o nível 2, condições por expressão podem crescer até virar lógica ilegível; mitigações já desenhadas (contexto limitado, casos de teste obrigatórios, limite de complexidade, leitura em linguagem natural para Compliance).

## Prós e contras das opções

### Opção 1 — Mínimo + pontos de extensão
- Prós: simples agora, evolutivo depois, fronteira explícita.
- Contras: PR para o que não cabe nos seis campos.

### Opção 2 — Vocabulário amplo + expressões na v1
- Prós: menos deploys futuros.
- Contras: construir simulador, contexto versionado e avaliador sem demanda; mais superfície a validar e auditar desde o dia 1.

### Opção 3 — Regras em código
- Prós: máxima simplicidade técnica.
- Contras: mudar timeout é deploy aprovado por engenharia; trilha de "quem decidiu" no Git, fora da auditoria; decisão de Produto/Compliance sem autonomia.

### Opção 4 — Engine genérica / DSL
- Prós: quase zero deploy.
- Contras: validação de regra arbitrária antes de publicar é difícil; exige simulador; auditoria vira "regra 47 avaliou verdadeiro"; superfície de ataque editável por UI.

### Opção 5 — Override por template
- Prós: flexibilidade pontual.
- Contras: sem caso real; dono do texto ganharia poder sobre canal e custo; adiciona-se com evidência.

## Como saberemos que foi a decisão certa

- Nenhuma mudança de valor de política exigiu deploy nos primeiros 6 meses.
- Zero ou um tipo de regra novo nos primeiros 6 meses (se forem vários, o vocabulário v1 estava errado: revisar).
- `GET /v1/audit/notifications/{id}` responde "por que esse canal" de forma que Compliance aceita sem tradução.

## Referências

- Design de Sistema — §3, §4.3 "Políticas", §6 (`CLASS_POLICY_VERSION`, `POLICY_EVALUATION`), §7.4, §9.2, §16 riscos 20–21.
