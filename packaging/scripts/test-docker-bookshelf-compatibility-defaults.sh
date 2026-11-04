#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if [[ -n "${TASK_SCRATCH_DIR:-}" ]]; then
  test_root="$TASK_SCRATCH_DIR"
  if [[ -e "$test_root" ]]; then
    echo "Refusing to reuse test scratch path: $test_root" >&2
    exit 2
  fi
  mkdir -p "$(dirname "$test_root")"
  mkdir "$test_root"
else
  test_root="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-docker-compatibility.XXXXXX")"
fi
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    rm -rf -- "$test_root"
  else
    echo "Docker compatibility test failed; evidence kept at $test_root" >&2
  fi
  exit "$result"
}
trap cleanup EXIT


run_case() {
  local name="$1"
  shift
  if ! ( "$@" ); then
    if [[ "$name" == 'ask mode without a terminal records deferral and does not start ChaptarrNG' && -f "$test_root/ask.log" ]]; then
      printf 'Headless Ask-mode supervisor log:\n' >&2
      cat "$test_root/ask.log" >&2
    fi
    printf 'Failed Docker compatibility defaults case: %s\n' "$name" >&2
    exit 1
  fi
}

check_desktop_ask_choices() {
  set -euo pipefail
  local data_dir="$test_root/desktop-ask-data"
  local app_dir="$test_root/desktop-ask-app"
  local stub_bin="$test_root/desktop-ask-bin"
  local supervisor="$repo_root/packaging/legacy/desktop/bookshelfng-supervisor.sh"
  local database_hash status state_file

  mkdir -p "$data_dir" "$app_dir" "$stub_bin"
  printf 'synthetic BookshelfNG database\n' > "$data_dir/readarr.db"
  database_hash="$(sha256sum "$data_dir/readarr.db" | cut -d' ' -f1)"
  : > "$test_root/desktop-console-calls"
  cat > "$stub_bin/zenity" <<'SH'
#!/bin/sh
printf '%s\n' "$*" >> "$ZENITY_CALLS"
if [ "$1" = --question ]; then
  exit "$ZENITY_QUESTION_STATUS"
fi
exit 0
SH
  cat > "$app_dir/Chaptarr.Console" <<'SH'
#!/bin/sh
printf '%s\n' "$*" >> "$DESKTOP_CONSOLE_CALLS"
case "$1" in
  -preview-bookshelf-migration) exit 1 ;;
  *) exit 2 ;;
esac
SH
  printf '#!/bin/sh\nexit 0\n' > "$app_dir/Chaptarr"
  chmod 0755 "$stub_bin/zenity" "$app_dir/Chaptarr.Console" "$app_dir/Chaptarr"
  export ZENITY_CALLS="$test_root/desktop-zenity-calls"
  export DESKTOP_CONSOLE_CALLS="$test_root/desktop-console-calls"
  export PATH="$stub_bin:$PATH"
  export BOOKSHELFNG_MIGRATION_MODE=ask

  export ZENITY_QUESTION_STATUS=1
  if sh "$supervisor" "$app_dir/Chaptarr" "$data_dir" > "$test_root/desktop-no.log" 2>&1; then
    status=0
  else
    status=$?
  fi
  test "$status" -eq 0
  grep -Fq -- '--question --default-cancel --ok-label=Replace now --cancel-label=Not now' "$ZENITY_CALLS"
  test ! -s "$DESKTOP_CONSOLE_CALLS"
  test "$(sha256sum "$data_dir/readarr.db" | cut -d' ' -f1)" = "$database_hash"
  state_file="$data_dir/.chaptarrng-bookshelfng-desktop-handoff"
  test ! -e "$state_file"
  grep -Fq 'BookshelfNG conversion deferred. The source database was left untouched.' "$test_root/desktop-no.log"

  export ZENITY_QUESTION_STATUS=0
  if sh "$supervisor" "$app_dir/Chaptarr" "$data_dir" > "$test_root/desktop-yes.log" 2>&1; then
    status=0
  else
    status=$?
  fi
  test "$status" -eq 1
  grep -Fq -- '-preview-bookshelf-migration' "$DESKTOP_CONSOLE_CALLS"
  if grep -Fq -- '-migrate-bookshelf' "$DESKTOP_CONSOLE_CALLS"; then
    echo 'The desktop affirmative choice bypassed migration preview.' >&2
    return 1
  fi
  test "$(cat "$state_file")" = intervention-required
  test "$(sha256sum "$data_dir/readarr.db" | cut -d' ' -f1)" = "$database_hash"
  grep -Fq 'BookshelfNG migration preflight failed' "$test_root/desktop-yes.log"
}

check_deferred_container_stops_promptly() {
  local data_dir="$test_root/deferred-stop-data"
  local app="$test_root/deferred-stop-app"
  local supervisor_pid process_group watchdog_pid status

  mkdir -p "$data_dir"
  printf 'synthetic BookshelfNG database\n' > "$data_dir/readarr.db"
  printf '#!/bin/sh\nexit 0\n' > "$app"
  chmod 0755 "$app"

  export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
  export CHAPTARR_COMPAT_DATA_DIR="$data_dir"
  export CHAPTARR_CONFIG_FILE="$data_dir/config.xml"
  export BOOKSHELFNG_MIGRATION_MODE=ask

  setsid sh "$repo_root/docker-bookshelf-compat-supervisor.sh" "$app" \
    > "$test_root/deferred-stop.log" 2>&1 &
  supervisor_pid=$!
  process_group="$(ps -o pgid= -p "$supervisor_pid" | tr -d '[:space:]')"
  test -n "$process_group"

  for _ in {1..100}; do
    if grep -Fq 'BookshelfNG migration is deferred' "$test_root/deferred-stop.log"; then
      break
    fi
    if ! kill -0 "$supervisor_pid" 2>/dev/null; then
      wait "$supervisor_pid" || true
      cat "$test_root/deferred-stop.log" >&2
      return 1
    fi
    sleep 0.1
  done

  if ! grep -Fq 'BookshelfNG migration is deferred' "$test_root/deferred-stop.log"; then
    echo 'Deferred supervisor did not reach Ask mode within ten seconds.' >&2
    cat "$test_root/deferred-stop.log" >&2
    kill -TERM "$supervisor_pid" 2>/dev/null || true
    (
      sleep 5
      kill -KILL -- "-$process_group" 2>/dev/null || true
    ) &
    watchdog_pid=$!
    wait "$supervisor_pid" 2>/dev/null || true
    kill "$watchdog_pid" 2>/dev/null || true
    wait "$watchdog_pid" 2>/dev/null || true
    return 1
  fi

  kill -TERM "$supervisor_pid"
  (
    sleep 5
    kill -KILL -- "-$process_group" 2>/dev/null || true
  ) &
  watchdog_pid=$!
  if wait "$supervisor_pid"; then
    status=0
  else
    status=$?
  fi
  kill "$watchdog_pid" 2>/dev/null || true
  wait "$watchdog_pid" 2>/dev/null || true

  if [[ "$status" -ne 143 ]]; then
    echo "Deferred supervisor did not stop promptly on SIGTERM (exit $status)." >&2
    cat "$test_root/deferred-stop.log" >&2
    return 1
  fi
  test "$(cat "$data_dir/.chaptarrng-bookshelf-handoff/deferred")" = manual-review
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/pending"
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/failed"
}

check_multiple_container_sources_require_explicit_merge() {
  local data_dir="$test_root/multiple-source-data"
  local stub_bin="$test_root/multiple-source-bin"
  local app_started="$test_root/multiple-source-app-started"
  local app="$test_root/multiple-source-app"
  local status

  mkdir -p "$data_dir" "$stub_bin"
  printf 'readarr fixture\n' > "$data_dir/readarr.db"
  printf 'audioarr fixture\n' > "$data_dir/audioarr.db"
  readarr_hash="$(sha256sum "$data_dir/readarr.db" | cut -d' ' -f1)"
  audioarr_hash="$(sha256sum "$data_dir/audioarr.db" | cut -d' ' -f1)"
  cat > "$stub_bin/dotnet" <<'SH'
#!/bin/sh
printf '%s\n' "$*" >> "$DOTNET_CALLS"
exit 1
SH
  printf '#!/bin/sh\ntouch "$APP_STARTED"\n' > "$app"
  chmod 0755 "$stub_bin/dotnet" "$app"
  export PATH="$stub_bin:$PATH"
  export DOTNET_CALLS="$test_root/multiple-source-dotnet.log"
  export APP_STARTED="$app_started"
  export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
  export CHAPTARR_COMPAT_DATA_DIR="$data_dir"
  export CHAPTARR_CONFIG_FILE="$data_dir/config.xml"
  export BOOKSHELFNG_MIGRATION_MODE=automatic

  if timeout --signal=TERM --kill-after=2 2 sh "$repo_root/docker-bookshelf-compat-supervisor.sh" "$app" \
    > "$test_root/multiple-source.log" 2>&1; then
    status=0
  else
    status=$?
  fi
  case "$status" in 124|143) ;; *) echo "unexpected dual-source supervisor exit status: $status" >&2; return 1 ;; esac
  grep -Fq -- '-preview-bookshelf-migration -merge-bookshelf-databases' "$DOTNET_CALLS"
  grep -Fq 'migration preview failed before conversion' "$test_root/multiple-source.log"
  if grep -Fq -- '-migrate-bookshelf' "$DOTNET_CALLS"; then
    echo 'The failed preview incorrectly continued into migration.' >&2
    return 1
  fi
  test "$(sha256sum "$data_dir/readarr.db" | cut -d' ' -f1)" = "$readarr_hash"
  test "$(sha256sum "$data_dir/audioarr.db" | cut -d' ' -f1)" = "$audioarr_hash"
  test ! -e "$app_started"
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/pending"
  test "$(cat "$data_dir/.chaptarrng-bookshelf-handoff/failed")" = rolled-back
}

check_orphaned_container_wal_waits_for_recovery() {
  local data_dir="$test_root/orphaned-wal-data"
  local stub_bin="$test_root/orphaned-wal-bin"
  local app_started="$test_root/orphaned-wal-app-started"
  local app="$test_root/orphaned-wal-app"
  local status

  mkdir -p "$data_dir" "$stub_bin"
  : > "$data_dir/readarr.db"
  printf 'uncheckpointed fixture\n' > "$data_dir/readarr.db-wal"
  printf '#!/bin/sh\nexit 1\n' > "$stub_bin/dotnet"
  printf '#!/bin/sh\ntouch "$APP_STARTED"\n' > "$app"
  chmod 0755 "$stub_bin/dotnet" "$app"
  export PATH="$stub_bin:$PATH"
  export APP_STARTED="$app_started"
  export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
  export CHAPTARR_COMPAT_DATA_DIR="$data_dir"
  export CHAPTARR_CONFIG_FILE="$data_dir/config.xml"
  export BOOKSHELFNG_MIGRATION_MODE=automatic

  if timeout --signal=TERM --kill-after=2 2 sh "$repo_root/docker-bookshelf-compat-supervisor.sh" "$app" \
    > "$test_root/orphaned-wal.log" 2>&1; then
    status=0
  else
    status=$?
  fi
  case "$status" in 124|143) ;; *) echo "unexpected orphaned-WAL supervisor exit status: $status" >&2; return 1 ;; esac
  grep -Fq 'recovery data exists without a non-empty main database' "$test_root/orphaned-wal.log"
  test ! -e "$app_started"
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/pending"
}

check_shared_volume_lock_waits_with_liveness() {
  local data_dir="$test_root/shared-lock-data"
  local handoff_root="$data_dir/.chaptarrng-bookshelf-handoff"
  local lock_path="$handoff_root/supervisor.lock"
  local stub_bin="$test_root/shared-lock-bin"
  local app_started="$test_root/shared-lock-app-started"
  local app="$test_root/shared-lock-app"
  local supervisor_pid lock_fd

  if [[ -e /tmp/chaptarr-bookshelf-lock-waiting || -L /tmp/chaptarr-bookshelf-lock-waiting ]]; then
    echo 'The container lock-wait marker already exists; refusing to reuse it.' >&2
    return 1
  fi

  mkdir -p "$handoff_root" "$stub_bin"
  printf 'native fixture\n' > "$data_dir/chaptarr.db"
  printf '#!/bin/sh\nexit 0\n' > "$stub_bin/dotnet"
  printf '#!/bin/sh\ntouch "$APP_STARTED"\n' > "$app"
  chmod 0755 "$stub_bin/dotnet" "$app"
  exec {lock_fd}> "$lock_path"
  flock -n "$lock_fd"
  export PATH="$stub_bin:$PATH"
  export APP_STARTED="$app_started"
  export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
  export CHAPTARR_COMPAT_DATA_DIR="$data_dir"
  export CHAPTARR_CONFIG_FILE="$data_dir/config.xml"
  export CHAPTARR_EXPECTED_PORT=1

  sh "$repo_root/docker-bookshelf-compat-supervisor.sh" "$app" > "$test_root/shared-lock.log" 2>&1 &
  supervisor_pid=$!
  for _ in {1..50}; do
    [[ -e /tmp/chaptarr-bookshelf-lock-waiting ]] && break
    sleep 0.1
  done
  if [[ ! -e /tmp/chaptarr-bookshelf-lock-waiting ]]; then
    kill "$supervisor_pid" 2>/dev/null || true
    wait "$supervisor_pid" 2>/dev/null || true
    flock -u "$lock_fd"
    exec {lock_fd}>&-
    echo 'The supervisor did not publish its lock-wait marker.' >&2
    return 1
  fi

  CHAPTARR_BOOKSHELF_COMPATIBILITY=true sh "$repo_root/docker-bookshelf-liveness.sh"
  if CHAPTARR_BOOKSHELF_COMPATIBILITY=true CHAPTARR_COMPAT_DATA_DIR="$data_dir" \
    CHAPTARR_EXPECTED_PORT=1 sh "$repo_root/docker-healthcheck.sh" >/dev/null 2>&1; then
    kill "$supervisor_pid" 2>/dev/null || true
    wait "$supervisor_pid" 2>/dev/null || true
    flock -u "$lock_fd"
    exec {lock_fd}>&-
    echo 'A lock-waiting replica incorrectly reported ready.' >&2
    return 1
  fi

  flock -u "$lock_fd"
  exec {lock_fd}>&-
  wait "$supervisor_pid"
  test -e "$app_started"
  test ! -e /tmp/chaptarr-bookshelf-lock-waiting
}

check_container_handoff_ownership_and_signal_contract() {
  grep -Fq 'find -P "$path" -xdev ! -type l -exec chown "$uid:$gid" {} +' "$repo_root/docker-entrypoint.sh"
  grep -Fq '"$DATA_DIR"/.chaptarrng-bookshelf-handoff' "$repo_root/docker-entrypoint.sh"
  grep -Fq '"$DATA_DIR"/.chaptarrng-bookshelfng-journal' "$repo_root/docker-entrypoint.sh"
  grep -Fq '"$DATA_DIR"/asp' "$repo_root/docker-entrypoint.sh"
  grep -Fq '"$DATA_DIR"/*.pid' "$repo_root/docker-entrypoint.sh"
  grep -Fq 'kill -TERM -"$app_pid"' "$repo_root/docker-bookshelf-compat-supervisor.sh"
  grep -Fq 'kill -KILL -"$app_pid"' "$repo_root/docker-bookshelf-compat-supervisor.sh"
  if grep -Fq 'kill -TERM -- "-$app_pid"' "$repo_root/docker-bookshelf-compat-supervisor.sh"; then
    echo 'The /bin/sh compatibility supervisor still uses unsupported dash kill syntax.' >&2
    return 1
  fi
}

run_case 'production image keeps the StartOS /init command' bash -c '
  set -euo pipefail
  grep -Fq "COPY --chmod=0755 docker-startos-init.sh /init" "$1"
  grep -Fxq "ENTRYPOINT [\"/entrypoint.sh\"]" "$1"
  grep -Fq "dotnet /app/Chaptarr.dll -nobrowser -data=/config" "$2"
' _ "$repo_root/Dockerfile.cross" "$repo_root/docker-startos-init.sh"

run_case 'fresh and migrated installations publish one standard image target' bash -c '
  set -euo pipefail
  [[ "$(grep -Fc "AS chaptarrng-standard" "$1")" -eq 1 ]]
  for image_dockerfile in "$1" "$5" "$6"; do
    grep -Fxq "EXPOSE 8787 8789" "$image_dockerfile"
    if grep -Eq "bookshelf-(softcover|hardcover)|CHAPTARR_DEFAULT_PORT=8787" "$image_dockerfile"; then exit 1; fi
  done
  [[ "$(grep -Fc "docker/build-push-action@" "$2")" -eq 1 ]]
  grep -Fq "target: chaptarrng-standard" "$2"
  if grep -Eq "target: bookshelf-(softcover|hardcover)|build_(softcover|hardcover)" "$2"; then exit 1; fi
  grep -Fq "target: chaptarrng-standard" "$3"
  if grep -Eq "bookshelf-(softcover|hardcover)|matrix.target" "$3"; then exit 1; fi
  if grep -Fq "docker-bookshelf-compatibility-defaults.sh" "$4" "$5" "$6"; then exit 1; fi
' _ "$repo_root/Dockerfile.cross" "$repo_root/.github/workflows/docker-publish.yml" "$repo_root/.github/workflows/build.yml" "$repo_root/docker-entrypoint.sh" "$repo_root/Dockerfile.build" "$repo_root/Dockerfile.build.optimized"

run_case 'the standard image detects legacy appdata and preserves the saved port' bash -c '
  set -euo pipefail
  legacy_dir="$1/legacy-autodetect"
  fresh_dir="$1/fresh-autodetect"
  mkdir -p "$legacy_dir" "$fresh_dir"
  : > "$legacy_dir/readarr.db"
  printf "<Config><Port>9127</Port></Config>\n" > "$legacy_dir/config.xml"
  : > "$fresh_dir/chaptarr.db"
  env -i PATH="$PATH" DATA_DIR="$legacy_dir" sh -c '\''
    . "$1"
    test "$CHAPTARR_BOOKSHELF_COMPATIBILITY" = true
    test "$BOOKSHELFNG_MIGRATION_MODE" = automatic
    test "$CHAPTARR_DEFAULT_PORT" = 8787
    test -z "${Chaptarr__Server__Port:-}"
    test -z "${HARDCOVER:-}"
  '\'' sh "$2"
  env -i PATH="$PATH" DATA_DIR="$fresh_dir" sh -c '\''
    . "$1"
    test -z "${CHAPTARR_BOOKSHELF_COMPATIBILITY:-}"
    test -z "${CHAPTARR_DEFAULT_PORT:-}"
  '\'' sh "$2"
  for postgres_name in \
    Readarr__Postgres__Host READARR__POSTGRES__HOST \
    Audioarr__Postgres__Host AUDIOARR__POSTGRES__HOST \
    Bookshelf__Postgres__Host BOOKSHELF__POSTGRES__HOST; do
    env -i PATH="$PATH" DATA_DIR="$fresh_dir" \
      "$postgres_name=bookshelf-db" CHAPTARR_DEFAULT_PORT=8877 sh -c '\''
        . "$1"
        test "$CHAPTARR_BOOKSHELF_COMPATIBILITY" = true
        test "$BOOKSHELFNG_MIGRATION_MODE" = automatic
        test "$CHAPTARR_DEFAULT_PORT" = 8877
        test "$Chaptarr__Server__Port" = 8877
      '\'' sh "$2"
  done
  grep -Fq ". /docker-bookshelf-migration-detect.sh" "$3"
  grep -Fq "COPY docker-bookshelf-migration-detect.sh /docker-bookshelf-migration-detect.sh" "$4"
  grep -Fq "COPY docker-bookshelf-migration-detect.sh /docker-bookshelf-migration-detect.sh" "$5"
' _ "$test_root" "$repo_root/docker-bookshelf-migration-detect.sh" "$repo_root/docker-entrypoint.sh" "$repo_root/Dockerfile.build" "$repo_root/Dockerfile.cross"

run_case 'legacy channel aliases use the standard image and do not hardcode a separate provider build' bash -c '
  set -euo pipefail
  grep -Fq "Every BookshelfNG alias is published from the same standard ChaptarrNG image digest." "$1"
  grep -Fq "SOURCE_IMAGE: \${{ env.STANDARD_IMAGE }}@\${{ steps.build.outputs.digest }}" "$2"
  if grep -Eq "BOOKSHELF_(SOFTCOVER|HARDCOVER)_(IMAGE|DIGEST)|build_(softcover|hardcover)" "$2"; then exit 1; fi
' _ "$repo_root/packaging/scripts/publish-image-aliases.sh" "$repo_root/.github/workflows/docker-publish.yml"

run_case 'ask mode without a terminal records deferral and does not start ChaptarrNG' bash -c '
  set -euo pipefail
  data_dir="$1/ask-no-terminal"
  mkdir -p "$data_dir"
  stub_bin="$1/ask-bin"
  mkdir -p "$stub_bin"
  cat > "$stub_bin/dotnet" <<"SH"
#!/bin/sh
# This case tests the headless Ask decision, not the migration-status command.
exit 1
SH
  chmod 0755 "$stub_bin/dotnet"
  printf "synthetic BookshelfNG database\n" > "$data_dir/readarr.db"
  app_started="$1/ask-app-started"
  app="$1/ask-app"
  printf "#!/bin/sh\ntouch \"\$APP_STARTED\"\n" > "$app"
  chmod 0755 "$app"
  export APP_STARTED="$app_started"
  export CHAPTARR_BOOKSHELF_COMPATIBILITY=true
  export CHAPTARR_COMPAT_DATA_DIR="$data_dir"
  export CHAPTARR_CONFIG_FILE="$data_dir/config.xml"
  export BOOKSHELFNG_MIGRATION_MODE=ask
  if PATH="$stub_bin:$PATH" timeout --signal=TERM --kill-after=2 5 sh "$2" "$app" > "$1/ask.log" 2>&1; then
    status=0
  else
    status=$?
  fi
  case "$status" in 124|143) ;; *) echo "unexpected supervisor exit status: $status" >&2; exit 1 ;; esac
  deferred="$data_dir/.chaptarrng-bookshelf-handoff/deferred"
  if [[ ! -f "$deferred" ]]; then
    echo "Headless migration supervisor did not write its deferral marker within five seconds (exit $status)." >&2
    cat "$1/ask.log" >&2
    exit 1
  fi
  grep -Fxq manual-review "$deferred"
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/pending"
  test ! -e "$data_dir/.chaptarrng-bookshelf-handoff/failed"
  test ! -e "$app_started"
  if ! grep -Fq "Interactive migration choice requested without a terminal. Deferring migration" "$1/ask.log"; then
    echo "Headless migration supervisor did not reach Ask mode within five seconds (exit $status)." >&2
    cat "$1/ask.log" >&2
    exit 1
  fi
  grep -Fq "BookshelfNG migration is deferred" "$1/ask.log"
' _ "$test_root" "$repo_root/docker-bookshelf-compat-supervisor.sh"

run_case 'deferred container supervisor handles SIGTERM promptly for Kubernetes restarts' check_deferred_container_stops_promptly
run_case 'container validates a dual-database merge before startup' check_multiple_container_sources_require_explicit_merge
run_case 'container preserves orphaned SQLite WAL data for recovery' check_orphaned_container_wal_waits_for_recovery
run_case 'shared-volume container waits safely for its lock while liveness stays healthy' check_shared_volume_lock_waits_with_liveness
run_case 'handoff metadata ownership and process-group signals support the runtime shell' check_container_handoff_ownership_and_signal_contract

run_case 'AppImage and Flatpak default-No choice defers; Yes previews before applying' check_desktop_ask_choices

mkdir -p "$test_root/bin"
cat > "$test_root/bin/wget" <<'SH'
#!/bin/sh
printf '%s\n' "$*" > "$WGET_CAPTURE"
SH
chmod +x "$test_root/bin/wget"

run_case 'health check honors uppercase explicit port' bash -c '
  set -euo pipefail
  export CHAPTARR_CONFIG_FILE="$1/missing-config.xml"
  export CHAPTARR__SERVER__PORT=9123
  export CHAPTARR_DEFAULT_PORT=8787
  export WGET_CAPTURE="$1/wget.args"
  export PATH="$1/bin:$PATH"
  sh "$2"
  grep -Fq "http://127.0.0.1:9123/ping" "$WGET_CAPTURE"
' _ "$test_root" "$repo_root/docker-healthcheck.sh"

run_case 'health check honors legacy Readarr port and URL base' bash -c '
  set -euo pipefail
  export CHAPTARR_CONFIG_FILE="$1/missing-config.xml"
  unset Chaptarr__Server__Port CHAPTARR__SERVER__PORT Readarr__Server__Port READARR__SERVER__PORT Audioarr__Server__Port AUDIOARR__SERVER__PORT
  unset Chaptarr__Server__UrlBase CHAPTARR__SERVER__URLBASE Readarr__Server__UrlBase READARR__SERVER__URLBASE Audioarr__Server__UrlBase AUDIOARR__SERVER__URLBASE
  export Readarr__Server__Port=9124
  export Readarr__Server__UrlBase=/bookshelf
  export CHAPTARR_DEFAULT_PORT=8787
  export WGET_CAPTURE="$1/wget-legacy-readarr.args"
  export PATH="$1/bin:$PATH"
  sh "$2"
  grep -Fq "http://127.0.0.1:9124/bookshelf/ping" "$WGET_CAPTURE"
' _ "$test_root" "$repo_root/docker-healthcheck.sh"

run_case 'health check honors uppercase legacy Audioarr port' bash -c '
  set -euo pipefail
  export CHAPTARR_CONFIG_FILE="$1/missing-config.xml"
  unset Chaptarr__Server__Port CHAPTARR__SERVER__PORT Readarr__Server__Port READARR__SERVER__PORT Audioarr__Server__Port AUDIOARR__SERVER__PORT
  unset Chaptarr__Server__UrlBase CHAPTARR__SERVER__URLBASE Readarr__Server__UrlBase READARR__SERVER__URLBASE Audioarr__Server__UrlBase AUDIOARR__SERVER__URLBASE
  export AUDIOARR__SERVER__PORT=9133
  export CHAPTARR_DEFAULT_PORT=8787
  export WGET_CAPTURE="$1/wget-legacy-audioarr.args"
  export PATH="$1/bin:$PATH"
  sh "$2"
  grep -Fq "http://127.0.0.1:9133/ping" "$WGET_CAPTURE"
' _ "$test_root" "$repo_root/docker-healthcheck.sh"

printf 'All Docker BookshelfNG compatibility default checks passed.\n'
