#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch_root="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-retry-unmapped-env.XXXXXX")"
trap 'rm -rf -- "$scratch_root"' EXIT

mkdir -p "$scratch_root/bin"
cat > "$scratch_root/bin/curl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$@" > "$CURL_CAPTURE"
printf '%s' '{"id":42}'
SH
cat > "$scratch_root/bin/jq" <<'SH'
#!/bin/sh
cat >/dev/null
printf '42\n'
SH
chmod +x "$scratch_root/bin/curl" "$scratch_root/bin/jq"

assert_request() {
  local expected_url="$1"
  local expected_key="$2"
  shift 2
  local argument
  local parsing_script_arguments=false
  local -a environment=()
  local -a script_arguments=()

  for argument in "$@"; do
    if [[ "$argument" == "--" ]]; then
      parsing_script_arguments=true
    elif [[ "$parsing_script_arguments" == true ]]; then
      script_arguments+=("$argument")
    else
      environment+=("$argument")
    fi
  done

  : > "$scratch_root/curl.args"
  env -i PATH="$scratch_root/bin:$PATH" CURL_CAPTURE="$scratch_root/curl.args" \
    "${environment[@]}" "$repo_root/scripts/chaptarr-retry-unmapped.sh" \
    "${script_arguments[@]}"

  grep -Fxq "X-Api-Key: $expected_key" "$scratch_root/curl.args"
  grep -Fxq "$expected_url" "$scratch_root/curl.args"
}

assert_request \
  'https://bookshelf.example/library/api/v1/command' \
  'legacy-test-key' \
  BOOKSHELF_URL='https://bookshelf.example/library/' BOOKSHELF_API_KEY='legacy-test-key'

assert_request \
  'https://chaptarr.example/api/v1/command' \
  'chaptarr-test-key' \
  CHAPTARR_URL='https://chaptarr.example' CHAPTARR_API_KEY='chaptarr-test-key' \
    BOOKSHELF_URL='https://bookshelf.example' BOOKSHELF_API_KEY='legacy-test-key'

assert_request \
  'https://cli.example/api/v1/command' \
  'cli-test-key' \
  CHAPTARR_URL='https://chaptarr.example' CHAPTARR_API_KEY='chaptarr-test-key' \
    BOOKSHELF_URL='https://bookshelf.example' BOOKSHELF_API_KEY='legacy-test-key' \
  -- --url 'https://cli.example' --api-key 'cli-test-key'

printf 'ChaptarrNG unmapped retry honors legacy environment variables and precedence.\n'
