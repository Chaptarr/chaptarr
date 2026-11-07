import contextlib
import copy
import io
import json
import os
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch


class RuntimePackReuseTest(unittest.TestCase):
    def setUp(self):
        self.responses = {
            'actions/runs/123': {'head_repository': {'full_name': 'owner/repo'}, 'event': 'workflow_dispatch', 'head_branch': 'main', 'path': '.github/workflows/standalone-asset-validation.yml', 'head_sha': 'source-sha'},
            'actions/runs/123/jobs?per_page=100': {'jobs': [{'name': 'Build Linux x86 and FreeBSD runtime packs', 'conclusion': 'success'}]},
            'actions/runs/123/artifacts?per_page=100': {'artifacts': [{'name': 'chaptarrng-platform-runtime-packs', 'expired': False, 'id': 42, 'digest': 'sha256:fixture'}]},
        }

    def run_verifier(self, run_id='123'):
        def response(argv, **kwargs):
            if argv[:2] == ['git', 'hash-object']:
                return 'matching-blob\n'
            self.assertEqual(argv[:2], ['gh', 'api'])
            endpoint = argv[2].removeprefix('repos/owner/repo/')
            return json.dumps({'sha': 'matching-blob'} if endpoint.startswith('contents/') else self.responses[endpoint])

        script = Path(__file__).with_name('verify-runtime-pack-reuse.py')
        with tempfile.TemporaryDirectory(prefix='chaptarr-runtime-reuse-') as directory:
            output = Path(directory) / 'provenance.json'
            with patch.dict(os.environ, {'GITHUB_REPOSITORY': 'owner/repo'}), patch('sys.argv', [str(script), run_id, str(output)]), patch('subprocess.check_output', side_effect=response), contextlib.redirect_stdout(io.StringIO()):
                runpy.run_path(str(script), run_name='__main__')
            return json.loads(output.read_text())

    def test_matching_successful_producer_records_original_identity(self):
        result = self.run_verifier()
        self.assertEqual(result['artifactId'], 42)
        self.assertEqual(result['sourceCommit'], 'source-sha')
        self.assertEqual(len(result['producerInputBlobs']), 3)

    def test_rejects_untrusted_or_different_workflow_sources(self):
        original = copy.deepcopy(self.responses)
        for key, value in [('head_repository', {'full_name': 'fork/repo'}), ('event', 'pull_request'), ('head_branch', 'feature'), ('path', '.github/workflows/build.yml')]:
            with self.subTest(key=key):
                self.responses = copy.deepcopy(original)
                self.responses['actions/runs/123'][key] = value
                with self.assertRaisesRegex(SystemExit, 'trusted main'):
                    self.run_verifier()

    def test_rejects_failed_producer_even_when_other_jobs_pass(self):
        self.responses['actions/runs/123/jobs?per_page=100']['jobs'][0]['conclusion'] = 'failure'
        with self.assertRaisesRegex(SystemExit, 'successfully'):
            self.run_verifier()

    def test_rejects_changed_producer_input(self):
        with patch('subprocess.check_output', side_effect=lambda argv, **kwargs: 'changed-blob' if argv[:2] == ['git', 'hash-object'] else json.dumps({'sha': 'old-blob'} if '/contents/' in argv[2] else self.responses[argv[2].removeprefix('repos/owner/repo/')])):
            with tempfile.TemporaryDirectory(prefix='chaptarr-runtime-reuse-') as directory:
                script = Path(__file__).with_name('verify-runtime-pack-reuse.py')
                with patch.dict(os.environ, {'GITHUB_REPOSITORY': 'owner/repo'}), patch('sys.argv', [str(script), '123', str(Path(directory) / 'result.json')]), self.assertRaisesRegex(SystemExit, 'input changed'):
                    runpy.run_path(str(script), run_name='__main__')

    def test_rejects_expired_artifact(self):
        self.responses['actions/runs/123/artifacts?per_page=100']['artifacts'][0]['expired'] = True
        with self.assertRaisesRegex(SystemExit, 'unexpired'):
            self.run_verifier()

    def test_rejects_non_numeric_id_before_network_access(self):
        with self.assertRaisesRegex(SystemExit, 'positive integer'):
            self.run_verifier('123;echo injected')


if __name__ == '__main__':
    unittest.main()
