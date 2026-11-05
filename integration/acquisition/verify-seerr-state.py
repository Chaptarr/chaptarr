#!/usr/bin/env python3
"""Audit persisted synthetic Seerr request identity after all consumers exit."""
import json
from pathlib import Path
import sqlite3
import sys

root = Path(sys.argv[1]).resolve()
phase = sys.argv[2] if len(sys.argv) > 2 else 'import'
assert phase in ['import', 'restart']
manifest = json.loads((root / 'manifest.json').read_text())
plan = json.loads((root / 'web-request.json').read_text())
with sqlite3.connect((root / 'seerr-config/db/db.sqlite3').as_uri() + '?mode=ro', uri=True) as connection:
    connection.execute('BEGIN')
    query = ('SELECT id FROM media WHERE mediaType=? AND externalServiceId=?'
             if manifest['mediaType'] == 'ebook' else
             'SELECT id FROM media WHERE mediaType=? AND audiobookExternalServiceId=?')
    matches = connection.execute(query, ('book', manifest['bookId'])).fetchall()
    request = connection.execute('SELECT mediaId,requestedById,status FROM media_request WHERE id=?', (plan['requestId'],)).fetchone()
    orphans = connection.execute('SELECT COUNT(*) FROM media_identifier i LEFT JOIN media m ON m.id=i.mediaId WHERE m.id IS NULL').fetchone()[0]
    result = {'phase': phase, 'format': manifest['mediaType'], 'matchingMediaRecords': len(matches), 'orphanedIdentifiers': orphans,
              'originalMediaRecordRetained': matches == [(plan['mediaDatabaseId'],)],
              'requestAndRequesterRetained': request is not None and request[:2] == (plan['mediaDatabaseId'], plan['requesterId']),
              'requestCompleted': request is not None and request[2] == 5}
(root / ('web-' + phase + '-database-audit.json')).write_text(json.dumps(result, indent=2) + '\n')
assert result['originalMediaRecordRetained'], 'Scan duplicated or replaced the original request media record'
assert result['orphanedIdentifiers'] == 0, 'Dispatch produced unowned identifiers'
assert result['requestAndRequesterRetained'] and result['requestCompleted']
print('Persisted Seerr request: original media/request/requester retained, completed, zero duplicate books or orphaned identifiers.')
