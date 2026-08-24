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
- **Status**: accepted, gated, and measured twice. The fallback is still not
  scheduled. The cheap corrections came first and landed: with the tail index
  in the schema and the window at three round trips, the median hold measured
  2.43 ms, 2.06 ms and 2.84 ms at ten thousand, five hundred thousand and two
  million rows, flat with the volume instead of growing with it. The round trip
  removed does not show in the median of this bench, but it shows in the tail
  under contention at two million rows: p99 window of 10.7 ms against 100.0 ms
  for the four round trip shape, and 426 appends per second against 235.
- **Read the table carefully at high volume**: above ten thousand rows the
  control arm held the lock *longer* than the treatment arm (675 ms against
  318 ms at two million). That is not an anomaly. Without an index, four
  appenders on four partitions ran four concurrent scans, while four appenders
  on one partition serialized and kept a single scan hot in cache: the
  serialization was protecting the database. The methodological consequence is
  the boundary of that result. The contention delta was only clean where the
  scan is cheap, which is the ten thousand row volume, and that is where it was
  taken. Now that the index is in the schema, redoing the isolation of control
  against treatment at high volume is cheap and confirms the signature without
  the noise of the scan; that pair has not been re-run yet.
- **Review condition**: re-measure on representative infrastructure once the
  tail index is applied. The local bench cannot decide the absolute rule, since
  even the arm where the lock never disputes reaches only about 160 appends per
  second on one partition, well under the planning demand. What transfers from
  the bench is the shape: the ratio between arms, and how the hold window moves
  with the size of the partition. The sub-budget of the acceptance path stays
  open and is decided on representative infrastructure; the tail of this bench
  belongs to the host, so no percentile measured here approves it.

## The chain tail read is indexed, and only the tail read is

- **Corrected, measured before and after**: every append reads the tail of its
  partition inside the advisory lock, and until 2026-08-24 no index answered
  that read. The table carried a primary key on `(id, occurred_at)` and one
  secondary index on `(entity_type, entity_id)`; nothing indexed `seq`. With
  partition pruning the plan was a parallel sequential scan of the whole
  monthly partition plus a top-N sort, taken with the lock already held, so the
  hold window grew with the partition and the cost of a month was quadratic.
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
- **Applied shape**: `ix_audit_event_chain_tail`, `(seq DESC) WHERE hash IS NOT
  NULL`, declared on the partitioned parent so PostgreSQL propagates it to
  every partition, current and future; the partition the provisioner creates
  three months ahead carries it from birth, which is what an index created per
  partition would not give. The partial predicate has to appear literally in
  the query for the planner to match the index, and it does today: any edit
  that drops `hash IS NOT NULL` from the appender's statement silently costs
  the index. Measured with the schema the migrations leave, over the same three
  volumes: the tail read is an `Index Scan` over the propagated index and costs
  0.040 ms reading 3 buffers, 0.042 ms reading 4, and 0.046 ms reading 4, flat
  with the size of the partition.
- **Creating it is a maintenance window on a populated database**, and the same
  holds for the pre-chain index that landed with it: the create takes a lock
  over the parent and builds the index on every attached partition. **A
  concurrent build does not exist for a partitioned table**, and does not exist
  inside a migration transaction either, so nobody should promise one in
  production; the path there is to build per partition outside the migration
  and attach.
- **Effect on the hold window**: without the index the median hold measured
  5 ms to 7 ms at ten thousand rows, 73 ms to 75 ms at five hundred thousand,
  and 232 ms to 375 ms at two million. With the index, and with the lock and
  the sequence value folded into one round trip, the median hold measured
  2.45 ms, 2.52 ms, and 2.02 ms at the same three volumes: flat, because the
  read stopped depending on the size of the partition.
- **Owner**: Audit module maintainers with Engineering.
- **Status**: applied and re-measured for the hot path. The probe now detects
  the index by reading the plan and stops creating one of its own, so the gate
  watches production's index. What the index does **not** serve is the next
  entry.
- **Review condition**: re-measure on representative infrastructure before
  deciding anything about sub-chains. The local bench decides the shape of the
  curve, never the absolute rule.

## The verification and the export read each side of the chain boundary

- **Risk, measured, then corrected**: the tail index is partial, and a partial
  index only matches a statement that carries its predicate. The range read
  that the periodic verification and the export share (`AuditTrailReader`) has
  to return pre-chain rows as well, which carry no hash, so it could not carry
  `hash IS NOT NULL` and was not served by anything. Same for the `MAX(seq)`
  the export planner asks for.
- **Evidence**: plans read over the current partition, batch of twenty
  thousand rows: 24.835 ms and 2,601 buffers at ten thousand rows, 438.547 ms
  and 170,438 buffers at five hundred thousand, 2,133.796 ms and 672,321
  buffers at two million, the last one with a `Gather Merge` and an external
  sort spilling to disk. The `MAX(seq)` costs 212.134 ms and 182,097 buffers at
  two million. Because a full replay advances in batches, the cost of the month
  was superlinear, and the full-replay curve did not improve when the tail
  index landed. Read that as a direction and nothing more: a full replay is a
  long IO-bound measurement on a shared host, so the difference in seconds
  between two runs describes the day, not the schema.
- **Owner**: Architecture with Audit module maintainers.
- **Status**: decided and applied. The read is split at the chain boundary, one
  statement per side, each carrying the predicate of the partial index that
  answers it, merged by `seq` in the reader and walked by key in blocks. The
  non-partial index that would have served every path from one structure was
  refused on cost direction: it would charge maintenance on every insert of the
  hottest table in the system, forever, to serve two background jobs that run
  hourly and daily. The pre-chain index is free by construction, because those
  rows are a closed set that never takes an insert, and it turns "this
  partition holds no pre-chain rows" into one lookup instead of a scan to prove
  an absence.
- **What has to be true for that decision to hold**: the ordering must be gone
  from the plan of the range read. It was the expensive half, because it
  carried the canonical text of every row of the partition through an external
  merge. If a future plan shows it back, the split failed and the recourse is a
  single non-partial index with the partial one removed, never both, and that
  needs ratification before it is applied.
- **Review condition**: re-measure the full-replay curve with the same
  instrument before the cadence of the periodic verification is fixed. The
  cadence itself changed shape: it is blocks per execution now, not how often a
  whole pass fits, which is what decouples it from the size of the partition.

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
- **Status**: applied in 2026-08-24. The window is three round trips, and the
  writer refuses any transaction that is not READ COMMITTED, checking both the
  level the caller declared, before it sends a statement, and the level the
  server reports for the running transaction. The first check is what stops a
  caller that chose a stronger level from even taking the lock; the second is
  what catches a server, database or role default that no call site mentions.
- **The refusal happens with the lock held, and that closes outside the code**:
  the server-reported level rides in the statement that already takes the lock,
  so a caller running under a level nobody declared is refused after the lock
  was granted. The lock is transaction scoped and falls with the rollback, so
  the danger is not the refusal, it is a transaction left open indefinitely,
  which is a connection problem rather than a writer problem. The control is
  `idle_in_transaction_session_timeout` set on the database or the role, not
  more code in the probative path.
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
