#!/bin/sh

# The standard ChaptarrNG image recognizes a BookshelfNG data directory when a
# legacy distribution alias is redirected to it. Platform adapters may set the
# flag explicitly; this detects direct Docker installs that only carry appdata.
bookshelf_source_found=false
for database in "$DATA_DIR/readarr.db" "$DATA_DIR/audioarr.db"; do
    if [ -e "$database" ] || [ -L "$database" ] || \
       [ -s "$database-wal" ] || [ -s "$database-journal" ] || \
       [ -L "$database-wal" ] || [ -L "$database-journal" ]; then
        bookshelf_source_found=true
        break
    fi
done

if [ "$bookshelf_source_found" != true ]; then
    # These legacy service prefixes identify migration sources. Chaptarr's own
    # PostgreSQL setting is also used by fresh installs and is not sufficient.
    for name in \
        Readarr__Postgres__Host READARR__POSTGRES__HOST \
        Audioarr__Postgres__Host AUDIOARR__POSTGRES__HOST \
        Bookshelf__Postgres__Host BOOKSHELF__POSTGRES__HOST; do
        value=$(printenv "$name" 2>/dev/null || true)
        if [ -n "$value" ]; then
            bookshelf_source_found=true
            break
        fi
    done
fi

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" != true ] && [ "$bookshelf_source_found" = true ]; then
    export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
    export BOOKSHELFNG_MIGRATION_MODE="${BOOKSHELFNG_MIGRATION_MODE:-automatic}"
fi

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = true ]; then
    export CHAPTARR_DEFAULT_PORT="${CHAPTARR_DEFAULT_PORT:-8787}"

    configured_port="${Chaptarr__Server__Port:-${CHAPTARR__SERVER__PORT:-${Readarr__Server__Port:-${READARR__SERVER__PORT:-${Audioarr__Server__Port:-${AUDIOARR__SERVER__PORT:-}}}}}}"
    if [ -z "$configured_port" ]; then
        configured_port="${Bookshelf__Server__Port:-${BOOKSHELF__SERVER__PORT:-${BOOKSHELF_PORT:-}}}"
    fi

    if [ -z "$configured_port" ]; then
        saved_port=""
        if [ -r "${DATA_DIR}/config.xml" ]; then
            saved_port="$(sed -n 's/.*<Port>\([0-9][0-9]*\)<\/Port>.*/\1/p' "${DATA_DIR}/config.xml" | head -n 1)"
        fi

        if [ -z "$saved_port" ]; then
            export Chaptarr__Server__Port="$CHAPTARR_DEFAULT_PORT"
        fi
    fi
fi
