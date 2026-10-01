#!/usr/bin/env bash
set -euo pipefail

TAG="${1:?usage: package-yunohost-release-assets.sh vX.Y.Z [output-directory]}"
OUTPUT_DIR="${2:-dist-yunohost}"
IMAGE="${GHCR_IMAGE:-ghcr.io/snapetech/chaptarrng}"
VERSION="${TAG#v}"

if [[ ! "$TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
    echo "Invalid ChaptarrNG release tag: $TAG" >&2
    exit 2
fi

SOURCE_DATE_EPOCH="$(git show -s --format=%ct HEAD)"
mkdir -p "$OUTPUT_DIR"

for architecture in amd64 arm64; do
    platform="linux/$architecture"
    stage="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-yunohost-${architecture}.XXXXXX")"
    container="chaptarrng-yunohost-${architecture}-$$"

    cleanup() {
        docker rm -f "$container" >/dev/null 2>&1 || true
        rm -rf "$stage"
    }
    trap cleanup EXIT

    image_digest="$(docker manifest inspect "$IMAGE:$VERSION" | jq -er \
        --arg architecture "$architecture" \
        '.manifests[] | select(.platform.os == "linux" and .platform.architecture == $architecture) | .digest' \
        | head -n 1)"
    image_ref="$IMAGE@$image_digest"
    docker pull "$image_ref"
    actual_platform="$(docker image inspect "$image_ref" --format '{{.Os}}/{{.Architecture}}')"
    if [[ "$actual_platform" != "$platform" ]]; then
        echo "Expected $platform image, got $actual_platform ($image_ref)" >&2
        exit 1
    fi

    docker create --platform "$platform" --name "$container" "$image_ref" >/dev/null
    mkdir -p "$stage/bin" "$stage/tools" "$stage/opt"
    docker cp "$container:/app/." "$stage/bin/"
    docker cp "$container:/opt/mp4v2" "$stage/opt/"
    mkdir -p "$stage/vendor-bin"
    docker cp "$container:/usr/local/bin/." "$stage/vendor-bin/"
    docker rm "$container" >/dev/null

    [[ -s "$stage/bin/Chaptarr.dll" ]] || { echo "Chaptarr.dll missing from $platform image" >&2; exit 1; }
    [[ -s "$stage/vendor-bin/m4b-tool" ]] || { echo "m4b-tool missing from $platform image" >&2; exit 1; }
    loader="$(basename "$stage"/opt/mp4v2/lib/ld-musl-*.so.1)"
    [[ -s "$stage/opt/mp4v2/lib/$loader" ]] || { echo "mp4v2 musl loader missing from $platform image" >&2; exit 1; }

    for source in "$stage"/vendor-bin/mp4*; do
        [[ -f "$source" ]] || continue
        tool="${source##*/}"
        cat > "$stage/tools/$tool" <<WRAPPER
#!/bin/sh
set -eu
tool_path=\$(command -v "\$0")
tools_dir=\$(CDPATH= cd -- "\$(dirname -- "\$tool_path")" && pwd)
app_root=\$(dirname -- "\$tools_dir")
exec "\$app_root/opt/mp4v2/lib/$loader" --library-path "\$app_root/opt/mp4v2/lib" "\$app_root/opt/mp4v2/bin/$tool" "\$@"
WRAPPER
        chmod 0755 "$stage/tools/$tool"
    done
    install -m 0755 "$stage/vendor-bin/m4b-tool" "$stage/tools/m4b-tool"
    rm -rf "$stage/vendor-bin"

    cat > "$stage/package_info" <<PACKAGE_INFO
PackageVersion=$VERSION
PackageAuthor=YunoHost-Apps
UpdateMethod=External
UpdateMethodMessage=Updates are managed by YunoHost.
Branch=main
PACKAGE_INFO

    chmod -R a+rX "$stage"
    asset="$OUTPUT_DIR/chaptarrng-v${VERSION}-linux-${architecture}-yunohost.tar.gz"
    tar --sort=name --mtime="@$SOURCE_DATE_EPOCH" --owner=0 --group=0 --numeric-owner \
        -C "$stage" -cf - . | gzip -n > "$asset"
    sha256sum "$asset"

    cleanup
    trap - EXIT
    echo "Created $asset"
done
