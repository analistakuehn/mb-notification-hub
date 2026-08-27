# Refusal Log Protocol (Shared)

Defines the durable log of skill-level refusals, every time a skill or agent **declines to proceed** because a gate fired, an Auto-Clarity trigger blocked execution, a verdict came back BLOCKED, or a destructive operation was vetoed. The log is the framework's instrument for measuring whether its safety gates are calibrated correctly: too quiet, the gates are not catching real harms; too loud, the gates are friction users will route around.

## Why a Durable Log

Refusals are currently shown to the user inline and then lost. That makes two questions unanswerable after the fact:

1. **Are gates firing too often?** A gate that fires on every other invocation is friction; users will start routing around it. Without a log, this is invisible until a user complains.
2. **Are gates catching real harms?** A gate that has fired a hundred times and was overridden a hundred times is either too sensitive or pointed at the wrong situation. Without a log, the framework cannot tell the difference between "high-value gate" and "noise".

The log answers both questions by making refusals queryable.

## Storage

Path: `.araia/refusal-log.jsonl` (project-local, per-project state).

Format: JSON Lines (one JSON object per line, append-only). Reasoning: trivially appendable from any skill via Bash or Write, trivially parseable for analysis, survives concurrent appends because each line is atomic at the filesystem level on POSIX (and Windows as long as writes stay under a single-write block, ~4 KB, refusal entries are small).

The file is created on first refusal in a project; it is not pre-seeded. Skills MUST NOT delete it. Rotation, compression, or archival is a future-tooling concern; for now the file grows monotonically.

## Entry Schema

Each line is a JSON object with the following fields:

```json
{
  "ts": "2026-05-02T14:30:00Z",
  "skill": "dotnet-scaffold",
  "agent": null,
  "trigger": "auto-clarity-1-private-feed",
  "category": "harmless",
  "context": {
    "spec_id": "SPEC-007",
    "stage": "SPECIFY",
    "command": "/dotnet-scaffold --solution Contoso.Web ..."
  },
  "what_was_proposed": "dotnet restore against pkgs.dev.azure.com/acme/_packaging/...",
  "user_resolution": "approved",
  "user_resolution_text": "yes, internal feed is expected for this project",
  "elapsed_ms": 8400
}
```

| Field | Required | Type | Description |
|---|---|---|---|
| `ts` | yes | ISO-8601 UTC | Time the refusal was raised. |
| `skill` | yes | string | Skill that raised the refusal (or `araia` if the orchestrator caught it). |
| `agent` | no | string\|null | Agent involved if the refusal originated inside an agent dispatch. |
| `trigger` | yes | string | Canonical trigger ID (see below). |
| `category` | yes | enum | One of `helpful`, `harmless`, `honest`: which HHH axis the gate protects. |
| `context.spec_id` | no | string | Active spec, if any. |
| `context.stage` | no | string | Active stage, if any. |
| `context.command` | yes | string | The command (with redacted secrets) that triggered the gate. |
| `what_was_proposed` | yes | string | Plain-prose description of the action the gate blocked. |
| `user_resolution` | yes | enum | `approved`, `declined`, `cancelled`, `timeout`, `n/a` (informational). |
| `user_resolution_text` | no | string | Verbatim user reply when applicable; redact obvious secrets. |
| `elapsed_ms` | no | integer | Wall-clock time between gate-firing and user resolution. |

## Canonical Trigger IDs

Triggers MUST use a stable namespaced identifier so analysis can group them across runs. Format: `<source>-<index>-<keyword>`.

| Source | Examples |
|---|---|
| `auto-clarity-1` | `auto-clarity-1-irreversible-write`, `auto-clarity-1-private-feed`, `auto-clarity-1-amend-published` |
| `auto-clarity-2` | `auto-clarity-2-ambiguous-slice`, `auto-clarity-2-multi-mode-tie` |
| `auto-clarity-3` | `auto-clarity-3-clean-tree`, `auto-clarity-3-no-slice` |
| `auto-clarity-4` | `auto-clarity-4-red-passes`, `auto-clarity-4-build-fail-after-retry` |
| `auto-clarity-5` | `auto-clarity-5-tier-violation`, `auto-clarity-5-conventional-commit-breach` |
| `git-hygiene` | `git-hygiene-no-verify`, `git-hygiene-force-protected-branch`, `git-hygiene-skip-ci` |
| `validation` | `validation-c1-unsourced-finding`, `validation-s1-missing-markers` |
| `verdict` | `verdict-blocked-quality-gate`, `verdict-blocked-code-review` |
| `gate` | `gate-g1-fail`, `gate-g4-fail` |
| `staleness` | `staleness-mtime-mismatch` |
| `language` | `language-low-confidence`, `language-insufficient-evidence` |
| `uncertainty` | `uncertainty-insufficient-evidence` |

When a skill raises a refusal that does not fit an existing trigger ID, it MUST mint a new one in this format and log it. Adding a new ID is a one-line change in the skill plus a row in this table on the next protocol pass.

## Append Mechanics

Skills append by emitting a single line to `.araia/refusal-log.jsonl`. Use atomic-append semantics:

- Bash (POSIX): `printf '%s\n' "$JSON" >> .araia/refusal-log.jsonl`. The shell `>>` open uses `O_APPEND`; entries shorter than `PIPE_BUF` (4096 bytes) interleave safely under concurrency.
- PowerShell: `Add-Content -Path .araia/refusal-log.jsonl -Value $json -Encoding utf8`.
- Write tool: read the file, append the line, write back. Use this only when the line might exceed 4 KB (rare; only if `user_resolution_text` is a paragraph).

Skills MUST NOT use the Edit tool to modify earlier entries. The log is append-only by contract; corrections come as new entries with a `corrects: <line-number-or-ts>` field.

## Reading the Log

The framework does not ship a query tool yet; users grep or jq the file directly. Common queries:

```bash
# Trigger frequency over the last week
jq -r '.trigger' .araia/refusal-log.jsonl | sort | uniq -c | sort -rn

# Most-overridden triggers (gate is too noisy?)
jq -r 'select(.user_resolution=="approved") | .trigger' .araia/refusal-log.jsonl | sort | uniq -c | sort -rn

# Most-declined triggers (gate is catching real harms)
jq -r 'select(.user_resolution=="declined") | .trigger' .araia/refusal-log.jsonl | sort | uniq -c | sort -rn

# Slow refusals (user took >30s to resolve)
jq 'select(.elapsed_ms > 30000)' .araia/refusal-log.jsonl
```

A future `araia refusal-summary` command may consume this file, but the protocol is independent of any tooling layer.

## What NOT to Log

The log is for **safety/correctness gate firings**, not for general telemetry:

- Do NOT log routine clarification questions ("which file did you mean?") that are not protecting a gate.
- Do NOT log successful skill completions.
- Do NOT log validation warnings that did not block execution.
- Do NOT log retry-protocol Level 1/2/3 events, those are operational, not refusals.
- Do NOT log AskUserQuestion prompts that are simple branching choices (mode selection, language selection, etc.): those are not refusals, they are configuration.

If an event would help calibrate the safety architecture (gate calibration, override rate, time-to-resolve), log it. If it would only help debug the skill, do not, use the run summary or skill-specific traces.

## Privacy

The `command` and `user_resolution_text` fields can contain secrets, file paths, business names, etc. Skills SHOULD:

- Redact obvious secrets in `command` (`--token=***`, `Authorization: ***`).
- Avoid storing the full text of generated artifacts in `what_was_proposed`: describe the action, do not embed the artifact.
- Treat the file as project-confidential; do not check it into version control. The framework's default `.gitignore` template excludes `.araia/`.

## Cross-References

- `./.claude/araia/shared/auto-clarity-protocol.md`: the source of triggers `auto-clarity-1` through `auto-clarity-5`.
- `./.claude/araia/shared/git-hygiene-protocol.md`: the source of `git-hygiene-*` triggers.
- `./.claude/araia/shared/validation-protocol.md`: the source of `validation-*` triggers (C1 unsourced findings, S1 missing markers).
- `./.claude/araia/shared/agent-uncertainty-protocol.md`: agents that emit the uncertainty envelope log a `uncertainty-insufficient-evidence` entry.
- `./.claude/araia/shared/failure-taxonomy.md`: `F-GATE` failures map to `verdict-*` and `gate-*` triggers.
