import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('verify-producer.py')


class ProducerTrustTests(unittest.TestCase):
    def setUp(self):
        self.run = {'head_repository': {'full_name': 'snapetech-org/chaptarrng'}, 'head_branch': 'main',
                    'event': 'workflow_dispatch', 'path': '.github/workflows/standalone-asset-validation.yml',
                    'head_sha': 'a' * 40}
        self.jobs = {'jobs': [{'name': 'Build and verify win-x64 archive', 'conclusion': 'success'}]}

    def verify(self, run=None, jobs=None, optimized=False):
        with tempfile.TemporaryDirectory(prefix='standalone-producer-guard-') as directory:
            root = Path(directory)
            (root / 'run.json').write_text(json.dumps(run or self.run))
            (root / 'jobs.json').write_text(json.dumps(jobs or self.jobs))
            return subprocess.run([sys.executable, *(['-O'] if optimized else []), str(SCRIPT), '--run', str(root / 'run.json'),
                                   '--jobs', str(root / 'jobs.json'), '--repository', 'snapetech-org/chaptarrng',
                                   '--runtime-id', 'win-x64'], capture_output=True, text=True)

    def test_successful_same_repository_main_archive_is_addressable_before_unrelated_jobs_finish(self):
        run = copy.deepcopy(self.run)
        run['status'] = 'in_progress'
        result = self.verify(run)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), 'a' * 40)

    def test_rejects_fork_wrong_branch_event_workflow_and_malformed_revision(self):
        variants = [dict(head_repository={'full_name': 'untrusted/fork'}), dict(head_branch='feature'),
                    dict(event='pull_request'), dict(path='.github/workflows/other.yml'),
                    dict(head_sha='a' * 40 + '\nINJECTED=1')]
        for change in variants:
            with self.subTest(change=change):
                run = {**self.run, **change}
                self.assertNotEqual(self.verify(run).returncode, 0)

    def test_optimized_python_cannot_disable_the_provenance_checks(self):
        run = {**self.run, 'head_repository': {'full_name': 'untrusted/fork'}}
        self.assertNotEqual(self.verify(run, optimized=True).returncode, 0)

    def test_rejects_failed_missing_and_ambiguous_archive_jobs(self):
        for jobs in [{'jobs': []}, {'jobs': [{'name': 'Build and verify win-x64 archive', 'conclusion': 'failure'}]},
                     {'jobs': self.jobs['jobs'] * 2}]:
            with self.subTest(jobs=jobs):
                self.assertNotEqual(self.verify(jobs=jobs).returncode, 0)


if __name__ == '__main__':
    unittest.main()
