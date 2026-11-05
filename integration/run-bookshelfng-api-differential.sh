#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK_DIR="${BOOKSHELFNG_DIFFERENTIAL_WORK_DIR:?Set a task-owned work directory}"
SOURCE_IMAGE="${BOOKSHELFNG_SOURCE_IMAGE:?Set the digest-pinned BookshelfNG image}"
TARGET_COMMIT="${CHAPTARRNG_TARGET_COMMIT:?Set the exact ChaptarrNG commit}"
REPORT_DIR="$WORK_DIR/evidence"
PROJECT="bookshelfng-api-differential-${GITHUB_RUN_ID:-local}-$$"
COMPOSE=(docker compose --project-name "$PROJECT" --file "$ROOT/integration/bookshelfng-api-differential.compose.yml")

if [[ -e "$WORK_DIR" ]]; then
  if [[ ! -d "$WORK_DIR" ]] || find "$WORK_DIR" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    printf 'Refusing to use a non-empty differential work directory: %s\n' "$WORK_DIR" >&2
    exit 1
  fi
fi

mkdir -p "$WORK_DIR/source/config" "$WORK_DIR/source/library" \
  "$WORK_DIR/target/config" "$WORK_DIR/target/library" "$REPORT_DIR"

export BOOKSHELFNG_SOURCE_IMAGE="$SOURCE_IMAGE"
export BOOKSHELFNG_SOURCE_CONFIG="$WORK_DIR/source/config"
export BOOKSHELFNG_SOURCE_LIBRARY="$WORK_DIR/source/library"
export CHAPTARRNG_TARGET_CONFIG="$WORK_DIR/target/config"
export CHAPTARRNG_TARGET_LIBRARY="$WORK_DIR/target/library"
export TEST_PUID="$(id -u)"
export TEST_PGID="$(id -g)"
export BOOKSHELFNG_API_KEY="$(openssl rand -hex 32)"
export CHAPTARRNG_API_KEY="$BOOKSHELFNG_API_KEY"
export CHAPTARRNG_TARGET_COMMIT="$TARGET_COMMIT"
export CHAPTARRNG_TARGET_IMAGE_ID="target-image-id-not-built"
export BOOKSHELFNG_FIXTURE_SHA256="runtime-seed-pending"
export BOOKSHELFNG_API_DIFFERENTIAL_SCRIPT="$ROOT/integration/bookshelfng-api-differential.mjs"
export BOOKSHELFNG_API_CONTRACT_SCRIPT="$ROOT/integration/bookshelfng-api-contract.mjs"
export BOOKSHELFNG_DIFFERENTIAL_EVIDENCE="$REPORT_DIR"

cleanup() {
  set +e
  "${COMPOSE[@]}" logs --no-color bookshelfng chaptarrng 2>&1 \
    | sed "s/${BOOKSHELFNG_API_KEY}/<redacted-api-key>/g" > "$REPORT_DIR/containers.log"
  "${COMPOSE[@]}" down --timeout 30 >/dev/null 2>&1
}
trap cleanup EXIT

python3 "$ROOT/integration/prepare-bookshelfng-smoke.py" \
  --expanded-fixture \
  --config "$BOOKSHELFNG_SOURCE_CONFIG" \
  --library "$BOOKSHELFNG_SOURCE_LIBRARY" \
  --schema "$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.Schema.158.sql" \
  --seed "$ROOT/src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.PopulatedMigration158.sql" \
  | tee "$REPORT_DIR/source-seed.log"

python3 "$ROOT/integration/normalize-bookshelfng-api-differential-fixture.py" \
  "$BOOKSHELFNG_SOURCE_CONFIG/readarr.db" \
  | tee "$REPORT_DIR/fixture-normalization.log"

docker pull "$BOOKSHELFNG_SOURCE_IMAGE"
source_image_id="$(docker image inspect --format '{{.Id}}' "$BOOKSHELFNG_SOURCE_IMAGE")"
printf 'BookshelfNG source image: %s (%s)\n' "$BOOKSHELFNG_SOURCE_IMAGE" "$source_image_id"
export BOOKSHELFNG_SOURCE_IMAGE_ID="$source_image_id"

# Let the pinned BookshelfNG build seed the database and write its real runtime
# config before taking the migration snapshot. This makes API defaults and
# config.xml match the installed source handoff rather than a hand-authored DB.
"${COMPOSE[@]}" up --detach bookshelfng
"${COMPOSE[@]}" run --rm --no-deps --entrypoint node differential -e '
const deadline = Date.now() + 180_000;
const endpoint = new URL("/ping", process.env.BOOKSHELFNG_API_URL);
while (Date.now() < deadline) {
  try {
    const response = await fetch(endpoint);
    if (response.ok) process.exit(0);
  } catch {}
  await new Promise(resolve => setTimeout(resolve, 1000));
}
console.error("BookshelfNG did not become ready within 180 seconds.");
process.exit(1);
'

python3 "$ROOT/integration/snapshot-bookshelfng-api-differential.py" \
  --source-database "$BOOKSHELFNG_SOURCE_CONFIG/readarr.db" \
  --target-database "$CHAPTARRNG_TARGET_CONFIG/readarr.db" \
  --source-config "$BOOKSHELFNG_SOURCE_CONFIG/config.xml" \
  --target-config "$CHAPTARRNG_TARGET_CONFIG/config.xml" \
  --source-library "$BOOKSHELFNG_SOURCE_LIBRARY" \
  --target-library "$CHAPTARRNG_TARGET_LIBRARY" \
  | tee "$REPORT_DIR/source-runtime-snapshot.log"

source_database_sha="$(sha256sum "$CHAPTARRNG_TARGET_CONFIG/readarr.db" | cut -d ' ' -f 1)"
printf 'BookshelfNG runtime-seeded snapshot SHA-256: %s\n' "$source_database_sha"
export BOOKSHELFNG_FIXTURE_SHA256="$source_database_sha"

# Exercise the same API mode that an installed, migrated ChaptarrNG instance uses.
python3 "$ROOT/integration/normalize-bookshelfng-api-differential-fixture.py" \
  --seed-target-migration-profile "$CHAPTARRNG_TARGET_CONFIG/readarr.db"

if [[ "${CHAPTARRNG_DIFFERENTIAL_SKIP_BUILD:-false}" == "true" ]]; then
  test "$(git -C "$ROOT" rev-parse HEAD)" = "$TARGET_COMMIT"
  if ! git -C "$ROOT" diff --quiet "$TARGET_COMMIT" -- \
    src frontend package.json yarn.lock patches Dockerfile.build \
    docker-entrypoint.sh docker-bookshelf-migration-detect.sh \
    docker-bookshelf-compat-supervisor.sh docker-bookshelf-liveness.sh \
    docker-healthcheck.sh; then
    printf 'Refusing to skip the image build because application inputs changed.\n' >&2
    exit 1
  fi
  untracked_app_inputs="$(git -C "$ROOT" ls-files --others --exclude-standard -- \
    src frontend package.json yarn.lock patches Dockerfile.build \
    docker-entrypoint.sh docker-bookshelf-migration-detect.sh \
    docker-bookshelf-compat-supervisor.sh docker-bookshelf-liveness.sh \
    docker-healthcheck.sh)"
  if [[ -n "$untracked_app_inputs" ]]; then
    printf 'Refusing to skip the image build because application inputs are untracked.\n%s\n' \
      "$untracked_app_inputs" >&2
    exit 1
  fi
  docker image inspect chaptarrng-bookshelfng-differential:local >/dev/null
  printf 'Reusing the validated local target image for comparator iteration.\n'
else
  "${COMPOSE[@]}" build --pull chaptarrng
fi
target_image_id="$(docker image inspect --format '{{.Id}}' chaptarrng-bookshelfng-differential:local)"
printf 'ChaptarrNG target commit: %s (%s)\n' "$CHAPTARRNG_TARGET_COMMIT" "$target_image_id"
export CHAPTARRNG_TARGET_IMAGE_ID="$target_image_id"

"${COMPOSE[@]}" up --detach chaptarrng
"${COMPOSE[@]}" run --rm differential
