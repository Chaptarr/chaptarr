#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <verified-linux-x64-release-archive.tar.gz> <new-output-directory>" >&2
  exit 2
fi

archive=$(realpath -e -- "$1")
output=$2
archive_name=$(basename -- "$archive")
if [[ ! "$archive_name" =~ ^ChaptarrNG-([0-9]+\.[0-9]+\.[0-9]+)-linux-x64\.tar\.gz$ ]]; then
  echo "Expected a ChaptarrNG MAJOR.MINOR.PATCH linux-x64 .tar.gz archive." >&2
  exit 2
fi
version=${BASH_REMATCH[1]}
IFS=. read -r major minor patch <<< "$version"
if (( 10#$major == 0 && (10#$minor < 9 || (10#$minor == 9 && 10#$patch < 978)) )); then
  echo "ChaptarrNG $version predates the BookshelfNG provider-identity migration fix." >&2
  echo "Use a release archive at version 0.9.978 or newer." >&2
  exit 2
fi

if [[ -e "$output" || -L "$output" ]]; then
  echo "Output path already exists; choose a new task-scoped directory: $output" >&2
  exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
parent=$(dirname -- "$output")
mkdir -p -- "$parent"
parent=$(cd "$parent" && pwd -P)
output="$parent/$(basename -- "$output")"
if [[ -e "$output" || -L "$output" ]]; then
  echo "Output path already exists; choose a new task-scoped directory: $output" >&2
  exit 2
fi

checksum_file="$archive.sha256"
if [[ ! -s "$checksum_file" ]]; then
  echo "Release archive checksum sidecar is missing: $checksum_file" >&2
  exit 2
fi
(cd "$(dirname "$archive")" && sha256sum --check "$(basename "$checksum_file")")

work=$(mktemp -d "$parent/.bookshelfng-desktop-candidates.XXXXXX")
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    rm -rf -- "$work"
  else
    echo "Desktop candidate staging failed; evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

mkdir -p "$work/extracted" "$work/output/appimage/ChaptarrNG.AppDir/usr/bin" \
  "$work/output/appimage/ChaptarrNG.AppDir/usr/libexec" \
  "$work/output/appimage/ChaptarrNG.AppDir/usr/share/applications" \
  "$work/output/appimage/ChaptarrNG.AppDir/usr/share/icons/hicolor/512x512/apps" \
  "$work/output/flatpak/src"
python3 - "$archive" "$work/extracted" <<'PY'
import pathlib
import sys
import tarfile

archive_path = pathlib.Path(sys.argv[1])
extract_root = pathlib.Path(sys.argv[2])
seen = set()
found_root = False

try:
    with tarfile.open(archive_path, "r:gz") as bundle:
        members = bundle.getmembers()
        if not members:
            raise ValueError("archive is empty")

        for member in members:
            name = member.name
            path = pathlib.PurePosixPath(name)
            parts = path.parts
            if path.is_absolute() or not parts or any(part in ("", ".", "..") for part in parts):
                raise ValueError(f"archive contains an unsafe path: {name}")
            if parts[0] != "ChaptarrNG":
                raise ValueError(f"archive has an unexpected root path: {name}")
            if not (member.isdir() or member.isfile()):
                raise ValueError(f"archive contains a non-regular file: {name}")
            if name in seen:
                raise ValueError(f"archive contains a duplicate path: {name}")
            seen.add(name)
            found_root |= len(parts) == 1 and member.isdir()

            destination = extract_root.joinpath(*parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            if member.isdir():
                destination.mkdir(exist_ok=True)
                destination.chmod(member.mode & 0o777)
                continue

            source = bundle.extractfile(member)
            if source is None:
                raise ValueError(f"archive file has no content: {name}")
            with source, destination.open("xb") as target:
                while chunk := source.read(1024 * 1024):
                    target.write(chunk)
            destination.chmod(member.mode & 0o777)

        if not found_root:
            raise ValueError("archive is missing its ChaptarrNG root directory")
except (OSError, tarfile.TarError, ValueError) as error:
    print(f"Release archive validation failed: {error}", file=sys.stderr)
    raise SystemExit(2)
PY
bundle="$work/extracted/ChaptarrNG"
if [[ ! -x "$bundle/Chaptarr" || ! -s "$bundle/UI/index.html" ]]; then
  echo "The release archive is missing its Chaptarr executable or web UI." >&2
  exit 1
fi

appdir="$work/output/appimage/ChaptarrNG.AppDir"
cp -a "$bundle/." "$appdir/usr/bin/"
cp "$repo_root/packaging/legacy/appimage/AppRun" "$appdir/AppRun"
chmod 0755 "$appdir/AppRun"
cp "$repo_root/packaging/legacy/desktop/bookshelfng-supervisor.sh" \
  "$appdir/usr/libexec/bookshelfng-supervisor.sh"
chmod 0755 "$appdir/usr/libexec/bookshelfng-supervisor.sh"
cp "$repo_root/packaging/legacy/scripts/bookshelfng-journal" \
  "$appdir/usr/libexec/bookshelfng-journal"
chmod 0755 "$appdir/usr/libexec/bookshelfng-journal"
cp "$repo_root/packaging/legacy/appimage/com.snapetech.bookshelfng.desktop" \
  "$appdir/com.snapetech.bookshelfng.desktop"
cp "$appdir/com.snapetech.bookshelfng.desktop" \
  "$appdir/usr/share/applications/com.snapetech.bookshelfng.desktop"
cp "$repo_root/Logo/512.png" "$appdir/bookshelfng.png"
cp "$repo_root/Logo/512.png" \
  "$appdir/usr/share/icons/hicolor/512x512/apps/bookshelfng.png"

cp -a "$bundle/." "$work/output/flatpak/src/"
cp "$repo_root/packaging/legacy/desktop/bookshelfng-supervisor.sh" \
  "$work/output/flatpak/src/bookshelfng-supervisor.sh"
cp "$repo_root/packaging/legacy/scripts/bookshelfng-journal" \
  "$work/output/flatpak/src/bookshelfng-journal"
chmod 0755 "$work/output/flatpak/src/bookshelfng-supervisor.sh" \
  "$work/output/flatpak/src/bookshelfng-journal"
cp "$repo_root/packaging/legacy/flatpak/com.snapetech.BookshelfNG.desktop" \
  "$work/output/flatpak/src/com.snapetech.BookshelfNG.desktop"
cp "$repo_root/packaging/legacy/flatpak/com.snapetech.BookshelfNG.metainfo.xml" \
  "$work/output/flatpak/src/com.snapetech.BookshelfNG.metainfo.xml"
cp "$repo_root/Logo/512.png" \
  "$work/output/flatpak/src/com.snapetech.BookshelfNG.png"
cp "$repo_root/packaging/legacy/flatpak/com.snapetech.BookshelfNG.yml" \
  "$work/output/flatpak/com.snapetech.BookshelfNG.yml"

mv -- "$work/output" "$output"
echo "Prepared AppImage and Flatpak sources for ChaptarrNG $version at $output"
