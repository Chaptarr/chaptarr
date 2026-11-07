"""Tag writing is enabled: reading content must survive the metadata update."""
from pathlib import Path
import hashlib
import json
import sys
import zipfile
import xml.etree.ElementTree as ET

root = Path(sys.argv[1])
manifest = json.loads((root / 'manifest.json').read_text())
assert hashlib.sha256((root / 'downloads' / manifest['filename']).read_bytes()).hexdigest() == manifest['sha256']
if manifest['mediaType'] == 'audiobook':
    files = list((root / 'library').rglob('*.mp3'))
    assert len(files) == 1
    # Audio tagging is disabled by the retained default; require exact bytes.
    assert hashlib.sha256(files[0].read_bytes()).hexdigest() == manifest['sha256']
    print(json.dumps({'audioBytesUnchanged': True, 'downloadHashUnchanged': True}))
    sys.exit(0)
files = list((root / 'library').rglob('*.epub'))
assert len(files) == 1
with zipfile.ZipFile(root / 'seed' / manifest['filename']) as source, zipfile.ZipFile(files[0]) as target:
    assert set(source.namelist()) == set(target.namelist())
    for name in source.namelist():
        if name != 'EPUB/package.opf':
            assert source.read(name) == target.read(name), name
    original = ET.fromstring(source.read('EPUB/package.opf'))
    imported = ET.fromstring(target.read('EPUB/package.opf'))
    ns = {'opf': 'http://www.idpf.org/2007/opf', 'dc': 'http://purl.org/dc/elements/1.1/'}
    for field in ['identifier', 'title', 'creator', 'language']:
        assert original.find('opf:metadata/dc:' + field, ns).text == imported.find('opf:metadata/dc:' + field, ns).text
    metadata = {item.attrib.get('name'): item.attrib.get('content') for item in imported.findall('opf:metadata/opf:meta', ns)}
    assert metadata['calibre:series'] == 'Fixture Series'
    assert metadata['calibre:series_index'] == '1'
print(json.dumps({'readingContentUnchanged': True, 'downloadHashUnchanged': True, 'configuredSeriesTagsWritten': True}))
