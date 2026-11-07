#!/usr/bin/env python3
"""Keep the archive builder, release matrix, and validation matrix aligned."""

import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
EXPECTED_RIDS = {
    "linux-x64",
    "linux-x86",
    "linux-arm64",
    "linux-musl-x64",
    "linux-musl-arm64",
    "freebsd-x64",
    "osx-x64",
    "osx-arm64",
    "win-x64",
    "win-x86",
}


def matrix_rids(path: Path) -> list[list[str]]:
    text = path.read_text()
    return [
        [rid.strip() for rid in match.split(",") if rid.strip()]
        for match in re.findall(r"^\s*rid:\s*\[([^\]]+)\]", text, re.MULTILINE)
    ]


def main() -> None:
    builder = (ROOT / "packaging/scripts/build-standalone.sh").read_text()
    case = re.search(r'case\s+"\$rid"\s+in\s*\n(.*?)\n\s*\*\)', builder, re.DOTALL)
    if case is None:
        raise SystemExit("Standalone builder RID allowlist was not found")
    builder_rids = {
        rid.strip()
        for rid in re.search(r"^\s*([^\n]+?)\s*;;$", case.group(1), re.MULTILINE)
        .group(1)
        .rstrip(")")
        .split("|")
    }

    release_matrices = matrix_rids(ROOT / ".github/workflows/release.yml")
    validation_matrices = matrix_rids(
        ROOT / ".github/workflows/standalone-asset-validation.yml"
    )
    if len(release_matrices) != 1:
        raise SystemExit(f"Expected one release matrix, found {len(release_matrices)}")
    if len(validation_matrices) != 2:
        raise SystemExit(
            f"Expected standard and extra-platform validation matrices, found {len(validation_matrices)}"
        )

    release_rids = set(release_matrices[0])
    validation_rids = {rid for matrix in validation_matrices for rid in matrix}
    checks = {
        "builder": builder_rids,
        "release": release_rids,
        "validation": validation_rids,
    }
    for name, rids in checks.items():
        if len(rids) != len(EXPECTED_RIDS) or rids != EXPECTED_RIDS:
            missing = sorted(EXPECTED_RIDS - rids)
            extra = sorted(rids - EXPECTED_RIDS)
            raise SystemExit(f"{name} RID mismatch; missing={missing}, extra={extra}")

    print(f"Standalone RID configuration matches for {len(EXPECTED_RIDS)} targets.")


if __name__ == "__main__":
    main()
