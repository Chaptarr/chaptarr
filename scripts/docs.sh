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
SWAGGER_PID=""

cleanup() {
  if [[ -n "$SWAGGER_PID" ]] && kill -0 "$SWAGGER_PID" 2>/dev/null; then
    kill "$SWAGGER_PID" 2>/dev/null || true
    wait "$SWAGGER_PID" 2>/dev/null || true
  fi

  rm -rf "$TOOL_DIR"
}
trap cleanup EXIT

dotnet msbuild -restore "$SLN_FILE"   -p:Configuration=Debug   -p:Platform=Posix   -p:RuntimeIdentifiers="$RUNTIME"   -t:PublishAllRids

dotnet tool install --tool-path "$TOOL_DIR" --version 9.0.6 Swashbuckle.AspNetCore.Cli

rm -f "$OUTPUT_FILE"

Chaptarr__App__LaunchBrowser=false DOTNET_ROLL_FORWARD=Major "$TOOL_DIR/swagger" tofile --output "$OUTPUT_FILE" "$APP_PATH" v1 &
SWAGGER_PID="$!"

sleep "$SWAGGER_WAIT_SECONDS"

if kill -0 "$SWAGGER_PID" 2>/dev/null; then
  kill "$SWAGGER_PID" 2>/dev/null || true
  wait "$SWAGGER_PID" 2>/dev/null || true
else
  wait "$SWAGGER_PID"
fi
SWAGGER_PID=""

if [[ ! -f "$OUTPUT_FILE" ]]; then
  echo "$OUTPUT_FILE not found, check logs for errors" >&2
  exit 1
fi
