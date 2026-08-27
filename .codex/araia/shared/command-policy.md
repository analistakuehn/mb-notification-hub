# Command Policy

Mechanical, per-harness enforcement counterpart to
`shared/git-hygiene-protocol.md`. That protocol tells skills and agents which
git operations require explicit confirmation; this file, plus its structured
source `shared/command-policy.json`, is what turns a subset of those rules
into something the harness itself checks before a command runs, instead of
relying only on the agent following the prose.

## Why a mechanical layer

`ai-authored-change-gate.md` frames agent containment as defense in depth:
deterministic layers gate, the probabilistic layer (an agent reading and
following its own instructions) only advises. Until this file existed, every
git-hygiene rule sat entirely in the probabilistic layer: nothing stopped
`--no-verify` or `git add -A` except the acting agent choosing to follow
`git-hygiene-protocol.md`. `command-policy.json` is the deterministic
counterpart for the git-operation subset of that gate. It does not replace
the protocol or its Auto-Clarity triggers; a skill still owns the
user-facing confirmation ritual (naming the rule and the reason). The harness
layer is a backstop for when a skill's own check is skipped, degraded, or
bypassed by construction (a raw Bash call outside any skill).

## Two rule shapes

`command-policy.json` splits into `prefix-rules` and `content-rules` because
the two need different enforcement primitives:

| Shape | Matches | Example | Reliable primitive |
|---|---|---|---|
| `prefix-rules` | A flag or subcommand that always appears immediately after the git verb | `git push --force`, `git add -A` | Native ask/prompt patterns in Claude, Codex, and Kimi |
| `content-rules` | A flag that can appear anywhere later in the argument list | `git commit -m "wip" --no-verify` | Claude/Codex full-command `PreToolUse`; explicit Kimi `Bash(*token*)` permission mappings |

Claude Code's own documentation names this exact limitation: a permission
pattern that tries to constrain arguments by position is fragile, and the
documented fix for "does this flag appear anywhere" is a `PreToolUse` hook,
not a `Bash(...)` pattern. `content-rules` exist as a separate list because
they need that different mechanism.

## Decision vocabulary

Every rule in this file resolves to `ask`, never a hard block. This is
deliberate, not an oversight: `git-hygiene-protocol.md` Rule 1 reserves an
explicit, session-scoped user override for each of these operations ("the
user has explicitly typed, in this session, a request that names the bypass
and the reason"). A hard `deny` (Claude Code) or `forbidden` (Codex) cannot
express that carve-out, since neither harness distinguishes "the user just
asked for this" from any other invocation once the pattern matches. `ask`
mirrors the protocol's actual intent: force a confirmation, don't remove the
choice.

## Per-harness enforcement

See `harness/harness-contract.md` `command_policy` and each profile's own
row for the authoritative mapping. Summary:

| Harness | `prefix-rules` | `content-rules` |
|---|---|---|
| Claude Code | `.codex/hooks.json` `permissions.ask`, one `Bash(<pattern> *)` entry per rule | `hooks/pre-bash-git-hygiene-check.mjs`, wired as `hooks.PreToolUse[]` matcher `Bash` |
| Codex | `.codex/rules/araia.rules`, one `prefix_rule(decision="prompt")` per rule | `PreToolUse` inspects the full command and denies a match before execution because Codex does not yet support `permissionDecision:"ask"`; the user can execute the reviewed exception manually |
| Kimi Code | `$KIMI_CODE_HOME/config.toml` native `[[permission.rules]] decision="ask"` | The same native rules, generated from explicit `kimi-pattern`/`kimi-patterns` fields in the canonical policy |

## Known limitations (read before trusting this as a hard boundary)

- **Conservative over-approximation on three prefix rules.** `rebase`,
  `commit --amend`, and `cherry-pick` are asked on every invocation, not only
  when the affected commits are already published, because a static pattern
  cannot check push status or target-branch state. This means occasional
  prompts on operations that turn out to be safe; that is the accepted
  trade-off over a silent history rewrite that turns out not to be.
- **`content-rules` matching is a text scan, not a shell parse.** The hook
  looks for each pattern anywhere in the raw command string, gated only by
  the presence of the word `git`. It does not track quoting, so a commit
  message that happens to contain the literal text `--no-verify` inside a
  quoted string produces an unnecessary `ask` (safe-side false positive, not
  a false negative). It also does not track compound commands segment by
  segment, unlike Claude Code's own permission-pattern engine; a match
  anywhere in a `&&`-chained command triggers the decision for the whole
  chain.
- **Fails closed on policy integrity failure.** Install and generation halt on
  a missing, malformed, incomplete, or non-`ask` policy. At runtime, the
  pre-execution hook asks before any git command when it cannot load the
  policy. Codex uses its safe `deny` equivalent because `ask` is not supported
  in that hook event.
- **Not a substitute for the four-layer gate.** This file covers the git-
  operation subset of `ai-authored-change-gate.md` Part B only. Sandboxed
  execution, a network-egress allow-list, and secret scrubbing are separate,
  not-yet-mechanized concerns; do not read this file's existence as evidence
  that the full containment posture is enforced.

## Updating the policy

Edit `command-policy.json`, not the generated artifacts. `araia sync`
regenerates Claude's `permissions.ask`, Codex `.rules`, and Kimi native
permission entries from the current JSON. A rule change reaches an adopted
project on its next sync, the same as any other managed file.
