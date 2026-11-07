#!/usr/bin/env python3
"""Exercise the shipped supervisor's process-group termination with a real shell."""
from pathlib import Path
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest

SUPERVISOR = Path(__file__).resolve().parents[1] / 'legacy/desktop/bookshelfng-supervisor.sh'


@unittest.skipUnless(os.name == 'posix', 'The desktop shell supervisor runs on POSIX hosts')
class SupervisorSignalTests(unittest.TestCase):
    def check_group_stops(self, ignore_term):
        self.assertIsNotNone(shutil.which('ps'), 'Process inspection is a supervisor runtime prerequisite')
        source = SUPERVISOR.read_text()
        functions = source[source.index('process_is_running() {'):source.index('lock_owner_is_running() {')]
        with tempfile.TemporaryDirectory(prefix='desktop-supervisor-signals-') as directory:
            ready = Path(directory) / 'grandchild-ready'
            child_source = (
                'import os,signal,time; from pathlib import Path; '
                + ('signal.signal(signal.SIGTERM,signal.SIG_IGN); ' if ignore_term else '')
                + 'Path(' + repr(str(ready)) + ').write_text(str(os.getpid())); time.sleep(120)'
            )
            parent_source = (
                'import signal,subprocess,sys,time; '
                + ('signal.signal(signal.SIGTERM,signal.SIG_IGN); ' if ignore_term else '')
                + 'subprocess.Popen([sys.executable,"-c",' + repr(child_source) + ']); time.sleep(120)'
            )
            process = subprocess.Popen([sys.executable, '-c', parent_source], start_new_session=True)
            try:
                deadline = time.monotonic() + 10
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(.05)
                self.assertTrue(ready.exists(), 'Test descendant did not start')
                self.assertNotEqual(os.getpgrp(), process.pid, 'Test group must be isolated from its caller')
                self.assertEqual(os.getpgid(int(ready.read_text())), process.pid)
                # Invoke the actual shipped functions; keep their real TERM-to-KILL deadline.
                command = functions + '\napp_pid=$1\napp_process_group=$1\nterminate_app_tree\n'
                subprocess.run([os.environ.get('DESKTOP_TEST_SHELL', '/bin/sh'), '-eu', '-c', command,
                                'supervisor-signal-test', str(process.pid)], check=True, timeout=45)
                process.wait(timeout=5)
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline:
                    groups = subprocess.check_output(['ps', '-eo', 'pgid=,stat='], text=True)
                    live = [line for line in groups.splitlines()
                            if line.split()[0] == str(process.pid) and not line.split()[1].startswith('Z')]
                    if not live:
                        break
                    time.sleep(.05)
                self.assertFalse(live, 'Supervisor left a live application descendant')
                self.assertEqual(process.returncode, -signal.SIGKILL if ignore_term else -signal.SIGTERM)
            finally:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=10)

    def test_term_stops_the_application_and_its_descendants(self):
        self.check_group_stops(False)

    def test_kill_stops_descendants_that_ignore_term(self):
        self.check_group_stops(True)


if __name__ == '__main__':
    unittest.main()
