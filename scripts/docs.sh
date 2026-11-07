#!/usr/bin/env bash
set -euo pipefail

FRAMEWORK="net10.0"
PLATFORM="${1:-}"
ARCHITECTURE="${2:-x64}"
SWAGGER_WAIT_SECONDS="${SWAGGER_WAIT_SECONDS:-45}"

case "$PLATFORM" in
  Windows)
    RUNTIME="win-$ARCHITECTURE"
    APPLICATION="Chaptarr.Console.dll"
    ;;
  Linux)
    RUNTIME="linux-$ARCHITECTURE"
    APPLICATION="Chaptarr.dll"
    ;;
  Mac)
    RUNTIME="osx-$ARCHITECTURE"
    APPLICATION="Chaptarr.dll"
    ;;
  *)
    echo "Platform must be provided as first argument: Windows, Linux or Mac" >&2
    exit 1
    ;;
esac

SLN_FILE="src/Chaptarr.sln"
OUTPUT_FILE="src/Chaptarr.Api.V1/openapi.json"
APP_PATH="_output/$FRAMEWORK/$RUNTIME/$APPLICATION"
TOOL_DIR="$(mktemp -d)"
GENERATED_OUTPUT="$TOOL_DIR/openapi.json"
SWAGGER_CLI=""
SWAGGER_PID=""

cleanup() {
  if [[ -n "$SWAGGER_PID" ]] && kill -0 "$SWAGGER_PID" 2>/dev/null; then
    kill "$SWAGGER_PID" 2>/dev/null || true
    wait "$SWAGGER_PID" 2>/dev/null || true
  fi

  rm -rf "$TOOL_DIR"
}
trap cleanup EXIT

run_msbuild() {
  dotnet msbuild -restore "$SLN_FILE" \
    -p:Configuration=Debug \
    -p:Platform=Posix \
    -p:RuntimeIdentifiers="$RUNTIME" \
    -t:PublishAllRids \
    "$@"
}

run_msbuild

dotnet tool install --tool-path "$TOOL_DIR" --version 10.2.3 Swashbuckle.AspNetCore.Cli

SWAGGER_CLI="$(find "$TOOL_DIR/.store" -type f -name dotnet-swagger.dll -print -quit)"
if [[ -z "$SWAGGER_CLI" ]]; then
  echo "Could not locate the installed Swashbuckle CLI assembly." >&2
  exit 1
fi

# Swashbuckle starts the application host while discovering the OpenAPI document.
# Keep its profile and listener isolated so generation cannot touch the operator's
# Chaptarr data folder or expose a temporary unauthenticated listener on the LAN.
HOME="$TOOL_DIR/home" \
XDG_CONFIG_HOME="$TOOL_DIR/config" \
XDG_DATA_HOME="$TOOL_DIR/data" \
XDG_CACHE_HOME="$TOOL_DIR/cache" \
Chaptarr__Server__BindAddress=127.0.0.1 \
Chaptarr__Server__Port=0 \
Chaptarr__App__LaunchBrowser=false \
DOTNET_ROLL_FORWARD=Major dotnet "$SWAGGER_CLI" tofile --output "$GENERATED_OUTPUT" "$APP_PATH" v1 &
SWAGGER_PID="$!"

for ((attempt = 0; attempt < SWAGGER_WAIT_SECONDS * 10; attempt++)); do
  if [[ -s "$GENERATED_OUTPUT" ]] && python3 -c 'import json, sys; json.load(open(sys.argv[1], encoding="utf-8"))' "$GENERATED_OUTPUT" >/dev/null 2>&1; then
    break
  fi

  if ! kill -0 "$SWAGGER_PID" 2>/dev/null; then
    if ! wait "$SWAGGER_PID"; then
      SWAGGER_PID=""
      exit 1
    fi
    SWAGGER_PID=""
    break
  fi

  sleep 0.1
done

if [[ -n "$SWAGGER_PID" ]]; then
  if [[ ! -s "$GENERATED_OUTPUT" ]] || ! python3 -c 'import json, sys; json.load(open(sys.argv[1], encoding="utf-8"))' "$GENERATED_OUTPUT" >/dev/null 2>&1; then
    echo "Swagger did not produce a complete OpenAPI document within $SWAGGER_WAIT_SECONDS seconds." >&2
    exit 1
  fi

  kill "$SWAGGER_PID" 2>/dev/null || true
  wait "$SWAGGER_PID" 2>/dev/null || true
fi
SWAGGER_PID=""

if [[ ! -f "$GENERATED_OUTPUT" ]]; then
  echo "$GENERATED_OUTPUT not found, check logs for errors" >&2
  exit 1
fi

mv "$GENERATED_OUTPUT" "$OUTPUT_FILE"
