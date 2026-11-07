#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
temp_root=${VALIDATION_TEMP_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "$temp_root"
work=$(mktemp -d "$temp_root/chaptarrng-desktop-build-test.XXXXXX")
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    python3 - "$work" <<'PY'
from pathlib import Path
import shutil
import sys
shutil.rmtree(Path(sys.argv[1]))
PY
  else
    echo "Desktop-candidate build test failed; evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

mkdir -p "$work/archive/ChaptarrNG/UI" "$work/stubs"
printf '#!/usr/bin/env sh\nexit 0\n' > "$work/archive/ChaptarrNG/Chaptarr"
chmod 0755 "$work/archive/ChaptarrNG/Chaptarr"
printf '<!doctype html><title>ChaptarrNG</title>\n' > "$work/archive/ChaptarrNG/UI/index.html"
archive="$work/ChaptarrNG-0.9.978-linux-x64.tar.gz"
tar -czf "$archive" -C "$work/archive" ChaptarrNG
(cd "$work" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")

cat > "$work/stubs/appimagetool" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
test "${ARCH:-}" = x86_64
if [[ -n "${EXPECTED_RUNTIME_FILE:-}" ]]; then
  test "$1" = --runtime-file
  test "$2" = "$EXPECTED_RUNTIME_FILE"
  shift 2
fi
test -x "$1/AppRun"
printf 'synthetic AppImage payload\n' > "$2"
SH
cat > "$work/stubs/flatpak-builder" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
repo=''
state_dir=''
positional=()
for arg in "$@"; do
  case "$arg" in
    --repo=*) repo=${arg#--repo=} ;;
    --state-dir=*) state_dir=${arg#--state-dir=} ;;
    --force-clean|--disable-download) ;;
    *) positional+=("$arg") ;;
  esac
done
test -n "$repo"
test -n "$state_dir"
test "$(basename "$state_dir")" = flatpak-builder-state
test "${#positional[@]}" -eq 2
test -s "${positional[1]}"
mkdir -p "$repo"
printf 'synthetic Flatpak repo\n' > "$repo/summary"
SH
cat > "$work/stubs/flatpak" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
case "${1:-}" in
  info)
    printf 'synthetic-runtime-commit\n'
    ;;
  build-bundle)
    test "$#" -eq 5
    test -d "$2"
    test "$4" = com.snapetech.BookshelfNG
    test "$5" = stable
    printf 'synthetic Flatpak bundle\n' > "$3"
    ;;
  *)
    echo "unexpected flatpak command: $*" >&2
    exit 2
    ;;
esac
SH
chmod 0755 "$work/stubs/appimagetool" "$work/stubs/flatpak-builder" "$work/stubs/flatpak"

output="$work/candidates"
runtime_file="$work/runtime-x86_64"
printf 'pinned runtime fixture\n' > "$runtime_file"
PATH="$work/stubs:$PATH" \
  APPIMAGETOOL_RUNTIME_FILE="$runtime_file" \
  EXPECTED_RUNTIME_FILE="$runtime_file" \
  bash "$repo_root/packaging/scripts/build-bookshelf-desktop-candidates.sh" "$archive" "$output"
appimage="$output/ChaptarrNG-0.9.978-bookshelfng-x86_64.AppImage"
flatpak_bundle="$output/ChaptarrNG-0.9.978-bookshelfng.flatpak"
test -x "$appimage"
test -s "$flatpak_bundle"
test -s "$output/flatpak-repo/summary"
(cd "$output" && sha256sum --check "$(basename "$appimage").sha256" "$(basename "$flatpak_bundle").sha256")

if PATH="$work/stubs:$PATH" bash "$repo_root/packaging/scripts/build-bookshelf-desktop-candidates.sh" "$archive" "$output" >/dev/null 2>&1; then
  echo 'Candidate builder unexpectedly overwrote an existing output directory.' >&2
  exit 1
fi

echo 'BookshelfNG desktop candidate artifact orchestration checks passed.'
