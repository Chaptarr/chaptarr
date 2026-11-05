#!/usr/bin/env bash
set -euo pipefail

readonly runtime_tag="v10.0.12"
readonly runtime_sdk="10.0.110"
readonly aspnet_sdk="10.0.111"
readonly app_sdk="${DOTNETVERSION:-10.0.403}"
readonly cross_image="mcr.microsoft.com/dotnet-buildtools/prereqs:azurelinux-3.0-net10.0-cross-x86@sha256:ecc9c107cd33f8303cba2280eb1537f045acaa88fc0f00780ea6232d19e942b8"
readonly freebsd_archive_url="https://github.com/Thefrank/dotnet-freebsd-crossbuild/releases/download/v10.0.112-amd64-freebsd-14/Private.SourceBuilt.Artifacts.10.0.112-servicing.26422.108.freebsd-x64.tar.gz"
readonly freebsd_archive_sha256="7e032c2024cfb17ac3bb27ab8d35234a6e74f5ede16e66ca8420bcf66028d978"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work_root="${PLATFORM_RUNTIME_WORK_DIR:?Set PLATFORM_RUNTIME_WORK_DIR to a task-scoped scratch directory}"
output_root="${PLATFORM_RUNTIME_PACKS_OUTPUT:?Set PLATFORM_RUNTIME_PACKS_OUTPUT to a task-scoped output directory}"
dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
runtime_repo="$work_root/runtime"
aspnet_repo="$work_root/aspnetcore"
runtime_shipping="$runtime_repo/artifacts/packages/Release/Shipping"

die() {
    echo "$*" >&2
    exit 1
}

get_official_build_id() {
    local build_id="${DOTNET_OFFICIAL_BUILD_ID:-}"
    if [[ "$build_id" =~ ^20[0-9]{6}\.[0-9]+$ ]]; then
        printf '%s\n' "$build_id"
    else
        printf '%s.1\n' "$(date -u +%Y%m%d)"
    fi
}

require_sdk() {
    local version="$1"
    [ -x "$dotnet_root/dotnet" ] || die "dotnet was not found at $dotnet_root/dotnet"
    [ -f "$dotnet_root/sdk/$version/Microsoft.NETCoreSdk.BundledVersions.props" ] || die ".NET SDK $version must be installed under $dotnet_root"
}

patch_sdk_runtime_ids() {
    local sdk_version="$1"
    local sdk_root="${2:-$dotnet_root}"
    local props="$sdk_root/sdk/$sdk_version/Microsoft.NETCoreSdk.BundledVersions.props"
    python3 - "$props" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
content = path.read_text(encoding="utf-8-sig")

def patch(match):
    block = match.group(0)
    if 'TargetFramework="net10.0"' not in block:
        return block
    include = re.search(r'Include="([^"]+)"', block)
    if not include:
        return block
    package = include.group(1)
    attributes = []
    if package in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
        attributes.append("RuntimePackRuntimeIdentifiers")
    if package == "Microsoft.NETCore.App":
        attributes.append("AppHostRuntimeIdentifiers")
    for attribute in attributes:
        attr = re.search(rf'{attribute}="([^"]*)"', block)
        if attr:
            values = attr.group(1).split(";")
            for rid in ("linux-x86", "freebsd-x64"):
                if rid not in values:
                    values.append(rid)
                    block = block[:attr.start(1)] + ";".join(values) + block[attr.end(1):]
                    attr = re.search(rf'{attribute}="([^"]*)"', block)
    return block

pattern = re.compile(r'<(?:KnownFrameworkReference|KnownAppHostPack)\s+Include="[^"]+"[\s\S]*?/>')
updated = pattern.sub(patch, content)
for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
    match = re.search(rf'<KnownFrameworkReference\s+Include="{re.escape(framework)}"[\s\S]*?TargetFramework="net10\.0"[\s\S]*?RuntimePackRuntimeIdentifiers="([^"]*)', updated)
    if not match or not {"linux-x86", "freebsd-x64"}.issubset(set(match.group(1).split(";"))):
        raise SystemExit(f"SDK metadata is missing custom .NET 10 runtime identifiers for {framework}")
path.write_text(updated, encoding="utf-8")
PY
}

clone_sources() {
    mkdir -p "$work_root"
    git clone --depth 1 --branch "$runtime_tag" https://github.com/dotnet/runtime.git "$runtime_repo"
    git clone --depth 1 --branch "$runtime_tag" --recurse-submodules https://github.com/dotnet/aspnetcore.git "$aspnet_repo"
    git -C "$aspnet_repo" submodule update --init --depth 1
}

patch_runtime_sources() {
    python3 - "$runtime_repo" <<'PY'
from pathlib import Path
import re
import sys

root = Path(sys.argv[1])

def edit(relative, transform):
    path = root / relative
    original = path.read_text()
    updated = transform(original)
    if updated == original:
        return
    path.write_text(updated)

def add_rid_attributes(content):
    def update(match):
        tag = match.group(0)
        for name in ("RuntimePackRuntimeIdentifiers", "AppHostRuntimeIdentifiers"):
            attribute = re.search(rf'{name}="([^"]*)"', tag)
            if attribute and "linux-x86" not in attribute.group(1).split(";"):
                values = attribute.group(1).split(";")
                values.insert(values.index("linux-x64") if "linux-x64" in values else len(values), "linux-x86")
                tag = tag[:attribute.start(1)] + ";".join(values) + tag[attribute.end(1):]
        return tag
    return re.sub(r'<(?:KnownFrameworkReference|KnownAppHostPack)\s+Include="Microsoft\.NETCore\.App"[\s\S]*?/>', update, content)

edit("eng/targetingpacks.targets", add_rid_attributes)
# Unix x86 uses ECX/EDX for managed this/return-buffer arguments, matching
# i386/asmhelpers.asm. The upstream .S worker swaps callee-saved EDI/ESI,
# corrupting closed static delegates that return structs (including nullable
# doubles in compiled property accessors). Keep this patch scoped to the
# version-pinned runtime source and qualify the resulting runtime by execution.
worker = root / "src/coreclr/vm/i386/asmhelpers.S"
old_worker = """//edi -This pointer
//esi -ReturnBuffer
LEAF_ENTRY ThisPtrRetBufPrecodeWorker, _TEXT
    mov  eax, [eax + ThisPtrRetBufPrecodeData__Target]
    // Use XOR swap technique to set avoid the need to spill to the stack
    xor edi, esi
    xor esi, edi
    xor edi, esi
    jmp eax
LEAF_END ThisPtrRetBufPrecodeWorker, _TEXT"""
new_worker = old_worker.replace("//edi -This pointer", "//ecx -This pointer").replace(
    "//esi -ReturnBuffer", "//edx -ReturnBuffer").replace(
    "xor edi, esi", "xor ecx, edx").replace("xor esi, edi", "xor edx, ecx")
worker_source = worker.read_text()
if worker_source.count(old_worker) != 1:
    raise SystemExit("Unrecognized Unix x86 delegate return-buffer worker; review the runtime patch")
worker.write_text(worker_source.replace(old_worker, new_worker, 1))
edit("eng/Subsets.props", lambda s: re.sub(r'^.*crossgen-corelib\.proj.*\n', "", s, flags=re.MULTILINE))
edit("eng/Subsets.props", lambda s: s.replace(
    "Condition=\"'$(RuntimeFlavor)' != 'Mono' and ('$(TargetsMobile)' != 'true' and '$(TargetsLinuxBionic)' != 'true')\" Include=\"$(InstallerProjectRoot)pkg\\sfx\\Microsoft.NETCore.App\\Microsoft.NETCore.App.Crossgen2.sfxproj\"",
    "Condition=\"'$(RuntimeFlavor)' != 'Mono' and '$(TargetArchitecture)' != 'x86' and ('$(TargetsMobile)' != 'true' and '$(TargetsLinuxBionic)' != 'true')\" Include=\"$(InstallerProjectRoot)pkg\\sfx\\Microsoft.NETCore.App\\Microsoft.NETCore.App.Crossgen2.sfxproj\""))
for project in ("src/coreclr/tools/aot/crossgen2/crossgen2_publish.csproj", "src/coreclr/tools/aot/ILCompiler/ILCompiler_publish.csproj"):
    edit(project, lambda s: s.replace(
        '<RuntimeIdentifier>$(PortableOS)-$(TargetArchitecture)</RuntimeIdentifier>',
        '<RuntimeIdentifier Condition="\'$(CrossBuild)\' == \'true\'">$(NETCoreSdkRuntimeIdentifier)</RuntimeIdentifier>\n    <RuntimeIdentifier Condition="\'$(CrossBuild)\' != \'true\'">$(PortableOS)-$(TargetArchitecture)</RuntimeIdentifier>'))
edit("src/installer/pkg/sfx/Microsoft.NETCore.App/Microsoft.NETCore.App.Runtime.props", lambda s: s.replace(
    "<IncludeFallbacksInDepsFile>true</IncludeFallbacksInDepsFile>",
    "<IncludeFallbacksInDepsFile>true</IncludeFallbacksInDepsFile>\n    <PublishReadyToRun>false</PublishReadyToRun>", 1))
edit("src/installer/pkg/sfx/Microsoft.NETCore.App/Microsoft.NETCore.App.Runtime.CoreCLR.sfxproj", lambda s: s.replace(
    "</Project>",
    "  <PropertyGroup Condition=\"'$(TargetArchitecture)' == 'x86'\">\n    <NoWarn>$(NoWarn);NU5118</NoWarn>\n  </PropertyGroup>\n</Project>", 1))

subsets = (root / "eng/Subsets.props").read_text()
if "TargetArchitecture)' != 'x86'" not in subsets or "crossgen-corelib.proj" in subsets:
    raise SystemExit("Failed to apply the Linux x86 runtime subset adjustments")
PY
    sed -i '/dnceng\/internal/d' "$runtime_repo/NuGet.config"
}

build_linux_x86_runtime() {
    local official_build_id
    official_build_id="$(get_official_build_id)"
    docker run --rm --init \
        -e DOTNET_INSTALL_DIR=/usr/share/dotnet \
        -e DOTNET_ROOT=/usr/share/dotnet \
        -e DOTNET_SDK_VERSION="$runtime_sdk" \
        -e ROOTFS_DIR=/crossrootfs/x86 \
        -v "$dotnet_root:/usr/share/dotnet" \
        -v "$runtime_repo:/runtime" \
        -w /runtime \
        "$cross_image" \
        bash -lc "export PATH=/usr/share/dotnet:\$PATH; ./eng/build.sh -ci -c Release -cross -os Linux -arch x86 /m:1 /p:OfficialBuildId=$official_build_id /p:SdkVersion=$runtime_sdk /p:AppHostSourcePath=/runtime/artifacts/obj/linux-x86.Release/apphost/standalone/apphost /p:SelfContained=false /p:UseAppHost=false /p:PublishTrimmed=false /p:UseNativeAotForComponents=false /p:PublishAot=false /p:PublishReadyToRun=false /p:PublishSingleFile=false /p:StabilizePackageVersion=true -subset clr.native+clr.corelib+libs.native+libs.sfx+libs.oob+host.native+host.pkg+packs.product -pack"
}

patch_aspnet_sources() {
    python3 - "$aspnet_repo" <<'PY'
from pathlib import Path
import re
import sys

root = Path(sys.argv[1])
props = root / "Directory.Build.props"
text = props.read_text()
text = re.sub(r'(<SupportedRuntimeIdentifiers>)([^<]*)(</SupportedRuntimeIdentifiers>)',
              lambda m: m.group(1) + m.group(2) + ("" if "linux-x86" in m.group(2).split(";") else ";linux-x86") + m.group(3), text, count=1)
props.write_text(text)

dependencies = root / "eng/Dependencies.props"
text = dependencies.read_text()
anchor = '<_LatestRuntimePackageReference Include="Microsoft.NETCore.App.Runtime.linux-x64" />'
if anchor not in text:
    raise SystemExit("Could not find the linux-x64 runtime package in ASP.NET Core dependency metadata")
if 'Microsoft.NETCore.App.Runtime.linux-x86' not in text:
    text = text.replace(anchor, anchor + '\n    <_LatestRuntimePackageReference Include="Microsoft.NETCore.App.Runtime.linux-x86" />', 1)
dependencies.write_text(text)
PY
    sed -i '/dnceng\/internal/d' "$aspnet_repo/NuGet.config"
    dotnet nuget add source "$runtime_shipping" --name chaptarr-linux-x86-runtime --configfile "$aspnet_repo/NuGet.config"
}

build_linux_x86_aspnetcore() {
    local official_build_id
    official_build_id="$(get_official_build_id)"
    (
        cd "$aspnet_repo"
        DOTNET_INSTALL_DIR="$dotnet_root" DOTNET_ROOT="$dotnet_root" DOTNET_SDK_VERSION="$aspnet_sdk" \
            DOTNET_MULTILEVEL_LOOKUP=0 PATH="$dotnet_root:$PATH" ./eng/build.sh --only-build-repo-tasks \
            "/p:SdkVersion=$aspnet_sdk"
    )
    patch_sdk_runtime_ids "$aspnet_sdk" "$aspnet_repo/.dotnet"
    (
        cd "$aspnet_repo"
        DOTNET_INSTALL_DIR="$dotnet_root" DOTNET_ROOT="$dotnet_root" DOTNET_SDK_VERSION="$aspnet_sdk" \
            DOTNET_MULTILEVEL_LOOKUP=0 PATH="$dotnet_root:$PATH" ./eng/build.sh -c Release -ci -arch x86 -pack \
            --projects "$aspnet_repo/src/Framework/App.Runtime/src/Microsoft.AspNetCore.App.Runtime.sfxproj" /m:1 \
            "/p:OfficialBuildId=$official_build_id" \
            "/p:SdkVersion=$aspnet_sdk" \
            /p:StabilizePackageVersion=true \
            /p:CrossgenOutput=false \
            /p:PublishReadyToRun=false
    )
}

verify_sha256() {
    local expected="$1"
    local file="$2"
    if command -v sha256sum >/dev/null 2>&1; then
        printf '%s  %s\n' "$expected" "$file" | sha256sum --check --status || die "SHA-256 verification failed for $file"
    elif command -v shasum >/dev/null 2>&1; then
        printf '%s  %s\n' "$expected" "$file" | shasum -a 256 --check --status || die "SHA-256 verification failed for $file"
    else
        die "No SHA-256 checksum utility is installed"
    fi
}

collect_platform_artifacts() {
    local freebsd_archive="$work_root/freebsd-source-built.tar.gz"
    local runtime_archive
    local freebsd_runtime_path
    local freebsd_runtime_archive

    mkdir -p "$output_root/packages" "$output_root/runtimes" "$output_root/sdk/sdk" "$output_root/sdk/packs"

    for package in \
        Microsoft.NETCore.App.Host.linux-x86.10.0.12.nupkg \
        Microsoft.NETCore.App.Runtime.linux-x86.10.0.12.nupkg; do
        [ -s "$runtime_shipping/$package" ] || die "The .NET runtime build did not produce $package"
        cp "$runtime_shipping/$package" "$output_root/packages/"
    done

    local aspnet_package
    aspnet_package="$(find "$aspnet_repo/artifacts/packages/Release/Shipping" -maxdepth 1 -type f -name 'Microsoft.AspNetCore.App.Runtime.linux-x86.10.0.12.nupkg' -print -quit)"
    [ -n "$aspnet_package" ] || die "ASP.NET Core did not produce the Linux x86 runtime pack"
    cp "$aspnet_package" "$output_root/packages/"

    runtime_archive="$(find "$runtime_shipping" -maxdepth 1 -type f -name 'dotnet-runtime-10.0.12-linux-x86.tar.gz' -print -quit)"
    [ -n "$runtime_archive" ] || die "The .NET runtime build did not produce the Linux x86 runtime archive"
    cp "$runtime_archive" "$output_root/runtimes/linux-x86-runtime.tar.gz"

    curl --fail --location --retry 3 --silent --show-error --output "$freebsd_archive" "$freebsd_archive_url"
    verify_sha256 "$freebsd_archive_sha256" "$freebsd_archive"
    tar -xzf "$freebsd_archive" -C "$output_root/packages" \
        Microsoft.NETCore.App.Host.freebsd-x64.10.0.12.nupkg \
        Microsoft.NETCore.App.Runtime.freebsd-x64.10.0.12.nupkg \
        Microsoft.AspNetCore.App.Runtime.freebsd-x64.10.0.12.nupkg
    freebsd_runtime_path="$(
      tar -tzf "$freebsd_archive" |
        awk '
          /^assets\/Release\/Runtime\/[^/]+\/dotnet-runtime-10\.0\.12-freebsd-x64\.tar\.gz$/ {
            if (!found) first = $0
            found = 1
          }
          END { if (found) print first }
        '
    )"
    [ -n "$freebsd_runtime_path" ] || die "Pinned FreeBSD archive is missing its .NET runtime tarball"
    tar -xzf "$freebsd_archive" -C "$work_root" "$freebsd_runtime_path"
    freebsd_runtime_archive="$work_root/$freebsd_runtime_path"
    cp "$freebsd_runtime_archive" "$output_root/runtimes/freebsd-x64-runtime.tar.gz"

    for rid in linux-x86 freebsd-x64; do
        local source_package="$output_root/packages/Microsoft.AspNetCore.App.Runtime.$rid.10.0.12.nupkg"
        local shared_folder="$work_root/aspnet-shared/$rid"
        mkdir -p "$shared_folder"
        unzip -q -j "$source_package" "runtimes/$rid/lib/net10.0/*" -d "$shared_folder"
        tar -czf "$output_root/runtimes/$rid-aspnetcore-shared.tar.gz" -C "$shared_folder" .
    done

    cp -a "$dotnet_root/sdk/$app_sdk" "$output_root/sdk/sdk/"
    bash "$repo_root/packaging/scripts/copy-dotnet-sdk-reference-packs.sh" \
        "$dotnet_root" "$output_root/sdk/packs"
    if [ -d "$dotnet_root/sdk-manifests/10.0.400" ]; then
        mkdir -p "$output_root/sdk/sdk-manifests"
        cp -a "$dotnet_root/sdk-manifests/10.0.400" "$output_root/sdk/sdk-manifests/"
    fi

    python3 -c 'from pathlib import Path; import sys; Path(sys.argv[1]).unlink(missing_ok=True)' "$freebsd_archive"
    printf 'Platform runtime packs built from .NET %s and ASP.NET Core %s.\n' "$runtime_tag" "$runtime_tag" > "$output_root/BUILD-INFO.txt"
    cp "$repo_root/packaging/scripts/install-extra-platform-runtime-packs.sh" "$output_root/"
}

require_sdk "$runtime_sdk"
require_sdk "$aspnet_sdk"
require_sdk "$app_sdk"
[[ "$work_root" != "$output_root" ]] || die "Runtime-pack work and output paths must be different"
[[ "$work_root" = /* && "$output_root" = /* ]] || die "Runtime-pack work and output paths must be absolute"
[[ ! -e "$work_root" ]] || die "Task-scoped runtime-pack work path already exists: $work_root"
[[ ! -e "$output_root" ]] || die "Task-scoped runtime-pack output path already exists: $output_root"
command -v docker >/dev/null || die "Docker is required to build the Linux x86 runtime in the pinned cross-build image"
command -v python3 >/dev/null || die "Python 3 is required to patch the pinned .NET build metadata"
command -v unzip >/dev/null || die "unzip is required to assemble the ASP.NET shared runtime"

clone_sources
patch_sdk_runtime_ids "$runtime_sdk"
patch_sdk_runtime_ids "$app_sdk"
patch_runtime_sources
build_linux_x86_runtime
patch_aspnet_sources
build_linux_x86_aspnetcore
collect_platform_artifacts
