#!/usr/bin/env bash
set -euo pipefail

version="${VERSION:-}"
deb_version="${DEB_VERSION:-}"
archive_path="${ARCHIVE_PATH:-}"
output_dir="${OUTPUT_DIR:-}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo 'VERSION must be MAJOR.MINOR.PATCH.' >&2
  exit 2
fi
if ! dpkg --compare-versions "$version" gt 0.4.21.71; then
  echo 'The bridge version must be newer than the final public BookshelfNG package (0.4.21.71).' >&2
  exit 2
fi
if [[ -z "$deb_version" ]]; then
  deb_version="${version}-1"
fi
if ! dpkg --validate-version "$deb_version" || ! dpkg --compare-versions "$deb_version" gt 0.4.21.71; then
  echo 'DEB_VERSION must be a valid Debian version newer than the final public BookshelfNG package (0.4.21.71).' >&2
  exit 2
fi
if [[ ! -s "$archive_path" || -z "$output_dir" ]]; then
  echo 'ARCHIVE_PATH and OUTPUT_DIR are required.' >&2
  exit 2
fi
command -v dpkg-deb >/dev/null
command -v tar >/dev/null

output_dir="$(mkdir -p "$output_dir" && cd "$output_dir" && pwd -P)"
work_root="$(mktemp -d "$output_dir/.bookshelfng-deb-bridge.XXXXXX")"
trap 'rm -rf -- "$work_root"' EXIT

package_root="$work_root/package"
payload="$work_root/payload"
mkdir -p "$package_root/DEBIAN" "$package_root/usr/lib/chaptarrng/bin" \
  "$package_root/usr/bin" "$package_root/etc/bookshelfng" \
  "$package_root/lib/systemd/system" "$package_root/usr/lib/sysusers.d" \
  "$package_root/usr/lib/tmpfiles.d" "$package_root/usr/share/doc/bookshelfng"
mkdir -p "$package_root/usr/lib/chaptarrng/handoff"
mkdir -p "$payload"
tar -xzf "$archive_path" --strip-components=1 -C "$payload"
if [[ ! -x "$payload/Chaptarr" || ! -f "$payload/LICENSE" ]]; then
  echo 'The ChaptarrNG Linux x64 archive must contain executable Chaptarr and LICENSE files at its root.' >&2
  exit 1
fi

cp -a "$payload/." "$package_root/usr/lib/chaptarrng/bin/"
install -m 0644 "$repo_root/packaging/native/chaptarrng.package_info" \
  "$package_root/usr/lib/chaptarrng/package_info"
install -m 0644 "$payload/LICENSE" "$package_root/usr/share/doc/bookshelfng/copyright"
install -m 0644 "$repo_root/packaging/legacy/debian/bookshelfng.service" \
  "$package_root/lib/systemd/system/bookshelfng.service"
install -m 0600 "$repo_root/packaging/legacy/debian/bookshelfng.env" \
  "$package_root/etc/bookshelfng/bookshelfng.env"
install -m 0644 "$repo_root/packaging/legacy/debian/bookshelfng.sysusers" \
  "$package_root/usr/lib/sysusers.d/bookshelfng.conf"
install -m 0644 "$repo_root/packaging/legacy/debian/bookshelfng.tmpfiles" \
  "$package_root/usr/lib/tmpfiles.d/bookshelfng.conf"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-healthcheck" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-healthcheck"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-begin" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-begin"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-prepare" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-prepare"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-cleanup" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-cleanup"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-rollback" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-rollback"
install -m 0755 "$repo_root/packaging/legacy/scripts/bookshelfng-journal" \
  "$package_root/usr/lib/chaptarrng/handoff/bookshelfng-journal"
install -m 0755 "$repo_root/packaging/legacy/debian/postinst" "$package_root/DEBIAN/postinst"
install -m 0755 "$repo_root/packaging/legacy/debian/preinst" "$package_root/DEBIAN/preinst"
install -m 0755 "$repo_root/packaging/legacy/debian/prerm" "$package_root/DEBIAN/prerm"
install -m 0755 "$repo_root/packaging/legacy/debian/postrm" "$package_root/DEBIAN/postrm"
printf '/etc/bookshelfng/bookshelfng.env\n' > "$package_root/DEBIAN/conffiles"
sed "s/__DEB_VERSION__/${deb_version}/g" "$repo_root/packaging/legacy/debian/control.in" \
  > "$package_root/DEBIAN/control"
ln -s ../lib/chaptarrng/bin/Chaptarr "$package_root/usr/bin/bookshelfng"
chmod -R u=rwX,go=rX "$package_root/usr/lib/chaptarrng"
chmod 0600 "$package_root/etc/bookshelfng/bookshelfng.env"

output_package="$output_dir/bookshelfng_${deb_version}_amd64.deb"
if [[ -e "$output_package" || -e "${output_package}.sha256" ]]; then
  echo "Refusing to overwrite an existing BookshelfNG Debian package: $output_package" >&2
  exit 1
fi
dpkg-deb --root-owner-group --build "$package_root" "$output_package"
test "$(dpkg-deb --field "$output_package" Package)" = bookshelfng
test "$(dpkg-deb --field "$output_package" Version)" = "$deb_version"
test "$(dpkg-deb --field "$output_package" Architecture)" = amd64
(cd "$output_dir" && sha256sum "$(basename "$output_package")" > "$(basename "$output_package").sha256")
echo "Built candidate-only BookshelfNG Debian bridge: $output_package"
