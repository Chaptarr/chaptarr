#!/usr/bin/env bash
set -euo pipefail

# A legacy platform argument is ignored: the contract is generated on this host.
# Startup/Swagger registration runs without booting the app or opening a database.
DOCS_DIR="$(mktemp -d)"
DOCS_PID=""
DOCS_OUTPUT="src/Chaptarr.Api.V1/openapi.json"
cleanup() {
  if [[ -n "$DOCS_PID" ]]; then
    kill -- "-$DOCS_PID" 2>/dev/null || true
    wait "$DOCS_PID" 2>/dev/null || true
  fi
  rm -rf "$DOCS_DIR"
  rm -f "$DOCS_OUTPUT.tmp"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# dotnet build/test also spawn children. Stop the whole owned group on interruption.
set -m
run() {
  "$@" &
  DOCS_PID="$!"
  wait "$DOCS_PID"
  kill -- "-$DOCS_PID" 2>/dev/null || true
  DOCS_PID=""
}
run dotnet build src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj -m:1 -nr:false -p:UseSharedCompilation=false
run dotnet test _tests/net10.0/Chaptarr.Core.Test.dll \
  --filter 'FullyQualifiedName=Chaptarr.Core.Test.Api.OpenApiContractFixture.export_current_contract' \
  --results-directory "$DOCS_DIR/results" -- \
  "NUnit.WorkDirectory=$DOCS_DIR"

if [[ ! -s "$DOCS_DIR/openapi.generated.json" ]]; then
  echo "OpenAPI export failed: the contract test produced no openapi.generated.json" >&2
  exit 1
fi
cp "$DOCS_DIR/openapi.generated.json" "$DOCS_OUTPUT.tmp"
mv "$DOCS_OUTPUT.tmp" "$DOCS_OUTPUT"
