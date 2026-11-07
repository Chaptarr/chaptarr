#!/usr/bin/env python3
"""Regression tests for the manual systemd handoff confirmation."""

from __future__ import annotations

import importlib.util
import io
from pathlib import Path
import sys
import unittest
from types import SimpleNamespace
from unittest import mock


BOOTSTRAP_PATH = Path(__file__).with_name("bookshelfng-systemd-bootstrap.py")
SPEC = importlib.util.spec_from_file_location("bookshelfng_systemd_bootstrap", BOOTSTRAP_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Could not load the systemd BookshelfNG bootstrap module.")
BOOTSTRAP = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = BOOTSTRAP
SPEC.loader.exec_module(BOOTSTRAP)


class TerminalInput(io.StringIO):
    def isatty(self) -> bool:
        return True


class TerminalOutput(io.StringIO):
    def isatty(self) -> bool:
        return True


class SystemdBootstrapPromptTests(unittest.TestCase):
    def test_yes_and_yes_word_accept_the_handoff(self) -> None:
        for response in ("y\n", "yes\n"):
            with self.subTest(response=response):
                output = TerminalOutput()
                self.assertTrue(
                    BOOTSTRAP.confirm_handoff(
                        "ask",
                        input_stream=TerminalInput(response),
                        output_stream=output,
                    )
                )
                self.assertIn("Replace BookshelfNG with ChaptarrNG", output.getvalue())

    def test_no_blank_and_eof_decline_without_acquiring_handoff_lock(self) -> None:
        for response in ("n\n", "no\n", "\n", ""):
            with self.subTest(response=response):
                output = TerminalOutput()
                with (
                    mock.patch.object(BOOTSTRAP.os, "geteuid", return_value=0),
                    mock.patch.object(BOOTSTRAP, "validate_service_name", return_value="bookshelfng.service"),
                    mock.patch.object(BOOTSTRAP, "acquire_service_handoff_lock") as acquire_lock,
                    mock.patch.object(BOOTSTRAP, "install_mode_locked") as install_locked,
                    mock.patch.object(BOOTSTRAP.sys, "stdin", TerminalInput(response)),
                    mock.patch.object(BOOTSTRAP.sys, "stderr", output),
                ):
                    result = BOOTSTRAP.install_mode(SimpleNamespace(
                        service="bookshelfng.service",
                        migration_mode="ask",
                    ))

                self.assertEqual(result, 0)
                acquire_lock.assert_not_called()
                install_locked.assert_not_called()
                self.assertIn("installation and data remain unchanged", output.getvalue())

    def test_ask_fails_closed_without_a_terminal(self) -> None:
        with self.assertRaisesRegex(BOOTSTRAP.HandoffError, "No interactive terminal"):
            BOOTSTRAP.confirm_handoff(
                "ask",
                input_stream=io.StringIO("y\n"),
                output_stream=io.StringIO(),
            )

    def test_automatic_mode_is_an_explicit_noninteractive_yes(self) -> None:
        self.assertTrue(
            BOOTSTRAP.confirm_handoff(
                "automatic",
                input_stream=io.StringIO(""),
                output_stream=io.StringIO(),
            )
        )

    def test_unrecognized_answer_fails_without_claiming_yes(self) -> None:
        with self.assertRaisesRegex(BOOTSTRAP.HandoffError, "answer y or n"):
            BOOTSTRAP.confirm_handoff(
                "ask",
                input_stream=TerminalInput("maybe\n"),
                output_stream=TerminalOutput(),
            )


if __name__ == "__main__":
    unittest.main()
