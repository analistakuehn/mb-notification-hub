---
language: pt-BR
---

# ADR-0005: Templates, layouts e políticas como dados geridos pelo hub, com workflow próprio

| | |
|---|---|
| **Status** | Proposta |
| **Data** | 2026-08-22 |
| **Decisores** | Arquitetura, Compliance, Produto |
| **Consultados** | Engenharia de Plataforma, Segurança da Informação |
| **Relacionadas** | ADR-0006 (auditoria), ADR-0007 (API REST), ADR-0011 (política), ADR-0013 (Scriban como engine de templates) |
| **Documento-mãe** | Design de Sistema, §4.3 "Template Management", §7.4, §9.2 |

## Contexto e problema

Todo texto que o hub envia ao cliente é, para uma instituição financeira, comunicação oficial: tem classe, base legal LGPD, regras antiphishing e precisa ser reconstruído exatamente como foi enviado, anos depois, com a prova de quem o aprovou.

Três modelos foram considerados ao longo do desenho:

- **(a) Texto embutido no código** dos produtores ou do hub (o estado atual dos serviços).
- **(b) Templates como código** em repositório Git, com CODEOWNERS exigindo aprovação de Engenharia e Compliance e publicação por pipeline.
- **(c) Templates como dados geridos pelo hub**, com ciclo de vida, validação e auditoria internas.

O time decidiu que **nenhum template pode existir em código**. Dentro do modelo (c), restava dimensionar a máquina de gestão: quanto de UI, workflow de aprovação e promoção a v1 precisa antes do primeiro template em produção.

## Fatores de decisão

- **Quem edita**: os donos do texto são Produto e Compliance, não Engenharia; depender de PR os torna dependentes de engenheiros.
- **Integridade da aprovação**: aprovar um texto e publicar outro deve ser impossível.
- **Validação única**: a mesma validação que roda antes de publicar deve ser a que o runtime usa para renderizar.
- **Auditoria transacional**: cada transição registrada na mesma transação, com identidade Entra.
- **Reprodutibilidade**: dado `notification_id`, reconstruir texto, layout e quem aprovou.
- **Tempo de mudança**: corrigir um texto em produção não pode exigir release.
- **Simplicidade na v1**: não construir UI, fluxo formal de review nem promoção sem demanda; a máquina de gestão não pode custar mais que o core que ela governa.

## Opções consideradas

1. **(c) Dados no hub com gestão essencial via API e pontos de extensão nomeados** (escolhida).
2. (c) Dados no hub com gestão completa já na v1: Template Studio, aprovação dupla e promoção entre ambientes.
3. (b) Templates como código em Git + CODEOWNERS + pipeline.
4. (a) Texto em código.
5. Plataforma SaaS de templates (Novu, Courier, Knock) como registry.

## Decisão

Adotar a opção 1: templates, layouts e políticas são dados geridos pelo hub, nunca código. A v1 contém somente a gestão essencial, com pontos de extensão definidos agora, no mesmo padrão da ADR-0011.

**v1: o que existe.**

- **Modelo.** `TEMPLATE` (metadados mínimos: classe, `purpose`, owner, base legal, `links_allowed`, variáveis sensíveis) → `TEMPLATE_VERSION` (imutável após publicada, com `variables_schema`, `change_note` opcional, `layout_version` fixada e `content_hash` agregado) → `TEMPLATE_CONTENT` por (canal, locale), com a cadeia de fallback de locales já definida. Layouts versionados como templates. Renderiza somente a versão publicada.
- **Gestão exclusivamente via REST** na superfície única (ADR-0007): `/v1/templates/*` e `/v1/layouts/*`. Rascunho editado com `PUT` idempotente e `ETag`/`If-Match`.
- **Ciclo de vida mínimo.** Versão: `draft → published`, imutável após publicada; rollback é republicar uma versão anterior. Template: `active | deprecated | disabled`; `deprecated` e `disabled` rejeitam novas solicitações, com os motivos `template-deprecated` e `template-disabled` do catálogo de §7.3.
- **Quatro olhos no publish.** Quem publica não pode ser o autor da versão (autorização por recurso, ADR-0007). O publish registra `approval` sobre o `content_hash` da versão e o `audit_event` na mesma transação (ADR-0006).
- **Validação automática integral** no `publish` e no `validate`, controle de segurança que o corte não reduz: compilação Scriban em sandbox com limites (ADR-0013), variáveis tipadas declaradas (variáveis ⊆ schema), allowlist de domínios de URL por template, `sensitive_variables` só via máscara, limites de tamanho e de canal, paridade com o Content template da Meta para WhatsApp, completude de locales; relatório completo em `checks[]`.
- **Render de teste por API** (preview, sem envio): `POST /v1/templates/{key}/versions/{n}/render` com variáveis de exemplo.
- **WhatsApp.** Sincronização com os Content templates da Meta permanece (exigência do canal).

**v1: o que não existe (pontos de extensão nomeados).**

- **Template Studio.** A v1 opera via API; o contrato para qualquer cliente administrativo futuro é o documento OpenAPI que a própria API serve em `GET /openapi/v1.json`, autenticado e disponível em todos os ambientes (ADR-0007, errata de 2026-08-26). O Studio e seu cliente TypeScript gerado são itens de roadmap, não da v1.
- **Aprovação dupla e fluxo formal de review** (`/submit`, `/reviews`, diff obrigatório, evento `review.diff_viewed`). Ponto de extensão: exigência de aprovação dupla por classe, ativável quando Compliance exigir, reaproveitando o registro `approval` existente.
- **Promoção entre ambientes.** Cada ambiente publica via seu pipeline, pela API; não há mecanismo de promoção.
- **Envio de teste** para destino real e casos de teste salvos com o template.
- **`review_due`** (revisão periódica de template).

**Critério de retorno.** Um corte volta ao escopo quando a necessidade concreta aparecer **duas vezes** ou, no caso da aprovação dupla por classe, quando Compliance exigir.

**Fronteira que não muda.** Não existe template em código, nem no hub (fixtures só em projetos de teste), e não existe outro caminho de edição além da API.

### Errata de 2026-08-31: as variáveis sensíveis são da versão, e a promessa de validação integral está por metade

Três itens de decisão acima falam da lista de variáveis sensíveis. Um é superseção parcial, um não muda e é justamente o que transforma a lacuna em violação de contrato, e um prometia mais do que o código entrega.

**Modelo, superseção parcial.** O item de modelo lista as variáveis sensíveis entre os metadados mínimos do `TEMPLATE`. Elas saem de lá e passam ao `TEMPLATE_VERSION`, entrando no `content_hash`. O motivo é que o dado é objeto de aprovação e a máquina de aprovação é toda de versão: quatro olhos, hash, `approval` e imutabilidade após publicada existem no `TEMPLATE_VERSION` e não existem no `TEMPLATE`. Enquanto a lista morava na identidade, quem criava o template a gravava sozinho, na criação, e nenhum mutador a alcançava depois. Uma lista errada não tinha ato que a corrigisse.

**Quatro olhos no publish, sem alteração.** O item que promete `approval` sobre o `content_hash` da versão permanece exatamente como está, e é ele que qualifica o que havia. Foi medido que o `ContentHash` de uma versão era idêntico para `["cpf"]` e para `[]`: a lista não entrava na forma canônica, logo o `approval` registrado não cobria nada do que ela dizia. A ADR prometia aprovação sobre o conteúdo e a lista viajava fora dele. Isso é violação de contrato, não lacuna de escopo, e a superseção acima é o que passa a cumprir a promessa que este item sempre fez.

**Validação integral, promessa por metade.** O item de validação diz que `sensitive_variables` é validada "só via máscara". A implementação entrega `lista ⊆ schema`: cada nome declarado precisa resolver por um caminho que o schema descreve, ou a máscara nunca o alcançaria. É uma metade, e é satisfazível de modo vazio: foi medido que a checagem passa com uma lista de dois nomes declarados sobre um conteúdo que não lê variável nenhuma. A metade que falta é `sensível-de-fato ⊆ lista`, isto é, garantir que todo valor sensível que o conteúdo carrega esteja declarado.

Essa metade não é fechável por código. Detectar dado pessoal por padrão de conteúdo foi medido e recusado em decisão anterior, e essa recusa permanece: fechar por adivinhação de conteúdo produz falso negativo silencioso, que é exatamente o modo de falha que esta lista existe para evitar. O que substitui a detecção é ato humano sob quatro olhos: a lista agora é aprovada por uma segunda pessoa, junto com o conteúdo e o schema que ela descreve, no mesmo `content_hash`.

**Superfície e persistência.** A declaração deixa de viajar no `POST /v1/templates` e passa a ter porta própria, `PUT /v1/templates/{key}/versions/{version}/sensitive-variables`, isomórfica à porta do layout: só rascunho aceita, o editor fica registrado, e por isso quem declara não publica. O catálogo de validação ganha duas checagens de nome próprio. `sensitive-variables-retained` reprova a versão que larga um nome que a versão em vigor declara, porque a ingestão por barramento e a máscara leem a versão publicada e mudariam sem aviso. `sensitive-variables-unused` avisa, e não reprova, quando nenhum conteúdo da versão lê um nome declarado; ele existe pelo registro durável, já que a trilha grava nomes de checagem e descarta mensagens. No banco, a coluna sai de `template` e entra em `template_version`, sem backfill, e o `content_hash` de toda versão muda.

**O que fica declaradamente aberto.** A lista omissa. O achado passa de "declaração de ator único, nunca aprovada" para "declaração aprovada, possivelmente incompleta". É progresso, e não é fechamento.

### Consequências

**Positivas**
- Publicar ou corrigir um template em produção não exige deploy nem intervenção de Engenharia.
- A publicação é sobre o conteúdo exato (`content_hash` de versão imutável), por alguém que não é o autor; a validação é a mesma do runtime; a trilha é transacional.
- Reconstrução completa por `notification_id` (texto, layout, quem publicou).
- "Texto de notificação em código de produtor" passa a ser *finding* de code review.
- A v1 entrega a fronteira estrutural sem pagar UI, fluxo de review e promoção antes do primeiro template em produção.

**Negativas**
- Sem UI, a autoria exige chamadas de API (curl, scripts, coleção compartilhada), o que limita autores não técnicos na v1.
- Sem aprovação dupla, o controle de conteúdo repousa em três camadas: quatro olhos no publish, validação automática integral e auditoria transacional.

## Prós e contras das opções

### Opção 1: Dados no hub, gestão essencial via API
- Prós: fronteira estrutural (dados, nunca código) entregue já na v1; sem custo de UI; superfície privilegiada menor.
- Contras: autoria por API na v1; aprovação dupla e promoção dependem dos pontos de extensão.

### Opção 2: Dados no hub, gestão completa na v1
- Prós: autonomia máxima de Produto e Compliance desde o primeiro dia; governança formal de review imediata.
- Contras: Studio, fluxo de review e bundles assinados de promoção seriam construídos antes do primeiro template em produção; superfície privilegiada adicional (UI que edita o que chega ao cliente) exigindo ZTNA, Conditional Access e PIM desde o dia 1. Rejeitada pelo custo de construção antes do primeiro template em produção.

### Opção 3: Templates como código (Git + CODEOWNERS)
- Prós: trilha de aprovação nativa do Git; sem UI.
- Contras: Produto/Compliance dependem de engenharia; PR pode receber commits após aprovação; validação duplicada (CI vs. runtime); mudança em produção vira deploy; trilha fora da auditoria do hub.

### Opção 4: Texto em código
- Prós: nenhum.
- Contras: texto regulado espalhado por N serviços; mudança de vírgula é deploy; sem aprovação de Compliance; impossível reconstruir com prova.

### Opção 5: SaaS de templates
- Prós: UI pronta.
- Contras: a parte regulada (aprovação, auditoria, reconstrução) ficaria em terceiro fora do controle; mais um operador LGPD; ADR-0009.

## Como saberemos que foi a decisão certa

- Zero templates em repositórios de produtores após a migração (verificado por busca automatizada por padrões de texto de notificação).
- Publicar template novo não exige deploy em nenhum ambiente.
- Nenhum `publish` executado pelo autor da versão aparece na auditoria (o hub impede por construção).
- A validação automática bloqueia 100 % dos casos do catálogo de segurança de templates.
- Tempo médio de correção de texto em produção < 1 h, sempre com publicador distinto do autor registrado na trilha.

## Referências

- Design de Sistema, §4.3, §6, §7.4, §9.2, §9.5, §16 riscos 12 a 14.
