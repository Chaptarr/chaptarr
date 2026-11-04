# BookshelfNG migration fixture evidence

## Expanded schema-158 migration rehearsal (2026-11-04)

The source-built Release Console from application commit `8d902d26` passed a
full preview/apply/check/restore-preview/rollback round trip against the
expanded synthetic BookshelfNG schema-158 fixture. Later commits `b7c542e5`
and `7e9fbcf2` changed tests/docs only; fix commits `7f39b52b` and
`6614d55c` add BookshelfNG book- and author-editor response projections below.
Preview left the
source database hash unchanged. Apply retained a valid source backup and
produced schema 125 with SQLite integrity `ok` and zero foreign-key violations.

The converted target contained four authors, nine books representing six
source works, nine editions, four files, four history rows, three
download-history rows, two blocklist rows, two series, four series-book links,
and 34 legacy-identity mappings. Provider settings were preserved. Synthetic
MyAnonamouse seed
criteria, seed time, discography, blocklist behavior, and ordered DirectDownload
URL settings were also verified in the target.

Migration check and restore preview passed; restore preview left both the
converted target and retained source backup unchanged. Explicit rollback
restored the source database byte-for-byte to its retained backup and kept the
converted ChaptarrNG database separately. The restored source remained schema
158 with clean integrity and foreign-key checks.

The source Release build completed with zero warnings and errors. The full Core
suite passed 4,976 tests, skipped 29, and had no failures; focused book- and
author-response projection tests each passed 1/1. This is synthetic SQLite evidence. It
does not establish representative operator-data acceptance, PostgreSQL
operator-data conversion, live provider fulfillment, real-media recovery, full
API mutation parity, installed-channel acceptance, external delivery, or
production cutover.

## BookshelfNG API write-response regression (2026-11-04)

The public read/write differential on test/docs commit `7e9fbcf2` passed
34/35 cases and exposed a real BookshelfNG compatibility gap in
`PUT /api/v1/book/editor`: the monitoring change persisted, but the ChaptarrNG
response projected prefixed Hardcover IDs and omitted `grabbed=false`.
Commit `7f39b52b` now uses facade-aware book-resource mapping for the mutation
response and applies the legacy identity projection. A focused regression
fixture verifies the source book ID, bare provider IDs, and false grabbed
state.

The focused test passed 1/1 and the full Core suite passed 4,975 tests with 29
skipped and no failures. Exact-head public API differential
[`41919639403`](https://github.com/snapetech-org/chaptarrng/actions/runs/41919639403)
passed 35/35 cases, including both writes and readbacks. It reported 707
additive target properties and 251 provider-presentation differences; these
were not strict legacy-contract mismatches. The full Build
[`41919631600`](https://github.com/snapetech-org/chaptarrng/actions/runs/41919631600)
on `7f39b52b` completed with Docker, PostgreSQL, macOS, and Windows passing,
including the Chocolatey handoff; Linux failed on a stale workflow-policy test
label. Test-only commit `5308d7f9` corrected the assertion, and the local policy
suite passes 21/21. Exact-head public Build
[`41925573068`](https://github.com/snapetech-org/chaptarrng/actions/runs/41925573068)
passed on `6614d55c` across Linux, macOS Intel, Windows, PostgreSQL migrations,
and Docker, including Windows installer upgrade/rollback, selected data
directory, and Chocolatey `.76` handoff checks. The differential does not establish
all 236 shared API method/path contracts, command behavior, provider
fulfillment, or unchanged-client end-to-end parity.

The next public differential, [`41921091303`](https://github.com/snapetech-org/chaptarrng/actions/runs/41921091303),
added legacy author bulk monitoring and passed 36/37 cases. It exposed a
second response-projection gap: `authorMetadataId`, `ebookQualityProfileId`,
and `foreignAuthorId` used target values instead of BookshelfNG values. Commit
`6614d55c` now applies facade-aware author mapping and restores the legacy
metadata ID from `BookshelfLegacyIdentityMap`; a focused controller regression
passes and the full Core suite passes 4,976 with 29 skipped. Exact-head API run
[`41922942122`](https://github.com/snapetech-org/chaptarrng/actions/runs/41922942122)
passed 37/37 cases with zero strict mismatches, including all three writes and
their readbacks. The report recorded 741 additive target properties and 251
provider-presentation differences. The complete route/command contract
remains outstanding.

## Operator-data SQLite conversion and rollback (2026-10-25)

ChaptarrNG `3bfb4b15` passed a Release solution build with zero warnings and
errors, followed by the full Core suite: 4,910 passed, 28 skipped, and none
failed. The built CLI then previewed, applied, and rolled back two separate
private SQLite snapshots from active BookshelfNG installations. The live
services and source databases were not modified.

For each preview, all rows across the 39 imported tables were accounted for:

| Snapshot | Source rows | Transformed | Reset |
| --- | ---: | ---: | ---: |
| First SQLite installation | 145,513 | 142,772 | 2,741 |
| Second SQLite installation | 160,584 | 157,780 | 2,804 |

The first database had six stale `SeriesBookLink` rows pointing to missing
books; the second had three. Preview counted them as resets, and the converted
databases retained every valid link while passing SQLite integrity and foreign
key checks. The first database also had one history event for an unmapped book;
it remains in history with required `BookId=0`. All History, DownloadHistory,
and BookFiles IDs were retained in both targets.

The DownloadHistory totals were 3,904 and 4,049. Of these, 3,440 and 3,585
already had the no-author `AuthorId=0` value in the source snapshots; neither
database had a positive DownloadHistory author ID missing from Authors. The
preview now reports these unassociated rows separately from positive IDs that
cannot be mapped, and apply preserves them with the target-required
`AuthorId=0` sentinel.

Each retained BookshelfNG backup matched its source snapshot across all 40
tables. Restore-preview passed; rollback restored the source snapshots across
all 40 tables and retained the converted ChaptarrNG database separately, with
all 81 target tables matching their pre-restore contents. Restored source and
retained target backups passed integrity checks; source foreign-key results
remained the expected six and three stale links, while both ChaptarrNG backups
had zero foreign-key errors. The full table and row details remain in private
task logs; this record contains no source identifiers, titles, credentials, or
paths.

This closes migration and rollback acceptance for these two SQLite snapshots.
It does not establish PostgreSQL operator-data acceptance, live-provider
fulfillment, complete unchanged-client workflows, representative media
playback, installed-channel update/rollback, external feed delivery, or
production cutover. Release and migration-bridge gates remain closed.

## Explicit utility-mode migration matrix (2026-10-25)

The exact-head Windows installer service round-trip in Build
[40154279131](https://github.com/snapetech-org/chaptarrng/actions/runs/40154279131)
failed while invoking the documented read-only preview. Its ChaptarrNG process
could not resolve `IMigrationController` because the utility host had not
registered a Microsoft `ILoggerProvider`. The utility router also eagerly
resolved `IConfigService`, which constructs the main database; registering the
missing logger alone could therefore have run normal startup migration before
the explicit preview route. The same DryIoc failure was reproduced locally
against the disposable schema-158 fixture.

The fix registers NLog-backed framework logging in utility mode and injects
`Lazy<IConfigService>` into `UtilityModeRouter`, resolving it only for the
post-migration health-check command. This keeps preview and apply composition
from touching the target database before the selected utility route runs. A
composition regression test verifies the config service is not resolved while
constructing the router.

The source-built Release Console then passed the actual CLI flow against three
separate expanded schema-158 fixtures: Goodreads with `HARDCOVER=false`, the
BookshelfNG-default Goodreads mode with both variables unset, and native
Hardcover with `HARDCOVER=true`. For all three:

- Preview exited successfully and the source database SHA-256 was unchanged.
- Explicit apply exited successfully, retained a valid schema-158 backup, and
  produced a schema-124 ChaptarrNG database with `integrity_check=ok` and zero
  foreign-key violations. Each target contained four authors, nine books,
  nine editions, and four files.
- The stored source dialect was `gr` for both Goodreads cases and `hc` for
  Hardcover; the native Hardcover setting remained enabled only in the
  Hardcover case.
- `-check-bookshelf-migration` passed. Restore preview left both the converted
  database and source backup unchanged. Explicit rollback restored the
  source-backup bytes exactly and retained the converted ChaptarrNG database
  separately.

The changed Release build completed with zero warnings and zero errors; the
focused composition test passed 1/1. The full Core suite passed 4,901 tests with
28 skipped and none failed. The isolated PostgreSQL 17 migration/backup smoke
passed all 15 tests. These are local synthetic-data results. They do not prove
live provider fulfillment, representative operator-data acceptance, full
installed-channel rollback, or exact-head hosted validation of the fix.

## Exact-head runtime, media, and distribution candidate batch (2026-10-25)

ChaptarrNG commit `bb4fa11989148563d9f36731f8fb2bb1c09b888f` passed a
source-built migration/API run using the checked-in schema-158 fixture and the
SeerrNG `ReadarrAPI` adapter at clean source commit
`8b6c562f874990e487119ac515a206ff506acf73`. The run verified the unchanged
`/api/v1` entry point, legacy IDs for a split audiobook/ebook work, mapped file
and history reads, the restricted key's capabilities and denial of admin and
unrelated commands, and pending-import create/poll/cancel. All records and API
keys were synthetic; provider-success fulfillment was not exercised.

The Release test assembly from the same ChaptarrNG commit passed all 10 pinned
real-tool audiobook tests using the checked-in FFmpeg, m4b-tool 0.5.2, and
MP4v2 test image. Coverage includes Move/Copy import, unprobeable-part and
conversion-failure fallback, embedded chapter preservation, application restart
recovery, and recovery after terminating the conversion-host process. Media
was synthetic; this does not prove recovery after killing the complete
installed ChaptarrNG host or behavior on representative operator audiobooks.

Candidate delivery contracts also passed locally: 11/11 legacy-channel
contracts; AppImage/Flatpak staging and artifact orchestration; digest-pinned
Cloudron, Co-op Cloud, and StartOS candidate builds (including StartOS typecheck
and bundle); Debian compatibility-package build plus Trixie apt upgrade/downgrade
and canary preservation; Launchpad source-package build; Docker mode/port
defaults; release publisher credential validation and redaction; fail-closed
release gates; GHCR bridge payload and image alias checks; mocked Chocolatey
identity selection; and Helm handoff rendering. These are candidate-code and
synthetic package checks, not publication, external-feed acceptance, or
installed-device update/rollback evidence. Exact-head hosted Build
[40156061844](https://github.com/snapetech-org/chaptarrng/actions/runs/40156061844)
was still running when this record was prepared.

ChaptarrNG remains **not ready for user cutover**. Representative operator data,
successful live-provider request-to-import fulfillment, full unchanged-client
workflow acceptance, and real installed-channel update/rollback on the
remaining supported platforms are still open. Release readiness and the legacy
migration bridge remain disabled.

## Expanded multi-record source-build rehearsal (2026-10-21)

The optional `--expanded-fixture` mode in
[`integration/prepare-bookshelfng-smoke.py`](../../integration/prepare-bookshelfng-smoke.py)
extends the shared BookshelfNG schema-158 fixture without changing the smaller
SeerrNG smoke default. This local Linux publish used the current `main`
worktree at `3094b0ab`, .NET SDK `10.0.402`, and reported ChaptarrNG
`0.9.978.0-Release-chaptarr-dev-chaptarr`. It was a source build, not a public
release artifact. The worktree also carried local uncommitted source edits, so
this is local behavioral evidence rather than exact-head hosted CI evidence.
The expanded fixture helper is now committed in `380454f5`.

**Historical fallback note:** This 2026-10-21 run records the behavior of that
candidate: its two unresolved works were unmonitored in both catalogs. The
automatic fallback changed in ChaptarrNG commit
`5b9801484bb200c2ff1436a9ffccb296e93f243c` on 2026-10-22 to preserve source
book-level monitoring (monitored sources map to both monitored formats;
unmonitored sources remain unmonitored). This fixture report does not test
that later behavior; current evidence is in the
[retirement status](../BOOKSHELFNG_RETIREMENT.md).

The source had 39 tables and passed SQLite integrity and foreign-key checks.
It contained four authors, six works, nine editions, four placeholder files,
two sidecars, four history rows, three download-history rows, two series, three
series links, provider configurations, and fake credentials. The works covered
mixed audiobook/ebook media, selected and alternate editions in both formats,
an audiobook with two narrators, a work with no edition rows, an unselected
mixed-format work, and an unmonitored audiobook. The placeholder files are not
valid media. The fixture values were synthetic and no external provider was
called.

With `HARDCOVER=true HARDCOVER_NATIVE=true`:

- **Preview:** passed. `readarr.db` retained the same SHA-256 before and after
  preview; all source table counts and the logical SQLite dump were unchanged.
  Integrity was `ok`, there were no foreign-key errors, and no stage, snapshot,
  migration-copy, or conversion-backup files remained. The review sidecar was
  generated for the two unresolved works.
- **Apply guard:** running `-migrate-bookshelf` without
  `-confirm-bookshelf-service-stopped` was rejected with the required
  confirmation error.
- **Apply:** passed with explicit stopped-service confirmation. The target
  reached schema 121, passed integrity and foreign-key checks, and contained
  four authors, nine books, nine editions, and four files. The retained
  schema-158 backup had the same logical SQLite dump and all 39 table row
  counts as the source. The mixed-format source work split into format-scoped
  records; the two unresolved works remained unmonitored in both catalogs.
- **Mapped records:** all six provider setting objects retained every source
  field for the fixture indexers, download client, Goodreads list, and BookLore
  notification. Native Hardcover identities, format roots, file paths,
  alternate-edition monitoring, history, series links, profiles, users, tags,
  blocklist rows, and both sidecars were present in the expected target counts.
  `MetadataFiles.Consumer=Readarr` mapped to
  `LegacyAudiobookshelfMetadata`.
- **Restore preview:** passed and left the target and retained source backup
  byte-identical to their pre-preview hashes.
- **Rollback:** passed. The restored `readarr.db` matched the retained
  BookshelfNG backup byte-for-byte, and its logical dump, all 39 table counts,
  schema 158, integrity, and foreign-key checks matched the starting source.
  The schema-121 ChaptarrNG database was retained separately with nine books
  and clean integrity and foreign-key checks.
- **Fail-closed case:** a disposable copy with two monitored audiobook
  editions for one work was rejected before promotion. The source hash and
  schema 158 remained unchanged, integrity and foreign-key checks passed, and
  no stage or backup file remained.

This is stronger synthetic SQLite evidence, not operator-data acceptance. It
does not prove full BookshelfNG feature parity, PostgreSQL operator-data
conversion, provider fulfillment, installed rollback, or update-channel
handoff. The existing release freeze and operator acceptance gates remain in
force; the public latest remains `v0.9.977`, no release or compatibility
bridge was published, and this rehearsal does not approve a BookshelfNG cutover.

## Released BookshelfNG fixture against current ChaptarrNG candidate (2026-10-21)

This rehearsal used the public BookshelfNG `main-v0.4.21.71` Linux x64 archive
and the Linux artifact from ChaptarrNG Build
[39561683854](https://github.com/snapetech-org/chaptarrng/actions/runs/39561683854),
built from commit `c2535e712aa7c1ecbf29347a7e1e9d477bb870e3`. The archive
checksum was verified before the released Readarr binary created and served a
deterministic synthetic Hardcover library. The ChaptarrNG artifact hashes were
`b31763c3c0412fabdf22a7c5c31b3a1b05a884a8d85701055018d74df7fde3cc`
(apphost) and
`07689c53c6c942a67a68248ffca91fb38ca75642b4b0e7e904f088b7912fd8a3`
(`Chaptarr.Core.dll`). Exact-head Build
[39561683854](https://github.com/snapetech-org/chaptarrng/actions/runs/39561683854)
completed successfully on Linux, Windows, macOS, and PostgreSQL; native-runner
receipt reconciliation passed. The release-note contract was skipped for this
internal fix.

The fixture contained linked author/book records, ebook and audiobook editions
and files, an extra file, a metadata sidecar, series, user, notification,
history, download history, blocklist, roots, tag, and settings. It reproduced a
real compatibility gap: `MetadataFiles.Consumer=Readarr` was rejected by
preview/apply. ChaptarrNG commit `c2535e71` now maps that legacy consumer to
`LegacyAudiobookshelfMetadata`; the importer regression fixture covers it.

With `HARDCOVER=true HARDCOVER_NATIVE=true`, preview exited successfully,
reported the sidecar as one retained row with no unsupported consumer, and
left the source database hash unchanged. Explicit apply exited successfully;
the target reached schema 121 with SQLite integrity `ok` and zero foreign-key
errors. It retained 1 author, 2 books, 2 editions, 2 book files, 1 metadata
file, 1 series, 2 series-book links, 2 roots, 1 history row, 1 download-history
row, and 1 blocklist row. The author identity remained `HardcoverAuthorId=hc:9001`,
and the sidecar mapped to `LegacyAudiobookshelfMetadata` with type 1.

The retained BookshelfNG backup's raw SQLite file hash differs from the source
hash because checkpointing/WAL normalization changes the file representation.
Its logical SQLite dump, all 39 table row counts, and schema 158 match the
source exactly; integrity is `ok` and there are no foreign-key errors. This is
logical database equivalence, not byte-for-byte identity.

Restore preview exited successfully and did not change either database. Explicit
rollback exited successfully and restored `readarr.db` to the retained backup's
hash. The restored source database has the original logical dump and row counts,
schema 158, integrity `ok`, and zero foreign-key errors; ChaptarrNG's schema-121
database remains separately backed up. These results establish one successful
released-binary SQLite round trip for synthetic data. They do not establish
representative operator-data acceptance, PostgreSQL operator-data support,
provider fulfillment, complete semantic parity, or installed-channel handoff.

**Reviewed:** 2026-10-21. ChaptarrNG PR #131 merged to `main` on 2026-10-20;
the public release remains `v0.9.977`. This file covers synthetic fixtures and one private
populated migration-158 snapshot tested on a disposable copy. This is
maintainer evidence, not approval to cut over operator data.

**Prior PR #131 application-code validation (2026-10-19):** Code snapshot `0612cb4c90b75818e5349eb868cc0638825de02a` passed exact-head Build [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373): Linux, macOS, and Windows tests, PostgreSQL migration smoke, native-runner receipt reconciliation, and the release-note contract all passed. CodeQL [39239934583](https://github.com/snapetech-org/chaptarrng/actions/runs/39239934583), security [39239940344](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940344), NuGet submission [39239932165](https://github.com/snapetech-org/chaptarrng/actions/runs/39239932165), and dependency review [39239940313](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940313) passed. Disposable-fork validation [39239940512](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940512) was skipped by its external-fork gate. SeerrNG's adapter and request-route suite at source commit `b810153c` passed 4 files / 283 tests. The Linux artifact also passed the earlier empty-library API and authorization smoke. The populated source-build check on later code is recorded below.

**SeerrNG pending-request lifecycle probe (2026-10-19):** On the isolated Linux candidate from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), SeerrNG's real `ReadarrAPI` (`b810153c`) submitted an ebook request for a synthetic work whose author was unavailable. `addBook` received a pending ID, `getPendingAuthorImport` read it, `cancelPendingAuthorImport` cancelled it, and a second poll observed the expected terminal cancellation state: `OverallStatus=Failed` with `LastError=Cancelled by user`. The cancelled record remains queryable rather than being removed. This proves request creation, polling, and cancellation across the adapter/API boundary. It used a disposable database and synthetic IDs; provider-success behavior, retries, availability, history, migrated records, production settings, full SeerrNG operator workflow, and operator acceptance remain open. The tested app artifact is code snapshot `0612cb4c`; PR commit `d9713604` added the repeatable helper and docs, and the intervening app change is Calibre selector tie-breaking rather than a SeerrNG API change. The probe is now repeatable through `integration/seerrng-request-lifecycle.mjs` and is wired after the authorization smoke in `integration/seerrng-smoke.compose.yml`. On 2026-10-19, both scripts passed when run against the isolated artifact from Build 39239940373 and SeerrNG source `b810153c`; `node --check`, Prettier, and Compose configuration validation also passed. The earlier Compose invocation used a temporary runtime image with `--no-build`. A follow-up source-build run on 2026-10-19 built the candidate through the checked-in `Dockerfile.build` from PR head `7cfb5d87` and passed both scripts after the harness prepared its disposable `/books` volume as UID/GID `99:100`. Successful fulfillment, retries, availability/history, converted records, saved operator settings, and acceptance remain open.

## Candidate and fixture

The original populated fixture checkpoint also exercised provider settings
disposition. A matching Hardcover public-list provider was accepted; a Torznab
row with an unknown settings field and unsupported plugin providers were
reported as blockers. At that checkpoint, Goodreads OAuth bookshelf settings,
owned-book import lists, and Goodreads bookshelf/owned-book notifications were
also blockers. The later PR #131 candidate adds those Goodreads compatibility
providers. Importer tests verify complete saved settings are carried forward,
missing or partial OAuth credentials block migration, and reports never
include credential values. Runtime HTTP fixtures cover private-shelf and
owned-book reads plus Goodreads writes; existing-account API acceptance is
still unverified. The populated SQLite snapshot itself contains no real
Goodreads credentials or live provider response. Preview leaves source bytes
unchanged, and apply refuses any remaining unsupported provider before
promotion. Reports contain row IDs, implementation/contract names, and
unsupported field names, never saved values or credentials.

- ChaptarrNG source: merged PR [#131](https://github.com/snapetech-org/chaptarrng/pull/131), merge commit `d2bcba7953a14d2d726f47005a660314cc5e4c26`. The populated rehearsal below is one fixture run; it does not stand in for representative-data acceptance.
- Earlier integrated code snapshot `8b4c6e23` passed Linux, macOS, Windows, PostgreSQL migration, release-note, and runner-receipt checks in Build run [39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341). CodeQL, dependency review, security scan, and NuGet submission also passed. Those hosted checks do not rerun the private populated-data rehearsal or expand its scope.
- Earlier exact-head Build run
  [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646)
  passed Linux, macOS, and Windows builds/tests, PostgreSQL migration fixtures,
  release-note validation, and runner-receipt reconciliation. CodeQL,
  dependency review, dependency/secret scans, and NuGet submission passed on
  the same head. Four Windows media-move fixtures failed in the preceding run
  because they used POSIX-only paths; the current fixtures use native temporary
  paths and pass. Disposable-fork validation was skipped by its gate.
- Documentation-only checkpoint `898b72fd` passed exact-head hosted run
  [39198708193](https://github.com/snapetech-org/chaptarrng/actions/runs/39198708193),
  including Linux, macOS, and Windows, PostgreSQL fixtures, release-note,
  CodeQL, dependency/security, NuGet, and runner-receipt checks. It did not
  change application code. PR #131 later merged; these checks do not authorize
  operator cutover.
- Documentation-only checkpoint `ddec3e81` passed exact-head hosted run
  [39199730309](https://github.com/snapetech-org/chaptarrng/actions/runs/39199730309),
  including Linux, macOS, and Windows builds/tests, PostgreSQL, CodeQL,
  security/dependency, NuGet, release-note, and receipt checks. Disposable-fork
  validation was skipped by its gate. It did not change application code.
- The Linux artifact reports ChaptarrNG `0.9.978.0`. Its `Chaptarr` apphost
  SHA-256 is
  `dfdaae95b9cbd6a4cfe44cfe042f7e6ef87ca60402be00fa8376dc1d9fea1194`; the
  `Chaptarr.Core.dll` SHA-256 is
  `19852117505d35c9f5819ac8a5a2583d3ff01c2e79e99de1cc3fe1e2fd211e52`.
- Source: the published BookshelfNG `0.4.21.71` Linux x64 release archive,
  SHA-256
  `4197d53b757ebe99642fb8f4213f963dd54641401d1f188ab6daadcb3b024d5d`; its
  extracted `Readarr` executable SHA-256 is
  `0d7c82722b1948cc4d33de139698050944b1f4f1647006836364cd9b736e5f6b`.
- The fixture helper started that released binary on loopback, upgraded a
  disposable database to schema 158, and populated linked records. No
  production backup, real credentials, external service, or real media was
  used. Fixture credentials and paths are synthetic.

The fixture contains one author, one book/work, two editions (ebook and
audiobook), two book files, one extra file, one metadata sidecar, one series
and link, one user, one notification, one history row, one download-history
row, one blocklist row, two roots, one tag, and six settings. It uses bare
numeric Hardcover IDs as stored by BookshelfNG native Hardcover mode. The
settings cover a fake Hardcover token, match threshold `75`, supplemental
catalogs, per-field source preferences, and a fake Google Books key. The
source `NamingConfig` table is empty, matching the default-settings case.

## Runtime results

The following checks used separate disposable copies of the fixture and the
Linux artifact from run `39169786430`:

- **Preview:** passed. The SHA-256 of `readarr.db` was unchanged
  (`2e3bd5e423f543e19244b5105d53f2651fc52f2a05523a24f42e040c7961d55b`).
- **Explicit apply:** passed with the required
  `-confirm-bookshelf-service-stopped` option. The target reached ChaptarrNG
  schema 121, passed SQLite `integrity_check`, and had no foreign-key errors.
  One BookshelfNG backup remained at schema 158 with its original author/book
  rows and empty naming-settings table.
- **Naming defaults:** the imported target retained one seeded ChaptarrNG
  naming row (`MultiEpisodeStyle = 0`) when BookshelfNG had no saved row. The
  source importer fixture also verifies that saved BookshelfNG naming settings
  replace the target defaults without leaving a duplicate row.
- **Data and settings:** the single source work became separate ebook and
  audiobook catalog records. Both editions and files remained; the extra
  file, metadata sidecar, series relationship, user, notification, history,
  download history, blocklist, roots, and tag were retained. The native
  Hardcover author ID became `hc:9001`, the book ID became `hc:9002`, and six
  provider alias rows were created. Ebook/audiobook quality and metadata
  profile overrides remained attached to their formats. The saved Hardcover
  credential mapped to `HardcoverApiToken`; the source setting remained in the
  backup. Threshold `75` mapped to `Balanced` as a documented lossy
  approximation, and the supplemental catalog preferences and fake Google
  Books key were retained. BookLore notification settings and triggers were
  retained unchanged.
- **Automatic startup:** passed in a network-isolated container. ChaptarrNG
  converted the source, retained one schema-158 BookshelfNG backup, and
  reached schema 121 with one author and two format-specific book records.
  SQLite integrity and foreign-key checks passed. Startup logs had no error,
  fatal, or author-path-repair failure lines. The API returned the migrated
  author, audiobook, ebook, provider identities, and one file for each format.
- **SeerrNG integration:** `scripts/chaptarrng-integration-smoke.mjs` passed
  against this populated candidate and SeerrNG source commit `0b267d526`. It
  verified status, capability discovery, one audiobook through SeerrNG's
  format-scoped paged-library adapter, and denial of an administrative route
  and unrelated command with the restricted key. The raw ChaptarrNG library
  exposed both the audiobook and ebook records. This did not exercise
  end-to-end SeerrNG request creation, history, pending-import retries, or
  existing operator settings.
- **Current source-candidate smoke:** on 2026-10-18, the same harness passed
  against PR #131 source head `ddec3e8104f02fe15e1885c617cdb621e2f71fc5`
  (application-code snapshot `2c0401fc`) and SeerrNG source commit
  `10f06eab9`. Its isolated Compose run verified capability discovery, the
  empty paged library, and denial of administrative and unrelated command
  routes to the restricted service key. It does not establish populated
  migration acceptance or complete SeerrNG workflows.
- **Restore:** preview did not change the target or source backup. Explicit
  restore returned the database to schema 158 with the original author/book
  rows, passed integrity and foreign-key checks, and retained the schema-121
  ChaptarrNG database separately.
- **Fail-closed cases:** apply without stopped-service confirmation, two
  populated source database candidates, and future schema 159 were rejected.
  Each source database hash remained unchanged and no conversion backup was
  created.

## Docker image-switch and rollback rehearsal (2026-10-18)

A second, isolated rehearsal used the public BookshelfNG `.71` Hardcover image
(`ghcr.io/snapetech-org/bookshelfng:hardcover-v0.4.21.71`, digest
`sha256:fc9c8b5b91808d10cbe66dc2338243b2db79452778640c6963493363c389430b`).
The release fixture helper created a populated synthetic schema-158 database;
the source container reported BookshelfNG `0.4.21.71` and served its author and
book through the API.

An initial Chaptarr candidate container was started without the source image's
`HARDCOVER=true` setting. It promoted the bare numeric IDs into Goodreads
fields. Repeating the same image switch with `HARDCOVER=true` preserved author
`hc:9001`, book `hc:9002`, and editions `hc:9003` and `hc:9004` in the
Hardcover fields. The converted database reached Chaptarr schema 121, passed
SQLite integrity checks, had no foreign-key errors, retained the BookshelfNG
source backup, and served the migrated author plus both format-specific book
records through the Chaptarr API.

With Chaptarr stopped, rollback preview left both database hashes unchanged.
Explicit rollback restored the source schema-158 database byte-for-byte from
its retained backup and kept the converted Chaptarr database as a separate
backup. The released `.71` Hardcover image then reopened the restored source
database and served the author and book API records again.

This exposed a provider-mode handoff hazard in the prior candidate. The current
source change makes migration fail closed if bare numeric IDs are present and
`HARDCOVER` is unset or invalid, and the Docker release workflow now builds
separate softcover and Hardcover compatibility targets for the legacy aliases.
At the time of this 2026-10-18 rehearsal, these changes were source-only and
exact-head hosted checks were still pending. The later exact-head result is
recorded below. This rehearsal used a synthetic fixture and does not establish
behavior on operator data or validate the Unraid template handoff.

These results show the candidate's SQLite preview, apply, automatic startup,
data mapping, refusal, rollback, and populated API paths working together on
this synthetic fixture. They do not establish correct classification of
ambiguous real records, PostgreSQL operator-data safety, behavior for every
historical schema, complete provider/client continuity, end-to-end request
parity, or installation-channel behavior. Do not use this candidate for
operator data. ChaptarrNG `v0.9.977` remains the latest public release; no
release, package, image, or compatibility bridge was published during these
checks.

See [retirement status](../BOOKSHELFNG_RETIREMENT.md), the
[operator migration guide](../MIGRATE_FROM_BOOKSHELFNG.md), and the
[PostgreSQL candidate notes](../BOOKSHELFNG_POSTGRESQL_MIGRATION.md) for the
remaining acceptance gates.

## Legacy BookshelfNG backup ZIP restore candidate

Merged PR #131 adds source-lineage validation that allows the standard restore
flow to stage a recognized BookshelfNG database extracted from a legacy
`readarr_backup_*.zip`. Normal startup then upgrades a disposable copy and
converts it through the existing row-accounted workflow; the archive remains
untouched and the BookshelfNG source database is retained by the converter for
rollback. The previous ChaptarrNG database in `.pre-restore` is removed after
successful migration and restored if conversion fails. A regression fixture
covers restore staging and automatic conversion from an authentic schema-158
fixture. The local test run at this historical checkpoint was blocked before
compilation by ImageSharp's build-license requirement; hosted exact-head
validation passed on code head `6f57d355` in
[run 39211491613](https://github.com/snapetech-org/chaptarrng/actions/runs/39211491613).
Linux reported 4,363 passed, macOS 4,360 passed, and Windows 4,326 passed, with
zero failures on all three platforms. PostgreSQL migration checks passed 4/4.
The first run failed because the synthetic schema-158 restore fixture contained
only its maximum `VersionInfo` row; the migration runner expects the complete
applied-version history. Both restore fixtures now include versions 1 through
158, matching the existing migration fixtures. This is not operator-data
acceptance, and the published `.977` restore path remains unchanged.

## Exact-head provider-mode validation (2026-10-18)

ChaptarrNG PR #131 head `e4db2d461773ea59cc57440d1226fcfcf24ccb58` passed
[exact-head run 39175250100](https://github.com/snapetech-org/chaptarrng/actions/runs/39175250100).
GitHub validated PR merge candidate `724029a27e37458994ee3e5d3d9c4601f3d1c5a2`.
Platform receipts reported:

- Linux: 4,341 passed, 0 failed, 4 skipped, 2 inconclusive.
- macOS: 4,338 passed, 0 failed, 7 skipped, 2 inconclusive.
- Windows: 4,304 passed, 0 failed, 10 skipped, 33 inconclusive.
- PostgreSQL migration fixture: 4/4 cases passed, including normal-startup
  conversion and backup retention.
- Release-note validation and native-runner receipt reconciliation passed.
  C#, JavaScript/TypeScript, Python, and Actions CodeQL, dependency review,
  secret scan, and NuGet submission checks passed. The disposable-fork check
  was skipped by its pull-request policy.

The Linux artifact reports ChaptarrNG `0.9.978.0`; its `Chaptarr.Core.dll`
SHA-256 is
`74089c13ebfa844acb8363a72cf8fc634fc04e5f73d0945fc8d40a785c308e4a`. I built
that artifact into an isolated local ASP.NET runtime image and tested three
copies of the populated synthetic migration-158 database created by the
published BookshelfNG `.71` binary. The original source database hash was
`9861ec1bb7d940841b564a00000ef35de43cac7a84b85ad1b733e1f2824d3ab0`.

- With `HARDCOVER` unset, preview exited with the explicit fail-closed message;
  the source hash and schema version 158 remained unchanged.
- With `HARDCOVER=false`, preview left the source hash unchanged and apply
  mapped author/book/edition/series IDs into Goodreads fields. The converted
  database reached schema 121 and passed SQLite integrity and foreign-key
  checks.
- With `HARDCOVER=true`, preview and apply preserved author `hc:9001`, book
  `hc:9002`, editions `9003`/`9004`, and series `hc:9005` in Hardcover fields.
  Normal startup also converted a separate copy, served `/ping`, returned one
  author and two books across ebook and audiobook formats, retained the source
  backup, and passed integrity and foreign-key checks.
- Restore preview left the converted database unchanged. Explicit rollback
  restored the retained BookshelfNG snapshot byte-for-byte (SHA-256
  `f59219f827571eb7111b3a2067d708e4193dc338e3dce9fffbf1d10d27c6e273`),
  returned to schema 158, and preserved the ChaptarrNG schema-121 backup
  (SHA-256
  `ab57924a7662bbf7a8184b8627ebab9d0d18f53c18d8c693f4093d9d184b4301`).
  Integrity and foreign-key checks passed after restore.

These are hosted candidate checks and local synthetic rehearsals. They do not
establish representative operator-data acceptance, review of ambiguous media
choices, provider API parity, PostgreSQL operator-data rollback, or installer
and update-channel acceptance. ChaptarrNG `v0.9.977` and BookshelfNG `0.4.21.71`
remain the latest public releases; no image, package, release, or compatibility
alias was published or advanced during these checks.

## Populated private SQLite preview, apply, and restore rehearsal (2026-10-19)

An unpublished PR #131 candidate build reporting ChaptarrNG `0.9.978.0` was
tested against a disposable copy of a private populated BookshelfNG
migration-158 SQLite snapshot. The test ran with `HARDCOVER=true`, no network
access, a writable isolated config copy, and a read-only candidate app mount.
The live BookshelfNG service and the original snapshot were not changed.

- **Preview:** migration preview passed; the source snapshot remained
  byte-identical.
- **Apply:** explicit apply passed with stopped-service confirmation. The
  converted ChaptarrNG database reached schema 121 and passed SQLite
  `integrity_check` with zero foreign-key errors. The retained BookshelfNG
  backup remained schema 158 and also passed integrity and foreign-key checks.
- **Restore preview:** passed without changing either the converted database
  or the source backup.
- **Restore:** explicit rollback restored a byte-identical copy of the
  retained BookshelfNG backup and preserved the converted schema-121 database
  as a separate ChaptarrNG backup. The restored source reached schema 158; the
  independent original snapshot hash was unchanged.

The populated source exposed two inconsistent relationship shapes. Files
whose positive `EditionId` did not exist are now preserved as unmapped, with
no guessed book or media type. History rows whose source book disagrees with
the referenced file remain attached to the source book, preserving their
payload while leaving the unsafe file and edition association empty. Ambiguous
multiple-file history matches also remain unlinked and are reported instead
of aborting conversion. Focused regression cases cover these paths.

The focused `BookshelfDatabaseImporterFixture`,
`EditionCatalogResourceFixture`, and
`SystemIntegrationCapabilitiesResourceFixture` suites pass 52/52 locally.
The private library's contents, counts, identifiers, and hashes are omitted.
This validates one populated SQLite candidate path and its rollback, not
semantic correctness for every record. The ambiguous-media review artifact
still requires operator classification. Older real schemas, PostgreSQL
operator data, complete settings/provider and client continuity, feature
parity, and installer/update-channel acceptance remain open. The earlier populated fixture validation used code snapshot `2c0401fc` and passed
exact-head hosted run [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646).
An earlier integrated code snapshot `8b4c6e23` passed cross-platform and
PostgreSQL CI in run [39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341),
but the private populated fixture was not rerun in that workflow. The source
changes remain unpublished, and operator-data, feature-parity,
SeerrNG workflow, and installation-channel acceptance remain open.

## Populated migration-to-SeerrNG source build (2026-10-19)

ChaptarrNG candidate commit `12f290bd` was built from source with the checked-in
`Dockerfile.build`. The Compose harness seeded a disposable named config volume
with the shared populated BookshelfNG migration-158 fixture used by
`BookshelfDatabaseImporterFixture`, plus synthetic `.m4b` and `.epub`
placeholder files in a disposable named library volume. It then started
ChaptarrNG normally, exercising startup conversion rather than a direct test
helper. The target reached schema 121 and the original schema-158 database was
retained as the migration backup. Both databases passed integrity and foreign
key checks. With `HARDCOVER=true`, the target config included
`HardcoverEnabled=true`.

SeerrNG's `ReadarrAPI` at commit `fdc439aa9` read the converted audiobook
`Fixture Audiobook` and ebook `Fixture Book` through the format-specific
facades and checked their Hardcover book identities. The same run exercised
capability discovery, the restricted-key boundary, and the pending ebook
add/poll/cancel lifecycle. It reported ChaptarrNG `0.9.978.0`. The importer
suite passed 43/43; Compose config validation, `node --check`, and Prettier
checks passed. The shared fixture avoids separate, drifting seed data between
the unit tests and integration smoke.

The media files are deliberately tiny placeholders and are not valid M4B or
EPUB samples. Separately, the six-case real-tool importer smoke passed on
candidate `12f290bd` with generated MP3 input and the pinned `m4b-tool` 0.5.2 /
`mp4v2` 2.1.1 toolchain; it checks M4B creation and fallback but does not
represent operator media or embedded source chapters. That test is now run by
the Linux build workflow. Real provider responses, operator records, pre-14
schemas, PostgreSQL operator data, successful SeerrNG fulfillment, installed
rollback, and update channels remain unvalidated. It is local synthetic
evidence; no public image, release, package, or channel alias was published.
PR #131 merged on 2026-10-20; the release freeze remains active.
