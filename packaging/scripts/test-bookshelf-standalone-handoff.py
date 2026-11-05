#!/usr/bin/env python3
"""Exercise the standalone archive handoff against real predecessor and candidate binaries."""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import http.client
import json
import os
import pty
import shutil
import signal
import sqlite3
import subprocess
import sys
import tarfile
import tempfile
import termios
import time
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parent
RUNNING: list[subprocess.Popen[bytes]] = []


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def extract_archive(archive: Path, destination: Path) -> Path:
    require(archive.is_file(), f"Archive is missing: {archive}")
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive, "r:gz") as bundle:
        members = bundle.getmembers()
        for member in members:
            path = PurePosixPath(member.name)
            require(not path.is_absolute() and ".." not in path.parts and "\0" not in member.name,
                    f"Archive contains an unsafe path: {member.name}")
            if not path.parts:
                require(member.isdir(), f"Archive contains an invalid root entry: {member.name}")
                continue

            target = destination.joinpath(*path.parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue

            require(member.isfile(), f"Archive contains an unsupported entry type: {member.name}")
            target.parent.mkdir(parents=True, exist_ok=True)
            source = bundle.extractfile(member)
            require(source is not None, f"Archive file has no content: {member.name}")
            with source, target.open("xb") as output:
                shutil.copyfileobj(source, output)
            target.chmod(member.mode & 0o777)
    return destination


def executable(root: Path, name: str) -> Path:
    candidates = [path for path in root.rglob(name) if path.is_file() and os.access(path, os.X_OK)]
    require(len(candidates) == 1, f"Expected one executable named {name} beneath {root}, found {len(candidates)}")
    return candidates[0]


def create_source_data(data_dir: Path) -> None:
    data_dir.mkdir(parents=True)
    database = data_dir / "readarr.db"
    schema = REPO / "src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.Schema.158.sql"
    fixture = REPO / "src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.PopulatedMigration158.sql"
    with sqlite3.connect(database) as connection:
        connection.executescript(schema.read_text(encoding="utf-8"))
        connection.executescript(fixture.read_text(encoding="utf-8"))
        connection.commit()
    (data_dir / "config.xml").write_text(
        "<?xml version='1.0' encoding='utf-8'?>\n"
        "<Config><BindAddress>127.0.0.1</BindAddress><Port>18787</Port>"
        "<EnableSsl>False</EnableSsl><UrlBase></UrlBase><LaunchBrowser>False</LaunchBrowser></Config>\n",
        encoding="utf-8",
    )
    (data_dir / "channel-test-canary.txt").write_text("standalone-handoff-canary=preserve-me\n", encoding="utf-8")


def http_ready() -> bool:
    connection = http.client.HTTPConnection("127.0.0.1", 18787, timeout=1)
    try:
        connection.request("GET", "/ping")
        response = connection.getresponse()
        payload = json.loads(response.read().decode("utf-8"))
        return response.status == 200 and payload.get("status") == "OK"
    except (OSError, ValueError, http.client.HTTPException):
        return False
    finally:
        connection.close()


def wait_http(process: subprocess.Popen[bytes], timeout: int, label: str) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if http_ready():
            print(f"{label}: /ping returned OK", flush=True)
            return
        if process.poll() is not None:
            raise RuntimeError(f"{label} exited with code {process.returncode} before /ping became healthy")
        time.sleep(0.25)
    raise RuntimeError(f"{label} did not return HTTP 200 from /ping within {timeout} seconds")


def start_predecessor(app: Path, data_dir: Path, log_path: Path) -> subprocess.Popen[bytes]:
    log = log_path.open("wb")
    process = subprocess.Popen(
        [str(app), "-nobrowser", f"-data={data_dir}"],
        cwd=app.parent,
        stdin=subprocess.DEVNULL,
        stdout=log,
        stderr=subprocess.STDOUT,
        start_new_session=True,
    )
    RUNNING.append(process)
    wait_http(process, 90, "BookshelfNG predecessor")
    return process


def stop_process(process: subprocess.Popen[bytes], label: str) -> None:
    if process.poll() is not None:
        if process in RUNNING:
            RUNNING.remove(process)
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=20)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait(timeout=10)
    if process in RUNNING:
        RUNNING.remove(process)
    print(f"{label}: stopped", flush=True)


def start_candidate_interactive(launcher: Path, data_dir: Path, log_path: Path, env: dict[str, str]):
    master, slave = pty.openpty()
    os.set_blocking(master, False)

    def acquire_controlling_tty() -> None:
        os.setsid()
        fcntl.ioctl(slave, termios.TIOCSCTTY, 0)

    log = log_path.open("wb")
    process = subprocess.Popen(
        [str(launcher), str(data_dir)],
        cwd=launcher.parents[1],
        stdin=slave,
        stdout=slave,
        stderr=slave,
        env=env,
        preexec_fn=acquire_controlling_tty,
    )
    os.close(slave)
    RUNNING.append(process)
    return process, master, log


def collect_terminal(master: int, log, output: bytearray) -> None:
    while True:
        try:
            chunk = os.read(master, 65536)
        except BlockingIOError:
            return
        except OSError:
            return
        if not chunk:
            return
        log.write(chunk)
        log.flush()
        output.extend(chunk)
        if len(output) > 1024 * 1024:
            del output[:-1024 * 1024]


def answer_prompt(process, master: int, log, choice: bytes, timeout: int = 45) -> bytearray:
    import select

    output = bytearray()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        ready, _, _ = select.select([master], [], [], 0.25)
        if ready:
            collect_terminal(master, log, output)
        if b"[y/N]" in output:
            os.write(master, choice + b"\n")
            print(f"standalone launcher: answered {choice.decode()} to [y/N]", flush=True)
            return output
        if process.poll() is not None:
            raise RuntimeError(f"Standalone launcher exited with code {process.returncode} before its [y/N] prompt")
    raise RuntimeError("Standalone launcher did not display its [y/N] prompt")


def wait_candidate_healthy(process, master: int, log, data_dir: Path, timeout: int = 180) -> bytearray:
    import select

    output = bytearray()
    state_file = data_dir / ".chaptarrng-bookshelfng-desktop-handoff"
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        ready, _, _ = select.select([master], [], [], 0.25)
        if ready:
            collect_terminal(master, log, output)
        state = state_file.read_text(encoding="utf-8").strip() if state_file.exists() else ""
        if state == "healthy" and http_ready():
            print("ChaptarrNG candidate: healthy state and /ping returned OK", flush=True)
            return output
        if process.poll() is not None:
            raise RuntimeError(f"Standalone candidate exited with code {process.returncode} before health passed")
    raise RuntimeError("Standalone candidate did not reach healthy state and /ping within the timeout")


def wait_for_exit(process, master: int, log, timeout: int = 45) -> bytearray:
    import select

    output = bytearray()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        ready, _, _ = select.select([master], [], [], 0.25)
        if ready:
            collect_terminal(master, log, output)
        if process.poll() is not None:
            if process in RUNNING:
                RUNNING.remove(process)
            return output
    raise RuntimeError("Standalone launcher did not exit after the No choice or forced rollback")


def child_process_ids(root_pid: int) -> list[int]:
    result = subprocess.run(
        ["ps", "-A", "-o", "pid=", "-o", "ppid="],
        check=True,
        capture_output=True,
        text=True,
    )
    parents: dict[int, list[int]] = {}
    for line in result.stdout.splitlines():
        fields = line.split()
        if len(fields) != 2:
            continue
        try:
            child_pid, parent_pid = (int(field) for field in fields)
        except ValueError:
            continue
        parents.setdefault(parent_pid, []).append(child_pid)

    descendants: list[int] = []
    frontier = [root_pid]
    while frontier:
        parent_pid = frontier.pop()
        for child_pid in parents.get(parent_pid, []):
            descendants.append(child_pid)
            frontier.append(child_pid)
    return descendants


def pid_is_running(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def stop_candidate(process, master: int, log) -> None:
    descendants = set(child_process_ids(process.pid))
    if process.poll() is None:
        # Send the same signal the PTY's Ctrl+C generates directly to the
        # supervisor. This avoids terminal line-discipline differences across
        # macOS runner architectures while leaving the supervisor's EXIT trap
        # responsible for stopping ChaptarrNG and removing its handoff lock.
        try:
            os.kill(process.pid, signal.SIGINT)
        except ProcessLookupError:
            pass

        def wait_for_supervisor(timeout: int) -> bool:
            import select

            deadline = time.monotonic() + timeout
            output = bytearray()
            while time.monotonic() < deadline:
                ready, _, _ = select.select([master], [], [], 0.1)
                if ready:
                    collect_terminal(master, log, output)
                if process.poll() is not None:
                    return True
            return process.poll() is not None

        if not wait_for_supervisor(45):
            print("Candidate supervisor did not stop after SIGINT; sending SIGHUP.", flush=True)
            try:
                os.kill(process.pid, signal.SIGHUP)
            except ProcessLookupError:
                pass
            if not wait_for_supervisor(45):
                print("Candidate supervisor did not stop after SIGHUP; sending SIGTERM.", flush=True)
                try:
                    os.kill(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                wait_for_supervisor(45)

    # macOS has no setsid --wait, so the app can remain a child in the
    # launcher's process tree. Signal those exact child PIDs rather than
    # relying on killpg for this controlling-terminal process group.
    descendants.update(child_process_ids(process.pid))
    for pid in reversed(sorted(descendants)):
        if pid_is_running(pid):
            try:
                os.kill(pid, signal.SIGTERM)
            except (ProcessLookupError, PermissionError):
                pass
    if process.poll() is None:
        try:
            process.terminate()
        except (ProcessLookupError, PermissionError):
            pass

    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        descendants.update(child_process_ids(process.pid))
        if not any(pid_is_running(pid) for pid in descendants) and process.poll() is not None:
            break
        time.sleep(0.1)

    descendants.update(child_process_ids(process.pid))
    remaining = [pid for pid in descendants if pid_is_running(pid)]
    for pid in reversed(sorted(remaining)):
        try:
            os.kill(pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
    if process.poll() is None:
        try:
            process.kill()
        except (ProcessLookupError, PermissionError):
            pass
    process.wait(timeout=10)
    remaining = [pid for pid in descendants if pid_is_running(pid)]
    require(not remaining, f"Candidate child processes remained after cleanup: {remaining}")
    if process in RUNNING:
        RUNNING.remove(process)
    try:
        os.close(master)
    except OSError:
        pass
    log.close()


def file_snapshot(data_dir: Path) -> dict[str, str]:
    return {
        name: sha256(data_dir / name)
        for name in ("readarr.db", "config.xml", "channel-test-canary.txt")
    }


def database_details(database: Path) -> dict[str, int | str]:
    uri = database.resolve().as_uri() + "?mode=ro"
    with sqlite3.connect(uri, uri=True, timeout=5) as connection:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        foreign_keys = connection.execute("PRAGMA foreign_key_check").fetchall()
        version = connection.execute("SELECT MAX(Version) FROM VersionInfo").fetchone()[0]
        authors = connection.execute("SELECT COUNT(*) FROM Authors").fetchone()[0]
        books = connection.execute("SELECT COUNT(*) FROM Books").fetchone()[0]
        editions = connection.execute("SELECT COUNT(*) FROM Editions").fetchone()[0]
        return {
            "integrity": integrity,
            "foreign_key_violations": len(foreign_keys),
            "version": int(version),
            "authors": int(authors),
            "books": int(books),
            "editions": int(editions),
        }


def logical_database_hash(database: Path) -> str:
    uri = database.resolve().as_uri() + "?mode=ro"
    digest = hashlib.sha256()
    with sqlite3.connect(uri, uri=True, timeout=5) as connection:
        for statement in connection.iterdump():
            digest.update(statement.encode("utf-8"))
            digest.update(b"\n")
    return digest.hexdigest()


def assert_source_database(database: Path, expected_hash: str, label: str) -> None:
    require(logical_database_hash(database) == expected_hash, f"{label}: source database contents changed")
    details = database_details(database)
    require(details["integrity"] == "ok", f"{label}: source SQLite integrity failed")
    require(details["foreign_key_violations"] == 0, f"{label}: source has foreign-key violations")
    require(details["version"] == 158, f"{label}: expected BookshelfNG schema 158, found {details['version']}")


def verify_target_database(data_dir: Path, source_hash: str, label: str) -> None:
    database = data_dir / "readarr.db"
    details = database_details(database)
    require(details["integrity"] == "ok", f"{label}: target SQLite integrity failed")
    require(details["foreign_key_violations"] == 0, f"{label}: target has foreign-key violations")
    require(details["version"] == 126, f"{label}: expected ChaptarrNG schema 126, found {details['version']}")
    require(details["authors"] > 0 and details["books"] > 0 and details["editions"] > 0,
            f"{label}: populated author/book/edition data was not preserved")
    backups = list(data_dir.glob("readarr.db.bookshelfng-backup-*.db"))
    require(any(logical_database_hash(backup) == source_hash for backup in backups),
            f"{label}: a schema-158 source backup with identical database contents was not retained")
    print(f"{label}: migrated database checks passed: {details}", flush=True)


def verify_candidate_backup(data_dir: Path) -> None:
    targets = list(data_dir.glob("readarr.db.chaptarrng-backup-*.db"))
    require(bool(targets), "forced rollback did not retain the failed ChaptarrNG target database")
    details = database_details(targets[-1])
    require(details["version"] == 126 and details["integrity"] == "ok",
            "forced rollback's retained target database is not a valid migrated database")


def run_no_case(predecessor: Path, launcher: Path, work: Path, base_env: dict[str, str]) -> None:
    data_dir = work / "no-choice-data"
    create_source_data(data_dir)
    old = start_predecessor(predecessor, data_dir, work / "no-predecessor.log")
    stop_process(old, "BookshelfNG before No")
    before = file_snapshot(data_dir)
    source_hash = logical_database_hash(data_dir / "readarr.db")
    assert_source_database(data_dir / "readarr.db", source_hash, "No fixture")

    env = base_env.copy()
    env.pop("BOOKSHELFNG_MIGRATION_MODE", None)
    process, master, log = start_candidate_interactive(launcher, data_dir, work / "no-choice.tty.log", env)
    answer_prompt(process, master, log, b"n")
    wait_for_exit(process, master, log)
    log.close()
    os.close(master)

    require(process.returncode == 0, f"No choice returned exit code {process.returncode}")
    require(not (data_dir / ".chaptarrng-bookshelfng-desktop-handoff").exists(),
            "No choice recorded a conversion state despite leaving the legacy database untouched")
    require(file_snapshot(data_dir) == before, "No choice changed the predecessor database, config, or canary")

    old = start_predecessor(predecessor, data_dir, work / "no-predecessor-restart.log")
    stop_process(old, "BookshelfNG after No")
    print("No choice: predecessor database, config, and canary were unchanged; old /ping restarted successfully", flush=True)


def run_headless_manual_case(predecessor: Path, launcher: Path, work: Path, base_env: dict[str, str]) -> None:
    data_dir = work / "headless-manual-data"
    create_source_data(data_dir)
    old = start_predecessor(predecessor, data_dir, work / "headless-manual-predecessor.log")
    stop_process(old, "BookshelfNG before headless Manual")
    before = file_snapshot(data_dir)
    source_hash = logical_database_hash(data_dir / "readarr.db")
    env = base_env.copy()
    env.pop("BOOKSHELFNG_MIGRATION_MODE", None)
    env.pop("DISPLAY", None)
    env.pop("WAYLAND_DISPLAY", None)
    log_path = work / "headless-manual.log"
    with log_path.open("wb") as log:
        process = subprocess.Popen([str(launcher), str(data_dir)], cwd=launcher.parents[1], env=env,
                                   stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        RUNNING.append(process)
        try:
            process.wait(timeout=60)
        except subprocess.TimeoutExpired as error:
            stop_process(process, "headless Manual candidate")
            raise RuntimeError("Headless Manual choice did not finish within 60 seconds") from error
    if process in RUNNING:
        RUNNING.remove(process)

    require(process.returncode == 0, f"Headless Manual returned exit code {process.returncode}")
    require("conversion was deferred" in log_path.read_text(encoding="utf-8"),
            "headless launch did not explain that conversion was deferred")
    require(not (data_dir / ".chaptarrng-bookshelfng-desktop-handoff").exists(),
            "headless Manual recorded a conversion state")
    require(file_snapshot(data_dir) == before, "headless Manual changed the predecessor database, config, or canary")
    assert_source_database(data_dir / "readarr.db", source_hash, "headless Manual")
    old = start_predecessor(predecessor, data_dir, work / "headless-manual-predecessor-restart.log")
    stop_process(old, "BookshelfNG after headless Manual")
    print("Headless Manual: source files remained unchanged and the old /ping restarted successfully", flush=True)


def run_yes_case(predecessor: Path, launcher: Path, work: Path, base_env: dict[str, str]) -> None:
    data_dir = work / "yes-choice-data"
    create_source_data(data_dir)
    old = start_predecessor(predecessor, data_dir, work / "yes-predecessor.log")
    stop_process(old, "BookshelfNG before Yes")
    source_hash = logical_database_hash(data_dir / "readarr.db")
    source_details = database_details(data_dir / "readarr.db")
    require(source_details["version"] == 158 and source_details["authors"] > 0,
            "Yes fixture is not a populated BookshelfNG schema-158 database")

    env = base_env.copy()
    env.pop("BOOKSHELFNG_MIGRATION_MODE", None)
    env["CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS"] = "120"
    process, master, log = start_candidate_interactive(launcher, data_dir, work / "yes-choice.tty.log", env)
    answer_prompt(process, master, log, b"y")
    wait_candidate_healthy(process, master, log, data_dir)
    verify_target_database(data_dir, source_hash, "Yes choice")
    stop_candidate(process, master, log)
    require((data_dir / ".chaptarrng-bookshelfng-desktop-handoff").read_text().strip() == "healthy",
            "healthy state was not retained after stopping the candidate")
    lock_dir = data_dir / ".chaptarrng-bookshelfng-desktop.lock"
    lock_entries = sorted(path.name for path in lock_dir.iterdir()) if lock_dir.is_dir() else []
    require(not lock_dir.exists(),
            f"candidate did not remove its handoff lock after stopping; remaining entries: {lock_entries}")


def run_rollback_case(predecessor: Path, launcher: Path, work: Path, base_env: dict[str, str]) -> None:
    data_dir = work / "rollback-data"
    create_source_data(data_dir)
    old = start_predecessor(predecessor, data_dir, work / "rollback-predecessor.log")
    stop_process(old, "BookshelfNG before forced rollback")
    source_hash = logical_database_hash(data_dir / "readarr.db")

    env = base_env.copy()
    env["BOOKSHELFNG_MIGRATION_MODE"] = "automatic"
    env["BOOKSHELFNG_HEALTH_URL"] = "http://127.0.0.1:1/ping"
    env["CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS"] = "8"
    log_path = work / "forced-rollback.log"
    with log_path.open("wb") as log:
        process = subprocess.Popen([str(launcher), str(data_dir)], cwd=launcher.parents[1], env=env,
                                   stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        RUNNING.append(process)
        try:
            process.wait(timeout=150)
        except subprocess.TimeoutExpired as error:
            stop_process(process, "forced-rollback candidate")
            raise RuntimeError("Forced health failure did not finish rollback in 150 seconds") from error
    if process in RUNNING:
        RUNNING.remove(process)

    require(process.returncode != 0, "forced health failure unexpectedly returned success")
    state_file = data_dir / ".chaptarrng-bookshelfng-desktop-handoff"
    require(state_file.read_text(encoding="utf-8").strip() == "rolled-back",
            "forced health failure did not record rolled-back")
    assert_source_database(data_dir / "readarr.db", source_hash, "forced rollback")
    verify_candidate_backup(data_dir)
    require(not http_ready(), "failed ChaptarrNG candidate remained available after rollback")
    old = start_predecessor(predecessor, data_dir, work / "rollback-predecessor-restart.log")
    stop_process(old, "BookshelfNG after forced rollback")
    print("Forced health failure: source database contents restored, failed target retained, and BookshelfNG /ping recovered", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("predecessor_archive", type=Path)
    parser.add_argument("candidate_archive", type=Path)
    args = parser.parse_args()

    temp_root = Path(os.environ.get("TMPDIR", tempfile.gettempdir()))
    temp_root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="bookshelfng-standalone-handoff-", dir=temp_root))
    success = False
    try:
        predecessor_root = extract_archive(args.predecessor_archive.resolve(), work / "predecessor")
        candidate_root = extract_archive(args.candidate_archive.resolve(), work / "candidate")
        predecessor = executable(predecessor_root, "Readarr")
        launcher = candidate_root / "ChaptarrNG/scripts/bookshelfng-standalone-launch.sh"
        require(launcher.is_file() and os.access(launcher, os.X_OK),
                "Candidate archive does not contain an executable standalone handoff launcher")
        require(http_ready() is False, "The acceptance port 127.0.0.1:18787 is already in use")
        base_env = os.environ.copy()
        base_env.pop("BOOKSHELFNG_MIGRATION_MODE", None)

        print(f"BookshelfNG predecessor SHA-256: {sha256(args.predecessor_archive)}", flush=True)
        print(f"ChaptarrNG candidate SHA-256: {sha256(args.candidate_archive)}", flush=True)
        run_no_case(predecessor, launcher, work, base_env)
        run_headless_manual_case(predecessor, launcher, work, base_env)
        run_yes_case(predecessor, launcher, work, base_env)
        run_rollback_case(predecessor, launcher, work, base_env)
        success = True
        print("Standalone archive No/Manual/Yes/health/backup/rollback acceptance passed.", flush=True)
        return 0
    except Exception:
        print(f"Standalone handoff test workspace preserved at: {work}", file=sys.stderr, flush=True)
        raise
    finally:
        for process in list(RUNNING):
            try:
                stop_process(process, "remaining test process")
            except Exception as error:
                print(f"Could not stop a test process cleanly: {error}", file=sys.stderr, flush=True)
        if success:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
