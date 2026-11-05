#!/usr/bin/env bash
set -euo pipefail

rid="${1:?Usage: install-extra-platform-runtime-packs.sh <linux-x86|freebsd-x64> <artifact-root> [dotnet-root]}"
artifact_root="${2:?Usage: install-extra-platform-runtime-packs.sh <linux-x86|freebsd-x64> <artifact-root> [dotnet-root]}"
dotnet_root="${3:-${DOTNET_ROOT:-/opt/dotnet}}"
sdk_version="${DOTNETVERSION:-10.0.403}"

case "$rid" in
    linux-x86|freebsd-x64) ;;
    *) echo "Unsupported platform runtime: $rid" >&2; exit 2 ;;
esac

# Self-contained publish obtains the target host/runtime from the local NuGet
# packages. Installing those foreign binaries under shared/ or host/fxr/ can
# replace a same-version native build runtime. Only reference packs belong here.
if [[ ! -x "$dotnet_root/dotnet" || ! -f "$dotnet_root/sdk/$sdk_version/dotnet.dll" ]]; then
    echo "Install .NET SDK $sdk_version in DOTNET_ROOT before adding extra-platform reference packs." >&2
    exit 1
fi
if [[ ! -d "$artifact_root/sdk/packs" ]]; then
    echo "Runtime-pack artifact is missing its SDK reference packs." >&2
    exit 1
fi
export DOTNET_ROOT="$dotnet_root"
export DOTNET_MULTILEVEL_LOOKUP=0
export PATH="$dotnet_root:$PATH"
# Validate the selected host before writing even reference-pack cache entries.
"$dotnet_root/dotnet" --info >/dev/null
mkdir -p "$dotnet_root/packs"
cp -a --no-preserve=mode,ownership,timestamps "$artifact_root/sdk/packs/." "$dotnet_root/packs/"
"$dotnet_root/dotnet" --info
