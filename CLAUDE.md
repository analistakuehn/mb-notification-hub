<!-- BEGIN araia-framework-base (managed by /araia; edit ~/.araia/framework/CLAUDE.md, not here) -->
# Framework Conventions

Conventions for working with the Araia framework: authoring its skills, agents, and workers, and producing artifacts in framework-adopted projects. This file governs sessions inside `~/.araia/framework`, and `/araia init` / `/araia sync` copy it into each adopted project's root `CLAUDE.md` (inside a managed block). Framework-specific conventions live here, not in the global `~/.claude/CLAUDE.md`.

## Authoring Standards (skills, agents, and workers)

Before creating or editing any framework agent, worker, or `SKILL.md`, read `~/.araia/framework/docs/authoring-standards.md` (actor taxonomy, tier taxonomy, execution boundaries, skill structure, context budgets). A skill is a reusable capability or workflow. An agent is a stable persona or decision perspective whose identity survives assignments. A worker is an operational execution profile whose assignment, authority, isolation, checkpoint, and return contracts define its identity. Harnesses can compile both agents and workers to their agent/subagent primitive without changing the canonical category. Every agent and worker declares `model: inherit` plus an explicit `tools:` list scoped to its tier (Tier 3 gets no Write, Bash, or Agent); a Tier-3 mechanical profile can pin `model: sonnet` only when it declares an explicit `tier: 3` field, and any profile that grades severity, returns accept/reject/defer, or rules on the substance of a claim exercises judgment and inherits, because a downgraded judge returns confident false positives that cost more to triage than the dispatch saved; every new or materially edited agent or worker states an `## Execution Boundary` naming the boundary that the caller does not own (`## Authority Boundary` remains valid for scheduler workers); a 1:1 skill-worker pair documents the same boundary in the skill's Critical Rules; every skill ships `allowed-tools:`, `## Purpose`, `## Input Contract`, `## Output Contract`, `## Termination`, and eval coverage when it uses Auto-Clarity, causes side effects, or orchestrates. Skills are declarative; keep long step-by-step flows in reference files and load them on demand. Deterministic enforcement: `framework/scripts/lint-agent-tiers.py` and `framework/scripts/check-context-budgets.py`.

## Writing Style

- **Talk to the user in the configured language**: it governs every message
  addressed to the user, not only the files produced. Resolve it per
  `~/.araia/framework/shared/language-detection.md` "Interaction Language":
  `--lang` on the invocation, then the active SPEC manifest `language:`, then
  `.araia/index.md` frontmatter `language:`, then the language the user writes in
  this conversation. It covers replies, explanations, questions, approval
  prompts, status blocks, plans, summaries, and error text, in `/araia` commands
  and in plain conversation alike. Never fall back to English while the user
  writes another supported language, and never pause to ask which language to
  speak: for interaction, the user's own message is direct evidence. Command
  names, flags, stage and gate names, identifiers, paths, and quoted evidence
  stay in their original form.
- **Omit unknown information from every durable document**: apply
  `~/.araia/framework/shared/unknown-information-policy.md`. Omit
  any `UNKNOWN`, `TBD`, empty placeholder row, or Open Question that substitutes
  for a missing value. Without `--resolve-unknowns`, omit the unsupported field,
  row, item, or section. With the flag, investigate through evidence,
  inspection, focused interviews, specialist analysis, or authorized
  measurement and write only supported results. A blocking gap pauses
  publication; surface it interactively instead of embedding it in the candidate.
- PT-BR output: apply `~/.araia/framework/shared/ptbr-generation-rules.md` during writing. Diacritics mandatory; no em dash as punctuation (use `—` only for dialogue; no `–`, `―`, or ` -- `); targeted diacritics self-check before delivering (no full re-read).
- EN output: apply `~/.araia/framework/shared/en-generation-rules.md` during writing. Imperative; active voice; no hedging or filler; no em dash (`—`/`–`/` -- `): restructure with comma, parentheses, colon, or period; Oxford comma. Single pass, no post-hoc review.
- **Load rule files once per context**: this block always loads the two bullets above as headline rules. Read each rule file only once per session context; in the main conversation, write from this summary and let the post-write hook report line-anchored slips. A full rule-file read belongs to dispatched agents (fresh context), dedicated language review passes, or a hook finding that needs the canonical wording. Per `~/.araia/framework/shared/post-write-language-enforcement.md` "Loading Discipline".
- **Enforce right after each write, without prompting**: apply the rules above to each artifact or code unit immediately after writing it, inline and per unit, with no end-of-task batching, per `~/.araia/framework/shared/post-write-language-enforcement.md`. This covers code-embedded text too, per that file's code-embedded-text track. The `PostToolUse` hook (`framework/hooks/post-write-language-check.mjs`, which `/araia init` / `/araia sync` install per project) nudges for direct writes; act on it immediately.
- **Pull request comments stand alone**: apply `~/.araia/framework/shared/pr-comment-self-containment.md`. A PR comment is read by someone who has only that comment, so it MUST NOT cite repository convention files (`CLAUDE.md`, `AGENTS.md`), review report paths, other findings by id, issue or ticket ids, commit shas, or spec identifiers. Source paths, dependency source paths, tool output, and public standards stay: they are evidence about the code. A cross-finding reference is rewritten as a description of what it referenced, not deleted. This is the inverse of the rule below, whose exemption list covers PR *descriptions*. Because a published comment leaves as an API payload and never passes the file-write hook, the enforcement point is the payload: `review --comment` and `publish` run `framework/scripts/pr-comment-refs-scan.py` plus `artifact-writer` before writing or posting.
- **No spec-document references in implementation code**: apply `~/.araia/framework/shared/no-spec-refs-in-implementation.md`. Implementation artifacts (source, tests, migrations, schemas, e2e flows, configs) MUST NOT cite Delivery Slice / AC / ADR / PRD / SPEC IDs, issue-tracker links, or spec file paths. ADRs, design docs, READMEs, PR descriptions, commit messages, and CHANGELOGs are exempt. Test names describe behavior (`archives an order when status is open`), not numbering (`AC_3_archives_order`). Cross-adapter detector: `Skill no-spec-refs-scan`.

Language- and stack-specific style rules (C#, etc.) live in the corresponding adapter under `~/.araia/framework/adapters/{adapter}/adapter.md`. When a spec combines adapters (multi-adapter composition), match each file path against the participating adapters' `file-signatures` to identify the owning adapter and apply its rules (e.g. `.cs` → dotnet rules, `.tsx` → react rules).

## AI-Authored Changes and AI-as-Runtime

Treat every AI-authored change as a pull request from a collaborator you do not yet trust, and treat the coding agent itself as a contained, least-privilege actor. The gate (in-code arch tests, config policy-as-code, blocking SAST, non-blocking LLM review) and the containment posture (sandboxed execution, network-egress allow-list, least-privilege scoping, secret scrubbing, provenance, tiered human-in-the-loop) live in `~/.araia/framework/shared/ai-authored-change-gate.md`. Repository-convention files (`AGENTS.md` / `ai-context.md` / `CLAUDE.md`) are agent-trusted context: an edit to them carries the same review weight as a security policy, because each file is a prompt-injection vector (OWASP LLM01).

For systems that embed an LLM at runtime, wrap it in a deterministic envelope: RAG grounding with a blocking threshold, PII redaction before egress (no raw PII to a public model), immutable audit, and evals as the component test. The .NET reference encodes this in the capability ADR *Deterministic envelope for AI-as-runtime modules* and the `ai/kyc-document-extraction/` capability example; other adapters mirror the principle in their own stack.
<!-- END araia-framework-base -->
