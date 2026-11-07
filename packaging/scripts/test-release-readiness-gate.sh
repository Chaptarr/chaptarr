#!/usr/bin/env bash
# shellcheck disable=SC2016
set -euo pipefail

script="$(dirname "$0")/require-release-readiness.sh"

if CHAPTARRNG_RELEASE_READY=false bash "$script" >/dev/null 2>&1; then
  echo 'The readiness gate accepted an explicit false value.' >&2
  exit 1
fi

if CHAPTARRNG_RELEASE_READY=true BOOKSHELFNG_RELEASE_READY=false bash "$script" >/dev/null 2>&1; then
  echo 'The readiness gate accepted incomplete BookshelfNG retirement readiness.' >&2
  exit 1
fi

if CHAPTARRNG_RELEASE_READY=true env -u BOOKSHELFNG_RELEASE_READY bash "$script" >/dev/null 2>&1; then
  echo 'The readiness gate accepted a missing BookshelfNG readiness value.' >&2
  exit 1
fi

CHAPTARRNG_RELEASE_READY=true BOOKSHELFNG_RELEASE_READY=true bash "$script"

for workflow in \
  .github/workflows/release.yml \
  .github/workflows/release-distribution.yml \
  .github/workflows/docker-publish.yml; do
  if ! grep -Fq 'CHAPTARRNG_RELEASE_READY: ${{ vars.CHAPTARRNG_RELEASE_READY }}' "$workflow" ||
    ! grep -Fq 'BOOKSHELFNG_RELEASE_READY: ${{ vars.BOOKSHELFNG_RELEASE_READY }}' "$workflow" ||
    ! grep -Fq './packaging/scripts/require-release-readiness.sh' "$workflow"; then
    echo "The readiness gate is missing from $workflow." >&2
    exit 1
  fi
done

if ! grep -Fq 'needs: require-release-readiness' .github/workflows/docker-publish.yml; then
  echo 'Docker publication is not dependent on its readiness gate.' >&2
  exit 1
fi

echo 'Release and distribution readiness gates are fail-closed and wired to all publishers.'
