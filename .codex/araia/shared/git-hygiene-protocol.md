# Git Hygiene Protocol (Shared)

Framework-level rules every commit-, push-, or rebase-producing skill MUST follow. The goal is to keep local hooks, signing infrastructure, and shared-history conventions intact, since these are usually the user's last line of defense against bad commits reaching the remote.

## Scope

This protocol applies to any skill or agent that:

- Stages or commits files (`git add`, `git commit`, `git commit --amend`).
- Pushes to a remote (`git push`, `git push --force`, `git push --force-with-lease`).
- Rewrites local history (`git rebase`, `git reset --hard`, `git cherry-pick` onto a published branch).
- Generates or proposes any of the above as suggested commands for the user to run.

The rules apply equally to commands the skill executes itself and to commands it prints for the user to copy.

## Inviolable Rules

### Rule 1: Never bypass hooks without explicit user authorization

Skills MUST NOT include any of the following flags in commit/push commands:

- `--no-verify` (skips pre-commit, commit-msg, pre-push hooks)
- `--no-gpg-sign` / `-c commit.gpgsign=false` (bypasses signing)
- `--no-signoff` when the project enforces DCO signoff
- Any equivalent that disables hook execution

If a hook fails, the correct response is to **investigate and fix the underlying issue**, not to bypass the hook. Hook failure is signal, not noise.

The only exception: the user has explicitly typed (in this session) a request that names the bypass and the reason. Memory of past authorization in another session does NOT count. Git operations are session-scoped per `~/.codex/AGENTS.md` ("A user approving an action once does NOT mean that they approve it in all contexts").

### Rule 2: Never silently amend or rewrite published commits

`git commit --amend`, `git rebase`, and `git push --force` rewrite history. When the rewritten commits already exist on a remote, this affects everyone tracking the branch.

Skills MUST:

- Default to creating a **new** commit rather than amending an existing one.
- Before any amend/rebase/force-push, check whether the affected commits exist on a remote (`git log @{u}..HEAD` is empty means the local branch is behind or equal, so amending is safe; non-empty means the local branch has unpushed commits, so amending them is safe). If the affected commits are already on a remote, surface the situation to the user and obtain explicit confirmation before proceeding.
- Never use `git push --force` against `main`, `master`, `develop`, `release/*`, or any branch the project marks as protected. Prefer `--force-with-lease` over raw `--force` when force-pushing is necessary on a feature branch.

### Rule 3: Never skip CI/required-status checks via flags or rerun loops

Skills MUST NOT:

- Add `[skip ci]`, `[ci skip]`, or equivalent magic strings to commit messages without explicit user request that names the reason.
- Re-run failing CI checks in a loop hoping for a different result. Flaky test? Surface the flakiness, do not mask it.
- Merge a PR via `gh pr merge` while required checks are failing or pending without user confirmation.

### Rule 4: Never mix unrelated changes into a single commit

If the staged set spans unrelated logical changes, split them. The skill's commit message describes the *why* of the change; one commit, one *why*. Lumping unrelated changes hides the why and makes revert hard.

This rule is descriptive guidance for `semantic-commit`-style skills, but applies to any skill that constructs commits.

### Rule 5: Surface signing failures, do not work around them

If `git commit` fails due to GPG/SSH signing configuration, the skill MUST surface the underlying error and ask the user to fix the signing setup. The skill MUST NOT:

- Disable signing for that commit (see Rule 1).
- Switch the commit to a different author/committer to dodge the signing requirement.
- Stash and re-stage to "reset" the commit state.

### Rule 6: Stage explicitly, never `git add -A` / `git add .` blindly

Skills that stage files MUST list the files by path. Wildcard staging risks pulling in `.env`, credentials, build artifacts, IDE config, or in-progress work the user did not mean to commit. The Bash-tool default rule in `~/.codex/AGENTS.md` already says this for ad-hoc commits; this protocol extends the rule to every commit-producing skill.

When the staged set is large (>20 files) and the skill cannot enumerate confidently, surface the list to the user for confirmation before commit.

## Auto-Clarity Triggers (commit-$push-producing skills)

Skills covered by this protocol must include at least these domain-specific Auto-Clarity triggers in addition to the five minimums:

- The proposed commit/push command contains any of: `--no-verify`, `--no-gpg-sign`, `--force` (without `-with-lease`), `--force-with-lease` against a protected branch, `[skip ci]`.
- The proposed action would amend or rebase commits that exist on a remote.
- A pre-commit, commit-msg, or pre-push hook just failed and the skill is considering retry, bypass, or alternative path.
- Signing fails and a non-signed commit would otherwise proceed.

Each trigger above must produce explicit user confirmation that **names the relaxed rule and the reason**. Generic "should I retry?" prompts do not satisfy this protocol.

## Anti-Patterns

1. **"Hook is failing, let me just `--no-verify` past it."** No. Investigate. Hooks exist because the user opted into them.
2. **"The amend is small, surely it's fine."** No. If the affected commit exists on a remote, ask first. The bytes-changed-on-disk are not a proxy for blast radius.
3. **"Force push to fix the branch."** Use `--force-with-lease`. Never raw `--force` against shared branches.
4. **"Add `[skip ci]` to land docs faster."** Only if the user asked. Otherwise CI is the project's policy.
5. **"`git add -A` is faster."** Yes, and it is exactly how `.env` ends up in commits. List paths.

## Cross-References

- `~/.codex/AGENTS.md`: base Bash-tool git safety protocol; this file extends it to skill/agent behavior.
- `./.codex/araia/shared/auto-clarity-protocol.md`: the trigger framework this protocol plugs into.
- `./.codex/araia/shared/failure-taxonomy.md`: hook failure maps to `F-TOOL`, but recovery routes through this protocol's Rule 1 (investigate, do not bypass).
- `./.codex/araia/shared/command-policy.md`: the mechanical, per-harness enforcement counterpart to Rules 1, 2, 3, and 6 above (Claude Code `permissions.ask` plus a `PreToolUse` hook; Codex `.rules`). This protocol remains the source of truth for *why*; that file and `command-policy.json` are the *what gets checked before the command runs*, where a harness can express it.
- Skills and workflows covered today: `semantic-commit`, `semantic-pr` (push + PR-create steps), the global IMPLEMENT commit boundary, any future `$commit`-flavored skill, and `$araia commit`. Each must cite this protocol and adopt the Auto-Clarity triggers above.
