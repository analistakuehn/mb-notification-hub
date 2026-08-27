# Output Templates (dotnet-architect)

Loaded on demand when `dotnet-architect` produces an artifact. Not preloaded into the agent personality.

## ADR

```markdown
# ADR-XXX: [Title]
## Status: PROPOSED / APPROVED / REJECTED / SUPERSEDED
## Context
[Problem, constraints, .NET version, existing stack]
## Decision
[What we decided -- specific NuGet packages, versions, patterns]
## Consequences
Positive / Negative / Mitigations
## Alternatives Considered
Alt 1 -- rejected: [Reason]
Alt 2 -- rejected: [Reason]
## References
[Spike reports, MS docs, benchmarks]
```

## Technology Selection Matrix

```markdown
# Technology Selection: [Context]

| Criterion (weight)     | Opt 1 | Opt 2 | Opt 3 |
|------------------------|:----:|:----:|:----:|
| Team Expertise (30%)   | X/10 | X/10 | X/10 |
| Performance (20%)      | X/10 | X/10 | X/10 |
| Strategic Fit (20%)    | X/10 | X/10 | X/10 |
| Community/NuGet (15%)  | X/10 | X/10 | X/10 |
| Cost (15%)             | X/10 | X/10 | X/10 |
| **Weighted Total**     | X.X  | X.X  | X.X  |

Risk level: weighted total <7.0 = HIGH RISK.

## Recommendation / Conditions for revisiting
```

## Architecture Review

```markdown
# Architecture Review: [System]

## Summary -- HEALTHY / CONCERNS / AT RISK, one paragraph
## Style Fitness -- current style, needed characteristics, star rating alignment, hybrid boundaries
## Strengths
## Concerns -- | # | Concern | Severity | Recommendation |
## NFR Assessment -- | NFR | Target | Current | Status |
## Action Items -- owner + timeline
```

## Migration Plan

```markdown
# Migration Plan: [From] → [To]

## Current State / Target State
## Strategy -- Big Bang / Strangler Fig / Branch by Abstraction / Parallel Run
## Phases
### Phase N: [Name] (timeline)
Steps / Rollback / Validation
## Risks -- | Risk | Probability | Impact | Mitigation |
## Success Criteria
```
