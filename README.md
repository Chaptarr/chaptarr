<div align="center">

<img src="Logo/chaptarr.png" width="160" alt="ChaptarrNG logo">

# ChaptarrNG

A Snapetech-maintained Chaptarr fork for audiobook and eBook libraries.

[![License](https://img.shields.io/github/license/snapetech-org/chaptarrng)](https://github.com/snapetech-org/chaptarrng/blob/main/LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/platform/support/policy)
[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/nqFGsGUug2)

</div>

Support ChaptarrNG development on [Ko-fi](https://ko-fi.com/snapetech).

> **ChaptarrNG is beta software.** It is under active development, and breaking changes are possible. Keep regular backups and avoid pointing it at a library you cannot afford to lose.

## What is ChaptarrNG?

ChaptarrNG is Snapetech's maintained fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself an independent Readarr-derived audiobook and eBook library manager. ChaptarrNG keeps that standalone library-management experience while maintaining the request and format APIs used by SeerrNG.

We forked Chaptarr so SeerrNG can rely on a book backend with explicit ebook/audiobook routing and durable handling for imports that Chaptarr queues while preparing author metadata. Maintaining the API contract here lets SeerrNG track a request through that asynchronous work without depending on upstream changes landing first. ChaptarrNG runs on its own and does not require SeerrNG.

The application continues to identify itself as `Chaptarr` through its Readarr-compatible API so SeerrNG can detect it. **ChaptarrNG** is the name of this maintained repository, its container image, and its Unraid template.

For local MCP access, see the [SnapeTech MCP integration guide](docs/integrations/snapetech-mcp.md).

### BookshelfNG migration status

**Status checked: 2026-11-04 UTC.** BookshelfNG `0.4.21.76` and ChaptarrNG
`v0.9.977` remain the latest published releases; the local `0.9.978` candidate
is unpublished. The current code head is `d3a67c58`. Trusted build and
validation jobs may use compatible public GitHub-hosted runners again; private
ephemeral JIT remains available for jobs that need it. See
[`AGENTS.md`](AGENTS.md) for runner and release policy.

The focused public Windows handoff run
[#41785474409](https://github.com/snapetech-org/chaptarrng/actions/runs/41785474409)
passed on `d12798d1`: full .NET tests, installer build and SQLite fingerprint,
service upgrade/rollback, selected data-directory preservation, and the
installed BookshelfNG `.76` to exact-candidate Chocolatey handoff, including
manual defer, injected rollback, and automatic migration.

Full public Build run
[#41789426720](https://github.com/snapetech-org/chaptarrng/actions/runs/41789426720)
at `d12798d1` passed Linux and macOS build/test/artifact jobs, PostgreSQL
migration smoke, and the standard Docker build. Windows exposed a test-only
line-ending mismatch while hashing the JSON source registry on a CRLF checkout.
Commit `d3a67c58` normalizes text line endings before hashing and adds an
LF/CRLF regression test. The full public rerun
[#41792679639](https://github.com/snapetech-org/chaptarrng/actions/runs/41792679639)
passed on `d3a67c58`: Linux, Windows, macOS, PostgreSQL migration smoke, and
the standard Docker build all succeeded. The Windows job also passed the
installer fingerprint, service upgrade/rollback, selected data-directory,
and installed Chocolatey `.76` automatic migration checks.

The current Helm installed handoff
[#41756992768](https://github.com/snapetech-org/chaptarrng/actions/runs/41756992768)
passed on `0583bc8c`: populated automatic migration, Hardcover edition ID and
narrator preservation, health and database checks, retained source backup,
chart rollback Job, and BookshelfNG recovery with matching database, config,
and canary fingerprints. The separate Intel/ARM64 installed macOS acceptance
run [#41761249985](https://github.com/snapetech-org/chaptarrng/actions/runs/41761249985)
passed on `8b26fa3e`, covering No, headless Manual, Yes, health, source backup,
and failed-start rollback.

BookshelfNG remains in service. Representative operator-data conversion and
rollback, live provider and real-media behavior, every installed channel,
external delivery, and operator acceptance remain open. No release, package
feed, image alias, compatibility bridge, or production cutover is authorized.

Migration uses the normal ChaptarrNG application and one standard image for
fresh and migrated installations. The container detects legacy SQLite and
Readarr/Audioarr/Bookshelf PostgreSQL sources, then starts the bounded
migration/rollback supervisor. Every migration requires an explicit provider
mode and fails closed before source mutation without one. The image advertises
both `8787/tcp` for migrated BookshelfNG data and `8789/tcp` for fresh installs.

The 2026-10-31 BookshelfNG source-path audit compared source `1bd7f2dc` with
ChaptarrNG `main` at `fa55ffab` (application source snapshot `bd589312`). It
records 1,174 changed paths, 56 reviewed mappings, 587 dispositions, and 587
paths still open. These are path counts, not missing-feature counts, and do not
establish runtime parity, migration acceptance, or channel continuity. See the
[pinned BookshelfNG consolidation inventory and review](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md#exact-head-source-path-audit--2026-10-31).

The implementation and rehearsal notes below preserve their original dates
and scopes; none alone establishes cutover readiness.

ChaptarrNG [`v0.9.977`](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.977)
is the current published release. It includes the migration and rollback
tools for BookshelfNG SQLite, plus the embedded source migration chain. Current
source adds a Docker appdata handoff helper and a guarded manual Linux
systemd bootstrap. Newly approved Linux x64 and arm64 standalone archives will
include both; the published `.977` archives do not contain these newer paths.
ChaptarrNG `v0.9.974` through the published `.977` initializes new-author
format paths from configured defaults. Current `main` adds the requested
disabled-by-default switch for this behavior; it also preserves the saved
setting and BookshelfNG ebook/audiobook root defaults during migration. The
new switch is source-only and is not in `.977`. The next version bump is not a release: publication is frozen
until the BookshelfNG migration, rollback, feature-parity, and install-channel
acceptance gates are closed. Project/provider maintainer and operator acceptance
was recorded on 2026-10-21; this closes the human sign-off gate, not technical
migration, parity, or install-channel readiness. The merged release and distribution workflows
fail closed unless both `CHAPTARRNG_RELEASE_READY=true` and
`BOOKSHELFNG_RELEASE_READY=true`. They must remain false until every retirement
gate closes. Read the [migration guide](docs/MIGRATE_FROM_BOOKSHELFNG.md)
and [retirement status](docs/BOOKSHELFNG_RETIREMENT.md) before switching.

Once those gates are closed, a pushed `v*` tag matching `package.json` starts
the release pipeline. Main-branch version changes alone do not publish a
release; release tags must point to a commit on `main`.

On 2026-10-21, the current source candidate passed migration, database
rollback, and normal-startup rehearsals against two isolated, populated
BookshelfNG SQLite snapshots. Both startup copies auto-converted to schema
121 and returned `/ping` 200 on their saved ports, 8788 and 8787. Their saved
Forms authentication, required-auth setting, and API keys remained unchanged.
This covers these data sets only; feature parity, live providers, and installed
channel acceptance remain open. See the
[rehearsal record](docs/MIGRATE_FROM_BOOKSHELFNG.md#populated-sqlite-migration-and-restore-rehearsal-2026-10-21).

Current `main` accepts raw SQLite Bookshelf backup uploads with `.db` or legacy
`.xml` filename suffixes; the content must still be SQLite. It also contains the
BookshelfNG migration and installer work, and a combined Bookshelf **Formats**
view that groups audiobook and ebook records only when they share a recognized
work-level provider ID. Each record keeps its own details, progress, and
monitoring; bulk monitoring applies to both formats. Separately, the Books
index's **Both** filter lists audiobook and ebook records as separate rows,
with status, monitoring, and quality profiles scoped to each record's format.
These additions are not in `v0.9.977`. The runtime feature commit is
`c75ecbb37c5708441219887f7180d5ab10ee194d`; current source code is on
[ChaptarrNG `main`](https://github.com/snapetech-org/chaptarrng/tree/main).
Current main also offers announced load errors with Retry across activity and
library lists, Manual Import, File Browser, book-file details and quality
lookup, Calendar, backup lists, Wanted pages, author history, and conversion
settings. Startup errors identify the failed data source and retry failed setup
requests when browser storage is available. The frontend CJS suite passes
122/122 on this source; these changes
are not in `.977`.
It also lets operators preview
eligible existing authors and skip reasons under Settings > Media Management >
Root Folders, then separately confirm filling missing format paths. The command
rechecks current settings, preserves manual paths, queues author refreshes, and
does not move book files. Author profile reset controls are disabled when the
selected root has no configured default, protecting explicit profiles. It
reuses matching local
catalog books during add, preserves an explicitly selected manual edition,
and resolves bare numeric Hardcover/Goodreads IDs through the Readarr facade
before title search. Author edit actions from book rows now open the editor
with the row's audiobook or ebook format. The current UI pass adds accessible
ChaptarrNG branding, a responsive search and sidebar, slate-and-purple dark
colors, and visible keyboard focus. Source-only updates also add visible retry
actions to the Editions and Narrations panels and tighten the SeerrNG service
key: it can read audiobook-scoped narrator comparisons and set a preferred
narrator only with the non-overriding payload. Ebook-scoped narrator access
and unrelated or administrative routes stay denied.
Goodreads series imports now fetch all pages while preserving duplicate
filtering and configured import limits. Focused checks pass author/migration
11/11, configuration persistence 1/1, format-profile refresh 5/5, Calibre
language 4/4, Calibre proxy/series 6/6, Hardcover metadata/search 45/45,
book-file move planning 15/15, Goodreads pagination 5/5, and author-move path
coverage 3/3. All three migration CLI helpers pass Bash syntax, help, and
ShellCheck checks. Manual Import author, book, edition, and quality selectors
explain empty states, retry failed loads, hide stale choices, and block empty
quality submission. Add New provider-search failures announce the error and
offer Retry in search-as-you-type mode; interactive release search does the
same. Narrator discovery errors announce the failure and can be retried without
stale errors during loading. Organize, retag, and convert preview failures show
available error details and Retry. Manual Import hides stale rows and disables
Import after a preview error; File Browser can retry the current directory. Activity lists, author history,
book-edit edition and book-file detail/quality loading, Calendar, backup lists,
Wanted pages, and conversion settings also retry after load errors. The frontend
CJS regression suite passes 122/122 locally. These application
changes are on [ChaptarrNG main](https://github.com/snapetech-org/chaptarrng/tree/main).
Exact-head CI status for current `main` is tracked by the [Build ChaptarrNG workflow](https://github.com/snapetech-org/chaptarrng/actions/workflows/build.yml). Earlier matrix results predate the latest picker changes;
follow the [Build ChaptarrNG workflow](https://github.com/snapetech-org/chaptarrng/actions/workflows/build.yml)
for the exact current head. These source changes remain unpublished.
Current `main` also applies the author freshness policy to book-added events
while retaining the author-scoped folder rescan, even when metadata is skipped
or unchanged. This behavior is present in the current source. See the
[author metadata refresh guide](docs/AUTHOR_METADATA_REFRESH.md). This source
update is not in `v0.9.977`.

The HTTP security controls remain source-only: explicit trusted proxies and
CORS origins, per-client request limits, a bounded body size, query-key
controls, and a restricted SeerrNG key with route-level authorization. Existing
`BOOKSHELF_SEERRNG_API_KEY` values remain accepted only when
`CHAPTARR__AUTH__SEERRAPIKEY` is unset. The restore route retains its separate
1 GiB upload limit. The restricted key's narrator routes require
`mediaType=audiobook`; the preference update also requires
`overrideExisting=false`. See [HTTP security settings](docs/HTTP_SECURITY.md)
and the [SeerrNG integration contract](docs/SEERRNG_INTEGRATION.md).

ChaptarrNG application snapshot `3254c15b5e964dae513c6d16758e8b749a77db4b`
passed exact-head Build
[39387591772](https://github.com/snapetech-org/chaptarrng/actions/runs/39387591772)
on Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. Security analysis, Semgrep CE, Trivy, dependency submission,
and push checks passed; the release-note contract was skipped for that
docs-only source update. The merged code remains unpublished and is not
approved for operator cutover.

The HTTP security controls and regression fixtures entered `main` in
`29852b13`. The first exact-head run on `36623faf` found missing test imports
and an incorrect attribute property. Follow-up `aac32822` fixed the imports;
its Build exposed that request-size metadata is an explicit interface, now
handled in `29852b13`. Exact-head Build
[39394240337](https://github.com/snapetech-org/chaptarrng/actions/runs/39394240337)
passed Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. Security analysis, Semgrep CE, Trivy, and dependency submission
also passed. The release-note contract was skipped because no release was
prepared. These source-only changes are not in `.977`, and the release freeze
remains in force.

The exact-head Linux artifact passed a populated synthetic migration and
SeerrNG smoke: normal startup converted the checked-in BookshelfNG schema-158
fixture to schema 121 and retained an integrity-checked source backup;
SeerrNG read both migrated formats and verified provider identities, restricted
key access, and the pending-import cancel lifecycle. The `.m4b` and `.epub`
files are placeholders, so this does not establish media processing or
operator-data acceptance. See the [current migration evidence](docs/MIGRATE_FROM_BOOKSHELFNG.md#exact-head-artifact-migration-and-seerrng-smoke-2026-10-20).

The exact BookshelfNG inventory, revision pins, path dispositions, and open
review count are maintained in the canonical
[consolidation report](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md).
Path-level records are not proof of behavior or feature parity; no runtime
feature has been approved for retirement.

BookshelfNG audit-tool commit `9fcc0aea` passed Build
[39386658595](https://github.com/snapetech-org/bookshelfng/actions/runs/39386658595).
Its docs-only main head `c63eb848` passed its push and security checks. Unraid
main `296caed2` passed CI
[39387608787](https://github.com/snapetech-org/bookshelfng-unraid/actions/runs/39387608787)
and package-sync, security, and release-note checks. Unraid transition PR
[#5](https://github.com/snapetech-org/bookshelfng-unraid/pull/5) remains draft
at `1261a9f8`; its exact-head build, template, package-sync, dependency,
release-note, Semgrep, and Trivy checks passed. Publication jobs were skipped.
The real Community Applications update/rollback path and representative-data
migration/rollback remain open.

The release freeze remains in force. BookshelfNG issue [#144](https://github.com/snapetech-org/bookshelfng/issues/144)
remains open, and BookshelfNG must stay in service until representative data,
rollback, feature parity, provider workflows, update-channel handoff, and
technical acceptance are complete. Project/provider maintainer and operator
sign-off is recorded; it does not close those technical gates. No release or
compatibility bridge was published.

The BookshelfNG Windows WPF installer still installs BookshelfNG. ChaptarrNG
`main` contains an unpublished transactional WPF handoff installer. It keeps
the existing `Readarr` data directory, asks for provider mode, retains a
verified backup, previews before apply, then switches to the `Chaptarr`
service and checks local health. It supports one PostgreSQL source or both
SQLite databases under the standard `NT AUTHORITY\LocalService` account and
fails closed on custom service accounts or unsupported PostgreSQL settings. A
read-only SQLite `PRAGMA data_version` guard stays open for each source through
preview review and rejects committed changes before apply. Raw database, WAL,
and rollback-journal fingerprints remain diagnostics because SQLite may change
WAL bytes without a committed database update; the rebuildable `-shm` index is
ignored.

The Windows handoff candidate added by merged ChaptarrNG PR #131 uses the
actual installer to preserve the BookshelfNG service identity, data directory,
and verified backup. It uses a read-only SQLite `PRAGMA data_version` guard
through preview review and rejects committed source changes before apply.
Raw file fingerprints are diagnostic and are captured outside the guard
lifetime. PR #131 merged on 2026-10-20 as
`d2bcba7953a14d2d726f47005a660314cc5e4c26`, from code head
`87c57a2bffd25a5ecbda40b9c63fd6d82993c54d`. Exact-head Build
[39374140961](https://github.com/snapetech-org/chaptarrng/actions/runs/39374140961)
passed Linux, macOS, Windows, PostgreSQL migration, release-note, and
native-runner receipt checks. CodeQL, security analysis, Trivy, Semgrep
Community, dependency review, NuGet, and Helm checks passed. Disposable-fork
validation was skipped by its gate. The separate GHAS Semgrep OSS check failed
with 12 SQL-format findings and three fixture-path warnings; those findings
were reviewed and are not being reported as a clean check. These are source
and synthetic checks only; representative operator data, semantic parity,
installed rollback, and installer/update-channel acceptance remain open. The
public release is still `v0.9.977`; the installer remains unpublished, and
BookshelfNG must stay in service until cutover is approved.

Helm has a source-only, fail-closed handoff chart at
[`charts/bookshelfng`](charts/bookshelfng/README.md). It keeps the existing
release, StatefulSet, Service, selector, PVC, and `/config` identities, but no
chart has been published for cutover. A versioned publisher now targets the
existing BookshelfNG OCI chart identity after both release-readiness gates,
the migration bridge gate, and an explicit bridge opt-in pass. Existing Helm
users can keep the same chart source after acceptance; an installed PVC
upgrade, GitOps sync, and rollback still need validation.

The unpublished migration candidate includes BookshelfNG's Hardcover
public-list and reading-status provider, plus compatibility ports for its
OAuth-backed Goodreads bookshelf and owned-book lists and shelf/owned-book
notifications. Complete saved credentials and settings are carried forward;
preview blocks missing or partial credentials for providers that require
OAuth, while a public shelf can still use its numeric Goodreads user ID. This
does not prove Goodreads will accept an existing credential. Goodreads stopped
issuing new developer keys and has announced API retirement, so live provider
behavior remains an acceptance gate. These changes are not in published
`v0.9.977`; keep BookshelfNG in service while the release freeze remains.

Merged PR #131 also adds restore support for supported BookshelfNG backup ZIPs:
the database is staged and the archive remains unchanged. Normal startup runs
the validated conversion and retains the BookshelfNG source database for
rollback. If conversion fails, startup restores the previous ChaptarrNG
database; after success, it removes that temporary `.pre-restore` copy. This
source candidate is not in `v0.9.977` and does not authorize cutover.
Earlier PR #131 code head 6f57d355 passed exact-head Build run [39211491613](https://github.com/snapetech-org/chaptarrng/actions/runs/39211491613) on Linux, macOS, Windows, PostgreSQL migration, and release-note validation.

Current `main` also accepts raw `.db` and `.xml` legacy backup uploads. The
`.xml` suffix is a legacy filename; uploaded content still has to be SQLite.
ChaptarrNG validates SQLite integrity and supported database lineage before
staging the file. This compatibility change is unreleased and is not in
`v0.9.977`.

The current migration candidate also restores BookshelfNG's optional
**Move Extra Files on Rename** setting. It preserves the setting through
migration and backups, and moves only configured loose files when no book
media remains in the old folder. This source change is unpublished and is not
approved for operator cutover.

The retirement candidate also adds portable encrypted `.enc` export and
restore compatible with BookshelfNG's BKSENC01 format. ChaptarrNG's existing
scheduled `.zip.enc` format remains separate. See the
[encrypted backup guide](docs/BOOKSHELFNG_ENCRYPTED_BACKUPS.md); this support
is not in the published release and does not close the migration, parity, or
installation-channel gates.

**Prior PR #131 application-code validation (2026-10-19):** Code snapshot `0612cb4c90b75818e5349eb868cc0638825de02a` passed exact-head Build [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373): Linux, macOS, and Windows tests, PostgreSQL migration smoke, native-runner receipt reconciliation, and the release-note contract all passed. CodeQL [39239934583](https://github.com/snapetech-org/chaptarrng/actions/runs/39239934583), security [39239940344](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940344), NuGet submission [39239932165](https://github.com/snapetech-org/chaptarrng/actions/runs/39239932165), and dependency review [39239940313](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940313) passed. Disposable-fork validation [39239940512](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940512) was skipped by its external-fork gate. SeerrNG's adapter and request-route suite at source commit `b810153c` passed 4 files / 283 tests. The Linux artifact also passed the authorization smoke with an empty paged library. The newer populated source-build smoke is recorded below; neither smoke establishes full workflow or operator-data acceptance.


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
[39292223717](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223717):
Linux, macOS, and Windows suites, PostgreSQL migration smoke, release-note
contract, and native-runner receipt reconciliation passed. Linux also passed
the pinned-tool M4B merge and fallback smoke. CodeQL
[118195871399](https://github.com/snapetech-org/chaptarrng/runs/118195871399),
security and secret scan
[39292223457](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223457),
dependency review
[39292223396](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223396),
and NuGet submission
[39292213603](https://github.com/snapetech-org/chaptarrng/actions/runs/39292213603)
passed. Disposable-fork validation was skipped by its external-fork gate.
This validates application code only. PR #131 later merged on 2026-10-20;
representative data, semantic parity, installed rollback, and channel
acceptance remain open. No release or bridge was published.

**Candidate code CI (2026-10-19):** ChaptarrNG PR #131 code commit `12f290bd` passed [Build 39256254892](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254892): Linux, macOS, Windows, PostgreSQL migration, release-note, and native-runner receipt checks passed. CodeQL, [security scan 39256254713](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254713), [dependency review 39256254710](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254710), and [NuGet submission 39256277391](https://github.com/snapetech-org/chaptarrng/actions/runs/39256277391) passed. Disposable-fork validation was skipped by its external-fork gate. This is source validation, not migration or release acceptance.

**SeerrNG pending-request lifecycle probe (2026-10-19):** On the isolated Linux candidate from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), SeerrNG's real `ReadarrAPI` (`b810153c`) submitted an ebook request for a synthetic work whose author was unavailable. `addBook` received a pending ID, `getPendingAuthorImport` read it, `cancelPendingAuthorImport` cancelled it, and a second poll observed the expected terminal cancellation state: `OverallStatus=Failed` with `LastError=Cancelled by user`. The cancelled record remains queryable rather than being removed. This proves request creation, polling, and cancellation across the adapter/API boundary. It used a disposable database and synthetic IDs; provider-success behavior, retries, availability, history, migrated records, production settings, full SeerrNG operator workflow, and operator acceptance remain open. The tested app artifact is code snapshot `0612cb4c`; PR commit `d9713604` added the repeatable helper and docs, and the intervening app change is Calibre selector tie-breaking rather than a SeerrNG API change. The probe is now repeatable through `integration/seerrng-request-lifecycle.mjs` and is wired after the authorization smoke in `integration/seerrng-smoke.compose.yml`. On 2026-10-19, both scripts passed when run against the isolated artifact from Build 39239940373 and SeerrNG source `b810153c`; `node --check`, Prettier, and Compose configuration validation also passed. The earlier Compose invocation used a temporary runtime image with `--no-build`. A follow-up source-build run on 2026-10-19 built the candidate through the checked-in `Dockerfile.build` from PR head `7cfb5d87` and passed both scripts after the harness prepared its disposable `/books` volume as UID/GID `99:100`. Successful fulfillment, retries, availability/history, converted records, saved operator settings, and acceptance remain open.

**Populated migration-to-SeerrNG source smoke (2026-10-19):** ChaptarrNG candidate `12f290bd` built from the checked-in `Dockerfile.build` and converted a disposable, populated BookshelfNG migration-158 SQLite fixture during normal startup. The fixture is shared with the importer tests and includes one author, one mixed-format book, an audiobook edition, an ebook edition, and tiny synthetic `.m4b`/`.epub` placeholders. ChaptarrNG retained and validated the schema-158 source backup, converted the target to schema 121, and persisted `HardcoverEnabled=true` from the source's `HARDCOVER=true` mode. SeerrNG's real `ReadarrAPI` at commit `fdc439aa9` then read both migrated audiobook and ebook records, checked their Hardcover identities, verified the restricted service-key boundary, and completed the pending add/poll/cancel lifecycle. The local run reported `0.9.978.0`; importer tests passed 43/43, `node --check`, Prettier, and Compose configuration validation passed. This closes the earlier empty-library-only smoke gap for this synthetic migration path. The fixture's `.m4b`/`.epub` files are placeholders; a separate real-tool importer check passed six synthetic multipart M4B cases on candidate `12f290bd`, and Linux CI now runs that check in a pinned tool image. It verifies synthetic merge output and fallback. Later PR #131 tests verify embedded-chapter preservation during Move/Copy imports and TagLib writes; representative audiobooks and restart/resume remain unverified. Private operator records, pre-migration-14 schemas, PostgreSQL operator data, live provider responses, full SeerrNG workflows, installed rollback, and release/channel handoff remain open. PR #131 merged on 2026-10-20, but no release or bridge was published and the freeze stays in force.

Until those gates close, the published converter is for read-only preview
only. Do not apply a conversion or start ChaptarrNG against BookshelfNG's
appdata: normal ChaptarrNG startup automatically converts a supported source
database. Keep BookshelfNG in service.

Current ChaptarrNG source stages and validates a merge when both
`readarr.db` and `audioarr.db` contain data; it preserves both originals and
stops before promotion if their records or settings conflict. The published
`v0.9.977` converter does not include this newer dual-database behavior. Keep
independently operated BookshelfNG installations separate. To combine their
tracked media into one library, use BookshelfNG's
[two-instance consolidation runbook](https://github.com/snapetech-org/bookshelfng/blob/main/README.md#one-instance-for-ebooks-and-audiobooks)
first; database history, profiles, monitoring, and settings are not merged.

The source migration runs on a disposable copy and keeps the original database
for rollback. A populated synthetic v22 SQLite fixture upgrades through the
embedded chain, converts, and rolls back; preview leaves the source unchanged,
and a future schema is rejected. The published `.977` release currently
rejects pre-migration-14 databases because their `Blacklist` table must first
be renamed by the embedded chain. The version-aware fix and authentic
migration-1 fixture passed the hosted Linux, macOS, Windows, and PostgreSQL
migration checks in PR #126 and are on `main`, but are not in public `.977`.
The synthetic v22 fixture does not establish real-data behavior or every
historical release. A private populated migration-158 SQLite snapshot was
also tested against public `.977` on 2026-10-18. That structural round trip
did not validate provider identity: `.977` maps bare numeric IDs to Goodreads
even when the source uses native Hardcover mode. Do not apply `.977` to a
native Hardcover BookshelfNG database.

On 2026-10-19, the unpublished PR #131 candidate (build version `0.9.978.0`)
passed preview, explicit apply, rollback preview, and explicit rollback against
a task-scoped copy of populated migration-158 operator data with
`HARDCOVER=true`. The live BookshelfNG service remained running; every
candidate command targeted the separate copy, so this does not validate an
installed service handoff. Migration preview left its source copy unchanged.
Apply produced a schema-121 ChaptarrNG database and retained the schema-158
source backup; rollback preview left the converted database and source backup
unchanged. Explicit rollback restored a byte-identical copy of the source
backup and preserved the converted database separately. Integrity and
foreign-key checks passed on the converted database, source backup, and
restored source. The private baseline snapshot remained unchanged. Unresolved
books were retained in both catalogs as unmonitored and recorded in the private
review sidecar; their formats still need human review. The provider records in
the snapshot passed migration compatibility checks with no blockers; this does
not test live provider calls. With the source's native Hardcover mode enabled,
all migrated author, book, and edition records retained Hardcover identities;
none were assigned Goodreads IDs. This confirms one populated SQLite round
trip for the candidate. Older schemas, PostgreSQL operator data, full feature
parity, and install channels remain open.

The retirement candidate now also carries BookshelfNG's legacy
`AudiobookshelfMetadata` OPF writer as Chaptarr's distinct
`LegacyAudiobookshelfMetadata` provider. It maps both legacy provider and
sidecar IDs while preserving rows only for an exact `NullConfig` contract.
The current source additionally maps legacy
`MetadataFiles.Consumer=Readarr` author sidecars to that consumer; importer
fixtures and the released-binary synthetic rehearsal cover the mapping. The
public `.977` release does not include it. Unknown metadata providers and
sidecar consumers still block apply. Representative operator libraries and
release readiness remain unverified.

A fresh copy of the restored database also opened in the BookshelfNG
`hardcover` image on a Docker network with no external access; its configured
`/ping` endpoint returned HTTP 200. This verifies BookshelfNG can reopen the
restored database, not the complete library workflow or an installed image
switch.

The `.978` Linux app artifact from Build [39267876585](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876585)
also started against a fresh copy of the converted schema-121 database, using
the published `.977` runtime image, empty scratch mounts for the configured
roots, and an internal-only Docker network. Its `/ping` endpoint returned HTTP
200. This verifies candidate app startup with migrated data; it does not test
the candidate image packaging or a live image switch.

The candidate importer preserves files that reference a missing edition as
unmapped and keeps conflicting history rows attached to their source book
without an unsafe file or edition link. Focused importer, edition-catalog, and capability tests pass 52/52
locally. The earlier Windows run [39195460843](https://github.com/snapetech-org/chaptarrng/actions/runs/39195460843)
found four media-move fixtures using POSIX-only paths; code snapshot
`2c0401fc` changed those fixtures to native temporary paths and passed exact-
head run [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646).
Earlier integrated candidate code snapshot `8b4c6e23` includes current ChaptarrNG
`main`, the Editions and Narrations tab, and the series narrator comparison.
Exact-head Build run [39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341)
passed Linux, macOS, Windows, PostgreSQL migration, release-note, and native-
runner receipt checks. CodeQL [39205449712](https://github.com/snapetech-org/chaptarrng/actions/runs/39205449712),
dependency review [39205452346](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452346),
security scan [39205452337](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452337),
and NuGet submission [39205449409](https://github.com/snapetech-org/chaptarrng/actions/runs/39205449409)
also passed. Disposable-fork validation was skipped by its gate. Local
translation, type, targeted ESLint and Stylelint checks, and release-note
preview pass. These are candidate checks, not production cutover approval;
operator-data and channel acceptance remain open. The workflow uploaded
validation artifacts only and published no release. Docs-only checkpoint
`ddec3e81` passed exact-head run
[39199730309](https://github.com/snapetech-org/chaptarrng/actions/runs/39199730309),
including all three platform builds, PostgreSQL, CodeQL, security/dependency,
NuGet, release-note, and receipt checks. It did not change application code.
Merged ChaptarrNG [PR #131](https://github.com/snapetech-org/chaptarrng/pull/131)
contains older SQLite and one-schema PostgreSQL migration support, native
Hardcover identity handling, BookOrbit Book Dock, SeerrNG edition-catalog
compatibility with the new Editions and Narrations book-details tab, and
previewable media moves. The copy-only populated SQLite
rehearsal, synthetic provider-mode and startup checks, and rollback evidence
are recorded in the
[migration fixture report](docs/maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).
Earlier SeerrNG Compose smokes checked the restricted API key with an empty
library and with populated synthetic records. The current source-built,
populated migration-158 smoke is recorded above and in the
[SeerrNG integration guide](docs/SEERRNG_INTEGRATION.md); it adds normal-startup
conversion and reads both migrated formats through SeerrNG. It remains
synthetic evidence and does not establish operator-data acceptance or complete
SeerrNG workflows.
Keep the release freeze in place. See the [PostgreSQL migration candidate
notes](docs/BOOKSHELFNG_POSTGRESQL_MIGRATION.md).

Books with no recorded format remain in both ChaptarrNG catalogs, unmonitored,
with a private review file so the operator can classify them after startup.
The helper does not copy external media/download folders, translate every
external setting, or update pinned images and package channels. The legacy
bridge remains disabled. The local AUR BookshelfNG-identity `.977` candidate
passed an isolated, network-disabled pacman upgrade from the public `.71`
package and downgrade back, preserving an environment override and synthetic
data canary. Its installed package also passed preview, conversion, restore,
and SQLite integrity/foreign-key checks against a synthetic populated
migration-158 database. Systemd was not running in that package-only rehearsal.
A separate network-isolated Arch systemd rehearsal upgraded the active `.71`
service; ChaptarrNG started and converted the database, then restore and
downgrade returned to active BookshelfNG. This uses synthetic data, and the
network-online wait was masked inside the test container. Acceptance with
operator data remains open; see the [package status](packaging/README.md).
The Fedora RPM bridge candidate also passed a `.71` to `.977` package
transaction and downgrade with config/data canaries. Its `systemctl` scriptlet
checks were mocked, and the current legacy COPR repo contains no package
entries, so live DNF/COPR and real-systemd RPM acceptance remain open.
ChaptarrNG PR #127 merged to `main` as `b3089a39`,
adding selectable supplemental catalogs, per-field metadata preferences, and
exact-identity recovery for unmapped files. It is not in public `v0.9.977`;
provider/settings behavior on converted operator data still needs acceptance.
The current converter retains `BookImportMinimumMatchPercent` and maps it to
the closest `BookMatchingStrictness` mode: 50–66 to Aggressive, 67–82 to
Balanced, and 83–99 to Strict. The original number stays in the imported
config and the migration report names the translation. It is an approximation:
the two applications use different fuzzy parsing and import-scoring
algorithms. An explicit ChaptarrNG strictness value takes precedence, and an
invalid legacy value stops conversion. PR #129 merged to `main` as `949ae3dd`
with the opt-in M4B merge settings, tagged track order and chapter titles,
original-track fallback, and move/copy source handling. Its clean head passed
hosted platform and database checks. The current retirement candidate also
passes a tool-enabled synthetic two-track importer smoke through the real
converter: it checks track order, chapters, metadata, database registration,
Move-mode cleanup, and Copy-mode source retention. The six-case real-tool test
also verifies fallback to separate original-track imports in both modes when a
source cannot be probed, and when `m4b-tool` fails after a controlled FFmpeg
conversion error. Linux CI runs these six cases in a pinned tool image. This
is synthetic evidence, not representative-media acceptance, and neither source
change is in public `.977`. Representative M4B media, restart/resume, full feature parity, and several package-channel upgrades remain unverified.

The same release-frozen retirement candidate adds format-aware media moves
through `/api/v1/author/media-move`: preview and confirm a single author or a
bulk selection, review registered sidecars and destination conflicts, then
queue the move. Single-author and bulk command-line wrappers use the same
preview-token flow; see the [media-move guide](docs/MEDIA_MOVES.md). Only files
registered in ChaptarrNG move; untracked files and
author-wide extras stay in place. The candidate includes regression coverage
for format isolation, shared-root and format-root sidecars, traversal,
untracked-file safety, conflicts, and stale sources. Exact-head run
[39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646)
passed on earlier head 2c0401fc, including Windows tests after its native-path fixture
fix. The feature remains unpublished and representative-library, cross-device,
partial-failure rollback, and channel acceptance remain open.

The merged retirement source also ports BookshelfNG's native Hardcover
metadata client and maps a saved `HardcoverAuth` setting to
`HardcoverApiToken`. It preserves the existing `HARDCOVER_AUTH` and
`HARDCOVER_API_KEY` environment options and routes Hardcover work, author, and
edition IDs through the native provider when the BookshelfNG runtime predicate
is true: `HARDCOVER=true` and `HARDCOVER_NATIVE` is not `false`. An unset or
non-true `HARDCOVER` value, or `HARDCOVER_NATIVE=false`, selects Goodreads.
The importer persists that effective source dialect because bare numeric IDs
do not identify their provider. Keep both source settings through preview and
migration. This provider and migration handling are source-only and are not in
`.977`; the published `.977` importer maps bare numeric IDs to Goodreads even
if `HARDCOVER=true` is present. Exact-head run
[39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646)
passed on the earlier PR #131 code snapshot 2c0401fc. An earlier integrated code snapshot
8b4c6e23 passed platform and PostgreSQL validation in run
[39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341),
but the merged source remains unsupported for operator data until semantic review,
representative conversion and rollback,
feature parity, and channel acceptance are complete. PR #131 merged on
2026-10-20; the release freeze remains active.
See the [retirement status and channel matrix](docs/BOOKSHELFNG_RETIREMENT.md)
for the current release and acceptance gates.

ChaptarrNG is independently maintained and is not affiliated with the Servarr team or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects.

The backend is built on .NET 10 LTS. Docker images include the ASP.NET Core
runtime, while source builds require the .NET 10 SDK.

### What makes this fork different

- **SeerrNG contract:** The current candidate advertises provider IDs, format routes, paged library scans, pending-import operations, and `editionCatalog` through `GET /api/v1/system/capabilities`. Its `GET /api/v1/book/{id}/editions/catalog` route returns narrator, monitoring, ownership, and conservative audiobook-description fields for edition cards, matching the new BookshelfNG SeerrNG catalog behavior. This edition-catalog addition is not in public `v0.9.977`; release and migration gates remain closed. Both format services can use one ChaptarrNG instance while requests retain the selected format and provider identity.
- **Pending-import integration:** Chaptarr's current development branch also has pending author imports. ChaptarrNG exposes that asynchronous work through the SeerrNG capability contract and keeps retry/cancel behavior safe when multiple requests share an import.
- **Narration and edition choices for SeerrNG:** `GET /api/v1/book/{id}/editions/catalog` lists every known edition with narrators, solo or full-cast, abridged or unabridged, length, language, and library state, and `GET /api/v1/series/narrators/matrix` compares narrators across every volume of a series, including editions that are not monitored. The existing wanted-edition endpoint tracks a second performance alongside the first. Unstated details stay unknown. See the [SeerrNG integration guide](docs/SEERRNG_INTEGRATION.md#narration-and-edition-choices).
- **Restricted service access:** ChaptarrNG `0.9.941` and later can use a SeerrNG-only API key for supported book requests, searches, library status, and pending imports without granting global administration or destructive access.
- **Standalone additions maintained here:** An optional built-in indexer and download client can search configured ebook sources, use API-key downloads, and optionally fall back to browser-assisted links. Full backup archives can use passphrase-based authenticated encryption; the direct Chaptarr project separately offers encryption for quick-start settings backups.
- **Safer list and file workflows:** Preview an import list's next sync, inspect durable per-list sync history, and review manual-import matching evidence. Book-file downloads are limited to configured roots; imports check destinations before writing, extract supported archives, and can merge or convert multipart audiobooks with progress and cancellation. New multi-part releases can remain as separate book files. See [multipart audiobook merging](docs/AUDIOBOOK_M4B_MERGING.md) for the legacy settings and release limits.
- **Cross-format library views:** The Books index can show audiobooks and ebooks together, labels each record by format, and keeps monitoring actions scoped to that record. Bookshelf's **Formats** view groups records only when they share a recognized work-level provider ID; book details link formats by the same safe identity. These additions are not in published `v0.9.977`.
- **More metadata and library connections:** RanobeDB adds light-novel ebook metadata; Audible searches can use a selected marketplace. Metadata profiles can rank edition terms and choose preferred series. AudioBookShelf, Calibre, Grimmory, and BookLore BookDrop provide optional library connections; BookOrbit Book Dock is in the unpublished retirement candidate; Emby/Jellyfin can refresh after an import.
- **Search and library maintenance:** Indexers can run optional recurring searches for missing monitored books. MyAnonaMouse health reports explain when stale account status blocks grabs. Operators can preview and backfill missing author root folders without moving files.
- **Operations and automation:** An optional MQTT gateway publishes library snapshots and lifecycle events, including Home Assistant discovery. A separate Prometheus listener exposes low-cardinality metrics. The full-library API supports paging, large library views use paged reads, and long author deletions run in the background.
- **Localization:** The interface and help text have catalogs for 12 additional languages, including Indonesian and Traditional Chinese, with English fallback for untranslated strings.
- **Fork-owned distribution:** ChaptarrNG publishes its own images and maintains an Unraid package. Its [fork feature guide](docs/FORK_FEATURES.md) compares these additions with the parent project.

The [fork feature guide](docs/FORK_FEATURES.md) covers the additions and their
limits. Dedicated guides explain [import-list preview and history](docs/IMPORT_LIST_SYNC.md),
[media moves](docs/MEDIA_MOVES.md), [author metadata refresh](docs/AUTHOR_METADATA_REFRESH.md),
[external library connections](docs/EXTERNAL_LIBRARY_INTEGRATIONS.md),
[MQTT and canonical events](docs/MQTT_GATEWAY.md), and
[Prometheus metrics](docs/PROMETHEUS_METRICS.md). The
[changelog](CHANGELOG.md) records changes by released version.
The feature guide describes the current source checkout; some newer work may
not be included in the latest published image yet.

The [ChaptarrNG retirement matrix](docs/FORK_FEATURES.md#chaptarrng-retirement-blockers)
defines the cumulative upstream gate: every BookshelfNG feature carried over
and every ChaptarrNG feature introduced or maintained through `v0.9.977`
blocks retirement as a whole and remains an individual acceptance gate.
Carrying over a BookshelfNG capability adds to this set; it does not replace
or reduce ChaptarrNG's own blockers.

See the [SeerrNG Bookshelf backend guide](https://github.com/snapetech-org/seerrng/blob/main/docs/using-seerr/bookshelf-backend.md) for setup and the integration contract.

## Key Features

### Audiobooks
* **Narrator Aware** - Can help recognize and organize your books with narrator info
* **Format-scoped books and requests** - Track ebook and audiobook records
  separately while one ChaptarrNG instance serves both; SeerrNG can route
  each request to its matching format
* **Publisher Aware** - Special handling for dramatized audiobooks and multi-part releases
* **Audio Formats** - Handles M4B, MP3 chapters, and multi-file audiobooks
* **MP3 → M4B Conversion** - Convert new downloads or books already in the library into a single chaptered M4B, with chapter preservation or insertion if missing. Conversion shows progress and can be cancelled (powered by [m4b-tool](https://github.com/sandreas/m4b-tool))
* **Multipart Audiobooks** - Import numbered parts side by side with `{ReleasePart}` and `{ReleaseParts}` filename tokens, or optionally merge eligible downloads into one M4B after checking the final destination

### Organization
* **Dual Media Libraries** - You can have separate root folders for audiobooks and eBooks or choose to have your eBooks placed alongside your audiobooks
* **Format-specific author paths** - When enabled, the default-off setting initializes missing audiobook and ebook roots for new authors; explicit roots and existing authors or files remain unchanged
* **Matching** - Match audiobook and eBook files using tags, folders, and release metadata
* **Series Management** - Automatically organize books by series
* **Metadata Profiles** - Control allowed languages, rank preferred edition terms, and choose the series name used in filenames and generated metadata. Stronger match evidence remains authoritative.
* **Genre Filtering** - Exclude configured genres, such as comics or graphic novels, through metadata profiles
* **Flexible Renaming** - Customizable file naming with audiobook-specific tokens

### Integration & Automation
* **Standard *arr Integrations** - Works with the usual *arr-family download clients and indexer protocols
* **External Libraries** - Connect AudioBookShelf, Calibre Content Server, and Grimmory for supported refresh, import, metadata, cover, and edit-forwarding workflows; see the [integration guide](docs/EXTERNAL_LIBRARY_INTEGRATIONS.md) for the direction and limits of each connection
* **BookLore and BookOrbit** - BookLore BookDrop is an optional external connection, not a ChaptarrNG dependency; BookOrbit Book Dock support is in the unpublished retirement candidate and is not in `v0.9.977`. See the [integration guide](docs/BOOKLORE_INTEGRATION.md) for setup, file handling, and the explicit legacy-settings handoff.
* **Emby and Jellyfin** - Refresh the media-server library after a grabbed release is imported
* **MQTT and Webhooks** - Publish snapshots and canonical lifecycle events to MQTT, with optional Home Assistant discovery and opt-in canonical events for existing webhooks
* **Import Lists** - Preview the next sync and review durable per-list history before relying on automatic-add behavior

### Quality & Upgrades
* **Automatic Upgrades** - Replace lower quality versions automatically
* **Profile Flexibility** - Create custom quality profiles for different libraries

## Metadata

ChaptarrNG does not use Readarr's metadata-provider plugin protocol. It uses
Chaptarr's modular pipeline to resolve entities across metadata providers and
aggregate their data through automated refinement and consensus. The current
source checkout also includes optional Open Library, Google Books, Library of
Congress, Gutendex, Internet Archive, NDL, Europeana, and Apify catalogs, plus
per-field source preferences. These additions are not in public `v0.9.977`.
See the [supplemental metadata guide](docs/SUPPLEMENTAL_METADATA.md) for
configuration and provider identity details.

For light-novel eBooks, ChaptarrNG can search RanobeDB explicitly and store its
work and release IDs alongside the normal eBook records. RanobeDB's public API
is currently v0 and is for non-commercial use; ChaptarrNG applies a conservative
local request throttle below the API's 60-per-minute guidance. ChaptarrNG links
results back to RanobeDB and does not import its descriptions or cover images,
which have separate licensing conditions. If several instances or other clients
share an IP address, keep their combined requests below the service limit.
See the [RanobeDB API documentation](https://ranobedb.org/api/docs/v0) and
[database licensing notes](https://ranobedb.org/about) before enabling this
source.

Audible search and ASIN lookups can use a configured marketplace for the US,
UK, France, Germany, Italy, Spain, Canada, Australia, India, Japan, or Brazil.
Results use that storefront's language headers and link back to the selected
marketplace.

## Getting Started

> **Docker is the primary maintained installation.** Releases also include standalone Linux and Windows archives, with package-manager feeds available on selected platforms. Publication and upgrade coverage varies by channel; check the [package status](packaging/README.md) before choosing one. For a BookshelfNG move, start with the [migration guide](docs/MIGRATE_FROM_BOOKSHELFNG.md). The [unmapped-files guide](docs/UNMAPPED_FILES.md) covers match retries.

### Docker

Pull the image:
```bash
docker pull ghcr.io/snapetech-org/chaptarrng:latest
```

Run with Docker:
```bash
docker run -d \
  --name chaptarrng \
  -p 127.0.0.1:8789:8789 \
  -e PUID=1000 \
  -e PGID=1000 \
  -v /path/to/config:/config \
  -v /path/to/audiobooks:/audiobooks \
  -v /path/to/ebooks:/ebooks \
  -v /path/to/downloads:/downloads \
  --restart unless-stopped \
  ghcr.io/snapetech-org/chaptarrng:latest
```

Note: if `PUID`/`PGID` are not set, the image defaults to `99:100`. If `/path/to/config` doesn't exist, Docker will create it as `root:root`. Create it first (or fix ownership) so it matches `PUID`/`PGID`. Avoid setting `user:` in Compose; it bypasses the entrypoint permission setup. On Unraid, media folders commonly use `99:100`, so use `PUID=99` and `PGID=100` unless your share is owned differently. If multiple containers/users share the same media group, add `-e UMASK=002`. When testing permissions with `docker exec`, test as the app user, not root, for example: `docker exec -u 99:100 chaptarrng sh -c 'id; touch /audiobooks/.chaptarr-write-test && rm /audiobooks/.chaptarr-write-test'`.

The host port is bound to loopback by default. To expose ChaptarrNG directly on
your LAN, change the mapping to `-p 8789:8789` and restrict access with your
firewall or reverse proxy. If SeerrNG runs in another container, attach both
containers to a shared Docker network and use `http://chaptarrng:8789`; do not
use `localhost` from inside SeerrNG.

Or use Docker Compose:
```bash
wget https://raw.githubusercontent.com/snapetech-org/chaptarrng/main/docker-compose.yml
# Edit paths in docker-compose.yml
docker compose up -d
```

#### Memory limits for large libraries

The Compose example does not set a memory limit because the right value depends
on the host and library. For a large initial scan, set a container limit sized
for the memory the host can spare. Without a cgroup limit, .NET may size its
garbage-collection heap from all the memory visible to the process, including
the full VM or host. Docker Compose's [`mem_limit`](https://docs.docker.com/reference/compose-file/services/#mem_limit)
sets a container memory limit; .NET uses that limit when sizing its heap. On a
Docker-in-LXC setup, make sure the Docker container itself has a limit visible
inside the container rather than relying only on the LXC allocation.

For example, add this under the `chaptarrng` service and choose a value that
fits your host and workload:

```yaml
mem_limit: 4g # Example only; size this for your host and library.
```

When a container memory limit is visible, .NET's default GC heap hard limit is
75% of that limit. If you choose to override it with
`DOTNET_GCHeapHardLimitPercent`, note that .NET parses the environment-variable
value as hexadecimal: `55` (or `0x55`) means 85%, while `85` means 133%.
Consider the memory needed outside the GC heap before increasing the percentage.
See Microsoft's [.NET GC configuration reference](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#heap-hard-limit-percent)
for details.

### PostgreSQL (Optional)

ChaptarrNG supports using an external PostgreSQL database instead of the default SQLite `chaptarr.db` file. To enable it, set at least `Chaptarr__Postgres__Host` (and credentials).

Environment variables:
- `Chaptarr__Postgres__Host`
- `Chaptarr__Postgres__Port` (default: `5432`)
- `Chaptarr__Postgres__User`
- `Chaptarr__Postgres__Password`
- `Chaptarr__Postgres__MainDb` (default: `chaptarr-main`)
- `Chaptarr__Postgres__LogDb` (default: `chaptarr-log`)
- `Chaptarr__Postgres__CacheDb` (default: `chaptarr-cache`)
- `Chaptarr__Postgres__SslMode` (optional: `disable`, `allow`, `prefer`, `require`, `verify-ca`, or `verify-full`; when unset the Npgsql default applies)
- `Chaptarr__Postgres__RootCert` (optional: path to a PEM root certificate used to verify the server for `verify-ca` and `verify-full`; startup fails with a clear message if the file is missing)
- `Chaptarr__Postgres__NoResetOnClose` (default: `true`; skips the session reset round-trip when a pooled connection is returned, set to `false` to restore it)

Note: ChaptarrNG does not create PostgreSQL databases automatically; create the databases and grant the configured user access.

### Direct Download sources

Direct Download is an optional, built-in ebook indexer and download client. It does not provide audiobook searches, recent-item feeds, or automatic discovery of arbitrary sources. Configure it only with sources you are allowed to access and use.

#### Configure the indexer

1. Add the **Direct Download** indexer.
2. Enter one absolute `http://` or `https://` URL per line in **URLs**.
3. Enter an **API Key** when the source supports fast downloads, or enable **Enable Slow-Download Browser Fallback** when you need browser-assisted links. At least one download method is required.
4. Save the indexer and select the **Direct Download** client for it.

Blank lines are ignored. Duplicate URLs are removed case-insensitively, and trailing slashes are normalized. The first occurrence remains in the configured order. Chaptarr never reorders the list or stores probe health as part of the settings. The provider Test action validates the URL list and, when an API key is configured, checks it against the first source without requesting a real file. A successful Test does not search for a book or prove that a title is available.

The API Key field is masked. A saved key is preserved when the indexer is edited or included in a settings backup, while API responses, logs, and validation errors must not reveal its value. Do not put credentials in a URL. If different URLs use different credentials, create separate indexers rather than placing them in one fallback list. A key must not be copied into a fallback URL or sent to a host that was not selected for the request.

#### API and browser-assisted grabs

When you grab a result from a supported catalog source, Chaptarr first uses
the source's fast-download API when an API key is configured. If that cannot
provide a file URL, the optional slow-download browser fallback can resolve a
link in a headless browser during the background transfer. Browser fallback
can take longer than an API grab. Direct Download is built in; it does not
require qBittorrent, Transmission, or another external download client.
The official Docker image includes the Playwright Chromium runtime on AMD64
and ARM64. ARMv7 images omit Chromium, so browser fallback is unavailable on
ARMv7; API-key-resolved downloads still work there. A custom image must include
a matching browser runtime for browser fallback to work.

The built-in client reports transfer progress and keeps its state and staged
files across restarts. Once a supported ebook file is complete, Chaptarr can
send it through the normal import flow. See the [fork feature guide](docs/FORK_FEATURES.md#direct-ebook-downloads)
for the behavior and safety limits.

#### Ordering, probing, and fallback

During an ebook search, Chaptarr checks the configured URLs from top to bottom. It prefers the book ISBN when one is available, then falls back to the monitored edition title. A URL is selected only after it returns a supported response with usable ebook results. Chaptarr moves to the next URL when the current one is unsafe, unavailable, times out, returns an unsupported response, or produces no usable result. It stops after the first successful URL and does not probe later entries for that search.

Each request has a bounded timeout and response-size limit. Redirects are followed only after every destination passes the same URL safety checks, and redirect chains are bounded. Catalog searches request EPUB, KEPUB, MOBI, AZW3, and PDF results; mirror sources can provide other importer-supported ebook types. Search results carry the normalized title, format, size, ISBN when available, and a direct download URL. A search fails with an actionable error when every configured URL fails or returns no usable result.

#### Ebook-only limitation

Direct Download accepts ebook searches only. Audiobook searches and audiobook releases are rejected before a Direct download is started. Supported ebook formats are limited to the formats recognized by the importer. A source that returns a page, an empty response, an unsupported format, or a non-ebook file is not a valid Direct release.

#### Staging, restart, and cleanup

The Direct Download client requires a configured **Staging Folder**. Create the folder first and make sure the Chaptarr process user can read, write, create subfolders, rename files, and delete files there. Keep the staging folder on persistent storage, not a temporary container filesystem. Do not point it at a library root or a folder shared with another downloader.

Each download gets its own state record and working directory. Data is written to a `.part` file and promoted to the final staged file only after a non-empty response completes. The client reports queued, downloading, completed, and failed states. Transient connection, timeout, and server failures are retried up to three attempts. An interrupted partial file is discarded before a retry, so it is not treated as a completed ebook.

On restart, Chaptarr reloads Direct state from the staging folder. Queued and in-progress items are reconciled and started again, an existing completed file is reused, and a missing completed file becomes failed. Keep the state files and staged files together until the item has been imported or deliberately removed.

Cleanup is explicit. Removing an item with delete-data enabled removes its state, partial file, completed staged file, and empty per-download directory. Removing it without delete-data removes the state but preserves downloaded data for manual inspection or recovery. After an import, use the normal completed-download cleanup action and choose whether staged data should be deleted. Do not delete staged files manually while an item is queued or being imported.

#### Security and operational requirements

Only absolute HTTP and HTTPS URLs are accepted. URLs with embedded credentials are rejected. Chaptarr blocks localhost, loopback, private, link-local, carrier-grade NAT, and cloud metadata targets, including targets reached through redirects. TLS certificate validation remains enabled. Keep source URLs and keys private, restrict access to the Chaptarr web interface, and never paste a key into an issue, log excerpt, backup shared with others, or shell history.

The feature is not a general-purpose web downloader. It depends on a source returning a supported, bounded response and a usable direct file URL. Access controls, rate limits, expiring links, changed page formats, unavailable mirrors, and licensing restrictions can cause a previously working URL to fail. A provider Test cannot detect those runtime conditions. Monitor the queue and failed items, keep backups of the configuration and library, and remove stale staging data after confirming that no queued or import-pending item references it.

### Unraid Community Applications

ChaptarrNG's Unraid Community Apps template is [templates/chaptarrng.xml](templates/chaptarrng.xml),
with repository metadata in [ca_profile.xml](ca_profile.xml). It installs the
maintained fork image, maps the web interface on port `8789`, and provides
separate paths for appdata, audiobooks, ebooks, and downloads. The template
tracks the validated `main` image and uses `PUID=99` and `PGID=100` by default;
change those values to match the ownership of your media shares.

To make ChaptarrNG searchable in Community Applications, submit the package
repository URL `https://github.com/snapetech-org/chaptarrng` through Unraid's
[Community Apps submission flow](https://ca.unraid.net/submit/new) and complete
its Validate and Scan steps. The template's `TemplateURL` points to its raw XML
in this repository. The `latest` GHCR tag remains available for stable Docker
installs; the Unraid template follows the validated `main` image.

## Building from Source
Building from source requires the .NET 10 SDK, Node.js, and Yarn. When running ChaptarrNG natively, install FFmpeg for your operating system and make sure both `ffmpeg` and `ffprobe` are available on the `PATH` used to start the application. Normal source builds do not bundle them automatically; the ChaptarrNG Docker image already includes them.

**Linux / macOS:**
```bash
# Clone the repository
git clone https://github.com/snapetech-org/chaptarrng.git
cd chaptarrng

# Build the backend
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish

# Build the frontend
yarn install
yarn build
cp -r _output/UI _output/publish/UI

# Run ChaptarrNG
dotnet _output/publish/Chaptarr.dll
```

**Windows (Command Prompt):**
```cmd
:: Clone the repository
git clone https://github.com/snapetech-org/chaptarrng.git
cd chaptarrng

:: Build the backend
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish

:: Build the frontend
yarn install
yarn build
xcopy _output\UI _output\publish\UI /E /I

:: Run ChaptarrNG
dotnet _output/publish/Chaptarr.Console.dll
```

Note: the inherited assembly is named `Chaptarr.dll` on Linux/macOS and `Chaptarr.Console.dll` on Windows.

## Documentation
- [Documentation index](docs/README.md): migration, features, operations, and development guides
- [OpenID Connect setup, account admission, and recovery](docs/oidc-authentication.md) · [shared provider catalog](https://github.com/snapetech-org/seerrng/blob/main/docs/maintainers/oidc-provider-catalog.md) · [implementation contract](https://github.com/snapetech-org/seerrng/blob/main/docs/maintainers/oidc-cross-project-template.md)
- [Setup experience contract](docs/development/setup-experience-contract.md): evolve Quickstart in place for ChaptarrNG's ebook and audiobook workflow
- [UI localization roadmap](docs/localization-roadmap.md): current locale order, translated high-use strings, and remaining catalog work

- Default ChaptarrNG web UI: http://localhost:8789
- Default username/password: Set on first launch
- [Fork changes and capabilities](docs/FORK_FEATURES.md): feature overview and links to detailed setup and API references
- [Import-list preview and history](docs/IMPORT_LIST_SYNC.md): dry-run behavior, saved sync outcomes, retention, and refresh settings
- [External library integrations](docs/EXTERNAL_LIBRARY_INTEGRATIONS.md): setup and synchronization boundaries for AudioBookShelf, Calibre, Grimmory, and Emby/Jellyfin
- [MQTT Gateway](docs/MQTT_GATEWAY.md): broker settings, Home Assistant discovery, snapshots, events, and canonical webhook requests
- [Prometheus metrics](docs/PROMETHEUS_METRICS.md): listener configuration, scrape setup, metric names, and network exposure
- [Optional diagnostics](docs/OPTIONAL_DIAGNOSTICS.md): explicit opt-in, aggregate event counts, and privacy boundaries
- [Validation engine guide](docs/development/validation-engine.md): local and CI validation tasks, progress reporting, and receipt checks
- [Changelog](CHANGELOG.md): release-by-release changes shipped by the fork
- [SeerrNG Bookshelf backend guide](https://github.com/snapetech-org/seerrng/blob/main/docs/using-seerr/bookshelf-backend.md): setup and request lifecycle
- [SeerrNG integration contract](docs/SEERRNG_INTEGRATION.md): capabilities, service-key setup, and integration smoke test
- [API identity and lifecycle](docs/API_IDENTITY_AND_LIFECYCLE.md): compatibility details for maintainers

## Contributing

ChaptarrNG is a community project, and we welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Bug Reports & Features

Found a bug or have a feature request? Open an issue on GitHub with the steps
to reproduce it, the expected behavior, and your environment details. See
[Reporting issues](docs/REPORTING_ISSUES.md) for useful log excerpts and
privacy guidance before attaching logs.

## Security

ChaptarrNG inherits the Readarr-derived operational model and security work from Chaptarr. Its current protections include:

- Constant-time API-key comparison
- Login brute-force throttling
- Inbound security headers including HSTS, CSP, X-Frame-Options, X-Content-Type-Options, and Referrer-Policy
- Image proxy target validation before fetching remote images
- API responses redact provider secrets
- No analytics or crash reporting is enabled by default; [optional diagnostics](docs/OPTIONAL_DIAGNOSTICS.md) sends only operator-configured aggregate event counts
- Optional passphrase-encrypted Quickstart Settings Backups
- Optional authenticated encryption for full backup archives
- Update binaries are verified by SHA256 before install

CI runs Trivy dependency/secret scans and Semgrep Community Edition. CodeQL is
disabled for this repository. Published container images
include a BuildKit SBOM and provenance metadata, plus a GitHub build-provenance
attestation. The Compose example also enables `no-new-privileges` and binds
the published web port to loopback by default.

The frontend build uses a local `braces` parser depth limit while its upstream
package has no published fix for GHSA-vfj7-8cjw-p6xm. See the
[dependency security notes](docs/dependency-security.md) for the patch details,
audit commands, and removal criteria.

Use the `X-Api-Key` header for API clients. ChaptarrNG still accepts the legacy
`apikey` query parameter for compatibility, so reverse proxies should redact
that parameter from access logs.

Traditional full backups contain the database and config file, including credentials. Without the setting below they are stored as plain ZIP files; protect them as secrets.

To encrypt scheduled, update, and manual full backups, store a randomly
generated passphrase with at least 16 non-whitespace characters in a secret
file, mount it read-only in the ChaptarrNG container, and set
`CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE` to its in-container path (for example,
`/run/secrets/chaptarr_backup_passphrase`). The Compose example includes a
commented Docker secret setup. Backups are then written as authenticated,
chunk-encrypted `.zip.enc` files; existing `.zip` backups remain restorable.
Mount the same secret file on a replacement instance before restoring an
encrypted backup. Keep the secret outside `Config.xml` and the backup archive;
losing it makes those encrypted backups unrecoverable. Enabling encryption does
not convert existing `.zip` backups, so protect or remove those separately.
With the file path unset, new backups retain the legacy plain ZIP format.
Protect the secret file through your container secret manager, and retain old
secret files if you need to restore archives made with them.
On POSIX systems, backup staging, temporary archives, and restore extraction
directories are restricted to the application owner while the files are in use.

The Compose example publishes its web port on `127.0.0.1` by default. Set
`CHAPTARRNG_BIND_ADDRESS=0.0.0.0` in the host environment to publish it on all
host interfaces, or use a shared Docker network for container-to-container
access without publishing the port.

## Privacy

ChaptarrNG uses `api2.chaptarr.com` for metadata and matching. Metadata requests may include provider IDs, search text, media type, selected audio/eBook tags, and the file name being matched. Full file paths, user identity, and indexer or download-client credentials are not sent. Update checks send version, OS, architecture, and runtime information.

Please see [SECURITY.md](SECURITY.md) for reporting security vulnerabilities.

## Acknowledgments

ChaptarrNG is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself an independent fork of [Readarr](https://github.com/Readarr/Readarr), from the [Servarr](https://wiki.servarr.com/) project family. This project builds on work by Chaptarr, Readarr, and the Servarr contributors to Readarr, Sonarr, Radarr, Lidarr, and Prowlarr. Thank you.

Audiobook conversion is powered by [m4b-tool](https://github.com/sandreas/m4b-tool) (by sandreas), which builds on [FFmpeg](https://ffmpeg.org/) and [mp4v2](https://github.com/enzo1982/mp4v2). These are bundled in the ChaptarrNG Docker image under their respective licenses.

See [COPYRIGHT.md](COPYRIGHT.md) for full attribution and copyright details.

## Disclaimer

ChaptarrNG is independently maintained. It is **not affiliated with, endorsed by, or supported by** the Servarr team or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects — please don't send ChaptarrNG support requests their way.

ChaptarrNG is provided under the GNU GPL v3 with no warranty (see the [License](#license) section below).

## AI Development Disclosure

ChaptarrNG is developed and maintained with the assistance of AI tools.

## License

- [GNU GPL v3](https://www.gnu.org/licenses/gpl.html)
- Copyright © 2026 Snapetech and SeerrNG contributors for fork-specific changes
- Portions copyright © 2010–2026 the Servarr team and contributors

See the [LICENSE](LICENSE) file for the full license text and [COPYRIGHT.md](COPYRIGHT.md) for attribution details.

## Security scanning

Run scripts/security-scan-local.sh for local Semgrep Community Edition and applicable Trivy scans. Tag and manually dispatched GitHub Actions workflows run these scans on private ephemeral JIT runners; CodeQL is disabled. See docs/security-scanning.md for offline setup and updates. Run scripts/install-security-hooks.sh once to enable the staged Semgrep pre-commit hook.
