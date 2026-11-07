#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Queue a catalog match retry for all unmapped ChaptarrNG library files.

Usage:
  chaptarr-retry-unmapped.sh --url URL [--api-key KEY] [--wait] [--json]

Environment:
  CHAPTARR_URL       ChaptarrNG base URL, including any URL base path
  CHAPTARR_API_KEY   API key (prefer this over --api-key)
  BOOKSHELF_URL      Legacy base URL fallback for migrated BookshelfNG scripts
  BOOKSHELF_API_KEY  Legacy API key fallback for migrated BookshelfNG scripts

Options:
  --url URL       ChaptarrNG base URL; may end in /api/v1
  --api-key KEY   API key sent in the X-Api-Key header
  --wait          Wait for completion and print the final command result
  --json          Print the API response as JSON
  -h, --help      Show this help
USAGE
}

base_url="${CHAPTARR_URL:-${BOOKSHELF_URL:-}}"
api_key="${CHAPTARR_API_KEY:-${BOOKSHELF_API_KEY:-}}"
wait_for_completion=false
json_output=false

while (($#)); do
  case "$1" in
    --url)
      (($# >= 2)) || { usage >&2; exit 2; }
      base_url="$2"
      shift 2
      ;;
    --api-key)
      (($# >= 2)) || { usage >&2; exit 2; }
      api_key="$2"
      shift 2
      ;;
    --wait)
      wait_for_completion=true
      shift
      ;;
    --json)
      json_output=true
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

if [[ -z "$base_url" || -z "$api_key" ]]; then
  printf 'Set CHAPTARR_URL and CHAPTARR_API_KEY (or legacy BOOKSHELF_URL and BOOKSHELF_API_KEY), or pass --url and --api-key.\n' >&2
  exit 2
fi

if ! command -v curl >/dev/null 2>&1; then
  printf 'curl is required.\n' >&2
  exit 2
fi

if [[ "$wait_for_completion" == true ]] && ! command -v jq >/dev/null 2>&1; then
  printf 'jq is required with --wait.\n' >&2
  exit 2
fi

base_url="${base_url%/}"
if [[ "$base_url" != */api/v1 ]]; then
  api_url="$base_url/api/v1"
else
  api_url="$base_url"
fi

command_response="$(curl --fail --silent --show-error \
  --request POST \
  --header "X-Api-Key: $api_key" \
  --header 'Content-Type: application/json' \
  --data '{"name":"RetryUnmappedMatch","mediaType":"all","unmappedFiles":{"scope":"all"}}' \
  "$api_url/command")"

if [[ "$wait_for_completion" != true ]]; then
  if [[ "$json_output" == true ]]; then
    printf '%s\n' "$command_response"
  elif command -v jq >/dev/null 2>&1; then
    command_id="$(jq -r '.id // empty' <<<"$command_response")"
    if [[ -n "$command_id" ]]; then
      printf 'Queued unmapped-file match retry as command %s.\n' "$command_id"
    else
      printf '%s\n' "$command_response"
    fi
  else
    printf 'Queued unmapped-file match retry. API response: %s\n' "$command_response"
  fi
  exit 0
fi

command_id="$(jq -er '.id // empty' <<<"$command_response")"
if [[ -z "$command_id" ]]; then
  printf 'The API accepted the request but returned no command id: %s\n' "$command_response" >&2
  exit 1
fi

while true; do
  command_response="$(curl --fail --silent --show-error \
    --header "X-Api-Key: $api_key" \
    "$api_url/command/$command_id")"
  status="$(jq -r '.status // empty' <<<"$command_response")"

  case "$status" in
    completed|failed|aborted|cancelled)
      break
      ;;
  esac

  sleep 2
done

if [[ "$json_output" == true ]]; then
  printf '%s\n' "$command_response"
else
  printf 'Unmapped-file match retry %s.\n' "$status"
  jq -r '.message // .result // empty' <<<"$command_response"
fi

[[ "$status" == completed ]]
