#!/usr/bin/env bash
set -euo pipefail

: "${VERSION:?VERSION is required}"
: "${IMAGE_DIGEST:?IMAGE_DIGEST is required}"
: "${CHART_PACKAGE:?CHART_PACKAGE is required}"
: "${GITHUB_TOKEN:?GITHUB_TOKEN is required}"
: "${GITHUB_ACTOR:?GITHUB_ACTOR is required}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "VERSION must be a stable MAJOR.MINOR.PATCH value." >&2
  exit 2
fi

if [[ ! "$IMAGE_DIGEST" =~ ^sha256:[a-f0-9]{64}$ ]]; then
  echo "IMAGE_DIGEST must be sha256 followed by 64 lowercase hexadecimal characters." >&2
  exit 2
fi

expected_package="bookshelfng-${VERSION}.tgz"
if [[ "$(basename "$CHART_PACKAGE")" != "$expected_package" || ! -s "$CHART_PACKAGE" ]]; then
  echo "CHART_PACKAGE must be a non-empty $expected_package archive." >&2
  exit 2
fi

for command in helm python3; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "$command is required to publish the BookshelfNG Helm chart bridge." >&2
    exit 127
  fi
done

chart_metadata="$(helm show chart "$CHART_PACKAGE")"
grep -Fxq 'name: bookshelfng' <<< "$chart_metadata"
grep -Fxq "version: $VERSION" <<< "$chart_metadata"
chart_values="$(helm show values "$CHART_PACKAGE")"
grep -Fxq '  repository: ghcr.io/snapetech-org/chaptarrng' <<< "$chart_values"
grep -Fxq "  digest: \"$IMAGE_DIGEST\"" <<< "$chart_values"

chart_root="oci://ghcr.io/snapetech/bookshelfng"
chart_ref="${chart_root}/bookshelfng"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-bookshelfng-chart-publish.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

printf '%s\n' "$GITHUB_TOKEN" | helm registry login ghcr.io \
  --username "$GITHUB_ACTOR" \
  --password-stdin

compare_chart_archives() {
  python3 - "$1" "$2" <<'PY'
from pathlib import PurePosixPath
import hashlib
import sys
import tarfile

def contents(archive_path):
    result = {}
    with tarfile.open(archive_path, "r:gz") as archive:
        for member in archive.getmembers():
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or not path.parts or path.parts[0] != "bookshelfng":
                raise SystemExit(f"Unsafe or unexpected chart archive path: {member.name!r}")
            if member.isdir():
                continue
            if not member.isfile():
                raise SystemExit(f"Unsupported chart archive member: {member.name!r}")
            stream = archive.extractfile(member)
            if stream is None:
                raise SystemExit(f"Unable to read chart archive member: {member.name!r}")
            key = "/".join(path.parts[1:])
            if not key or key in result:
                raise SystemExit(f"Duplicate or empty chart archive path: {member.name!r}")
            result[key] = hashlib.sha256(stream.read()).hexdigest()
    return result

if contents(sys.argv[1]) != contents(sys.argv[2]):
    raise SystemExit("The published chart version exists with different contents; refusing to overwrite it.")
PY
}

pull_existing() {
  local destination="$1"
  local output_path="$2"
  mkdir -p "$destination"
  if helm pull "$chart_ref" --version "$VERSION" --destination "$destination" > "$output_path" 2>&1; then
    return 0
  fi
  return 1
}

existing_dir="$work_dir/existing"
if pull_existing "$existing_dir" "$work_dir/pull.log"; then
  existing_package="$existing_dir/$expected_package"
  compare_chart_archives "$CHART_PACKAGE" "$existing_package"
  echo "BookshelfNG Helm chart $VERSION is already published with matching contents."
  exit 0
fi

if ! grep -Eiq '404|not found|manifest unknown' "$work_dir/pull.log"; then
  cat "$work_dir/pull.log" >&2
  echo "Unable to confirm whether BookshelfNG Helm chart $VERSION already exists." >&2
  exit 1
fi

helm push "$CHART_PACKAGE" "$chart_root"

published_dir="$work_dir/published"
if ! pull_existing "$published_dir" "$work_dir/verify-pull.log"; then
  cat "$work_dir/verify-pull.log" >&2
  echo "The chart push completed, but the published version could not be pulled for verification." >&2
  exit 1
fi
compare_chart_archives "$CHART_PACKAGE" "$published_dir/$expected_package"

echo "Published and verified $chart_ref:$VERSION with ChaptarrNG image $IMAGE_DIGEST."
