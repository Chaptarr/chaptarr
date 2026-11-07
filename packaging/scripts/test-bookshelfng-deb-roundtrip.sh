#!/usr/bin/env bash
set -euo pipefail

old_deb="${OLD_DEB:-}"
new_deb="${NEW_DEB:-}"
task_scratch_dir="${TASK_SCRATCH_DIR:-}"

if [[ ! -s "$old_deb" || ! -s "$new_deb" || -z "$task_scratch_dir" ]]; then
  echo 'OLD_DEB, NEW_DEB, and TASK_SCRATCH_DIR are required.' >&2
  exit 2
fi
if [[ -e "$task_scratch_dir" ]]; then
  echo "Refusing to reuse existing test scratch path: $task_scratch_dir" >&2
  exit 2
fi
for command_name in dpkg dpkg-deb dpkg-query python3; do
  command -v "$command_name" >/dev/null
done

old_package="$(dpkg-deb --field "$old_deb" Package)"
old_version="$(dpkg-deb --field "$old_deb" Version)"
new_package="$(dpkg-deb --field "$new_deb" Package)"
new_version="$(dpkg-deb --field "$new_deb" Version)"
if [[ "$old_package" != bookshelfng || "$new_package" != "$old_package" ]]; then
  echo 'Both Debian packages must use the existing bookshelfng package identity.' >&2
  exit 1
fi
if ! dpkg --compare-versions "$new_version" gt "$old_version"; then
  echo "Candidate version $new_version must be newer than installed version $old_version." >&2
  exit 1
fi

mkdir -p "$task_scratch_dir"
export TASK_SCRATCH_DIR="$task_scratch_dir"
trap 'python3 -c '\''import os, shutil; shutil.rmtree(os.environ["TASK_SCRATCH_DIR"], ignore_errors=True)'\''' EXIT

root="$task_scratch_dir/root"
mock_bin="$task_scratch_dir/mock-bin"
mkdir -p "$root/var/lib/dpkg" "$root/run" "$mock_bin"
: > "$root/var/lib/dpkg/status"
printf 'inactive\n' > "$root/run/service-state"

cat > "$mock_bin/systemctl" <<'MOCK'
#!/bin/sh
printf 'systemctl %s\n' "$*" >> "$MOCK_LOG"
case "$1" in
  is-active) [ "$(cat "$MOCK_ACTIVE_FILE")" = active ] ;;
  start) printf 'active\n' > "$MOCK_ACTIVE_FILE" ;;
  stop) printf 'inactive\n' > "$MOCK_ACTIVE_FILE" ;;
  *) exit 0 ;;
esac
MOCK
cat > "$mock_bin/deb-systemd-invoke" <<'MOCK'
#!/bin/sh
action="$1"
shift
exec systemctl "$action" "$@"
MOCK
cat > "$mock_bin/systemd-sysusers" <<'MOCK'
#!/bin/sh
exit 0
MOCK
cat > "$mock_bin/systemd-tmpfiles" <<'MOCK'
#!/bin/sh
mkdir -p "$DPKG_ROOT/var/lib/bookshelfng"
MOCK
chmod 0755 "$mock_bin/systemctl" "$mock_bin/deb-systemd-invoke" \
  "$mock_bin/systemd-sysusers" "$mock_bin/systemd-tmpfiles"

export PATH="$mock_bin:$PATH"
export DPKG_ROOT="$root"
export MOCK_LOG="$task_scratch_dir/systemd.log"
export MOCK_ACTIVE_FILE="$root/run/service-state"

dpkg --root="$root" --admindir="$root/var/lib/dpkg" --instdir="$root" \
  --force-not-root --force-script-chrootless --force-depends --install "$old_deb"
printf 'operator-override=true\n' >> "$root/etc/bookshelfng/bookshelfng.env"
printf 'data-canary\n' > "$root/var/lib/bookshelfng/data-canary"
printf 'active\n' > "$MOCK_ACTIVE_FILE"

dpkg --root="$root" --admindir="$root/var/lib/dpkg" --instdir="$root" \
  --force-not-root --force-script-chrootless --force-depends \
  --install "$new_deb"
installed_version="$(dpkg-query --root="$root" --admindir="$root/var/lib/dpkg" \
  -W -f='${Version}' bookshelfng)"
test "$installed_version" = "$new_version"
grep -Fq 'operator-override=true' "$root/etc/bookshelfng/bookshelfng.env"
grep -Fq 'data-canary' "$root/var/lib/bookshelfng/data-canary"
test -x "$root/usr/lib/chaptarrng/bin/Chaptarr"
test "$(cat "$MOCK_ACTIVE_FILE")" = active

dpkg --root="$root" --admindir="$root/var/lib/dpkg" --instdir="$root" \
  --force-not-root --force-script-chrootless --force-depends \
  --force-downgrade --install "$old_deb"
installed_version="$(dpkg-query --root="$root" --admindir="$root/var/lib/dpkg" \
  -W -f='${Version}' bookshelfng)"
test "$installed_version" = "$old_version"
grep -Fq 'operator-override=true' "$root/etc/bookshelfng/bookshelfng.env"
grep -Fq 'data-canary' "$root/var/lib/bookshelfng/data-canary"
test -x "$root/usr/lib/bookshelfng/Readarr"
test "$(cat "$MOCK_ACTIVE_FILE")" = active
grep -Fq 'systemctl stop bookshelfng.service' "$MOCK_LOG"
grep -Fq 'systemctl start bookshelfng.service' "$MOCK_LOG"

echo "Debian package round-trip passed: $old_package $old_version -> $new_version -> $old_version."
echo 'dpkg dependency checks and systemd commands were mocked; database conversion was not run.'
