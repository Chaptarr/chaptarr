#!/usr/bin/env python3
"""Kill the native product and real conversion tools, then verify durable recovery.

Uses a new native database and an offline queued-job fixture. This does not test
HTTP acquisition/import, migrated operator data, or distribution installation.
The supplied application directory is mounted read-only and never modified.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import time
import uuid


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def command(arguments):
    result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=180)
    require(result.returncode == 0, f"Command failed ({result.returncode}): {arguments}\n{result.stderr}")
    return result.stdout


def wait_for(predicate, label, timeout=120):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            print(label, flush=True)
            return
        time.sleep(0.5)
    raise RuntimeError(f"Timed out: {label}")


def process_identity(pid):
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
        return fields[0], fields[19]
    except FileNotFoundError:
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate-directory", type=Path, required=True)
    parser.add_argument("--candidate-revision", required=True)
    parser.add_argument("--image", required=True, help="Previously built integration/Dockerfile.m4b-test image")
    parser.add_argument("--work-dir", type=Path, required=True, help="New task-owned directory; all evidence is retained")
    args = parser.parse_args()
    candidate = args.candidate_directory.resolve(strict=True)
    root = args.work_dir.resolve()
    require(not root.exists(), "Work directory already exists; preserve previous evidence")
    require(not root.is_relative_to(candidate), "Keep test outputs outside the application directory")
    executable = candidate / "Chaptarr"
    with executable.open("rb") as stream:
        elf = stream.read(20)
    require(elf[:5] == b"\x7fELF\x02" and elf[18:20] == b"\x3e\x00", "This tool image requires a native Linux x64 application")
    image_id = command(["docker", "image", "inspect", args.image, "--format", "{{.Id}}"]).strip()
    root.mkdir(parents=True, mode=0o700)
    evidence = root / "evidence"
    evidence.mkdir()
    task = uuid.uuid4().hex
    name = f"chaptarrng-m4b-recovery-{task[:12]}"
    result = {"success": False, "candidateRevision": args.candidate_revision, "fixture": "Synthetic offline queued conversion job; actual product process and real media tools"}
    created = False
    database = None

    def inspect():
        data = json.loads(command(["docker", "inspect", name]))[0]
        require(data["Config"]["Labels"].get("chaptarrng.acceptance-task") == task, "Container ownership changed")
        return data

    def logs(filename):
        data = subprocess.run(["docker", "logs", name], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        (evidence / filename).write_bytes(data.stdout)

    def job():
        with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
            connection.row_factory = sqlite3.Row
            row = connection.execute("SELECT * FROM ConversionJobs WHERE DownloadId=?", (task,)).fetchone()
            return dict(row) if row else None

    def healthy():
        probe = subprocess.run(["docker", "exec", name, "curl", "-fsS", "--max-time", "2", "http://127.0.0.1:18787/ping"], capture_output=True, timeout=10)
        return probe.returncode == 0 and json.loads(probe.stdout).get("status") == "OK"

    base = ["docker", "run", "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "--memory", "2g", "--cpus", "2", "--user", f"{os.getuid()}:{os.getgid()}",
            "--label", f"chaptarrng.acceptance-task={task}", "--volume", f"{root}:/acceptance"]
    try:
        (evidence / "provenance.json").write_text(json.dumps({
            "candidateRevision": args.candidate_revision, "candidateDirectory": str(candidate),
            "applicationHashes": {file: sha256(candidate / file) for file in ["Chaptarr", "Chaptarr.dll", "Chaptarr.Core.dll", "Chaptarr.Host.dll", "Chaptarr.Api.V1.dll"]},
            "toolImage": args.image, "toolImageId": image_id,
            "scope": result["fixture"], "installedArchiveAcceptance": False,
        }, indent=2) + "\n")
        config, sources, tool = (root / part for part in ["config", "source", "tool"])
        for directory in [config, sources, tool]:
            directory.mkdir()
        (config / "config.xml").write_text("<Config><BindAddress>127.0.0.1</BindAddress><Port>18787</Port><EnableSsl>False</EnableSsl><LaunchBrowser>False</LaunchBrowser></Config>")
        wrapper = tool / "ffmpeg"
        wrapper.write_text("""#!/bin/sh
for arg in "$@"; do
  if [ "$arg" = "-version" ] || [ "$arg" = "-buildconf" ] || [ "$arg" = "0:v:0" ]; then exec /usr/bin/ffmpeg "$@"; fi
done
printf '%s\\n' "$$" > "$CHAPTARRNG_M4B_TEST_GATE"
while [ ! -f "$CHAPTARRNG_M4B_TEST_RELEASE" ]; do sleep 0.05; done
exec /usr/bin/ffmpeg "$@"
""")
        wrapper.chmod(0o755)
        for index, frequency, title in [(1, 330, "Opening"), (2, 440, "The Middle")]:
            command(base + ["--rm", "--entrypoint", "/usr/bin/ffmpeg", args.image, "-hide_banner", "-loglevel", "error",
                            "-f", "lavfi", "-i", f"sine=frequency={frequency}:duration=3", "-codec:a", "libmp3lame",
                            "-metadata", f"title={title}", "-metadata", f"track={index}", "-y", f"/acceptance/source/{index:02d}.mp3"])
        command(base + ["--rm", "--entrypoint", "/usr/bin/ffmpeg", args.image, "-hide_banner", "-loglevel", "error",
                        "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", "-y", "/acceptance/source/cover.jpg"])
        result["sourceHashesBefore"] = {file.name: sha256(file) for file in sources.iterdir()}
        command(base + ["--detach", "--name", name, "--volume", f"{candidate}:/candidate:ro",
                        "--env", "HOME=/acceptance/config", "--env", "HARDCOVER=false", "--env", "HARDCOVER_NATIVE=false",
                        "--env", "BOOKSHELF_FFMPEG_PATH=/acceptance/tool/ffmpeg",
                        "--env", "CHAPTARRNG_M4B_TEST_GATE=/acceptance/ffmpeg-started",
                        "--env", "CHAPTARRNG_M4B_TEST_RELEASE=/acceptance/ffmpeg-release",
                        "--entrypoint", "/candidate/Chaptarr", args.image, "-nobrowser", "-data=/acceptance/config"])
        created = True
        wait_for(healthy, "Fresh native product HTTP health passed")
        versions = []
        for arguments in [["dotnet", "--list-runtimes"], ["/usr/bin/ffmpeg", "-version"],
                          ["/usr/bin/ffprobe", "-version"], ["/usr/bin/php", "/usr/local/bin/m4b-tool", "--version"],
                          ["/usr/local/bin/mp4info", "--version"]]:
            versions.append(" ".join(arguments) + "\n" + command(["docker", "exec", name] + arguments))
        (evidence / "tool-versions.log").write_text("\n".join(versions))
        command(["docker", "stop", "--time", "30", name])
        require(not inspect()["State"]["Running"], "Bootstrap consumer is still active")
        logs("bootstrap.log")
        databases = []
        for path in config.glob("*.db"):
            with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as connection:
                if connection.execute("SELECT 1 FROM sqlite_master WHERE name='ConversionJobs'").fetchone():
                    databases.append(path)
        require(len(databases) == 1, "Expected exactly one native conversion database")
        database = databases[0]
        work = f"/acceptance/.chaptarr-conversions/{task}"
        output = root / f".chaptarr-conversions/{task}/work/book.m4b"
        output.parent.mkdir(parents=True)
        manifest = output.with_name("conversion-artifact.json")
        # Match ImportApprovedBooks.CreateProviderChapterSidecarInputs: the
        # durable worker receives staged inputs beside their chapter sidecar.
        # TagOptions alone does not stage the importer's input files.
        stage = output.parent / "provider-chapters"
        stage.mkdir()
        chapters = "## total-length 00:00:06.046\n00:00:00.000 Opening\n00:00:03.023 The Middle\n"
        (stage / "chapters.txt").write_text(chapters)
        for index in [1, 2]:
            (stage / f"{index:04d}.mp3").symlink_to(os.path.relpath(sources / f"{index:02d}.mp3", stage))
        request = {
            "DownloadId": task, "BookTitle": "Whole application recovery", "WorkRoot": work,
            "WorkFolder": work + "/work", "OutputPath": work + "/work/book.m4b",
            "ConversionInputFiles": [work + f"/work/provider-chapters/{index:04d}.mp3" for index in [1, 2]],
            "Sources": [{"Path": f"/acceptance/source/{index:02d}.mp3", "Size": (sources / f"{index:02d}.mp3").stat().st_size,
                         "ModifiedUtcTicks": 621355968000000000 + (sources / f"{index:02d}.mp3").stat().st_mtime_ns // 100} for index in [1, 2]],
            "TargetQualityId": 12, "TargetQualityName": "M4B", "AudioBitrate": 64, "AudioSampleRate": 44100, "AudioChannels": 2,
            "TagSignature": "whole-app-crash", "TagOptions": {"Mode": "preserve", "Name": "Whole application recovery",
                "Cover": "/acceptance/source/cover.jpg", "CoverIsSource": True,
                "ChaptersTxtContent": chapters},
        }
        now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")
        with sqlite3.connect(database) as connection:
            require(connection.execute("PRAGMA integrity_check").fetchone() == ("ok",), "Invalid native fixture database")
            connection.execute("INSERT INTO ConversionJobs (DownloadId,Status,RequestJson,WorkRoot,WorkFolder,OutputPath,TargetQualityId,TargetQualityName,AttemptCount,CreatedAt,UpdatedAt) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                               (task, 0, json.dumps(request), work, work + "/work", work + "/work/book.m4b", 12, "M4B", 0, now, now))
            connection.commit()
        command(["docker", "start", name])
        wait_for(lambda: (root / "ffmpeg-started").is_file(), "Real conversion reached the FFmpeg gate")
        before = job()
        require(before["Status"] == 1 and before["AttemptCount"] == 1, "Job was not durably converting on its first attempt")
        result["beforeKill"] = before
        processes = command(["docker", "top", name, "-eo", "pid,ppid,comm,args"])
        (evidence / "before-kill-processes.log").write_text(processes)
        identities = {int(line.split()[0]): process_identity(int(line.split()[0])) for line in processes.splitlines()[1:]}
        require(Path(f"/proc/{inspect()['State']['Pid']}/exe").resolve().name == "Chaptarr", "PID 1 is not the native application")
        require(not output.exists() and not manifest.exists(), "A completed artifact exists before interruption")
        command(["docker", "kill", "--signal", "KILL", name])
        wait_for(lambda: not inspect()["State"]["Running"], "Whole product container was killed")
        def all_consumers_exited():
            for pid, prior in identities.items():
                current = process_identity(pid)
                if prior and current and current[1] == prior[1] and current[0] != "Z":
                    return False
            return True
        wait_for(all_consumers_exited, "All original application and tool processes exited")
        killed = inspect()["State"]
        require(killed["ExitCode"] == 137 and not killed["OOMKilled"], "Application did not exit through the injected hard kill")
        result["killState"] = {key: killed[key] for key in ["ExitCode", "OOMKilled", "Running"]}
        interrupted = job()
        require(interrupted["Status"] == 1 and interrupted["AttemptCount"] == 1, "Hard kill did not retain durable in-flight state")
        require(not (root / "ffmpeg-release").exists() and not output.exists() and not manifest.exists(), "Conversion escaped the interruption gate")
        logs("after-kill.log")
        (root / "ffmpeg-started").rename(evidence / "ffmpeg-first-process")
        (root / "ffmpeg-release").write_text("continue\n")
        command(["docker", "start", name])
        wait_for(healthy, "Restarted native product HTTP health passed")
        wait_for(lambda: job()["Status"] in [2, 4], "Durable conversion reached a final worker state")
        after = job()
        result["afterRestart"] = after
        require(after["Status"] == 2 and after["AttemptCount"] == 2, "Interrupted conversion did not recover exactly once")
        require(output.is_file() and output.stat().st_size > 32768 and manifest.is_file(), "Recovered artifact is missing or too small")
        artifact = json.loads(manifest.read_text())
        require(artifact["Importable"] and bytes.fromhex(artifact["OutputSha256"]) == bytes.fromhex(sha256(output)), "Recovered artifact fingerprint is invalid")
        result["sourceHashesAfter"] = {file.name: sha256(file) for file in sources.iterdir()}
        require(result["sourceHashesAfter"] == result["sourceHashesBefore"], "An original media input changed")
        probe = json.loads(command(["docker", "exec", name, "/usr/bin/ffprobe", "-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", work + "/work/book.m4b"]))
        (evidence / "ffprobe.json").write_text(json.dumps(probe, indent=2) + "\n")
        require(5.5 <= float(probe["format"]["duration"]) <= 6.5, "Recovered duration changed")
        require(any(stream["codec_type"] == "audio" and stream["codec_name"] == "aac" for stream in probe["streams"]), "Recovered output has no AAC audio")
        require([chapter.get("tags", {}).get("title") for chapter in probe.get("chapters", [])] == ["Opening", "The Middle"], "Recovered chapter order/titles changed")
        require(abs(float(probe["chapters"][0]["start_time"])) <= 0.01 and
                abs(float(probe["chapters"][1]["start_time"]) - 3.023) <= 0.1,
                "Recovered chapter offsets changed")
        require(healthy(), "Product lost HTTP health after conversion recovery")
        shutil.copy2(output, evidence / "recovered.m4b")
        shutil.copy2(manifest, evidence / manifest.name)
        result.update(success=True, outputSha256=sha256(output), outputBytes=output.stat().st_size, chapters=2)
        print("Whole-application hard-kill and real M4B conversion recovery passed", flush=True)
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        if created:
            try:
                if inspect()["State"]["Running"]:
                    command(["docker", "stop", "--time", "30", name])
                logs("product.log")
                require(not inspect()["State"]["Running"], "Preserve the container while its consumers remain active")
                command(["docker", "rm", name])
            except Exception as error:
                result["cleanupError"] = str(error)
                result["success"] = False
        (evidence / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(f"Retained evidence: {evidence}", flush=True)
        if result.get("cleanupError"):
            raise RuntimeError(result["cleanupError"])


if __name__ == "__main__":
    main()
