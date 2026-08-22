<!--
  Framework-managed file. Installed and kept in sync by /araia (init / sync) as
  .github/PULL_REQUEST_TEMPLATE.md. Edit the canonical copy in the Araia framework,
  not the per-project copy, or your changes will be overwritten on the next sync.

  Section HEADINGS and Conventional-Commit type tokens (feat, fix, docs, ...) stay in
  English. Write the prose under each heading in your team's language.

  GitHub mechanics: closing keywords (Closes #N) auto-close an issue only when the issue
  is in the SAME repo and the PR merges into the default branch. Draft PRs do not request
  required reviewers until marked ready.
-->

## Summary

<!-- One or two sentences: what does this PR do and, above all, why is it needed? Lead with intent, not a restatement of the diff. -->

## Changes made

<!-- The what and how: the approach taken and any notable implementation decisions a reviewer should understand to read the diff. -->

## Linked issues

<!-- Use a GitHub closing keyword to auto-close on merge: Closes #N, Fixes #N, Resolves #N (cross-repo: Closes owner/repo#N). Spec and backlog references are welcome here: Refs SPEC-007 / SLICE-003. Remove this section if nothing applies. -->

## Type of change

<!-- Check the type that matches this change. Tokens map to Conventional-Commits types and stay English. -->

- [ ] Bug fix (`fix`)
- [ ] New feature (`feat`)
- [ ] Documentation (`docs`)
- [ ] Refactor (`refactor`): behavior unchanged, including renames and cleanup
- [ ] Performance (`perf`)
- [ ] Tests (`test`)
- [ ] Build or CI (`build`, `ci`)
- [ ] Chore (`chore`): config bumps, dependency updates, tooling
- [ ] Breaking change: complete the section below

## How to test

<!-- Where should the reviewer look first, and where does the risk concentrate? Then the exact steps or commands to reproduce verification, with the expected result. -->

## Breaking changes & migration

<!-- Required when "Breaking change" is checked; otherwise delete this section. What breaks, which consumers/APIs/clients are affected, the migration steps, and the version bump. -->

## Risk & rollback

<!-- Optional; one line is fine for low-risk changes. Blast radius if this is wrong, and how to undo it (revert commit / feature flag / down-migration). Note any irreversible step or data backfill. -->

## Checklist

<!-- Check an item only when it is true and evidenced (e.g. test files appear in the diff). Leave unevidenced items unchecked and add a one-line "N/A: reason". Never blanket-check. -->

- [ ] Tests added or updated to cover this change
- [ ] Documentation updated
- [ ] Contract changes propagated to dependent APIs and consumers
- [ ] Verified locally per the steps above

<details>
<summary><strong>Optional: screenshots and reviewer notes</strong></summary>

**Screenshots / recordings**

<!-- For UI or output changes: before/after images or a short recording. -->

**Notes for the reviewer**

<!-- Known gaps, follow-ups, anything intentionally out of scope, or why an alternative approach was rejected. -->

</details>
