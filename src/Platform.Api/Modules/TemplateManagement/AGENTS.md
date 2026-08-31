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
- `details` carries compact JSON evidence and nothing else. A publication or a
  rollback records the content hash, the superseded version, the schema version
  where the artifact has one, and a `validation` object holding the verdict, the
  distinct names of the checks that ran, the distinct names of the checks that
  warned, and how many warnings there were. Both write the same object: a
  rollback is a publication and a weaker record of it says less for no gain.
  There is no failed list and no failed count, because a report that does not
  pass returns a blocked outcome before any audit entry exists, so the field
  could only ever hold an empty value. The names of the checks that ran are
  kept because the catalog is code, changes on deploy and carries no stamp of
  its own: that list is the only part of the report a later revalidation cannot
  reproduce. The names that warned and their count are kept because publishing
  over a warning is a decision, and a decision belongs in the trail.
- That revalidation is a surface, not a hope: every governed artifact answers
  `POST .../versions/{v}/validate` with the integral report for the version the
  route names, draft or published, and writes nothing. Read the reproducibility
  of that report per artifact. A class policy reproduces it exactly, because the
  validation is a function of the stored definition alone and a published
  definition never changes; measured, a published version revalidates byte for
  byte after a newer version supersedes it, after a layout publishes and is
  disabled, and after a template of the same class changes. A template does not:
  the `layout-reference` check reads the layout identity and version as they
  stand today, so the same immutable version, at the same content hash, passed at
  publication and fails on revalidation once the pinned layout stops resolving.
- What never crosses into `details`: personal data, rendered content, the value
  **or the name** of a variable, and any text lifted out of the body of a
  template or of a layout. That last clause is what rules out the message and
  the location of a validation check: the message interpolates a declared
  variable name or a link host read off a wrapper body, and the location points
  at the content unit the finding came from. It also rules out the note of a
  lifecycle transition, which is why that note reaches the handler as a type and
  not as a second string beside the reason code: carried as a type, the only way
  to reach the prose is to name `Text`, and a scan can look for that.
  `tests/Platform.SecurityArchTests` scans the producers of `DetailsJson` in
  this module and fails on a check message, a check location, or the text of a
  note.
- The reason of a deprecate or a disable is a code from
  `Integration.V1.LifecycleReasons` plus an optional free-text note, never a
  sentence in the code field: the periodic evidence read groups the trail by
  that field and the archived report copies the group name verbatim, so prose
  turns every phrasing into a category of its own. The note is capped at
  `Domain.LifecycleNoteText.MaxLength`, read by the four endpoint validators and
  by the column that stores it, and is required only for `other`, which is the
  single refusal the note adds; `other` exists so that no operator is ever
  denied a traffic stop for lack of a matching entry.
- **The note is not in the trail.** It is stored in `lifecycle_note`, owned by
  this context, and the trail records the reason code plus `noteRef`, a random
  identifier of the stored row. The prose is unbounded in content by design: it
  is written under pressure while traffic is being stopped, and refusing a stop
  because the words look like personal data is worse than the ambiguity such a
  refusal would remove. Since nothing can bound the content, the content must
  not go where nothing can remove it. `noteRef` is `Guid.CreateVersion7()` and
  never a digest of the words: a digest of a short value is a lookup table away
  from the value, and it would tie every transition carrying the same sentence
  together forever, which is the link an erasure exists to break.
- **Erasure is an act, not an absence.** The eraser under
  `Infrastructure/Retention/` deletes the row and appends the erasure action of
  the subject, carrying the same `noteRef`, in one transaction and in the same
  shape as the transitions. Without that event, a reference pointing
  at nothing would read the same as a transition that never carried a note, and
  a store that can lose a record without saying so is a store an auditor cannot
  use. It has **no HTTP surface**, and that is deliberate: no context of this
  system exposes a forgetting endpoint, and the first one is a capability with
  its own review, not a rider on a storage change. Whoever adds the trigger owns
  the authorization, the rate limit and the four-eyes question that come with
  it.
- The note is written by the `SaveChanges` the transition already runs, one
  statement before the append, and the erasure follows the same order. Nothing
  is placed between the append and the commit: the append holds the chain
  advisory lock of the partition until the transaction ends.
- Nothing migrates the rows written before each of these rules, and nothing
  can: the trail is append-only by database trigger and hash-chained per
  partition, so rewriting a row breaks the chain it belongs to. The eras are
  readable from the row itself and need no clock. A publication whose
  `validation` object carries `warned` was written under the compact-evidence
  rule; an earlier one carries the full report, with check messages and
  locations. A lifecycle transition reads in three eras: details with neither
  `note` nor `noteRef` are the oldest, from when the reason was free prose;
  details carrying `note` hold the operator's words in the trail itself and
  cannot be cleared; details carrying `noteRef` are current, and the words they
  point at are erasable. The middle era is the one an operator asking to be
  forgotten cannot be fully served in, and saying so is the only thing this
  module can do about it.
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
  variables schema, in the shared checks vocabulary),
  `IPublishedTemplateRenderer` (published version by channel and locale with
  the pinned layout applied), and `IHistoricalCatalog` (one exact version by
  `(application, templateKey, version)`, returned as
  `HistoricalTemplateVersion` with the layout version it pinned).
- The two catalogs are separate contracts because they answer opposite
  questions. The published catalog answers "what would go out now", and a
  caller deciding what to send reads it. The historical catalog answers "what
  went out then", and a caller reconstructing a past notification reads it.
  Mixing them is how an audit surface starts quoting a version nobody used, so
  a caller that already knows the version number asks the historical contract
  for it and never the published one.
- The renderer produces the full form for dispatch and, on demand, the masked
  form for the trail; each form carries the canonical hash of exactly the
  fields it shipped. Masking replaces sensitive values with `***` before the
  masked render, so the stored form proves that a value was sent, never which
  one.
- Contracts expose immutable DTOs and `Result`/`Result<T>` only; domain
  entities never cross the boundary. The published reads show published state
  only: drafts and superseded versions stay internal to them. The historical
  read is the one exception, and it is bounded by construction, because it
  answers the single version the caller already named and never a lookup that
  could discover one.
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
- The historical read is not memoized at all, and never as a current pointer.
  It reads the store for the version it was named, and a version does not move
  once it leaves draft, so a lifecycle transition has nothing to invalidate
  there. Memoizing it behind a pointer would produce the exact defect the two
  contracts exist to keep apart: the answer about a past notification would
  start following the current publication.

## The historical read answers for published and superseded only

Three declarations described this surface and two of them disagreed with the
third. `IHistoricalCatalog` promised one version that is published or
superseded. `HistoricalCatalog` filtered by key and version number with no
status test at all, on the version path and on the pinned layout path alike.
`HistoricalTemplateVersion.VersionStatus` documented published, superseded or
draft, so the published contract type agreed with the code against the
interface. The interface carried the truth. The code and the contract doc moved
to it.

Three measurements decide that, and none of them is a preference:

1. The version lifecycle runs draft, published, superseded, and never back.
   `TemplateVersionStatuses.AllowedTransitions` allows `Draft => [Published]`,
   `Published => [Superseded]`, and `Superseded => []`. A version that is a
   draft today has never been published.
2. Only a published version renders. The published reads load their version
   through `PublishedTemplateQueries.FindPublishedVersionAsync`, which filters
   on `Status == Published`. Evidence naming version N therefore cannot have
   come from an N that is a draft today.
3. Publishing a version whose pin resolves to a layout draft is refused.
   `TemplateValidation.AddLayoutReferenceChecks` fails the layout-reference
   check unless the pinned layout version is published, and
   `LayoutReferenceFacts.PinIsUsable` carries the same rule for the rules that
   read the layout text. A published version pinning a layout draft is
   impossible for the same reason, one step further out.

Neither withheld state is reachable through a legitimate path, so reaching one
is reported and never absorbed. That half carries the weight. The consumer of
this contract is the notification evidence disclosure, and it turns a failed
lookup into a missing template block, so a bare filter would trade a wrong
answer for a silent one: the compliance surface would say "no record of that
version" while the truth is "that version exists and never shipped", which is a
different and more alarming fact, and which reads exactly like a version that
never existed or was deleted. `HistoricalCatalogLogger` records both cases at
error level, event `2120` for the version and event `3100` for the pinned
layout. Error rather than warning for three reasons: the state is unreachable
rather than uncommon; the version number arrives from the stored notification
evidence and never from a caller, so no request can flood the level; and this
repository already reserves error for evidence that contradicts itself, as
`ChainVerifier` does for a broken audit chain.

The new layout case entered under an ambiguity that another finding closes. A
`null` layout in the answer already carried two meanings, "the version pinned
no layout" and "the version pinned one that no longer resolves", and a pin that
resolves to a draft is now a third. Separating them is out of scope here and
belongs to `ARC-007`, still recorded as `PENDENTE`. This change neither narrows
that ambiguity nor counts as progress on it. It only puts the new case on the
log axis, so the omission stays as ambiguous as it was while the reason behind
it becomes audible, and whoever closes `ARC-007` finds three cases to separate
instead of two.

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

- Loggers follow the repository dialect: `*.Logger.cs` files with
  source-generated extension methods, identifiers in English, message text in
  pt-BR, never personal data, variables or rendered content in placeholders.
  Identifiers name domain facts (`TemplateCreated`, `EndpointInvocationStarted`)
  and placeholders carry domain names: template key, version, channel, locale.
- A slice keeps its events in a top-level `internal static partial class
  <UseCase>Logger`, beside the slice container and never inside it, because an
  extension method does not compile in a nested class. Handlers call
  `logger.Event(...)` on the injected `ILogger`.
- The pairing is enforced in both directions, and it is the whole rule: a
  handler that takes an `ILogger` ships `<UseCase>.Handler.Logger.cs` beside it,
  and that file sits only beside a handler that takes one. The architecture test
  project owns the rule and carries no exemption list.
- Whether a slice logs at all is not enforced, here or anywhere else in this
  repository. The gap is deliberate and its cost is accepted: the slice decides,
  and no rule guesses the decision back.
- Coverage keyed on the verb was measured and rejected, even confined to this
  module, where it has no violation and no exemption today. It holds by the
  current shape of these slices and not by design, and the counterexample
  already lives in two of the four modules: the notification history read writes
  the recipient snapshot back to the cache, and the attempt content read moves
  the disclosure alarm. The repository has already paid for that predicate once.
  The security rule that demands authorization and rate limiting matches
  `MapPost`, `MapPut`, `MapPatch` and `MapDelete` only, so the two disclosure
  routes, the most sensitive reads in the system, fell outside it and had their
  rate limiting applied by hand. Take the decision back to review when a GET
  slice in this module needs to log.
- Deferred, with the condition that unblocks it: a `catch` in a slice that wraps
  an audit trail write declares a log event. Measured before deferring, every
  handler `catch` in this module translates a concurrency or a uniqueness
  failure into the `Result` axis and none of them swallows a trail failure. The
  rule would be born with one violation, in the kill switch administration slice
  of the notification module, so it belongs to the security architecture
  project, where a trail that fails without a witness is the harm. Write it once
  that slice is closed, never with a named exemption: an exemption, even a dated
  one, teaches that the defect it carries is acceptable.
- Deferred, with the condition that unblocks it: every mapped endpoint applies
  `.WithRequestLogging()`. Two routes stand outside it today, the provider
  webhook, which carries its own logger and needs a separate decision, and the
  kill switch administration route, which carries nothing. The rule belongs
  beside the state-changing endpoint rule in the security architecture project
  and covers every verb, because it is the direct remedy for the blind spot of
  that rule.

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
- **The destination guard reads a destination the way a client resolves it,
  not the way an author writes it.** After a scheme, any run of slashes and
  backslashes is a separator, the empty run included, so `https://evil.example`,
  `https:\\evil.example`, `https:/\evil.example` and `https:evil.example` are one
  address and are decided alike; the same run with no scheme in front of it is
  protocol-relative, and it is read only where a value starts, because a run in
  the middle of a value is a path a doubled separator produced.
- **Inside a URI-bearing attribute, the authority handed to the canonizer runs
  to the first real URL delimiter and to nothing else.** A URL parser ends an
  authority at a slash, a backslash, a question mark, a number sign, or the end
  of the value. It carries a quote, an angle bracket and every kind of space
  into the userinfo and reads the host after the at sign, so a detector that
  stops at one of those hands over a prefix and approves the wrong host. There
  the tab, the line feed and the carriage return are removed, every other
  character the candidate grammar cannot carry is percent-encoded, and a
  character reference that spells the attribute's own quote is percent-encoded
  before the document is decoded, so it stays data instead of closing the value
  a scan reads. Do not answer the next character of this family by naming it;
  the rule is the delimiter set, not a list. **The claim is scoped on purpose**:
  a meta refresh destination, a CSS `url()` value, plain text and `bodyText`
  still cut their candidate by character class, which is what they did before
  this rule existed. Do not repeat the principle as if it held for the whole
  file.
- **`srcset` and `ping` hold a list, and each entry is prepared on its own.**
  Their separators are ASCII whitespace and commas, and a separator is the
  boundary a destination is read from, so percent-encoding one welds two entries
  together and hides the second. Nothing wider than ASCII counts as a separator
  there: a no-break space is not one in either grammar, so it stays inside the
  entry and reaches the canonizer.
- **The gate that tells an address from writing never answers with silence.** It
  answers "address" or "writing", and a value the canonizer cannot read is an
  address, not writing, because unreadable already has an answer of its own.
  This is the one place the guard was ever fail-open: dropping such a candidate
  quietly left no host behind it, so a percent-encoded dot such as
  `//evil%2Eru`, which a host parser decodes to a real domain and System.Uri
  refuses, was approved and disarmed the class-wide ban on links at the same
  time.
- **A separator that carries nothing has to earn its reading.** A scheme glued
  straight to its authority, and an authority with no scheme, are also how
  ordinary writing looks. Measured over Portuguese operational text, reading
  every one of them as a destination refused `codigo HTTP:200`, `HTTPS:443`,
  `erro HTTP:404`, `status http:500`, `http:port nao configurado`, `Https:Sim`,
  `campo https:true`, `compartilhamento \\fileserver\notas` and `razao 2//3`.
  The gate is a dot or a colon inside the part a parser would read as the
  authority, or a canonical host that is a dotted address outside `0.0.0.0/8`.
  That keeps `https:3232235777`, which is 192.168.1.1 without dots, and gives up
  exactly one thing, named here: a dotless intranet name such as
  `https:relatorios` stops being read as a destination.
- **An attribute destination is delivered for five schemes and no others:
  `http`, `https`, `mailto`, `cid` and `tel`.** A value with no scheme is
  relative to the message itself and stays. `data:`, `blob:`, `javascript:` and
  `sms:` are refused in `href`, `src` and `action` alike, inline base64 images
  in `<img src>` included; a CSS `url()` keeps its own tokenizer and still
  accepts `url(data:...)`.
- **Having no host is not one failure, it is two opposite ones.** `data:` and
  `blob:` carry their payload instead of naming anywhere, and the allowlist
  cannot rule on them because there is nothing to rule on. `mailto:`, `cid:`
  and `tel:` name a mailbox, a part of the message the reader already holds,
  and a telephone number: no host there means no external destination, which is
  the opposite case, and `cid:` is how mail has always carried an inline image.
  **None of those three has an authority, so a value that opens one is refused**
  whatever it wrote before the colon: that keeps `cid://elsewhere.example/x` and
  `mailto://elsewhere.example/x` out without depending on a client declining to
  read a host where the scheme defines none. The scheme is compared whole, so
  `xcid:` and `nottel:` are different schemes and stay refused.
- **Delivering a scheme never exempts its value.** The separators a URL parser
  removes come out first, so the scheme is decided on the text the client
  parses, and the rest is prepared and scanned like any other destination.
  Measured, that reads the domain part of a Content-ID: `cid:logo@good.example`
  is refused when `good.example` is outside the allowed domains, while
  `cid:logo123`, a UUID, an Outlook-style `image001.png@01DA1234.5678ABCD` and a
  Content-ID whose domain is an allowed one all pass. An author whose generator
  stamps a foreign domain into a Content-ID adds that domain or drops the
  suffix.
- **The refusal names the host the value carried when it carried one**, so
  `blob:https://elsewhere.example/1` reports that host and only a value with no
  host at all reports the fixed marker.
- **The authentication-SMS ban is wider than the allowlist, and stays wider by
  decision.** It bans every spelling above plus the scheme-less ones. It is a
  regex predicate with no canonizer, so the gate that tells an address from a
  note about configuration is deliberately **not** applied to it: measured, it
  refuses `codigo HTTP:200`, `campo https:true`, a Windows share path and `Nota
  fiscal 1.234/56`, and only the last of those was already refused before that
  gate existed. The asymmetry is the point and it is the one the detector's own
  comment states: the two error budgets run in opposite directions, and in the
  single class where a link is banned outright a false negative is a phishing
  link inside the message people are trained to act on without reading twice,
  while a false positive is one authentication code. Do not carry the gate over
  to close the gap; close it by narrowing what an authentication SMS may say.
- **A `NonBacktracking` expression in this module carries no `matchTimeout`,
  and that is a correctness rule.** Measured on this runtime, the two together
  answer "no match" instead of matching or throwing once the stretch in front
  of the first match passes roughly a hundred thousand characters: with the
  timeout the answer is wrong, without it the same call answers correctly in
  under a millisecond, so nothing is being timed out. Because it never throws,
  the `catch (RegexMatchTimeoutException)` that exists to fail closed is never
  reached, and "no match" reads as "no host", which reads as approval. The
  whole preparation of an attribute destination sits behind one such call, so
  above the turn there is no removal of separators, no percent-encoding and no
  scheme allowlist: an author switched the defence off by writing a long enough
  body, and the render ceiling is a million characters. `NonBacktracking`
  already guarantees linear time, which is why it is here, so the timeout buys
  nothing and costs correctness. Keep the existing catch blocks as they are and
  do not add a timeout back for symmetry with a neighbour.
- **A long body does not hide a destination, and cost is not the measure of
  that.** The same defect made the markup scan of a body carrying thousands of
  CSS `url()` carriers stop answering, at its own turn, while the plain-text
  reading of the same body never did. It also blinded the expression that
  removes an XML namespace declaration, so an `xmlns` written far enough into a
  body, which is how an inline SVG arrives, was read as a link to `www.w3.org`
  and refused the template: measured, refused at a hundred and fifty thousand
  characters and accepted at ninety thousand, and accepted at every size now. Do
  not treat a fall in the cost of this scan as a win without checking that the
  host count did not fall with it.
- **Before deploying, sweep the published versions.** Removing that timeout is
  the first time two rules already written here reach a large body, so a version
  above roughly a hundred and ten thousand characters may have been publishing
  under a guard that was not running: sweep for a refused scheme in `href`,
  `src` or `action`, and for a composed dynamic destination. Delivering `cid:`
  and `tel:` takes most of the first sweep away, and `data:` in `src` stays in
  it.
- **A published version that carries a refused scheme, or a composed dynamic
  destination, fails revalidation and cannot be rolled back, and that is
  accepted with no carve-out**: the rule is the correct one, and a rollback onto
  a version the current rule refuses is a rollback onto an unsafe version.
- **Whoever composes final rendered content applies the output policy.** The
  allowlist is checked while a version is authored, but authoring sees
  fragments: the destination a reader receives exists only after interpolation,
  after the layout frames the body and after the channel normalizer rewrites
  it. `Domain/RenderedOutputPolicy.Apply` is that point and it is the only one:
  the preview an author reads and the published render a sibling module
  dispatches call the same function, so the two can no longer answer
  differently about the same text.
- **The order inside that policy is the rule, not an arrangement of it.**
  Normalize for the channel, ban a link inside an authentication SMS over the
  already normalized text, guard the destination, measure the result against
  what the channel carries, hash what is left.
  Normalization comes first because every step after it decides about the bytes
  a provider actually receives, and the audited hash has to describe those
  bytes: hashing before normalizing leaves the audit calling every SMS tampered
  with. The ban reads the normalized form, which is the form it always read,
  and it runs before the hash because a refused render has no output to
  describe. `SmsContentNormalizer` moved out of `Infrastructure/Templating/`
  into `Domain/` for this, with no change of behavior: `Domain` may not depend
  on `Infrastructure`, and the policy is where the normalizer is now called
  from. The single call site is itself a rule, since two of them are two
  orderings waiting to diverge.
- **The rendered SMS has a ceiling, and it is counted in segments.** The unit
  is the segment because that is what a carrier splits, bills and delivers, and
  because the same text costs a different number of them depending on which
  characters it carries: one character outside the GSM 03.38 tables switches the
  whole message to a two-byte encoding and can more than double the count.
  `Domain/SmsSegmentCount` is the counter and `Domain/SmsSegmentCeiling` holds
  the number. Ten is derived, `floor(1600 / 153)`, and the derivation is written
  next to the constant: `153 * 10 = 1530 <= 1600`, so nothing the ceiling admits
  can overflow the assumed provider body limit in any encoding, while eleven
  would give `1683 > 1600` and the two numbers would contradict each other. The
  1600 is an inference about the provider and is not verified anywhere in this
  repository; the identical 1600 that bounds the template source is a different
  measurement over a different thing, and the two agreeing is not evidence about
  either. Ten is the largest defensible ceiling and not a spending policy:
  whoever pays the bill may lower it, and raising it needs the assumed limit
  confirmed first.
- **The counter segments for real in both alphabets and never divides a total
  by a capacity.** Each alphabet has a unit that may not be split across a
  boundary, and a segment that cannot fit the next whole unit gives its last
  position up. In UCS-2 that unit is the surrogate pair: a segment nominally
  carries 67 UTF-16 units, but a pair is one character in two units and a
  segment that ended between them would ship two halves that decode to nothing,
  so a boundary landing mid-pair leaves the segment carrying 66. Sixty-seven
  emoji are 134 units and three segments, while dividing by 67 predicts two. In
  GSM-7 that unit is the escape sequence: a character of the extension table
  travels as an escape plus itself, so a segment holding 152 of its 153 septets
  gives the last position up and carries 76 extension characters rather than
  76 and a half. Seven hundred and sixty-one extension characters are 1522
  septets, which divided by 153 predicts ten segments and really occupies
  eleven, and the range 761 to 765 is the one that crossed the ceiling. Both
  arms were division once and both were wrong in the permissive direction,
  which is the direction that turns a ceiling into a bypass.
- **Every bound over this counter uses 66, the worst-case UCS-2 capacity, and
  GSM-7 cannot take that place.** The most expensive GSM text is every
  character from the extension table at 76 per segment, so 660 of them are nine
  segments against the ten that 660 units of astral text cost, and at the upper
  bound the cheapest character is still the basic one at one septet. The 1378
  counterexamples over the all-astral lengths from 2 to 5000 units are what the
  permanent oracle in the unit tests exists to keep out. That oracle is an upper
  bound and therefore polices overcounting only: an undercount can never violate
  it, so the two boundary tests, one per alphabet, are what police the direction
  that matters, and each asserts both of its sides.
- **The ceiling runs the counter only between 661 and 1530 units.** At or below
  660 nothing can exceed ten segments, because the worst case per unit is 66;
  above 1530 nothing can stay within ten, because the best case per unit is one
  septet at 153 per segment. The upper bound is exact, and the lower one is
  conservative by a single unit: 661 units also always fit, because the last
  segment has nothing after it to split and takes its full 67, while 662 units
  of astral text cost eleven. The derived form is kept over a hand-tuned 661,
  because it can be rechecked by hand whenever the ceiling or the capacity
  moves. Both bounds are pinned by tests asserting every side, since a bound
  asserted from one side alone does not distinguish the right number from any
  looser one, and the lower one asserts three lengths rather than two so the
  conservatism is stated instead of discovered later as a defect. The lower bound is an admitting shortcut, so
  it must be conservative: at 670, which the nominal rate would give, 331 astral
  characters are 662 units and cost 11 segments and would be admitted without
  ever being measured.
- **The size check is the fourth of the five steps, after both security checks
  and before the hash, and the order costs something on purpose.** Normalization
  has to precede it because composing changes the length in both directions: 400
  copies of one precomposed Hebrew code point become 1200 characters under NFC,
  and 800 letters written with a combining accent become 800 characters instead
  of 1600, so a measure taken first refuses messages that fit and admits
  messages that do not. The two security checks precede it because capacity is
  not a security question: if the size answered first, an operator reading "too
  large" would never learn that the message also carried a phishing link inside
  an authentication SMS, and the producer would shorten the text until the real
  refusal finally surfaced. The measured price of that order is 12,3 ms per
  refused render, with identical allocation.
- **`RenderedSizeCeiling.Exempt` exists for the masked form and nothing else,
  and its reason is the opposite of the ban exemption's.** The ban may be
  skipped on the masked form because masking only ever removes a link. The
  ceiling must be skipped on it because masking may add text: the marker is
  three characters, so a one-character authentication code makes the masked
  field two characters longer than the message, and an authentication code of a
  single digit is enough to refuse a legitimate message over the size of its own
  trail copy. The masked form is not the message. Never reuse
  `AuthenticationLinkBan` for this axis and never derive either from the other,
  or a later pass that skips the ban stops being measured without anyone
  deciding that.
- **`rendered-content-too-large` names no channel on purpose.** The catalog is
  closed, and a second channel gaining a ceiling has to reuse this member rather
  than add one. Push, e-mail and WhatsApp have no ceiling here today, and each
  is out for its own reason rather than by omission. Push is a separate finding
  and not a number to copy: its unit is bytes, not segments, and the provider
  budget is shared with a data payload this policy never sees, so a ceiling set
  here would bound the wrong half of the request. E-mail has no comparable hard
  limit at this layer, and the gateway-side limits that do exist are about
  attachments and total message size, neither of which this policy composes.
  WhatsApp is template-governed at the provider, which enforces its own body
  limits when the template is approved, so a second ceiling here would refuse
  content the provider already accepted.
- **The publication check speaks about the template source, and its message says
  so.** It counts the characters an author wrote, placeholders included, and it
  cannot say anything about the message a recipient receives, because the values
  that replace the placeholders are unknown at that point and the same source
  renders to a different length for every request. It is not weakened by the
  render-time ceiling and it does not substitute for it.
- **`Integration/V1/SmsSegmentLimit` publishes the number, the unit and the
  counter, and deliberately no gate.** Nothing can assess this before a render:
  the size that matters belongs to text that does not exist until the variables
  are interpolated, the layout frames the body and the channel normalizer
  rewrites the result. A published gate would be a contract no caller could
  satisfy and every caller would believe, which is worse than none, because a
  consumer would check it, pass, and still be refused at the render. A unit test
  pins the published members one by one, so any entry point shaped like a
  verdict on a request fails it whatever it is called.
- **The refusal shape is a parameter because it is a consumer contract, not a
  preference.** `RefusalShape.Bare` returns the bare word: the Core pipeline
  compares the whole error text against it for equality, and anything wrapped
  around it collapses a security refusal into a plain render failure.
  `RefusalShape.Formatted` returns the same code carrying a sentence, for the
  preview, which a person reads. The sentence names the rule and never quotes
  what tripped it: at that point the text is the recipient's own data, and the
  detector answers on ordinary prose by design. The destination refusal is not
  on this axis, because it already builds the same string for both callers.
- **The alarm stays outside the policy.** `Domain` does not log, and the event
  carries the application, the key, and the version, none of which the policy
  receives. The published renderer recognizes its own refusal exactly the way
  its consumer does, by equality against
  `TemplateValidation.AuthenticationSmsLinkCode`, and logs then. The preview
  raises no alarm.
- **`AuthenticationLinkBan.AlreadyEnforced` exists for the masked form and
  nothing else.** Masking replaces a value with a fixed marker, so it can
  remove a link and never write one: the full form already answered the ban
  over the same content, and a second scan of every rendered field of every
  notification carrying a sensitive variable buys nothing. Every other caller
  passes `Enforce`. Neither enum has a member on the zero value, so a caller
  that leaves either decision to a default gets a value the policy refuses to
  act on.
- `tests/Platform.SecurityArchTests` fails a file of this module three ways: one
  that drives the sandbox render without calling the output policy, one that
  names the layout wrapper or the channel normalizer without calling it, and a
  second file that normalizes channel content at all. Consulting the destination
  policy directly does not satisfy the first two, on purpose: accepting it would
  pass a third orchestrator that guards the destination and skips normalization,
  the ban, and the hash. The single exemption is the policy's own file, named
  and not matched by a pattern, and it opens no hole, because a fourth rule
  demands all five steps of that file instead of the presence of one call. Two
  residues are known, measured, and written into those tests: a composer that
  neither drives the sandbox nor names the wrapper or the normalizer escapes all
  three, and the first rule anchors on the identifier `engine`, which is a
  parameter name, so a renamed receiver escapes it.
- **The order is pinned by unit tests that read the policy directly**, not only
  by the render behavior tests. Three of them pass under a policy that runs the
  same five steps in the wrong sequence, so the ones that matter are the ones
  about precedence: the ban answers before the destination guard, the
  normalizer runs before the ban, and the ceiling answers after both security
  checks, before the hash, and over the already normalized text. A change that
  reorders the five steps has a gate, and it is not the architecture scan,
  which reads presence and never order.
- **The preview draws its line at content, never at identity.** It refuses a
  disabled layout, and it does not refuse a `deprecated` or a `disabled`
  template, while dispatch refuses all three. The goal is not "preview equals
  dispatch": a preview renders drafts by definition, which is to say things
  dispatch would never send, so the goal is that the preview never approves what
  dispatch refuses **on account of the content**. A template status is an
  identity decision, and a disabled template is still being edited by whoever
  prepares the next version. A disabled layout is the other case: its own text
  has to stop going out, and a body without its frame carries a hash nobody
  approved.
- **The payload ceiling is one number behind two doors with two refusal shapes,
  and the asymmetry is deliberate.** `Domain/VariablesPayloadSize.MaxBytes`
  governs both, so nothing admitted at one door is refused for its size at
  another. The preview refuses inside its request validator, so the answer is a
  validation problem carrying an `errors` dictionary, beside every other
  malformed field of the same request, which is what the author's tooling
  reads. The published renderer refuses with
  `ErrorCodes.VariablesPayloadTooLarge` on this module's single error axis,
  because its caller is a sibling module reading a coded result and not a form.
  What matters is the property both hold: each door refuses before any query and
  before anything walks the payload. The shape follows the surface, and unifying
  the two changes an endpoint contract.
- **That ceiling answers three ways, not two, and the third is refused closed.**
  A payload can parse and still not transcode: an escape may name a surrogate
  the payload never pairs, which is legal JSON text, so the reader accepts it
  and it binds to a `JsonElement` without complaint. Only the rewrite to UTF-8
  discovers that the escape names no character, and that rewrite is what
  measuring the payload does, what reading any string value out of it does, and
  what masking it does. Measured, a payload like that used to take every door
  of this system out through a runtime exception rather than an answer.
  `Domain/VariablesPayloadSize` therefore reports admitted, unreadable, or over
  the ceiling, from one traversal, and an unreadable payload is refused with a
  reason of its own rather than borrowed from the size, which would name the
  wrong cause. `Integration/V1/VariablesPayloadLimit` publishes the same three
  answers, because a consumer that could only ask about size would carry the
  other refusal to a place that cannot make it.
- **Nothing in this module reads what a surrogate is.** The runtime already
  owns that rule, and it owns it at the exact point that transcodes; a scanner
  of our own would be a second reading of it, free to disagree with the one
  that decides. The single place that answers is `SharedKernel/CompactJsonSize`,
  which measures and reports readability from the same walk so no caller can
  hold one answer and act as if it had both. Its catch names the one exception
  type the transcoding raises and no wider one: catching everything would
  swallow a defect in the walk and report it as an unreadable payload, which is
  how a measure stops being able to fail. The mechanism lives there rather than
  here because the sibling module imposes a different ceiling on a different
  payload with the same walk, and a second copy of it is a second place for the
  same defect to survive the fix applied to the first. The numbers stay with
  their owners; only the walk is shared.
- **The same rule governs the document the author writes, and it is one rule
  over three frontiers.** The ceiling above answers about the payload a
  producer sends; the schema of a template version and the definition of a
  class policy are documents too, and they reach a canonical form, a hash, a
  declaration walk and a diff. `Domain/CanonicalJson.TryNormalize` is where the
  refusal lives for the two that get hashed: one traversal answers whether the
  text is JSON, whether anything can read it, and whether it is an object, and
  the caller translates that verdict on its own error axis. The traversal is
  the one the hash was already paying for, so the guard costs no walk and no
  allocation beyond it. The authoring endpoints refuse in front of
  `GetRawText`, with `SharedKernel/CompactJsonSize`, because the raw text is
  the one read such a body survives and everything past it transcodes; the
  policy door in particular stands in front of the structural validation, which
  runs on the submitted definition before the aggregate ever sees it.
  `Domain/VariablesSchema` and `Domain/VersionDiff` settle readability over the
  whole root before they read a name or a value, which adds no catch anywhere:
  a guard shaped around the fields they read today would reopen the day a field
  is added or renamed, because which lookup trips first depends on the sought
  name's length against the escaped candidate key's.
- **The shape rule travels with the readability rule, in one verdict.** A
  schema that is legal JSON and not an object used to be refused only at the
  transport. Left out of the domain it publishes: the declaration walk reads no
  properties out of it, the catalog reports the schema readable, and every
  undeclared-name check passes over a version that declares nothing at all. The
  two findings come from one traversal and are returned by one call, for the
  same reason the ceiling and the readability of a payload are.
- **`stored-content-unreadable` is not `content-hash-mismatch`.** A row that
  cannot be read did not diverge from its hash; nothing can recompute one over
  it. Answering it as a mismatch would accuse the stored bytes of a change
  nobody made and send an operator looking for one. `variables-schema-unreadable`
  is the door's code, which an author fixes by resending. Neither code appears
  in the producer guide, and correctly so: `ErrorCodes` is this module's
  internal error axis, decoded once at the HTTP boundary, and the guide's
  catalog is `NotificationRejectionReasons`.
- **`Rehydrate` still throws on a document it cannot read, on both aggregates.**
  It is the one entry point that skips every guard, and its contract says it
  never receives user input, so a document that does not transcode there is a
  caller that broke that contract rather than a document with a property. That
  is the unexpected system failure an exception exists for, and giving it a
  refusal to return would invite a caller to route user input through the one
  door with no guards. A test asserts that nothing under `src/` calls it.
- **No hash moved, and none may.** Every published version verifies its stored
  content against a hash computed the same way it was when the version was
  approved, so a canonical form that shifted by one byte would report tampering
  in bulk over content nobody touched. `CanonicalHash.OfVersion` therefore
  takes the canonical schema rather than the schema, because producing that
  form is the step that can refuse and a hash that could fail would put the
  refusal in the one place with no way to report it. The fence is a corpus of
  schemas whose hashes are pinned as literals, measured before the refusal
  moved.
- **What this refusal did not close, and what reopens each one.** None of these
  is reachable through any endpoint today, past or present: before the refusal
  existed the write itself failed, so no API ever stored such a document. They
  are the paths a row written around the API would take.
  - *The canonical form is not injective.* `{"a":1,"a":2}` and `{"a":2}` hash
    alike, because both the writer and jsonb collapse a duplicate key to the
    last occurrence, and the endpoint accepts the duplicate. The hash therefore
    vouches for less than the stored bytes. Refusing a duplicate would change
    what a public endpoint accepts and would turn any stored row carrying one
    into a version nobody can publish, which collides with the retroactive
    neutrality above. Reopen it when a duplicate key is observed in a stored
    schema or definition, or when the acceptance change is deliberately wanted.
  - *`Infrastructure/Http/JsonProjections.ParseOrNull` still fails on such a
    row.* Making it total is trivial; deciding what a version read should
    answer when its stored schema cannot be projected is not, and the same
    answer has to serve five projections. Returning null would make an
    unreadable column indistinguishable from an absent one, which is a silent
    failure in a governance read. Reopen it with that contract decision.
  - *`Domain/ClassPolicyValidation` has no readability guard of its own.* It
    walks names and string values of the submitted definition and is protected
    only by the endpoint that refuses in front of it. Reopen it the moment a
    second caller reaches that catalog, or the moment the definition can arrive
    from anywhere but that door.
  - *The canonical form of a schema at the ceiling is a large-object
    allocation.* One integrity verification over a 64000-character schema
    allocates about 519 KiB, and the canonical string alone lands on the large
    object heap. There is a way to hash without materializing it. That is a
    performance finding of its own, fenced here only against growth.
  - *`Encoding.UTF8` over `Subject`, `Body` and `BodyText` replaces silently.*
    Those fields reach the hash as raw bytes with replacement, which is the
    option this rule forbids for a JSON document. It is unreachable today
    because no door admits invalid UTF-16 into them, and correcting it would
    move the hash of every stored row. Reopen it only together with a hash
    migration.
- **One number governs the whole template source axis, and it lives in
  `Domain/TemplateSourceSize`.** The body and the text body of a template
  version, the body and the text body of a layout version, the two authoring
  validators and the ceiling the engine refuses a source by all read that one
  constant. No aggregate carries a body limit of its own any more: the two
  `MaxBodyLength` constants that held 512000 were removed and the compiler
  proved no reader was left behind. A subject keeps a ceiling of its own
  because 998 comes from the mail header line and from the column that stores
  it, never from what a subject costs to parse; a variables schema keeps one
  for the opposite reason, since it never reaches the engine at all.
- **The regression over published versions is zero because the number that
  governs does not move, and for no other reason.** It is tempting to say that
  rehydration and cloning skip `SetContent` and stop there. That fact is true
  and it is not what carries the conclusion: a rollback reruns the validation
  with the analyzer, so a published version is regated on that path. What makes
  the change safe is that the ceiling the analyzer applies was already 131072
  and stayed 131072.
- **131072 is a measurement anchor and not a derivation, and 208411 was refused
  on purpose.** Three independent readings put the number where it is. The
  richest legitimate source ever probed is 128 KB of marketing HTML carrying
  200 interpolations and 2781 tokens. At that same character count plain text
  parses in 0.6 ms while a single chain of member accesses parses in 92 ms,
  which says the cost of a source follows its shape and never its length, so
  length is the wrong knob to spend on parse cost. And 131072 is the size this
  module was sized around when those readings were taken. The larger candidate,
  208411, is the remainder of a division between five constants of the parse
  memoization, one of which the memoization itself declares will move on the
  next engine upgrade, and the hypothesis that produces it, two parsed nodes per
  source character, is unreal by a factor of 25.6 against the 0.078 nodes per
  character the densest admitted shape delivers. A safety ceiling whose
  renumbering is already announced is not a ceiling. Do not re-derive this
  number from the memoization constants.
- **The tie to the memoization budget is asserted at compile time, and deleting
  the declaration is how it gets lost.** `ScribanParseCache.SourceCeiling.cs`
  declares the slack between the budget and the source ceiling as an unsigned
  constant, so a ceiling the memoization cannot promise to hold does not build.
  It is the exact substitute for what the configuration range used to do and it
  is strictly better, because it fails on a build instead of on a deploy. Two
  things follow. The compiler names neither constant when it fires: raising the
  ceiling to 300000 reports `error CS0031: Constant value '-91589' cannot be
  converted to a 'uint'` and says nothing else, so the comment beside the
  declaration is load-bearing and answering that error by removing the
  declaration removes the guard without a trace. And the declaration sits in a
  file of its own because the part of the memoization that holds the budget
  imports the engine, whose `Template` is a different type from the domain
  `Template`, and the two namespaces cannot meet in one file.
- **The configured ceiling is bounded below as well as above, and the lower
  bound is measured.** A subject is source the engine analyzes, so a
  configuration under the longest subject a version may carry recreates on the
  subject axis the dead band this change closed on the body axis: measured with
  the ceiling at 500 and a subject of 700 characters, the write is accepted and
  the analysis refuses with `The template has 700 characters and exceeds the
  500 character limit`, a message that still calls the subject a template. The
  range therefore runs from the subject ceiling to the source ceiling, and the
  default is the source ceiling itself. Two configurations that started the
  host before this change no longer do: 200000 and 500.
- **Accepted limit: the authoring validators do not read the configured
  ceiling.** Making them read it was considered and refused, and the
  consequence of the refusal is written here rather than discovered later. If
  an operator tightens `MaxTemplateSizeChars` below the source ceiling, an
  author submitting a body between the two receives `200` on the write and the
  refusal at `validate`, with the tightened number, which is the correct one to
  report. The premise the refusal rests on is that the shipped default is the
  source ceiling, so nothing splits the two axes until someone types a number.
  Two tests pin exactly that: one asserts the default equals the constant, and
  one sweeps the settings files both hosts ship for a key that tightens it. The
  day either turns red, this refusal is what gets reopened.
- **Out of scope here, with the reason recorded so it is not rediscovered as
  new.** Lowering the source ceiling shrinks the worst case of the raw catalog
  sweep by a factor of 3.9 and does not close it, because the catalog does not
  short-circuit by declared design; the measured saving per call is 67.7 ms of
  CPU, 27.84 MB and 3371 response items. The sensitive-variable check runs once
  per occurrence rather than once per variable, so its report grows with the
  body: 4538 checks were counted over a body of 512000 characters, and the new
  ceiling lowers that count in proportion without changing the shape of the
  defect. And three `catch (RegexMatchTimeoutException)` blocks, in
  `Domain/TemplateValidation.cs` at lines 98, 465 and 510, cannot be reached,
  because the expressions they guard are `NonBacktracking` and carry no
  timeout. That is worse than dead code: the comment on the first one states
  that it fails closed and stays on the `Result` axis, and that route never
  runs, so the file documents a guarantee nothing provides.
- **O objeto de builtins do sandbox é um só por processo, e ele é sempre
  selado.** O motor empurra esse objeto para o fundo da pilha de globais de
  todo contexto e o preserva no reset que roda entre dois renders, então o que
  for gravado ali sobrevive ao render, ao chamador e à instância do motor. Sem
  o selo, um template publicado de uma aplicação grava um valor do destinatário
  em um membro novo de `object` e o template de outra aplicação o lê, e
  sobrescrever `date.format` move toda data implícita de todo render seguinte
  do processo até reiniciar. O selo é `IsReadOnly` na raiz e em cada grupo
  aninhado, aplicado como último passo de `BuildSandboxBuiltin`, depois das
  remoções de membro, que um objeto selado recusaria. O relatório de
  publicação não dá sinal disso, e não é por descuido: o alvo da atribuição é
  expressão de membro, a coleta de variáveis usadas só registra atribuição a
  variável simples, e o nome que sobra é o do grupo de builtin, que a mesma
  coleta remove da lista. A versão passa limpa na validação e vaza no render.
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

## Sandbox de templates: sinal de recusa

O motor conhece o modo da recusa e nunca a identidade do que renderizou; o
chamador conhece a identidade e nunca o modo. Por isso o modo volta ao lado do
`Result`, num canal lateral, e cada superfície emite o seu próprio evento: a
prévia como nota, o publicado como aviso. O nível pertence à superfície e nunca
ao modo.

Ficou fora do escopo desta correção, com a razão de cada item:

- **Sinal na análise de origem**: o relatório de validação é um sinal mais forte
  que log, e a trilha já o registra.
- **Separar limite de laço, limite de recursão e erro de autor em render**:
  impossibilidade medida no Scriban 7.2.6, não escolha. A versão não expõe
  subtipo de exceção, não expõe hook utilizável e o tipo de nó colide com quatro
  erros de autor comuns. O modo `Unclassified` declara em letra o que carrega, e
  o teste que fixa a versão de arquivo do pacote é o gatilho de reabertura: ele
  fica vermelho sozinho quando o motor se move.
- **Contagem, alarme e limiar**: pertencem à fatia de telemetria. Continua
  valendo a decisão de não haver `Meter` nem contador em `src/`.
- **Paridade de oráculo entre a prévia e o publicado**: já é achado aberto de
  outra lente.
- **Custo da prévia**: a prévia custa 4,1 vezes a alocação e 3,1 vezes o tempo do
  despacho, por abrir um contexto por campo. É achado de desempenho separado, e
  não serve de justificativa para abrir escopo na prévia: a prévia é a única
  escritora da memoização do host de API.
- **Segundo furo da redação de valores**: a troca só alcança substring exata de
  escalar com três ou mais caracteres, então valor reformatado pelo motor
  escapa. Não é agravado aqui, porque o evento não carrega a mensagem do motor,
  mas o fato fica registrado.

Registro de um fato descoberto ao corrigir: os dois oráculos que cobriam o
limite de laço e o limite de recursão passavam por motivo errado. O que casava
`LoopLimit` valia para o laço sobre intervalo e ficava vermelho sobre coleção,
sobre `while` e sobre iteração interna. O que casava `recursive depth limit`
ficava verde sobre um erro de parse, porque o gatilho é a pilha restante e não a
profundidade. Os dois foram reescritos para afirmar o que o módulo consegue
distinguir.

## Sandbox de templates: estado compartilhado entre renders

O objeto de builtins que o sandbox expõe é construído uma vez, guardado em campo
estático e passado a todo `TemplateContext`. O construtor do motor o empurra
para o fundo da pilha de globais, e o `Reset()` que roda ao fim de cada render o
preserva por desenho. Ele é selado por isso, e o selo cobre a superfície inteira
medida contra o Scriban 7.2.6:

- **Profundidade 2**, a raiz e um nível de grupos abaixo dela. Não existe
  terceiro nível.
- **Oito grupos**: `array`, `date`, `html`, `math`, `object`, `regex`, `string`
  e `timespan`.
- **Cinco membros de dados**: `blank`, `empty`, `date.default_format`,
  `date.format` e `timespan.zero`. Os outros 125 membros são funções, e função
  o motor já recusava sozinho por membro somente leitura. Membro de dados e
  membro novo não eram recusados por ninguém.

Selar a raiz é redundante contra o motor fixado, e foi medido assim: com os oito
grupos selados e a raiz aberta, nada muda em nenhum dos dois vazamentos. O motor
já recusa por conta própria a escrita que resolve na raiz, e todo render empurra
globais próprios acima dela. A raiz continua selada porque a superfície carrega
uma regra só em vez de duas, e porque uma versão do motor que pare de sombrear a
raiz reabriria o buraco sem sinal nenhum.

**Gatilho de reabertura.** O selo percorre um nível abaixo da raiz, que é toda a
superfície que este motor tem. Uma versão que aninhe um terceiro nível, ou que
traga um nono grupo, deixa o que ela trouxe gravável e compartilhado, e todo
teste de vazamento continua verde, porque cada um deles nomeia membro que o selo
já alcança. Quem fica vermelho é o teste que afirma o inventário acima, em
`ScribanSharedBuiltinTests`. No dia em que ele ficar vermelho, o que se faz é
reconferir a superfície e estender o selo, nunca mover o inventário para o que
foi medido depois.

**Consequência de implantação do selo.** Uma versão publicada que hoje escreva
num membro de builtin renderiza com sucesso e vaza; com o selo ela passa a
falhar o render. A troca é correta, porque recusa é melhor que vazamento
silencioso, mas ela não é invisível: a segunda consulta da seção abaixo é o que
diz, por ambiente, se existe alguma dessas versões antes de a implantação
acontecer.

## Formatação de saída: o que está decidido e o que foi medido

**A decisão, para que o próximo leitor não a reabra.** A formatação de saída
passa a ser **invariante e imposta**, com três peças que só valem juntas: banir o
argumento de cultura nos filtros de formatação, fixar as culturas predefinidas
do runtime, e fixar a imagem base por digest para que a versão da ICU pare de se
mover por baixo. As três entraram juntas, no mesmo commit, junto com o check de
publicação que avisa o autor antes de a mensagem sair. O registro durável delas
é a **ADR-0017**; o que fica abaixo é o que um leitor deste módulo precisa saber
sem sair daqui.

**`InvariantGlobalization` está proibido como caminho.** Ele parece resolver o
mesmo problema por um interruptor e não resolve: sob globalização invariante a
composição de acentos vira no-op silencioso, a consulta de normalização passa a
mentir, e o primeiro passo da política de saída passa a operar sobre um texto
que ele acredita ter normalizado. É troca de um defeito visível por um
invisível. Junto com isso: os quatro testes que falham sob globalização
invariante **ficam como estão**. Eles são o único oráculo que o repositório tem,
de graça, para a dependência de ICU do caminho de saída, e deixá-los verdes
apagaria esse sinal.

**A premissa de hash determinístico entre hosts já é falsa, e foi medida nas duas
pontas.** No mesmo commit, `en-ZA` formata `1234567.5` de dois jeitos:

| Host | ICU | Saída | SHA-256 do UTF-8 |
|---|---|---|---|
| Windows 11 | `icu.dll` 72.1.0.4 | `1 234 567,50` | `cf3a9964…` |
| Ubuntu 24.04 | `libicu74` 74.2-1ubuntu3.1 | `1,234,567.50` | `29870715…` |

O separador de milhar do lado Windows é espaço inquebrável (U+00A0) e não espaço
comum, detalhe que decide se uma remedição reproduz ou não. Quem argumentar a
partir de "o hash é o mesmo em qualquer host" está argumentando a partir de algo
que a medição já derrubou.

**O bloqueio foi levantado em 2026-08-30, e as duas consultas estavam furadas.**
O censo rodou contra o banco de desenvolvimento (`compose.yaml`, `postgres:17-alpine`).
As duas devolveram **0**, e não é incidente: não há uma linha viva em nenhuma
tabela de nenhum dos sete esquemas, e não há ambiente publicado. O raio do passo
irreversível é zero.

Antes de aceitar esse zero, as consultas foram submetidas a sondas semeadas que
tinham que casar e sondas que não podiam casar, dentro de uma transação
revertida. As consultas originais reprovaram em quatro pontos, e os quatro
estão corrigidos abaixo. Os três primeiros vieram das sondas; o quarto só
apareceu quando a implementação foi ler a API do motor, e nenhuma sonda o
teria achado, porque uma sonda confirma o que a lista procura e nunca revela o
que a lista esqueceu:

| Furo | O que escapava | Por que importa |
|---|---|---|
| Só aspas simples | `math.format "N1" "pt-BR"` | É a forma canônica do Scriban, e era o próprio exemplo que derrubou a premissa da ficha |
| Subtag de região obrigatória | `'pt'`, `'en'` sem região | Tag só de idioma é cultura válida e resolve normalmente |
| Lista de filtros errada | `date.parse`, `date.parse_to_string`, `object.format` | Medidos na API do Scriban 7.2.6 durante a implementação. A lista antiga procurava `string.to_string`, que **não existe** neste motor, e ignorava três filtros reais; `date.parse_to_string` sozinho carrega **dois** argumentos de cultura |
| Filtro `status = 'published'` | toda versão `superseded` | O índice único `ux_template_version_single_published` deixa **uma** versão publicada por template; a história restaurável inteira mora em `superseded`, e `RollbackTemplate` clona a fonte dela exigindo hash idêntico |

O terceiro furo era o de maior alcance: contava no máximo uma versão por
template e ignorava todo o resto. O quarto era o mais perigoso, porque um
filtro ausente da lista devolve zero com a mesma cara de um filtro ausente do
catálogo.

As consultas corrigidas **superestimam** de propósito. Um formato de data todo
em letras com 2 ou 3 caracteres (`'dd'`, `'MMM'`) lê como tag de idioma e conta
como falso positivo; `'yyyy'`, `'N1'` e `'C2'` não contam. Essa é a troca
deliberada: o número deixa de ser piso e passa a ser **teto**, e teto é a direção
certa de erro para um censo de custo, porque **um teto zero prova ausência,
enquanto um piso zero não prova nada**. Um falso positivo custa uma leitura
manual; um falso negativo custa um template quebrado na implantação. O que
continua escapando dos dois: cultura ou nome de membro que cheguem por variável
em vez de literal.

Quantas versões passam argumento de cultura a um filtro de formatação, que é o
que decide o custo de banir esse argumento:

```sql
SELECT count(*) AS versoes_com_argumento_de_cultura
FROM (
    SELECT tv.template_key AS chave, tv.version AS versao
      FROM templatemanagement.template_version tv
      JOIN templatemanagement.template_content tc
        ON tc.template_key = tv.template_key
       AND tc.version = tv.version
     WHERE tv.status IN ('published', 'superseded')
       AND concat_ws(' ', tc.subject, tc.body, tc.body_text)
           ~ $re$(date\.parse(_to_string)?|date\.to_string|math\.format|object\.format)[^}]*['"][A-Za-z]{2,3}(-[A-Za-z]{2,4}){0,2}['"]$re$
    UNION
    SELECT lv.layout_key, lv.version
      FROM templatemanagement.layout_version lv
      JOIN templatemanagement.layout_content lc
        ON lc.layout_key = lv.layout_key
       AND lc.version = lv.version
     WHERE lv.status IN ('published', 'superseded')
       AND concat_ws(' ', lc.body, lc.body_text)
           ~ $re$(date\.parse(_to_string)?|date\.to_string|math\.format|object\.format)[^}]*['"][A-Za-z]{2,3}(-[A-Za-z]{2,4}){0,2}['"]$re$
) AS achados;
```

Quantas versões contêm atribuição a membro de builtin. **Se este número for
maior que zero, isso deixa de ser correção e passa a ser incidente**, e a
resposta é resposta a incidente: cada versão achada gravou dado de destinatário
num objeto que todo render do processo lia, e o alcance do que vazou é a janela
entre a publicação dela e a reinicialização do processo, para todas as
aplicações servidas por aquele processo. O selo de `e2e5fee` fecha o vazamento
daqui para a frente, então esta consulta é forense e não preventiva: ela pergunta
se alguém já vazou antes do selo, e por isso `superseded` importa mais aqui do
que na outra.

```sql
SELECT count(*) AS versoes_com_atribuicao_a_builtin
FROM (
    SELECT tv.template_key AS chave, tv.version AS versao
      FROM templatemanagement.template_version tv
      JOIN templatemanagement.template_content tc
        ON tc.template_key = tv.template_key
       AND tc.version = tv.version
     WHERE tv.status IN ('published', 'superseded')
       AND concat_ws(' ', tc.subject, tc.body, tc.body_text)
           ~ $re$(array|date|html|math|object|regex|string|timespan)[[:space:]]*(\.[A-Za-z_0-9]+|\[[^]]*\])[[:space:]]*=[^=]$re$
    UNION
    SELECT lv.layout_key, lv.version
      FROM templatemanagement.layout_version lv
      JOIN templatemanagement.layout_content lc
        ON lc.layout_key = lv.layout_key
       AND lc.version = lv.version
     WHERE lv.status IN ('published', 'superseded')
       AND concat_ws(' ', lc.body, lc.body_text)
           ~ $re$(array|date|html|math|object|regex|string|timespan)[[:space:]]*(\.[A-Za-z_0-9]+|\[[^]]*\])[[:space:]]*=[^=]$re$
) AS achados;
```

Trocar `count(*)` pela lista de `chave` e `versao` é o que dá o alvo do trabalho
seguinte, nos dois casos.

**O que ficou no código, e o que a medição corrigiu do texto acima.** As duas
consultas nomeiam três filtros, e o texto da decisão fala em quatro. Nenhum dos
dois números estava certo, e o conjunto real foi lido do próprio motor,
percorrendo o objeto de builtins inteiro pela informação de parâmetro que o
Scriban 7.2.6 publica. São **cinco membros e seis argumentos**:

| Membro | Argumento | Posição |
|---|---|---|
| `date.parse` | `culture` | 2 |
| `date.parse_to_string` | `output_culture` | 2 |
| `date.parse_to_string` | `input_culture` | 4 |
| `date.to_string` | `culture` | 2 |
| `math.format` | `culture` | 2 |
| `object.format` | `culture` | 2 |

`string.to_string` **não existe** neste motor: as consultas procuravam um filtro
que nunca esteve lá, e por isso o zero que elas devolveram vale menos do que
parece para esse nome e não menos para os outros dois. `date.parse`,
`date.parse_to_string` e `object.format` não eram procurados por ninguém. E
`date.parse_to_string` carrega **dois** argumentos de cultura, um de leitura e um
de escrita; banir só o de saída deixaria o de entrada aberto.

**O banimento é de execução, e o check de publicação é de sintaxe.** O que fecha
a porta é um embrulho em volta de cada membro acima, dentro de
`BuildSandboxBuiltin`, antes do selo. Ele vê os argumentos depois que o motor os
ligou, e é por isso que ele é completo: medido, a barra, a chamada posicional, a
chamada com parênteses, o argumento nomeado, a cultura por variável, o indexador
`math["format"]` e o apelido `grupo = math` chegam todos como um vetor posicional
único com a cultura na posição declarada. A recusa sai sob o modo
`TemplateRefusal.CultureArgument` e nunca sob o modo residual.

O check `output-culture` lê a árvore sintática e é mais fraco de propósito: ele
resolve `grupo.membro` e o indexador com literal, e **não** enxerga o grupo que
chegou por variável. Esse ponto cego está afirmado em teste, junto com a recusa
do render sobre a mesma fonte, para que ele fique visível em vez de descrito.
Quem alargar o check deve ver aquele caso virar; quem estreitar o banimento deve
ver um template sair com cultura dentro.

**O render é guarda de execução e responde sobre a expressão que executou.** Uma
fonte com variável não declarada e cultura junto reprova primeiro pela variável,
porque o argumento é avaliado antes da chamada, e a cultura nunca é alcançada. O
check de publicação lê a fonte e relata as duas. É essa a distância que ele
cobre, e sem ele só o template cujo payload de preview estivesse completo seria
avisado.

**Gatilho de reabertura.** O conjunto de cinco membros é afirmado por
`ScribanCultureBanTests`, contra a superfície que o motor publica. Uma versão que
traga um sexto membro com cultura, ou um terceiro argumento num destes, deixa
essa porta aberta e todo o resto do arquivo continua verde, porque cada caso
nomeia membro que o banimento já cobre. Quem fica vermelho é a sentinela. No dia
em que isso acontecer, estenda `CultureBearingBuiltins`, nunca mova a sentinela
para o que foi medido depois.

**A tabela está em `CultureBearingBuiltins`, e não dentro do motor, por um
motivo que não deixa rastro na fonte.** O motor constrói a superfície do sandbox
num inicializador de campo estático declarado no outro arquivo parcial da mesma
classe, e a ordem entre inicializadores estáticos de dois arquivos parciais é a
ordem em que o compilador os recebeu. Declarada dentro do motor, a tabela estava
nula na hora de construir a superfície e o tipo inteiro falhava ao inicializar,
com a suíte reprovando noventa e um testes de uma vez. Um tipo próprio inicializa
no primeiro uso, seja qual for o caminho que chegue lá primeiro.

## Igualdade dos contratos publicados: o que foi convertido e por quê

**A decisão, para que o próximo leitor não a reabra.** A régua não é "carrega
coleção" nem "é do módulo". É **"este tipo quebra a promessa?"**. Seis
declarações de `Integration/V1` viraram `sealed class`, todas autônomas, e três
continuam `record` porque não devem nada.

| Convertido para `sealed class` | Arquivo |
|---|---|
| `ClassPolicyDefinition` | `Integration/V1/ClassPolicyDefinition.cs` |
| `PublishedClassPolicy` | `Integration/V1/PublishedClassPolicy.cs` |
| `VariablesValidationReport` | `Integration/V1/VariablesValidationReport.cs` |
| `PublishedTemplate` | `Integration/V1/PublishedTemplate.cs` |
| `HistoricalTemplateVersion` | `Integration/V1/HistoricalTemplateVersion.cs` |
| `PublishedRenderRequest` | `Integration/V1/PublishedTemplateRender.cs` |

`DeliveryPlanStep`, `QuietHoursWindow` e `HistoricalLayoutVersion` **ficam
`record`**, e não por esquecimento: medidos, os três comparam por conteúdo hoje.

**O mecanismo não é o que a leitura da declaração sugere.** A igualdade
sintetizada fecha sobre `EqualityComparer<TipoDeclarado>.Default`, e para membro
de interface, de array, ou de struct que carrega referência, isso é despacho
virtual sobre a instância que o produtor injetou. Duas `List<T>` dão `False`;
duas coleções que sobrescrevem `Equals` dão `True`; dois boxes do mesmo
`ImmutableArray` dão `True`. Ou seja: **a igualdade do contrato publicado é
escolhida em tempo de execução pelo produtor, não pelo contrato.** Isso é
instabilidade, não incorreção, e é o argumento que sustenta a conversão.

**Alternativas mortas por medição. Não reabra.**

| Alternativa | Por que morreu |
|---|---|
| `readonly record struct Channel` | `Result<T>` é `readonly record struct` com `T` sem restrição, então uma falha de `Create` materializaria `Value == null` passando em `is not null` nos portões de consentimento e de supressão |
| `Equals` à mão nos contratos | Publicaria uma segunda identidade **mais fraca** que o hash, e `IReadOnlyList<T>` é vista sobre a coleção do chamador, então o hash estrutural some do próprio dicionário quando o chamador muta a lista |
| Tipo novo de coleção no `SharedKernel` | Custo de vocabulário novo sem fechar a pergunta |
| "Trocar `Channel` resolve" | Falso por construção: a quebra está nos membros de coleção, não no `Channel` |

**A guarda é comportamental, não sintática**, e vive em
`tests/Platform.ArchTests/PublishedContractEqualityTests.cs` porque atravessa os
cinco módulos que publicam contrato. Para cada `record` público em
`*.Integration.V1` ela constrói dois valores de conteúdo igual por membro em
instâncias distintas e pergunta a `EqualityComparer<T>.Default`. O conjunto dos
que quebram é comparado por **igualdade exata** com um inventário nomeado, então
consertar um tipo sem tirar a linha reprova tanto quanto acrescentar uma quebra
sem pôr a linha. **O portão barra o esquecimento, não a decisão**: quem
acrescentar um contrato quebrado junto com a entrada dele no inventário passa, e
isso está certo.

Duas limitações estão declaradas no próprio teste, porque nenhuma é alcançável
por uma regra indexada por tipo. A primeira: **quebra dependente do valor**.
`DispatchRequest.Message` compara por conteúdo quando carrega `EmailMessage` e
por referência quando carrega `PushMessage`; a guarda escolhe o primeiro subtipo
concreto por nome ordinal e relata aquele veredito. A segunda: **contrato fora
do segmento de namespace**, fechada por um teste próprio que reprova se algum
tipo público com membro público nascer em `Modules.{X}.Integration` fora de
`.V1`.

**O censo é piso, não total.** Medido antes da conversão: **20** contratos
publicados quebravam, sendo 12 nos outros quatro módulos. Depois das cinco
primeiras: **15**, exatamente os 20 menos as cinco, e nenhuma quebra nova nasceu
da conversão. Depois da sexta: **14**. O
número é piso porque a quebra dependente de valor fica fora de qualquer contagem
indexada por tipo.

**`NO-CONSENSUS` sobre o alcance amplo.** Os 12 restantes nos outros quatro
módulos continuam sem decisão, e faltam dois desempates que **não existem hoje**:
as lentes de revisão daqueles módulos, que não correram, e uma execução da suíte
de integração sobre o caminho `CachedRecipientSnapshot`, onde `RecipientSnapshot`
viaja serializado e cifrado no Redis. O conjunto recomendado é **subconjunto
próprio** do amplo, então nada do que se fez agora se desfaz depois.

**A sexta entrou depois da mesa, e a razão de ela ter faltado é o método, não
o descuido.** `PublishedRenderRequest` quebra por `JsonElement? Variables`: um
`JsonElement` não define igualdade própria, então a comparação cai nos campos
dele, e o campo que decide é a referência ao documento de onde ele foi
analisado. Dois pedidos com as mesmas variáveis analisadas em separado
respondem `False`; dois pedidos que partilham um documento analisado respondem
`True`. Ou seja, a resposta é sobre qual instância de documento o produtor
entregou, nunca sobre as variáveis. A lista das cinco veio do censo
**estrutural**, que enxerga membro de coleção e é cego a `JsonElement` e a
`ReadOnlyMemory<byte>`; quem achou esta foi o censo **comportamental**, que é a
pergunta que a guarda faz. Converter estava **dentro** da régua decidida e não a
expandia: deixar de fora criaria exceção interna sem razão na regra do módulo.
A conversão não custou nada porque a declaração é autônoma, não é posicional, e
toda construção usa inicializador de objeto.

**`PublishedTemplateLookup.Published` e `PolicyRuleResult.FilterChannels` ficam
no inventário da guarda por restrição de hierarquia, não por esquecimento.** As
duas são folha de hierarquia fechada de `record`, e virar `class` não é local:
arrastaria a raiz e todas as irmãs. As irmãs **cumprem** a promessa hoje
(`Rejected(string)` num caso; `Allow`, `Defer(DateTimeOffset)` e
`Reject(string)` no outro), então converter qualquer uma das duas apagaria
igualdade de conteúdo que existe e funciona, para consertar uma folha. É essa a
razão registrada no inventário. Quem quiser reabrir decide primeiro o que fazer
com as irmãs.

**O `ARC-003` continua `PENDENTE` e isto não é progresso sobre ele.** O dano
dele é real, presente e maior que a ficha registra. Medido nesta leitura:
`ContactConsent.Domain.ContactChannels` codifica **três** canais em vez de
quatro, sem push; `StoredAttemptContent.ToRenderedMessage` decide por um `switch`
de três braços sem whatsapp; e `PushChannel = "push"` está repetido em
`ChannelSelectionRule`, `RouteStage` e `AttemptDispatchWriter`. Pior:
`AdmittedDeliveryPlan.Read` devolve `null` quando `Channel.Create` recusa uma
palavra, e `FallbackRequestHandler` lê esse `null` como "sem plano armazenado" e
cai no plano publicado agora, **trocando o plano de entrega de uma notificação em
voo**. Não feche nem sugira fechar.

## Cache: o que este módulo registra e o que ele deliberadamente não registra

**A decisão, para que o próximo leitor não a reabra.** O cache publicado deste
módulo é o **de memória**, em `Infrastructure/Integration/PublishedReadCache.cs`.
O módulo **não registra Redis** e não registra abstração transversal nenhuma no
container.

**O que foi removido, e por quê.** O módulo registrava `IConnectionMultiplexer`
como singleton e chamava `AddStackExchangeRedisCache`, que registra
`IDistributedCache`. Medido: **nenhum tipo do repositório resolvia qualquer uma
das duas**. As únicas ocorrências de `IConnectionMultiplexer` fora daquele
arquivo eram três comentários de documentação nos módulos irmãos dizendo que
deliberadamente **não** resolvem o do container, e `IDistributedCache` não
aparecia em `src/` nem em `tests/`. Três consequências justificaram a remoção,
e não apenas o código morto:

1. **Acoplamento de boot.** A opção era `[Required]` com
   `.ValidateDataAnnotations().ValidateOnStart()`, então a API recusava subir sem
   cadeia de conexão para algo que nada consumia. As fixtures de integração
   injetavam a cadeia só para o host conseguir subir, e isso saiu junto.
2. **Fronteira.** Era o único módulo registrando no container duas abstrações
   transversais que não pertencem ao contexto dele, e o teste de arquitetura por
   namespace não enxerga esse acoplamento, porque ele acontece na coleção de
   serviços e não em referência de tipo.
3. **Armadilha de disponibilidade.** A conexão não forçava
   `AbortOnConnectFail = false`, que é exatamente o cuidado que os três irmãos
   documentam. No dia em que alguém resolvesse aquele singleton, um Redis
   inacessível viraria exceção de resolução em vez de falha no ponto de uso.

**Prova de boot, medida e não argumentada.** Com a seção de configuração
removida dos dois `appsettings`, o host subiu: `Now listening on` e
`Application started`. As falhas de Postgres no log são ambiente (a cadeia de
desenvolvimento não declara senha e o contêiner exige) e acontecem em serviços de
fundo **depois** do start, nunca na validação de opções, que roda antes.

**A regra para o futuro.** Se memoização distribuída for desejada, ela entra por
decisão aceita e com **wrapper próprio do módulo**, no padrão que
`ContactConsent`, `Dispatch` e `Notifications` já documentam: multiplexer
preguiçoso, `AbortOnConnectFail` forçado a `false`, e falha aparecendo na
primeira operação. **Nunca por registro de abstração global no container.**

**Por que não há portão para isso, e a medição que decidiu.** Um portão do tipo
"nenhum módulo registra abstração vinda de pacote externo" **não é viável como
regra mecânica**. Medido: `TimeProvider.System` é registrado por vários módulos
(`AuditModule`, `PartitionManagerSetup`, `ChainVerificationSetup`, e o próprio
`TemplateManagementModule`) e é abstração de framework legítima; some-se
`IValidator` por varredura de assembly, `AddDbContext`, health checks e rate
limiters. A distinção entre "abstração de framework que um módulo pode
registrar" e "infraestrutura transversal de terceiro que ele deve envolver" é
semântica e não mecânica, então o portão exigiria lista de exceção grande e sem
princípio, que é pior que portão nenhum. O que segura esta linha é a regra
escrita acima mais revisão humana, e isso está dito aqui em vez de ficar
implícito.

**Pacote.** `Microsoft.Extensions.Caching.StackExchangeRedis` saiu de
`Directory.Packages.props` e do `.csproj`, porque só ele fornecia
`AddStackExchangeRedisCache`. **`StackExchange.Redis` fica**, porque os três
irmãos o usam direto. O serviço `redis` do `compose.yaml` fica pelo mesmo
motivo; saiu só a variável de ambiente deste módulo.

## Serialização do vocabulário de canal: o que a ida e volta fecha e o que não

**O estado anterior, medido e não argumentado.** `Channel` se escrevia como
objeto envelope, `{"value":"email"}`, e não voltava de jeito nenhum: ler um de
volta lançava `NotSupportedException`, porque o tipo não tem construtor sem
parâmetro nem construtor público. Era essa a causa de os consumidores
projetarem a palavra à mão na ida e na volta.

**A decisão: o conversor fica `internal`.** Medido: `internal` compila e opera
igual, e público acrescenta um tipo à superfície publicada deste módulo sem que
nenhum consumidor precise nomeá-lo, porque o atributo no próprio vocabulário o
aplica onde quer que o tipo seja serializado. **Nenhum dos testes de
arquitetura percebe esse acréscimo**, então a contenção tem de ser a decisão, e
não o portão.

**O oráculo é ida e volta byte a byte, e ele é necessário e insuficiente.**
Igualdade estrutural de `ClassPolicyDefinition` não serve: o tipo é referência
sem igualdade por valor por decisão documentada, porque a identidade dele é o
hash do conteúdo publicado. A ida e volta, porém, compara o serializador contra
ele mesmo, e fica verde sobre documento que a autoria recusaria. Por isso o
teste tem um segundo braço, que submete o documento produzido ao parser
canônico e assere a **recusa**.

**Assimetria de contrato declaradamente aberta.** As duas formas de duração já
coexistiam antes do conversor e não são regressão dele: o documento de política
soletra duração como número inteiro de segundos seguido de `s`, e o serializador
escreve a forma de intervalo do framework, `00:10:00`. A consequência que
importa é que **este caminho nunca autora documento de política**: a autoria
corre pelo documento do operador e pelo parser manual, que produz o relatório de
checagens por campo que um serializador não produz. O segundo braço do oráculo
existe para impedir que as duas formas sejam confundidas, e ele fica vermelho no
dia em que alguém as reconciliar, nomeando a decisão em vez de escondê-la.

**Completude do vocabulário é afirmação separada da serialização.** Um conversor
serializa o vocabulário e não diz nada sobre a completude dele: acrescentar um
canal ao conjunto fechado dava 0 erros, 0 avisos e 0 vermelhos, com ou sem
conversor. Dois portões fecham isso, e cada um reprova sozinho: a lista
publicada tem de conter todo canal que o tipo declara, lido por reflexão e não
por lista à mão; e o vocabulário menos os canais de ponto de contato é
exatamente `push`, que está fora de propósito porque o roteamento dele vive em
token de dispositivo. O recorte é derivado e verificado, **nunca apagado**.

## Variáveis sensíveis: o que a mudança fecha e o que fica aberto

A declaração de quais variáveis carregam dado sensível saiu da identidade e
passou à versão, entrando no `content_hash` que a aprovação assina. O que ela
era antes: declaração de ator único, gravada na criação do template, fora do
hash canônico, sem mutador e por isso sem ato que a corrigisse. O que ela é
agora: edição de rascunho como qualquer outra, que registra o editor e por isso
recusa que quem a declarou publique a versão.

**O campo entra na forma canônica sempre, lista vazia inclusive.** Há
precedente de recorte condicional nos campos de layout, com o motivo escrito de
preservar bytes históricos de hash, e esse motivo não vale aqui: recortar faria
`[]` colidir com "campo inexistente", que é como o defeito volta disfarçado.
Consequência medida: os catorze hashes fixados em `ContentHashNeutralityTests`
mudaram, todos, e foram refixados de propósito. A janela para isso fecha na
primeira linha gravada que precise sobreviver a um redeploy; a partir daí um
deslocamento ali é defeito, não decisão.

**A guarda de não regressão vale em três chamadores, não em um.** Quem lê a
declaração é a versão publicada, então publicar uma versão que a largue reabre
a ingestão por barramento e para a máscara no mesmo instante. Fechar só a
publicação deixava duas portas abertas: o rollback, que é publicação em todo
sentido e cuja fonte é justamente a forma que declara menos que a em vigor, e a
validação de ensaio, que responderia verde sobre uma versão que a publicação
recusa. Os três leem a versão em vigor antes de validar.

**O que fica declaradamente aberto.**

1. **A lista omissa não é fechada por alternativa nenhuma.** O achado passa de
   "declaração de ator único, nunca aprovada" para "declaração aprovada,
   possivelmente incompleta". É progresso e não é fechamento. A metade que
   falta, `sensível-de-fato ⊆ lista`, não é fechável por código: detectar dado
   pessoal por padrão de conteúdo foi medido e recusado em decisão anterior, e
   essa recusa permanece. O que a substitui é ato humano sob quatro olhos.
2. **Conluio entre autor e publicador não é coberto por nada.** Quatro olhos
   exige duas pessoas distintas e não exige que a segunda discorde. Nenhuma
   checagem deste catálogo, nenhum portão de arquitetura e nenhuma trilha
   distingue uma aprovação lida de uma aprovação carimbada.
3. **Postura de transporte é decisão própria, e ela tem um defeito a responder
   primeiro.** Hoje declarar qualquer variável sensível fecha a ingestão por
   barramento para o template, sem que ninguém possa dizer o contrário. Trocar
   isso por uma postura explícita parece a evolução natural, mas uma postura
   imutável fixada na criação seria ato de ator único sem quatro olhos: uma
   postura erradamente permissiva ficaria permanentemente incorrigível. É a
   trava original movida de uma lista para um booleano, e quem abrir essa
   decisão responde a isso antes de escolher a forma.
4. **Promover `sensitive-variables-unused` a `Failed` está pendente de
   medição.** Ela avisa e não reprova porque a mesma forma é a de quem prepara
   canal ou locale que ainda não chegou, e ninguém mediu quantos casos legítimos
   de preparo existem. Sem esse número, reprovar troca um risco medido por um
   custo não medido.

**Satisfação vazia, medida e fechada no arranjo.** Esvaziar a lista no arranjo
de `SensitiveVariableValidationTests` deixava oito dos doze casos verdes, cinco
deles afirmando `Passed` e recebendo `Passed` de graça, porque a checagem
responde `Passed` sobre lista vazia. O arranjo agora afirma que a declaração
chegou à versão; sob a mesma mutação, nove dos doze reprovam. A vacuidade era da
suíte e não da checagem, e é assim que ela fica fechada.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
