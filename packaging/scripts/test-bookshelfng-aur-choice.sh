#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
test_image="fedora:44@sha256:43b29f65a41eb9c35e1cd5323e3bdf3b655c2357a9f4f1ff2f9c2798e5045d80"
task_root="${TASK_SCRATCH_DIR:-}"
if [[ -n "$task_root" ]]; then
  if [[ -e "$task_root" ]]; then
    echo "Refusing to reuse test scratch path: $task_root" >&2
    exit 2
  fi
  mkdir -p "$(dirname "$task_root")"
  mkdir "$task_root"
else
  task_root="$(mktemp -d "${TMPDIR:-/tmp}/chaptarrng-aur-choice-test.XXXXXX")"
fi

cleanup() {
  result=$?
  if [[ -d "$task_root" ]] && docker image inspect "$test_image" >/dev/null 2>&1; then
    docker run --rm --user 0:0 --mount "type=bind,src=$task_root,dst=/work" \
      "$test_image" chown -R "$(id -u):$(id -g)" /work >/dev/null 2>&1 || {
        echo "Could not restore ownership of AUR choice test scratch: $task_root" >&2
        if [[ $result -eq 0 ]]; then result=1; fi
      }
  fi
  if [[ $result -eq 0 ]]; then
    rm -rf -- "$task_root"
  else
    echo "AUR migration choice test failed; evidence kept at $task_root" >&2
    for evidence_file in "$task_root"/evidence/*; do
      [[ -f "$evidence_file" ]] || continue
      echo "--- $evidence_file ---" >&2
      tail -n 80 "$evidence_file" >&2 || true
    done
  fi
  exit "$result"
}
trap cleanup EXIT

mkdir -p "$task_root/evidence"
container_test=$(cat <<'AUR_TEST'
set -eu

cat > /usr/bin/systemctl <<'SYSTEMCTL'
#!/bin/sh
printf '%s\n' "$*" >> /work/evidence/systemctl.log
case "$1" in
  is-active) test -f /run/bookshelfng-aur-choice-test-active ;;
  stop) rm -f /run/bookshelfng-aur-choice-test-active ;;
  start) touch /run/bookshelfng-aur-choice-test-active ;;
  *) exit 0 ;;
esac
SYSTEMCTL
chmod 0755 /usr/bin/systemctl

mkdir -p /usr/lib/bookshelfng /usr/lib/systemd/system /etc/bookshelfng /var/lib/bookshelfng
cat > /usr/lib/bookshelfng/Readarr <<'OLDAPP'
#!/bin/sh
echo BookshelfNG-test-binary
OLDAPP
chmod 0755 /usr/lib/bookshelfng/Readarr
cat > /usr/lib/systemd/system/bookshelfng.service <<'OLDUNIT'
[Service]
ExecStart=/usr/lib/bookshelfng/Readarr
OLDUNIT
printf 'old-package-launcher\n' > /usr/bin/bookshelfng
cp /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.env \
  /etc/bookshelfng/bookshelfng.env
printf '# operator-override=true\n' >> /etc/bookshelfng/bookshelfng.env
printf 'migration-data-canary\n' > /var/lib/bookshelfng/data-canary
touch /run/bookshelfng-aur-choice-test-active

env BOOKSHELFNG_MIGRATION_MODE=ask bash -c ". /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.install; pre_upgrade" \
  > /work/evidence/ask-no.log 2>&1
grep -Fq 'pacman will finish the package update' /work/evidence/ask-no.log
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-deferred
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/snapshot-ready
test -x /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/source-app/Readarr
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/source-unit
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/source-bin-entry
test -e /run/bookshelfng-package-was-active
test ! -e /run/bookshelfng-aur-choice-test-active
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary

# Reproduce pacman's non-atomic behavior: the package payload is replaced
# after No, then post_upgrade restores the saved BookshelfNG runtime.
rm -rf /usr/lib/bookshelfng
cat > /usr/lib/systemd/system/bookshelfng.service <<'TARGETUNIT'
[Service]
ExecStart=/usr/lib/chaptarrng/bin/Chaptarr
TARGETUNIT
mkdir -p /usr/lib/chaptarrng/bin /usr/lib/chaptarrng/handoff
printf '#!/bin/sh\necho ChaptarrNG-test-binary\n' > /usr/lib/chaptarrng/bin/Chaptarr
chmod 0755 /usr/lib/chaptarrng/bin/Chaptarr
printf 'ChaptarrNG test package\n' > /usr/lib/chaptarrng/package_info
cp /repo/packaging/legacy/scripts/bookshelfng-journal \
  /usr/lib/chaptarrng/handoff/bookshelfng-journal
chmod 0755 /usr/lib/chaptarrng/handoff/bookshelfng-journal
printf 'target-package-launcher\n' > /usr/bin/bookshelfng

bash -c ". /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.install; post_upgrade" \
  > /work/evidence/post-no.log 2>&1

test -x /usr/lib/bookshelfng/Readarr
grep -Fq 'BookshelfNG-test-binary' /usr/lib/bookshelfng/Readarr
grep -Fq 'ExecStart=/usr/lib/bookshelfng/Readarr' /usr/lib/systemd/system/bookshelfng.service
grep -Fq 'old-package-launcher' /usr/bin/bookshelfng
test -f /usr/lib/chaptarrng/package_info
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-deferred
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test "$(cat /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff-status)" = deferred
test "$(/usr/lib/chaptarrng/handoff/bookshelfng-journal get-state)" = deferred
test -e /run/bookshelfng-aur-choice-test-active
test ! -e /run/bookshelfng-package-was-active
grep -Fq 'start bookshelfng.service' /work/evidence/systemctl.log
grep -Fq 'daemon-reload' /work/evidence/systemctl.log
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary

# Recheck the deferred transaction without a controlling terminal. A file
# permission probe on /dev/tty is insufficient because the device node exists
# even when the current package process has no terminal.
env BOOKSHELFNG_MIGRATION_MODE=ask bash -c ". /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.install; pre_upgrade" \
  < /dev/null > /work/evidence/ask-headless.log 2>&1
grep -Fq 'No terminal is available' /work/evidence/ask-headless.log
! grep -Fq '[y/N]' /work/evidence/ask-headless.log
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-deferred
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
bash -c ". /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.install; post_upgrade" \
  > /work/evidence/ask-headless-restore.log 2>&1
test -x /usr/lib/bookshelfng/Readarr
grep -Fq 'BookshelfNG-test-binary' /usr/lib/bookshelfng/Readarr
test -e /run/bookshelfng-aur-choice-test-active

# A later explicit Yes consumes the deferred marker and schedules normal
# conversion during the next package transaction.
env BOOKSHELFNG_MIGRATION_MODE=ask bash -c ". /repo/packaging/legacy/aur/bookshelfng-bin/bookshelfng.install; pre_upgrade" \
  > /work/evidence/ask-yes.log 2>&1
test ! -e /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-deferred
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-migration-pending
test -f /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/snapshot-ready
test -x /var/lib/bookshelfng/.chaptarrng-bookshelfng-handoff/source-app/Readarr
test -e /run/bookshelfng-package-was-active
test ! -e /run/bookshelfng-aur-choice-test-active
grep -Fq 'stop bookshelfng.service' /work/evidence/systemctl.log
grep -Fq '# operator-override=true' /etc/bookshelfng/bookshelfng.env
grep -Fq 'migration-data-canary' /var/lib/bookshelfng/data-canary

echo 'AUR package hook safely defers No across payload replacement, restores BookshelfNG, and retries on later Yes.'
AUR_TEST
)

AUR_TEST_SCRIPT="$container_test" python3 - "$test_image" "$repo_root" "$task_root" <<'PTY_DRIVER'
import os
import pty
import select
import subprocess
import sys
import time
from pathlib import Path

image, repo_root, task_root = sys.argv[1:]
container_script = os.environ["AUR_TEST_SCRIPT"]
transcript_path = Path(task_root) / "evidence" / "pty-session.log"
prompt = b"Replace BookshelfNG with ChaptarrNG now? A still-legacy database will be migrated automatically. [y/N] "
command = [
    "docker", "run", "--rm", "--interactive", "--tty", "--network", "none", "--user", "0:0",
    "--mount", f"type=bind,src={repo_root},dst=/repo,readonly",
    "--mount", f"type=bind,src={task_root},dst=/work",
    image, "bash", "-c", container_script,
]
master, slave = pty.openpty()
proc = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave, close_fds=True)
os.close(slave)
transcript = bytearray()
prompt_count = 0
search_offset = 0
deadline = time.monotonic() + 180
try:
    with transcript_path.open("wb") as evidence:
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                proc.kill()
                raise RuntimeError("Timed out waiting for the AUR migration choice container")
            readable, _, _ = select.select([master], [], [], min(remaining, 0.2))
            if readable:
                try:
                    chunk = os.read(master, 4096)
                except OSError:
                    break
                if not chunk:
                    break
                evidence.write(chunk)
                evidence.flush()
                transcript.extend(chunk)
                while True:
                    position = transcript.find(prompt, search_offset)
                    if position < 0:
                        break
                    if prompt_count >= 2:
                        raise RuntimeError("The AUR migration test emitted an unexpected extra prompt")
                    os.write(master, b"\r" if prompt_count == 0 else b"y\r")
                    prompt_count += 1
                    search_offset = position + len(prompt)
            if proc.poll() is not None and not readable:
                break
    result = proc.wait(timeout=5)
finally:
    os.close(master)
if result != 0:
    raise SystemExit(result)
if prompt_count != 2:
    raise SystemExit(f"Expected the AUR test to show two No/Yes prompts; observed {prompt_count}")
print("AUR migration choice container passed its No, headless defer, and later Yes retry checks.")
PTY_DRIVER

echo 'BookshelfNG AUR migration choice passed.'
