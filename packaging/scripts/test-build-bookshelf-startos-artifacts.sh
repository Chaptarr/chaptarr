#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
task_scratch=${TASK_SCRATCH_DIR:-${TMPDIR:-/tmp}/chaptarrng-startos-artifact-test}
mkdir -p -- "$task_scratch"
task_scratch=$(cd -- "$task_scratch" && pwd -P)
logs="$task_scratch/logs"
mkdir -p -- "$logs"
work=$(mktemp -d "$task_scratch/startos-artifact-test.XXXXXX")

cleanup() {
  result=$?
  trap - EXIT
  if [[ $result -eq 0 ]]; then
    if ! python3 - "$task_scratch" "$work" <<'PY'
from pathlib import Path
import os
import shutil
import sys

parent = Path(sys.argv[1]).resolve(strict=True)
path = Path(sys.argv[2])
if not path.name.startswith("startos-artifact-test.") or path.is_symlink() or not path.is_dir():
    raise SystemExit(f"Refusing to clean an unexpected test path: {path}")
if path.parent.resolve(strict=True) != parent or path.resolve(strict=True).parent != parent:
    raise SystemExit(f"Refusing to clean a path outside task scratch: {path}")
if path.stat().st_uid != os.getuid():
    raise SystemExit(f"Refusing to clean a task path owned by another user: {path}")
shutil.rmtree(path)
PY
    then
      echo "Could not safely remove StartOS artifact test scratch; retained at $work" >&2
    fi
    echo "StartOS artifact build logs retained at $logs"
  else
    echo "StartOS artifact builder test failed; scratch and failure evidence kept at $work; logs at $logs" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

fake_bin="$work/fake-bin"
workspace="$work/startos-workspace"
candidate_output="$work/candidate-output"
mkdir -p -- "$fake_bin" "$workspace/.startos"
chmod 0700 "$workspace/.startos"
printf 'fixture key path only\n' > "$workspace/.startos/build.key.pem"
chmod 0600 "$workspace/.startos/build.key.pem"

cat > "$fake_bin/docker" <<'SH'
#!/bin/sh
exit 0
SH
cat > "$fake_bin/node" <<'SH'
#!/bin/sh
printf '22\n'
SH
cat > "$fake_bin/npm" <<'SH'
#!/bin/sh
exit 0
SH
cat > "$fake_bin/start-cli" <<'SH'
#!/bin/sh
exit 0
SH
cat > "$fake_bin/make" <<'SH'
#!/bin/sh
set -eu
printf 'fixture x86_64 package\n' > bookshelfng_x86_64.s9pk
printf 'fixture aarch64 package\n' > bookshelfng_aarch64.s9pk
SH
chmod 0755 "$fake_bin"/*

digest="sha256:$(printf '%064d' 0 | tr '0' 'a')"
VERSION=0.9.978 \
CHAPTARRNG_IMAGE_DIGEST="$digest" \
OUTPUT_DIR="$candidate_output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-container-platform-candidates.sh" \
  > "$logs/container-candidate-build.log" 2>&1

bundle="$candidate_output/ChaptarrNG-0.9.978-bookshelfng-container-platform-candidates.tar.gz"
test -s "$bundle"
test -s "$bundle.sha256"
output="$work/signed-startos-artifacts"
PATH="$fake_bin:$PATH" \
STARTOS_WORKSPACE="$workspace" \
CANDIDATE_BUNDLE="$bundle" \
OUTPUT_DIR="$output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-startos-artifacts.sh" \
  > "$logs/startos-artifact-build.log" 2>&1

test -s "$output/bookshelfng_x86_64.s9pk"
test -s "$output/bookshelfng_aarch64.s9pk"
test -s "$output/bookshelfng_x86_64.s9pk.sha256"
test -s "$output/bookshelfng_aarch64.s9pk.sha256"
python3 - "$output/bookshelfng-startos-artifacts.json" "$digest" <<'PY'
import json
import sys

manifest = json.load(open(sys.argv[1], encoding="utf-8"))
assert manifest["packageId"] == "bookshelfng"
assert manifest["packageVersion"] == "0.9.978:1"
assert manifest["chaptarrngRelease"] == "0.9.978"
assert manifest["image"] == "ghcr.io/snapetech-org/chaptarrng@" + sys.argv[2]
assert [entry["architecture"] for entry in manifest["artifacts"]] == ["x86_64", "aarch64"]
assert all(len(entry["sha256"]) == 64 for entry in manifest["artifacts"])
PY

if find "$workspace" -mindepth 1 -maxdepth 1 \
  \( -name '.chaptarrng-startos-artifacts.*' -o -name '.bookshelfng-startos-package.*' \) \
  -print -quit | grep -q .; then
  echo "Successful StartOS artifact build left temporary source behind." >&2
  exit 1
fi
if find "$work" -mindepth 1 -maxdepth 1 -name '.chaptarrng-startos-output.*' -print -quit | grep -q .; then
  echo "Successful StartOS artifact build left output staging behind." >&2
  exit 1
fi

mkdir -p -- "$work/preexisting-output"
printf 'keep\n' > "$work/preexisting-output/operator-file"
if PATH="$fake_bin:$PATH" \
  STARTOS_WORKSPACE="$workspace" \
  CANDIDATE_BUNDLE="$bundle" \
  OUTPUT_DIR="$work/preexisting-output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-startos-artifacts.sh" \
  > "$logs/existing-output-rejection.log" 2>&1; then
  echo "StartOS artifact builder accepted an existing output directory." >&2
  exit 1
fi
test "$(cat "$work/preexisting-output/operator-file")" = keep

private_output="$workspace/.startos/nested/artifacts"
if PATH="$fake_bin:$PATH" \
  STARTOS_WORKSPACE="$workspace" \
  CANDIDATE_BUNDLE="$bundle" \
  OUTPUT_DIR="$private_output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-startos-artifacts.sh" \
  > "$logs/private-output-rejection.log" 2>&1; then
  echo "StartOS artifact builder accepted a path inside the private signing directory." >&2
  exit 1
fi
test ! -e "$workspace/.startos/nested"

tampered_dir="$work/tampered"
mkdir -p -- "$tampered_dir"
tampered_bundle="$tampered_dir/$(basename "$bundle")"
cp -- "$bundle" "$tampered_bundle"
cp -- "$bundle.sha256" "$tampered_bundle.sha256"
printf 'tamper\n' >> "$tampered_bundle"
if PATH="$fake_bin:$PATH" \
  STARTOS_WORKSPACE="$workspace" \
  CANDIDATE_BUNDLE="$tampered_bundle" \
  OUTPUT_DIR="$work/tampered-output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-startos-artifacts.sh" \
  > "$logs/checksum-rejection.log" 2>&1; then
  echo "StartOS artifact builder accepted a bundle with a mismatched checksum." >&2
  exit 1
fi
test ! -e "$work/tampered-output"

unsafe_dir="$work/unsafe"
mkdir -p -- "$unsafe_dir"
unsafe_bundle="$unsafe_dir/ChaptarrNG-0.9.978-bookshelfng-container-platform-candidates.tar.gz"
python3 - "$unsafe_bundle" <<'PY'
import io
import tarfile
import sys

with tarfile.open(sys.argv[1], "w:gz") as archive:
    payload = b"unsafe\n"
    member = tarfile.TarInfo("bookshelfng-container-platform-candidates/startos/../../outside")
    member.size = len(payload)
    archive.addfile(member, io.BytesIO(payload))
PY
(cd "$unsafe_dir" && sha256sum "$(basename "$unsafe_bundle")" > "$(basename "$unsafe_bundle").sha256")
if PATH="$fake_bin:$PATH" \
  STARTOS_WORKSPACE="$workspace" \
  CANDIDATE_BUNDLE="$unsafe_bundle" \
  OUTPUT_DIR="$work/unsafe-output" \
  bash "$repo_root/packaging/scripts/build-bookshelf-startos-artifacts.sh" \
  > "$logs/archive-traversal-rejection.log" 2>&1; then
  echo "StartOS artifact builder accepted an archive traversal path." >&2
  exit 1
fi
test ! -e "$work/unsafe-output"

echo "StartOS artifact builder preserves identities, checksums, output safety, and source extraction boundaries."
