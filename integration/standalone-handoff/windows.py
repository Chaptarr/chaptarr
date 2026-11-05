#!/usr/bin/env python3
"""Exercise the documented Windows archive migration and explicit rollback tools."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import http.client
import json
import os
import shutil
import socket
import sqlite3
import struct
import subprocess
import sys
import time
import zipfile

REPO = Path(__file__).resolve().parents[2]
RUNNING = []


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def extract(archive, destination):
    destination.mkdir()
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            path = PurePosixPath(entry.filename.replace('\\', '/'))
            require(path.parts and not path.is_absolute() and '..' not in path.parts and ':' not in path.parts[0], 'Unsafe archive path')
            require((entry.external_attr >> 16) & 0o170000 != 0o120000, 'Archive symlinks are unsupported')
            target = destination.joinpath(*path.parts)
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(entry) as source, target.open('xb') as output:
                    shutil.copyfileobj(source, output)


def executable(root, names):
    for name in names:
        matches = list(root.rglob(name))
        if matches:
            require(len(matches) == 1, 'Ambiguous executable: ' + name)
            return matches[0]
    raise RuntimeError('Expected executable missing: ' + ', '.join(names))


def pe_machine(path):
    with path.open('rb') as source:
        header = source.read(64)
        require(header[:2] == b'MZ', 'Expected a Windows executable')
        source.seek(struct.unpack('<I', header[60:64])[0])
        header = source.read(6)
        require(header[:4] == b'PE\0\0', 'Expected a PE executable')
        return struct.unpack('<H', header[4:6])[0]


def database_hash(path):
    with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True) as connection:
        return hashlib.sha256('\n'.join(sorted(connection.iterdump())).encode()).hexdigest()


def database_check(path, version):
    with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True) as connection:
        require(connection.execute('PRAGMA integrity_check').fetchall() == [('ok',)], 'Database integrity failure')
        require(not connection.execute('PRAGMA foreign_key_check').fetchall(), 'Foreign-key violations')
        require(connection.execute('SELECT MAX(Version) FROM VersionInfo').fetchone()[0] == version, 'Unexpected schema version')
        return connection.execute('SELECT Id,Path,Size,Part FROM BookFiles ORDER BY Id').fetchall()


def healthy():
    connection = http.client.HTTPConnection('127.0.0.1', 18787, timeout=2)
    try:
        connection.request('GET', '/ping')
        response = connection.getresponse()
        return response.status == 200 and json.loads(response.read()).get('status') == 'OK'
    except (OSError, ValueError, http.client.HTTPException):
        return False
    finally:
        connection.close()


def start(app, data, log_path):
    output = log_path.open('wb')
    try:
        process = subprocess.Popen([str(app), '-nobrowser', '-data=' + str(data)], cwd=app.parent,
                                   stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT)
    finally:
        output.close()
    RUNNING.append(process)
    return process


def wait_healthy(process):
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        if healthy():
            return
        require(process.poll() is None, 'Application exited before becoming healthy: ' + str(process.returncode))
        time.sleep(0.25)
    raise RuntimeError('Application health timed out')


def stop(process):
    if process.poll() is None:
        # Only a PID created and retained by this harness; never name-based process termination.
        killed = subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
        require(killed.returncode == 0 or process.poll() is not None, 'Could not stop an owned test process')
        process.wait(timeout=30)
    if process in RUNNING:
        RUNNING.remove(process)


def utility(app, data, phase, arguments, root):
    with (root / (phase + '.log')).open('wb') as output:
        result = subprocess.run([str(app), '-exitimmediately', '-data=' + str(data), *arguments],
                                cwd=app.parent, stdin=subprocess.DEVNULL, stdout=output,
                                stderr=subprocess.STDOUT, timeout=240)
    require(result.returncode == 0, phase + ' failed; see its retained log')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('predecessor', type=Path)
    parser.add_argument('candidate', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--runtime-id', choices=['win-x64', 'win-x86'], required=True)
    parser.add_argument('--candidate-revision', required=True)
    args = parser.parse_args()
    require(os.name == 'nt', 'Windows native acceptance must run on Windows')
    require(os.environ.get('HARDCOVER', '').lower() in {'true', 'false'}, 'Choose an explicit source provider mode before the rehearsal')
    root = args.output.resolve()
    require(not root.exists(), 'Refuse to reuse an earlier acceptance directory')
    root.mkdir(parents=True)
    result = {'runtimeId': args.runtime_id, 'candidateRevision': args.candidate_revision,
              'sourceHardcoverMode': os.environ['HARDCOVER'],
              'predecessorArchiveSha256': digest(args.predecessor), 'candidateArchiveSha256': digest(args.candidate),
              'cases': [], 'success': False}
    try:
        extract(args.predecessor, root / 'predecessor')
        extract(args.candidate, root / 'candidate')
        old = executable(root / 'predecessor', ['Readarr.Console.exe', 'Readarr.exe'])
        new = executable(root / 'candidate', ['Chaptarr.Console.exe', 'Chaptarr.exe'])
        machine = 0x8664 if args.runtime_id == 'win-x64' else 0x14c
        require(pe_machine(old) == machine and pe_machine(new) == machine, 'Executable architecture differs from requested RID')
        require(not healthy(), 'Acceptance port is already occupied')
        data = root / 'data'
        data.mkdir()
        with sqlite3.connect(data / 'readarr.db') as connection:
            for name in ['BookshelfNG.Schema.158.sql', 'BookshelfNG.PopulatedMigration158.sql']:
                connection.executescript((REPO / 'src/Chaptarr.Core.Test/Datastore/Fixtures' / name).read_text())
            connection.execute('UPDATE BookFiles SET Size=?,Path=? WHERE Id=50', (4294967419, 'C:\\library\\Grüße — fixture.m4b'))
            connection.execute('UPDATE Indexers SET EnableRss=0,EnableAutomaticSearch=0,EnableInteractiveSearch=0')
            connection.execute('UPDATE DownloadClients SET Enable=0')
            connection.execute('UPDATE ImportLists SET EnableAutomaticAdd=0,ShouldSearch=0')
            # Disable the startup-triggered notifications in this committed synthetic schema.
            connection.execute('UPDATE Notifications SET OnApplicationUpdate=0,OnHealthIssue=0,OnAuthorAdded=0')
        (data / 'config.xml').write_text('<Config><BindAddress>127.0.0.1</BindAddress><Port>18787</Port><ApiKey>0123456789abcdef0123456789abcdef</ApiKey><EnableSsl>False</EnableSsl><UrlBase></UrlBase><LaunchBrowser>False</LaunchBrowser><UpdateAutomatically>False</UpdateAutomatically></Config>')
        (data / 'channel-test-canary.txt').write_text('windows-standalone-canary=preserve-me\n')
        process = start(old, data, root / 'predecessor.log')
        wait_healthy(process)
        stop(process)
        before = database_hash(data / 'readarr.db')
        files = database_check(data / 'readarr.db', 158)
        utility(new, data, 'preview', ['-preview-bookshelf-migration'], root)
        require(database_hash(data / 'readarr.db') == before, 'Preview modified source data')
        process = start(old, data, root / 'deferred-predecessor.log')
        wait_healthy(process)
        stop(process)
        # Rebaseline after the predecessor's own startup housekeeping, before migration.
        before = database_hash(data / 'readarr.db')
        files = database_check(data / 'readarr.db', 158)
        result['cases'].append('readonly preview, manual defer and healthy predecessor')
        print('Preview/defer preserved the source; predecessor health passed.', flush=True)
        utility(new, data, 'migrate', ['-migrate-bookshelf', '-confirm-bookshelf-service-stopped'], root)
        require(database_check(data / 'readarr.db', 126) == files, 'File identity, Unicode paths, 64-bit sizes or parts changed')
        backups = list(data.glob('readarr.db.bookshelfng-backup-*.db'))
        require(any(database_hash(backup) == before for backup in backups), 'Identical source backup was not retained')
        process = start(new, data, root / 'candidate.log')
        wait_healthy(process)
        stop(process)
        utility(new, data, 'check', ['-check-bookshelf-migration'], root)
        result['cases'].append('migration, healthy candidate, preserved files and retained source backup')
        print('Migration, file preservation, source backup and candidate health passed.', flush=True)
        blocker = socket.socket()
        try:
            blocker.bind(('127.0.0.1', 18787))
            blocker.listen()
            process = start(new, data, root / 'injected-port-failure.log')
            process.wait(timeout=90)
            require(process.returncode != 0, 'Injected occupied-port startup unexpectedly succeeded')
            stop(process)
        finally:
            blocker.close()
        target_hash = database_hash(data / 'readarr.db')
        utility(new, data, 'restore-preview', ['-preview-bookshelf-restore'], root)
        require(database_hash(data / 'readarr.db') == target_hash, 'Restore preview modified the target')
        utility(new, data, 'restore', ['-restore-bookshelf', '-confirm-bookshelf-service-stopped'], root)
        require(database_check(data / 'readarr.db', 158) == files and database_hash(data / 'readarr.db') == before,
                'Explicit rollback did not restore the source database exactly')
        targets = list(data.glob('readarr.db.chaptarrng-backup-*.db'))
        require(targets and database_check(targets[-1], 126) == files, 'Failed target was not retained')
        process = start(old, data, root / 'restored-predecessor.log')
        wait_healthy(process)
        stop(process)
        require((data / 'channel-test-canary.txt').read_text() == 'windows-standalone-canary=preserve-me\n', 'Canary changed')
        result['cases'].append('failed startup, readonly restore preview, explicit rollback and healthy source')
        print('Failed startup, explicit rollback and recovered source health passed.', flush=True)
        result['success'] = True
        print('Windows standalone archive migration/defer/health/explicit rollback passed.', flush=True)
    finally:
        for process in list(RUNNING):
            stop(process)
        (root / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        # Preserve logs, data and backups, then remove only extracted task-owned binaries.
        for name in ['candidate', 'predecessor']:
            path = root / name
            if path.is_dir():
                shutil.rmtree(path)


if __name__ == '__main__':
    main()
