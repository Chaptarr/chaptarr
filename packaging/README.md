# Native package channels

Status checked 2026-10-26. ChaptarrNG [`v0.9.977`](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.977)
is the latest published release; [main](https://github.com/snapetech-org/chaptarrng/tree/main) includes candidate-only
BookshelfNG migration and package-bridge tooling. GitHub Release archives are the payload for
native packages; the standard ChaptarrNG container is built separately from
the same application source and migration code. Package recipes add service
and filesystem integration. See the
[ChaptarrNG v0.9.977 release](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.977)
for the published archives, checksums, and source RPMs.

The current local candidate has passed the installed and runtime rehearsals
listed in the [channel acceptance matrix](../docs/maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).
It remains unpublished; platform, architecture, and publisher gaps listed in
that matrix keep the release gates closed.

The old-identity package recipes remain disabled candidates. Do not install a
bridge package or point ChaptarrNG at BookshelfNG appdata until migration,
rollback, feature-parity, and channel acceptance gates close. Normal startup
can convert a supported BookshelfNG database automatically.

For current BookshelfNG migration and legacy-channel acceptance status, see
[BookshelfNG retirement status](../docs/BOOKSHELFNG_RETIREMENT.md).

The standalone validation workflow accepts optional `runtime_packs_run_id` to
reuse an unexpired runtime-pack artifact. The source must be this repository's
trusted main archive workflow, its runtime producer must have succeeded, and
all producer/helper Git blob hashes must still match. The artifact's original
run, commit, digest and input hashes are retained with the current run's packs.
Omit the input to build packs again. Reuse applies only to the pinned runtime
packs; application archives and acquisition tests still build/run at the current
workflow revision. This validation does not publish anything.

## Package identities

| Channel | New ChaptarrNG install | Existing BookshelfNG identity | Data path during upgrade |
| --- | --- | --- | --- |
| Debian / Ubuntu `.deb` | No ChaptarrNG `.deb` is currently published | Candidate package keeps `bookshelfng` | `/var/lib/bookshelfng` |
| Arch AUR | `chaptarrng-bin` | `bookshelfng-bin` | `/var/lib/bookshelfng` |
| Fedora COPR | `slskdn/chaptarrng` | `slskdn/bookshelfng` | `/var/lib/bookshelfng` |
| Chocolatey | `chaptarrng` | `bookshelfng` | `%ProgramData%\BookshelfNG\data` |
| macOS standalone | ChaptarrNG `osx-x64` / `osx-arm64` release archive | One-time archive download and launcher update; the old updater cannot discover ChaptarrNG | Existing `-data` directory |
| FreeBSD standalone | ChaptarrNG `freebsd-x64` release archive | One-time archive download and launcher update; the old updater cannot discover ChaptarrNG | Existing `-data` directory |
| Docker | `ghcr.io/snapetech-org/chaptarrng` | Legacy image aliases | Existing `/config` mount |
| AppImage | ChaptarrNG standalone Linux x64 archive | Candidate keeps desktop file ID `com.snapetech.bookshelfng` | `$XDG_DATA_HOME/bookshelfng` |
| Flatpak | ChaptarrNG standalone Linux x64 archive | Candidate keeps app ID `com.snapetech.BookshelfNG` | Flatpak config path `bookshelfng` |
| Cloudron | Candidate manifest in `packaging/legacy/cloudron/` | Keeps app ID `tech.snapetech.bookshelfng` | `/config`, `/books`, `/downloads` |
| Co-op Cloud | Candidate recipe in `packaging/legacy/coop-cloud/bookshelfng/` | Keeps recipe and volume identities `bookshelfng` | `/config`, `/books`, `/downloads` |
| Self-hosting catalogs | Candidate Compose service in `packaging/legacy/catalogs/` | Keeps container name and named volumes `bookshelfng` | `/config`, `/books`, `/downloads` |
| StartOS | No ChaptarrNG package update is published | `bookshelfng-startos` keeps package ID `bookshelfng` | StartOS 0.4.0.2 or newer; `/config`, `/books`, `/downloads` |
| Helm | ChaptarrNG `charts/bookshelfng` with a versioned publisher for the existing OCI identity; unpublished while the release freeze is active | `oci://ghcr.io/snapetech/bookshelfng/bookshelfng` | Same Helm release, namespace, `/config` PVC, and volume name |

Legacy package recipes keep the old package identity, service name, and data
path so a package upgrade can start the normal ChaptarrNG database migration.
They are a bridge candidate, not proof that every existing install can be
upgraded safely.

The Chocolatey `bookshelfng` bridge stops the service, runs a read-only
migration preview, backs up PostgreSQL main/log/cache databases with `pg_dump`
when PostgreSQL is configured, and checks `/ping` on the saved local port
before it reports a successful upgrade. New installs use
`%ProgramData%\BookshelfNG\data`; upgrades preserve a custom `-data` service
argument and the other NSSM service arguments and settings. PostgreSQL dumps
and SQLite migration backups stay beside the source data. A failed health
check rolls back the application payload and restores the source database; if
the rollback cannot be verified, the service stays stopped and the backup is
retained for manual recovery. Existing NSSM and machine-level `HARDCOVER` and
`HARDCOVER_NATIVE` settings are retained. If the source did not save a mode,
BookshelfNG defaults to Goodreads and the bridge keeps that behavior. Pass an
explicit parameter only to override it, for example:

```powershell
choco upgrade bookshelfng --yes --params "'/ProviderMode=Goodreads'"
```

Use `/ProviderMode=Hardcover` for native Hardcover IDs. An explicit choice is
stored in the service environment so later Chocolatey updates keep the same
identity interpretation. A preview that cannot classify IDs fails before the
package switches the application files.

The transition package keeps unattended updates automatic by default. To ask
before any service stop or application staging, use
`choco upgrade bookshelfng --yes --params "'/MigrationMode=ask'"`; answering
No cancels the Chocolatey update and leaves BookshelfNG in place. Use
`/MigrationMode=manual` to defer conversion without prompting and follow the
standalone Console migration steps in
[`docs/MIGRATE_FROM_BOOKSHELFNG.md`](../docs/MIGRATE_FROM_BOOKSHELFNG.md).

The private Windows acceptance helper also injects a failure after a real
BookshelfNG database migration passes health checks. It verifies Chocolatey
still records the `.76` predecessor, the schema-158 source database and canary
are restored, the original service executable and settings run healthy again,
and the failed target database is retained for diagnosis before it retries the
successful upgrade. This source-level rollback leg still needs an exact-head
private Windows JIT pass before it counts as channel acceptance.
Debian and RPM pre-install hooks also cancel their package transaction on No.
Pacman cannot cancel an active transaction from a package scriptlet: the AUR
hook records the deferral, restores the BookshelfNG application/unit/launcher
after the package payload phase, and asks again on the next package transaction.
AppImage and Flatpak ask on first migration launch; their desktop entries open
a terminal, and No defers data conversion without undoing a downloaded or
deployed application. Docker shows the same choice when run with an attached
TTY; headless Docker/Compose and Kubernetes have no terminal, so ask mode
defers startup until the operator chooses automatic mode and restarts. StartOS
offers an automatic/manual setting, while Unraid, Co-op Cloud, and Compose
recipes expose the migration mode as an environment setting. Cloudron has no
terminal prompt: the administrator holds or applies the app update and can set
`BOOKSHELFNG_MIGRATION_MODE=manual` before updating to defer conversion.
For Co-op Cloud, choosing No means leaving the BookshelfNG stack deployed.
Applying the candidate in Manual mode preserves the source database but leaves
the replacement app unavailable until Automatic is selected and redeployed.

The Debian candidate recipe and local package builder are in
`packaging/legacy/debian/` and
`packaging/scripts/build-bookshelfng-deb-bridge.sh`. The builder consumes the
standard Linux x64 release archive and produces a `.deb` under the old package
identity. The package-content contract checks the payload, service and config
paths, and maintainer-script handoff. It also verifies that the shipped
`bookshelfng.env` has the same SHA-256 as the final public `.71` package, then
runs an isolated dpkg round-trip and an apt upgrade/downgrade in a pinned
Debian Trixie container.

A local candidate built from the checksum-verified ChaptarrNG `v0.9.977` Linux
x64 archive upgraded the checksum-verified BookshelfNG `0.4.21.71-1` package
and downgraded back. Both dpkg and apt transactions preserved an edited
`/etc/bookshelfng/bookshelfng.env` and a data canary. The apt transaction
resolved real Debian dependencies, including `libicu76` and
`ca-certificates`; it installed the CA bundle and completed without a conffile
prompt. A separate Debian Trixie systemd-container rehearsal started the
actual `.71` service from a fresh data directory, upgraded it to the local
`.977` candidate, and confirmed the active service restarted and returned
`/ping` successfully. The run exercised the previously active but disabled
unit case. It then restored the pre-upgrade data copy, downgraded to `.71`, and
confirmed that service returned `/ping` again. Edited config and data canaries
survived both transactions. This was not populated BookshelfNG operator data;
the candidate started against the `.71` package's fresh empty database. The
isolated dpkg transaction bypassed dependency checks and mocked systemd. There
is no apt repository update path; the candidate has not been exercised on a
real host or published to a GitHub Release or Ubuntu PPA. Keep this channel
disabled until representative-data rollback and channel acceptance pass.

The Debian Trixie apt regression now also drives the optional prompt through a
real package-manager transaction. Answering `n` keeps the synthetic `.71`
BookshelfNG package, config, and data canary in place without creating handoff
markers. Answering `y` installs the candidate and its retained handoff snapshot;
the test then downgrades to the legacy fixture. This choice test does not run a
systemd service or migrate a populated operator database.

The stable release workflow now attaches the legacy `.deb` and checksum only
after an explicit bridge request and when the migration and both release
readiness gates are true. Those gates remain closed; this workflow has not
published a bridge asset. The gated Launchpad publisher now builds signed
source packages from the checksum-verified Linux x64 release archive for each
configured Ubuntu series, uses Launchpad's numeric series version in the Debian
revision, then lets Launchpad build the `.deb`. It requires
the `GPG_PRIVATE_KEY` Actions secret and `LAUNCHPAD_PPA` plus
`LAUNCHPAD_SERIES` repository variables, as well as the same migration and
release-readiness gates. Launchpad does not accept direct binary-package
uploads; see its [PPA upload instructions](https://help.launchpad.net/Packaging/PPA/Uploading).
The external PPA credentials, archive, and enabled series still need to be
validated; this change has not uploaded a source package.

The legacy environment-file conffile is byte-for-byte identical to the final
public BookshelfNG package. Keeping that checksum stable avoids an unnecessary
dpkg prompt during unattended upgrades; add new migration guidance to external
documentation instead of changing this file.

## Standalone archive coverage

The published `v0.9.977` archives cover `linux-x64`, `linux-arm64`, and
`win-x64`. The current source release workflow now covers the full BookshelfNG
archive matrix: `linux-x64`, `linux-x86`, `linux-arm64`, `linux-musl-x64`,
`linux-musl-arm64`, `freebsd-x64`, `osx-x64`, `osx-arm64`, `win-x64`, and
`win-x86`. Linux x86 and FreeBSD use separately built, pinned .NET runtime
packs. A no-publication validation workflow builds and verifies all ten
candidate archives. Run
[39582093706](https://github.com/snapetech-org/chaptarrng/actions/runs/39582093706)
passed eight archive targets; Linux x86 and FreeBSD initially failed because
`cp -a` tried to restore ownership and timestamps on preexisting SDK files.
ChaptarrNG `main` now skips those attributes when merging custom packs. The
follow-up
[39595241427](https://github.com/snapetech-org/chaptarrng/actions/runs/39595241427)
reached pack collection but failed because it expected SDK 10.0.402 to include
the pinned 10.0.12 reference packs. The source fix now packages the installed
SDK reference-pack versions and pins custom-runtime publishing to 10.0.12. Its
focused checks pass locally; hosted validation is pending. CI artifacts are
temporary and do not publish assets. These archives are not public until a later
release is approved. Keep
BookshelfNG in service until the full migration and channel acceptance gates
close.
Keep the BookshelfNG installation in service until a replacement channel is
validated or that platform is explicitly retired.

BookshelfNG's built-in updater cannot discover the first ChaptarrNG release.
Manual macOS and FreeBSD installs therefore need a one-time download of the
matching ChaptarrNG archive and a launcher or service path update. Starting
that archive with the existing `-data` path performs the automatic database
migration; the importer switches the built-in updater to checksum-verified
ChaptarrNG GitHub releases for later updates. The migration guide has the
platform-specific command and rollback notes.

## Public status

Channel-level installed acceptance for the local 0.9.978 candidate is recorded
in the [2026-10-26 matrix](../docs/maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).
It distinguishes an installed predecessor upgrade from source-only or feed-only
checks.

| Channel | Published state | Local installed acceptance | Open blocker |
| --- | --- | --- | --- |
| Debian / Ubuntu | No ChaptarrNG bridge .deb or PPA package is published; the final BookshelfNG .deb is 0.4.21.71. | Debian 13 installed .71 to local .978. Ask No cancelled before staging; Yes converted and started the service; source backup, health, and rollback passed. | PPA source upload, credentials, series, and feed upgrade are untested. |
| AUR | ChaptarrNG 0.9.977-1 is public; BookshelfNG 0.4.21.71-1 remains the predecessor. | Arch systemd installed .71 to local .978. Pacman No deferred/restored BookshelfNG; a later transaction Yes converted and passed health and restore checks. | Publish and verify the exact accepted AUR candidate; representative operator data remains open. |
| Fedora / COPR | ChaptarrNG .977 builds exist; the BookshelfNG COPR repository has no package entries for Fedora 43, 44, 45, or Rawhide. | Fedora 44 with SELinux enforcing installed .71 to local .978. DNF No cancelled before staging; Yes migrated, started, and passed health/rollback checks. | The live BookshelfNG COPR feed has no predecessor to upgrade and no candidate is published there. |
| Chocolatey | Public BookshelfNG 0.4.21.76 remains; a separate ChaptarrNG 0.9.973 package is also public. No current 0.9.978 BookshelfNG bridge is published; the earlier bridge push was rejected. | Windows Server 2025 upgraded public BookshelfNG .70 to a local .978 bridge. No cancelled before service/file staging; Yes passed checksum, migration, service identity, database, source-backup, and `/ping` checks. A new private-Windows candidate path now pins public .76 and checks populated logical migration canaries, but has not run. | Pass the exact .76 No/Yes handoff and rollback on private Windows JIT, resolve the bridge feed rejection, and publish only after release gates. PostgreSQL-specific installed acceptance remains open. |
| AppImage / Flatpak | ChaptarrNG archives exist; the BookshelfNG-identity bridge candidates are unpublished. | Same-identity .71 to local .978 migration, data-path preservation, health, and rollback passed with synthetic schema-158 databases. No defers conversion. | Publish signed/accepted artifacts and repeat with representative operator data. |
| Docker / GHCR | Standard .977 images are public; mutable BookshelfNG aliases remain unchanged. | Exact-head image passed interactive TTY No/Yes, headless Ask/defer, automatic conversion, `/ping`, migration health, source backup, and injected-failure rollback. | External image/alias delivery remains gated and unpublished; pinned tags still need operator pull/recreate. |
| Helm / OCI | No BookshelfNG-identity replacement chart is published. | Kind installed upgrade from main-v0.4.21.71 passed ask No/defer, Yes, health, and explicit source restore. | Publish the exact accepted chart/image and exercise the external OCI/GitOps delivery path. |
| Cloudron | No ChaptarrNG package update is published. | Cloudron 10.1.6 installed `.4.21` to local candidate `.4.23`; Manual defer, native backup/restore, Automatic migration, and source-database plus old-package rollback passed. The administrator controls the app update and can set `BOOKSHELFNG_MIGRATION_MODE=manual` first. | Local platform acceptance passed. Feed/catalog publication remains open. The old `.4.21` pre-update backup excludes persistent directories because that package has no hooks. |
| StartOS | No candidate package is published. | StartOS 0.4.0.3 x86_64 and aarch64 VMs passed test-key SDK3 package updates. ARM64 tested Manual defer, Console preview/apply, Automatic migration, and an injected health failure that restored schema 158 and left ChaptarrNG stopped. Database integrity, foreign keys, and `/books` plus `/downloads` canaries passed. | Official signed package, public `.71` predecessor update, external package repository, and package-manager rollback remain untested. |
| Unraid | No replacement template is published. | The local legacy-template Docker rehearsal passed Manual defer, Automatic migration, `/ping`, retained source data, settings and media canaries, and forced rollback/recovery against BookshelfNG `.71` and the local `.978` candidate. The template update flow has no y/n prompt. | Community Applications review and an Unraid-host update/rollback test remain open. |
| Co-op Cloud | No replacement recipe is published. | Isolated Debian 13 / Docker 26.1 Swarm upgraded the pinned BookshelfNG .71 source image to the local exact-head .978 candidate. The same `bookshelfng_app` service and all three volume names remained in place. Manual preserved the schema-158 database byte-for-byte and its media canaries; Automatic migrated to schema 124, passed integrity checks, retained a source backup, and served `/ping`. A deliberately unhealthy update paused; restoring the three-volume snapshot and redeploying the .71 image returned healthy BookshelfNG. The official Backupbot II 2.4.0 CLI backed up and restored a schema-158 fixture and all three same-name volumes using the recipe label; SQLite integrity, byte-exact database recovery, and volume canaries passed. Abra 0.13.0-beta deployed an isolated recipe with the same service/volume identities, created and listed a snapshot, then restored the corrupted schema-158 database and all three canaries through `abra app restore`. | Canonical recipe review, published digest, and maintainer acceptance remain open. The channel uses an operator setting; choosing No means holding the old stack. |
| External self-hosting catalogs | No replacements are published. | Local Compose contract only; CasaOS, ZimaOS, Umbrel, TrueNAS, Cosmos, CapRover, Portainer, and other catalog entries have not been updated or accepted. There is no common y/n contract. | Each catalog needs its own identity, volume, update, defer, and recovery acceptance. |
| macOS standalone | No BookshelfNG-owned remote update route can redirect these installs. | Hosted run [40270599787](https://github.com/snapetech-org/chaptarrng/actions/runs/40270599787) passed No, headless Manual, Yes, health, source backup, and forced rollback on x64 and arm64 against checksum-pinned BookshelfNG .71 predecessors. No release assets were uploaded. | One-time manual bootstrap and launcher/service path switch remain operator actions. |
| FreeBSD standalone | No BookshelfNG-owned remote update route can redirect these installs. | FreeBSD 15.1 installed BookshelfNG .71 to the exact local .978 archive. Terminal No/Yes, health, retained source backup, and forced-failure rollback passed. | One-time manual launcher/service switch remains an operator action; publication is gated. |
| Windows standalone installer | No BookshelfNG-owned remote update route can redirect this install. | Hosted runs [40218131519](https://github.com/snapetech-org/chaptarrng/actions/runs/40218131519) and [40274632625](https://github.com/snapetech-org/chaptarrng/actions/runs/40274632625) passed the pinned BookshelfNG service handoff, No-at-preview defer, Yes upgrade, injected rollback, explicit restore, cleanup, and selected-data-directory checks. | The old BookshelfNG updater cannot redirect this install; users must run the replacement installer. Keep package publication gated. |

Core release validation passed on Linux, macOS, Windows, and PostgreSQL. An
earlier Chocolatey package smoke covered install/uninstall with an empty
database; the installed Windows Server 2025 acceptance above supersedes that
smoke and verifies populated synthetic conversion, service/data continuity,
retained source backup, and the optional No/Yes handoff. The local bridge
package remains unpublished. The Windows VM build matrix also runs the
Chocolatey ask choice handler with No, blank, Yes, and redirected-input cases.

Before a release can publish, the workflow validates AUR private-key
usability, package-scoped ChaptarrNG and BookshelfNG COPR webhook formats,
Launchpad signing-key import, PPA name and public API lookup, configured Ubuntu
series, and Chocolatey key shape. Invalid optional publisher settings skip that channel without
writing credential values to the workflow summary. The main release preflight
also validates the Discord webhook before artifact or image publishing. These
checks do not establish publisher authorization or channel acceptance. The
Launchpad publisher remains unpublished and still requires the migration and
release-readiness gates.

The `v0.9.977` release and standard image were published successfully. The
Chocolatey `chaptarrng 0.9.973` package was approved on October 20, 2026. It
installs the `ChaptarrNG` service and uses `%ProgramData%\ChaptarrNG\data`, so
it is a standard ChaptarrNG install, not a handoff from the existing
BookshelfNG service and data directory. The `.977` package page is not on the
feed; an earlier `.977` bridge push returned HTTP 403. The `bookshelfng`
identity bridge remains unpublished while the migration bridge is disabled.
No credential value is recorded here.

The release workflow validated source RPMs for both identities and triggered
the standard ChaptarrNG COPR webhook for `0.9.977-1`. COPR reports the build
succeeded for Fedora 43, 44, 45, and Rawhide x86_64. The
legacy BookshelfNG COPR trigger was skipped because the migration bridge is
disabled. The ChaptarrNG AUR
identity was published at `0.9.977-1`; the BookshelfNG AUR identity was not
updated. The legacy package and image bridges remain disabled. Re-enabling
them requires both release-readiness gates, the migration bridge gate, and an
explicit bridge opt-in.

The current legacy COPR repository metadata contains no package entries for
Fedora 43, 44, 45, or Rawhide x86_64. The Fedora 44 DNF regression builds a
synthetic BookshelfNG `.71` package fixture and the `.977` candidate from the
current bridge spec. It answers No and Yes at the package-script prompt, checks
that No leaves the old package/config/data in place, confirms Yes creates the
handoff snapshot, and downgrades back to the fixture. A mocked `systemctl`
checks stop-then-start ordering; DNF resolves the container dependencies. A
separate earlier transaction rehearsal used the published `.71` RPM and
`--nodeps`. Neither test runs real systemd or migrates a database. The DNF
regression does not use COPR, which still has no published old-identity package.

On 2026-10-18, the AUR build script built both `chaptarrng-bin 0.9.977-1`
and a local `bookshelfng-bin 0.9.977-1` candidate against the public Linux
release archive. Package-content checks confirmed the ChaptarrNG executable
and package metadata in both packages, plus the legacy service and
`/etc/bookshelfng/bookshelfng.env` path in the candidate. An isolated,
network-disconnected Arch container installed the published AUR version
`0.4.21.71-1`, upgraded the same package identity to the local `.977` candidate,
and downgraded back to `.71`. Pacman retained the modified environment file
with a `.pacnew` default, preserved a synthetic data canary, and restored the
old executable on downgrade.

The installed `.977` package then previewed and converted a synthetic populated
BookshelfNG migration-158 SQLite database to ChaptarrNG migration 120. The
preview left the source bytes unchanged; apply retained the source backup and
passed SQLite integrity and foreign-key checks. Restore returned the active
database to migration 158, retained the converted database as a backup, and
passed the same checks.

A second rehearsal booted systemd in a network-isolated Arch container. It
started the public `.71` BookshelfNG package against the same populated fixture,
then upgraded the active service to the local `.977` candidate. The pacman hook
stopped the BookshelfNG process before replacement; the post-upgrade hook
reloaded systemd and started ChaptarrNG, whose normal startup converted the
database to migration 120. The migration-158 source backup passed integrity and
foreign-key checks; the target had two format-specific book rows and passed the
same checks. The environment override was retained with a `.pacnew` default,
and the data canary remained intact.

With the service stopped, restore preview preserved both database hashes.
Explicit restore returned the database to migration 158 and retained the
converted migration-120 database as a backup; both passed integrity and
foreign-key checks. Pacman downgrade restored `.71`, and starting the service
opened the restored database. The test container had no network, so
`systemd-networkd-wait-online` was masked only inside that disposable container
to let its `network-online.target` complete. This validates synthetic package
and service lifecycle behavior; representative operator data and host-specific
settings remain open. The candidate remains unpublished.

The new Linux build-matrix check also runs the AUR pre-upgrade hook with
`BOOKSHELFNG_MIGRATION_MODE=ask`. A default-No Enter leaves the old app,
service marker, config, and data canary untouched; Yes saves the source app,
unit, launcher, and pending marker before the mocked service stop. The pacman
upgrade rehearsal above covers the package-manager transaction separately.

## Migration behavior and limits

The default ChaptarrNG app-data location differs from BookshelfNG's
native-package locations. A legacy package recipe preserves the old location
so the standard ChaptarrNG app can find `readarr.db` and run its migration.
The published `v0.9.977` converter accepts BookshelfNG migration-158 SQLite
databases and bundles the BookshelfNG SQLite migration chain. It upgrades
recognized source schemas on a disposable copy before conversion; the raw
snapshot remains available for rollback. The current `.977` pre-upgrade check
rejects source schemas before migration 14 because they still have `Blacklist`.
PR #126's version-aware table check and authentic migration-1
rollback fixture passed hosted Linux, macOS, Windows, and PostgreSQL migration
checks, but the fix is still source-only. No follow-up release will be cut
until the full retirement gates close. Populated synthetic migration-22 tests
cover conversion and rollback; real older-schema operator data remains
untested. Unknown or newer schemas, PostgreSQL sources, and two populated
source databases fail closed in the published `.977` build.
The ChaptarrNG-owned Docker handoff helper and manual-systemd bootstrap are
included in Linux x64 and arm64 standalone bundles built from current source.
The public `.977` archives do not contain these newer handoff paths. See the
[migration guide](../docs/MIGRATE_FROM_BOOKSHELFNG.md#chaptarrng-owned-docker-handoff-helper)
for the Docker helper's backup, preview, apply, and rollback behavior, and its
systemd section for the one-time executable-path transition.
The separate PostgreSQL candidate is limited to one migration-158 schema. Earlier smoke run 39127070836 on code head 21c41ad5 timed out during application startup before readiness; this was not a confirmed converter assertion failure. PR #131 later merged to `main` on 2026-10-20. Its exact-head Build [39374140961](https://github.com/snapetech-org/chaptarrng/actions/runs/39374140961) passed Linux, macOS, Windows, PostgreSQL migration, release-note, and native-runner receipt checks. Those PostgreSQL checks use synthetic fixtures only; they do not establish PostgreSQL operator-data support, and the candidate remains unpublished.
The importer keeps records with unresolved formats in both catalogs
without monitoring, writes a private review file, retains the source backup,
and supports validated rollback.

Docker Compose, Unraid, and other container users can keep `/config`,
media/download mounts, ports, UID/GID, and environment settings. BookshelfNG
stores primary Hardcover IDs as bare numbers, so the ChaptarrNG importer
requires an explicit `HARDCOVER=true` or `HARDCOVER=false` setting and stops
before promotion if the mode is missing or invalid. The previous plan to build
separate Goodreads and Hardcover compatibility images is withdrawn. All legacy
mutable image aliases must resolve to the same standard ChaptarrNG image digest;
platform adapters and migration tooling carry the source mode. The standard
image uses ChaptarrNG's normal fresh-install port `8789`; saved `config.xml`,
explicit server settings, and a channel's host-port mapping preserve existing
installations. This source change remains unpublished and gated. Pinned
versions and image digests still need an operator update, and a repointed tag
does not alter a running container.

The BookshelfNG Windows WPF installer still installs the old application and
does not hand off its `Readarr` service. ChaptarrNG now contains a source-only
WPF migration-installer candidate in `distribution/windows/installer-net`.
The release workflow builds it from the exact Windows release payload and
attaches it only when the migration-bridge and both release-readiness gates
are enabled; no installer asset is currently published. It supports one
PostgreSQL source or both SQLite databases, asks for provider mode, creates a
verified retained backup, previews before apply, then switches to the `Chaptarr`
service and checks the saved data path and health endpoint. It requires the
standard `NT AUTHORITY\LocalService` account. Native Windows PostgreSQL
rollback, representative data, and channel acceptance remain open, so it is
not approved for operator use. See the
[Windows migration procedure](../docs/MIGRATE_FROM_BOOKSHELFNG.md#windows-standalone-service-migration).

BookshelfNG's in-app updater still checks its existing upstream update service,
which ChaptarrNG cannot redirect. Automatic migration must therefore use a
validated image/package identity bridge, or operators must change their install
source. Debian/PPA, AppImage, Flatpak, Helm, the Cloudron feed, Co-op Cloud,
third-party catalogs, and pinned/offline installs still need an accepted
published channel update or an explicit retirement decision.

The BookshelfNG StartOS package keeps package ID `bookshelfng`, volume IDs
`main`, `books`, and `downloads`, mounts `/config`, `/books`, and `/downloads`,
and exposes port `8787` with native Hardcover compatibility. The candidate
inherits its pinned image entrypoint through `sdk.useEntrypoint()` and uses the
SDK's port-listening readiness check; it does not invoke `/init` as a child
process or rely on a health script missing from the package rootfs. A StartOS
0.4.0.3 x86_64 VM accepted a test-only update from a synthetic `.4.21:1`
predecessor wrapper to `.9.979:1`: Manual kept the schema-158 database
byte-identical and deferred startup; Automatic migrated to schema 124, kept a
readable source backup and `/books`/`/downloads` canaries, passed integrity and
foreign-key checks, and served `/ping`. This used a synthetic package signed
with a task-local test key and a local x86_64 image. The official signed
package, aarch64 runtime, external repository, and package-manager rollback
still require acceptance.

ChaptarrNG carries local AppImage, Flatpak, Cloudron, Co-op Cloud, self-hosting
catalog, and StartOS candidates. AppImage and Flatpak passed local migration
and rollback rehearsal; Cloudron passed an installed platform update,
backup/restore, migration, and source/package rollback on Cloudron 10.1.6; the
Co-op Cloud candidate passed an isolated Swarm update, defer, failure-pause,
full-volume restore, official Backupbot II CLI backup/restore, and Abra
0.13.0-beta backup/restore orchestration rehearsals; and StartOS passed
test-package VM updates on x86_64 and aarch64, including manual defer,
automatic migration, and automatic source rollback on ARM64. These candidates
remain unpublished. Cloudron feed/catalog publication, canonical Co-op Cloud
recipe review and maintainer acceptance, catalog platform updates, and Unraid
Community Applications/host acceptance are open. The StartOS candidate uses SDK 3.1.0
and requires StartOS 0.4.0.2 or newer. The external `bookshelfng-startos`
package repository has not been modified; official signing, update from the
public `.71` package, and package-manager rollback remain open. See the
[legacy channel candidates](legacy/README.md) and
[BookshelfNG retirement status](../docs/BOOKSHELFNG_RETIREMENT.md).

## Legacy AppImage, Flatpak, Cloudron, and catalog candidates

The AppImage launcher preserves `$XDG_DATA_HOME/bookshelfng`; the Flatpak
manifest preserves app ID `com.snapetech.BookshelfNG`, its desktop launcher,
and its sandbox config path. Both launchers supervise the first startup, check
the local HTTP endpoint and migrated database, and retain enough state to
restore the SQLite/PostgreSQL source after a failed or interrupted launch.
Custom data paths, ports, and URL bases are honored. Their default provider mode
matches the legacy standalone launcher's Goodreads behavior; preserve
`HARDCOVER=true` when the source used native Hardcover IDs. The
supervisor restores data automatically; replacing the AppImage or reinstalling
the prior Flatpak bundle remains the executable rollback step.

Cloudron keeps app ID `tech.snapetech.bookshelfng`, port `8787`, `/ping`, and
all three persistent directories. The Compose candidates keep the `bookshelfng`
container or named volume identities and the same port and mount paths. Those
container files now pin the one standard ChaptarrNG image digest during
candidate generation. They do not create a second application image. Third-party
catalog listings remain owned by their maintainers and are unchanged.

The rebuilt local `.978` AppImage and Flatpak candidates are unsigned and
unpublished. Both passed an installed-channel update rehearsal from the
checksum-verified public `.71` packages: the AppImage replaced the old binary
at the same path, and Flatpak updated the same app ID. On a task-scoped
schema-158 fixture, migration preview left the source unchanged, startup
converted to schema 121, and restore recovered the source database
byte-for-byte. Candidate startup preserved distinct ebook and audiobook paths
for three authors whose shared root equaled the author path; no path repair ran.
The old `.71` packages were reinstalled and each returned `/ping` HTTP 200
against the restored schema-158 database. The first local `.978` candidate
build exposed this path collapse; the rebuilt candidates include the regression
fix, covered by builder and startup-repair tests. The full Core test project
passed 4,607 tests with 0 failures and 19 skips. These checks do not establish
an update from a published ChaptarrNG candidate or acceptance with operator
data. Representative-data acceptance remains open. Cloudron's installed
10.1.6 platform path passed locally, but package/feed metadata remains
unpublished; the `.4.21` pre-update backup excludes persistent directories.
The canonical Co-op Cloud recipe and external maintainer acceptance, and
catalog-specific update and rollback flows have not been accepted. Keep each old installation in service
until that channel has passed its acceptance checks or has an explicit
retirement decision. The StartOS package candidate and external repository boundary are documented in
[`legacy/startos/README.md`](legacy/startos/README.md).

## Work required before enabling the legacy bridge

- Validate the AUR candidate with representative operator data and
  host-specific service settings; the Fedora `.71` to `.977` RPM transaction
  rehearsal passed with canaries, but the legacy COPR repo is empty and live
  DNF/COPR upgrade acceptance remains open. Test the old-identity Chocolatey
  upgrade in a disposable Windows system. Preserve service settings and data
  paths, retain the source backup, and verify rollback without losing later
  writes.
- Update the approved Chocolatey `chaptarrng` package to `.977`, then resolve
  the legacy `bookshelfng` bridge's HTTP 403, publish that identity bridge, and
  confirm public checksums match the tested packages.
- Publish and validate the migration-1-to-13 alias fix before recommending
  `.977` for databases earlier than migration 14.
- Verify old GHCR/Docker Hub destinations, credentials, and per-platform
  manifest digests before changing any mutable image aliases.
- Complete the separate [BookshelfNG feature-parity roadmap](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/bookshelfng-parity-roadmap.md),
  or explicitly accept and document the behavior that will not move.
- Validate AppImage and Flatpak migration against representative operator data,
  including the shared-root path fix now in source. Generate Cloudron version
  metadata from the accepted image and validate published feed/catalog
  delivery; its local installed update, native backup/restore, migration, and
  rollback have passed. Communicate the `.4.21` pre-update backup limitation.
  Update and test the external StartOS package on both architectures,
  the canonical Co-op Cloud recipe, and supported catalog records using a
  verified immutable digest. Run each channel's installed update, backup, and
  rollback checks. Record explicit retirement decisions for unsupported
  channels and preserve a documented manual path for pinned/offline installs.

The database conversion and published-image migration smoke test are complete
for the supported schema. The published BookshelfNG identity bridges and full
feature/channel retirement are not.
