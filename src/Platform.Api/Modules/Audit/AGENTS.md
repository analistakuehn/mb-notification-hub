# Audit module

## Boundary

- Keep one bounded context in this module: the transactional audit trail. It
  proves, for years, what happened, who caused it, over which exact content,
  and who looked at it afterwards.
- This module owns the `audit_event` and `approval` tables, the hash-chain
  integrity columns, the monthly partitioning of `audit_event`, the WORM
  export of the trail, the periodic chain verification and its checkpoint
  table, the partition closing cycle, and the health checks over both. The
  generic provisioning mechanics (window planning, idempotent partition
  creation, the coverage check implementation) live in
  `src/Platform.Api/Infrastructure/Partitioning/`; this module registers its
  schema and tables on that infrastructure and keeps the partition-closing
  steps (write revoke, export, retention) as trail semantics that never leave
  the module.
- The maintenance jobs run in the `audit-maintenance` worker role and nowhere
  else. A request-serving host keeps the `audit-partitions` health check,
  because it must see the coverage running out, and hosts none of the jobs:
  revoking grants and detaching partitions is not work that may run once per
  replica.
- Do not read or write another context's data store, infrastructure types, or
  mutable domain types. Subject identities arrive already composed in the
  producing context's naming; this module never models foreign aggregates.
- Publish cross-context capability as distinct, versioned contracts under
  `src/Platform.Api/Modules/Audit/Integration/V1/`.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `src/Platform.Api/Modules/Audit/Domain/` | audit event and approval entities, hash-chain arithmetic (canonical form, anchor, link) |
| `src/Platform.Api/Modules/Audit/Integration/V1/` | `IAuditTrail` (transactional append and approval), `AuditEntry`, `ApprovalGrant`, audit vocabulary constants |
| `src/Platform.Api/Modules/Audit/Infrastructure/` | `AuditDbContext` and migrations (schema `audit`), the transactional appender, partition maintenance and closing cycle, WORM export, chain verification, health checks |
| `src/Platform.Api/Modules/Audit/Infrastructure/Worm/` | the module-owned write-once store contract and its object-store implementation |
| `src/Platform.Api/Modules/Audit/AuditModule.cs` | service registration for a request-serving host (persistence, trail contract, coverage health check) |
| `src/Platform.Api/Modules/Audit/AuditMaintenanceWorkerRole.cs` | composition of the `audit-maintenance` worker role (provisioning, export, closing cycle, verification) |

## Transactional append contract

- Every governed effect records its `audit_event` **in the same database
  transaction** as the effect. `IAuditTrail` receives the caller's raw
  `DbTransaction` and inserts with parameterized SQL on that transaction; an
  asynchronous or separate-transaction trail is a defect.
- The caller's shape: open an explicit transaction, run its own
  `SaveChanges`, call `RecordApprovalAsync` (when the effect carries an
  approval) and then `AppendAsync`, and commit immediately. The append takes
  the chain advisory lock, and that lock is held until the transaction ends,
  so anything placed between append and commit stretches the serialization
  window of the whole partition.
- **The caller's transaction must be READ COMMITTED**, and the writer refuses
  anything else: it checks the level the caller declared before it sends a
  statement and the level the server reports for the running transaction. A
  stronger level takes its snapshot on the first statement of the transaction,
  before the lock is granted, so the append would read a chain tail older than
  the commit of the appender it waited for and fork the chain. Nothing at the
  call site announces that dependency, which is why it is enforced instead of
  documented.
- The window is three round trips: the lock together with the sequence value,
  the previous hash on its own, and the insert. The previous hash never joins
  the lock statement, for the same snapshot reason; the sequence value does,
  because it reads no snapshot and is evaluated after the lock is granted,
  which keeps sequence order equal to chain order.
- Both DbContexts stay isolated: the producing module never maps the audit
  tables, and this module never reads a producer's model. Shared database
  deployment does not imply shared data ownership; the append shares only the
  caller's transaction, which is exactly why every producer must point at the
  same physical database as this module.

## Hash chain

- Scope: one chain per monthly partition of `audit_event` (partition key
  `occurred_at`); there is no global sequence.
- Each chained row stores `canonical` (plain text, the exact UTF-8 bytes that
  were hashed; text on purpose, `jsonb` would rewrite the bytes), `prev_hash`,
  and `hash = SHA-256(prev_hash ‖ canonical)`.
- Canonical form (produced by `Domain/AuditChain.cs`): compact JSON, object
  keys in ordinal order, `details` embedded canonicalized, fields `action`,
  `actorId`, `actorType`, `application` (JSON null when absent), `details`,
  `entityId`, `entityType`, `id` (lowercase UUID), `occurredAt` (UTC,
  microsecond precision, `yyyy-MM-ddTHH:mm:ss.ffffffZ`), `seq`. Timestamps are
  truncated to microseconds before hashing so the canonical text and the
  stored `timestamptz` describe the same instant.
- Concurrency: `prev_hash` is read after `pg_advisory_xact_lock` over the
  partition of the occurrence month (key: high 32 bits `0x41554449`, low 32
  bits `year * 100 + month`), in the same transaction as the effect, and in a
  statement that begins after the lock statement returned. Appends to one
  partition serialize; the chain never forks. Sequence holes from aborted
  transactions are legitimate and belong to the verification job's tolerance,
  not to the writer.
- The tail read carries `hash IS NOT NULL` literally, and that predicate is
  what makes the partial tail index match. Editing the statement to drop it
  returns the same row and quietly costs the index, which turns the hold
  window back into a scan of the whole month.
- **Anchor rule**: the first chained event of a partition links to the
  deterministic anchor `SHA-256("notification-hub:audit-chain:{partition}:anchor")`,
  for example `notification-hub:audit-chain:audit_event_2026_08:anchor`. A
  verifier rebuilds it from the partition name alone, and the preimage shape
  keeps it outside the value space of real links.
- **Pre-chain rows**: rows written before the chain existed keep `canonical`,
  `prev_hash`, and `hash` absent; the check constraint
  `ck_audit_event_chain_complete` pins every row to all-or-none of the three.
  Nothing retroactive is fabricated; those partitions receive WORM protection
  at export because no chain vouches for them, and the chain of a partition
  that already holds pre-chain rows still starts at the partition anchor.
- **Continuity between partitions** is the manifest chain in the WORM store,
  never a `prev_hash` that crosses a partition. Every partition is a
  self-contained chain starting at its own deterministic anchor, and each
  exported manifest references the key and the tail hash of the manifest
  before it, including across the partition boundary. Tying the first link of
  a month to the previous month's final hash would couple the start of a
  month to a value that is still moving inside the stabilization window.
- **Column drift**: the chain covers the canonical text, so editing a column
  beside it would leave every hash valid. The verification compares the
  scalar columns of each row against its canonical text and reports the one
  that drifted. The `details` column is out of that comparison, because
  `jsonb` re-serializes on read and an exact comparison would raise integrity
  alarms about formatting; evidence consumers read `canonical`.

## Persistence and migrations

- Schema `audit`, history table `audit.__EFMigrationsHistory`, design-time
  factory `AuditDbContextFactory`.
- The first migration **adopts** the tables the TemplateManagement history
  creates: it moves `audit_event` (parent, partitions, sequence, indexes) and
  `approval` into the `audit` schema, re-points the append-only triggers to
  `audit.reject_append_only_mutation()`, and adds the chain columns. Apply
  TemplateManagement migrations before this module's; on any database the
  order is the same, and the adoption fails loudly when the source tables are
  missing.
- Both tables stay append-only by construction: row triggers reject `UPDATE`
  and `DELETE` (including on the chain columns). TRUNCATE and owner-issued DDL
  remain possible until the dedicated database roles arrive in a later phase.
- Indexes of `audit_event` are declared on the partitioned parent, never on a
  partition: PostgreSQL propagates a parent index to every partition, current
  and future, which is what keeps the month the provisioner creates ahead of
  time from being born without them. On a populated database, creating one is
  a maintenance window, because a concurrent build exists neither for a
  partitioned table nor inside a migration transaction.
- Two partial indexes cover the sequence, and the split between them is the
  chain boundary: `ix_audit_event_chain_tail`, `(seq DESC) WHERE hash IS NOT
  NULL`, and `ix_audit_event_prechain_seq`, `(seq) WHERE hash IS NULL`. A
  partial index only answers a statement that carries its predicate, so every
  read that walks a partition by `seq` states which side of the chain boundary
  it wants. The pre-chain index costs nothing to keep: those rows are a closed
  set, so an inserted row never matches its predicate and never enters it.
- The partition manager keeps monthly partitions provisioned ahead of time
  (`Modules:Audit:PartitionManager`); the `audit-partitions` health check
  degrades the host while there is still time to act. A missing month makes
  every audit insert fail, which aborts every governed effect in the same
  transaction: that is the designed behavior, not an accident.
- The migration creates the `audit_appender` grant role (NOLOGIN) with
  `INSERT` and `SELECT` on the trail, plus default privileges so a new
  partition carries the same grant. Login users per environment are
  provisioned by infrastructure and granted into the role.

## WORM export

- One engine, two triggers. The daily export slices a partition by day; the
  closing export restates the whole partition and is the authoritative
  artifact. Keys are deterministic
  (`audit-export/v1/{table}/{partition}/{daily/{yyyy-MM-dd}|closing}/`), so a
  rerun addresses exactly the objects the first run wrote and skips them when
  the digest matches.
- The events object is NDJSON with the stored `canonical` text **byte for
  byte**, one line per row, in chain order. Nothing is reparsed or
  re-serialized: the hash covers those exact bytes. Pre-chain rows travel in
  a separate object, canonicalized at export time, with no hash fabricated
  for them.
- The reader the export and the verification share fetches each side of the
  chain boundary with its own statement and merges them by `seq`, walking by
  key in blocks. Only the fetching is split: the rows, their order and their
  bytes are what one statement returned before, and nothing about the objects,
  the manifest or the tolerance for sequence holes depends on it. A block takes
  its own snapshot, which is why the stabilization watermark of the
  verification and the high-water mark of the export stay the bound on what a
  pass may claim.
- The authoritative claim of an export is its sequence range, not its
  calendar window. A daily slice carries the contiguous range that ends at
  the day's highest sequence, which keeps the segment replayable even when an
  effect commits with an occurrence instant older than one already committed.
- The manifest carries the anchor, the head and tail hashes, the sequence
  range, the counts, the digests, and the reference to the previous manifest.
  It contains no clock and no run identifier, because a rerun must produce
  the same bytes. The attestation next to it signs the digest of the manifest
  and names the key and the algorithm; the public key is archived in the same
  bucket at the first export.
- Verification from the bucket alone (`WormExportVerifier`) is the contract
  that matters: check the signature with the archived key, check the digests,
  replay the chain from the head to the tail, and follow the manifest
  references backwards. The platform runs exactly this code before it
  detaches or drops anything.

## Closing cycle

Order is the contract, and each stage that fails stops the cycle where it is:

1. revoke writes on the closed partition (`Modules:Audit:PartitionManager:EnableRevokeOnClosedPartitions`);
2. verify the whole partition from its anchor, green required;
3. export the partition (closing export);
4. verify the copy by reading the objects back and replaying them;
5. record the closing in the trail with the manifest key and the tail hash;
6. `DETACH`, never before step 4 succeeded;
7. drop, only behind `EnableDropDetachedPartitions`, which is off by default,
   only past the database residency, and only after the copy verifies again.

Revoking the grant alone does not stop a write: a row inserted through the
partitioned parent is routed to its partition and the privilege checked is
the parent's. The closing step therefore also installs a row-level trigger
that refuses inserts on the closed partition, which is what the accepted
immutability design prescribes.

`EnableRetentionCycle` governs up to `DETACH` and never authorizes
destruction; the drop has a gate of its own on purpose. Exporting evidence is
additive and reversible, dropping a table is not, and the two must never
share one switch.

## Out of scope for now

The `/v1/audit/*` read API (reads must generate `audit.read` through it), the
pseudonymized export for the regulator, the monthly evidence reports, and the
lifecycle rules of the bucket. Do not work around their absence here.

## Error axis and logging

- The published contract throws on infrastructure failure and rejects invalid
  entries with argument exceptions; it never returns a business `Result`,
  because a governed effect without a trail must abort, not degrade.
- Loggers follow the repository dialect: `*.Logger.cs` files with
  source-generated extension methods, identifiers in English, message text in
  pt-BR, never personal data or rendered content in placeholders.

Update this file in the same change that alters the module boundary, public
contracts, the canonical form, the anchor rule, or the append-only guarantees.
