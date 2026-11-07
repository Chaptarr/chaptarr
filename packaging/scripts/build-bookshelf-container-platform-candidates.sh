#!/usr/bin/env bash
set -euo pipefail

: "${VERSION:?VERSION must contain the ChaptarrNG release version}"
: "${CHAPTARRNG_IMAGE_DIGEST:?CHAPTARRNG_IMAGE_DIGEST must contain the standard ChaptarrNG image digest}"
: "${OUTPUT_DIR:?OUTPUT_DIR must be a new task-scoped output directory}"

if [[ ! "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Expected a stable MAJOR.MINOR.PATCH version, found: $VERSION" >&2
  exit 2
fi

if [[ ! "$CHAPTARRNG_IMAGE_DIGEST" =~ ^sha256:[a-f0-9]{64}$ ]]; then
  echo "Expected a sha256 digest for the standard ChaptarrNG image." >&2
  exit 2
fi

output_name=$(basename -- "$OUTPUT_DIR")
if [[ "$output_name" == . || "$output_name" == .. || -z "$output_name" || -e "$OUTPUT_DIR" || -L "$OUTPUT_DIR" ]]; then
  echo "Output path must be a new directory: $OUTPUT_DIR" >&2
  exit 2
fi

parent=$(dirname -- "$OUTPUT_DIR")
mkdir -p -- "$parent"
parent=$(cd "$parent" && pwd -P)
OUTPUT_DIR="$parent/$output_name"
if [[ -e "$OUTPUT_DIR" || -L "$OUTPUT_DIR" ]]; then
  echo "Output path already exists; choose a new task-scoped directory: $OUTPUT_DIR" >&2
  exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
image="ghcr.io/snapetech-org/chaptarrng@$CHAPTARRNG_IMAGE_DIGEST"
work=$(mktemp -d "$parent/.bookshelfng-container-candidates.XXXXXX")
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    python3 - "$work" <<'PY'
from pathlib import Path
import shutil
import sys
shutil.rmtree(Path(sys.argv[1]))
PY
  else
    echo "Container candidate build failed; source and failure evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

stage="$work/bookshelfng-container-platform-candidates"
mkdir -p "$stage/cloudron" "$stage/coop-cloud/bookshelfng" "$stage/catalogs" "$stage/startos" "$stage/unraid"
cp "$repo_root/packaging/legacy/cloudron/Dockerfile" "$stage/cloudron/Dockerfile"
cp "$repo_root/packaging/legacy/cloudron/CloudronManifest.json" "$stage/cloudron/CloudronManifest.json"
cp "$repo_root/packaging/legacy/cloudron/nginx.conf" "$stage/cloudron/nginx.conf"
cp "$repo_root/packaging/legacy/cloudron/migration-unavailable.html" "$stage/cloudron/migration-unavailable.html"
cp "$repo_root/packaging/legacy/cloudron/cloudron-entrypoint.sh" "$stage/cloudron/cloudron-entrypoint.sh"
cp "$repo_root/packaging/legacy/cloudron/cloudron-backup.sh" "$stage/cloudron/cloudron-backup.sh"
cp "$repo_root/packaging/legacy/cloudron/cloudron-restore.sh" "$stage/cloudron/cloudron-restore.sh"
cp -a "$repo_root/packaging/legacy/coop-cloud/bookshelfng/." "$stage/coop-cloud/bookshelfng/"
cp "$repo_root/packaging/legacy/catalogs/compose.yaml" "$stage/catalogs/compose.yaml"
cp "$repo_root/packaging/legacy/catalogs/.env.sample" "$stage/catalogs/.env.sample"
python3 - "$repo_root/packaging/legacy/startos/candidate" "$stage/startos" <<'PY'
from pathlib import Path
import shutil
import sys

source = Path(sys.argv[1])
destination = Path(sys.argv[2])
ignored = {"node_modules", "javascript", ".git", ".startos"}
for item in source.iterdir():
    if item.name in ignored or (item.is_file() and item.suffix == ".s9pk"):
        continue
    target = destination / item.name
    if item.is_dir():
        shutil.copytree(item, target, symlinks=True)
    else:
        shutil.copy2(item, target, follow_symlinks=False)
PY
cp "$repo_root/packaging/legacy/unraid/bookshelfng.xml" "$stage/unraid/bookshelfng.xml"

python3 - "$stage/startos/icon.png" <<'PY'
from pathlib import Path
import sys

icon = Path(sys.argv[1])
if not icon.is_file() or icon.stat().st_size > 40 * 1024:
    raise SystemExit("StartOS candidate icon must be present and no larger than 40 KiB.")
PY

python3 - "$stage/cloudron/CloudronManifest.json" "$VERSION" <<'PY'
import json
import pathlib
import sys

manifest_path = pathlib.Path(sys.argv[1])
version = sys.argv[2]
manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
if manifest.get("id") != "tech.snapetech.bookshelfng":
    raise SystemExit("Cloudron manifest does not preserve the BookshelfNG app identity.")
manifest["version"] = version
manifest["upstreamVersion"] = version
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
PY

python3 - "$stage" "$image" "$VERSION" <<'PY'
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
image = sys.argv[2]
version = sys.argv[3]
startos_manifest = root / "startos" / "startos" / "manifest" / "index.ts"
startos_text = startos_manifest.read_text(encoding="utf-8")
placeholder = "ghcr.io/snapetech-org/chaptarrng@sha256:__CHAPTARRNG_IMAGE_DIGEST__"
if startos_text.count(placeholder) != 1:
    raise SystemExit("StartOS manifest did not contain exactly one image digest placeholder.")
startos_manifest.write_text(startos_text.replace(placeholder, image), encoding="utf-8")

startos_version_file = root / "startos" / "startos" / "versions" / "current.ts"
startos_version_text = startos_version_file.read_text(encoding="utf-8")
old_startos_version = "version: '__CHAPTARRNG_STARTOS_VERSION__'"
new_startos_version = "version: '" + version + ":1'"
if startos_version_text.count(old_startos_version) != 1:
    raise SystemExit("StartOS candidate does not contain exactly one package-version placeholder.")
startos_version_file.write_text(startos_version_text.replace(old_startos_version, new_startos_version), encoding="utf-8")

legacy_tag = "ghcr.io/snapetech-org/chaptarrng:latest"
dockerfile_old = (
    "# This standard ChaptarrNG image tag is a source candidate. Before a Cloudron update\n"
    "# is accepted, replace it with the exact published multi-architecture digest\n"
    "# that passed the migration and rollback rehearsals.\n"
    "FROM " + legacy_tag
)
dockerfile_new = (
    "# This ChaptarrNG image digest is pinned to the release that produced this candidate.\n"
    "FROM " + image
)

replacements = {
    root / "cloudron" / "Dockerfile": (dockerfile_old, dockerfile_new),
    root / "coop-cloud" / "bookshelfng" / "compose.yml": (legacy_tag, image),
    root / "coop-cloud" / "bookshelfng" / ".env.sample": (
        "CHAPTARRNG_IMAGE=" + legacy_tag,
        "CHAPTARRNG_IMAGE=" + image,
    ),
    root / "catalogs" / "compose.yaml": (legacy_tag, image),
    root / "catalogs" / ".env.sample": (
        "CHAPTARRNG_IMAGE=" + legacy_tag,
        "CHAPTARRNG_IMAGE=" + image,
    ),
}

for path, (old, new) in replacements.items():
    text = path.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise SystemExit(f"{path.relative_to(root)} did not contain exactly one expected standard image reference.")
    path.write_text(text.replace(old, new), encoding="utf-8")

compose_path = root / "coop-cloud" / "bookshelfng" / "compose.yml"
text = compose_path.read_text(encoding="utf-8")
version_placeholder = "__CHAPTARRNG_VERSION__"
if text.count(version_placeholder) != 1:
    raise SystemExit("Co-op Cloud recipe did not contain exactly one ChaptarrNG version placeholder.")
compose_path.write_text(text.replace(version_placeholder, version), encoding="utf-8")

unraid_path = root / "unraid" / "bookshelfng.xml"
unraid_text = unraid_path.read_text(encoding="utf-8")
unraid_image = "<Repository>" + legacy_tag + "</Repository>"
if unraid_text.count(unraid_image) != 1:
    raise SystemExit("Unraid candidate does not preserve the BookshelfNG package identity on the standard ChaptarrNG image.")
unraid_path.write_text(unraid_text.replace(unraid_image, "<Repository>" + image + "</Repository>"), encoding="utf-8")
PY

cat > "$stage/README.md" <<'EOF'
# BookshelfNG container-platform handoff candidates

ChaptarrNG release: @VERSION@

Release multi-architecture image: `@IMAGE@`

This bundle contains the Cloudron app source, Co-op Cloud recipe, self-hosting
Compose candidate and its `.env.sample`, Unraid template, and StartOS package
source. Container platform candidates pin the exact image digest above. The
StartOS package version is @VERSION@:1 so each ChaptarrNG release advances the
existing package channel. The existing BookshelfNG app, stack, package,
container, and volume identities remain in place.

Every image-backed candidate pins the same standard ChaptarrNG image digest.
The standard image does not infer provider mode from an old tag. Preserve the
BookshelfNG source's `HARDCOVER=true` or `HARDCOVER=false` setting in channel
configuration; if it is unavailable, resolve the provider mode before
migration. ChaptarrNG stops conversion when the required mode is absent or
invalid. Existing package, service, volume, and host-port identities are kept
by platform configuration; no separate ChaptarrNG image target or fresh-install
port default is used.

These files are handoff candidates for their platform maintainers. They do not
update external catalogs or establish platform install, backup, or rollback
acceptance. The Unraid XML preserves the existing BookshelfNG CA identity and
points future template checks to the ChaptarrNG-owned candidate path. The
external Unraid catalog still needs the template change accepted and delivered.
Keep the current channel installed until that platform's handoff has been
accepted.
EOF

python3 - "$stage/README.md" "$VERSION" "$image" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
text = text.replace("@VERSION@", sys.argv[2]).replace("@IMAGE@", sys.argv[3])
if "@VERSION@" in text or "@IMAGE@" in text:
    raise SystemExit("The container-platform candidate README contains an unresolved release placeholder.")
path.write_text(text, encoding="utf-8")
PY

mkdir -p -- "$OUTPUT_DIR"
asset="ChaptarrNG-${VERSION}-bookshelfng-container-platform-candidates.tar.gz"
tar -czf "$OUTPUT_DIR/$asset" -C "$work" "$(basename "$stage")"
(cd "$OUTPUT_DIR" && sha256sum "$asset" > "$asset.sha256")

echo "Built digest-pinned BookshelfNG container-platform candidates for ChaptarrNG $VERSION at $OUTPUT_DIR"
