#!/usr/bin/env python3
"""Verify the trusted archive-producing job before consuming its retained payload."""
import argparse
import json
from pathlib import Path
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--run', type=Path, required=True)
parser.add_argument('--jobs', type=Path, required=True)
parser.add_argument('--repository', required=True)
parser.add_argument('--runtime-id', required=True)
args = parser.parse_args()
run = json.loads(args.run.read_text(encoding='utf-8-sig'))
jobs = json.loads(args.jobs.read_text(encoding='utf-8-sig'))['jobs']
if run['head_repository']['full_name'] != args.repository:
    raise ValueError('Producer repository differs from the trusted repository')
if run['head_branch'] != 'main' or run['event'] != 'workflow_dispatch':
    raise ValueError('Producer must be a trusted main-branch manual validation run')
if run['path'].split('@', 1)[0] != '.github/workflows/standalone-asset-validation.yml':
    raise ValueError('Unexpected archive producer workflow')
if not re.fullmatch('[0-9a-f]{40}', run['head_sha']):
    raise ValueError('Malformed producer revision')
matches = [job for job in jobs if job['name'] == 'Build and verify ' + args.runtime_id + ' archive']
if len(matches) != 1 or matches[0]['conclusion'] != 'success':
    raise ValueError('Producer archive job has not succeeded')
print(run['head_sha'])
