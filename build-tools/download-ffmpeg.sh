#!/bin/bash

# Download pinned FFmpeg binaries for explicit native packaging or manual staging.
# Normal source builds and the compile/test CI workflow do not run this script.

# Note: We don't use 'set -e' here because we want to continue even if some downloads fail

TOOLS_DIR="$(cd "$(dirname "$0")/.." && pwd)/Tools"
mkdir -p "$TOOLS_DIR"

STRICT_MODE=0
if [ "${CHAPTARR_FFMPEG_STRICT:-}" = "1" ] || [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    STRICT_MODE=1
fi

# Optional filter: an exact RID (linux-x64) or platform (linux). With no
# filter, download every automatically supported platform. macOS is reported as
# unsupported only when it is explicitly requested.
TARGET_PLATFORM="${1:-}"

echo "Downloading FFmpeg binaries..."

compute_sha256() {
    local file="$1"

    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | awk '{print $1}'
        return 0
    fi

    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | awk '{print $1}'
        return 0
    fi

    echo "Error: Missing sha256sum/shasum for SHA256 verification"
    return 1
}

verify_sha256() {
    local file="$1"
    local expected="$2"

    if ! echo "$expected" | grep -Eq '^[0-9a-fA-F]{64}$'; then
        echo "Error: Invalid committed SHA256 value: '$expected'"
        return 1
    fi

    local actual
    if ! actual="$(compute_sha256 "$file")"; then
        return 1
    fi

    if [ "$(echo "$actual" | tr '[:upper:]' '[:lower:]')" != "$(echo "$expected" | tr '[:upper:]' '[:lower:]')" ]; then
        echo "Error: SHA256 mismatch for $file"
        echo "Expected: $expected"
        echo "Actual:   $actual"
        return 1
    fi

    return 0
}

verify_native_executables() {
    local platform="$1"
    local ffmpeg_path="$2"
    local ffprobe_path="$3"
    local host
    host="$(uname -s):$(uname -m)"

    case "$platform:$host" in
        linux-x64:Linux:x86_64|linux-x64:Linux:amd64|linux-arm64:Linux:aarch64|linux-arm64:Linux:arm64|win-x64:MINGW*:x86_64|win-x64:MSYS*:x86_64|win-x64:CYGWIN*:x86_64)
            ;;
        *)
            echo "Skipping -version execution for cross-target $platform on $host; archive SHA256 and contents were verified."
            return 0
            ;;
    esac

    local output
    if ! output="$("$ffmpeg_path" -version 2>&1)" || ! echo "$output" | grep -qi '^ffmpeg version'; then
        echo "Error: extracted ffmpeg failed its -version check for $platform"
        return 1
    fi

    if ! output="$("$ffprobe_path" -version 2>&1)" || ! echo "$output" | grep -qi '^ffprobe version'; then
        echo "Error: extracted ffprobe failed its -version check for $platform"
        return 1
    fi

    return 0
}

download_verified_archive() {
    local archive_path="$1"
    local archive_url="$2"
    local expected_sha256="$3"

    local attempt
    for attempt in 1 2 3; do
        rm -f "$archive_path"

        if curl -fL --retry 3 --retry-delay 5 "$archive_url" -o "$archive_path" &&
            verify_sha256 "$archive_path" "$expected_sha256"; then
            return 0
        fi

        if [ "$attempt" -lt 3 ]; then
            echo "Warning: archive/SHA256 validation attempt $attempt failed for $archive_url; retrying in 5 seconds."
            sleep 5
        fi
    done

    rm -f "$archive_path"
    return 1
}

# Check only the tools needed by the requested archive type. Unsupported
# targets should report their own error instead of failing on an unrelated tool.
check_dependencies() {
    local missing=""
    local needs_download_tools=0
    local needs_unzip=0
    local needs_tar=0

    case "$TARGET_PLATFORM" in
        "")
            needs_download_tools=1
            needs_unzip=1
            needs_tar=1
            ;;
        win|win-x64)
            needs_download_tools=1
            needs_unzip=1
            ;;
        linux|linux-x64|linux-arm64)
            needs_download_tools=1
            needs_tar=1
            ;;
    esac

    if [ "$needs_download_tools" -eq 1 ]; then
        if ! command -v curl >/dev/null 2>&1; then
            missing="$missing curl"
        fi

        if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
            missing="$missing sha256sum/shasum"
        fi
    fi

    if [ "$needs_unzip" -eq 1 ] && ! command -v unzip >/dev/null 2>&1; then
        missing="$missing unzip"
    fi

    if [ "$needs_tar" -eq 1 ] && ! command -v tar >/dev/null 2>&1; then
        missing="$missing tar"
    fi

    if [ -n "$missing" ]; then
        echo "Error: Missing required tools:$missing"
        echo "Please install these tools and try again."
        exit 1
    fi
}

# Function to download and extract FFmpeg
download_ffmpeg() {
    local platform="$1"
    local url="$2"
    local expected_sha256="$3"
    
    echo "Downloading FFmpeg for $platform..."
    mkdir -p "$TOOLS_DIR/ffmpeg/$platform"
    mkdir -p "$TOOLS_DIR/ffprobe/$platform"
    
    local temp_dir=$(mktemp -d)
    cd "$temp_dir"
    
    if [[ "$platform" == "win-"* ]]; then
        # Windows downloads
        if ! download_verified_archive "ffmpeg.zip" "$url" "$expected_sha256"; then
            echo "Error: Failed to download and verify FFmpeg for $platform"
            rm -rf "$temp_dir"
            return 1
        fi
        
        if ! unzip -j ffmpeg.zip "*/bin/ffmpeg.exe" "*/bin/ffprobe.exe" 2>/dev/null; then
            echo "Warning: Could not extract from standard paths for $platform, trying alternative extraction"
            if ! unzip -j ffmpeg.zip "ffmpeg.exe" "ffprobe.exe" 2>/dev/null; then
                echo "Error: Failed to extract FFmpeg binaries for $platform"
                rm -rf "$temp_dir"
                return 1
            fi
        fi
        
        if ! mv ffmpeg.exe "$TOOLS_DIR/ffmpeg/$platform/ffmpeg.exe" ||
            ! mv ffprobe.exe "$TOOLS_DIR/ffprobe/$platform/ffprobe.exe"; then
            echo "Error: Failed to install extracted binaries for $platform"
            rm -rf "$temp_dir"
            return 1
        fi
    else
        # Unix downloads
        if ! download_verified_archive "ffmpeg.tar.xz" "$url" "$expected_sha256"; then
            echo "Error: Failed to download and verify FFmpeg for $platform"
            rm -rf "$temp_dir"
            return 1
        fi
        
        tar xf ffmpeg.tar.xz

        # Find the binaries wherever they landed
        local found_ffmpeg=$(find . -name "ffmpeg" -type f ! -name "*.txt" | head -1)
        local found_ffprobe=$(find . -name "ffprobe" -type f ! -name "*.txt" | head -1)

        if [ -z "$found_ffmpeg" ] || [ -z "$found_ffprobe" ]; then
            echo "Error: Could not find ffmpeg/ffprobe binaries in archive for $platform"
            echo "Archive contents:"
            find . -type f | head -20
            rm -rf "$temp_dir"
            return 1
        fi

        chmod +x "$found_ffmpeg" "$found_ffprobe"
        if ! mv "$found_ffmpeg" "$TOOLS_DIR/ffmpeg/$platform/ffmpeg" ||
            ! mv "$found_ffprobe" "$TOOLS_DIR/ffprobe/$platform/ffprobe"; then
            echo "Error: Failed to install extracted binaries for $platform"
            rm -rf "$temp_dir"
            return 1
        fi
    fi

    # Validate that expected binaries exist at their final locations (this is what Chaptarr looks for at runtime).
    local ffmpeg_target="$TOOLS_DIR/ffmpeg/$platform/ffmpeg"
    local ffprobe_target="$TOOLS_DIR/ffprobe/$platform/ffprobe"
    if [[ "$platform" == "win-"* ]]; then
        ffmpeg_target="${ffmpeg_target}.exe"
        ffprobe_target="${ffprobe_target}.exe"
    fi

    if [ ! -f "$ffmpeg_target" ]; then
        echo "Error: Missing extracted ffmpeg binary for $platform at $ffmpeg_target"
        rm -rf "$temp_dir"
        return 1
    fi

    if [ ! -f "$ffprobe_target" ]; then
        echo "Error: Missing extracted ffprobe binary for $platform at $ffprobe_target"
        rm -rf "$temp_dir"
        return 1
    fi

    if ! verify_native_executables "$platform" "$ffmpeg_target" "$ffprobe_target"; then
        rm -rf "$temp_dir"
        return 1
    fi

    rm -rf "$temp_dir"
    echo "Completed download for $platform"
}

# Check dependencies first
check_dependencies

should_download() {
    local rid="$1"
    [ -z "$TARGET_PLATFORM" ] && return 0
    [ "$rid" = "$TARGET_PLATFORM" ] && return 0
    [[ "$TARGET_PLATFORM" != *-* && "$rid" == "${TARGET_PLATFORM}-"* ]] && return 0
    return 1
}

# Download requested platforms, but report every requested failure explicitly.
failed_downloads=""
attempted_downloads=0

# Windows x64
if should_download "win-x64"; then
    attempted_downloads=1
    echo "=== Downloading Windows x64 ==="
    if ! download_ffmpeg "win-x64" "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip" "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec"; then
        failed_downloads="$failed_downloads win-x64"
    fi
fi

# Linux x64
if should_download "linux-x64"; then
    attempted_downloads=1
    echo "=== Downloading Linux x64 ==="
    if ! download_ffmpeg "linux-x64" "https://johnvansickle.com/ffmpeg/releases/ffmpeg-7.0.2-amd64-static.tar.xz" "abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67"; then
        failed_downloads="$failed_downloads linux-x64"
    fi
fi

# Linux ARM64
if should_download "linux-arm64"; then
    attempted_downloads=1
    echo "=== Downloading Linux ARM64 ==="
    if ! download_ffmpeg "linux-arm64" "https://johnvansickle.com/ffmpeg/releases/ffmpeg-7.0.2-arm64-static.tar.xz" "f4149bb2b0784e30e99bdda85471c9b5930d3402014e934a5098b41d0f7201b1"; then
        failed_downloads="$failed_downloads linux-arm64"
    fi
fi

# macOS has no automatic source in this script. Do not create empty tool
# directories or report success: native packaging must be given real binaries.
case "$TARGET_PLATFORM" in
    osx|osx-x64|osx-arm64)
        for mac_rid in osx-x64 osx-arm64; do
            if should_download "$mac_rid"; then
                attempted_downloads=1
                echo "=== $mac_rid ==="
                echo "Error: automatic FFmpeg/FFprobe acquisition is not supported for $mac_rid."
                echo "Install both tools on PATH for a source build, or place verified binaries under Tools/ before native packaging."
                failed_downloads="$failed_downloads $mac_rid"
            fi
        done
        ;;
esac

if [ -n "$TARGET_PLATFORM" ] && [ "$attempted_downloads" -eq 0 ]; then
    echo "Error: no automatic FFmpeg source is configured for '$TARGET_PLATFORM'."
    failed_downloads="$failed_downloads $TARGET_PLATFORM"
fi

echo ""
echo "=== FFmpeg Download Summary ==="
if [ -z "$failed_downloads" ]; then
    echo "All FFmpeg downloads completed successfully!"
else
    echo "Some downloads failed: $failed_downloads"
    echo "The build may still work if FFmpeg is available system-wide or if you're not targeting those platforms."

    if [ "$STRICT_MODE" -eq 1 ]; then
        echo "Strict mode enabled: failing due to FFmpeg download errors."
        exit 1
    fi
fi

echo ""
echo "Created Tools directory structure:"
find "$TOOLS_DIR" -type f 2>/dev/null | head -20 || echo "No files found in Tools directory"
echo ""
echo "Remember to add Tools/** to your .gitignore if you don't want to commit binaries"
