#!/usr/bin/env python3
"""Run the discovered Core fixtures in isolated, serial test processes."""

import argparse
import collections
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET


TRX = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def result_name(value):
    # TRX escapes supplementary characters as UTF-16 surrogate pairs.
    return "".join(json.dumps(char)[1:-1] if ord(char) > 0xFFFF else char for char in value)


def fixture_name(value):
    return value.split("`", 1)[0].split("<", 1)[0].split("(", 1)[0]


def discover_fixtures(path):
    fixtures = {}

    def visit(node, inherited_state="Runnable"):
        state = node.get("runstate", inherited_state)
        if inherited_state != "Runnable":
            state = inherited_state
        if node.tag == "test-suite" and node.get("type") == "TestFixture":
            name = fixture_name(node.get("classname"))
            fixture = fixtures.setdefault(name, {"cases": [], "serial": False})
            fixture["serial"] |= any(p.get("name") == "ParallelScope" and p.get("value") == "None"
                                     for p in node.iter("property"))
            for case in node.iter("test-case"):
                fixture["cases"].append({"fixture": node.get("fullname"), "name": result_name(case.get("name")),
                                         "state": state if state != "Runnable" else case.get("runstate", state)})
            return
        for child in node:
            visit(child, state)

    visit(ET.parse(path).getroot())
    if not fixtures or None in fixtures:
        raise ValueError("Discovery did not return named fixtures")
    return fixtures


def read_results(path):
    root = ET.parse(path).getroot()
    definitions = {test.get("id"): test.find("t:TestMethod", TRX).get("className")
                   for test in root.findall(".//t:UnitTest", TRX)}
    results = []
    for test in root.findall(".//t:UnitTestResult", TRX):
        hours, minutes, seconds = test.get("duration", "00:00:00").split(":")
        results.append({"fixture": definitions[test.get("testId")], "name": result_name(test.get("testName")),
                        "outcome": test.get("outcome"), "seconds": int(hours) * 3600 + int(minutes) * 60 + float(seconds),
                        "reason": test.findtext("t:Output/t:StdOut", default="", namespaces=TRX)})
    return results


def partition(fixtures, workers, timings=None):
    if workers < 1:
        raise ValueError("workers must be positive")
    groups = [[] for _ in range(workers)]
    weights = [0.0] * len(groups)
    timings = timings or {}
    weight = lambda name: timings.get(name, len(fixtures[name]["cases"]))
    for name in sorted(fixtures, key=lambda name: (-weight(name), name)):
        index = min(range(len(groups)), key=lambda i: weights[i])
        groups[index].append(name)
        weights[index] += weight(name)
    result = [sorted(group) for group in groups if group]
    assert collections.Counter(name for group in result for name in group) == collections.Counter(fixtures.keys())
    for index, group in enumerate(result):
        for name in group:
            for case in fixtures[name]["cases"]:
                full_name = case["fixture"] + "." + case["name"]
                selections = [i for i, names in enumerate(result) if any(candidate in full_name for candidate in names)]
                if selections != [index]:
                    raise ValueError(f"Fixture filter is ambiguous or incomplete for {full_name}: {selections}")
    return result


def verify_coverage(fixtures, results, baseline=None):
    expected = collections.Counter((case["fixture"], case["name"]) for value in fixtures.values() for case in value["cases"])
    explicit = collections.Counter((case["fixture"], case["name"]) for value in fixtures.values()
                                   for case in value["cases"] if case["state"] == "Explicit")
    actual = collections.Counter((case["fixture"], case["name"]) for case in results)
    missing, extra = expected - actual, actual - expected
    # The adapter omits Explicit tests from a filtered run; it must never execute them.
    if missing - explicit or extra:
        raise ValueError(f"Test coverage changed: missing={list((missing - explicit).items())[:10]}, extra={list(extra.items())[:10]}")
    if any(case["outcome"] == "Passed" and (case["fixture"], case["name"]) in explicit for case in results):
        raise ValueError("An explicit test ran during the full suite")
    if baseline is not None:
        old_passes = collections.Counter((case["fixture"], case["name"]) for case in baseline if case["outcome"] == "Passed")
        passes = collections.Counter((case["fixture"], case["name"]) for case in results if case["outcome"] == "Passed")
        if old_passes - passes:
            raise ValueError(f"Previously passing cases did not pass: {list((old_passes - passes).items())[:10]}")
    return {"discovered": sum(expected.values()), "reported": len(results),
            "explicitNotSelected": sum(missing.values()), "outcomes": dict(collections.Counter(case["outcome"] for case in results))}


def settings(path, work, fixture_names=None, discovery=False, case_count=1):
    root = ET.Element("RunSettings")
    run = ET.SubElement(root, "RunConfiguration")
    ET.SubElement(run, "MaxCpuCount").text = "1"
    if fixture_names:
        ET.SubElement(run, "TestCaseFilter").text = "|".join("FullyQualifiedName~" + name for name in fixture_names)
    nunit = ET.SubElement(root, "NUnit")
    ET.SubElement(nunit, "NumberOfTestWorkers").text = "0"
    ET.SubElement(nunit, "WorkDirectory").text = str(work)
    ET.SubElement(nunit, "ExplicitMode").text = "Strict"
    # Keep large selections intact instead of the adapter falling back to the whole assembly.
    ET.SubElement(nunit, "UseNUnitFilter").text = "true"
    ET.SubElement(nunit, "AssemblySelectLimit").text = str(case_count)
    if discovery:
        ET.SubElement(nunit, "DumpXmlTestDiscovery").text = "true"
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def group_rss(pid):
    if not Path("/proc").is_dir():
        return None
    total = 0
    for directory in Path("/proc").iterdir():
        if not directory.name.isdigit():
            continue
        try:
            stat = (directory / "stat").read_text().rsplit(")", 1)[1].split()
            if int(stat[2]) == pid:
                total += int((directory / "statm").read_text().split()[1]) * os.sysconf("SC_PAGE_SIZE")
        except (OSError, ValueError, IndexError):
            pass
    return total


def stop_process(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, check=False)
    else:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
    process.wait()


def run_process(command, cwd, environment, log, timeout, cancelled):
    started = time.monotonic()
    peak = 0
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen(command, cwd=cwd, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=os.name != "nt")
        try:
            while process.poll() is None:
                peak = max(peak, group_rss(process.pid) or 0)
                if cancelled.wait(1):
                    raise InterruptedError("Test run cancelled")
                if time.monotonic() - started > timeout:
                    raise TimeoutError(f"Test process exceeded {timeout} seconds: {log}")
            return {"exitCode": process.returncode, "seconds": time.monotonic() - started,
                    "peakProcessGroupRssBytes": peak or None}
        finally:
            stop_process(process)


def isolated_runtime(assembly, directory, workspace):
    # Use one real spelling for NUnit, the child process, and its temporary files,
    # including Windows short-name aliases and macOS temporary-directory links.
    directory, workspace = directory.resolve(strict=True), workspace.resolve(strict=True)
    runtime = directory / "runtime"
    # Independent files also isolate tests that change assembly timestamps.
    shutil.copytree(assembly.parent, runtime, ignore=shutil.ignore_patterns("Dump", "TestResults"))
    work, temp = workspace / "work", workspace / "temp"
    work.mkdir()
    temp.mkdir()
    environment = dict(os.environ, TMPDIR=str(temp), TMP=str(temp), TEMP=str(temp))
    return runtime / assembly.name, work, environment


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path, default=Path("_tests/net10.0/Chaptarr.Core.Test.dll"))
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--timeout", type=int, default=3600, help="Per-process deadline in seconds")
    parser.add_argument("--baseline", type=Path, help="Previous full TRX for verifying unchanged test coverage")
    parser.add_argument("--timings", type=Path, default=Path(__file__).with_name("core-test-times.json"),
                        help="Measured fixture durations used to balance the processes")
    parser.add_argument("--fixture", action="append", help="Exact fixture name for a targeted check")
    args = parser.parse_args()
    assembly, output = args.assembly.resolve(), args.results.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if not assembly.is_file() or args.workers < 1:
        parser.error("A built test assembly and a positive worker count are required")
    baseline = read_results(args.baseline) if args.baseline else None
    timings = json.loads(args.timings.read_text(encoding="utf-8"))
    cancelled = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: cancelled.set())
    repo = Path(__file__).resolve().parents[1]
    scratch_parent = repo / "_temp"
    scratch_parent.mkdir(exist_ok=True)
    started = time.monotonic()
    postgres_required = os.environ.get("CHAPTARR_REQUIRE_POSTGRES", "").lower() == "true"
    if postgres_required and not os.environ.get("CHAPTARR_TEST_POSTGRES_CONNECTION_STRING"):
        parser.error("PostgreSQL is required but its test connection is missing")
    summary = {"assemblySha256": hashlib.sha256(assembly.read_bytes()).hexdigest(), "workers": args.workers,
               "postgresRequired": postgres_required}
    with tempfile.TemporaryDirectory(prefix="core-test-shards-", dir=scratch_parent) as scratch:
        scratch = Path(scratch)
        with tempfile.TemporaryDirectory(prefix="discovery-", dir=scratch) as directory, tempfile.TemporaryDirectory(prefix="chaptarr-test-discovery-") as workspace:
            dll, work, environment = isolated_runtime(assembly, Path(directory), Path(workspace))
            configuration = output / "discovery.runsettings"
            settings(configuration, work, discovery=True)
            result = run_process(["dotnet", "vstest", str(dll), "--ListTests", "--Settings:" + str(configuration)],
                                 work, environment, output / "discovery.log", args.timeout, cancelled)
            if result["exitCode"]:
                raise RuntimeError("Test discovery failed; see discovery.log")
            shutil.copyfile(dll.parent / "Dump" / ("D_" + dll.name + ".dump"), output / "discovery.xml")
        fixtures = discover_fixtures(output / "discovery.xml")
        if args.fixture:
            fixtures = {fixture_name(name): fixtures[fixture_name(name)] for name in args.fixture}
            baseline = [case for case in baseline or [] if fixture_name(case["fixture"]) in fixtures] if baseline else None
        groups = partition(fixtures, args.workers, timings)
        write_json(output / "plan.json", {"fixtures": fixtures, "groups": groups})

        def execute(index, group):
            with tempfile.TemporaryDirectory(prefix=f"shard-{index}-", dir=scratch) as directory, tempfile.TemporaryDirectory(prefix="chaptarr-test-work-") as workspace:
                dll, work, environment = isolated_runtime(assembly, Path(directory), Path(workspace))
                shard_output = output / f"shard-{index}"
                shard_output.mkdir()
                configuration = shard_output / "tests.runsettings"
                settings(configuration, work, group, case_count=sum(len(fixtures[name]["cases"]) for name in group))
                if not any(case["state"] == "Runnable" for name in group for case in fixtures[name]["cases"]):
                    raise ValueError("Refusing a shard containing only explicit or excluded tests")
                command = ["dotnet", "vstest", str(dll), "--Settings:" + str(configuration),
                           "--ResultsDirectory:" + str(shard_output), "--Logger:trx;LogFileName=results.trx",
                           "--Logger:console;Verbosity=normal"]
                result = run_process(command, work, environment, shard_output / "console.log", args.timeout, cancelled)
                results = read_results(shard_output / "results.trx") if (shard_output / "results.trx").exists() else []
                try:
                    result["coverage"] = verify_coverage({name: fixtures[name] for name in group}, results)
                except ValueError as error:
                    result["coverageError"] = str(error)
                write_json(shard_output / "summary.json", result)
                print(f"Shard {index}: {result['seconds']:.1f}s, {dict(collections.Counter(case['outcome'] for case in results))}, exit {result['exitCode']}", flush=True)
                return result, results

        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
            futures = [pool.submit(execute, i, group) for i, group in enumerate(groups)]
            completed = [future.result() for future in futures]
        results = [case for _, cases in completed for case in cases]
        summary.update(seconds=time.monotonic() - started, shards=[result for result, _ in completed])
        try:
            summary["coverage"] = verify_coverage(fixtures, results, baseline)
            if postgres_required and any(case["outcome"] == "NotExecuted" and "postgres" in case["reason"].lower() for case in results):
                raise ValueError("Required PostgreSQL coverage was skipped")
        except ValueError as error:
            summary["coverageError"] = str(error)
        write_json(output / "summary.json", summary)
        write_json(output / "case-results.json", results)
        measured = collections.defaultdict(float)
        for case in results:
            measured[fixture_name(case["fixture"])] += case["seconds"]
        write_json(output / "fixture-times.json", {name: round(seconds, 6) for name, seconds in sorted(measured.items())})
        print(json.dumps(summary, indent=2))
        if "coverageError" in summary or any(result["exitCode"] or "coverageError" in result for result, _ in completed) or any(case["outcome"] not in ("Passed", "NotExecuted") for case in results):
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
