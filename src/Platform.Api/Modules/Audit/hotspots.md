# Audit decision hotspots

Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in the interactive task or an ephemeral discovery inventory.

## Advisory-lock serialization per partition

- **Assumption accepted**: chain appends serialize on one advisory lock per
  monthly partition, so audit-writing throughput inside a month is bounded by
  that lock.
- **Evidence**: the appender takes `pg_advisory_xact_lock` before reading
  `prev_hash` (`Infrastructure/AuditTrail/TransactionalAuditTrail.cs`); the
  serialization consequence is acknowledged in the accepted integrity design.
  The contention probe (`tests/Platform.PerformanceTests`) measured it on a
  local container with four concurrent appenders and a partition of ten
  thousand rows: appenders writing to distinct monthly partitions wait 0.62 ms
  at the median, appenders on one partition wait 15.4 ms, and both hold the
  lock for the same 5 ms to 6 ms. Sampling `pg_stat_activity` during the same
  arms found 1,853 samples on `Lock/advisory` for the shared partition and none
  for the distinct ones. Waiting grows while holding stays flat, which is the
  signature of the lock rather than of a saturated database.
- **Owner**: Audit module maintainers.
- **Status**: accepted, gated, and measured. The fallback is not scheduled: the
  measurement says the cheap corrections come first, because the hold window
  itself is dominated by a read that no index answers (see the next entry).
- **Read the table carefully at high volume**: above ten thousand rows the
  control arm holds the lock *longer* than the treatment arm (675 ms against
  318 ms at two million). That is not an anomaly. Without an index, four
  appenders on four partitions run four concurrent scans, while four appenders
  on one partition serialize and keep a single scan hot in cache: the
  serialization was protecting the database. The methodological consequence is
  the boundary of the result. The contention delta is only clean where the scan
  is cheap, which is the ten thousand row volume, and that is where it was
  taken. Once the tail index lands, redoing the isolation at high volume is
  cheap and confirms the signature without the noise of the scan.
- **Review condition**: re-measure on representative infrastructure once the
  tail index is applied. The local bench cannot decide the absolute rule, since
  even the arm where the lock never disputes reaches only about 160 appends per
  second on one partition, well under the planning demand. What transfers from
  the bench is the shape: the ratio between arms, and how the hold window moves
  with the size of the partition. The sub-budget of the acceptance path stays
  open and is decided on representative infrastructure; the tail of this bench
  belongs to the host, so no percentile measured here approves it.

## The chain tail read has no index, and the obvious composite does not help

- **Risk, measured**: every append reads the tail of its partition inside the
  advisory lock, and no index answers that read. The table carries a primary key
  on `(id, occurred_at)` and one secondary index on `(entity_type, entity_id)`;
  nothing indexes `seq`. With partition pruning the plan is a parallel
  sequential scan of the whole monthly partition plus a top-N sort, taken with
  the lock already held, so the hold window grows with the partition and the
  cost of a month is quadratic.
- **Evidence**: `Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs`
  declares the only secondary index; `LastChainedHashSql` in
  `Infrastructure/AuditTrail/TransactionalAuditTrail.cs` orders by `seq DESC`.
  The probe read the plan of that exact statement over a partition of two
  million rows: without an index it runs in 330 ms touching 181,230 buffers;
  with a partial index on `(occurred_at, seq DESC)` the planner ignores the
  index and runs the same scan in 371 ms; with a partial index on `(seq DESC)`
  it runs in 0.174 ms touching 4 buffers. A composite index led by the
  partition key cannot help twice over: pruning already satisfied the time
  predicate, so the leading column is a useless prefix, and the remaining
  predicate is a range rather than an equality, so the composite offers no
  ordering by sequence inside it. The same missing index affects the hourly
  verification and the export by sequence range, which also walk the partition
  ordered by `seq`, so the gain is not confined to the hot path.
- **Ratified shape**: `(seq DESC) WHERE hash IS NOT NULL`. The partial
  predicate has to appear literally in the query for the planner to match the
  index, and it does today: any edit that drops `hash IS NOT NULL` from the
  appender's statement silently costs the index.
- **Effect on the hold window**: without the index the median hold measured
  5 ms to 7 ms at ten thousand rows, 73 ms to 75 ms at five hundred thousand,
  and 232 ms to 375 ms at two million. With the index, and with the lock and
  the sequence value folded into one round trip, the median hold measured
  2.45 ms, 2.52 ms, and 2.02 ms at the same three volumes: flat, because the
  read stopped depending on the size of the partition.
- **Owner**: Audit module maintainers with Engineering.
- **Status**: open. The probe measures the index in an arm of its own and never
  applies it; creating it is a migration and belongs to its own change.
- **Review condition**: apply the index before deciding anything about
  sub-chains, then re-measure. Deciding on sub-chains against the current shape
  would compare against a scan nobody intends to keep.

## The lock statement cannot also read the tail

- **Risk, measured**: folding the tail read into the statement that takes the
  advisory lock forks the chain. The probe measured 6,707 forked links out of
  8,711 rows with the folded shape, and none with the read in a statement of
  its own.
- **Evidence**: under READ COMMITTED a statement takes its snapshot when it
  starts, which is before it blocks on the lock. A statement that waits and
  then reads the trail therefore reads a snapshot older than the commit of the
  appender it waited for, so the tail comes back stale and the next link points
  at the wrong predecessor. Only a statement that begins after the lock
  statement returned sees the predecessor's row. The probe's verification
  scenario replays the whole partition and is what caught it.
- **Depends on the isolation level, and the dependency is not enforced yet**:
  four round trips fold to three only because the caller runs in READ
  COMMITTED, where each statement takes a fresh snapshot with the lock already
  held. A caller in REPEATABLE READ or SERIALIZABLE takes its snapshot on the
  first statement of the transaction, before the lock, and the stale read comes
  back even with the statements separated. Today the driver's default saves the
  chain by accident rather than by design. The guard belongs in the writer:
  check the isolation level and refuse anything that is not READ COMMITTED,
  with the reason in the XML doc.
- **Owner**: Audit module maintainers.
- **Status**: recorded before any collapse is attempted in production code. The
  collapse and the isolation guard are one corrective change, not this one.
- **Review condition**: any future change that reduces round trips inside the
  window, and any change of isolation level on a writer that appends. Lock plus
  `nextval` fold safely, because `nextval` reads no snapshot and sits in the
  projection over the locked expression, which keeps sequence order equal to
  chain order. The tail read does not fold at all.

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
