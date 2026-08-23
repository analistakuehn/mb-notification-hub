# Audit decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## Advisory-lock serialization per partition

- **Assumption accepted**: chain appends serialize on one advisory lock per
  monthly partition, so audit-writing throughput inside a month is bounded by
  that lock.
- **Evidence**: the appender takes `pg_advisory_xact_lock` before reading
  `prev_hash` (`Infrastructure/AuditTrail/TransactionalAuditTrail.cs`); the
  serialization consequence is acknowledged in the accepted integrity design.
- **Owner**: Audit module maintainers.
- **Status**: accepted, gated.
- **Review condition**: the ingestion load test of the current phase (p99 of
  ingestion). If the gate fails, the planned fallback is sub-chains per
  `application` inside the partition.

## Pre-chain rows stay unchained until export

- **Assumption accepted**: rows written before the chain existed carry no
  `canonical`/`prev_hash`/`hash` and are not covered by any chain; their
  protection is the append-only trigger now and WORM at export.
- **Evidence**: `ck_audit_event_chain_complete` allows the all-null shape;
  the adoption migration fabricates nothing retroactively.
- **Owner**: Audit module maintainers.
- **Status**: accepted.
- **Review condition**: the export cycle implementation must include the
  pre-chain partitions in the WORM manifest.
