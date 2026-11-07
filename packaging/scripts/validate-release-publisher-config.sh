#!/usr/bin/env bash
set -euo pipefail

validate_discord_webhook() {
  local webhook="${DISCORD_RELEASE_WEBHOOK:-}"
  [[ "$webhook" =~ ^https://(discord\.com|discordapp\.com)/api/webhooks/[0-9]+/[A-Za-z0-9._~-]+/?(\?[A-Za-z0-9._~=%&-]*)?$ ]]
}

mode="${1:-publishers}"
case "$mode" in
  discord)
    if validate_discord_webhook; then
      echo 'Discord release webhook URL format is valid.'
      exit 0
    fi
    echo 'DISCORD_RELEASE_WEBHOOK must be a valid HTTPS Discord webhook URL.' >&2
    exit 1
    ;;
  publishers) ;;
  *)
    echo 'Usage: validate-release-publisher-config.sh [publishers|discord]' >&2
    exit 2
    ;;
esac

: "${GITHUB_OUTPUT:?GITHUB_OUTPUT must name a writable output file}"
: "${GITHUB_STEP_SUMMARY:?GITHUB_STEP_SUMMARY must name a writable summary file}"

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-publisher-preflight.XXXXXX")"
trap 'rm -rf -- "$tmp_dir"' EXIT

aur=false
copr=false
bookshelfng_copr=false
ppa=false
ppa_series_json='[{"codename":"noble","version":"24.04"}]'
chocolatey=false

skip() {
  printf '%s\n' "- **$1:** skipped. $2" >> "$GITHUB_STEP_SUMMARY"
}

if [[ -z "${AUR_SSH_KEY:-}" ]]; then
  skip AUR 'AUR_SSH_KEY is unset; the AUR publisher will not run.'
else
  aur_key="$tmp_dir/aur-key"
  printf '%s\n' "$AUR_SSH_KEY" > "$aur_key"
  chmod 0600 "$aur_key"
  if ssh-keygen -y -P '' -f "$aur_key" >/dev/null 2>&1; then
    aur=true
  else
    skip AUR 'AUR_SSH_KEY is not a usable unencrypted SSH private key; update the Actions secret.'
  fi
fi

if [[ "${COPR_WEBHOOK_URL:-}" =~ ^https://copr\.fedorainfracloud\.org/webhooks/custom/[0-9]+/[A-Za-z0-9._~-]+/chaptarrng/?$ ]]; then
  copr=true
else
  skip COPR 'COPR_WEBHOOK_URL is not a package-scoped COPR custom webhook URL for chaptarrng; replace the Actions secret.'
fi

if [[ "${BOOKSHELFNG_COPR_WEBHOOK_URL:-}" =~ ^https://copr\.fedorainfracloud\.org/webhooks/custom/[0-9]+/[A-Za-z0-9._~-]+/bookshelfng/?$ ]]; then
  bookshelfng_copr=true
else
  skip 'BookshelfNG COPR' 'BOOKSHELFNG_COPR_WEBHOOK_URL is not a package-scoped COPR custom webhook URL for bookshelfng; replace the Actions secret.'
fi

ppa_target="${LAUNCHPAD_PPA:-}"
ppa_series="${LAUNCHPAD_SERIES:-}"
if [[ -z "${GPG_PRIVATE_KEY:-}" || -z "$ppa_target" || -z "$ppa_series" ]]; then
  skip PPA 'GPG_PRIVATE_KEY, LAUNCHPAD_PPA, and LAUNCHPAD_SERIES are required; the PPA publisher will not run.'
elif [[ ! "$ppa_target" =~ ^ppa:~?[A-Za-z0-9][A-Za-z0-9.+_-]*/[A-Za-z0-9][A-Za-z0-9.+_-]*$ ]]; then
  skip PPA 'LAUNCHPAD_PPA must use the form ppa:<owner>/<archive>; update the Actions secret or variable.'
elif [[ ! "$ppa_series" =~ ^[a-z][a-z0-9]+(,[a-z][a-z0-9]+)*$ ]]; then
  skip PPA 'LAUNCHPAD_SERIES must be a comma-separated list of Ubuntu series codenames enabled on the configured PPA.'
else
  gpg_home="$tmp_dir/gnupg"
  mkdir -m 0700 "$gpg_home"
  if ! printf '%s\n' "$GPG_PRIVATE_KEY" | GNUPGHOME="$gpg_home" gpg --batch --import >/dev/null 2>&1 || \
    ! GNUPGHOME="$gpg_home" gpg --batch --with-colons --list-secret-keys 2>/dev/null | awk -F: '$1 == "sec" { found = 1 } END { exit !found }'; then
    skip PPA 'GPG_PRIVATE_KEY is not an importable private signing key; update the Actions secret.'
  else
    signing_fingerprint="$(GNUPGHOME="$gpg_home" gpg --batch --with-colons --list-secret-keys 2>/dev/null | awk -F: '$1 == "fpr" { print $10; exit }')"
    printf 'ChaptarrNG PPA signing preflight\n' > "$tmp_dir/gpg-check.txt"
    if ! [[ "$signing_fingerprint" =~ ^[A-Fa-f0-9]{40,64}$ ]] || \
      ! GNUPGHOME="$gpg_home" gpg --batch --yes --local-user "$signing_fingerprint" \
        --detach-sign --output "$tmp_dir/gpg-check.sig" "$tmp_dir/gpg-check.txt" >/dev/null 2>&1; then
      skip PPA 'GPG_PRIVATE_KEY cannot sign without interactive input; provide an unattended signing key.'
    else
      shorthand="${ppa_target#ppa:}"
      owner="${shorthand%%/*}"
      archive="${shorthand#*/}"
      owner="${owner#\~}"
      archive_api="https://api.launchpad.net/1.0/~${owner}/%2Barchive/ubuntu/${archive}"
      if archive_data="$(curl --fail --silent --show-error --max-time 20 "$archive_api" 2>/dev/null)" && \
        jq -e --arg archive "$archive" '.name == $archive and (.owner_link | type == "string")' <<< "$archive_data" >/dev/null 2>&1; then
        IFS=',' read -r -a series_list <<< "$ppa_series"
        ppa_series_json='['
        series_valid=true
        declare -A seen_series=()
        for series in "${series_list[@]}"; do
          if [[ -n "${seen_series[$series]:-}" ]]; then
            series_valid=false
            break
          fi
          seen_series["$series"]=true
          series_api="https://api.launchpad.net/1.0/ubuntu/${series}"
          if ! series_data="$(curl --fail --silent --show-error --max-time 20 "$series_api" 2>/dev/null)"; then
            series_valid=false
            break
          fi
          if ! jq -e '.active == true and (.version | type == "string")' <<< "$series_data" >/dev/null 2>&1; then
            series_valid=false
            break
          fi
          series_version="$(jq -r '.version' <<< "$series_data")"
          if [[ ! "$series_version" =~ ^[0-9]+(\.[0-9]+){1,2}$ ]]; then
            series_valid=false
            break
          fi
          ppa_series_json+="{\"codename\":\"${series}\",\"version\":\"${series_version}\"},"
        done
        if [[ "$series_valid" == true ]]; then
          ppa_series_json="${ppa_series_json%,}]"
          ppa=true
        else
          ppa_series_json='[{"codename":"noble","version":"24.04"}]'
          skip PPA 'A configured Launchpad series is duplicated, inactive, or could not be found through the public API; use active codenames enabled on the PPA.'
        fi
      else
        skip PPA 'The configured Launchpad PPA could not be found through its public API; check its owner and archive name.'
      fi
    fi
  fi
fi

if [[ -z "${CHOCOLATEY_API_KEY:-}" ]]; then
  skip Chocolatey 'CHOCOLATEY_API_KEY is unset; the Chocolatey publisher will not run.'
elif [[ "$CHOCOLATEY_API_KEY" =~ [[:space:]] ]]; then
  skip Chocolatey 'CHOCOLATEY_API_KEY contains whitespace; update the Actions secret.'
else
  chocolatey=true
fi

{
  printf 'aur=%s\n' "$aur"
  printf 'copr=%s\n' "$copr"
  printf 'bookshelfng_copr=%s\n' "$bookshelfng_copr"
  printf 'ppa=%s\n' "$ppa"
  printf 'ppa_series=%s\n' "$ppa_series_json"
  printf 'chocolatey=%s\n' "$chocolatey"
} >> "$GITHUB_OUTPUT"
