# Audit module

## Boundary

- Keep one bounded context in this module: the transactional audit trail. It
  proves, for years, what happened, who caused it, over which exact content,
  and who looked at it afterwards.
- This module owns the `audit_event` and `approval` tables, the hash-chain
  integrity columns, the monthly partitioning of `audit_event`, the partition
  manager job, and the partition-coverage health check. The generic
  provisioning mechanics (window planning, idempotent partition creation, the
  coverage check implementation) live in
  `src/Platform.Api/Infrastructure/Partitioning/`; this module registers its
  schema and tables on that infrastructure and keeps the partition-closing
  steps (write revoke, WORM retention) as trail semantics that never leave
  the module.
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
| `src/Platform.Api/Modules/Audit/Infrastructure/` | `AuditDbContext` and migrations (schema `audit`), the transactional appender, partition manager, health check |
| `src/Platform.Api/Modules/Audit/AuditModule.cs` | service registration for this context |

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
  bits `year * 100 + month`), in the same transaction as the effect. Appends
  to one partition serialize; the chain never forks. Sequence holes from
  aborted transactions are legitimate and belong to the verification job's
  tolerance, not to the writer.
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
- Continuity between partitions (closing anchor in the WORM manifest, next
  partition linking to the anchored final hash) belongs to the partition
  closing cycle, not yet built; until then every partition starts at its own
  anchor.

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
- The partition manager keeps monthly partitions provisioned ahead of time
  (`Modules:Audit:PartitionManager`); the `audit-partitions` health check
  degrades the host while there is still time to act. A missing month makes
  every audit insert fail, which aborts every governed effect in the same
  transaction: that is the designed behavior, not an accident.

## Out of scope for now

Hourly chain verification with stabilization watermark, WORM export with the
KMS-signed manifest, partition closing anchoring, the `/v1/audit/*` read API
(reads must generate `audit.read` through it), and the dedicated database
roles. Do not work around their absence here.

## Error axis and logging

- The published contract throws on infrastructure failure and rejects invalid
  entries with argument exceptions; it never returns a business `Result`,
  because a governed effect without a trail must abort, not degrade.
- Loggers follow the repository dialect: `*.Logger.cs` files with
  source-generated extension methods, identifiers in English, message text in
  pt-BR, never personal data or rendered content in placeholders.

Update this file in the same change that alters the module boundary, public
contracts, the canonical form, the anchor rule, or the append-only guarantees.
