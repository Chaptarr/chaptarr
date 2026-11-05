#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <version> <runtime-id> <output-directory>" >&2
  exit 2
fi

version=$1
rid=$2
output=$3

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must be MAJOR.MINOR.PATCH." >&2
  exit 2
fi

case "$rid" in
  linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-x64|osx-arm64|win-x64|win-x86|linux-x86|freebsd-x64) ;;
  *) echo "Unsupported release runtime identifier: $rid" >&2; exit 2 ;;
esac

if [[ "$rid" == linux-x86 || "$rid" == freebsd-x64 ]]; then
  runtime_packs="${CUSTOM_RUNTIME_PACKS_DIR:?CUSTOM_RUNTIME_PACKS_DIR is required for $rid}"
  [[ -d "$runtime_packs" ]] || {
    echo "Custom runtime-pack directory does not exist: $runtime_packs" >&2
    exit 2
  }
  for package in \
    "Microsoft.NETCore.App.Host.${rid}.10.0.12.nupkg" \
    "Microsoft.NETCore.App.Runtime.${rid}.10.0.12.nupkg" \
    "Microsoft.AspNetCore.App.Runtime.${rid}.10.0.12.nupkg"; do
    [[ -s "$runtime_packs/$package" ]] || {
      echo "Missing custom runtime package: $runtime_packs/$package" >&2
      exit 2
    }
  done
  dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
  export DOTNET_ROOT="$dotnet_root"
  export DOTNET_MULTILEVEL_LOOKUP=0
  export PATH="$dotnet_root:$PATH"
  bash packaging/scripts/install-extra-platform-runtime-packs.sh "$rid" "$(dirname "$runtime_packs")" "$dotnet_root"
fi

if [[ ! -s _output/UI/index.html ]]; then
  echo "Build the frontend first; _output/UI/index.html is missing." >&2
  exit 2
fi

mkdir -p -- "$output"
output=$(cd "$output" && pwd -P)
work=$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-standalone.XXXXXX")
trap 'rm -rf -- "$work"' EXIT
bundle="$work/ChaptarrNG"
mkdir -p "$bundle"

if [[ "$rid" == linux-x86 || "$rid" == freebsd-x64 ]]; then
  custom_nuget_config="$work/platform-runtime.NuGet.config"
  python3 packaging/scripts/create-standalone-nuget-config.py \
    "$runtime_packs" src/NuGet.config "$custom_nuget_config" "$rid"
  # The custom Linux x86 and FreeBSD runtime packs are pinned to 10.0.12.
  restore_sources=(
    "-p:RestoreConfigFile=$custom_nuget_config"
    "-p:RuntimeFrameworkVersion=10.0.12"
    "-p:ChaptarrExtraPlatformRuntimePacks=true"
  )
fi

# The Windows targeting pack metadata shipped with the pinned .NET SDK also lists
# FreeBSD as a WindowsDesktop runtime identifier, but no WindowsDesktop
# runtime pack exists for FreeBSD. This server uses the portable console
# project, so avoid restoring that unrelated framework pack for this RID.
if [[ "$rid" == freebsd-x64 ]]; then
  restore_sources+=("-p:EnableWindowsTargeting=false")
fi

publish_standalone_project() {
  # Bash 3.2 (the system Bash on supported macOS runners) treats expansion of
  # an empty array as an unset variable under nounset. Only expand this array
  # for the RIDs that populate it.
  if [[ "$rid" == linux-x86 || "$rid" == freebsd-x64 ]]; then
    dotnet publish "$@" "${restore_sources[@]}"
  else
    dotnet publish "$@"
  fi
}

publish_standalone_project src/NzbDrone.Console/Chaptarr.Console.csproj \
  --configuration Release \
  --framework net10.0 \
  --runtime "$rid" \
  --self-contained true \
  -p:UseAppHost=true \
  --verbosity minimal \
  --output "$bundle"

publish_standalone_project src/NzbDrone.Update/Chaptarr.Update.csproj \
  --configuration Release \
  --framework net10.0 \
  --runtime "$rid" \
  --self-contained true \
  -p:UseAppHost=true \
  --verbosity minimal \
  --output "$bundle/Chaptarr.Update"

mkdir -p "$bundle/UI"
cp -a _output/UI/. "$bundle/UI/"
cp LICENSE "$bundle/LICENSE"
test -s "$bundle/UI/index.html"

if [[ "$rid" == linux-* || "$rid" == osx-* ]]; then
  mkdir -p "$bundle/scripts"
  install -m 0755 scripts/bookshelfng-to-chaptarrng.sh \
    "$bundle/scripts/bookshelfng-to-chaptarrng.sh"
  install -m 0755 scripts/capture-bookshelf-metadata-env.py \
    "$bundle/scripts/capture-bookshelf-metadata-env.py"
fi

if [[ "$rid" == osx-* || "$rid" == freebsd-x64 ]]; then
  handoff_dir="$bundle/scripts/bookshelfng-handoff"
  mkdir -p "$handoff_dir"
  install -m 0755 scripts/bookshelfng-standalone-launch.sh \
    "$bundle/scripts/bookshelfng-standalone-launch.sh"
  install -m 0755 packaging/legacy/desktop/bookshelfng-supervisor.sh \
    "$handoff_dir/bookshelfng-supervisor.sh"
  install -m 0755 packaging/legacy/scripts/bookshelfng-journal \
    "$handoff_dir/bookshelfng-journal"
fi

if [[ "$rid" == linux-x64 || "$rid" == linux-arm64 ]]; then
  install -m 0755 scripts/bookshelfng-systemd-bootstrap.py \
    "$bundle/scripts/bookshelfng-systemd-bootstrap.py"
fi

asset="ChaptarrNG-${version}-${rid}"
if [[ "$rid" == win-* ]]; then
  archive="$output/${asset}.zip"
  python3 - "$bundle" "$archive" <<'PY'
import pathlib
import sys
import zipfile

root = pathlib.Path(sys.argv[1])
archive = pathlib.Path(sys.argv[2])
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output_zip:
    for path in sorted(root.rglob("*")):
        output_zip.write(path, path.relative_to(root).as_posix())
PY
else
  archive="$output/${asset}.tar.gz"
  if [[ "$(uname -s)" == Darwin ]]; then
    # macOS ships BSD tar, which does not support the GNU reproducibility flags.
    tar -czf "$archive" -C "$work" ChaptarrNG
  else
    tar --sort=name --mtime='UTC 2020-01-01' --owner=0 --group=0 --numeric-owner \
      --format=gnu -czf "$archive" -C "$work" ChaptarrNG
  fi
fi
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$output" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
elif command -v shasum >/dev/null 2>&1; then
  (cd "$output" && shasum -a 256 "$(basename "$archive")" > "$(basename "$archive").sha256")
else
  echo "No SHA-256 checksum utility is installed." >&2
  exit 2
fi
echo "Built ${archive}"
