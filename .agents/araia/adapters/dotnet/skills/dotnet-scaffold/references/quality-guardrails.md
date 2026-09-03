# Quality, Security, and Runtime Guardrails

AI-assisted delivery raises change volume; it does not lower the need for
verification. Volume without automated tests, fast feedback, and decoupled
architecture converts directly into instability, which is why a simpler
architecture needs stronger executable guardrails, not weaker ones. Every
guardrail here is a promotion gate that runs, not documented intent.

## Architecture fitness functions

The architecture test project protects, in every topology:

- the selected topology's declared dependency rules, generated from its manifest;
- cross-context isolation, so one Bounded Context never depends on another
  context's domain or infrastructure namespaces;
- one consistent error axis across handlers;
- the shared-kernel public type budget.

The scaffold ships those checks with the initial dependency rules and the type
cap. Extend the suite when the first real module makes a rule concrete. Never
claim a fitness function that does not exist yet.

Keep the active set curated, roughly three to six rules that matter, and review
it on the same cadence as the architecture decisions it protects.

## Security fitness functions

The host fails closed. Authenticated fallback authorization is the default and
anonymous endpoints opt out explicitly. State-changing and upstream-cost
endpoints declare named authorization and rate-limiting policies.

The security test project asserts, at source level:

- no request binding to domain types, so mass assignment cannot happen;
- no interpolated raw SQL and no unsafe deserialization APIs;
- no personal data property name inside a log message template;
- no pseudo-random generator on authentication, token, or cryptography paths;
- authorization and rate limiting present on every state-changing endpoint.

Apply the rest of the posture in code and configuration:

- Validate tokens fully: issuer, audience, lifetime, signature, and key id.
  Short access tokens, rotating refresh tokens with reuse detection, and no
  custom token parsing.
- Prefer policy-based authorization over role strings, and use resource-based
  checks on every endpoint that loads a record by id, so one user cannot read
  another's object.
- Rate-limit authentication, password reset, financial operations, and anything
  that costs money upstream. Bound request body size, multipart length, and
  request timeouts.
- Keep secrets in user secrets during development and in a managed secret store
  in production, bound through validated options that fail startup when absent.
- Never log tokens, passwords, secrets, connection strings, session identifiers,
  financial values, or raw provider responses. Mask or hash personal data in
  structured logs and tag those properties as personal data.
- Sanitize error responses in production and keep detailed diagnostics to
  development.
- Restrict outbound provider calls to an allow-list of hosts, resolve names
  server-side, reject private and loopback addresses, validate every redirect
  hop, and apply timeouts, bounded retries, and circuit breakers.
- Use authenticated encryption with a unique nonce per message, a slow password
  hash, a cryptographic random source, and constant-time comparison for tokens
  and signatures.

For a regulated Brazilian domain, the privacy obligations enter the design:
documented legal basis per processing activity, personal-data tagging on
entities, DTOs, and events, retention aligned to fiscal duties with minimization
elsewhere, endpoints for access, portability, and erasure, an isolated audit
log, a named data-protection officer with a contact channel, and an incident
runbook with the regulator notification window.

Source-level tests are one layer. Static analysis, dependency scanning, secret
scanning, infrastructure policy, dynamic testing where applicable, and human
review remain separate layers.

## The gate over AI-authored change

Treat every AI-authored change as a pull request from a collaborator you do not
yet trust. Roughly half of generated code introduces a known vulnerability, and
that rate does not improve with larger or newer models, so the funnel must be
deterministic before it is judgmental:

| Layer | Mechanism | Role |
|---|---|---|
| Architecture | Architecture tests in code | Enforces the topology's boundaries |
| Configuration | Policy as code | Enforces infrastructure and config rules |
| Security | Static analysis plus the security tests above | Blocking gate |
| Review | LLM reviewer | Supplement for breadth and triage, never blocking |

An LLM reviewer is noisy in both directions: it detects a minority of the issues
humans raise, degrades as more context is added, and rejects correct code at a
high rate. Use it for breadth, keep the deterministic checks and human review as
the gate, and measure the reviewer's own precision, recall, and false-rejection
rate before trusting it further.

Contain the agent as well as its output: sandboxed execution, a network egress
allow-list, least-privilege scoping per task, secret scrubbing from the
environment, provenance tracking for AI-authored changes, and tiered
human-in-the-loop approval. Repository convention files are agent-trusted
context and therefore a prompt-injection vector; changing one carries the review
weight of a security policy. The agent never receives production credentials or
raw regulated data.

## Test strategy

| Layer | Protects |
|---|---|
| Unit | Aggregate invariants, value objects, policies, state transitions, Domain Events |
| Slice behavior | One use case end to end with fakes at the boundaries |
| Integration | Persistence mappings and indexes, optimistic concurrency, outbox and inbox behavior, provider, broker, cache, and HTTP contracts |
| Architecture and security | Structural and posture rules |
| Performance | Repeatable baselines for identified hot paths, with a regression gate |
| Evals | Probabilistic runtime AI components |

Architecture, security, performance, and eval suites are lateral guardrails, not
tiers of the pyramid.

Practice test-driven development as a feedback discipline: a failing behavior
test, the smallest implementation, then refactoring under green tests. The
third occurrence of duplication is discovered while the tests are green, which
is exactly when an abstraction can emerge without changing behavior. Name tests
for behavior, never for specification identifiers. Write characterization tests
before deleting or reshaping legacy code. Project count is not coverage.

## Evals for runtime AI

When a module embeds a language model, deterministic tests are insufficient
because the output is not deterministic. Add a versioned eval suite beside that
module: a small curated dataset derived from sampled production traces, run in
CI, with deterministic assertions first (valid schema, no personal data,
answers grounded in the retrieved sources) and a model judge only for the
subjective gaps, never the model judging itself. Verdicts are binary. For any
model feeding a financial flow, add output-drift detection across a repeated run
or a second provider, plus an audit log.

## Supply chain and build

Use central package management, commit the lock file, restore with locked mode
in CI, and treat warnings as errors. Run vulnerability, deprecation, license,
secret, and provenance checks for the delivery environment. Pin the SDK and
container base images deliberately.

The generated NuGet configuration requires signed packages and trusts the public
feed's repository signing certificates. Add a trusted signer entry before
restoring from any other feed, and refresh fingerprints when a signing
certificate rotates. Private or unknown feeds require explicit authorization
before the generator restores.

## Runtime evidence

Architecture shape does not imply performance. Reducing files per feature lowers
cognitive cost, not latency. Capture baselines before setting budgets for
latency percentiles, throughput, allocation rate, CPU, memory, garbage-collection
pauses, database and broker pressure, and eventual-consistency lag. Attach a
measurement method, workload, environment, owner, alert, and rollback action to
every promoted objective.

For runtime AI, measure additionally: answer quality, safety violations, cost
and token usage per operation, provider latency, end-to-end latency including
retrieval and guardrails, cache hit rate, retrieval quality, fallback rate, and
the model and prompt version in use. Treat those thresholds as fitness functions
with the same standing as the architecture tests.
