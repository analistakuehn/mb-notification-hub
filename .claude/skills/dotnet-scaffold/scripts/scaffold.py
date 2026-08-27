#!/usr/bin/env python3
"""Deterministic, low-context .NET scaffold generator used by dotnet-scaffold.

The script owns template rendering, overlay composition, safety checks, build
verification, and Stack Profile publication. Its stdout contract is one compact
JSON object so an orchestrating agent never needs to load or reproduce templates.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


AXES = ("transports", "persistence", "messaging", "cache")
PUBLIC_NUGET = "https://api.nuget.org/v3/index.json"
TEMPLATE_TOKEN = re.compile(r"\{\{([A-Za-z][A-Za-z0-9-]*)\}\}")
SAFE_SOLUTION = re.compile(r"^[A-Za-z_][A-Za-z0-9_.-]*$")
SAFE_NAMESPACE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
SAFE_MODULE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
FRAMEWORK = re.compile(r"^net(?P<major>\d+)\.0$")
SUPPORTED_FRAMEWORKS = ("net10.0",)
PRIVATE_HOST_HINTS = (
    "pkgs.dev.azure.com",
    "myget.org",
    "jfrog.io",
    "gitlab.com",
    "nexus.",
    "artifactory.",
)
SAFE_OVERLAP_FILES = frozenset(
    {
        ".gitignore",
        "README.md",
        "LICENSE",
        "AGENTS.md",
        "CLAUDE.md",
        ".github/PULL_REQUEST_TEMPLATE.md",
    }
)
SAFE_OVERLAP_PREFIXES = (".araia/", ".codex/", ".claude/", "docs/")
COMMON_STARTER_FILES = (
    "Directory.Build.props.tmpl",
    "Directory.Packages.props.tmpl",
    "NuGet.Config.tmpl",
    "global.json.tmpl",
    ".gitignore.tmpl",
    ".editorconfig.tmpl",
)
SAFE_CATALOG_ID = re.compile(r"^[a-z][a-z0-9-]*$")
SAFE_DOTTED_IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$")
SAFE_SDK = re.compile(r"^[A-Za-z][A-Za-z0-9.]*$")
PRODUCTION_ROLES = frozenset({"host", "library", "domain", "application", "infrastructure"})
REQUIRED_SURFACES = (
    "composition",
    "endpoint-composition",
    "host-infrastructure",
    "shared-kernel",
    "module-context",
    "domain",
    "features",
    "integration",
    "module-infrastructure",
)
REGISTRATION_KEYS = ("application", "infrastructure", "endpoints")
REGISTRATION_MARKERS = {
    "application": ("usings-module", "di-module"),
    "infrastructure": (
        "usings-persistence",
        "usings-messaging",
        "usings-cache",
        "di-persistence",
        "di-messaging",
        "di-cache",
    ),
    "endpoints": ("module-endpoints",),
}
MODULE_TOKEN = "{{ModuleName}}"


class ScaffoldError(Exception):
    """Structured failure that maps to the script's compact JSON contract."""

    def __init__(self, code: str, message: str, exit_code: int | None = None, **details: Any) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        default_exit_code = 5 if code == "invalid-starter-manifest" else 2
        self.exit_code = default_exit_code if exit_code is None else exit_code
        self.details = details


class JsonArgumentParser(argparse.ArgumentParser):
    """Convert CLI syntax failures to the generator's JSON error contract."""

    def error(self, message: str) -> None:
        raise ScaffoldError("invalid-arguments", message)


@dataclass(frozen=True)
class ProjectSpec:
    name: str
    path: str
    sdk: str = "Microsoft.NET.Sdk"
    references: tuple[str, ...] = ()
    role: str = "library"
    namespace_suffix: str | None = None
    packages: tuple[str, ...] = ()

    @property
    def is_production(self) -> bool:
        return self.role in PRODUCTION_ROLES


@dataclass(frozen=True)
class Surface:
    key: str
    project: str
    path: str
    namespace: str


@dataclass(frozen=True)
class Registration:
    key: str
    project: str
    path: str
    type_name: str
    namespace: str


@dataclass(frozen=True)
class DependencyRule:
    id: str
    scope: str
    forbid_namespaces: tuple[str, ...]
    forbid_packages: tuple[str, ...]


@dataclass(frozen=True)
class Architecture:
    id: str
    title: str
    host_name: str
    host_path: str
    infrastructure_name: str
    infrastructure_path: str
    features_root: str
    slice_shape: str
    projects: tuple[ProjectSpec, ...]
    surfaces: dict[str, Surface]
    registrations: dict[str, Registration]
    rules: tuple[DependencyRule, ...]

    @property
    def host_project(self) -> ProjectSpec:
        return next(project for project in self.projects if project.name == self.host_name)

    @property
    def production_projects(self) -> tuple[ProjectSpec, ...]:
        return tuple(project for project in self.projects if project.is_production)

    def project(self, name: str) -> ProjectSpec:
        return next(project for project in self.projects if project.name == name)

@dataclass(frozen=True)
class StarterSpec:
    id: str
    aliases: tuple[str, ...]
    template_set: str
    architecture: Architecture


@dataclass
class Overlay:
    id: str
    axis: str
    root: Path
    requires: list[str]
    conflicts: list[str]
    packages: list[dict[str, str]]
    project_references: dict[str, list[str]]
    files: list[dict[str, str]]
    patches: list[dict[str, str]]
    appsettings: dict[str, Any]
    readme: str


@dataclass
class ScaffoldConfig:
    output: Path
    solution: str
    namespace: str
    framework: str
    starter: StarterSpec
    architecture: Architecture
    module: str | None
    selections: dict[str, list[str]]
    profile_path: str
    force: bool
    allow_private_feed: bool
    dry_run: bool
    skip_verification: bool

    def resolve(self, text: str) -> str:
        return text.replace(MODULE_TOKEN, self.module or "")

    def project_namespace(self, project: ProjectSpec) -> str:
        return f"{self.namespace}.{project.namespace_suffix or project.name}"

    def surface(self, key: str) -> Surface:
        return self.architecture.surfaces[key]

    def surface_path(self, key: str) -> str:
        return self.resolve(self.surface(key).path)

    def surface_namespace(self, key: str) -> str:
        return f"{self.namespace}.{self.resolve(self.surface(key).namespace)}"

    def surface_project(self, key: str) -> ProjectSpec:
        return self.architecture.project(self.surface(key).project)

    def registration(self, key: str) -> Registration:
        return self.architecture.registrations[key]

    def registration_path(self, key: str) -> str:
        return self.resolve(self.registration(key).path)

    def registration_namespace(self, key: str) -> str:
        return f"{self.namespace}.{self.resolve(self.registration(key).namespace)}"

    def fingerprint(self) -> dict[str, Any]:
        return {
            "solution": self.solution,
            "namespace": self.namespace,
            "framework": self.framework,
            "architecture": self.architecture.id,
            "template-set": self.starter.template_set,
            "module": self.module,
            "selections": self.selections,
            "profile-path": self.profile_path,
        }


def compact_json(payload: dict[str, Any]) -> str:
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def parse_scalar(value: str) -> Any:
    value = value.strip()
    if not value:
        return {}
    if value in ("[]", "{}"):
        return [] if value == "[]" else {}
    if value.lower() in ("true", "false"):
        return value.lower() == "true"
    if (value.startswith('"') and value.endswith('"')) or (value.startswith("'") and value.endswith("'")):
        return value[1:-1]
    if re.fullmatch(r"-?\d+", value):
        return int(value)
    return value


def parse_inline_list(value: str, error_code: str = "invalid-overlay-manifest") -> list[str]:
    value = value.strip()
    if value == "[]":
        return []
    if not (value.startswith("[") and value.endswith("]")):
        raise ScaffoldError(error_code, f"Expected inline YAML list, got: {value}")
    return [str(parse_scalar(item)) for item in value[1:-1].split(",") if item.strip()]


def parse_inline_map(value: str, error_code: str = "invalid-overlay-manifest") -> dict[str, str]:
    result: dict[str, str] = {}
    for match in re.finditer(r"([A-Za-z][A-Za-z0-9-]*):\s*(\"[^\"]*\"|'[^']*'|[^,}]+)", value):
        result[match.group(1)] = str(parse_scalar(match.group(2)))
    if not result:
        raise ScaffoldError(error_code, f"Expected inline YAML mapping, got: {value}")
    return result


def top_value(
    lines: list[str],
    key: str,
    error_code: str = "invalid-overlay-manifest",
    subject: str = "overlay manifest",
) -> str:
    prefix = f"{key}:"
    for line in lines:
        if line.startswith(prefix):
            return line[len(prefix) :].strip()
    raise ScaffoldError(error_code, f"Missing '{key}' in {subject}")


def section(lines: list[str], key: str) -> list[str]:
    marker = f"{key}:"
    start = next((index for index, line in enumerate(lines) if line.startswith(marker)), None)
    if start is None:
        return []
    collected: list[str] = []
    for line in lines[start + 1 :]:
        if line and not line[0].isspace():
            break
        collected.append(line)
    return collected


def parse_list_of_maps(lines: list[str], error_code: str = "invalid-overlay-manifest") -> list[dict[str, str]]:
    records: list[dict[str, str]] = []
    current: dict[str, str] | None = None
    for line in lines:
        stripped = line.strip()
        if not stripped:
            continue
        if stripped.startswith("- {"):
            records.append(parse_inline_map(stripped[2:], error_code))
            current = None
        elif stripped.startswith("- "):
            current = {}
            records.append(current)
            key, separator, value = stripped[2:].partition(":")
            if not separator:
                raise ScaffoldError(error_code, f"Invalid YAML list entry: {stripped}")
            current[key.strip()] = str(parse_scalar(value))
        else:
            if current is None:
                raise ScaffoldError(error_code, f"Orphan YAML mapping entry: {stripped}")
            key, separator, value = stripped.partition(":")
            if not separator:
                raise ScaffoldError(error_code, f"Invalid YAML mapping entry: {stripped}")
            current[key.strip()] = str(parse_scalar(value))
    return records


def parse_keyed_maps(lines: list[str], error_code: str = "invalid-starter-manifest") -> dict[str, dict[str, str]]:
    """Parse a mapping whose values are inline YAML maps, preserving declaration order."""
    records: dict[str, dict[str, str]] = {}
    for line in lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        key, separator, value = stripped.partition(":")
        if not separator or not value.strip().startswith("{"):
            raise ScaffoldError(error_code, f"Expected '<key>: {{ ... }}' mapping entry, got: {stripped}")
        key = key.strip()
        if key in records:
            raise ScaffoldError(error_code, f"Duplicate mapping key '{key}'")
        records[key] = parse_inline_map(value.strip(), error_code)
    return records


def parse_project_references(lines: list[str]) -> dict[str, list[str]]:
    result: dict[str, list[str]] = {}
    current: str | None = None
    for line in lines:
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        stripped = line.strip()
        if indent == 2 and stripped.endswith(":"):
            current = stripped[:-1]
            result[current] = []
        elif indent >= 4 and stripped.startswith("- ") and current:
            result[current].append(str(parse_scalar(stripped[2:])))
    return result


def parse_nested_mapping(lines: list[str]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    stack: list[tuple[int, dict[str, Any]]] = [(-1, result)]
    for line in lines:
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        key, separator, value = line.strip().partition(":")
        if not separator:
            raise ScaffoldError("invalid-overlay-manifest", f"Invalid nested YAML entry: {line.strip()}")
        while stack and indent <= stack[-1][0]:
            stack.pop()
        parent = stack[-1][1]
        parsed = parse_scalar(value)
        parent[key] = parsed
        if isinstance(parsed, dict):
            stack.append((indent, parsed))
    return result


def parse_block_scalar(lines: list[str], key: str) -> str:
    marker = f"{key}: |"
    start = next((index for index, line in enumerate(lines) if line == marker), None)
    if start is None:
        return ""
    collected: list[str] = []
    for line in lines[start + 1 :]:
        if line and not line[0].isspace():
            break
        collected.append(line[2:] if line.startswith("  ") else line)
    return "\n".join(collected).rstrip() + "\n"


def load_overlays(templates_root: Path) -> dict[str, Overlay]:
    overlays: dict[str, Overlay] = {}
    for manifest_path in sorted((templates_root / "features").glob("*/*/manifest.yaml")):
        lines = manifest_path.read_text(encoding="utf-8").splitlines()
        overlay_id = top_value(lines, "id")
        manifest_axis = top_value(lines, "axis")
        axis = "transports" if manifest_axis == "transport" else manifest_axis
        if axis not in AXES:
            raise ScaffoldError("invalid-overlay-manifest", f"Unknown axis '{manifest_axis}' in {manifest_path}")
        overlay = Overlay(
            id=overlay_id,
            axis=axis,
            root=manifest_path.parent,
            requires=parse_inline_list(top_value(lines, "requires")),
            conflicts=parse_inline_list(top_value(lines, "conflicts")),
            packages=parse_list_of_maps(section(lines, "packages")),
            project_references=parse_project_references(section(lines, "project-references")),
            files=parse_list_of_maps(section(lines, "files")),
            patches=parse_list_of_maps(section(lines, "patches")),
            appsettings=parse_nested_mapping(section(lines, "appsettings-additions")),
            readme=parse_block_scalar(lines, "readme-section"),
        )
        if overlay_id in overlays:
            raise ScaffoldError("invalid-overlay-manifest", f"Duplicate overlay id '{overlay_id}'")
        overlays[overlay_id] = overlay
    return overlays


def _starter_value(lines: list[str], key: str, manifest_path: Path) -> str:
    return top_value(
        lines,
        key,
        error_code="invalid-starter-manifest",
        subject=f"starter manifest '{manifest_path}'",
    )


def _validate_catalog_id(value: str, field: str, manifest_path: Path) -> str:
    normalized = value.strip().lower()
    if value.strip() != normalized or not SAFE_CATALOG_ID.fullmatch(normalized):
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Invalid {field} '{value}' in {manifest_path}",
        )
    return normalized


def _validate_catalog_path(value: str, field: str, manifest_path: Path) -> str:
    candidate = PurePosixPath(value)
    if (
        not value
        or candidate.as_posix() == "."
        or candidate.is_absolute()
        or ".." in candidate.parts
        or ":" in value
        or "\\" in value
    ):
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Invalid relative {field} '{value}' in {manifest_path}",
        )
    return candidate.as_posix()


def _architecture_from_manifest(lines: list[str], manifest_path: Path, architecture_id: str) -> Architecture:
    records = parse_list_of_maps(section(lines, "projects"), "invalid-starter-manifest")
    if not records:
        raise ScaffoldError("invalid-starter-manifest", f"No projects declared in {manifest_path}")

    projects: list[ProjectSpec] = []
    required_project_fields = ("name", "path", "sdk", "role")
    for record in records:
        missing = [field for field in required_project_fields if not record.get(field)]
        if missing:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Project in {manifest_path} is missing: {', '.join(missing)}",
            )
        name = record["name"]
        sdk = record["sdk"]
        role = record["role"]
        namespace_suffix = record.get("namespace-suffix") or name
        if (
            not SAFE_SOLUTION.fullmatch(name)
            or not SAFE_SDK.fullmatch(sdk)
            or not SAFE_CATALOG_ID.fullmatch(role)
            or not SAFE_DOTTED_IDENTIFIER.fullmatch(namespace_suffix)
        ):
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Project in {manifest_path} has an invalid name, SDK, role, or namespace suffix",
                project=name,
            )
        references = tuple(item.strip() for item in record.get("references", "").split("|") if item.strip())
        packages = tuple(item.strip() for item in record.get("packages", "").split("|") if item.strip())
        projects.append(
            ProjectSpec(
                name=name,
                path=_validate_catalog_path(record["path"], "project path", manifest_path),
                sdk=sdk,
                references=references,
                role=role,
                namespace_suffix=namespace_suffix,
                packages=packages,
            )
        )

    names = [project.name for project in projects]
    paths = [project.path for project in projects]
    if len(names) != len(set(names)) or len(paths) != len(set(paths)):
        raise ScaffoldError("invalid-starter-manifest", f"Duplicate project name or path in {manifest_path}")

    host_name = _starter_value(lines, "host-name", manifest_path)
    host_path = _validate_catalog_path(_starter_value(lines, "host-path", manifest_path), "host path", manifest_path)
    infrastructure_name = _starter_value(lines, "infrastructure-name", manifest_path)
    infrastructure_path = _validate_catalog_path(
        _starter_value(lines, "infrastructure-path", manifest_path),
        "infrastructure path",
        manifest_path,
    )
    names_set = set(names)
    unknown_references = sorted(
        reference for project in projects for reference in project.references if reference not in names_set
    )
    if host_name not in names_set or infrastructure_name not in names_set or unknown_references:
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Invalid project linkage in {manifest_path}",
            host=host_name,
            infrastructure=infrastructure_name,
            unknown_references=unknown_references,
        )
    if sum(project.role == "host" for project in projects) != 1:
        raise ScaffoldError("invalid-starter-manifest", f"Exactly one host project is required in {manifest_path}")
    host_project = next(project for project in projects if project.name == host_name)
    infrastructure_project = next(project for project in projects if project.name == infrastructure_name)
    if host_project.role != "host" or host_project.path != host_path or infrastructure_project.path != infrastructure_path:
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Host or infrastructure descriptor does not match its project in {manifest_path}",
        )

    projects_by_name = {project.name: project for project in projects}
    surfaces = _surfaces_from_manifest(lines, manifest_path, projects_by_name)
    registrations = _registrations_from_manifest(lines, manifest_path, projects_by_name)
    rules = _rules_from_manifest(lines, manifest_path)

    return Architecture(
        id=architecture_id,
        title=_starter_value(lines, "title", manifest_path),
        host_name=host_name,
        host_path=host_path,
        infrastructure_name=infrastructure_name,
        infrastructure_path=infrastructure_path,
        features_root=_validate_catalog_path(
            _starter_value(lines, "features-root", manifest_path),
            "features root",
            manifest_path,
        ),
        slice_shape=_starter_value(lines, "slice-shape", manifest_path),
        projects=tuple(projects),
        surfaces=surfaces,
        registrations=registrations,
        rules=rules,
    )


def _derive_namespace(project: ProjectSpec, path: str, manifest_path: Path) -> str:
    """Derive a namespace suffix from a folder that lives inside its owning project."""
    prefix = project.path.rstrip("/") + "/"
    if path == project.path:
        return project.namespace_suffix or project.name
    if not path.startswith(prefix):
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Path '{path}' is outside its declared project '{project.name}' in {manifest_path}",
        )
    tail = path[len(prefix) :].replace("/", ".")
    return f"{project.namespace_suffix or project.name}.{tail}"


def _surface_owner(
    record: dict[str, str],
    key: str,
    manifest_path: Path,
    projects_by_name: dict[str, ProjectSpec],
) -> tuple[ProjectSpec, str]:
    project_name = record.get("project", "")
    raw_path = record.get("path", "")
    if project_name not in projects_by_name or not raw_path:
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Entry '{key}' in {manifest_path} needs a known project and a path",
            project=project_name,
        )
    template = raw_path.replace(MODULE_TOKEN, "Module")
    _validate_catalog_path(template, f"'{key}' path", manifest_path)
    return projects_by_name[project_name], raw_path


def _surfaces_from_manifest(
    lines: list[str],
    manifest_path: Path,
    projects_by_name: dict[str, ProjectSpec],
) -> dict[str, Surface]:
    records = parse_keyed_maps(section(lines, "surfaces"))
    missing = [key for key in REQUIRED_SURFACES if key not in records]
    if missing:
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Starter manifest {manifest_path} is missing surfaces",
            missing=missing,
        )
    surfaces: dict[str, Surface] = {}
    for key, record in records.items():
        if not SAFE_CATALOG_ID.fullmatch(key):
            raise ScaffoldError("invalid-starter-manifest", f"Invalid surface key '{key}' in {manifest_path}")
        project, path = _surface_owner(record, key, manifest_path, projects_by_name)
        namespace = record.get("namespace") or _derive_namespace(project, path, manifest_path)
        if not SAFE_DOTTED_IDENTIFIER.fullmatch(namespace.replace(MODULE_TOKEN, "Module")):
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Surface '{key}' resolves to an invalid namespace in {manifest_path}",
                namespace=namespace,
            )
        surfaces[key] = Surface(key=key, project=project.name, path=path, namespace=namespace)
    return surfaces


def _registrations_from_manifest(
    lines: list[str],
    manifest_path: Path,
    projects_by_name: dict[str, ProjectSpec],
) -> dict[str, Registration]:
    records = parse_keyed_maps(section(lines, "registrations"))
    missing = [key for key in REGISTRATION_KEYS if key not in records]
    unexpected = [key for key in records if key not in REGISTRATION_KEYS]
    if missing or unexpected:
        raise ScaffoldError(
            "invalid-starter-manifest",
            f"Starter manifest {manifest_path} declares the wrong registration keys",
            missing=missing,
            unexpected=unexpected,
        )
    registrations: dict[str, Registration] = {}
    for key in REGISTRATION_KEYS:
        record = records[key]
        project, path = _surface_owner(record, key, manifest_path, projects_by_name)
        type_name = record.get("type", "")
        if not type_name or not SAFE_MODULE.fullmatch(type_name.replace(MODULE_TOKEN, "Module")):
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Registration '{key}' in {manifest_path} needs a valid type name",
                type=type_name,
            )
        folder = PurePosixPath(path).parent.as_posix()
        namespace = _derive_namespace(project, folder, manifest_path)
        registrations[key] = Registration(
            key=key,
            project=project.name,
            path=path,
            type_name=type_name,
            namespace=namespace,
        )
    return registrations


def _rules_from_manifest(lines: list[str], manifest_path: Path) -> tuple[DependencyRule, ...]:
    rules: list[DependencyRule] = []
    seen: set[str] = set()
    for record in parse_list_of_maps(section(lines, "dependency-rules"), "invalid-starter-manifest"):
        rule_id = record.get("id", "")
        scope = record.get("scope", "")
        if not SAFE_CATALOG_ID.fullmatch(rule_id) or not scope:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Dependency rule in {manifest_path} needs a kebab-case id and a scope",
                id=rule_id,
            )
        if rule_id in seen:
            raise ScaffoldError("invalid-starter-manifest", f"Duplicate dependency rule '{rule_id}' in {manifest_path}")
        seen.add(rule_id)
        forbid = tuple(item.strip() for item in record.get("forbid", "").split("|") if item.strip())
        forbid_packages = tuple(item.strip() for item in record.get("forbid-packages", "").split("|") if item.strip())
        if not forbid and not forbid_packages:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Dependency rule '{rule_id}' in {manifest_path} forbids nothing",
            )
        rules.append(
            DependencyRule(
                id=rule_id,
                scope=scope,
                forbid_namespaces=forbid,
                forbid_packages=forbid_packages,
            )
        )
    if not rules:
        raise ScaffoldError("invalid-starter-manifest", f"No dependency rules declared in {manifest_path}")
    return tuple(rules)


def load_starters(templates_root: Path) -> dict[str, StarterSpec]:
    starters_root = templates_root / "starters"
    manifests: dict[str, dict[str, Any]] = {}
    for manifest_path in sorted(starters_root.glob("*/starter.yaml")):
        lines = manifest_path.read_text(encoding="utf-8").splitlines()
        starter_id = _validate_catalog_id(_starter_value(lines, "id", manifest_path), "starter id", manifest_path)
        canonical_id = _validate_catalog_id(
            _starter_value(lines, "canonical-architecture", manifest_path),
            "canonical architecture",
            manifest_path,
        )
        aliases = tuple(
            _validate_catalog_id(alias, "starter alias", manifest_path)
            for alias in parse_inline_list(
                _starter_value(lines, "aliases", manifest_path),
                "invalid-starter-manifest",
            )
        )
        template_set = _validate_catalog_id(
            _starter_value(lines, "template-set", manifest_path),
            "template set",
            manifest_path,
        )
        if starter_id in manifests:
            raise ScaffoldError("invalid-starter-manifest", f"Duplicate starter id '{starter_id}'")
        unexpected = sorted(
            path.relative_to(manifest_path.parent).as_posix()
            for path in manifest_path.parent.rglob("*")
            if path != manifest_path
        )
        if unexpected:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Starter entry '{starter_id}' must contain only starter.yaml",
                unexpected=unexpected,
            )
        manifests[starter_id] = {
            "aliases": aliases,
            "canonical": canonical_id,
            "lines": lines,
            "manifest": manifest_path,
            "template-set": template_set,
        }

    if not manifests:
        raise ScaffoldError("invalid-starter-manifest", f"No starter manifests found under {starters_root}")

    architectures: dict[str, Architecture] = {}
    for starter_id, manifest in manifests.items():
        if manifest["canonical"] == starter_id:
            architectures[starter_id] = _architecture_from_manifest(
                manifest["lines"],
                manifest["manifest"],
                starter_id,
            )

    catalog: dict[str, StarterSpec] = {}
    for starter_id, manifest in manifests.items():
        canonical_id = manifest["canonical"]
        architecture = architectures.get(canonical_id)
        if architecture is None:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Starter '{starter_id}' references unknown canonical architecture '{canonical_id}'",
            )
        template_root = starters_root / manifest["template-set"]
        missing_templates = [name for name in COMMON_STARTER_FILES if not (template_root / name).is_file()]
        if missing_templates:
            raise ScaffoldError(
                "invalid-starter-manifest",
                f"Template set '{manifest['template-set']}' is incomplete",
                missing=missing_templates,
            )
        spec = StarterSpec(
            id=starter_id,
            aliases=manifest["aliases"],
            template_set=manifest["template-set"],
            architecture=architecture,
        )
        for token in (starter_id, *spec.aliases):
            if token in catalog:
                raise ScaffoldError("invalid-starter-manifest", f"Duplicate starter token '{token}'")
            catalog[token] = spec
    return catalog


def resolve_starter(value: str, catalog: dict[str, StarterSpec]) -> StarterSpec:
    normalized = value.strip().lower()
    if normalized not in catalog:
        raise ScaffoldError(
            "unknown-architecture",
            f"Unknown architecture starter '{value}'",
            valid=sorted({spec.id for spec in catalog.values()}),
        )
    return catalog[normalized]


def parse_csv(value: str | None) -> list[str] | None:
    if value is None:
        return None
    return list(dict.fromkeys(item.strip().lower() for item in value.split(",") if item.strip()))


def derive_namespace(solution: str) -> str:
    parts = [re.sub(r"[^A-Za-z0-9_]", "", part) for part in solution.split(".")]
    return "".join(parts[:2]) or "Application"


def validate_selection_graph(selections: dict[str, list[str]], overlays: dict[str, Overlay]) -> list[Overlay]:
    requested = {item for values in selections.values() for item in values}
    for axis, values in selections.items():
        for overlay_id in values:
            overlay = overlays.get(overlay_id)
            if overlay is None or overlay.axis != axis:
                valid = sorted(item.id for item in overlays.values() if item.axis == axis)
                raise ScaffoldError(
                    "unknown-overlay",
                    f"Unknown {axis} overlay '{overlay_id}'",
                    axis=axis,
                    valid=valid,
                )
    for overlay_id in requested:
        overlay = overlays[overlay_id]
        unknown_edges = sorted((set(overlay.requires) | set(overlay.conflicts)) - set(overlays))
        if unknown_edges:
            raise ScaffoldError(
                "invalid-overlay-manifest",
                f"Overlay '{overlay_id}' references unknown ids: {', '.join(unknown_edges)}",
            )
        missing = sorted(set(overlay.requires) - requested)
        if missing:
            raise ScaffoldError(
                "missing-required-overlay",
                f"Overlay '{overlay_id}' requires: {', '.join(missing)}",
                selected=sorted(requested),
            )
        conflicts = sorted(set(overlay.conflicts) & requested)
        if conflicts:
            raise ScaffoldError(
                "conflicting-overlays",
                f"Overlay '{overlay_id}' conflicts with: {', '.join(conflicts)}",
                selected=sorted(requested),
            )

    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(overlay_id: str) -> None:
        if overlay_id in visiting:
            raise ScaffoldError("overlay-cycle", f"Overlay requires graph contains a cycle at '{overlay_id}'")
        if overlay_id in visited:
            return
        visiting.add(overlay_id)
        for required in overlays[overlay_id].requires:
            visit(required)
        visiting.remove(overlay_id)
        visited.add(overlay_id)

    for overlay_id in requested:
        visit(overlay_id)

    return [
        overlays[overlay_id]
        for axis in AXES
        for overlay_id in sorted(selections[axis])
    ]


def validate_sdk(framework: str) -> None:
    match = FRAMEWORK.fullmatch(framework)
    if not match:
        raise ScaffoldError("invalid-framework", f"Framework must match netN.0, got '{framework}'")
    if framework not in SUPPORTED_FRAMEWORKS:
        raise ScaffoldError(
            "unsupported-framework",
            f"The bundled package set does not support {framework}",
            valid=list(SUPPORTED_FRAMEWORKS),
        )
    try:
        result = subprocess.run(
            ["dotnet", "--list-sdks"],
            capture_output=True,
            text=True,
            check=False,
            timeout=30,
        )
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        raise ScaffoldError("dotnet-unavailable", "The dotnet SDK is not available on PATH", exit_code=5) from exc
    major = match.group("major")
    if result.returncode != 0 or not any(line.startswith(f"{major}.") for line in result.stdout.splitlines()):
        raise ScaffoldError(
            "sdk-not-installed",
            f"No installed .NET SDK can target {framework}",
            exit_code=5,
            installed=result.stdout.splitlines(),
        )


def render_template(content: str, variables: dict[str, str], source: str) -> str:
    unknown = sorted(set(TEMPLATE_TOKEN.findall(content)) - set(variables))
    if unknown:
        raise ScaffoldError(
            "unknown-template-token",
            f"Unknown template token(s) in {source}: {', '.join(unknown)}",
            exit_code=5,
        )
    for key, value in variables.items():
        content = content.replace(f"{{{{{key}}}}}", value)
    return content.replace("\r\n", "\n")


def write_text(root: Path, relative: str, content: str) -> None:
    target = root / PurePosixPath(relative)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content.rstrip() + "\n", encoding="utf-8", newline="\n")


def project_by_name(architecture: Architecture, name: str) -> ProjectSpec:
    return next(project for project in architecture.projects if project.name == name)


def project_reference(from_project: ProjectSpec, to_project: ProjectSpec) -> str:
    source = Path(from_project.path)
    target = Path(to_project.path) / f"{to_project.name}.csproj"
    relative = os.path.relpath(target, source).replace("/", "\\")
    return relative


def marker_lines(project_name: str) -> str:
    marker_project = project_name.lower().replace(".", "-")
    return "\n".join(
        f"  <!-- <araia:packageref-{marker_project}-{axis}></araia:packageref-{marker_project}-{axis}> -->"
        for axis in AXES
    )


def render_csproj(project: ProjectSpec, architecture: Architecture, namespace: str, framework: str, secret_id: str) -> str:
    project_namespace = f"{namespace}.{project.namespace_suffix or project.name}"
    properties = [
        f"    <TargetFramework>{framework}</TargetFramework>",
        f"    <RootNamespace>{project_namespace}</RootNamespace>",
        f"    <AssemblyName>{project_namespace}</AssemblyName>",
    ]
    if project.role == "host":
        properties.extend(("    <ServerGarbageCollection>true</ServerGarbageCollection>", f"    <UserSecretsId>{secret_id}</UserSecretsId>"))
    if project.role == "performance-benchmarks":
        properties.append("    <OutputType>Exe</OutputType>")
    if project.role.endswith("tests"):
        properties.extend(("    <IsPackable>false</IsPackable>", "    <IsTestProject>true</IsTestProject>"))

    blocks: list[str] = []
    if project.references:
        refs = "\n".join(
            f'    <ProjectReference Include="{project_reference(project, project_by_name(architecture, target))}" />'
            for target in project.references
        )
        blocks.append(f"  <ItemGroup Label=\"Project References\">\n{refs}\n  </ItemGroup>")

    usings = implicit_using_items(project)
    if usings:
        blocks.append(usings)

    if project.is_production:
        if project.packages:
            references = "\n".join(f'    <PackageReference Include="{package}" />' for package in project.packages)
            blocks.append(f'  <ItemGroup Label="Project Packages">\n{references}\n  </ItemGroup>')
        visibility = "\n".join(
            f'    <InternalsVisibleTo Include="{namespace}.{candidate.namespace_suffix or candidate.name}" />'
            for candidate in architecture.projects
            if candidate.role.endswith("tests")
        )
        blocks.append(
            f'  <ItemGroup Label="Test Visibility">\n{visibility}\n'
            '    <InternalsVisibleTo Include="DynamicProxyGenAssembly2" />\n'
            "  </ItemGroup>"
        )
    if project.role.endswith("tests"):
        test_references = [
            "Microsoft.NET.Test.Sdk",
            "xunit",
            "xunit.runner.visualstudio",
            "Shouldly",
            "coverlet.collector",
        ]
        if project.role == "integration-tests":
            test_references.insert(1, "Microsoft.AspNetCore.Mvc.Testing")
        if project.role == "unit-tests":
            test_references.extend(("NSubstitute", "NSubstitute.Analyzers.CSharp", "Bogus"))
        if project.role in ("arch-tests", "security-arch-tests"):
            test_references.append("NetArchTest.eNhancedEdition")
        refs = "\n".join(f'    <PackageReference Include="{package}" />' for package in test_references)
        blocks.append(f"  <ItemGroup Label=\"Test Framework\">\n{refs}\n  </ItemGroup>")
    elif project.role == "performance-benchmarks":
        blocks.append(
            "  <ItemGroup Label=\"Performance Framework\">\n"
            "    <PackageReference Include=\"BenchmarkDotNet\" />\n"
            "  </ItemGroup>"
        )

    if project.name in (architecture.host_name, architecture.infrastructure_name):
        blocks.append(marker_lines(project.name))

    body = "\n\n".join(blocks)
    return (
        f'<Project Sdk="{project.sdk}">\n\n'
        "  <PropertyGroup>\n"
        + "\n".join(properties)
        + "\n  </PropertyGroup>\n\n"
        + body
        + "\n\n</Project>\n"
    )


def base_program(config: ScaffoldConfig) -> str:
    composition_usings = "\n".join(
        f"using {value};"
        for value in dict.fromkeys(
            (config.surface_namespace("composition"), config.surface_namespace("endpoint-composition"))
        )
    )
    return f"""using Microsoft.AspNetCore.Authorization;
{composition_usings}
// <araia:usings-transports></araia:usings-transports>

var builder = WebApplication.CreateBuilder(args);

// <araia:di-base>
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHealthChecks();
builder.Services.AddAuthentication();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddRateLimiter(options =>
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
builder.Services.AddModules(builder.Configuration, SolutionAssemblies.All);
// </araia:di-base>

// <araia:di-transports></araia:di-transports>

var app = builder.Build();

// <araia:pipeline-base>
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
// </araia:pipeline-base>

// <araia:pipeline-transports></araia:pipeline-transports>

// <araia:endpoints>
app.MapModuleEndpoints(SolutionAssemblies.All);
// </araia:endpoints>

await app.RunAsync();

public partial class Program;
"""


def appsettings() -> dict[str, Any]:
    return {
        "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning"}},
        "AllowedHosts": "*",
    }


def generic_readme(config: ScaffoldConfig) -> str:
    arch = config.architecture
    project_rows = "\n".join(f"| `{project.path}/` | {project.role.replace('-', ' ')} |" for project in arch.projects)
    module_note = (
        f"The initial bounded context is `{config.module}`. Its context home is "
        f"`{config.surface_path('module-context')}/`."
        if config.module
        else "No bounded context was invented. Add a module only after its boundary is supported by domain discovery."
    )
    return f"""# {config.solution}

{arch.title} .NET foundation generated by `dotnet-scaffold`.

## Architecture

| Path | Responsibility |
|---|---|
{project_rows}

{module_note}

Each bounded context owns its domain model, its vertical slices, its versioned public contracts, and its technology. Keep a feature's command, validator, handler, source-generated logger, response, and transport mapping together. Domain code must not depend on infrastructure, and one context must never reach into another context's infrastructure. `docs/architecture/standards/{arch.id}-architecture.md` records the enforced dependency rules for this topology.

Interfaces represent demonstrated external boundaries. Repositories and domain policies remain concrete unless observed pain justifies an abstraction. When cross-context asynchronous delivery is implemented, use distinct, versioned Integration Event contracts and add a transactional outbox plus an idempotent inbox before relying on delivery guarantees; those mechanisms are not generated by this scaffold.

## Run

```bash
dotnet run --project {arch.host_path}
```

## Test

```bash
dotnet test
```

Commit `packages.lock.json` after the first verified restore. The generator performs a locked restore, builds with warnings as errors, and runs the deterministic tests locally. Add equivalent CI gates and package vulnerability/deprecation checks before production promotion; this scaffold does not create a CI workflow.
"""


def dockerfile(config: ScaffoldConfig) -> str:
    arch = config.architecture
    major = FRAMEWORK.fullmatch(config.framework).group("major")  # type: ignore[union-attr]
    copy_lines = "\n".join(
        f'COPY ["{project.path}/{project.name}.csproj", "{project.path}/"]' for project in arch.production_projects
    )
    return f"""ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:{major}.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:{major}.0

FROM ${{RUNTIME_IMAGE}} AS base
WORKDIR /app
EXPOSE 8080

FROM ${{SDK_IMAGE}} AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["global.json", "."]
{copy_lines}
RUN dotnet restore "{arch.host_path}/{arch.host_name}.csproj"
COPY . .
WORKDIR "/src/{arch.host_path}"
RUN dotnet publish "{arch.host_name}.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "{config.namespace}.{arch.host_project.namespace_suffix or arch.host_name}.dll"]
"""


def composition_sources(config: ScaffoldConfig) -> dict[str, str]:
    namespace = config.surface_namespace("composition")
    return {
        "IModule.cs": f"""namespace {namespace};

/// <summary>Service registration contract for one bounded context.</summary>
public interface IModule
{{
    static abstract void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}}
""",
        "ModuleRegistrationExtensions.cs": f"""using System.Reflection;

namespace {namespace};

public static class ModuleRegistrationExtensions
{{
    private const string ConfigureServicesMethod = "ConfigureServices";

    public static IServiceCollection AddModules(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] assemblies)
    {{
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var module in DiscoverImplementations(typeof(IModule), assemblies))
        {{
            var method = module.GetMethod(
                ConfigureServicesMethod,
                BindingFlags.Public | BindingFlags.Static);

            method?.Invoke(null, [services, configuration]);
        }}

        return services;
    }}

    public static IEnumerable<Type> DiscoverImplementations(Type contract, Assembly[] assemblies)
        => assemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is {{ IsAbstract: false, IsInterface: false }}
                && type.GetInterfaces().Contains(contract))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);
}}
""",
    }


def endpoint_composition_sources(config: ScaffoldConfig) -> dict[str, str]:
    namespace = config.surface_namespace("endpoint-composition")
    composition = config.surface_namespace("composition")
    composition_using = "" if composition == namespace else f"using {composition};\n"
    markers = ",\n        ".join(
        f"typeof({config.project_namespace(project)}.AssemblyMarker).Assembly"
        for project in config.architecture.production_projects
    )
    return {
        "IEndpointModule.cs": f"""namespace {namespace};

/// <summary>Endpoint mapping contract for one bounded context.</summary>
public interface IEndpointModule
{{
    static abstract void MapEndpoints(IEndpointRouteBuilder app);
}}
""",
        "EndpointModuleExtensions.cs": f"""using System.Reflection;
{composition_using}
namespace {namespace};

public static class EndpointModuleExtensions
{{
    private const string MapEndpointsMethod = "MapEndpoints";

    public static IEndpointRouteBuilder MapModuleEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {{
        ArgumentNullException.ThrowIfNull(app);

        foreach (var module in ModuleRegistrationExtensions.DiscoverImplementations(typeof(IEndpointModule), assemblies))
        {{
            var method = module.GetMethod(
                MapEndpointsMethod,
                BindingFlags.Public | BindingFlags.Static);

            method?.Invoke(null, [app]);
        }}

        return app;
    }}
}}
""",
        "SolutionAssemblies.cs": f"""using System.Reflection;

namespace {namespace};

/// <summary>Production assemblies scanned for module and endpoint registrations.</summary>
public static class SolutionAssemblies
{{
    public static Assembly[] All {{ get; }} =
    [
        {markers},
    ];
}}
""",
    }


def shared_kernel_sources(namespace: str) -> dict[str, str]:
    return {
        "DomainEvent.cs": f"""namespace {namespace};

public abstract record DomainEvent(DateTimeOffset OccurredAt);
""",
        "PersonalDataAttribute.cs": f"""namespace {namespace};

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class PersonalDataAttribute : Attribute;
""",
        "Result.cs": f"""namespace {namespace};

public enum ResultErrorKind
{{
    None = 0,
    Validation = 1,
    BusinessRule = 2,
    Integration = 3,
    NotFound = 4,
    Forbidden = 5,
}}

public readonly record struct Result(bool IsSuccess, ResultErrorKind ErrorKind, string? Error)
{{
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(true, ResultErrorKind.None, null);

    public static Result<T> Success<T>(T value) => new(true, value, ResultErrorKind.None, null);

    public static Result<T> ValidationError<T>(string error)
        => Failure<T>(ResultErrorKind.Validation, error);

    public static Result BusinessRuleViolation(string error)
        => Failure(ResultErrorKind.BusinessRule, error);

    public static Result<T> BusinessRuleViolation<T>(string error)
        => Failure<T>(ResultErrorKind.BusinessRule, error);

    public static Result<T> IntegrationFailure<T>(string error)
        => Failure<T>(ResultErrorKind.Integration, error);

    public static Result<T> NotFound<T>(string error)
        => Failure<T>(ResultErrorKind.NotFound, error);

    public static Result<T> Forbidden<T>(string error)
        => Failure<T>(ResultErrorKind.Forbidden, error);

    private static Result Failure(ResultErrorKind kind, string error)
    {{
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new Result(false, kind, error);
    }}

    private static Result<T> Failure<T>(ResultErrorKind kind, string error)
    {{
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new Result<T>(false, default, kind, error);
    }}
}}

public readonly record struct Result<T>(bool IsSuccess, T? Value, ResultErrorKind ErrorKind, string? Error)
{{
    public bool IsFailure => !IsSuccess;

    public TResult Match<TResult>(
        Func<T, TResult> onSuccess,
        Func<string, TResult> onValidationError,
        Func<string, TResult> onBusinessRuleViolation,
        Func<string, TResult> onIntegrationFailure,
        Func<string, TResult> onNotFound,
        Func<string, TResult> onForbidden)
    {{
        if (IsSuccess)
        {{
            return onSuccess(Value!);
        }}

        string error = Error ?? "An unspecified failure occurred.";
        return ErrorKind switch
        {{
            ResultErrorKind.Validation => onValidationError(error),
            ResultErrorKind.BusinessRule => onBusinessRuleViolation(error),
            ResultErrorKind.Integration => onIntegrationFailure(error),
            ResultErrorKind.NotFound => onNotFound(error),
            ResultErrorKind.Forbidden => onForbidden(error),
            _ => throw new InvalidOperationException($"Unsupported result error kind: {{ErrorKind}}."),
        }};
    }}

}}
""",
    }


def module_registration_sources(config: ScaffoldConfig) -> dict[str, str]:
    """Render the module registration files, merging keys that share one path."""
    assert config.module is not None
    grouped: dict[str, list[str]] = {}
    for key in REGISTRATION_KEYS:
        grouped.setdefault(config.registration_path(key), []).append(key)

    composition = config.surface_namespace("composition")
    endpoint_composition = config.surface_namespace("endpoint-composition")
    sources: dict[str, str] = {}
    for path, keys in grouped.items():
        registration = config.registration(keys[0])
        namespace = config.registration_namespace(keys[0])
        type_name = config.resolve(registration.type_name)
        contracts = [name for key, name in (("application", "IModule"), ("infrastructure", "IModule"), ("endpoints", "IEndpointModule")) if key in keys]
        contracts = list(dict.fromkeys(contracts))
        contract_namespaces = []
        if "IModule" in contracts:
            contract_namespaces.append(composition)
        if "IEndpointModule" in contracts:
            contract_namespaces.append(endpoint_composition)
        using_block = "\n".join(
            f"using {value};" for value in dict.fromkeys(contract_namespaces) if value != namespace
        )

        marker_usings = "\n".join(
            f"// <araia:{marker}></araia:{marker}>"
            for key in keys
            for marker in REGISTRATION_MARKERS[key]
            if marker.startswith("usings-")
        )
        members: list[str] = []
        if "IModule" in contracts:
            body = "\n".join(
                f"        // <araia:{marker}></araia:{marker}>"
                for key in keys
                for marker in REGISTRATION_MARKERS[key]
                if marker.startswith("di-")
            )
            members.append(
                "    public static void ConfigureServices(\n"
                "        IServiceCollection services,\n"
                "        IConfiguration configuration)\n"
                "    {\n"
                f"{body}\n"
                "    }"
            )
        if "IEndpointModule" in contracts:
            members.append(
                "    public static void MapEndpoints(IEndpointRouteBuilder app)\n"
                "    {\n"
                "        // <araia:module-endpoints></araia:module-endpoints>\n"
                "    }"
            )

        sources[path] = (
            f"{using_block}\n"
            f"{marker_usings}\n\n"
            f"namespace {namespace};\n\n"
            f"public sealed class {type_name} : {', '.join(contracts)}\n"
            "{\n"
            + "\n\n".join(members)
            + "\n}\n"
        )
    return sources


def module_surface_rows(config: ScaffoldConfig) -> str:
    labels = (
        ("domain", "aggregates, value objects, domain policies, internal Domain Events"),
        ("features", "vertical slices for this context"),
        ("integration", "versioned public Integration Event contracts"),
        ("ports", "domain-facing contracts this context owns"),
        ("module-infrastructure", "persistence, providers, broker, cache, outbox, inbox"),
    )
    rows = [
        f"| `{config.surface_path(key)}/` | {description} |"
        for key, description in labels
        if key in config.architecture.surfaces
    ]
    descriptions = {
        "application": "application service registration for this context",
        "infrastructure": "technology registration for this context",
        "endpoints": "endpoint mapping for this context",
    }
    seen: dict[str, list[str]] = {}
    for key in REGISTRATION_KEYS:
        seen.setdefault(config.registration_path(key), []).append(descriptions[key])
    rows.extend(f"| `{path}` | {'; '.join(values)} |" for path, values in seen.items())
    return "\n".join(rows)


def module_agents(config: ScaffoldConfig) -> str:
    assert config.module is not None
    return f"""# {config.module} module

## Boundary

- Keep one bounded context in this module. Its name comes from domain discovery, never from a table, screen, or technical layer.
- Keep invariants in the aggregates and value objects under `{config.surface_path("domain")}/`.
- Keep use-case orchestration in the slices under `{config.surface_path("features")}/`.
- Do not read or write another context's data store, infrastructure types, or mutable domain types.
- Publish cross-context facts as distinct, versioned contracts under `{config.surface_path("integration")}/`.

## Owned surfaces

| Path | Responsibility |
|---|---|
{module_surface_rows(config)}

## Implementation

- Organize new use cases as vertical slices; keep one use case's input, structural validation, orchestration, response, logging, and transport mapping together.
- Keep commands primitive at the transport boundary and rebuild value objects before invoking domain behavior.
- Keep repositories and domain policies concrete unless an observed boundary or test seam justifies an interface.
- Return `Result<T>` for expected outcomes; reserve exceptions for unexpected system failures.
- Raise a Domain Event when behavior inside this context reacts to a fact. Map it to a versioned Integration Event only when another context consumes it.

## Security and tests

- Require named authorization and rate-limiting policies on state-changing endpoints.
- Never bind HTTP bodies directly to domain types.
- Do not log personal data, financial values, tokens, secrets, or connection strings.
- Start with a failing behavior test; add unit tests for aggregate invariants and Domain Events.

Update this file in the same change that alters the module boundary, public contracts, ubiquitous language, or non-negotiable security rules.
"""


def architecture_standard(config: ScaffoldConfig) -> str:
    architecture = config.architecture
    project_rows = "\n".join(
        f"| `{project.path}/` | {project.role.replace('-', ' ')} |" for project in architecture.projects
    )
    rule_rows = "\n".join(
        f"| `{rule.id}` | {rule.scope} | {', '.join(rule.forbid_namespaces + rule.forbid_packages)} |"
        for rule in architecture.rules
    )
    return f"""# {architecture.title}

This solution organizes code by business capability first and technology second. Bounded contexts come from domain discovery, and a vertical slice is the unit of change inside a context.

## Foundation

| Path | Responsibility |
|---|---|
{project_rows}

- Keep one bounded context per module folder under `{architecture.features_root}/`.
- Discover module boundaries from domain evidence; do not infer them from technical layers, tables, or screens.
- Keep the shared kernel limited to true universals and enforce its size with an architecture test.
- Keep architecture, security, unit, integration, and performance validation separated by purpose under `tests/`.

## Module contract

Each bounded context owns its domain model, its slices, its versioned public contracts, and its technology. Domain Events stay internal to the producing context. Integration Events are distinct, immutable, versioned public contracts. Cross-context asynchronous delivery uses an outbox in the producer's store and an idempotent inbox at the consumer.

## Change contract

Keep a slice's request, structural validation, handler, source-generated logger, response, and transport mapping together. Put invariants in aggregates or value objects. Introduce repositories, specifications, policies, ports, CQRS projections, or separate services only in response to demonstrated complexity or operational pressure.

## Enforced dependency rules

| Rule | Scope | Forbidden |
|---|---|---|
{rule_rows}

`{architecture.host_name}` is the composition root. Cross-context isolation, the error axis, and the shared-kernel budget are enforced by the same architecture test project.

## Guardrails

The default posture is fail-closed: authenticated fallback authorization, explicit anonymous exceptions, rate limiting for state-changing or upstream-cost endpoints, sanitized problem details, no domain binding from request bodies, no interpolated raw SQL, and no personal data in logs.

Runtime performance is not inferred from folder structure. Establish endpoint and workload baselines before setting latency, throughput, allocation, garbage-collection, eventual-consistency, or AI-runtime thresholds.
"""


def integration_settings(config: ScaffoldConfig) -> list[str]:
    if not config.module:
        return []
    prefix = f"Modules:{config.module}"
    values: dict[str, str] = {}
    if "ef" in config.selections["persistence"]:
        values[f"{prefix}:Persistence:Ef:ConnectionString"] = "Host=localhost;Database=integration_tests;Username=test"
    if "dapper" in config.selections["persistence"]:
        values[f"{prefix}:Persistence:Dapper:ConnectionString"] = "Host=localhost;Database=integration_tests;Username=test"
    if "mongo" in config.selections["persistence"]:
        values[f"{prefix}:Persistence:Mongo:ConnectionString"] = "mongodb://localhost:27017"
        values[f"{prefix}:Persistence:Mongo:DatabaseName"] = "integration_tests"
    if "rabbitmq" in config.selections["messaging"]:
        values[f"{prefix}:Messaging:RabbitMq:Host"] = "localhost"
        values[f"{prefix}:Messaging:RabbitMq:Username"] = "integration-test"
        values[f"{prefix}:Messaging:RabbitMq:Password"] = "integration-test"
    if "kafka" in config.selections["messaging"]:
        values[f"{prefix}:Messaging:Kafka:BootstrapServers"] = "localhost:9092"
        values[f"{prefix}:Messaging:Kafka:GroupId"] = "integration-tests"
    if "redis" in config.selections["cache"]:
        values[f"{prefix}:Cache:Redis:ConnectionString"] = "localhost:6379"
        values[f"{prefix}:Cache:Redis:InstanceName"] = "integration-tests:"
    return [f'            ["{key}"] = "{value}",' for key, value in sorted(values.items())]


def project_by_role(config: ScaffoldConfig, role: str) -> ProjectSpec:
    return next(project for project in config.architecture.projects if project.role == role)


def module_namespace_roots(config: ScaffoldConfig) -> list[str]:
    """Namespace prefixes that a module name follows, one per module-scoped surface family."""
    templates = [
        value.namespace.split(MODULE_TOKEN)[0]
        for value in (*config.architecture.surfaces.values(), *config.architecture.registrations.values())
        if MODULE_TOKEN in value.namespace
    ]
    return sorted({f"{config.namespace}.{value}" for value in templates})


def scope_pattern(config: ScaffoldConfig, scope: str) -> str:
    """Translate a manifest namespace scope into an anchored regular expression."""
    escaped = re.escape(f"{config.namespace}.{scope}").replace(re.escape("*"), "[^.]+")
    return f"^{escaped}(\\.|$)"


def rule_test_name(rule_id: str) -> str:
    return rule_id.replace("-", "_").capitalize()


def dependency_rule_tests(config: ScaffoldConfig) -> str:
    blocks: list[str] = []
    for rule in config.architecture.rules:
        forbidden = [f"{config.namespace}.{value}" for value in rule.forbid_namespaces] + list(rule.forbid_packages)
        arguments = ",\n                ".join(f'"{value}"' for value in forbidden)
        blocks.append(
            "    [Fact]\n"
            f"    public void {rule_test_name(rule.id)}()\n"
            "    {\n"
            "        TestResult result = Types\n"
            "            .InAssemblies(Production)\n"
            "            .That()\n"
            f'            .ResideInNamespaceMatching(@"{scope_pattern(config, rule.scope)}")\n'
            "            .ShouldNot()\n"
            "            .HaveDependencyOnAny(\n"
            f"                {arguments})\n"
            "            .GetResult();\n"
            "\n"
            "        result.IsSuccessful.ShouldBeTrue();\n"
            "    }"
        )
    return "\n\n".join(blocks)


def architecture_tests_source(config: ScaffoldConfig) -> str:
    endpoint_composition = config.surface_namespace("endpoint-composition")
    shared_kernel = config.surface_namespace("shared-kernel")
    roots = ",\n        ".join(f'"{value}"' for value in module_namespace_roots(config))
    return f"""using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using {endpoint_composition};
using {shared_kernel};

namespace {config.project_namespace(project_by_role(config, "arch-tests"))};

public sealed class ArchitectureTests
{{
    private static readonly Assembly[] Production = SolutionAssemblies.All;

    private static readonly string[] ModuleNamespaceRoots =
    [
        {roots},
    ];

{dependency_rule_tests(config)}

    [Fact]
    public void Bounded_contexts_must_not_depend_on_each_other()
    {{
        string[] modules = DiscoveredModules();

        foreach (string module in modules)
        {{
            string[] forbidden = modules
                .Where(other => !other.Equals(module, StringComparison.Ordinal))
                .SelectMany(other => ModuleNamespaceRoots.Select(root => root + other))
                .ToArray();

            if (forbidden.Length == 0)
            {{
                continue;
            }}

            foreach (string root in ModuleNamespaceRoots)
            {{
                TestResult result = Types
                    .InAssemblies(Production)
                    .That()
                    .ResideInNamespaceMatching($@"^{{Regex.Escape(root + module)}}(\\.|$)")
                    .ShouldNot()
                    .HaveDependencyOnAny(forbidden)
                    .GetResult();

                result.IsSuccessful.ShouldBeTrue();
            }}
        }}
    }}

    [Fact]
    public void Handlers_must_use_the_declared_result_axis()
    {{
        MethodInfo[] handlers = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Name == "Handler")
            .Select(type => type.GetMethod(
                "HandleAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .OfType<MethodInfo>()
            .ToArray();

        string[] invalid = handlers
            .Where(method => !ReturnsResult(method.ReturnType))
            .Select(method => $"{{method.DeclaringType?.FullName}}.{{method.Name}}")
            .ToArray();

        invalid.ShouldBeEmpty();
    }}

    [Fact]
    public void Shared_kernel_must_remain_small()
    {{
        int publicTypeCount = typeof(Result).Assembly
            .GetExportedTypes()
            .Count(type => type.Namespace == "{shared_kernel}");

        publicTypeCount.ShouldBeLessThanOrEqualTo(12);
    }}

    private static string[] DiscoveredModules()
        => Production
            .SelectMany(assembly => assembly.GetTypes())
            .Select(type => type.Namespace)
            .OfType<string>()
            .SelectMany(value => ModuleNamespaceRoots
                .Where(root => value.StartsWith(root, StringComparison.Ordinal))
                .Select(root => value[root.Length..].Split('.')[0]))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool ReturnsResult(Type returnType)
    {{
        Type candidate = returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>)
                ? returnType.GetGenericArguments()[0]
                : returnType;

        return candidate == typeof(Result)
            || candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == typeof(Result<>);
    }}
}}
"""


def domain_namespace_regex(config: ScaffoldConfig) -> str:
    """Match every bounded context's domain namespace in the selected topology."""
    template = f"{config.namespace}.{config.surface('domain').namespace}"
    escaped = re.escape(template).replace(re.escape(MODULE_TOKEN), "[^.]+")
    return f"^{escaped}(\\.|$)"


def security_tests_source(config: ScaffoldConfig) -> str:
    shared_kernel = config.surface_namespace("shared-kernel")
    endpoint_composition = config.surface_namespace("endpoint-composition")
    domain_namespace_pattern = domain_namespace_regex(config)
    roots = ",\n        ".join(f'"{project.path}"' for project in config.architecture.production_projects)
    return f"""using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using {endpoint_composition};
using {shared_kernel};

namespace {config.project_namespace(project_by_role(config, "security-arch-tests"))};

public sealed partial class SecurityArchitectureTests
{{
    private static readonly Assembly[] Production = SolutionAssemblies.All;

    private static readonly string[] ProductionRoots =
    [
        {roots},
    ];

    [Fact]
    public void Source_must_not_use_known_dangerous_apis()
    {{
        string[] findings = SourceFiles()
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => DangerousApi().IsMatch(item.line))
            .Select(item => $"{{item.path}}:{{item.number}}")
            .ToArray();

        findings.ShouldBeEmpty();
    }}

    [Fact]
    public void State_changing_endpoints_must_declare_authorization_and_rate_limiting()
    {{
        string[] findings = SourceFiles()
            .SelectMany(path => Regex.Split(File.ReadAllText(path), @";\\s*")
                .Where(statement => StateChangingEndpoint().IsMatch(statement))
                .Where(statement => !statement.Contains("RequireAuthorization", StringComparison.Ordinal)
                    || !statement.Contains("RequireRateLimiting", StringComparison.Ordinal))
                .Select(_ => path))
            .ToArray();

        findings.ShouldBeEmpty();
    }}

    [Fact]
    public void Endpoint_inputs_must_not_bind_domain_types()
    {{
        string[] domainTypeNames = Production
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace is string value && DomainNamespace().IsMatch(value))
            .Select(type => type.Name.Split('`')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] findings = SourceFiles()
            .SelectMany(path => Regex.Split(File.ReadAllText(path), @";\\s*")
                .Where(statement => EndpointRegistration().IsMatch(statement))
                .Where(statement => domainTypeNames.Any(name =>
                    Regex.IsMatch(statement, $@"\\b{{Regex.Escape(name)}}\\b")))
                .Select(_ => path))
            .ToArray();

        findings.ShouldBeEmpty();
    }}

    [Fact]
    public void Personal_data_names_must_not_appear_in_logger_templates()
    {{
        string[] personalNames = Production
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(property => property.IsDefined(typeof(PersonalDataAttribute), inherit: true))
            .Select(property => property.Name)
            .Concat(Production
                .SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.IsDefined(typeof(PersonalDataAttribute), inherit: true))
                .Select(parameter => parameter.Name)
                .OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] messages = Production
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(method => method.GetCustomAttribute<LoggerMessageAttribute>()?.Message)
            .OfType<string>()
            .ToArray();

        string[] findings = personalNames
            .Where(name => messages.Any(message =>
                message.Contains("{{" + name + "}}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        findings.ShouldBeEmpty();
    }}

    [Fact]
    public void Security_paths_must_not_use_pseudo_random_generators()
    {{
        string[] findings = SourceFiles()
            .Where(path => SecurityPath().IsMatch(path))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, number: index + 1)))
            .Where(item => PseudoRandom().IsMatch(item.line))
            .Select(item => $"{{item.path}}:{{item.number}}")
            .ToArray();

        findings.ShouldBeEmpty();
    }}

    private static IEnumerable<string> SourceFiles()
        => ProductionRoots
            .Select(relative => Path.Combine(
                FindSolutionRoot(),
                relative.Replace('/', Path.DirectorySeparatorChar)))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !BuildOutput().IsMatch(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string FindSolutionRoot()
    {{
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {{
            directory = directory.Parent;
        }}

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the solution root.");
    }}

    [GeneratedRegex(@"FromSqlRaw\\s*\\(\\s*\\$|ExecuteSqlRaw\\s*\\(\\s*\\$|BinaryFormatter|SoapFormatter|NetDataContractSerializer")]
    private static partial Regex DangerousApi();

    [GeneratedRegex(@"\\bMap(Post|Put|Patch|Delete)\\s*\\(")]
    private static partial Regex StateChangingEndpoint();

    [GeneratedRegex(@"\\bMap(Get|Post|Put|Patch|Delete|Methods)\\s*\\(")]
    private static partial Regex EndpointRegistration();

    [GeneratedRegex(@"{domain_namespace_pattern}")]
    private static partial Regex DomainNamespace();

    [GeneratedRegex(@"[\\\\/](Auth|Authentication|Token|Crypto|Cryptography)(?:[\\\\/]|[^\\\\/]*\\.cs$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityPath();

    [GeneratedRegex(@"[\\\\/](bin|obj)[\\\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutput();

    [GeneratedRegex(@"\\bRandom(?:\\.Shared)?\\b")]
    private static partial Regex PseudoRandom();
}}
"""


def test_sources(config: ScaffoldConfig) -> dict[str, str]:
    settings = "\n".join(integration_settings(config))
    unit = project_by_role(config, "unit-tests")
    integration = project_by_role(config, "integration-tests")
    arch = project_by_role(config, "arch-tests")
    security = project_by_role(config, "security-arch-tests")
    performance = project_by_role(config, "performance-benchmarks")
    shared_kernel = config.surface_namespace("shared-kernel")

    integration_factory = f"""using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace {config.project_namespace(integration)};

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {{
        builder.ConfigureAppConfiguration((_, configuration) =>
        {{
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {{
{settings}
            }});
        }});
    }}
}}
"""
    return {
        f"{unit.path}/SharedKernelTests.cs": f"""using {shared_kernel};

namespace {config.project_namespace(unit)};

public sealed class SharedKernelTests
{{
    [Fact]
    public void Validation_error_preserves_the_expected_error_axis()
    {{
        Result<int> result = Result.ValidationError<int>("invalid input");

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }}
}}
""",
        f"{integration.path}/TestApplicationFactory.cs": integration_factory,
        f"{integration.path}/HealthCheckSmokeTests.cs": f"""namespace {config.project_namespace(integration)};

public sealed class HealthCheckSmokeTests(TestApplicationFactory factory)
    : IClassFixture<TestApplicationFactory>
{{
    [Fact]
    public async Task Health_endpoint_returns_success()
    {{
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
    }}
}}
""",
        f"{arch.path}/ArchitectureTests.cs": architecture_tests_source(config),
        f"{security.path}/SecurityArchitectureTests.cs": security_tests_source(config),
        f"{performance.path}/Program.cs": """Console.WriteLine("Add measured BenchmarkDotNet baselines for identified hot paths.");
""",
    }


IMPLICIT_USINGS_BY_ROLE = {
    "application": (
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging",
    ),
    "infrastructure": (
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging",
    ),
    "integration-tests": ("Microsoft.AspNetCore.Mvc.Testing", "Shouldly", "Xunit"),
    "unit-tests": ("Shouldly", "Xunit"),
    "arch-tests": ("Shouldly", "Xunit"),
    "security-arch-tests": ("Shouldly", "Xunit"),
}


def implicit_using_items(project: ProjectSpec) -> str:
    """Extend the SDK implicit usings so an unused entry never trips IDE0005."""
    namespaces = IMPLICIT_USINGS_BY_ROLE.get(project.role, ())
    if not namespaces:
        return ""
    items = "\n".join(f'    <Using Include="{value}" />' for value in namespaces)
    return f'  <ItemGroup Label="Implicit Usings">\n{items}\n  </ItemGroup>'


def generate_generic_starter(stage: Path, config: ScaffoldConfig, templates_root: Path, variables: dict[str, str]) -> None:
    common = templates_root / "starters" / config.starter.template_set
    for name in COMMON_STARTER_FILES:
        content = render_template((common / name).read_text(encoding="utf-8"), variables, str(common / name))
        write_text(stage, name.removesuffix(".tmpl"), content)

    write_text(stage, "README.md", generic_readme(config))
    write_text(stage, f"{config.architecture.host_path}/Program.cs", base_program(config))
    write_text(stage, f"{config.architecture.host_path}/appsettings.json", json.dumps(appsettings(), indent=2))
    write_text(stage, f"{config.architecture.host_path}/appsettings.Development.json", json.dumps(appsettings(), indent=2))
    write_text(stage, f"{config.architecture.host_path}/Dockerfile", dockerfile(config))
    write_text(
        stage,
        f"{config.architecture.host_path}/Properties/launchSettings.json",
        json.dumps(
            {
                "$schema": "https://json.schemastore.org/launchsettings.json",
                "profiles": {
                    "http": {
                        "commandName": "Project",
                        "dotnetRunMessages": True,
                        "launchBrowser": False,
                        "applicationUrl": "http://localhost:5080",
                        "environmentVariables": {"ASPNETCORE_ENVIRONMENT": "Development"},
                    }
                },
            },
            indent=2,
        ),
    )

    for project in config.architecture.projects:
        write_text(
            stage,
            f"{project.path}/{project.name}.csproj",
            render_csproj(project, config.architecture, config.namespace, config.framework, variables["UserSecretsId"]),
        )
        if project.is_production:
            write_text(
                stage,
                f"{project.path}/AssemblyMarker.cs",
                f"namespace {config.project_namespace(project)};\n\n"
                "/// <summary>Anchors assembly discovery for module composition and architecture tests.</summary>\n"
                "public sealed class AssemblyMarker;\n",
            )

    for name, content in composition_sources(config).items():
        write_text(stage, f"{config.surface_path('composition')}/{name}", content)
    for name, content in endpoint_composition_sources(config).items():
        write_text(stage, f"{config.surface_path('endpoint-composition')}/{name}", content)
    for name, content in shared_kernel_sources(config.surface_namespace("shared-kernel")).items():
        write_text(stage, f"{config.surface_path('shared-kernel')}/{name}", content)

    if config.module:
        for path, content in module_registration_sources(config).items():
            write_text(stage, path, content)
        write_text(stage, f"{config.surface_path('module-context')}/AGENTS.md", module_agents(config))
        write_text(
            stage,
            f"{config.surface_path('module-context')}/hotspots.md",
            f"# {config.module} decision hotspots\n\n"
            "Record only evidence-backed risks, accepted assumptions, scheduled actions, or formally deferred decisions. "
            "Every entry requires its evidence source, owner, status, and review condition. Keep unresolved questions in "
            "the interactive task or an ephemeral discovery inventory.\n",
        )
        for key, surface in config.architecture.surfaces.items():
            if MODULE_TOKEN not in surface.path or key == "module-context":
                continue
            keep = stage / PurePosixPath(config.surface_path(key)) / ".gitkeep"
            keep.parent.mkdir(parents=True, exist_ok=True)
            keep.write_text("", encoding="utf-8")
    else:
        keep = stage / PurePosixPath(config.architecture.features_root) / ".gitkeep"
        keep.parent.mkdir(parents=True, exist_ok=True)
        keep.write_text("", encoding="utf-8")

    write_text(stage, f"docs/architecture/standards/{config.architecture.id}-architecture.md", architecture_standard(config))
    for folder in ("docs/architecture/adrs", "docs/security"):
        keep = stage / PurePosixPath(folder) / ".gitkeep"
        keep.parent.mkdir(parents=True, exist_ok=True)
        keep.write_text("", encoding="utf-8")

    for relative, content in test_sources(config).items():
        write_text(stage, relative, content)


def solution_text(config: ScaffoldConfig) -> str:
    project_type = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"
    projects: list[str] = []
    configs: list[str] = []
    for project in config.architecture.projects:
        path = f"{project.path}/{project.name}.csproj".replace("/", "\\")
        project_id = "{" + str(uuid.uuid5(uuid.NAMESPACE_URL, f"{config.solution}:{path}")).upper() + "}"
        projects.append(f'Project("{project_type}") = "{project.name}", "{path}", "{project_id}"\nEndProject')
        configs.extend(
            (
                f"\t\t{project_id}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
                f"\t\t{project_id}.Debug|Any CPU.Build.0 = Debug|Any CPU",
                f"\t\t{project_id}.Release|Any CPU.ActiveCfg = Release|Any CPU",
                f"\t\t{project_id}.Release|Any CPU.Build.0 = Release|Any CPU",
            )
        )
    return (
        "Microsoft Visual Studio Solution File, Format Version 12.00\n"
        "# Visual Studio Version 17\n"
        "VisualStudioVersion = 17.0.31903.59\n"
        "MinimumVisualStudioVersion = 10.0.40219.1\n"
        + "\n".join(projects)
        + "\nGlobal\n"
        "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n"
        "\t\tDebug|Any CPU = Debug|Any CPU\n"
        "\t\tRelease|Any CPU = Release|Any CPU\n"
        "\tEndGlobalSection\n"
        "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n"
        + "\n".join(configs)
        + "\n\tEndGlobalSection\nEndGlobal\n"
    )


def replace_empty_marker(content: str, marker: str, snippets: Iterable[str], style: str) -> str:
    """Substitute one named marker, re-indenting inserted lines to the marker's column."""
    snippets = [snippet.strip() for snippet in snippets if snippet.strip()]
    if style == "cs":
        token = f"// <araia:{marker}></araia:{marker}>"
        open_tag, close_tag = f"// <araia:{marker}>", f"// </araia:{marker}>"
    else:
        token = f"<!-- <araia:{marker}></araia:{marker}> -->"
        open_tag, close_tag = f"<!-- <araia:{marker}> -->", f"<!-- </araia:{marker}> -->"
    if token not in content:
        raise ScaffoldError("missing-marker", f"Marker '{marker}' not found", exit_code=5)
    if not snippets:
        return content.replace(token, "", 1)

    host_line = next(line for line in content.splitlines() if token in line)
    indent = host_line[: len(host_line) - len(host_line.lstrip())]
    body = "\n".join(
        f"{indent}{value}" if value.strip() else value
        for snippet in snippets
        for value in snippet.splitlines()
    )
    return content.replace(token, f"{open_tag}\n{body}\n{indent}{close_tag}", 1)


def remove_scaffold_markers(content: str) -> str:
    """Remove internal composition markers from a generated artifact."""
    patterns = (
        r"(?m)^[ \t]*//[ \t]*</?araia:[a-z][a-z0-9-]*>(?:</araia:[a-z][a-z0-9-]*>)?[ \t]*(?:\r?\n|$)",
        r"(?m)^[ \t]*<!--[ \t]*</?araia:[a-z][a-z0-9-]*>(?:</araia:[a-z][a-z0-9-]*>)?[ \t]*-->[ \t]*(?:\r?\n|$)",
    )
    for pattern in patterns:
        content = re.sub(pattern, "", content)
    return content


def target_project_for_overlay(architecture: Architecture, logical_project: str, axis: str) -> str:
    """Route overlay packages to the host for transports and to the infrastructure owner otherwise."""
    del logical_project
    return architecture.host_name if axis == "transports" else architecture.infrastructure_name


def map_overlay_destination(
    config: ScaffoldConfig,
    axis: str,
    destination: str,
    variables: dict[str, str],
) -> str:
    del axis
    return render_template(destination, variables, f"overlay destination '{destination}'")


def overlay_namespace(config: ScaffoldConfig, axis: str) -> tuple[str, str]:
    """Return the infrastructure namespace and the owning context namespace for one axis."""
    if axis == "transports":
        return config.surface_namespace("host-infrastructure"), config.project_namespace(
            config.architecture.host_project
        )
    assert config.module is not None
    return config.surface_namespace("module-infrastructure"), config.surface_namespace("module-context")


def map_overlay_content(content: str, config: ScaffoldConfig, axis: str) -> str:
    infrastructure, context = overlay_namespace(config, axis)
    return content.replace(f"{config.namespace}.Services.Infrastructure", infrastructure).replace(
        f"{config.namespace}.Services",
        context,
    )


def map_readme(content: str, config: ScaffoldConfig, axis: str) -> str:
    if axis == "transports":
        replacements = (
            ("src/Services/Infrastructure", config.surface_path("host-infrastructure")),
            ("src/Services", config.architecture.host_path),
        )
    else:
        replacements = (
            ("src/Services/Features", config.surface_path("features")),
            ("src/Services/Infrastructure", config.surface_path("module-infrastructure")),
            ("src/Services", config.surface_path("module-context")),
        )
    for source, target in replacements:
        content = content.replace(source, target)
    return content


def deep_merge(target: dict[str, Any], additions: dict[str, Any], source: str) -> None:
    for key, value in additions.items():
        if key not in target:
            target[key] = value
        elif isinstance(target[key], dict) and isinstance(value, dict):
            deep_merge(target[key], value, source)
        elif target[key] != value:
            raise ScaffoldError("appsettings-conflict", f"Conflicting appsettings key '{key}' from {source}", exit_code=5)


def apply_overlays(stage: Path, config: ScaffoldConfig, overlays: list[Overlay], variables: dict[str, str]) -> None:
    package_versions: dict[str, list[str]] = {axis: [] for axis in AXES}
    project_packages: dict[tuple[str, str], list[str]] = {}
    file_patches: dict[tuple[str, str], list[str]] = {}
    readme_sections: list[str] = []
    settings_additions: dict[str, Any] = {}
    destinations: set[str] = set()

    for overlay in overlays:
        scoped_variables = {**variables, "feature-id": overlay.id}
        for package in overlay.packages:
            package_versions[overlay.axis].append(
                f'  <PackageVersion Include="{package["name"]}" Version="{package["version"]}" />'
            )
        for logical_project, packages in overlay.project_references.items():
            actual_project = target_project_for_overlay(config.architecture, logical_project, overlay.axis)
            project_packages.setdefault((actual_project, overlay.axis), []).extend(packages)
        for file_entry in overlay.files:
            source = overlay.root / file_entry["src"]
            destination = map_overlay_destination(config, overlay.axis, file_entry["dest"], scoped_variables)
            if destination in destinations:
                raise ScaffoldError("overlay-file-collision", f"Multiple overlays emit '{destination}'", exit_code=5)
            destinations.add(destination)
            content = render_template(source.read_text(encoding="utf-8"), scoped_variables, str(source))
            write_text(stage, destination, map_overlay_content(content, config, overlay.axis))
        for patch in overlay.patches:
            snippet_path = overlay.root / patch["snippet-file"]
            snippet = render_template(snippet_path.read_text(encoding="utf-8"), scoped_variables, str(snippet_path))
            if "<araia:" in snippet or "</araia:" in snippet:
                raise ScaffoldError("nested-marker", f"Snippet declares a marker: {snippet_path}", exit_code=5)
            target = map_overlay_destination(config, overlay.axis, patch["file"], scoped_variables)
            file_patches.setdefault((target, patch["marker"]), []).append(map_overlay_content(snippet, config, overlay.axis))
        deep_merge(settings_additions, overlay.appsettings, overlay.id)
        readme_sections.append(map_readme(render_template(overlay.readme, scoped_variables, f"{overlay.id}:readme"), config, overlay.axis))

    packages_file = stage / "Directory.Packages.props"
    packages_content = packages_file.read_text(encoding="utf-8")
    for axis in AXES:
        package_lines = sorted(dict.fromkeys(package_versions[axis]))
        snippet = [] if not package_lines else ["<ItemGroup Label=\"Araia " + axis.title() + "\">\n" + "\n".join(package_lines) + "\n</ItemGroup>"]
        packages_content = replace_empty_marker(packages_content, f"packages-{axis}", snippet, "xml")
    packages_file.write_text(cleanup(remove_scaffold_markers(packages_content)), encoding="utf-8", newline="\n")

    for project in config.architecture.projects:
        if project.name not in (config.architecture.host_name, config.architecture.infrastructure_name):
            continue
        csproj = stage / PurePosixPath(project.path) / f"{project.name}.csproj"
        content = csproj.read_text(encoding="utf-8")
        for axis in AXES:
            refs = sorted(dict.fromkeys(project_packages.get((project.name, axis), [])))
            items = [f'    <PackageReference Include="{package}" />' for package in refs]
            if (
                axis == "persistence"
                and "dapper" in config.selections[axis]
                and project.name == config.architecture.infrastructure_name
            ):
                assert config.module is not None
                queries = PurePosixPath(config.surface_path("module-infrastructure")) / "Persistence" / "Queries"
                relative = queries.relative_to(project.path).as_posix().replace("/", "\\")
                items.append(f'    <EmbeddedResource Include="{relative}\\**\\*.sql" />')
            snippet = [] if not items else [f"  <ItemGroup Label=\"Araia {axis.title()}\">\n" + "\n".join(items) + "\n  </ItemGroup>"]
            marker_project = project.name.lower().replace(".", "-")
            content = replace_empty_marker(content, f"packageref-{marker_project}-{axis}", snippet, "xml")
        csproj.write_text(cleanup(remove_scaffold_markers(content)), encoding="utf-8", newline="\n")

    for (relative, marker), snippets in file_patches.items():
        target = stage / PurePosixPath(relative)
        content = target.read_text(encoding="utf-8")
        unique_snippets = list(dict.fromkeys(snippets))
        target.write_text(replace_empty_marker(content, marker, unique_snippets, "cs"), encoding="utf-8", newline="\n")

    program = stage / PurePosixPath(config.architecture.host_path) / "Program.cs"
    content = program.read_text(encoding="utf-8")
    for marker in ("usings-transports", "di-transports", "pipeline-transports"):
        token = f"// <araia:{marker}></araia:{marker}>"
        if token in content:
            content = content.replace(token, "")
    program.write_text(cleanup(remove_scaffold_markers(content)), encoding="utf-8", newline="\n")

    rendered_settings = json.loads(render_template(json.dumps(settings_additions), variables, "appsettings-additions"))
    for name in ("appsettings.json", "appsettings.Development.json"):
        target = stage / PurePosixPath(config.architecture.host_path) / name
        current = json.loads(target.read_text(encoding="utf-8"))
        deep_merge(current, rendered_settings, "selected overlays")
        target.write_text(json.dumps(current, indent=2) + "\n", encoding="utf-8", newline="\n")

    readme = stage / "README.md"
    content = readme.read_text(encoding="utf-8").rstrip()
    if readme_sections:
        content += "\n\n## Selected features\n\n" + "\n".join(section.rstrip() + "\n" for section in readme_sections)
    readme.write_text(content.rstrip() + "\n", encoding="utf-8", newline="\n")

    for generated in (*stage.rglob("*.cs"), *stage.rglob("*.csproj"), *stage.rglob("*.props")):
        generated.write_text(
            cleanup(remove_scaffold_markers(generated.read_text(encoding="utf-8"))),
            encoding="utf-8",
            newline="\n",
        )


def cleanup(content: str) -> str:
    content = re.sub(r"[ \t]+\n", "\n", content)
    content = re.sub(r"\n{3,}", "\n\n", content)
    return content.rstrip() + "\n"


def prepare_stage(config: ScaffoldConfig, templates_root: Path, overlays: list[Overlay], stage: Path) -> list[str]:
    framework_match = FRAMEWORK.fullmatch(config.framework)
    assert framework_match is not None
    major = framework_match.group("major")
    variables = {
        "SolutionName": config.solution,
        "NamespacePrefix": config.namespace,
        "ArchitectureId": config.architecture.id,
        "TargetFramework": config.framework,
        "TargetFrameworkVersion": f"{major}.0",
        "SdkVersion": f"{major}.0.100",
        "UserSecretsId": str(uuid.uuid5(uuid.NAMESPACE_URL, f"{config.solution}:{config.namespace}:user-secrets")),
        "HostNamespace": config.project_namespace(config.architecture.host_project),
        "HostProjectPath": config.architecture.host_path,
        "HostProgramFile": f"{config.architecture.host_path}/Program.cs",
        "HostInfrastructureRoot": config.surface_path("host-infrastructure"),
        "ModuleName": config.module or "",
        "ModuleNamespace": config.surface_namespace("module-context") if config.module else "",
        "ModuleSchema": config.module.lower() if config.module else "",
        "ModuleContextRoot": config.surface_path("module-context") if config.module else "",
        "ModuleFeaturesRoot": config.surface_path("features") if config.module else "",
        "ModuleInfrastructureRoot": config.surface_path("module-infrastructure") if config.module else "",
        "ModuleRegistrationFile": config.registration_path("infrastructure") if config.module else "",
        "ModuleInfrastructureProjectPath": config.surface_project("module-infrastructure").path,
        "ProjectName": "",
        "SliceName": "",
    }
    generate_generic_starter(stage, config, templates_root, variables)
    apply_overlays(stage, config, overlays, variables)
    write_text(stage, f"{config.solution}.sln", solution_text(config))
    return sorted(path.relative_to(stage).as_posix() for path in stage.rglob("*") if path.is_file())


def run_writing_lint(stage: Path, skill_root: Path) -> None:
    linter = skill_root.parents[3] / "scripts" / "check-writing-rules.py"
    if not linter.exists():
        raise ScaffoldError("writing-linter-missing", f"Writing linter not found at {linter}", exit_code=5)
    groups = (
        ("markdown", sorted(stage.rglob("*.md"))),
        ("source", sorted(stage.rglob("*.cs"))),
    )
    for mode, paths in groups:
        if not paths:
            continue
        command = [sys.executable, str(linter), "--lang", "en", "--mode", mode, "--strict", *map(str, paths)]
        try:
            result = subprocess.run(command, capture_output=True, text=True, check=False, timeout=120)
        except subprocess.TimeoutExpired as error:
            raise ScaffoldError(
                "writing-linter-timeout",
                f"Generated {mode} content writing-rules check timed out",
                exit_code=5,
            ) from error
        if result.returncode != 0:
            findings = (result.stdout + "\n" + result.stderr).strip().splitlines()
            raise ScaffoldError(
                "writing-rules-failed",
                f"Generated {mode} content failed deterministic writing rules",
                exit_code=5,
                findings=findings[:20],
            )


def find_dotnet_files(output: Path) -> list[str]:
    found: list[str] = []
    if not output.exists():
        return found
    for path in output.rglob("*"):
        if not path.is_file() or any(part in ("bin", "obj", ".git") for part in path.parts):
            continue
        if path.suffix.lower() in (".sln", ".csproj", ".cs"):
            found.append(path.relative_to(output).as_posix())
            if len(found) == 20:
                break
    return sorted(found)


def metadata_matches(config: ScaffoldConfig) -> bool:
    metadata = config.output / ".araia" / "scaffold-metadata.json"
    if not metadata.exists():
        return False
    try:
        value = json.loads(metadata.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return False
    return value.get("config") == config.fingerprint()


def is_safe_overlap(relative: str) -> bool:
    return relative in SAFE_OVERLAP_FILES or relative.startswith(SAFE_OVERLAP_PREFIXES)


def copy_stage(stage: Path, config: ScaffoldConfig, files: list[str]) -> tuple[int, int, int]:
    collisions: list[str] = []
    for relative in files:
        source = stage / PurePosixPath(relative)
        target = config.output / PurePosixPath(relative)
        if target.exists() and target.read_bytes() != source.read_bytes() and not is_safe_overlap(relative):
            collisions.append(relative)
    if collisions and not config.force:
        raise ScaffoldError(
            "existing-generated-files",
            "Generated files would overwrite existing content",
            exit_code=3,
            files=collisions[:20],
        )

    written = 0
    unchanged = 0
    preserved = 0
    for relative in files:
        source = stage / PurePosixPath(relative)
        target = config.output / PurePosixPath(relative)
        if target.exists() and target.read_bytes() == source.read_bytes():
            unchanged += 1
            continue
        if target.exists() and is_safe_overlap(relative):
            preserved += 1
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        written += 1
    return written, unchanged, preserved


def classify_feeds(output: Path) -> list[dict[str, str]]:
    configs: list[Path] = []
    cursor = output.resolve()
    while True:
        try:
            children = list(cursor.iterdir()) if cursor.exists() else []
        except OSError:
            children = []
        for child in children:
            try:
                is_config = child.is_file() and child.name.lower() == "nuget.config"
            except OSError:
                continue
            if is_config:
                configs.append(child)
        if cursor.parent == cursor:
            break
        cursor = cursor.parent

    feeds: list[dict[str, str]] = []
    for config in dict.fromkeys(configs):
        try:
            root = ET.fromstring(config.read_text(encoding="utf-8-sig"))
        except (ET.ParseError, OSError) as exc:
            feeds.append({"name": config.name, "value": str(config), "classification": "unknown"})
            continue
        package_sources = root.find(".//packageSources")
        if package_sources is None:
            continue
        for node in package_sources.findall("add"):
            value = (node.attrib.get("value") or "").strip()
            lowered = value.lower().rstrip("/")
            if not value or any(token in value for token in ("%", "${", "$(")):
                classification = "unknown"
            elif lowered == PUBLIC_NUGET.rstrip("/"):
                classification = "public"
            elif any(hint in lowered for hint in PRIVATE_HOST_HINTS) or lowered.startswith(("http://", "https://")):
                classification = "private"
            else:
                classification = "unknown"
            feeds.append(
                {
                    "name": node.attrib.get("key", "unnamed"),
                    "value": value,
                    "classification": classification,
                }
            )
    return feeds


def write_metadata(config: ScaffoldConfig, status: str) -> None:
    target = config.output / ".araia" / "scaffold-metadata.json"
    target.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "generator": "dotnet-scaffold",
        "schema-version": 2,
        "status": status,
        "starter": config.starter.id,
        "config": config.fingerprint(),
    }
    target.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8", newline="\n")


def write_log(config: ScaffoldConfig, content: str) -> str:
    target = config.output / ".araia" / "scaffold-run.log"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content.rstrip() + "\n", encoding="utf-8", newline="\n")
    return target.relative_to(config.output).as_posix()


def run_verification(config: ScaffoldConfig, feeds: list[dict[str, str]]) -> None:
    solution = f"{config.solution}.sln"
    commands = (
        ("restore", ["dotnet", "restore", solution, "--use-lock-file"]),
        ("locked-restore", ["dotnet", "restore", solution, "--locked-mode"]),
        ("build", ["dotnet", "build", solution, "--no-restore", "--warnaserror"]),
        ("test", ["dotnet", "test", solution, "--no-build", "--no-restore"]),
    )
    log_parts = [
        f"dotnet-scaffold {datetime.now(timezone.utc).isoformat()}",
        f"starter={config.starter.id}",
        f"architecture={config.architecture.id}",
    ]
    private = [feed for feed in feeds if feed["classification"] != "public"]
    if private:
        log_parts.append("network-decision=authorized")
        log_parts.extend(f"feed={feed['classification']}:{feed['value']}" for feed in private)
    for step, command in commands:
        try:
            result = subprocess.run(
                command,
                cwd=config.output,
                capture_output=True,
                text=True,
                check=False,
                timeout=900,
            )
        except subprocess.TimeoutExpired as exc:
            stdout = exc.stdout.decode(errors="replace") if isinstance(exc.stdout, bytes) else exc.stdout or ""
            stderr = exc.stderr.decode(errors="replace") if isinstance(exc.stderr, bytes) else exc.stderr or ""
            log_parts.extend((f"command={subprocess.list2cmdline(command)}", stdout, stderr, "timeout=900s"))
            log_path = write_log(config, "\n".join(log_parts))
            raise ScaffoldError(
                "verification-timeout",
                f"dotnet {step} timed out; inspect {log_path}",
                exit_code=6,
                step=step,
                command=subprocess.list2cmdline(command),
                log=log_path,
            ) from exc
        log_parts.extend((f"command={subprocess.list2cmdline(command)}", result.stdout, result.stderr))
        if result.returncode != 0:
            log_path = write_log(config, "\n".join(log_parts))
            raise ScaffoldError(
                "verification-failed",
                f"dotnet {step} failed; inspect {log_path}",
                exit_code=6,
                step=step,
                command=subprocess.list2cmdline(command),
                log=log_path,
            )
    if private:
        write_log(config, "\n".join(log_parts))


def yaml_list(values: list[str]) -> str:
    return "[" + ", ".join(values) + "]"


def stack_profile(config: ScaffoldConfig) -> str:
    cache = ["hybrid" if value == "hybrid-cache" else value for value in config.selections["cache"]]
    consumer = "raw" if config.selections["messaging"] else "none"
    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    return f"""# Generated by dotnet-scaffold on {generated}.
# Override values by editing this file; add `# manually-edited: true` before running the analyzer.
target-framework: {config.framework}
lang-version: latest
nullable: enable
implicit-usings: enable
namespace-strategy: prefixed
root-namespace-prefix: {config.namespace}
central-package-management: true
architecture: {config.architecture.id}
mediator: none
validation: fluent-validation
error-handling: result-pattern
test-framework: xunit
test-mocking: nsubstitute
test-assertions: shouldly
test-data: bogus
arch-tests: netarchtest
http-mocking: mvc-testing
transports: {yaml_list(config.selections['transports'])}
persistence: {yaml_list(config.selections['persistence'])}
messaging: {yaml_list(config.selections['messaging'])}
messaging-consumer-pattern: {consumer}
cache: {yaml_list(cache)}
resilience: http-resilience
serialization: system-text-json
telemetry: none
auth: none
distributed-locks: none
slice-layout:
  features-root: {config.architecture.features_root}
  slice-shape: {config.architecture.slice_shape}
  file-naming: dot-suffix
"""


def write_stack_profile(config: ScaffoldConfig) -> bool:
    target = config.output / PurePosixPath(config.profile_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    content = stack_profile(config)
    if target.exists():
        existing = target.read_text(encoding="utf-8")
        existing_body = "\n".join(existing.splitlines()[1:])
        content_body = "\n".join(content.splitlines()[1:])
        if existing_body == content_body:
            return False
    target.write_text(content, encoding="utf-8", newline="\n")
    return True


def validate_profile_guard(config: ScaffoldConfig) -> None:
    target = config.output / PurePosixPath(config.profile_path)
    if not target.exists() or config.force:
        return
    try:
        existing = target.read_text(encoding="utf-8")
    except OSError as error:
        raise ScaffoldError(
            "profile-read-failed",
            f"Could not read stack profile '{config.profile_path}'",
            exit_code=3,
        ) from error
    if "# manually-edited: true" in existing:
        raise ScaffoldError(
            "manually-edited-profile",
            f"Refusing to overwrite manually edited profile '{config.profile_path}'",
            exit_code=3,
        )


def resolve_config(
    args: argparse.Namespace,
    overlays: dict[str, Overlay],
    starters: dict[str, StarterSpec],
) -> ScaffoldConfig:
    output = Path(args.output).resolve()
    solution = args.solution or output.name
    namespace = args.namespace or derive_namespace(solution)
    if not SAFE_SOLUTION.fullmatch(solution):
        raise ScaffoldError("invalid-solution", f"Invalid solution name '{solution}'")
    if not SAFE_NAMESPACE.fullmatch(namespace) or namespace in ("System", "Microsoft"):
        raise ScaffoldError("invalid-namespace", f"Invalid or reserved namespace prefix '{namespace}'")

    parsed = {axis: parse_csv(getattr(args, axis)) for axis in AXES}
    if args.non_interactive and parsed["transports"] is None:
        raise ScaffoldError(
            "missing-required-axis",
            "--transports is required with --non-interactive",
            valid=sorted(item.id for item in overlays.values() if item.axis == "transports"),
        )
    if parsed["transports"] == []:
        raise ScaffoldError("empty-required-axis", "--transports must select at least one transport")
    selections: dict[str, list[str]] = {
        "transports": parsed["transports"] or ["minimal-api"],
        "persistence": parsed["persistence"] or [],
        "messaging": parsed["messaging"] or [],
        "cache": parsed["cache"] or [],
    }
    module = args.module.strip() if args.module else None
    if module and (not SAFE_MODULE.fullmatch(module) or module in ("System", "Microsoft", "SharedKernel")):
        raise ScaffoldError("invalid-module", f"Invalid or reserved module name '{module}'")
    module_scoped = selections["persistence"] + selections["messaging"] + selections["cache"]
    if module_scoped and not module:
        raise ScaffoldError(
            "missing-module",
            "--module is required when persistence, messaging, or cache overlays are selected",
            overlays=module_scoped,
        )

    profile = PurePosixPath(args.profile_path)
    if profile.is_absolute() or ".." in profile.parts:
        raise ScaffoldError("invalid-profile-path", "--profile-path must stay inside --output")
    starter = resolve_starter(args.architecture, starters)
    return ScaffoldConfig(
        output=output,
        solution=solution,
        namespace=namespace,
        framework=args.framework,
        starter=starter,
        architecture=starter.architecture,
        module=module,
        selections=selections,
        profile_path=profile.as_posix(),
        force=args.force,
        allow_private_feed=args.allow_private_feed,
        dry_run=args.dry_run,
        skip_verification=args.skip_verification,
    )


def build_parser() -> argparse.ArgumentParser:
    parser = JsonArgumentParser(description="Generate and verify a deterministic .NET scaffold")
    parser.add_argument("--output", default=".")
    parser.add_argument("--solution")
    parser.add_argument("--namespace")
    parser.add_argument("--framework", default="net10.0")
    parser.add_argument("--architecture", default="modular-monolith")
    parser.add_argument("--module")
    parser.add_argument("--transports")
    parser.add_argument("--persistence")
    parser.add_argument("--messaging")
    parser.add_argument("--cache")
    parser.add_argument("--profile-path", default=".araia/stack-profile.yaml")
    parser.add_argument("--non-interactive", action="store_true")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--allow-private-feed", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--skip-verification", action="store_true", help=argparse.SUPPRESS)
    return parser


def execute(args: argparse.Namespace) -> dict[str, Any]:
    skill_root = Path(__file__).resolve().parent.parent
    templates_root = skill_root / "templates"
    starters_by_token = load_starters(templates_root)
    overlays_by_id = load_overlays(templates_root)
    config = resolve_config(args, overlays_by_id, starters_by_token)
    selected_overlays = validate_selection_graph(config.selections, overlays_by_id)
    validate_sdk(config.framework)
    validate_profile_guard(config)

    existing = find_dotnet_files(config.output)
    if existing and not config.force and not metadata_matches(config):
        raise ScaffoldError(
            "existing-dotnet-files",
            "Target contains an existing .NET project",
            exit_code=3,
            files=existing,
        )

    feeds = classify_feeds(config.output)
    gated = [feed for feed in feeds if feed["classification"] != "public"]
    if gated and not config.allow_private_feed:
        raise ScaffoldError(
            "private-feed-confirmation-required",
            "Private or unknown NuGet feeds require explicit authorization",
            exit_code=4,
            feeds=gated,
        )

    with tempfile.TemporaryDirectory(prefix="araia-dotnet-scaffold-") as temp:
        stage = Path(temp)
        files = prepare_stage(config, templates_root, selected_overlays, stage)
        run_writing_lint(stage, skill_root)
        if config.dry_run:
            return {
                "status": "planned",
                "starter": config.starter.id,
                "architecture": config.architecture.id,
                "solution": config.solution,
                "namespace": config.namespace,
                "module": config.module,
                "files": len(files),
                "features": config.selections,
                "private-feeds": gated,
            }
        config.output.mkdir(parents=True, exist_ok=True)
        written, unchanged, preserved = copy_stage(stage, config, files)

    write_metadata(config, "generated-unverified")
    if config.skip_verification:
        return {
            "status": "generated-unverified",
            "starter": config.starter.id,
            "architecture": config.architecture.id,
            "solution": config.solution,
            "namespace": config.namespace,
            "module": config.module,
            "written": written,
            "unchanged": unchanged,
            "preserved": preserved,
        }

    run_verification(config, feeds)
    write_metadata(config, "verified")
    profile_written = write_stack_profile(config)
    return {
        "status": "completed",
        "starter": config.starter.id,
        "architecture": config.architecture.id,
        "solution": config.solution,
        "namespace": config.namespace,
        "module": config.module,
        "written": written,
        "unchanged": unchanged,
        "preserved": preserved,
        "profile": config.profile_path,
        "profile-written": profile_written,
        "run": f"dotnet run --project {config.architecture.host_path}",
    }


def main(argv: list[str] | None = None) -> int:
    try:
        payload = execute(build_parser().parse_args(argv))
    except ScaffoldError as exc:
        print(compact_json({"status": "error", "code": exc.code, "message": exc.message, **exc.details}))
        return exc.exit_code
    except Exception as exc:  # defensive boundary: keep stdout machine-readable
        print(compact_json({"status": "error", "code": "unexpected", "message": str(exc)}))
        return 10
    print(compact_json(payload))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
