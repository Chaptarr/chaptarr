#!/bin/sh
set -eu

# Matching clients preserve same-version restore compatibility; server packages are not installed.
pg_arch=$(dpkg --print-architecture)
case "$pg_arch" in
    amd64|arm64)
        pg_codename=$(. /etc/os-release; printf '%s' "$VERSION_CODENAME")
        wget -q -O /usr/share/keyrings/postgresql.asc https://www.postgresql.org/media/keys/ACCC4CF8.asc
        printf 'deb [signed-by=/usr/share/keyrings/postgresql.asc] https://apt.postgresql.org/pub/repos/apt %s-pgdg main\n' "$pg_codename" > /etc/apt/sources.list.d/postgresql.list
        apt-get update
        apt-get install -y --no-install-recommends postgresql-client-14 postgresql-client-15 postgresql-client-16 postgresql-client-17 postgresql-client-18
        ;;
    *)
        # PGDG does not publish 32-bit packages; native distro clients remain available on these images.
        apt-get install -y --no-install-recommends postgresql-client
        ;;
esac
rm -rf /var/lib/apt/lists/*
