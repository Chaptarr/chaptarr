import importlib.util
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('capture_bookshelf_metadata_env', Path(__file__).with_name('capture-bookshelf-metadata-env.py'))
capture_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture_module)


class MetadataEnvironmentCaptureTest(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='chaptarr-metadata-env-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.data = self.root / 'source appdata'
        self.data.mkdir()
        self.output = self.root / 'captured metadata.env'
        self.url = 'https://metadata.example/v1?token=fixture-only'
        self.record = {'Id': 'source-container', 'Image': 'sha256:source-image',
            'Mounts': [{'Source': str(self.data), 'Destination': '/config'}],
            'Config': {'Env': ['METADATA_URL=' + self.url, 'UNRELATED_PASSWORD=fixture-only']}}

    def capture(self):
        with patch.object(capture_module.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout=json.dumps([self.record]))):
            return capture_module.capture('source-container', self.data, self.output)

    def test_captures_inherited_value_without_unrelated_secrets(self):
        self.assertEqual(self.capture(), self.output)
        self.assertEqual(self.output.read_text(), 'METADATA_URL=' + self.url + '\n')
        proof = self.output.with_name(self.output.name + '.source.json')
        self.assertNotIn(self.url, proof.read_text())
        self.assertNotIn('UNRELATED_PASSWORD', self.output.read_text() + proof.read_text())
        if os.name == 'posix':
            self.assertEqual(self.output.stat().st_mode & 0o777, 0o600)
            self.assertEqual(proof.stat().st_mode & 0o777, 0o600)

    def test_missing_environment_does_not_invent_an_endpoint(self):
        self.record['Config']['Env'] = []
        self.capture()
        self.assertEqual(self.output.read_text(), 'METADATA_URL=\n')

    def test_rejects_unmatched_source_mount(self):
        self.record['Mounts'][0]['Source'] = str(self.root / 'different data')
        with self.assertRaisesRegex(ValueError, 'does not mount'):
            self.capture()
        self.assertFalse(self.output.exists())

    def test_rejects_duplicate_values_and_env_file_injection(self):
        for values in [['METADATA_URL=a', 'METADATA_URL=b'], ['METADATA_URL=fixture\nINJECTED=value']]:
            with self.subTest(values=values):
                self.record['Config']['Env'] = values
                with self.assertRaises(ValueError):
                    self.capture()
                self.assertFalse(self.output.exists())

    def test_does_not_write_inside_source_or_overwrite_files(self):
        self.output = self.data / 'metadata.env'
        with self.assertRaisesRegex(ValueError, 'outside source'):
            self.capture()
        self.output = self.root / 'already exists.env'
        self.output.write_text('retained')
        with self.assertRaisesRegex(ValueError, 'overwrite'):
            self.capture()
        self.assertEqual(self.output.read_text(), 'retained')


if __name__ == '__main__':
    unittest.main()
