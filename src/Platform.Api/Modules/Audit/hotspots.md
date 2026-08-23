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
- **Status**: resolved for the export side. The exporter writes those rows to
  a separate `unchained.ndjson.gz`, canonicalized at export time, and the
  manifest counts them and carries their digest; no hash is fabricated.
- **Review condition**: none open. Integrity of those rows is still only
  guaranteed from the WORM copy onwards, which is the accepted limit.

## The details column is outside the drift comparison

- **Assumption accepted**: the periodic verification compares each row's
  scalar columns against its canonical text, but not `details`. A row whose
  `details` column was edited without touching `canonical` keeps verifying
  clean.
- **Evidence**: `AuditTrailRow.CanonicalDrift()` lists the compared columns
  and states the exclusion. The column is `jsonb`, which re-serializes on
  read (key order, whitespace, numeric forms), so an exact comparison would
  raise integrity alarms about formatting rather than about tampering.
- **Owner**: Audit module maintainers.
- **Status**: accepted, with a consumer rule: evidence is read from
  `canonical`, never from `details`.
- **Review condition**: the audit read API. If it exposes `details` as proof
  rather than as a query surface, the comparison has to be reintroduced with
  a parsed-value oracle instead of a byte oracle.

## A daily slice is a sequence range, not literally a day

- **Assumption accepted**: the daily export carries the contiguous sequence
  range ending at the day's highest sequence, which can include a row whose
  occurrence instant belongs to the next day, and can leave a very late row
  of that day to the following slice.
- **Evidence**: `AuditExportPlanner` computes the high-water mark from the
  day and exports everything after the previous slice; the chain is built in
  sequence order under the partition advisory lock, and only a contiguous
  range can be replayed from a head hash to a tail hash without carrying a
  hash per line.
- **Owner**: Audit module maintainers.
- **Status**: accepted. Coverage stays complete and without duplication; the
  manifest window documents the day and the sequence range is the claim.
- **Review condition**: any change that lets an effect commit with an
  occurrence instant far behind the current one (bulk import, replay of an
  old bus offset) needs the stabilization delay reviewed against it.

## Object Lock immutability is not exercised by the test suite

- **Assumption accepted**: the tests assert that the bucket is created with
  Object Lock enabled and that every written object carries Compliance mode
  with a retention date, but no test proves that a delete is refused.
- **Evidence**: the local emulator records the lock attributes and still
  accepts a delete; no test depends on a denied deletion.
- **Owner**: Audit module maintainers with Platform Engineering.
- **Status**: accepted for the automated suite.
- **Review condition**: the pre-production smoke against real S3 and KMS,
  which is a prerequisite of the ninety-day gate the accepted integrity
  design requires before go-live.
