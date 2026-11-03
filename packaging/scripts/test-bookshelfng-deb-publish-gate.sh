#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
release_workflow="$repo_root/.github/workflows/release.yml"
distribution_workflow="$repo_root/.github/workflows/release-distribution.yml"

release_events="$(sed -n '/^on:/,/^permissions:/p' "$release_workflow")"
grep -Fq '  workflow_dispatch:' <<< "$release_events"
grep -Fq '          - publish' <<< "$release_events"
grep -Fq '          - private' <<< "$release_events"
grep -Fq '      release_tag:' <<< "$release_events"
release_tag_input="$(sed -n '/      release_tag:/,/^permissions:/p' "$release_workflow")"
grep -Fq 'Exact existing stable release tag to validate and build' <<< "$release_tag_input"
grep -Fq '        required: true' <<< "$release_tag_input"
grep -Fq '        type: string' <<< "$release_tag_input"
if grep -Fq '  push:' <<< "$release_events"; then
  echo 'Stable releases must be admitted through the private main-branch dispatch.' >&2
  exit 1
fi
grep -Fq 'release_requested: ${{ steps.release-request.outputs.release_requested }}' "$release_workflow"
grep -Fq 'gh release list --limit 100' "$release_workflow"
preflight_steps="$(sed -n '/^  preflight:/,/^  validate:/p' "$release_workflow")"
grep -Fq 'python3 packaging/scripts/check_stable_release_version.py' <<< "$preflight_steps"
grep -Fq 'git fetch --no-tags origin "refs/tags/${TAG}:refs/tags/${TAG}" refs/heads/main:refs/remotes/origin/main' <<< "$preflight_steps"
grep -Fq 'if ! git merge-base --is-ancestor "$RELEASE_SHA" refs/remotes/origin/main; then' <<< "$preflight_steps"
if grep -Fq 'gh release list' <<< "$preflight_steps"; then
  echo 'The private JIT preflight must not depend on the GitHub CLI package.' >&2
  exit 1
fi
for workflow in "$repo_root/.github/workflows/build.yml" \
  "$repo_root/.github/workflows/docker-publish.yml" \
  "$release_workflow" "$repo_root/.github/workflows/release-distribution.yml"; do
  if [[ "$workflow" == "$repo_root/.github/workflows/build.yml" ]]; then
    if grep -Fq 'sudo apt-get' "$workflow" && {
      ! grep -Fq 'RUNNER_POOL: ${{ inputs.runner_pool }}' "$workflow" ||
      ! grep -Fq 'if [[ "$RUNNER_POOL" == "public" ]]' "$workflow" ||
      ! grep -Fq "inputs.runner_pool == 'public'" "$workflow"
    }; then
      echo 'Hosted PostgreSQL client installation must be restricted to the explicit public runner pool.' >&2
      exit 1
    fi
  elif grep -Fq 'sudo apt-get' "$workflow"; then
    echo "Private JIT release dependencies must be installed by the broker: $workflow" >&2
    exit 1
  fi
done
grep -Fq 'packaging/scripts/require-release-readiness.sh' "$release_workflow"
grep -Fq "if: \${{ vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == 'true' && vars.CHAPTARRNG_RELEASE_READY == 'true' && vars.BOOKSHELFNG_RELEASE_READY == 'true' }}" "$release_workflow"
grep -Fq 'bash packaging/scripts/build-bookshelfng-deb-bridge.sh' "$release_workflow"
grep -Fq "if [[ \"\$LEGACY_BRIDGE_ENABLED\" == true ]]; then" "$release_workflow"
grep -Fq "gh release upload \"\$TAG\" \"\${release_files[@]}\" --clobber" "$release_workflow"
if grep -Fq 'publish-legacy-deb:' "$distribution_workflow"; then
  echo 'The Debian compatibility asset must be attached by the release job that owns the draft and contents:write permission.' >&2
  exit 1
fi

echo 'BookshelfNG Debian release-asset publication remains gated on release readiness.'
