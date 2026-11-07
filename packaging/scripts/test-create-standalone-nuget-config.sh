#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-standalone-nuget-config.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT

runtime_packs="$work/runtime-packs"
mkdir -p "$runtime_packs"

for rid in linux-x86 freebsd-x64; do
  config="$work/$rid.NuGet.config"
  python3 "$script_dir/create-standalone-nuget-config.py" \
    "$runtime_packs" "$repo_root/src/NuGet.config" "$config" "$rid"

  python3 - "$config" "$rid" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

config_path = Path(sys.argv[1])
rid = sys.argv[2]
root = ET.parse(config_path).getroot()
mapping = root.find("packageSourceMapping")
if mapping is None:
    raise SystemExit("The custom NuGet config has no package source mapping")

sources = {
    source.get("key"): {package.get("pattern") for package in source.findall("package")}
    for source in mapping.findall("packageSource")
}
custom = sources.get("chaptarr-extra-runtime-packs")
expected = {
    f"Microsoft.NETCore.App.Host.{rid}",
    f"Microsoft.NETCore.App.Runtime.{rid}",
    f"Microsoft.AspNetCore.App.Runtime.{rid}",
}
if custom != expected:
    raise SystemExit(f"Custom source mappings for {rid} were {custom}, expected {expected}")
if "*" not in sources.get("nuget.org", set()):
    raise SystemExit("The standard NuGet source no longer provides unmapped packages")
for other_rid in {"linux-x86", "freebsd-x64"} - {rid}:
    other_ids = {
        f"Microsoft.NETCore.App.Host.{other_rid}",
        f"Microsoft.NETCore.App.Runtime.{other_rid}",
        f"Microsoft.AspNetCore.App.Runtime.{other_rid}",
    }
    if custom & other_ids:
        raise SystemExit(f"The {rid} config routes {other_rid} packages to the custom source")
PY
done

if python3 "$script_dir/create-standalone-nuget-config.py" \
  "$runtime_packs" "$repo_root/src/NuGet.config" "$work/unsupported.NuGet.config" linux-arm64 >/dev/null 2>&1; then
  echo "An unsupported RID unexpectedly received a custom NuGet config." >&2
  exit 1
fi

printf '<configuration />\n' > "$work/untrusted.NuGet.config"
if python3 "$script_dir/create-standalone-nuget-config.py" \
  "$runtime_packs" "$work/untrusted.NuGet.config" "$work/untrusted-output.NuGet.config" linux-x86 >/dev/null 2>&1; then
  echo "An arbitrary XML file unexpectedly passed the source-config boundary." >&2
  exit 1
fi

echo 'Extra-platform NuGet mappings are restricted to the selected runtime packages.'
