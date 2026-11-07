#!/usr/bin/env bash
set -euo pipefail

version="${VERSION:-}"
archive_sha256="${ARCHIVE_SHA256:-}"
source_archive_path="${SOURCE_ARCHIVE_PATH:-}"
output_dir="${OUTPUT_DIR:-}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'VERSION must be MAJOR.MINOR.PATCH.' >&2
  exit 2
fi
if [[ ! "$archive_sha256" =~ ^[0-9a-f]{64}$ ]]; then
  echo 'ARCHIVE_SHA256 must be a lowercase SHA-256 digest.' >&2
  exit 2
fi
if [[ -n "$source_archive_path" ]]; then
  if [[ ! -s "$source_archive_path" ]]; then
    echo 'SOURCE_ARCHIVE_PATH does not name a non-empty release archive.' >&2
    exit 2
  fi
  actual_sha256="$(sha256sum "$source_archive_path" | awk '{ print $1 }')"
  if [[ "$actual_sha256" != "$archive_sha256" ]]; then
    echo 'SOURCE_ARCHIVE_PATH does not match ARCHIVE_SHA256.' >&2
    exit 2
  fi
fi
if [[ -z "$output_dir" ]]; then
  echo 'OUTPUT_DIR is required.' >&2
  exit 2
fi
command -v docker >/dev/null

output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd -P)"
work_root="$(mktemp -d)"
trap 'rm -rf -- "$work_root"' EXIT

for pkgbase in chaptarrng-bin bookshelfng-bin; do
  work="$work_root/$pkgbase"
  output="$output_dir/$pkgbase"
  mkdir -p "$work" "$output"

  if [[ "$pkgbase" == chaptarrng-bin ]]; then
    recipe="$repo_root/packaging/aur"
    cp "$recipe/PKGBUILD.in" "$work/PKGBUILD"
    cp "$recipe/chaptarrng.install" "$work/"
    cp "$repo_root/packaging/systemd/chaptarrng.service" "$work/"
    cp "$repo_root/packaging/systemd/chaptarrng.sysusers" "$work/"
    cp "$repo_root/packaging/systemd/chaptarrng.tmpfiles" "$work/"
    cp "$repo_root/packaging/native/chaptarrng.package_info" "$work/"
  else
    recipe="$repo_root/packaging/legacy/aur/bookshelfng-bin"
    cp "$recipe/PKGBUILD.in" "$work/PKGBUILD"
    cp "$recipe/bookshelfng.install" "$work/"
    cp "$recipe/bookshelfng.service" "$work/"
    cp "$recipe/bookshelfng.env" "$work/"
    cp "$recipe/bookshelfng.sysusers" "$work/"
    cp "$recipe/bookshelfng.tmpfiles" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-healthcheck" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-begin" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-prepare" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-cleanup" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-rollback" "$work/"
    cp "$repo_root/packaging/legacy/scripts/bookshelfng-journal" "$work/"
    cp "$repo_root/packaging/native/chaptarrng.package_info" "$work/"
  fi
  use_local_source=false
  if [[ -n "$source_archive_path" ]]; then
    cp "$source_archive_path" "$work/ChaptarrNG-${version}-linux-x64.tar.gz"
    use_local_source=true
  fi

  sed -i \
    -e "s/__VERSION__/${version}/g" \
    -e "s/__ARCHIVE_SHA256__/${archive_sha256}/g" \
    "$work/PKGBUILD"
  if grep -nE '__[A-Z_]+__' "$work/PKGBUILD"; then
    echo "The $pkgbase recipe has an unresolved release placeholder." >&2
    exit 1
  fi

  chmod 0755 "$work"
  chmod -R a+rX "$work"
  chmod 0777 "$output"
  docker run --rm \
    --user 65534:65534 \
    --volume "$work:/pkg:ro" \
    --volume "$output:/out:rw" \
    --workdir /tmp \
    --env HOME=/tmp \
    --env PKGBASE="$pkgbase" \
    --env VERSION="$version" \
    --env USE_LOCAL_SOURCE="$use_local_source" \
    archlinux:base-devel \
    bash -euc '
      mkdir -p /tmp/aur-package /tmp/pkgdest
      cp -R /pkg/. /tmp/aur-package/
      cd /tmp/aur-package
      if [[ "$USE_LOCAL_SOURCE" == true ]]; then
        cp PKGBUILD PKGBUILD.canonical
        sed -i "s#https://github.com/snapetech/chaptarrng/releases/download/v\${pkgver}/ChaptarrNG-\${pkgver}-linux-x64.tar.gz#file:///pkg/ChaptarrNG-${VERSION}-linux-x64.tar.gz#" PKGBUILD
        grep -Fq "file:///pkg/ChaptarrNG-${VERSION}-linux-x64.tar.gz" PKGBUILD
        makepkg --printsrcinfo -p PKGBUILD.canonical > /out/.SRCINFO
      else
        makepkg --printsrcinfo > /out/.SRCINFO
      fi
      PKGDEST=/tmp/pkgdest makepkg --noconfirm --nodeps
      pkgfile="$(find /tmp/pkgdest -maxdepth 1 -type f -name "${PKGBASE}-[0-9]*-x86_64.pkg.tar.*" -print -quit)"
      test -n "$pkgfile"
      pacman -Qlp "$pkgfile" > /tmp/package-files
      if [[ "$PKGBASE" == chaptarrng-bin ]]; then
        grep -Fq " /usr/lib/chaptarrng/bin/Chaptarr" /tmp/package-files
        grep -Fq " /usr/lib/chaptarrng/package_info" /tmp/package-files
      else
        grep -Fq " /usr/lib/chaptarrng/bin/Chaptarr" /tmp/package-files
        grep -Fq " /usr/lib/systemd/system/bookshelfng.service" /tmp/package-files
        grep -Fq " /usr/lib/chaptarrng/package_info" /tmp/package-files
        grep -Fq " /etc/bookshelfng/bookshelfng.env" /tmp/package-files
      fi
    '

  cp "$work/PKGBUILD" "$output/PKGBUILD"
  cp "$work"/*.install "$work"/*.service "$work"/*.sysusers "$work"/*.tmpfiles "$work"/*.package_info "$output/"
  if [[ "$pkgbase" == bookshelfng-bin ]]; then
    cp "$work"/bookshelfng-healthcheck "$work"/bookshelfng-begin "$work"/bookshelfng-prepare "$work"/bookshelfng-cleanup "$work"/bookshelfng-rollback "$work"/bookshelfng-journal "$output/"
  fi
  if compgen -G "$work/*.env" >/dev/null; then cp "$work"/*.env "$output/"; fi
  grep -Fqx "pkgbase = $pkgbase" "$output/.SRCINFO"
  awk -v expected="$version" '$1 == "pkgver" && $2 == "=" && $3 == expected { found = 1 } END { exit !found }' "$output/.SRCINFO"
  echo "Built and validated $pkgbase $version."
done
