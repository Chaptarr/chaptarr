#!/usr/bin/env python3
"""Verify preserved format-specific author profiles after fixture consumers exit."""
import json
from pathlib import Path
import sqlite3
import sys

root = Path(sys.argv[1]).resolve()
phase = sys.argv[2]
assert phase in ['import', 'restart']
manifest = json.loads((root / 'manifest.json').read_text())
with sqlite3.connect((root / 'config/readarr.db').as_uri() + '?mode=ro', uri=True) as connection:
    # These are the expected mappings of the two distinct source profiles, not
    # assumptions derived from the IDs supplied to the request being tested.
    profiles = connection.execute('SELECT AudiobookQualityProfileId,EbookQualityProfileId,AudiobookMetadataProfileId,EbookMetadataProfileId FROM Authors WHERE Id=20').fetchone()
    assert profiles == (1, 4, 1, 2), 'Request changed an existing migrated author profile'
    for profile_id, expected_type in [(profiles[0], 1), (profiles[1], 2)]:
        assert connection.execute('SELECT ProfileType FROM QualityProfiles WHERE Id=?', (profile_id,)).fetchone() == (expected_type,)
    result = {'phase': phase, 'format': manifest['mediaType'], 'profileIdSpace': manifest['profileIdSpace'],
              'bothFormatProfilesPreserved': True, 'metadataProfilesPreserved': True, 'qualityProfileTypesCorrect': True}
(root / ('profile-' + phase + '-audit.json')).write_text(json.dumps(result, indent=2) + '\n')
print('Existing migrated author: both format quality and metadata profiles preserved with correct profile types.')
