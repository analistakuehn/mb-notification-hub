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
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
