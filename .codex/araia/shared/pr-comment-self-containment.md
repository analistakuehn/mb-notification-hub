# Self-Containment in Pull Request Comments

Cross-adapter rule. Applies to every pull request comment body an Araia
capability composes or publishes: `review --comment` output, the review body and
summary `publish` assembles, and any adapter's `code-review comment`
contribution. Authoritative.

## Rule

The subject of this rule is the **comment body**, not the file that holds it. A
composed comment file has two parts, frontmatter and body, and only the body is
published; the declaration lives in `shared/templates/pr-comment.template.md`
"The comment file has two parts", and `commands/publish.md` step 6 extracts it.
Everything below governs what reaches the provider.

A pull request comment MUST stand alone for a reader who has only that comment.
It MUST NOT reference anything the reader cannot open from the comment itself:

- repository convention files (`AGENTS.md`, `AGENTS.md`, `ai-context.md`);
- review report paths, report ids, or adherence report paths;
- **other findings by id** (`ARC-001`, "see the security finding above");
- issue or ticket ids and links (`#482`, `PROJ-123`, `AB#1234`);
- commit shas;
- Delivery Slice, AC, ADR, PRD, or SPEC identifiers; and
- spec file paths (`docs/SPEC-001/...`).

What stays, because it is evidence about the code rather than a pointer to a
document the reader lacks:

- source paths and line numbers in the repository under review;
- dependency source paths (`node_modules/@radix-ui/...`);
- tool output: compiler messages, test output, linter findings, profiler
  numbers;
- public technical standards and specifications (WCAG, RFCs, MDN, language and
  framework documentation); and
- permalinks to the repository under review at the head commit, which `publish`
  emits for anchors outside the diff.

## Why this rule is stricter than the implementation rule

`shared/no-spec-refs-in-implementation.md` governs a different surface and
explicitly **exempts pull request descriptions**, which should cite the Delivery
Slice and the ADR for traceability. A pull request *comment* needs the inverse
rule, and a stricter one, for two reasons:

1. **The audience is different.** A PR description is read by someone working
   inside the project, with the spec tree available. A comment is read by the
   author in a notification, often on a phone, with nothing but the diff.
2. **The pointers do not exist on the destination.** A finding id appears
   nowhere in the pull request, because the comment template puts no id in the
   title. `see ARC-001` therefore references something the reader cannot find
   anywhere, which is worse than an unexplained assertion: it looks like an
   oversight on the reader's part.

## Cross-finding references need a rewrite, not a ban

Findings genuinely depend on each other, and deleting the dependency loses
information. Replace the id with a description of what it referenced:

```markdown
<!-- WRONG: the reader cannot resolve the id -->
Same root cause as ARC-001.

<!-- WRONG: deletes the relationship instead of expressing it -->
Same root cause as another finding.

<!-- RIGHT: the relationship survives without the pointer -->
Same root cause as the duplicated tooltip mount in `src/components/info.tsx`.
```

## A rewritten reference still dangles under subset publication

The rewrite above assumes the published set equals the composed set. It rarely
does: `review --comment --all` composes every finding, and the user then
publishes the four that matter. A sentence that satisfies the rule at
composition time,

```markdown
The alternative approach is described in a separate comment about the event origin.
```

becomes, on a pull request that received one of those comments, a pointer to
something that was never posted. That is worse than a bare id. An id is visibly
a pointer and visibly unresolvable; "described in a separate comment" reads as
an instruction to keep looking, and the reader has nowhere to look.

The published set is not knowable at composition, so the check belongs at
assembly, where `publish` holds it:

- `review --comment` runs the detector, records each `sibling-comment` hit in
  its composition summary, and leaves the sentence alone. The reference is
  self-contained against the set that mode composed.
- `publish` resolves each hit against the set actually being published. A
  referenced comment inside the set clears the hit. A referenced comment
  outside it, or one whose identity cannot be determined, requires the content
  to be inlined or the sentence rewritten to stand on its own.

The detector cannot decide this: it sees one body, never the set. It reports
the phrase and its severity is `MODERATE` for that reason, the same reason
`tracker-id` carries it. `MODERATE` means the consuming command resolves the
hit from context it has and the detector does not; it never means the hit may
be ignored.

Apply the same rewrite to repository convention files and specification text:
state the constraint in plain words instead of naming the document.

```markdown
<!-- WRONG -->
This violates the naming convention in `AGENTS.md`.
<!-- RIGHT -->
The surrounding modules name hooks `use{Domain}{Action}`; this one inverts the order.

<!-- WRONG -->
Per issue #482, the empty state must render before the fetch resolves.
<!-- RIGHT -->
The empty state renders only after the fetch resolves, so the first paint is blank.
```

## Enforcement

A published comment leaves as an API payload, not as a file write, so the
file-write enforcement path never sees it. The enforcement point is therefore
the payload, and it runs twice:

1. `review --comment` runs the detector over each composed body before writing
   the file, and `artifact-writer` over the same body in the target language.
2. `publish` re-runs both over every **extracted** body and over the assembled
   review body before the approval gate, with `--payload`, because comment
   files can be edited by hand between composition and publication, because the
   publication set is only known there, and because the extraction itself needs
   checking.

The two points are not redundant, and they do not enforce the same thing. A
pointer pattern is decidable from the body, so composition is the right place
to catch it. A `sibling-comment` phrase is decidable only from the publication
set, so assembly is the only place it can be resolved at all.

Detector: `framework/scripts/pr-comment-refs-scan.py`, the mechanical
counterpart to this file. It reports `file:line:col`, the matched text, and a
static rewrite hint per pattern. It is detection only: the rewrite itself is
model judgment, exactly as `no-spec-refs-scan` splits its script from its skill.

## Detection patterns

```
\bSLICE-?\d+\b
\bAC-?\d+\b
\bADR-?\d+\b
\bSPEC-?\d+\b
\b(?:PRF|ENG|STK|TST|ARC|SEC|ADH)-\d{2,}\b        # finding ids
\b(?:CLAUDE|AGENTS|ai-context)\.md\b
docs/SPEC-\d+/
\b[0-9a-f]{7,40}\b                                 # commit shas
\bAB#\d+\b
(?:^|\s)#\d+\b                                     # issue references
\b(?:separate|another|other|different)\s+comments?\b   # sibling comments
\bsee\s+the\s+comments?\s+(?:about|on|regarding)\b
\b(?:outro|outra)s?\s+coment[áa]rios?\b
\bcoment[áa]rios?\s+(?:separado|apartado|à\s+parte)\w*\b
\bveja\s+o\s+coment[áa]rio\b
\b[A-Z]{2,}-\d+\b                                  # tracker ids
```

Severity is `SERIOUS` for every pattern except the sibling-comment phrases and
the bare tracker-id shape, both `MODERATE`: neither can be decided from one
body alone, so the consuming command resolves them from context the detector
lacks (the publication set, or whether the identifier is really a tracker id
rather than an error code).

**`--payload` adds two structural patterns.** A composed comment file has two
parts (`shared/templates/pr-comment.template.md` "The comment file has two
parts") and only the body is published, so a frontmatter delimiter on the first
non-blank line, in text about to be posted, means the file was copied where its
body belongs. The revalidation heading is the same signal for a comment file an
earlier version wrote, when the result was a section rather than a frontmatter
field:

```
^---\s*$                                           # frontmatter delimiter, first line only
^#{1,6}\s*Revalida(?:tion|ç[ãa]o|cao)\b            # revalidation heading
```

Both are `SERIOUS`, and both are off without the flag, because every file under
`pr-comments/` legitimately carries them. `publish` passes `--payload` because
it scans extracted bodies; `review --comment` does not, because it scans a body
before the other two parts exist. This is the one defect a payload preview
cannot show, since a preview of a file body looks like a file body.

**The commit-sha pattern exempts a repository permalink.** A sha inside
`https://{host}/{owner}/{repo}/blob/{sha}/{path}` is the head-commit permalink
this file lists among what stays, and the one `publish` emits for every anchor
outside the diff. Without the exemption, every outside-the-diff finding halts
its own publication: the rule permits the permalink, the template instructs the
author to emit it, and the detector blocks it, leaving a reader to either
override the detector (which trains them to ignore it) or drop the permalink
(which the template forbids). The exemption is that URL shape and nothing
wider. A bare sha in prose stays `SERIOUS`.

## Cross-references

- `shared/source-review-lenses.md`: the finding id scheme these patterns match,
  and the rule that ids never leave the report.
- `shared/templates/pr-comment.template.md`: carries this rule in its
  note-for-authors block, so the constraint reaches the author at composition
  time rather than only at the gate.
- `shared/no-spec-refs-in-implementation.md`: the sibling rule for a different
  surface, whose exemption list this file inverts.
- `commands/review.md` "Comment Mode" and `commands/publish.md`: the two
  enforcement points.
