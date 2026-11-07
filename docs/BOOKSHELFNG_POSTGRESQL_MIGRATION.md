# BookshelfNG PostgreSQL migration candidate

> **Not supported in a published release.** ChaptarrNG `v0.9.977` cannot
> convert a PostgreSQL BookshelfNG database. Current `main` upgrades supported
> older BookshelfNG schemas on a disposable PostgreSQL snapshot before staging
> ChaptarrNG, and retains the untouched source schema for rollback. Local
> PostgreSQL 17 converter fixtures cover source migrations 1, 13, 22, 33, and
> 158, startup conversion, rollback, and fail-closed unsupported schemas. The
> full Core suite with PostgreSQL enabled passed 4,632 tests with 0 failures
> and 11 skips; the focused selector, inspector, and converter checks passed
> 22/22. The trusted local bulk suite passed on 2026-10-22 with 4,688 passed,
> 0 failed, 22 skipped, and 2 inconclusive NUnit cases; its real-tool backup
> fixture is reserved for the hosted disposable-PostgreSQL job. A candidate
> Windows installer adds an independent `pg_dump` backup and `pg_restore`
> rollback path. Native Windows service/rollback validation and exact-head
> hosted PostgreSQL validation remain outstanding. Representative
> operator-data conversion, installed restore/rollback, provider parity, and
> channel handoff remain open. See the
> [retirement status report](BOOKSHELFNG_RETIREMENT.md) for the full release
> and acceptance state.


**Prior PR #131 validation, 2026-10-19:** Draft code head a111b5754421cccaafc756abcf4efe226f86d7eb passed exact-head Build [39230899323](https://github.com/snapetech-org/chaptarrng/actions/runs/39230899323), including Linux, macOS, and Windows tests, PostgreSQL migration smoke, and native receipt reconciliation. CodeQL [39230892934](https://github.com/snapetech-org/chaptarrng/actions/runs/39230892934), security [39230899375](https://github.com/snapetech-org/chaptarrng/actions/runs/39230899375), NuGet [39230915422](https://github.com/snapetech-org/chaptarrng/actions/runs/39230915422), dependency review [39230899544](https://github.com/snapetech-org/chaptarrng/actions/runs/39230899544), and the release-note contract passed. Disposable-fork validation [39230899435](https://github.com/snapetech-org/chaptarrng/actions/runs/39230899435) was skipped by its external-fork gate. These are source checks only; representative operator data, full feature parity, installed rollback, and channel acceptance remain open. No release or final handoff is authorized.

## Candidate behavior

The candidate keeps the BookshelfNG database in the same PostgreSQL database.
It copies the source tables into a temporary schema, migrates a fresh Chaptarr
schema in a second temporary schema, imports the snapshot, and checks lineage
and per-table row disposition before promotion. Promotion moves the original
BookshelfNG tables and sequences into a `bookshelfng_backup_*` schema, then
moves the Chaptarr tables into the schema selected by the existing connection
search path. Rollback moves the retained BookshelfNG tables back and preserves
the displaced Chaptarr tables in a `chaptarrng_backup_*` schema.

### Database-name compatibility

BookshelfNG's implicit PostgreSQL database names are `readarr-main`,
`readarr-log`, and `readarr-cache`; ChaptarrNG's fresh-install defaults are
`chaptarr-main`, `chaptarr-log`, and `chaptarr-cache`. Current `main` checks
the implicit `chaptarr-main` database and falls back to `readarr-main` only
when the former is empty and the latter has a recognized BookshelfNG or
ChaptarrNG schema. If the default database is unsupported while the legacy
database contains application data, startup stops instead of choosing one.
Explicitly configured database names are preserved.

When the selected main database is BookshelfNG, or a converted ChaptarrNG
database carrying the dedicated `BookshelfNGDatabaseMigrated` marker, implicit
log and cache names resolve to `readarr-log` and `readarr-cache`. Explicit log
and cache names remain in effect. This allows a restart after conversion to
continue using the same PostgreSQL databases even though the defaults are not
written to `config.xml`. This is source-candidate behavior; it does not change
the published `.977` support statement above.

The schema backups remain in the same PostgreSQL database. The candidate
Windows installer and Linux package handoff also require `pg_dump` and
`pg_restore` on `PATH`, an explicit `PostgresUser`, and no other client sessions
on the selected main, log, and cache databases before backup. They create
verified custom-format dumps of each unique database's active application
schema. Automatic Linux rollback restores main data from the retained source
schema, restores log/cache from the independent dumps, and keeps the displaced
ChaptarrNG main schema so post-cutover writes are not overwritten. The current
dump directory is recorded in
`.chaptarrng-bookshelfng-postgres-backup-current`. Restore archives are
validated before changes and each database restore is transactional. These
backups do not capture PostgreSQL roles, cluster settings, or unrelated
schemas, and do not replace a full operator-managed cluster backup. Keep all
database clients stopped through preview, review, and apply. The converter's
advisory lock coordinates ChaptarrNG conversions but does not stop BookshelfNG
or other database clients from writing.

Current `main` requires a `VersionInfo` table whose maximum version is between
1 and 158. BookshelfNG shares migration IDs with its upstream, so valid
`VersionInfo` rows are not limited to the fork-specific migrations embedded in
this repository. If the source is older, the PostgreSQL-specific migration
runner advances only the disposable snapshot to 158 before import; it never
migrates the live source in place. Future versions and unsupported tables,
PostgreSQL views, materialized views, foreign tables, and partitioned tables
fail closed. The published `.977` release still supports SQLite only.

The candidate importer translates a saved `HardcoverAuth` config row to
ChaptarrNG's `HardcoverApiToken` setting when the source has no explicit
ChaptarrNG token. The original BookshelfNG row remains in the source backup;
the migration report describes the mapping without including the token value.
Earlier hosted runs found and corrected staging-schema lookup and migration-runner API issues. Run [39125697386](https://github.com/snapetech-org/chaptarrng/actions/runs/39125697386) on `fb99a694` compiled the candidate but failed because the smoke runner could not produce a valid NUnit summary. Its receipt contained no test totals or failed cases, so it did not establish a converter assertion failure. Commit `8ab6100a` adds bounded diagnostics for the NUnit result-file state. Run [39126322692](https://github.com/snapetech-org/chaptarrng/actions/runs/39126322692) failed after about 90 seconds with a missing NUnit summary, no totals, and no safe diagnostic; the cause is unconfirmed.

Run [39132760214](https://github.com/snapetech-org/chaptarrng/actions/runs/39132760214) on `9c051d3d` reported NUnit source line 162 in `should_preview_convert_and_restore_a_current_bookshelf_postgres_schema`. The fixture seeded `BookImportMinimumMatchPercent` with its original casing but checked lowercase, a fixture expectation error. ChaptarrNG commit [b5bd9527](https://github.com/snapetech-org/chaptarrng/commit/b5bd9527) corrects all three retained-config checks. Exact-head run [39135608892](https://github.com/snapetech-org/chaptarrng/actions/runs/39135608892) on commit [`43bc6363`](https://github.com/snapetech-org/chaptarrng/commit/43bc6363) passed Linux, macOS, and Windows platform validation and the release-note contract; all 4 synthetic PostgreSQL migration cases also passed, including normal-startup conversion with backup retention. This validates the migration-158 fixture only. The candidate remains unsupported for operator data and unpublished pending representative populated-data conversion and rollback, parity, and channel acceptance.
Numeric provider IDs follow the active
BookshelfNG mode: run preview and conversion
with `HARDCOVER=true` and without `HARDCOVER_NATIVE=false` when the source uses
native Hardcover, or the importer will interpret bare numbers as Goodreads
IDs. The preview report states the interpretation applied to authors, books,
editions, and series.

## Command shape after support is released

Use the same ChaptarrNG PostgreSQL main-database connection settings and app
data directory for preview, apply, and rollback. The exact option names are
available in the ChaptarrNG utility help:

```sh
dotnet /app/Chaptarr.dll -preview-bookshelf-migration -data=/config

dotnet /app/Chaptarr.dll -migrate-bookshelf \
  -confirm-bookshelf-service-stopped -data=/config

dotnet /app/Chaptarr.dll -preview-bookshelf-restore \
  -bookshelf-backup=bookshelfng_backup_YYYYMMDDHHMMSS_ID -data=/config

dotnet /app/Chaptarr.dll -preview-bookshelf-postgres-restore \
  -confirm-bookshelf-service-stopped \
  -bookshelf-postgres-log-cache-only \
  -bookshelf-postgres-backup=/config/.chaptarrng-bookshelf-handoff/<attempt-id>/postgres-source \
  -data=/config

dotnet /app/Chaptarr.dll -restore-bookshelf \
  -confirm-bookshelf-service-stopped \
  -bookshelf-backup=bookshelfng_backup_YYYYMMDDHHMMSS_ID -data=/config
```

Apply and rollback require the explicit stopped-service confirmation. Save the
backup schema name printed by apply. A PostgreSQL backup argument is a schema
name, not a filesystem path. The PostgreSQL restore preview validates the
independent dump files and confirms the databases are idle before a rollback
restores the primary BookshelfNG schema. Review files for ambiguous media
choices are stored beside `readarr.db` in the ChaptarrNG app data directory.

For a rollback after ChaptarrNG has been running, validate the retained full
PostgreSQL dump set first. Back up the current ChaptarrNG log/cache databases
before restoring the BookshelfNG main schema, then restore the source
log/cache databases after the main schema rollback. Use separate empty paths
under the handoff directory so both generations of data remain available:

```sh
mkdir -m 0700 /config/.chaptarrng-bookshelf-handoff/<attempt-id>/manual-target-postgres-log-cache
dotnet /app/Chaptarr.dll -preview-bookshelf-postgres-restore \
  -confirm-bookshelf-service-stopped \
  -bookshelf-postgres-backup=/config/.chaptarrng-bookshelf-handoff/<attempt-id>/postgres-source \
  -data=/config
dotnet /app/Chaptarr.dll -preview-bookshelf-restore \
  -bookshelf-backup=bookshelfng_backup_<schema> -data=/config
dotnet /app/Chaptarr.dll -backup-bookshelf-postgres \
  -confirm-bookshelf-service-stopped -bookshelf-postgres-log-cache-only \
  -bookshelf-postgres-backup=/config/.chaptarrng-bookshelf-handoff/<attempt-id>/manual-target-postgres-log-cache \
  -data=/config
dotnet /app/Chaptarr.dll -restore-bookshelf \
  -confirm-bookshelf-service-stopped -bookshelf-backup=bookshelfng_backup_<schema> -data=/config
dotnet /app/Chaptarr.dll -restore-bookshelf-postgres \
  -confirm-bookshelf-service-stopped -bookshelf-postgres-log-cache-only \
  -bookshelf-postgres-backup=/config/.chaptarrng-bookshelf-handoff/<attempt-id>/postgres-source \
  -data=/config
```

## Acceptance still required

- Current-source local PostgreSQL 17 conversion fixture: 8/8 cases pass,
  including older source versions 1, 13, 22, and 33; migration-158 preview,
  apply, restore preview, rollback, normal-startup conversion, and rejection of
  a future schema version and an unsupported view. This does not substitute for
  representative data.
- 2026-10-22 default-name compatibility validation: the full Core suite with
  PostgreSQL enabled passed 4,632 tests with 0 failures and 11 skips. The
  focused PostgreSQL 17 selector, schema-inspector, and converter checks passed
  22/22, including migration-marker inspection and real database-name
  selection across a conversion restart. Representative operator PostgreSQL
  handoff remains open.
- Hosted synthetic PostgreSQL conversion and rollback fixture: passed 4/4 cases on exact-head run [39175250100](https://github.com/snapetech-org/chaptarrng/actions/runs/39175250100), including normal-startup conversion and backup retention. This is one synthetic migration-158 schema. Representative operator data, independent backup/restore, and rollback acceptance on a supported deployment remain open.
- Verify PostgreSQL BookshelfNG migrations and ChaptarrNG runtime startup in
  the supported deployment, including sequence allocation after import.
- Rehearse a representative operator database and review every ambiguous media
  choice, preserved identity, setting, file link, and history disposition.
- Restore from the schema backup and from an independent `pg_dump` backup.
- Close the BookshelfNG feature-parity matrix and each package, image, updater,
  and install-channel handoff before enabling any release or legacy bridge.

Until those checks pass, use the supported SQLite route in the
[BookshelfNG migration guide](MIGRATE_FROM_BOOKSHELFNG.md), or keep the existing
BookshelfNG PostgreSQL installation in service.
