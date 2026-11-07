#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
task_scratch=${TASK_SCRATCH_DIR:-${TMPDIR:-/tmp}/chaptarrng-container-candidate-test}
mkdir -p -- "$task_scratch"
work=$(mktemp -d "$task_scratch/container-candidates.XXXXXX")
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
    echo "Container candidate test failed; evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

script="$repo_root/packaging/scripts/build-bookshelf-container-platform-candidates.sh"
digest="sha256:$(printf '%064d' 0 | tr '0' 'a')"
output="$work/output"
VERSION=0.9.978 CHAPTARRNG_IMAGE_DIGEST="$digest" OUTPUT_DIR="$output" bash "$script"

asset="ChaptarrNG-0.9.978-bookshelfng-container-platform-candidates.tar.gz"
test -s "$output/$asset"
(cd "$output" && sha256sum --check "$asset.sha256")
mkdir "$work/extracted"
tar -xzf "$output/$asset" -C "$work/extracted"
stage="$work/extracted/bookshelfng-container-platform-candidates"
image="ghcr.io/snapetech-org/chaptarrng@$digest"

python3 - "$stage/cloudron/CloudronManifest.json" <<'PY'
import json
import pathlib
import sys
manifest = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
assert manifest["id"] == "tech.snapetech.bookshelfng"
assert manifest["version"] == "0.9.978"
assert manifest["upstreamVersion"] == "0.9.978"
assert manifest["httpPort"] == 8787
assert manifest["persistentDirs"] == ["/config", "/books", "/downloads"]
assert manifest["minBoxVersion"] == "9.1.0"
assert manifest["backupCommand"] == "/usr/local/bin/chaptarr-cloudron-backup"
assert manifest["restoreCommand"] == "/usr/local/bin/chaptarr-cloudron-restore"
PY

grep -Fxq "FROM $image" "$stage/cloudron/Dockerfile"
test -s "$stage/cloudron/nginx.conf"
test -s "$stage/cloudron/cloudron-entrypoint.sh"
test -s "$stage/cloudron/cloudron-backup.sh"
test -s "$stage/cloudron/cloudron-restore.sh"
test -s "$stage/cloudron/migration-unavailable.html"
grep -Fq 'COPY nginx.conf /etc/nginx/nginx.conf' "$stage/cloudron/Dockerfile"
grep -Fq 'COPY migration-unavailable.html /usr/share/nginx/html/bookshelfng-unavailable.html' "$stage/cloudron/Dockerfile"
grep -Fq 'ENTRYPOINT ["/usr/local/bin/chaptarr-cloudron-entrypoint"]' "$stage/cloudron/Dockerfile"
grep -Fq 'export CHAPTARR__SERVER__PORT=8788' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'export CHAPTARR__SERVER__BINDADDRESS=127.0.0.1' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'listen 8787;' "$stage/cloudron/nginx.conf"
grep -Fq 'proxy_pass http://127.0.0.1:8788;' "$stage/cloudron/nginx.conf"
grep -Fq 'return 200 "healthy\n";' "$stage/cloudron/nginx.conf"
grep -Fq 'client_body_temp_path /run/chaptarr-nginx/client_body;' "$stage/cloudron/nginx.conf"
grep -Fq 'proxy_temp_path /run/chaptarr-nginx/proxy_temp;' "$stage/cloudron/nginx.conf"
grep -Fq 'mkdir -p' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'chmod 1777' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'export PUID=0' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'export PGID=0' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq '/usr/local/bin/chaptarr-cloudron-backup|/usr/local/bin/chaptarr-cloudron-restore)' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq '/bin/sh|sh|/bin/bash|bash)' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'exec "$@"' "$stage/cloudron/cloudron-entrypoint.sh"
grep -Fq 'COPY cloudron-backup.sh /usr/local/bin/chaptarr-cloudron-backup' "$stage/cloudron/Dockerfile"
grep -Fq 'COPY cloudron-restore.sh /usr/local/bin/chaptarr-cloudron-restore' "$stage/cloudron/Dockerfile"
grep -Fq 'sqlite3' "$stage/cloudron/Dockerfile"
grep -Fq 'PRAGMA integrity_check;' "$stage/cloudron/cloudron-backup.sh"
grep -Fq 'PRAGMA integrity_check;' "$stage/cloudron/cloudron-restore.sh"
grep -Fq 'chaptarr-cloudron-persistent-dirs.tar' "$stage/cloudron/cloudron-backup.sh"
grep -Fq 'chaptarr-cloudron-persistent-dirs.tar' "$stage/cloudron/cloudron-restore.sh"
for database in readarr.db audioarr.db chaptarr.db logs.db cache.db staging.db; do
  grep -Fq "$database" "$stage/cloudron/cloudron-backup.sh"
  grep -Fq "$database" "$stage/cloudron/cloudron-restore.sh"
done
grep -Fq "This ChaptarrNG image digest is pinned to the release" "$stage/cloudron/Dockerfile"
if grep -Fq "replace it with the exact published multi-architecture digest" "$stage/cloudron/Dockerfile"; then
  echo "Cloudron Dockerfile retained stale instructions to replace the compatibility tag." >&2
  exit 1
fi
compose_default_prefix="\${CHAPTARRNG_IMAGE:-"
compose_default="image: $compose_default_prefix$image}"
grep -Fq "$compose_default" "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fq "coop-cloud.\${STACK_NAME}.version=\${COOP_CLOUD_RECIPE_VERSION:-0.1.0+0.9.978}" "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fxq "CHAPTARRNG_IMAGE=$image" "$stage/coop-cloud/bookshelfng/.env.sample"
grep -Fq 'backupbot.backup=true' "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fq 'test: ["CMD", "/usr/local/bin/chaptarr-healthcheck"]' "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fq 'start_period: 245m' "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fq 'order: stop-first' "$stage/coop-cloud/bookshelfng/compose.yml"
grep -Fq 'failure_action: pause' "$stage/coop-cloud/bookshelfng/compose.yml"
if grep -Fq '__CHAPTARRNG_VERSION__' "$stage/coop-cloud/bookshelfng/compose.yml"; then
  echo "Co-op Cloud recipe retained its version placeholder." >&2
  exit 1
fi
test -s "$stage/coop-cloud/bookshelfng/README.md"
test -s "$stage/coop-cloud/bookshelfng/release/next"
if grep -Fq 'BOOKSHELFNG_HOST_PORT' "$stage/coop-cloud/bookshelfng/.env.sample"; then
  echo "Co-op Cloud recipe advertises an unused host-port setting." >&2
  exit 1
fi
grep -Fq "$compose_default" "$stage/catalogs/compose.yaml"
test -s "$stage/catalogs/.env.sample"
grep -Fxq "CHAPTARRNG_IMAGE=$image" "$stage/catalogs/.env.sample"
grep -Fq 'READARR__POSTGRES__PASSWORD=' "$stage/catalogs/.env.sample"
grep -Fq 'READARR__APP__INSTANCENAME=' "$stage/catalogs/.env.sample"
grep -Fq 'READARR__AUTH__APIKEY=' "$stage/catalogs/.env.sample"
grep -Fq 'READARR__SERVER__TRUSTEDPROXIES=' "$stage/catalogs/.env.sample"
grep -Fq 'READARR__LOG__SYSLOGSERVER=' "$stage/catalogs/.env.sample"
grep -Fq 'READARR__UPDATE__BRANCH=' "$stage/catalogs/.env.sample"
grep -Fq 'GOOGLE_BOOKS_API_KEY=' "$stage/catalogs/.env.sample"
grep -Fq 'EUROPEANA_API_KEY=' "$stage/catalogs/.env.sample"
grep -Fq 'OPEN_LIBRARY_CONTACT_EMAIL=' "$stage/catalogs/.env.sample"
grep -Fq 'MAX_PATH=' "$stage/catalogs/.env.sample"
grep -Fq 'MAX_NAME=' "$stage/catalogs/.env.sample"
grep -Fq "Release multi-architecture image: \`$image\`" "$stage/README.md"
grep -Fq "BookshelfNG source's \`HARDCOVER=true\` or \`HARDCOVER=false\` setting in channel" "$stage/README.md"
grep -Fq 'Existing package, service, volume, and host-port identities are kept' "$stage/README.md"

python3 - "$repo_root/packaging/legacy/unraid/bookshelfng.xml" "$stage/unraid/bookshelfng.xml" "$image" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

source = ET.parse(Path(sys.argv[1])).getroot()
candidate = ET.parse(Path(sys.argv[2])).getroot()
expected_image = sys.argv[3]
source_image = source.find("Repository")
candidate_image = candidate.find("Repository")
if source.tag != "Container" or candidate.tag != "Container":
    raise SystemExit("The Unraid source and release candidate must be container templates.")
if source_image is None or candidate_image is None:
    raise SystemExit("The Unraid source and release candidate must declare an image repository.")
if candidate_image.text != expected_image:
    raise SystemExit("The generated Unraid template is not pinned to the release image digest.")
source_image.text = expected_image
if ET.tostring(source) != ET.tostring(candidate):
    raise SystemExit("The generated Unraid template changed settings beyond the release image digest.")
PY

startos_manifest="$stage/startos/startos/manifest/index.ts"
startos_version="$stage/startos/startos/versions/current.ts"
test -s "$stage/startos/package-lock.json"
grep -Fq "id: 'bookshelfng'" "$startos_manifest"
grep -Fq "volumes: ['main', 'books', 'downloads', 'startos']" "$startos_manifest"
grep -Fq "arch: ['x86_64', 'aarch64']" "$startos_manifest"
grep -Fq "source: { dockerTag: '$image' }" "$startos_manifest"
if grep -Fq '__CHAPTARRNG_IMAGE_DIGEST__' "$startos_manifest"; then
  echo "StartOS manifest retained its source digest placeholder." >&2
  exit 1
fi
if grep -Fq 'docker.io/snapetech/bookshelfng:' "$startos_manifest"; then
  echo "StartOS manifest retained the BookshelfNG runtime image." >&2
  exit 1
fi
grep -Fq "version: '0.9.978:1'" "$startos_version"
grep -Fq "imageId: 'bookshelfng'" "$stage/startos/startos/main.ts"
grep -Fq "mountpoint: '/config'" "$stage/startos/startos/main.ts"
grep -Fq "mountpoint: '/books'" "$stage/startos/startos/main.ts"
grep -Fq "mountpoint: '/downloads'" "$stage/startos/startos/main.ts"
grep -Fq 'command: sdk.useEntrypoint()' "$stage/startos/startos/main.ts"
grep -Fq 'sdk.healthCheck.checkPortListening(effects, uiPort' "$stage/startos/startos/main.ts"
grep -Fq 'https://github.com/snapetech-org/chaptarrng' "$startos_manifest"
grep -Fq 'export const uiPort = 8787' "$stage/startos/startos/utils.ts"
npm --prefix "$stage/startos" ci --ignore-scripts --no-audit --no-fund
npm --prefix "$stage/startos" run check
npm --prefix "$stage/startos" run build

if VERSION=0.9.978 CHAPTARRNG_IMAGE_DIGEST=not-a-digest OUTPUT_DIR="$work/invalid" bash "$script" >/dev/null 2>&1; then
  echo "Container candidate builder accepted an unverified image reference." >&2
  exit 1
fi
test ! -e "$work/invalid"

if VERSION=0.9.978 CHAPTARRNG_IMAGE_DIGEST="$digest" OUTPUT_DIR="$output" bash "$script" >/dev/null 2>&1; then
  echo "Container candidate builder unexpectedly overwrote an existing output directory." >&2
  exit 1
fi

echo "BookshelfNG container-platform candidate bundle checks passed."
