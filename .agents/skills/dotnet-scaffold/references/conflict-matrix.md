# Overlay Conflict Matrix

The generator validates the complete overlay graph before staging any output.
`requires` and `conflicts` reference overlay ids, not axes. The resolved
selection must satisfy every requirement, conflicts are treated symmetrically,
unknown ids and dependency cycles fail, and selecting several overlays on one
axis is valid unless an explicit edge forbids it.

| Overlay | Axis | Scope | Requires | Conflicts |
|---|---|---|---|---|
| `minimal-api` | transports | host | none | none |
| `graphql` | transports | host | none | none |
| `mongo` | persistence | module | none | none |
| `ef` | persistence | module | none | none |
| `dapper` | persistence | module | none | none |
| `rabbitmq` | messaging | module | none | none |
| `kafka` | messaging | module | none | none |
| `redis` | cache | module | none | none |
| `hybrid-cache` | cache | module | none | none |

The current overlays compose independently. Selecting several persistence
overlays for one context is legitimate polyglot persistence, not a conflict: a
context can write aggregates through an ORM, project reads through explicit SQL,
and serve a read model from a document store. Record the access-pattern
rationale per [`persistence-selection.md`](persistence-selection.md).

Scope is a configuration precondition, not a manifest dependency. A
module-scoped overlay requires `--module`, because the generated infrastructure
belongs to a Bounded Context and lands in that context's infrastructure surface.
Do not fake that requirement with an overlay edge.

Introduce `requires` only when generated code directly depends on a capability
another overlay emits. Package dependencies stay in `packages`. Introduce
`conflicts` only when the combined generated runtime is incoherent, never to
encode taste or to discourage a legitimate polyglot context.

```yaml
requires: [mongo]
conflicts: []
```

Declare both sides of a conflict, so either manifest reveals the relationship.

Diagnostics name the failure (`unknown-overlay`, `missing-required-overlay`,
`conflicting-overlays`, or `overlay-cycle`), the responsible overlay, the
resolved selection, and the smallest corrective action.
