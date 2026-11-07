#!/bin/sh
set -e

if [ "$#" -eq 0 ]; then
    set -- dotnet /app/Chaptarr.dll -nobrowser -data=/config
fi

exec /entrypoint.sh "$@"
