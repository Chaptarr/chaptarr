#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Usage: $0 <dotnet-root> <output-packs-root>" >&2
    exit 2
fi

dotnet_root=$1
output_root=$2

die() {
    echo "$*" >&2
    exit 1
}

for pack in Microsoft.NETCore.App.Ref Microsoft.AspNetCore.App.Ref; do
    source_root="$dotnet_root/packs/$pack"
    [[ -d "$source_root" ]] || die "SDK reference pack is missing: $source_root"

    version_dirs=()
    while IFS= read -r -d '' version_dir; do
        version_dirs+=("$version_dir")
    done < <(find "$source_root" -mindepth 1 -maxdepth 1 -type d -print0 | sort -z)
    ((${#version_dirs[@]} > 0)) || die "SDK reference pack has no installed versions: $source_root"

    destination_root="$output_root/$pack"
    mkdir -p "$destination_root"
    cp -a -- "${version_dirs[@]}" "$destination_root/"
done
