#!/usr/bin/env python3
"""A dead systemd main process must release the migration health hook."""
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest


HEALTHCHECK = Path(__file__).resolve().parents[1] / 'legacy/scripts/bookshelfng-healthcheck'


@unittest.skipUnless(os.name == 'posix', 'Native package health hooks require POSIX')
class HealthcheckProcessTests(unittest.TestCase):
    def check_dead_service(self, listener):
        with tempfile.TemporaryDirectory(prefix='bookshelfng-health-process-') as directory:
            root = Path(directory)
            data = root / 'data'
            data.mkdir()
            (data / '.chaptarrng-bookshelfng-migration-pending').touch()
            (root / 'enforce').write_text('1' if listener else '0')
            (root / 'tcp').write_text('sl local_address rem_address st\n')
            systemctl = root / 'systemctl'
            systemctl.write_text('#!/bin/sh\ncase "$*" in\n*FragmentPath*) echo /usr/lib/systemd/system/bookshelfng.service;;\nesac\n')
            app = root / 'Chaptarr'
            app.write_text('#!/bin/sh\ncase "$*" in\n*-bookshelf-health-url=*) touch "' + str(root / 'waiting') + '"; sleep 1; exit 1;;\nesac\nexit 0\n')
            for executable in (systemctl, app):
                executable.chmod(0o700)
            source = HEALTHCHECK.read_text().replace('data_dir=/var/lib/bookshelfng', 'data_dir="' + str(data) + '"')
            source = source.replace('/usr/lib/chaptarrng/bin/Chaptarr', str(app))
            source = source.replace('journal=/usr/lib/chaptarrng/handoff/bookshelfng-journal', 'journal=/bin/true')
            script = root / 'healthcheck'
            script.write_text(source)
            main = subprocess.Popen(['sleep', '120'])
            env = dict(os.environ, PATH=str(root) + ':' + os.environ['PATH'], MAINPID=str(main.pid),
                       CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS='60',
                       CHAPTARR_BOOKSHELF_HANDOFF_HEALTH_MODE='database-listener' if listener else '',
                       CHAPTARR_BOOKSHELF_SELINUX_ENFORCE_FILE=str(root / 'enforce'),
                       CHAPTARR_BOOKSHELF_PROC_NET_TCP_FILE=str(root / 'tcp'),
                       CHAPTARR_BOOKSHELF_PROC_NET_TCP6_FILE=str(root / 'tcp'))
            hook = subprocess.Popen(['/bin/sh', str(script)], env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                # Let the hook enter its inner listener/HTTP wait, then reap the
                # main process just as systemd does after an application crash.
                time.sleep(1)
                self.assertIsNone(hook.poll())
                main.kill()
                main.wait(timeout=5)
                hook.communicate(timeout=12)
                self.assertEqual(hook.returncode, 1)
                self.assertEqual((data / '.chaptarrng-bookshelfng-handoff-status').read_text().strip(), 'intervention-required')
                self.assertTrue((data / '.chaptarrng-bookshelfng-migration-pending').exists())
            finally:
                if main.poll() is None:
                    main.kill()
                main.wait(timeout=5)
                if hook.poll() is None:
                    hook.kill()
                hook.communicate(timeout=5)

    def test_http_wait_releases_after_service_death(self):
        self.check_dead_service(False)

    def test_selinux_listener_wait_releases_after_service_death(self):
        self.check_dead_service(True)


if __name__ == '__main__':
    unittest.main()
