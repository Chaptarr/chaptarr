#!/bin/bash

# Download FFmpeg binaries for all platforms
# This script should be run during CI/CD build process

# Note: We don't use 'set -e' here because we want to continue even if some downloads fail

TOOLS_DIR="$(cd "$(dirname "$0")/.." && pwd)/Tools"
mkdir -p "$TOOLS_DIR"

STRICT_MODE=0
if [ "${CHAPTARR_FFMPEG_STRICT:-}" = "1" ] || [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    STRICT_MODE=1
fi

echo "Downloading FFmpeg binaries..."

to_lower() {
    tr '[:upper:]' '[:lower:]'
}

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

compute_md5() {
    local file="$1"

    if command -v md5sum >/dev/null 2>&1; then
        md5sum "$file" | awk '{print $1}'
        return 0
    fi

    if command -v md5 >/dev/null 2>&1; then
        md5 -q "$file"
        return 0
    fi

    echo "Error: Missing md5sum/md5 for MD5 verification"
    return 1
}

verify_sha256_from_url() {
    local file="$1"
    local sha_url="$2"

    local expected
    if ! expected="$(curl -fsSL --retry 3 --retry-delay 5 "$sha_url" | tr -d '\r\n ')" ; then
        echo "Error: Failed to download SHA256 checksum from $sha_url"
        return 1
    fi

    if ! echo "$expected" | grep -Eq '^[0-9a-fA-F]{64}$'; then
        echo "Error: Invalid SHA256 checksum format from $sha_url: '$expected'"
        return 1
    fi

    local actual
    if ! actual="$(compute_sha256 "$file")"; then
        return 1
    fi

    if [ "$(echo "$actual" | to_lower)" != "$(echo "$expected" | to_lower)" ]; then
        echo "Error: SHA256 mismatch for $file"
        echo "Expected: $expected"
        echo "Actual:   $actual"
        return 1
    fi

    return 0
}

verify_md5_from_url() {
    local file="$1"
    local md5_url="$2"

    local content
    if ! content="$(curl -fsSL --retry 3 --retry-delay 5 "$md5_url")" ; then
        echo "Error: Failed to download MD5 checksum from $md5_url"
        return 1
    fi

    local expected
    expected="$(echo "$content" | awk '{print $1}' | tr -d '\r\n ')"

    if ! echo "$expected" | grep -Eq '^[0-9a-fA-F]{32}$'; then
        echo "Error: Invalid MD5 checksum format from $md5_url: '$expected'"
        return 1
    fi

    local actual
    if ! actual="$(compute_md5 "$file")"; then
        return 1
    fi

    if [ "$(echo "$actual" | to_lower)" != "$(echo "$expected" | to_lower)" ]; then
        echo "Error: MD5 mismatch for $file"
        echo "Expected: $expected"
        echo "Actual:   $actual"
        return 1
    fi

    return 0
}

# Check for required tools
check_dependencies() {
    local missing=""
    
    if ! command -v curl >/dev/null 2>&1; then
        missing="$missing curl"
    fi
    
    if ! command -v unzip >/dev/null 2>&1; then
        missing="$missing unzip"
    fi
    
    if ! command -v tar >/dev/null 2>&1; then
        missing="$missing tar"
    fi

    if ! command -v sha256sum >/dev/null 2>&1 && ! command -v shasum >/dev/null 2>&1; then
        missing="$missing sha256sum/shasum"
    fi

    if ! command -v md5sum >/dev/null 2>&1 && ! command -v md5 >/dev/null 2>&1; then
        missing="$missing md5sum/md5"
    fi
    
    if [ -n "$missing" ]; then
        echo "Error: Missing required tools:$missing"
        echo "Please install these tools and try again."
        exit 1
    fi
}

# Function to download and extract FFmpeg
download_ffmpeg() {
    local platform=$1
    local url=$2
    
    echo "Downloading FFmpeg for $platform..."
    mkdir -p "$TOOLS_DIR/ffmpeg/$platform"
    mkdir -p "$TOOLS_DIR/ffprobe/$platform"
    
    local temp_dir=$(mktemp -d)
    cd "$temp_dir"
    
    if [[ "$platform" == "win-"* ]]; then
        # Windows downloads
        if ! curl -fL --retry 3 --retry-delay 5 "$url" -o ffmpeg.zip; then
            echo "Error: Failed to download FFmpeg for $platform"
            rm -rf "$temp_dir"
            return 1
        fi

        if ! verify_sha256_from_url "ffmpeg.zip" "${url}.sha256"; then
            echo "Error: FFmpeg checksum verification failed for $platform"
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
        
        # Move to final location
        mv ffmpeg.exe "$TOOLS_DIR/ffmpeg/$platform/" 2>/dev/null || echo "Warning: ffmpeg.exe not found for $platform"
        mv ffprobe.exe "$TOOLS_DIR/ffmpeg/$platform/" 2>/dev/null || echo "Warning: ffprobe.exe not found for $platform"
        
        # Copy to ffprobe directory
        if [ -f "$TOOLS_DIR/ffmpeg/$platform/ffprobe.exe" ]; then
            cp "$TOOLS_DIR/ffmpeg/$platform/ffprobe.exe" "$TOOLS_DIR/ffprobe/$platform/"
        fi
    else
        # Unix downloads
        if ! curl -fL --retry 3 --retry-delay 5 "$url" -o ffmpeg.tar.xz; then
            echo "Error: Failed to download FFmpeg for $platform"
            rm -rf "$temp_dir"
            return 1
        fi

        if ! verify_md5_from_url "ffmpeg.tar.xz" "${url}.md5"; then
            echo "Error: FFmpeg checksum verification failed for $platform"
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
        mv "$found_ffmpeg" "$TOOLS_DIR/ffmpeg/$platform/ffmpeg"
        mv "$found_ffprobe" "$TOOLS_DIR/ffmpeg/$platform/ffprobe"
        
        # Copy to ffprobe directory
        if [ -f "$TOOLS_DIR/ffmpeg/$platform/ffprobe" ]; then
            cp "$TOOLS_DIR/ffmpeg/$platform/ffprobe" "$TOOLS_DIR/ffprobe/$platform/"
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
    
    rm -rf "$temp_dir"
    echo "Completed download for $platform"
}

# Check dependencies first
check_dependencies

# Optional platform filter: linux, win, osx, or empty for all platforms.
# In CI each runner passes its own platform so we only download what it needs.
TARGET_PLATFORM="${1:-}"

should_download() {
    local rid="$1"
    [ -z "$TARGET_PLATFORM" ] && return 0
    [[ "$rid" == "${TARGET_PLATFORM}-"* ]] && return 0
    return 1
}

# Download for requested platforms (continue even if some fail)
failed_downloads=""

# Windows x64
if should_download "win-x64"; then
    echo "=== Downloading Windows x64 ==="
    if ! download_ffmpeg "win-x64" "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"; then
        failed_downloads="$failed_downloads win-x64"
    fi
fi

# Linux x64
if should_download "linux-x64"; then
    echo "=== Downloading Linux x64 ==="
    if ! download_ffmpeg "linux-x64" "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz"; then
        failed_downloads="$failed_downloads linux-x64"
    fi
fi

# Linux ARM64
if should_download "linux-arm64"; then
    echo "=== Downloading Linux ARM64 ==="
    if ! download_ffmpeg "linux-arm64" "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz"; then
        failed_downloads="$failed_downloads linux-arm64"
    fi
fi

# macOS (manual)
if should_download "osx-x64"; then
    echo "=== macOS x64 ==="
    mkdir -p "$TOOLS_DIR/ffmpeg/osx-x64"
    mkdir -p "$TOOLS_DIR/ffprobe/osx-x64"
    echo "macOS FFmpeg/FFprobe are not downloaded automatically."
    echo "Download manually from https://evermeet.cx/ffmpeg/ and place binaries under Tools/ffmpeg/osx-x64/"
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
