# Move a BookshelfNG installation to ChaptarrNG

> **Current status (2026-11-04 UTC; code head `d3a67c58`):** BookshelfNG
> `0.4.21.76` and ChaptarrNG `v0.9.977` remain the latest public releases; the
> local `0.9.978` candidate is unpublished. Trusted jobs may use compatible
> public GitHub-hosted runners again under [`AGENTS.md`](../AGENTS.md); private
> ephemeral JIT remains available for jobs that need it.
>
> Earlier full public Build run
> [41755889814](https://github.com/snapetech-org/chaptarrng/actions/runs/41755889814)
> at `29b5f31f` passed PostgreSQL migrations and the standard Docker build, but
> failed Linux/macOS RTL/localization and Windows translation-catalog checks
> before those jobs reached their full build and test stages. Draft PR #190
> remains untouched.
>
> Earlier Windows Chocolatey run
> [41756297204](https://github.com/snapetech-org/chaptarrng/actions/runs/41756297204)
> passed .NET tests, build, service round trip, and migration health, then
> exposed rollback backup discovery missing the retained SQLite source. The
> installer now includes hidden backups and reports the names it examines;
> exact-head rerun at `8b26fa3e` was superseded by focused public Windows run
> [41785474409](https://github.com/snapetech-org/chaptarrng/actions/runs/41785474409)
> on `d12798d1`. It passed the full Windows .NET suite, installer build and
> SQLite fingerprint, service upgrade/rollback, selected data-directory
> preservation, and the installed BookshelfNG `.76` to exact-candidate
> Chocolatey handoff. Manual defer, injected migration-health rollback with
> source recovery, and automatic migration all passed.
>
> Full public Build run
> [41789426720](https://github.com/snapetech-org/chaptarrng/actions/runs/41789426720)
> at `d12798d1` passed Linux and macOS build/test/artifact jobs, PostgreSQL
> migration smoke, and the standard Docker build. Windows found that the
> locale test hashed a JSON registry after CRLF checkout conversion. Commit
> `d3a67c58` normalizes text line endings before hashing and adds an LF/CRLF
> regression test. Full public rerun
> [41792679639](https://github.com/snapetech-org/chaptarrng/actions/runs/41792679639)
> passed on `d3a67c58`: Linux, Windows, macOS, PostgreSQL migration smoke, and
> the standard Docker build all succeeded. The Windows job passed the installer
> fingerprint, service upgrade/rollback, selected-data-directory check, and
> installed Chocolatey `.76` automatic handoff.
>
> Installed macOS acceptance
> [41761249985](https://github.com/snapetech-org/chaptarrng/actions/runs/41761249985)
> passed on Intel and ARM64 at `8b26fa3e`: No, headless Manual, Yes, health,
> source backup, and failed-start rollback. Helm installed acceptance
> [41756992768](https://github.com/snapetech-org/chaptarrng/actions/runs/41756992768)
> passed populated migration, provider-ID/narrator checks, health, database
> integrity, source backup, chart rollback, and BookshelfNG recovery with
> matching database/config/canary fingerprints.
>
> Keep BookshelfNG running and backed up. Representative operator data, live
> provider/media behavior, remaining installed-channel recovery, external
> delivery, and operator acceptance remain open. These CI and synthetic-fixture
> results do not authorize publication or production cutover.

> **Historical source-path audit snapshot (2026-10-31):** The ledger compares
> BookshelfNG `1bd7f2dc` to ChaptarrNG
> `main` target `fa55ffab` (application source `bd589312`): 1,174 changed paths,
> 56 reviewed mappings, 587 dispositions, and 587 paths without disposition.
> Path counts are review records, not feature-parity or migration acceptance.
> ChaptarrNG's separate upstream-retirement gate includes this full carryover
> plus every ChaptarrNG feature that is maintained, active, or unreleased.

> **Historical channel status (2026-10-26).** ChaptarrNG `v0.9.977` remains
> the latest public release. Local 0.9.978 candidates have passed installed handoff
> acceptance on Debian, Fedora, Arch, Windows Chocolatey, AppImage, Flatpak,
> Helm, Docker, Cloudron, and Co-op Cloud. Docker offers y/n with an attached
> TTY; headless Docker and Helm defer Ask mode. Cloudron 10.1.6 passed an
> installed `.4.21` to `.4.23` update, manual defer, native backup/restore,
> automatic migration, and source-database plus old-package rollback. FreeBSD
> standalone archives passed installed No/Yes and rollback acceptance in a
> FreeBSD 15.1 VM. These candidates remain unpublished; StartOS, Co-op Cloud's
> canonical recipe, Unraid, external catalogs, the standalone Windows
> installer, and macOS still need platform-specific acceptance. Representative operator-data
> and provider acceptance remain open, and no release, bridge publication, or
> cutover has been authorized. See the
> [channel acceptance matrix](maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).

> **Superseding image design (2026-10-30):** Older evidence below may mention
> provider-specific compatibility images or tags. That proposal was withdrawn.
> All legacy aliases and generated platform candidates use the same standard
> ChaptarrNG image; generated release candidates pin its exact digest. Provider
> mode comes from preserved channel configuration or an explicit operator
> choice. The standard container entrypoint recognizes `readarr.db`,
> `audioarr.db`, and legacy Readarr/Bookshelf PostgreSQL settings and starts
> the rollback supervisor automatically. When provider IDs are bare numeric
> and their source mode is unavailable, preview stops without changing the
> source; set the old `HARDCOVER` mode and retry. No redirect, release, or
> cutover is authorized by this note.

> Local Docker and Helm acceptance exercises passed, including deferred Ask,
> automatic handoff, health checks, and recovery. They do not publish an
> image/chart or establish acceptance of external catalogs and appliance
> platforms.
>
> SQLite and PostgreSQL migration operate on staged copies and retain the
> untouched source for rollback. Synthetic conversion and rollback coverage is
> recorded in the [PostgreSQL candidate notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md)
> and [fixture evidence](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).
> Those fixtures and prior populated rehearsals do not establish acceptance of
> representative operator data, live provider fulfillment, real-media recovery,
> installed rollback, or every distribution channel. Unknown provider/plugin
> implementations intentionally block apply rather than being silently
> activated or discarded. The notes below are dated evidence snapshots, not a
> release or cutover authorization.

After technical readiness closes, run the migration with the standard
ChaptarrNG application and container build. The `Chaptarr` executable provides
explicit preview and apply commands, and normal startup also detects and
converts a supported BookshelfNG database automatically. Do not run normal
ChaptarrNG startup against BookshelfNG appdata while cutover readiness remains
incomplete.

## Choose the migration timing

Legacy package updates keep automatic migration as the unattended default.
To choose before conversion, set `MigrationMode=ask` for Chocolatey or
`BOOKSHELFNG_MIGRATION_MODE=ask` for Debian, RPM, or AUR. The prompt defaults
to **No**. Chocolatey, dpkg, and RPM can cancel their package transaction
before service or application staging. Pacman cannot cancel a transaction from
a scriptlet: on No, the AUR hook lets the transaction complete, restores the
saved BookshelfNG application/unit/launcher, leaves the database unchanged, and
defers conversion to the next package transaction. Use `manual` to defer
conversion without prompting; whether that cancels package installation depends
on the package manager.

| Choice | What happens |
| --- | --- |
| Automatic | Package transitions convert BookshelfNG data automatically; this remains the unattended default. Desktop launchers convert at first launch. |
| Ask | Prompts default to No. Chocolatey/dpkg/RPM cancel before staging. Pacman completes the package transaction, restores the prior BookshelfNG runtime, and retries the choice on a later transaction. Desktop and container No defer conversion without undoing deployment. |
| Manual | Defers conversion without prompting. Chocolatey/dpkg/RPM cancel the package transaction; the AUR transaction completes and restores BookshelfNG. Desktop launchers skip conversion. Use the standalone Console to preview and apply when ready. |

AppImage and Flatpak read `BOOKSHELFNG_MIGRATION_MODE` at first launch of the
new artifact. There, **No** or `manual` skips startup conversion and leaves
the source database untouched; it does not undo an AppImage download or a
Flatpak deployment. Declining exits the new desktop launcher; use your previous
AppImage or restore the previous Flatpak deployment if you want to keep using
BookshelfNG before migrating. After a manual conversion, launch the desktop app
again.

Chocolatey examples:

```powershell
choco upgrade bookshelfng --yes --params "'/MigrationMode=ask'"
choco upgrade bookshelfng --yes --params "'/MigrationMode=manual'"
```

For Debian, RPM, and AUR package hooks, the environment variable accepts
`automatic`, `ask`, or `manual`. Debian and RPM require a terminal for
`ask` and cancel before staging when none is available. Pacman has no
transaction-cancel hook: without a terminal it completes the package
transaction, restores BookshelfNG, and defers conversion. Set `automatic` or
`manual` explicitly for noninteractive runs. For example:

```sh
sudo env BOOKSHELFNG_MIGRATION_MODE=ask apt-get upgrade bookshelfng
sudo env BOOKSHELFNG_MIGRATION_MODE=ask dnf upgrade bookshelfng
BOOKSHELFNG_MIGRATION_MODE=ask yay -S bookshelfng-bin
```

The Debian, RPM, and AUR systemd bridge gives conversion up to 245 minutes to
pass its database-readiness check and allows the same bounded window for the
complete service start, including preflight and PostgreSQL backup. Stop grace
allows up to 510 minutes for a bounded migration and recovery. Set
`CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS` in the existing package
environment file to choose a shorter health window; accepted values are 1 to
14700 seconds, with 14700 as the default.

The desktop supervisor accepts the same variable. AppImage and Flatpak use a
GUI confirmation when `zenity` is available; Flatpak can pass it to one
launch with
`flatpak run --env=BOOKSHELFNG_MIGRATION_MODE=ask com.snapetech.BookshelfNG`.

Container candidates also accept `BOOKSHELFNG_MIGRATION_MODE`. Their default is
`automatic`; `manual` leaves the container running without starting the app,
and `ask` behaves as manual when the platform has no interactive terminal.
`retry` is an explicit re-attempt after a clean automatic rollback: the
supervisor requires either a valid completed migration or a fresh successful
preview of the restored BookshelfNG source. It does not bypass an
`intervention-required` state.
When both `readarr.db` and `audioarr.db` contain data, automatic startup stages
and validates a merge of both libraries before promoting `chaptarr.db`. The
original databases remain unchanged and are fingerprinted for rollback.
Conflicting identities, settings, or references stop the upgrade before
promotion and leave the service unready. In manual migration mode, use the
explicit `-merge-bookshelf-databases` preview/apply commands below after
stopping every process that uses the directory.
Kubernetes uses the supervisor marker for startup/liveness and the app port
for readiness, so a deferred or failed handoff does not trigger a pod restart
loop while readiness remains false. Docker health reports readiness only after
the handoff marker is cleared and `/ping` responds. Docker's startup grace and
the Kubernetes startup-probe budget allow the 245-minute migration/startup
window. Compose, Helm, and Unraid stop grace allows 510 minutes for a bounded
migration and recovery. StartOS uses the same marker-aware health script and
does not report ready until the migration marker clears and the converted
database and local `/ping` check pass. A retained failed or
deferred marker keeps health false for operator recovery. Docker does not
restart a container solely because its health status is unhealthy. After a
manual migration, restart with `automatic`; after a clean automatic rollback,
set `retry` once the cause is corrected. An `intervention-required` state needs
the documented manual recovery or a return to the prior BookshelfNG image.
On StartOS, use the **Configure BookshelfNG migration** package action to
choose automatic or manual first-start migration; automatic is the default,
and the choice is included in StartOS backups.

BookshelfNG selects native Hardcover identities only when `HARDCOVER=true` and
`HARDCOVER_NATIVE` is not `false`; its runtime defaults an unset or non-true
`HARDCOVER` value to Goodreads. The migration importer requires an explicit
provider mode for every BookshelfNG conversion so it can persist the source
dialect in the compatibility profile. Set `HARDCOVER=true` or `false`, or use
`HARDCOVER_NATIVE=false` to explicitly select Goodreads. Preserve the source
values for both preview and apply; missing or invalid values stop conversion
before the source database is changed. The single standard image and its legacy
aliases do not encode provider mode. Cloudron, Co-op Cloud, catalog Compose,
Unraid, and StartOS candidates preserve those channels' native Hardcover
defaults; set `HARDCOVER=false` when the source used Goodreads. Standalone
desktop launchers keep their Goodreads default. The Cloudron candidate keeps
`/ping` available during supervised migration on its existing port while
proxying application traffic to the local ChaptarrNG process. It has no
terminal prompt: the administrator holds or applies the app update, or sets
`BOOKSHELFNG_MIGRATION_MODE=manual` before updating and switches to `automatic`
when accepting conversion. Cloudron 10.1.6 installed update, native
backup/restore, automatic migration, and source-database plus old-package
rollback passed locally; feed publication remains open, and the legacy `.4.21`
pre-update backup excludes persistent directories. The Helm bridge infers the
old chart's flavor and readable overrides, and requires
`bookshelfMigration.providerMode` when a Secret or unrecognized image tag
leaves the effective mode ambiguous. Never infer provider mode from numeric
IDs.

### Migrate with the standalone ChaptarrNG Console

Download and extract the matching ChaptarrNG release archive for the host.
Stop BookshelfNG and every other process using its data directory, then run a
read-only preview. Apply only after reviewing a successful preview. The apply
command retains a BookshelfNG backup. Once it succeeds, rerun the package
transition in its default `automatic` mode (do not pass `manual`); the package
will validate the converted target database and skip a second conversion. If
the data directory already passes `-check-bookshelf-migration`, skip apply and
complete the package transition.

On Windows, run PowerShell as an account that can stop the service and access
the BookshelfNG data directory:

```powershell
Stop-Service -Name BookshelfNG
$console = 'C:\path\to\Chaptarr.Console.exe'
$data = 'C:\ProgramData\BookshelfNG\data'
& $console -preview-bookshelf-migration "-data=$data"
if ($LASTEXITCODE -ne 0) { throw 'Migration preview failed; do not apply.' }
& $console -migrate-bookshelf -confirm-bookshelf-service-stopped "-data=$data"
if ($LASTEXITCODE -ne 0) { throw 'Migration did not complete; preserve the reported backups.' }
# Then rerun the Chocolatey transition in its default automatic mode.
choco upgrade bookshelfng --yes
```

On Linux, replace the paths with the extracted Console and installed data
directory:

```sh
sudo systemctl stop bookshelfng.service
console=/path/to/Chaptarr
data=/var/lib/bookshelfng
"$console" -preview-bookshelf-migration "-data=$data" || exit 1
"$console" -migrate-bookshelf -confirm-bookshelf-service-stopped "-data=$data" || exit 1
```

For an app-data directory with both populated `readarr.db` and `audioarr.db`,
add `-merge-bookshelf-databases` to the preview and apply commands. The
merge preserves both original databases and stops before promotion if record
identities or settings conflict.

### One-time bootstrap for a manually installed Linux systemd service

BookshelfNG's old in-app updater only checks the Readarr update service, so it
cannot discover the first ChaptarrNG release. For a manual Linux systemd
installation, download the matching `linux-x64` or `linux-arm64` release
archive and its `.sha256` sidecar from the ChaptarrNG release page. Extract the
archive, then run its `scripts/bookshelfng-systemd-bootstrap.py` as root while
the existing BookshelfNG service is running. It asks `Replace BookshelfNG with
ChaptarrNG now? [y/N]` by default; No, a blank answer, or end-of-input returns
before acquiring the handoff lock, stopping the service, or writing files.
Without an interactive terminal, Ask mode fails closed. Pass
`--migration-mode automatic` only when an unattended replacement is intended.
After Yes, the bootstrap verifies the archive checksum and service executable,
captures the service's effective
environment in memory, detects a dual-database SQLite source, and previews its
merge with the same service account. It retains the prior application and
`config.xml`, then installs ChaptarrNG at
the old executable path. The service's first normal startup performs
migration; temporary systemd hooks verify HTTP health and database lineage and
restore the source database, config, and application if the handoff fails or
is interrupted. The temporary unit override allows up to 245 minutes for
startup and up to 510 minutes for stop and recovery. PostgreSQL installs also
retain and restore the source log/cache schemas.

The running service must use an explicit `-data` argument, and it must resolve
to the same directory passed as `--data-dir`. The bootstrap checks this before
stopping the service or changing files. `--health-timeout-seconds` accepts
1–14700 seconds and defaults to 14700; its temporary systemd unit allows an
additional 180 seconds for service start and up to 30600 seconds for stop and
rollback recovery.

Example for an installation whose service starts
`/opt/bookshelfng/Readarr` and uses `/var/lib/bookshelfng`:

```sh
version=<accepted-chaptarrng-release>
sudo python3 ./ChaptarrNG/scripts/bookshelfng-systemd-bootstrap.py \
  --service bookshelfng.service \
  --install-dir /opt/bookshelfng \
  --legacy-executable /opt/bookshelfng/Readarr \
  --data-dir /var/lib/bookshelfng \
  --archive "$PWD/ChaptarrNG-${version}-linux-x64.tar.gz" \
  --backup-dir "/var/backups/chaptarrng-bookshelfng-${version}-first-handoff"
```

Use the exact archive filename and architecture for the host. The backup path
must be new and absolute, outside both the install and data directories, and
its existing parent must be root-owned and not writable by group or other
users. For the example, `/var/backups` is the existing parent; the bootstrap
creates the versioned child directory. Paths used by the systemd recovery hook
cannot contain whitespace. Keep an independent appdata backup too.
The bootstrap supports active systemd services whose executable lives inside
the stated install directory; package-managed installations should continue
through their existing package identity instead. After BookshelfNG migration,
the built-in updater checks checksum-verified ChaptarrNG GitHub releases.

If automatic rollback records `intervention-required`, systemd keeps the
service blocked and the backup directory retains the source database, config,
and both application snapshots. After correcting the reported external issue,
retry the guarded rollback with the retained helper:

```sh
sudo python3 "/var/backups/chaptarrng-bookshelfng-${version}-first-handoff/bookshelfng-systemd-bootstrap.py" \
  --recover \
  --state-file "/var/backups/chaptarrng-bookshelfng-${version}-first-handoff/state.json"
```

Recovery runs through the original systemd unit so its configured database
environment stays in effect. It only starts BookshelfNG after the rollback
state is verified.

For a package-managed install that used the standalone Console migration above,
complete the package transition using the package source already configured
on that host:

| Platform | Package transition |
| --- | --- |
| Debian or Ubuntu | `sudo apt-get install --only-upgrade bookshelfng` |
| Fedora | `sudo dnf upgrade bookshelfng` |
| Arch AUR | `yay -S bookshelfng-bin` |

For PostgreSQL, run the Console with the same database connection environment
used by the BookshelfNG service and ensure its PostgreSQL backup tools are
available. Keep the service stopped until the package transition is complete.
If preview or apply reports an error, preserve the source database and every
reported backup for review; do not restart the old service after apply has
begun.

### One-time handoff for standalone macOS and FreeBSD archives

BookshelfNG's old in-app updater cannot discover ChaptarrNG releases. For a
manual standalone macOS or FreeBSD install, download the matching ChaptarrNG
`osx-x64`, `osx-arm64`, or `freebsd-x64` archive and its `.sha256` sidecar.
Compare the archive's digest with the first field in the sidecar. Use
`shasum -a 256 ARCHIVE.tar.gz` on macOS or `sha256 ARCHIVE.tar.gz` on FreeBSD.
Extract ChaptarrNG beside the existing BookshelfNG files. Stop the old process
before starting the replacement. Use the bundled
`scripts/bookshelfng-standalone-launch.sh` for the first start; running
`Chaptarr` directly skips the optional handoff prompt. The launcher defaults
to Ask when a terminal is available and Manual when it is not. Answer No to
leave the predecessor data unchanged; restart BookshelfNG if you stopped its
service before the handoff. Answer Yes to stage migration and start the
replacement. You can also set `BOOKSHELFNG_MIGRATION_MODE=ask`, `manual`, or
`automatic` explicitly. Use the exact data directory, arguments, and service
environment from the existing launcher, including any PostgreSQL and provider
settings. Keep credentials in the existing service environment; do not print
or paste them. Keep the prior application directory until the first ChaptarrNG
startup passes its health check.

For example, from the extracted ChaptarrNG directory:

```sh
./scripts/bookshelfng-standalone-launch.sh "$HOME/.local/share/bookshelfng" -nobrowser
```

Replace the example data path with the path used by the existing process. On
Yes, the supervisor runs the staged migration, retains the source database,
checks startup health, and restores the source on a failed handoff. It does not
rewrite a LaunchAgent, rc script, or local launcher; update those to point at
the new `Chaptarr` executable only after startup succeeds. Package-managed
services should use their package transition instead. This first archive
download and launcher change is a one-time operator action. After the migrated
database is accepted, ChaptarrNG's built-in updater uses its checksum-verified
GitHub release route.

## Choose the user experience

ChaptarrNG stores the selected experience with the UI settings. A migrated
BookshelfNG database defaults to **Simple**, which keeps the common author and
book lists, Bookshelf, Add New, calendar, queue, missing books, core settings,
and basic system pages in the navigation. Fresh ChaptarrNG installs default to
**Advanced**, with the full navigation available. The header shows the current
mode and provides a one-click switch. The same choice is available under
**Settings → UI → Experience**. The mode changes
navigation density and hides advanced form fields in Simple mode; it does not
change library data or remove configuration. The separate Advanced Settings
control continues to show or hide advanced fields in Advanced mode.

The ChaptarrNG source also includes
[`scripts/bookshelfng-to-chaptarrng.sh`](../scripts/bookshelfng-to-chaptarrng.sh),
a SQLite-only host helper for Docker installations. Each action first creates
a mode-`0600` archive of the full appdata directory in a separate backup
directory. The helper supports `snapshot`, migration `preview` and `apply`,
and restore `preview` and `rollback`. When both `readarr.db` and `audioarr.db`
contain data, migration requires the explicit `--merge-bookshelf-databases`
option; conflicting identities or settings stop before either original is
changed. Rollback uses `chaptarr.db` and its retained merge receipt, so the
option is not needed while both originals remain in place. PostgreSQL users
should use a channel handoff that creates and verifies the independent database
dump; this SQLite helper does not create one. Migration requires an explicit
Goodreads or native Hardcover choice and accepts only a versioned ChaptarrNG
image or digest. Native Hardcover migration requires a versioned `v0.9.978` or
newer image; published `.977` does not support that identity conversion. The
source packaging workflow includes the helper in Linux x64 and arm64
standalone archives. The published `.977` archives do not contain this newer
bootstrap. No new release or cutover is authorized by this source change.

The Add New page carries over the BookshelfNG “search as you type” choice when
you open ChaptarrNG from the same browser origin. If you move to a different
host, scheme, or port, browser storage is separate; set the option on the
ChaptarrNG Add New page there.

Series rows include the BookshelfNG series pack search action. Grabbed packs
retain their series books for ChaptarrNG's existing Manual Import review; check
the file-to-book assignments before importing a pack.

Interactive search checks the selected torrent client for a matching info hash.
If it finds one, ChaptarrNG offers to adopt that existing item into its tracked
queue without starting a second download. Confirm the match before adopting it.

If both `readarr.db` and `audioarr.db` are populated, normal ChaptarrNG startup
validates and merges them automatically. Operators who selected manual
migration can run the explicit SQLite merge utility after stopping every
process that uses the directory:

```sh
Chaptarr -preview-bookshelf-migration -merge-bookshelf-databases -data=/path/to/appdata
Chaptarr -migrate-bookshelf -merge-bookshelf-databases -confirm-bookshelf-service-stopped -data=/path/to/appdata
```

The source helper accepts the same opt-in as
`bookshelfng-to-chaptarrng.sh preview|apply ... --merge-bookshelf-databases`.
Preview and apply make consistent working copies, run the bundled source
migrations on those copies, and merge only records whose identities remain
unambiguous. Reused primary IDs with different row contents, different values
for the same configuration key, and other unique-key/reference conflicts stop
the operation before `chaptarr.db` is promoted. Library and provider IDs are
not silently rewritten; the internal `Config.Id` key may be reassigned after
settings are reconciled by key and value. Transient commands, health state,
pending releases, and schedules follow the normal migration reset behavior
and remain available in the retained source files. The original `readarr.db`
and `audioarr.db` files are retained unchanged beside the new `chaptarr.db`; a
receipt fingerprints both retained database families so ChaptarrNG refuses
startup if either is edited after the merge. Keep both original source files
until rollback is no longer needed; dual-database rollback requires both to
remain present and unchanged. If either is removed, restore it from the
independent appdata backup before rolling back. The merged BookshelfNG snapshot
is retained under `chaptarr.db.bookshelfng-backup-*` and is accepted by the
normal rollback command. For this dual-database case,
rollback moves the ChaptarrNG target aside and leaves both original source
databases in place, so BookshelfNG can resume and a later ChaptarrNG upgrade
can merge their latest contents. The displaced ChaptarrNG database remains
available as a separate rollback artifact.

If ambiguous media-format choices are present in both databases, their
`*.bookshelf-media-map.json` sidecars are combined when their choices do not
conflict. A reviewed combined map can instead be supplied as
`bookshelfng-merge-media-map.json`; merge preview writes a separate
`bookshelfng-merge-media-review-*.json` report. Keep independently operated
installations in separate appdata directories and migrate each one separately.
The published BookshelfNG `.71` helper may print a zero-byte `readarr.db` beside
a populated `audioarr.db`; ChaptarrNG resolves the sole populated file without
requiring this merge option. The BookshelfNG two-instance consolidation
runbook remains the recommended path when the databases have conflicting IDs
or settings; it consolidates media, not database histories, profiles,
monitoring, or settings.

The published `v0.9.977` converter supports BookshelfNG migration-158 SQLite
databases and bundles the BookshelfNG source migration chain. It upgrades a
recognized source schema on a disposable copy, keeps the untouched snapshot
for rollback, then stages a fresh native Chaptarr database, accounts for every
source-table row, checks SQLite integrity and foreign keys, and promotes only
after validation. The public `.977` release rejects schemas before migration
14 because its table guard sees the pre-rename `Blacklist` name. PR #126 merged
a version-aware allowlist fix and synthetic migration-1 rollback fixture into
`main`; hosted Linux, macOS, Windows, and PostgreSQL migration checks passed.
That source fix is not in `.977`. Populated synthetic v22 coverage exercises
conversion and rollback, but it is not a real-data rehearsal or proof for every
historical BookshelfNG release. Unknown or newer schemas fail closed without
changing the source.

The converted Chaptarr database retains a `BookshelfLegacyIdentityMap` for
source author, book, edition, and book-file IDs. A book split by media type has
one row for each resulting Chaptarr book. Format-scoped `GET /api/v1/book`
lookups and book-scoped API operations resolve these mappings, while the legacy
Bookshelf monitoring endpoint expands selected source IDs to their target rows.
Generic API commands that carry book IDs also resolve migrated BookshelfNG
identities. Book searches, refreshes, cover downloads, and supported metadata
pushes include every target format row for an unscoped request; a format-scoped
Readarr facade stays within its ebook or audiobook row. A `RefreshBook` command
whose legacy book split into both formats is queued as `BulkRefreshBook` so both
records refresh.
Other clients can query the map with
`GET /api/v1/bookshelf/legacy-identities?entityType=Book&sourceIds=30` (up to
500 IDs per request). The map remains in the Chaptarr database and its normal
database backups. It helps migrate local references, while provider IDs remain
the durable identity for newly built integrations.

**Prior PR #131 application-code validation (2026-10-19):** Code snapshot `0612cb4c90b75818e5349eb868cc0638825de02a` passed exact-head Build [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373): Linux, macOS, and Windows tests, PostgreSQL migration smoke, native-runner receipt reconciliation, and the release-note contract all passed. CodeQL [39239934583](https://github.com/snapetech-org/chaptarrng/actions/runs/39239934583), security [39239940344](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940344), NuGet submission [39239932165](https://github.com/snapetech-org/chaptarrng/actions/runs/39239932165), and dependency review [39239940313](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940313) passed. Disposable-fork validation [39239940512](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940512) was skipped by its external-fork gate. SeerrNG's adapter and request-route suite at source commit `b810153c` passed 4 files / 283 tests. The Linux artifact also passed the earlier empty-library API and authorization smoke. The populated source-build check on later code is recorded below.

**Prior candidate code CI (2026-10-19):** ChaptarrNG PR #131 head `38be9422a0390c0b3d4d1c453ec14b25d3aa99a4` includes portable BookshelfNG BKSENC01 `.enc` backup export and restore. After two compile fixes and a cleanup-callback test-fixture correction, exact-head Build [39267876585](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876585) passed Linux, macOS, Windows, PostgreSQL migration, release-note, and native-runner receipt checks. CodeQL [118112421054](https://github.com/snapetech-org/chaptarrng/runs/118112421054), security analysis [39267876503](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876503), and dependency review [39267876754](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876754) passed. Disposable-fork validation was skipped by its external-fork gate. This is code validation only; migration, parity, installed rollback, update-channel, and operator-acceptance gates remain open.

**M4B fix and previous exact-head validation (2026-10-19):** PR #131 commit
`c4fa8125095f583ad9eee534f70328e8eef1b44c` fixes a Move-mode defect found by
embedded-chapter tests: after a profile-based multipart M4B merge succeeded,
the importer kept its source tracks. Fixture isolation alone did not fix it;
Builds [39275841754](https://github.com/snapetech-org/chaptarrng/actions/runs/39275841754)
and [39287179772](https://github.com/snapetech-org/chaptarrng/actions/runs/39287179772)
still failed the Move cleanup check. Exact-head Build
[39288736177](https://github.com/snapetech-org/chaptarrng/actions/runs/39288736177)
passed Linux's full suite and pinned-tool M4B checks, PostgreSQL migration
smoke, and release-note contract. Windows build and full test steps passed. The macOS job was cancelled before a runner started, so the cross-platform matrix is incomplete. CodeQL
[39288728262](https://github.com/snapetech-org/chaptarrng/actions/runs/39288728262),
security analysis
[39288735208](https://github.com/snapetech-org/chaptarrng/actions/runs/39288735208),
dependency review
[39288734998](https://github.com/snapetech-org/chaptarrng/actions/runs/39288734998),
and NuGet submission
[39288748702](https://github.com/snapetech-org/chaptarrng/actions/runs/39288748702)
passed; disposable-fork validation was skipped by its gate. Representative
M4B media, restart/resume, and operator acceptance remain open. No release or
bridge was published.

**Latest passed PR #131 application-code validation (2026-10-19):** Code commit
`b7e900d49648269d5118bbfba44289c0200b095b` passed Build
[39292223717](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223717)
on Linux, macOS, and Windows, including PostgreSQL migration smoke,
release-note contract, native-runner receipt reconciliation, and the Linux
pinned-tool M4B smoke. CodeQL, security/secret scan, dependency review, and
NuGet submission passed; disposable-fork validation was skipped by its gate.
This validates application code only. PR #131 later merged on 2026-10-20.
It does not authorize production conversion or close the representative-data,
parity, installed-rollback, and channel-acceptance gates.

**SeerrNG pending-request lifecycle probe (2026-10-19):** On the isolated Linux candidate from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), SeerrNG's real `ReadarrAPI` (`b810153c`) submitted an ebook request for a synthetic work whose author was unavailable. `addBook` received a pending ID, `getPendingAuthorImport` read it, `cancelPendingAuthorImport` cancelled it, and a second poll observed the expected terminal cancellation state: `OverallStatus=Failed` with `LastError=Cancelled by user`. The cancelled record remains queryable rather than being removed. This proves request creation, polling, and cancellation across the adapter/API boundary. It used a disposable database and synthetic IDs; provider-success behavior, retries, availability, history, migrated records, production settings, full SeerrNG operator workflow, and operator acceptance remain open. The tested app artifact is code snapshot `0612cb4c`; PR commit `d9713604` added the repeatable helper and docs, and the intervening app change is Calibre selector tie-breaking rather than a SeerrNG API change. The probe is now repeatable through `integration/seerrng-request-lifecycle.mjs` and is wired after the authorization smoke in `integration/seerrng-smoke.compose.yml`. On 2026-10-19, both scripts passed when run against the isolated artifact from Build 39239940373 and SeerrNG source `b810153c`; `node --check`, Prettier, and Compose configuration validation also passed. The earlier Compose invocation used a temporary runtime image with `--no-build`. A follow-up source-build run on 2026-10-19 built the candidate through the checked-in `Dockerfile.build` from PR head `7cfb5d87` and passed both scripts after the harness prepared its disposable `/books` volume as UID/GID `99:100`. Successful fulfillment, retries, availability/history, converted records, saved operator settings, and acceptance remain open.

## Encrypted BookshelfNG portable backups

The retirement candidate adds passphrase-based `.enc` backup export and
restore using BookshelfNG's BKSENC01 format. This allows a BookshelfNG
encrypted portable backup to be selected directly in ChaptarrNG's restore
dialog, and lets ChaptarrNG create backups that BookshelfNG can read. The
passphrase is entered through the UI or sent in the
`X-Bookshelf-Backup-Passphrase` API header. ChaptarrNG's scheduled `.zip.enc`
files use a separate format and secret-file setting. Details and limits are in
the [encrypted backup guide](BOOKSHELFNG_ENCRYPTED_BACKUPS.md). This candidate
change is not in published `v0.9.977` and does not replace independent
appdata/database backups or close the release gates.

## Restore a BookshelfNG backup archive

The published `.977` restore service accepts Chaptarr/Readarr-format ZIP
archives but rejects a BookshelfNG database inside them. Merged PR #131 adds
support for a recognized BookshelfNG database in a standard
`readarr_backup_*.zip` archive: ChaptarrNG checks SQLite integrity, BookshelfNG
lineage, the source migration-version range, and the legacy table-name boundary,
then stages the database for restore. On normal startup, the existing converter
upgrades a disposable source copy, builds and validates a ChaptarrNG database,
and retains the original BookshelfNG database for rollback. The archive itself
remains untouched. The previous ChaptarrNG database is restored if conversion
fails and its temporary `.pre-restore` copy is removed after success. A
regression fixture covers restore followed by automatic conversion. ChaptarrNG
PR #131 code head `6f57d355` passed exact-head Build run
[39211491613](https://github.com/snapetech-org/chaptarrng/actions/runs/39211491613)
on Linux, macOS, Windows, PostgreSQL migration, and release-note validation.
The fixture uses the complete migration history expected in a BookshelfNG
schema-158 database. This is hosted synthetic validation, not operator-data
acceptance. The source-candidate path is not available in `.977` and must not
be used for production cutover while the retirement gates remain open.

Current `main` also accepts raw `.db` and `.xml` backup uploads through the
same restore endpoint. The `.xml` suffix is a legacy filename; the uploaded
content must still be a SQLite database. ChaptarrNG checks SQLite integrity
and database lineage before staging it, and applies BookshelfNG source-version
and table-boundary validation when the database is BookshelfNG. Normal startup
then converts the staged database and retains the source for rollback. These
raw upload extensions are not accepted by published `v0.9.977`.

The published `.977` converter does not support PostgreSQL. Current `main`
contains a separate PostgreSQL route that migrates recognized older source
schemas on a disposable PostgreSQL snapshot, and preserves the original schema
for rollback. This source candidate remains unpublished and is not ready for
operator cutover; representative populated data, independent backup restore,
installed rollback, and channel handoff are still required. See the
[PostgreSQL candidate notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md). For SQLite,
the current source also bundles the BookshelfNG migration chain; `.977` still
requires migration 158, and databases earlier than migration 14 require that
unreleased source fix. Do not switch applications while the retirement freeze
is active.

The current PostgreSQL candidate also recognizes BookshelfNG's implicit
`readarr-main`, `readarr-log`, and `readarr-cache` names when ChaptarrNG's
database names were never explicitly configured. It resumes on those names
after conversion using the migration marker in the main database. Explicit
database-name settings take precedence, and ambiguous populated databases stop
startup for review. See the candidate notes for the exact selection rules;
this behavior is not included in published `v0.9.977`.

Environment variables are outside the database conversion. If BookshelfNG
used `BOOKSHELF_ALLOW_API_KEY_QUERY=false`, ChaptarrNG honors it while
`CHAPTARR__AUTH__ALLOWAPIKEYQUERY` is unset. Prefer the ChaptarrNG setting for
new deployments; query-key authentication remains enabled by default. The
legacy `BOOKSHELF_SEERRNG_API_KEY` also remains active when
`CHAPTARR__AUTH__SEERRAPIKEY` is unset, so existing SeerrNG credentials keep
working when the container environment is carried forward. Remove the legacy
value to revoke it or set the ChaptarrNG key explicitly to rotate it. Preserve
the BookshelfNG diagnostics variables if that optional module was explicitly
enabled; ChaptarrNG honors that choice only when the archived appdata also
contains the old diagnostics module marker. See the
[HTTP security settings](HTTP_SECURITY.md),
[SeerrNG integration guide](SEERRNG_INTEGRATION.md), and
[optional diagnostics guide](OPTIONAL_DIAGNOSTICS.md).

The host also carries forward the old `Readarr__...` and `Audioarr__...`
configuration sections for app, authentication, server, logging, update, and
PostgreSQL settings. A value already set under `Chaptarr__...` takes precedence
field by field; unset Chaptarr values fall back to `Readarr__...`, then
`Audioarr__...`. This lets deployments keep their existing environment while
they switch images or packages. The legacy Docker aliases recognize an old
custom `Readarr__Server__Port` or `Audioarr__Server__Port` before adding the
BookshelfNG-compatible default of `8787`. The container health check uses the
same legacy port and URL-base values, so it probes the application listener
after the image switch.

The optional Playwright browser fallback used by the Direct Download indexer
also honors `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`, so deployments with a
system-installed Chromium can keep their existing executable override.

When ChaptarrNG has no saved metadata-server URL, it also honors the legacy
`METADATA_URL` environment variable for its Readarr-compatible Metadata API
catalog. During database conversion, a saved BookshelfNG `MetadataSource` URL
is copied to ChaptarrNG's `MetadataServerUrl`; an existing saved
`MetadataServerUrl` takes precedence. The original `MetadataSource` row remains
in the converted database and the source backup. This setting does not change
the primary metadata provider or make native Hardcover fall back automatically
when a request fails.

The current ChaptarrNG main candidate also reads BookshelfNG's
`BOOKSHELF_OIDC_*` environment variables when the corresponding ChaptarrNG OIDC
setting is empty. This keeps
the issuer, client ID and secret, scopes, email allow-lists, allow-any-verified
choice, and private-issuer behavior when the deployment carries its existing
environment forward. A saved non-empty ChaptarrNG value takes precedence. The
legacy callback override is accepted only as an HTTPS URL ending in
`/signin-oidc`; otherwise ChaptarrNG uses its normal callback URL. Keep these
variables in the deployment definition because the appdata archive does not
include them. OIDC backchannel requests reject issuer hosts that resolve to
private or reserved addresses by default. Set
`BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true` for an internal issuer; ChaptarrNG
keeps configured proxy and certificate handling, disables redirects, and caps
backchannel response bodies at 1 MiB. The paged library API defaults to 50
records, sorts by the stored book title, and includes each book's author and
links when `include` is omitted, matching BookshelfNG's default contract. These
current-main changes are not in public `.977`, and OIDC denies sign-in unless
an allow-list or the explicit
allow-any-verified setting is configured.

### Provider configuration blockers

Provider rows are copied only when ChaptarrNG has the same implementation and
settings contract. The retirement candidate includes BookshelfNG's Hardcover
public-list and reading-status provider, so its API token, list owner, selected
lists, and settings can remain active. BookLore notification settings and
triggers also remain connected to their saved BookLore URL and account;
migration does not treat BookLore as BookOrbit. The candidate adds BookOrbit as
a separate Book Dock destination. Configure and test that separate connection
in **Settings > Connect** after an approved migration.

The MyAnonaMouse `useFreeleechWedge=2` (`Required`) option is preserved during
conversion and remains selectable in ChaptarrNG. Eligible releases request the
personal freeleech wedge; if MAM does not return a usable torrent for that
request, the grab fails as unavailable instead of retrying without the wedge.
This matches BookshelfNG's setting and download behavior. Live account
acceptance remains part of the provider migration rehearsal.

Provider configuration is preserved only when the implementation, contract,
and every populated saved settings field match the target provider. The
unreleased PR #131 candidate now includes compatibility ports for
BookshelfNG's OAuth-backed Goodreads bookshelf and owned-book lists and its
bookshelf/owned-book write notifications. Complete saved OAuth settings are
preserved; the preview blocks missing or partial credentials for OAuth-only
providers. A public Goodreads shelf can keep working without OAuth when it has
a valid numeric user ID. Migration reports identify the row and missing field
names without printing settings or credentials.

The candidate ports BookshelfNG's `AudiobookshelfMetadata` OPF consumer as
ChaptarrNG's distinct `LegacyAudiobookshelfMetadata` provider. The importer
maps the old provider implementation and both legacy sidecar IDs to that name,
preserving the `NullConfig` contract, metadata-file IDs, and remapped book/file
links. Unknown metadata providers or sidecar consumers appear in the preview
and block apply. ChaptarrNG's `AudioBookShelfMetadata` JSON and cover writer
remains a separate provider.

This source code does not restore Goodreads developer-key issuance or prove
that an existing token remains accepted. Goodreads stopped issuing new
developer keys and announced API retirement, so existing credentials and live
API behavior must be tested with representative accounts before cutover. The
compatibility port is not in published `v0.9.977`; do not use it for operator
data while the release freeze is active. Keep the original appdata backup and
see Goodreads' [API deprecation notice](https://www.goodreads.com/topic/show/21788520-api-deprecation).

Unknown or plugin download clients, import lists, notifications, indexers,
metadata providers, and sidecar consumers are handled the same way: preview
identifies the unsupported mapping and apply stops before promotion. A source
backup preserves the original configuration, but it does not make an
unsupported provider active in ChaptarrNG. These candidate changes are not in
published `v0.9.977` while the release freeze remains active.

ChaptarrNG does not dynamically load arbitrary BookshelfNG indexer or download-
client assemblies. Built-in provider implementations have explicit settings
and behavior mappings; an unrecognized implementation cannot safely be treated
as runnable just because its saved JSON can be copied. Such a row remains a
preview blocker until a Chaptarr-compatible implementation is available.

The built-in BookshelfNG `ReadarrImport` list is mapped to ChaptarrNG's
`ChaptarrImport` implementation. Its saved `ReadarrSettings` fields
(`BaseUrl`, `ApiKey`, `ProfileIds`, `TagIds`, and `RootFolderPaths`) match the
target settings contract; conversion updates the provider and contract names
while retaining those values. This compatibility is source-only and is not in
published `v0.9.977`.

Preserve `HARDCOVER`, `HARDCOVER_NATIVE`, `HARDCOVER_AUTH`,
`HARDCOVER_API_KEY`, and any custom `HARDCOVER_API_URL` used by BookshelfNG.
Run preview and migration with the same provider mode as the source install.
When native Hardcover mode is active, BookshelfNG stores many primary provider
IDs as bare numbers; the converter uses the explicit mode to preserve those as
Hardcover identities. Set `HARDCOVER=false` for a Goodreads-primary source, or
set `HARDCOVER_NATIVE=false` to explicitly select Goodreads. Although
BookshelfNG's application runtime defaults an unset `HARDCOVER` value to
Goodreads, the migration importer does not make that guess: it requires an
explicit mode for each migration and reports the selected interpretation for
authors, books, editions, and series. Changing the mode between preview and
apply can associate every bare numeric ID with the wrong provider.

PR #131 candidate commit `12f290bd` now also writes ChaptarrNG's persisted
`HardcoverEnabled=true` setting when migration runs with native Hardcover mode
and the source has no explicit saved mode. This keeps the target in the same
provider mode after startup conversion; an existing saved target setting is
preserved, and `HARDCOVER=false` does not enable Hardcover. The importer suite
and a disposable schema-158 startup-conversion smoke cover both cases. This is
unreleased candidate behavior and does not authorize applying the migration to
operator data while the freeze remains active.

The current ChaptarrNG work-branch candidate also maps a saved `HardcoverAuth`
database value to `HardcoverApiToken`; a saved ChaptarrNG token takes
precedence, and the original BookshelfNG database remains available in the
rollback backup. That native provider and token mapping are not in published
`v0.9.977`. The earlier integrated code snapshot `8b4c6e23` passed cross-platform
and PostgreSQL migration checks in exact-head run
[39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341).
Representative converted-data acceptance remains open; see the [PostgreSQL
candidate notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md). Do not treat this as
supported migration behavior during the release freeze.

BookshelfNG's `BOOKSHELF_M4B_MERGE`,
`BOOKSHELF_M4B_AAC_BITRATE_KBPS`, and `BOOKSHELF_FFMPEG_PATH` variables are
also outside database conversion. The published ChaptarrNG `v0.9.977` does not
honor them. ChaptarrNG PR #129 merged the compatibility implementation to
`main` as `949ae3dd`, but it is not in `.977`; keep `BOOKSHELF_M4B_MERGE` unset
until a build with that support is available and the remaining acceptance
gates close. The current retirement candidate's
`real_m4b_tool_should_merge_and_import_legacy_multipart_audiobook_when_installed`
test passed 6/6 on candidate `12f290bd` in an isolated Linux tool image with
`m4b-tool` 0.5.2, `mp4v2` 2.1.1, and Debian Bookworm FFmpeg. It generates two
MP3 tracks and imports them through the real converter, verifying tag-based
track order, chapter titles and offsets, output metadata, library registration,
Move-mode source cleanup, and Copy-mode source retention. Failure cases verify
fallback to separate original-track imports in both modes when FFprobe cannot
read a source and when a controlled FFmpeg error makes the real `m4b-tool`
process fail. The Linux build workflow runs these six cases in the checked-in
`integration/Dockerfile.m4b-test` image. The current application snapshot also
passed eight pinned-tool cases covering embedded-chapter Move/Copy and
TagLib-write/reprobe. `ConversionJobServiceFixture` simulates startup after an
interrupted conversion: it restores a persisted converting job with its
synthetic output and manifest, marks the artifact ready to import, and queues
the import sweep. That fixture passes in the full Core suite. Representative
M4B files and recovery of a real in-progress conversion after an installed
service restart remain unverified.
The implementation requires the FFmpeg executable to be named `ffmpeg`
(`ffmpeg.exe` on Windows) for `m4b-tool` to find it through `PATH`.

The conversion reuses the existing data directory and `config.xml`, preserving
the saved HTTP port, URL base, API key, and server settings. Keep the same media
and download mounts and host/container port mappings. This preserves existing
SeerrNG and reverse-proxy endpoints when those services used the old address.

## Recovering unmapped files

The published `v0.9.977` release retries unmapped files against the local
library and Chaptarr's V5 matcher. ChaptarrNG PR #127 merged configured-catalog
recovery to `main` as `b3089a39`; that source change is not in `.977`. It
searches after those paths fail and
searches from an exact ISBN, ASIN, or provider-ID tag (including Goodreads IDs),
or from one unambiguous book title and author tag. Every supplied identity field
must match the result, including normalized title and author when present. The
result must use the same media type and have stable provider work and author
IDs. Conflicting or ambiguous results stay unmapped. A single result is sent
through the existing pending-author-import flow; catalog search never moves or
attaches a file directly. This source change does not change release or
channel acceptance status. The ChaptarrNG shell helper can also queue an
all-format retry using RetryUnmappedMatch and unmappedFiles.scope=all; see the
[unmapped-files guide](UNMAPPED_FILES.md). It is not in public `.977`.

## Matching setting compatibility

The published ChaptarrNG `v0.9.977` converter preserves the legacy
`BookImportMinimumMatchPercent` row but does not apply it to candidate
acceptance. Current source keeps the value active for BookshelfNG's fuzzy
RSS-title fallback and enables a migrated-install compatibility gate for
candidate scoring and acceptance. The gate uses the saved threshold, or
BookshelfNG's 70% default when that setting was never saved. Fresh ChaptarrNG
installs leave the compatibility gate disabled. When the source has no
explicit `BookMatchingStrictness`, migration also maps a saved legacy threshold
to the closest policy band:

| BookshelfNG percentage | ChaptarrNG mode |
| ---------------------- | --------------- |
| 50–66                  | Aggressive      |
| 67–82                  | Balanced        |
| 83–99                  | Strict          |

An explicit ChaptarrNG mode is preserved and remains an additional matching
safety policy; it does not disable the migrated numeric score gate. The
compatibility scorer ports BookshelfNG's weighted author, title, identifier,
year, language, publisher, narrator, format, and preferred-edition signals.
ChaptarrNG's newer identity-proof and ambiguity checks still apply, so the
combined decision pipeline is not identical in every case. Review matching on
converted data before cutover. The migration report states both dispositions;
values outside 50–99 stop conversion before promotion. This source change is
not in `.977` while the release freeze remains active.

For migrated installs, the same compatibility marker restores BookshelfNG's
audiobook release order when comparing two audiobook candidates: quality and
revision, custom-format score, preferred protocol, indexer priority, torrent
peers, book count, Usenet age, and size rounded to 200 MB. Fresh installs and
mixed ebook/audiobook comparisons keep ChaptarrNG's current ranking. This
change is also absent from the published `.977` release.

## New author format paths

The current migration candidate preserves BookshelfNG's
`AutoInitializeFormatSpecificAuthorPaths` setting; it remains opt-in and is
false when the source did not save it. When enabled, saved
`NewAuthorEbookRootFolderPath` and `NewAuthorAudiobookRootFolderPath` values map
to ChaptarrNG's `DefaultEbookRootFolderPath` and
`DefaultAudiobookRootFolderPath`. Existing non-empty ChaptarrNG defaults take
precedence, and the original BookshelfNG config rows remain available for
review. When BookshelfNG had the feature disabled, its new-author root overrides
are not applied. The setting only affects newly added authors; it does not
backfill existing paths or move files. This source change remains under the
release freeze and still needs hosted validation and migration acceptance.

## Extra files after rename

The unpublished migration candidate preserves BookshelfNG's
`MoveExtraFilesOnRename` setting in the converted database, exposes it in
Media Management, and includes it in settings backups. It remains off by
default. When enabled, ChaptarrNG moves only extensions listed in
`ExtraFileExtensions` from a renamed file's old folder after all renamed files
from that folder agree on one destination. It leaves the files in place if the
source still contains a recognized book file or a subfolder, or if the
destination already contains a file with the same name. This behavior is not
in the published `.977` release and has not passed operator-data or installed
rollback acceptance; keep the release freeze in place.

## Read-only preview and gated apply path

Stop BookshelfNG first and make an independent copy of the entire data
directory. Run the preview with the **same regular ChaptarrNG release** that
will run the migrated service. Preview performs the full conversion into a
temporary staged database, prints the row disposition for every source table,
and leaves the source database untouched.

The commands below are for an approved migration-capable release. The published
`.977` image does not include the current provider-identity migration; keep the
release freeze above in effect until an approved image is published. Set
`CHAPTARRNG_IMAGE` to that exact versioned image or immutable digest.

```sh
CHAPTARRNG_IMAGE="ghcr.io/snapetech-org/chaptarrng:<approved-version-or-digest>"
docker run --rm \
  -e HARDCOVER=true \
  -e HARDCOVER_NATIVE=true \
  -v /host/path/to/bookshelfng-config:/config \
  "$CHAPTARRNG_IMAGE" \
  dotnet /app/Chaptarr.dll -preview-bookshelf-migration -data=/config
```

If both BookshelfNG SQLite databases contain data, add
`-merge-bookshelf-databases` before `-data=/config` in both the preview and
apply commands below. The native app-data supervisors select this merge
automatically; this flag selects it for an explicit utility run.

These values are shown for a native Hardcover source. Use `HARDCOVER=false`
for a Goodreads-primary source, and preserve the source's `HARDCOVER_NATIVE`
value if it was explicitly set. Preview, apply, and automatic startup must use
the same provider-mode values.

Some source records have no edition rows, so their media type cannot be
determined from the database. `IsEbook=false` also covers both audio and print
editions. ChaptarrNG uses edition format and narrator evidence plus recognized
linked-file extensions to distinguish them. When neither source metadata nor a
recognized file identifies an edition, preview and startup preserve it in both
target catalogs by default, with the legacy provider identity kept on the
audiobook row. The private review file records that ambiguity, and a media map
can narrow the edition to one format or preserve both without monitoring.
Source-monitored works with unresolved format evidence remain monitored in
both target formats; source-unmonitored works remain unmonitored in both. A
temporary duplicate title can appear across the ebook and audiobook catalogs.
Physical editions stay in the ebook catalog with their physical reading-format
ID; linked files and file-specific history follow the target row matching the
file extension when it is recognized.

A monitored mixed-format book can also have no selected edition. For that
case, the same fallback preserves both target formats and their source
editions and files, while a media map can choose which format is monitored.

The current ChaptarrNG migration candidate maps legacy
`MetadataFiles.Consumer=Readarr` rows to `LegacyAudiobookshelfMetadata`. This
keeps those sidecar records attached to ChaptarrNG's retained legacy metadata
consumer; unknown consumer values still block apply. The public `.977` release
does not include this mapping.

The converter writes
`readarr.db.bookshelf-media-review.json` beside the database and keeps it after
automatic conversion. It lists the source book IDs, titles, authors, original
monitoring state, reason, and the chosen fallback. On Unix it is created with
owner-only permissions because it contains library metadata. When a source
book is monitored but no edition identifies its format, the fallback creates
both ChaptarrNG media records and preserves monitoring for both. An
unmonitored source book remains unmonitored in both records. Use the review
file and optional media map to narrow an ambiguous edition or book to one
format. The normal startup log gives the review file path.

When one audiobook edition and one ebook edition are selected for the same
BookshelfNG work, conversion preserves each selection on its matching
ChaptarrNG record. If two or more editions of the same format are selected,
conversion stops before promotion because ChaptarrNG has one selected-edition
field per format record. Correct that selection in BookshelfNG and preview
again; the source database remains available for another attempt.

To choose formats during conversion, set each required `mediaType` to
`audiobook`, `ebook`, `both`, or `both-unmonitored`. Save the completed
document as `readarr.db.bookshelf-media-map.json` beside `readarr.db`. For a
book without editions, the map creates the corresponding ChaptarrNG media
record or records.
For an ambiguous edition, it selects which media-specific target edition rows
are created.
For a monitored mixed-format book without a selected edition, the map controls
which existing formats are monitored while preserving all source editions and
files. `both-unmonitored` retains both formats without starting searches. Run
preview again with the map in place. Normal startup uses the same map and
converter, so no separate migration build is needed.

The map is tied to source book IDs and the current ambiguous-record set. If the
database changes after review, preview rejects missing entries for unresolved
books and entries for IDs absent from the source. A choice for an existing
book that no longer needs a decision is ignored and the sidecar is retained;
remove stale entries as housekeeping or use the updated review file.

Once the retirement gates close and apply is approved, either let normal
startup apply conversion or run the explicit apply command once:

> **Do not run this apply command or normal startup against BookshelfNG data
> during the current release freeze.** The migration and rollback commands are
> implemented, but operator-data, parity, and channel acceptance remain open.

```sh
CHAPTARRNG_IMAGE="ghcr.io/snapetech-org/chaptarrng:<approved-version-or-digest>"
docker run --rm \
  -e HARDCOVER=true \
  -e HARDCOVER_NATIVE=true \
  -v /host/path/to/bookshelfng-config:/config \
  "$CHAPTARRNG_IMAGE" \
  dotnet /app/Chaptarr.dll -migrate-bookshelf -confirm-bookshelf-service-stopped -data=/config
```

Apply retains a backup beside the original database. Keep the independent
directory copy and generated database backup until you have checked the
library, root paths, profiles, indexers, download clients, notifications, and
SeerrNG connection. The explicit stopped-service flag makes the required
quiescent data directory an operator-confirmed precondition.

For SQLite sources, stale `SeriesBookLink` rows whose BookshelfNG `Books` or
`Series` endpoint was deleted are listed as reset in the migration accounting
and omitted from ChaptarrNG. The untouched BookshelfNG backup keeps those rows
for rollback. Other source foreign-key violations still block conversion, and
the promoted ChaptarrNG database must pass a clean foreign-key check.

History for deleted BookshelfNG books or authors remains in the converted
history tables without a link to a future ChaptarrNG record that might reuse
the old numeric ID. The native history schema requires `BookId`, so an event
for an unmapped book uses `BookId=0`; an unmapped nullable author link is
cleared. The preview report counts these unlinked history records; the retained
source backup keeps their original IDs and relationships.
Download-history events without a mapped author remain present with the
target-required `AuthorId=0` sentinel, and the preview reports their count.

### ChaptarrNG-owned Docker handoff helper

Run the host helper from a ChaptarrNG source checkout. It is a wrapper around
the same application migration and restore commands documented above; it does
not perform a second conversion implementation. Use a separate working copy
for preview while the retirement gate remains open. After operator approval,
the migration sequence is:

```sh
scripts/bookshelfng-to-chaptarrng.sh snapshot \
  --data-dir /host/path/to/bookshelfng-config \
  --backup-dir /host/path/to/external-backups

scripts/bookshelfng-to-chaptarrng.sh preview \
  --data-dir /host/path/to/working-copy \
  --backup-dir /host/path/to/external-backups \
  --image "$CHAPTARRNG_IMAGE" \
  --provider-mode native-hardcover
```

Set `CHAPTARRNG_IMAGE` to the exact versioned image or immutable digest that
will run the replacement. Use `--provider-mode goodreads` for a
Goodreads-primary source. Preview runs in a temporary container and does not
apply the conversion. Review its report before proceeding.

After stopping every process using the data directory, and only after the
retirement gates close, apply with the same image and provider mode plus
`--confirm-stopped`. The helper saves the complete appdata archive outside the
source directory before running ChaptarrNG. For rollback, use the filename of
the retained `readarr.db.bookshelfng-backup-*` database directly inside
`--data-dir`; `rollback-preview` remains separate from `rollback`, and actual
restore also requires `--confirm-stopped`. Keep the independent full-directory
archive until the restored BookshelfNG instance has been checked.

### Chocolatey identity bridge

The candidate `bookshelfng` Chocolatey package keeps the existing BookshelfNG
service name and data directory. New installs use
`%ProgramData%\BookshelfNG\data`; upgrades read and preserve an existing
`-data` service argument, including a custom path, along with other service
arguments and startup mode. Before replacing the application files, it stops
the service and runs the non-mutating ChaptarrNG migration preview. A
successful upgrade must also pass `/ping` on the saved local port. SQLite
source backups and independent PostgreSQL `pg_dump` backups remain beside the
source data. If the new service fails its health check, the package rolls the
application back and restores the database from its retained backup. If the
database, application files, or service configuration cannot be restored
safely, it leaves the service stopped and retains the backup for manual
recovery. PostgreSQL requires `pg_dump`, `pg_restore`, an explicit
`PostgresUser`, and no other database clients during the handoff.

BookshelfNG can store bare numeric IDs without recording whether they came
from Goodreads or native Hardcover. Existing `HARDCOVER` and
`HARDCOVER_NATIVE` service or machine settings are preserved. If the source
does not record a mode, the package follows BookshelfNG's Goodreads default.
Specify a mode only when you want to override that source behavior:

```powershell
choco upgrade bookshelfng --yes --params "'/ProviderMode=Goodreads'"
```

Use `/ProviderMode=Hardcover` for native Hardcover IDs. An explicit choice is
persisted in the BookshelfNG service environment. The source and package
templates are present, but feed publication and representative installed-data
acceptance remain open.

### Windows standalone service migration

The existing BookshelfNG WPF installer still installs and upgrades BookshelfNG.
ChaptarrNG has a WPF migration installer at
`distribution/windows/installer-net`. The release workflow builds a
versioned installer from the exact Windows release payload and attaches it
only when the migration-bridge and both release-readiness gates are enabled;
no such asset is currently published. The installer supports one PostgreSQL
source or both BookshelfNG SQLite databases and preserves the existing service identity
for built-in accounts, local or domain users, and group Managed Service
Accounts. It securely prompts for a password when Windows requires one;
Readarr-specific virtual service accounts are rejected because Windows binds
them to the Readarr service name. It requires a provider identity choice, makes
and verifies a full rollback backup, previews before apply, and checks the
migrated database and
ChaptarrNG's local health. A read-only SQLite `PRAGMA data_version` guard
remains active for every populated SQLite source through preview review and
blocks committed changes from another connection immediately before apply. WAL
representation changes are diagnostic and do not count as committed database
changes.

The service handoff keeps the existing `Readarr` registration until ChaptarrNG
has passed its health check. After the verified backup, the installer records
Readarr's original startup mode and whether it was running, then disables it
so it cannot start beside ChaptarrNG. A helper waits for the installer to
close, confirms both services target the same data directory and ChaptarrNG is
running, then removes the disabled Readarr registration. If cleanup cannot be
scheduled, Readarr stays disabled and the installer reports that state.

Automatic rollback stops ChaptarrNG, restores the verified files and database,
and reuses the existing Readarr registration when it is still present. It
restores Readarr's startup mode and starts it only if it was running before the
migration. If post-exit cleanup already removed the service, rollback recreates
it with the saved startup mode and service identity, prompting for the account
password when needed. The password is not retained in the migration record. The
installer's **Restore BookshelfNG** action remains available while the retained
backup exists.

When an installed BookshelfNG service is stopped during upgrade, Chocolatey
now retains the prior app and registers a SYSTEM scheduled task for the first
service start. It waits for the configured local `/ping` URL and the migrated
database/provider check; on timeout or service exit it restores the source
database, prior app, original NSSM settings, both environment blocks, and
startup mode. The service account is preserved by the installer. The deferred
task and PostgreSQL restore still need installed-Windows acceptance.

This is still source-only migration tooling. The published `v0.9.977` Windows
binary does not preserve `-data` when registering its service, and the manual
service-registration commands from earlier drafts are not a supported
handoff. The SQLite Windows installed-service route has since passed the
exact-head service round-trip recorded below. The PostgreSQL rollback code
requires `pg_dump` and `pg_restore`, an explicit database user, and no
service-only PostgreSQL environment overrides; its Windows end-to-end
installed rollback remains unvalidated. Unsupported schemas and representative
operator-data rollback are not covered by this installer. Do not change the
service or apply the migration during the release freeze. Keep BookshelfNG
available until the installer and all retirement acceptance gates have
passed.

## Update the local MCP connection

If `snapetech-mcp` is configured for BookshelfNG, change the MCP host to use
the ChaptarrNG app entry and URL after migration:

```text
SNAPETECH_MCP_APPS=chaptarrng
SNAPETECH_MCP_CHAPTARRNG_URL=<ChaptarrNG base URL>
SNAPETECH_MCP_CHAPTARRNG_API_KEY=<dedicated ChaptarrNG service key>
```

Configure that credential in ChaptarrNG as
`CHAPTARR__AUTH__SEERRAPIKEY`, then restart the MCP process. The BookshelfNG
entry uses a media-type selector; ChaptarrNG serves both media types through
one entry, so remove `SNAPETECH_MCP_BOOKSHELFNG_MEDIA_TYPE`. The current MCP
tools read status, paged books, bounded catalog search, queue, and history;
catalog search may contact the configured metadata providers. See the
[SnapeTech MCP guide](integrations/snapetech-mcp.md) for access and data
boundaries.

## Roll back after ChaptarrNG has started

Stop every process using the data directory. First validate the retained
BookshelfNG backup without changing either database:

```sh
docker run --rm \
  -v /host/path/to/bookshelfng-config:/config \
  ghcr.io/snapetech-org/chaptarrng:0.9.977 \
  dotnet /app/Chaptarr.dll -preview-bookshelf-restore \
    -bookshelf-backup=/config/readarr.db.bookshelfng-backup-YYYYMMDDTHHMMSSfffZ-ID.db \
    -data=/config
```

After preview passes, apply the restore while all services remain stopped:

```sh
docker run --rm \
  -v /host/path/to/bookshelfng-config:/config \
  ghcr.io/snapetech-org/chaptarrng:0.9.977 \
  dotnet /app/Chaptarr.dll -restore-bookshelf \
    -confirm-bookshelf-service-stopped \
    -bookshelf-backup=/config/readarr.db.bookshelfng-backup-YYYYMMDDTHHMMSSfffZ-ID.db \
    -data=/config
```

Rollback validates the current ChaptarrNG database and exact supported
BookshelfNG migration-158 backup before changing files. It preserves the
current ChaptarrNG database, including writes made since conversion, as
`readarr.db.chaptarrng-backup-<timestamp>-<id>.db`; the original
`readarr.db.bookshelfng-backup-*` remains untouched. Start only BookshelfNG
after restore. If promotion validation fails, the tool restores the prior
ChaptarrNG database and preserves the failed restore candidate for inspection.

## Unraid

> **Not approved for cutover yet.** [BookshelfNG Unraid PR #5](https://github.com/snapetech-org/bookshelfng-unraid/pull/5) contains a draft
> template handoff; its Community Applications upgrade and rollback flow has
> not passed acceptance. Do not change the template or start ChaptarrNG against
> the BookshelfNG appdata until the retirement gates close.

Stop BookshelfNG and back up the full appdata directory. In **Apps > Previous
Apps**, apply the updated BookshelfNG template. The current source candidate
points to the standard ChaptarrNG release image; the released template still
points to BookshelfNG until the candidate is accepted. It
retains the old `/config`, `/data`, `/download`, port,
PUID/PGID, timezone, and Hardcover settings. Starting the container runs the
regular ChaptarrNG app and its automatic migration. The old `config.xml` keeps
the saved app port and URL base.

The template also carries the legacy OIDC, SeerrNG API key, API-key query
policy, diagnostics, metadata-source, Google Books, Europeana, Open Library,
Hardcover/Apify, audiobook-merge, metadata-server, path-limit, and Chromium
environment settings into the new container.
Keep their existing values if you configured them outside the BookshelfNG
settings database. It also carries the old Bookshelf, Audioarr, and Readarr
application, authentication, server/security, logging, updater, and PostgreSQL
settings, including TLS and credentials; secret fields remain masked in the
Unraid template. The Docker Compose candidates pass through the same settings
from the environment file. Leave compatibility fields blank when `config.xml`
already owns the setting.

The template exposes `BOOKSHELFNG_MIGRATION_MODE`. Leave it as `automatic` for
first-start conversion. Set it to `manual` to keep the old database untouched
and the new container unavailable until a Console preview/apply is completed;
set it back to `automatic` and restart the container to finish startup. `ask`
defers in Unraid because container startup has no interactive terminal.

The old Unraid template defaults to the native Hardcover image and sets both
`HARDCOVER=true` and `HARDCOVER_NATIVE=true`; the candidate preserves those
defaults. Keep both existing values during the update, including
`HARDCOVER_NATIVE=false` when the prior service used Goodreads-compatible IDs.
Preview and automatic startup must use the same provider-mode values. The template XML and
settings-preservation script passed locally, but the actual Community
Applications update and rollback flow has not been run.

Wait for the container health check, open the existing URL, and inspect
**System > Logs**. The backup filename starts with
`readarr.db.bookshelfng-backup-`. Check the SeerrNG service connection and run
its diagnostic if the migration changed any network address or API key.

If startup fails, keep both the original copy and the retained source backup.
Stop ChaptarrNG, restore the independent appdata copy, then reinstall the old
BookshelfNG image. Do not start BookshelfNG against a ChaptarrNG-converted
database.

## Docker Compose

> **Not approved for cutover yet.** Do not change the image or start ChaptarrNG
> against BookshelfNG appdata until migration and rollback acceptance close.

Change the image to ChaptarrNG and retain the old service/container names, host
paths, mount targets, and port mapping. The legacy catalog Compose recipe used
the Hardcover image, so its candidate defaults to `HARDCOVER=true` and
`HARDCOVER_NATIVE=true`. Set `HARDCOVER=false` only when migrating a prior
softcover image. Start with `packaging/legacy/catalogs/.env.sample`, then copy
the prior Compose environment values into `.env` before starting the candidate.
Preserve any configured `HARDCOVER_AUTH`, `HARDCOVER_API_KEY`,
`HARDCOVER_API_URL`, `GOOGLE_BOOKS_API_KEY`, `EUROPEANA_API_KEY`,
`OPEN_LIBRARY_CONTACT_EMAIL`, `MAX_PATH`, `MAX_NAME`, `BOOKSHELF_M4B_MERGE`,
`BOOKSHELF_M4B_AAC_BITRATE_KBPS`, and `BOOKSHELF_FFMPEG_PATH` values in the
Compose environment. Preserve custom `BOOKSHELF__`, `AUDIOARR__`, or
`READARR__` application, authentication, server/security, logging, updater,
and PostgreSQL variables too, including TLS certificate settings and database
credentials.
The compatibility aliases map `latest`, `main`, `master`, and
`hardcover` to the native Hardcover target, and `softcover` to the Goodreads
target; release automation verifies each alias against its mode-specific image
digest. Those images use port `8787` with a fresh data directory. They have not
been published or enabled while the release freeze is active; a saved
`config.xml` port or explicit `Chaptarr__Server__Port` setting takes precedence.
The ordinary ChaptarrNG image still defaults to `8789` for fresh installs. For
an installation that used the default BookshelfNG port, the shape is:

```yaml
services:
  bookshelfng:
    image: ghcr.io/snapetech-org/chaptarrng:latest
    container_name: bookshelfng
    environment:
      PUID: "99"
      PGID: "100"
      TZ: Etc/UTC
      CHAPTARR_BOOKSHELF_COMPATIBILITY: "true"
      CHAPTARR_DEFAULT_PORT: "8787"
      CHAPTARR_EXPECTED_PORT: "8787"
      BOOKSHELFNG_MIGRATION_MODE: automatic
      HARDCOVER: "true"
      HARDCOVER_NATIVE: "true"
    volumes:
      - /host/path/to/bookshelfng-config:/config
      - /host/path/to/media:/data
      - /host/path/to/downloads:/download
    ports:
      - "8787:8787"
    restart: unless-stopped
```

The saved BookshelfNG `config.xml` keeps the internal port at `8787`. If the old
instance used a different port, preserve that port in both its container
configuration and host mapping.

## Unpublished candidate populated SQLite round trip (2026-10-19)

The unpublished ChaptarrNG PR #131 candidate (build version `0.9.978.0`)
completed a full preview/apply/restore rehearsal on a task-scoped copy of a
private populated BookshelfNG migration-158 SQLite database. The live
BookshelfNG service remained running, the source snapshot stayed byte-identical,
and every candidate command used the separate copy with `HARDCOVER=true`. This
validates the migration utility path, not an installed service handoff.

- Migration preview passed without changing the source database.
- The explicit apply mode and its `-confirm-bookshelf-service-stopped` option
  were used only against the isolated copy. The live service stayed running,
  so this did not validate the actual stop-before-apply handoff. The converted
  database reached ChaptarrNG schema 121 and passed SQLite integrity and
  foreign-key checks; the original schema-158 backup remained retained.
- Rollback preview passed without changing either the converted database or
  the source backup.
- Explicit rollback restored a byte-identical copy of the retained schema-158
  source backup and preserved the converted schema-121 database as a separate
  ChaptarrNG backup.
- A fresh copy of the restored database opened in the BookshelfNG `hardcover`
  image on Docker's isolated `none` network; the configured `/ping` endpoint
  returned HTTP 200. This verifies startup and database reopen only, not the
  complete library workflow or an installed image switch.
- The `.978` Linux app artifact from Build
  [39267876585](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876585)
  also returned HTTP 200 from `/ping` against a fresh converted schema-121 copy,
  running on the published `.977` runtime image with empty scratch mounts for
  configured roots and an internal-only network. This checks candidate app
  startup, not candidate image packaging or a live image switch.

The private library contents, counts, identifiers, and hashes are omitted.
This validates one populated SQLite migration and restore path. It does not
classify ambiguous formats, prove every data mapping or client behavior, or
close older-schema, PostgreSQL, full feature-parity, or installation-channel
acceptance. Configured provider rows in this snapshot passed compatibility
checks without blockers, but no live provider calls were tested. With native
Hardcover mode enabled, every migrated author, book, and edition retained its
Hardcover identity; none were assigned Goodreads IDs. Keep the release freeze
and production cutover restrictions in place; the candidate is not a published
migration tool.

## Exact-head artifact migration and SeerrNG smoke (2026-10-20)

The Linux artifact from ChaptarrNG Build
[39394240337](https://github.com/snapetech-org/chaptarrng/actions/runs/39394240337)
was run from the current application snapshot
`29852b1334ee049e30de84b44806932a3e9364f4`. A task-scoped config and library
were seeded from the checked-in BookshelfNG schema-158 fixture. Normal startup
converted the database to ChaptarrNG schema 121 and retained a schema-158
BookshelfNG backup. Both databases passed `PRAGMA integrity_check`; the app
reported `0.9.978.0`.

SeerrNG at source commit
[`ce632a6e6834fe094eb24ceda9b8a388472cc23e`](https://github.com/snapetech-org/seerrng/commit/ce632a6e6834fe094eb24ceda9b8a388472cc23e)
read the migrated audiobook and ebook through its real `ReadarrAPI` adapter
and verified their Hardcover identities. The restricted key read capabilities
and both format libraries, was denied administrative and unrelated command
routes, and the global key retained administrative access. ChaptarrNG's
`integration/seerrng-request-lifecycle.mjs` then created, polled, and cancelled
a synthetic pending import, which remained queryable in its terminal state.

This uses only synthetic records. The `.m4b` and `.epub` files are placeholders,
so it does not test media decoding, file access, provider fulfillment, operator
data, PostgreSQL operator data, or installed rollback. The Build's Windows
installer test is tracked separately in the exact-head validation section.
Build [39394240337](https://github.com/snapetech-org/chaptarrng/actions/runs/39394240337)
passed Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. The Windows job passed the installer fingerprint, service
upgrade/rollback, cleanup, and selected-data-directory checks. No release or
compatibility bridge was published.

## Current candidate SeerrNG source-build smoke (2026-10-20)

ChaptarrNG source snapshot `ef0a144da7a54f98cd5e13347a0ed06a89c15cfc` built
through the checked-in `Dockerfile.build` with its pinned .NET 10.0.13 runtime.
The local SeerrNG source at
[`ce632a6e6834fe094eb24ceda9b8a388472cc23e`](https://github.com/snapetech-org/seerrng/commit/ce632a6e6834fe094eb24ceda9b8a388472cc23e)
read the synthetic audiobook and ebook converted from the BookshelfNG
migration-158 fixture. ChaptarrNG reached schema 121, retained its schema-158
backup, and both databases passed SQLite integrity and foreign-key checks.
SeerrNG's restricted key read capabilities and both formats while
administrative and unrelated command routes were denied. The real `ReadarrAPI`
created, polled, and cancelled a synthetic pending import.

This run used placeholder `.m4b` and `.epub` files; it does not validate media
processing, successful provider fulfillment, operator data, PostgreSQL
operator data, or installed rollback. The later source commit
`ad40d565` additionally requires the audiobook media scope and
non-overriding narrator-preference payload for the restricted key. Focused
ChaptarrNG NUnit tests cover both formats, allowed and denied narrator calls,
and plus-encoded spaces; SeerrNG's focused adapter tests confirm that it sends
`mediaType=audiobook`. This migration smoke predates the later narrator-scope
and UI-shell changes. At that time, exact-head Build
[39474807076](https://github.com/snapetech-org/chaptarrng/actions/runs/39474807076)
passed Linux, Windows, and PostgreSQL migration jobs for source commit
`55f6ae1a`; the macOS job was cancelled before runner setup on both attempts.
The current exact-head result is recorded at the top of this guide. No release
or compatibility bridge was published.

## Populated SQLite migration and restore rehearsal (2026-10-21)

The ChaptarrNG `main` candidate at
[`69abed74`](https://github.com/snapetech-org/chaptarrng/commit/69abed74de74250c5ad3f6bed2c0c762dbcd813f)
passed preview, explicit migration, restore preview, and rollback against
isolated SQLite snapshots from two populated BookshelfNG installations. Both
sources were at migration 158 and used Hardcover mode. Each preview covered 39
source tables with no blocking issues; the import report accounted for every
source row, including the command, provider-health, scheduled-task, and
migration-history state that ChaptarrNG intentionally recreates.

Each explicit apply reached ChaptarrNG schema 121, passed SQLite integrity and
foreign-key checks, and retained a BookshelfNG source backup. The saved
Hardcover token was present under `HardcoverApiToken` with the expected
`Bearer ` prefix normalization, and `HardcoverEnabled` remained true. Restore
preview and apply passed for both copies. The restored database and retained
backup matched the original snapshots by table definitions and all table row
contents; integrity checks passed and no foreign-key violations were present.
The original snapshots remained byte-unchanged. The live BookshelfNG services
were left running and continued to answer `/ping`.

This establishes populated SQLite conversion and database rollback for these
two data sets on the current source candidate. Normal startup on fresh copies
of the same snapshots is recorded below. Filesystem or media processing, live
provider behavior, SeerrNG workflows, PostgreSQL operator data, and installed
package, image, Helm, or Unraid handoffs remain unvalidated. The public
`v0.9.977` release was not the tested candidate; publication and compatibility
bridges remain frozen.

## Populated SQLite normal startup and configuration preservation (2026-10-21)

A Linux x64 candidate built from ChaptarrNG `main` at
[`69abed74`](https://github.com/snapetech-org/chaptarrng/commit/69abed74de74250c5ad3f6bed2c0c762dbcd813f)
was started against fresh, isolated copies of both populated source snapshots.
Each container had no network access, mounted only its copied `/config`, and
used the source's `HARDCOVER=true` setting. ChaptarrNG automatically converted
each BookshelfNG database from migration 158 to schema 121 during normal
startup.

The audiobook copy returned `/ping` HTTP 200 on its saved port 8788 after 102
seconds. The ebook copy returned HTTP 200 on its saved port 8787 after 105
seconds. Both converted databases passed SQLite integrity checks and had zero
foreign-key violations. Their original `config.xml` files retained
`AuthenticationMethod=Forms`, `AuthenticationRequired=Enabled`, and the same
API key values after startup; no credential value is recorded here. A request
to `/api/v1/system/status` without an API key returned HTTP 401 on the
converted audiobook copy. The original source snapshots stayed byte-unchanged,
and both live BookshelfNG services remained up and answered `/ping` with HTTP 200.

This verifies normal startup and configuration preservation for these two
isolated SQLite snapshots. The configured library directories were not mounted
and the container network was disabled, so this does not validate media file
access, provider calls, SeerrNG workflows, an image or package switch, rollback
on an installed service, or full BookshelfNG feature parity.

## StartOS `/init` and populated-database rehearsal (2026-10-21, historical design)

A local Linux/amd64 `bookshelf-hardcover` image built from ChaptarrNG commit
[`2d6124e2`](https://github.com/snapetech-org/chaptarrng/commit/2d6124e2) was
run through a wrapper with `ENTRYPOINT ["/init"]` and `CMD []`, matching the
StartOS command that invokes `/init` without arguments. The isolated
containers had no network and mounted task-scoped copies at `/config`,
`/books`, and `/downloads`. Two copied populated BookshelfNG SQLite databases
converted from schema 158 to ChaptarrNG schema 121 during normal startup.

The no-argument run preserved the source config's port `8787`, Forms
authentication, and API key. `/ping` returned HTTP 200, the Docker
health-check command passed, SQLite integrity was `ok`, and there were zero
foreign-key violations. An unauthenticated `/api/v1/system/status` request
returned HTTP 401. The original source database snapshot remained unchanged.
At the time, the test compared two temporary compatibility targets with the
standard image's `8789/tcp` metadata. That packaging design was later
withdrawn; the current candidate uses only the standard image, and channel
configuration preserves the legacy service port.

This exercises the Linux image and copied data only. The `/books` and
`/downloads` mounts were empty, the container network was disabled, and the
StartOS package was not built or installed on a device. It does not validate
media access, provider calls, package update, StartOS backup/restore, or
rollback on an installed StartOS service.

## Historical populated-database rehearsal (2026-10-14)

The ordinary Linux/amd64 ChaptarrNG image was exercised against an isolated
copy of a real BookshelfNG migration-158 SQLite database. Preview accounted
for all source tables and rows and left the source database hash
unchanged. At the time, that earlier candidate required a complete per-book
media map before preview could pass. The same standard image then completed
explicit apply on one copy and normal first-start conversion on another. No
compatibility image or build was used, and the live BookshelfNG service was
not changed.

The applied database reached native migration v119. Its retained backup
matched the source table definitions and rows exactly; both databases
passed SQLite integrity and foreign-key checks. The importer retained source
file rows with `EditionId=0`, BookshelfNG's intentional unmapped-file
sentinel, without inferring an edition, book, or media type. A focused fixture
also verifies that explicit sidecar and history links to such a file remain
attached correctly. Current importer code infers only the media type for a
sentinel or missing-edition file when its path has a recognized ebook or
audiobook extension; its `EditionId` and book relationship remain unassigned.
Sidecars and history that already name a source book use that inferred format
to select the corresponding split book row. Unknown extensions remain
unclassified. Volatile command queue/execution, provider health/cursor,
scheduled-task, and source `VersionInfo` state is deliberately recreated;
acquisition history is migrated.

Normal startup kept the saved port, URL base, API key, and SSL settings,
advertised Docker image-pull updates, retained a source backup, served the
existing port's ping and UI routes, and returned all four configured indexers
with their runtime-derived RSS and search capabilities. The conversion logs
and database copies were kept in a private VM scratch directory.

The real database exposed books whose media choice could not be derived from
source relationships. Earlier test maps used explicit technical choices
only; those choices were not reviewed as correct library classification. The
published `v0.9.977` release keeps unresolved records in both catalogs
without monitoring and retains a private review file. Operators still need to
confirm the true format and monitoring intent for those records. A map can
apply that review before conversion; otherwise correct the records after
startup. The fallback removes the startup blocker, but it does not establish
semantic parity for those books.

This proves one populated migration-158 SQLite path on Linux/amd64. It does not
prove older BookshelfNG schemas, the PostgreSQL candidate, every provider or
notification setting, representative M4B conversion, Windows-native
conversion, package-channel upgrades, rollback against a real populated deployment, populated SeerrNG
requests, or complete feature parity. Feature, installation, and distribution
channels remain separate continuity checks.

## Published-image populated SQLite round trip (2026-10-18)

The public ChaptarrNG `v0.9.977` Linux/amd64 image was also tested against a
private populated migration-158 SQLite snapshot. The live BookshelfNG service
and database were not used as the test target. Preview completed with the
snapshot database bytes unchanged and created no migration backup. It produced
private media-review artifacts; no media map was supplied, so ambiguous format
choices remain for operator review.

Explicit apply on a separate copy reached native Chaptarr migration v120 and
retained the migration-158 source backup. The source backup matched the input
snapshot's logical database contents. The converted database and retained
source backup both passed SQLite integrity and foreign-key checks. On that
copy, restore preview passed, and explicit rollback restored migration 158
with the same logical database contents as the source snapshot. Rollback kept
the migration-120 ChaptarrNG database as a separate backup matching its
pre-restore contents; both databases passed integrity and foreign-key checks.
A second isolated restore-preview check left both database files byte-for-byte
unchanged and created no ChaptarrNG backup artifact.

This adds populated SQLite apply and rollback evidence for one migration-158
database. It does not resolve ambiguous media classifications or establish
older-schema, PostgreSQL, Windows-native, package-channel, external-client, or
full feature-parity acceptance. No user data or database files were committed.

An earlier hosted Windows run of the migration candidate failed four
promotion/rollback cases. A later native Windows service round-trip on Build
39365402673 passed injected-health-failure rollback, retry, explicit restore,
deferred service cleanup, and selected data-directory preservation for the
SQLite installer route. The new Windows PostgreSQL backup/restore extension
has not yet had an end-to-end Windows service rehearsal.

**Latest Windows service validation (2026-10-24):** ChaptarrNG commit
`db110883` passed hosted Build [40087832094](https://github.com/snapetech-org/chaptarrng/actions/runs/40087832094).
The service smoke verified automatic BookshelfNG rollback after an injected
health-check failure, retry and successful migration, and explicit restore
after the deferred service cleanup, preserving the original `NetworkService`
identity and BookshelfNG data. The failed candidate run exposed a PowerShell 7
dynamic `Add-Type` compilation error for `Win32Exception`; the corrected helper
now reports native service-control failures with their Win32 error codes.
This remains synthetic SQLite service evidence. Windows PostgreSQL installed
rollback and representative operator-data acceptance are still open.

## Published image smoke test (2026-10-15)

The public standard `v0.9.971` Linux/amd64 image
([release](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.971))
was exercised against a populated synthetic database built from the checked-in
BookshelfNG migration-158 schema and importer fixture: one author, one source
book, two editions, two files, and two history rows.

Preview left the source database bytes unchanged and created no backup. The
explicit apply command converted the data to native migration v120, retained
the v158 source backup, and passed SQLite integrity and foreign-key checks.
After adding a configuration row as the image's default service user (UID 99),
rollback restored the v158 source and retained the newer ChaptarrNG database,
including that write, in its ChaptarrNG backup.

On a separate copy, the ordinary container startup command detected and
converted the same source automatically, retained its backup, passed the
container health check, and passed SQLite integrity and foreign-key checks.
A v157 preview was rejected with the source bytes unchanged and no backup or
promotion. These checks exercise the published Linux image and synthetic
fixture; they do not replace validation against an operator's own data or prove
support for another schema or database engine.

A separate fixture included a monitored source book with no editions. Preview
stopped without changing the database and wrote the review file with mode
`0600`, owned by the container's default UID 99. After the operator map chose
`both`, preview passed and apply created audiobook and ebook records with the
source monitoring state, removed the review file, retained the source backup,
and passed SQLite integrity and foreign-key checks.

## Published image smoke test (2026-10-17)

The public `ghcr.io/snapetech-org/chaptarrng:0.9.974` image was exercised
against three isolated copies of the checked-in migration-158 fixture (39
source tables). The tested `latest` tag had the same per-platform manifest
digests as `0.9.974` and reported application version `v0.9.974.0` at release
commit `53a265f0`.

Preview left the source database hash unchanged and created no backup. Explicit
apply completed and retained the BookshelfNG backup. Rollback preview passed.
After writing a setting to the converted ChaptarrNG database, rollback restored
the BookshelfNG v158 source while retaining the later ChaptarrNG write in a
separate backup. On a third copy, normal startup detected `readarr.db`,
converted it to the native Chaptarr schema, and started the service.

The fixture has no `/library` mount or desktop browser, so normal startup also
logged the expected missing-root and headless-browser warnings; those did not
stop conversion or service startup. This validates the current public image's
migration, rollback, and automatic startup paths against the synthetic fixture.
It does not establish behavior for older schemas, PostgreSQL source
conversion, live operator data, every BookshelfNG feature, or every package
and update channel.

## Published image smoke test (2026-10-18)

On an isolated Linux/amd64 Docker host, the published BookshelfNG `main` image reported version `0.4.21.71` and created an empty migration-158 SQLite database with 39 tables. After stopping it, ChaptarrNG `v0.9.977` started against the same `/config`, `/data`, and `/download` mounts. Its normal startup converted the database to Chaptarr migration 120, retained the untouched BookshelfNG backup, and listened on the saved port 8787. The converted database and source backup both passed SQLite integrity checks.

Rollback preview and apply passed. The restored BookshelfNG database remained at migration 158 with 39 tables, and the previous BookshelfNG image started against it on port 8787. A test-only setting written after conversion remained in the retained ChaptarrNG backup. This was an empty-database Docker rehearsal; it does not validate populated operator data or the Unraid Community Apps template update flow.

## Provider-mode image-switch and rollback (2026-10-18)

A follow-up rehearsal used a populated synthetic database served by the public
BookshelfNG `.71` Hardcover image. It exposed that starting the prior Chaptarr
candidate without `HARDCOVER=true` could misclassify bare numeric provider IDs
as Goodreads IDs. With the source mode preserved, migration kept author,
book, and edition IDs in Hardcover fields, passed database integrity checks,
and served the converted API records. Rollback restored the source database
byte-for-byte, retained the converted database separately, and the `.71`
Hardcover image reopened the restored source. The current importer fails
closed on bare IDs when the mode is unset or invalid. An earlier candidate
added separate compatibility image targets; that proposal was withdrawn and
no such images were published. All legacy aliases now point to the standard
ChaptarrNG image, and channel configuration or an operator choice supplies
provider mode.
Exact-head run [39175250100](https://github.com/snapetech-org/chaptarrng/actions/runs/39175250100)
passed the provider-mode regression and PostgreSQL fixture checks. The final
Linux artifact refused conversion without a provider mode, preserved Goodreads
and Hardcover identities with their explicit modes, converted during normal
startup, and restored the source byte-for-byte during rollback. Local BuildKit
target checks and hosted alias/bridge regression scripts also pass; no
provider-specific images were published, and the standard-image legacy alias
bridge remains gated and unpublished.
This synthetic rehearsal does not establish operator-data or installer
acceptance. See the detailed
[fixture evidence](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).

## AUR package candidate rehearsal (2026-10-18)

In an isolated Arch container, the active BookshelfNG `.71` systemd service was
upgraded through the local BookshelfNG-identity `.977` package candidate. The
pacman hooks stopped and restarted the service; ChaptarrNG startup converted a
synthetic populated migration-158 database to migration 120 and retained the
source backup. Restore preview and apply returned it to migration 158, retained
the converted database, and passed SQLite integrity and foreign-key checks.
Pacman downgrade restored `.71`, and the BookshelfNG service started against the
restored database. The config override and synthetic data canary survived the
transition. `systemd-networkd-wait-online` was masked only in the test container
because its network was disconnected. This validates the candidate's synthetic
service and database path, not operator data or host-specific settings; the
candidate remains unpublished. See the [package test record](../packaging/README.md).

## Limits and update channels

The published `v0.9.977` converter accepts SQLite databases at BookshelfNG
migration 158 and bundles the source migration chain. Its table guard rejects
versions before migration 14 because the chain has not yet renamed `Blacklist`
to `Blocklist`. PR #126 merged the version-aware fix into `main`, with hosted
migration checks passing, but the fix is not in the published release.
Populated synthetic v22 coverage exercises conversion, rollback, and
non-mutating preview. This does not establish older-schema behavior on real
operator data or PostgreSQL source conversion. The PostgreSQL candidate remains unavailable to operators and unpublished; see the [candidate notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md).
The published SQLite converter can preview a migration-158 source, but apply
is not approved during the current release freeze. Do not start normal
ChaptarrNG against BookshelfNG appdata: startup automatically converts a
supported source database. Keep BookshelfNG in service until the acceptance
gates close and a supported cutover is announced.

The public ChaptarrNG `v0.9.977` release contains the converter and standalone
archives. The version-1-to-13 table-alias fix passed hosted validation on
`main` but remains unavailable in the public release during the freeze.
Current package and image-channel status is tracked in
[`packaging/README.md`](../packaging/README.md). The migration bridge is
disabled: existing BookshelfNG package IDs and mutable image aliases do not
currently redirect to ChaptarrNG. Pinned tags, digests, offline installs, and
third-party deployment definitions always require an operator update. A
mutable tag only affects a later pull or container recreation.

The parked StartOS package is still BookshelfNG package ID `bookshelfng` and
pins its `.37` native-Hardcover image. Its wrapper expects port `8787` and
starts `/init`; public ChaptarrNG `v0.9.977` defaults to port `8789`. The
current StartOS source candidate uses the one standard ChaptarrNG image digest,
provides `/init` in that image, and supplies the legacy port and provider mode
through package configuration. A disposable wrapper rehearsal against the
public `.977` digest passed `/init`, `/ping` on `8787`, and mount checks; it
does not validate the current source candidate. The package has not been
updated or tested on a StartOS device. Keep it on BookshelfNG until the
standard-image candidate passes package build, upgrade, backup, and rollback
checks on a StartOS device.
See the [channel status](../packaging/README.md#package-identities).

### Helm chart handoff

ChaptarrNG's [`charts/bookshelfng`](../charts/bookshelfng/README.md) is a
source-only, fail-closed compatibility chart in ChaptarrNG `main`. It keeps the
legacy chart and Kubernetes resource identities, including the release name,
StatefulSet selector, Service, port `8787`, `/config` mount, and default PVC
name. It requires explicit migration confirmation, the source's
Hardcover/Softcover mode, and an immutable ChaptarrNG image digest. The release
workflow now contains a versioned chart publisher for the existing OCI chart
identity, but both release-readiness gates, the migration bridge gate, and an
explicit bridge request are required; the publisher has not run and no chart
artifact is available during the freeze.

The chart now includes a fail-closed restore Job for rollback after a healthy
conversion. It requires a separate scale-to-zero step, explicit database type,
operator confirmation, and—on PostgreSQL—the retained independent
log/cache-backup directory. This adds a source recovery path; it has not been
accepted on an installed cluster. An installed PVC upgrade, GitOps sync, and
Helm rollback remain unverified, so this does not authorize a live cutover.
After all migration and channel acceptance gates close, Helm users can upgrade from the same
`oci://ghcr.io/snapetech/bookshelfng/bookshelfng` source while keeping the
existing release name, namespace, values, and PVC. Pinned chart archives need a
manual update. See the chart guide for the command shape; do not use it against
live BookshelfNG data during the release freeze.

BookshelfNG's in-app updater still uses its existing upstream update service;
Snapetech cannot redirect that service. The practical handoff paths are a
manual image/package/chart-version change or a separately validated
legacy-identity bridge.
The bridge must preserve each channel's app-data path and service settings and
must be tested as an upgrade, including startup conversion and rollback, before
it is enabled.

A local Fedora 44 RPM transaction upgraded the published BookshelfNG `.71`
package to a `.977` bridge candidate and downgraded back while preserving
config/data canaries. The candidate used the legacy package identity; its
`systemctl` scriptlet checks were mocked and RPM dependencies were bypassed.
The current legacy COPR repo has no package entries, so this does not validate
installation through COPR/DNF, a real systemd service, or database migration.
See the [package-channel status](../packaging/README.md).

Database conversion is only one retirement requirement. ChaptarrNG does not
yet provide full BookshelfNG behavior parity. Follow the separate
[BookshelfNG parity roadmap](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/bookshelfng-parity-roadmap.md)
for open feature, integration, and distribution work, including SeerrNG and
client behavior. Do not treat successful database conversion as proof that a
BookshelfNG deployment can be retired without losing behavior.
