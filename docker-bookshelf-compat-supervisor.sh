#!/bin/sh
set -eu

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" != "true" ]; then
    exec "$@"
fi

if [ "$#" -eq 0 ]; then
    echo 'BookshelfNG compatibility startup is missing the ChaptarrNG command.' >&2
    exit 2
fi

if ! command -v setsid >/dev/null 2>&1 || ! setsid --help 2>&1 | grep -q -- '--wait'; then
    echo 'BookshelfNG compatibility startup requires setsid with process-group wait support.' >&2
    exit 2
fi

data_dir=${CHAPTARR_COMPAT_DATA_DIR:-/config}
config_file=${CHAPTARR_CONFIG_FILE:-$data_dir/config.xml}
handoff_root="$data_dir/.chaptarrng-bookshelf-handoff"
pending_marker="$handoff_root/pending"
failed_marker="$handoff_root/failed"
deferred_marker="$handoff_root/deferred"
app_pid=
operator_wait_pid=
app_status=0
handoff_deadline_epoch=
migration_approved=false
merge_bookshelf_databases=false
lock_wait_marker=/tmp/chaptarr-bookshelf-lock-waiting

acquire_supervisor_lock() {
    if [ -L "$handoff_root" ] || { [ -e "$handoff_root" ] && [ ! -d "$handoff_root" ]; }; then
        echo 'BookshelfNG compatibility startup requires a real handoff state directory.' >&2
        return 1
    fi

    mkdir -p "$handoff_root" || return 1
    chmod 0700 "$handoff_root" || return 1

    lock_path="$handoff_root/supervisor.lock"
    if [ -L "$lock_path" ] || { [ -e "$lock_path" ] && [ ! -f "$lock_path" ]; }; then
        echo 'BookshelfNG compatibility startup refuses an unsafe supervisor lock path.' >&2
        return 1
    fi
    if ! command -v flock >/dev/null 2>&1; then
        echo 'BookshelfNG compatibility startup requires flock to protect shared app-data during migration.' >&2
        return 1
    fi
    if [ -L "$lock_wait_marker" ] || { [ -e "$lock_wait_marker" ] && [ ! -f "$lock_wait_marker" ]; }; then
        echo 'BookshelfNG compatibility startup refuses an unsafe lock-wait marker path.' >&2
        return 1
    fi
    rm -f "$lock_wait_marker"

    (umask 077; : >> "$lock_path") || return 1
    chmod 0600 "$lock_path" || return 1
    exec 9<> "$lock_path" || return 1
    if ! flock -n 9; then
        echo 'Another ChaptarrNG BookshelfNG compatibility process is using this app-data directory; waiting for it to exit before opening the database.' >&2
        (umask 077; set -C; : > "$lock_wait_marker") || return 1
        if ! flock 9; then
            rm -f "$lock_wait_marker"
            return 1
        fi
        rm -f "$lock_wait_marker"
    fi
}

utility() {
    command=$1
    shift
    remaining=$(remaining_handoff_seconds) || {
        echo 'The BookshelfNG handoff reached its configured deadline.' >&2
        return 124
    }
    if [ -t 0 ]; then
        # GNU timeout normally creates a separate process group. With an
        # attached Docker TTY, that group cannot read the terminal and gets
        # stopped before the operator can answer the migration prompt.
        timeout --foreground --signal=TERM --kill-after=30 "$remaining" \
            dotnet /app/Chaptarr.dll "$command" "$@" "-data=$data_dir" -exitimmediately
    else
        timeout --signal=TERM --kill-after=30 "$remaining" \
            dotnet /app/Chaptarr.dll "$command" "$@" "-data=$data_dir" -exitimmediately
    fi
}

remaining_handoff_seconds() {
    now=$(date +%s)
    case "$now" in
        ''|*[!0-9]*) return 1 ;;
    esac
    if [ -z "$handoff_deadline_epoch" ]; then
        handoff_deadline_epoch=$((now + handoff_timeout))
    fi
    remaining=$((handoff_deadline_epoch - now))
    [ "$remaining" -gt 0 ] || return 1
    printf '%s\n' "$remaining"
}

database_is_migrated() {
    utility -check-bookshelf-migration >/dev/null 2>&1
}

has_postgres_source() {
    for name in Chaptarr__Postgres__Host CHAPTARR__POSTGRES__HOST Readarr__Postgres__Host READARR__POSTGRES__HOST Audioarr__Postgres__Host AUDIOARR__POSTGRES__HOST Bookshelf__Postgres__Host BOOKSHELF__POSTGRES__HOST; do
        if printenv "$name" >/dev/null 2>&1; then
            host=$(printenv "$name")
            if [ -n "$host" ]; then
                return 0
            fi
            return 1
        fi
    done

    if [ -f "$config_file" ] && [ ! -L "$config_file" ]; then
        host=$(sed -n 's/.*<PostgresHost>\([^<]*\)<\/PostgresHost>.*/\1/p' "$config_file" | head -n 1 | tr -d '[:space:]')
        [ -n "$host" ] && return 0
    fi

    return 1
}

backup_postgres_source() {
    has_postgres_source || return 0

    postgres_backup="$attempt_dir/postgres-source"
    if ! mkdir -m 0700 "$postgres_backup"; then
        echo 'BookshelfNG compatibility startup could not create a private PostgreSQL backup directory.' >&2
        return 1
    fi
    if ! utility -check-bookshelf-postgres-backup -confirm-bookshelf-service-stopped; then
        echo 'BookshelfNG PostgreSQL backup preflight failed; migration was not started.' >&2
        return 1
    fi
    if ! utility -backup-bookshelf-postgres -confirm-bookshelf-service-stopped "-bookshelf-postgres-backup=$postgres_backup"; then
        echo "BookshelfNG PostgreSQL backup failed; retained any backup evidence at $postgres_backup and did not start migration." >&2
        return 1
    fi
    if [ ! -s "$postgres_backup/postgres-main.dump" ]; then
        echo "BookshelfNG PostgreSQL backup produced no verified main database dump at $postgres_backup." >&2
        return 1
    fi
}

preserve_postgres_side_schemas() {
    has_postgres_source || return 0
    source_backup="$attempt_dir/postgres-source"
    if [ ! -s "$source_backup/postgres-log.dump" ] && [ ! -s "$source_backup/postgres-cache.dump" ]; then
        return 0
    fi

    target_backup="$attempt_dir/target-postgres-log-cache"
    if ! mkdir -m 0700 "$target_backup"; then
        echo 'Could not create a retained backup directory for the current PostgreSQL log/cache schemas.' >&2
        return 1
    fi
    if ! utility -backup-bookshelf-postgres -confirm-bookshelf-service-stopped -bookshelf-postgres-log-cache-only "-bookshelf-postgres-backup=$target_backup"; then
        echo "Could not retain the current PostgreSQL log/cache schemas at $target_backup." >&2
        return 1
    fi
}

restore_postgres_side_schemas() {
    has_postgres_source || return 0
    source_backup="$attempt_dir/postgres-source"
    if [ ! -s "$source_backup/postgres-log.dump" ] && [ ! -s "$source_backup/postgres-cache.dump" ]; then
        return 0
    fi

    if ! utility -restore-bookshelf-postgres -confirm-bookshelf-service-stopped -bookshelf-postgres-log-cache-only "-bookshelf-postgres-backup=$source_backup"; then
        echo "Could not restore the retained BookshelfNG PostgreSQL log/cache schemas from $source_backup." >&2
        return 1
    fi
}

has_bookshelf_source_candidate() {
    local_found=false
    for database in "$data_dir/readarr.db" "$data_dir/audioarr.db"; do
        if [ -L "$database" ]; then
            echo "BookshelfNG compatibility startup refuses a symlinked database: $database" >&2
            return 2
        fi

        if [ -f "$database" ] && [ -s "$database" ]; then
            local_found=true
        elif [ -s "$database-wal" ] || [ -s "$database-journal" ]; then
            echo "BookshelfNG SQLite recovery data exists without a non-empty main database: $database. Preserve the database family and recover it before starting either application." >&2
            return 2
        fi
    done

    if [ "$local_found" = true ] || has_postgres_source; then
        return 0
    fi

    return 1
}

has_multiple_populated_sqlite_sources() {
    ! has_postgres_source &&
        sqlite_database_has_data "$data_dir/readarr.db" &&
        sqlite_database_has_data "$data_dir/audioarr.db"
}

sqlite_database_has_data() {
    database=$1
    [ -s "$database" ] || [ -s "$database-wal" ] || [ -s "$database-journal" ]
}

preview_bookshelf_migration() {
    if [ "$merge_bookshelf_databases" = true ]; then
        utility -preview-bookshelf-migration -merge-bookshelf-databases
    else
        utility -preview-bookshelf-migration
    fi
}

validate_source_paths() {
    if [ -L "$data_dir" ]; then
        echo "BookshelfNG compatibility startup refuses a symlinked data directory: $data_dir" >&2
        return 1
    fi

    if [ -L "$config_file" ] || { [ -e "$config_file" ] && [ ! -f "$config_file" ]; }; then
        echo "BookshelfNG compatibility startup requires config.xml to be a regular file: $config_file" >&2
        return 1
    fi

    for source_path in \
        "$data_dir/readarr.db" "$data_dir/readarr.db-journal" "$data_dir/readarr.db-wal" "$data_dir/readarr.db-shm" \
        "$data_dir/audioarr.db" "$data_dir/audioarr.db-journal" "$data_dir/audioarr.db-wal" "$data_dir/audioarr.db-shm"; do
        if [ -L "$source_path" ] || { [ -e "$source_path" ] && [ ! -f "$source_path" ]; }; then
            echo "BookshelfNG compatibility startup requires regular SQLite source files: $source_path" >&2
            return 1
        fi
    done
}

write_marker() {
    path=$1
    contents=$2
    temporary="$path.tmp.$$"
    (umask 077; set -C; printf '%s\n' "$contents" > "$temporary") || return 1
    chmod 0600 "$temporary" || return 1
    mv -f "$temporary" "$path"
}

new_attempt_id() {
    printf '%s-%s\n' "$(date -u +%Y%m%dT%H%M%SZ)" "$$"
}

validate_handoff_timeout() {
    handoff_timeout=${CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS:-14700}
    case "$handoff_timeout" in
        ''|*[!0-9]*)
            echo 'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS must be an integer from 1 to 14700.' >&2
            return 1
            ;;
    esac
    while [ "${handoff_timeout#0}" != "$handoff_timeout" ] && [ "${#handoff_timeout}" -gt 1 ]; do
        handoff_timeout=${handoff_timeout#0}
    done
    if [ "${#handoff_timeout}" -gt 5 ]; then
        echo 'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS must be between 1 and 14700.' >&2
        return 1
    fi
    if [ "$handoff_timeout" -lt 1 ] || [ "$handoff_timeout" -gt 14700 ]; then
        echo 'CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS must be between 1 and 14700.' >&2
        return 1
    fi
    if ! command -v timeout >/dev/null 2>&1; then
        echo 'BookshelfNG compatibility startup requires the timeout command for bounded migration recovery.' >&2
        return 1
    fi
    now=$(date +%s)
    case "$now" in
        ''|*[!0-9]*)
            echo 'BookshelfNG compatibility startup could not establish a bounded handoff deadline.' >&2
            return 1
            ;;
    esac
    handoff_deadline_epoch=$((now + handoff_timeout))
}

validate_migration_mode() {
    migration_mode=${BOOKSHELFNG_MIGRATION_MODE:-automatic}
    case "$migration_mode" in
        automatic|ask|manual|retry) ;;
        *)
            echo 'BOOKSHELFNG_MIGRATION_MODE must be automatic, ask, manual, or retry.' >&2
            return 1
            ;;
    esac
}

migration_requested_automatic() {
    case "$migration_mode" in
        automatic|retry)
            return 0
            ;;
        manual)
            return 1
            ;;
        ask)
            if [ ! -t 0 ]; then
                echo 'Interactive migration choice requested without a terminal. Deferring migration; set BOOKSHELFNG_MIGRATION_MODE=automatic to start it.' >&2
                return 1
            fi
            printf 'Replace BookshelfNG now? This first startup will migrate its database. [y/N] '
            answer=
            if ! IFS= read -r answer; then answer=; fi
            case "$answer" in
                y|Y|yes|YES|Yes) return 0 ;;
                *) return 1 ;;
            esac
            ;;
    esac
}

prepare_attempt() {
    if [ -L "$handoff_root" ]; then
        echo 'BookshelfNG compatibility startup refuses a symlinked handoff directory.' >&2
        return 1
    fi
    mkdir -p "$handoff_root" || return 1
    chmod 0700 "$handoff_root" || return 1

    validate_handoff_timeout || return 1
    validate_migration_mode || return 1

    if [ -L "$pending_marker" ] || [ -L "$failed_marker" ] || [ -L "$deferred_marker" ]; then
        echo 'BookshelfNG compatibility startup refuses a symlinked handoff state file.' >&2
        return 1
    fi

    if [ -e "$failed_marker" ] && [ -e "$pending_marker" ]; then
        echo 'BookshelfNG compatibility startup found both failed and pending handoff markers; operator recovery is required.' >&2
        return 1
    fi

    if { [ -e "$failed_marker" ] && [ ! -f "$failed_marker" ]; } ||
       { [ -e "$deferred_marker" ] && [ ! -f "$deferred_marker" ]; };
    then
        echo 'BookshelfNG compatibility startup found an invalid handoff state file.' >&2
        return 1
    fi

    if [ -e "$failed_marker" ]; then
        failure_state=$(cat "$failed_marker") || return 1
        if [ "$failure_state" = 'rolled-back' ] && [ "$migration_mode" = 'retry' ]; then
            if database_is_migrated; then
                rm -f "$failed_marker" "$pending_marker" "$deferred_marker"
                echo 'The operator selected retry and the database already has a valid completed BookshelfNG migration.'
                return 2
            fi
            if preview_bookshelf_migration; then
                rm -f "$failed_marker"
                echo 'The operator selected retry and the restored BookshelfNG source passed migration preview.'
            else
                echo 'The retry preview failed. The rollback marker and source data are retained for operator recovery.' >&2
                return 4
            fi
        else
            echo 'A prior BookshelfNG handoff failed or needs intervention. Set BOOKSHELFNG_MIGRATION_MODE=retry only after correcting the cause and when automatic migration should run again.' >&2
            return 4
        fi
    fi

    if [ -e "$pending_marker" ] && [ ! -f "$pending_marker" ]; then
        echo 'BookshelfNG compatibility startup found an invalid handoff state file.' >&2
        return 1
    fi

    if [ -f "$pending_marker" ]; then
        attempt_id=$(cat "$pending_marker") || return 1
        case "$attempt_id" in
            ''|*[!0-9TZ-]*)
                echo 'BookshelfNG compatibility startup found an invalid handoff marker; refusing to continue.' >&2
                return 1
                ;;
        esac

        attempt_dir="$handoff_root/$attempt_id"
        if [ ! -d "$attempt_dir" ] || [ -L "$attempt_dir" ]; then
            echo 'BookshelfNG compatibility startup cannot find the recorded handoff snapshot.' >&2
            return 1
        fi
        conversion_marker="$attempt_dir/conversion-authorized"
        if [ -L "$conversion_marker" ] || { [ -e "$conversion_marker" ] && [ ! -f "$conversion_marker" ]; }; then
            echo 'BookshelfNG compatibility startup found an invalid conversion-phase marker.' >&2
            return 1
        fi

        if [ -L "$attempt_dir/config.xml" ] ||
           { [ -f "$attempt_dir/config.xml.present" ] && [ ! -f "$attempt_dir/config.xml" ]; } ||
           { [ ! -f "$attempt_dir/config.xml.present" ] && [ ! -f "$attempt_dir/config.xml.absent" ]; };
        then
            echo 'BookshelfNG compatibility startup found an incomplete config snapshot.' >&2
            return 1
        fi

        validate_handoff_timeout || return 1
        echo 'A previous container process ended during the first BookshelfNG migration. Restoring the source state before waiting for operator recovery.' >&2
        if [ -f "$conversion_marker" ]; then
            rollback_failed_handoff 'the previous container process ended before first-start health was confirmed' true || true
        else
            rollback_failed_handoff 'the previous container process ended before conversion was authorized' false || true
        fi
        return 4
    fi

    if database_is_migrated; then
        rm -f "$deferred_marker"
        return 2
    fi

    if has_multiple_populated_sqlite_sources; then
        # Preview the same conflict-failing merge that normal ChaptarrNG startup
        # will apply. The originals remain untouched; unresolved identities or
        # settings keep the handoff blocked before the app is launched.
        merge_bookshelf_databases=true
    fi

    if [ -f "$deferred_marker" ]; then
        case "$migration_mode" in
            automatic|retry)
                rm -f "$deferred_marker"
                ;;
            ask)
                if migration_requested_automatic; then
                    rm -f "$deferred_marker"
                    migration_approved=true
                else
                    return 5
                fi
                ;;
            manual)
                return 5
                ;;
        esac
    fi

    if has_bookshelf_source_candidate; then
        :
    else
        source_status=$?
        if [ "$source_status" -eq 2 ]; then
            return 1
        fi
        return 3
    fi

    if [ "$migration_approved" != true ] && ! migration_requested_automatic; then
        write_marker "$deferred_marker" 'manual-review' || return 1
        return 5
    fi

    rm -f "$deferred_marker"

    attempt_id=$(new_attempt_id)
    attempt_dir="$handoff_root/$attempt_id"
    conversion_marker="$attempt_dir/conversion-authorized"
    if ! mkdir -m 0700 "$attempt_dir"; then
        echo 'BookshelfNG compatibility startup could not create a private handoff snapshot directory.' >&2
        return 1
    fi

    if [ -L "$config_file" ]; then
        echo 'BookshelfNG compatibility startup refuses a symlinked config.xml.' >&2
        rm -rf "$attempt_dir"
        return 1
    elif [ -e "$config_file" ]; then
        if [ ! -f "$config_file" ]; then
            echo 'BookshelfNG compatibility startup expected config.xml to be a regular file.' >&2
            rm -rf "$attempt_dir"
            return 1
        fi
        if ! cp -p "$config_file" "$attempt_dir/config.xml" || ! touch "$attempt_dir/config.xml.present"; then
            rm -rf "$attempt_dir"
            return 1
        fi
    else
        if ! touch "$attempt_dir/config.xml.absent"; then
            rm -rf "$attempt_dir"
            return 1
        fi
    fi
    chmod 0600 "$attempt_dir"/config.xml* 2>/dev/null || true

    write_marker "$pending_marker" "$attempt_id" || return 1
    if ! preview_bookshelf_migration; then
        echo 'BookshelfNG migration preview failed before conversion. The source database remains untouched; the container will wait for recovery or an image rollback.' >&2
        rollback_failed_handoff 'migration preview failed before conversion was started' false || true
        return 4
    fi

    if ! backup_postgres_source; then
        rollback_failed_handoff 'the independent PostgreSQL source backup failed before conversion was started' false || true
        return 4
    fi

    write_marker "$conversion_marker" 'source-previewed' || {
        rollback_failed_handoff 'the conversion phase could not be recorded before first startup' false || true
        return 4
    }

    echo "BookshelfNG handoff snapshot recorded at $attempt_dir. Starting the first ChaptarrNG launch under health supervision."
}

restore_original_config() {
    if [ -L "$config_file" ]; then
        echo 'The current config.xml path became a symlink before rollback.' >&2
        return 1
    fi

    if [ -f "$attempt_dir/config.xml.present" ]; then
        if [ ! -f "$attempt_dir/config.xml" ] || [ -L "$attempt_dir/config.xml" ]; then
            echo 'The saved BookshelfNG config.xml snapshot is missing or unsafe.' >&2
            return 1
        fi
        cp -p "$attempt_dir/config.xml" "$config_file"
    elif [ -f "$attempt_dir/config.xml.absent" ]; then
        rm -f "$config_file"
    else
        echo 'The saved BookshelfNG config.xml snapshot state is missing.' >&2
        return 1
    fi
}

stop_app() {
    [ -n "$app_pid" ] || return 0
    # The container's /bin/sh is dash; its kill builtin rejects `--` here.
    # app_pid comes from $!, so the negative numeric operand is safe.
    kill -TERM -"$app_pid" 2>/dev/null || true
    waited=0
    while kill -0 -"$app_pid" 2>/dev/null && [ "$waited" -lt 30 ]; do
        sleep 1
        waited=$((waited + 1))
    done
    if kill -0 -"$app_pid" 2>/dev/null; then
        kill -KILL -"$app_pid" 2>/dev/null || true
    fi
    wait "$app_pid" 2>/dev/null || true
    app_pid=
}

stop_operator_wait() {
    [ -n "$operator_wait_pid" ] || return 0
    kill -TERM "$operator_wait_pid" 2>/dev/null || true
    wait "$operator_wait_pid" 2>/dev/null || true
    operator_wait_pid=
}

handle_container_stop() {
    stop_operator_wait
    stop_app
    if [ -f "$pending_marker" ] && [ ! -L "$pending_marker" ]; then
        attempt_id=$(cat "$pending_marker" 2>/dev/null || true)
        database_may_have_changed=true
        case "$attempt_id" in
            ''|*[!0-9TZ-]*) ;;
            *)
                attempt_dir="$handoff_root/$attempt_id"
                conversion_marker="$attempt_dir/conversion-authorized"
                if [ -d "$attempt_dir" ] && [ ! -L "$attempt_dir" ] &&
                   [ ! -e "$conversion_marker" ] && [ ! -L "$conversion_marker" ]; then
                    database_may_have_changed=false
                fi
                ;;
        esac
        rollback_failed_handoff 'container stop interrupted the first startup' "$database_may_have_changed"
    fi
    exit 143
}

wait_for_operator() {
    trap handle_container_stop INT TERM HUP
    message=${1:-'ChaptarrNG is not serving this installation after the BookshelfNG handoff attempt. Replace the image with the prior BookshelfNG image or complete the documented recovery before retrying.'}
    echo "$message" >&2
    while :; do
        sleep 3600 &
        operator_wait_pid=$!
        wait "$operator_wait_pid" || true
        operator_wait_pid=
    done
}

rollback_failed_handoff() {
    reason=$1
    database_may_have_changed=${2:-true}
    trap - INT TERM HUP
    stop_app
    now=$(date +%s)
    case "$now" in
        ''|*[!0-9]*)
            echo 'Could not establish a rollback deadline; retained source data was not changed.' >&2
            return 1
            ;;
    esac
    # Reserve a fresh bounded window for recovery after the migration/startup
    # budget expires or the container is stopped during first launch.
    handoff_deadline_epoch=$((now + handoff_timeout))

    echo "The first ChaptarrNG startup did not pass its BookshelfNG handoff check ($reason). Recovering the saved BookshelfNG state."
    rollback_ok=true
    if ! restore_original_config; then
        rollback_ok=false
    fi
    database_restore_needed=false
    if [ "$database_may_have_changed" = true ]; then
        if database_is_migrated; then
            database_restore_needed=true
        elif [ "$merge_bookshelf_databases" = true ] && ! sqlite_database_has_data "$data_dir/chaptarr.db"; then
            echo 'The dual BookshelfNG source databases remain intact and no ChaptarrNG target was promoted.'
        elif preview_bookshelf_migration; then
            echo 'The BookshelfNG source still passes migration preview; leaving the source database untouched.'
        else
            database_restore_needed=true
        fi
    fi
    if [ "$rollback_ok" = true ] && [ "$database_restore_needed" = true ]; then
        source_postgres_backup="$attempt_dir/postgres-source"
        if has_postgres_source && {
            [ -s "$source_postgres_backup/postgres-log.dump" ] ||
            [ -s "$source_postgres_backup/postgres-cache.dump" ];
        }; then
            if ! utility -preview-bookshelf-postgres-restore -confirm-bookshelf-service-stopped -bookshelf-postgres-log-cache-only "-bookshelf-postgres-backup=$source_postgres_backup"; then
                echo "The retained PostgreSQL log/cache backups could not be validated before database rollback: $source_postgres_backup" >&2
                rollback_ok=false
            fi
        fi
    fi
    if [ "$rollback_ok" = true ] && [ "$database_restore_needed" = true ]; then
        if ! preserve_postgres_side_schemas; then
            rollback_ok=false
        fi
    fi
    if [ "$rollback_ok" = true ] && [ "$database_restore_needed" = true ]; then
        if ! utility -restore-bookshelf -confirm-bookshelf-service-stopped -bookshelf-backup=auto; then
            rollback_ok=false
        fi
        if [ "$rollback_ok" = true ] && ! restore_postgres_side_schemas; then
            rollback_ok=false
        fi
    fi

    if [ "$rollback_ok" = true ]; then
        write_marker "$failed_marker" 'rolled-back' || return 1
        rm -f "$pending_marker" || return 1
        if [ "$database_restore_needed" = true ]; then
            echo 'BookshelfNG data was restored. The app remains stopped to prevent an automatic migration retry; replace the image with BookshelfNG or inspect the retained backup before retrying.' >&2
        else
            echo 'The original BookshelfNG source databases remain intact. The app remains stopped; resolve the migration issue, then retry or restore the prior image.' >&2
        fi
        return 0
    fi

    write_marker "$failed_marker" 'intervention-required' || return 1
    rm -f "$pending_marker" || return 1
    echo 'Automatic BookshelfNG rollback could not be verified. The source database backup and handoff snapshot are retained; do not start either app until the data is reviewed.' >&2
    return 1
}

validate_source_paths || wait_for_operator 'BookshelfNG migration found an unsafe linked or non-regular source path. The source has not been migrated; resolve the path and restart.'
if has_multiple_populated_sqlite_sources; then
    merge_bookshelf_databases=true
fi
acquire_supervisor_lock || wait_for_operator 'BookshelfNG compatibility startup could not lock the app-data directory. Stop other instances and resolve the handoff state before restarting.'

prepare_status=0
prepare_attempt || prepare_status=$?
case "$prepare_status" in
    0) ;;
    1) wait_for_operator 'BookshelfNG handoff found an invalid or unsafe state. ChaptarrNG was not started; preserve appdata and use the documented manual recovery before retrying.' ;;
    2) exec "$@" ;;
    3) exec "$@" ;;
    4) wait_for_operator ;;
    5) wait_for_operator 'BookshelfNG migration is deferred by BOOKSHELFNG_MIGRATION_MODE. Run the documented preview/apply workflow, or change the mode to automatic and restart this container.' ;;
    *) exit 1 ;;
esac

trap handle_container_stop INT TERM HUP
setsid --wait "$@" &
app_pid=$!
while :; do
    remaining=$(remaining_handoff_seconds) || break
    if ! kill -0 "$app_pid" 2>/dev/null; then
        if wait "$app_pid"; then
            app_status=0
        else
            app_status=$?
        fi
        app_pid=
        rollback_failed_handoff "the app exited before migration health was confirmed (exit $app_status)" || true
        wait_for_operator
    fi

    if /usr/local/bin/chaptarr-healthcheck --app-only >/dev/null 2>&1 && database_is_migrated; then
        rm -f "$pending_marker"
        echo 'BookshelfNG database migration and ChaptarrNG startup health checks passed.'
        wait "$app_pid"
        exit $?
    fi

    sleep 2
done

rollback_failed_handoff "health was not confirmed within ${handoff_timeout} seconds" || true
wait_for_operator
