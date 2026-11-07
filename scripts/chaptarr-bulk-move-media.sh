#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Preview and move tracked ebook or audiobook files for selected authors through the ChaptarrNG API.

Usage:
  scripts/chaptarr-bulk-move-media.sh --url URL --api-key KEY \
    (--author-ids ID,ID,... | --all) --format ebook|audiobook \
    --destination-root PATH [--yes]

The script always prints the server preview. Pass --yes to skip the final prompt.
The API key is the instance's administrator-level credential. Requires curl and jq.
EOF
}

app_url=''
api_key=''
author_ids_input=''
format=''
destination_root=''
include_all=false
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
    --author-ids)
      author_ids_input=${2-}
      shift 2
      ;;
    --all)
      include_all=true
      shift
      ;;
    --format)
      format=${2-}
      shift 2
      ;;
    --destination-root)
      destination_root=${2-}
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

if [[ -z "$app_url" || -z "$api_key" || -z "$format" || -z "$destination_root" ]]; then
  usage >&2
  exit 2
fi

if [[ "$include_all" == true && -n "$author_ids_input" ]] || [[ "$include_all" != true && -z "$author_ids_input" ]]; then
  printf 'Choose exactly one of --author-ids or --all.\n' >&2
  usage >&2
  exit 2
fi

if [[ -n "$author_ids_input" && ! "$author_ids_input" =~ ^[1-9][0-9]*(,[1-9][0-9]*)*$ ]]; then
  printf 'Author IDs must be positive numbers separated by commas, without spaces.\n' >&2
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
api_url="${app_url}/api/v1/author/media-move/bulk"

if [[ "$include_all" == true ]]; then
  authors=$(curl --silent --show-error --fail-with-body \
    --header "X-Api-Key: ${api_key}" \
    "${app_url}/api/v1/author")
  author_ids=$(jq -c '[.[].id]' <<<"$authors")
else
  author_ids=$(jq -cn --arg ids "$author_ids_input" '$ids | split(",") | map(tonumber)')
fi

if [[ $(jq 'length' <<<"$author_ids") -eq 0 ]]; then
  printf 'No authors were selected for the move.\n' >&2
  exit 1
fi

request=$(jq -n \
  --argjson authorIds "$author_ids" \
  --arg format "$format" \
  --arg destinationRootPath "$destination_root" \
  '{authorIds: $authorIds, format: $format, destinationRootPath: $destinationRootPath}')

preview=$(post_json "${api_url}/preview" "$request")

printf 'Bulk move preview (%s)\n' "$format"
printf 'Destination root: %s\n' "$(jq -r '.destinationRootPath' <<<"$preview")"
printf 'Authors:          %s\n' "$(jq -r '.authorCount' <<<"$preview")"
printf 'Media files:      %s\n' "$(jq -r '.mediaFileCount' <<<"$preview")"
printf 'Sidecars:         %s\n' "$(jq -r '.sidecarFileCount' <<<"$preview")"
printf 'Total size:       %s bytes\n' "$(jq -r '.totalSize' <<<"$preview")"
printf 'Missing:          %s\n' "$(jq -r '.missingFileCount' <<<"$preview")"
printf 'Copy space:       %s bytes required; %s bytes available\n' \
  "$(jq -r '.requiredCopyBytes' <<<"$preview")" \
  "$(jq -r '.availableSpace // "unknown"' <<<"$preview")"

jq -r '.authors[] | "  \(.authorName): \(.mediaFileCount) media files, \(.sidecarFileCount) sidecars -> \(.destinationPath)"' <<<"$preview"
jq -r '.warnings[]? | "Warning: \(.)"' <<<"$preview"
jq -r '.conflicts[]? | "Conflict: \(.)"' <<<"$preview"

if [[ $(jq -r '.canMove' <<<"$preview") != "true" ]]; then
  printf 'The preview has no safe move to start. Resolve conflicts or missing files first.\n' >&2
  exit 1
fi

if [[ "$assume_yes" != true ]]; then
  read -r -p 'Queue this bulk move? [y/N] ' answer
  if [[ ! "${answer,,}" =~ ^(y|yes)$ ]]; then
    printf 'Move cancelled.\n'
    exit 0
  fi
fi

start_request=$(jq \
  --arg previewToken "$(jq -r '.previewToken' <<<"$preview")" \
  '. + {previewToken: $previewToken}' <<<"$request")

command=$(post_json "${api_url}/start" "$start_request")

printf 'Bulk move queued as command %s. Check progress with:\n' "$(jq -r '.id' <<<"$command")"
printf "curl --header \"X-Api-Key: \\$CHAPTARR_API_KEY\" \"%s/api/v1/command/%s\"\n" \\
  "$app_url" "$(jq -r '.id' <<<"$command")"
