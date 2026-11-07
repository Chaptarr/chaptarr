#!/usr/bin/env bash
set -uo pipefail

# Cloudron runs backupCommand and restoreCommand in a temporary container with
# this same image entrypoint. Let both the direct commands and shell-wrapped
# commands run without starting nginx or the Chaptarr migration supervisor.
case "${1:-}" in
    /usr/local/bin/chaptarr-cloudron-backup|/usr/local/bin/chaptarr-cloudron-restore)
        exec "$@"
        ;;
esac

case "${1:-}" in
    /bin/sh|sh|/bin/bash|bash)
        exec "$@"
        ;;
esac

# The app listens behind this package's proxy. Keep the internal port and bind
# address fixed even if the old app environment contains similarly named
# settings with different casing.
unset Chaptarr__Server__Port Chaptarr__Server__BindAddress
export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
export CHAPTARR_DEFAULT_PORT=8788
export CHAPTARR_EXPECTED_PORT=8788
export CHAPTARR__SERVER__PORT=8788
export CHAPTARR__SERVER__BINDADDRESS=127.0.0.1

# Cloudron mounts the image filesystem read-only. The base image normally
# creates its PUID account in /etc/passwd at startup, so select its built-in
# root path and leave persistent-volume ownership unchanged for rollback.
export PUID=0
export PGID=0

# Cloudron mounts the container root read-only. Keep nginx request/proxy
# buffers on its writable /run mount instead of the image's /var/lib paths.
nginx_tmp=/run/chaptarr-nginx
mkdir -p \
    "$nginx_tmp/client_body" \
    "$nginx_tmp/proxy_temp" \
    "$nginx_tmp/fastcgi_temp" \
    "$nginx_tmp/uwsgi_temp" \
    "$nginx_tmp/scgi_temp"
chmod 1777 \
    "$nginx_tmp" \
    "$nginx_tmp/client_body" \
    "$nginx_tmp/proxy_temp" \
    "$nginx_tmp/fastcgi_temp" \
    "$nginx_tmp/uwsgi_temp" \
    "$nginx_tmp/scgi_temp"

nginx_pid=
app_pid=

stop_children() {
    trap - TERM INT HUP
    for child in "$nginx_pid" "$app_pid"; do
        if [[ -n "$child" ]]; then
            kill -TERM "$child" 2>/dev/null || true
        fi
    done
    for child in "$nginx_pid" "$app_pid"; do
        if [[ -n "$child" ]]; then
            wait "$child" 2>/dev/null || true
        fi
    done
}

trap 'stop_children; exit 143' TERM
trap 'stop_children; exit 130' INT
trap 'stop_children; exit 129' HUP

nginx -g 'daemon off;' &
nginx_pid=$!

"/entrypoint.sh" "$@" &
app_pid=$!

set +e
wait -n "$nginx_pid" "$app_pid"
status=$?
set -e

stop_children
exit "$status"
