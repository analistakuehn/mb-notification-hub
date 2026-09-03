# Specialty: Data Persistence and Caching

Load-on-demand .NET specialty pack covering MongoDB Driver, PostgreSQL (surface use), Redis and HybridCache, EF Core, Dapper, repository design, query performance, indexing, caching strategy, and data-consistency reasoning across mixed-persistence systems.

## Activation signals

- File paths: `**/Persistence/**`, `**/Infrastructure/Data/**`, `**/Migrations/**`, `**/Outbox/**`, `**/Inbox/**`, `*DbContext.cs`, `*Repository.cs`, `*Outbox*.cs`, `*Inbox*.cs`.
- Code symbols: `DbSet<`, `IMongoCollection<`, `IDbConnection`, `Dapper`, `IDistributedCache`, `HybridCache`, `IDatabase` (StackExchange.Redis), `IntegrationEventInbox`.
- Stack Profile axes: `persistence` lists `ef`, `mongo`, `dapper`, or mixed; `cache` lists `redis` or `hybrid`.
- Task language: query performance triage, repository design, cache strategy, persistence technology selection, data-consistency review, outbox or inbox implementation.

## Scope

Design, implement, review, and troubleshoot data-access and caching code. Optimize for clear data ownership, safe connection lifetimes, efficient queries, explicit consistency guarantees, and cache behavior that can be operated in production.

In scope: connection and lifetime management; query and cache correctness (N+1 prevention, projections, index strategy, TTL, invalidation, key naming); data consistency (transactions, optimistic concurrency, idempotency, retry safety); observability of data operations.

Out of scope: architecture outside persistence (`dotnet-architect`); general C# and maintainability outside data access (`dotnet-engineer` plus source-review); MongoDB depth (load `mongo.md`); PostgreSQL/PostGIS/pgvector depth (load `postgresql.md`); test-suite quality (`dotnet-testing`); and allocation or GC analysis (`dotnet-runtime-diagnostics`).

## Technical authority

### MongoDB Driver

`BsonClassMap`, attributes, custom serializers, value objects, strongly typed IDs. Aggregate-oriented repositories with `IMongoCollection<T>` encapsulation, filter / update / projection builders. Aggregation pipelines: stage ordering, early `$match`, `$lookup` cost, `$group` cardinality, pagination, memory and index implications. Index strategy: compound indexes for common filters and sorts, text indexes, TTL indexes, uniqueness, sparse and partial indexes. `MongoClient` as singleton; pool configuration; `ReadPreference`; `WriteConcern.WMajority`; `ReadConcern.Majority`; causal-consistency sessions. Transactions on replica set with session management, transient retry, transaction scope minimization. Change streams: resume tokens, filtered events, backpressure, idempotent consumers.

For schema design patterns, anti-patterns, Atlas Search, Vector Search, time-series collections, and `explain` execution-stats analysis, load `mongo.md`.

### Redis and HybridCache

Singleton `ConnectionMultiplexer`; lazy initialization; health checks; reconnection behavior. Structured key naming (`user:{id}:profile`); bounded-context prefixes; collision avoidance; versioned key formats. Serialization: JSON, MessagePack, Protobuf; compression trade-offs; cross-deploy compatibility. Cache patterns: cache-aside, write-through, write-behind, refresh-ahead. TTL management: absolute, sliding, jitter for thundering-herd avoidance. `IDistributedCache`, HybridCache L1/L2 behavior, stampede prevention. Pub/Sub: channel naming, message serialization, subscriber lifetime, backpressure, idempotency. Data structures: strings, hashes, sets, sorted sets, lists.

### EF Core

`DbContext` lifetime scoped by default; `IDbContextFactory<T>` for background work and cross-scope creation. `AsNoTracking()` for read-only paths; projections to DTOs; `AsSplitQuery()` for cartesian explosion; compiled queries for hot paths. N+1 prevention through eager loading, explicit loading, and projection-first design. Change tracking: snapshot vs notification, `DetectChanges()` cost, `ChangeTracker.Clear()` when appropriate. Migrations: code-first, idempotent scripts, data vs schema migrations, migration bundles. DDD integration: owned types, value converters, shadow properties, strongly typed IDs. Fluent API: `IEntityTypeConfiguration<T>`. Interceptors: audit trails, soft delete, multi-tenancy, query tagging, connection instrumentation.

### Dapper

Parameterized queries with `@param`; never string concatenation or interpolation for SQL values. Multi-mapping with `splitOn`, nested mapping, cartesian-product avoidance. Dispose `IDbConnection`; respect pooling; use async APIs. Buffered vs unbuffered streaming for large result sets. Batch operations with `Execute` plus `IEnumerable<T>` and explicit transaction wrapping. Stored procedures with `CommandType.StoredProcedure`, output parameters, return values. `SqlMapper.TypeHandler<T>` for value objects and strongly typed IDs.

## Cross-cutting concerns

- Repository abstraction quality fits the storage model. Do not expose `IQueryable` from EF Core or raw `IMongoCollection<T>` from application-facing abstractions.
- Connection and lifetime: avoid scoped-in-singleton capture; register singleton-safe clients correctly; dispose per-operation connections.
- Data consistency: document transactional boundaries, eventual consistency, optimistic concurrency, ETags, row versions, retry safety.
- Observability: instrument queries, cache hit/miss, slow-query thresholds, retry counts, data-operation failures.
- Security: parameterize SQL, avoid leaking secrets in logs, protect PII in cache and telemetry, respect tenant boundaries.

## Technology selection heuristics

| Scenario | Often fits | Reason |
|---|---|---|
| Rich aggregate persistence | MongoDB or EF Core | Document mapping or ORM tracking can preserve aggregate behavior. |
| High-performance read projections | Dapper | Direct SQL control and low overhead. |
| Hot distributed cache | Redis or HybridCache | Fast reads, TTL support, distributed coordination. |
| Event-sourced aggregate storage | MongoDB or purpose-built event store | Append and stream patterns can fit document or event storage. |
| Complex relational joins and reporting | EF Core or Dapper | Relational engines handle joins and set-based operations well. |
| Session or token storage | Redis | Fast lookup and natural expiry. |
| Search beyond simple text indexes | Dedicated search engine | MongoDB text indexes are useful for simple cases, not full-search relevance. |
| Embedding / semantic retrieval (RAG) | pgvector, MongoDB Atlas Vector Search, or a dedicated vector store | Dense-vector index, optionally hybrid with lexical search; load `postgresql.md` for pgvector or `mongo.md` for `$vectorSearch` mechanics. |

## Quality principles

- Never concatenate SQL. Always parameterize values.
- Never capture scoped services in singletons.
- Never skip index analysis for hot MongoDB or relational queries.
- Prefer projection-first reads; fetch only what the path needs.
- Use `AsNoTracking()` for EF Core read-only paths unless tracking is required.
- Set TTLs intentionally; unbounded cache entries need explicit justification.
- Define invalidation strategy before adding cache.
- Treat transient failures as normal in distributed data systems.
- Use compiled EF Core queries for proven hot paths, not as decoration.
- Keep migrations and schema changes reviewable, reversible where possible, and validated before deployment.

## Red flags

- Generic `IRepository<T>` over a storage that is intentionally aggregate-scoped.
- Lazy loading enabled "to fix" an N+1 (lazy loading masks N+1, not solves it).
- Redis used as a system of record.
- Bulk `ExecuteUpdateAsync` / `ExecuteDeleteAsync` without an expected rowcount assertion.
- Cache-key rename without a dual-write window.
- Consistency level downgrade (majority → local, snapshot → eventual) made silently.
- Schema migration via stop-the-world ETL when versioned-then-lazy migration fits.
- `MongoClient` instantiated per request.
- Cross-context queries that bypass module ownership.

## Auto-Clarity triggers (specialty-specific)

Surface and pause before applying any of the following:

1. **Irreversible data actions**: schema changes (EF migrations, Mongo collection layout, BSON discriminators, Redis key format); index drop, rebuild, or change of partial filter or TTL; bulk update or delete without explicit rowcount expectation; cache-key rename without dual-write window; consistency-level downgrade. Surface impact and require confirmation.
2. **Material ambiguity**: when the request admits two reasonable persistence shapes (EF tracked aggregate vs projection-only DTO, MongoDB embed vs reference, Redis cache-aside vs write-through, single transaction vs eventual consistency with retry), expose the trade-off and ask which workload property dominates (read/write ratio, consistency tolerance, latency budget) before writing code.
3. **User mistaken**: requests to add `IRepository<T>` to an aggregate-scoped codebase; lazy loading to "fix" N+1; Redis as system of record. Show the real cost first.
4. **Multi-step sequences**: index creation that depends on a migration that depends on a schema change; cache invalidation that depends on a publish step that depends on a transaction commit. A failed intermediate step requires diagnosis, not retry.
5. **Rule conflict**: proposed change violates `persistence` or `cache` axis in `.araia/stack-profile.yaml`, an existing ADR (e.g., persistence-per-bounded-context), or `~/.claude/CLAUDE.md`. Cross-context queries bypassing module ownership require ADR-level authorization.

## Deep references

| When | Load |
|---|---|
| Writing or reviewing MongoDB code (BSON setup, repositories, aggregations, indexes, transactions, change streams, DI) | [`mongo/code-standards.md`](mongo/code-standards.md) |
| Writing or reviewing module persistence in `mediator: none` projects | [`../../skills/dotnet-scaffold/references/persistence-selection.md`](../../skills/dotnet-scaffold/references/persistence-selection.md) plus the `ef`, `mongo`, `dapper`, `redis`, and `hybrid-cache` manifests under [`../../skills/dotnet-scaffold/templates/features/`](../../skills/dotnet-scaffold/templates/features/) |

## Validation checklist

- Correct connection and service lifetimes.
- Query shape, projection, and N+1 risk inspected.
- Index or query-plan implications named for hot paths.
- Cache key format, TTL, invalidation, and stale-data behavior explicit.
- Transaction, concurrency, and retry safety addressed.
- Observability for slow operations and cache effectiveness present.
- Tests, query-plan inspection, migration validation, or realistic spot checks performed or recommended.
- Schema, index, permission, or operational impact called out clearly.
