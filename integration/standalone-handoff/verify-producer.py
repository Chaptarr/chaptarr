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
assert run['head_repository']['full_name'] == args.repository
assert run['head_branch'] == 'main' and run['event'] == 'workflow_dispatch'
assert run['path'].split('@', 1)[0] == '.github/workflows/standalone-asset-validation.yml'
assert re.fullmatch('[0-9a-f]{40}', run['head_sha'])
matches = [job for job in jobs if job['name'] == 'Build and verify ' + args.runtime_id + ' archive']
assert len(matches) == 1 and matches[0]['conclusion'] == 'success', 'Producer archive job has not succeeded'
print(run['head_sha'])
