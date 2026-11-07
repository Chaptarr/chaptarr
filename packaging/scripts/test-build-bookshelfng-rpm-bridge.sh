#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
test_image="fedora:44@sha256:43b29f65a41eb9c35e1cd5323e3bdf3b655c2357a9f4f1ff2f9c2798e5045d80"
candidate_version="$(sed -nE 's|^[[:space:]]*<AssemblyVersion>([0-9]+\.[0-9]+\.[0-9]+)\.0</AssemblyVersion>[[:space:]]*$|\1|p' \
  "$repo_root/src/Directory.Build.props" | head -n 1)"
if [[ ! "$candidate_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Could not derive a three-part candidate version from $repo_root/src/Directory.Build.props." >&2
  exit 1
fi
task_root="${TASK_SCRATCH_DIR:-}"
if [[ -n "$task_root" ]]; then
  if [[ -e "$task_root" ]]; then
    echo "Refusing to reuse test scratch path: $task_root" >&2
    exit 2
  fi
  mkdir -p "$(dirname "$task_root")"
  mkdir "$task_root"
else
  task_root="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-rpm-bridge-test.XXXXXX")"
fi

cleanup() {
  result=$?
  if [[ -d "$task_root" ]] && docker image inspect "$test_image" >/dev/null 2>&1; then
    docker run --rm --user 0:0 --mount "type=bind,src=$task_root,dst=/work" \
      "$test_image" chown -R "$(id -u):$(id -g)" /work >/dev/null 2>&1 || {
        echo "Could not restore ownership of RPM bridge test scratch: $task_root" >&2
        if [[ $result -eq 0 ]]; then result=1; fi
      }
  fi
  if [[ $result -eq 0 ]]; then
    echo "RPM bridge package transaction passed; evidence kept at $task_root" >&2
  else
    echo "RPM bridge package test failed; evidence kept at $task_root" >&2
    for evidence_file in "$task_root"/evidence/*; do
      [[ -f "$evidence_file" ]] || continue
      echo "--- $evidence_file ---" >&2
      tail -n 80 "$evidence_file" >&2 || true
    done
  fi
  exit "$result"
}
trap cleanup EXIT

mkdir -p "$task_root/payload/ChaptarrNG" "$task_root/evidence"
printf '%s\n' "$candidate_version" > "$task_root/evidence/candidate-version.txt"
cat > "$task_root/payload/ChaptarrNG/Chaptarr" <<'PAYLOAD'
#!/bin/sh
exit 0
PAYLOAD
chmod 0755 "$task_root/payload/ChaptarrNG/Chaptarr"
printf 'synthetic archive license\n' > "$task_root/payload/ChaptarrNG/LICENSE"
cat > "$task_root/payload/ChaptarrNG/playwright.ps1" <<'PWSH'
#!/usr/bin/env pwsh
exit 0
PWSH
mkdir -p "$task_root/payload/ChaptarrNG/.playwright/node/linux-x64"
cat > "$task_root/payload/ChaptarrNG/.playwright/node/linux-x64/node" <<'NODE'
#!/usr/bin/env node
exit 0
NODE
chmod 0755 "$task_root/payload/ChaptarrNG/playwright.ps1" \
  "$task_root/payload/ChaptarrNG/.playwright/node/linux-x64/node"
tar -czf "$task_root/ChaptarrNG-$candidate_version-linux-x64.tar.gz" \
  -C "$task_root/payload" ChaptarrNG

if ! command -v docker >/dev/null 2>&1; then
  printf '%s\n' 'Docker CLI is missing from the runner.' > "$task_root/evidence/docker-preflight.log"
  echo 'RPM bridge package test requires the Docker CLI on the runner.' >&2
  exit 127
fi
if ! docker version --format '{{.Server.Version}}' > "$task_root/evidence/docker-preflight.log" 2>&1; then
  echo 'Docker CLI is present, but its daemon is unavailable to the runner.' >&2
  exit 1
fi

docker_proxy_args=()
for proxy_name in HTTP_PROXY HTTPS_PROXY http_proxy https_proxy; do
  if [[ -n "${!proxy_name:-}" ]]; then
    docker_proxy_args+=(--env "$proxy_name=${!proxy_name}")
  fi
done

docker run --rm -i --user 0:0 \
  --env "CHAPTARRNG_CANDIDATE_VERSION=$candidate_version" "${docker_proxy_args[@]}" \
  --mount "type=bind,src=$repo_root,dst=/repo,readonly" \
  --mount "type=bind,src=$task_root,dst=/work" \
  "$test_image" sh -s > "$task_root/evidence/rpm-transaction.log" 2>&1 <<'FEDORA_TEST'
set -eu
export DNF_YUM_AUTO_YES=1
candidate_version="${CHAPTARRNG_CANDIDATE_VERSION:?candidate version is required}"
cat > /etc/yum.repos.d/fedora.repo <<'FEDORA_REPOS'
[fedora]
name=Fedora 44 - x86_64
baseurl=https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Everything/$basearch/os/
enabled=1
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-fedora-44-primary
skip_if_unavailable=0

[updates]
name=Fedora 44 - x86_64 - Updates
baseurl=https://dl.fedoraproject.org/pub/fedora/linux/updates/44/Everything/$basearch/
enabled=1
gpgcheck=1
gpgkey=file:///etc/pki/rpm-gpg/RPM-GPG-KEY-fedora-44-primary
skip_if_unavailable=0
FEDORA_REPOS
dnf -q --disablerepo='*' --enablerepo=fedora --enablerepo=updates install -y \
  expect rpm-build systemd libicu openssl-libs python3 sqlite-libs util-linux zlib \
  > /work/evidence/fedora-dependencies.log 2>&1

top=/work/rpmbuild
mkdir -p "$top/BUILD" "$top/BUILDROOT" "$top/RPMS" "$top/SOURCES" "$top/SPECS" "$top/SRPMS"
cp "/work/ChaptarrNG-$candidate_version-linux-x64.tar.gz" \
  "$top/SOURCES/ChaptarrNG-$candidate_version-linux-x64.tar.gz"
for source in /repo/packaging/legacy/rpm/*.service \
  /repo/packaging/legacy/rpm/*.sysusers /repo/packaging/legacy/rpm/*.tmpfiles; do
  cp "$source" "$top/SOURCES/"
done
cp /repo/packaging/legacy/rpm/bookshelfng.env \
  /repo/packaging/native/chaptarrng.package_info \
  /repo/packaging/legacy/scripts/bookshelfng-healthcheck \
  /repo/packaging/legacy/scripts/bookshelfng-begin \
  /repo/packaging/legacy/scripts/bookshelfng-cleanup \
  /repo/packaging/legacy/scripts/bookshelfng-rollback \
  /repo/packaging/legacy/scripts/bookshelfng-prepare \
  /repo/packaging/legacy/scripts/bookshelfng-journal \
  /repo/packaging/legacy/scripts/bookshelfng-finalize "$top/SOURCES/"
sed "s/__VERSION__/$candidate_version/g" /repo/packaging/legacy/rpm/bookshelfng.spec.in \
  > "$top/SPECS/bookshelfng.spec"

mkdir -p /work/old-payload
cat > /work/old-payload/Readarr <<'OLDAPP'
#!/bin/sh
exit 0
OLDAPP
chmod 0755 /work/old-payload/Readarr
cat > /work/old-payload/bookshelfng.service <<'OLDUNIT'
[Unit]
Description=BookshelfNG personal book library manager
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=bookshelfng
Group=bookshelfng
EnvironmentFile=-/etc/bookshelfng/bookshelfng.env
ExecStart=/usr/lib/bookshelfng/Readarr -nobrowser -data=/var/lib/bookshelfng
Restart=on-failure
RestartSec=5s

[Install]
WantedBy=multi-user.target
OLDUNIT
cat > "$top/SPECS/bookshelfng-old.spec" <<'OLDSPEC'
Name: bookshelfng
Version: 0.4.21.71
Release: 1
Summary: Synthetic installed BookshelfNG package fixture
License: GPL-3.0-only
BuildArch: x86_64

%description
Synthetic package fixture for the ChaptarrNG migration bridge transaction.

%install
install -D -m 0755 /work/old-payload/Readarr %{buildroot}/usr/lib/bookshelfng/Readarr
install -D -m 0600 /repo/packaging/legacy/rpm/bookshelfng.env %{buildroot}/etc/bookshelfng/bookshelfng.env
install -D -m 0644 /work/old-payload/bookshelfng.service %{buildroot}/usr/lib/systemd/system/bookshelfng.service
install -d %{buildroot}/usr/bin
ln -s ../lib/bookshelfng/Readarr %{buildroot}/usr/bin/bookshelfng

%files
/usr/lib/bookshelfng/Readarr
%config(noreplace) /etc/bookshelfng/bookshelfng.env
/usr/lib/systemd/system/bookshelfng.service
/usr/bin/bookshelfng
OLDSPEC

rpmbuild -bb --define "_topdir $top" "$top/SPECS/bookshelfng-old.spec"
rpmbuild -bb --define "_topdir $top" "$top/SPECS/bookshelfng.spec"
old_rpm="$top/RPMS/x86_64/bookshelfng-0.4.21.71-1.x86_64.rpm"
new_rpm=$(find "$top/RPMS/x86_64" -maxdepth 1 -name "bookshelfng-$candidate_version-1*.rpm" -print -quit)
test -s "$old_rpm"
test -s "$new_rpm"
if rpm -qp --requires "$new_rpm" | grep -FxE '/usr/bin/(node|pwsh)'; then
  echo 'RPM auto-generated a dependency on an optional Playwright runtime helper.' >&2
  exit 1
fi

mv /usr/bin/systemctl /usr/bin/systemctl.rpm-bridge-test-real
cat > /usr/bin/systemctl <<'SYSTEMCTL'
#!/bin/sh
printf '%s\n' "$*" >> /work/evidence/systemctl.log
case "$1" in
  is-active) test -f /run/bookshelfng-rpm-test-active ;;
  stop) rm -f /run/bookshelfng-rpm-test-active ;;
  start)
    /usr/lib/chaptarrng/handoff/bookshelfng-begin
    touch /run/bookshelfng-rpm-test-active
    ;;
  show)
    case "$2" in
      --property=FragmentPath) printf '%s\n' /usr/lib/systemd/system/bookshelfng.service ;;
      --property=DropInPaths) printf '%s\n' "${RPM_TEST_DROPIN_PATHS:-}" ;;
    esac
    ;;
  *) exit 0 ;;
esac
SYSTEMCTL
chmod 0755 /usr/bin/systemctl

rpm -Uvh --nodeps "$old_rpm"
printf '# operator-override=true\n' >> /etc/bookshelfng/bookshelfng.env
mkdir -p /var/lib/bookshelfng
printf 'migration-data-canary\n' > /var/lib/bookshelfng/data-canary
touch /run/bookshelfng-rpm-test-active
export RPM_TEST_DROPIN_PATHS=/usr/lib/systemd/system/service.d/10-timeout-abort.conf
export RPM_CANDIDATE="$new_rpm"

if env BOOKSHELFNG_MIGRATION_MODE=ask dnf -q install -y "$RPM_CANDIDATE" \
    </dev/null > /work/evidence/ask-headless.log 2>&1; then
  echo 'RPM ask mode unexpectedly accepted a package upgrade without a controlling terminal.' >&2
  exit 1
fi
grep -Fq 'no terminal is available' /work/evidence/ask-headless.log
! grep -Fq '[y/N]' /work/evidence/ask-headless.log
test "$(rpm -q --qf '%{VERSION}-%{RELEASE}' bookshelfng)" = 0.4.21.71-1
test -x /usr/lib/bookshelfng/Readarr
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff
test -f /run/bookshelfng-rpm-test-active

if ! expect <<'ASKNO'
log_user 1
log_file -noappend /work/evidence/ask-no.log
set timeout 180
set prompt_seen 0
set cancellation_seen 0
spawn env BOOKSHELFNG_MIGRATION_MODE=ask dnf -q install -y $::env(RPM_CANDIDATE)
expect {
  -re {Replace BookshelfNG with ChaptarrNG now\?.*\[y/N\] } { set prompt_seen 1; send -- "n\r" }
  timeout { exit 98 }
  eof { exit 99 }
}
expect {
  -re {BookshelfNG replacement cancelled} { set cancellation_seen 1; exp_continue }
  eof {}
  timeout { exit 97 }
}
set result [wait]
if {!$prompt_seen || !$cancellation_seen || [lindex $result 3] == 0} { exit 96 }
exit 0
ASKNO
then
  echo 'RPM ask mode did not cancel cleanly after answering No.' >&2
  cat /work/evidence/ask-no.log >&2 || true
  exit 1
fi

test "$(rpm -q --qf '%{VERSION}-%{RELEASE}' bookshelfng)" = 0.4.21.71-1
test -x /usr/lib/bookshelfng/Readarr
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff
test -f /run/bookshelfng-rpm-test-active
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary
test ! -s /work/evidence/systemctl.log

if ! expect <<'ASKYES'
log_user 1
log_file -noappend /work/evidence/ask-yes.log
set timeout 180
set prompt_seen 0
spawn env BOOKSHELFNG_MIGRATION_MODE=ask dnf -q install -y $::env(RPM_CANDIDATE)
expect {
  -re {Replace BookshelfNG with ChaptarrNG now\?.*\[y/N\] } { set prompt_seen 1; send -- "y\r" }
  timeout { exit 98 }
  eof { exit 99 }
}
expect eof
set result [wait]
if {!$prompt_seen || [lindex $result 3] != 0} { exit 96 }
exit 0
ASKYES
then
  echo 'RPM ask mode did not complete after answering Yes.' >&2
  cat /work/evidence/ask-yes.log >&2 || true
  exit 1
fi

{
  printf 'package='; rpm -q bookshelfng 2>&1 || true
  printf 'pending='; test -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending && echo yes || echo no
  printf 'status='; cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status 2>&1 || true
  printf 'handoff='; test -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff && echo present || echo absent
  printf 'package-active-marker='; test -e /run/bookshelfng-package-was-active && echo yes || echo no
  printf 'systemctl calls:\n'; cat /work/evidence/systemctl.log
} > /work/evidence/post-yes-state.log 2>&1

grep -Fq '[y/N]' /work/evidence/ask-no.log
grep -Fq 'replacement cancelled' /work/evidence/ask-no.log
grep -Fq '[y/N]' /work/evidence/ask-yes.log
test "$(rpm -q --qf '%{VERSION}' bookshelfng)" = "$candidate_version"
test -x /usr/lib/chaptarrng/bin/Chaptarr
test "$(cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status)" = healthy
test ! -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff
test -x /usr/lib/chaptarrng/handoff/bookshelfng-finalize
test -f /run/bookshelfng-rpm-test-active
test ! -e /run/bookshelfng-package-was-active
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary
grep -Fq 'Environment=CHAPTARR_BOOKSHELF_HANDOFF_HEALTH_MODE=database-listener' /usr/lib/systemd/system/bookshelfng.service
grep -Fq 'BookshelfNG handoff database and local HTTP checks passed in the RPM transaction.' /work/evidence/ask-yes.log

mkdir -p /work/evidence/proc-net
printf '1\n' > /work/evidence/selinux-enforce
printf '  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n   0: 00000000:2253 00000000:0000 0A 00000000:00000000 00:00000000 00000000 1000 0 1234\n' > /work/evidence/proc-net/tcp
printf '  sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n' > /work/evidence/proc-net/tcp6
touch /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
printf 'starting\n' > /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status
touch /run/bookshelfng-package-was-active
CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS=2 \
CHAPTARR_BOOKSHELF_HANDOFF_HEALTH_MODE=database-listener \
CHAPTARR_BOOKSHELF_SELINUX_ENFORCE_FILE=/work/evidence/selinux-enforce \
CHAPTARR_BOOKSHELF_PROC_NET_TCP_FILE=/work/evidence/proc-net/tcp \
CHAPTARR_BOOKSHELF_PROC_NET_TCP6_FILE=/work/evidence/proc-net/tcp6 \
/usr/lib/chaptarrng/handoff/bookshelfng-healthcheck > /work/evidence/selinux-package-healthcheck.log
test "$(cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status)" = starting
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
rm -f /run/bookshelfng-package-was-active
CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS=2 \
CHAPTARR_BOOKSHELF_HANDOFF_HEALTH_MODE=database-listener \
CHAPTARR_BOOKSHELF_SELINUX_ENFORCE_FILE=/work/evidence/selinux-enforce \
CHAPTARR_BOOKSHELF_PROC_NET_TCP_FILE=/work/evidence/proc-net/tcp \
CHAPTARR_BOOKSHELF_PROC_NET_TCP6_FILE=/work/evidence/proc-net/tcp6 \
/usr/lib/chaptarrng/handoff/bookshelfng-healthcheck > /work/evidence/selinux-manual-healthcheck.log
test "$(cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status)" = healthy
test ! -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
grep -Fq database-and-listener-readable /var/lib/bookshelfng/.chaptarrng-bookshelfng-journal/outcomes/health

stop_line=$(grep -n '^stop ' /work/evidence/systemctl.log | head -1 | cut -d: -f1)
start_line=$(grep -n '^start ' /work/evidence/systemctl.log | head -1 | cut -d: -f1)
test -n "$stop_line" && test -n "$start_line" && test "$stop_line" -lt "$start_line"

rpm -Uvh --nodeps --oldpackage "$old_rpm"
test "$(rpm -q --qf '%{VERSION}-%{RELEASE}' bookshelfng)" = 0.4.21.71-1
test -x /usr/lib/bookshelfng/Readarr
grep -Fq 'ExecStart=/usr/lib/bookshelfng/Readarr' /usr/lib/systemd/system/bookshelfng.service
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary

touch /run/bookshelfng-rpm-test-active
export RPM_TEST_DROPIN_PATHS=/etc/systemd/system/bookshelfng.service.d/operator.conf
if ! expect <<'ASKYES_CUSTOM_DROPIN'
log_user 1
log_file -noappend /work/evidence/ask-yes-custom-dropin.log
set timeout 180
set prompt_seen 0
spawn env BOOKSHELFNG_MIGRATION_MODE=ask dnf -q install -y $::env(RPM_CANDIDATE)
expect {
  -re {Replace BookshelfNG with ChaptarrNG now\?.*\[y/N\] } { set prompt_seen 1; send -- "y\r" }
  timeout { exit 98 }
  eof { exit 99 }
}
expect eof
set result [wait]
if {!$prompt_seen || [lindex $result 3] != 0} { exit 96 }
exit 0
ASKYES_CUSTOM_DROPIN
then
  echo 'RPM ask mode did not defer safely for a unit-specific systemd drop-in.' >&2
  cat /work/evidence/ask-yes-custom-dropin.log >&2 || true
  exit 1
fi

grep -Fq '[y/N]' /work/evidence/ask-yes-custom-dropin.log
test "$(rpm -q --qf '%{VERSION}' bookshelfng)" = "$candidate_version"
test -x /usr/lib/bookshelfng/Readarr
grep -Fq 'ExecStart=/usr/lib/bookshelfng/Readarr' /usr/lib/systemd/system/bookshelfng.service
test "$(cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status)" = operator-review-required
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test -f /run/bookshelfng-rpm-test-active
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary

rpm -Uvh --nodeps --oldpackage "$old_rpm"
test "$(rpm -q --qf '%{VERSION}-%{RELEASE}' bookshelfng)" = 0.4.21.71-1
test -x /usr/lib/bookshelfng/Readarr
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary
echo 'Fedora 44 RPM ask No/Yes, post-transaction HTTP finalization, SELinux listener fallback, distro drop-in, unit-specific deferral, and downgrade passed; systemd was mocked.'
FEDORA_TEST

echo 'BookshelfNG RPM bridge prompt transaction passed in Fedora 44.'
