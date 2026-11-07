#!/usr/bin/env python3
"""Read-only, commit-pinned replacement inventory; never certifies parity.

Uses BookshelfNG's existing reviewed ledger without changing its dispositions.
Only tracked Git objects enter the report. No databases, settings, credentials,
working-tree files, network services, or release gates are read or modified.
"""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import subprocess


LEDGER = "docs/maintainers/chaptarr-consolidation-inventory.json"
HTTP_METHODS = {"get", "post", "put", "patch", "delete", "head", "options", "trace"}
COMMAND_CLASS = re.compile(r"\bclass\s+(\w+Command)\s*:\s*Command\b")


def git(repo, *args):
    return subprocess.check_output(["git", "-C", str(repo), *args])


def commit(repo, ref):
    return git(repo, "rev-parse", "--verify", ref + "^{commit}").decode().strip()


def read_json(repo, revision, path):
    return json.loads(git(repo, "show", revision + ":" + path))


def tree(repo, revision):
    result = {}
    for entry in git(repo, "ls-tree", "-r", "-z", revision).split(b"\0"):
        if not entry:
            continue
        metadata, path = entry.split(b"\t", 1)
        mode, kind, sha = metadata.decode().split()
        if kind == "blob":
            result[path.decode()] = {"mode": mode, "sha": sha}
    return result


def area(path):
    if path.startswith("src/") and (".Test/" in path or "/NzbDrone.Test." in path):
        return "tests"
    if path.startswith("src/") and path.endswith(".cs"):
        return "backend-code"
    if path.startswith("frontend/src/"):
        return "frontend"
    if path.startswith((".github/", "scripts/", "packaging/", "distribution/", "charts/")):
        return "build-install-tooling"
    if path.startswith(("docs/", "release-notes/")) or path.endswith(".md"):
        return "documentation"
    return "other-build-manifests"


def operations(document):
    return {
        (path, method.upper())
        for path, item in document["paths"].items()
        for method in item
        if method in HTTP_METHODS
    }


def command_classes(repo, revision, entries):
    result = {}
    # Search class declarations, not filenames: RetryUnmappedFilesCommand is
    # declared in RescanFoldersCommand.cs in ChaptarrNG.
    for path in sorted(entries):
        if not path.startswith("src/NzbDrone.Core/") or not path.endswith(".cs"):
            continue
        contents = git(repo, "show", revision + ":" + path).decode("utf-8-sig")
        for name in COMMAND_CLASS.findall(contents):
            result.setdefault(name, []).append(path)
    return result


def snapshot(bookshelf, chaptarr, source_ref, target_ref):
    source = commit(bookshelf, source_ref)
    target = commit(chaptarr, target_ref)
    ledger = read_json(bookshelf, source, LEDGER)
    old_source = tree(bookshelf, ledger["sourceCommit"])
    old_target = tree(chaptarr, ledger["targetCommit"])
    source_tree = tree(bookshelf, source)
    target_tree = tree(chaptarr, target)
    open_review = []
    invalidated = []
    unchanged_reviews = 0

    for entry in ledger["files"]:
        path = entry["sourcePath"]
        disposition = entry.get("reviewedDisposition")
        targets = disposition.get("targetPaths", []) if disposition else (
            [entry["targetPath"]] if entry.get("targetPath") else []
        )
        source_changed = old_source.get(path) != source_tree.get(path)
        changed_targets = [p for p in targets if old_target.get(p) != target_tree.get(p)]
        row = {
            "sourcePath": path,
            "area": area(path),
            "ledgerState": entry["state"],
            "mappedTargetPaths": targets,
            "sourcePresent": path in source_tree,
            "sourceChangedSinceLedger": source_changed,
            "changedTargetPaths": changed_targets,
            "missingTargetPaths": [p for p in targets if p not in target_tree],
        }
        if disposition is None:
            open_review.append(row)
        elif source_changed or changed_targets:
            row["previousDisposition"] = disposition["disposition"]
            invalidated.append(row)
        else:
            unchanged_reviews += 1

    source_operations = operations(read_json(bookshelf, source, "src/Readarr.Api.V1/openapi.json"))
    target_operations = operations(read_json(chaptarr, target, "src/Chaptarr.Api.V1/openapi.json"))
    source_commands = command_classes(bookshelf, source, source_tree)
    target_commands = command_classes(chaptarr, target, target_tree)
    changed_source_paths = [
        {"path": p, "area": area(p), "added": p not in old_source, "deleted": p not in source_tree}
        for p in sorted(old_source.keys() | source_tree.keys())
        if old_source.get(p) != source_tree.get(p)
    ]
    return {
        "schemaVersion": 1,
        "sourceRepository": "snapetech-org/bookshelfng",
        "sourceCommit": source,
        "targetRepository": "snapetech-org/chaptarrng",
        "targetCommit": target,
        "limitations": [
            "This is a review-queue snapshot, not a feature-completion percentage or release decision.",
            "OpenAPI operation presence does not prove running routing, payload, identity, authorization, side-effect, or response parity.",
            "Command names are a regex inventory of direct Command subclasses, not compilation, registration, dispatch, or behavior evidence.",
            "An unchanged review fingerprint is source evidence only; changes elsewhere can still alter behavior.",
            "Changed fingerprints require revalidation; they are not automatically regressions or missing features.",
            "The historical fork-delta ledger is overlaid with current trees, not regenerated from fork history; source-change and review lists overlap.",
            "Absent filename mappings can have implementations under other names; no retirement or equivalence is inferred.",
        ],
        "legacyLedger": {
            "path": LEDGER,
            "sourceCommit": ledger["sourceCommit"],
            "targetCommit": ledger["targetCommit"],
            "counts": ledger["counts"],
        },
        "apiOperations": {
            "bookshelf": len(source_operations),
            "chaptarr": len(target_operations),
            "shared": len(source_operations & target_operations),
            "sourceOnly": [{"method": m, "path": p} for p, m in sorted(source_operations - target_operations)],
            "targetOnlyCount": len(target_operations - source_operations),
        },
        "commandDeclarations": {
            "bookshelf": len(source_commands),
            "chaptarr": len(target_commands),
            "sourceOnly": sorted(source_commands.keys() - target_commands.keys()),
            "sourceClasses": source_commands,
            "targetClasses": target_commands,
        },
        "reviewSummary": {
            "previouslyReviewedFingerprintsUnchanged": unchanged_reviews,
            "previouslyReviewedFingerprintsChanged": len(invalidated),
            "previouslyUnreviewed": len(open_review),
            "previouslyUnreviewedByArea": dict(sorted(Counter(r["area"] for r in open_review).items())),
            "sourcePathsChangedSinceLedger": len(changed_source_paths),
        },
        "previouslyUnreviewedPaths": open_review,
        "reviewFingerprintsToRevalidate": invalidated,
        "sourcePathsChangedSinceLedger": changed_source_paths,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bookshelf", required=True, type=Path)
    parser.add_argument("--chaptarr", type=Path, default=Path("."))
    parser.add_argument("--source-ref", default="HEAD")
    parser.add_argument("--target-ref", default="HEAD")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        report = snapshot(args.bookshelf, args.chaptarr, args.source_ref, args.target_ref)
    except (subprocess.CalledProcessError, KeyError, ValueError) as error:
        parser.error("Cannot read pinned source evidence; fetch both ledger commits first. " + str(error))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps({"apiOperations": report["apiOperations"], "reviewSummary": report["reviewSummary"]}, indent=2))


if __name__ == "__main__":
    main()
