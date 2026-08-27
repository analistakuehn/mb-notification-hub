#!/usr/bin/env python3
"""Seal, verify, and sweep Araia Delivery Slice quality checkpoints.

Implements the deterministic half of ``framework/shared/slice-quality-checkpoint.md``:
the surface fingerprint that a passing checkpoint seals, the digest-only sweep
that detects when a later Delivery Slice regresses an earlier one, and the G5
aggregation over every seal in a SPEC.

Commands:
  seal     Compute the surface fingerprint from a candidate payload and write the seal.
  verify   Recompute digests and report fresh or stale. Read-only.
  sweep    Verify, then persist staleness plus its cause into the affected seals.
  summary  Aggregate every seal in a SPEC for the G5 check.

Exit codes:
  0: every inspected seal is present, passing, and fresh
  1: structurally valid but gate-relevant (missing, failed, or stale)
  2: input or invocation is structurally invalid
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any


SEAL_VERSION = "1.0"
SPEC_ID_PATTERN = re.compile(r"^SPEC-\d{3,}$")
SLICE_ID_PATTERN = re.compile(r"^[A-Z][A-Z0-9]*-\d{3,}$")
DIGEST_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
# Explicit offset required; `Z` is the zero-offset case, so seals written before
# the timezone rule stay valid. See shared/timezone-resolution.md.
TIMESTAMP_PATTERN = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(?:Z|[+-]\d{2}:\d{2})$"
)

VALID_STATUS = {"passed", "failed", "passed-with-override", "pending"}
PASSING_STATUS = {"passed", "passed-with-override"}
VALID_ADHERENCE = {"ALIGNED", "PARTIAL", "MISALIGNED"}
VALID_FRESHNESS = {"fresh", "stale"}

SEAL_FILENAME = "quality-seal.json"


# --------------------------------------------------------------------------
# Fingerprint primitives. These mirror validate-implementation-assurance.py so
# a slice surface and a G5 receipt hash identical inputs identically.
# --------------------------------------------------------------------------


def canonical_fingerprint(head: str, inputs: list[dict[str, str]]) -> str:
    payload = {
        "head": head,
        "inputs": sorted(
            ({"path": item["path"], "digest": item["digest"]} for item in inputs),
            key=lambda item: item["path"],
        ),
    }
    encoded = json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(encoded).hexdigest()}"


def file_digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return f"sha256:{digest.hexdigest()}"


def normalize_path(raw: str) -> str:
    return raw.replace("\\", "/").strip()


def unsafe_path(value: str) -> bool:
    if not value:
        return True
    if value.startswith("/") or value.startswith("~"):
        return True
    if re.match(r"^[A-Za-z]:", value):
        return True
    return ".." in Path(value).parts


# --------------------------------------------------------------------------
# Layout
# --------------------------------------------------------------------------


def seal_path(project_root: Path, spec_id: str, slice_id: str) -> Path:
    return (
        project_root
        / ".araia"
        / "runs"
        / spec_id
        / "IMPLEMENT"
        / slice_id
        / SEAL_FILENAME
    )


def discover_seals(project_root: Path, spec_id: str) -> list[Path]:
    root = project_root / ".araia" / "runs" / spec_id / "IMPLEMENT"
    if not root.is_dir():
        return []
    found = [
        entry / SEAL_FILENAME
        for entry in sorted(root.iterdir())
        if entry.is_dir() and (entry / SEAL_FILENAME).is_file()
    ]
    return found


# --------------------------------------------------------------------------
# Validation
# --------------------------------------------------------------------------


def _require_mapping(
    data: Any, owner: str, errors: list[str]
) -> dict[str, Any]:
    if not isinstance(data, dict):
        errors.append(f"{owner}: expected object")
        return {}
    return data


def _require_string(
    data: dict[str, Any], key: str, owner: str, errors: list[str]
) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        errors.append(f"{owner}.{key}: expected non-empty string")
        return ""
    return value


def validate_surface_inputs(
    inputs: Any, errors: list[str], *, require_digest: bool
) -> list[dict[str, Any]]:
    if not isinstance(inputs, list) or not inputs:
        errors.append("surface.inputs: expected non-empty array")
        return []

    normalized: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, raw in enumerate(inputs):
        owner = f"surface.inputs[{index}]"
        entry = _require_mapping(raw, owner, errors)
        if not entry:
            continue

        path_value = normalize_path(_require_string(entry, "path", owner, errors))
        if path_value and unsafe_path(path_value):
            errors.append(f"{owner}.path: must be a safe project-relative path")
            continue
        if path_value in seen:
            errors.append(f"{owner}.path: duplicate entry '{path_value}'")
            continue
        seen.add(path_value)

        anchors = entry.get("anchors")
        if not isinstance(anchors, list) or not all(
            isinstance(item, str) and item.strip() for item in anchors
        ):
            errors.append(f"{owner}.anchors: expected array of criterion IDs")
            anchors = []

        record: dict[str, Any] = {
            "path": path_value,
            "anchors": sorted(set(anchors)),
        }

        if require_digest:
            digest = entry.get("digest")
            if not isinstance(digest, str) or not DIGEST_PATTERN.match(digest):
                errors.append(f"{owner}.digest: expected sha256:<64 hex>")
            else:
                record["digest"] = digest

        normalized.append(record)

    return normalized


def validate_seal(seal: Any, path: Path) -> tuple[dict[str, Any], list[str]]:
    """Structural validation. Returns the seal and the list of errors."""
    errors: list[str] = []
    data = _require_mapping(seal, str(path), errors)
    if not data:
        return {}, errors

    version = data.get("seal-version")
    if version != SEAL_VERSION:
        errors.append(f"seal-version: expected '{SEAL_VERSION}', got {version!r}")

    spec_id = _require_string(data, "spec-id", "seal", errors)
    if spec_id and not SPEC_ID_PATTERN.match(spec_id):
        errors.append(f"spec-id: '{spec_id}' does not match SPEC-NNN")

    slice_id = _require_string(data, "slice-id", "seal", errors)
    if slice_id and not SLICE_ID_PATTERN.match(slice_id):
        errors.append(f"slice-id: '{slice_id}' does not match PREFIX-NNN")

    _require_string(data, "adapter", "seal", errors)

    sealed_at = data.get("sealed-at")
    if not isinstance(sealed_at, str) or not TIMESTAMP_PATTERN.match(sealed_at):
        errors.append("sealed-at: expected ISO-8601 instant with an explicit offset")

    status = data.get("status")
    if status not in VALID_STATUS:
        errors.append(f"status: expected one of {sorted(VALID_STATUS)}")

    adherence = _require_mapping(data.get("adherence"), "adherence", errors)
    if adherence:
        verdict = adherence.get("verdict")
        if verdict not in VALID_ADHERENCE:
            errors.append(f"adherence.verdict: expected one of {sorted(VALID_ADHERENCE)}")

    eqi_slice = _require_mapping(data.get("eqi-slice"), "eqi-slice", errors)
    if eqi_slice:
        supported = eqi_slice.get("supported")
        if not isinstance(supported, bool):
            errors.append("eqi-slice.supported: expected boolean")
        elif supported:
            raw = eqi_slice.get("raw")
            if isinstance(raw, bool) or not isinstance(raw, (int, float)):
                errors.append("eqi-slice.raw: expected number when supported")
            elif not 0 <= float(raw) <= 10:
                errors.append("eqi-slice.raw: expected a value between 0 and 10")

    override = data.get("override")
    if override is not None:
        override_map = _require_mapping(override, "override", errors)
        if override_map:
            _require_string(override_map, "reason", "override", errors)
            criteria = override_map.get("criteria")
            if not isinstance(criteria, list) or not criteria:
                errors.append("override.criteria: expected non-empty array")

    if status == "passed-with-override" and override is None:
        errors.append("status 'passed-with-override' requires an override object")
    if status == "passed" and override is not None:
        errors.append("status 'passed' must not carry an override")

    surface = _require_mapping(data.get("surface"), "surface", errors)
    inputs: list[dict[str, Any]] = []
    if surface:
        algorithm = surface.get("fingerprint-algorithm")
        if algorithm != "sha256":
            errors.append("surface.fingerprint-algorithm: expected 'sha256'")
        fingerprint = surface.get("fingerprint")
        if not isinstance(fingerprint, str) or not DIGEST_PATTERN.match(fingerprint):
            errors.append("surface.fingerprint: expected sha256:<64 hex>")
        inputs = validate_surface_inputs(
            surface.get("inputs"), errors, require_digest=True
        )

    head = data.get("sealed-head")
    if head is not None and not isinstance(head, str):
        errors.append("sealed-head: expected string or null")

    if not errors and inputs:
        expected = canonical_fingerprint(head or "", inputs)
        if expected != surface.get("fingerprint"):
            errors.append(
                "surface.fingerprint: does not match the recorded input ledger"
            )

    freshness = _require_mapping(data.get("freshness"), "freshness", errors)
    if freshness:
        state = freshness.get("status")
        if state not in VALID_FRESHNESS:
            errors.append(f"freshness.status: expected one of {sorted(VALID_FRESHNESS)}")
        for key in ("changed-inputs", "stale-criteria"):
            value = freshness.get(key)
            if not isinstance(value, list):
                errors.append(f"freshness.{key}: expected array")

    history = data.get("history")
    if history is not None and not isinstance(history, list):
        errors.append("history: expected array")

    return data, errors


# --------------------------------------------------------------------------
# Freshness
# --------------------------------------------------------------------------


def evaluate_freshness(
    seal: dict[str, Any], project_root: Path
) -> dict[str, Any]:
    """Recompute digests. Pure read; never mutates the seal on disk."""
    changed: list[str] = []
    missing: list[str] = []
    anchors: set[str] = set()

    for entry in seal["surface"]["inputs"]:
        target = project_root / entry["path"]
        if not target.is_file():
            missing.append(entry["path"])
            changed.append(entry["path"])
            anchors.update(entry["anchors"])
            continue
        if file_digest(target) != entry["digest"]:
            changed.append(entry["path"])
            anchors.update(entry["anchors"])

    unanchored = [
        path
        for path in changed
        if not next(
            (e["anchors"] for e in seal["surface"]["inputs"] if e["path"] == path),
            [],
        )
    ]

    return {
        "slice-id": seal["slice-id"],
        "status": "stale" if changed else "fresh",
        "changed-inputs": sorted(changed),
        "missing-inputs": sorted(missing),
        "stale-criteria": sorted(anchors),
        "unanchored-inputs": sorted(unanchored),
    }


def load_seal(path: Path) -> tuple[dict[str, Any], list[str]]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {}, [f"{path}: seal not found"]
    except json.JSONDecodeError as error:
        return {}, [f"{path}: invalid JSON ({error})"]
    return validate_seal(raw, path)


def write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )


# --------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------


def command_seal(args: argparse.Namespace) -> int:
    project_root = Path(args.project_root).resolve()
    candidate_path = Path(args.candidate)

    try:
        candidate = json.loads(candidate_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        print(f"F-SEAL: candidate not found: {candidate_path}", file=sys.stderr)
        return 2
    except json.JSONDecodeError as error:
        print(f"F-SEAL: candidate is not valid JSON: {error}", file=sys.stderr)
        return 2

    errors: list[str] = []
    candidate = _require_mapping(candidate, "candidate", errors)
    if errors:
        for message in errors:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    spec_id = _require_string(candidate, "spec-id", "candidate", errors)
    slice_id = _require_string(candidate, "slice-id", "candidate", errors)
    surface = _require_mapping(candidate.get("surface"), "surface", errors)
    declared = validate_surface_inputs(
        surface.get("inputs") if surface else None, errors, require_digest=False
    )
    if errors:
        for message in errors:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    resolved: list[dict[str, Any]] = []
    for entry in declared:
        target = project_root / entry["path"]
        if not target.is_file():
            print(
                f"SURFACE-INCOMPLETE: declared input is absent: {entry['path']}",
                file=sys.stderr,
            )
            return 2
        resolved.append(
            {
                "path": entry["path"],
                "digest": file_digest(target),
                "anchors": entry["anchors"],
            }
        )

    resolved.sort(key=lambda item: item["path"])
    head = candidate.get("sealed-head") or ""
    fingerprint = canonical_fingerprint(head, resolved)

    sealed_at = candidate.get("sealed-at")
    if not isinstance(sealed_at, str) or not TIMESTAMP_PATTERN.match(sealed_at):
        print(
            "F-SEAL: candidate.sealed-at: expected ISO-8601 instant with an explicit offset",
            file=sys.stderr,
        )
        return 2

    seal = {
        "seal-version": SEAL_VERSION,
        "spec-id": spec_id,
        "slice-id": slice_id,
        "adapter": candidate.get("adapter", ""),
        "sealed-at": sealed_at,
        "sealed-head": candidate.get("sealed-head"),
        "status": candidate.get("status"),
        "adherence": candidate.get("adherence"),
        "eqi-slice": candidate.get("eqi-slice"),
        "override": candidate.get("override"),
        "surface": {
            "fingerprint-algorithm": "sha256",
            "fingerprint": fingerprint,
            "inputs": resolved,
        },
        "freshness": {
            "status": "fresh",
            "checked-at": sealed_at,
            "checked-head": candidate.get("sealed-head"),
            "stale-cause": None,
            "changed-inputs": [],
            "stale-criteria": [],
        },
        "history": candidate.get("history", []),
    }

    validated, seal_errors = validate_seal(seal, candidate_path)
    if seal_errors:
        for message in seal_errors:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    destination = (
        Path(args.output)
        if args.output
        else seal_path(project_root, spec_id, slice_id)
    )
    write_json(destination, validated)

    payload = {
        "command": "seal",
        "spec-id": spec_id,
        "slice-id": slice_id,
        "status": validated["status"],
        "fingerprint": fingerprint,
        "inputs": len(resolved),
        "seal": str(destination),
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0 if validated["status"] in PASSING_STATUS else 1


def _collect(args: argparse.Namespace) -> tuple[Path, list[Path], int]:
    project_root = Path(args.project_root).resolve()
    if not SPEC_ID_PATTERN.match(args.spec_id):
        print(f"F-SEAL: --spec-id '{args.spec_id}' does not match SPEC-NNN", file=sys.stderr)
        return project_root, [], 2
    if getattr(args, "slice_id", None):
        return project_root, [seal_path(project_root, args.spec_id, args.slice_id)], 0
    return project_root, discover_seals(project_root, args.spec_id), 0


def _inspect(
    project_root: Path, paths: list[Path]
) -> tuple[list[dict[str, Any]], list[str], list[tuple[Path, dict[str, Any]]]]:
    results: list[dict[str, Any]] = []
    invalid: list[str] = []
    loaded: list[tuple[Path, dict[str, Any]]] = []

    for path in paths:
        seal, errors = load_seal(path)
        if errors:
            invalid.extend(errors)
            continue
        freshness = evaluate_freshness(seal, project_root)
        freshness["seal"] = str(path)
        freshness["checkpoint-status"] = seal["status"]
        results.append(freshness)
        loaded.append((path, seal))

    return results, invalid, loaded


def command_verify(args: argparse.Namespace) -> int:
    project_root, paths, code = _collect(args)
    if code:
        return code

    results, invalid, _ = _inspect(project_root, paths)
    if invalid:
        for message in invalid:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    stale = [item for item in results if item["status"] == "stale"]
    payload = {
        "command": "verify",
        "spec-id": args.spec_id,
        "seals": len(results),
        "fresh": len(results) - len(stale),
        "stale": len(stale),
        "results": results,
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 1 if stale or not results else 0


def command_sweep(args: argparse.Namespace) -> int:
    project_root, paths, code = _collect(args)
    if code:
        return code
    if not SLICE_ID_PATTERN.match(args.cause):
        print(f"F-SEAL: --cause '{args.cause}' does not match PREFIX-NNN", file=sys.stderr)
        return 2

    results, invalid, loaded = _inspect(project_root, paths)
    if invalid:
        for message in invalid:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    by_slice = {item["slice-id"]: item for item in results}
    marked: list[str] = []
    incomplete: list[str] = []

    for path, seal in loaded:
        if seal["slice-id"] == args.cause:
            continue
        outcome = by_slice[seal["slice-id"]]
        if outcome["status"] != "stale":
            continue
        if outcome["unanchored-inputs"]:
            incomplete.append(seal["slice-id"])

        # The first cause wins. Drift is recomputed against the sealed digests,
        # so changed-inputs is already cumulative; only the attribution would
        # be lost by overwriting it on a later sweep.
        previous = seal.get("freshness", {})
        cause = (
            previous.get("stale-cause")
            if previous.get("status") == "stale" and previous.get("stale-cause")
            else args.cause
        )

        seal["freshness"] = {
            "status": "stale",
            "checked-at": args.at,
            "checked-head": args.head,
            "stale-cause": cause,
            "changed-inputs": outcome["changed-inputs"],
            "stale-criteria": outcome["stale-criteria"],
        }

        validated, seal_errors = validate_seal(seal, path)
        if seal_errors:
            for message in seal_errors:
                print(f"F-SEAL: {path}: {message}", file=sys.stderr)
            return 2

        write_json(path, validated)
        marked.append(seal["slice-id"])

    payload = {
        "command": "sweep",
        "spec-id": args.spec_id,
        "cause": args.cause,
        "seals": len(loaded),
        "marked-stale": sorted(marked),
        "anchor-missing": sorted(incomplete),
        "results": results,
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    if incomplete:
        return 1
    return 1 if marked else 0


def command_summary(args: argparse.Namespace) -> int:
    project_root, paths, code = _collect(args)
    if code:
        return code

    results, invalid, loaded = _inspect(project_root, paths)
    if invalid:
        for message in invalid:
            print(f"F-SEAL: {message}", file=sys.stderr)
        return 2

    expected = sorted(set(args.expect_slice or []))
    sealed = sorted(seal["slice-id"] for _, seal in loaded)
    missing = [slice_id for slice_id in expected if slice_id not in sealed]

    failed: list[str] = []
    overrides: list[dict[str, Any]] = []
    for _, seal in loaded:
        if seal["status"] not in PASSING_STATUS:
            failed.append(seal["slice-id"])
        if seal["status"] == "passed-with-override" and seal.get("override"):
            overrides.append(
                {
                    "slice-id": seal["slice-id"],
                    "reason": seal["override"].get("reason"),
                    "criteria": seal["override"].get("criteria", []),
                }
            )

    stale = [
        {
            "slice-id": item["slice-id"],
            "stale-criteria": item["stale-criteria"],
            "changed-inputs": item["changed-inputs"],
        }
        for item in results
        if item["status"] == "stale"
    ]

    payload = {
        "command": "summary",
        "spec-id": args.spec_id,
        "seals": len(loaded),
        "expected": expected,
        "missing": missing,
        "failed": sorted(failed),
        "stale": stale,
        "overrides": overrides,
        "ok": not (missing or failed or stale),
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))

    return 0 if payload["ok"] else 1


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Seal, verify, and sweep Delivery Slice quality checkpoints."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    seal_parser = subparsers.add_parser(
        "seal", help="Write a seal from a candidate payload."
    )
    seal_parser.add_argument("--candidate", required=True)
    seal_parser.add_argument("--project-root", default=".")
    seal_parser.add_argument("--output", default=None)
    seal_parser.set_defaults(handler=command_seal)

    verify_parser = subparsers.add_parser(
        "verify", help="Report fresh or stale for one seal or a whole SPEC."
    )
    verify_parser.add_argument("--project-root", default=".")
    verify_parser.add_argument("--spec-id", required=True)
    verify_parser.add_argument("--slice-id", default=None)
    verify_parser.set_defaults(handler=command_verify)

    sweep_parser = subparsers.add_parser(
        "sweep", help="Persist staleness and its cause into the affected seals."
    )
    sweep_parser.add_argument("--project-root", default=".")
    sweep_parser.add_argument("--spec-id", required=True)
    sweep_parser.add_argument("--cause", required=True)
    sweep_parser.add_argument("--head", default=None)
    sweep_parser.add_argument("--at", required=True)
    sweep_parser.set_defaults(handler=command_sweep)

    summary_parser = subparsers.add_parser(
        "summary", help="Aggregate every seal in a SPEC for the G5 check."
    )
    summary_parser.add_argument("--project-root", default=".")
    summary_parser.add_argument("--spec-id", required=True)
    summary_parser.add_argument("--expect-slice", action="append", required=True)
    summary_parser.set_defaults(handler=command_summary)

    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.handler(args)


if __name__ == "__main__":
    raise SystemExit(main())
