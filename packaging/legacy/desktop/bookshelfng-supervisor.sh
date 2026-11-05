#!/usr/bin/env sh
set -eu

if [ "$#" -lt 2 ]; then
  echo 'BookshelfNG desktop launcher requires the ChaptarrNG executable and data directory.' >&2
  exit 2
fi

app=$1
data_dir=$2
shift 2
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
journal_tool="$script_dir/bookshelfng-journal"

is_option_value() {
  case "$1" in
    -*) return 0 ;;
  esac

  normalized=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  case "$normalized" in
    /port|/port=*|/urlbase|/urlbase=*|/bindaddress|/bindaddress=*|/sslport|/sslport=*|\
    /enablessl|/enablessl=*|/sslcertpath|/sslcertpath=*|/sslcertpassword|/sslcertpassword=*|\
    /i|/u|/\?|/nobrowser|/nostart|/terminateexisting|/restart|/registerurl|\
    /nosingleinstancecheck|/exitimmediately|/installservice|/uninstallservice|\
    /migrate-bookshelf|/preview-bookshelf-migration|/check-bookshelf-migration|\
    /restore-bookshelf|/preview-bookshelf-restore|/check-bookshelf-postgres-backup|/preview-bookshelf-postgres-restore|\
    /backup-bookshelf-postgres|/restore-bookshelf-postgres|/confirm-bookshelf-service-stopped)
      return 0
      ;;
  esac
  return 1
}

record_journal() {
  BOOKSHELF_DATA_DIR="$data_dir" BOOKSHELF_JOURNAL_PRESERVE_OWNER=true "$journal_tool" "$@"
}
custom_data_argument=false
custom_data_count=0
expect_data_value=false
for argument in "$@"; do
  if [ "$expect_data_value" = true ]; then
    if [ -z "$argument" ] || is_option_value "$argument"; then
      echo 'BookshelfNG desktop launcher expected a data directory after -data, not another option.' >&2
      exit 2
    fi
    custom_data_count=$((custom_data_count + 1))
    data_dir=$argument
    expect_data_value=false
    custom_data_argument=true
    continue
  fi
  case "$argument" in
    -data|--data|/data)
      expect_data_value=true
      custom_data_argument=true
      ;;
    -data=*|--data=*|/data=*)
      selected_data_dir=${argument#*=}
      if [ -z "$selected_data_dir" ] || [ "${selected_data_dir#-}" != "$selected_data_dir" ]; then
        echo 'BookshelfNG desktop launcher expected a data directory after -data, not another option.' >&2
        exit 2
      fi
      custom_data_count=$((custom_data_count + 1))
      data_dir=$selected_data_dir
      custom_data_argument=true
      ;;
  esac
done
if [ "$expect_data_value" = true ] || [ "$custom_data_count" -gt 1 ] || [ -z "$data_dir" ]; then
  echo 'BookshelfNG desktop launcher received multiple or empty data-directory arguments.' >&2
  exit 2
fi
console="$(dirname "$app")/Chaptarr.Console"
if [ ! -x "$console" ]; then
  # Standalone release archives name the .NET apphost Chaptarr and route
  # utility modes through that same executable. Windows package layouts keep
  # a separate Chaptarr.Console entry point.
  console="$app"
fi
run_utility() {
  "$console" -exitimmediately "$@"
}
merge_sqlite_sources=false
preview_migration() {
  if [ "$merge_sqlite_sources" = true ]; then
    run_utility -preview-bookshelf-migration -merge-bookshelf-databases "-data=$data_dir"
  else
    run_utility -preview-bookshelf-migration "-data=$data_dir"
  fi
}
state_file="$data_dir/.chaptarrng-bookshelfng-desktop-handoff"
metadata_dir="$data_dir/.chaptarrng-bookshelfng-desktop-handoff-data"
lock_dir="$data_dir/.chaptarrng-bookshelfng-desktop.lock"
app_pid=''
app_launcher_pid=''
app_process_group=''
healthy=false
resuming=false

write_state() {
  status_tmp="$state_file.tmp-$$"
  printf '%s\n' "$1" > "$status_tmp"
  chmod 0600 "$status_tmp"
  mv -f "$status_tmp" "$state_file"

  if [ -x "$journal_tool" ]; then
    case "$1" in
      healthy) record_journal state healthy ;;
      rolled-back) record_journal state rolled-back ;;
      intervention-required) record_journal state intervention-required ;;
    esac
    record_journal outcome channel-state "$1"
  fi
}

record_channel() {
  if [ -x "$journal_tool" ]; then
    record_journal outcome channel desktop
    record_journal outcome desktop-launcher "${BOOKSHELFNG_DESKTOP_CHANNEL:-unknown}"
  fi
}

has_controlling_tty() {
  [ -t 0 ] && ( : </dev/tty >/dev/tty ) 2>/dev/null
}

process_is_running() {
  process_pid=$1
  if ! kill -0 "$process_pid" 2>/dev/null; then return 1; fi
  process_state=$(ps -o stat= -p "$process_pid" 2>/dev/null | awk '{print $1}' || true)
  case "$process_state" in Z*) return 1 ;; esac
  return 0
}

process_group_is_running() {
  process_group=$1
  case "$process_group" in ''|*[!0-9]*) return 1 ;; esac
  ps -eo pgid=,stat= 2>/dev/null | awk -v group="$process_group" '$1 == group && $2 !~ /^Z/ { live = 1 } END { exit !live }'
}

terminate_app_tree() {
  if [ -n "$app_process_group" ]; then
    kill -TERM -"$app_process_group" 2>/dev/null || true
    stop_deadline=$(( $(date +%s) + 30 ))
    while process_group_is_running "$app_process_group" && [ "$(date +%s)" -lt "$stop_deadline" ]; do sleep 1; done
    if process_group_is_running "$app_process_group"; then kill -KILL -"$app_process_group" 2>/dev/null || true; fi
  elif [ -n "$app_pid" ] && process_is_running "$app_pid"; then
    kill -TERM "$app_pid" 2>/dev/null || true
    stop_deadline=$(( $(date +%s) + 30 ))
    while process_is_running "$app_pid" && [ "$(date +%s)" -lt "$stop_deadline" ]; do sleep 1; done
    if process_is_running "$app_pid"; then kill -KILL "$app_pid" 2>/dev/null || true; fi
  fi
}

lock_owner_is_running() {
  process_pid=$1
  case "$process_pid" in ''|*[!0-9]*) return 1 ;; esac
  if ! kill -0 "$process_pid" 2>/dev/null; then return 1; fi
  process_state=$(ps -o stat= -p "$process_pid" 2>/dev/null | awk '{print $1}' || true)
  case "$process_state" in ''|Z*) return 1 ;; esac
  return 0
}

cleanup() {
  terminate_app_tree
  rm -rf "$lock_dir"
  rm -f "$state_file.tmp-$$"
}

launch_without_migration() {
  rm -rf "$lock_dir"
  trap - EXIT HUP INT TERM
  if [ "$custom_data_argument" = true ]; then
    exec "$app" -nobrowser "$@"
  fi
  exec "$app" -nobrowser "-data=$data_dir" "$@"
}

restore_source_database() {
  postgres_source=$(cat "$metadata_dir/postgres-source" 2>/dev/null || echo false)
  if [ "$postgres_source" = true ]; then
    postgres_backup_dir=$(cat "$metadata_dir/postgres-backup-directory" 2>/dev/null || true)
    if [ -n "$postgres_backup_dir" ] && [ -d "$postgres_backup_dir" ] &&
       find "$postgres_backup_dir" -type f -name 'postgres-main.dump' -print -quit | grep -q .; then
      run_utility -preview-bookshelf-postgres-restore -confirm-bookshelf-service-stopped \
        "-bookshelf-postgres-backup=$postgres_backup_dir" "-data=$data_dir" || return $?
      if find "$postgres_backup_dir" -type f \
        \( -name 'postgres-log.dump' -o -name 'postgres-cache.dump' \) -print -quit | grep -q .; then
        target_postgres_backup="$metadata_dir/target-postgres-log-cache"
        if ! mkdir -m 0700 "$target_postgres_backup"; then
          echo "Could not create a retained ChaptarrNG PostgreSQL log/cache backup directory: $target_postgres_backup" >&2
          return 1
        fi
        if ! run_utility -backup-bookshelf-postgres -confirm-bookshelf-service-stopped \
          -bookshelf-postgres-log-cache-only \
          "-bookshelf-postgres-backup=$target_postgres_backup" "-data=$data_dir"; then
          echo "Could not preserve the current ChaptarrNG PostgreSQL log/cache data at $target_postgres_backup" >&2
          return 1
        fi
        if ! find "$target_postgres_backup" -type f \
          \( -name 'postgres-log.dump' -o -name 'postgres-cache.dump' \) -print -quit | grep -q .; then
          rmdir "$target_postgres_backup"
        elif [ -x "$journal_tool" ]; then
          record_journal fingerprint rollback-target-postgres-log-cache "$target_postgres_backup" --refresh
          record_journal outcome rollback-target-postgres-log-cache retained
        fi
      fi
      run_utility -restore-bookshelf-postgres -confirm-bookshelf-service-stopped \
        "-bookshelf-postgres-backup=$postgres_backup_dir" "-data=$data_dir"
      return $?
    fi
    return 1
  fi

  select_sqlite_source
  if preview_migration >/dev/null 2>&1; then
    # The atomic converter left both BookshelfNG source databases untouched.
    return 0
  fi

  if [ "$merge_sqlite_sources" = true ] && ! sqlite_database_has_data "$data_dir/chaptarr.db"; then
    # A failed merge preview or pre-promotion attempt did not replace either
    # original source. Keep them available to BookshelfNG and allow the
    # operator to resolve the merge before trying ChaptarrNG again.
    return 0
  fi

  rollback_source="$sqlite_source"
  if [ "$merge_sqlite_sources" = true ]; then
    rollback_source="$data_dir/chaptarr.db"
  fi
  if [ -f "$rollback_source" ]; then
    old_backups="$metadata_dir/sqlite-backups-before"
    newest_backup=''
    newest_time=0
    for backup in "$rollback_source".bookshelfng-backup-*.db; do
      [ -f "$backup" ] || continue
      if [ -f "$old_backups" ] && grep -F -x -q "$(basename "$backup")" "$old_backups"; then continue; fi
      backup_time=$(stat -c %Y "$backup" 2>/dev/null || stat -f %m "$backup" 2>/dev/null || echo 0)
      case "$backup_time" in ''|*[!0-9]*) backup_time=0 ;; esac
      if [ "$backup_time" -ge "$newest_time" ]; then
        newest_time=$backup_time
        newest_backup=$backup
      fi
    done

    if [ -n "$newest_backup" ]; then
      run_utility -restore-bookshelf -confirm-bookshelf-service-stopped \
        "-bookshelf-backup=$newest_backup" "-data=$data_dir"
      return $?
    fi
  fi

  return 1
}

select_sqlite_source() {
  for database in readarr.db audioarr.db; do
    for suffix in '' -journal -wal -shm; do
      source_path="$data_dir/$database$suffix"
      if [ -L "$source_path" ] || { [ -e "$source_path" ] && [ ! -f "$source_path" ]; }; then
        write_state intervention-required
        echo "BookshelfNG desktop migration requires regular SQLite source files: $source_path" >&2
        exit 1
      fi
    done
    if [ ! -e "$data_dir/$database" ] && { [ -s "$data_dir/$database-wal" ] || [ -s "$data_dir/$database-journal" ]; }; then
      write_state intervention-required
      echo "BookshelfNG desktop migration found recovery data without its main database: $database. Preserve the database family and recover it before starting either application." >&2
      exit 1
    fi
  done
  if sqlite_database_has_data "$data_dir/readarr.db" && sqlite_database_has_data "$data_dir/audioarr.db"; then
    merge_sqlite_sources=true
    sqlite_source="$data_dir/readarr.db"
    return
  fi
  sqlite_source="$data_dir/readarr.db"
  if ! sqlite_database_has_data "$sqlite_source" && sqlite_database_has_data "$data_dir/audioarr.db"; then
    sqlite_source="$data_dir/audioarr.db"
  fi
}

sqlite_database_has_data() {
  database=$1
  [ -s "$database" ] || [ -s "$database-wal" ] || [ -s "$database-journal" ]
}

mkdir -p "$data_dir"
data_dir=$(cd "$data_dir" && pwd -P)
record_channel
if [ ! -x "$console" ]; then
  echo "ChaptarrNG desktop migration utility is missing: $console" >&2
  exit 1
fi

if ! mkdir -m 0700 "$lock_dir" 2>/dev/null; then
  previous_pid=$(cat "$lock_dir/pid" 2>/dev/null || true)
  case "$previous_pid" in ''|*[!0-9]*) previous_pid='' ;; esac
  if [ -n "$previous_pid" ] && ! lock_owner_is_running "$previous_pid"; then
    rm -rf "$lock_dir"
    mkdir -m 0700 "$lock_dir"
  elif [ -z "$previous_pid" ]; then
    sleep 1
    previous_pid=$(cat "$lock_dir/pid" 2>/dev/null || true)
    case "$previous_pid" in ''|*[!0-9]*) previous_pid='' ;; esac
    if [ -z "$previous_pid" ] || ! lock_owner_is_running "$previous_pid"; then
      rm -rf "$lock_dir"
      mkdir -m 0700 "$lock_dir"
    else
      echo "Another BookshelfNG-compatible desktop startup is using $data_dir." >&2
      exit 1
    fi
  else
    echo "Another BookshelfNG-compatible desktop startup is using $data_dir." >&2
    exit 1
  fi
fi
printf '%s\n' "$$" > "$lock_dir/pid"
chmod 0600 "$lock_dir/pid"
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

# The source standalone launchers defaulted to Goodreads. Keep that choice for
# migrated installs unless the operator already configured Hardcover mode.
export HARDCOVER="${HARDCOVER:-false}"
export CHAPTARR_DEFAULT_PORT="${CHAPTARR_DEFAULT_PORT:-8787}"
export CHAPTARR_PACKAGE_UPDATE_MECHANISM="${CHAPTARR_PACKAGE_UPDATE_MECHANISM:-External}"

handoff_state=$(cat "$state_file" 2>/dev/null || true)
case "$handoff_state" in
  intervention-required)
    echo "A previous BookshelfNG desktop migration needs recovery. Preserve the source and target backups in $data_dir and review the migration journal before retrying." >&2
    exit 1
    ;;
  rolled-back)
    if [ "${BOOKSHELFNG_RETRY_MIGRATION:-false}" != true ]; then
      echo 'The prior desktop migration was rolled back. Reopen the original BookshelfNG app, or set BOOKSHELFNG_RETRY_MIGRATION=true after resolving its startup failure.' >&2
      exit 1
    fi
    rm -f "$state_file"
    rm -rf "$metadata_dir"
    handoff_state=''
    if [ -x "$journal_tool" ]; then record_journal state detected; fi
    ;;
  healthy)
    if run_utility -check-bookshelf-migration "-data=$data_dir" >/dev/null 2>&1; then
      launch_without_migration "$@"
    fi
    write_state intervention-required
    echo 'The desktop handoff was marked healthy, but its migrated database no longer passes the compatibility check. Preserve it for recovery.' >&2
    exit 1
    ;;
  starting)
    if run_utility -check-bookshelf-migration "-data=$data_dir" >/dev/null 2>&1; then
      resuming=true
    elif restore_source_database; then
      write_state rolled-back
      echo 'A prior desktop startup stopped before its migration health check completed. The original BookshelfNG database remains available; reopen the prior app or explicitly retry migration.' >&2
      exit 1
    else
      write_state intervention-required
      echo 'A prior desktop startup stopped before health validation and no safe source restore completed. The current database and retained backups were left for recovery.' >&2
      exit 1
    fi
    ;;
  '' ) ;;
  *)
    write_state intervention-required
    echo "Unknown desktop migration state '$handoff_state'. Preserve the handoff record and inspect it before starting." >&2
    exit 1
    ;;
esac

if [ "$resuming" != true ] && run_utility -check-bookshelf-migration "-data=$data_dir" >/dev/null 2>&1; then
  launch_without_migration "$@"
fi

postgres_source=false
postgres_environment_override=false
for name in Chaptarr__Postgres__Host CHAPTARR__POSTGRES__HOST Readarr__Postgres__Host READARR__POSTGRES__HOST Audioarr__Postgres__Host AUDIOARR__POSTGRES__HOST Bookshelf__Postgres__Host BOOKSHELF__POSTGRES__HOST; do
  if value=$(printenv "$name" 2>/dev/null); then
    postgres_environment_override=true
    if [ -n "$value" ]; then postgres_source=true; fi
    break
  fi
done
if [ "$postgres_environment_override" != true ] && [ -f "$data_dir/config.xml" ] &&
   grep -Eiq '<PostgresHost>[^<[:space:]][^<]*</PostgresHost>' "$data_dir/config.xml"; then
  postgres_source=true
fi

if [ "$postgres_source" = true ]; then
  sqlite_source="$data_dir/readarr.db"
else
  select_sqlite_source
fi

if [ "$resuming" != true ] && ! sqlite_database_has_data "$sqlite_source" && [ "$postgres_source" != true ]; then
  launch_without_migration "$@"
fi

if [ "$resuming" != true ]; then
  migration_mode=${BOOKSHELFNG_MIGRATION_MODE:-automatic}
  case "$migration_mode" in
    automatic) ;;
    manual)
      manual_message='BookshelfNG conversion is deferred. The source database was left untouched. Preview and apply the conversion using the ChaptarrNG Console; full steps: https://github.com/snapetech-org/chaptarrng/blob/main/docs/MIGRATE_FROM_BOOKSHELFNG.md'
      if command -v zenity >/dev/null 2>&1; then
        zenity --info --title='ChaptarrNG migration deferred' --text="$manual_message" >/dev/null 2>&1 || true
      fi
      printf '%s\n' 'BookshelfNG conversion was deferred; its source database was not changed.' \
        'Extract the ChaptarrNG package, stop BookshelfNG, then run:' >&2
      if [ "$merge_sqlite_sources" = true ]; then
        printf '  "%s" -preview-bookshelf-migration -merge-bookshelf-databases "-data=%s" -exitimmediately\n' "$console" "$data_dir" >&2
        printf '  "%s" -migrate-bookshelf -merge-bookshelf-databases -confirm-bookshelf-service-stopped "-data=%s" -exitimmediately\n' "$console" "$data_dir" >&2
      else
        printf '  "%s" -preview-bookshelf-migration "-data=%s" -exitimmediately\n' "$console" "$data_dir" >&2
        printf '  "%s" -migrate-bookshelf -confirm-bookshelf-service-stopped "-data=%s" -exitimmediately\n' "$console" "$data_dir" >&2
      fi
      exit 0
      ;;
    ask)
      # A terminal launch should keep the choice in the terminal even when
      # the desktop session also provides zenity. Desktop launches normally
      # have no controlling terminal and continue to use the graphical prompt.
      if has_controlling_tty; then
        printf 'Replace BookshelfNG with ChaptarrNG now? A still-legacy database will be migrated automatically. [y/N] ' >/dev/tty
        IFS= read -r migration_choice </dev/tty || migration_choice=
        case "$migration_choice" in
          y|Y|yes|YES|Yes) ;;
          *) echo 'BookshelfNG conversion deferred. The source database was left untouched.' >&2; exit 0 ;;
        esac
      elif command -v zenity >/dev/null 2>&1; then
        if ! zenity --question --default-cancel --ok-label='Replace now' --cancel-label='Not now' --title='ChaptarrNG migration' \
          --text='Replace BookshelfNG with ChaptarrNG now? A still-legacy database will be migrated automatically and its source retained for rollback.'; then
          zenity --info --title='Migration deferred' \
            --text='No database changes were made. Relaunch your previous AppImage or restore the previous Flatpak deployment. Use the migration guide to convert manually.' >/dev/null 2>&1 || true
          echo 'BookshelfNG conversion deferred. The source database was left untouched.' >&2
          exit 0
        fi
      else
        echo 'MigrationMode=ask needs zenity or an interactive terminal. The source database was left untouched.' >&2
        exit 1
      fi
      ;;
    *)
      echo 'BOOKSHELFNG_MIGRATION_MODE must be automatic, ask, or manual.' >&2
      exit 2
      ;;
  esac
fi

isolated_app_process=false
if command -v setsid >/dev/null 2>&1 && setsid --help 2>&1 | grep -q -- '--wait'; then
  isolated_app_process=true
fi

if [ "$resuming" != true ]; then
  if ! preview_migration >/dev/null 2>&1; then
    write_state intervention-required
    echo 'BookshelfNG migration preflight failed. The source database was not changed; inspect the ChaptarrNG migration journal before retrying.' >&2
    exit 1
  fi
  if [ -x "$journal_tool" ]; then
    record_journal state detected
    record_journal outcome preflight verified
    record_journal state preflighted
  fi

  mkdir -m 0700 -p "$metadata_dir"
  printf '%s\n' "$postgres_source" > "$metadata_dir/postgres-source"
  chmod 0600 "$metadata_dir/postgres-source"
  old_backups="$metadata_dir/sqlite-backups-before"
  : > "$old_backups"
  rollback_source="$sqlite_source"
  if [ "$merge_sqlite_sources" = true ]; then
    rollback_source="$data_dir/chaptarr.db"
  fi
  for backup in "$rollback_source".bookshelfng-backup-*.db; do
    [ -f "$backup" ] || continue
    basename "$backup" >> "$old_backups"
  done
  chmod 0600 "$old_backups"

  if [ "$postgres_source" = true ]; then
    postgres_backup_dir=$(mktemp -d "$data_dir/.chaptarrng-bookshelfng-postgres-backup.XXXXXX")
    chmod 0700 "$postgres_backup_dir"
    printf '%s\n' "$postgres_backup_dir" > "$metadata_dir/postgres-backup-directory"
    chmod 0600 "$metadata_dir/postgres-backup-directory"
    if ! run_utility -check-bookshelf-postgres-backup -confirm-bookshelf-service-stopped "-data=$data_dir"; then
      write_state intervention-required
      echo 'BookshelfNG PostgreSQL backup preflight failed. The source database was not changed.' >&2
      exit 1
    fi
    if ! run_utility -backup-bookshelf-postgres -confirm-bookshelf-service-stopped \
        "-bookshelf-postgres-backup=$postgres_backup_dir" "-data=$data_dir"; then
      write_state intervention-required
      echo "BookshelfNG PostgreSQL backup failed; migration is blocked. Preserve $postgres_backup_dir for review." >&2
      exit 1
    fi
    if [ -x "$journal_tool" ]; then
      record_journal fingerprint source-postgres-backup "$postgres_backup_dir"
      record_journal outcome external-postgres-backup retained
    fi
  fi

  if [ -x "$journal_tool" ]; then
    record_journal fingerprint target-artifact "$app"
    record_journal fingerprint source-config "$data_dir/config.xml"
    record_journal outcome source-artifact-fingerprints captured
  fi

  write_state starting
fi

expect_server_argument=''
for argument in "$@"; do
  if [ "$expect_server_argument" = enablessl ]; then
    case "$argument" in
      true|false|TRUE|FALSE|True|False)
        export Chaptarr__Server__EnableSsl="$argument"
        expect_server_argument=''
        continue
        ;;
      -*)
        export Chaptarr__Server__EnableSsl=true
        expect_server_argument=''
        ;;
      *)
        export Chaptarr__Server__EnableSsl=true
        expect_server_argument=''
        continue
        ;;
    esac
  elif [ -n "$expect_server_argument" ]; then
    case "$argument" in
      -*)
        write_state intervention-required
        echo "BookshelfNG desktop launcher received $expect_server_argument without a value." >&2
        exit 1
        ;;
    esac
    case "$expect_server_argument" in
      port)
        case "$argument" in ''|*[!0-9]*) ;; *) export Chaptarr__Server__Port="$argument" ;; esac
        ;;
      urlbase)
        export Chaptarr__Server__UrlBase="$argument"
        ;;
      bindaddress)
        export Chaptarr__Server__BindAddress="$argument"
        ;;
      sslport)
        export Chaptarr__Server__SslPort="$argument"
        ;;
      sslcertpath)
        export Chaptarr__Server__SslCertPath="$argument"
        ;;
      sslcertpassword)
        export Chaptarr__Server__SslCertPassword="$argument"
        ;;
    esac
    expect_server_argument=''
    continue
  fi
  case "$argument" in
    -port|--port) expect_server_argument=port ;;
    -urlbase|--urlbase) expect_server_argument=urlbase ;;
    -bindaddress|--bindaddress) expect_server_argument=bindaddress ;;
    -sslport|--sslport) expect_server_argument=sslport ;;
    -enablessl|--enablessl) expect_server_argument=enablessl ;;
    -sslcertpath|--sslcertpath) expect_server_argument=sslcertpath ;;
    -sslcertpassword|--sslcertpassword) expect_server_argument=sslcertpassword ;;
    -port=*|--port=*)
      value=${argument#*=}
      case "$value" in ''|*[!0-9]*) ;; *) export Chaptarr__Server__Port="$value" ;; esac
      ;;
    -urlbase=*|--urlbase=*)
      export Chaptarr__Server__UrlBase="${argument#*=}"
      ;;
    -bindaddress=*|--bindaddress=*)
      export Chaptarr__Server__BindAddress="${argument#*=}"
      ;;
    -sslport=*|--sslport=*)
      export Chaptarr__Server__SslPort="${argument#*=}"
      ;;
    -enablessl=*|--enablessl=*)
      export Chaptarr__Server__EnableSsl="${argument#*=}"
      ;;
    -sslcertpath=*|--sslcertpath=*)
      export Chaptarr__Server__SslCertPath="${argument#*=}"
      ;;
    -sslcertpassword=*|--sslcertpassword=*)
      export Chaptarr__Server__SslCertPassword="${argument#*=}"
      ;;
  esac
done
if [ -n "$expect_server_argument" ]; then
  if [ "$expect_server_argument" = enablessl ]; then
    export Chaptarr__Server__EnableSsl=true
  else
    write_state intervention-required
    echo "BookshelfNG desktop launcher received $expect_server_argument without a value." >&2
    exit 1
  fi
fi
health_url=${BOOKSHELFNG_HEALTH_URL:-auto}
if [ "$health_url" != auto ]; then
case "$health_url" in
  http://*|https://*) ;;
  *)
    write_state intervention-required
    echo 'BookshelfNG desktop health URL must use local HTTP or HTTPS.' >&2
    exit 1
    ;;
esac
health_authority=${health_url#*://}
health_authority=${health_authority%%/*}
case "$health_authority" in
  *'@'*)
    write_state intervention-required
    echo 'BookshelfNG desktop health URL cannot include credentials.' >&2
    exit 1
    ;;
esac
case "$health_authority" in
  localhost|127.0.0.1|'[::1]') ;;
  localhost:*|127.0.0.1:*|'[::1]:*')
    health_port_override=${health_authority##*:}
    case "$health_port_override" in ''|*[!0-9]*)
      write_state intervention-required
      echo 'BookshelfNG desktop health URL has an invalid port.' >&2
      exit 1
      ;;
    esac
    if [ "$health_port_override" -lt 1 ] || [ "$health_port_override" -gt 65535 ]; then
      write_state intervention-required
      echo 'BookshelfNG desktop health URL has an invalid port.' >&2
      exit 1
    fi
    ;;
  *)
    write_state intervention-required
    echo 'BookshelfNG desktop health URL must target localhost, 127.0.0.1, or [::1].' >&2
    exit 1
    ;;
esac
fi
timeout_seconds=${CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS:-${BOOKSHELFNG_STARTUP_TIMEOUT_SECONDS:-14700}}
case "$timeout_seconds" in
  ''|*[!0-9]*)
    echo 'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS must be an integer from 1 to 14700.' >&2
    exit 2
    ;;
esac
while [ "${timeout_seconds#0}" != "$timeout_seconds" ] && [ "${#timeout_seconds}" -gt 1 ]; do
  timeout_seconds=${timeout_seconds#0}
done
if [ "${#timeout_seconds}" -gt 5 ] || [ "$timeout_seconds" -lt 1 ] || [ "$timeout_seconds" -gt 14700 ]; then
  echo 'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS must be between 1 and 14700.' >&2
  exit 2
fi

app_pid_file="$lock_dir/app.pid"
if [ "$isolated_app_process" = true ]; then
  if [ "$custom_data_argument" = true ]; then
    setsid --wait sh -c 'printf "%s\n" "$$" > "$1"; shift; exec "$@"' bookshelfng-app "$app_pid_file" "$app" -nobrowser "$@" &
  else
    setsid --wait sh -c 'printf "%s\n" "$$" > "$1"; shift; exec "$@"' bookshelfng-app "$app_pid_file" "$app" -nobrowser "-data=$data_dir" "$@" &
  fi
  app_launcher_pid=$!
  app_start_deadline=$(( $(date +%s) + 10 ))
  while [ ! -s "$app_pid_file" ] && [ "$(date +%s)" -lt "$app_start_deadline" ]; do
    if ! process_is_running "$app_launcher_pid"; then break; fi
    sleep 0.1
  done
  app_pid=$(cat "$app_pid_file" 2>/dev/null || true)
  case "$app_pid" in ''|*[!0-9]*) app_pid='' ;; esac
else
  if [ "$custom_data_argument" = true ]; then
    "$app" -nobrowser "$@" &
  else
    "$app" -nobrowser "-data=$data_dir" "$@" &
  fi
  app_launcher_pid=$!
  app_pid=$app_launcher_pid
fi
if [ -n "$app_pid" ] && process_is_running "$app_pid"; then
  if [ "$isolated_app_process" = true ]; then
    observed_group=$(ps -o pgid= -p "$app_pid" 2>/dev/null | tr -d '[:space:]' || true)
    if [ "$observed_group" = "$app_pid" ]; then
      app_process_group=$observed_group
    else
      echo 'The desktop application did not enter its isolated process group; stopping before the migration health check.' >&2
      terminate_app_tree
      wait "$app_launcher_pid" 2>/dev/null || true
      exit 1
    fi
  fi
else
  echo 'The desktop application did not start; no migration health check was run.' >&2
  wait "$app_launcher_pid" 2>/dev/null || true
  exit 1
fi
health_deadline=$(( $(date +%s) + timeout_seconds ))
while :; do
  if ! process_is_running "$app_pid"; then
    break
  fi

  if run_utility -check-bookshelf-migration "-data=$data_dir" \
      "-bookshelf-health-url=$health_url" \
      -bookshelf-health-timeout-seconds=1 >/dev/null 2>&1; then
    if process_is_running "$app_pid"; then healthy=true; fi
    break
  fi

  if [ "$(date +%s)" -ge "$health_deadline" ]; then break; fi
  sleep 1
done

if [ "$healthy" = true ]; then
  write_state healthy
  rm -rf "$metadata_dir"
  wait "$app_launcher_pid"
  exit $?
fi

if process_is_running "$app_pid"; then
  if [ "$(date +%s)" -ge "$health_deadline" ]; then
    echo "ChaptarrNG did not pass the database and local HTTP health checks within $timeout_seconds seconds." >&2
  else
    echo 'ChaptarrNG exited before both the database and local HTTP health checks passed.' >&2
  fi
else
  echo 'ChaptarrNG exited before both the database and local HTTP health checks passed.' >&2
fi

terminate_app_tree
wait "$app_launcher_pid" 2>/dev/null || true

if restore_source_database; then
  write_state rolled-back
  echo 'BookshelfNG source data was restored after the first ChaptarrNG startup failed its database and HTTP health checks. Reopen the prior BookshelfNG application before retrying.' >&2
else
  write_state intervention-required
  echo 'BookshelfNG migration did not pass startup health and no safe automatic source restore completed. The current database and retained backups were left for recovery.' >&2
fi
exit 1
