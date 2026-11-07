#!/bin/sh
set -eu

app_only=false
if [ "$#" -gt 1 ]; then
    echo "Usage: chaptarr-healthcheck [--app-only]" >&2
    exit 2
fi
if [ "$#" -eq 1 ]; then
    case "$1" in
        --app-only) app_only=true ;;
        *)
            echo "Usage: chaptarr-healthcheck [--app-only]" >&2
            exit 2
            ;;
    esac
fi

data_dir=${CHAPTARR_COMPAT_DATA_DIR:-}
if [ -z "$data_dir" ] && [ -r /tmp/chaptarr-health-paths ]; then
    IFS= read -r data_dir < /tmp/chaptarr-health-paths || true
fi
data_dir=${data_dir:-/config}

if [ "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}" = "true" ] && [ "$app_only" != true ]; then
    handoff_root="$data_dir/.chaptarrng-bookshelf-handoff"
    if [ -L "$handoff_root" ]; then
        echo 'BookshelfNG handoff state directory is unsafe.' >&2
        exit 1
    fi

    for state in pending deferred failed; do
        marker="$handoff_root/$state"
        if [ -L "$marker" ]; then
            echo 'BookshelfNG handoff state marker is unsafe.' >&2
            exit 1
        fi
        if [ -e "$marker" ]; then
            if [ ! -f "$marker" ]; then
                echo 'BookshelfNG handoff state marker is invalid.' >&2
            else
                echo "BookshelfNG migration is not ready ($state state)." >&2
            fi
            exit 1
        fi
    done
fi

config_file=${CHAPTARR_CONFIG_FILE:-}
if [ -z "$config_file" ] && [ -r /tmp/chaptarr-health-paths ]; then
    config_file=$(sed -n '2p' /tmp/chaptarr-health-paths)
fi
config_file="${config_file:-/config/config.xml}"
port="${Chaptarr__Server__Port:-${CHAPTARR__SERVER__PORT:-${Readarr__Server__Port:-${READARR__SERVER__PORT:-${Audioarr__Server__Port:-${AUDIOARR__SERVER__PORT:-}}}}}}"
if [ -z "$port" ]; then port="${Bookshelf__Server__Port:-${BOOKSHELF__SERVER__PORT:-${BOOKSHELF_PORT:-}}}"; fi
url_base="${Chaptarr__Server__UrlBase:-${CHAPTARR__SERVER__URLBASE:-${Readarr__Server__UrlBase:-${READARR__SERVER__URLBASE:-${Audioarr__Server__UrlBase:-${AUDIOARR__SERVER__URLBASE:-}}}}}}"
if [ -z "$url_base" ]; then url_base="${Bookshelf__Server__UrlBase:-${BOOKSHELF__SERVER__URLBASE:-${BOOKSHELF_URLBASE:-}}}"; fi
bind_address="${Chaptarr__Server__BindAddress:-${CHAPTARR__SERVER__BINDADDRESS:-${Readarr__Server__BindAddress:-${READARR__SERVER__BINDADDRESS:-${Audioarr__Server__BindAddress:-${AUDIOARR__SERVER__BINDADDRESS:-}}}}}}"
if [ -z "$bind_address" ]; then bind_address="${Bookshelf__Server__BindAddress:-${BOOKSHELF__SERVER__BINDADDRESS:-}}"; fi

if [ -r "$config_file" ]; then
    if [ -z "$port" ]; then
        port="$(sed -n 's/.*<Port>\([0-9][0-9]*\)<\/Port>.*/\1/p' "$config_file" | head -n 1)"
    fi
    if [ -z "$url_base" ]; then
        url_base="$(sed -n 's/.*<UrlBase>\([^<]*\)<\/UrlBase>.*/\1/p' "$config_file" | head -n 1)"
    fi
    if [ -z "$bind_address" ]; then
        bind_address="$(sed -n 's/.*<BindAddress>\([^<]*\)<\/BindAddress>.*/\1/p' "$config_file" | head -n 1)"
    fi
fi

port="${port:-${CHAPTARR_DEFAULT_PORT:-8789}}"
case "$port" in
    ''|*[!0-9]*)
        echo "Invalid Chaptarr HTTP port for Docker health check." >&2
        exit 1
        ;;
esac

expected_port=${CHAPTARR_EXPECTED_PORT:-}
if [ -n "$expected_port" ] && [ "$port" != "$expected_port" ]; then
    echo "Chaptarr is configured for port $port, but this deployment routes to port $expected_port." >&2
    exit 1
fi

if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
    echo "Invalid Chaptarr HTTP port for Docker health check." >&2
    exit 1
fi

bind_address=${bind_address:-*}
case "$bind_address" in
    ''|'*'|+|0.0.0.0)
        health_host=127.0.0.1
        ;;
    ::|'[::]')
        health_host='[::1]'
        ;;
    localhost|127.*|'[::1]')
        health_host=$bind_address
        ;;
    *)
        case "$bind_address" in
            \[*\])
                bind_address=${bind_address#\[}
                bind_address=${bind_address%\]}
                ;;
        esac
        if ! command -v ip >/dev/null 2>&1; then
            echo 'Cannot verify the configured Chaptarr bind address because iproute2 is unavailable.' >&2
            exit 1
        fi
        case "$bind_address" in
            *:*)
                if ! ip -o -6 address show | awk -v address="$bind_address" '$3 == "inet6" { split($4, item, "/"); if (item[1] == address) found = 1 } END { exit !found }'; then
                    echo 'The configured Chaptarr IPv6 bind address is not assigned to this container.' >&2
                    exit 1
                fi
                health_host="[$bind_address]"
                ;;
            *)
                if ! ip -o -4 address show | awk -v address="$bind_address" '$3 == "inet" { split($4, item, "/"); if (item[1] == address) found = 1 } END { exit !found }'; then
                    echo 'The configured Chaptarr IPv4 bind address is not assigned to this container.' >&2
                    exit 1
                fi
                health_host=$bind_address
                ;;
        esac
        ;;
esac

url_base="${url_base#/}"
url_base="${url_base%/}"
if [ -n "$url_base" ]; then
    url_base="/$url_base"
fi

exec wget --no-proxy --max-redirect=0 --quiet --output-document=/dev/null "http://${health_host}:${port}${url_base}/ping"
