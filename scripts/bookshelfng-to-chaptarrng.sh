#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  cat <<'USAGE'
Usage:
  bookshelfng-to-chaptarrng.sh snapshot --data-dir PATH --backup-dir PATH [--source-container NAME]
  bookshelfng-to-chaptarrng.sh preview  --data-dir PATH --backup-dir PATH --image IMAGE --provider-mode goodreads|native-hardcover [--merge-bookshelf-databases]
  bookshelfng-to-chaptarrng.sh apply   --data-dir PATH --backup-dir PATH --image IMAGE --confirm-stopped --provider-mode goodreads|native-hardcover [--merge-bookshelf-databases]
  bookshelfng-to-chaptarrng.sh rollback-preview --data-dir PATH --backup-dir PATH --image IMAGE --bookshelf-backup FILE
  bookshelfng-to-chaptarrng.sh rollback --data-dir PATH --backup-dir PATH --image IMAGE --bookshelf-backup FILE --confirm-stopped

IMAGE must be a versioned ghcr.io/snapetech-org/chaptarrng:vMAJOR.MINOR.PATCH
tag or an immutable sha256 digest. Every action first archives the complete
BookshelfNG appdata directory outside that directory. Preview and apply require
an explicit provider mode. The mode is passed only to the temporary ChaptarrNG
migration container; configure the same mode in the permanent replacement
container after migration. Native Hardcover mode requires a versioned ChaptarrNG
tag v0.9.978 or newer; digest references cannot be checked for that capability.
The optional merge flag enables an explicit SQLite merge when both
readarr.db and audioarr.db are populated; conflicting identities or settings
stop the conversion before either original database is changed.
Rollback actions use the current chaptarr.db and its retained source receipt;
they do not need the merge option. Dual-database rollback requires both
original BookshelfNG files to remain present and unchanged.
For container handoffs, --source-container NAME captures its inherited
METADATA_URL after verifying the /config mount. Preview/apply can use that
option or --metadata-env-file PATH; conversion retains it as a fallback.
Keep the file with the backup. Other runtime environment settings must still
be preserved in the permanent replacement; appdata cannot recover image defaults.
USAGE
}

fail() {
  printf 'Error: %s\n' "$*" >&2
  exit 2
}

resolve_directory_path() {
  local path="$1"
  local current="/"
  local candidate
  local component
  local -a components

  if [[ "$path" != /* ]]; then
    path="$(pwd -P)/$path"
  fi

  IFS='/' read -r -a components <<< "$path"
  for component in "${components[@]}"; do
    case "$component" in
      ''|.)
        continue
        ;;
      ..)
        if [[ "$current" != / ]]; then
          current="${current%/*}"
          [[ -n "$current" ]] || current=/
        fi
        ;;
      *)
        candidate="${current%/}/$component"
        if [[ -d "$candidate" ]]; then
          current="$(cd -- "$candidate" && pwd -P)" || return 1
        elif [[ -e "$candidate" || -L "$candidate" ]]; then
          return 1
        else
          current="$candidate"
        fi
        ;;
    esac
  done

  printf '%s\n' "$current"
}

require_native_hardcover_capable_image() {
  local major minor patch
  if [[ ! "$image" =~ ^ghcr\.io/snapetech-org/chaptarrng:v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
    fail 'native-hardcover requires a versioned ChaptarrNG image tag v0.9.978 or newer; digest references cannot be checked for provider-mode support'
  fi

  major=$((10#${BASH_REMATCH[1]}))
  minor=$((10#${BASH_REMATCH[2]}))
  patch=$((10#${BASH_REMATCH[3]}))
  if (( major < 0 || (major == 0 && minor < 9) || (major == 0 && minor == 9 && patch < 978) )); then
    fail 'native-hardcover requires a versioned ChaptarrNG image tag v0.9.978 or newer; this image does not support native provider identity migration'
  fi
}

action="${1:-}"
[[ -n "$action" ]] || { usage >&2; exit 2; }
shift

case "$action" in
  snapshot|preview|apply|rollback-preview|rollback) ;;
  -h|--help|help) usage; exit 0 ;;
  *) usage >&2; fail "unknown action: $action" ;;
esac

data_dir=""
backup_dir=""
image=""
bookshelf_backup=""
confirm_stopped=false
provider_mode=""
provider_mode_set=false
merge_bookshelf_databases=false
source_container=""
metadata_env_file=""

while (($#)); do
  case "$1" in
    --data-dir)
      (($# >= 2)) || fail '--data-dir needs a path'
      data_dir="$2"
      shift 2
      ;;
    --backup-dir)
      (($# >= 2)) || fail '--backup-dir needs a path'
      backup_dir="$2"
      shift 2
      ;;
    --image)
      (($# >= 2)) || fail '--image needs an image reference'
      image="$2"
      shift 2
      ;;
    --bookshelf-backup)
      (($# >= 2)) || fail '--bookshelf-backup needs a file name'
      bookshelf_backup="$2"
      shift 2
      ;;
    --provider-mode)
      (($# >= 2)) || fail '--provider-mode needs goodreads or native-hardcover'
      provider_mode="$2"
      provider_mode_set=true
      shift 2
      ;;
    --confirm-stopped)
      confirm_stopped=true
      shift
      ;;
    --merge-bookshelf-databases)
      merge_bookshelf_databases=true
      shift
      ;;
    --source-container)
      (($# >= 2)) || fail '--source-container needs a container name'
      source_container="$2"
      shift 2
      ;;
    --metadata-env-file)
      (($# >= 2)) || fail '--metadata-env-file needs a path'
      metadata_env_file="$2"
      shift 2
      ;;
    -h|--help|help)
      usage
      exit 0
      ;;
    *) fail "unknown option: $1" ;;
  esac
done

if [[ -n "$source_container" && -n "$metadata_env_file" ]]; then
  fail 'choose --source-container or --metadata-env-file, not both'
fi
if [[ -n "$source_container" || -n "$metadata_env_file" ]]; then
  [[ "$action" == snapshot || "$action" == preview || "$action" == apply ]] ||
    fail 'metadata environment capture applies only to snapshot, preview and apply'
fi
if [[ -n "$metadata_env_file" ]]; then
  [[ "$action" != snapshot ]] || fail '--metadata-env-file applies only to preview and apply'
  [[ -f "$metadata_env_file" && ! -L "$metadata_env_file" ]] || fail '--metadata-env-file must be a regular file'
  metadata_env_file="$(cd -- "$(dirname -- "$metadata_env_file")" && pwd -P)/$(basename -- "$metadata_env_file")"
fi

[[ -n "$data_dir" ]] || fail '--data-dir is required'
[[ "$data_dir" == /* ]] || fail '--data-dir must be an absolute path'
[[ -d "$data_dir" ]] || fail "data directory does not exist: $data_dir"
data_dir="$(cd -- "$data_dir" && pwd -P)"
[[ "$data_dir" != / ]] || fail 'refusing to archive the filesystem root'

for database_name in readarr.db audioarr.db; do
  for suffix in '' -journal -wal -shm; do
    database_path="$data_dir/$database_name$suffix"
    [[ ! -L "$database_path" ]] ||
      fail "BookshelfNG database paths must not be symbolic links: $database_name$suffix"
    [[ ! -e "$database_path" || -f "$database_path" ]] ||
      fail "BookshelfNG database paths must be regular files: $database_name$suffix"
  done
  if [[ ! -e "$data_dir/$database_name" && ( -s "$data_dir/$database_name-wal" || -s "$data_dir/$database_name-journal" ) ]]; then
    fail "BookshelfNG database $database_name is missing but its WAL or rollback journal contains data; preserve the database family and resolve recovery first"
  fi
done

if [[ "$action" == rollback-preview || "$action" == rollback ]]; then
  for suffix in '' -journal -wal -shm; do
    database_path="$data_dir/chaptarr.db$suffix"
    [[ ! -L "$database_path" ]] ||
      fail "ChaptarrNG database paths must not be symbolic links: chaptarr.db$suffix"
    [[ ! -e "$database_path" || -f "$database_path" ]] ||
      fail "ChaptarrNG database paths must be regular files: chaptarr.db$suffix"
  done
fi

sqlite_database_has_data() {
  local database="$1"
  [[ -s "$database" || -s "$database-wal" || -s "$database-journal" ]]
}

readarr_populated=false
audioarr_populated=false
if sqlite_database_has_data "$data_dir/readarr.db"; then readarr_populated=true; fi
if sqlite_database_has_data "$data_dir/audioarr.db"; then audioarr_populated=true; fi

case "$action" in
  preview|apply)
    if [[ "$readarr_populated" == true && "$audioarr_populated" == true && "$merge_bookshelf_databases" != true ]]; then
      fail 'both BookshelfNG databases contain data; use --merge-bookshelf-databases to opt into the explicit, conflict-failing merge'
    fi
    if [[ "$merge_bookshelf_databases" == true && ( "$readarr_populated" != true || "$audioarr_populated" != true || ! -f "$data_dir/readarr.db" || ! -f "$data_dir/audioarr.db" ) ]]; then
      fail '--merge-bookshelf-databases requires regular readarr.db and audioarr.db files with recoverable data'
    fi
    if [[ "$readarr_populated" == true ]]; then
      source_database="$data_dir/readarr.db"
    elif [[ "$audioarr_populated" == true ]]; then
      source_database="$data_dir/audioarr.db"
    else
      fail 'data directory must contain a populated readarr.db or audioarr.db SQLite database'
    fi
    ;;
  rollback-preview|rollback)
    source_database="$data_dir/chaptarr.db"
    [[ -f "$source_database" && ! -L "$source_database" ]] ||
      fail 'rollback requires a regular chaptarr.db target in the data directory'
    sqlite_database_has_data "$source_database" ||
      fail 'rollback requires recoverable data in chaptarr.db or its WAL/rollback journal'
    ;;
  snapshot)
    if [[ "$readarr_populated" == true ]]; then
      source_database="$data_dir/readarr.db"
    elif [[ "$audioarr_populated" == true ]]; then
      source_database="$data_dir/audioarr.db"
    elif [[ -f "$data_dir/chaptarr.db" && ! -L "$data_dir/chaptarr.db" && -s "$data_dir/chaptarr.db" ]]; then
      source_database="$data_dir/chaptarr.db"
    else
      source_database="$data_dir"
    fi
    ;;
esac

[[ -n "$backup_dir" ]] || fail '--backup-dir is required so you can choose a separate backup location'
backup_dir="$(resolve_directory_path "$backup_dir")" ||
  fail '--backup-dir must resolve through directory paths only'
case "$backup_dir/" in
  "$data_dir/"*) fail '--backup-dir must be outside the BookshelfNG data directory' ;;
esac

if [[ "$merge_bookshelf_databases" == true && "$action" != preview && "$action" != apply ]]; then
  fail '--merge-bookshelf-databases applies only to preview and apply'
fi
mkdir -p -- "$backup_dir"
backup_dir="$(cd -- "$backup_dir" && pwd -P)"
case "$backup_dir/" in
  "$data_dir/"*) fail '--backup-dir must be outside the BookshelfNG data directory' ;;
esac

case "$action" in
  snapshot) ;;
  preview|apply|rollback-preview|rollback)
    [[ -n "$image" ]] || fail '--image is required for migration commands'
    if [[ ! "$image" =~ ^ghcr\.io/snapetech-org/chaptarrng:v[0-9]+\.[0-9]+\.[0-9]+$ &&
          ! "$image" =~ ^ghcr\.io/snapetech-org/chaptarrng@sha256:[a-fA-F0-9]{64}$ ]]; then
      fail '--image must be a versioned ChaptarrNG release tag or an immutable sha256 digest'
    fi
    command -v docker >/dev/null 2>&1 || fail 'docker is required for migration commands'
    ;;
esac

case "$action" in
  apply|rollback)
    [[ "$confirm_stopped" == true ]] || fail "$action requires --confirm-stopped after stopping every process using this data directory"
    ;;
esac

case "$action" in
  preview|apply)
    [[ "$provider_mode_set" == true ]] ||
      fail "$action requires --provider-mode goodreads or native-hardcover; do not guess provider identity"
    case "$provider_mode" in
      goodreads) ;;
      native-hardcover) require_native_hardcover_capable_image ;;
      *) fail '--provider-mode must be goodreads or native-hardcover' ;;
    esac
    ;;
  *) [[ "$provider_mode_set" == false ]] || fail '--provider-mode applies only to preview and apply' ;;
esac

case "$action" in
  rollback-preview|rollback)
    [[ -n "$bookshelf_backup" ]] || fail '--bookshelf-backup is required for rollback commands'
    [[ "$bookshelf_backup" != */* && "$bookshelf_backup" != '.' && "$bookshelf_backup" != '..' ]] ||
      fail '--bookshelf-backup must be the name of a file directly inside --data-dir'
    [[ -f "$data_dir/$bookshelf_backup" && ! -L "$data_dir/$bookshelf_backup" ]] ||
      fail "BookshelfNG backup is not a regular file in the data directory: $bookshelf_backup"
    ;;
esac

command -v tar >/dev/null 2>&1 || fail 'tar is required to create the appdata archive'
data_parent="$(dirname -- "$data_dir")"
data_name="$(basename -- "$data_dir")"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
archive="$backup_dir/bookshelfng-${action}-${timestamp}-$$.tar.gz"
[[ ! -e "$archive" ]] || fail "backup archive already exists: $archive"

umask 077
if ! tar -czpf "$archive" -C "$data_parent" -- "$data_name"; then
  rm -f -- "$archive"
  fail 'could not create the appdata archive; no ChaptarrNG command was run'
fi
chmod 600 "$archive"
printf 'Appdata archive: %s\n' "$archive"
printf 'Database selected for the ChaptarrNG handoff: %s\n' "$(basename -- "$source_database")"

if [[ -n "$source_container" ]]; then
  metadata_env_file="${archive%.tar.gz}.metadata.env"
  capture_script="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)/capture-bookshelf-metadata-env.py"
  python3 "$capture_script" --source-container "$source_container" --data-dir "$data_dir" --output "$metadata_env_file"
fi

[[ "$action" != snapshot ]] || exit 0

container_args=(run --rm --pull=always)
if [[ -n "$metadata_env_file" ]]; then
  container_args+=(--env-file "$metadata_env_file")
fi
if [[ "$action" == preview || "$action" == apply ]]; then
  if [[ "$provider_mode" == native-hardcover ]]; then
    container_args+=(-e HARDCOVER=true -e HARDCOVER_NATIVE=true)
    printf 'Provider mode for conversion: native Hardcover\n'
  else
    container_args+=(-e HARDCOVER=false -e HARDCOVER_NATIVE=false)
    printf 'Provider mode for conversion: Goodreads\n'
  fi
fi
container_args+=(-v "$data_dir:/config" "$image" dotnet /app/Chaptarr.dll)
if [[ "$merge_bookshelf_databases" == true ]]; then
  container_args+=(-merge-bookshelf-databases)
fi
case "$action" in
  preview)
    container_args+=(-preview-bookshelf-migration -data=/config)
    ;;
  apply)
    container_args+=(-migrate-bookshelf -confirm-bookshelf-service-stopped -data=/config)
    ;;
  rollback-preview)
    container_args+=(-preview-bookshelf-restore "-bookshelf-backup=/config/$bookshelf_backup" -data=/config)
    ;;
  rollback)
    container_args+=(-restore-bookshelf -confirm-bookshelf-service-stopped "-bookshelf-backup=/config/$bookshelf_backup" -data=/config)
    ;;
esac

exec docker "${container_args[@]}"
