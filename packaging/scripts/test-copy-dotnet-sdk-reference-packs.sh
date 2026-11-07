#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
copy_script="$script_dir/copy-dotnet-sdk-reference-packs.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-sdk-reference-packs.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT

sdk_root="$work/sdk"
output_root="$work/output/packs"
versions=(10.0.12 10.0.13)

for pack in Microsoft.NETCore.App.Ref Microsoft.AspNetCore.App.Ref; do
    for version in "${versions[@]}"; do
        marker="$sdk_root/packs/$pack/$version/ref/net10.0/pack.marker"
        mkdir -p "$(dirname "$marker")"
        printf '%s %s\n' "$pack" "$version" > "$marker"
    done
done

bash "$copy_script" "$sdk_root" "$output_root"
for pack in Microsoft.NETCore.App.Ref Microsoft.AspNetCore.App.Ref; do
    for version in "${versions[@]}"; do
        source="$sdk_root/packs/$pack/$version/ref/net10.0/pack.marker"
        copied="$output_root/$pack/$version/ref/net10.0/pack.marker"
        [[ -f "$copied" ]] || {
            echo "Reference pack version $version was not copied for $pack." >&2
            exit 1
        }
        cmp -s "$source" "$copied" || {
            echo "Reference pack contents changed while copying $pack $version." >&2
            exit 1
        }
    done
done

rm -rf -- "$sdk_root/packs/Microsoft.AspNetCore.App.Ref"
if bash "$copy_script" "$sdk_root" "$work/missing-pack-output" >/dev/null 2>&1; then
    echo "Reference-pack collection succeeded despite a missing SDK pack." >&2
    exit 1
fi

echo 'SDK reference-pack collection preserves every installed servicing version and rejects missing packs.'
