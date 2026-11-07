#!/usr/bin/env python3

"""Snapshot a running BookshelfNG fixture for its ChaptarrNG migration peer."""

from pathlib import Path
import argparse
import shutil
import sqlite3


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--source-database", required=True, type=Path)
parser.add_argument("--target-database", required=True, type=Path)
parser.add_argument("--source-config", required=True, type=Path)
parser.add_argument("--target-config", required=True, type=Path)
parser.add_argument("--source-library", required=True, type=Path)
parser.add_argument("--target-library", required=True, type=Path)
args = parser.parse_args()

for path in (args.source_database, args.source_config):
    if not path.is_file():
        raise RuntimeError(f"BookshelfNG runtime file is missing: {path}")

if args.target_database.exists() or args.target_config.exists():
    raise RuntimeError("Refusing to overwrite an existing target database or config.xml.")
if any(args.target_library.iterdir()):
    raise RuntimeError("Refusing to overwrite a non-empty target library directory.")

with sqlite3.connect(f"file:{args.source_database.resolve()}?mode=ro", uri=True) as source:
    with sqlite3.connect(args.target_database) as target:
        source.backup(target, pages=256, sleep=0.01)
        integrity = target.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise RuntimeError(f"The BookshelfNG runtime snapshot failed integrity_check: {integrity}.")
        version = target.execute("SELECT MAX(Version) FROM VersionInfo").fetchone()[0]
        if version != 158:
            raise RuntimeError(f"Expected BookshelfNG schema 158 after startup, found {version}.")
        authors = target.execute("SELECT COUNT(*) FROM Authors").fetchone()[0]
        books = target.execute("SELECT COUNT(*) FROM Books").fetchone()[0]
        editions = target.execute("SELECT COUNT(*) FROM Editions").fetchone()[0]
        book_files = target.execute("SELECT COUNT(*) FROM BookFiles").fetchone()[0]
        quality_definitions = target.execute("SELECT COUNT(*) FROM QualityDefinitions").fetchone()[0]

if (authors, books, editions, book_files) != (4, 6, 9, 4):
    raise RuntimeError(
        "The BookshelfNG runtime changed the expanded fixture row counts before snapshot."
    )
if quality_definitions != 9:
    raise RuntimeError(
        "Expected BookshelfNG startup to seed nine quality definitions before snapshot, "
        f"found {quality_definitions}."
    )

shutil.copy2(args.source_config, args.target_config)
shutil.copytree(args.source_library, args.target_library, dirs_exist_ok=True)

print(
    "Snapshotted running BookshelfNG schema 158, config.xml, and nine runtime quality "
    "definitions for migration: "
    f"{authors} authors, {books} works, {editions} editions, {book_files} book files."
)
