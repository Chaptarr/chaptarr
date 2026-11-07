#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Preview and move an author's existing ebook or audiobook files through the ChaptarrNG API.

Usage:
  scripts/chaptarr-move-media.sh --url URL --api-key KEY --author-id ID \
    --format ebook|audiobook --destination PATH [--yes]

The script always prints the server preview. Pass --yes to skip the final prompt.
Requires curl and jq.
EOF
}

app_url=''
api_key=''
author_id=''
format=''
destination=''
assume_yes=false

while (($#)); do
  case "$1" in
    --url)
      app_url=${2-}
      shift 2
      ;;
    --api-key)
      api_key=${2-}
      shift 2
      ;;
    --author-id)
      author_id=${2-}
      shift 2
      ;;
    --format)
      format=${2-}
      shift 2
      ;;
    --destination)
      destination=${2-}
      shift 2
      ;;
    --yes)
      assume_yes=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'Unknown option: %s\n' "$1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$app_url" || -z "$api_key" || -z "$author_id" || -z "$format" || -z "$destination" ]]; then
  usage >&2
  exit 2
fi

if [[ ! "$author_id" =~ ^[1-9][0-9]*$ ]]; then
  printf 'Author ID must be a positive number without leading zeroes.\n' >&2
  exit 2
fi

if [[ "$format" != "ebook" && "$format" != "audiobook" ]]; then
  printf 'Format must be ebook or audiobook.\n' >&2
  exit 2
fi

if ! command -v curl >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
  printf 'This command requires curl and jq.\n' >&2
  exit 2
fi

post_json() {
  local endpoint=$1
  local payload=$2
  local response

  if ! response=$(curl --silent --show-error --fail-with-body \
    --request POST \
    --header "X-Api-Key: ${api_key}" \
    --header 'Content-Type: application/json' \
    --data "$payload" \
    "$endpoint"); then
    printf '%s\n' "$response" >&2
    return 1
  fi

  printf '%s' "$response"
}

app_url=${app_url%/}
api_url="${app_url}/api/v1/author/media-move"
request=$(jq -n \
  --argjson id "$author_id" \
  --arg format "$format" \
  --arg destinationPath "$destination" \
  '{id: $id, format: $format, destinationPath: $destinationPath}')

preview=$(post_json "${api_url}/preview" "$request")

printf 'Move preview for author %s (%s)\n' "$author_id" "$format"
printf 'Source:      %s\n' "$(jq -r '.sourcePath' <<<"$preview")"
printf 'Destination: %s\n' "$(jq -r '.destinationPath' <<<"$preview")"
printf 'Media files: %s\n' "$(jq -r '.mediaFileCount' <<<"$preview")"
printf 'Sidecars:    %s\n' "$(jq -r '.sidecarFileCount' <<<"$preview")"
printf 'Total size:  %s bytes\n' "$(jq -r '.totalSize' <<<"$preview")"
printf 'Missing:     %s\n' "$(jq -r '.missingFileCount' <<<"$preview")"
printf 'Copy space:  %s bytes required; %s bytes available\n' \
  "$(jq -r '.requiredCopyBytes' <<<"$preview")" \
  "$(jq -r '.availableSpace // "unknown"' <<<"$preview")"

jq -r '.files[] | "  [\(.status)] \(.sourcePath) -> \(.destinationPath)"' <<<"$preview"
jq -r '.warnings[]? | "Warning: \(.)"' <<<"$preview"
jq -r '.conflicts[]? | "Conflict: \(.)"' <<<"$preview"

if [[ $(jq -r '.canMove' <<<"$preview") != "true" ]]; then
  printf 'The preview has no safe move to start. Resolve conflicts or missing files first.\n' >&2
  exit 1
fi

if [[ "$assume_yes" != true ]]; then
  read -r -p 'Queue this move? [y/N] ' answer
  if [[ ! "${answer,,}" =~ ^(y|yes)$ ]]; then
    printf 'Move cancelled.\n'
    exit 0
  fi
fi

start_request=$(jq \
  --arg previewToken "$(jq -r '.previewToken' <<<"$preview")" \
  '. + {previewToken: $previewToken}' <<<"$request")

command=$(post_json "${api_url}/start" "$start_request")

printf 'Move queued as command %s. Check progress with:\n' "$(jq -r '.id' <<<"$command")"
printf "curl --header \"X-Api-Key: \\$CHAPTARR_API_KEY\" \"%s/api/v1/command/%s\"\n" \\
  "$app_url" "$(jq -r '.id' <<<"$command")"
