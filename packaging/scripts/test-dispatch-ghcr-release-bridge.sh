#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-ghcr-bridge-dispatch.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/runner"

cat > "$work/bin/gh" <<'MOCK_GH'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == api && "$2" == --method && "$3" == POST && "$4" == --input ]]
payload="$5"
[[ "$6" == repos/snapetech/bookshelfng-ghcr-release-bridge/dispatches ]]
jq --arg expected_digest "$EXPECTED_DIGEST" -e '.event_type == "chaptarrng-release" and
  .client_payload.version == "1.2.3" and
  .client_payload.source_sha == "0123456789abcdef0123456789abcdef01234567" and
  .client_payload.image_digest == $expected_digest and
  .client_payload.source_repository == "snapetech-org/chaptarrng"' "$payload" >/dev/null
cp "$payload" "$CAPTURED_PAYLOAD"
MOCK_GH
chmod 0700 "$work/bin/gh"

valid_digest="sha256:$(printf x | sha256sum | cut -d ' ' -f1)"
PATH="$work/bin:$PATH" RUNNER_TEMP="$work/runner" GITHUB_STEP_SUMMARY="$work/summary.md" \
  CAPTURED_PAYLOAD="$work/payload.json" EXPECTED_DIGEST="$valid_digest" GH_TOKEN='test-token' \
  VERSION=1.2.3 SOURCE_SHA=0123456789abcdef0123456789abcdef01234567 IMAGE_DIGEST="$valid_digest" \
  SOURCE_REPOSITORY=snapetech-org/chaptarrng bash "$repo_root/packaging/scripts/dispatch-ghcr-release-bridge.sh"
grep -Fq "Dispatched BookshelfNG GHCR alias bridge for ChaptarrNG 1.2.3 using standard image digest $valid_digest" "$work/summary.md"

for input in invalid_version invalid_sha invalid_digest invalid_repository; do
  version=1.2.3; sha=0123456789abcdef0123456789abcdef01234567; digest="$valid_digest"; repository=snapetech-org/chaptarrng
  case "$input" in
    invalid_version) version=1.2 ;;
    invalid_sha) sha=not-a-commit ;;
    invalid_digest) digest=latest ;;
    invalid_repository) repository=attacker/repo ;;
  esac
  if PATH="$work/bin:$PATH" RUNNER_TEMP="$work/runner" GITHUB_STEP_SUMMARY="$work/summary.md" GH_TOKEN='test-token' \
    VERSION="$version" SOURCE_SHA="$sha" IMAGE_DIGEST="$digest" SOURCE_REPOSITORY="$repository" \
    bash "$repo_root/packaging/scripts/dispatch-ghcr-release-bridge.sh" >/dev/null 2>&1; then
    echo "The dispatch accepted $input." >&2; exit 1
  fi
done
if grep -Eq 'CHAPTARRNG_AUTOMATION_APP_PRIVATE_KEY|GH_TOKEN|token' "$work/payload.json"; then echo 'The dispatch payload contains credential material.' >&2; exit 1; fi
echo 'Single standard image bridge dispatch tests passed.'
