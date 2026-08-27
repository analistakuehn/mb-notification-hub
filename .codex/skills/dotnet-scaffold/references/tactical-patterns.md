# Tactical DDD and Abstraction on Demand

Tactical DDD is a modeling toolkit, not a checklist of classes to instantiate.
It earns its cost when it makes a business rule explicit, protects an invariant,
or removes ambiguity. It becomes pure cost when every trivial concept must pass
through entity, aggregate, repository, specification, factory, service, and
event without a real pain behind it.

The direction is fixed: the pain appears first, the model answers. Never the
reverse.

## Start from the nature of the rule

| Question | Signal that tactical DDD helps |
|---|---|
| Is there a rule that must hold at all times? | Model it in the domain, not in the handler |
| Is there identity and a lifecycle? | Consider an Entity or an Aggregate |
| Is there a value with its own rules but no identity? | Consider a Value Object |
| Must a change be atomic? | Consider an Aggregate as the transactional boundary |
| Is there a fact other flows care about? | Consider a Domain Event, and an Integration Event if it crosses a context |
| Is there a shared selection rule? | Consider a Specification |
| Is persistence complexity leaking? | Consider a concrete Repository |

If the only answer is "it matches the pattern", there is no justification.

## Pattern by pattern

| Pattern | Introduce when | Avoid when |
|---|---|---|
| Entity | The business recognizes the object as something that exists, changes state, and must be tracked | It only carries data for one screen or one request |
| Value Object | Equality comes from the values and local rules must be protected | It wraps a primitive with no rule, behavior, or clarity gain |
| Aggregate | Invariants must stay consistent inside one transaction | There is no real transactional invariant, only a data tree |
| Domain Service | A rule spans concepts and belongs to no single entity | It coordinates technical steps such as fetching, calling, or publishing |
| Policy | The idea is a named calculation or decision that varies on its own | The value is a constant or a single branch |
| Domain Event | Behavior inside this context reacts to an accepted fact | It reports operational noise or a trivial field change |
| Repository | It hides real query or persistence complexity for one aggregate root | A handler can use its module context directly and read more clearly |
| Specification | A selection rule carries business meaning, gets reused, and composes | The filter is trivial and local |
| CQRS projection | Read and write models have materially different access patterns | Separate models only duplicate simple CRUD |
| Event Sourcing | Event history is the authoritative record and replay or audit justifies the operational cost | An audit log already answers the question |

### Entity

Use an Entity when the object keeps its identity while its attributes change: a
customer with a registration history, a confirmed operation with a lifecycle, a
contract that moves through active, suspended, and cancelled. A command, a
report row, and a screen filter are not entities.

### Value Object

Use a Value Object when equality comes from the values and the concept owns
rules: an amount paired with a currency, a currency pair, a national tax id, a
bounded percentage. The practical test is direct: if this stays primitive, does
an important rule become easy to violate? A wrapper holding nothing but a
string is noise.

### Aggregate

An Aggregate is a consistency boundary, not a folder, a module, or a set of
tables. Use it when several objects must change together so the business never
observes an invalid state: an operation that cannot be confirmed without a valid
quote, a contract that cannot be cancelled while incompatible charges exist, an
invoice whose items must sum to its total.

Large aggregates built to mirror a data tree produce lock contention, oversized
loads, and rules that resist change. Keep the boundary at the smallest set that
the invariant actually requires, and reference other aggregates by identity.

### Domain Service and Policy

Use a Domain Service when a business rule belongs to no single entity or value
object. Use a Policy when the core idea is a calculation or a decision that
varies independently: a spread policy that depends on amount, channel, and
customer profile, or a tax policy that changes by operation type and period.

Fetching data, calling an API, opening a transaction, publishing a message, and
building a response are application or infrastructure responsibilities. The
test is whether the class expresses a business rule or merely sequences
technical steps.

### Repository

Introduce a repository when it protects the feature from non-trivial
persistence or concentrates domain queries that would otherwise repeat and
obscure the slice. Scope it to one aggregate root and name it in the ubiquitous
language.

Do not create a generic `IRepository<T>` by default. Using the module's own
context directly inside a handler is often simpler, more explicit, and cheaper
for both humans and agents. That freedom is not a license to query across
aggregates: the aggregate remains the consistency boundary. The test is whether
the repository hides real complexity or just renames a `DbSet`.

### Specification

Use a Specification when a selection rule carries business meaning, is reused,
and benefits from composition: customers eligible for a higher limit, contracts
ready for renewal, operations flagged for compliance review. Do not wrap
`x => x.Id == id` or `x => x.Status == Status.Active` in a named type.

## Growth by evidence

A feature can start as a plain slice and grow only where a pain appears:

| Observed pain | Model that answers it |
|---|---|
| An invalid currency reaches several flows | A currency pair value object |
| A calculation varies by amount, channel, and location | A named policy |
| A confirmed record must become immutable | An aggregate with an explicit state transition |
| A confirmation must trigger work in other contexts | A Domain Event plus a versioned Integration Event |
| One query rule repeats across features | A concrete repository or a named specification |

## Encapsulation is not abstraction

Encapsulation is mandatory. Dependency inversion is contextual. Hide technology
inside the owning module without creating an interface merely because a class
touches a database, a cache, a clock, or an API.

Add an abstraction only against evidence of a real pressure:

- multiple implementations are active or imminent;
- a volatile external boundary needs isolation, usually as an anti-corruption layer;
- a test seam must replace an expensive or nondeterministic dependency;
- a named business policy varies independently;
- an accepted architecture boundary requires the inversion.

A concrete repository over a module-owned context encapsulates persistence. An
interface over an external rate provider represents a real boundary with
fallback, caching, simulation, and vendor substitution behind it. Those are
different situations, and only the second earns an interface.

Policies and domain services stay concrete by default. Introduce an interface
for them only when a test must inject behavior the production class cannot
express through constructor parameters.

Prefer concrete, context-specific names over generic ones. Interfaces belong at
the consumer-owned boundary and speak domain language, not the provider's API.

In layered topologies the port lives with the consumer and the adapter lives
outside it, so the dependency still points inward. That placement does not
change the rule above: the topology decides where a justified abstraction goes,
never whether it is justified.

## Error consistency

The starter uses `Result<T>` for expected outcomes: validation failures,
business-rule violations, integration failures, not-found, and forbidden.
Exceptions stay for unexpected system faults such as I/O and infrastructure
errors, translated centrally at the host boundary.

A project may instead choose typed exceptions as its axis, provided it does so
consistently and ships middleware that maps them to responses. What the
architecture test forbids is mixing both axes for the same expected outcome
inside one module without a recorded migration decision.
