#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
if [[ -z "${TASK_SCRATCH_DIR:-}" || ! -d "$TASK_SCRATCH_DIR" ]]; then
  echo 'Set TASK_SCRATCH_DIR to an existing task-owned scratch directory.' >&2
  exit 2
fi

scratch=$(mktemp -d "$TASK_SCRATCH_DIR/bookshelfng-journal-fingerprint.XXXXXX")
printf 'Journal fingerprint test scratch: %s\n' "$scratch"
active_pid=''

cleanup() {
  status=$?
  trap - EXIT
  if [ -n "$active_pid" ]; then
    kill -TERM "$active_pid" 2>/dev/null || true
    wait "$active_pid" 2>/dev/null || true
    active_pid=''
  fi
  if [ "$status" -eq 0 ]; then
    rm -rf -- "$scratch"
    printf 'Removed journal fingerprint test scratch: %s\n' "$scratch"
  else
    printf 'Journal fingerprint test failed; preserving evidence at %s\n' "$scratch" >&2
  fi
  exit "$status"
}
trap cleanup EXIT

backup_dir="$scratch/postgres-backup"
journal_dir="$scratch/data/.chaptarrng-bookshelfng-journal/fingerprints"
mkdir -m 0700 "$backup_dir"
printf '%s' 'main-dump-fixture' > "$backup_dir/postgres-main.dump"
mkdir -m 0700 "$backup_dir/nested directory"
printf '%s' 'newline-path-fixture' > "$backup_dir/nested directory/$(printf 'line\nbreak.dump')"

journal() {
  BOOKSHELF_DATA_DIR="${BOOKSHELF_DATA_DIR:-$scratch/data}" \
    BOOKSHELF_JOURNAL_PRESERVE_OWNER=true \
    "$repo_root/packaging/legacy/scripts/bookshelfng-journal" "$@"
}

journal fingerprint source-postgres-backup "$backup_dir"
fingerprint_file="$journal_dir/source-postgres-backup"
first_fingerprint=$(cat "$fingerprint_file")
[[ "$first_fingerprint" =~ ^sha256:[a-f0-9]{64}$ ]]

relocated_backup_dir="$scratch/relocated-backup"
mkdir -m 0700 "$relocated_backup_dir"
cp -R "$backup_dir/." "$relocated_backup_dir/"
journal fingerprint relocated-postgres-backup "$relocated_backup_dir"
[[ "$(cat "$journal_dir/relocated-postgres-backup")" = "$first_fingerprint" ]]

journal fingerprint source-postgres-backup "$backup_dir"
[[ "$(cat "$fingerprint_file")" = "$first_fingerprint" ]]

printf '%s' 'log-dump-fixture' > "$backup_dir/postgres-log.dump"
journal fingerprint source-postgres-backup "$backup_dir" --refresh
updated_fingerprint=$(cat "$fingerprint_file")
[[ "$updated_fingerprint" =~ ^sha256:[a-f0-9]{64}$ ]]
[[ "$updated_fingerprint" != "$first_fingerprint" ]]

journal fingerprint missing-postgres-backup "$scratch/not-present"
[[ "$(cat "$journal_dir/missing-postgres-backup")" = missing ]]

printf 'Desktop journal fingerprints passed: directory %s -> %s; missing paths are recorded.\n' \
  "$first_fingerprint" "$updated_fingerprint"

app_dir="$scratch/desktop-postgres-app"
data_dir="$scratch/desktop-postgres-data"
desktop_scripts="$scratch/desktop-scripts"
mkdir -m 0700 "$app_dir" "$data_dir" "$desktop_scripts"
app="$app_dir/Chaptarr"
console="$app_dir/Chaptarr.Console"
install -m 0755 "$repo_root/packaging/legacy/desktop/bookshelfng-supervisor.sh" "$desktop_scripts/bookshelfng-supervisor.sh"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-journal" "$desktop_scripts/bookshelfng-journal"
supervisor="$desktop_scripts/bookshelfng-supervisor.sh"

cat > "$console" <<'SH'
#!/bin/sh
set -eu

if [ "${1:-}" = -exitimmediately ]; then shift; fi
data_dir=''
backup_dir=''
for argument in "$@"; do
  case "$argument" in
    -data=*) data_dir=${argument#-data=} ;;
    -bookshelf-postgres-backup=*) backup_dir=${argument#-bookshelf-postgres-backup=} ;;
  esac
done

case "${1:-}" in
  -check-bookshelf-migration)
    [ -f "$data_dir/.postgres-backup-ready" ]
    ;;
  -preview-bookshelf-migration|-check-bookshelf-postgres-backup)
    exit 0
    ;;
  -backup-bookshelf-postgres)
    [ -n "$backup_dir" ]
    printf '%s' 'postgres-main-fixture' > "$backup_dir/postgres-main.dump"
    printf '%s' 'postgres-log-fixture' > "$backup_dir/postgres-log.dump"
    : > "$data_dir/.postgres-backup-ready"
    ;;
  *)
    exit 3
    ;;
esac
SH

cat > "$app" <<'SH'
#!/bin/sh
trap 'exit 0' TERM INT
while :; do sleep 1; done
SH
chmod 0755 "$console" "$app"

CHAPTARR__POSTGRES__HOST=postgres.fixture \
BOOKSHELFNG_STARTUP_TIMEOUT_SECONDS=10 \
  sh "$supervisor" "$app" "$data_dir" > "$scratch/desktop-postgres-supervisor.log" 2>&1 &
active_pid=$!

state_file="$data_dir/.chaptarrng-bookshelfng-desktop-handoff"
for iteration in {1..100}; do
  if [ -f "$state_file" ] && [ "$(cat "$state_file")" = healthy ]; then
    break
  fi
  if ! kill -0 "$active_pid" 2>/dev/null; then
    wait "$active_pid" || true
    active_pid=''
    cat "$scratch/desktop-postgres-supervisor.log" >&2
    echo 'Desktop supervisor exited before its PostgreSQL handoff became healthy.' >&2
    exit 1
  fi
  sleep 0.1
done

if [ ! -f "$state_file" ] || [ "$(cat "$state_file")" != healthy ]; then
  cat "$scratch/desktop-postgres-supervisor.log" >&2
  echo 'Desktop supervisor did not complete the PostgreSQL handoff within the test deadline.' >&2
  exit 1
fi

postgres_backup_dir=$(find "$data_dir" -mindepth 1 -maxdepth 1 -type d -name '.chaptarrng-bookshelfng-postgres-backup.*' -print -quit)
journal_dir="$data_dir/.chaptarrng-bookshelfng-journal"
recorded_fingerprint=$(cat "$journal_dir/fingerprints/source-postgres-backup")
expected_fingerprint=$(
  BOOKSHELF_DATA_DIR="$data_dir" journal fingerprint expected-desktop-postgres-backup "$postgres_backup_dir" --refresh
  cat "$journal_dir/fingerprints/expected-desktop-postgres-backup"
)
[[ -n "$postgres_backup_dir" && -s "$postgres_backup_dir/postgres-main.dump" && -s "$postgres_backup_dir/postgres-log.dump" ]]
[[ "$recorded_fingerprint" = "$expected_fingerprint" ]]
[[ "$(cat "$journal_dir/outcomes/external-postgres-backup")" = retained ]]
[[ "$(cat "$journal_dir/fingerprints/target-artifact")" = "sha256:$(sha256sum "$app" | awk '{ print $1 }')" ]]
[[ "$(cat "$journal_dir/fingerprints/source-config")" = missing ]]
[[ "$(cat "$journal_dir/outcomes/source-artifact-fingerprints")" = captured ]]

kill -TERM "$active_pid"
if wait "$active_pid"; then
  supervisor_status=0
else
  supervisor_status=$?
fi
active_pid=''
[[ "$supervisor_status" -eq 143 ]]

printf 'Desktop supervisor PostgreSQL handoff passed: retained %s fingerprinted dumps at %s.\n' \
  "$(find "$postgres_backup_dir" -type f | wc -l | tr -d '[:space:]')" "$recorded_fingerprint"
