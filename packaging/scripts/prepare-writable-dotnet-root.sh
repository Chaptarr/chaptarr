#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <source-dotnet-root> <destination-dotnet-root>" >&2
  exit 2
fi

source_root="$1"
destination_root="$2"

[[ -d "$source_root" ]] || {
  echo "The source .NET root does not exist: $source_root" >&2
  exit 2
}

source_root="$(cd -- "$source_root" && pwd -P)"
destination_root="$(realpath -m -- "$destination_root")"
case "$destination_root/" in
  "$source_root/"*)
    echo "The destination .NET root must be outside the source tree." >&2
    exit 2
    ;;
esac

[[ ! -e "$destination_root" && ! -L "$destination_root" ]] || {
  echo "The destination .NET root already exists: $destination_root" >&2
  exit 2
}

mkdir -m 0700 -p -- "$destination_root"
cp -RL --no-preserve=ownership,timestamps "$source_root"/. "$destination_root"/
find "$destination_root" -type d -exec chmod u+rwx {} +
find "$destination_root" -type f -exec chmod u+rw {} +

[[ -x "$destination_root/dotnet" ]] || {
  echo "The copied .NET root does not contain an executable dotnet host." >&2
  exit 1
}

DOTNET_ROOT="$destination_root" DOTNET_MULTILEVEL_LOOKUP=0 \
  "$destination_root/dotnet" --info >/dev/null

echo "Prepared a writable task-local .NET SDK root."
