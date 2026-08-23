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
  `IRecipientDirectory` for reads and reveals, and `IDeviceTokenLifecycle`
  for the provider-feedback invalidation write. This module reads siblings
  exclusively through `Modules.Audit.Integration.V1` (transactional audit
  append). Never touch another context's data store or internal types.
- Platform infrastructure is a dependency, not a sibling: the outbox writer
  (`NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter`) and the
  envelope cipher (`NotificationHub.Api.Infrastructure.Cryptography.IEnvelopeCipher`)
  live outside `Modules.*` and never reference module types back.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/ContactConsent/Domain/` | profile, contact point, consent and device entities; channel, source and platform vocabularies; value normalization |
| `src/Platform.Api/Modules/ContactConsent/Features/` | vertical slices for this context |
| `src/Platform.Api/Modules/ContactConsent/Integration/V1/` | `IRecipientDirectory`, the degradation-aware read fallback and the snapshot records other modules consume |
| `src/Platform.Api/Modules/ContactConsent/Infrastructure/` | persistence (schema `contactconsent`), value protection, transactional writer, invalidation events, snapshot cache, invalidation consumer, authorization, problems |
| `src/Platform.Api/Modules/ContactConsent/ContactConsentModule.cs` | service registration and endpoint mapping for this context |
| `src/Platform.Api/Modules/ContactConsent/ContactConsentWorkerRole.cs` | composition of the `contact-consent` worker role, discovered by the worker host |

Owned state: `recipient_profile`, `contact_point`, `consent` (append-only),
`device_token`. None is partitioned. The platform `outbox` belongs to the
messaging infrastructure; this module only appends through the contract.

## Transactional invariant of every write

A write commits its entity changes, its cache-invalidation outbox messages
(`contact.changed` / `consent.changed`, destination `contacts-changed`,
message key = recipient id, claim-check payload with recipient id and contact
point id only) and its `audit_event` appended through `IAuditTrail` with the
raw `DbTransaction`, in one database transaction or not at all
(`Infrastructure/Persistence/ContactConsentWriter.cs`). The audit append holds
the partition chain lock until the transaction ends, so the commit follows it
immediately. A declarative no-op has no business effect and records its trail
in its own short transaction.

## Contact values and PII

- A contact value is persisted as `value_enc` (envelope cipher, dedicated key
  scope `contact-consent`, never an application scope) plus `value_hash`
  (HMAC-SHA256 under a key derived from the platform master key with the
  module-owned label `contact-consent:value-hash`, never a plain digest).
- The plaintext only leaves the module through
  `IRecipientDirectory.RevealContactValueAsync`, decrypted inside the module:
  every plaintext egress is an explicit call site. It never appears in
  responses, logs, audit details or outbox payloads.
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
  and applies the mark; the effect is idempotent by design.
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
- `POST .../devices` registers a token and creates the profile row on first
  contact; re-posting the same token refreshes `last_seen_at` and
  `app_version` without duplicating and without an invalidation event.
  `invalidated_at` has exactly one writer: the provider feedback path,
  through `IDeviceTokenLifecycle.InvalidateDeviceTokenAsync`, which commits
  the stamp, the cache-invalidation event and the `device.invalidated`
  audit in one transaction and answers a repeated report as a declarative
  no-op with its own trail.

## Extension points (outside v1, with a named return trigger)

- **Suppression**: the suppression ledger (`SUPPRESSION` in the system model)
  and its read are not part of `Integration/V1`. When the delivery-feedback
  phase introduces hard-bounce suppression, its read joins the contract as a
  new member or a `V2` surface; nothing here may fake it meanwhile.
- **Kafka ingestion** (`contacts.events.v1`): a later slice adds the consumer
  path; it must reuse the same handlers' reconciliation semantics, never a
  parallel write path.
- **Device token invalidation** by FCM feedback: delivered through
  `IDeviceTokenLifecycle`; webhooks of the delivery-feedback phase reuse the
  same contract, never a parallel write path.

## Audit vocabulary

Actions follow the platform dot vocabulary: `contact.points.declared`,
`consents.declared`, `device.registered`, `device.invalidated`; entity types
`recipient` and `device_token`; actor type `system` for `appid` principals
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
`no-contact-point-for-channel`, `writer-identity-required`,
`concurrent-update-conflict`.

## Security and tests

- Every route requires the `Contacts.Write` app role (policy `contacts-write`)
  and the named rate-limit policy.
- Never bind HTTP bodies to domain types; never log contact values, device
  tokens or consent evidence beyond identifiers and counts.
- Start with a failing behavior test; keep the transactional invariant, the
  append-only ledger, the encrypted-at-rest guarantee and the published
  contract covered by integration tests.

Update this file in the same change that alters the module boundary, the
published contract, the declarative semantics, or the PII rules.
