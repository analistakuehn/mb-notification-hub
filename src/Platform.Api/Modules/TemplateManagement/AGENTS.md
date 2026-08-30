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
  already normalized text, guard the destination, hash what is left.
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
  demands all four steps of that file instead of the presence of one call. Two
  residues are known, measured, and written into those tests: a composer that
  neither drives the sandbox nor names the wrapper or the normalizer escapes all
  three, and the first rule anchors on the identifier `engine`, which is a
  parameter name, so a renamed receiver escapes it.
- **The order is pinned by unit tests that read the policy directly**, not only
  by the render behavior tests. Three of them pass under a policy that runs the
  same four steps in the wrong sequence, so the ones that matter are the two
  about precedence: the ban answers before the destination guard, and the
  normalizer runs before the ban. A change that reorders the four steps has a
  gate, and it is not the architecture scan, which reads presence and never
  order.
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
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
