# Domain and Integration Event Contracts

Domain Events and Integration Events sit on different boundaries and must stay
different types. Reexposing an internal event on a broker couples every consumer
to a model the producer intends to keep refactoring.

## Domain Events

A Domain Event records a fact accepted by an aggregate inside one Bounded
Context. It is internal and immutable, the aggregate raises it, and dispatch
happens after the state transition succeeds, at the transaction boundary or
through the outbox. It carries identifiers and the few values consumers need,
never whole aggregates.

Raise one when behavior inside the same context must react to the fact without
direct coupling: a quote confirmed, an invoice generated, a contract cancelled, a
customer risk profile changed.

Do not raise events for operational noise or trivial field changes. A saved
entity, a viewed report, or a clicked button is not a domain fact. A renamed
customer is not either, unless another context genuinely reacts to it, which in
know-your-customer flows it does. That exception is the point: the test is
whether someone reacts, not whether something changed.

## Integration Events

An Integration Event is a public, versioned contract living in the context's
integration surface under `Integration/V<N>/`. It contains only the stable data
consumers need, carries an event id and an occurrence time, states its version,
and never leaks an internal entity graph or a sensitive field by convenience.

Use the namespace convention `<Context>.Integration.V<N>.<EventName>`. Evolution
is additive; a breaking change creates a new version alongside the old one, with
a compatibility window and a retirement plan.

Map Domain Events to Integration Events at the module boundary. That mapping is
where disclosure, vocabulary, versioning, and consumer compatibility get decided
deliberately. Refactoring the internal aggregate must not break a consumer, and
breaking the external schema must require a new version.

```text
Contracts (internal):   domain event      ContractActivated
                                            |
                                          outbox
                                            |
Public surface:         integration event Contracts.Integration.V1.ContractActivated
                                            |
                             ┌──────────────┼──────────────┐
                             v              v              v
                          Billing        Reports      Compliance
```

## Named policies

Every reaction to an Integration Event is a named policy, never an implicit
chain. Name it in the consuming context and write it down:

```text
When  Contracts.Integration.V1.ContractActivated
Then  Billing runs policy "GenerateFirstInvoice"

When  Contracts.Integration.V1.ContractCancelled
Then  Billing runs policy "CancelFutureCharges"
      Reports runs policy "ProjectChurn"
```

An unnamed reaction is undiscoverable in review and invisible to an agent
reading the code.

## Transactional publication

Publishing directly from a handler is not a reliable cross-context path: the
write can commit while the publish fails, or the reverse. Write the aggregate
state and the outbox record in the same local transaction.

The outbox record carries at minimum the event id, the contract name and
version, the occurrence time, the serialized payload, attempt state, and
processing timestamps. A dispatcher reads the table in the background by polling
or change capture, publishes, and marks the record dispatched, with bounded
retries, exponential backoff, and dead-lettering after a declared attempt
budget.

Consumers store an inbox or deduplication record keyed by the event id before or
inside the same transaction as their effect, because brokers and dispatchers
deliver more than once. Scope ordering deliberately, usually by aggregate id
through a partition key or sequence number, and never assume global ordering.

Additional operational requirements:

- The outbox lives in the producing module's own store, never in a global shared
  table, and it participates in that module's transaction.
- Keep payloads minimal: identifiers plus the few fields consumers actually read.
- Protect payloads that carry personal data with encryption at rest, and detect
  broker tampering with a message signature.
- Use optimistic concurrency on the producing aggregate so conflicting updates
  surface before the outbox commits.
- Persist resume tokens for change-stream consumers.

An infrastructure overlay ships broker primitives only. Cross-context delivery
still requires this outbox, the versioned mapping, the idempotent inbox, the
tests, and the SLO.

## Choreography and orchestration

Prefer choreography, each consumer reacting independently, when flows evolve
separately and no cross-context compensation exists.

Introduce orchestration, a process manager or saga, when three or more contexts
must be coordinated with mandatory ordering or compensation: cancelling a
contract that must reverse issued billing, notify compliance, and adjust
reporting as one business outcome. The orchestrator holds explicit state,
timeouts, compensation steps, and operator visibility.

Choose by the nature of the coordination, not by broker preference or taste.

## Operational contract

Define and measure eventual-consistency SLOs as numbers with a dashboard, an
alert, and an owner. "A few seconds" is not an SLO. State publication delay,
end-to-end propagation delay, retry budget, dead-letter behavior, replay
procedure, and retention per flow.

Tests cover the atomic write, duplicate delivery, retry behavior, poison
messages, schema compatibility between producer and consumer, and payload
redaction.
