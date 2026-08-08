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
for arg in "$@"; do
    case "$arg" in
        -data=*|/data=*|--data=*)
            DATA_DIR="${arg#*=}"
            ;;
    esac
done

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

# Only run user management if we're root
if [ "$EUID" -eq 0 ]; then
    echo "Running as root, setting up user..."
    
    # Handle special case: root user requested
    if [ "$PUID" -eq 0 ] || [ "$PGID" -eq 0 ]; then
        echo "Warning: Running as root (UID 0) is not recommended for security reasons"
        exec "$@"
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
    
    # Smart ownership management - only change what's needed
    echo "Checking $DATA_DIR ownership..."
    
    # First, check if we can even write to the parent directory
    if touch "$DATA_DIR"/.write_test 2>/dev/null; then
        rm -f "$DATA_DIR"/.write_test
        
        # Only fix ownership of key files/directories that Chaptarr creates
        for item in "$DATA_DIR" "$DATA_DIR"/*.db "$DATA_DIR"/logs "$DATA_DIR"/MediaCover "$DATA_DIR"/Backups; do
            safe_chown "$item" "$PUID" "$PGID"
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

    if touch "$DATA_DIR"/.write_test 2>/dev/null; then
        rm -f "$DATA_DIR"/.write_test
    else
        echo "Error: Data directory '$DATA_DIR' is not writable by UID=$(id -u) GID=$(id -g)."
        echo "If you're using a bind mount and the host folder didn't exist, Docker created it as root:root; create it manually (or fix ownership) and try again."
        exit 1
    fi

    exec "$@"
fi
