#!/usr/bin/env bash
set -euo pipefail

version="${VERSION:-}"
archive_path="${ARCHIVE_PATH:-}"
output_dir="${OUTPUT_DIR:-}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'VERSION must be MAJOR.MINOR.PATCH.' >&2
  exit 2
fi
if [[ ! -s "$archive_path" || -z "$output_dir" ]]; then
  echo 'ARCHIVE_PATH and OUTPUT_DIR are required.' >&2
  exit 2
fi
command -v rpmbuild >/dev/null

output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd -P)"
work_root="$(mktemp -d)"
trap 'rm -rf -- "$work_root"' EXIT

build_one() {
  local package="$1"
  local spec_template="$2"
  local source_dir="$3"
  local top="$work_root/$package"
  mkdir -p "$top/BUILD" "$top/BUILDROOT" "$top/RPMS" "$top/SOURCES" "$top/SPECS" "$top/SRPMS"
  install -m 0644 "$archive_path" "$top/SOURCES/ChaptarrNG-${version}-linux-x64.tar.gz"
  install -m 0644 "$repo_root/$source_dir"/*.service "$top/SOURCES/"
  install -m 0644 "$repo_root/$source_dir"/*.sysusers "$top/SOURCES/"
  install -m 0644 "$repo_root/$source_dir"/*.tmpfiles "$top/SOURCES/"
  install -m 0644 "$repo_root/packaging/native/chaptarrng.package_info" "$top/SOURCES/"
  if [[ "$package" == bookshelfng ]]; then
    install -m 0600 "$repo_root/$source_dir/bookshelfng.env" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-healthcheck" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-begin" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-prepare" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-cleanup" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-rollback" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-journal" "$top/SOURCES/"
    install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-finalize" "$top/SOURCES/"
  fi
  sed "s/__VERSION__/${version}/g" "$repo_root/$spec_template" > "$top/SPECS/${package}.spec"
  rpmbuild -bs --define "_topdir $top" "$top/SPECS/${package}.spec"
  local srpm="$top/SRPMS/${package}-${version}-1.src.rpm"
  test -s "$srpm"
  install -m 0644 "$srpm" "$output_dir/"
  (cd "$output_dir" && sha256sum "$(basename "$srpm")" > "$(basename "$srpm").sha256")
  echo "Built $package $version source RPM."
}

build_one chaptarrng packaging/rpm/chaptarrng.spec packaging/systemd
build_one bookshelfng packaging/legacy/rpm/bookshelfng.spec.in packaging/legacy/rpm
