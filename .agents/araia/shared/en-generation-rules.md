# EN Artifact Generation Rules

Constraints applied **during** generation. Single pass, no post-hoc review.

Scope: any artifact generated in English (docs, reports, Delivery Slice specs, commit messages, test names, XML docs, logs, error messages).

## Whole-document language consistency

Write every narrative surface in English: document title, H1-H6 headings,
metadata labels, table headers, captions, callouts, navigation items, lists,
instructions, and body paragraphs. Do not retain Portuguese headings or prose
copied from a template. Preserve identifiers, paths, commands, frontmatter
fields, stage names, lifecycle values, acronyms, and established technical
terms.

## Voice

- Imperative mood for instructions: "Run the build", not "You should run the build".
- Active voice: "The handler throws", not "An exception is thrown by the handler".
- Fragments OK in lists and rules; full sentences in prose.
- Rule pattern: `[Thing] [action] [reason].` Example: "Validator runs synchronously. DB checks belong in Application Services."

## Compression

Drop:

- Articles (a, an, the) when context is unambiguous in step lists.
- Filler: `just`, `really`, `basically`, `actually`, `simply`, `essentially`, `effectively`.
- Hedging: should, might, could, would, may; sure, certainly, of course; this should, might want to, could potentially.
- Padding phrases: `"It is worth noting that"`, `"Please note that"`, `"It is important to"`, `"In other words"`, `"As mentioned above"`, `"As you can see"`.
- Verbose negations: "Never use X" -> "No X"; "Always include Y" -> "Include Y" (in rule lists).
- Explanatory tails when title is self-explanatory.

Keep verbatim:

- Technical terms, identifiers, file paths, command names.
- Code blocks and inline code.
- Error messages, output strings, numbers, thresholds, measurements.

## Punctuation

- Oxford comma in lists of 3+: "read, write, and execute".
- No em dash. The literal em dash `—`, en dash `–`, horizontal bar `―`, and the spaced ASCII ` -- ` are forbidden as punctuation. Parenthetical -> commas or parentheses; strong break or contrast -> period, colon, or semicolon; term-and-definition pair -> colon.
- Colons introduce lists, examples, explanations; lowercase after unless proper noun.
- Semicolons separate complex list items (items containing commas) or related independent clauses.
- Periods end declarative sentences; consistent in lists (all or none).
- Double quotes for direct quotes, single for nested; backticks for code identifiers.

## Substitutions

| Avoid | Use |
|---|---|
| results in / leads to | -> |
| extensive | big |
| implement a solution for | fix |
| `leverage` | `use` |
| `It is recommended that` | `recommend` |
| `There is a need for` | `needs` |
| `In the event that` | `if` |
| appropriate / relevant / proper | (concrete name) |

## Specificity

- Concrete over abstract: reference exact file paths, class names, method names.
- Same term for the same concept (do not alternate "Delivery Slice" and "backlog item").
- "Handlers orchestrate; delegate business logic to Application Services", not "Handlers should not contain business logic".

## Why Single-Pass

These constraints are simple enough to apply during writing. A second-pass review of the entire artifact wastes tokens and produces churn. Write it right the first time. If a constraint slips through, fix only that token.

Single-pass is the discipline, not the only safeguard. A per-unit check right after each write, plus the `Write`/`Edit` harness hook, catches slips surgically, scoped to the unit just written, never a re-read of the whole artifact. See `post-write-language-enforcement.md`.
