#!/usr/bin/env bash
# shellcheck disable=SC2016
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-image-aliases.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/home/.docker" "$work/runner"
touch "$work/home/.docker/config.json"

cat > "$work/bin/docker" <<'MOCK_DOCKER'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == buildx && "$2" == imagetools && "$3" == create ]]
shift 3
target=""; metadata=""; source=""
while (($#)); do
  case "$1" in
    --tag) target="$2"; shift 2 ;;
    --metadata-file) metadata="$2"; shift 2 ;;
    *) source="$1"; shift ;;
  esac
done
[[ -n "$target" && -n "$metadata" && -n "$source" ]]
printf '%s\t%s\n' "$target" "$source" >> "$PUBLISH_LOG"
digest="${source##*@}"
[[ "$target" != "$MOCK_BAD_TARGET" ]] || digest="$MOCK_WRONG_DIGEST"
printf '{"containerimage.descriptor":{"digest":"%s"}}\n' "$digest" > "$metadata"
MOCK_DOCKER

cat > "$work/bin/skopeo" <<'MOCK_SKOPEO'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == --authfile ]]
case "$3" in
  copy)
    source="${@: -2:1}"; target="${@: -1}"
    [[ "$source" == "docker://$SOURCE_IMAGE" ]]
    printf 'copy\t%s\t%s\n' "$source" "$target" >> "$SKOPEO_LOG"
    printf '%s\t%s\n' "$target" "$MOCK_STANDARD_MANIFEST" >> "$SKOPEO_STATE"
    ;;
  inspect)
    [[ "$4" == --raw ]]
    target="$5"
    printf 'inspect\t%s\n' "$target" >> "$SKOPEO_LOG"
    if [[ "$target" == "docker://docker.io/snapetech/chaptarrng:$VERSION" && "$MOCK_BAD_DOCKERHUB_VERSION" == true ]]; then
      printf '%s\n' "$MOCK_WRONG_MANIFEST"
    else
      manifest="$(awk -F '\t' -v target="$target" '$1 == target { value = $2 } END { print value }' "$SKOPEO_STATE")"
      [[ -n "$manifest" ]]
      printf '%s\n' "$manifest"
    fi
    ;;
  *) exit 2 ;;
esac
MOCK_SKOPEO
chmod 0700 "$work/bin/docker" "$work/bin/skopeo"

manifest='{"schemaVersion":2,"image":"standard-chaptarrng"}'
wrong_manifest='{"schemaVersion":2,"image":"wrong"}'
digest="sha256:$(printf '%s\n' "$manifest" | sha256sum | cut -d ' ' -f1)"
wrong_digest="sha256:$(printf '%s\n' "$wrong_manifest" | sha256sum | cut -d ' ' -f1)"
source="ghcr.io/snapetech-org/chaptarrng@$digest"
script="$repo_root/packaging/scripts/publish-image-aliases.sh"
docker_workflow="$repo_root/.github/workflows/docker-publish.yml"
release_workflow="$repo_root/.github/workflows/release.yml"
package_workflow="$repo_root/.github/workflows/release-distribution.yml"
chocolatey_publish_script="$repo_root/packaging/scripts/publish-chocolatey-packages.ps1"
build_workflow="$repo_root/.github/workflows/build.yml"
readiness_gate="$repo_root/packaging/scripts/require-release-readiness.sh"

grep -Fq 'STANDARD_IMAGE: ghcr.io/snapetech-org/chaptarrng' "$docker_workflow"
grep -Fq 'REGISTRY: ghcr.io' "$docker_workflow"
grep -Fq 'target: chaptarrng-standard' "$docker_workflow"
[[ "$(grep -Fc 'docker/build-push-action@' "$docker_workflow")" -eq 1 ]]
[[ "$(grep -Fc 'actions/attest-build-provenance@' "$docker_workflow")" -eq 1 ]]
if grep -Eq 'bookshelf-(softcover|hardcover)|build_(softcover|hardcover)|bookshelf_(softcover|hardcover)_digest' "$docker_workflow" "$build_workflow" "$release_workflow"; then
  echo 'A provider-specific image target or digest remains in the active release path.' >&2
  exit 1
fi
grep -Fq 'SOURCE_IMAGE: ${{ env.STANDARD_IMAGE }}@${{ steps.build.outputs.digest }}' "$docker_workflow"
grep -Fq 'name: Test legacy aliases use the single standard image digest' "$docker_workflow"
grep -Fq 'legacy_bridge_enabled: ${{ vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == '\''true'\'' && vars.CHAPTARRNG_RELEASE_READY == '\''true'\'' && vars.BOOKSHELFNG_RELEASE_READY == '\''true'\'' }}' "$release_workflow"
grep -Fq 'PUBLISH_LEGACY_ALIASES: ${{ inputs.legacy_bridge_enabled && vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED == '\''true'\'' && vars.CHAPTARRNG_RELEASE_READY == '\''true'\'' && vars.BOOKSHELFNG_RELEASE_READY == '\''true'\'' }}' "$docker_workflow"
grep -Fq 'PUBLISH_GHCR_ALIASES: false' "$docker_workflow"
for workflow in "$release_workflow" "$docker_workflow" "$package_workflow"; do
  grep -Fq 'run: ./packaging/scripts/require-release-readiness.sh' "$workflow" || { echo "Retirement release-readiness gate is missing from $workflow." >&2; exit 1; }
done
if CHAPTARRNG_RELEASE_READY=false BOOKSHELFNG_RELEASE_READY=true bash "$readiness_gate" >/dev/null 2>&1; then exit 1; fi
if CHAPTARRNG_RELEASE_READY=true BOOKSHELFNG_RELEASE_READY=false bash "$readiness_gate" >/dev/null 2>&1; then exit 1; fi
CHAPTARRNG_RELEASE_READY=true BOOKSHELFNG_RELEASE_READY=true bash "$readiness_gate"
grep -Fq 'name: Dispatch personal-account BookshelfNG GHCR bridge' "$release_workflow"
grep -Fq 'repositories: bookshelfng-ghcr-release-bridge' "$release_workflow"
grep -Fq 'permission-contents: write' "$release_workflow"
grep -Fq 'IMAGE_DIGEST: ${{ needs.docker.outputs.image_digest }}' "$release_workflow"
grep -Fq 'value: ${{ jobs.build-and-push.outputs.image_digest }}' "$docker_workflow"
grep -Fq 'publish_legacy_aliases="${PUBLISH_LEGACY_ALIASES:-false}"' "$script"
grep -Fq 'legacy_bridge_enabled:' "$package_workflow"
grep -Fq 'REQUESTED_LEGACY_BRIDGE: ${{ inputs.legacy_bridge_enabled }}' "$package_workflow"
grep -Fq 'MIGRATION_BRIDGE_GATE: ${{ vars.CHAPTARRNG_MIGRATION_BRIDGE_ENABLED || ' "$package_workflow"
grep -Fq 'legacy_bridge_enabled=false' "$package_workflow"
grep -Fq 'if [[ "${REQUESTED_LEGACY_BRIDGE,,}" == true && "${MIGRATION_BRIDGE_GATE,,}" == true && "${BOOKSHELFNG_RELEASE_READY,,}" == true ]]' "$package_workflow"
grep -Fq 'needs.release-target.outputs.legacy_bridge_enabled' "$package_workflow"
grep -Fq 'LEGACY_BRIDGE_ENABLED: ${{ needs.release-target.outputs.legacy_bridge_enabled }}' "$package_workflow"
grep -Fq 'pkg_bases=(chaptarrng-bin)' "$package_workflow"
grep -Fq "needs.credentials.outputs.bookshelfng_copr == 'true' && needs.release-target.outputs.legacy_bridge_enabled == 'true'" "$package_workflow"
grep -Fq 'run: ./packaging/scripts/publish-chocolatey-packages.ps1' "$package_workflow"
grep -Fq "LEGACY_BRIDGE_ENABLED -ne 'true'" "$chocolatey_publish_script"
if grep -Fq 'ghcr.io/${{ github.repository }}' "$docker_workflow"; then echo 'The published GHCR identity must not be derived from the transferred repository owner.' >&2; exit 1; fi

run_aliases() {
  local configured="$1" publish_ghcr="${2:-false}" publish_legacy="${3:-false}" bad_target="${4:-}" bad_dockerhub_version="${5:-false}"
  : > "$work/publish.log"; : > "$work/skopeo.log"; : > "$work/skopeo-state.tsv"; : > "$work/summary.md"
  PATH="$work/bin:$PATH" HOME="$work/home" RUNNER_TEMP="$work/runner" GITHUB_STEP_SUMMARY="$work/summary.md" \
    PUBLISH_LOG="$work/publish.log" SKOPEO_LOG="$work/skopeo.log" SKOPEO_STATE="$work/skopeo-state.tsv" \
    SOURCE_IMAGE="$source" EXPECTED_DIGEST="$digest" MOCK_STANDARD_DIGEST="$digest" MOCK_STANDARD_MANIFEST="$manifest" \
    MOCK_WRONG_MANIFEST="$wrong_manifest" MOCK_WRONG_DIGEST="$wrong_digest" MOCK_BAD_TARGET="$bad_target" \
    MOCK_BAD_DOCKERHUB_VERSION="$bad_dockerhub_version" VERSION=1.2.3 DOCKERHUB_CONFIGURED="$configured" \
    PUBLISH_GHCR_ALIASES="$publish_ghcr" PUBLISH_LEGACY_ALIASES="$publish_legacy" bash "$script"
}

run_aliases false false false
[[ ! -s "$work/publish.log" && ! -s "$work/skopeo.log" ]]
grep -Fq 'GHCR legacy aliases: skipped because the BookshelfNG migration bridge is disabled.' "$work/summary.md"
grep -Fq 'Docker Hub image aliases: skipped' "$work/summary.md"

run_aliases false true true
for alias in latest main master hardcover softcover; do printf '%s\t%s\n' "ghcr.io/snapetech/bookshelfng:$alias" "$source"; done > "$work/expected-ghcr.log"
cmp "$work/expected-ghcr.log" "$work/publish.log"
grep -Fq 'Every BookshelfNG alias is published from the same standard ChaptarrNG image digest.' "$work/summary.md"

run_aliases true false true
[[ "$(wc -l < "$work/publish.log")" -eq 4 ]]
[[ "$(wc -l < "$work/skopeo.log")" -eq 12 ]]
for alias in latest main master stable; do grep -Fq "docker.io/snapetech/chaptarrng:$alias" "$work/publish.log"; done
for alias in latest main master hardcover softcover; do grep -Fq "docker://docker.io/snapetech/bookshelfng:$alias" "$work/skopeo.log"; done
grep -Fq "published with the verified standard image digest $digest" "$work/summary.md"

if run_aliases false true true ghcr.io/snapetech/bookshelfng:hardcover; then echo 'The publisher accepted a mismatched legacy alias digest.' >&2; exit 1; fi
if run_aliases true false false '' true; then echo 'The publisher accepted a mismatched Docker Hub standard digest.' >&2; exit 1; fi

echo 'Single standard image alias contract tests passed.'
