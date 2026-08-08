#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

SEMVER_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
ASSEMBLY_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$")
DEFAULT_ALLOW_MARKER = "[allow-version-decrease]"
VERSION_ONLY_COMMIT_PATTERNS = (
    re.compile(r"\b(?:auto-)?version bump\b", re.IGNORECASE),
    re.compile(r"\bbump version\b", re.IGNORECASE),
    re.compile(r"\bkeep version\b", re.IGNORECASE),
    re.compile(r"\brestore version\b", re.IGNORECASE),
    re.compile(r"\bmaintain version\b", re.IGNORECASE),
)
VERSION_ONLY_EXCLUDE_PATTERNS = (
    re.compile(r"^fix:", re.IGNORECASE),
    re.compile(r"script", re.IGNORECASE),
)
VERSION_ONLY_ALLOWED_PATHS = {
    "package.json",
    "src/Directory.Build.props",
}


@dataclass(frozen=True)
class VersionSnapshot:
    label: str
    package_version: str
    assembly_version: str
    file_version: str

    @property
    def normalized_assembly_version(self) -> str:
        return normalize_assembly_version(self.assembly_version, f"{self.label} AssemblyVersion")

    @property
    def normalized_file_version(self) -> str:
        return normalize_assembly_version(self.file_version, f"{self.label} AssemblyFileVersion")

    @property
    def package_tuple(self) -> tuple[int, int, int]:
        return parse_semver(self.package_version, f"{self.label} package.json version")


def fail(message: str) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(1)


def parse_semver(version: str, label: str) -> tuple[int, int, int]:
    match = SEMVER_PATTERN.fullmatch(version.strip())
    if not match:
        fail(f"Unexpected {label} format: {version}")

    return tuple(int(part) for part in match.groups())


def normalize_assembly_version(version: str, label: str) -> str:
    match = ASSEMBLY_PATTERN.fullmatch(version.strip())
    if not match:
        fail(f"Unexpected {label} format: {version}")

    return ".".join(match.group(1, 2, 3))


def parse_package_version(text: str, label: str) -> str:
    try:
        package = json.loads(text)
    except json.JSONDecodeError as error:
        fail(f"Invalid {label}: {error}")

    version = package.get("version")
    if not isinstance(version, str) or not version.strip():
        fail(f"Missing version in {label}")

    parse_semver(version, f"{label} version")
    return version.strip()


def parse_props_versions(text: str, label: str) -> tuple[str, str]:
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        fail(f"Invalid {label}: {error}")

    assembly_version = next((element.text.strip() for element in root.iter("AssemblyVersion") if element.text and element.text.strip()), None)
    file_version = next((element.text.strip() for element in root.iter("AssemblyFileVersion") if element.text and element.text.strip()), None)

    if not assembly_version or not file_version:
        fail(f"Missing AssemblyVersion/AssemblyFileVersion in {label}")

    normalize_assembly_version(assembly_version, f"{label} AssemblyVersion")
    normalize_assembly_version(file_version, f"{label} AssemblyFileVersion")

    return assembly_version, file_version


def repo_root_from_script() -> Path:
    return Path(__file__).resolve().parents[1]


def git_show(repo_root: Path, ref: str, relative_path: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo_root), "show", f"{ref}:{relative_path}"],
        check=False,
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        error = result.stderr.strip() or result.stdout.strip() or "unknown git error"
        fail(f"Failed to read {relative_path} at {ref}: {error}")

    return result.stdout


def git_lines(repo_root: Path, args: list[str], error_context: str) -> list[str]:
    result = subprocess.run(
        ["git", "-C", str(repo_root), *args],
        check=False,
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        error = result.stderr.strip() or result.stdout.strip() or "unknown git error"
        fail(f"{error_context}: {error}")

    return [line for line in result.stdout.splitlines() if line.strip()]


def load_snapshot_from_texts(label: str, package_text: str, props_text: str) -> VersionSnapshot:
    package_version = parse_package_version(package_text, f"{label} package.json")
    assembly_version, file_version = parse_props_versions(props_text, f"{label} src/Directory.Build.props")

    return VersionSnapshot(
        label=label,
        package_version=package_version,
        assembly_version=assembly_version,
        file_version=file_version,
    )


def load_worktree_snapshot(repo_root: Path) -> VersionSnapshot:
    package_text = (repo_root / "package.json").read_text(encoding="utf-8")
    props_text = (repo_root / "src" / "Directory.Build.props").read_text(encoding="utf-8")
    return load_snapshot_from_texts("working tree", package_text, props_text)


def load_ref_snapshot(repo_root: Path, ref: str) -> VersionSnapshot:
    package_text = git_show(repo_root, ref, "package.json")
    props_text = git_show(repo_root, ref, "src/Directory.Build.props")
    return load_snapshot_from_texts(ref, package_text, props_text)


def ensure_versions_in_sync(snapshot: VersionSnapshot) -> None:
    assembly_version = snapshot.normalized_assembly_version
    file_version = snapshot.normalized_file_version

    if assembly_version != snapshot.package_version or file_version != snapshot.package_version:
        fail(
            f"Version mismatch in {snapshot.label}: "
            f"package.json={snapshot.package_version}, "
            f"AssemblyVersion={snapshot.assembly_version}, "
            f"AssemblyFileVersion={snapshot.file_version}"
        )

    print(f"Version sync OK for {snapshot.label}: {snapshot.package_version}")


def load_event_messages(event_path: str | None) -> list[str]:
    if not event_path:
        return []

    path = Path(event_path)
    if not path.exists():
        fail(f"GitHub event payload not found: {event_path}")

    try:
        event = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        fail(f"Invalid GitHub event payload {event_path}: {error}")

    messages: list[str] = []
    pull_request = event.get("pull_request")

    if isinstance(pull_request, dict):
        for key in ("title", "body"):
            value = pull_request.get(key)
            if isinstance(value, str) and value.strip():
                messages.append(value)

    head_commit = event.get("head_commit")
    if isinstance(head_commit, dict):
        value = head_commit.get("message")
        if isinstance(value, str) and value.strip():
            messages.append(value)

    for commit in event.get("commits", []):
        if not isinstance(commit, dict):
            continue

        value = commit.get("message")
        if isinstance(value, str) and value.strip():
            messages.append(value)

    return messages


def event_allows_version_decrease(event_path: str | None, marker: str) -> bool:
    return any(marker in message for message in load_event_messages(event_path))


def is_version_only_commit(subject: str) -> bool:
    if any(pattern.search(subject) for pattern in VERSION_ONLY_EXCLUDE_PATTERNS):
        return False
    return any(pattern.search(subject) for pattern in VERSION_ONLY_COMMIT_PATTERNS)


def guard_sync(repo_root: Path) -> None:
    ensure_versions_in_sync(load_worktree_snapshot(repo_root))


def guard_release_tag(repo_root: Path, tag: str) -> None:
    match = re.fullmatch(r"v(\d+\.\d+\.\d+)", tag.strip())
    if not match:
        fail(f"Unexpected tag format: {tag} (expected vX.Y.Z)")

    snapshot = load_worktree_snapshot(repo_root)
    ensure_versions_in_sync(snapshot)

    tag_version = match.group(1)
    if snapshot.package_version != tag_version:
        fail(f"Tag/version mismatch: tag={tag_version} package.json={snapshot.package_version}")

    print(f"Release version OK: {tag}")


def guard_monotonic(repo_root: Path, compare_ref: str, event_path: str | None, marker: str) -> None:
    current = load_worktree_snapshot(repo_root)
    previous = load_ref_snapshot(repo_root, compare_ref)

    ensure_versions_in_sync(current)
    ensure_versions_in_sync(previous)

    if current.package_tuple < previous.package_tuple:
        if event_allows_version_decrease(event_path, marker):
            print(
                f"Version decrease acknowledged via {marker}: "
                f"{previous.package_version} -> {current.package_version}"
            )
            return

        fail(
            f"Version decreased: {previous.package_version} -> {current.package_version}. "
            f"If this reset is intentional, include {marker} in the PR title/body or commit message."
        )

    print(
        f"Version monotonic OK: {current.package_version} "
        f"(compare ref {compare_ref}: {previous.package_version})"
    )


def guard_commit_hygiene(repo_root: Path, compare_ref: str) -> None:
    commit_lines = git_lines(
        repo_root,
        ["log", "--format=%H%x00%s", f"{compare_ref}..HEAD"],
        f"Failed to enumerate commits in range {compare_ref}..HEAD",
    )

    offenders: list[tuple[str, str, list[str]]] = []

    for line in commit_lines:
        commit, _, subject = line.partition("\0")
        if not commit or not is_version_only_commit(subject):
            continue

        changed_files = git_lines(
            repo_root,
            ["diff-tree", "--no-commit-id", "--name-only", "-r", "--root", commit],
            f"Failed to enumerate files for commit {commit}",
        )

        disallowed = sorted(path for path in changed_files if path not in VERSION_ONLY_ALLOWED_PATHS)
        if disallowed:
            offenders.append((commit, subject, disallowed))

    if offenders:
        lines = [
            "Version-only commit hygiene failure. Commits that claim to be version-only may only touch:",
            *[f"  - {path}" for path in sorted(VERSION_ONLY_ALLOWED_PATHS)],
        ]

        for commit, subject, disallowed in offenders:
            lines.append(f"- {commit[:8]} {subject}")
            lines.extend(f"    {path}" for path in disallowed)

        fail("\n".join(lines))

    print(f"Version-only commit hygiene OK for range {compare_ref}..HEAD")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Validate Chaptarr version invariants.")
    parser.add_argument("--repo-root", default=str(repo_root_from_script()), help="Repository root (defaults to script parent)")

    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("sync", help="Ensure package.json and Directory.Build.props are in sync")

    release_parser = subparsers.add_parser("release-tag", help="Ensure a release tag matches repo version files")
    release_parser.add_argument("--tag", required=True, help="Git tag name (expected format: vX.Y.Z)")

    monotonic_parser = subparsers.add_parser("monotonic", help="Ensure version does not decrease relative to another git ref")
    monotonic_parser.add_argument("--compare-ref", required=True, help="Git ref to compare against")
    monotonic_parser.add_argument("--event-path", help="GitHub event payload path for explicit decrease acknowledgements")
    monotonic_parser.add_argument("--allow-marker", default=DEFAULT_ALLOW_MARKER, help="Acknowledgement marker for intentional decreases")

    hygiene_parser = subparsers.add_parser("commit-hygiene", help="Ensure version-only commits only touch version files")
    hygiene_parser.add_argument("--compare-ref", required=True, help="Git ref used as the lower bound for commit inspection")

    return parser


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    repo_root = Path(args.repo_root).resolve()

    if args.command == "sync":
        guard_sync(repo_root)
        return

    if args.command == "release-tag":
        guard_release_tag(repo_root, args.tag)
        return

    if args.command == "monotonic":
        guard_monotonic(repo_root, args.compare_ref, args.event_path, args.allow_marker)
        return

    if args.command == "commit-hygiene":
        guard_commit_hygiene(repo_root, args.compare_ref)
        return

    fail(f"Unsupported command: {args.command}")


if __name__ == "__main__":
    main()
