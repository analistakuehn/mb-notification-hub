# Layered guardrails as the gate on AI-authored changes

Cross-cutting policy. When an AI agent authors or edits code, treat the change as **a pull request from a collaborator you do not yet trust**: it passes the same deterministic gate as any other change, and the agent itself is a contained, least-privilege actor. This is the AI-as-builder counterpart of the AI-as-runtime decision (the .NET reference encodes the latter in the capability ADR *Deterministic envelope for AI-as-runtime modules*).

Adapters reference this file from their security capability, source-review
sensor, or architect role. The .NET adapter does so from
the .NET source-review sensor with `dotnet-architect` threat-boundary evidence. It
complements and never duplicates an adapter's existing arch tests, security
arch tests, and CI gate.

## Part A: The Four-Layer Gate

Order the gate so the deterministic, authoritative layers run before the probabilistic, advisory one.

1. **In-code architecture tests** (e.g. NetArchTest / ArchUnit). Dependency matrix, module boundaries, error-axis consistency. Blocking.
2. **Configuration policy-as-code** (e.g. OPA / Conftest / Rego). Infrastructure and configuration rules. Blocking.
3. **SAST** (e.g. CodeQL taint tracking, plus the package-vulnerability scan). Blocking. This is the layer that catches the injected vulnerability.
4. **LLM review / LLM-as-judge.** Breadth and triage only. It is
   **non-blocking** and never gates merge on its own.

Why the deterministic layers gate and the LLM layer only advises:

- About **45%** of AI-generated code introduces a known vulnerability, with no improvement from larger or newer models (Veracode 2025). The deterministic SAST layer is not optional.
- LLM reviewers detect only **15-31%** of the issues human reviewers flag, and they **degrade** with more context (SWE-PRBench 2026). They also over-reject correct code (**25-58%** under a direct prompt, rising to **60-88%** under explain/repair prompting, 2026). An LLM reviewer is a noisy supplement, not the gate.
- Before trusting an LLM reviewer even as a supplement, **measure its own precision, recall, and hallucination rate** against a labeled sample. Treat it like any other detector.

## Part B: The Agent as Attack Surface

An agent that reads the repository, runs commands, and opens PRs is itself a risk surface. Posture: **defense in depth, default-deny, fail-closed**.

- **Prompt injection is OWASP LLM01:2025.** It arrives through vectors that look harmless: repository-convention files (`AGENTS.md`, `CLAUDE.md`, `.cursorrules`, `copilot-instructions.md`), issue and PR text, and MCP tool poisoning or rug-pull. Treat every piece of untrusted content that enters the agent's context as hostile input. Sensitive-information disclosure is the correlated risk (LLM02:2025).
- **Treat convention files as trusted context.** An edit to `AGENTS.md` / `ai-context.md` / `CLAUDE.md` carries the same review weight as a security-policy change; gate it with CODEOWNERS.
- **Real incidents, not hypotheticals.** A Replit agent deleted a production database during a code freeze (2025); Pillar Security disclosed a sandbox escape to remote code execution in a Google Antigravity agent (2026).
- **Containment is mandatory:** sandboxed execution with a network-egress allow-list; capability and least-privilege scoping per task; secret scrubbing so the agent never sees raw PII or production credentials; provenance tracking of AI-authored changes; tiered human-in-the-loop gates (read-only, logged writes, confirmed execution, blocked credentials).

## How to apply

- Wire the four-layer gate into CI with the deterministic layers blocking and the LLM review reporting only.
- Tag AI-authored changes with provenance and require tiered human review for any code touching money-moving, fiscal, or KYC paths.
- Keep an explicit, written policy for when an agent may generate production code; existing SDLC/CI-CD tooling targets human-authored code and needs deliberate extension (blocking SAST, pre-commit secret detection, dependency allow-lists).
- For the git-operation subset of tiered human-in-the-loop gating (a force-push, a hook bypass, blind wildcard staging), `./.claude/araia/shared/command-policy.md` and `command-policy.json` mechanize it per harness instead of leaving it to the agent's own judgment. This covers one narrow slice of "capability and least-privilege scoping per task"; sandboxed execution, the network-egress allow-list, and secret scrubbing remain separate, not-yet-mechanized concerns.

## References

- `Arquitetura de Software na Era da IA: Redução de Custos sem Sacrificar Legibilidade`, version 1.2, Sections 6.9 (agent attack surface), 10.5 (layered gate), 11.6 (evals).
- OWASP Top 10 for LLM Applications 2025. <https://genai.owasp.org/llm-top-10/>
- NIST AI Risk Management Framework: Generative AI Profile (NIST-AI-600-1).
