#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <verified-linux-x64-release-archive.tar.gz> <new-output-directory>" >&2
  exit 2
fi

archive=$(realpath -e -- "$1")
archive_name=$(basename -- "$archive")
if [[ ! "$archive_name" =~ ^ChaptarrNG-([0-9]+\.[0-9]+\.[0-9]+)-linux-x64\.tar\.gz$ ]]; then
  echo "Expected a ChaptarrNG MAJOR.MINOR.PATCH linux-x64 .tar.gz archive." >&2
  exit 2
fi
version=${BASH_REMATCH[1]}

if [[ "$(uname -m)" != x86_64 ]]; then
  echo "Legacy AppImage and Flatpak candidates currently target Linux x86_64." >&2
  exit 2
fi

for tool in appimagetool flatpak-builder flatpak sha256sum; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "Required local build tool is missing: $tool" >&2
    exit 2
  fi
done

for ref in 'org.freedesktop.Platform//26.08' 'org.freedesktop.Sdk//26.08'; do
  if ! flatpak info --show-commit "$ref" >/dev/null 2>&1; then
    echo "Required Flatpak runtime is not installed: $ref" >&2
    echo "Install the 26.08 Flathub runtime and SDK, then retry; this builder does not install system runtimes." >&2
    exit 2
  fi
done

output=$2
output_name=$(basename -- "$output")
if [[ "$output_name" == . || "$output_name" == .. || -z "$output_name" || -e "$output" || -L "$output" ]]; then
  echo "Output path must be a new directory: $output" >&2
  exit 2
fi

parent=$(dirname -- "$output")
mkdir -p -- "$parent"
parent=$(cd "$parent" && pwd -P)
output="$parent/$output_name"
if [[ -e "$output" || -L "$output" ]]; then
  echo "Output path already exists; choose a new task-scoped directory: $output" >&2
  exit 2
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
work=$(mktemp -d "$parent/.bookshelfng-desktop-build.XXXXXX")
cleanup() {
  result=$?
  if [[ $result -eq 0 ]]; then
    rm -rf -- "$work"
  else
    echo "Desktop package build failed; build evidence kept at $work" >&2
  fi
  exit "$result"
}
trap cleanup EXIT

staged="$work/staged"
bash "$repo_root/packaging/scripts/prepare-bookshelf-desktop-candidates.sh" "$archive" "$staged"
mkdir -p -- "$output"

appimage="$output/ChaptarrNG-${version}-bookshelfng-x86_64.AppImage"
appimagetool_runtime_args=()
if [[ -n "${APPIMAGETOOL_RUNTIME_FILE:-}" ]]; then
  if [[ ! -s "$APPIMAGETOOL_RUNTIME_FILE" ]]; then
    echo "APPIMAGETOOL_RUNTIME_FILE must name a non-empty pinned runtime file." >&2
    exit 2
  fi
  appimagetool_runtime_args+=(--runtime-file "$APPIMAGETOOL_RUNTIME_FILE")
fi
ARCH=x86_64 appimagetool "${appimagetool_runtime_args[@]}" "$staged/appimage/ChaptarrNG.AppDir" "$appimage"
chmod 0755 "$appimage"
sha256sum "$appimage" > "$appimage.sha256"

flatpak_repo="$output/flatpak-repo"
flatpak_build="$work/flatpak-build"
flatpak-builder --force-clean --disable-download --state-dir="$work/flatpak-builder-state" --repo="$flatpak_repo" \
  "$flatpak_build" "$staged/flatpak/com.snapetech.BookshelfNG.yml"
flatpak_bundle="$output/ChaptarrNG-${version}-bookshelfng.flatpak"
flatpak build-bundle "$flatpak_repo" "$flatpak_bundle" com.snapetech.BookshelfNG stable
sha256sum "$flatpak_bundle" > "$flatpak_bundle.sha256"

echo "Built BookshelfNG-identity desktop candidates for ChaptarrNG $version at $output"
echo "AppImage updates are external; the Flatpak bundle is not signed or published."
