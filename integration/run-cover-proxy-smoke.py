#!/usr/bin/env python3
"""Verify actual Seerr cover methods against a public original fixture image.

Only this synthetic fixture receives external network access. Never run this
with operator configuration, databases, credentials or media mounts.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--candidate', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--source-commit', required=True)
args = parser.parse_args()
assert re.fullmatch('[0-9a-f]{40}', args.source_commit)
candidate = args.candidate.resolve()
assert (candidate / 'Chaptarr').is_file()
root = args.output.resolve()
assert not root.exists()
fixtures = Path(__file__).resolve().parent / 'acquisition'
prefix = 'chaptarr-cover-' + uuid.uuid4().hex[:12]
owned = []
base = 'docker.io/snapetech/bookshelfng@sha256:40a53ac528b6789ca78686b4303bb6c436cd191cd813f915ea171d72b47a8c38'
seerr = 'ghcr.io/snapetech-org/seerrng@sha256:e433d13a2c50fdc96948361f3c090ed9bb12e8788fc6741337737c973e956aff'

def command(argv, log=None, timeout=180):
    result = subprocess.run([str(item) for item in argv], capture_output=True, text=True, timeout=timeout)
    if log:
        (root / log).write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f'Command failed; evidence: {log or argv[0]}')
    return result.stdout

command(['python3', fixtures / 'prepare.py', root])
config = root / 'config/config.xml'
config.write_text(config.read_text().replace('<AuthenticationMethod>External</AuthenticationMethod>', '<AuthenticationMethod>Forms</AuthenticationMethod>'))
url = f'https://raw.githubusercontent.com/snapetech-org/chaptarrng/{args.source_commit}/integration/acquisition/fixture-cover.png'
with sqlite3.connect(root / 'config/readarr.db') as connection:
    connection.executescript('DELETE FROM Indexers; DELETE FROM DownloadClients;')
    connection.execute('UPDATE Editions SET Images=?,Monitored=1 WHERE Id=41', (json.dumps([{'CoverType': 6, 'Url': url}]),))
    connection.execute('UPDATE AuthorMetadata SET Images=?', (json.dumps([{'CoverType': 1, 'Url': url}]),))
shutil.copyfile(fixtures / 'provider.mjs', root / 'provider/server.mjs')
for image in [base, seerr, 'node:22-alpine']:
    if subprocess.run(['docker', 'image', 'inspect', image], capture_output=True).returncode:
        command(['docker', 'pull', image], 'image-pull.log', 300)
security = ['--user', f'{os.getuid()}:{os.getgid()}', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--memory', '2g', '--cpus', '2']

def start(name, options):
    owned.append(name)
    command(['docker', 'run', '-d', '--name', name, *security, *options])

try:
    start(prefix + '-app', ['--network', 'bridge', '--tmpfs', '/run/readarr-temp:rw,nosuid,nodev,size=256m,mode=1777',
        '-e', 'HARDCOVER=true', '-e', 'HARDCOVER_NATIVE=', '-e', 'METADATA_URL=http://127.0.0.1:8980',
        '-e', 'CHAPTARR__AUTH__SEERRAPIKEY=synthetic-e2e-restricted-key-20261105',
        '-v', f'{candidate}:/candidate:ro', '-v', f'{root}/config:/config', '-v', f'{root}/library:/library',
        '--entrypoint', '/candidate/Chaptarr', base, '-nobrowser', '-data=/config'])
    start(prefix + '-provider', ['--network', 'container:' + prefix + '-app', '-v', f'{root}:/fixture', 'node:22-alpine', 'node', '/fixture/provider/server.mjs'])
    owned.append(prefix + '-seerr')
    command(['docker', 'run', '--rm', '--name', prefix + '-seerr', '--network', 'container:' + prefix + '-app', *security,
        '-v', f'{fixtures}:/checks:ro', '-v', f'{root}:/fixture:ro', '--entrypoint', 'node', seerr, '/checks/verify-cover.mjs'], 'cover-results.log', 300)
    (root / 'provenance.json').write_text(json.dumps({'sourceCommit': args.source_commit, 'fixtureUrl': url,
        'candidateAssemblySha256': {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in sorted(candidate.glob('*.dll'))}}, indent=2))
    print('PASS: actual Seerr author/book cover methods, source PNG hashes and authentication boundaries. Evidence:', root)
finally:
    for name in reversed(owned):
        result = subprocess.run(['docker', 'logs', name], capture_output=True, text=True)
        (root / (name + '.log')).write_text(result.stdout + result.stderr)
        subprocess.run(['docker', 'stop', '-t', '10', name], capture_output=True)
        subprocess.run(['docker', 'rm', name], capture_output=True)
