#!/usr/bin/env python3
"""Capture a matched BookshelfNG container's metadata environment privately."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def capture(container, data_directory, output):
    source = data_directory.resolve(strict=True)
    destination = output.parent.resolve(strict=True) / output.name
    if destination.is_relative_to(source):
        raise ValueError('Metadata environment output must be outside source appdata')
    if destination.exists() or destination.is_symlink():
        raise ValueError('Refusing to overwrite an existing metadata environment file')
    proof = destination.with_name(destination.name + '.source.json')
    if proof.exists() or proof.is_symlink():
        raise ValueError('Refusing to overwrite existing source provenance')

    result = subprocess.run(['docker', 'inspect', '--', container], capture_output=True, text=True)
    if result.returncode:
        raise ValueError('Could not inspect the selected source container')
    records = json.loads(result.stdout)
    if len(records) != 1:
        raise ValueError('Exactly one source container is required')
    record = records[0]
    mounts = [mount for mount in record.get('Mounts', []) if mount.get('Destination') == '/config']
    if len(mounts) != 1 or Path(mounts[0]['Source']).resolve() != source:
        raise ValueError('The source container does not mount the selected appdata at /config')

    values = [entry.split('=', 1)[1] for entry in record.get('Config', {}).get('Env', []) if entry.startswith('METADATA_URL=')]
    if len(values) > 1:
        raise ValueError('The source has duplicate metadata URL environment values')
    value = values[0] if values else ''
    if '\n' in value or '\r' in value or '\0' in value:
        raise ValueError('The metadata URL cannot be represented safely in a Docker environment file')

    def create_private(path, content):
        with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'w') as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())

    create_private(destination, 'METADATA_URL=' + value + '\n')
    create_private(proof, json.dumps({
        'sourceContainerId': record['Id'], 'sourceImageId': record['Image'],
        'metadataEnvironmentPresent': bool(values),
        'metadataEnvironmentSha256': hashlib.sha256(value.encode()).hexdigest(),
    }, indent=2) + '\n')
    return destination


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-container', required=True)
    parser.add_argument('--data-dir', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        path = capture(args.source_container, args.data_dir, args.output)
    except (ValueError, OSError, json.JSONDecodeError) as error:
        # Deliberately omit inspect payloads and URL values from diagnostics.
        raise SystemExit(str(error)) from None
    print('Captured private metadata environment:', path)
