#!/usr/bin/env bash
set -euo pipefail

: "${SOURCE_IMAGE:?SOURCE_IMAGE must identify the pushed standard ChaptarrNG image by digest}"
: "${EXPECTED_DIGEST:?EXPECTED_DIGEST must contain the standard image build output digest}"
: "${VERSION:?VERSION must contain the ChaptarrNG package version}"
: "${GITHUB_STEP_SUMMARY:?GITHUB_STEP_SUMMARY must point to the workflow summary file}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required for alias metadata files}"

if [[ ! "$EXPECTED_DIGEST" =~ ^sha256:[a-f0-9]{64}$ || "$SOURCE_IMAGE" != *"@$EXPECTED_DIGEST" ]]; then
  echo "SOURCE_IMAGE must be pinned to the valid EXPECTED_DIGEST." >&2
  exit 2
fi

summary="$GITHUB_STEP_SUMMARY"
metadata_index=0
failed=0
publish_ghcr_aliases="${PUBLISH_GHCR_ALIASES:-false}"
publish_legacy_aliases="${PUBLISH_LEGACY_ALIASES:-false}"
for setting in PUBLISH_GHCR_ALIASES PUBLISH_LEGACY_ALIASES; do
  value="${!setting:-false}"
  if [[ "$value" != "true" && "$value" != "false" ]]; then
    echo "$setting must be true or false." >&2
    exit 2
  fi
done

publish_alias() {
  local target="$1"
  local source="$2"
  local expected_digest="$3"
  local metadata="$RUNNER_TEMP/chaptarrng-alias-$metadata_index.json"
  metadata_index=$((metadata_index + 1))

  if [[ "$source" != *"@$expected_digest" ]]; then
    echo "- $target: source is not pinned to expected digest $expected_digest." >> "$summary"
    failed=1
    return
  fi
  if ! docker buildx imagetools create --tag "$target" --metadata-file "$metadata" "$source"; then
    echo "- $target: publish failed." >> "$summary"
    failed=1
    return
  fi

  local actual_digest
  if ! actual_digest="$(python3 - "$metadata" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    data = json.load(stream)
print(data.get("containerimage.descriptor", {}).get("digest", ""))
PY
  )"; then
    echo "- $target: could not read the published manifest digest." >> "$summary"
    failed=1
    return
  fi
  if [[ "$actual_digest" != "$expected_digest" ]]; then
    echo "- $target: published manifest digest $actual_digest; expected $expected_digest." >> "$summary"
    failed=1
    return
  fi
  echo "- $target: published with the verified standard image digest $expected_digest." >> "$summary"
}

publish_dockerhub_alias() {
  local target="$1"
  local source="docker://$SOURCE_IMAGE"
  local destination="docker://$target"
  local actual_digest
  if ! skopeo --authfile "$HOME/.docker/config.json" copy --all --preserve-digests "$source" "$destination"; then
    echo "- $target: standard image copy failed." >> "$summary"
    failed=1
    return 1
  fi
  if ! actual_digest="$(skopeo --authfile "$HOME/.docker/config.json" inspect --raw "$destination" | sha256sum | cut -d ' ' -f1)"; then
    echo "- $target: copied manifest could not be inspected." >> "$summary"
    failed=1
    return 1
  fi
  if [[ "sha256:$actual_digest" != "$EXPECTED_DIGEST" ]]; then
    echo "- $target: copied manifest digest was sha256:$actual_digest; expected $EXPECTED_DIGEST." >> "$summary"
    failed=1
    return 1
  fi
  echo "- $target: published with the verified standard image digest $EXPECTED_DIGEST." >> "$summary"
}

{
  echo "### Legacy image aliases"
  echo
  echo "Every BookshelfNG alias is published from the same standard ChaptarrNG image digest. Provider mode is selected from migrated settings or explicit runtime configuration."
} >> "$summary"

if [[ "$publish_legacy_aliases" == "true" && "$publish_ghcr_aliases" == "true" ]]; then
  for alias in latest main master hardcover softcover; do
    publish_alias "ghcr.io/snapetech/bookshelfng:$alias" "$SOURCE_IMAGE" "$EXPECTED_DIGEST"
  done
else
  if [[ "$publish_legacy_aliases" != "true" ]]; then
    echo '- GHCR legacy aliases: skipped because the BookshelfNG migration bridge is disabled.' >> "$summary"
  else
    echo '- GHCR legacy aliases: skipped because GHCR alias publication was disabled for this invocation.' >> "$summary"
  fi
fi

if [[ "${DOCKERHUB_CONFIGURED:-false}" == "true" ]]; then
  version_target="docker.io/snapetech/chaptarrng:$VERSION"
  if publish_dockerhub_alias "$version_target"; then
    for alias in latest main master stable; do
      publish_alias "docker.io/snapetech/chaptarrng:$alias" "$version_target@$EXPECTED_DIGEST" "$EXPECTED_DIGEST"
    done
    if [[ "$publish_legacy_aliases" == "true" ]]; then
      for alias in latest main master hardcover softcover; do
        publish_dockerhub_alias "docker.io/snapetech/bookshelfng:$alias"
      done
    else
      echo '- Docker Hub BookshelfNG aliases: skipped because the migration bridge is disabled.' >> "$summary"
    fi
  else
    failed=1
  fi
else
  echo '- Docker Hub image aliases: skipped because publisher credentials are not configured.' >> "$summary"
fi

if (( failed != 0 )); then
  exit 1
fi
