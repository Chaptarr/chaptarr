#!/usr/bin/env bash
set -euo pipefail

version="${VERSION:-}"
series="${SERIES:-}"
series_version="${SERIES_VERSION:-}"
archive_path="${ARCHIVE_PATH:-}"
output_dir="${OUTPUT_DIR:-}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'VERSION must be MAJOR.MINOR.PATCH.' >&2
  exit 2
fi
if [[ ! "$series" =~ ^[a-z][a-z0-9]+$ ]]; then
  echo 'SERIES must be one Ubuntu series codename.' >&2
  exit 2
fi
if [[ ! "$series_version" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]]; then
  echo 'SERIES_VERSION must be the numeric Ubuntu version for SERIES.' >&2
  exit 2
fi
if [[ ! -s "$archive_path" || ! -s "${archive_path}.sha256" || -z "$output_dir" ]]; then
  echo 'ARCHIVE_PATH, its .sha256 sidecar, and OUTPUT_DIR are required.' >&2
  exit 2
fi
if [[ "$(basename "$archive_path")" != "ChaptarrNG-${version}-linux-x64.tar.gz" ]]; then
  echo 'ARCHIVE_PATH must name the matching ChaptarrNG Linux x64 release archive.' >&2
  exit 2
fi
if ! dpkg --compare-versions "$version" gt 0.4.21.71; then
  echo 'The PPA bridge version must be newer than BookshelfNG 0.4.21.71.' >&2
  exit 2
fi
command -v dpkg-buildpackage >/dev/null
command -v dpkg-source >/dev/null
command -v dpkg-parsechangelog >/dev/null
command -v tar >/dev/null
if [[ -n "${GPG_PRIVATE_KEY:-}" ]]; then
  command -v gpg >/dev/null
fi

(cd "$(dirname "$archive_path")" && sha256sum --check "$(basename "$archive_path").sha256")
output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd -P)"
source_version="${version}-2~ppa1~ubuntu${series_version}.1"
dpkg --validate-version "$source_version"
for artifact in \
  "bookshelfng_${source_version}.dsc" \
  "bookshelfng_${source_version}.debian.tar.xz" \
  "bookshelfng_${source_version}_source.changes" \
  "bookshelfng_${version}.orig.tar.gz"; do
  if [[ -e "$output_dir/$artifact" ]]; then
    echo "Refusing to overwrite existing PPA source artifact: $artifact" >&2
    exit 1
  fi
done

build_root="$(mktemp -d "$output_dir/.bookshelfng-ppa-${series}.XXXXXX")"
gpg_home=""
cleanup() {
  local status=$?
  if [[ -n "$gpg_home" ]]; then
    rm -rf -- "$gpg_home"
  fi
  if [[ "$status" == 0 ]]; then
    rm -rf -- "$build_root"
  else
    echo "PPA source build failed; review retained work directory: $build_root" >&2
  fi
  exit "$status"
}
trap cleanup EXIT

source_tree="$build_root/bookshelfng-$version"
mkdir -p "$source_tree"
(
  cd "$repo_root"
  git ls-files -z --cached | tar --null --files-from=- --create --file=-
) | tar --extract --file=- --directory="$source_tree"
cp -- "$archive_path" "$source_tree/ChaptarrNG-${version}-linux-x64.tar.gz"

build_epoch="${SOURCE_DATE_EPOCH:-$(git -C "$repo_root" show -s --format=%ct HEAD)}"
if [[ ! "$build_epoch" =~ ^[0-9]+$ ]]; then
  echo 'SOURCE_DATE_EPOCH must be a Unix timestamp.' >&2
  exit 2
fi
orig_tar="$build_root/bookshelfng_${version}.orig.tar.gz"
tar --sort=name --mtime="@${build_epoch}" --owner=0 --group=0 --numeric-owner \
  --create --gzip --file="$orig_tar" --directory="$build_root" "bookshelfng-$version"

debian_dir="$source_tree/debian"
mkdir -p "$debian_dir/source"
install -m 0644 "$repo_root/packaging/legacy/debian-ppa/control" "$debian_dir/control"
install -m 0644 "$repo_root/packaging/legacy/debian-ppa/source/format" "$debian_dir/source/format"
install -m 0755 "$repo_root/packaging/legacy/debian-ppa/rules" "$debian_dir/rules"
changelog_date="$(date -Ru --date="@${build_epoch}")"
cat > "$debian_dir/changelog" <<EOF
bookshelfng (${source_version}) ${series}; urgency=medium

  * Package the checksum-verified ChaptarrNG ${version} release under the
    existing BookshelfNG Debian identity.

 -- ChaptarrNG maintainers <noreply@github.com>  ${changelog_date}
EOF

gpg_args=(-us -uc)
if [[ -n "${GPG_PRIVATE_KEY:-}" ]]; then
  gpg_home="$(mktemp -d "$output_dir/.bookshelfng-ppa-gnupg.XXXXXX")"
  chmod 0700 "$gpg_home"
  if ! printf '%s\n' "$GPG_PRIVATE_KEY" | GNUPGHOME="$gpg_home" gpg --batch --import >/dev/null 2>&1; then
    echo 'GPG_PRIVATE_KEY could not be imported for source package signing.' >&2
    exit 1
  fi
  fingerprint="$(GNUPGHOME="$gpg_home" gpg --batch --with-colons --list-secret-keys 2>/dev/null | awk -F: '$1 == "fpr" { print $10; exit }')"
  if [[ ! "$fingerprint" =~ ^[A-Fa-f0-9]{40,64}$ ]]; then
    echo 'GPG_PRIVATE_KEY did not provide a usable signing key.' >&2
    exit 1
  fi
  gpg_args=("--sign-keyid=$fingerprint")
fi

(
  cd "$source_tree"
  # This step only assembles the signed source package. The Launchpad binary
  # build resolves Build-Depends from debian/control in its build environment.
  if [[ -n "${GPG_PRIVATE_KEY:-}" ]]; then
    GNUPGHOME="$gpg_home" dpkg-buildpackage -S -sa -d "${gpg_args[@]}"
  else
    dpkg-buildpackage -S -sa -d "${gpg_args[@]}"
  fi
)

for artifact in \
  "bookshelfng_${source_version}.dsc" \
  "bookshelfng_${source_version}.debian.tar.xz" \
  "bookshelfng_${source_version}_source.changes" \
  "bookshelfng_${version}.orig.tar.gz"; do
  test -s "$build_root/$artifact"
  cp -- "$build_root/$artifact" "$output_dir/$artifact"
done

echo "Built ${series} BookshelfNG PPA source package for ChaptarrNG ${version} in $output_dir"
