# Source-Review Lenses and Finding Identity

Cross-adapter rule. Authoritative for the review lens vocabulary and the finding
id scheme every adapter's `{adapter}-code-review` capability and every
`commands/review.md` mode uses. Adapters specialize one lens; none renames,
adds, or drops a lens, and none invents an id namespace.

## The six lenses

Every source review evaluates all six. Each adapter names lens 3 after its own
stack; the other five are identical across adapters.

| Code | Lens | Scope |
|---|---|---|
| `PRF` | Performance | hot paths, allocations, I/O, concurrency, latency, rendering cost, and measured runtime evidence |
| `ENG` | Software Engineering | correctness, cohesion, coupling, maintainability, error handling, observability, and change scope |
| `STK` | Stack Quality | the owning stack's idioms, type and null flow, async semantics, framework and API use, analyzers, and toolchain compatibility |
| `TST` | Test | behavior coverage, oracle strength, isolation, failure paths, regression risk, and test dialect. Apply `shared/test-tautology-rules.md` to every test in the reviewed diff: an oracle that cannot fail is a `TST` finding regardless of coverage |
| `ARC` | Architecture | boundaries, dependency direction, contracts, consistency, NFR allocation, and accepted decisions |
| `SEC` | Security | authentication, authorization, input and data protection, secrets, supply chain, abuse paths, and auditability |

Lens 3 specializations, declared by each adapter and never abbreviated
differently in the id scheme:

| Adapter | Lens 3 name |
|---|---|
| `dotnet` | .NET Quality |
| `react` | React/TypeScript Quality |
| `flutter` | Flutter/Dart Quality |
| `devops` | Platform Quality |

Call the set "the six lenses" or "six-lens review". There is no acronym: an
acronym would have to encode lens 3, which is the one letter that changes per
adapter, so it stops matching the set it names the moment a second adapter
adopts it.

## Finding identity

Every finding carries `id` and `lens`. The id is
`{LENS-CODE}-{NNN}`: the lens code from the table above, a hyphen, and a
zero-padded three-digit sequence numbered per report per lens starting at
`001`. `ARC-001`, `SEC-014`, `STK-003`.

Rules:

1. **The lens code is the namespace.** A finding raised by the Architecture lens
   is `ARC-*` whichever adapter raised it, so ids stay comparable across a
   multi-adapter review.
2. **Ids are stable within a report.** Renumbering an existing report breaks
   every published comment, verification stamp, and thread anchor that quotes
   it. A re-run produces a new report with its own numbering.
3. **Ids are report-local, not global.** `ARC-001` in two reports are different
   findings. Anything that must survive across reports quotes the report id too.
4. **Adherence findings use `ADH-{NNN}`**, produced by the
   `--against issues` sub-report in `commands/review.md`. It is not a lens: it
   answers a different question (does the change satisfy stated criteria) from a
   different evidence source (the pull request and its linked issues).
5. **No ad hoc namespace.** A capability that needs a code not in this file
   proposes it here first. Inventing one per run produces ids that mean nothing
   to a later reader, which is what this file exists to prevent.
6. **Lens codes are identifiers, not prose, and are never translated.**
   `PRF`, `ENG`, `STK`, `TST`, `ARC`, `SEC`, and `ADH` stay exactly as written
   in every report, regardless of the manifest `language`. A PT-BR report says
   "a lente ARC" in prose while the id stays `ARC-001`; it never mints `ARQ-001`.
   Only the lens **display name** localizes ("Architecture" / "Arquitetura");
   the code is a stable pointer, not a word, and translating it produces two
   incompatible schemes for the same six lenses inside one repository, where a
   finding referenced as `QUA-002` in one report and `STK-002` in the next
   cannot be tracked across them.

## Canonical order and file slug

The per-lens persistence layout (`commands/update.md` "Write findings", `--layout
per-lens`) writes one numbered file per lens plus one for adherence. The
ordinal and the file slug are fixed here, once, so every report orders and
names its lens files identically regardless of which adapter produced them:

| Ordinal | Code | File slug |
|---|---|---|
| 1 | `PRF` | `performance` |
| 2 | `ENG` | `software-engineering` |
| 3 | `STK` | the adapter's lens 3 name, slugged (see below) |
| 4 | `TST` | `test` |
| 5 | `ARC` | `architecture` |
| 6 | `SEC` | `security` |
| 7 | `ADH` | `issue-adherence`, only present when `--against issues` ran |

`STK`'s file slug applies the same collapse rule `commands/update.md` "Target
vocabulary and persistence scope" defines for `{target-slug}`: lowercase, every
character outside `[a-z0-9]` collapsed to a single `-`, leading and trailing
`-` trimmed. `.NET Quality` becomes `net-quality`; `React/TypeScript Quality`
becomes `react-typescript-quality`; `Flutter/Dart Quality` becomes
`flutter-dart-quality`; `Platform Quality` becomes `platform-quality`.

A finding's id already names its file: `{LENS-CODE}-{NNN}` carries the lens
code, and the code fixes the ordinal and the slug through this table. Resolving
`ARC-001` to `5-architecture.md` needs no directory listing or guess, only this
lookup.

## Ids never leave the report

A pull request comment is written for a reader who has only that comment, and no
review report id appears in a pull request. Comment bodies therefore carry no
finding id, in the title or the body, and a cross-finding reference is rewritten
as a description of what it referenced. See
`shared/pr-comment-self-containment.md`, enforced by
`scripts/pr-comment-refs-scan.py`.

## Severity

Severity is `CRITICAL`, `HIGH`, `MEDIUM`, or `LOW`, and is independent of lens.
`CRITICAL` means an immediate security, data-integrity, availability,
destructive-change, or public-contract hazard. `HIGH` means likely incorrect
behavior or a material regression in any lens. Lower severities still name
concrete impact; style-only preference without project evidence is not a
finding.

Severity does not select a comment type. Comment type follows intent, per
`shared/templates/pr-comment.template.md`.

## Cross-references

- `commands/review.md`: the finding schema, the mode contracts, and the
  adherence sub-report.
- `commands/publish.md`: what a finding must carry before it can be published.
- Each adapter's `{adapter}-code-review` skill: the lens 3 name and the reviewer
  roster that applies the six lenses.
