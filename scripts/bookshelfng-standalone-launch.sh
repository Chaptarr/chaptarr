#!/usr/bin/env sh
set -eu

if [ "$#" -lt 1 ] || [ -z "$1" ]; then
  echo "Usage: $0 <BookshelfNG data directory> [Chaptarr arguments...]" >&2
  exit 2
fi

data_dir=$1
shift
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
bundle_dir=$(CDPATH= cd -- "$script_dir/.." && pwd -P)
handoff_dir="$script_dir/bookshelfng-handoff"
app="$bundle_dir/Chaptarr"

if [ ! -x "$app" ]; then
  echo "ChaptarrNG executable is missing: $app" >&2
  exit 1
fi
if [ ! -x "$handoff_dir/bookshelfng-supervisor.sh" ]; then
  echo "BookshelfNG handoff supervisor is missing: $handoff_dir/bookshelfng-supervisor.sh" >&2
  exit 1
fi

has_controlling_tty() {
  [ -t 0 ] && ( : </dev/tty >/dev/tty ) 2>/dev/null
}

if [ -z "${BOOKSHELFNG_MIGRATION_MODE:-}" ]; then
  if has_controlling_tty; then
    BOOKSHELFNG_MIGRATION_MODE=ask
  else
    BOOKSHELFNG_MIGRATION_MODE=manual
  fi
fi
export BOOKSHELFNG_MIGRATION_MODE

# The FreeBSD .NET runtime probes the Ports OpenSSL ABI .12 and base ABI .30.
# Newer FreeBSD packages can provide .17 or .35 instead; select an installed
# ABI only when the operator has not supplied an explicit override.
if [ "$(uname -s 2>/dev/null || true)" = FreeBSD ] && [ -z "${DOTNET_OPENSSL_VERSION_OVERRIDE:-}" ]; then
  if [ -e /usr/local/lib/libssl.so.17 ]; then
    DOTNET_OPENSSL_VERSION_OVERRIDE=17
  elif [ -e /usr/lib/libssl.so.35 ]; then
    DOTNET_OPENSSL_VERSION_OVERRIDE=35
  fi
  export DOTNET_OPENSSL_VERSION_OVERRIDE
fi

if [ "$(uname -s 2>/dev/null || true)" = FreeBSD ]; then
  if [ -L "$bundle_dir/libe_sqlite3.so" ] && [ ! -e "$bundle_dir/libe_sqlite3.so" ]; then
    rm -f "$bundle_dir/libe_sqlite3.so"
  fi
  if [ ! -e "$bundle_dir/libe_sqlite3.so" ]; then
    sqlite_library=''
    for candidate in /usr/local/lib/libsqlite3.so.0 /usr/lib/libsqlite3.so.0; do
      if [ -e "$candidate" ]; then
        sqlite_library=$candidate
        break
      fi
    done
    if [ -z "$sqlite_library" ]; then
      echo 'FreeBSD standalone archives require the SQLite runtime library. Install the sqlite3 package, then launch again.' >&2
      exit 1
    fi
    if ! ln -s "$sqlite_library" "$bundle_dir/libe_sqlite3.so"; then
      echo "Could not link the FreeBSD SQLite runtime into the standalone bundle: $bundle_dir/libe_sqlite3.so" >&2
      exit 1
    fi
  fi
fi

export BOOKSHELFNG_DESKTOP_CHANNEL=standalone-archive
export CHAPTARR_PACKAGE_UPDATE_MECHANISM=${CHAPTARR_PACKAGE_UPDATE_MECHANISM:-External}
export CHAPTARR_PACKAGE_UPDATE_MECHANISM_MESSAGE=${CHAPTARR_PACKAGE_UPDATE_MECHANISM_MESSAGE:-Replace this standalone archive with a newer compatible build.}

exec "$handoff_dir/bookshelfng-supervisor.sh" "$app" "$data_dir" "$@"
