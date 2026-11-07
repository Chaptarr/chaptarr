#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
temp_root=${VALIDATION_TEMP_ROOT:-${TMPDIR:-/tmp}}
mkdir -p "$temp_root"
work=$(mktemp -d "$temp_root/chaptarrng-desktop-candidates-test.XXXXXX")
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    rm -rf -- "$work"
  else
    echo "Desktop-candidate test failed; evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

mkdir -p "$work/archive/ChaptarrNG/UI"
printf '#!/usr/bin/env sh\nexit 0\n' > "$work/archive/ChaptarrNG/Chaptarr"
chmod 0755 "$work/archive/ChaptarrNG/Chaptarr"
printf '<!doctype html><title>ChaptarrNG</title>\n' > "$work/archive/ChaptarrNG/UI/index.html"
tar -czf "$work/ChaptarrNG-0.9.978-linux-x64.tar.gz" -C "$work/archive" ChaptarrNG
archive="$work/ChaptarrNG-0.9.978-linux-x64.tar.gz"
(cd "$work" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")

output="$work/prepared"
bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$archive" "$output"
appdir="$output/appimage/ChaptarrNG.AppDir"
flatpak="$output/flatpak"

test -x "$appdir/AppRun"
test -x "$appdir/usr/libexec/bookshelfng-supervisor.sh"
test -x "$appdir/usr/libexec/bookshelfng-journal"
test -x "$appdir/usr/bin/Chaptarr"
test -s "$appdir/com.snapetech.bookshelfng.desktop"
test -s "$appdir/bookshelfng.png"
test -x "$flatpak/src/Chaptarr"
test -x "$flatpak/src/bookshelfng-supervisor.sh"
test -x "$flatpak/src/bookshelfng-journal"
test -s "$flatpak/src/UI/index.html"
test -s "$flatpak/src/com.snapetech.BookshelfNG.desktop"
test -s "$flatpak/src/com.snapetech.BookshelfNG.metainfo.xml"
test -s "$flatpak/src/com.snapetech.BookshelfNG.png"
test -s "$flatpak/com.snapetech.BookshelfNG.yml"
grep -Fq 'app-id: com.snapetech.BookshelfNG' "$flatpak/com.snapetech.BookshelfNG.yml"
grep -Fq 'branch: stable' "$flatpak/com.snapetech.BookshelfNG.yml"

if bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$archive" "$output" >/dev/null 2>&1; then
  echo 'Candidate preparation unexpectedly overwrote an existing output directory.' >&2
  exit 1
fi

bad_archive="$work/ChaptarrNG-0.9.977-win-x64.tar.gz"
cp "$archive" "$bad_archive"
cp "$archive.sha256" "$bad_archive.sha256"
if bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$bad_archive" "$work/invalid" >/dev/null 2>&1; then
  echo 'Candidate preparation unexpectedly accepted a non-Linux archive name.' >&2
  exit 1
fi

old_archive="$work/ChaptarrNG-0.9.977-linux-x64.tar.gz"
cp "$archive" "$old_archive"
(cd "$work" && sha256sum "$(basename "$old_archive")" > "$(basename "$old_archive").sha256")
if bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$old_archive" "$work/old-release" >/dev/null 2>&1; then
  echo 'Candidate preparation unexpectedly accepted the published pre-identity-fix release.' >&2
  exit 1
fi

unsafe_root="$work/unsafe/ChaptarrNG"
mkdir -p "$unsafe_root/UI"
printf '#!/usr/bin/env sh\nexit 0\n' > "$unsafe_root/Chaptarr"
chmod 0755 "$unsafe_root/Chaptarr"
printf '<!doctype html><title>ChaptarrNG</title>\n' > "$unsafe_root/UI/index.html"
ln -s ../../../../etc/passwd "$unsafe_root/unsafe-link"
mkdir -p "$work/unsafe-archive"
unsafe_archive="$work/unsafe-archive/ChaptarrNG-0.9.978-linux-x64.tar.gz"
tar -czf "$unsafe_archive" -C "$work/unsafe" ChaptarrNG
(cd "$(dirname "$unsafe_archive")" && sha256sum "$(basename "$unsafe_archive")" > "$(basename "$unsafe_archive").sha256")
if bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$unsafe_archive" "$work/unsafe-output" >/dev/null 2>&1; then
  echo 'Candidate preparation unexpectedly accepted a non-regular archive member.' >&2
  exit 1
fi

echo 'BookshelfNG desktop candidate preparation checks passed.'
