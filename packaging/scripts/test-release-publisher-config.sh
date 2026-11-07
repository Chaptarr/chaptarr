#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
validator="$script_dir/validate-release-publisher-config.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-publisher-validation-test.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin"

gpg_home="$work/gpg-home"
mkdir -m 0700 "$gpg_home"
GNUPGHOME="$gpg_home" gpg --batch --passphrase '' --quick-generate-key \
  'ChaptarrNG workflow test <workflow-test@example.invalid>' ed25519 sign 0 >/dev/null 2>&1
gpg_key="$(GNUPGHOME="$gpg_home" gpg --batch --armor --export-secret-keys workflow-test@example.invalid)"

cat > "$work/bin/ssh-keygen" <<'MOCK_SSH_KEYGEN'
#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == -y && "$2" == -P && "$3" == '' && "$4" == -f && -s "$5" ]]
grep -Fxq 'valid-test-key' "$5"
MOCK_SSH_KEYGEN
chmod 0700 "$work/bin/ssh-keygen"

cat > "$work/bin/curl" <<'MOCK_CURL'
#!/usr/bin/env bash
set -euo pipefail
url="${*: -1}"
case "$url" in
  https://api.launchpad.net/1.0/ubuntu/noble)
    printf '%s\n' '{"name":"noble","version":"24.04","active":true}'
    ;;
  https://api.launchpad.net/1.0/ubuntu/jammy)
    printf '%s\n' '{"name":"jammy","version":"22.04","active":true}'
    ;;
  https://api.launchpad.net/1.0/ubuntu/obsolete)
    printf '%s\n' '{"name":"obsolete","version":"18.04","active":false}'
    ;;
  *)
    [[ "$url" == "${EXPECTED_LAUNCHPAD_API_URL:-}" ]]
    printf '%s\n' '{"name":"bookshelfng","owner_link":"https://api.launchpad.net/1.0/~slskdn"}'
    ;;
esac
[[ "${LAUNCHPAD_LOOKUP_FAIL:-false}" != true ]]
MOCK_CURL
chmod 0700 "$work/bin/curl"

run_publishers() {
  local name="$1"
  shift
  local output="$work/$name.output"
  local summary="$work/$name.summary"
  : > "$output"
  : > "$summary"
  env -i PATH="$work/bin:$PATH" TMPDIR="${TMPDIR:-/tmp}" GITHUB_OUTPUT="$output" GITHUB_STEP_SUMMARY="$summary" "$@" bash "$validator" publishers
}

run_publishers valid EXPECTED_LAUNCHPAD_API_URL=https://api.launchpad.net/1.0/~slskdn/%2Barchive/ubuntu/bookshelfng GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES=noble,jammy AUR_SSH_KEY=valid-test-key COPR_WEBHOOK_URL=https://copr.fedorainfracloud.org/webhooks/custom/1234/token/chaptarrng/ BOOKSHELFNG_COPR_WEBHOOK_URL=https://copr.fedorainfracloud.org/webhooks/custom/5678/token/bookshelfng/ CHOCOLATEY_API_KEY=valid-test-key
grep -Fxq 'aur=true' "$work/valid.output"
grep -Fxq 'copr=true' "$work/valid.output"
grep -Fxq 'bookshelfng_copr=true' "$work/valid.output"
grep -Fxq 'ppa=true' "$work/valid.output"
grep -Fxq 'ppa_series=[{"codename":"noble","version":"24.04"},{"codename":"jammy","version":"22.04"}]' "$work/valid.output"
grep -Fxq 'chocolatey=true' "$work/valid.output"
[[ ! -s "$work/valid.summary" ]]

run_publishers invalid AUR_SSH_KEY=invalid-test-key COPR_WEBHOOK_URL=https://example.invalid/custom/secret/chaptarrng/ BOOKSHELFNG_COPR_WEBHOOK_URL=https://copr.fedorainfracloud.org/webhooks/custom/1234/secret/other/ CHOCOLATEY_API_KEY='contains whitespace'
grep -Fxq 'aur=false' "$work/invalid.output"
grep -Fxq 'copr=false' "$work/invalid.output"
grep -Fxq 'bookshelfng_copr=false' "$work/invalid.output"
grep -Fxq 'ppa=false' "$work/invalid.output"
grep -Fxq 'chocolatey=false' "$work/invalid.output"
grep -Fq 'package-scoped COPR custom webhook URL for chaptarrng' "$work/invalid.summary"
grep -Fq 'package-scoped COPR custom webhook URL for bookshelfng' "$work/invalid.summary"
grep -Fq 'CHOCOLATEY_API_KEY contains whitespace' "$work/invalid.summary"
if grep -Fq 'invalid-test-key' "$work/invalid.summary" ||
  grep -Fq 'example.invalid' "$work/invalid.summary" ||
  grep -Fq '/1234/secret/other/' "$work/invalid.summary"; then
  echo 'Publisher preflight leaked a secret value into the workflow summary.' >&2
  exit 1
fi

run_publishers invalid-ppa GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=not-a-ppa LAUNCHPAD_SERIES=noble
grep -Fxq 'ppa=false' "$work/invalid-ppa.output"
grep -Fq 'LAUNCHPAD_PPA must use the form' "$work/invalid-ppa.summary"

run_publishers invalid-gpg GPG_PRIVATE_KEY='not a private key' LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES=noble
grep -Fxq 'ppa=false' "$work/invalid-gpg.output"
grep -Fq 'GPG_PRIVATE_KEY is not an importable' "$work/invalid-gpg.summary"

run_publishers missing-ppa GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES=noble EXPECTED_LAUNCHPAD_API_URL=https://api.launchpad.net/1.0/~slskdn/%2Barchive/ubuntu/bookshelfng LAUNCHPAD_LOOKUP_FAIL=true
grep -Fxq 'ppa=false' "$work/missing-ppa.output"
grep -Fq 'could not be found through its public API' "$work/missing-ppa.summary"
if grep -Fq 'PRIVATE KEY' "$work/missing-ppa.summary"; then
  echo 'Publisher preflight exposed private-key material in the workflow summary.' >&2
  exit 1
fi

run_publishers invalid-series GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES='noble;curl'
grep -Fxq 'ppa=false' "$work/invalid-series.output"
grep -Fq 'comma-separated list of Ubuntu series codenames' "$work/invalid-series.summary"

run_publishers inactive-series GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES=obsolete EXPECTED_LAUNCHPAD_API_URL=https://api.launchpad.net/1.0/~slskdn/%2Barchive/ubuntu/bookshelfng
grep -Fxq 'ppa=false' "$work/inactive-series.output"
grep -Fq 'duplicated, inactive, or could not be found' "$work/inactive-series.summary"

run_publishers duplicate-series GPG_PRIVATE_KEY="$gpg_key" LAUNCHPAD_PPA=ppa:slskdn/bookshelfng LAUNCHPAD_SERIES=noble,noble EXPECTED_LAUNCHPAD_API_URL=https://api.launchpad.net/1.0/~slskdn/%2Barchive/ubuntu/bookshelfng
grep -Fxq 'ppa=false' "$work/duplicate-series.output"
grep -Fq 'duplicated, inactive, or could not be found' "$work/duplicate-series.summary"

run_publishers missing
grep -Fxq 'aur=false' "$work/missing.output"
grep -Fxq 'copr=false' "$work/missing.output"
grep -Fxq 'bookshelfng_copr=false' "$work/missing.output"
grep -Fxq 'ppa=false' "$work/missing.output"
grep -Fxq 'ppa_series=[{"codename":"noble","version":"24.04"}]' "$work/missing.output"
grep -Fxq 'chocolatey=false' "$work/missing.output"

env -i PATH="$PATH" DISCORD_RELEASE_WEBHOOK=https://discord.com/api/webhooks/123456789012345678/token bash "$validator" discord > "$work/discord-valid.output"
grep -Fq 'format is valid' "$work/discord-valid.output"

if env -i PATH="$PATH" DISCORD_RELEASE_WEBHOOK=https://example.invalid/webhook/secret bash "$validator" discord > "$work/discord-invalid.output" 2> "$work/discord-invalid.error"; then
  echo 'Publisher preflight accepted an invalid Discord webhook URL.' >&2
  exit 1
fi
if grep -Fq 'secret' "$work/discord-invalid.error"; then
  echo 'Publisher preflight leaked the Discord webhook value.' >&2
  exit 1
fi

if bash "$validator" unknown >/dev/null 2>&1; then
  echo 'Publisher preflight accepted an unknown validation mode.' >&2
  exit 1
fi

echo 'Release publisher preflight tests passed.'
