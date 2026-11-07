#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
workflow="$repo_root/.github/workflows/release-distribution.yml"
release_workflow="$repo_root/.github/workflows/release.yml"
validator="$repo_root/packaging/scripts/validate-release-publisher-config.sh"

grep -Fq 'ppa: ${{ steps.check.outputs.ppa }}' "$workflow"
grep -Fq 'ppa_series: ${{ steps.check.outputs.ppa_series }}' "$workflow"
grep -Fq "needs.credentials.outputs.ppa == 'true'" "$workflow"
grep -Fq "needs.release-target.outputs.legacy_bridge_enabled == 'true'" "$workflow"
grep -Fq 'GPG_PRIVATE_KEY: ${{ secrets.GPG_PRIVATE_KEY }}' "$workflow"
grep -Fq 'LAUNCHPAD_PPA: ${{ vars.LAUNCHPAD_PPA }}' "$workflow"
grep -Fq 'LAUNCHPAD_SERIES: ${{ vars.LAUNCHPAD_SERIES }}' "$workflow"
grep -Fq 'SERIES_VERSION: ${{ matrix.series.version }}' "$workflow"
grep -Fq 'bash packaging/scripts/build-bookshelfng-ppa-source.sh' "$workflow"
grep -Fq 'dput "$LAUNCHPAD_PPA" "$changes"' "$workflow"
grep -Fq 'GPG_PRIVATE_KEY: ${{ secrets.GPG_PRIVATE_KEY }}' "$release_workflow"
grep -Fq 'LAUNCHPAD_SERIES' "$validator"
grep -Fq 'CHAPTARRNG_MIGRATION_BRIDGE_ENABLED' "$workflow"
grep -Fq 'BOOKSHELFNG_RELEASE_READY' "$workflow"

if grep -Fq 'publish-legacy-deb:' "$workflow"; then
  echo 'Launchpad accepts source packages; direct binary Debian uploads are not configured.' >&2
  exit 1
fi

echo 'BookshelfNG Launchpad publishing is gated by readiness, explicit bridge enablement, and valid publisher credentials.'
