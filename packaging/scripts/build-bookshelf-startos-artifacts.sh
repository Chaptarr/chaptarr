#!/usr/bin/env bash
set -euo pipefail

: "${STARTOS_WORKSPACE:?STARTOS_WORKSPACE must point to a StartOS packaging workspace}"
: "${CANDIDATE_BUNDLE:?CANDIDATE_BUNDLE must name the release container-platform candidate tarball}"
: "${OUTPUT_DIR:?OUTPUT_DIR must name a new directory for the signed StartOS artifacts}"

if [[ "$OUTPUT_DIR" == */ ]]; then
  echo "Output path must not end with a slash: $OUTPUT_DIR" >&2
  exit 2
fi

workspace_input=$STARTOS_WORKSPACE
if [[ ! -d "$workspace_input" || -L "$workspace_input" ]]; then
  echo "StartOS workspace must be a real directory: $workspace_input" >&2
  exit 2
fi
workspace=$(cd -- "$workspace_input" && pwd -P)
if [[ ! -d "$workspace/.startos" || -L "$workspace/.startos" || \
      ! -f "$workspace/.startos/build.key.pem" || -L "$workspace/.startos/build.key.pem" ]]; then
  echo "The StartOS workspace signing-key path is unavailable; the key contents were not read." >&2
  exit 2
fi

bundle_input=$CANDIDATE_BUNDLE
if [[ ! -f "$bundle_input" || -L "$bundle_input" ]]; then
  echo "Candidate bundle must be a regular file: $bundle_input" >&2
  exit 2
fi
bundle_parent=$(cd -- "$(dirname -- "$bundle_input")" && pwd -P)
bundle_path="$bundle_parent/$(basename -- "$bundle_input")"
bundle_name=$(basename -- "$bundle_path")
if [[ ! "$bundle_name" =~ ^ChaptarrNG-([0-9]+\.[0-9]+\.[0-9]+)-bookshelfng-container-platform-candidates\.tar\.gz$ ]]; then
  echo "Expected a versioned BookshelfNG container-platform candidate bundle; found: $bundle_name" >&2
  exit 2
fi
release_version=${BASH_REMATCH[1]}
sidecar="$bundle_path.sha256"
if [[ ! -f "$sidecar" || -L "$sidecar" ]]; then
  echo "The candidate bundle SHA-256 sidecar is missing: $sidecar" >&2
  exit 2
fi

python3 - "$bundle_path" "$sidecar" "$bundle_name" <<'PY'
from pathlib import Path
import hashlib
import re
import sys

bundle = Path(sys.argv[1])
sidecar = Path(sys.argv[2])
expected_name = sys.argv[3]
lines = sidecar.read_text(encoding="ascii").splitlines()
if len(lines) != 1:
    raise SystemExit("The candidate SHA-256 sidecar must contain one checksum entry.")
match = re.fullmatch(r"([a-f0-9]{64})\s+\*?([^\s]+)", lines[0])
if not match or match.group(2) != expected_name:
    raise SystemExit("The candidate SHA-256 sidecar does not name the selected release bundle.")

digest = hashlib.sha256()
with bundle.open("rb") as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
if digest.hexdigest() != match.group(1):
    raise SystemExit("The candidate bundle does not match its SHA-256 sidecar.")
PY

output_name=$(basename -- "$OUTPUT_DIR")
if [[ "$output_name" == . || "$output_name" == .. || -z "$output_name" ]]; then
  echo "Output path must name a new directory: $OUTPUT_DIR" >&2
  exit 2
fi
output_parent=$(dirname -- "$OUTPUT_DIR")
output_dir="$output_parent/$output_name"
python3 - "$workspace/.startos" "$output_dir" <<'PY'
from pathlib import Path
import os
import sys

private = Path(sys.argv[1]).resolve(strict=True)
output = Path(sys.argv[2]).resolve(strict=False)
if os.path.commonpath((str(private), str(output))) == str(private):
    raise SystemExit("StartOS artifacts cannot be written into the private .startos directory.")
PY
mkdir -p -- "$output_parent"
output_parent=$(cd -- "$output_parent" && pwd -P)
output_dir="$output_parent/$output_name"
if [[ -e "$output_dir" || -L "$output_dir" ]]; then
  echo "Output path already exists; choose a new directory: $output_dir" >&2
  exit 2
fi

python3 - "$workspace/.startos" "$output_dir" <<'PY'
from pathlib import Path
import os
import sys

private = Path(sys.argv[1]).resolve(strict=True)
output = Path(sys.argv[2]).resolve(strict=False)
if os.path.commonpath((str(private), str(output))) == str(private):
    raise SystemExit("StartOS artifacts cannot be written into the private .startos directory.")
PY

work=$(mktemp -d "$workspace/.chaptarrng-startos-artifacts.XXXXXX")
artifact_work=
candidate_dir="$work/package"
candidate_output=

cleanup() {
  result=$?
  trap - EXIT
  if [[ $result -eq 0 ]]; then
    if ! python3 - "$workspace" "$work" "$output_parent" "$artifact_work" <<'PY'
from pathlib import Path
import os
import shutil
import sys

paths = [(sys.argv[1], sys.argv[2], ".chaptarrng-startos-artifacts.")]
if sys.argv[4]:
    paths.append((sys.argv[3], sys.argv[4], ".chaptarrng-startos-output."))
for parent_text, path_text, prefix in paths:
    parent = Path(parent_text).resolve(strict=True)
    path = Path(path_text)
    if path.name.startswith(prefix) is False or path.is_symlink() or not path.is_dir():
        raise SystemExit(f"Refusing to clean an unexpected task path: {path}")
    if path.parent.resolve(strict=True) != parent or path.resolve(strict=True).parent != parent:
        raise SystemExit(f"Refusing to clean a path outside its task parent: {path}")
    if path.stat().st_uid != os.getuid():
        raise SystemExit(f"Refusing to clean a path owned by another user: {path}")
    shutil.rmtree(path)
PY
    then
      echo "Could not safely remove StartOS build scratch; retained at $work ${artifact_work:-}" >&2
    fi
  else
    echo "StartOS artifact build failed; task-owned source and failure evidence kept at $work and $artifact_work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

artifact_work=$(mktemp -d "$output_parent/.chaptarrng-startos-output.XXXXXX")
candidate_output="$artifact_work/output"
umask 077

mkdir -p -- "$candidate_dir"
python3 - "$bundle_path" "$candidate_dir" "$release_version" <<'PY'
from pathlib import Path, PurePosixPath
import os
import re
import shutil
import sys
import tarfile

bundle = Path(sys.argv[1])
destination = Path(sys.argv[2]).resolve(strict=True)
release_version = sys.argv[3]
archive_root = "bookshelfng-container-platform-candidates"
seen = set()
found_startos_root = False
expanded_bytes = 0
max_expanded_bytes = 128 * 1024 * 1024

with tarfile.open(bundle, "r:gz") as archive:
    for member in archive.getmembers():
        raw_name = member.name
        if not raw_name or raw_name.startswith("/") or "\\" in raw_name:
            raise SystemExit(f"Unsafe path in candidate archive: {raw_name!r}")
        raw_parts = raw_name.split("/")
        if member.isdir() and raw_parts[-1] == "":
            raw_parts.pop()
        if any(part in ("", ".", "..") for part in raw_parts):
            raise SystemExit(f"Unexpected path in candidate archive: {raw_name!r}")
        parts = PurePosixPath(raw_name).parts
        if not parts or parts[0] != archive_root or any(part in ("", ".", "..") for part in parts):
            raise SystemExit(f"Unexpected path in candidate archive: {raw_name!r}")
        if member.issym() or member.islnk() or member.isdev() or member.isfifo():
            if parts[:2] == (archive_root, "startos"):
                raise SystemExit(f"Unsupported link or special file in StartOS candidate: {raw_name!r}")
            continue
        if not member.isdir() and not member.isfile():
            raise SystemExit(f"Unsupported archive entry: {raw_name!r}")
        if parts[:2] != (archive_root, "startos"):
            continue

        relative = parts[2:]
        if not relative:
            if not member.isdir():
                raise SystemExit("StartOS candidate root is not a directory.")
            found_startos_root = True
            continue
        if any(part in {".startos", "node_modules", "javascript"} for part in relative):
            raise SystemExit(f"Generated or private content is not allowed in the StartOS candidate: {raw_name!r}")
        if relative[-1].endswith(".s9pk"):
            raise SystemExit(f"A signed package must not be embedded in the source candidate: {raw_name!r}")
        if member.size < 0 or member.size > max_expanded_bytes:
            raise SystemExit(f"StartOS candidate member is too large: {raw_name!r}")
        expanded_bytes += member.size
        if expanded_bytes > max_expanded_bytes:
            raise SystemExit("The StartOS candidate source exceeds the unpacked size limit.")

        target = destination.joinpath(*relative)
        if os.path.commonpath((str(destination), str(target.resolve(strict=False)))) != str(destination):
            raise SystemExit(f"Archive path escapes the StartOS candidate directory: {raw_name!r}")
        key = str(target)
        if key in seen:
            raise SystemExit(f"Duplicate path in StartOS candidate archive: {raw_name!r}")
        seen.add(key)

        if member.isdir():
            target.mkdir(parents=True, exist_ok=True, mode=0o755)
            continue

        target.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
        source = archive.extractfile(member)
        if source is None:
            raise SystemExit(f"Could not read candidate file: {raw_name!r}")
        mode = (member.mode & 0o111) | 0o644
        with source, target.open("xb") as output:
            shutil.copyfileobj(source, output, length=1024 * 1024)
        target.chmod(mode)

if not found_startos_root:
    raise SystemExit("The release candidate bundle does not contain its StartOS package source.")

required = (
    "build-startos-package.sh",
    "Makefile",
    "package.json",
    "package-lock.json",
    "startos/manifest/index.ts",
    "startos/versions/current.ts",
)
missing = [name for name in required if not (destination / name).is_file()]
if missing:
    raise SystemExit("The StartOS candidate is incomplete: " + ", ".join(missing))

manifest = (destination / "startos/manifest/index.ts").read_text(encoding="utf-8")
version_source = (destination / "startos/versions/current.ts").read_text(encoding="utf-8")
if "id: 'bookshelfng'" not in manifest:
    raise SystemExit("The StartOS package candidate does not preserve the BookshelfNG package identity.")
if not re.search(r"ghcr\.io/snapetech-org/chaptarrng@sha256:[a-f0-9]{64}", manifest):
    raise SystemExit("The StartOS candidate is not pinned to a ChaptarrNG image digest.")
if f"version: '{release_version}:1'" not in version_source:
    raise SystemExit("The StartOS package version does not match the candidate release.")
PY

OUTPUT_DIR="$candidate_output" bash "$candidate_dir/build-startos-package.sh"

python3 - "$candidate_output" "$release_version" "$bundle_path" "$candidate_dir" <<'PY'
from pathlib import Path
import hashlib
import json
import re
import sys

directory = Path(sys.argv[1])
release_version = sys.argv[2]
bundle = Path(sys.argv[3])
candidate_dir = Path(sys.argv[4])
artifacts = (
    ("bookshelfng_x86_64.s9pk", "x86_64"),
    ("bookshelfng_aarch64.s9pk", "aarch64"),
)
records = []
for name, architecture in artifacts:
    package = directory / name
    sidecar = directory / (name + ".sha256")
    if not package.is_file() or package.is_symlink() or package.stat().st_size == 0:
        raise SystemExit(f"StartOS package artifact is missing or empty: {name}")
    if not sidecar.is_file() or sidecar.is_symlink():
        raise SystemExit(f"StartOS package checksum is missing: {name}.sha256")
    lines = sidecar.read_text(encoding="ascii").splitlines()
    if len(lines) != 1:
        raise SystemExit(f"StartOS package checksum must contain one entry: {name}.sha256")
    match = re.fullmatch(r"([a-f0-9]{64})\s+\*?([^\s]+)", lines[0])
    if not match or match.group(2) != name:
        raise SystemExit(f"StartOS package checksum names the wrong file: {name}.sha256")
    package_hash = hashlib.sha256()
    with package.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            package_hash.update(chunk)
    digest = package_hash.hexdigest()
    if digest != match.group(1):
        raise SystemExit(f"StartOS package checksum does not match: {name}")
    records.append({"file": name, "architecture": architecture, "sha256": digest})

manifest_path = directory / "bookshelfng-startos-artifacts.json"
candidate_manifest = (candidate_dir / "startos/manifest/index.ts").read_text(encoding="utf-8")
image_match = re.search(
    r"ghcr\.io/snapetech-org/chaptarrng@sha256:[a-f0-9]{64}",
    candidate_manifest,
)
if not image_match:
    raise SystemExit("The packaged StartOS candidate has no digest-pinned ChaptarrNG image.")
bundle_hash = hashlib.sha256()
with bundle.open("rb") as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        bundle_hash.update(chunk)
manifest_path.write_text(
    json.dumps(
        {
            "packageId": "bookshelfng",
            "packageVersion": f"{release_version}:1",
            "chaptarrngRelease": release_version,
            "image": image_match.group(0),
            "candidateBundle": bundle.name,
            "candidateBundleSha256": bundle_hash.hexdigest(),
            "artifacts": records,
        },
        indent=2,
    )
    + "\n",
    encoding="utf-8",
)
PY

mv --no-clobber --no-target-directory -- "$candidate_output" "$output_dir"
if [[ -e "$candidate_output" || -L "$candidate_output" ]]; then
  echo "The output directory appeared during package generation; no artifacts were moved: $output_dir" >&2
  exit 1
fi
echo "Built signed BookshelfNG-identity StartOS packages for ChaptarrNG $release_version at $output_dir"
