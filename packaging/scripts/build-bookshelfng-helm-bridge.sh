#!/usr/bin/env bash
set -euo pipefail

: "${VERSION:?VERSION is required}"
: "${IMAGE_DIGEST:?IMAGE_DIGEST is required}"
: "${OUTPUT_DIR:?OUTPUT_DIR is required}"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "VERSION must be a stable MAJOR.MINOR.PATCH value." >&2
  exit 2
fi

if [[ ! "$IMAGE_DIGEST" =~ ^sha256:[a-f0-9]{64}$ ]]; then
  echo "IMAGE_DIGEST must be sha256 followed by 64 lowercase hexadecimal characters." >&2
  exit 2
fi

if ! command -v helm >/dev/null 2>&1; then
  echo "Helm is required to build the BookshelfNG chart bridge." >&2
  exit 127
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
source_chart="$repo_root/charts/bookshelfng"
if [[ ! -f "$source_chart/Chart.yaml" || ! -f "$source_chart/values.yaml" ]]; then
  echo "BookshelfNG compatibility chart source is incomplete: $source_chart" >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"
work_dir="$(mktemp -d "$OUTPUT_DIR/.bookshelfng-helm-bridge.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT
chart_dir="$work_dir/bookshelfng"
cp -a "$source_chart" "$chart_dir"

python3 - "$chart_dir/values.yaml" "$IMAGE_DIGEST" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
digest = sys.argv[2]
text = path.read_text(encoding="utf-8")
replacements = [
    (r"(?m)^  repository: .*?$", "  repository: ghcr.io/snapetech-org/chaptarrng"),
    (r'(?m)^  digest: ""$', f'  digest: "{digest}"'),
]
for pattern, replacement in replacements:
    text, count = re.subn(pattern, replacement, text, count=1)
    if count != 1:
        raise SystemExit(f"Expected exactly one chart value matching {pattern!r}.")
path.write_text(text, encoding="utf-8")
PY

cat > "$chart_dir/README.md" <<'EOF'
# BookshelfNG Helm migration bridge

This version retains the BookshelfNG OCI chart name and Kubernetes resource
identities while pinning the ChaptarrNG @VERSION@ image at
@IMAGE_DIGEST@. The existing Helm release name, namespace, PVC, values, and
service settings must be preserved.

Before upgrading, stop BookshelfNG, take an independent backup or storage
snapshot, and complete the migration and rollback rehearsal. Keep the old
`image.tag` and any existing `HARDCOVER` or `HARDCOVER_NATIVE` setting in the
reused values. The old chart defaults to its native Hardcover image; a
retained `softcover` tag, readable `HARDCOVER=false`, or
`HARDCOVER_NATIVE=false` preserves Goodreads mode. If a Secret makes the
effective mode ambiguous (for example, `HARDCOVER=true` with
`HARDCOVER_NATIVE` from a Secret), set `bookshelfMigration.providerMode` to
`hardcover` or `softcover` explicitly.

```sh
export RELEASE_NAME=bookshelfng
export NAMESPACE=your-existing-namespace

helm upgrade "$RELEASE_NAME" oci://ghcr.io/snapetech/bookshelfng/bookshelfng \
  --version @VERSION@ \
  --namespace "$NAMESPACE" \
  --reuse-values \
  --set-string image.repository=ghcr.io/snapetech-org/chaptarrng \
  --set-string image.digest=@IMAGE_DIGEST@ \
  --set bookshelfMigration.confirmed=true
# Add --set bookshelfMigration.providerMode=hardcover when the old source
# used native Hardcover IDs and its mode is not saved in the existing values.
```

The published chart is available only after both retirement-readiness gates
are enabled, the migration bridge gate is enabled, and the bridge is explicitly
requested. Keep one replica while SQLite is migrated. Review the rendered
manifest and retain the source backup until the converted instance is accepted.
The chart enables the image's BookshelfNG handoff supervisor and sets the
resolved provider mode in the pod environment so failed first-start migration
restores the original database and configuration.

Pinned or offline chart archives require a manual update.
EOF

python3 - "$chart_dir/README.md" "$VERSION" "$IMAGE_DIGEST" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
text = text.replace("@VERSION@", sys.argv[2]).replace("@IMAGE_DIGEST@", sys.argv[3])
if "@VERSION@" in text or "@IMAGE_DIGEST@" in text:
    raise SystemExit("The generated chart README contains an unresolved placeholder.")
path.write_text(text, encoding="utf-8")
PY

for provider_mode in hardcover softcover; do
  helm lint "$chart_dir" \
    --set bookshelfMigration.confirmed=true \
    --set "bookshelfMigration.providerMode=$provider_mode" \
    --set-string "image.tag=$provider_mode" \
    --set-string "image.digest=$IMAGE_DIGEST"

  helm template bookshelfng "$chart_dir" \
    --namespace bookshelf-test \
    --set bookshelfMigration.confirmed=true \
    --set "bookshelfMigration.providerMode=$provider_mode" \
    --set-string "image.tag=$provider_mode" \
    --set-string "image.digest=$IMAGE_DIGEST" \
    > "$work_dir/rendered-$provider_mode.yaml"
  grep -Fq "image: \"ghcr.io/snapetech-org/chaptarrng@${IMAGE_DIGEST}\"" "$work_dir/rendered-$provider_mode.yaml"
  grep -Fq 'claimName: bookshelfng-config' "$work_dir/rendered-$provider_mode.yaml"
  grep -Fq 'containerPort: 8787' "$work_dir/rendered-$provider_mode.yaml"
done

helm package "$chart_dir" \
  --version "$VERSION" \
  --app-version "$VERSION" \
  --destination "$OUTPUT_DIR"

package_path="$OUTPUT_DIR/bookshelfng-${VERSION}.tgz"
if [[ ! -s "$package_path" ]]; then
  echo "Helm did not create the expected chart package: $package_path" >&2
  exit 1
fi

echo "Built and validated $package_path for both BookshelfNG provider modes."
