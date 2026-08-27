# Post-Write Language Enforcement

Single source of truth for **when** and **how** to apply the framework writing rules (`en-generation-rules.md`, `ptbr-generation-rules.md`) after content production. Skills and agents reference this file instead of restating the rule in prose.

## Principle

Single-pass-at-write stays the discipline: write it right the first time per the active language's generation rules. This file adds the safety net that catches the inevitable slips: a per-unit check right after each write, plus a harness-level hook. Neither is a full second-pass re-read of the whole artifact; both are surgical, scoped to the unit just written.

## Two tracks

| Track | What it covers | Mechanism |
|---|---|---|
| Markdown artifacts | docs, Delivery Slices, refinements, ADRs, reports, READMEs | `$artifact-writer <path> --lang <en\|pt-BR>` (Tier-3 polisher) |
| Code-embedded text | Documentation comments (XML doc, dartdoc, JSDoc, docstrings), log messages, exception and error messages, user-facing strings, code comments, and test text (suite / case descriptions and assertion failure messages) | generation rules applied in place by the adapter engineer; the adapter implementation skill is the final enforcer |

### Prevalidated deterministic-template exception

Skip the Tier-3 `artifact-writer` dispatch only when all conditions below hold:

1. A framework-owned deterministic script renders the artifact from immutable bundle templates or constant strings; the model does not author or paraphrase its prose.
2. The script accepts only constrained scalar substitution values, not free-form user or model text.
3. Before publication, the script runs `framework/scripts/check-writing-rules.py --strict` with the explicit language and correct `markdown` or `source` mode over every rendered unit.
4. A failed lint aborts publication; it never degrades to a warning.

This exception preserves the same deterministic gate while avoiding a model dispatch that cannot improve prevalidated static content. Any free-form section, even inside an otherwise deterministic artifact, removes the exception and follows the normal track.

`artifact-writer` is markdown-only by design: it never edits source files, fenced code, or identifiers. Code-embedded text is therefore enforced at the point of writing, not by `artifact-writer`.

## Timing rule (inline, per unit)

Enforce **immediately after each unit, before moving to the next**. Never batch at end-of-run or defer to a promotion step:

- **Producing skills**: polish each markdown file immediately after writing or staging it. A skill that writes five artifacts polishes each one as it lands, not all five at the end.
- **Coding skills and the TDD DOCUMENT phase**: apply the code-embedded-text rules to the XML docs / logs / messages / comments as they finish each file (REFACTOR/DOCUMENT for the TDD cycle; the generation step for scaffold/test skills).

## Language

Respect the artifact's output language (EN or PT-BR) per `language-detection.md`. Never force-translate. Implementation skills and code-writing agents cite **both** rule files and select per detected language:

```
PT-BR output: read and apply `./.codex/araia/shared/ptbr-generation-rules.md` (do not paraphrase; load the file).
EN output: read and apply `./.codex/araia/shared/en-generation-rules.md` (do not paraphrase; load the file).
```

Language applies to the complete narrative surface: title, H1-H6 headings,
metadata labels, table headers, captions, callouts, lists, template guidance,
and body prose. The post-write pass localizes content copied from a template
when it does not match the resolved language. "Never force-translate" means
never override the resolved language; it does not permit mixed-language
artifacts after language resolution.

## Loading Discipline (read once per context)

Read each rule file at most once per context window: when `ptbr-generation-rules.md` or `en-generation-rules.md` is already in the session context, apply it from context; do not Read it again (every re-read re-injects the whole file for zero new information). In the MAIN conversation, the headline summary in the project `AGENTS.md` managed block plus the harness backstop below replace pre-emptive full-file reads. A full rule-file read belongs to: a dispatched agent (each dispatch is a fresh context and MUST load the file, per the Language section above), a dedicated language review pass, or resolving a hook finding that needs the canonical wording.

## Harness backstop (deterministic, covers subagents)

The master hook lives at `./.codex/araia/hooks/post-write-language-check.mjs`. `$araia init` and `$araia sync` copy it into each project's harness directory and register it as a `PostToolUse` hook. Claude Code executes the project copy through `Write|Edit|MultiEdit`. Codex matches `apply_patch|Edit|Write`, while Kimi matches `Write|Edit`; both execute the stable framework installation, and the Kimi command acts only when the project ledger is present.

After every matched mutation, the hook runs the deterministic linter (`framework/scripts/check-writing-rules.py`, resolved at the global framework path) over each file just written: `--mode source` for code (the linter reads only comments, doc comments, and string literals; it never scans identifiers or logic) and `--mode markdown` for prose. For a Codex `apply_patch` payload, it extracts every `Update/Add/Delete File` header from `tool_input.command`. When the linter reports violations, the hook surfaces them as `additionalContext` with the file, line, and exact correction (`line 83: verificacao -> verificação`), so the model fixes a concrete list instead of rereading the whole artifact. A clean scan is silent. If the linter cannot run because Python or the script is missing, the hook degrades to a generic nudge, so the rule remains visible.

Two properties matter:

- **It covers subagent writes, not only main-agent writes.** Subagents were the blind spot: the earlier nudge-only hook stayed silent for them because it assumed that agent wiring enforced rules inline. Evidence showed otherwise, so whole source files of PT-BR violations shipped. The deterministic scan runs for every writer, closing that gap.
- **It still never blocks.** The hook only adds context the model acts on; it always exits 0 and no-ops on config, generated, and binary files. `check-writing-rules.py` provides the single source for the deterministic verdict. CI also runs the script, so the hook, the CI gate, and any skill that calls it share one rule set.

The script is the gate; the inline per-unit discipline (above) is still the primary safeguard that keeps most writes clean before the hook ever fires. The hook is the deterministic net that catches what slips, for main-agent and subagent writes alike.

## How to reference this

**Reference the track by name; never re-list the surfaces.** The Code-embedded-text track above is the canonical surface set; only this section enumerates it. An agent, worker, or skill cites "the code-embedded-text track of `post-write-language-enforcement.md`" and stops there; it does not copy the surface list. Each adapter maps the track to its own syntax exactly once, in its `code-style.md` (for Flutter: dartdoc `///`, `group` / `test` / `testWidgets` descriptions, `reason:` / `fail()` strings); agents, workers, and skills point to that mapping instead of restating it. When the surface set changes, edit this file, and the adapter `code-style.md` if the syntax mapping shifts, nothing else.

- Producing skills: replace any "polish at promotion / after all artifacts" step with "polish each file per `shared/post-write-language-enforcement.md` (inline, per file)."
- Coding skills that emit source: add an obligation to apply the code-embedded-text track from this file to freshly written files.
- Implementation skills: cite this file in the Critical Rules / Invariants block for the two-language selection above.
