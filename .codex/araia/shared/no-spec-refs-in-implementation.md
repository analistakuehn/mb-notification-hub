# No Spec-Document References in Implementation Code

Cross-adapter rule. Applies to every adapter's `code-style.md` and to every agent or skill that generates, edits, or reviews implementation artifacts (source code, tests, migrations, schemas, e2e flows). Authoritative.

## Rule

Implementation artifacts MUST NOT reference spec-document identifiers by name, number, file path, or URL. The forbidden references include but are not limited to:

- Delivery Slice IDs and titles (`SLICE-001`, `SLICE-042-archive-orders`, "Per Delivery Slice 042")
- Acceptance Criteria numbers (`AC-1`, `AC-3`, "Acceptance Criterion 2")
- ADR IDs, titles, or file paths (`ADR-2026-05-23`, `ADR-0042`, `docs/.../adr/...`)
- PRD section numbers (`PRD §2.4`, "per the PRD")
- Sprint, iteration, or release IDs
- Issue / ticket links (Jira `PROJ-123`, Linear `LIN-456`, GitHub `#123`)
- Specification file paths (`docs/SPEC-001/requirements/...`)

This rule applies to all implementation forms: code comments, JSDoc / TSDoc / XML doc text, test names, test descriptions, test fixtures, error messages, log messages, commit-embedded tags, generated TODO markers, and any other text that ships inside the implementation artifact.

## Why

- **IDs rot.** SLICE-001 may be split, merged, renumbered, or moved between projects. AC numbering shifts every time the spec is regenerated. ADR titles are renamed; ADR files are superseded.
- **Cross-repo references break.** Issue-tracker links die when projects move, get archived, or change tracker.
- **Code becomes self-archaeology.** A reader chasing "what does SLICE-042 mean" lands in a graveyard of stale links instead of the explanation.
- **The PR / commit trail already records the why.** Git blame -> commit message -> PR description -> spec is the canonical chain. Embedding the trail in code duplicates it and the duplicate decays.
- **Behavior-named tests outlive AC-named tests.** A test called `archives the order when the user presses the button` survives a spec rewrite; a test called `AC-3 verifies acceptance criterion 3` becomes meaningless the moment AC numbering shifts.

## What counts as "implementation"

The rule applies to:

- Source files in any language (`.ts`, `.tsx`, `.cs`, `.dart`, `.py`, `.go`, `.rs`, `.kt`, `.swift`, `.rb`, ...)
- Test files (unit, component, integration, E2E)
- Schema / migration files (`*.sql`, `*.proto`, `*.graphql` schemas, `database.types.ts`, OpenAPI specs hand-edited)
- Configuration that ships with the app (`eas.json`, `app.json`, `app.config.ts`, `next.config.*`, `vite.config.*`, `pubspec.yaml`)
- E2E test flows (`.maestro/*.yaml`, `cypress/e2e/*.cy.ts`, `e2e/*.spec.ts`)
- Generated docs that live alongside code (TSDoc, JSDoc, XML docs, RustDoc): these document the code's behavior, not the spec history
- Inline tag-style TODOs (`// TODO(SLICE-001)`, `// FIXME(AC-3)`)

## What's exempt

- The spec documents themselves: Delivery Slice files, ADR files, PRD, design docs, sequence docs, they live under `docs/{SPEC-ID}/` and naturally reference each other.
- Top-level documentation files (README, CONTRIBUTING, ARCHITECTURE.md): they may cite ADRs by title to anchor the project's history.
- Architecture artifacts (`docs/architecture/*.md`): they may cross-reference ADRs.
- PR descriptions, they SHOULD cite the Delivery Slice, the ADR, and any other spec context for traceability.
- Commit messages, they SHOULD include the Delivery Slice ID and / or the ADR ID for `git log` searchability.
- CHANGELOG entries, they may cite ADRs and PRD items when relevant.

The boundary: documents about the work cite the spec; the work itself does not.

## Right pattern (and wrong pattern) examples

When a non-obvious decision needs explanation in code, write a short comment that explains WHAT and WHY without citing the document.

```ts
// WRONG — references Delivery Slice / AC / ADR
// SLICE-042 / AC-3: archive when status is open
function archive(id: string) { /* ... */ }

// WRONG — references ADR file path
// per ADR-2026-05-23-decision-workflow
const decision = await decisionPanel.evaluate(...);

// WRONG — TODO with Delivery Slice marker
// TODO(SLICE-103): replace with rate-limited variant

// RIGHT — explains the constraint without citing the doc
// Archive flips status to 'archived' without deleting the row; downstream
// reports read this state for chargeback reconciliation.
function archive(id: string) { /* ... */ }

// RIGHT — explains why a particular shape was chosen
// Refresh tokens live in the HttpOnly cookie set by the server; the in-memory
// access token is rotated by the HTTP interceptor on 401. Direct token writes
// from this code path would bypass the rotation contract.
const token = getAccessToken();

// RIGHT — TODO with technical reason
// TODO: replace with a rate-limited variant once the backend exposes the bulk endpoint
```

For tests:

```ts
// WRONG
it('AC-3: archives the order', async () => { /* ... */ });
describe('SLICE-042 Orders feature', () => { /* ... */ });

// RIGHT
it('archives an order when the user presses the archive button', async () => { /* ... */ });
describe('OrdersScreen', () => { /* ... */ });
```

For E2E flows:

```yaml
# WRONG — Maestro flow
appId: com.example.app
name: SLICE-042 archive-flow

# RIGHT
appId: com.example.app
name: archive an order from the list
```

## Replacements for legitimate use cases

| You want to | Right approach |
|---|---|
| Track which Delivery Slice a piece of code came from | Commit message includes the Delivery Slice ID; `git blame` -> commit -> PR -> Delivery Slice chain |
| Explain a deviation from baseline that an ADR documents | Comment explains the constraint in plain language; the ADR is found via `git log --grep` from the commit message |
| Mark code that depends on a deferred Delivery Slice | TODO comment with the technical condition that must be satisfied, not the Delivery Slice ID |
| Trace a test back to an Acceptance Criterion | The Delivery Slice's test-traceability table maps tests to AC; tests are named after behavior, not numbering |
| Note that a value comes from a PRD requirement | Comment names the requirement in plain words (e.g., "Max 50 items per page per the rate-limit policy"); the PRD is found via the project's docs |
| Mark a feature flag tied to a rollout Delivery Slice | The flag name describes the feature; the rollout plan lives in the Delivery Slice; the code does not name the Delivery Slice |

## Enforcement layers

This rule is enforced at four layers:

1. **`code-style.md` of each adapter**: declares the rule, references this file as authoritative.
2. **Agents that generate code**: specialist, TDD specialist, scaffold-related agents, explicitly forbid the references in their prompts.
3. **Docs-reviewer and code-review skills**: flag violations as findings.
4. **Audit-scan skills**: pattern-match for the forbidden references with regex against changed files; severity is SERIOUS (not BLOCKER, these are rot risks, not security risks; but a project may bump to BLOCKER via configuration).

## Detection patterns

Regex patterns that match the forbidden references in source files:

```
\bSLICE-?\d+\b
\bAC-?\d+\b
\bADR-?\d+\b
\bADR-\d{4}-\d{2}-\d{2}\b
\bPRD\s*§\s*\d+
\bSPEC-?\d+\b
\b(?:Jira|Linear|GitHub|GH)\s*#?\s*[A-Z]+-?\d+\b
docs/SPEC-\d+/
docs/.+/adr/ADR-
```

These patterns produce findings under filenames matching the implementation glob (every language's source, test, schema, and configuration extensions). The global `no-spec-refs-scan` capability reports each occurrence with severity SERIOUS.

## Cross-references

- Each adapter's `code-style.md`: applies this rule.
- The framework's per-language generation rules (`ptbr-generation-rules.md`, `en-generation-rules.md`): implicitly compatible; spec IDs are not part of the writing standard.
- Technology source is generated by `{adapter}-engineer` through implementation or test-driven-development, and by deterministic scaffold generators after an architect-approved foundation brief. Specialists remain read-only.
- Skills that review code: each supported adapter's `{adapter}-code-review`.
