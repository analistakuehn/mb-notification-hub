# Compliance module

## Boundary

- Keep one bounded context in this module: the composition of evidence. It owns
  the question, not the data, and it never gains a table. Nothing here is a
  source of truth, and nothing outside consumes it: this module is a leaf of
  the dependency graph, on purpose.
- The `/v1/audit/*` routes live here and nowhere else. Putting them in the Audit
  module would invert the direction that makes the transactional append work:
  every module depends on `Audit.Integration.V1`, so an Audit module that also
  read from every module would close a cycle between contexts. Spreading the
  routes across the owning modules would dissolve the guarantee this surface
  exists for, because "every call records `audit.read`" needs one point of
  enforcement, not four reminders.
- This module reads **only** through `Integration/V1` of the four owning
  contexts (Audit, Notifications, ContactConsent, TemplateManagement) and holds
  no `DbContext`, no migration and no schema. The dependency-direction
  architecture test stays green without a carve-out, and that is the evidence
  the boundary is right: the moment this module needs a store, the boundary was
  drawn wrong.
- The only write it performs is the disclosure record of its own answers,
  through `Audit.Integration.V1`.

## Owned surfaces

| Path | Responsibility |
|---|---|
| `Features/Queries/GetNotificationEvidence/` | reconstruction of one notification: trail block, state block, prior accesses |
| `Features/Queries/GetAttemptContent/` | opening the stored content of one attempt, in its masked form, with hash verification |
| `Infrastructure/Authorization/` | the audit policy, its requirement, and the security log of a denial |
| `Infrastructure/RateLimiting/` | separate budgets for the evidence route and the content route |
| `Infrastructure/Disclosure/` | the shape of a disclosure record and the append that writes it |
| `Infrastructure/Http/` | published numbers and notices, problem responses, principal resolution, volume alarm |

## The disclosure contract

- Every successful call records `audit.read` **before** the first byte of the
  body. The handler composes the whole answer, records the disclosure, and only
  then returns a value the endpoint can serialize. A failure of the append
  returns `503` and discloses nothing.
- The append is synchronous and never an outbox. An outbox would place the
  record after the egress and open exactly the window an insider needs.
- `403` and `404` record a structured security log and **no** trail row. An
  access that disclosed nothing has nothing for the chain to vouch for, and a
  row per miss would let a sweep of identities fatten the chain for free.
- The recorded subject is the subject that was read (`notification`,
  `recipient`), so "who looked at this afterwards" stays a read by subject and
  never a scan. One answer that touches two subjects writes one link per
  subject, inside one transaction, so both stay answerable and the chain lock is
  taken once.
- Every link of one call carries the **same access identifier** in its details.
  One call is one access, whatever the number of subjects it touched, and
  without the shared identifier an auditor counting rows would read two accesses
  where there was one, inflating "who looked at this afterwards" with every
  subject a future answer happens to reach.
- The details carry the route, the disclosed scope, the access identifier, the
  attempt sequences and the disclosed hashes. Never a contact value, never a
  fragment of content, never a variable.
- Every heavy read happens before the append opens its transaction: the chain
  advisory lock is held until that transaction commits.

## The two blocks of the answer

- `trail` holds chained links, each rebuilt from the **parse of the canonical
  text**. The `details` column of the trail is a query and indexing surface, not
  a payload of proof: it is `jsonb` and re-serializes on read, so its bytes are
  outside the chain.
- `state` holds domain projections of the owning modules, which no chain covers.
  Approvals sit here too: the table is append-only but outside the hash chain.
- Without the split an auditor cannot tell what the chain covers, and a
  projection would borrow credibility the chain never gave it.

## Absence discipline

- A member that is genuinely absent is omitted; an empty array asserts a fact.
- Delivery events and the read receipt are **not declared at all**, in any form,
  because no table records them yet. Neither is a delivery timestamp on an
  attempt. What the answer states about the provider is acceptance, and the
  OpenAPI description says so in those words.
- The historical version is omitted when the catalog no longer resolves it.
  Answering with the version published today would not be a partial answer, it
  would be a wrong one.
- The prior-access list is cut at `disclosure.composedAt`, and the answer
  declares the cut, so an auditor never reads their own footprint.

## Content disclosure

- The route serves the **masked** form and names the form it served. It works
  the same whether the attempt reached a terminal verdict or not: while the
  complete form is still stored, the masked companion is served and
  `completeFormStillStored` states it. The complete form never leaves.
- `contentHashMasked` is recomputed with the canonical hasher published by
  TemplateManagement and never reimplemented on the consumer side.
  `contentHashFull` travels declared, with no verification member, because
  cryptographic verification of the complete form is impossible after masking.
- The route has a rate-limit budget of its own plus a volume alarm, because the
  risk is not a burst, it is a patient sweep that never trips a per-minute
  ceiling.

## Out of scope for now

The recipient consent ledger as a route of its own, the generic audit-event
search, the asynchronous pseudonymized export, and the plaintext reveal of a
contact value for audit. Do not work around their absence here.

The masked variables projection is **not** in that list: it leaves through this
surface and through no other, so withholding it here would mean it leaves
nowhere at all, and the repudiation question ("the producer denies asking for
it") would lose the business payload of the request. The masking already
happened at ingestion and is irreversible; the column is the durable projection,
never the encrypted originals.

## Error axis and logging

- Handlers return the project's single `Result` axis. A refused disclosure is an
  integration failure, mapped to `503`; an unknown subject is not found, mapped
  to `404` with a body identical for every unknown identity.
- Loggers follow the repository dialect: `*.Logger.cs` files with
  source-generated extension methods, identifiers in English, message text in
  pt-BR, never personal data or rendered content in placeholders.

Update this file in the same change that alters the disclosure contract, the
split between the two blocks, the absence rules, or the set of published
contracts this module consumes.
