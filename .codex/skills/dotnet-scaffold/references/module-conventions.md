# Strategic DDD and Module Conventions

A module is the packaging unit. A Bounded Context is the language and model
boundary. A module must host exactly one Bounded Context, and that equivalence
is a deliberate, verified decision, never an assumption inherited from the
folder tree.

Strategic DDD carries more weight than tactical DDD here. A well-drawn context
is the unit a human reads, a reviewer approves, a test suite covers, and an
agent loads into its window. Getting the boundary right shrinks every later
cost. Getting it wrong multiplies them.

## Discover the boundary before scaffolding it

Run Event Storming before naming a module. The three levels produce different
evidence, and the scaffold consumes the output of all three:

| Level | Purpose | Output the scaffold consumes |
|---|---|---|
| Big Picture | Map the whole domain, its seams, and its hotspots | Candidate contexts, pivotal events, `Modules/` tree |
| Process Level | Detail one process with commands, actors, and policies | Named policies, read models, slice candidates |
| Design Level | Settle aggregates, commands, events, and invariants | Aggregate names, consistency boundaries, event contracts |

Apply the facilitation rules that make the output usable:

- Phrase events in the past tense: "quote confirmed", not "confirm quote".
- Use business language, not storage language: "payment reconciled", not "row updated".
- Treat every disagreement or unanswered question as a hotspot, and carry it
  into the module's `hotspots.md` with its evidence, owner, and status.
- Name every event-to-command reaction as an explicit policy. A chain that
  nobody named is a facilitation error, not a design.
- Mark pivotal events. They usually sit on the seam between contexts.

An accepted ADR, an existing Context Map, or an equivalent approved domain
artifact satisfies the same evidence requirement. Absent that evidence, generate
the foundation without `--module` and leave the boundary open. Never invent a
context to fill a CLI argument.

Reject module names taken from technology or org charts: `Persistence`,
`Services`, `Infrastructure`, `Common`, `Core`, a database name, a vendor name,
or a team name. A context is named after a capability the business recognizes.

## Classify the subdomain before investing

Investment follows classification, not enthusiasm. Decide the class for every
module and record it in the module's `AGENTS.md`:

| Class | Meaning | Investment |
|---|---|---|
| Core | The differentiator the business competes on | Rich domain model, dense aggregates, deep tests, senior ownership |
| Supporting | Necessary, specific to the business, not a differentiator | Selective tactical DDD, strong boundaries, solid audit |
| Generic | Solved the same way everywhere | Buy or adopt, integrate through an anti-corruption layer, never model in depth |

A Generic subdomain modeled with the full tactical toolkit is waste. A Core
subdomain modeled as CRUD is risk. The classification also governs how much
autonomy a runtime AI component may hold inside the module: regulated Core
contexts keep the model assistive with a deterministic or human final decision.

## Draw the Context Map, pair by pair

For every material pair of modules, record the relationship and why it holds.
The pattern is a commitment about who absorbs change:

| Pattern | Use when |
|---|---|
| Published Language | The upstream context publishes a stable, versioned contract that several consumers read |
| Customer/Supplier | The downstream context can negotiate the upstream contract |
| Conformist | The downstream context accepts the upstream model as given |
| Open Host Service | One context serves many consumers through a deliberately stable API |
| Anti-Corruption Layer | An external or legacy model must never leak inward |
| Shared Kernel | A genuinely universal concept, kept minimal and jointly owned |
| Separate Ways | Duplication is cheaper than integration |

Every external provider (payment, fiscal, identity, scoring) sits behind an
anti-corruption layer. The provider's vocabulary stops at that translator.

## Owned surfaces

Every module owns the same logical surfaces in every topology. Where those
surfaces land physically is the topology's decision, recorded in its starter
manifest and described in
[`architecture-layouts.md`](architecture-layouts.md):

| Surface | Contents |
|---|---|
| domain | Aggregates, entities, value objects, domain policies, and internal Domain Events |
| features | Vertical slices grouped by business operation |
| integration | Immutable, versioned contracts other contexts consume |
| ports | Domain-facing contracts the context owns, when the topology names them separately |
| module infrastructure | Persistence, providers, broker integration, cache, outbox, and inbox |
| registrations | Application, technology, and endpoint registration for the context |
| module context | `AGENTS.md` and `hotspots.md` for the context |

Nest configuration keys under `Modules:<Context>:<Capability>:<Provider>`.
Relational modules own a schema and keep their outbox in that same
transactional store. Document modules own their database, collections, and
indexes.

## Cross-module rules

One module must not reference another module's infrastructure, data model, or
mutable domain types. Synchronous collaboration goes through an explicitly
approved contract or facade that returns DTOs, never aggregates. Asynchronous
collaboration publishes a versioned Integration Event through the producer's
outbox and deduplicates it in the consumer's inbox.

Shared database deployment does not imply shared data ownership. Cross-module
joins, foreign keys into another module's tables, a shared `DbContext`, and
direct collection access erase the boundary. Each requires an explicit ADR when
temporarily unavoidable.

Atomicity across modules does not exist. Two contexts that must commit together
are one context, or they need a saga with explicit compensation and the full
cost on the table.

## SharedKernel budget

The shared kernel holds only genuine universals whose meaning is stable across
every context: a result type, a domain event base, a truly universal value
object. Business entities such as `Customer`, `Order`, or `Account` never
belong there. Its public type count is a fitness function; exceeding the cap
triggers an architectural review instead of a silent raise.

## Extraction threshold

Keep the module in the monolith by default. Maturity alone is a precondition,
not a trigger. Extract only when evidence shows independent deployment or
scaling, fault or regulatory isolation, a materially different runtime, or
durable team autonomy, and only when at least one of those pains is concrete.

Network latency is neither the reason nor the proof: in-process calls cost
hundreds of nanoseconds and network hops cost milliseconds, so extraction
spends latency rather than saving it.

Before extracting, prove the boundary inside the monolith: the public contract,
the versioned Integration Events, the outbox, the idempotent inbox, contract
tests between producer and consumer, correlated observability, a rollback plan,
and a numeric consistency SLO accepted by the business. A successful extraction
is anticlimactic, because the module already behaved like a service.

Do not extract when the boundary still moves weekly, when one team owns
everything at one deploy cadence, when the operation needs an ACID transaction
across the split, or when the new service would have to query the other's
database to function. That last case means the boundary is still wrong.
