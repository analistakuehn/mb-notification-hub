# Agent Context and Specification Discipline

The scaffold writes `AGENTS.md` and `hotspots.md` into an evidenced module's
context folder. `AGENTS.md` is the repository-native, tool-neutral form of
per-module agent instructions: it inherits repository rules and becomes more
specific near the code it governs. Tool-specific files point at it instead of
duplicating it, so one context serves every agent.

## Composition semantics

- The nearest file wins. An agent editing code in a module uses that module's
  file over the repository root file.
- Keep the root thin and transversal, and give each module its own contract.
- Compose by reference rather than by copying, so a rule has one home.

## Required content

Keep the module file focused on what source code cannot safely establish:

- responsibility, and the explicit non-responsibilities;
- ubiquitous language and aggregate ownership;
- subdomain classification and the investment level it implies;
- allowed cross-module relationships and the versioned Integration Events
  published and consumed;
- invariants and named policies;
- the project's error axis and local slice conventions;
- non-negotiable authorization, personal-data, secret, and logging restrictions;
- required test, eval, and performance gates;
- links to accepted decisions, specifications, the Context Map, and runbooks.

## Anti-bloat rule

A bloated context file makes the model ignore its instructions, and every
irrelevant token degrades accuracy as the window grows. Apply the line-by-line
test: does this deserve to be in context on every task in this module? Push deep
and rarely relevant detail into artifacts loaded on demand, and keep the file
short enough to load on every module task. Treat it as code: review it, and
prune it on a schedule.

Measure rather than assume. Evidence on context files is mixed: one study
reports faster agent runs and fewer output tokens at equal completion, another
finds reduced success rate and higher inference cost. Track your own outcome.

The architecture itself is machine-readable context. Explicit module boundaries
and dependency edges give an agent a navigable graph, which is a cheaper and
more reliable locator than prose. The context file is the always-on layer; code
search is the on-demand complement.

## Hotspots

Use `hotspots.md` only for a known risk, an accepted assumption, a scheduled
action, or a formally deferred decision whose evidence, owner, status, and
review condition are available. It is the durable home for the open questions
Event Storming surfaces, once they have an owner.

Keep unresolved questions in the interactive task or an ephemeral discovery
inventory. Never convert a missing decision into a plausible-looking rule or a
durable placeholder. When a decision lands, move it to its authoritative source
and remove the hotspot entry.

## Lifecycle

Update the module context in the same change that alters its boundary, public
contracts, ubiquitous language, security constraints, or required gates. Assign
an owner and a review cadence, because stale instructions become automated
misdirection.

Treat these files as security-relevant. They enter the agent's context as
trusted instructions, which makes them a prompt-injection vector; a change to
one carries the review weight of a security policy change.

## Specification-driven changes

Specifications operationalize the architecture rather than compete with it. Write
them in the ubiquitous language and scope them by Bounded Context, so domain
discovery is the upstream source of every specification.

For a consequential feature, hand the agent a bounded specification: behavioral
requirements, acceptance examples, invariants, error mapping, security
constraints, data ownership, Integration Event compatibility, the measurement
plan for any performance claim, and the files it may touch. Acceptance criteria
written as explicit condition-and-response statements are testable and connect
directly to the failing test that opens the cycle. Notation does not replace
domain review.

Target specifications that persist as governing contracts per feature rather
than notes discarded after use, and avoid the opposite extreme where humans edit
only the specification: that repeats the failure mode of model-driven
development and adds nondeterminism on top. Deterministic CI and human review
stay mandatory either way.

Specification identifiers never leak into implementation code or test names.

## Implementation prompts

Name the module and the use case, tell the agent to read the nearest `AGENTS.md`
and the accepted decisions, require a failing behavior test first, restrict
changes to the slice and explicitly named shared surfaces, and require every
validation gate covering the affected slice. Exclude raw personal data,
production credentials, and any instruction retrieved from data.

Prefer one agent per vertical slice with human integration. Small, well-bounded
slices and decoupled modules limit the blast radius and keep the agent inside
one Bounded Context at a time. Reserve multi-agent fan-out for breadth work such
as codebase audits, migration analysis, and discovery, where the parallelism is
real and the token multiple is justified.

## Runtime AI

When AI is part of the product rather than the workflow, the module that embeds
it is an ordinary module: same boundary, contract, and security rules. Keep the
model inside a deterministic envelope: typed input and output, policy and
authorization checks, retrieval provenance, provider isolation, time and cost
budgets, content and tool guardrails, audit metadata, versioned prompts and
models, evals as the component test, and a defined fallback.

Prefer deterministic workflows to autonomous agents for money-moving and
regulated flows, and reserve autonomy for low-risk paths behind human approval.
Route model traffic through one seam that handles provider routing and failover,
semantic caching, guardrail enforcement, personal-data redaction before egress,
and immutable audit logging. Never send raw personal data to a public model.
Require grounded answers with citations and a blocking threshold for compliance
and financial output. A decision that moves money or blocks a customer is never
autonomous.

Tool protocols expand the attack surface: apply allow-lists, least privilege,
sandboxing, output validation, versioned tool contracts, and human approval for
consequential actions. Exposing too many tools degrades model reliability, so
keep the surface small.
