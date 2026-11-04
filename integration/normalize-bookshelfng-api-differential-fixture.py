#!/usr/bin/env python3

"""Normalize the disposable API differential fixture to valid provider IDs."""

import argparse
import sqlite3
from pathlib import Path


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("database", nargs="*", type=Path)
parser.add_argument(
    "--seed-target-migration-profile",
    type=Path,
    help="Mark the target fixture as a schema-158 BookshelfNG migration result.",
)
args = parser.parse_args()

if not args.database and not args.seed_target_migration_profile:
    parser.error("provide one or more fixture databases or a target migration-profile database")

expected_counts = {
    "AuthorMetadata": (4, 'SELECT COUNT(*) FROM "AuthorMetadata"'),
    "Books": (6, 'SELECT COUNT(*) FROM "Books"'),
    "Editions": (9, 'SELECT COUNT(*) FROM "Editions"'),
}

for path in args.database:
    with sqlite3.connect(path) as connection:
        for table, (expected, query) in expected_counts.items():
            count = connection.execute(query).fetchone()[0]
            if count != expected:
                raise RuntimeError(
                    f"Expected {expected} {table} rows in the disposable fixture, found {count}."
                )

        for source_id, in connection.execute('SELECT "Id" FROM "AuthorMetadata"'):
            provider_id = str(10000 + source_id)
            connection.execute(
                'UPDATE "AuthorMetadata" SET "ForeignAuthorId" = ?, "TitleSlug" = ? WHERE "Id" = ?',
                (provider_id, f"hc-author-{provider_id}", source_id),
            )

        for source_id, in connection.execute('SELECT "Id" FROM "Books"'):
            provider_id = str(100000 + source_id)
            connection.execute(
                'UPDATE "Books" SET "ForeignBookId" = ?, "TitleSlug" = ? WHERE "Id" = ?',
                (provider_id, f"hc-book-{provider_id}", source_id),
            )

        for edition_id, is_ebook in connection.execute(
            'SELECT "Id", "IsEbook" FROM "Editions"'
        ):
            if is_ebook:
                provider_id = {
                    41: "ol:OL123M",
                    44: "ol:OL2100M",
                    45: "ol:OL2101M",
                    47: "ol:OL2102M",
                }.get(edition_id)
                if provider_id is None:
                    raise RuntimeError(
                        f"No Open Library fixture identity is defined for edition {edition_id}."
                    )
            else:
                provider_id = str(200000 + edition_id)

            connection.execute(
                'UPDATE "Editions" SET "ForeignEditionId" = ?, "TitleSlug" = ? WHERE "Id" = ?',
                (provider_id, f"edition-{edition_id}", edition_id),
            )

        connection.execute(
            'UPDATE "VersionInfo" SET "AppliedOn" = ? WHERE "AppliedOn" IS NOT NULL',
            ("2026-01-01 00:00:00",),
        )
        connection.commit()
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise RuntimeError(f"Disposable fixture integrity check failed: {integrity}.")

print(f"Normalized {len(args.database)} disposable fixture(s) with valid Hardcover and Open Library IDs.")

if args.seed_target_migration_profile:
    profile_values = {
        "BookshelfNGDatabaseMigrated": "true",
        "BookshelfNGProviderDialect": "gr",
        "BookshelfNGCompatibilityProfileVersion": "2",
        "BookshelfNGSourceProduct": "BookshelfNG",
        "BookshelfNGSourceSchemaVersion": "158",
        "BookshelfNGApiBasePath": "",
        "BookshelfNGFormatSemantics": "audiobook-ebook-split-v1",
        "BookshelfNGIdentityMapVersion": "1",
        "BookshelfNGMigrationOutcome": "converted",
    }

    with sqlite3.connect(args.seed_target_migration_profile) as connection:
        next_id = connection.execute('SELECT COALESCE(MAX("Id"), 0) + 1 FROM "Config"').fetchone()[0]
        for key, value in profile_values.items():
            connection.execute('DELETE FROM "Config" WHERE "Key" = ?', (key,))
            connection.execute(
                'INSERT INTO "Config" ("Id", "Key", "Value") VALUES (?, ?, ?)',
                (next_id, key, value),
            )
            next_id += 1
        connection.commit()
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise RuntimeError(f"Target migration-profile fixture integrity check failed: {integrity}.")

    print("Seeded a valid schema-158 BookshelfNG compatibility profile on the target fixture.")
