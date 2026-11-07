#!/usr/bin/env bash
set -euo pipefail

data_dir=/app/data
archive="$data_dir/chaptarr-cloudron-persistent-dirs.tar"
checksum="$archive.sha256"
state="$data_dir/chaptarr-cloudron-persistent-dirs.state"
databases=(readarr.db audioarr.db chaptarr.db logs.db cache.db staging.db)
work=$(mktemp -d /run/chaptarr-cloudron-restore.XXXXXX)

cleanup() {
    rm -rf -- "$work"
}
trap cleanup EXIT

if [[ ! -f "$archive" || -L "$archive" || ! -f "$checksum" || -L "$checksum" || ! -f "$state" || -L "$state" ]]; then
    echo "Cloudron persistent directory backup files are missing or invalid." >&2
    exit 1
fi

expected_checksum=$(cat "$checksum")
if [[ ! "$expected_checksum" =~ ^[a-f0-9]{64}$ ]]; then
    echo "Cloudron persistent directory backup checksum is malformed." >&2
    exit 1
fi
actual_checksum=$(sha256sum "$archive")
actual_checksum=${actual_checksum%% *}
if [[ "$actual_checksum" != "$expected_checksum" ]]; then
    echo "Cloudron persistent directory backup checksum does not match." >&2
    exit 1
fi

for directory in /config /books /downloads; do
    if [[ ! -d "$directory" || -L "$directory" ]]; then
        echo "Cloudron restore requires a real persistent directory at $directory." >&2
        exit 1
    fi
done

declare -A database_status database_mode database_uid database_gid
for database in "${databases[@]}"; do
    mapfile -t entries < <(awk -v key="$database" '
        {
            separator = index($0, "=")
            if (separator > 0 && substr($0, 1, separator - 1) == key) {
                print substr($0, separator + 1)
            }
        }
    ' "$state")
    if [[ ${#entries[@]} -ne 1 ]]; then
        echo "Cloudron backup has a missing or duplicate state entry for $database." >&2
        exit 1
    fi
    entry=${entries[0]}
    if [[ "$entry" == absent ]]; then
        database_status[$database]=absent
        continue
    fi
    if [[ ! "$entry" =~ ^present:([0-7]{3,4}):([0-9]+):([0-9]+)$ ]]; then
        echo "Cloudron backup has an invalid state entry for $database." >&2
        exit 1
    fi
    database_status[$database]=present
    database_mode[$database]=${BASH_REMATCH[1]}
    database_uid[$database]=${BASH_REMATCH[2]}
    database_gid[$database]=${BASH_REMATCH[3]}
done

# Validate the complete archive before clearing a persistent directory.
tar -tf "$archive" | awk \
    -v readarr="${database_status[readarr.db]}" \
    -v audioarr="${database_status[audioarr.db]}" \
    -v chaptarr="${database_status[chaptarr.db]}" \
    -v logs="${database_status[logs.db]}" \
    -v cache="${database_status[cache.db]}" \
    -v staging="${database_status[staging.db]}" '
    {
        path = $0
        sub(/^\.\//, "", path)
        if (path ~ /^\// || path ~ /(^|\/)\.\.(\/|$)/) invalid = 1
        if (path == "config" || path ~ /^config\//) config = 1
        else if (path == "books" || path ~ /^books\//) books = 1
        else if (path == "downloads" || path ~ /^downloads\//) downloads = 1
        else invalid = 1
        if (path == "config/readarr.db") has_readarr = 1
        if (path == "config/audioarr.db") has_audioarr = 1
        if (path == "config/chaptarr.db") has_chaptarr = 1
        if (path == "config/logs.db") has_logs = 1
        if (path == "config/cache.db") has_cache = 1
        if (path == "config/staging.db") has_staging = 1
    }
    END {
        if (invalid || !config || !books || !downloads) exit 1
        if ((readarr == "present") != has_readarr) exit 1
        if ((audioarr == "present") != has_audioarr) exit 1
        if ((chaptarr == "present") != has_chaptarr) exit 1
        if ((logs == "present") != has_logs) exit 1
        if ((cache == "present") != has_cache) exit 1
        if ((staging == "present") != has_staging) exit 1
    }
' || {
    echo "Cloudron persistent directory archive has an invalid layout." >&2
    exit 1
}

# Check each SQLite snapshot before changing any restored data.
for database in "${databases[@]}"; do
    if [[ "${database_status[$database]}" == present ]]; then
        snapshot="$work/restore-${database}"
        tar -xOf "$archive" "config/$database" > "$snapshot"
        if [[ ! -s "$snapshot" || $(sqlite3 -readonly "$snapshot" 'PRAGMA integrity_check;') != ok ]]; then
            echo "SQLite integrity check failed for archived database $database." >&2
            exit 1
        fi
    fi
done

# Cloudron bind-mounts each persistentDirs path. Clear its contents in place,
# then restore the archived trees without replacing the mount points.
for directory in /config /books /downloads; do
    find "$directory" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
done
tar --numeric-owner -C / -xpf "$archive"

for database in "${databases[@]}"; do
    database_path="/config/$database"
    for sidecar in "$database_path-wal" "$database_path-shm" "$database_path-journal"; do
        rm -f -- "$sidecar"
    done
    if [[ "${database_status[$database]}" == present ]]; then
        if [[ ! -f "$database_path" || -L "$database_path" ]]; then
            echo "Restored database is missing or invalid: $database_path." >&2
            exit 1
        fi
        chown "${database_uid[$database]}:${database_gid[$database]}" "$database_path"
        chmod "${database_mode[$database]}" "$database_path"
        if [[ $(sqlite3 -readonly "$database_path" 'PRAGMA integrity_check;') != ok ]]; then
            echo "SQLite integrity check failed after restoring $database." >&2
            exit 1
        fi
    else
        rm -f -- "$database_path"
    fi
done

echo "Cloudron persistent directory restore completed."
