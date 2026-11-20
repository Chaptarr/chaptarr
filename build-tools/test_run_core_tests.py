import importlib.util
import os
import subprocess
import sys
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("core_runner", Path(__file__).with_name("run-core-tests.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def fixture(name, count=1, serial=False, state="Runnable"):
    return {"serial": serial, "cases": [{"fixture": name, "name": f"test_{i}", "state": state} for i in range(count)]}


class CoreRunnerTests(unittest.TestCase):
    def test_symbol_selector_includes_helper_consumers_and_partials(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "Common").mkdir()
            (root / "Common/GateStub.cs").write_text("namespace Test.Common { class GateStub : IMetadataServerHealthGate {} }")
            (root / "Direct.cs").write_text("namespace Test { class DirectFixture { HttpTransportErrors h; } }")
            (root / "Author.Partial.cs").write_text("namespace Test { partial class AuthorFixture { GateStub h; } }")
            (root / "Unrelated.cs").write_text("namespace Test { class UnrelatedFixture {} }")
            discovered = ["Test.AuthorFixture", "Test.DirectFixture", "Test.UnrelatedFixture"]
            self.assertEqual(runner.fixtures_using(root, ["HttpTransportErrors", "IMetadataServerHealthGate"], discovered), ["Test.AuthorFixture", "Test.DirectFixture"])

    def test_symbol_selector_includes_all_fixture_declarations_and_deduplicates_partials(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Multiple.cs").write_text("namespace Test { class FirstFixture { Service s; } partial class SecondFixture { Service s; } }")
            (root / "Second.Partial.cs").write_text("namespace Test { partial class FirstFixture { Service s; } }")
            (root / "Unrelated.cs").write_text("namespace Test { class UnrelatedFixture {} }")
            discovered = ["Test.FirstFixture", "Test.SecondFixture", "Test.UnrelatedFixture"]
            self.assertEqual(runner.fixtures_using(root, ["Service"], discovered), ["Test.FirstFixture", "Test.SecondFixture"])

    def test_symbol_selector_follows_helpers_named_like_fixtures_wherever_they_live(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Common").mkdir()
            (root / "Common/DualDatabaseFixture.cs").write_text("namespace T.Common { static class DualDatabaseFixture { DbFactory f; } }")
            (root / "Uses.cs").write_text("namespace T { class UsesFixture { void M() { DualDatabaseFixture.Run(); } } }")
            # A nested helper inside another fixture's file, used by a third fixture.
            (root / "Owner.cs").write_text("namespace T { class OwnerFixture { internal sealed class SnapshotFixture { Rendition r; } } }")
            (root / "Consumer.cs").write_text("namespace T { class ConsumerFixture { OwnerFixture.SnapshotFixture s; } }")
            (root / "Unrelated.cs").write_text("namespace T { class UnrelatedFixture {} }")
            discovered = ["T.UsesFixture", "T.OwnerFixture", "T.ConsumerFixture", "T.UnrelatedFixture"]
            self.assertEqual(runner.fixtures_using(root, ["DbFactory"], discovered), ["T.UsesFixture"])
            self.assertEqual(runner.fixtures_using(root, ["Rendition"], discovered), ["T.ConsumerFixture", "T.OwnerFixture"])

    def test_symbol_selector_matches_discovered_fixtures_across_namespaces(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Mixed.cs").write_text(
                "namespace Test.Books { partial class BookFixture { Service s; } }\n"
                "namespace Test.Lists { partial class ListFixture { Service s; } }")
            discovered = ["Test.Books.BookFixture", "Test.Lists.ListFixture"]
            self.assertEqual(runner.fixtures_using(root, ["Service"], discovered), ["Test.Books.BookFixture", "Test.Lists.ListFixture"])

    def test_symbol_selector_does_not_follow_private_helpers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "A.cs").write_text("namespace T { class AFixture { private sealed class Stub { Service s; } } }")
            (root / "B.cs").write_text("namespace T { class BFixture { private sealed class Stub {} } }")
            discovered = ["T.AFixture", "T.BFixture"]
            self.assertEqual(runner.fixtures_using(root, ["Service"], discovered), ["T.AFixture"])

    def test_symbol_selector_ignores_constraint_keywords(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Generic.cs").write_text("namespace T { class GenericFixture { void M<X>() where X : class\n public Service s; } }")
            (root / "Other.cs").write_text("namespace T { public class OtherFixture {} }")
            discovered = ["T.GenericFixture", "T.OtherFixture"]
            self.assertEqual(runner.fixtures_using(root, ["Service"], discovered), ["T.GenericFixture"])

    def test_stale_assembly_is_detected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            assembly = root / "Tests.dll"
            assembly.write_text("")
            source = root / "src"
            source.mkdir()
            (source / "Old.cs").write_text("class OldFixture {}")
            os.utime(source / "Old.cs", (1, 1))
            self.assertIsNone(runner.newest_source_after(assembly, source))
            (source / "New.cs").write_text("class NewFixture {}")
            os.utime(source / "New.cs", (assembly.stat().st_mtime + 60,) * 2)
            self.assertEqual(runner.newest_source_after(assembly, source), source / "New.cs")

    def test_symbol_selection_rejects_empty_invalid_and_partial_names(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "A.cs").write_text("namespace T { class AFixture { ServiceExtra x; } }")
            self.assertEqual(runner.fixtures_using(root, ["Service"], ["T.AFixture"]), [])
            self.assertEqual(runner.fixtures_using(root, ["Absent"], ["T.AFixture"]), [])
            for symbols in ([], [""], ["Service.*"]):
                with self.assertRaises(ValueError):
                    runner.fixtures_using(root, symbols, ["T.AFixture"])

    def test_cli_rejects_empty_trailing_and_unmatched_symbols_without_traceback(self):
        with tempfile.TemporaryDirectory() as directory:
            for value in ("", "A,", "NoSuchConsumerSymbolForThisTest"):
                with self.subTest(value=value):
                    result = subprocess.run([sys.executable, runner.__file__, "--uses", value, "--results", directory], capture_output=True, text=True)
                    self.assertEqual(result.returncode, 2)
                    self.assertIn("error:", result.stderr)
                    self.assertNotIn("Traceback", result.stderr)
                    if value.startswith("NoSuch"):
                        self.assertIn("matched no fixtures", result.stderr)

    def test_isolated_runtime_resolves_aliases_before_setting_child_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            real = root / "Long runtime directory with spaces"
            real.mkdir()
            if os.name == "nt":
                import ctypes
                from ctypes import wintypes

                short_path = ctypes.WinDLL("kernel32", use_last_error=True).GetShortPathNameW
                short_path.argtypes = [wintypes.LPCWSTR, wintypes.LPWSTR, wintypes.DWORD]
                short_path.restype = wintypes.DWORD
                length = short_path(str(real), None, 0)
                if not length:
                    raise ctypes.WinError(ctypes.get_last_error())
                buffer = ctypes.create_unicode_buffer(length)
                if not short_path(str(real), buffer, length):
                    raise ctypes.WinError(ctypes.get_last_error())
                alias = Path(buffer.value)
                if alias == real:
                    self.skipTest("This volume does not provide an 8.3 alias for the test directory")
            else:
                alias = root / "alias"
                alias.symlink_to(real, target_is_directory=True)

            source, directory, workspace = real / "source", alias / "shard", alias / "workspace"
            for path in (source, directory, workspace):
                path.mkdir()
            assembly = source / "fixture.dll"
            assembly.write_bytes(b"independent runtime")
            dll, work, environment = runner.isolated_runtime(assembly, directory, workspace)

            self.assertEqual(dll, real / "shard" / "runtime" / assembly.name)
            self.assertEqual(work, real / "workspace" / "work")
            for key in ("TMPDIR", "TMP", "TEMP"):
                self.assertEqual(environment[key], str(real / "workspace" / "temp"))
            self.assertTrue(work.is_dir())
            self.assertTrue(Path(environment["TEMP"]).is_dir())
            self.assertEqual(dll.read_bytes(), assembly.read_bytes())
            dll.write_bytes(b"changed only in the shard")
            self.assertEqual(assembly.read_bytes(), b"independent runtime")
            configuration = root / "tests.runsettings"
            runner.settings(configuration, work)
            self.assertEqual(runner.ET.parse(configuration).findtext("NUnit/WorkDirectory"), str(work))

    def test_nonparallel_fixtures_can_use_isolated_processes_and_are_assigned_once(self):
        fixtures = {"A": fixture("A", 100), "B": fixture("B", 80, True), "C": fixture("C", 70, True),
                    "D": fixture("D", 50), "E": fixture("E", 20)}
        groups = runner.partition(fixtures, 3)
        self.assertNotEqual(next(i for i, group in enumerate(groups) if "B" in group),
                            next(i for i, group in enumerate(groups) if "C" in group))
        self.assertCountEqual([name for group in groups for name in group], fixtures)
        self.assertEqual(runner.partition(fixtures, 1), [sorted(fixtures)])

    def test_measured_durations_balance_work_instead_of_case_counts(self):
        fixtures = {name: fixture(name, 1 if name != "E" else 1000) for name in "ABCDE"}
        groups = runner.partition(fixtures, 3, {"A": 100, "B": 90, "C": 80, "D": 20, "E": 10})
        self.assertEqual(groups, [["A"], ["B", "E"], ["C", "D"]])

    def test_parameterized_fixtures_and_method_serial_attribute_survive_discovery(self):
        xml = '''<test-run><test-suite type="TestFixture" classname="N.F" fullname="N.F(False)" runstate="Runnable">
          <test-suite type="ParameterizedMethod"><properties><property name="ParallelScope" value="None"/></properties>
            <test-case name="a" runstate="Runnable"/></test-suite></test-suite>
          <test-suite type="TestFixture" classname="N.F" fullname="N.F(True)" runstate="Runnable">
            <test-case name="a" runstate="Runnable"/></test-suite></test-run>'''
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "discovery.xml"
            path.write_text(xml)
            fixtures = runner.discover_fixtures(path)
        self.assertTrue(fixtures["N.F"]["serial"])
        self.assertEqual([case["fixture"] for case in fixtures["N.F"]["cases"]], ["N.F(False)", "N.F(True)"])
        results = [{"fixture": name, "name": "a", "outcome": "Passed"} for name in ("N.F(False)", "N.F(True)")]
        self.assertEqual(runner.verify_coverage(fixtures, results)["discovered"], 2)

    def test_generic_fixture_filter_uses_its_displayed_type_name(self):
        self.assertEqual(runner.fixture_name("N.ProviderFixture`4"), "N.ProviderFixture")
        self.assertEqual(runner.fixture_name("N.ProviderFixture<A,B,C,D>"), "N.ProviderFixture")
        fixtures = {"N.ProviderFixture": fixture("N.ProviderFixture<A,B,C,D>"), "N.Other": fixture("N.Other")}
        self.assertEqual(len(runner.partition(fixtures, 2)), 2)

    def test_filters_cannot_select_another_shards_fixture(self):
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            runner.partition({"N.F": fixture("N.F"), "N.FExtra": fixture("N.FExtra")}, 2)

    def test_missing_or_duplicate_case_fails(self):
        fixtures = {"A": fixture("A", 2)}
        one = {"fixture": "A", "name": "test_0", "outcome": "Passed"}
        for results in ([one], [one, one]):
            with self.subTest(results=results), self.assertRaises(ValueError):
                runner.verify_coverage(fixtures, results)

    def test_supplementary_characters_use_the_same_test_identity_as_trx(self):
        self.assertEqual(runner.result_name('title("𝟚")'), 'title("\\ud835\\udfda")')
        self.assertEqual(runner.result_name('title("\\ud835\\udfda")'), 'title("\\ud835\\udfda")')
        self.assertNotEqual(runner.result_name('title("2")'), runner.result_name('title("𝟚")'))

    def test_settings_keep_the_filtered_case_list_and_serial_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tests.runsettings"
            runner.settings(path, Path(directory), ["N.F"], case_count=9000)
            root = runner.ET.parse(path).getroot()
        self.assertEqual(root.findtext("NUnit/UseNUnitFilter"), "true")
        self.assertEqual(root.findtext("NUnit/AssemblySelectLimit"), "9000")
        self.assertEqual(root.findtext("NUnit/NumberOfTestWorkers"), "0")
        self.assertEqual(root.findtext("RunConfiguration/TestCaseFilter"), "FullyQualifiedName~N.F")

    def test_explicit_case_can_be_unselected_but_never_run(self):
        fixtures = {"A": fixture("A"), "Live": fixture("Live", state="Explicit")}
        passed = {"fixture": "A", "name": "test_0", "outcome": "Passed"}
        self.assertEqual(runner.verify_coverage(fixtures, [passed])["explicitNotSelected"], 1)
        with self.assertRaisesRegex(ValueError, "explicit test ran"):
            runner.verify_coverage(fixtures, [passed, {"fixture": "Live", "name": "test_0", "outcome": "Passed"}])

    def test_new_skip_cannot_replace_a_previously_passing_test(self):
        fixtures = {"A": fixture("A")}
        passed = {"fixture": "A", "name": "test_0", "outcome": "Passed"}
        with self.assertRaisesRegex(ValueError, "Previously passing"):
            runner.verify_coverage(fixtures, [dict(passed, outcome="NotExecuted")], [passed])


if __name__ == "__main__":
    unittest.main()
