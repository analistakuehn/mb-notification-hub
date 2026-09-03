# Test Tautology Rules

Rejects an oracle that cannot fail. A test that passes against every possible
implementation is not evidence, whatever ritual produced it.

## Why This Exists

Writing the test before the code does not prevent a tautology: an agent holds
the requirement and the implementation shape at the same time, so it can author
an assertion derived from the code it is about to write. Measured TDD sessions
still produced tests that checked implementation output against itself.
Therefore the framework validates the content of the oracle, not the order in
which it appeared.

## The Falsification Check

One question decides every case:

> Name a concrete change to production code that makes this test fail.

If no such change exists, or the only answer is "delete the function", the test
is not an oracle for the criterion it claims to prove. Record the answer in the
evidence reference for high- and critical-risk rows.

## Rejected Shapes

| Shape | Symptom | Repair |
|---|---|---|
| Self-derived expectation | The expected value is produced by calling the same function, class, or pipeline under test. | Derive the expected value from the accepted requirement and write it as a literal or a fixture. |
| Fresh snapshot as oracle | A snapshot, golden, or approved file is generated and accepted inside the same change that produced the behavior, with no independent confirmation. | Confirm the content against the accepted requirement before approving it, and treat later diffs as re-approval, per the `approved-scenarios` strategy. |
| Mock echo | The assertion reads back a value the test itself programmed into a stub, with no production logic in between. | Assert the outcome the production code computes from the stubbed input. |
| Interaction stands in for outcome | The test asserts that a collaborator was called, while the criterion is about an observable result. | Assert the result. Keep the interaction assertion only when the interaction is the contract. |
| Vacuous assertion | `not null`, `is defined`, `is instance of`, or a truthiness check is the only assertion for a behavioral criterion. | Assert the specific value, state transition, or error type the criterion names. |
| Structural assertion | The test asserts a private field, a call order, or a method name instead of behavior. | Assert through the public surface the criterion describes. |
| Swallowed failure | A `try`/`catch`, a broad exception filter, or an unawaited async call lets a failing path pass. | Let the failure propagate, or assert the expected error explicitly. |
| Conditional assertion | The assertion sits behind a branch that the test data may never enter. | Make the branch unconditional, or split it into two tests with data that reaches each path. |
| Self-fulfilling fixture | The fixture is built by the production factory, mapper, or serializer that the test claims to verify. | Build the fixture independently of the unit under test. |

## Where This Applies

1. `shared/implementation-evidence-contract.md`, Evidence Quality Rules: a
   tautological oracle never promotes a row to `observed`.
2. `G5s` in `pipeline/quality-gates.md`: the checkpoint fails when an
   acceptance criterion's oracle matches a rejected shape.
3. Each adapter's code review capability: the reviewer applies this file to the
   tests inside the reviewed diff.
4. `shared/test-effectiveness-sensor.md`: a suite that clears this rule and
   still leaves mutants alive has a coverage gap, not a tautology. Report both
   separately; they have different repairs.

## Scope Limits

This file governs oracles for acceptance criteria and quality obligations. It
does not govern smoke checks, health probes, or compile-time guards, whose
purpose is exactly to prove that something exists. Declare those as such in the
evidence row instead of dressing them as behavioral tests.
