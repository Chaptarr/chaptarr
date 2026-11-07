# Embedded BookshelfNG source migrations

The migration classes in this directory are copied from
BookshelfNG `main-v0.4.21.71` at commit
`3fad9427d167ed69d30ca6ed55f40262c2846487` under GPL-3.0. They run only on a
disposable copy of a BookshelfNG SQLite database or PostgreSQL schema, before
the ChaptarrNG data importer runs. The migration controller filters to this
namespace, so these version numbers cannot run against a ChaptarrNG database.

The converter keeps the consistent source snapshot untouched for rollback,
applies the released BookshelfNG migration chain to a temporary SQLite file or
PostgreSQL schema, and requires the resulting schema to pass the importer’s
migration-158 checks. PostgreSQL sources use the PostgreSQL migration runner;
the converter does not run SQLite-specific database operations on PostgreSQL.
Preview removes the temporary snapshot and staging data without changing the
source. Apply retains the untouched original source as the rollback backup.

The copied migrations include PostgreSQL timestamp-type changes. Those type
changes are skipped for the SQLite snapshot because SQLite stores these values
with dynamic typing and the released BookshelfNG SQLite schema already uses
`DATETIME` affinity. New timestamp columns use that same SQLite-compatible
type. The isolated migration runner also includes BookshelfNG’s SQLite table
rebuild processor for its older alter, rename, and drop-column migrations.

The embedded fork-specific migration classes are versions 1–46 and 158. The
BookshelfNG `VersionInfo` table also records migrations shared with its
upstream, so valid source databases can contain version rows that are not
defined in this isolated namespace. The importer checks that a migration
history exists and that its maximum version is between 1 and 158; it does not
compare every history row with the fork-specific class list. Older schemas are
upgraded only on the disposable snapshot, and the original database is retained
unchanged for rollback.

The PostgreSQL converter stages native Chaptarr tables in a temporary schema,
retains the source tables in a backup schema after promotion, and provides
preview and rollback routes. This capability is not supported by a published
release. Synthetic PostgreSQL fixtures pass, but representative operator data,
independent backup restore, installed rollback, and channel acceptance remain
open. See
[`docs/BOOKSHELFNG_POSTGRESQL_MIGRATION.md`](../../../../docs/BOOKSHELFNG_POSTGRESQL_MIGRATION.md).

When updating this embedded migration chain, keep the original source tag and
commit recorded above, review SQLite-specific behavior, and extend the
older-schema conversion, non-mutating preview, rollback, and current-schema
fixtures in `src/Chaptarr.Core.Test/Datastore/BookshelfDatabaseImporterFixture.cs`.
