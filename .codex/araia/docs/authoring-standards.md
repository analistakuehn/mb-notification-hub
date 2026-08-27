# Authoring Standards

This document is the full reference for authoring framework skills, agents, and workers. `AGENTS.md` carries the one-line normative summaries; this file carries the detail. Load it on demand when authoring or reviewing framework artifacts. Deterministic enforcement: `framework/scripts/lint-agent-tiers.py` (tiers), `framework/scripts/check-auto-clarity.py` (Auto-Clarity structure), and `framework/scripts/check-context-budgets.py` (context budgets).

When the question is *which* actor a capability belongs to (skill, persona agent, operational worker, script, inline rule, or adapter contribution) rather than how to write it, read [`composition-boundaries.md`](composition-boundaries.md). That document carries the boundary rationale and the measurements taken from this repository; this one carries the authoring rules those boundaries produce.

## Actor Taxonomy

Araia distinguishes the canonical actor from the technical primitive used by a
harness:

| Category | Canonical meaning | Identity test | Typical examples |
|---|---|---|---|
| Skill | Reusable capability or workflow invoked to produce a bounded outcome. | The capability remains useful without a separate identity or fresh reasoning context. | `technical-document-writer`, `artifact-writer`, `araia` |
| Agent | Stable persona or perspective that exercises specialized judgment. | The role and decision lens survive across assignments. The same persona can receive different tasks without changing identity. | architect, engineer, UX specialist, accessibility reviewer |
| Worker | Operational execution profile for one bounded class of assignment. | The assignment, authority, isolation, checkpoint, and return contracts define the identity. Remove that envelope and no enduring persona remains. | Delivery Slice worker, stage worker, language-polish worker, protocol-check worker |

Use `framework/agents/` and capability- or adapter-local `agents/` only for
personas. Use `framework/workers/` for framework-wide operational profiles. A
capability with the same name as a skill defaults to direct skill execution or
a worker, not an agent, unless the definition establishes an enduring persona
and independent judgment lens.

Claude Code, Codex, Kimi Code, and other harnesses can expose both agents and
workers through an `agent` or `subagent` runtime primitive. That projection is a
deployment detail. It does not collapse the canonical categories.

## Agent and Worker Tier Taxonomy

Every framework agent and worker declares `model: inherit` (the profile runs on whatever model the parent session is using). Exception: Tier-3 mechanical profiles, normally workers such as formatters, checkers, and validators, can pin `model: sonnet`; their work is deterministic rule application, and dispatching it on the session model multiplies cost with no quality gain (`artifact-writer` and `auto-clarity-checker` pin it). Each profile must still declare `tools:` explicitly and scope them to its tier.

| Tier | Role | Tools |
|---|---|---|
| 1 | Orchestrators, architects, PMs, and UX | `Read, Write, Edit, Glob, Grep, Bash` |
| 2 | Domain specialists (language/stack/framework) | `Read, Write, Edit, Glob, Grep, Bash` |
| 3 | Mechanical formatters, checkers, and validators | `Read, Edit, Glob, Grep` (no Write to source, no Bash) |

### The model floor for judgment

Tier 3 covers profiles that **apply a fixed rule set**: a formatter, a structural
checker, a validator. It does not cover a profile that **exercises judgment**,
and the distinction decides the model.

A profile exercises judgment when it grades severity, returns
`accept` / `reject` / `defer`, emits a verdict on the substance of a claim, or
weighs evidence to decide whether a finding holds. Every such profile declares
`model: inherit` and never pins down, whatever its tool grant looks like.

The reason is cost, not purity. A downgraded judge does not fail loudly: it
returns a longer list of confident findings, many of them deliberate choices it
lacked the surrounding context to recognize. Triaging those false positives
costs more than the dispatch saved, and the review itself then needs reviewing.
Inside a fan-out, where several profiles judge concurrently, the cost multiplies
and the origin of a bad finding is hard to trace back to the node that produced
it. A mechanical checker is safe to downgrade precisely because its criteria are
enumerated and its output is reproducible.

Pinning is therefore a **declaration, not an inference**. A profile may pin
`model: sonnet` only when it carries an explicit `tier: 3` frontmatter field.
`lint-agent-tiers.py` still infers Tier 3 from a curated name list for the tool
restriction, because narrowing a grant by inference is safe, but that inference
never unlocks the pin; an inferred Tier 3 that pins reports `undeclared-pin`.
Adding `tier: 3` is cheap, and it forces the mechanical claim into the
frontmatter where review can see it.

Model choice is also one of the six execution boundaries below. A profile that
pins states the pin and its rationale in its `## Execution Boundary` section
rather than leaving the frontmatter to argue for itself.

Tier-1 orchestrator agents and scheduler workers can additionally declare `Agent` / `Task` for runtime-profile dispatch and `Skill` for direct specialist delegation. Only Tier 1 can use these orchestration tools; Tier-3 mechanical profiles must never declare them.

An agent or worker can declare an explicit `tier:` field (`1`, `2`, or `3`) to disambiguate when its role is not obvious from its name. Stable technology agents are personas such as architect, engineer, and specialist; review, security, testing, and documentation remain skill lenses or evidence-activated addenda. The linter treats `tier:` as authoritative; absent it, the linter infers the tier from the name.

**Rule**: classify the definition as a skill, agent, or worker before selecting a tier or tool grant. When an agent or worker tier is uncertain, default to Tier 2.

## Agent and Worker Execution Boundary Standard

Every new or materially edited framework agent or worker must state near the top of its
body what execution boundary justifies a separate reasoning context. Use an
`## Execution Boundary` section. Scheduler workers can retain the
stronger `## Authority Boundary` heading. The section must name at least one of
these boundaries and state what the profile owns that the caller or invoking
skill does not:

| Boundary | Required justification |
|---|---|
| Context compression | The agent privately consumes materially more evidence than the parent must consume from its return and durable artifacts. |
| Independent scheduling | The work unit can run concurrently, has no order dependency, and returns through a bounded merge contract. |
| Write isolation | Parallel writes use an explicit write set and physical isolation, normally a detached worktree. A separate context alone does not isolate files. |
| Permission or tool asymmetry | The agent declares a narrower or different grant. The active harness profile records the strongest enforceable mapping and any gap. |
| Model or effort asymmetry | The task has an explicit model or effort requirement that the active harness can preserve or must disclose as an approximation. |
| Independence of judgment | A fresh context prevents the review from inheriting the author's rationale or conclusion. |

A reusable capability or a matching skill name is not a boundary. A skill whose
entire workflow validates inputs and dispatches exactly one worker must name the
same boundary in its `## Critical Rules`; otherwise the skill executes the
capability directly. This rule permits deliberate 1:1 pairs such as
`artifact-writer` and `auto-clarity-checker` while rejecting wrapper-only
pairs.

For context-compression claims, declare a bounded return contract and the
evidence volume the profile expects to inspect. A 10:1 private-input-to-return
ratio is a conservative planning signal, not a compliance threshold. Include
durable artifacts the parent rereads and duplicated reads in the cost
comparison. Replace estimates with observed dispatch telemetry when available.

Independent read-only work can use runtime subagents for latency or throughput without
a worktree. Parallel writers require physical write isolation. Any sub-agent
that reaches a human approval checkpoint must suspend and return the stable
checkpoint token to the parent per the User Approval Gates protocol.

`framework/scripts/lint-agent-tiers.py` rejects any agent or worker without an
execution boundary. Scheduler workers may use the stronger
`## Authority Boundary` heading.

## Skill Authoring Standard

Every framework `SKILL.md` must include:

1. `allowed-tools:` in frontmatter (least-privilege tool list)
2. `## Purpose`: 1-2 declarative sentences (not "You are a ...")
3. `## Input Contract`: table of accepted arguments and defaults
4. `## Output Contract`: which artifacts the skill produces (files, directories, and format)
5. `## Termination`: explicit completion condition
6. **Eval coverage** when the skill (a) declares `## Auto-Clarity`, (b) performs side effects, or (c) orchestrates other skills, agents, workers, or commands: ship `framework/evals/skills/<name>/eval.json` per `./.codex/araia/evals/README.md`. Purely informational skills are exempt.

Skills are declarative (what to produce), not procedural (how to execute step-by-step). Move long step-by-step flows to on-demand reference files: `flows/step-N-*.md` for orchestration steps, `references/*.md` for invariants, rules of engagement, and protocols. Every path a SKILL.md cites must exist on disk; citation without materialization is drift.

## Context Budgets

Every byte in a `description:` field that an installed harness exposes contributes
to its discovery context; every byte in a SKILL.md body contributes on
invocation; every byte in an agent or worker body contributes on dispatch. Harness
generation and provider tokenization can change the exact runtime cost.
`check-context-budgets.py` enforces the source-level warn thresholds below in
CI; they tighten over time as outlier refactoring progresses.

| Surface | Budget | Rationale |
|---|---|---|
| Skill `description:` frontmatter | 600 chars | The harness loads it for discovery in every session. State what it does and when to trigger; move detail to `## Purpose`. |
| Agent `description:` frontmatter | 600 chars | The harness loads it for delegation routing. Trigger signals only; capability detail belongs in the body. |
| Worker `description:` frontmatter | 600 chars | The harness loads it for assignment routing. Trigger signals only; execution detail belongs in the body. |
| `SKILL.md` body | 20 KB | Per-invocation tax. Declarative contract only; flows and rules live in reference files. |
| Agent `.md` body | 35 KB | Per-dispatch system prompt. Role-specific content only; shared protocol text stays condensed. |
| Worker `.md` body | 35 KB | Per-dispatch system prompt. Assignment contract only; shared protocol text stays condensed. |
| `adapter.md` | 56 KB | The framework loads it at gates and bootstrap. Schemas that a single skill consults belong in that skill's `references/`. |

A size surface at 90% or more of its budget is reported as `NEAR` and never
blocks: a file inside its budget conforms. Treat it as the signal to plan the
next extraction, because a budget that only reports at 100% announces the
ceiling one edit after the cheap moment to act has passed. A warning that could
fail the build would invite padding the budget instead of extracting.

Writing a good `description:`: one sentence for what the skill, agent, or worker does, one for when to use it (trigger phrases), and optionally one for hard scope limits. Everything else moves to `## Purpose` and the contracts.
