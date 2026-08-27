# Unknown Information Policy

## Purpose

Keep unknown information out of every durable framework-generated document.
Absence is not content. Include only supported facts, declared
intent, accepted decisions, explicitly bounded assumptions, and verified
results.

This policy applies to requirements, briefs, PRDs, ADRs, RFCs, designs,
contracts, architecture views, UX artifacts, quality strategies, runbooks,
knowledge artifacts, reports, and document-like tables or diagrams.

## Global Parameter

Every document-producing command and skill accepts the cross-cutting
`--resolve-unknowns` flag, even when its local input table does not repeat the
flag. Propagate it unchanged to every downstream document
producer.

| Input | Default | Behavior |
|---|---|---|
| flag absent | omit | Exclude every unsupported field, row, item, and section. Do not investigate merely to make the document look complete. |
| `--resolve-unknowns` | active resolution | Build an ephemeral gap inventory, investigate material gaps, and write only values the resulting evidence supports. |

The flag authorizes elicitation and read-only investigation within the
caller's existing scope. It does not authorize network access, external
messages, measurements with side effects, or decisions that belong to an
accountable authority.

## Default Omission

When evidence does not support a value:

1. do not write `UNKNOWN`, `TBD`, `TODO`, `not evidenced`, `a definir`, an
   empty value, a question disguised as content, or an equivalent sentinel;
2. omit the containing field, table row, list item, or prose sentence;
3. omit a section when it has no remaining content;
4. do not create an Open Questions, Unknowns, Evidence Gaps, or Deferred
   Decisions section merely to preserve the missing information;
5. do not infer a value only to satisfy a template or validator; and
6. keep templates structural and conditional, never placeholder-driven in a
   completed artifact.

Allow a known risk, accepted assumption, scheduled action, or formally deferred
decision when evidence supports its actual content, owner, and status.
Do not use those categories as aliases for an unknown value.

## Resolution Mode

With `--resolve-unknowns`, keep the gap inventory transient and select the
least invasive method capable of producing authoritative evidence:

1. inspect supplied sources, accepted decisions, project knowledge, code,
   configuration, runtime artifacts, or primary references;
2. derive a value only when a reproducible rule or authoritative baseline
   supports the derivation;
3. run a focused interview, one material question at a time, with the person
   or role able to supply or approve the value;
4. request a bounded specialist analysis or the private `araia:DECISION`
   workflow when alternatives and trade-offs can narrow the answer without
   fabricating approval; or
5. propose a measurement, experiment, or prototype and execute it only when
   the caller already authorizes its effects.

Record provenance for every resolved value. Write the value only when the
evidence meets the document type's confidence and authority requirements.
Omit anything still unresolved.

## Blocking Gaps

If safety, legality, a public contract, an irreversible action, or a mandatory
document-type invariant requires an unsupported value:

- pause before publishing the document;
- explain the blocking gap in the interactive response or approval
  checkpoint, not in the candidate artifact;
- continue elicitation when `--resolve-unknowns` is active; and
- otherwise request the missing authoritative input.

Never publish a structurally complete-looking document by persisting an
unknown value.

## Review and Validation

Review mode removes persisted unknown sentinels and unresolved-question
sections by default. With `--resolve-unknowns`, attempt resolution before
removal, then retain only supported results.

Before publication, validate the complete artifact set with:

```text
node ./.claude/araia/scripts/check-document-unknowns.mjs <path-or-directory>
```

Any finding blocks publication. The validator ignores fenced code. Put
legitimate technical tokens whose spelling resembles an unknown sentinel,
such as a runtime enum member, inside a fenced example.
