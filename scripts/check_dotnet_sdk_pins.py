#!/usr/bin/env python3
"""Ensure hosted and container builds use the SDK pinned in global.json."""

from __future__ import annotations

import json
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
DOCKER_SDK = re.compile(r"mcr\.microsoft\.com/dotnet/sdk:([^\s@]+)")
WORKFLOW_SDK = re.compile(r"^\s*dotnet-version:\s*[\"']?([0-9]+\.[0-9]+\.[0-9]+)", re.MULTILINE)


def main() -> int:
    global_json = json.loads((ROOT / "global.json").read_text(encoding="utf-8"))
    expected = global_json["sdk"]["version"]
    tracked = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    ).stdout.decode("utf-8").split("\0")

    docker_pins: list[tuple[str, str]] = []
    workflow_pins: list[tuple[str, str]] = []
    for relative in filter(None, tracked):
        path = ROOT / relative
        if path.name.startswith("Dockerfile"):
            text = path.read_text(encoding="utf-8")
            docker_pins.extend((relative, version) for version in DOCKER_SDK.findall(text))
        if relative.startswith(".github/workflows/") and path.suffix in {".yml", ".yaml"}:
            text = path.read_text(encoding="utf-8")
            workflow_pins.extend((relative, version) for version in WORKFLOW_SDK.findall(text))

    mismatches = [
        (path, version, "container")
        for path, version in docker_pins
        if version != expected
    ] + [
        (path, version, "workflow")
        for path, version in workflow_pins
        if version != expected
    ]

    if not docker_pins:
        mismatches.append(("Dockerfiles", "(no SDK image pins found)", "container"))
    if not workflow_pins:
        mismatches.append(("GitHub Actions workflows", "(no dotnet-version pins found)", "workflow"))

    if mismatches:
        for path, version, kind in mismatches:
            print(f"{kind} SDK pin in {path} is {version}; global.json requires {expected}")
        return 1

    print(
        f".NET SDK pins match global.json ({expected}): "
        f"{len(docker_pins)} container image(s), {len(workflow_pins)} workflow pin(s)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
