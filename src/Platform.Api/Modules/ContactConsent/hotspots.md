# ContactConsent decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## `removed_at` on contact_point extends the accepted data model

- **Assumption accepted**: `contact_point` carries a `removed_at` column that
  the accepted data model does not list. The declarative PUT must distinguish
  the active set from superseded values, and a physical delete is impossible
  because the append-only consent ledger holds a foreign key to the row.
- **Evidence**: `Infrastructure/Persistence/Configurations/ContactPointConfiguration.cs`
  (restrict FK from `consent`), the append-only trigger on `consent`, and the
  reconciliation in `Features/Mutations/DeclareContactPoints/`.
- **Owner**: ContactConsent module maintainers with Arquitetura.
- **Status**: accepted.
- **Review condition**: the next data-model revision either adopts the column
  or replaces it with an accepted alternative for distinguishing active
  contact points.

## Device token uniqueness is per (recipient, token)

- **Assumption accepted**: the unique key is `(recipient_id, token)`; the same
  token may momentarily exist under two recipients (device handed to another
  logged-in user) and nothing here reconciles that.
- **Evidence**: `DeviceTokenConfiguration.cs`; no cross-recipient rule exists
  in the accepted design for this phase. The dispatch slice landed without
  deciding cross-recipient invalidation: the fan-out reads each recipient's
  own registrations and a provider `UNREGISTERED` invalidates only the
  reported registration.
- **Owner**: ContactConsent module maintainers with Arquitetura.
- **Status**: accepted, still pending the cross-recipient decision.
- **Review condition**: the delivery-feedback phase must decide whether a
  token registered by a newer recipient invalidates the older registration.

## The snapshot dropped the token value in favor of a dedicated reveal

- **Assumption accepted**: `DeviceRegistration` no longer carries the token;
  the fan-out uses ids and recency only, and the dispatcher reveals the
  routing address at send time through `RevealDeviceTokenAsync`, never
  cached.
- **Evidence**: `Integration/V1/RecipientSnapshot.cs`,
  `Integration/V1/IRecipientDirectory.cs` and the accepted deviation of the
  dispatch slice: the narrower PII boundary makes every token egress an
  explicit call site.
- **Owner**: ContactConsent module maintainers with Arquitetura.
- **Status**: accepted (deviation recorded in the phase document).
- **Review condition**: a consumer that legitimately needs the token in the
  snapshot reopens the contract discussion as a new decision.

## Profile preferences write through the contact-points declaration

- **Assumption accepted**: `timezone` and `locale` are written as optional
  riders of `PUT /v1/recipients/{id}/contact-points` (applied only when
  present); there is no dedicated profile route.
- **Evidence**: the accepted REST surface of this module lists exactly three
  write routes, and the profile columns live with the contact declaration in
  the accepted data model.
- **Owner**: ContactConsent module maintainers.
- **Status**: accepted.
- **Review condition**: a consumer that needs to clear a preference (reset to
  default) or write the profile without touching contacts forces a dedicated
  route in a later revision.

## Consent in force is computed across removed contact points

- **Assumption accepted**: the state in force per (purpose, channel) is the
  latest ledger record reached through any of the recipient's contact points,
  including removed ones: a revocation stays effective after a value change,
  and a new value does not silently reset consent.
- **Evidence**: `Infrastructure/Reads/RecipientDirectory.cs` and the contract
  test proving a revocation survives a contact value change.
- **Owner**: ContactConsent module maintainers with Compliance.
- **Status**: accepted.
- **Review condition**: an explicit ruling from Compliance that consent must
  be re-collected when the contact value changes reverses this computation.

## The published contract gained a degradation-aware read overload

- **Assumption accepted**: `IRecipientDirectory` now carries a second
  `FindAsync` overload taking `RecipientReadFallback`, because the accepted
  degradation rule is class-dependent (critical and authentication flows may
  act on the last known snapshot; other classes must fail and retry) and the
  class is only visible to the caller. The addition is additive: the original
  member is untouched and the store-backed implementation ignores the
  fallback.
- **Evidence**: `Integration/V1/IRecipientDirectory.cs`,
  `Infrastructure/Reads/CachedRecipientDirectory.cs`, and the stage that
  declares the fallback in the Notifications module.
- **Owner**: ContactConsent module maintainers with Arquitetura.
- **Status**: accepted, pending ratification of the contract addition.
- **Review condition**: the next architecture review of the published
  contracts either ratifies the overload or replaces it with an accepted
  alternative for expressing per-read degradation tolerance.

## Invalidation marks the cache entry stale instead of deleting it

- **Assumption accepted**: the `contacts-changed` consumer rewrites the
  cached snapshot with a stale flag rather than deleting it, so the entry
  still serves as the last known value for callers that declared the
  fallback; the 24 h TTL is the staleness ceiling.
- **Evidence**: `Infrastructure/Reads/RecipientSnapshotCache.cs` and the
  accepted stale-while-revalidate rule for critical and authentication flows.
- **Owner**: ContactConsent module maintainers.
- **Status**: accepted.
- **Review condition**: a compliance ruling that a revoked consent must never
  be acted on, even under degradation, replaces the stale mark with a hard
  delete and removes the last-known fallback.

## The bus ingestion deduplicates in a single layer

- **Assumption accepted**: the `contacts-ingress` consumer guards redelivery
  with the offset mark alone, committed inside the transaction of the effect.
  There is no business-key layer behind it, unlike the notification ingestion,
  whose `(application, idempotency_key)` constraint is the stronger guard.
- **Evidence**: a declaration is desired state over an append-only ledger, and
  the handlers already answer a repeated declaration with zero writes
  (`ContactPointsUnchanged`, `ConsentsUnchanged`), so the declarative semantics
  is the business idempotency. What the mark protects is the trail: the no-op
  path calls `AppendStandaloneAuditAsync`, and without a mark a rebalance would
  fill the hash-chained trail with entries of an event already settled
  (`Infrastructure/Persistence/ContactConsentWriter.cs`).
- **Owner**: ContactConsent module maintainers with Arquitetura.
- **Status**: accepted.
- **Review condition**: an event type on this topic that carries a business key
  of its own may revisit the layering; without one, the mark stays inside the
  transaction of the effect.

## The dead-letter of the contact ingestion has no redrive

- **Assumption accepted**: the body published on `contacts.events.dlt` is a
  summary rebuilt from an allow-list, so the record is not a faithful copy and
  no redrive can replay it.
- **Evidence**: every record of the entry topic carries an e-mail address or a
  phone number in the clear by construction, and the dead-letter topic retains
  fourteen times longer; the keyed hash is deterministic, so publishing it
  would hand out a stable correlatable pseudonym
  (`Infrastructure/Consuming/ContactIngestionDeadLetterWriter.cs`). With
  declarative semantics the repair is the emitting system publishing the
  correct state again, idempotent by construction.
- **Owner**: ContactConsent module maintainers with Segurança.
- **Status**: accepted (recorded in the phase document).
- **Review condition**: a diagnosis need that the summary cannot serve reopens
  the choice, and any field added to the allow-list is a privacy decision, not
  a formatting one.

## Write vocabulary lives module-locally

- **Assumption accepted**: the `contact.points.declared`, `consents.declared`
  and `device.registered` actions and the `recipient` / `device_token` entity
  types are declared in `Infrastructure/Auditing/ContactConsentAuditVocabulary.cs`
  instead of the Audit module's `Integration/V1` vocabulary.
- **Evidence**: the Audit contract accepts free strings
  (`Modules/Audit/Integration/V1/AuditEntry.cs`), and extending its constant
  vocabulary is an Audit-module change outside this unit's write boundary.
- **Owner**: ContactConsent module maintainers with Audit module maintainers.
- **Status**: accepted, pending promotion.
- **Review condition**: the next change that touches the Audit
  `Integration/V1` surface promotes these constants there and this module
  consumes them.
