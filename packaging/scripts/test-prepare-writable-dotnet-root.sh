#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
prepare_script="$script_dir/prepare-writable-dotnet-root.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-writable-dotnet-root.XXXXXX")"
source_root="$work/source-dotnet"
destination_root="$work/task-dotnet"
cleanup() {
  if [[ -d "$source_root" ]]; then
    find "$source_root" -type d -exec chmod u+rwx {} +
  fi
  rm -rf -- "$work"
}
trap cleanup EXIT

mkdir -p "$source_root/host/fxr/10.0.12" \
  "$source_root/shared/Microsoft.NETCore.App/10.0.12"
cat > "$source_root/dotnet" <<'DOTNET'
#!/usr/bin/env bash
[[ "${1:-}" == "--info" ]]
[[ "${DOTNET_ROOT:-}" == "$(dirname "$0")" ]]
[[ "${DOTNET_MULTILEVEL_LOOKUP:-}" == 0 ]]
DOTNET
chmod 0755 "$source_root/dotnet"
printf 'hostfxr\n' > "$source_root/host/fxr/10.0.12/hostfxr.marker"
printf 'runtime\n' > "$source_root/shared/Microsoft.NETCore.App/10.0.12/runtime.marker"
chmod 0555 "$source_root/host/fxr/10.0.12" \
  "$source_root/shared/Microsoft.NETCore.App/10.0.12"

source_mode_before="$(stat -c '%a' "$source_root/host/fxr/10.0.12")"
bash "$prepare_script" "$source_root" "$destination_root"

source_mode_after="$(stat -c '%a' "$source_root/host/fxr/10.0.12")"
destination_mode="$(stat -c '%a' "$destination_root/host/fxr/10.0.12")"
if [[ "$source_mode_before" != "$source_mode_after" ]]; then
  echo "Preparing the SDK changed the source directory mode." >&2
  exit 1
fi
if (( (8#$destination_mode & 0200) == 0 )); then
  echo "The copied SDK directory is not writable by its owner." >&2
  exit 1
fi
grep -Fxq 'hostfxr' "$destination_root/host/fxr/10.0.12/hostfxr.marker"
grep -Fxq 'runtime' "$destination_root/shared/Microsoft.NETCore.App/10.0.12/runtime.marker"

if bash "$prepare_script" "$source_root" "$source_root/nested-dotnet" >/dev/null 2>&1; then
  echo "Preparing an SDK inside its source tree should fail." >&2
  exit 1
fi

echo "Writable .NET SDK copy preserves source permissions and rejects nested output."
