#!/usr/bin/env python3

"""Seed disposable BookshelfNG 158 databases for smoke and migration rehearsals."""

from pathlib import Path
import argparse
import os
import sqlite3


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--config", type=Path, default=Path("/config"))
parser.add_argument("--library", type=Path, default=Path("/library"))
parser.add_argument("--schema", type=Path, default=Path("/fixture/schema.sql"))
parser.add_argument("--seed", type=Path, default=Path("/fixture/seed.sql"))
parser.add_argument(
    "--expanded-fixture",
    action="store_true",
    help="add multiple authors, works, edition selections, and legacy records for migration rehearsal",
)
args = parser.parse_args()

CONFIG = args.config
LIBRARY = args.library
DATABASE = CONFIG / "readarr.db"
SCHEMA = args.schema
SEED = args.seed


def seed_expanded_fixture(connection: sqlite3.Connection) -> None:
    """Add deterministic migration edge cases to the shared 158 fixture."""
    connection.executescript(
        """
        INSERT INTO Tags (Id, Label) VALUES (1, 'expanded migration fixture');
        INSERT INTO RootFolders (Id, Path, Name, DefaultMetadataProfileId, DefaultQualityProfileId, DefaultMonitorOption, DefaultTags, IsCalibreLibrary, CalibreSettings, DefaultNewItemMonitorOption)
        VALUES
            (2, '/library/Expanded Author', 'Expanded Author', 1, 1, 0, '[1]', 0, NULL, 2),
            (3, '/library/Review Author', 'Review Author', 2, 2, 0, '[1]', 0, NULL, 2),
            (4, '/library/Unmonitored Author', 'Unmonitored Author', 1, 1, 0, '[]', 0, NULL, 2);
        INSERT INTO AuthorMetadata (Id, ForeignAuthorId, TitleSlug, Name, Overview, Status, Images, Aliases, SortName, NameLastFirst, SortNameLastFirst)
        VALUES
            (11, 'hardcover-author:author-11', 'hardcover-author:author-11', 'Expanded Author', 'Synthetic audiobook and ebook cases', 0, '[]', '[]', 'Expanded Author', 'Author, Expanded', 'Author, Expanded'),
            (12, 'hardcover-author:author-12', 'hardcover-author:author-12', 'Review Author', 'Synthetic unresolved media cases', 0, '[]', '[]', 'Review Author', 'Author, Review', 'Author, Review'),
            (13, 'hardcover-author:author-13', 'hardcover-author:author-13', 'Unmonitored Author', 'Synthetic unmonitored case', 0, '[]', '[]', 'Unmonitored Author', 'Author, Unmonitored', 'Author, Unmonitored');
        INSERT INTO Authors (Id, CleanName, Path, Monitored, QualityProfileId, Tags, Added, MetadataProfileId, AuthorMetadataId, MonitorNewItems, EbookPath, AudiobookPath, EbookQualityProfileId, AudiobookQualityProfileId, EbookMetadataProfileId, AudiobookMetadataProfileId)
        VALUES
            (21, 'expandedauthor', '/library/Expanded Author', 1, 1, '[1]', '2026-02-01', 1, 11, 2, '/library/Expanded Author/Ebooks', '/library/Expanded Author/Audiobooks', 2, 1, 2, 1),
            (22, 'reviewauthor', '/library/Review Author', 1, 2, '[1]', '2026-02-02', 2, 12, 2, '/library/Review Author/Ebooks', '/library/Review Author/Audiobooks', 2, 1, 2, 1),
            (23, 'unmonitoredauthor', '/library/Unmonitored Author', 0, 1, '[]', '2026-02-03', 1, 13, 0, '/library/Unmonitored Author/Ebooks', '/library/Unmonitored Author/Audiobooks', 2, 1, 2, 1);
        INSERT INTO Books (Id, AuthorMetadataId, ForeignBookId, TitleSlug, Title, Links, Genres, Ratings, CleanTitle, Monitored, AnyEditionOk, Added, RelatedBooks, LastSearchTime)
        VALUES
            (31, 11, 'hardcover:book-31', 'hardcover:book-31', 'Audiobook Variant Work', '[]', '["fantasy"]', '{}', 'audiobook variant work', 1, 0, '2026-02-04', '[]', NULL),
            (32, 11, 'hardcover:book-32', 'hardcover:book-32', 'Ebook Editions Work', '[]', '["history"]', '{}', 'ebook editions work', 1, 0, '2026-02-05', '[]', NULL),
            (33, 12, 'hardcover:book-33', 'hardcover:book-33', 'Work Without Editions', '[]', '[]', '{}', 'work without editions', 1, 0, '2026-02-06', '[]', NULL),
            (34, 12, 'hardcover:book-34', 'hardcover:book-34', 'Unselected Mixed Work', '[]', '[]', '{}', 'unselected mixed work', 1, 0, '2026-02-07', '[]', NULL),
            (35, 13, 'hardcover:book-35', 'hardcover:book-35', 'Unmonitored Work', '[]', '[]', '{}', 'unmonitored work', 0, 0, '2026-02-08', '[]', NULL);
        INSERT INTO Editions (Id, BookId, ForeignEditionId, Isbn13, Asin, Title, TitleSlug, Images, Links, IsEbook, Monitored, ManualAdd, Narrator)
        VALUES
            (42, 31, 'hardcover-edition:audio-42', NULL, NULL, 'Audiobook Variant One', 'audio-variant-one', '[]', NULL, 0, 1, 0, 'Narrator One'),
            (43, 31, 'hardcover-edition:audio-43', NULL, NULL, 'Audiobook Variant Two', 'audio-variant-two', '[]', NULL, 0, 0, 0, 'Narrator Two'),
            (44, 32, 'openlibrary-edition:OL2100M', '9780000000002', NULL, 'Ebook Edition One', 'OL2100M', '[]', NULL, 1, 1, 0, NULL),
            (45, 32, 'openlibrary-edition:OL2101M', '9780000000003', NULL, 'Ebook Edition Two', 'OL2101M', '[]', NULL, 1, 0, 0, NULL),
            (46, 34, 'hardcover-edition:audio-46', NULL, NULL, 'Unselected Audiobook', 'audio-unselected', '[]', NULL, 0, 0, 0, 'Review Narrator'),
            (47, 34, 'openlibrary-edition:OL2102M', '9780000000004', NULL, 'Unselected Ebook', 'OL2102M', '[]', NULL, 1, 0, 0, NULL),
            (48, 35, 'hardcover-edition:audio-48', NULL, NULL, 'Unmonitored Audiobook', 'audio-unmonitored', '[]', NULL, 0, 0, 0, 'Unmonitored Narrator');
        INSERT INTO BookFiles (Id, EditionId, CalibreId, Quality, Size, DateAdded, MediaInfo, Modified, Path, Part)
        VALUES
            (52, 42, 0, '{}', 300, '2026-02-04', '{}', '2026-02-04', '/library/Expanded Author/Audiobooks/variant-one.m4b', 0),
            (53, 44, 0, '{}', 400, '2026-02-05', '{}', '2026-02-05', '/library/Expanded Author/Ebooks/edition-one.epub', 0);
        INSERT INTO MetadataFiles (Id, AuthorId, Consumer, Type, RelativePath, LastUpdated, BookId, BookFileId, Hash, Added, Extension)
        VALUES (62, 21, 'Readarr', 1, 'metadata.opf', '2026-02-05', 32, 53, 'fixture-hash', '2026-02-05', 'opf');
        INSERT INTO Series (Id, ForeignSeriesId, Title, Numbered, WorkCount, PrimaryWorkCount)
        VALUES (71, 'hardcover-series:series-71', 'Expanded Fixture Series', 1, 2, 2);
        INSERT INTO SeriesBookLink (Id, SeriesId, BookId, Position, IsPrimary, SeriesPosition)
        VALUES
            (81, 71, 31, '1', 1, 1),
            (82, 71, 32, '2', 1, 2);
        INSERT INTO History (Id, SourceTitle, Date, Quality, Data, EventType, DownloadId, AuthorId, BookId)
        VALUES
            (94, 'variant-one.m4b', '2026-02-06', '{}', '{"FileId":"52","ImportedPath":"/library/Expanded Author/Audiobooks/variant-one.m4b"}', 3, 'download-94', 21, 31),
            (95, 'edition-one.epub', '2026-02-06', '{}', '{"FileId":"53","ImportedPath":"/library/Expanded Author/Ebooks/edition-one.epub"}', 3, 'download-95', 21, 32);
        INSERT INTO DownloadHistory (Id, EventType, AuthorId, DownloadId, SourceTitle, Date, Data)
        VALUES
            (94, 1, 21, 'download-94', 'Variant One release', '2026-02-06', '{}'),
            (95, 1, 21, 'download-95', 'Ebook Edition One release', '2026-02-06', '{}');
        INSERT INTO Blocklist (Id, SourceTitle, Quality, Date, AuthorId, BookIds, IndexerFlags)
        VALUES (93, 'Rejected synthetic release', '{}', '2026-02-07', 21, '[31]', 0);
        INSERT INTO Config (Id, Key, Value)
        VALUES (2, 'FixtureCoverage', 'expanded-migration');
        """
    )

require(CONFIG.is_dir(), "The disposable Chaptarr config volume is missing.")
require(LIBRARY.is_dir(), "The disposable library volume is missing.")
require(SCHEMA.is_file() and SEED.is_file(), "The migration fixture SQL is missing.")
require(not any(CONFIG.iterdir()), "Refusing to overwrite a non-empty config volume; run docker compose down -v first.")
require(not any(LIBRARY.iterdir()), "Refusing to overwrite a non-empty library volume; run docker compose down -v first.")

with sqlite3.connect(DATABASE) as connection:
    connection.executescript(SCHEMA.read_text(encoding="utf-8"))
    connection.executescript(SEED.read_text(encoding="utf-8"))
    if args.expanded_fixture:
        seed_expanded_fixture(connection)
    version = connection.execute("SELECT MAX(Version) FROM VersionInfo").fetchone()[0]
    authors = connection.execute("SELECT COUNT(*) FROM Authors").fetchone()[0]
    books = connection.execute("SELECT COUNT(*) FROM Books").fetchone()[0]
    editions = connection.execute("SELECT COUNT(*) FROM Editions").fetchone()[0]
    book_files = connection.execute("SELECT COUNT(*) FROM BookFiles").fetchone()[0]
    users = connection.execute("SELECT COUNT(*) FROM Users").fetchone()[0]
    integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]

require(version == 158, f"Expected BookshelfNG schema 158, found {version}.")
expected_rows = (4, 6, 9, 4) if args.expanded_fixture else (1, 1, 2, 2)
require(
    (authors, books, editions, book_files) == expected_rows,
    "The populated migration fixture does not match its expected author, book, edition, and file rows.",
)
require(users == 1, f"Expected one legacy BookshelfNG user in the migration fixture, found {users}.")
require(integrity == "ok", "The seeded BookshelfNG source failed SQLite integrity_check.")

# Give the migrated file rows corresponding disposable files so ChaptarrNG sees
# the same paths it would see in a mounted library. The content is deliberately
# synthetic and is not intended to be decoded as media.
library_files = [
    ("Fixture Author/book.m4b", 100),
    ("Fixture Author/book.epub", 200),
]
if args.expanded_fixture:
    library_files.extend(
        [
            ("Expanded Author/Audiobooks/variant-one.m4b", 300),
            ("Expanded Author/Ebooks/edition-one.epub", 400),
        ]
    )

for relative_path, size in library_files:
    path = LIBRARY / relative_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(size))

if os.geteuid() == 0:
    for root in (CONFIG, LIBRARY):
        for path in (root, *root.rglob("*")):
            os.chown(path, 99, 100)
            if path.is_dir():
                path.chmod(0o775)
            else:
                path.chmod(0o664)

if args.expanded_fixture:
    print(
        "Seeded disposable BookshelfNG schema 158 with 4 authors, 6 works, 9 editions, "
        "4 synthetic book files, unresolved and unselected works, "
        "provider settings, history, sidecars, series, and synthetic credentials."
    )
else:
    print("Seeded disposable BookshelfNG schema 158 with one author, one mixed-format book, two editions, and two synthetic library files.")
