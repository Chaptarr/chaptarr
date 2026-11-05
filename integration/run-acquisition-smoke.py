#!/usr/bin/env python3
"""Isolated migration -> Seerr adapter -> Torznab -> real qBittorrent -> EPUB import.

Requires an extracted Linux musl x64 candidate and a NEW task output directory.
No host ports, live services, production credentials, or external torrent traffic.
Evidence and synthetic state remain in the output directory on success/failure.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--qbittorrent-image', default='chaptarrng-acquisition-qbittorrent:20261105')
args = parser.parse_args()
candidate = args.candidate.resolve()
assert (candidate / 'Chaptarr').is_file()
root = args.output.resolve()
assert not root.exists(), 'Refuse to reuse a previous rehearsal directory'
repo = Path(__file__).resolve().parents[1]
fixtures = repo / 'integration/acquisition'
prefix = 'chaptarrng-acquisition-' + uuid.uuid4().hex[:12]
app = prefix + '-app'
names = []
images = {
    'app': 'docker.io/snapetech/bookshelfng@sha256:40a53ac528b6789ca78686b4303bb6c436cd191cd813f915ea171d72b47a8c38',
    'seerr': 'ghcr.io/snapetech-org/seerrng@sha256:e433d13a2c50fdc96948361f3c090ed9bb12e8788fc6741337737c973e956aff',
    'node': 'node:22-alpine',
    'qbittorrent': args.qbittorrent_image,
}

def command(argv, log=None, timeout=180):
    result = subprocess.run([str(value) for value in argv], capture_output=True, text=True, timeout=timeout)
    if log:
        (root / log).write_text(result.stdout + result.stderr)
    if result.returncode:
        if not log and root.exists():
            (root / 'failed-command.log').write_text(result.stdout + result.stderr)
        raise RuntimeError(f'Command failed ({result.returncode}); evidence: {log or "failed-command.log"}')
    return result.stdout

def start(name, options):
    names.append(name)
    command(['docker', 'run', '-d', '--name', name, *options])

security = ['--user', f'{os.getuid()}:{os.getgid()}', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--memory', '2g', '--cpus', '2']

def start_stack():
    options = ['--network', 'none', '--tmpfs', '/run/readarr-temp:rw,nosuid,nodev,size=256m,mode=1777', *security,
               '-e', 'HARDCOVER=true', '-e', 'HARDCOVER_NATIVE=', '-e', 'METADATA_URL=http://127.0.0.1:8980',
               '-e', 'CHAPTARR__AUTH__SEERRAPIKEY=synthetic-e2e-restricted-key-20261105']
    for source, target, readonly in [(root / 'config', '/config', False), (root / 'library', '/library', False),
                                     (root / 'downloads', '/downloads', False), (candidate, '/candidate', True)]:
        options += ['-v', f'{source}:{target}' + (':ro' if readonly else '')]
    start(app, [*options, '--entrypoint', '/candidate/Chaptarr', images['app'], '-nobrowser', '-data=/config'])
    network = ['--network', 'container:' + app]
    start(prefix + '-provider', [*network, *security, '-v', f'{root}:/fixture', images['node'], 'node', '/fixture/provider/server.mjs'])
    for kind, web, peer in [('seed', 8081, 6881), ('download', 8080, 6882)]:
        options = [*network, *security]
        for source, target in [(root / ('qbit-' + kind), '/qb'), (root / 'seed', '/seed'), (root / 'downloads', '/downloads'), (root / 'provider', '/provider')]:
            options += ['-v', f'{source}:{target}']
        options += [images['qbittorrent'], '--profile=/qb', '--confirm-legal-notice', f'--webui-port={web}', f'--torrenting-port={peer}']
        if kind == 'seed':
            options += ['--save-path=/seed', '/provider/fixture.torrent']
        start(prefix + '-qb-' + kind, options)
    command(['docker', 'run', '--rm', *network, images['node'], 'node', '-e',
             'for(let n=0;n<90;n++){try{if((await fetch("http://127.0.0.1:8789/ping")).ok)process.exit(0)}catch{};await new Promise(r=>setTimeout(r,1000));}process.exit(1)'], 'health.log', 120)

def stop_stack(label):
    while names:
        name = names.pop()
        # Save diagnostics before removing only containers created by this run.
        result = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
        (root / f'{label}-{name}.log').write_text(result.stdout + result.stderr)
        subprocess.run(['docker', 'stop', '-t', '10', name], capture_output=True)
        subprocess.run(['docker', 'rm', name], capture_output=True)

def seerr(script, log, *arguments):
    probe = prefix + '-seerr'
    try:
        command(['docker', 'run', '--rm', '--name', probe, '--network', 'container:' + app, *security,
                 '-v', f'{root}:/fixture', '-v', f'{fixtures}:/checks:ro', '--entrypoint', 'node',
                 images['seerr'], '/checks/' + script, *arguments], log, 300)
    finally:
        subprocess.run(['docker', 'stop', '-t', '5', probe], capture_output=True)
        subprocess.run(['docker', 'rm', probe], capture_output=True)

def logical_database_hash(database):
    with sqlite3.connect(f'file:{database}?mode=ro', uri=True) as connection:
        tables = connection.execute("SELECT name,sql FROM sqlite_master WHERE type='table' ORDER BY name").fetchall()
        contents = []
        for name, schema in tables:
            quoted = '"' + name.replace('"', '""') + '"'
            rows = sorted(connection.execute('SELECT * FROM ' + quoted).fetchall(), key=repr)
            contents.append((name, schema, rows))
        return hashlib.sha256(repr(contents).encode()).hexdigest()

command(['python3', fixtures / 'prepare.py', root])
source_hash = logical_database_hash(root / 'config/readarr.db')
shutil.copyfile(fixtures / 'provider.mjs', root / 'provider/server.mjs')
(root / 'provenance.json').write_text(json.dumps({
    'commit': command(['git', '-C', repo, 'rev-parse', 'HEAD']).strip(),
    'candidate': str(candidate), 'candidateExecutableSha256': hashlib.sha256((candidate / 'Chaptarr').read_bytes()).hexdigest(),
    'harnessDirty': bool(command(['git', '-C', repo, 'status', '--porcelain', '--', 'integration/acquisition', 'integration/run-acquisition-smoke.py']).strip()),
    'images': images,
    'imageIds': {key: command(['docker', 'image', 'inspect', '--format', '{{.Id}}', value]).strip() for key, value in images.items()},
}, indent=2))
try:
    start_stack()
    seerr('request.mjs', 'request.log')
    seerr('verify.mjs', 'acquisition.log', '--transfer')
    command(['python3', fixtures / 'verify-content.py', root], 'content.log')
    stop_stack('first-run')
    start_stack()
    seerr('verify.mjs', 'restart.log')
    command(['python3', fixtures / 'verify-content.py', root], 'restart-content.log')
    stop_stack('restarted')
    backups = list((root / 'config').glob('readarr.db.bookshelfng-backup-*.db'))
    assert len(backups) == 1
    # SQLite's backup API can change page headers; compare schema and every row,
    # including sqlite_sequence, rather than requiring identical physical bytes.
    assert logical_database_hash(backups[0]) == source_hash
    for database, version in [(root / 'config/readarr.db', 125), (backups[0], 158)]:
        with sqlite3.connect(f'file:{database}?mode=ro', uri=True) as connection:
            assert connection.execute('PRAGMA integrity_check').fetchall() == [('ok',)]
            assert connection.execute('PRAGMA foreign_key_check').fetchall() == []
            assert connection.execute('SELECT MAX(Version) FROM VersionInfo').fetchone()[0] == version
    (root / 'result.json').write_text(json.dumps({'migration': True, 'seerrAdapterRequest': True, 'qBittorrentTransfer': True, 'import': True, 'restartAvailability': True, 'contentPreserved': True}))
    print('PASS: migration, Seerr adapter request, real local torrent transfer, EPUB import and restart availability. Evidence:', root)
finally:
    stop_stack('final-run')
