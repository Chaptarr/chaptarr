import importlib.util
import os
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("core_runner", Path(__file__).with_name("run-core-tests.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def fixture(name, count=1, serial=False, state="Runnable"):
    return {"serial": serial, "cases": [{"fixture": name, "name": f"test_{i}", "state": state} for i in range(count)]}


class CoreRunnerTests(unittest.TestCase):
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
