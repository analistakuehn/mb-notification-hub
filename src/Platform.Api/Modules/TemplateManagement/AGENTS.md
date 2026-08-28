# TemplateManagement module

## Boundary

- Keep one bounded context in this module. Its name comes from domain discovery, never from a table, screen, or technical layer.
- Keep invariants in the aggregates and value objects under `src/Platform.Api/Modules/TemplateManagement/Domain/`.
- Keep use-case orchestration in the slices under `src/Platform.Api/Modules/TemplateManagement/Features/`.
- Do not read or write another context's data store, infrastructure types, or mutable domain types.
- Publish cross-context facts as distinct, versioned contracts under `src/Platform.Api/Modules/TemplateManagement/Integration/V1/`.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/TemplateManagement/Domain/` | aggregates, value objects, domain policies, internal Domain Events |
| `src/Platform.Api/Modules/TemplateManagement/Features/` | vertical slices for this context |
| `src/Platform.Api/Modules/TemplateManagement/Integration/V1/` | versioned public contracts consumed by other modules (Integration Events, class policy contract) |
| `src/Platform.Api/Modules/TemplateManagement/Infrastructure/` | persistence, providers, broker, cache, outbox, inbox |
| `src/Platform.Api/Modules/TemplateManagement/TemplateManagementModule.cs` | application service registration for this context; technology registration for this context; endpoint mapping for this context |

## Implementation

- Organize new use cases as vertical slices; keep one use case's input, structural validation, orchestration, response, logging, and transport mapping together.
- Keep commands primitive at the transport boundary and rebuild value objects before invoking domain behavior.
- Keep repositories and domain policies concrete unless an observed boundary or test seam justifies an interface.
- Return `Result<T>` for expected outcomes; reserve exceptions for unexpected system failures.
- Raise a Domain Event when behavior inside this context reacts to a fact. Map it to a versioned Integration Event only when another context consumes it.

## Audit trail (consumer)

- The `audit_event` and `approval` tables, the hash-chain columns, the monthly
  partitioning, the partition manager, and the partition health check belong
  to the dedicated Audit module (`src/Platform.Api/Modules/Audit/`). This
  module no longer maps those tables in its DbContext and never touches them
  directly.
- Every governed effect (create, publish, deprecate, disable, rollback)
  records its trail through the published contract
  `NotificationHub.Api.Modules.Audit.Integration.V1.IAuditTrail`, **in the
  same database transaction** as the effect: the handler opens an explicit
  transaction, runs its own `SaveChanges`, calls `RecordApprovalAsync` (when
  the effect carries an approval) and `AppendAsync` with the raw
  `DbTransaction`, and commits immediately. A separate transaction for the
  trail is a defect; so is doing extra work between the append and the commit,
  because the append holds the partition chain lock until the transaction
  ends.
- The audit vocabulary (`AuditActions`, `AuditEntityTypes`, `AuditActorTypes`,
  `ApprovalSubjectTypes`, `ApprovalRoles`) lives in that contract. Approval
  subject ids are composed here, in this module's naming (template key, layout
  key, `application:class`).
- `details` carries compact JSON evidence (content hash, validation outcome,
  reason). Never personal data, variables, or rendered content.
- Retention, export, and the 90-day verification criterion counted from the
  partition's upper boundary are the Audit module's concerns; nothing in this
  module may depend on them.

## Layouts

- Layouts follow the same essential cycle as templates: immutable versions
  with a canonical content hash, one open draft at a time, version lifecycle
  `draft -> published -> superseded`, identity status
  `active | deprecated | disabled`, author and editors recorded on the
  version, four-eyes publication evaluated against the resource, transitions
  audited under the `layout.*` action vocabulary in the same transaction.
- **Layout reference is pinned.** A template version stores `layout_key` plus
  `layout_version` (both or neither), set on the draft through
  `PUT /v1/templates/{key}/versions/{v}/layout` with `If-Match`. The pin joins
  the version's canonical content hash, so approving a template also approves
  the exact layout version it renders inside. The template validation check
  `layout-reference` requires the pinned layout version to exist, to be
  published, and to resolve content for every (channel, locale) entry of the
  template version; a template without a reference stays valid.
- **Content placeholder contract.** Layout content is Scriban per
  (channel, locale) with a mandatory `body` and an optional `body_text`
  wrapper. Each wrapper MUST read the `content` variable: rendering first
  renders the template field with the caller's variables, then renders the
  layout wrapper with `content` bound to that finished text as its only
  variable. The subject never wraps; `body_text` wraps only when the layout
  ships a text wrapper. Layout locale resolution starts from the locale the
  template resolution landed on and follows exact -> base language -> layout
  default locale.

## Class policies

- This module owns the class policy **definition and its governance**: one
  aggregate per `(application, class)` with immutable versions, a JSON
  definition carrying `schemaVersion` plus the six version-1 fields
  (`channelsAllowed`, `deliveryPlan`, `defaultTtl`, `dedupeWindow`,
  `quietHours`, `consentPurpose`), structural validation returning the shared
  `checks[]` report, canonical content hash over the definition, a single open
  draft edited through `PUT .../policy/draft` with `If-Match`, version
  lifecycle `draft -> published -> superseded`, four-eyes publication
  evaluated against the resource, `approval` rows with subject
  `class_policy_version`, and audited transitions under the `class_policy.*`
  action vocabulary in the same transaction as the effect.
- The **Policy stage of the Core pipeline is the consumer**: it loads the
  published definition and executes an ordered list of `IPolicyRule`
  implementations. This module owns the `IPolicyRule` and `PolicyRuleResult`
  contracts (`Allow | FilterChannels(set) | Defer(releaseAt) | Reject(reason)`,
  always with compact JSON evidence) together with the tolerant definition
  reader, but ships no rule implementation: rules execute outside this module.
- The published policy contract lives under
  `src/Platform.Api/Modules/TemplateManagement/Integration/V1/` (namespace
  `NotificationHub.Api.Modules.TemplateManagement.Integration.V1`):
  `IPolicyRule<TContext>`, `PolicyRuleResult`, `ClassPolicyDefinition`,
  `DeliveryPlanStep`, `QuietHoursWindow`, and `Channel`. The bounded-context
  architecture rule carves out cross-module dependencies exclusively on
  `Modules.<other>.Integration.V1` namespaces; every other cross-module
  dependency stays forbidden. Inside this module the contract may read the
  Domain validation it fronts, and Domain types keep consuming the published
  vocabulary (`Channel`).
- The definition reader tolerates unknown fields on purpose: a field the
  version-1 vocabulary does not know belongs to a newer writer, never to an
  error. Delivery-plan steps are objects so an optional property extends them
  without a data migration. A field new to the vocabulary is a new
  `schemaVersion`, never a silent edit of version 1.

## Published read contracts

- Sibling modules read this context exclusively through the in-process
  contracts under `Integration/V1`, registered by `TemplateManagementModule`:
  `IPublishedCatalog` (published template decision metadata by
  `(application, templateKey)`, with `template-deprecated` and
  `template-disabled` returned as catalog data for the consumer to reject, and
  the published class policy by `(application, class)`),
  `IPublishedVariablesValidator` (variables payload against the published
  variables schema, in the shared checks vocabulary), and
  `IPublishedTemplateRenderer` (published version by channel and locale with
  the pinned layout applied).
- The renderer produces the full form for dispatch and, on demand, the masked
  form for the trail; each form carries the canonical hash of exactly the
  fields it shipped. Masking replaces sensitive values with `***` before the
  masked render, so the stored form proves that a value was sent, never which
  one.
- Contracts expose immutable DTOs and `Result`/`Result<T>` only; domain
  entities never cross the boundary. Only published state is visible: drafts
  and superseded versions stay internal.
- The published reads memoize in process. Per-version values never expire,
  because a version is immutable by the governance contract; the
  current-published pointers expire 60 seconds after they load. A lifecycle
  transition (template disable, template deprecation, version publication,
  version rollback, layout disable, layout deprecation, and class policy
  publication) drops the pointers it invalidates, in the process that
  committed it, right after the commit. Every other process keeps answering
  the previous value until its own pointer expires, so a stop command reaches
  the fleet within 60 seconds and never sooner. Treat that bound as the
  contract of this surface: a control that has to stop traffic faster does not
  belong behind these pointers.

## Error axis

- This module has exactly one error axis: `Result`/`Result<T>` from the
  SharedKernel carrying a `DomainError`-encoded string (stable code plus scalar
  detail, unit-separator framing). The string is decoded once, at the HTTP
  boundary (`Infrastructure/Http/ApiResults.cs`); handlers and domain code
  never parse it back.
- Hard rule: a structured or list-shaped payload never travels in the error
  string. When a use case produces a report, a collection, or any composite
  outcome, model it as a response value returned through `Result.Success`,
  even when the outcome describes failures (the validation report is the
  canonical example: failed checks are data, not errors).
- Review trigger: the moment a second module needs coded errors, promote a
  typed `Error` to the SharedKernel through an accepted ADR instead of copying
  the `DomainError` encoding dialect.

## Logging

- Each use case ships a dedicated `<UseCase>.Handler.Logger.cs` file holding a
  top-level `internal static partial class <UseCase>Logger` (extension methods
  do not compile in nested classes, so the logger class sits beside the slice
  container, not inside it).
- Log methods are source-generated: `[LoggerMessage] internal static partial
  void Evento(this ILogger logger, ...)`; handlers call `logger.Evento(...)` on
  the injected `ILogger`.
- Identifiers stay in English (`TemplateCreated`, `EndpointInvocationStarted`);
  pt-BR appears only in log message text and user-facing text, with proper
  diacritics. Placeholders carry real domain names (template key, version,
  channel, locale), never personal data, variables, or rendered content.
- The dialect covers every `.Logger.cs` in the repository, including the host's
  `Infrastructure/EndpointFilters/RequestLoggingFilter.Logger.cs`.

## Security and tests

- Require named authorization and rate-limiting policies on state-changing endpoints.
- Authorization on this surface is by route plus the four-eyes rule on the resource, and
  nothing else. There is no per-application scope: a principal holding the author role
  reads, creates and edits the draft of a template or of a class policy of any
  application, and a principal holding the publisher role publishes, deprecates,
  disables and rolls back a template of any application. Nothing binds a principal to an
  application in this phase, which is the same missing binding the ingestion and the
  query surfaces already record. The published read contracts do refuse a key the
  requested application does not own, so read this asymmetry as deliberate and recorded,
  never as an oversight to close locally: the accepted risk and the containment that
  stands in for the scope live in the system design.
- **Toda fonte de template passa por um teto de complexidade antes do parse.**
  O prazo de render cobre o render e só ele: o parse roda antes, o parser do
  Scriban não aceita token de cancelamento e nada o interrompe depois de
  começar. A fonte é medida pelo lexer do próprio motor e recusada antes de
  chegar ao parser, com dois tetos em `TemplatingOptions`. `MaxTemplateTokens`
  limita o custo de parse, que medido acompanha a contagem de tokens.
  `MaxCodeBlockTokens` limita a profundidade de uma expressão, porque um
  encadeamento pós-fixo (`a.b.c`, `a[0][0]`) é lido em laço, não entra no limite
  de profundidade do motor e derruba o processo por estouro de pilha em tudo
  que percorre a árvore depois do parse. A medição roda na admissão do parse e
  nunca na consulta à memoização. Limitar só o tamanho não substitui os dois
  tetos: na mesma contagem de caracteres, o custo de parse varia por um fator
  de 150 conforme a forma da fonte.
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
