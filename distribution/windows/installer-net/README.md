# ChaptarrNG Windows BookshelfNG migration installer

This is a **candidate-only, migration-only** Windows installer. It is not
published or linked from a release channel while BookshelfNG retirement remains
release-frozen. It does not install a fresh ChaptarrNG instance.

The installer upgrades an existing BookshelfNG Windows service from `Readarr`
to `Chaptarr`, keeping its selected data directory. It supports SQLite and
PostgreSQL sources and preserves the existing Windows service account and
startup policy. Built-in accounts and managed service accounts need no
password prompt. For a local or domain user account, the installer asks for its
password before stopping BookshelfNG, keeps it in secure memory only long
enough to configure Chaptarr, and never saves it in the registry, process
arguments, environment, or logs. The password is requested again only if a
later restore must recreate the removed Readarr service. Service-specific
virtual accounts such as `NT SERVICE\Readarr` cannot transfer to the differently
named Chaptarr service and fail closed. PostgreSQL handoff requires `pg_dump`
and `pg_restore` on `PATH` and an explicitly configured `PostgresUser`.
PostgreSQL settings in the existing service's NSSM environment blocks are
included in preflight and passed through to the Chaptarr service. A source
without an explicit database user still fails closed. If both BookshelfNG
SQLite databases are populated, the installer backs up and validates each,
previews the conflict-failing merge, and applies both only after operator
confirmation. Unsupported schemas and installations without the legacy Windows
service fail closed.

## Migration sequence

The installer requires administrator privileges and performs this sequence:

1. Detect the BookshelfNG `Readarr` service, its account and startup policy, and
   its configured `-data` directory. Request a custom account password before
   stopping the service when that account requires one.
2. Ask whether bare numeric provider IDs represent native Hardcover or Goodreads.
3. For PostgreSQL, check for the required client tools and an explicit database
   user before stopping the service. Stop BookshelfNG, then require that no
   other client sessions remain on the selected main, log, and cache databases.
4. Copy the original `bin`, config, every populated SQLite source and its WAL
   and journal, and existing user backups into a timestamped sibling backup
   directory. SQLite installs run `PRAGMA integrity_check` on each copied
   database. PostgreSQL installs use `pg_dump` to create and `pg_restore --list` to
   verify custom-format dumps for each unique main, log, and cache database.
   Each dump contains that database's active application schema. Rollback
   prevalidates the complete dump set and restores every database atomically
   per database.
   The dump keeps credentials in a temporary protected `PGPASSFILE`, excludes
   PostgreSQL owners and grants, and leaves unrelated database schemas alone.
   Backups are retained permanently; the installer does not rotate older ones.
5. Stage the ChaptarrNG payload outside `bin` and run the non-mutating migration
   preview with the chosen provider mode. A read-only SQLite connection holds
   `PRAGMA data_version` across preview and operator confirmation, then checks
   immediately before apply. Raw database, WAL, and rollback-journal
   fingerprints are captured before opening and after closing that connection;
   differences are diagnostic when `data_version` is unchanged. Windows may
   deny direct access to WAL bytes while SQLite holds the connection open.
   SQLite rebuilds its transient `-shm` index when the database is reopened, so
   it is not backed up. For PostgreSQL, keep all clients stopped through review
   and apply; the installer checks for other sessions before backup and again
   after preview review.
6. Show the preview output in the installer log and require a separate apply
   confirmation.
7. Apply the migration, replace `bin`, remove the `Readarr` service, register
   `Chaptarr` with the same explicit data directory, and set its service account
   to the saved BookshelfNG identity.
8. Verify the service reaches `RUNNING`, confirm its service command still
   contains the data directory, and check the configured local `/ping` endpoint.

Any failure after a complete backup invokes the embedded rollback helper. It
stops and removes either service registration, restores the PostgreSQL dumps
when the mutating converter has started, restores the saved BookshelfNG
binaries, config, SQLite files, and user backups, then reinstalls and starts
`Readarr`. A cancellation during preview leaves the untouched PostgreSQL data
in place. The backup is preserved after both success and failure. The UI also
offers **Restore BookshelfNG** after a successful migration. Manual restore
returns to the backup point, so Chaptarr changes made after migration are not
kept.

The preview and apply commands run with `HARDCOVER` set only in those child
processes. Native Hardcover mode sets `HARDCOVER=true`; Goodreads mode sets
`HARDCOVER=false`. No provider tokens are written to the registry or installer
logs.

## Build and candidate artifact

The Windows CI build packages the already-published ChaptarrNG application
files into the installer as an embedded zip. The output is an internal CI
artifact for validation; this step does not create or publish a release.

From a Windows PowerShell session after building the application into
`publish/Chaptarr`:

```powershell
./packaging/scripts/build-windows-bookshelf-migration-installer.ps1
```

The candidate executable is written to
`publish/ChaptarrNGInstaller/ChaptarrNG.Installer.exe`. The build script refuses
to overwrite an existing payload or output directory and removes the temporary
embedded zip after publishing.

Windows CI also runs a focused fingerprint regression smoke: changes to the
database, WAL, or rollback journal must invalidate the preview fingerprint,
while changes to SQLite's rebuildable `-shm` index must not.

Windows CI also runs the real installer upgrade and restore actions against a
disposable service installation. The smoke downloads BookshelfNG **0.4.21.71**
from its pinned public release asset and verifies the published SHA-256 before
use. It seeds the shared populated migration-158 fixture and disables provider
sync. First, it occupies the configured local HTTP port to make Chaptarr fail
its `/ping` check. The harness releases that port as soon as Chaptarr stops,
before rollback reinstalls the BookshelfNG service, then verifies automatic
restoration of the BookshelfNG service and database. It retries without the
conflict, verifies
the successful Chaptarr handoff and health check, then invokes the installer’s
explicit restore action and embedded rollback script. The test checks restored
BookshelfNG lineage, populated records, data directory, and NetworkService
account. It writes a cleanup manifest, then CI exits the smoke process before
a separate PowerShell step stops and deletes the test services. This releases
all smoke-process handles before Windows finalizes service deletion. The
cleanup step verifies each service registry entry is gone before removing test
registry state and temporary data; a failed cleanup keeps the scratch
directory for diagnosis.

To run the round-trip locally from an elevated Windows PowerShell session after
building the ChaptarrNG payload, set `RUNNER_TEMP` to a temporary directory and
run both the smoke and its post-exit cleanup step:

```powershell
$env:RUNNER_TEMP = $env:TEMP
$env:CHAPTARR_SERVICE_ROUNDTRIP_CLEANUP_MANIFEST = Join-Path $env:RUNNER_TEMP 'chaptarr-bookshelf-service-roundtrip-cleanup.json'
dotnet run --configuration Release --project ./distribution/windows/installer-net-smoke/ChaptarrNG.Installer.Smoke.csproj -- --windows-service-round-trip
$smokeExit = $LASTEXITCODE
./distribution/windows/installer-net-smoke/CleanupServiceRoundTrip.ps1 -ManifestPath $env:CHAPTARR_SERVICE_ROUNDTRIP_CLEANUP_MANIFEST
exit $smokeExit
```

The packaging script accepts `-KeepPayload` for this test build so the smoke
project can embed the same installer resources. CI keeps that temporary zip
through the Windows BookshelfNG service and Chocolatey handoff checks, then
removes it. The script’s default behavior still removes it immediately after
publishing the candidate installer.

## Remaining acceptance

The code and hosted build do not replace migration rehearsal on representative
Windows BookshelfNG data. Before enabling a release or update-channel handoff,
validate:

- A populated Windows SQLite or PostgreSQL install with provider IDs and paths representative
  of real deployments.
- An installed PostgreSQL handoff using the operator's `pg_dump`/`pg_restore`
  versions, database permissions, TLS settings, and an independent restore.
- Acceptance against representative operator data. The hosted round-trip uses
  a synthetic fixture and does not establish real-install parity.
- Failure injection beyond the local `/ping` port conflict, and additional
  failure points in backup, preview, apply, and service registration.
- Manual restore after Chaptarr has written to the migrated database.
- Installer behavior on each supported Windows BookshelfNG version and any
  supported legacy data-directory layout.

Keep BookshelfNG running and do not apply production migrations until the
maintainers explicitly close these acceptance gates.
