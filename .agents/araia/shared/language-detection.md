# Language Detection Protocol

Single source of truth for language detection across all skills, agents, and workers that produce output in the user's language.

## Supported Languages

| Code | Language |
|---|---|
| `pt-BR` | Brazilian Portuguese |
| `en` | English |

## Two Surfaces, One Configured Language

The configured language governs two surfaces, and both are in scope for every
capability that talks to a user or writes a file:

| Surface | Covers | Resolution |
|---|---|---|
| Artifact | Every durable file a skill, agent, or worker writes | `## Precedence`, with the confidence gate at Step 3a |
| Interaction | Every message addressed to the user | `## Interaction Language`, which never blocks |

## Interaction Language

Everything addressed to the user inside a framework-adopted project (or inside
`~/.araia/framework` itself) is written in the configured language, not only the
files produced. This covers:

- Conversational replies, explanations, and any reasoning shown to the user
- Questions, disambiguation menus, and clarification requests
- Approval prompts, confirmation rituals, and destructive-operation warnings
- Status blocks, dashboards, plans, previews, run summaries, and `Next Action` lines
- Progress narration, error messages, refusal explanations, and Auto-Clarity surfacing
- The user-facing text a dispatched agent or worker returns, including proxied questions

### Resolution (first hit wins)

1. `--lang <pt-BR|en>` on the current invocation, when the routed command accepts it
2. The resolved SPEC manifest `language:`, when the command resolved a manifest
3. `.araia/index.md` frontmatter `language:`, the project default
4. The language the user writes in this conversation
5. `en`

Step 4 is direct evidence, not a heuristic over repository files: the user's own
message states the language they read. Apply it without the marker count, the
confidence score, or the Step 3a prompt.

### Never block on the interaction language

Interaction never pauses to ask which language to speak, and never falls back to
`en` while the user writes another supported language. The confidence gate at
Step 3a governs the artifact language alone, because a wrong artifact language
persists in the repository while a wrong sentence in chat costs one reply. Ask
about language only when the answer changes what gets written.

### Configured language beats the language of the request

A configured language (sources 1 to 3) wins over the language of the incoming
message: a `pt-BR` project answers in `pt-BR` even when one request arrives in
English, and the reverse. Rendering a single reply in the other language
requires the user to ask for it, or `--lang` on that invocation. Text quoted as
evidence (a code comment, tool output, a file excerpt, a user phrase under
analysis) keeps its original wording.

The `### Always English` tokens below stay English on both surfaces: command
names, flags, stage and gate names, identifiers, and file paths.

## Precedence

Artifact-language precedence:
`--lang` flag > inline directive (frontmatter `language: <code>`) > heuristic detection > per-skill default.

## Override Flag

If the user provides `--lang <pt-BR|en>`, use that value regardless of detection. Skip the heuristic entirely.

## Detection Heuristic

### Step 1: Collect Text Samples

Scan in priority order:

| Priority | Source | Skills That Use It |
|---|---|---|
| 1 | Input file content (Initiative Brief, requirements) | global PLAN workflow, global SPECIFY workflow |
| 2 | Requirement artifacts (`.md` in requirements dir) | global REFINE workflow |
| 3 | XML doc comments in `.cs` files | adapter discovery, code review, implementation validation |
| 4 | README.md or CHANGELOG.md | all |
| 5 | Recent git commit messages (`git log --oneline -20`) | semantic-commit |
| 6 | Code comments and string literals | all (fallback) |

### Step 2: Count Language Markers

**PT-BR markers**:

- Accented characters: ã, ç, é, ó, ú, á, â, ê, í, ô, à
- Keywords: `deve`, `quando`, `retorno`, `usuário`, `sistema`, `critério`, `requisito`, `como um`, `para que`, `então`, `dado que`, `implementar`, `configurar`, `validar`, `funcionalidade`, `comportamento`, `cenário`

**EN markers**:

- Keywords: should, when, returns, the, as a, so that, given, then, must, shall, user, system, feature, scenario, requirement, implement, configure, validate

### Step 3: Classify

The classifier produces both a `LANG` choice and a `CONFIDENCE` score. A choice with low confidence is **not** auto-applied; the skill must ask the user before committing to it.

```
LET pt = pt-BR marker count
LET en = en marker count
LET total = pt + en

IF --lang flag provided:                 LANG = flag value;  CONFIDENCE = explicit
ELSE IF inline frontmatter directive:    LANG = directive;   CONFIDENCE = explicit
ELSE IF total < 5:                       LANG = unknown;     CONFIDENCE = insufficient-evidence
ELSE IF max(pt,en) / total >= 0.70 AND
        max(pt,en) - min(pt,en) >= 5:    LANG = winning;     CONFIDENCE = high
ELSE IF max(pt,en) / total >= 0.55:      LANG = winning;     CONFIDENCE = low
ELSE:                                    LANG = unknown;     CONFIDENCE = ambiguous
```

### Step 3a: Confidence Gate

| `CONFIDENCE` | Action |
|---|---|
| `explicit` | Use `LANG` immediately. No prompt. |
| `high` | Use `LANG`. Log the decision in the run summary (`Detected: pt-BR (high confidence: 18 markers, 89% pt-BR)`). |
| `low` | **Ask the user.** Show the marker count and ratio (e.g., `"15 markers found, 60% pt-BR / 40% en: low confidence. Use pt-BR, en, or specify --lang?"`). Use the user's answer. Do NOT silently apply the winning side. |
| `ambiguous` | **Ask the user.** Same prompt as `low`. |
| `insufficient-evidence` | **Ask the user.** Show what was scanned (`"Scanned: README.md (8 lines), git log --oneline -20 (3 entries). Found 2 PT-BR markers, 1 EN marker. Insufficient evidence. Use pt-BR, en, or specify --lang?"`). Do NOT fall back silently to the per-skill default. The per-skill default is reserved for *truly* zero-evidence cases (Step 3c).
| (any) | If detection contradicts the inline frontmatter directive (rare; both supplied), surface the conflict via Auto-Clarity §5; do not pick a side. |

This gate governs the artifact language. The interaction language resolves
through `## Interaction Language` and never blocks: write the prompts in this
table in the resolved interaction language.

### Step 3b: User-Confirmed Override

When the user answers the prompt at Step 3a, treat the answer as `--lang` for the remainder of the run. Persist the choice in the manifest under `language: "<chosen>"` so downstream stages do not re-prompt.

### Step 3c: Fallback to Per-Skill Default

The per-skill default below is used **only** when the user explicitly chooses "use the skill's default" at the Step 3a prompt, OR when the skill is non-interactive (rare; documented per skill). Silent fallback to the default is a protocol violation: the audit's F-9 finding showed that 51% signals were being treated as authoritative.

## Per-Skill Defaults (when inconclusive)

| Skill | Default |
|---|---|
| `semantic-commit` | pt-BR (detected from recent git log) |
| (other skills) | en |

## Language Instruction Injection

When dispatching agents, inject the language instruction at the start of the prompt:

```
[LANG_INSTRUCTION: "ALL content, and every message addressed to the user, MUST be written in [LANG]."]
```

Or in the agent prompt body:

```
## Language
Write the report in [LANG]. Every question, checkpoint, and summary the caller
proxies back to the user is written in [LANG] too.
```

## Application Rules

### Follows the configured language

- Document titles and every H1-H6 heading
- Metadata labels, table headers, captions, callouts, and navigation text
- Template guidance and placeholder instructions that remain in a rendered artifact
- Artifact descriptions and body text
- Commit message descriptions and bodies
- User-facing summaries and dashboards
- Acceptance criteria text
- Finding descriptions and recommendations
- Every message addressed to the user, per `## Interaction Language`

The configured language governs the complete narrative surface, not only body
paragraphs. A `pt-BR` artifact cannot retain headings such as `Executive
Summary`, `Decision Drivers`, or `Approval Record`; an `en` artifact cannot
retain their PT-BR equivalents. Preserve only the tokens listed under **Always
English** and the technical English terms explicitly allowed by the active
generation-rules file.

### Always English (regardless of configured language)

- Technical tokens: type, scope, identifiers, file names
- Commit types: `feat`, `fix`, `refactor`, etc.
- Commit scopes: module/component names
- Frontmatter field names: `spec-id`, `lifecycle`, `status`
- Stage names: SPECIFY, REFINE, PLAN, IMPLEMENT, VERIFY, DELIVER
- Role names: Business Analyst, TDD Driver, etc.
- Gate names: G2, G3, G4, G5, G6

## PT-BR Specific Rules

When language is `pt-BR`, also apply `./.agents/araia/shared/ptbr-generation-rules.md` during writing. Headline constraints:

1. Mandatory accentuation: never "adicao" when it should be "adição".
2. Imperative mood verbs: "adicionar", "remover", "corrigir" (not infinitive "adicionando").
3. Diacritics, cedillas, tildes are required, not optional.

## EN Specific Rules

When language is `en`, also apply `./.agents/araia/shared/en-generation-rules.md` during writing. Headline constraints: imperative; active voice; no hedging or filler; Oxford comma.

## Propagation

Once detected (or overridden), store the language in:

1. Spec frontmatter: `language: "pt-BR"`
2. Manifest: `language: "pt-BR"`
3. Spec index (`.araia/index.md`) frontmatter: `language: "pt-BR"`, the project
   default that commands resolving no manifest read (`review` without a SPEC,
   `adopt`, `docs`, `pr`, plus any plain conversation in the project)
4. Per-stage arguments: `--lang pt-BR`

Downstream stages inherit from the manifest. The user can override per-stage.

Within one conversation, resolve the interaction language once and keep it for
every later message. Re-resolve only when a higher-precedence source changes:
a new `--lang`, or a command that resolves a manifest declaring a different
language. Do not re-detect per message.
