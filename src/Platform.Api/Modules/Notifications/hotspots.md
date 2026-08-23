# Notifications decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## Ingestion audit vocabulary lives module-locally

- **Assumption accepted**: the `notification.*` audit actions, the
  `notification` entity type and the `producer` actor type are declared in
  `Infrastructure/Auditing/IngestionAuditVocabulary.cs` instead of the Audit
  module's `Integration/V1` vocabulary.
- **Evidence**: the Audit contract accepts free strings
  (`Modules/Audit/Integration/V1/AuditEntry.cs`), and extending its constant
  vocabulary is an Audit-module change outside this unit's write boundary.
- **Owner**: Notifications module maintainers with Audit module maintainers.
- **Status**: accepted, pending promotion.
- **Review condition**: the next change that touches the Audit
  `Integration/V1` surface promotes these constants there and this module
  consumes them.

## Request fields not persisted at ingestion

- **Assumption accepted**: `locale`, `channelsHint` and `metadata` are
  validated, participate in the idempotency payload hash, and are not stored:
  the notification row carries exactly the accepted data-model columns.
- **Evidence**: the accepted data model omits those columns, and the pipeline
  stages re-read state from the store by design.
- **Owner**: Notifications module maintainers.
- **Status**: accepted.
- **Review condition**: the Core pipeline slice that needs locale or channel
  preference at render/route time must revisit the ingestion persistence
  before consuming them.
