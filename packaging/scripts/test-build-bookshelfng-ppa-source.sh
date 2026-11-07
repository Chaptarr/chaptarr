#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
scratch_root="${TASK_SCRATCH_DIR:-${TMPDIR:-/tmp}/chaptarrng-ppa-source-test}"
mkdir -p "$scratch_root"
work="$(mktemp -d "$scratch_root/run.XXXXXX")"
test_gpg_home="$work/test-gnupg"
cleanup() {
  local status=$?
  rm -rf -- "$test_gpg_home"
  if [[ "$status" == 0 ]]; then
    rm -rf -- "$work"
  else
    echo "PPA source package test failed; review retained output: $work" >&2
  fi
  exit "$status"
}
trap cleanup EXIT

for tool in dpkg-buildpackage dpkg-deb dpkg-distaddfile dpkg-source tar sha256sum gpg; do
  command -v "$tool" >/dev/null
done

mkdir -m 0700 "$test_gpg_home"
GNUPGHOME="$test_gpg_home" gpg --batch --passphrase '' --quick-generate-key \
  'ChaptarrNG PPA package test <ppa-test@example.invalid>' ed25519 sign 0 >/dev/null 2>&1
gpg_key="$(GNUPGHOME="$test_gpg_home" gpg --batch --armor --export-secret-keys ppa-test@example.invalid)"

version=0.9.978
series=noble
archive="$work/ChaptarrNG-${version}-linux-x64.tar.gz"
mkdir -p "$work/input/ChaptarrNG"
printf '#!/bin/sh\nexit 0\n' > "$work/input/ChaptarrNG/Chaptarr"
printf 'Synthetic test payload license.\n' > "$work/input/ChaptarrNG/LICENSE"
chmod 0755 "$work/input/ChaptarrNG/Chaptarr"
tar -czf "$archive" -C "$work/input" ChaptarrNG
(cd "$work" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")

output="$work/source-artifacts"
VERSION="$version" \
  SERIES="$series" \
  SERIES_VERSION=24.04 \
  GPG_PRIVATE_KEY="$gpg_key" \
  ARCHIVE_PATH="$archive" \
  OUTPUT_DIR="$output" \
  SOURCE_DATE_EPOCH=1792627200 \
  bash "$repo_root/packaging/scripts/build-bookshelfng-ppa-source.sh"

source_version="${version}-2~ppa1~ubuntu24.04.1"
dsc="$output/bookshelfng_${source_version}.dsc"
changes="$output/bookshelfng_${source_version}_source.changes"
orig="$output/bookshelfng_${version}.orig.tar.gz"
test -s "$dsc"
test -s "$changes"
test -s "$orig"
test -s "$output/bookshelfng_${source_version}.debian.tar.xz"
grep -Fq "Distribution: ${series}" "$changes"
GNUPGHOME="$test_gpg_home" gpg --batch --verify "$dsc" >/dev/null 2>&1
GNUPGHOME="$test_gpg_home" gpg --batch --verify "$changes" >/dev/null 2>&1

extracted="$work/extracted"
dpkg-source --extract "$dsc" "$extracted"
(cd "$extracted" && dpkg-buildpackage -b -uc -us -d)

package="$work/bookshelfng_${source_version}_amd64.deb"
test -s "$package"
test "$(dpkg-deb --field "$package" Package)" = bookshelfng
test "$(dpkg-deb --field "$package" Version)" = "$source_version"
test "$(dpkg-deb --field "$package" Architecture)" = amd64
contents="$(dpkg-deb --contents "$package")"
grep -Fq './usr/lib/chaptarrng/bin/Chaptarr' <<< "$contents"
grep -Fq './etc/bookshelfng/bookshelfng.env' <<< "$contents"
grep -Fq './lib/systemd/system/bookshelfng.service' <<< "$contents"

printf '%s\n' 'BookshelfNG Launchpad source and binary package build passed.'
