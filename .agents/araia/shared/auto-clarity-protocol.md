# Auto-Clarity Protocol

Standard fallback protocol for framework capability definitions. Every skill, agent, or worker that alters default behavior (tone, flow, restrictions, or automatisms) must declare an `## Auto-Clarity` section enumerating situations in which it temporarily falls back to normal behavior and returns to its declared mode after clarification.

## Motivation

Skills without explicit guardrails can propagate costly errors: executing a destructive operation under ambiguity, proceeding with a plan whose intermediate step failed silently, or applying a compressed tone to a safety warning that needs absolute clarity. The Auto-Clarity Protocol consolidates these guardrails into a uniform, named clause instead of scattering them across ad-hoc rules.

## When a Skill, Agent, or Worker Needs the Section

Every installed skill and every runtime profile that satisfies at least one of the conditions below must include the `## Auto-Clarity` section in its definition file (`SKILL.md` for skills, `.md` system-prompt body for canonical agents and workers). Canonical sources live in `skills/`, `agents/`, or `workers/`; a harness can project both agents and workers into its runtime agent directory.

- Performs or proposes operations with side effects on disk, git, database, network, or process.
- Makes automatic decisions users can choose to review (type, scope, agent, or target selection).
- Persistently alters the response tone or verbosity.
- Orchestrates other skills, agents, or commands.

The protocol permits purely informational skills, agents, and workers (reference cards, read-only lookup helpers, or mechanical formatters with no autonomy) to omit the section.

## Standing Honesty Obligation (always on)

Independent of any specific trigger below, every skill and agent operates under
a standing obligation to **surface low-confidence claims, evidence gaps, and
contract uncertainty** even when no enumerated trigger matches. The five
triggers below describe situations that warrant a fallback to normal prose and
a clarification question. The standing obligation is broader, but durable
documents must also follow
`./.agents/araia/shared/unknown-information-policy.md`.

- The skill makes a claim on partial evidence and the gap has the potential to change the answer.
- A finding cites no file/line or code excerpt, yet reads as authoritative.
- The skill fills a contract field by inference rather than observation.
- The skill produces output it cannot stand behind when asked "are you sure?".

Uncertainty conventions:
- For durable artifacts: append `[LOW-CONFIDENCE: <one-line reason>]` only to a
  supported partial-evidence claim that the document type permits. Omit
  unsupported values, placeholders, and unresolved questions. A blocking gap
  pauses publication; surface it in the response or checkpoint.
- For responses: state the uncertainty in the same sentence as the claim,
  never in a trailing disclaimer.
- For agent outputs that cannot reach a finding at all: emit the structured uncertainty envelope from `./.agents/araia/shared/agent-uncertainty-protocol.md` instead of forcing a finding.

This standing obligation does not replace the five triggers. It covers the gap
between them. A skill that produces an authoritative-sounding artifact while
privately knowing a claim is shaky violates the protocol, as does a skill that
persists `UNKNOWN` or an Open Questions section instead of omitting or
resolving the unsupported information.

## The Five Minimum Triggers

The skill's `## Auto-Clarity` section must cover, at minimum, the five triggers below. The protocol permits the skill to add domain-specific triggers but prohibits it from reducing this list.

### 1. Safety Warnings and Irreversible Actions

Destructive operations (delete, drop, force-push, reset --hard, or file overwrite without backup), credential exposure, and breaking changes to public contracts. The skill must present explicit confirmation before executing and use clear prose without stylistic compression.

### 2. Material Ambiguity

When user input admits two or more interpretations with different consequences, ask for clarification instead of guessing. "Material" qualifies the ambiguity: small stylistic variations do not trigger it, only those that change the outcome.

### 3. User Visibly Confused or Mistaken

When the user shows incorrect understanding of system state, skill contract, or command effect, explain before executing. Typical signals: a command contradicts observed state, a repeated question phrased differently, or a factually wrong premise in the message.

### 4. Multi-Step Sequences with Cross-Dependencies

When the next step depends on the result of a previous step that can fail silently (a zero exit code with an invalid result, partial parsing, or truncated output), validate the intermediate result before proceeding.

### 5. Conflict with Global or Project Rules

When faithful skill execution conflicts with `~/.claude/CLAUDE.md`, `settings.json`, framework rules, or local project conventions, pause and expose the conflict to the user. The skill must never silently resolve the conflict in favor of its own rules.

## Section Format

In a `SKILL.md`, the section must appear after `## Termination` and before `## Reference Files` (or structural equivalent). In an agent's or worker's `.md` body, the section must appear after the main system-prompt body and any "Critical Rules" / "Inviolable Rules" block, and before any "Reference Files" or appendix block (or at the end of the body if no such trailing section exists). Canonical format:

```markdown
## Auto-Clarity

Standing obligation: surface low-confidence claims, evidence gaps, and contract uncertainty inline at all times — see `shared/auto-clarity-protocol.md` "Standing Honesty Obligation".

The skill temporarily falls back to normal prose and standard flow in the following situations, returning to skill mode after clarification:

1. **Safety warnings and irreversible actions**: <description contextualized to the skill>.
2. **Material ambiguity**: <description contextualized to the skill>.
3. **User visibly confused or mistaken**: <description contextualized to the skill>.
4. **Multi-step sequences with cross-dependencies**: <description contextualized to the skill>.
5. **Conflict with global or project rules**: <description contextualized to the skill>.

<additional domain-specific triggers, if applicable>
```

The skill must adapt each trigger description to its own context (for example, trigger 1 in a commit skill mentions `--no-verify` and amend of a published commit; in an orchestration skill it mentions manifest destruction or stage rollback).

## Relation to Existing Clauses

Current skills have scattered fragments that overlap with the Auto-Clarity Protocol: Error Recovery tables, Inviolable Rules lists, and numbered invariants. The Auto-Clarity Protocol does not replace these sections but can reference them. The practical rule is:

- Error Recovery: what to do when something has already failed. Stays as is.
- Inviolable Rules: absolute rules that admit no exception. Stays as is.
- Auto-Clarity: what triggers fallback to normal prose and clarification. New named section.

When an Inviolable Rules or Error Recovery item describes a fallback trigger, the `## Auto-Clarity` section can cite it by cross-reference instead of duplicating text.

## Validation

A skill, agent, or worker complies with the protocol when:

1. The `## Auto-Clarity` section exists in the definition file, in the canonical position.
2. The five minimum triggers are present, contextualized to the skill, agent, or worker domain.
3. Additional triggers, when present, are complementary and do not replace any of the five minimums.
4. Each trigger description is specific enough that a reader can recognize the situation in practice, avoiding generic text.
5. The standing-honesty-obligation preamble (or an explicit cross-reference to the canonical text in `shared/auto-clarity-protocol.md`) appears at the top of the section, signalling that uncertainty surfacing is always on, not gated by the five triggers, while durable artifacts omit unsupported information per `shared/unknown-information-policy.md`.

### Refusal Logging (mandatory)

Every time a trigger fires and the skill blocks, asks, or pauses, the skill MUST append an entry to `.araia/refusal-log.jsonl` per `./.agents/araia/shared/refusal-log-protocol.md`. This is how the framework measures trigger calibration. Skipping the log silently is a protocol violation: the gate fired but no one can see it later. Append the log entry after capturing the user's resolution, so the entry can record the resolution; for non-interactive refusals (the skill aborts without a prompt), log immediately with `user_resolution: "n/a"`.

### Automated check

The framework ships an `auto-clarity-checker` skill with a Tier-3 worker that mechanically applies the criteria above to one or many target files (`SKILL.md`, agent `.md`, or worker `.md`). The checker autodetects the kind from path: files in `skills/` named `SKILL.md` are skills; files in `agents/` are agents; and files in `workers/` are workers. Invoke it during authoring or as a periodic lint pass:

- Single file: `/auto-clarity-checker <path>`
- Batch (skills): `/auto-clarity-checker --glob "**/skills/*/SKILL.md"`
- Batch (agents): `/auto-clarity-checker --glob "**/agents/*.md"`
- Batch (workers): `/auto-clarity-checker --glob "**/workers/*.md"`

The checker is read-only. It reports BLOCKER / WARN / INFO findings and a per-file verdict (COMPLIANT / NON-COMPLIANT / EXEMPT) but never edits the target. Authors apply fixes manually based on the report.

Once the framework adopts tri-arm evals (next pattern in the adoption list), the framework can additionally test triggers with adversarial scenarios.
