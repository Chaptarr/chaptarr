#!/bin/bash
set -e

# Default values
PUID=${PUID:-99}
PGID=${PGID:-100}
UMASK=${UMASK:-}

is_numeric() {
    [[ "$1" =~ ^[0-9]+$ ]]
}

is_umask() {
    [[ "$1" =~ ^0?[0-7]{3}$ ]]
}

is_option_value() {
    local normalized=${1,,}
    [[ "$1" == -* ]] && return 0
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

if ! is_numeric "$PUID"; then
    echo "Error: PUID must be numeric (got: '$PUID')"
    exit 1
fi

if ! is_numeric "$PGID"; then
    echo "Error: PGID must be numeric (got: '$PGID')"
    exit 1
fi

if [ -n "$UMASK" ]; then
    if ! is_umask "$UMASK"; then
        echo "Error: UMASK must be an octal mask like 022, 002, or 0002 (got: '$UMASK')"
        exit 1
    fi

    umask "$UMASK"
fi

DATA_DIR="/config"
entrypoint_args=("$@")
data_argument_seen=false
for ((arg_index = 0; arg_index < ${#entrypoint_args[@]}; arg_index++)); do
    arg=${entrypoint_args[$arg_index]}
    case "$arg" in
        -data=*|/data=*|--data=*)
            selected_data_dir=${arg#*=}
            if [ -z "$selected_data_dir" ] || [[ "$selected_data_dir" == -* ]]; then
                echo "Error: $arg must include a data directory path, not another option."
                exit 2
            fi
            if [ "$data_argument_seen" = true ] && [ "$DATA_DIR" != "$selected_data_dir" ]; then
                echo "Error: conflicting data directory arguments were provided."
                exit 2
            fi
            DATA_DIR=$selected_data_dir
            data_argument_seen=true
            ;;
        -data|/data|--data)
            next_arg_index=$((arg_index + 1))
            if (( next_arg_index >= ${#entrypoint_args[@]} )); then
                echo "Error: $arg requires a data directory path."
                exit 2
            fi
            selected_data_dir=${entrypoint_args[$next_arg_index]}
            if [ -z "$selected_data_dir" ] || is_option_value "$selected_data_dir"; then
                echo "Error: $arg requires a data directory path, not another option."
                exit 2
            fi
            if [ "$data_argument_seen" = true ] && [ "$DATA_DIR" != "$selected_data_dir" ]; then
                echo "Error: conflicting data directory arguments were provided."
                exit 2
            fi
            DATA_DIR=$selected_data_dir
            data_argument_seen=true
            arg_index=$next_arg_index
            ;;
    esac
done

. /docker-bookshelf-migration-detect.sh
export CHAPTARR_COMPAT_DATA_DIR="$DATA_DIR"
export CHAPTARR_CONFIG_FILE="${CHAPTARR_CONFIG_FILE:-$DATA_DIR/config.xml}"

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ] && [ -L "$DATA_DIR" ]; then
    echo "Error: BookshelfNG compatibility startup refuses a symlinked data directory: $DATA_DIR" >&2
    exit 1
fi

# Docker health checks and Kubernetes exec probes run in separate processes,
# so they cannot read variables exported only by this entrypoint.
health_path_tmp="/tmp/chaptarr-health-paths.$$"
(umask 022; set -C; printf '%s\n%s\n' "$DATA_DIR" "$CHAPTARR_CONFIG_FILE" > "$health_path_tmp") || {
    echo 'Error: Could not publish the resolved data and config paths for container health checks.' >&2
    exit 1
}
chmod 0644 "$health_path_tmp"
mv -f "$health_path_tmp" /tmp/chaptarr-health-paths

echo "================================"
echo "Chaptarr Docker Entrypoint"
echo "PUID: $PUID"
echo "PGID: $PGID"
echo "UMASK: $(umask)"
echo "DATA_DIR: $DATA_DIR"
echo "================================"

# Function to safely change ownership
safe_chown() {
    local path="$1"
    local uid="$2"
    local gid="$3"

    # Never let privileged startup follow a user-controlled link and change
    # ownership outside the mounted data tree.
    if [ -L "$path" ]; then
        echo "Warning: Skipping ownership change for symlink $path"
        return 0
    fi
    
    # Check if path exists
    if [ ! -e "$path" ]; then
        return 0
    fi
    
    # Get current ownership
    local current_uid=$(stat -c "%u" "$path" 2>/dev/null || echo "-1")
    local current_gid=$(stat -c "%g" "$path" 2>/dev/null || echo "-1")
    
    # Only chown if needed
    if [ "$current_uid" != "$uid" ] || [ "$current_gid" != "$gid" ]; then
        # Try to change ownership, but don't fail if it doesn't work
        if chown "$uid:$gid" "$path" 2>/dev/null; then
            echo "Changed ownership of $path to $uid:$gid"
        else
            echo "Warning: Could not change ownership of $path (may be read-only or permission denied)"
        fi
    fi
}

safe_chown_tree() {
    local path="$1"
    local uid="$2"
    local gid="$3"

    if [ -L "$path" ] || [ ! -d "$path" ]; then
        return 0
    fi

    # These private, app-owned directories can survive a rollback through a
    # legacy image that recursively changes /config ownership. Do not follow
    # links or cross mounted filesystems while restoring ownership.
    if ! find -P "$path" -xdev ! -type l -exec chown "$uid:$gid" {} +; then
        echo "Warning: Could not restore ownership beneath $path" >&2
    fi
}

validate_compatibility_paths() {
    if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" != "true" ]; then
        return 0
    fi

    local path
    for path in \
        "$DATA_DIR/config.xml" \
        "$DATA_DIR/readarr.db" "$DATA_DIR/readarr.db-journal" "$DATA_DIR/readarr.db-wal" "$DATA_DIR/readarr.db-shm" \
        "$DATA_DIR/audioarr.db" "$DATA_DIR/audioarr.db-journal" "$DATA_DIR/audioarr.db-wal" "$DATA_DIR/audioarr.db-shm"; do
        if [ -L "$path" ]; then
            echo "Error: BookshelfNG compatibility startup refuses a symlinked migration path: $path" >&2
            return 1
        fi

        if [ -e "$path" ] && [ ! -f "$path" ]; then
            echo "Error: BookshelfNG compatibility startup expected a regular file at $path" >&2
            return 1
        fi
    done
}

run_application() {
    if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ]; then
        exec /usr/local/bin/chaptarr-bookshelf-compat-supervisor "$@"
    fi

    exec "$@"
}

# Only run user management if we're root
if [ "$EUID" -eq 0 ]; then
    echo "Running as root, setting up user..."
    
    # Handle special case: root user requested
    if [ "$PUID" -eq 0 ] || [ "$PGID" -eq 0 ]; then
        echo "Warning: Running as root (UID 0) is not recommended for security reasons"
        run_application "$@"
    fi
    
    # Create or modify group
    if getent group "$PGID" >/dev/null 2>&1; then
        echo "Group with GID $PGID already exists"
        groupname=$(getent group "$PGID" | cut -d: -f1)
    else
        echo "Creating group 'abc' with GID $PGID"
        groupadd -g "$PGID" abc
        groupname="abc"
    fi
    
    # Create or modify user
    if getent passwd "$PUID" >/dev/null 2>&1; then
        echo "User with UID $PUID already exists"
        username=$(getent passwd "$PUID" | cut -d: -f1)
        # Update group if needed
        if [ "$(id -g "$username")" != "$PGID" ]; then
            usermod -g "$PGID" "$username" 2>/dev/null || true
        fi
    else
        echo "Creating user 'abc' with UID $PUID and GID $PGID"
        useradd -u "$PUID" -g "$PGID" -d /config -s /bin/bash abc
        username="abc"
    fi
    
    # Ensure config directory exists
    mkdir -p "$DATA_DIR"
    validate_compatibility_paths || exit 1
    
    # Smart ownership management - only change what's needed
    echo "Checking $DATA_DIR ownership..."
    
    # First, check if we can even write to the parent directory
    if touch "$DATA_DIR"/.write_test 2>/dev/null; then
        rm -f "$DATA_DIR"/.write_test
        
        # Only fix ownership of key files/directories that Chaptarr creates
        for item in \
            "$DATA_DIR" "$DATA_DIR"/config.xml "$DATA_DIR"/*.db "$DATA_DIR"/*.db-* "$DATA_DIR"/*.pid \
            "$DATA_DIR"/logs "$DATA_DIR"/MediaCover "$DATA_DIR"/Backups; do
            safe_chown "$item" "$PUID" "$PGID"
        done

        for item in \
            "$DATA_DIR"/.chaptarrng-bookshelf-handoff \
            "$DATA_DIR"/.chaptarrng-bookshelfng-journal \
            "$DATA_DIR"/asp; do
            safe_chown_tree "$item" "$PUID" "$PGID"
        done
        
        # Don't recursively chown everything - that's too aggressive
        # Users can manually fix other files if needed
    else
        echo "Warning: $DATA_DIR appears to be read-only or access denied"
        echo "Chaptarr may not be able to write configuration files"
    fi
    
    echo "Starting Chaptarr as user $username (UID=$PUID, GID=$PGID)"
    gosu "$username" sh -c 'echo "Effective runtime user: $(id)"; echo "Effective runtime umask: $(umask)"'

    # Use gosu to properly drop privileges and run the command
    # This ensures signals are properly forwarded and the process runs as expected
    if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ]; then
        exec gosu "$username" /usr/local/bin/chaptarr-bookshelf-compat-supervisor "$@"
    fi
    exec gosu "$username" "$@"
else
    # Not running as root
    echo "Not running as root, executing directly"
    echo "Warning: Unable to change file ownership. Ensure your files have correct permissions."
    echo "Effective runtime user: $(id)"
    echo "Effective runtime umask: $(umask)"

    if [ ! -d "$DATA_DIR" ]; then
        if ! mkdir -p "$DATA_DIR" 2>/dev/null; then
            echo "Error: Data directory '$DATA_DIR' does not exist and cannot be created."
            echo "Mount a writable volume to '$DATA_DIR' or run the container as root with PUID/PGID so it can create/chown it."
            exit 1
        fi
    fi

    validate_compatibility_paths || exit 1

    if touch "$DATA_DIR"/.write_test 2>/dev/null; then
        rm -f "$DATA_DIR"/.write_test
    else
        echo "Error: Data directory '$DATA_DIR' is not writable by UID=$(id -u) GID=$(id -g)."
        echo "If you're using a bind mount and the host folder didn't exist, Docker created it as root:root; create it manually (or fix ownership) and try again."
        exit 1
    fi

    run_application "$@"
fi
