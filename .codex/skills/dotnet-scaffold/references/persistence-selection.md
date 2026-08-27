# Persistence Selection and Data Ownership

Choose persistence per Bounded Context and per access pattern. A solution can be
polyglot while each context keeps unambiguous ownership of its data. Persistence
per context is what makes polyglot storage possible; a universal `AppDbContext`
shared by every context removes both the ownership and the option.

## Engine by workload

| Workload | Candidate engine | Pattern |
|---|---|---|
| Transactional aggregates | Relational with EF Core | Concrete repository or module context, plus outbox |
| Measured query or write hot path | Dapper | Explicit parameterized SQL, verified plans |
| Read models and projections | MongoDB or a projected relational store | Subset, extended reference, computed |
| Market data and time series | MongoDB time-series collections | Bucket pattern with metadata and measurement |
| Event store for a ledger or strong audit | Dedicated event store, append-only collection, or append-only table | Stream per aggregate plus snapshots |
| Hot data and rate caching | Redis or HybridCache | Cache-aside with TTL and jitter |
| Session and token state | Redis | Absolute TTL plus sliding window |
| Outbox payloads | The same store as the write | Dedicated table or collection per module |
| Semantic retrieval | Vector store, or hybrid keyword plus dense vector | Retriever with reranker |
| Auditable structured retrieval | Knowledge graph | Traceable reasoning paths, at a latency cost |

The engine follows the access pattern, not preference. Selecting several
overlays does not justify using all of them in one feature; record the
access-pattern rationale for each.

## Option trade-offs

| Option | Prefer when | Required hygiene |
|---|---|---|
| EF Core | Relational invariants, aggregate writes, transactions, migrations, and optimistic concurrency dominate | Module-specific context and schema, explicit mappings, migrations, projections, no-tracking reads, indexes, concurrency token |
| Dapper | A measured hot path needs explicit SQL, or a read projection reads better as SQL | Parameterized SQL, explicit mapping, cancellation, verified query plans, tests against the real engine |
| MongoDB | Document-shaped aggregates, flexible read models, time-series data, or denormalized access dominate | Module-owned database and collections, explicit serializers, index registration at startup, document-size and consistency policy |

## Ownership

- Configure providers under `Modules:<Context>:Persistence:<Provider>`.
- Name contexts and factories after the module. Never create one universal
  context for every Bounded Context.
- Keep the schema, migrations, outbox, and transaction boundary with the
  producing module. Give each context its own migrations history table and
  invoke the tooling with an explicit context argument.
- Never expose a module's tables, collections, context, or repository to another
  module.
- Store credentials in user secrets or the deployment secret provider. Committed
  settings hold only non-secret local defaults.

## Query and write hygiene

Treat these as mandatory, not advisory:

- Read paths do not track entities. Tracking inflates the heap and taxes the
  next save with change detection.
- Project to the shape the caller needs instead of materializing whole entities
  to map three fields.
- Split queries when multiple includes would otherwise multiply rows.
- Compile queries on measured hot paths.
- Use set-based update and delete operations for bulk work rather than
  materializing entities.
- Give every mutable aggregate root a concurrency token and handle the conflict
  explicitly.
- Bound result sets and paginate deterministically.
- Design indexes from real query shapes, document the index next to the query
  that needs it, and verify index use and database time instead of inferring
  performance from the choice of ORM.

Transactions spanning two module contexts are forbidden. Schema-per-context
means cross-module atomicity does not exist: use the outbox and a saga with
explicit compensation. Never escalate to a distributed transaction coordinator.

Size the connection pool for the real workload. Several scoped contexts times
request concurrency exhausts a default pool quickly.

## Repositories and specifications

Avoid generic repositories and universal specification frameworks. Let a handler
use its module context directly when that reads clearest. Introduce a concrete
repository when aggregate persistence or a real seam earns it, and name it in
the ubiquitous language. The decision criteria live in
[`tactical-patterns.md`](tactical-patterns.md).

## Specialized stores

Vector stores, graph stores, event stores, and time-series databases are
capabilities with their own lifecycle, privacy, deletion, backup, and
consistency requirements. Add one only after its access pattern and operational
owner are explicit.

For retrieval over regulated data, define source provenance, tenant isolation,
retention, deletion, re-indexing, and leakage tests before ingesting anything.
Immutable event streams and the right to erasure conflict directly: resolve it
by encrypting personal data per subject and destroying the key on erasure, so
the remaining ciphertext is inert without rewriting the stream. Treat that as a
first-class design decision, not a footnote.
