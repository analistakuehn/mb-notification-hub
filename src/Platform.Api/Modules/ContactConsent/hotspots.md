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
  in the accepted design for this phase.
- **Owner**: ContactConsent module maintainers.
- **Status**: accepted, pending review.
- **Review condition**: the push fan-out slice of the dispatch phase must
  decide whether a token registered by a newer recipient invalidates the older
  registration.

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
