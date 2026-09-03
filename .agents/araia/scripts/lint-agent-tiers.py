#!/usr/bin/env python3
"""
Deterministic linter for the framework Agent and Worker Tier Taxonomy.

Mechanical check of the rules declared in `framework/CLAUDE.md` "Agent and
Worker Tier Taxonomy": every profile declares `model: inherit` and an explicit `tools:` list
drawn from the known vocabulary, and Tier-3 (mechanical formatter / checker /
validator) workers stay within the restricted toolset (`Read, Edit, Glob, Grep`;
no `Write`, no `Bash`). Exception: Tier-3 workers may pin `model: sonnet`;
their work is deterministic rule application, and dispatching it on the
session model multiplies cost with no quality gain.

Pinning requires an EXPLICIT `tier: 3` field. Name inference (below) restricts
tools but never unlocks the cheaper model. The asymmetry is deliberate:
restricting a grant by inference is safe, while relaxing a model floor by
inference is not. A profile that grades severity, returns accept/reject/defer,
or rules on the substance of a claim exercises judgment, and a downgraded judge
returns confident false positives that cost more to triage than the dispatch
saved. Making the pin a visible declaration keeps that choice reviewable instead
of accidental.

Why a script and not prose: tool-vs-tier membership is mechanical set math. This
closes the gap the framework audit flagged: the taxonomy was enforced only by
a manual checklist and an on-demand semantic check, never deterministically.

The linter also enforces the `## Execution Boundary` section required by the
Agent and Worker Execution Boundary Standard. The scheduler workers' stronger
`## Authority Boundary` heading is accepted as equivalent.

Tier resolution (in priority order):
  1. An explicit `tier:` frontmatter field (`1`, `2`, or `3`): exact. Only this
     form permits a Tier-3 model pin.
  2. A curated set of unambiguous mechanical agent names (`*-docs-reviewer`,
     `*-manifest-linter`, `*-accessibility-reviewer`, plus `auto-clarity-checker`
     and `artifact-writer`) -> Tier 3 for the tool restriction only.
  3. Otherwise unclassified: only the universal checks apply (model + tool
     vocabulary). Ambiguous roles (notably `*-security-reviewer`, whose tier
     the taxonomy does not pin down) are intentionally NOT auto-classified, to
     avoid false positives. Add `tier:` to such an agent to lint it precisely.

Pattern reference: see `framework/scripts/README.md`; mirrors the CLI shape of
`validate-manifest.py` and `no-spec-refs-scan.py`.
"""

from __future__ import annotations

import argparse
import re
import sys
from collections import namedtuple
from pathlib import Path

# The core tool vocabulary the taxonomy lists for Tier 1/2 agents.
ALLOWED_TOOLS = {"Read", "Write", "Edit", "Glob", "Grep", "Bash"}
# Orchestration tools: absent from the taxonomy's core file/tool list, but
# legitimately declared by Tier-1 orchestrators that dispatch sub-agents or
# invoke a specialist skill. Recognized so they are not "unknown", but still
# forbidden for Tier-3 (mechanical) agents.
ORCHESTRATION_TOOLS = {"Agent", "Task", "Skill"}
KNOWN_TOOLS = ALLOWED_TOOLS | ORCHESTRATION_TOOLS
# Tier 3 (mechanical) agents are restricted to read + in-place edit + search.
TIER3_ALLOWED = {"Read", "Edit", "Glob", "Grep"}

# Curated, unambiguous mechanical (Tier-3) actor names. Deliberately narrow:
# `-security-reviewer` is a specialist persona with VETO power, not a
# mechanical reviewer.
TIER3_NAME_SUFFIXES = ("-docs-reviewer", "-manifest-linter", "-accessibility-reviewer")
TIER3_NAME_EXACT = {"auto-clarity-checker", "artifact-writer"}
BOUNDARY_HEADING = re.compile(r"^## (?:Execution|Authority) Boundary\s*$", re.MULTILINE)

Finding = namedtuple("Finding", "file code message")


def extract_frontmatter(text: str) -> str | None:
    """Extract the YAML frontmatter block delimited by `---` lines."""
    if not text.startswith("---"):
        return None
    end = text.find("\n---", 3)
    if end < 0:
        return None
    return text[3:end].strip()


def parse_scalars(frontmatter: str) -> dict[str, str]:
    """Parse top-level `key: value` scalar lines (enough for name/model/tools/tier)."""
    result: dict[str, str] = {}
    for line in frontmatter.splitlines():
        if not line or line[0] in (" ", "\t", "#", "-"):
            continue
        if ":" in line:
            key, _, value = line.partition(":")
            result[key.strip()] = value.strip()
    return result


def parse_tools(value: str | None) -> list[str] | None:
    """Split the `tools:` scalar (`Read, Edit` or `[Read, Edit]`) into a list."""
    if value is None:
        return None
    text = value.strip()
    if text.startswith("[") and text.endswith("]"):
        text = text[1:-1]
    text = text.strip().strip('"').strip("'")
    if not text:
        return []
    return [t.strip() for t in text.split(",") if t.strip()]


def parse_declared_tier(tier_field: str | None) -> int | None:
    """Parse an explicit `tier:` field. Only this form authorizes a model pin."""
    if not tier_field:
        return None
    match = re.search(r"[123]", tier_field)
    return int(match.group()) if match else None


def resolve_tier(tier_field: str | None, name: str) -> int | None:
    """Resolve a profile tier from an explicit field, else a curated name match.

    Used for the tool restriction, where inference is safe because it only ever
    narrows a grant. The model floor uses `parse_declared_tier` instead.
    """
    declared = parse_declared_tier(tier_field)
    if declared is not None:
        return declared
    if name in TIER3_NAME_EXACT or name.endswith(TIER3_NAME_SUFFIXES):
        return 3
    return None


def lint_agent(path: Path) -> list[Finding]:
    """Lint one canonical agent or worker definition against the taxonomy."""
    rel = str(path).replace("\\", "/")
    findings: list[Finding] = []

    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        return [Finding(rel, "unreadable", "Could not read file as UTF-8")]

    frontmatter = extract_frontmatter(text)
    if frontmatter is None:
        return [Finding(rel, "no-frontmatter", "Missing YAML frontmatter (no leading `---` block)")]

    data = parse_scalars(frontmatter)
    name = data.get("name") or path.stem

    tier_field = data.get("tier")
    if tier_field and not re.search(r"[123]", tier_field):
        findings.append(Finding(rel, "bad-tier", f"`tier:` must be 1, 2, or 3, got '{tier_field}'"))
    tier = resolve_tier(tier_field, name)

    # A pin is authorized only by an EXPLICIT `tier: 3`, never by name inference:
    # narrowing a tool grant by inference is safe, relaxing a model floor is not.
    may_pin = parse_declared_tier(tier_field) == 3
    model = data.get("model")
    allowed_models = {"inherit", "sonnet"} if may_pin else {"inherit"}
    if model is None:
        findings.append(Finding(rel, "missing-model", "No `model:` declared; framework agents and workers must declare `model: inherit` (a profile with an explicit `tier: 3` may pin `sonnet`)"))
    else:
        declared_model = model.strip().strip('"').strip("'")
        if declared_model not in allowed_models:
            if declared_model == "sonnet" and tier == 3:
                findings.append(
                    Finding(
                        rel,
                        "undeclared-pin",
                        f"'{name}' pins `model: sonnet` on an inferred Tier 3; add an explicit `tier: 3` "
                        "field to declare the profile mechanical, or use `model: inherit`. A profile that "
                        "grades severity, returns accept/reject/defer, or rules on the substance of a claim "
                        "exercises judgment and inherits",
                    )
                )
            else:
                findings.append(Finding(rel, "bad-model", f"`model:` must be one of {sorted(allowed_models)} for this tier, got '{model}'"))

    if "tools" not in data:
        findings.append(Finding(rel, "missing-tools", "No `tools:` declared; every agent and worker must scope tools explicitly"))
        return findings

    tools = parse_tools(data.get("tools"))
    if not tools:
        findings.append(Finding(rel, "empty-tools", "`tools:` is present but empty"))
        return findings

    for tool in tools:
        if tool not in KNOWN_TOOLS:
            findings.append(
                Finding(rel, "unknown-tool", f"Tool '{tool}' is not a known tool {sorted(KNOWN_TOOLS)}")
            )

    if tier == 3:
        for tool in tools:
            if tool in KNOWN_TOOLS and tool not in TIER3_ALLOWED:
                findings.append(
                    Finding(
                        rel,
                        "tier3-tool",
                        f"Tier-3 profile '{name}' may not declare '{tool}' (allowed: {sorted(TIER3_ALLOWED)})",
                    )
                )

    return findings


def lint_boundary(path: Path) -> Finding | None:
    """Return a blocking finding when a profile lacks a boundary section."""
    rel = str(path).replace("\\", "/")
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        # `lint_agent` owns the blocking unreadable-file finding.
        return None

    if BOUNDARY_HEADING.search(text):
        return None
    return Finding(
        rel,
        "missing-boundary",
        "No `## Execution Boundary` section; declare the context, scheduling, "
        "write, permission, model, or independent-judgment boundary that "
        "justifies a separate profile (`## Authority Boundary` is accepted for "
        "scheduler workers)",
    )


# Roots that hold generated state rather than authored source. `.araia/` is the
# control plane: run state, staging, worktrees, and ledgers. The portfolio
# scheduler writes per-SPEC briefs to `.araia/runs/**/workers/{SPEC-ID}/`, whose
# `workers` segment would otherwise read as a profile directory and demand
# frontmatter from a mission brief.
_NON_SOURCE_ROOTS = {".araia", "node_modules", ".git"}

_PROFILE_DIRS = {"agents", "workers"}


def _is_profile_file(path: Path) -> bool:
    """Report whether `path` is a canonical agent or worker profile.

    A profile is Markdown directly inside an `agents/` or `workers/` directory.
    Requiring the *parent* to be that directory, rather than any ancestor,
    keeps unrelated trees that merely pass through such a segment out of scope.
    """
    if path.suffix != ".md":
        return False
    parts = {part.lower() for part in path.parts}
    if parts & _NON_SOURCE_ROOTS:
        return False
    return path.parent.name.lower() in _PROFILE_DIRS


def collect_targets(paths: list[str]) -> list[Path]:
    """Expand the given paths into canonical agent/worker `.md` files."""
    targets: list[Path] = []
    for raw in paths:
        path = Path(raw)
        if path.is_dir():
            targets.extend(sorted(p for p in path.rglob("*.md") if _is_profile_file(p)))
        elif path.is_file() and _is_profile_file(path):
            targets.append(path)
    return targets


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Lint framework agent and worker files against the tier taxonomy."
    )
    parser.add_argument(
        "paths",
        nargs="*",
        default=["."],
        help="Agent/worker files or directories to lint (default: current tree).",
    )
    parser.add_argument("--github", action="store_true", help="Emit GitHub Actions ::error annotations.")
    parser.add_argument("--quiet", action="store_true", help="Print nothing on success.")
    args = parser.parse_args()

    targets = collect_targets(args.paths or ["."])
    findings: list[Finding] = []
    boundary_findings: list[Finding] = []
    for target in targets:
        findings.extend(lint_agent(target))
        boundary_finding = lint_boundary(target)
        if boundary_finding is not None:
            boundary_findings.append(boundary_finding)

    blocking_findings = findings + boundary_findings

    if args.github:
        for finding in blocking_findings:
            print(f"::error file={finding.file}::[{finding.code}] {finding.message}")
    elif blocking_findings:
        print(f"FAIL: {len(blocking_findings)} profile-authoring violation(s) across {len(targets)} profile file(s)")
        for finding in blocking_findings:
            print(f"  {finding.file} [{finding.code}] {finding.message}")
    else:
        if not args.quiet:
            print(f"PASS: {len(targets)} profile file(s) conform to the tier taxonomy")

    return 1 if blocking_findings else 0


if __name__ == "__main__":
    sys.exit(main())
