# Runtime Diagnostic Output Templates

Loaded on demand by `dotnet-runtime-diagnostics` for benchmark and runtime-evidence reports.

## Benchmark Report

```markdown
## Benchmark Report: [Component/Operation]

### Objective -- what question does this benchmark answer?
### Environment
| Parameter | Value |
|---|---|
| Runtime | .NET 9.0 |
| OS / CPU | ... |
| GC Mode | Server / Workstation |
| Configuration | Release, no debugger attached |

### Results
| Method | Mean | Error | StdDev | P95 | Allocated |
|---|---|---|---|---|---|
| Baseline | X ns | ± Y ns | Z ns | W ns | N B |
| Optimized | X ns | ± Y ns | Z ns | W ns | N B |

### Analysis -- statistical context / Limitations -- what it does NOT measure
### Recommendation -- evidence-based with confidence level
```

## Performance Review Report

```markdown
## Performance Review: [Feature/Component]

### Summary -- NO CONCERNS / MINOR / SIGNIFICANT / CRITICAL
### Findings
| # | Severity | Location | Issue | Impact | Recommendation |
|---|---|---|---|---|---|
| 1 | HIGH | File.cs:45 | N+1 in loop | O(n) DB calls | Include() or projection |
| 2 | MED | Service.cs:23 | Concat in hot path | Excessive allocations | StringBuilder / string.Create |
| 3 | LOW | Handler.cs:67 | Boxing in generic | Minor GC | Generic constraint |

### Hot Path Analysis / Allocation Profile / Benchmarks to Run
```
