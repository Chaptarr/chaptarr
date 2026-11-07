# Cloudron BookshelfNG identity candidate

This candidate keeps the installed Cloudron application ID
`tech.snapetech.bookshelfng`, its `/config`, `/books`, and `/downloads`
persistent directories, port `8787`, and `/ping` health check. The standard ChaptarrNG image includes the first-start migration supervisor.
The Cloudron package wrapper enables it and explicitly carries the native
Hardcover mode used by the prior Cloudron image. Preserve `HARDCOVER=false` only
for an installation that previously used the Goodreads flavor. This is a
platform wrapper around the standard image, not a second ChaptarrNG build.
If a first-start attempt rolls back cleanly, set
`BOOKSHELFNG_MIGRATION_MODE=retry` only after correcting the cause. An
intervention-required state needs manual recovery.

The package installs a small nginx reverse proxy on Cloudron's stable port
`8787` and binds ChaptarrNG to loopback port `8788`. `/ping` is a liveness
response during migration and recovery, so Cloudron does not restart the
container while the supervisor is converting the database. Other requests are
forwarded to ChaptarrNG with WebSocket, streaming, and forwarded-header support;
they return an actionable migration/recovery page until the app is ready. If
either nginx or the supervised application exits, the package entrypoint stops
the other process and exits so Cloudron can recover the container.

Cloudron does not provide an interactive terminal prompt during an app update.
The update itself is the administrator's yes/no choice. To defer database
conversion while updating the package, set the app environment to
`BOOKSHELFNG_MIGRATION_MODE=manual` before applying the update; after reviewing
the Cloudron backup and migration plan, set it to `automatic` and restart the
app to accept conversion. Cloudron custom environment values override the
image defaults. The proxy keeps `/ping` healthy while manual mode waits, and
shows the migration instructions on normal app requests.

The candidate manifest and Dockerfile are not published. The Dockerfile uses
the standard ChaptarrNG image; candidate generation pins it to the exact
published multi-architecture digest. Before a Cloudron update is accepted, run
install, update, data backup, rollback, and health checks on Cloudron. Generate
`CloudronVersions.json` with the Cloudron CLI from the accepted package build;
do not create release metadata from an unbuilt candidate.

## Backup and restore

Cloudron does not include `persistentDirs` in its filesystem backup. The
manifest therefore sets `minBoxVersion` to `9.1.0` and uses `backupCommand` and
`restoreCommand` to carry `/config`, `/books`, and `/downloads` through the
backed-up `/app/data` directory. The backup hook uses SQLite's online backup
API for the active `readarr.db`, `audioarr.db`, `chaptarr.db`, `logs.db`,
`cache.db`, and `staging.db` files, records their presence and ownership, and
archives the persistent trees. The restore hook verifies the archive checksum
and database integrity, restores the bind mounts in place, removes stale
SQLite sidecars, and checks the restored databases again.

An installed Cloudron 10.1.6 VM rehearsal passed Manual defer, Automatic
migration, native app backup, and native restore after the test databases and
media canaries were changed. The restored schema-158 source returned healthy;
Automatic then converted it to schema 124 and retained a source backup. A
separate rollback restored the source database with Chaptarr's
`-restore-bookshelf` utility, restored the legacy `.4.21` package, and returned
BookshelfNG to healthy `/ping`.

The original `.4.21` package has no backup/restore hooks, so its pre-update
Cloudron backup contains app metadata and `/app/data` only; it does not contain
the persistent directories. The new hooks protect backups after the candidate
is installed. For an existing `.4.21` installation, use the candidate's
retained BookshelfNG source backup before restoring the old package after a
conversion. Cloudron runs `restoreCommand` in a temporary container before it
starts the main app container, with the restored `/app/data` and persistent
directories mounted. The restore hook therefore does not depend on a running
ChaptarrNG process.

The VM used a local test registry and a local exact-head ChaptarrNG image. This
is installed platform evidence, not a published Cloudron package or feed
acceptance. Results are recorded in the
[channel acceptance matrix](../../../docs/maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md)
and the [detailed Cloudron platform report](../../../docs/maintainers/CLOUDRON_PLATFORM_ACCEPTANCE_2026-10-26.md).

Cloudron package source historically lives in the BookshelfNG repository. This
ChaptarrNG-owned candidate records the package contract and supplies a safe
source handoff without changing the external Cloudron listing.
