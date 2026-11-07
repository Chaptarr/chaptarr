#!/usr/bin/env bash
set -euo pipefail

repo="snapetech/chaptarrng"
release_base="https://github.com/${repo}/releases/download"
tag="${1:-}"

if [[ -z "$tag" && -r hook_payload ]]; then
  tag="$(tr -d '\r\n' < hook_payload)"
fi

if [[ ! "$tag" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
  echo 'COPR webhook payload must be a verified vMAJOR.MINOR.PATCH release tag.' >&2
  exit 2
fi

version="${BASH_REMATCH[1]}"
source_rpm="bookshelfng-${version}-1.src.rpm"
resultdir="${COPR_RESULTDIR:-.}"
workdir="$(mktemp -d)"
trap 'rm -rf -- "$workdir"' EXIT

curl --fail --location --silent --show-error --retry 3 --retry-all-errors \
  --output "${workdir}/${source_rpm}" \
  "${release_base}/${tag}/${source_rpm}"
curl --fail --location --silent --show-error --retry 3 --retry-all-errors \
  --output "${workdir}/${source_rpm}.sha256" \
  "${release_base}/${tag}/${source_rpm}.sha256"
(cd "$workdir" && sha256sum --check "${source_rpm}.sha256")

mkdir -p "$resultdir"
resultdir="$(cd "$resultdir" && pwd -P)"
(
  cd "$resultdir"
  rpm2cpio "${workdir}/${source_rpm}" | cpio -idm --no-absolute-filenames
)

for required_file in \
  bookshelfng.spec \
  "ChaptarrNG-${version}-linux-x64.tar.gz" \
  bookshelfng.service bookshelfng.sysusers bookshelfng.tmpfiles bookshelfng.env chaptarrng.package_info bookshelfng-healthcheck bookshelfng-journal bookshelfng-finalize; do
  if [[ ! -f "${resultdir}/${required_file}" ]]; then
    echo "The COPR source RPM is missing ${required_file}." >&2
    exit 1
  fi
done

echo "Prepared the verified ChaptarrNG ${version} payload for the legacy bookshelfng COPR package."
