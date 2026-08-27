# Step 3: Adaptive Task Loop

Use this flow when the resolved implementation strategy is `adaptive`, which is
the default. Strict test-first strategies route to
`dotnet-test-driven-development`, which uses `dotnet-testing` for mechanics;
this file does not duplicate its RED/GREEN contract.
Load `./.codex/araia/shared/implementation-validation-pyramid.md` before
selecting or executing a sensor.
Load `./.codex/araia/shared/risk-based-implementation-evaluation.md` before
deciding whether the completed Task requires an isolated challenge.
Load `./.codex/araia/shared/test-tautology-rules.md` and
`./.codex/araia/shared/refactoring-triggers.md` before promoting an oracle
or scheduling cleanup.

## Sensor Profile

Use repository discovery and the Stack Profile to replace placeholders with
real paths:

| Level | Preferred sensors |
|---|---|
| `L1-fast` | Filtered unit/contract test, deterministic reproduction, analyzer on changed files, migration dry run, or focused architecture test |
| `L2-task` | Changed test project or module tests, affected-project build, and declared contract checks for direct boundaries |
| `L3-slice` | `dotnet build --warnaserror` for affected solution/projects plus all test projects in the Delivery Slice's adapter slice |
| `L4-spec` | Build and test commands for every source-contributing adapter, then declared cross-adapter contract, schema, infrastructure, or smoke checks |

Do not use the full solution suite as the default `L1-fast` sensor. A filtered
test is insufficient at `L2-task` when the Task changes a public contract,
persistence boundary, generated code, or shared module.

## Preconditions

Before changing source:

1. load the Delivery Slice evidence contract and map each row to one declared Task;
2. resolve a concrete strategy for every row still marked `adaptive`;
3. verify the strategy's required starting evidence;
4. run the legacy safety-net pre-phase when an existing target has no coverage;
5. create the durable progress snapshot with Task state `planned`.

If mapping a criterion changes its meaning, mark it
`blocked` and stop. Do not invent a Task or weaken its oracle during IMPLEMENT.

## Continuous Builder Context

Dispatch one `dotnet-engineer` builder for the current Task and preserve that
builder context through implementation, verification, and correction attempts.
The initial Task capsule contains:

- stable agent-role, stack-profile, documentation, Delivery Slice, and ADR context;
- the Task's owned evidence rows and their selected strategies;
- the Task's owned quality obligations, gates, sensors, and approved
  applicability dispositions;
- declared files, lock boundaries, constraints, and forbidden scope;
- required starting evidence, fast checks, boundary checks, and output schema;
- collaboration mode, specialty packs, approval policy, and remaining budget.

Place stable blocks before `<!-- cache-breakpoint -->`. After the marker, pass
the current Task state and the smallest actionable instruction.

When the harness cannot resume the same agent context, redispatch with the
unchanged Task capsule plus a compact attempt delta. The delta contains only
changed files; commands and exit codes; oracle observations; new diagnostics;
and the next hypothesis. Never replay an unbounded transcript.

## State Machine

Each Task follows:

```text
planned -> implementing -> verifying -> correcting -> candidate
                      \---------------------------> blocked
```

- `planned`: the Task capsule and evidence ownership are valid.
- `implementing`: the builder is producing the smallest useful increment.
- `verifying`: a declared fast check is observing the relevant oracle.
- `correcting`: the last observation failed and a new evidence-backed action is
  available.
- `candidate`: all owned rows are `boundary-verified`.
- `blocked`: no valid oracle, strategy, scope, or new corrective action exists.

Persist only these meaningful transitions, the evidence counters, and the
`loop_health` counters from `./.codex/araia/shared/progress-tracking.md`.
Tool chatter and intermediate reasoning are not progress states.

## Attempt Loop

For each Task:

1. Move its owned evidence rows from `pending` to `producing`.
2. Dispatch or resume the continuous builder with the Task capsule.
3. Allow the builder to edit only declared or explicitly expanded paths.
4. Run the cheapest declared `L1-fast` sensor that can observe the current
   oracle.
5. Record the command, exit code, concise observation, and artifact reference.
6. Apply the falsification check from `shared/test-tautology-rules.md` to every
   oracle the attempt introduced. An oracle that cannot fail does not promote a
   row.
7. If the oracle passes, mark the row `observed`; continue until every owned row
   is observed.
8. Run every required `L2-task` sensor. Mark passing rows `boundary-verified`
   and move the Task to `candidate`.
9. Run each quality-obligation sensor owned by the Task/AC and record it as
   `verified`; leave `SLICE` and `L4-spec` rows to their declared boundaries.
10. If a check fails, move to `correcting` only when the result adds a new
    diagnostic or supports a different action. Feed that delta to the same
    builder context and repeat.

The builder can choose `test-first`, `reproduce-first`, `contract-first`,
`approved-scenarios`, or another registered strategy within one Task. It must satisfy each selected
strategy's starting evidence; `adaptive` is not permission to code before
deciding how to prove the outcome.

## Circuit Breaker and Escalation

Fingerprint a failure from the check identifier, exit code, and normalized
diagnostic. Two consecutive attempts with the same fingerprint and no new
evidence open the circuit:

1. stop blind retries;
2. preserve the current workspace and evidence;
3. change strategy when a deterministic alternative exists;
4. otherwise promote collaboration mode or return `decision-required` to the global decision
   path;
5. mark the Task `blocked` if no bounded corrective action remains.

Escalate earlier for scope expansion, an invalid oracle, contradictory Delivery Slice or
ADR semantics, security or data-safety risk, or an exhausted budget. A breaker
that opens twice on the same module is a structural signal, per
`shared/refactoring-triggers.md`, not another coding attempt.
Specialists participate because evidence activates them, not because the loop
reached a ceremonial phase.

## Completion and Handoff

The continuous builder never commits and never marks the Delivery Slice complete. Return:

- final Task state and attempt count;
- selected strategy per evidence row;
- evidence state and durable reference per row;
- quality-obligation state, disposition evidence, sensor result, and durable
  reference per row;
- changed paths and any lock drift;
- `L1-fast` and `L2-task` sensor results and input fingerprints;
- `loop_health` counters for the Task;
- open risks, evaluator triggers and status, and resumability data.

Only a Task with every owned row `boundary-verified` can enter `candidate`.
Record whether risk evaluation is `not-required` or `pending`; the builder
cannot mark its own evaluation `confirmed`.
After all Tasks are candidates, return their evidence to
`dotnet-implementation` for declared build/test validation and handoff. The
parent `araia:IMPLEMENT` workflow owns slice-boundary review, approval,
packaging, integration, and commit decisions.
