# ContactConsent module

## Boundary

- Keep one bounded context in this module: the source of truth for recipient
  profile, contact points, the consent ledger and device tokens. The hub owns
  this data; the registration system and internal tools feed it through the
  write surface.
- Keep invariants in the entities and pure functions under
  `src/Platform.Api/Modules/ContactConsent/Domain/`.
- Keep use-case orchestration in the slices under
  `src/Platform.Api/Modules/ContactConsent/Features/`.
- Sibling contexts read this module exclusively through the published
  contracts of `Modules.ContactConsent.Integration.V1`:
  `IRecipientDirectory` for reads and reveals, `IDeviceTokenLifecycle` for the
  provider-feedback invalidation write, and `ISuppressionLedger` for the
  provider-feedback suppression write. This module reads siblings
  exclusively through `Modules.Audit.Integration.V1` (transactional audit
  append). Never touch another context's data store or internal types.
- Platform infrastructure is a dependency, not a sibling: the outbox writer
  (`NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter`) and the
  envelope cipher (`NotificationHub.Api.Infrastructure.Cryptography.IEnvelopeCipher`)
  live outside `Modules.*` and never reference module types back.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/ContactConsent/Domain/` | profile, contact point, consent, device and suppression entities; channel, source and platform vocabularies; the per-channel accumulation rule; value normalization |
| `src/Platform.Api/Modules/ContactConsent/Features/` | vertical slices for this context |
| `src/Platform.Api/Modules/ContactConsent/Integration/V1/` | `IRecipientDirectory`, `ISuppressionLedger`, the degradation-aware read fallback, the canonical consent-purpose key, the snapshot records other modules consume (including the suppressions in force), the masked contact-point record, and the refusal vocabulary of the contact ingestion |
| `src/Platform.Api/Modules/ContactConsent/Infrastructure/` | persistence (schema `contactconsent`), value protection, transactional writer, invalidation events, snapshot cache, invalidation consumer, bus ingestion topology and dead-letter writer, the suppression ledger, authorization, problems |
| `src/Platform.Api/Modules/ContactConsent/ContactConsentModule.cs` | service registration and endpoint mapping for this context |
| `src/Platform.Api/Modules/ContactConsent/ContactConsentWorkerRole.cs` | composition of the `contact-consent` worker role, discovered by the worker host |
| `src/Platform.Api/Modules/ContactConsent/ContactsIngressWorkerRole.cs` | composition of the `contacts-ingress` worker role, discovered by the worker host |

Owned state: `recipient_profile`, `contact_point`, `consent` (append-only),
`device_token`, `suppression_signal` (append-only) and `suppression`. None is
partitioned, which is what lets their unique keys exist without a partition
column: the unique key over `suppression_signal.source_event_id` is the whole
idempotency of the delivery-feedback path, and the partial unique index over
the suppressions in force is what keeps a reversal from leaving a second row
standing. The platform `outbox` belongs to the
messaging infrastructure; this module only appends through the contract.

## Transactional invariant of every write

A write commits its entity changes, its outbox messages and its `audit_event`
appended through `IAuditTrail` with the raw `DbTransaction`, in one database
transaction or not at all
(`Infrastructure/Persistence/ContactConsentWriter.cs`). The audit append holds
the partition chain lock until the transaction ends, so the commit follows it
immediately. A declarative no-op has no business effect and records its trail
in its own short transaction.

Three kinds of outbox message ride that transaction. The cache invalidation
(`contact.changed` / `consent.changed`, destination `contacts-changed`,
message key = recipient id, claim-check payload with recipient id and contact
point id only) never leaves the hub. The outgoing consent event
(`araia.notification.consent_changed.v1`, destination
`NotificationHub.Api.Infrastructure.Messaging.OutgoingEventBus.Topic`, subject
= recipient id, body with recipient, channel, purpose, granted and source) is
the integration contract with the domains, and the outgoing suppression event
(`araia.notification.contact_suppressed.v1`, same destination and subject, body
with recipient, channel and reason) announces that the hub stopped addressing a
channel. Only a write that really suppressed appends it, so the announcement
happens once per decision and never once per reported refusal. All of them are
appended before the audit call, because the append holds the chain lock until
the transaction ends.

Who writes, and which consumed record carried the write, travel in
`ContactWriteContext`, an explicit parameter of every handler. A write with
provenance stamps the deduplication mark of the record inside that same
transaction: unlike the notification ingestion, a contact declaration carries
no unique business key, so the mark is the only guard against a redelivery
appending a second entry to the hash-chained trail.

## Contact values and PII

- A contact value is persisted as `value_enc` (envelope cipher, dedicated key
  scope `contact-consent`, never an application scope) plus `value_hash`
  (HMAC-SHA256 under a key derived from the platform master key with the
  module-owned label `contact-consent:value-hash`, never a plain digest).
- The plaintext only leaves the module through
  `IRecipientDirectory.RevealContactValueAsync`, decrypted inside the module:
  every plaintext egress is an explicit call site. It never appears in
  responses, logs, audit details or outbox payloads.
- A consumer that must **show** a contact instead of addressing it calls
  `IRecipientDirectory.MaskContactPointsAsync`, which decrypts and masks
  inside this module and hands out only the masked form, per channel, in one
  read for a set of contact points. The rule lives in
  `Infrastructure/Privacy/ContactValueMask.cs`: an e-mail keeps the first
  character of the local part and the whole domain, a phone keeps the last
  four characters and a leading country marker. The read deliberately answers
  for a point already stamped removed, marking it inactive, because a
  historical consumer asks where a message went, not where one would go now.
- Values are normalized before hashing (trim; lowercase for e-mail), so the
  deterministic hash serves equality search and the uniqueness of
  `(recipient, channel, value_hash)`.
- Device tokens are routing addresses: stored in clear by design, banned
  from logs and audit details, and absent from the snapshot, which exposes
  only registration ids and recency for the push fan-out. The token value
  leaves the module exclusively through
  `IRecipientDirectory.RevealDeviceTokenAsync`, transient at send time,
  never cached.

## Snapshot cache and degraded reads

- The hot-path read of `IRecipientDirectory` runs behind a cache-aside layer
  inside this module (`Infrastructure/Reads/CachedRecipientDirectory.cs`):
  snapshots sealed with the module's dedicated key scope in the module's own
  Redis (`Modules:ContactConsent:Redis`, TTL 24 h), so contact data never
  sits in a cache in the clear nor under an application key.
- Invalidation marks the entry stale instead of deleting it: a stale entry
  forces the next read back to the store while staying available as the last
  known value. The `contact-consent` worker role consumes `contacts-changed`
  and applies the mark; the effect is idempotent by design. That queue is the
  only invalidation path: a write never touches Redis directly, because a
  second write path would be exactly what this module forbids.
- The key carries a version segment (`recipient:v2:`). It moves whenever the
  stored snapshot shape changes: an entry written before the change would
  otherwise deserialize into a snapshot missing the member a caller now decides
  on. Moving the segment retires the whole generation at once and the old keys
  expire on their own lifetime.
- Degradation is the caller's declaration, per read: only a caller that asked
  for `RecipientReadFallback.LastKnown` receives the cached snapshot when the
  local read fails; every other caller sees the failure and its queue retry
  owns the degradation. The reveal read is never cached.

## Declarative semantics

- `PUT .../contact-points` declares the whole active set. A new
  (channel, value) inserts a row; the same value re-declared revives a removed
  row and applies `verified`; an active value absent from the declaration is
  stamped `removed_at`, never deleted, because the consent ledger anchors on
  the row. `(channel, value)` is immutable per row; a value change is a new
  row plus a removal.
- Profile preferences (`timezone` IANA, absent = `America/Sao_Paulo`;
  `locale`) ride on the contact-points declaration and apply only when
  present. There is no dedicated profile route in v1; the design defines only
  the three write routes of this module.
- `PUT .../consents` reconciles the desired state per (purpose, channel)
  against the latest ledger record: a difference in `granted` appends a new
  record (anchored on the newest active contact point of the channel);
  identical state is an idempotent no-op answering the state in force; the
  first declaration of a pair always records, even a revocation. The ledger
  rejects UPDATE and DELETE by trigger.
- **The purpose half of that pair is a canonical key**
  (`Integration/V1/ConsentPurpose.cs`): trimmed and lowercased, because casing
  and surrounding whitespace carry no meaning and two spellings of one purpose
  would otherwise open two independent lineages, where a revocation revokes
  nothing. The vocabulary itself stays open: a purpose is minted outside the
  hub, so a closed list would turn every new one into a deploy and would
  refuse an opt-out the declaring system is obliged to record. The aggregate
  canonicalizes on write, every resolution keys on the canonical form (which
  is what folds records written before that rule into a single lineage,
  the only repair an append-only table admits), the response and the outgoing
  announcement carry the key, and a consumer comparing its own purpose
  against the snapshot canonicalizes it first. Two spellings inside one
  request are the same pair declared twice and the validator refuses the
  request whole. The ledger read of `IContactHistory` is the exception on
  purpose: it answers what was declared, spelling included.
- `POST .../devices` registers a token and creates the profile row on first
  contact; re-posting the same token refreshes `last_seen_at` and
  `app_version` without duplicating and without an invalidation event.
  `invalidated_at` has exactly one writer: the provider feedback path,
  through `IDeviceTokenLifecycle.InvalidateDeviceTokenAsync`, which commits
  the stamp, the cache-invalidation event and the `device.invalidated`
  audit in one transaction and answers a repeated report as a declarative
  no-op with its own trail.

## Extension points (outside v1, with a named return trigger)

- **Suppression**: delivered. The write is `ISuppressionLedger` and the read
  rides `RecipientSnapshot.Suppressions`, a new member rather than a `V2`
  surface, because the resolution already happens once per notification. A
  bounded suppression (`until`) has no rule minting it yet; the column and the
  contract member exist so that one can arrive without a migration.
- **Device token invalidation** by FCM feedback: delivered through
  `IDeviceTokenLifecycle`; webhooks of the delivery-feedback phase reuse the
  same contract, never a parallel write path.

## Bus ingestion of declarations

- The `contacts-ingress` worker role consumes `contacts.events.v1`, one record
  at a time, and owns the transport only:
  `araia.contact.contact_points_declared.v1` binds to the command of the
  contact-points route and `araia.contact.consents_declared.v1` to the command
  of the consents route, both through the same validator the route's filter
  runs and the same handler it calls. `subject` and record key are the
  recipient id. Device registration has no event: the token is registered by
  the app, never declared by the registration system.
- A role of its own, separate from `contact-consent`, which keeps consuming
  the invalidation queue. The two scale on different signals, consumer lag
  against queue depth, and the bus consumer registers a gate health check for
  the whole process. It writes, so it composes no read surface.
- Authorization is the broker ACL plus the accepted-source list of the role
  (`Modules:ContactConsent:KafkaIngress:AcceptedSources`), validated at boot:
  an empty list refuses the boot. The list is the actor vocabulary of this
  transport, the counterpart of the token's `appid` on the REST side, and the
  accepted source is what the trail and the consent ledger record as actor.
- Outcomes settle as the transport can act on them: applied is processed, a
  concurrency conflict holds the partition for a retry, and everything that
  can never be applied goes to `contacts.events.dlt` with a reason from
  `Integration/V1/ContactIngestionRejectionReasons.cs`.
- The dead-letter body is **always** a summary rebuilt from an allow-list,
  never the refused body: every record on this topic carries a contact value in
  the clear by construction, and the dead-letter topic retains fourteen times
  longer than the entry topic. The keyed hash never travels either, because it
  is deterministic and would hand out a stable correlatable pseudonym. The
  accepted consequence is that this topic pair has no redrive; the repair is
  the emitting system publishing the correct state again.

## Suppression ledger

- `ISuppressionLedger.ReportDeliveryFeedbackAsync` takes one refusal a provider
  reported about a destination and answers `SignalRecorded`,
  `ContactSuppressed` or `AlreadyApplied`. The reporter names the contact point
  it already addressed; it never sends a contact value and never decides the
  consequence.
- **The accumulation rule lives here** (`Domain/ContactSuppression.cs`): e-mail
  suppresses on the first definitive refusal, because a mailbox the provider
  declares nonexistent does not come back and every further message spends
  sender reputation. Every other channel requires two refusals inside a week,
  counted back from the newest one, because a number can be refused for reasons
  that pass and removing a reachable channel on one such refusal is worse for
  the recipient than the extra message. Deciding this needs the history of
  refusals of the contact point, and that history is contact data: exporting it
  so a caller could decide would export what this module exists to hold.
- **Idempotency is the unique key over `source_event_id`, not the check before
  the insert.** The check is the fast path; concurrent redeliveries both read
  absent and the constraint is what stops the second one from counting a
  refusal that never happened. A repeated report is a declarative no-op with
  its own trail and no second effect.
- A write that suppresses commits the signal row, the suppression row, the
  cache-invalidation message, the outgoing announcement and the audit event in
  one transaction, through the same transactional writer as every other write.
  A report that only records a signal commits no cache event, because the
  snapshot did not change.
- **Reversal is a named human act.** `POST /v1/recipients/{recipientId}/suppressions/{contactPointId}/removal`
  carries its own role (`Contacts.Suppression.Manage`) and its own rate-limit
  policy, requires a justification and records `suppression.removed` with the
  actor. The registration system's write role does not carry it. The row is
  stamped, never deleted: why a message was not sent on a given day has to
  survive the reversal.

## Audit vocabulary

Actions follow the platform dot vocabulary: `contact.points.declared`,
`consents.declared`, `device.registered`, `device.invalidated`,
`suppression.signal.recorded`, `suppression.added`, `suppression.removed`;
entity types `recipient`, `device_token` and `contact_point`; actor type `system` for `appid` principals
and `user` for human identities, with the stable id from the token. The
invalidation write records actor id `dispatcher`, the reporter of the
provider feedback. The constants live in
`Infrastructure/Auditing/ContactConsentAuditVocabulary.cs`; promoting them
into the Audit `Integration/V1` vocabulary is a pending cross-module decision.

## Error axis

Handlers return `Result<T>` with every business outcome as data inside the
response union, and the endpoint maps each case to RFC 9457 problems
(`Infrastructure/Http/ContactConsentProblems.cs`). Problem `type` values are
stable codes: `recipient-id-invalid`, `recipient-not-found`,
`no-contact-point-for-channel`, `contact-point-not-found`,
`writer-identity-required`, `concurrent-update-conflict`.

## Security and tests

- Every route requires the `Contacts.Write` app role (policy `contacts-write`)
  and the named rate-limit policy, except the suppression reversal, which
  requires `Contacts.Suppression.Manage` (policy and rate limit
  `contacts-suppression-removal`): re-opening a channel an automatic decision
  closed is not an ordinary contact write, and the registration system has no
  business performing it.
- Never bind HTTP bodies to domain types; never log contact values, device
  tokens or consent evidence beyond identifiers and counts.
- Start with a failing behavior test; keep the transactional invariant, the
  append-only ledger, the encrypted-at-rest guarantee and the published
  contract covered by integration tests.

Update this file in the same change that alters the module boundary, the
published contract, the declarative semantics, or the PII rules.
