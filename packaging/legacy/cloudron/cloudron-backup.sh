#!/usr/bin/env bash
set -euo pipefail
umask 077

data_dir=/app/data
archive="$data_dir/chaptarr-cloudron-persistent-dirs.tar"
checksum="$archive.sha256"
state="$data_dir/chaptarr-cloudron-persistent-dirs.state"
databases=(readarr.db audioarr.db chaptarr.db logs.db cache.db staging.db)
work=$(mktemp -d "$data_dir/.chaptarr-cloudron-backup.XXXXXX")

cleanup() {
    rm -rf -- "$work"
}
trap cleanup EXIT

for directory in /config /books /downloads; do
    if [[ ! -d "$directory" || -L "$directory" ]]; then
        echo "Cloudron backup requires a real persistent directory at $directory." >&2
        exit 1
    fi
done

mkdir -p "$work/snapshots/config"
: > "$work/state"

for database in "${databases[@]}"; do
    database_path="/config/$database"
    if [[ -e "$database_path" || -L "$database_path" ]]; then
        if [[ ! -f "$database_path" || -L "$database_path" ]]; then
            echo "Refusing to back up a non-regular database at $database_path." >&2
            exit 1
        fi

        mode=$(stat -c '%a' "$database_path")
        uid=$(stat -c '%u' "$database_path")
        gid=$(stat -c '%g' "$database_path")
        snapshot="$work/snapshots/config/$database"
        sqlite3 -readonly "$database_path" ".backup '$snapshot'"
        if [[ $(sqlite3 -readonly "$snapshot" 'PRAGMA integrity_check;') != ok ]]; then
            echo "SQLite integrity check failed for backup snapshot $database." >&2
            exit 1
        fi
        chmod "$mode" "$snapshot"
        chown "$uid:$gid" "$snapshot"
        printf '%s=present:%s:%s:%s\n' "$database" "$mode" "$uid" "$gid" >> "$work/state"
    else
        printf '%s=absent\n' "$database" >> "$work/state"
    fi
done

temporary_archive="$work/persistent-dirs.tar"
tar_excludes=()
for database in "${databases[@]}"; do
    tar_excludes+=(
        "--exclude=config/$database"
        "--exclude=config/$database-wal"
        "--exclude=config/$database-shm"
        "--exclude=config/$database-journal"
    )
done
tar "${tar_excludes[@]}" -C / -cf "$temporary_archive" config books downloads

for database in "${databases[@]}"; do
    if [[ -f "$work/snapshots/config/$database" ]]; then
        tar -C "$work/snapshots" -rf "$temporary_archive" "config/$database"
    fi
done

temporary_checksum="$work/persistent-dirs.tar.sha256"
sha256sum "$temporary_archive" | awk '{ print $1 }' > "$temporary_checksum"

mv -f -- "$temporary_archive" "$archive"
mv -f -- "$work/state" "$state"
mv -f -- "$temporary_checksum" "$checksum"
echo "Cloudron persistent directory backup completed."
