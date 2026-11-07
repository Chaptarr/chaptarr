#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
workdir="$(mktemp -d)"
trap 'rm -rf -- "$workdir"' EXIT

data_dir="$workdir/source config"
backup_dir="$workdir/backup location"
fakebin="$workdir/bin"
mkdir -p "$data_dir" "$backup_dir" "$fakebin"
printf 'fixture database\n' > "$data_dir/readarr.db"
printf '<Config><Port>8787</Port></Config>\n' > "$data_dir/config.xml"
mkdir -p "$data_dir/logs"
printf 'fixture log\n' > "$data_dir/logs/Readarr.debug.txt"

cat > "$fakebin/docker" <<'DOCKER'
#!/usr/bin/env bash
if [[ "$1" == inspect ]]; then
  cat "$DOCKER_INSPECT_FILE"
  exit 0
fi
printf '%s\n' "$@" > "$DOCKER_ARGS_FILE"
DOCKER
chmod +x "$fakebin/docker"
export DOCKER_ARGS_FILE="$workdir/docker-args.txt"
export PATH="$fakebin:$PATH"

tool="$repo_root/scripts/bookshelfng-to-chaptarrng.sh"
image='ghcr.io/snapetech-org/chaptarrng:v0.9.973'
native_image='ghcr.io/snapetech-org/chaptarrng:v0.9.978'

if "$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" 2>/dev/null; then
  echo 'preview without an explicit provider mode should have been rejected' >&2
  exit 1
fi
[[ -z "$(find "$backup_dir" -maxdepth 1 -type f -print -quit)" ]]

nested_backup_dir="$data_dir/backup must stay outside"
if "$tool" snapshot --data-dir "$data_dir" --backup-dir "$nested_backup_dir" 2>/dev/null; then
  echo 'a backup directory inside appdata should have been rejected' >&2
  exit 1
fi
[[ ! -e "$nested_backup_dir" ]]

symlink_backup_parent="$workdir/source alias"
ln -s "$data_dir" "$symlink_backup_parent"
symlink_backup_dir="$symlink_backup_parent/backup must stay outside"
if "$tool" snapshot --data-dir "$data_dir" --backup-dir "$symlink_backup_dir" 2>/dev/null; then
  echo 'a backup directory aliased into appdata should have been rejected' >&2
  exit 1
fi
[[ ! -e "$data_dir/backup must stay outside" ]]

if "$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --provider-mode native-hardcover 2>/dev/null; then
  echo 'native Hardcover with an unsupported image version should have been rejected' >&2
  exit 1
fi
if "$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" \
  --image 'ghcr.io/snapetech-org/chaptarrng@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' \
  --provider-mode native-hardcover 2>/dev/null; then
  echo 'native Hardcover with an unverified image digest should have been rejected' >&2
  exit 1
fi
[[ -z "$(find "$backup_dir" -maxdepth 1 -type f -print -quit)" ]]

"$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --provider-mode goodreads
grep -Fxq -- '-preview-bookshelf-migration' "$DOCKER_ARGS_FILE"
grep -Fxq -- '-v' "$DOCKER_ARGS_FILE"
grep -Fxq -- "$data_dir:/config" "$DOCKER_ARGS_FILE"
grep -Fxq -- "$image" "$DOCKER_ARGS_FILE"
grep -Fxq -- 'HARDCOVER=false' "$DOCKER_ARGS_FILE"
grep -Fxq -- 'HARDCOVER_NATIVE=false' "$DOCKER_ARGS_FILE"

archive="$(find "$backup_dir" -maxdepth 1 -type f -name 'bookshelfng-preview-*.tar.gz' -print -quit)"
[[ -n "$archive" ]]
[[ "$(stat -c '%a' "$archive")" == 600 ]]
tar -tzf "$archive" | grep -Fxq 'source config/readarr.db'
tar -tzf "$archive" | grep -Fxq 'source config/config.xml'
tar -tzf "$archive" | grep -Fxq 'source config/logs/Readarr.debug.txt'

"$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$native_image" \
  --provider-mode native-hardcover
grep -Fxq -- 'HARDCOVER=true' "$DOCKER_ARGS_FILE"
grep -Fxq -- 'HARDCOVER_NATIVE=true' "$DOCKER_ARGS_FILE"

audio_data_dir="$workdir/audio only"
audio_backup_dir="$workdir/audio backup"
mkdir -p "$audio_data_dir" "$audio_backup_dir"
: > "$audio_data_dir/readarr.db"
printf 'audio database\n' > "$audio_data_dir/audioarr.db"
audio_output="$("$tool" snapshot --data-dir "$audio_data_dir" --backup-dir "$audio_backup_dir")"
grep -Fq -- 'Database selected for the ChaptarrNG handoff: audioarr.db' <<< "$audio_output"

dual_data_dir="$workdir/two populated databases"
dual_backup_dir="$workdir/two database backup"
mkdir -p "$dual_data_dir" "$dual_backup_dir"
printf 'readarr database\n' > "$dual_data_dir/readarr.db"
printf 'audio database\n' > "$dual_data_dir/audioarr.db"
dual_snapshot_output="$("$tool" snapshot --data-dir "$dual_data_dir" --backup-dir "$dual_backup_dir")"
dual_archive="$(sed -n 's/^Appdata archive: //p' <<< "$dual_snapshot_output")"
[[ -n "$dual_archive" ]]
tar -tzf "$dual_archive" | grep -Fxq 'two populated databases/readarr.db'
tar -tzf "$dual_archive" | grep -Fxq 'two populated databases/audioarr.db'

symlink_data_dir="$workdir/symlink candidate"
symlink_backup_dir="$workdir/symlink backup"
mkdir -p "$symlink_data_dir" "$symlink_backup_dir"
printf 'readarr database\n' > "$symlink_data_dir/readarr.db"
ln -s readarr.db "$symlink_data_dir/audioarr.db"
if "$tool" snapshot --data-dir "$symlink_data_dir" --backup-dir "$symlink_backup_dir" 2>/dev/null; then
  echo 'two non-empty legacy paths including a symlink should have been rejected' >&2
  exit 1
fi
[[ -z "$(find "$symlink_backup_dir" -maxdepth 1 -type f -print -quit)" ]]

before_count="$(find "$backup_dir" -maxdepth 1 -type f -name '*.tar.gz' | wc -l)"
if "$tool" apply --data-dir "$data_dir" --backup-dir "$backup_dir" --image latest \
  --provider-mode goodreads 2>/dev/null; then
  echo 'mutable image tag should have been rejected' >&2
  exit 1
fi
if "$tool" apply --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --provider-mode goodreads 2>/dev/null; then
  echo 'apply without explicit stopped-service confirmation should have been rejected' >&2
  exit 1
fi
if "$tool" apply --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --confirm-stopped 2>/dev/null; then
  echo 'apply without an explicit provider mode should have been rejected' >&2
  exit 1
fi
if "$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --provider-mode unknown 2>/dev/null; then
  echo 'unknown provider mode should have been rejected' >&2
  exit 1
fi
after_count="$(find "$backup_dir" -maxdepth 1 -type f -name '*.tar.gz' | wc -l)"
[[ "$before_count" == "$after_count" ]]

"$tool" apply --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$native_image" \
  --confirm-stopped --provider-mode native-hardcover
grep -Fxq -- '-migrate-bookshelf' "$DOCKER_ARGS_FILE"
grep -Fxq -- '-confirm-bookshelf-service-stopped' "$DOCKER_ARGS_FILE"
grep -Fxq -- 'HARDCOVER=true' "$DOCKER_ARGS_FILE"
grep -Fxq -- 'HARDCOVER_NATIVE=true' "$DOCKER_ARGS_FILE"

# The fake Docker command records arguments but does not run the migration.
# Model the target database that a successful apply creates before rollback.
printf 'converted target fixture\n' > "$data_dir/chaptarr.db"

printf 'rollback fixture\n' > "$data_dir/readarr.db.bookshelfng-backup-test.db"
"$tool" rollback-preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --bookshelf-backup 'readarr.db.bookshelfng-backup-test.db'
grep -Fxq -- '-preview-bookshelf-restore' "$DOCKER_ARGS_FILE"
grep -Fxq -- '-bookshelf-backup=/config/readarr.db.bookshelfng-backup-test.db' "$DOCKER_ARGS_FILE"

if "$tool" rollback-preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --bookshelf-backup '../readarr.db' 2>/dev/null; then
  echo 'rollback path traversal should have been rejected' >&2
  exit 1
fi

"$tool" rollback --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --bookshelf-backup 'readarr.db.bookshelfng-backup-test.db' --confirm-stopped
grep -Fxq -- '-restore-bookshelf' "$DOCKER_ARGS_FILE"
grep -Fxq -- '-confirm-bookshelf-service-stopped' "$DOCKER_ARGS_FILE"

export DOCKER_INSPECT_FILE="$workdir/source-inspect.json"
python3 - "$data_dir" "$DOCKER_INSPECT_FILE" <<'PY'
import json,sys
from pathlib import Path
Path(sys.argv[2]).write_text(json.dumps([{
    'Id':'fixture-source','Image':'sha256:fixture-image',
    'Mounts':[{'Destination':'/config','Source':sys.argv[1]}],
    'Config':{'Env':['METADATA_URL=https://captured-metadata.example','OTHER_SECRET=fixture-only']}
}]))
PY
"$tool" snapshot --data-dir "$data_dir" --backup-dir "$backup_dir" \
  --source-container fixture-source > "$workdir/metadata-capture.log"
captured_env="$(find "$backup_dir" -maxdepth 1 -name '*.metadata.env' -print -quit)"
[[ -n "$captured_env" ]]
grep -Fxq 'METADATA_URL=https://captured-metadata.example' "$captured_env"
[[ "$(stat -c '%a' "$captured_env")" == 600 ]]
! grep -Fq 'OTHER_SECRET' "$captured_env"
! grep -Fq 'https://captured-metadata.example' "$workdir/metadata-capture.log"
"$tool" preview --data-dir "$data_dir" --backup-dir "$backup_dir" --image "$image" \
  --provider-mode goodreads --metadata-env-file "$captured_env"
grep -Fxq -- '--env-file' "$DOCKER_ARGS_FILE"
grep -Fxq -- "$captured_env" "$DOCKER_ARGS_FILE"
! grep -Fq 'https://captured-metadata.example' "$DOCKER_ARGS_FILE"
if "$tool" snapshot --data-dir "$data_dir" --backup-dir "$backup_dir" \
  --source-container fixture-source --metadata-env-file "$captured_env" 2>/dev/null; then
  echo 'ambiguous source environment inputs should have been rejected' >&2
  exit 1
fi

echo 'BookshelfNG to ChaptarrNG handoff helper checks passed.'
