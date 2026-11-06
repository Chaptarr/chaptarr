#!/usr/bin/env bash
set -euo pipefail

if [[ -n "${CHAPTARRNG_POSTGRES_SMOKE_DIR:-}" ]]; then
  data_dir="$CHAPTARRNG_POSTGRES_SMOKE_DIR"
  mkdir -m 700 -- "$data_dir"
else
  data_dir="$(mktemp -d "${TMPDIR:-/tmp}/chaptarr-migrations.XXXXXX")"
fi

app_log="$data_dir/app.log"
test_results_directory="$data_dir/nunit-results"
app_pid=""
readiness_timeout_seconds=300

cleanup() {
  local status=$?
  if [[ -n "$app_pid" ]]; then
    kill "$app_pid" >/dev/null 2>&1 || true
    wait "$app_pid" >/dev/null 2>&1 || true
  fi
  if (( status == 0 )); then
    rm -rf -- "$data_dir"
  else
    printf 'PostgreSQL smoke logs kept in %s\n' "$data_dir" >&2
  fi
}
trap cleanup EXIT

dotnet run --project src/NzbDrone.Console/Chaptarr.Console.csproj \
  -c Release --no-build --framework net10.0 -- \
  /nobrowser "/data=$data_dir" >"$app_log" 2>&1 &
app_pid=$!

ready=""
for _ in $(seq 1 "$readiness_timeout_seconds"); do
  if curl -fsS "http://127.0.0.1:8789/ping" >/dev/null 2>&1; then
    ready="yes"
    break
  fi
  if ! kill -0 "$app_pid" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

if [[ "$ready" != "yes" ]]; then
  echo "Chaptarr failed to start or /ping did not become ready." >&2
  tail -n 400 "$app_log" >&2 || true
  exit 1
fi

# The PostgreSQL fixtures create and restore disposable schemas in the configured
# database and verify that their source database is idle before taking a dump.
# Stop the startup-smoke process before those checks so its connection pool does
# not look like an external client holding the source database open.
kill "$app_pid" >/dev/null 2>&1 || true
wait "$app_pid" >/dev/null 2>&1 || true
app_pid=""

mkdir -p "$test_results_directory"
dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj \
  --configuration Release --no-restore --no-build --verbosity normal \
  --filter 'FullyQualifiedName~BookshelfPostgresDatabaseConverterFixture|FullyQualifiedName~BookshelfPostgresExternalBackupFixture|FullyQualifiedName~BookshelfQualityProfilePostgresServiceFixture' \
  -- \
  "NUnit.WorkDirectory=$data_dir" \
  "NUnit.TestOutputXml=nunit-results"

echo "PostgreSQL migrations and BookshelfNG conversion smoke tests passed."
