#!/usr/bin/env python3
"""Permit runtime pack reuse only from a successful trusted matching producer."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys

run_id, output = sys.argv[1:]
if not re.fullmatch(r'[1-9][0-9]*', run_id):
    raise SystemExit('Runtime pack run ID must be a positive integer')
repository = os.environ['GITHUB_REPOSITORY']

def api(path):
    return json.loads(subprocess.check_output(['gh', 'api', f'repos/{repository}/{path}'], text=True))

run = api(f'actions/runs/{run_id}')
if not (
    run['head_repository']['full_name'] == repository
    and run['event'] == 'workflow_dispatch'
    and run['head_branch'] == 'main'
    and run['path'] == '.github/workflows/standalone-asset-validation.yml'
):
    raise SystemExit('Runtime pack source must be this repository\'s trusted main archive workflow')
jobs = api(f'actions/runs/{run_id}/jobs?per_page=100')['jobs']
producer = [job for job in jobs if job['name'] == 'Build Linux x86 and FreeBSD runtime packs']
if len(producer) != 1 or producer[0]['conclusion'] != 'success':
    raise SystemExit('The source runtime pack producer did not complete successfully')

inputs = {}
for file in [
    'packaging/scripts/build-extra-platform-runtime-packs.sh',
    'packaging/scripts/copy-dotnet-sdk-reference-packs.sh',
    'packaging/scripts/install-extra-platform-runtime-packs.sh',
]:
    current = subprocess.check_output(['git', 'hash-object', file], text=True).strip()
    original = api(f'contents/{file}?ref={run["head_sha"]}')['sha']
    if current != original:
        raise SystemExit(f'Runtime pack producer input changed: {file}')
    inputs[file] = current
artifacts = api(f'actions/runs/{run_id}/artifacts?per_page=100')['artifacts']
packs = [artifact for artifact in artifacts if artifact['name'] == 'chaptarrng-platform-runtime-packs' and not artifact['expired']]
if len(packs) != 1:
    raise SystemExit('Exactly one unexpired runtime pack artifact is required')
Path(output).write_text(json.dumps({
    'sourceRunId': run_id, 'sourceCommit': run['head_sha'],
    'artifactId': packs[0]['id'], 'artifactDigest': packs[0].get('digest'),
    'producerInputBlobs': inputs,
}, indent=2) + '\n')
print('Verified successful trusted runtime producer and unchanged input blobs:', run_id)
