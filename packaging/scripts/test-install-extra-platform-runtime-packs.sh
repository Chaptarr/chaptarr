#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../.." && pwd -P)"
install_script="$repo_root/packaging/scripts/install-extra-platform-runtime-packs.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-runtime-pack-install.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT

for rid in linux-x86 freebsd-x64; do
  artifact_root="$work/$rid-artifacts"
  dotnet_root="$work/$rid-dotnet"
  runtime_source="$work/$rid-runtime"
  aspnet_source="$work/$rid-aspnet"
  mkdir -p \
    "$artifact_root/runtimes" \
    "$artifact_root/sdk/sdk/10.0.403" \
    "$artifact_root/sdk/packs/Microsoft.NETCore.App.Ref" \
    "$dotnet_root/packs/Microsoft.NETCore.App.Ref" \
    "$dotnet_root/sdk/10.0.403" \
    "$runtime_source/host/fxr/10.0.12" \
    "$runtime_source/shared/Microsoft.NETCore.App/10.0.12" \
    "$aspnet_source/shared/Microsoft.AspNetCore.App/10.0.12" \
    "$dotnet_root/host/fxr/10.0.12" \
    "$dotnet_root/shared/Microsoft.NETCore.App/10.0.12" \
    "$dotnet_root/shared/Microsoft.AspNetCore.App/10.0.12"

  cat > "$dotnet_root/dotnet" <<'DOTNET'
#!/usr/bin/env bash
[[ "${1:-}" == --info ]]
DOTNET
  chmod 0755 "$dotnet_root/dotnet"
  cp "$dotnet_root/dotnet" "$work/$rid-sdk-dotnet"
  touch "$dotnet_root/sdk/10.0.403/dotnet.dll"
  printf 'existing SDK\n' > "$dotnet_root/sdk/10.0.403/sdk.marker"
  printf 'existing hostfxr\n' > "$dotnet_root/host/fxr/10.0.12/hostfxr.marker"
  printf 'existing framework\n' > "$dotnet_root/shared/Microsoft.NETCore.App/10.0.12/framework.marker"
  printf 'existing aspnet\n' > "$dotnet_root/shared/Microsoft.AspNetCore.App/10.0.12/aspnet.marker"

  cat > "$runtime_source/dotnet" <<'TARGET_DOTNET'
#!/usr/bin/env bash
echo 'target runtime apphost must not replace the SDK host' >&2
exit 99
TARGET_DOTNET
  chmod 0755 "$runtime_source/dotnet"

  printf 'runtime %s\n' "$rid" > "$runtime_source/host/fxr/10.0.12/hostfxr.marker"
  printf 'framework %s\n' "$rid" > "$runtime_source/shared/Microsoft.NETCore.App/10.0.12/framework.marker"
  printf 'aspnet %s\n' "$rid" > "$aspnet_source/shared/Microsoft.AspNetCore.App/10.0.12/aspnet.marker"
  printf 'sdk %s\n' "$rid" > "$artifact_root/sdk/sdk/10.0.403/sdk.marker"
  printf 'pack %s\n' "$rid" > "$artifact_root/sdk/packs/Microsoft.NETCore.App.Ref/pack.marker"
  printf 'existing pack %s\n' "$rid" > "$dotnet_root/packs/Microsoft.NETCore.App.Ref/pack.marker"
  touch -d '@946684800' "$artifact_root/sdk/packs/Microsoft.NETCore.App.Ref/pack.marker"
  touch -d '@946771200' "$dotnet_root/packs/Microsoft.NETCore.App.Ref/pack.marker"

  tar --mode=0555 --mtime='@946684800' -czf "$artifact_root/runtimes/$rid-runtime.tar.gz" \
    -C "$runtime_source" .
  tar --mode=0555 --mtime='@946684800' -czf "$artifact_root/runtimes/$rid-aspnetcore-shared.tar.gz" \
    -C "$aspnet_source/shared/Microsoft.AspNetCore.App/10.0.12" .

  for directory in \
    "$dotnet_root/host" \
    "$dotnet_root/host/fxr" \
    "$dotnet_root/host/fxr/10.0.12" \
    "$dotnet_root/shared" \
    "$dotnet_root/shared/Microsoft.NETCore.App" \
    "$dotnet_root/shared/Microsoft.NETCore.App/10.0.12" \
    "$dotnet_root/shared/Microsoft.AspNetCore.App" \
    "$dotnet_root/shared/Microsoft.AspNetCore.App/10.0.12"; do
    chmod 0755 "$directory"
  done

  DOTNETVERSION=10.0.403 bash "$install_script" "$rid" "$artifact_root" "$dotnet_root" >/dev/null

  if ! cmp -s "$work/$rid-sdk-dotnet" "$dotnet_root/dotnet"; then
    echo "Runtime-pack installation replaced the SDK host executable: $dotnet_root/dotnet." >&2
    exit 1
  fi

  for directory in \
    "$dotnet_root/host/fxr/10.0.12" \
    "$dotnet_root/shared/Microsoft.NETCore.App/10.0.12" \
    "$dotnet_root/shared/Microsoft.AspNetCore.App/10.0.12"; do
    mode="$(stat -c '%a' "$directory")"
    if [[ "$mode" != 755 ]]; then
      echo "Runtime-pack installation changed existing directory permissions: $directory is $mode, expected 755." >&2
      exit 1
    fi
  done

  assert_marker() {
    local expected="$1"
    local path="$2"
    if ! grep -Fxq "$expected" "$path"; then
      echo "Runtime-pack installation changed protected content or failed to add the reference pack: $path." >&2
      exit 1
    fi
  }
  assert_marker "existing hostfxr" "$dotnet_root/host/fxr/10.0.12/hostfxr.marker"
  assert_marker "existing framework" "$dotnet_root/shared/Microsoft.NETCore.App/10.0.12/framework.marker"
  assert_marker "existing aspnet" "$dotnet_root/shared/Microsoft.AspNetCore.App/10.0.12/aspnet.marker"
  assert_marker "existing SDK" "$dotnet_root/sdk/10.0.403/sdk.marker"
  assert_marker "pack $rid" "$dotnet_root/packs/Microsoft.NETCore.App.Ref/pack.marker"
  pack_mtime="$(stat -c '%Y' "$dotnet_root/packs/Microsoft.NETCore.App.Ref/pack.marker")"
  if (( pack_mtime <= 1577836800 )); then
    echo "Runtime-pack installation restored stale package timestamps: $dotnet_root/packs/Microsoft.NETCore.App.Ref/pack.marker." >&2
    exit 1
  fi
done

# A wrong ambient DOTNET_ROOT must fail before it can install an SDK into the host.
missing_root="$work/missing-sdk"
mkdir -p "$missing_root"
cp "$work/linux-x86-sdk-dotnet" "$missing_root/dotnet"
if DOTNETVERSION=10.0.403 bash "$install_script" linux-x86 "$work/linux-x86-artifacts" "$missing_root" >"$work/missing-sdk.log" 2>&1; then
  echo 'Runtime-pack installation accepted a root without the configured SDK.' >&2
  exit 1
fi
if [[ -d "$missing_root/sdk" || -d "$missing_root/packs" || -d "$missing_root/shared" ]]; then
  echo 'Rejected SDK root was modified before validation.' >&2
  exit 1
fi

echo 'Extra-platform reference packs preserve host runtimes, hostfxr, SDK contents, permissions and current cache timestamps; invalid SDK roots fail before mutation.'
