# Output Validation Protocol (Shared)

Defines how skills validate agent outputs **before writing to staging**. Catches formatting issues, missing evidence, and protocol violations early.

This protocol validates generated agent artifacts. For executable validation of
implementation outcomes during IMPLEMENT, use
`shared/implementation-validation-pyramid.md`. Artifact checks and
implementation sensors are complementary and their result states are not
interchangeable.

## Validation Pipeline

After receiving agent output, before writing to staging:

```
Agent Output -> Structural Validation -> Content Validation -> Write to Staging
                      |                        |
                      v                        v
                 (fail? retry)           (warn? annotate)
```

## Structural Validation (Hard Failures)

Block the write to staging. On fail, trigger retry protocol Level 1.

### Check S1: Report Boundaries

Verify output contains start and end markers:
```
<!-- REPORT-START: DIMENSION={name}, AGENT={subagent_type} -->
... report content ...
<!-- REPORT-END: DIMENSION_SCORE={score} -->
```

**If missing** (report may be truncated/malformed), retry with:
> "Your report is missing the REPORT-START/REPORT-END markers. Wrap your entire report in these markers."

### Check S2: Minimum Content Length

- Analysis reports (EQI, Code Analyzer, Tech Refinement): min 500 chars
- Artifact documents (global SPECIFY workflow): min 300 chars
- TDD phase outputs (Mob Orchestrator): min 100 chars

**If too short**, retry:
> "Your output is too short ([N] chars). Expected at least [M] for this dimension."

### Check S3: Template Section Headers

Verify required template section headers are present.

**If sections missing**, retry:
> "Your report is missing these required sections: [list]. Include all sections from the template."

## Content Validation

C1 (code-evidence) is a **hard structural check**: see below. Checks C2-C5 are **soft warnings**: do not block the write, annotate the staging file, and surface during consolidation.

### Check C1: Code Evidence Presence

Analysis skills (EQI, Code Analyzer, Tech Refinement) must include file references for **every substantive finding**. The threshold is **100%**: there is no acceptable percentage of unsourced findings.

A finding is sourced when it carries one of:
- A file reference: `file.cs:NN`, `file.cs#LNN`, or `path/to/file.cs` next to the claim.
- An inline code block excerpting the cited code.
- An explicit `[NO-EVIDENCE: <reason>]` annotation that justifies the exception (legitimate exceptions are aggregate metrics, codebase-wide trends, or absence-of-feature observations that do not lend themselves to file:line citation).

**Enforcement (hard, not soft)**:
- Scan for `file.cs:NN`, `file.cs#LNN`, code block markers, or `[NO-EVIDENCE: ...]` annotations.
- Compute `unsourced = findings_total - findings_with_evidence - findings_with_no_evidence_annotation`.
- **If `unsourced > 0`**, treat as a **structural failure** and trigger Retry Protocol Level 1 with:
  > "Your report contains [N] findings without code evidence and without `[NO-EVIDENCE: <reason>]` annotation. Add a file reference, an excerpted code block, or an explicit `[NO-EVIDENCE: <reason>]` annotation to each. Unsourced findings are not acceptable."
- A `[NO-EVIDENCE: <reason>]` annotation with a reason shorter than 20 characters or matching `[NO-EVIDENCE: n/a]` / `[NO-EVIDENCE: -]` / `[NO-EVIDENCE: see above]` does NOT count and is treated as unsourced.
- If after Level 1/2 retries unsourced findings remain, surface them in the consolidated report:
  ```markdown
  <!-- VALIDATION-WARNING: [N] findings remain unsourced after retry. Listed below; treat as low-confidence. -->
  ```
  and list the offending findings verbatim.

### Check C2: Criterion/Finding Count

Skills with expected counts (EQI: 8-12 criteria; Tech Refinement: 8-12 findings):
- **Below minimum**:
  ```markdown
  <!-- VALIDATION-WARNING: [N] criteria found, expected [MIN]-[MAX]. -->
  ```
- **Above maximum** (consolidation truncates):
  ```markdown
  <!-- VALIDATION-WARNING: [N] criteria found, exceeds maximum [MAX]. Only first [MAX] will be scored. -->
  ```

### Check C3: Score Range (EQI Only)

Scores must be 0-10:
```markdown
<!-- VALIDATION-WARNING: Score [VALUE] outside valid range 0-10 for criterion [ID]. -->
```

### Check C4: Blocker Annotation (EQI Only)

Criteria with score <= 3 must be marked BLOCKER:
```markdown
<!-- VALIDATION-WARNING: Criterion [ID] scored [SCORE] but is not marked as BLOCKER. -->
```

### Check C5: Mandatory Coverage Areas

For skills defining mandatory areas per agent, scan for coverage:
```markdown
<!-- VALIDATION-WARNING: Missing mandatory coverage area: [AREA]. -->
```

## Sentinel Format for Agent Prompts

Instruct agents to include structured sentinels:

```markdown
## Output Sentinels (Required)

Wrap your ENTIRE report in these markers:

\`\`\`
<!-- REPORT-START: DIMENSION={your_dimension}, AGENT={your_subagent_type} -->

[Your full report content here]

<!-- BLOCKER: {criterion_id}, SCORE={score}, IMPACT="{one-line impact}" -->
(repeat for each blocker, if any)

<!-- REPORT-END: DIMENSION_SCORE={calculated_score} -->
\`\`\`

These markers enable automated validation. Do NOT omit them.
```

## Validation Result Actions

| Result | Action |
|--------|--------|
| All structural pass, no warnings | Write to staging as-is |
| All structural pass, warnings present | Write to staging with warnings prepended |
| Structural fail | Retry Level 1 with specific failure message |
| Structural fail after retry | Retry Level 2 (scope reduction) |
| Structural fail after Level 2 | Skip agent (Level 3) |

## Recovery Validation

After an `F-BUDGET` termination, validation may run against files already persisted in staging. Apply the same structural and content checks above plus the expected-roster, referential-integrity, placeholder, hash, and next-step checks from `./.claude/araia/shared/durable-candidate-recovery.md`. A candidate that passes recovery validation is not re-generated; it continues at its recorded review, consolidation, approval, or gate-preparation step.

## Consolidation Warning Surfacing

Consolidation step:
1. Read each staging file
2. Extract `<!-- VALIDATION-WARNING: ... -->` annotations
3. Include "Validation Notes" in consolidated report:
   ```markdown
   ## Validation Notes
   | Agent | Warning | Impact |
   |-------|---------|--------|
   | dotnet-architect | Only 60% evidence coverage | Some findings may lack traceability |
   | dotnet-specialist | 7 criteria (below minimum 8) | Dimension score may be less representative |
   ```

## Structured Findings Sidecar (machine-readable)

**Optional and additive.** The prose report remains the human artifact. An agent MAY also write a machine-readable sidecar next to its staging markdown so consolidation skills can aggregate numbers without re-parsing prose.

The sidecar lives at `.staging/{NN}-{agent}.findings.json`: the same `{NN}-{agent}` stem as the markdown report, with a `.findings.json` suffix. Schema:

```json
{
  "dimension": "security",
  "agent": "dotnet-architect",
  "dimension_score": 6.5,
  "criteria": [
    { "id": "no-secrets-in-config", "score": 3, "blocker": true, "evidence_ref": "src/Api/appsettings.json:12" }
  ],
  "findings": [
    { "id": "secret-in-source", "severity": "CRITICAL", "file": "src/Api/appsettings.json", "line": 12 }
  ]
}
```

When a sidecar is present, consolidation skills **MAY** parse it for numeric aggregation, dimension scores, criterion counts, and blocker lists, instead of re-parsing the prose for those numbers. This converts a silent count-mismatch (where consolidation miscounts findings or scores from prose and no one notices) into a catchable JSON-parse failure: a malformed or absent field fails loudly at parse time rather than corrupting the aggregate silently.

The sidecar is **not required**. When it is absent, consolidation falls back to prose parsing exactly as before; when it is present, the prose and the sidecar must agree, and a mismatch is surfaced as a validation warning rather than silently reconciled.
