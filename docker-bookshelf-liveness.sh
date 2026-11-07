#!/bin/sh
set -eu

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ]; then
    if [ -e /tmp/chaptarr-bookshelf-lock-waiting ] || [ -L /tmp/chaptarr-bookshelf-lock-waiting ]; then
        # A second replica sharing the app-data volume waits for the lock before
        # touching the database. Keep it alive while readiness remains false.
        exit 0
    fi

    data_dir=${CHAPTARR_COMPAT_DATA_DIR:-}
    if [ -z "$data_dir" ] && [ -r /tmp/chaptarr-health-paths ]; then
        IFS= read -r data_dir < /tmp/chaptarr-health-paths || true
    fi
    data_dir=${data_dir:-/config}
    handoff_root="$data_dir/.chaptarrng-bookshelf-handoff"

    if [ -L "$handoff_root" ]; then
        # Keep the process alive for operator recovery; readiness reports the
        # unsafe marker so an orchestrator does not create a restart loop.
        exit 0
    fi

    for marker in "$handoff_root/pending" "$handoff_root/failed" "$handoff_root/deferred"; do
        if [ -L "$marker" ]; then
            exit 0
        fi
        if [ -e "$marker" ]; then
            # The supervisor is deliberately keeping this container alive while
            # migration runs, reports an invalid marker, or waits for operator
            # recovery. Readiness remains false until every marker is cleared.
            exit 0
        fi
    done
fi

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ]; then
    # A bad service-port mapping is a readiness problem; keep the process alive
    # so the operator can correct the routing without a restart loop.
    unset CHAPTARR_EXPECTED_PORT
fi

exec /usr/local/bin/chaptarr-healthcheck
