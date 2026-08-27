# Overlay Application Order

Composition order is part of the deterministic output contract. The same
arguments produce byte-identical output in every run and in every catalog entry.

1. Load the starter catalog, resolve the selected entry to its canonical
   topology, and build its surfaces, registrations, and dependency rules.
2. Validate every overlay id, axis, requirement, conflict, cycle, module
   precondition, and destination collision before publication.
3. Apply axes in this fixed order: `transports`, `persistence`, `messaging`,
   `cache`.
4. Sort selected overlay ids alphabetically inside each axis.
5. For each overlay, collect package versions and project package references,
   render declared files to their resolved surfaces, collect marker patches,
   merge settings, and render the README section.
6. Materialize sorted and deduplicated package versions and references, then
   apply patches in collected order, re-indenting each snippet to its marker.
7. Deep-merge settings structurally into both host settings files. A key with a
   different existing scalar value fails instead of silently winning.
8. Append rendered feature documentation and remove every staging marker.
9. Run the writing lint, publish from staging, restore with and without locked
   mode, build with warnings as errors, run the tests, write verified metadata,
   and write the Stack Profile last.

Example for `--transports minimal-api,graphql --persistence ef,dapper --module Billing`:

```text
graphql -> minimal-api -> dapper -> ef
```

Transport overlays contribute host transport infrastructure and patch the host
composition root. Persistence overlays contribute `Billing` infrastructure to
the project that owns module infrastructure and patch that context's
infrastructure registration. Package references land in the project resolved
from the axis, which is the host in a modular monolith and the infrastructure
project in a layered topology.

Alphabetical ordering is a reproducibility decision, not a statement about
runtime preference. The order is not user-configurable. When runtime
registration precedence becomes meaningful, encode the relationship in a
purpose-built overlay or an architecture decision instead of depending on CLI
ordering.
