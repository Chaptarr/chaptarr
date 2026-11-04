# BookshelfNG compatibility code handoff

## Full public validation — 2026-11-04

Full public Build run
[41789426720](https://github.com/snapetech-org/chaptarrng/actions/runs/41789426720)
on `d12798d1` passed Linux and macOS build/test/artifact jobs, PostgreSQL
migration smoke, and the standard Docker build. Windows found a test-only
CRLF mismatch when the source-registry test hashed a JSON file from a Windows
checkout. Commit `d3a67c58` canonicalizes text line endings before fingerprint
comparison and covers LF/CRLF equivalence.

Full public rerun
[41792679639](https://github.com/snapetech-org/chaptarrng/actions/runs/41792679639)
passed Linux, Windows, macOS, PostgreSQL migration smoke, and the standard
Docker build. Its Windows job also passed the installed BookshelfNG `.76`
service, data-path, and Chocolatey automatic migration checks.

## Public Windows installed handoff — 2026-11-04

Public Windows run
[41785474409](https://github.com/snapetech-org/chaptarrng/actions/runs/41785474409)
passed on code head `d12798d1`. It ran the full Windows .NET suite, built the
migration installer, verified the SQLite fingerprint, upgraded and rolled back
the installed service, and checked preservation of the selected data directory.
The installed BookshelfNG `0.4.21.76` Chocolatey package then passed manual
defer, injected post-migration-health rollback with source recovery, and
automatic migration to the exact local candidate. This is hosted
synthetic-fixture evidence; representative operator data and feed publication
remain open.

## Runner-policy update — 2026-11-03

The private-JIT-only requirement recorded below is historical. Public
GitHub-hosted runner minutes are available again for trusted work, so
compatible public runners and private ephemeral JIT are both authorized.
Exact-head validation, installed-channel acceptance, representative data, and
live provider evidence remain separate gates.

## 2026-10-26 dual-database merge and validation checkpoint

Normal ChaptarrNG startup and the explicit `-merge-bookshelf-databases` utility
now stage both populated SQLite sources, apply the bundled BookshelfNG
migration chain to working copies, and merge only stable identities. Different
rows with the same identity, conflicting values for a setting, or another
unique-key/reference conflict stop before native promotion. `readarr.db` is the
deterministic primary identity source; source IDs are not silently remapped. Both original databases
remain untouched, while a validated native `chaptarr.db`, a retained merged
BookshelfNG rollback snapshot, and a durable source/target fingerprint receipt
are produced on apply. Startup accepts retained legacy siblings only when that
receipt validates.

The platform supervisors and Windows installers now run a read-only dual-source
preview before first launch or apply. Normal startup automatically merges when
both sources are compatible; conflicting source IDs, settings, or unique
references stop before promotion and leave both originals intact. The explicit
utility remains available for operators who choose manual migration. Source
media maps can be combined or replaced with a reviewed combined map. The merge
rechecks every input media-map fingerprint before promotion, so a concurrent
map edit aborts without changing either source. Empty main files with durable
WAL/journal data are recognized as recovery-bearing; a missing main file with
durable sidecar data fails closed. Explicit preview can still reach both
sources when a prior launch left a native target file, after which target
validation rejects any non-empty target. The CLI rollback gate accepts a
recoverable native database family with a WAL or rollback journal. The merge
does not rewrite provider identities or claim to consolidate two independently
configured libraries. A local bulk pass on `kspld0` built the Release solution
with zero errors and one transient shared-output copy retry, passed 4,920 Core
tests with 28 skipped, and passed the BookshelfNG shell and Docker handoff
checks. These are local diagnostic results; the current organization policy
allows compatible public hosted or private JIT execution for trusted jobs, so
these local results still need separate exact-head validation. PostgreSQL,
browser-backed, live-provider, and installed-channel evidence is still open.
Detailed channel results and remaining delivery gates
are recorded in [the October 26 channel acceptance report](BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).

The merge now holds the normal conversion locks for both legacy files and uses
a SQLite online backup when cloning migrated working copies, so WAL-committed
pages are not lost by a main-file copy. A matching `prepared` receipt is
reconciled to `complete` at native startup; Chocolatey lets its read-only
handoff check perform that validation after a crash between database promotion
and receipt finalization. Rollback moves the merged ChaptarrNG target aside
instead of putting its stale source snapshot back at `chaptarr.db`; BookshelfNG
continues from the two untouched source files, and a later ChaptarrNG attempt
merges their current contents. Dual-database rollback stops if either original
source file has been removed; restore it from the independent appdata backup
first. A nonempty BookshelfNG database at `chaptarr.db` beside both legacy
files now fails closed instead of being converted as if it were the only
source. The desktop launcher now previews both sources and
lets the ChaptarrNG startup path apply the same conflict-failing merge under
health supervision. A conflict keeps the previous application and both source
databases available for manual resolution.

The manual systemd bootstrap now detects the same dual-source layout and passes
the merge option to its service-account preview before replacing the old
application. The Docker handoff helper accepts the merge option for preview
and apply, and permits rollback against the existing ChaptarrNG target while
both retained BookshelfNG source files remain beside it. Helm and StartOS
manual command guides document the flag; automatic container startup uses the
same merge path without requiring a manual command.

Docker compatibility startup now holds an app-data-scoped `flock` for the
supervisor lifetime, preventing two containers sharing one volume from racing
through the handoff or opening the same database concurrently. If both legacy
databases are populated, the supervisor previews the merge and starts ChaptarrNG
only when the combined conversion is safe. It also
stops with recovery guidance when a non-empty SQLite WAL or rollback journal
exists without its non-empty main database, instead of treating that directory
as a fresh install. A replica waiting for the lock keeps liveness healthy while
its own app readiness remains false.

## 2026-10-26 code-only journal hardening

The shared migration journal now stores a fingerprint for the consistent
SQLite source snapshot, the staged ChaptarrNG database, the target executable,
and the source `config.xml`, without storing their paths or contents. Directory
fingerprints use sorted relative paths and file hashes so relocating a retained
backup does not change its value. Application, Linux, and Windows journal
writers reject symbolic links and reparse points and check file stability while
hashing. Promotion records that the source backup was retained. Rollback records
the source and displaced-target artifact names and, for SQLite, their
fingerprints. PostgreSQL records its source engine and converter-owned schema
milestones. Linux package hooks, Windows Chocolatey, and the desktop supervisor
fingerprint independent PostgreSQL source dumps and retained target log/cache
backups. Chocolatey also fingerprints the prior executable and candidate
artifact.

The Release solution compiled with zero warnings or errors, and the full Core
suite passed 4,919 tests with 28 skipped. Linux package, Windows Chocolatey,
desktop, Docker, Helm, StartOS, Cloudron, Co-op Cloud, and standalone handoff
evidence is summarized in the channel acceptance report. These results do not
establish representative operator-data, live-provider, external-feed, or
production cutover acceptance.

This continuation checkpoint closes the current source-level C4–C6 review
families and adds the remaining Windows, desktop, and webhook recovery fixes
along with an explicit Helm rollback job for post-migration recovery. Bulk
installed-channel and workflow acceptance remains separate.

## Implemented in this checkpoint

- The BookshelfNG Helm chart now has a confirmed, opt-in restore Job for a
  rollback after successful conversion. It refuses to render unless the
  StatefulSet is scaled to zero, the operator confirms the restore, persistent
  `/config` storage exists, and the source database type is explicit. PostgreSQL
  rollback validates the retained independent log/cache dumps and database
  availability before mutation, retains current ChaptarrNG log/cache dumps,
  restores the main source schema, then restores the BookshelfNG dumps. Its pod
  does not match the app Service selector. This is
  source implementation only; installed Helm acceptance remains open.
- PostgreSQL rollback now has a read-only dump preflight that verifies every
  selected dump and confirms the target databases are idle before any restore.
  Helm, Docker, Linux packages, desktop startup, and Windows recovery preserve
  the current ChaptarrNG PostgreSQL log/cache dumps before restoring the
  BookshelfNG generation. The main ChaptarrNG database/schema remains in its
  existing retained rollback copy.
- BookshelfNG conversions now write compatibility-profile version 2 into the
  target database: source product and original schema version, effective
  provider dialect (`hc` or `gr`), preserved API base path, audiobook/ebook
  split semantics, identity-map version, and conversion outcome. The unchanged
  legacy API facade and migration health command validate this profile before
  interpreting old IDs. Earlier profile versions remain readable for local
  candidate databases.
- Bare numeric source IDs now follow BookshelfNG's actual runtime predicate:
  native Hardcover is selected only when `HARDCOVER=true` and
  `HARDCOVER_NATIVE` is not `false`; an unset `HARDCOVER` therefore means
  Goodreads. The importer persists the resulting dialect and target mode.
- On a migrated installation, requests to the original `/api/v1` address use
  the recorded source dialect unless the caller identifies itself as a native
  ChaptarrNG client. Existing `/readarr/...` and media-scoped routes keep their
  explicit path behavior.
- The ChaptarrNG frontend marks its API requests with
  `X-ChaptarrNG-Client: native` (or `chaptarrngClient=native` for its EventSource
  request), keeping canonical IDs and native validation available after
  migration.
- An unscoped legacy book add uses the one monitored edition's format, matching
  BookshelfNG's single-work add model. It no longer creates both format rows for
  that legacy request. Legacy author and book adds do not enter ChaptarrNG's
  pending-import protocol.
- The unchanged `/api/v1` address now projects split audiobook/ebook rows back
  to one BookshelfNG book ID. The monitored source edition selects the legacy
  detail row, paged reads retain the old title order and one-work total, edits
  resolve the submitted edition to its target row, monitoring applies to the
  legacy work, old pagination values clamp to BookshelfNG's 1–200 page-size and
  non-negative offset behavior, and edition/file responses translate retained
  source IDs.
- Queue and history filters continue to resolve a legacy book ID to all mapped
  format rows, while queue/history response IDs and included book resources
  project those rows back to the original BookshelfNG work ID. Book responses
  also restore the original `AuthorMetadataId` after metadata rows are merged
  into ChaptarrNG author records. Rename previews include every mapped format
  and project retained book, edition, and file IDs back to the source handles.
- Legacy local book IDs now resolve only when the persisted BookshelfNG
  compatibility context is active. Native clients keep using canonical target
  IDs even when one numeric value overlaps a migrated source ID. The same
  context-aware resolution covers book editors, book files, editions, queue and
  history filters, release search, cover paths, rename/retag/convert previews,
  manual imports, and commands.
- Manual import now resolves source edition IDs before validating their book,
  uses the selected file's format to choose the correct split book row when no
  media type is supplied, and projects nested book, edition, and author
  metadata IDs back to BookshelfNG IDs in preview and update responses.
- Command start translates old book IDs only for legacy requests. Command list
  and detail responses project book IDs in supported command bodies, and
  mapped command/book/file SignalR changes emit `Sync` so each client reloads
  through its own API dialect instead of receiving a target ID. Release results
  and bulk book-editor responses also project split rows back to the old work
  ID.
- A legacy `RefreshBook` command that resolves to both format rows now keeps
  the original command type and response shape while refreshing both rows. Its
  internal fan-out IDs are removed from compatibility responses after the
  original BookshelfNG work ID is projected back.
- Migrated installs now use the BookshelfNG release comparer for same-format
  audiobook and ebook decisions. Mixed-format decisions keep ChaptarrNG's
  format preference; native installs keep the current comparer.
- Linux Debian/PPA, RPM/COPR, and AUR package upgrades now record a durable
  migration-pending marker and a root-only copy of the prior application and
  service unit before replacing an installed BookshelfNG package. The service
  runs a database readiness check after startup and clears the marker only
  after the migrated ChaptarrNG database and provider dialect are readable.
  When readiness fails, the service helper selects the newest retained SQLite
  backup or PostgreSQL backup schema, restores BookshelfNG main data, and
  restores PostgreSQL log/cache schemas from verified pre-cutover dumps. It
  then restores the previous app and unit and lets systemd restart that unit.
  It keeps the displaced ChaptarrNG main schema/database and independent
  PostgreSQL dumps for recovery. Custom
  units or drop-ins restore the previous app and default unit and leave
  migration pending with `operator-review-required`; installs without a valid
  pre-upgrade snapshot block automatic conversion. Debian, RPM, and AUR hooks
  propagate failed service starts. A retained private journal, writable only by
  root and the service account, records the
  detected, snapshotted, preflighted, converted, promoted, healthy, rolled-back,
  and intervention-required states, step outcomes, and SHA-256 fingerprints for
  the source app/database/configuration, service unit, and installed artifact.
  Startup reconciliation observes the current database readiness result after a
  restart; journal fingerprints and outcomes remain after healthy cleanup removes
  the temporary application snapshot.
- SQLite and PostgreSQL converters now record source detection, preflight,
  snapshot, validated conversion, and promotion boundaries in that journal.
  They remove abandoned disposable stage artifacts on a later safe conversion
  attempt; PostgreSQL temporary schemas carry a source-schema marker so cleanup
  removes only converter-owned artifacts for the locked source schema. Retained
  rollback copies remain available. Restore and successful database-readability
  checks advance the same journal.
- When a Windows Chocolatey upgrade starts ChaptarrNG during installation, the
  installer now checks the migrated database lineage and persisted source
  provider dialect after `/ping` before it removes the prior app snapshot. The
  converter milestones use the same data-directory journal on Windows.
- Windows standalone migration now parses both spaced and equals-form `-data`
  service arguments, rejects incomplete or conflicting data paths before
  mutation, and uses the selected path for backup, preview, migration and
  rollback. Its SQLite backup, source-change guard and rollback support both
  populated legacy databases, including their WAL and journal files. It previews
  and applies the explicit merge after operator confirmation; identity or
  configuration conflicts stop before promotion. Reparse-point sources fail
  closed.
- When the BookshelfNG service is stopped at upgrade time, Chocolatey now
  retains the prior app and registers a protected SYSTEM recovery task for the
  next service start. The task verifies the configured local health URL plus
  the migrated database/provider profile, then restores source SQLite or
  PostgreSQL data, the previous app, both NSSM environment blocks, arguments,
  and start mode after failure. It preserves the originally stopped state and
  writes channel outcome/state into the shared migration journal.
- AppImage and Flatpak now launch through a shared supervisor that preserves
  the legacy data path, supports an explicit `-data` override, and checks local
  HTTP plus database health. It records `starting`, `healthy`, `rolled-back`, or
  `intervention-required`, writes channel outcome/state into the shared
  migration journal, and restores source data after a failed/interrupted first
  launch. Spaced and equals-form `-data`, `-port`, and `-urlbase` arguments are
  parsed so health checks use the actual application settings. The health URL
  is restricted to loopback; HTTP checks do not follow redirects or use
  proxies. The launcher supervises and stops the full application process group
  on failure or launcher exit. Migration refuses to start unless
  `setsid --wait` is available. Reopening the old artifact remains the
  executable rollback.
- Desktop and Chocolatey automatic handoffs now detect both populated SQLite
  sources, pass the merge flag to a read-only preflight, and restore from the
  merged-source snapshot if target health fails. Conflicting data stops before
  promotion; desktop also rejects symlinked source files.
- The StartOS bridge now exposes automatic versus manual first-start migration
  in a package action. The choice is kept in the package's separately backed-up
  StartOS volume and passed to the compatibility supervisor at startup; new
  installs keep automatic migration enabled by default.
- A stopped Windows Chocolatey upgrade now distinguishes a BookshelfNG source
  database from an already converted ChaptarrNG database. A failed deferred
  target-to-target update restores the prior app and NSSM settings, preserves
  the ChaptarrNG database, keeps the service stopped, records
  `intervention-required`, and retains the health failure reason. It does not
  try to restore a BookshelfNG backup over a ChaptarrNG database.
- Standard webhook events now project mapped migrated books back to their
  BookshelfNG source IDs. Native ChaptarrNG books continue to use their target
  IDs; imported BookFile IDs already retain their source values.
- A source-pinned C4 pass added migrated-profile branches for BookshelfNG's
  raw-title release terms, quality-first upgrade/cutoff rules, shared author
  tags in delay/protocol/indexer/pending-queue/download-client selection,
  legacy author/book monitoring, and permanent torrent-seeder rejection. The
  remaining source decision specifications were compared; their behavior is
  unchanged, with differences limited to logging. Duplicate-grab suppression
  now resolves split target rows back to one BookshelfNG work ID, so an
  audiobook and ebook row from one legacy work cannot both be grabbed in one
  RSS pass. Source/target blob IDs and dispositions are recorded below.
  The C4 download, matching/import/filesystem and catalog paths; C5 provider,
  settings/auth and webhook contracts; and C6 request identity and primary UI
  workflows have now had source-level semantic review. Dispositions and
  fingerprints follow below. This closes source review only; fixture,
  live-provider, user-workflow and installed-channel acceptance remain open.

The Linux default-unit cutover now has automatic database and application
rollback code. C3/F4 still needs package-manager failure-path and restart
acceptance before it can be treated as closed.

## Container readiness and standalone transition additions

The follow-on code pass tightens container readiness and platform handoff:

- Docker health and Kubernetes readiness require a cleared migration marker
  as well as a healthy `/ping`. During first-start migration, the supervisor
  uses the internal app-only health mode before it clears the pending marker;
  deferred, rolled-back, and intervention-required states stay unready.
- The shared compatibility image no longer assumes every old deployment
  routes internal port `8787`. Compose, Cloudron, StartOS, and Helm supply an
  expected port because those adapters know their service mapping; Unraid can
  follow its saved config and template port.
- StartOS runs the marker-aware container health script before reporting ready,
  and its 245-minute grace covers the bounded migration/startup window.
  Kubernetes keeps liveness successful during migration and recovery; its
  startup-probe budget also covers the 245-minute migration window.
  The total Docker handoff timeout also accepts zero-padded values without
  shell octal interpretation and rejects values too large to compare safely.
- Container recovery distinguishes pre-conversion failures from startup
  failures after conversion may have begun. It retains an untouched source
  database without requiring a restore backup, and resumed `ask` mode prompts
  only once.
- The manual Linux systemd bootstrap verifies that the running service's
  explicit `-data` directory matches `--data-dir` before stopping the service.
  Its migration window now matches the shared 245-minute ceiling, and its
  temporary systemd stop/recovery window is 510 minutes.
- The Cloudron candidate now places nginx on the preserved port `8787` and
  binds ChaptarrNG to loopback port `8788`. Its `/ping` route remains a
  liveness response while the handoff supervisor converts or waits for
  recovery; normal HTTP, streaming, and WebSocket traffic is proxied to the
  app. The package entrypoint exits if either nginx or the supervised app
  exits. This source candidate still needs Cloudron build and installed-channel
  acceptance.
- The Co-op Cloud recipe now includes the full candidate payload and release
  note, the standard `0.1.0+<app-version>` recipe label, backupbot coverage for
  its retained named volumes, a marker-aware health check sized for migration,
  and stop-first single-writer updates. Failed Swarm updates pause rather than
  automatically starting the old BookshelfNG application against a database
  that may already have migrated. The candidate builder pins its image digest
  and fills the recipe version; real Swarm update, backup, recovery, and
  rollback acceptance remain open.
- The migration guide now gives the one-time manual archive handoff for
  standalone macOS and FreeBSD installs. Their old updater cannot discover the
  first ChaptarrNG release; after migration, the imported updater setting
  follows checksum-verified ChaptarrNG releases.
- ChaptarrNG's updater now selects checksum-verified GitHub releases by default
  for `main` when no `UseGitHubUpdates` choice has been saved. An explicit
  setting still wins, and package-managed installations retain their package
  manager route. The BookshelfNG importer persists the GitHub choice during
  migration; an old BookshelfNG binary still needs a one-time package,
  installer, or archive handoff because it cannot discover ChaptarrNG releases
  through the Readarr update service.

The 2026-10-25 local candidate batch passed the container-platform bundle build
and StartOS TypeScript/ncc build, desktop candidate preparation/build, all 11
legacy-channel contracts, Docker compatibility defaults, the Docker migration
helper, release-readiness and publisher preflights, and the GHCR dispatch
contract. These runs validate source and candidate assembly only; hosted
exact-head and installed-channel acceptance remain open.

The StartOS artifact handoff now has an in-repository builder at
`packaging/scripts/build-bookshelf-startos-artifacts.sh`. It consumes the
release's container-platform bundle and checksum, extracts only its StartOS
candidate, reuses the signing key already held by the external StartOS
workspace, and stages both signed architecture packages with checksum files
and a release manifest. The package key and external `bookshelfng-startos`
repository remain outside ChaptarrNG. Its focused fixture test passed, and the
existing bundle test passed with `npm ci`, the StartOS type check, and the ncc
build. The fixture test uses a placeholder key file and stubbed package tools,
so it checks the handoff and safety rules but does not verify StartOS SDK
signatures or produce valid signed packages. Full bulk validation, real package
generation, device installation, registry submission, backup, update, and
rollback still require their separate acceptance.

## Source comparison completed in this code slice

Source is pinned to BookshelfNG `d42ec2aba1e411f4fb538d4d7201f6930898a91e`.
BookshelfNG `main` has since advanced to `05aec3f127d1d2856e8cd4fbd66e07f89ea01ff3`.
The two intervening commits update the legacy release-bridge workflow/tests and
maintainer documentation; neither changes application runtime code. The target
blob IDs below pin the earlier ChaptarrNG source-comparison checkpoint; the
profile-v2 source update is recorded above.
`implemented-equivalent` means a migrated-profile code path now follows the
observed source contract; bulk acceptance still needs to verify the behavior
on fixtures and installed data.

| Acceptance ID | Contract reviewed | Source path (source blob) | Target path (target blob) | Disposition |
| --- | --- | --- | --- | --- |
| C4-01 | Match release-profile required/ignored terms against the original title and shared author tags; keep rejection permanent. | `ReleaseRestrictionsSpecification.cs` (`a2585901140e1d05f8726b6cc269d181f6823533`) | `ReleaseRestrictionsSpecification.cs` (`7c2a871cc7b1d43648ea70e26915db7d76ef9796`) | `implemented-equivalent` |
| C4-02 | Compare quality before custom-format score and preserve source revision behavior. | `UpgradableSpecification.cs` (`7d536e93cf159131dd9e9e779d558f07aecbca5b`) | `UpgradableSpecification.cs` (`620601565dba5ffb28c8514536c94df50fdb4f15`) | `implemented-equivalent` |
| C4-03 | Use source quality/cutoff behavior without planned-conversion or target split-part filters. | `CutoffSpecification.cs` (`40db4dd2d384ecc6f4a4d2d35bbf7175061a0b12`) | `CutoffSpecification.cs` (`eeafc455aecb34973cb40466e6d0d4fcedd40780`) | `implemented-equivalent` |
| C4-04 | Evaluate queued candidates against source quality rather than planned target conversion. | `QueueSpecification.cs` (`58a6535d46d0b5c63ce197ea0b13c7397bc47a37`) | `QueueSpecification.cs` (`1ef8a0eaf6af0d708e07eaa0487ea3869b102074`) | `implemented-equivalent` |
| C4-05 | Compare RSS history with source qualities and source upgrade rules. | `HistorySpecification.cs` (`9074091c95decf0f474657e647761081e515fa40`) | `HistorySpecification.cs` (`ccbbd2c367e2e9c1adb9e25978033a3399ea6c5d`) | `implemented-equivalent` |
| C4-06 | Keep upgrade-allowed decisions quality-first and compare only compatible file qualities. | `UpgradeAllowedSpecification.cs` (`7692f4363aabaf951108d14d141dca470fd99292`) | `UpgradeAllowedSpecification.cs` (`c8e2fc263e175809f1fc872febbc220ca242c755`) | `implemented-equivalent` |
| C4-07 | Reject an equal-or-better compatible on-disk file using the source preference order. | `UpgradeDiskSpecification.cs` (`d5214d1747816996a99e6cd9aa17ca219973ecae`) | `UpgradeDiskSpecification.cs` (`7f221510f182225259d3621bfee255238b382097`) | `implemented-equivalent` |
| C4-08 | Select delay protocol from the shared author tag set. | `ProtocolSpecification.cs` (`cd9c6a04b5e3083e6c2dac8d24749183eec3bc9e`) | `ProtocolSpecification.cs` (`6765fff4402d77ab3b40729cdb439b96f385ec0f`) | `implemented-equivalent` |
| C4-09 | Select RSS delay rules from shared author tags. | `DelaySpecification.cs` (`a6600ba229e43e9710d3f699c73ee8f301a53a28`) | `DelaySpecification.cs` (`bce8c92f1eff9e3ce5c9a8c6ef5ea0148d43199e`) | `implemented-equivalent` |
| C4-10 | Match indexer tags against the shared author tags. | `IndexerTagSpecification.cs` (`07c01f2cdcbe077fa876f1351ac419ab3f046e8a`) | `IndexerTagSpecification.cs` (`2e3f413891b71f0e095ce652c3531a49b7c381d8`) | `implemented-equivalent` |
| C4-11 | Gate RSS using the legacy author and source-work monitoring fields. | `MonitoredBookSpecification.cs` (`f88178d5b0eacfd569c49bbf6597e30e5612b618`) | `MonitoredBookSpecification.cs` (`ea54afe574b0fb3f7fcd83b4b88dea93c0a37656`) | `implemented-equivalent` |
| C4-12 | Keep minimum-seeder rejection permanent for migrated installs. | `TorrentSeedingSpecification.cs` (`53313042ebada5fc44deac3bccf4590fe6bffeb5`) | `TorrentSeedingSpecification.cs` (`3915c90290083bf74ff321f29b5a9955d74f77fa`) | `implemented-equivalent` |
| C4-13 | Choose pending candidates by source quality/revision first and shared author-tag protocol preference for both formats. | `PendingReleaseService.cs` (`3f2c0973d7ad2d4698cc89730300a8e54ec13d77`) | `PendingReleaseService.cs` (`99f2f47e1328efbee536554f218f6e969b1decc0`) | `implemented-equivalent` |
| C4-14 | Select a download client from the shared BookshelfNG author tags for both media types. | `src/NzbDrone.Core/Download/DownloadService.cs` (`a61b2393ab7730d6307a8d099bcd6b534260f6f6`) | `src/NzbDrone.Core/Download/DownloadService.cs` (`0bd75d9ae29c8747984e0019255fad7d1f8f6218`) | `implemented-equivalent` |

The remaining release-decision specifications were source-compared at the
pinned revision. Each target change below is a logging-level or safe-log
format change; the accepted/rejected decision and reason are unchanged.

| Acceptance ID | Source path (blob) | Target path (blob) | Disposition |
| --- | --- | --- | --- |
| C4-15 | `src/NzbDrone.Core/DecisionEngine/Specifications/AcceptableSizeSpecification.cs` (`5cc9e762ccaa73db305cc9e9d67ac218e306739f`) | same path (`9af8920165c3952835b4f4bf7c2999ab15c97dec`) | `validation-only` |
| C4-16 | `src/NzbDrone.Core/DecisionEngine/Specifications/AlreadyImportedSpecification.cs` (`8dfa646975af5fa862346096c7127bd5e8ce3ad4`) | same path (`0b171be3bb2acfb9be6fd90d32589739a686484e`) | `validation-only` |
| C4-17 | `src/NzbDrone.Core/DecisionEngine/Specifications/BlocklistSpecification.cs` (`8e6349a365d9e5a6bdc749a37330fc3493415104`) | same path (`1a3d6086462f97182cec90257f2930f94a941475`) | `validation-only` |
| C4-18 | `src/NzbDrone.Core/DecisionEngine/Specifications/EarlyReleaseSpecification.cs` (`13984bc28582624700770812a485d38c367a94e6`) | same path (`390c17c4e562b354313da77cf9608608f7fa5a89`) | `validation-only` |
| C4-19 | `src/NzbDrone.Core/DecisionEngine/Specifications/MaximumSizeSpecification.cs` (`6562f1ef8b3685b52bbbb8ab447194f790c02c61`) | same path (`df2c9097e29ef65a7291c05875a185fbdc7e81d0`) | `validation-only` |
| C4-20 | `src/NzbDrone.Core/DecisionEngine/Specifications/MinimumAgeSpecification.cs` (`ec6a050ebbcbf0afdfc4d3ea863e0c6ea25fd80b`) | same path (`e599c8dae571a3feb4cdfc76bd65f249b24e715c`) | `validation-only` |
| C4-21 | `src/NzbDrone.Core/DecisionEngine/Specifications/NotSampleSpecification.cs` (`7695d2c1255264b11a93c7fa7834a618f6e237b9`) | same path (`36df13f4a4eaabbf030bb984d4a66822e4ee6084`) | `validation-only` |
| C4-22 | `src/NzbDrone.Core/DecisionEngine/Specifications/RawDiskSpecification.cs` (`cd6855e51ad5d7752589e9ffc2bf38f0ce09ac9e`) | same path (`0f34e598d6ec91ec3aaaca00a519eeabf9ac91cf`) | `validation-only` |
| C4-23 | `src/NzbDrone.Core/DecisionEngine/Specifications/RepackSpecification.cs` (`1cf9ffa4dce91a31a0c5239ebd141a8f39df15a5`) | same path (`f564dab8582f16693dd7b382b39da0babef3605b`) | `validation-only` |
| C4-24 | `src/NzbDrone.Core/DecisionEngine/Specifications/RetentionSpecification.cs` (`94c7f520079a0910705b437564ea117c65bc523e`) | same path (`005f089cae34178aa0c062d20fcde4db22c063e3`) | `validation-only` |
| C4-25 | `src/NzbDrone.Core/DecisionEngine/Specifications/Search/AuthorSpecification.cs` (`94aa06322855badf008bdc43ec2ebd16b846cdd4`) | same path (`421031c1d6ca1171194fb8dbb9a686234c5eaaf2`) | `validation-only` |
| C4-26 | `src/NzbDrone.Core/DecisionEngine/Specifications/Search/BookRequestedSpecification.cs` (`0351c8e6a76a7b6c03f29c0e9a333270a72e8045`) | same path (`65bcd58c3282f81039e22f3d7d4e11c0e39ff8e9`) | `validation-only` |
| C4-27 | `src/NzbDrone.Core/DecisionEngine/Specifications/Search/SingleBookSearchMatchSpecification.cs` (`849684dba347aa12b6a17179f27bc264445b985d`) | same path (`c0f9814fd1df65ce4fee6bc490d1a3b812b1fb1c`) | `validation-only` |
| C4-28 | `src/NzbDrone.Core/DecisionEngine/Specifications/RssSync/DeletedBookFileSpecification.cs` (`f0ac87d85e6df3d136e4a7115c2d5c7f9b6068d0`) | same path (`757f569bab21a8603209ef899d20e748de8863bb`) | `validation-only` |
| C4-29 | `src/NzbDrone.Core/DecisionEngine/Specifications/RssSync/ProperSpecification.cs` (`ae3809bf9ca6a695d53a30e4c5dee403fd914706`) | same path (`be5b18053215c1f2d44b618e82726e5be54c4c40`) | `validation-only` |
| C4-30 | Suppress a second grab for the same source work even when migration split it into audiobook and ebook target IDs. | `src/NzbDrone.Core/Download/ProcessDownloadDecisions.cs` (`ebba934c1cc3e87b14b4c8fb1287dad80e355e11`) | same path (`7070f324555efc828be020c279cf103cb7a1c7b5`) | `implemented-equivalent` |

## Additional C5 and C6 review slices

| Acceptance ID | Contract reviewed | Source path (source blob) | Target path (target blob) | Disposition |
| --- | --- | --- | --- | --- |
| C5-01 | Preserve known provider implementations, settings contracts, and credentials; map the renamed Readarr import-list provider; block any unknown implementation or meaningful setting before apply. | `src/NzbDrone.Core/ImportLists/Readarr/ReadarrImport.cs` (`7e9a83545d0751276fbe39cbe2922458dfa15bd5`) | `BookshelfDatabaseImporter.cs` (`814a5ba92795834a7006ff80ac38c31969c56843`), `ChaptarrImport.cs` (`93c069f4ec543395f0de59b43f54c5aef01be442`) | `implemented-equivalent` for recognized contracts; unsupported values fail closed |
| C6-01 | Keep native ChaptarrNG API calls on canonical IDs after migration, including the global AJAX client, direct encrypted-backup fetch, bootstrap localization fetch, and native SSE query. Login localization and `/initialize.json` carry no provider IDs and do not require an identity dialect. | `frontend/src/Utilities/createAjaxRequest.js` (`0f025d74c3f4f680f34df3aa65a71525f9def5fc`) | same path (`32896f9d098402458300a7463cac574ce313acaa`); direct-call paths reviewed: `frontend/src/System/Backup/downloadEncryptedBackup.js`, `frontend/src/index.ts`, `frontend/src/Services/SeriesEnrichmentService.js` | `implemented-equivalent` for native request context; user-workflow source review is recorded in C6-02, with runtime acceptance still open |

C5-01 was checked against the pinned source provider inventory: all source
download-client base providers have corresponding target providers; the
Readarr self-import list has an explicit `ReadarrImport`/`ReadarrSettings` to
`ChaptarrImport`/`ChaptarrSettings` alias; source notification implementations
are represented in the target; and source Goodreads OAuth fields plus the
Hardcover API token have explicit migration handling. The importer disables
and blocks unknown provider contracts or meaningful settings instead of
silently dropping them. This does not close auth, webhook, or external-service
runtime acceptance.

## Broader C4–C6 source review dispositions

The following behavior families were compared against the pinned BookshelfNG
source at `d42ec2aba1e411f4fb538d4d7201f6930898a91e`. These are semantic
reviews of the source/target paths, not claims that every external service or
installed workflow has passed runtime acceptance. The detailed user-task
crosswalk remains in [BOOKSHELFNG_WORKFLOW_PARITY.md](../BOOKSHELFNG_WORKFLOW_PARITY.md).

| Acceptance ID | Contract reviewed | Source fingerprints | Target fingerprints | Disposition |
| --- | --- | --- | --- | --- |
| C4-31 | Download selection, adoption, ignored-download event, completion and import lifecycle. Migrated author tags select clients as in BookshelfNG; download matching carries the source author/work context. ChaptarrNG additionally snapshots client files, verifies the expected target set and supports detached conversion/retry. | `DownloadService.cs` (`a61b2393ab7730d6307a8d099bcd6b534260f6f6`); `CompletedDownloadService.cs` (`80118ccba67a2f1c31ce7b667e648992c0f3b57e`); `IgnoredDownloadService.cs` (`b9cd799a8bee04508826995dde460367d91c2c45`) | `DownloadService.cs` (`0bd75d9ae29c8747984e0019255fad7d1f8f6218`); `CompletedDownloadService.cs` (`37ad9980081383f8a59982d0abf9b1ae4a489a63`); `IgnoredDownloadService.cs` (`8c812800c9e673b9187e1345b7eb8a06e2090e94`); `TrackedDownloadAlreadyImported.cs` (`7c177df31f042c191d305faa1302dd66cfdbf24e`) | `validation-only` for the legacy successful path and client-selection contract; retry, strict-verification and crash-recovery behavior still needs same-input fault cases. |
| C4-32 | Local candidate scoring, ordered catalog fallback, remote recheck threshold, ties, close-match threshold, same-file rejection and manual/automatic decision split. The migrated path keeps the BookshelfNG score weights and default minimum of 70, 85% remote recheck, fallback order and source candidate order; target identity/ambiguity guards remain active. | `CandidateService.cs` (`2931adfd9453c2fb9a8d06a90b6571101f46da3e`); `IdentificationService.cs` (`8e0377de5447d5b99c1f5cb30da153ff23a2b4aa`); `DistanceCalculator.cs` (`44834d0a0bb0eed4a830cf650b7e8ea2f6c167b7`); `CloseAlbumMatchSpecification.cs` (`d3aaeda9f4db1ff8ed09b019c20adfb45f788272`); `SameFileSpecification.cs` (`32b8e9e8d42342f3b5fda924cba80b8709209840`); `ImportDecisionMaker.cs` (`d1f22d0fdaa93f4e330c6ce48af7156c9e3d710a`) | `FileMatchingService.cs` (`ca7c3ec07050f12f016a560acb4d0aa8fd5aec1d`); `UnmappedCatalogSuggestionHelper.cs` (`61e56d2a082d4065f910f3cf09fbd0d0084c7755`); `BookshelfMatchCompatibilityScorer.cs` (`0458c32b0882293bb130d0257b9231d557f875db`); `BookshelfSameFileCompatibility.cs` (`cbc03dff9818b5916c31e925544fbac82ca2558d`); `SimpleImportDecisionMaker.cs` (`257114b5da9d9361e4b8c91214b0692de891ce8e`); `ImportOrchestratorV2.cs` (`60b6805354fb9418a6ef6ff96764f9c9859ac6de`) | `implemented-equivalent` for migrated scoring, fallback and same-file contracts; provider/fault and representative-file acceptance remains open. |
| C4-33 | Disk scans, import approval, destination naming, format-root containment, free-space and same-file safety, sidecars and move/copy effects. The mapped target paths preserve normal import outcomes and add staged/contained writes and collision checks. | `DiskScanService.cs` (`ca8327f67e9fe21f0ff4aff7b09837ad759a268c`); `ImportApprovedBooks.cs` (`8fa84834a512aaf5c03778a6b3243675f497c3c5`); `FileNameBuilder.cs` (`1a1d476aa785631fbbb00c821a49380314e3ba5b`); `DownloadedBooksImportService.cs` (`1d259a5de9cc78f4699d0aabe595285e9dd9e90b`) | `DiskScanService.cs` (`6e1664fa9c0d8a2c8bd12fd31789f9d5a92f9141`); `ImportApprovedBooks.cs` (`6da8d5c19ddf328492b17deee83ac1aaef503152`); `FileNameBuilder.cs` (`4323e6a15c09071a531eaa98364d649419551d96`); `DownloadedBooksImportService.cs` (`cfdd658d9514b8b9e8bc168591188f3bee64f3e2`) | `validation-only` for normal filesystem outcomes; real files, mounts, collisions and interruption remain acceptance work. |
| C4-34 | Catalog refresh, supplemental metadata, field preferences, import-list sync and provider-state commit behavior. Current proxy and sync paths were compared with source behavior and the fallback/order contracts in the workflow crosswalk; no additional source-level code gap was found. | `BookInfoProxy.cs` (`03eedae79255edbc9d0d2a8c630a75cd5f2b3cc7`); `AdditionalBookMetadataProxy.cs` (`024643f01466b68903fe5ed72071da6480930510`); `ImportListSyncService.cs` (`dd3e645ad6357f000a52d5d2144c9f0f4ba2b0a2`) | `BookInfoProxy.cs` (`8543c75cafa7c44a2405d4d6540a9acf8d3c1798`); `AdditionalBookMetadataProxy.cs` (`d51dda2aba0b862539b13d6cc7d28e5eec899227`); `ImportListSyncService.cs` (`b31de2154e00bfd71df4f86d220a170c0767953a`) | `validation-only`; live catalogs, provider rate limits, saved credentials and scheduled sync need acceptance. |
| C5-02 | Standard webhook and Notifiarr payload identity and settings/auth compatibility. Mapped migrated book events now expose the source BookshelfNG work ID, while native target-only books retain their ChaptarrNG ID. Source/target provider, config-file, API-key and OIDC paths were also reviewed. | `WebhookBase.cs` (`6b4fd963bb438550d29a1e44b6c371adf1d2da5b`); `WebhookBook.cs` (`c94c02a7df7f0d374884d3923a3e3f25dda5948b`); `Notifiarr.cs` (`16df64ac88d6fc94d047611406e64c0e55fc5cfb`); `ConfigFileProvider.cs` (`ea860905319f4f20c2cbde6ffc22947f86df7da0`); `ApiKeyAuthenticationHandler.cs` (`3c01ae35ad7972b98fd8e399b5d03117be9de2df`); `OidcBackchannelHandler.cs` (`61882ba594f4b348ef47a99d713cc4d52e0755bd`) | `WebhookBase.cs` (`a3ca1f53265b6eeeb5992731130937ba139c4cb5`); `WebhookBook.cs` (`681cb52cd11c7bd6f3b15fcffb7c43919facdbd4`); `Notifiarr.cs` (`95785c5a2e00b07b7811868835a77b626f4518ad`); `ConfigFileProvider.cs` (`4509bd5c07582de655727ea978502f80449c0f81`); `ApiKeyAuthenticationHandler.cs` (`1288c0cb501014db888a7684b26015d2a44ff144`); `OidcBackchannelHandlerFactory.cs` (`66ddd2d6bd87343f74e193eab20f78c07e604273`) | `implemented-equivalent` for saved setting aliases and legacy webhook IDs. Private OIDC issuers remain blocked by default and require the documented explicit allow setting; preserve that access-control boundary. |
| C6-02 | Search/add, bookshelf grouping, unmapped-file retry, security settings and direct/native API request identity. The common client and direct backup fetch retain native IDs; native UI requests carry the native marker, while unchanged `/api/v1` clients use the migration facade. User-task paths are mapped in the workflow crosswalk. | `AddNewItem.js` (`55eb0b44b28aca8603acc91507efed5033e93479`); `Bookshelf.js` (`9e7d0f3535c8cef5bee97c5f122a74812152052d`); `UnmappedFilesTable.js` (`cf4eac2593d3a7b3822dfb7e6b47069db6abce4d`); `SecuritySettings.js` (`2d4cabdeca37c95729b2c0e269abe19516170a9b`); `downloadEncryptedBackup.js` (`45422149336ea5cc8b497656c54e477cdc41a7d8`); `index.ts` (`d3445e4da6cf169cec03aebce945c40e32b26e50`) | `AddNewItem.js` (`cd5fb4f696b7aa80afdc62125272290434ffe0e9`); `Bookshelf.js` (`9c714cb9f065409c724c1b6fb38a163523fe87bc`); `UnmappedFilesTable.js` (`fb4def5075b51d521058b6403632c9457fbc8983`); `SecuritySettings.js` (`430434db67ce1ae8674491d6052c72dd5a190984`); `downloadEncryptedBackup.js` (`d4c574214d191730aaff570e92533cb6fcca691a`); `index.ts` (`2e7987ec9b19fd65f16443a02279b10f851c1bc6`) | `validation-only` for source-level workflow identity and mapped surfaces; loading, editing, mixed-format, mobile/keyboard and migrated-browser acceptance remains open. |

The source-level C4–C6 review queue is closed for these behavior families. The
remaining work is acceptance of same-input outcomes, failure/restart
behavior, live providers and actual installed channels. One documented
authentication compatibility boundary remains: private OIDC issuers need
`BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true`; the migration does not silently
weaken the default issuer-target restrictions.

## Bulk acceptance queue

1. Verify the importer profile in SQLite and PostgreSQL conversions for
   `HARDCOVER` unset, `true`, and `true` with `HARDCOVER_NATIVE=false`. Include
   source databases with bare IDs and already-prefixed IDs. Confirm the source
   backup stays unchanged and duplicate profile keys fail before promotion.
2. On a migrated database, call unchanged `/api/v1` author/book lookup, add,
   list, detail-by-ID, paged list, update, monitor, delete, edition, and book
   file routes with bare Goodreads and Hardcover IDs. Seed a source work that
   split into ebook and audiobook rows; verify it appears once, keeps the old
   ID, resolves the monitored edition on detail/update, combines file counts,
   and does not edit or delete an unrelated work. Check the provider used
   internally and the provider ID projected back to the client. Verify queue
   details, paged queue, history by book, history since, and author history use
   the old book ID in both `BookId` and included `Book` resources, and preserve
   the source `AuthorMetadataId` in included and direct book responses. A rename
   preview for a split work should include both mapped formats with the legacy
   book/file/edition IDs. Confirm `/books/paged` keeps source title ordering,
   one-work totals, and BookshelfNG's pagination clamping for negative offsets,
   zero page size, and sizes over 200.
3. Repeat native frontend/API calls with `X-ChaptarrNG-Client: native`; confirm
   prefixed IDs stay canonical and bare IDs receive native validation errors.
   Confirm the old `/readarr/{hc|gr}/{ebook|audiobook}` and `/ebook` routes still
   scope media type and provider dialect.
4. Post a BookshelfNG-shaped book with exactly one monitored ebook edition and
   one with exactly one monitored audiobook edition. Verify only the selected
   format is added, its monitored edition remains selected, the returned
   resource is legacy-projectable, and no pending-import 202 is emitted. Cover
   missing/multiple selected editions and unavailable author metadata.
5. Verify an unavailable author add returns the legacy error path without
   creating a pending-import row, while native requests retain their existing
   queue behavior.
6. On Debian/PPA, RPM/COPR, and AUR, upgrade active and inactive installed
   BookshelfNG services. Force startup readiness to fail after SQLite and
   PostgreSQL promotion; confirm the newest retained source backup is restored,
   the displaced ChaptarrNG database is retained, the old executable and unit
   return, and systemd restarts BookshelfNG. For PostgreSQL, verify the
   pre-cutover dump covers main/log/cache, rollback restores BookshelfNG
   log/cache state, and the displaced ChaptarrNG log/cache state remains in its
   own verified dumps beside the retained ChaptarrNG main schema. Cover failure
   before database promotion, a missing or invalid backup, and custom
   unit/drop-in setups. Confirm successful readiness clears the marker and
   removes only the old application snapshot, while retaining database
   backups. Inspect the private journal for the expected state sequence,
   outcome records, and fingerprints; confirm a restart after promotion
   advances from observed database lineage without replacing original source
   fingerprints.
7. Start, list, fetch, and cancel commands through the unchanged API address.
   Confirm supported command bodies send and return BookshelfNG IDs, including
   multi-format work IDs. For a `RefreshBook` command targeting a work split
   across both formats, confirm both rows refresh while the command name remains
   `RefreshBook` and its legacy body retains the scalar work ID. Confirm command
   bodies are not mutated in the queue
   when a legacy response is projected. Repeat from a native client where the
   same numeric value is both a source ID and a Chaptarr target ID; the native
   request must keep the target row. Verify mapped book and file changes
   publish `Sync`, then confirm old and native clients reload their own
   projected resources.
8. Run manual-import preview and update with a split work, one ebook path and
   one audiobook path, and the same old Book ID. Confirm the selected target
   row follows the file extension, a submitted legacy Edition ID resolves to
   its mapped edition, mismatched editions are rejected, and nested response
   Book/Edition/AuthorMetadata IDs remain in the BookshelfNG dialect.
9. Fetch release results and save/delete through the bulk book editor on both
   migrated and native API contexts. Confirm release `BookId`, editor response
   IDs, monitoring, and deletion stay scoped to the expected legacy work or
   native target row. For migrated installs, compare audiobook and ebook release
   ordering against BookshelfNG for quality, revision preference, custom format
   score, protocol, indexer priority, torrent health, release scope, age, and
   size; confirm mixed-format and native ordering remain unchanged.
10. Upgrade an active Windows Chocolatey BookshelfNG service. After `/ping`
    succeeds, confirm `/check-bookshelf-migration` validates the target database
    and provider dialect before the installer discards the prior application.
    Force that check to fail and confirm the installer restores the database,
    previous application, service settings, environment, and prior running state.
11. Upgrade a **stopped** Windows Chocolatey service with custom `-data`,
    `-port`, URL base, NSSM `AppEnvironment`, and `AppEnvironmentExtra`. Confirm
    the recovery task and protected manifest are registered before cutover.
    Start the service and verify success removes only the prior app snapshot.
    Force a failed start after SQLite and PostgreSQL promotion; confirm recovery
    restores the source database, prior app, both NSSM environment blocks,
    arguments and start mode, and leaves the service stopped as it was before
    upgrade. Confirm task cleanup and a subsequent Chocolatey retry.
12. From the same AppImage path and Flatpak app ID, verify success and failed
    startup for SQLite and PostgreSQL, plus process interruption after database
    promotion. Preserve default and explicit `-data` paths, config/environment/
    CLI port and URL-base overrides, source backups, and readable recovery
    state. Force process-tree descendants and the launcher to stop during
    health validation; confirm no app/worker process is left behind. Reopen the
    old artifact after rollback and confirm it reads the restored source
    database. Test single-instance locking and retry only after a recorded
    rollback.
13. On migrated fixtures, compare release-profile raw-title terms, quality vs
    custom-format ranking, proper/revision behavior, cutoff, compatible formats,
    RSS/history, delay/protocol/indexer tags, pending queue winner, monitored
    author/work filtering, and permanent minimum-seeder rejection against the
    pinned BookshelfNG source. Confirm all branches are inactive for native
    ChaptarrNG installs.
14. Give one source BookshelfNG work both an audiobook and ebook target mapping.
    Submit equivalent approved decisions for both rows in one RSS pass: the
    migrated profile must grab at most one work and classify any delayed second
    candidate as a fallback. Repeat with compatibility disabled and confirm
    native media rows stay independent. Include an unmapped native target ID
    numerically equal to a legacy source ID to confirm identity namespaces do
    not collide.
15. Re-run the provider conversion fixtures for built-in indexers,
    download-clients, import-lists and notifications; verify the renamed
    Readarr self-import alias, full and partial Goodreads OAuth, Hardcover
    library token, nested unknown settings, and report redaction. Unknown
    meaningful settings must block promotion before source data is changed.
16. On a migrated profile, exercise the source-facing Book/Author search and
    add flows, detail and edit pages, mixed-format monitoring and bulk edits,
    manual import, unmapped-file retry, queues, backup/restore, and Settings
    forms. Verify native API calls retain canonical target IDs, source calls
    retain BookshelfNG IDs, and every direct UI API/SSE request uses the right
    native request marker or intentionally has no provider-ID context.
17. For standard Webhook grab, import, book-added, book-delete and file-delete
    events, confirm a mapped migrated book uses its original BookshelfNG ID,
    the source author and file IDs remain stable, and a target-only ChaptarrNG
    book keeps its target ID. Repeat after a migrated book is split across
    audiobook and ebook target rows.

## Still open after this checkpoint

- C4–C6 source comparisons and the C1–C3 migration implementation are present;
  the remaining application evidence is same-input differential behavior,
  representative operator data, live providers/media, full SeerrNG acquisition,
  and restart/recovery after real user writes.
- The local installed-candidate campaign is recorded in
  [the channel acceptance report](BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).
  It covers Debian, Fedora, Arch, Chocolatey, standalone, Docker, Helm,
  Cloudron, StartOS, Unraid-template, and Co-op Cloud rehearsals. Several are
  local or synthetic and do not prove external publication or actual operator
  feed/device acceptance.
- External delivery remains open for Debian/PPA, Fedora/COPR, Chocolatey, AUR,
  Docker aliases, Helm OCI, AppImage/Flatpak, StartOS, Cloudron, Unraid, and the
  canonical Co-op Cloud recipe. The old in-app BookshelfNG updater cannot be
  redirected from this repository; macOS and FreeBSD standalone installs need
  the documented one-time artifact/path handoff.
- Representative operator data, live provider and production API workflows,
  exact-head hosted checks, and explicit production cutover acceptance remain
  separate gates. Passing local builds or candidate handoffs does not close
  them.

## Operator experience and migration timing

The UI stores a Simple or Advanced experience choice. Migrated BookshelfNG databases default to Simple; fresh ChaptarrNG installs default to Advanced. Verify the global menu and Settings → UI selector persist across restart, Simple mode exposes the routine library workflow and hides advanced navigation and form controls, and switching back restores the full ChaptarrNG navigation.

Legacy package transitions retain automatic migration for unattended upgrades and accept an explicit `ask` or `manual` choice. Verify a declined or manual package choice stops before service or file mutation, and verify desktop first-launch deferral leaves source data untouched during bulk acceptance.

### Experience-control accessibility acceptance

Verify the header mode control is visible at desktop and narrow widths, has a useful screen-reader label for both current mode and next action, responds to keyboard activation, and retains a visible focus indicator. Verify the navigation toggle has a descriptive accessible name and the current destination is exposed with `aria-current="page"`. Verify Simple and Advanced remain selectable and persisted from Settings → UI.

Also verify that the Simple and Advanced choices are individually labelled radio cards with readable descriptions and visible selected/focus states. Use the skip link and confirm focus reaches the main landmark. After route navigation, focus should move to the main landmark unless a modal owns focus. With reduced motion enabled, the route transition should not fade or hide content.

On a narrow viewport, verify the navigation toggle announces its expanded state and controls the named primary-navigation landmark. The closed sidebar must be absent from keyboard navigation; Escape should close an open sidebar and return focus to its toggle. Resize across the mobile breakpoint and repeat outside-click and touch navigation checks, including a swipe that begins at the leftmost screen edge.
