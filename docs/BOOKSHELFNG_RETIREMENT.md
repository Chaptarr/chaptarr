# BookshelfNG retirement status

## Current status — 2026-11-05 UTC

The unpublished candidate now includes schema-126 migration identity indexes. Public hosted runners are
authorized for trusted validation; CodeQL remains disabled.

Fresh, consistent snapshots of both running BookshelfNG `.76` instances have now
passed isolated conversion, migration checks and rollback. Original source
services and media were not changed. The first real-data comparison exposed an
author-metadata reset; `53d76bcc` repairs it, and repeat conversions preserve
images, links, aliases and genres for all 1,379 and 1,400 managed authors. All
101 importer fixtures pass. `bd23e039` additionally preserves legacy work release
dates, selected-edition page counts and file-based percentage responses; 64
focused book/API checks pass. Packaged runtime comparisons confirm those response repairs. The legacy
edition catalog also retains all source editions, original cover URLs and the
predecessor ownership-loading semantics. See the [operator rehearsal](maintainers/BOOKSHELFNG_OPERATOR_REHEARSAL_2026-11-05.md).

Read-only real-media checks pass for EPUB, KEPUB, AZW3, MOBI, PDF, MP3, M4B and
M4A; authenticated range downloads match original bytes. This establishes bounded
read acceptance, not acquisition, write/import or failure-recovery acceptance.
The two installations retain separate ports, API keys and accounts.

Full public Build [42031944612](https://github.com/snapetech-org/chaptarrng/actions/runs/42031944612)
and all ten standalone archives
[42031945553](https://github.com/snapetech-org/chaptarrng/actions/runs/42031945553)
passed at schema-126 candidate `833d6d63`. The strict API run
[42031944507](https://github.com/snapetech-org/chaptarrng/actions/runs/42031944507)
also passed with explicit, independently verified runtime/security dispositions;
its source-side invalid paged fixture remains inconclusive.

A subsequent comparison of every managed author, work and file in both real
snapshots found additional response differences missed by the synthetic sample.
The compatibility fixes preserve raw narrator text, every series label, original
titles, empty unselected-edition metadata, null embedded relations, combined work
monitoring, source author image lists and unmapped audio quality weights.
Unscoped split works prefer the monitored edition retaining the source provider
identity. Legacy cover URLs retain their extension, URL base and last-write token;
known extensionless cached image names are supported without accepting arbitrary
extensionless files. The 267 focused API/importer regressions pass; a new packaged full-library
comparison is still required before claiming these gaps closed.

The full SeerrNG user/approval/acquisition/scan/restart rehearsal now audits database
identity, duplicates and orphaned identifiers. It exposed client defects repaired
in [SeerrNG PR 527](https://github.com/snapetech-org/seerrng/pull/527), whose trusted
owner CI [42028627704](https://github.com/snapetech-org/seerrng/actions/runs/42028627704)
passed; the repair is merged as `7b013338`. Both format rehearsals pass with the explicitly identified unpublished
test image; the unchanged deployed client is not claimed repaired.

Live-provider fulfillment, headed UI, remaining installed and external distribution
acceptance, and production acceptance remain open. Desktop Pool v2 acquisition
still times out; no headed UI pass is claimed. Publication, bridge activation,
cutover and retirement remain unauthorized. Revision-specific results are recorded
in the [operator rehearsal](maintainers/BOOKSHELFNG_OPERATOR_REHEARSAL_2026-11-05.md)
and [channel acceptance matrix](maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).

## Historical checkpoint — 2026-11-04 UTC

At application-code head `d3a67c58`; later main commits are runner policy,
acceptance-workflow, test, and documentation updates. BookshelfNG `0.4.21.76`
and ChaptarrNG `v0.9.977` remain the latest public releases; the local `0.9.978`
candidate is unpublished. Public GitHub-hosted
runners are authorized again for trusted
build and validation jobs; private ephemeral JIT remains available. See
[`AGENTS.md`](../AGENTS.md) for the current runner policy.

Full public Build run
[#41789426720](https://github.com/snapetech-org/chaptarrng/actions/runs/41789426720)
at `d12798d1` passed Linux and macOS build/test/artifact jobs, PostgreSQL
migration smoke, and the standard Docker build. Windows failed the
localization/RTL test because it hashed a JSON registry after CRLF checkout
conversion. Commit `d3a67c58` normalizes line endings before hashing and adds a
regression test. Full public rerun
[#41792679639](https://github.com/snapetech-org/chaptarrng/actions/runs/41792679639)
passed on `d3a67c58`: Linux, Windows, macOS, PostgreSQL migration smoke, and
the standard Docker build all succeeded. The Windows job also passed installer
fingerprinting, service upgrade/rollback, selected data-directory preservation,
and the installed Chocolatey `.76` automatic handoff.

Windows Chocolatey run
[#41756297204](https://github.com/snapetech-org/chaptarrng/actions/runs/41756297204)
at `29b5f31f` passed .NET tests, build, service round trip, and candidate
health, then failed its injected post-migration rollback because backup
discovery missed the retained SQLite source and left BookshelfNG stopped. The
failure artifact is retained in task scratch. Backup discovery now includes
hidden files, and exact-head rerun
[#41761241201](https://github.com/snapetech-org/chaptarrng/actions/runs/41761241201)
was superseded for Windows by focused public run
[#41785474409](https://github.com/snapetech-org/chaptarrng/actions/runs/41785474409)
on `d12798d1`. That run passed the full Windows .NET suite, installer build
and SQLite fingerprint, installed service upgrade/rollback, selected data
directory check, and BookshelfNG `.76` to exact-candidate Chocolatey handoff.
Manual defer, injected post-migration-health rollback with source recovery,
and automatic migration all passed.

The separate installed macOS acceptance run
[#41761249985](https://github.com/snapetech-org/chaptarrng/actions/runs/41761249985)
passed on Intel and ARM64 at `8b26fa3e`, covering No, headless Manual, Yes,
health, source backup, and failed-start rollback after the supervisor cleanup
assertion was corrected. The previous current-source Helm installed handoff
[#41756992768](https://github.com/snapetech-org/chaptarrng/actions/runs/41756992768)
passed populated migration, provider-ID/narrator mapping, health and database
checks, retained source backup, chart rollback Job, and BookshelfNG recovery
with matching database, configuration, and canary fingerprints.

**Exact-head hosted handoffs (2026-11-04):** Fedora 44 RPM acceptance
[#41844640426](https://github.com/snapetech-org/chaptarrng/actions/runs/41844640426)
passed at repository head `a64669b2`, covering the installed `.76` package's
headless/manual/automatic choices, migration, health checks, SELinux checks,
and rollback. The Helm installed handoff
[#41844474750](https://github.com/snapetech-org/chaptarrng/actions/runs/41844474750)
also passed at `a64669b2`: it exercised a populated schema-158 fixture,
manual defer, automatic conversion, retained source backup, the chart restore
Job, and BookshelfNG recovery with matching database, configuration, and
canary fingerprints. These runs used the public hosted runner route; application
code remains at `d3a67c58`, with the intervening `a64669b2` commit recording
runner and CodeQL policy only.

The current macOS standalone acceptance
[#41844474525](https://github.com/snapetech-org/chaptarrng/actions/runs/41844474525)
passed on Intel at `a64669b2`, including archive verification and No, headless
Manual, Yes, health, source backup, and failed-start rollback. The ARM64 job
was canceled twice after 15 minutes queued without a runner assignment, with no
test steps started. The workflow now accepts an architecture selector so the
next validation can target ARM64 without rebuilding the passing Intel archive.
ARM64-only run
[#41851491849](https://github.com/snapetech-org/chaptarrng/actions/runs/41851491849)
at `4643daf2` expanded exactly one job on the explicit private Apple route but
remains queued without a runner; no test step has started. These are runner
capacity results, not product-test failures.

Keep BookshelfNG operational. Representative SQLite/PostgreSQL operator-data
migration and rollback, real media, live provider fulfillment, every installed
channel, external delivery, and formal operator sign-off remain open. No
release, image alias, compatibility bridge, or production cutover is authorized
by these CI and synthetic-fixture results.

## Historical status snapshot — 2026-10-31 18:28 UTC

BookshelfNG `0.4.21.76` and ChaptarrNG `v0.9.977` remain the latest public
application releases; the local ChaptarrNG `0.9.978` candidate is unpublished.
Remote main advanced to `b1f10c72` during this consolidation; that commit's
private-fork validation policy is preserved. The source and workflow
consolidation candidate is `7e538f5c`, based on `fa55ffab`. It contains
private-only build and release routing; fixes for the
observed Windows Python lookup and localization-browser compile failures;
automatic book-number search fallback; same-recording audiobook import
matching that requires known matching language and narrator data; book updates
with multiple monitored editions; and the Chocolatey manual-defer health
recheck. No exact-head private-JIT result validates this candidate or the
current application source. No exact-head Build or platform Actions run
validates remote main `b1f10c72` or the consolidated candidate.

Build run
[#41168115692](https://github.com/snapetech-org/chaptarrng/actions/runs/41168115692)
is still queued at older SHA `64689f1f`; its Linux image-alias contract,
Windows Python lookup, and PostgreSQL localization-browser build jobs failed,
while macOS remains queued. The Windows Chocolatey `.76` run
[#41166442007](https://github.com/snapetech-org/chaptarrng/actions/runs/41166442007)
is still queued at `eec975d2`, so it has not exercised the new candidate.
Standalone-assets run
[#41168142569](https://github.com/snapetech-org/chaptarrng/actions/runs/41168142569)
has private-runner frontend and most archive results on the older SHA; its
Linux x86/FreeBSD runtime-pack job remains queued without a runner. macOS
handoff [#41168181470](https://github.com/snapetech-org/chaptarrng/actions/runs/41168181470)
is also queued on old source.

Helm run
[#41168121048](https://github.com/snapetech-org/chaptarrng/actions/runs/41168121048)
ran on private JIT at `64689f1f`. Checkout, Helm lint, cache preparation,
handoff contracts, and the pinned `kind` download/checksum passed. It then
failed when the runner proxy returned HTTP 403 fetching pinned `kubectl` from
`dl.k8s.io`; the installed BookshelfNG `.76` handoff script did not run. The
workflow uses an ephemeral Kind cluster on the JIT runner; no production
cluster was used.

The Packer broker is active on installed runtime `de29ac9`, which is behind
Packer main `ae20da4`. The Packer validation run
[#41229001428](https://github.com/snapetech-org/packer/actions/runs/41229001428)
failed two broker-policy tests and skipped its Windows canary. The 18:28 UTC
broker read found six active jobs, 22
eligible queue entries, and no free kspld0 slots. Online Linux guests belong to
other repositories; no compatible ChaptarrNG Linux or Windows x64 runner is
available. The Windows JIT runner is occupied by a different repository. The
broker owner still controls cutover and capacity; no C0/slot-owner handoff was
visible. Do not dispatch or retry until the owner confirms a compatible slot.
The latest organization catalog is 65 repositories and 389 workflow files
(08:12 UTC); the 17:54 group readback lists 65 repositories/393 refs in group
10, 8/19 in group 8, and 12/29 in group 11. The broker inventory authorizes
ChaptarrNG `build.yml` for `push`, `workflow_call`, and `workflow_dispatch`,
but not `pull_request_target`; the queued PR #185 dispatcher is therefore not
admitted. These different snapshots and incomplete event routing do not prove
organization-wide private-JIT coverage. Keep BookshelfNG running. The
`.76` Chocolatey installed No/Yes handoff, official StartOS package and
repository acceptance, Cloudron and Unraid delivery, canonical Co-op Cloud
recipe, remaining package-feed/catalog handoffs, representative operator data,
provider behavior, rollback, and release publication remain open.

**Single-image migration contract (2026-10-30):** The earlier proposal for
separate Goodreads and Hardcover compatibility images is withdrawn. The
ChaptarrNG source changes now define one standard image build and one digest
for all legacy aliases. At that historical checkpoint, no candidate image had
been published. The later Helm installed-handoff run builds a local image in
an ephemeral Kind cluster; this is not publication. A tag cannot carry the old
image's implicit provider setting,
so every migration must receive `HARDCOVER=true` or `HARDCOVER=false` from
preserved channel configuration or an explicit operator choice;
`HARDCOVER_NATIVE=false` explicitly selects Goodreads. The importer needs this
mode to record the source dialect even when source identities are namespaced,
and fails closed before changing the source database when mode is missing or
invalid. The standard image detects legacy SQLite files and
Readarr/Audioarr/Bookshelf PostgreSQL settings, then starts the migration
supervisor. Fresh ChaptarrNG installs keep port `8789`; migrated installs can
retain the legacy `8787` port through saved `config.xml` or explicit channel
settings, and the image advertises both ports for automatic Docker mappings.
These source changes still needed exact-head validation at the 2026-10-30
checkpoint and did not authorize a public alias redirect. Runner selection is
now governed by the current owner directive in [`AGENTS.md`](../AGENTS.md).

BookshelfNG retirement is blocked by every carried feature and client
contract, each database/schema/configuration and data path, rollback, and each
installation channel. Migration-158 SQLite rehearsal evidence does not prove
all schema lineages, providers, settings, real media, operator environments,
or installed rollback. The replacement is one ordinary ChaptarrNG product
and standard image for fresh and migrated installs; no compatibility build is
part of the handoff. The later ChaptarrNG-to-upstream retirement gate is a
separate cumulative blocker covering BookshelfNG carryover and every
ChaptarrNG capability that is maintained, active, or unreleased, including
localization and RTL work now in `main`.

The refreshed BookshelfNG source-path audit compares source `1bd7f2dc` with
current ChaptarrNG `main` target `fa55ffab` (application source snapshot
`bd589312`): 1,174 changed paths, 56 reviewed mappings, 587 dispositions, and
587 paths still needing disposition. The open set includes semantic review,
unmapped paths, source-deleted paths, and identical files without an explicit
decision; it is not a count of missing runtime features. See the
[exact-head consolidation audit](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md#exact-head-source-path-audit--2026-10-31)
and its pinned inventory and maps. These path decisions do not close feature,
data, rollback, or installed-channel acceptance.

**Runner policy snapshot at 2026-10-31 (superseded 2026-11-03):** At that
checkpoint, organization build, test, scan, package, and release jobs required
private ephemeral JIT capacity with no hosted fallback. Public GitHub-hosted
runners are now authorized for trusted jobs under [`AGENTS.md`](../AGENTS.md).
See the [BookshelfNG parity matrix](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/bookshelfng-parity-roadmap.md),
[distribution bridge status](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarrng-distribution-bridge.md),
and [consolidation report](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md).

**Historical channel acceptance update (2026-10-26):** Installed candidate results and
per-channel y/n behavior are in the
[channel acceptance matrix](maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).
The Debian, Fedora, Arch, Windows Chocolatey, desktop, Helm, and Docker
candidate handoffs now have local acceptance evidence. Docker TTY No/Yes,
headless Ask/defer, automatic conversion, startup health, and injected-failure
rollback passed on a local exact-head image. Cloudron 10.1.6 passed an installed
`.4.21` to `.4.23` update, manual defer, native backup/restore, automatic
migration, and source-database plus old-package rollback. Its candidate/feed
remains unpublished, and the legacy `.4.21` pre-update backup excludes
persistent directories. External publisher/device checks, representative
operator-data coverage, and the global release gate remain open.
The historical channel inventory below is superseded by that matrix for current
test status.

**Execution plan (2026-10-24):** The [replacement reassessment](maintainers/BOOKSHELFNG_REPLACEMENT_REASSESSMENT_2026-10-24.md)
separates confirmed compatibility code gaps, source-review work, and validation
from the historical evidence below. It prioritizes unchanged-client behavior at
the existing API address and recoverable automatic installation handoff, then
the remaining semantic review, comprehensive tests, refinement and delivery.
Use that dependency order for the 100% drop-in goal; old path counts are not
missing-feature counts. This planning update changes no release gate.

**Operator-data SQLite conversion and rollback (2026-10-25, ChaptarrNG
`3bfb4b15`):** Two private BookshelfNG database snapshots passed preview,
apply, integrity and foreign-key reconciliation, source-backup comparison, and
restore. The preview accounted for all rows in 39 imported tables; rollback
restored all 40 source tables and retained the converted targets separately.
The full evidence and precise data dispositions are in the
[migration fixture record](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md#operator-data-sqlite-conversion-and-rollback-2026-10-25).
This closes the migration/rollback case for these two SQLite snapshots only;
PostgreSQL, live-provider and unchanged-client workflows, representative media,
installed-channel acceptance, and external delivery remain open. No release or
cutover gate changed.

**BookshelfNG edition and unmapped-file migration parity (2026-10-24):** The
converter now classifies editions using ebook, audiobook, print, narrator, and
recognized linked-file evidence. It creates format-specific edition rows when
needed, keeps provider identity on one row, and routes files, sidecars, and
history to their matching format. Ambiguous editions retain the conservative
review/map workflow. A recognized extension now also classifies files whose
source `EditionId` is zero or missing, while leaving their book and edition
relationships unassigned; sidecars and history with an existing source-book
link follow that format's migrated book row. Unknown extensions remain
unclassified, and stale choices for currently unambiguous books are ignored.

The Release solution built with 0 warnings and 0 errors, and the current Core
suite passed 4,888 tests with 28 skips and no failures. The pinned Linux M4B
tool filter passed 10/10, including abrupt termination of the process hosting
`ConversionJobService`, reopening its SQLite job store without a shutdown
callback, and successful retry with manifest digest verification. This uses
synthetic audio and is conversion-service process evidence, not a complete
ChaptarrNG-host or installed-service crash rehearsal. A local Docker candidate
built successfully; its frontend emitted the existing 22 CSS-order warnings and its
source-link metadata warnings reflect the Docker build context without `.git`.
The populated SeerrNG smoke read 13,083 ebook and 6,561 audiobook records,
checked format-specific statistics and file extensions, and verified restricted
key boundaries. Its admin-key unmapped queries returned 6,150 ebook and 20
audiobook files, all still without a book link. Offline reconciliation found
27,532 source and target book files with matching IDs, paths, sizes, qualities,
and parts; all 1,910 history IDs were retained; both databases passed SQLite
integrity checks; and the target had zero foreign-key errors or recognized
extension misroutes. The private source snapshot hash remained unchanged. This
closes the reviewed local converter and API-classification code gap, not
representative media/playback, live-provider, installed rollback, hosted
exact-head, platform/channel handoff, or production cutover acceptance.

**Exact-head populated migration and SeerrNG smoke (2026-10-24):** ChaptarrNG
`9bf2cf654ce01dfb25d41a59525ce8e4dca39a29` built through the checked-in
`Dockerfile.build` and passed both SeerrNG Compose smoke scripts against
SeerrNG `f7769b65baa0b0d37ca9c5c37ecd860cde0f06a6`. Normal startup converted
the disposable populated BookshelfNG migration-158 fixture. SeerrNG's compiled
`ReadarrAPI` read the migrated ebook and audiobook libraries, format-specific
file counts, and history with the restricted service key; administrative and
unrelated command access was denied. The adapter created, polled, and cancelled
a synthetic pending import and observed its terminal cancellation state. The
fixture, media placeholders, and keys were synthetic. This does not establish
live provider fulfillment, successful request retries, full availability and
history parity, representative operator data or media, installed rollback,
channel handoff, or cutover acceptance. The exact-head hosted build was still
running when this smoke was recorded.

**Historical provider-mode image alias proposal (2026-10-24, superseded):** An
earlier draft proposed separate Goodreads and Hardcover images and
provider-specific alias digests. That design was withdrawn on 2026-10-30: no
such image was published, and every legacy alias must now resolve to the one
standard ChaptarrNG digest. Migration mode is carried through channel
configuration or an explicit operator choice; missing mode fails closed. The
release gates remain closed, and no alias redirect was published.

**Conversion artifact content integrity (2026-10-24):** Version 2 conversion
manifests include the output's SHA-256 digest. Startup recovery and import-time
reuse now verify the full file content as well as path, size, timestamp,
conversion settings, and source metadata. Version 1 manifests are not reusable.
This strengthens local artifact validation. Real-tool shutdown/restart recovery
passed the pinned Linux integration suite described below; representative-media
acceptance remains open.

**M4B recovery hardening (2026-10-24):** Detached conversion retries now write
to unique staging paths and promote output only after conversion succeeds.
Version 2 artifact manifests record whether an output is importable, its size,
modification time, and SHA-256 digest; readable output from a tool-reported
failure is retained for inspection but cannot be imported or reused. Startup
recovery continues to adopt validated output or requeue interrupted work.
The pinned Linux tool image passed 9/9 real-tool cases, including shutdown
cancellation, durable requeue, worker restart against the same SQLite job
store, successful retry, output promotion, and manifest digest verification.
This uses synthetic audio; representative media and abrupt whole-application
crash recovery remain open.

**Real-tool conversion restart smoke (2026-10-24):** The new
`real_m4b_tool_should_resume_after_application_restart_interrupts_conversion_when_installed`
case gates FFmpeg after the real `m4b-tool` starts, requests application
shutdown, verifies the child process exits and the persisted job returns to
`Queued`, then starts a new `ConversionJobService` over the same job database.
The resumed attempt reaches `ReadyToImport`, promotes a validated M4B, and
records a SHA-256 digest matching the output. The pinned-tool filter passed
9/9 cases on the current source. The inputs are generated synthetic audio, and
the test restarts the conversion worker rather than killing the ChaptarrNG
application process; representative-media and abrupt process-crash acceptance
remain open.

**Hardcover search request parity (2026-10-24):** The fallback catalog search
path now sends its entity search, author-book validation, and enrichment queries
through the same GraphQL executor as native Hardcover lookups. This restores the
shared request pacing and semaphore, response-header and `Retry-After` pause,
daily-quota reserve, configured API URL, and transient retry policy. User-driven
catalog searches carry the interactive quota flag through every query. The
Release solution build passed with zero warnings or errors; the Release Core
suite passed 4,880 tests, skipped 25, and failed 0. The TRX records 4,907 rows
(4,880 passed and 27 `NotExecuted`), retaining the existing two-row runner
accounting difference. The new cross-path 429 pause and interactive-quota
fixtures passed. Live-provider behavior and representative migrated settings
remain open.

**Bookshelf matching and audiobook-tag compatibility batch (2026-10-24):**
ChaptarrNG now carries BookshelfNG's legacy weighted candidate scoring,
per-format author/root profile selection, filename fallback, and ordered
catalog identification tiers through routine imports and unmapped-match
retries. The local-to-remote recheck keeps the legacy 85% threshold and only
replaces a local candidate with a strictly higher remote score; manual import
preview retains its exact-query boundary, and the newer media identity and
ambiguity safeguards remain enabled. Pending-author retries now persist
selected remote editions until the requested work is available. Audio metadata
preserves BookshelfNG's null-only Album/AlbumSort and Title/TitleSort
projection—including empty-value behavior—through extraction, matching,
caching, and compatible API fields; the private projection is filtered from
public tag responses. The source review and crosswalk are in
[the workflow parity map](BOOKSHELFNG_WORKFLOW_PARITY.md#import-and-media-source-path-crosswalk).
The completed same-file guard was validated with a serial Release solution
build (0 warnings, 0 errors) and the full Release Core suite (4,859 passed, 25
skipped, 0 failed). All three new same-size compatibility cases passed. The TRX
contains 4,886 rows: 4,859 `Passed` and 27 `NotExecuted`, while the console
summary counts 4,884 total with 25 skipped; the two-row runner accounting
difference remains recorded rather than inferred away. Local synthetic
regressions do not establish live-provider fulfillment,
representative-library acceptance, or installed-channel handoff.

**BookshelfNG same-file import guard (2026-10-24):** Migrated installations
reject a candidate when an existing book file has the same size and a
compatible format, including a file assigned to another edition. Manual import
preview and final import use the same migration-gated check; same-path files
follow the source rule, while explicit generated conversions remain exempt.
Fresh installations keep ChaptarrNG's multi-edition behavior. The Release
solution build and full Core suite passed; the migrated-preview rejection, the
migrated final-import rejection, and the fresh-install allowance all passed.
This closes the newly reviewed `SameFileSpecification.cs` source-path gap
only; live-provider, representative-data, installed-channel, and rollback
acceptance remain open.

**Image dependency/security update (2026-10-23):** ChaptarrNG now uses
SkiaSharp for resized JPEG, PNG, JFIF, and WebP covers, and PDFsharp 6.2.4 for
PDF metadata extraction. PDFsharp 6.2.4 targets .NET 10 and retains its MIT
license. Removing ImageSharp and PdfSharpCore also removes the transitive
ImageSharp dependency and its build-license gates from source, Docker, archive,
and CI builds. The full application and Core test builds passed with zero
warnings or errors. Release Core passed 4,808 tests, skipped 25, and failed 0;
the frontend CommonJS suite passed 126/126, and the production UI bundle,
typecheck, translation check, Compose validation, and package-graph check all
passed. The 25 skips require configured PostgreSQL, headless Chromium, or live
BookLore credentials. This closes the local dependency/code review item;
hosted exact-head checks, provider, representative-data, and installed-channel
acceptance remain open.

**Migrated release-decision parity — 2026-10-23:** ChaptarrNG now keeps
BookshelfNG's non-bypassable required/ignored release-term and quality-profile
rejections for converted installs, and does not skip cutoff or upgrade checks
during resolved interactive searches. It also preserves BookshelfNG's empty
discography acceptance edge. Fresh installs retain ChaptarrNG's current
bypassable filters and stricter empty-discography handling. Split-book IDs are
allocated above the complete source ID range to avoid colliding with adjacent
legacy records. The Release Core suite passed 4,783 tests with 25 skips and no
failures; the full Release solution built with 0 warnings and errors. This is
source-level regression evidence; provider, representative-data, and installed
handoff acceptance remain open.

**Routine catalog identification fallback — 2026-10-23:** When local and V5
matching leave files unmatched, ChaptarrNG now searches the configured catalog
using ISBN, ASIN, Goodreads/provider IDs, author/title, title, a single-file
filename, then author. Routine search stops broadening when a tier returns
candidates; a review suggestion still requires the exact media type and one
stable author/work identity. Author-scoped searches stay within that provider
identity, and book-scoped searches do not widen. Manual Import preview and
eligible completed-download imports use the same path. The explicit Unmapped
Files retry retains its established query behavior, including rejecting
conflicting identifier and text-tier identities. Release Core passed 4,800
tests with 25 skipped and no failures; the full Release solution built with 0
warnings and errors. This closes a routine source-level catalog-search gap;
live-provider fulfillment, representative-data acceptance, and installed
handoff remain open.

**Maintainer/operator acceptance — 2026-10-22:** The project maintainer,
provider maintainer, and operator explicitly accepted the BookshelfNG
retirement effort and its migration and provider/platform handoff decisions,
including preserving source-monitored ambiguous works as monitored in both
ChaptarrNG media catalogs. This closes the internal human sign-off gate; no
further user approval is required for that sign-off. It does not replace
technical evidence for feature parity, provider fulfillment, or installed
update-channel handoff.

**Legacy-ID book-search command compatibility — 2026-10-23:** The API command
endpoint now resolves `BookSearchCommand` IDs through the migrated
`BookshelfLegacyIdentityMap`. An unscoped request searches every target row
created when a legacy book splits by format; an ebook or audiobook Readarr
facade searches only its matching row. Regression cases cover both paths. The
Release Core suite passed 4,762 tests, skipped 25, and failed 0; the full
Release application solution built with 0 warnings and 0 errors. This closes
the server-side legacy-ID command path; converted-data acceptance and live
SeerrNG fulfillment remain open.

**Legacy-ID book command compatibility — 2026-10-23:** Generic API commands
for book search, refresh, cover downloads, and supported metadata pushes now
resolve migrated BookshelfNG IDs to every target format row when unscoped, and
respect the ebook or audiobook Readarr facade when scoped. A single-book
`RefreshBook` request that splits by format is queued as `BulkRefreshBook`;
cover downloads process all resolved rows. The direct book cover-download
endpoint follows the same expansion. Regression coverage includes command
mapping and cover processing. The Release Core suite passed 4,771 tests,
skipped 25, and failed 0; the full Release application solution built with 0
warnings and 0 errors. Other client routes, converted-data acceptance, and live
SeerrNG fulfillment remain open.

**Structured migration field-loss protection — 2026-10-23:** ChaptarrNG commit
`5dad84d17c2ed6818d25eeab249e52783927f2a9` extends the importer's fail-closed
column mapping checks to all copied or transformed structured tables, including
profiles, roots, authors and metadata, books and editions, media files, series,
blocklists, history, and provider configuration. Source-only values are allowed
only where the importer explicitly translates them, including per-format book
monitoring. Parameterized regression cases inject a non-empty unknown field
into 21 tables and verify conversion stops before promotion while leaving the
source database byte-identical and creating no backup. The Release Core suite
passed 4,758 tests, skipped 25, and failed 0; the commit-hook Semgrep scan found
0 findings. This closes another silent data-loss path; it does not establish
complete migration fidelity, live provider behavior, feature parity,
representative-data acceptance, or release/channel readiness.

**Converted matching threshold application — 2026-10-23:** ChaptarrNG commit
`f783e27c` adds an integration case through the real `FileMatchingService` for
the converted-install compatibility marker. It confirms the saved BookshelfNG
threshold changes whether a candidate is accepted (51% accepts; 70% rejects),
while a fresh-install matcher ignores that legacy threshold even when set to
99%. The Release Core suite passed 4,760 tests, skipped 25, and failed 0;
commit-hook Semgrep found 0 findings. This validates the migrated setting's
runtime wiring with synthetic match data. Representative converted-library
acceptance and full feature parity remain open.

**Built-in provider settings preservation matrix — 2026-10-23:** ChaptarrNG
commit `37703f82204fd993f49ea37ed11020c6a5435af5` adds a migration fixture for
all 15 built-in BookshelfNG download clients, six import-list providers (with
the `ReadarrImport` to `ChaptarrImport` alias covered separately), and 24
notifications. Each row carries representative populated settings; preview
must report no blocking issues, and apply must preserve its implementation,
settings contract, and serialized settings exactly. Existing fixtures cover
all ten indexers and the metadata settings adapter. The full Release Core suite
passed 4,759 tests, skipped 25, and failed 0. This broadens source-level
provider migration coverage; live fulfillment, real credentials and operator
data, complete semantic parity, and installed-channel acceptance remain open.

**Provider migration fail-closed coverage — 2026-10-23:** ChaptarrNG commit
`2ae3d0959bf8da7e6e1999bf69cc5d2f5b611f8d` rejects non-empty source columns
without a target mapping in `Indexers`, `ImportLists`, and `Notifications`,
while preserving the explicit `ProfileId` and `OnTrackRetag` conversions. The
indexer migration fixture now covers all ten built-in BookshelfNG indexer
contracts with representative saved settings and verifies exact settings
preservation. The Release Core suite passed 4,740 tests, skipped 25, and failed
0; commit-hook Semgrep found zero findings. Exact-head hosted Build
[`40010572006`](https://github.com/snapetech-org/chaptarrng/actions/runs/40010572006),
Security Analysis
[`40010572313`](https://github.com/snapetech-org/chaptarrng/actions/runs/40010572313),
and standalone-asset validation
[`40010572013`](https://github.com/snapetech-org/chaptarrng/actions/runs/40010572013)
were queued or pending. This closes a source-level silent-column-drop path;
full provider mapping, live fulfillment, representative operator data, and
release/channel acceptance remain open.

**Latest ChaptarrNG validation — 2026-10-23:** Application-code snapshot
`a2d0af9fc31a94369bf091189b10b63f8ab1dc7e` (before documentation-only
`ec83157eee0c3d14b223565418dee7d913376cdd` and
`65867fa78ea95fa39887e94f4483fe77ffa70f6e`) passes the Release Core suite
(4,736 passed, 25 skipped, 0 failed). The pinned-tool M4B integration passed
8/8 with no skips, covering merge/chapter handling, Move/Copy behavior, and
failure fallback. The Core suite also covers simulated restart recovery from a
persisted conversion job with its output and manifest, then queues the import
sweep; actual interrupted-media recovery remains open. A source-built Compose
smoke against SeerrNG
`bc580332ab49a7efa87e3d78b680a2a0cf1abbd0` converted the populated synthetic
BookshelfNG schema-158 fixture during startup; SeerrNG read both format
libraries through the restricted key, administrative and unrelated access
were denied, and a pending request completed create/poll/cancel. Hosted exact-
head build and security workflows for `65867fa78ea95fa39887e94f4483fe77ffa70f6e`
are still queued. Release run
[`40003167055`](https://github.com/snapetech-org/chaptarrng/actions/runs/40003167055)
was cancelled before publication; public `v0.9.977` remains latest. These
synthetic checks do not establish live-provider fulfillment, representative
operator-data acceptance, installed rollback, or update-channel handoff. No
release or cutover was published.

**Prior parity checkpoint — 2026-10-23:** Application-code
commit `4b57b479d73c844db19278233e25efe94509e99b` is on `main`; it includes
migrated-audiobook release and pending-queue ranking compatibility, orders
inferred audiobook parts by complete embedded disc/track tags, and preserves
release-push provenance through decision evaluation. It also handles pushed
releases whose indexer name is absent. The Release Core suite passed 4,709
tests with 25 skips and no failures. Commit-hook Semgrep found zero findings,
and push readiness checks passed. On upstream PR
[#114](https://github.com/Chaptarr/chaptarr/pull/114), exact-head Semgrep
([39972277607](https://github.com/Chaptarr/chaptarr/actions/runs/39972277607))
and Trivy
([39972277647](https://github.com/Chaptarr/chaptarr/actions/runs/39972277647))
are `action_required` with no jobs started. GitHub rejected this agent's
workflow-approval request because repository-admin rights are required, so
hosted validation remains open. The public `v0.9.977` release is unchanged;
no release or cutover was published.

**BookshelfNG API contract and release-search compatibility — 2026-10-23:**
ChaptarrNG restores the legacy `/api/v1/release` array response and moves the
interactive search wrapper to `/api/v1/release/search`; the frontend now calls
the wrapper route. Legacy book IDs mapped to both ebook and audiobook rows are
searched across both rows unless the request has a format-scoped facade
context. Generated OpenAPI now documents encrypted backup passphrase and
multipart restore requirements, encrypted download responses, and author/book
creation response contracts. The Core suite passed 4,734 tests with 25 skips;
the six backup-controller fixtures, including a bound multipart file, passed.
The frontend suite passed 126 tests, typecheck passed, and the changed frontend
files passed ESLint. This closes these identified source/API-contract gaps; it
does not establish complete feature parity, representative-data acceptance,
hosted exact-head validation, provider fulfillment, or installed-channel
handoff. No release or cutover was published.

**BookshelfNG import-list provider migration — 2026-10-23:** A read-only
provider-source comparison found that BookshelfNG's built-in `ReadarrImport`
was the only import-list implementation without a target-name match. ChaptarrNG
now maps it to `ChaptarrImport` and translates the identical
`ReadarrSettings`/`ChaptarrSettings` contract names while preserving the saved
URL, API key, profile IDs, tag IDs, and root-folder paths. The migration fixture
previews and applies that mapping, then deserializes the result as the target
settings type. The full Release Core suite passed 4,735 tests with 25 skips and
no failures. This closes the identified source-level provider mapping gap; it
does not establish live remote-instance behavior, representative operator-data
acceptance, hosted exact-head validation, or installed-channel handoff. No
release or cutover was published.

**BookshelfNG startup diagnostics — 2026-10-23:** ChaptarrNG restores the
startup diagnostics for build/runtime metadata, HTTP binding, and packaged UI
entry-point presence. Existing `BOOKSHELF_IMAGE_*` and `BOOKSHELF_CONTAINER`
values remain readable, with CR/LF sanitized before logging. A host-lifecycle
fixture verifies legacy image metadata, sanitized output, HTTP settings, and
the missing-UI report. The full Release Core suite passed 4,736 tests with 25
skips and no failures. This restores the identified operational diagnostics
path; exact-head hosted validation, representative operator-data acceptance,
and installed-channel handoff remain open. No release or cutover was
published.

**Current source-built SeerrNG migrated-ID and lifecycle smoke — 2026-10-22:**
ChaptarrNG commit `e5788975b0071aae0d1990460461b4321dde0e5a` built through
`Dockerfile.build` and passed both checked-in smoke scripts against SeerrNG
source commit `d0072cee5176554601108209296038f855ac740c`. Normal startup
converted the populated synthetic migration-158 fixture. SeerrNG's compiled
`ReadarrAPI` resolved legacy BookshelfNG book ID 30 to audiobook ID 30 and
ebook ID 31, and returned their corresponding file IDs 50 and 51. Restricted-
key capability and library reads passed; administrative and unrelated
command access was denied, while the global key retained administrative
access. The pending-import add, poll, cancellation, and terminal-state read
also passed. The Release Core suite passed 4,663 tests with 25 skips and no
failures. Records, keys, profiles, and media placeholders were synthetic.
This closes local source-build and migrated-ID smoke coverage; live successful
provider fulfillment, availability and history parity, production settings,
real operator records, full SeerrNG workflow acceptance, and installed-channel
handoff remain open. No release or bridge was published. See the
[SeerrNG integration evidence](SEERRNG_INTEGRATION.md).

**Historical channel rehearsal (2026-10-24).** BookshelfNG `0.4.21.71` and ChaptarrNG `v0.9.977` were the latest public app releases at that checkpoint. ChaptarrNG [main](https://github.com/snapetech-org/chaptarrng/tree/main) contained a candidate-only Debian bridge builder. An isolated dpkg round-trip against the checksum-verified public BookshelfNG `.71` package preserved a modified config and data canary with dependency checks bypassed and systemd mocked. A separate Debian Trixie apt transaction used that actual package and the local `.977` candidate, resolved dependencies (including `libicu76` and `ca-certificates`), installed the CA bundle, upgraded and downgraded without a conffile prompt, and preserved the canaries. In a systemd-running Trixie container, the actual `.71` service upgraded to the local candidate, restarted, and passed `/ping`; after restoring the pre-upgrade database copy, downgrade to `.71` also restarted and passed `/ping`. This used a fresh empty `.71` database and synthetic canaries, not operator data. No compatibility package or migration bridge had been published. Release publication remained frozen and publisher readiness variables remained unset at that checkpoint. The current handoff status appears at the top of this document. The detailed feature inventory remains in the [BookshelfNG parity roadmap](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/bookshelfng-parity-roadmap.md).

**BookshelfNG JSON-only API guard parity — 2026-10-22:** BookshelfNG commit `55850043cfe28d414657525cc73bdbcf256f11d2` added `[Consumes("application/json")]` to six state-changing actions across Author Editor, Bookshelf, Book Editor, and Manual Import. ChaptarrNG commit `12993c122404caa83bb1e7d20f1b6beecac8e675` ports all six guards; `BookshelfJsonContentTypeParityFixture` checks each action. The exact-head product solution and Core test project built with 0 warnings and errors, and the Core suite passed 4,648 tests with 0 failures and 25 skips. This closes the identified request-content-type source gap; full semantic parity and hosted exact-head checks remain open.

**Matcher parity validation — 2026-10-22:** The converted-install scorer now follows BookshelfNG's per-format tag fields and legacy identifier, year, narrator, and format comparisons; EPUB identifier extraction uses the legacy ISBN priority. ChaptarrNG's stronger identity and ambiguity checks remain in the pipeline. The full Release Core test project passed 4,604 tests with 0 failures and 19 skips; a separate Chromium browser integration run passed 3/3. PostgreSQL/live-service checks and real M4B checks remain skipped without their configured connection or toolchain. These are code checks, not scoring acceptance against a representative converted library. The public `v0.9.977` release is unchanged.

**Shared-root author-path regression fix — 2026-10-22:** Startup repair and author-path building now preserve separate audiobook and ebook directories when both use the same configured root and that root is also the author's current path. Focused regression tests cover both the builder and startup repair. The full Core test project passed 4,607 tests with 0 failures and 19 skips. Rebuilt local AppImage and Flatpak candidates both preserve the paths during installed-channel startup. Exact-head hosted validation, representative operator data, and provider acceptance remain open.

**Desktop installed-channel rehearsal — 2026-10-22:** Checksum-verified public BookshelfNG `0.4.21.71` AppImage and Flatpak packages were installed in task-scoped locations, then replaced with rebuilt local ChaptarrNG `.978` candidates under the same desktop identity and data path. Both migration previews left the source DB unchanged; normal startup converted the synthetic schema-158 fixture to schema 121 (four authors, nine books; SQLite integrity `ok`, no foreign-key violations), retained a source backup, and preserved the three authors' distinct ebook and audiobook paths when their shared root equaled the author path. No startup path-repair entries appeared. Restore previews left both databases unchanged; explicit rollback recovered the BookshelfNG database byte-for-byte. The public `.71` packages were restored and each started against schema 158 with `/ping` HTTP 200. The first local candidate build exposed the path collapse; the rebuilt candidate includes the regression fix. This rehearsal proves package identity and rollback behavior against a synthetic fixture, not operator-data acceptance or a live published update channel. Exact-head hosted validation and representative-data review remain open. No candidate or bridge was published.

**PostgreSQL default-name compatibility — 2026-10-22:** The source candidate now resolves implicit BookshelfNG `readarr-*` PostgreSQL database names, carries the main/log/cache selection across restart through a dedicated migration marker, preserves explicitly configured names, and stops on ambiguous populated schemas. The full Core suite with PostgreSQL enabled passed 4,632 tests with 0 failures and 11 skips; focused PostgreSQL 17 selector, inspector, and converter checks passed 22/22, including live database-name selection and restart behavior. Separate operator data and installed PostgreSQL handoff remain unverified. No release or bridge was published.

**Windows PostgreSQL installer path — 2026-10-22:** The candidate installer checks for `pg_dump`/`pg_restore`, requires an explicit `PostgresUser`, rejects other connected PostgreSQL clients after stopping BookshelfNG, stores verified custom-format dumps for each unique main/log/cache database, rechecks client sessions after preview review, and restores validated dumps transactionally before restoring BookshelfNG files and service. Automatic rollback skips PostgreSQL restore until the mutating converter has started, so preview cancellation cannot overwrite later database changes. The trusted local bulk suite passed 2/2 validation tasks with 4,688 passed, 0 failed, 22 skipped, and 2 inconclusive NUnit cases. Its real-tool PostgreSQL backup/restore fixture is assigned to the hosted disposable-PostgreSQL job; exact-head hosted validation and an end-to-end Windows PostgreSQL service rehearsal remain outstanding. The installer stays candidate-only and no release or bridge was published.

**Author-folder, import-matching, and discography parity — 2026-10-22:** Disk scans now honor BookshelfNG's existing empty-author-folder settings within both the configured root and selected scan path, apply configured folder permissions, and respect selected-author scans. Import matching removes the exact `_UNPACK_` and `_FAILED_` download markers from derived author-folder evidence. Discography releases are accepted only when every included book has a release date that has arrived; ordinary multi-book packs remain rejected. The trusted local validation engine passed both validation tasks: 4,697 passed, 0 failed, 22 skipped, and 2 inconclusive NUnit cases; validation contracts passed. The real-tool PostgreSQL fixture remains assigned to hosted disposable-PostgreSQL validation. These code and synthetic-test results do not establish representative operator-data acceptance, provider fulfillment, exact-head hosted completion, or cutover readiness. No release or bridge was published.

**Ambiguous-media monitoring preservation — 2026-10-22:** When BookshelfNG has no edition that identifies a book's format, the automatic migration fallback now preserves a monitored source book as monitored in both media catalogs; source-unmonitored books remain unmonitored in both. Operators can still use the per-book media map to choose one format. Regression fixtures cover monitored and unmonitored records. The trusted local validation engine passed both tasks with Node 26.12.1 and .NET 10.0.402: 4,722 active cases, 4,698 passed, 0 failed, 22 skipped, and 2 inconclusive; one hosted PostgreSQL task was excluded. The application solution and Core test project both built with 0 warnings and 0 errors.

**Populated active-installation SQLite rehearsal — 2026-10-22:** A private SQLite online backup was taken from the running BookshelfNG installation on `kspls0`; the source service remained running and its appdata was not modified. The snapshot had schema 158, 39 tables, and 143,790 rows. ChaptarrNG preview passed and left its input byte-for-byte unchanged. Applying the current automatic fallback to a separate disposable copy migrated it to ChaptarrNG schema 121 with 1,393 authors, 15,968 media-specific books, 85,192 editions, and 27,532 book files; SQLite integrity and foreign-key checks passed, native Hardcover identities were populated, and the source backup was retained. The 80 ambiguous works became 160 media records: 65 source-monitored works remained monitored in both formats, and 15 source-unmonitored works remained unmonitored in both, with zero monitoring mismatches. All 27,532 book-file path/size/quality/part records, the three root-folder paths, and all author paths matched the source. Four indexers, both download clients, and the notification row retained their saved settings and contracts; the disabled download client remained disabled. The global metadata adapter was mapped and kept its settings and disabled state. The review sidecar, which contains library names, remained private and out of Git. Restore preview left the converted database unchanged. Explicit rollback restored the retained BookshelfNG backup byte-for-byte and matched the original snapshot's logical rows across all 39 tables, while retaining the converted database separately. This validates populated-data conversion and database rollback, not live media reads, provider requests, or installed update-channel handoff. No release or bridge was published.

**Windows application build compatibility — 2026-10-22:** The `net10.0-windows` application target now overrides `OnFormClosing(FormClosingEventArgs)` instead of the obsolete WinForms `OnClosing(CancelEventArgs)`, preserving cancellation behavior and avoiding compiler warning CS0672 under warnings-as-errors. The complete application solution built for all target frameworks with zero warnings and errors. This is an internal build fix; no release was published.

The exact revision-pinned path audit stays canonical in BookshelfNG's
maintainer ledger: the
[consolidation report](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md),
[generated inventory](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation-inventory.json),
[reviewed path mappings](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation-review-map.json),
and [feature dispositions](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation-disposition-map.json).
These path records organize the review; they do not establish feature parity
or migration acceptance.

**Current source and audit status — 2026-10-21**

ChaptarrNG `main` implements BookshelfNG issue [#114](https://github.com/snapetech-org/bookshelfng/issues/114) as an
opt-in, disabled-by-default setting. Migration preserves the saved choice and
maps BookshelfNG's configured ebook/audiobook roots to ChaptarrNG defaults
when ChaptarrNG defaults are empty. Existing authors and files are untouched.
Current source also restores whole-author move updates, paginates Goodreads
series imports, and improves Manual Import's author, book, edition, and quality
selectors with empty-state guidance, retry, stale-choice protection, and
disabled submission when no quality options exist. Add New and narrator search
failures also announce errors and provide retry actions. Interactive release
search and organize, retag, and convert previews also expose retry after errors.
Manual Import hides stale rows and disables Import after a preview error; File
Browser can retry the current directory. Activity lists, author history,
book-edit edition and book-file detail/quality loading, Calendar, backup lists,
Wanted pages, and conversion settings also retry after load errors. Startup
errors identify the failed data source and retry failed setup requests when
browser storage works. All 36 frontend CJS test files passed on 2026-10-23: 126 passed, zero
skipped, and zero failed. Focused migration, metadata, move, and CLI checks are
recorded below. An earlier application migration fix is commit
[`c2535e71`](https://github.com/snapetech-org/chaptarrng/commit/c2535e712aa7c1ecbf29347a7e1e9d477bb870e3).
Its exact-head Build
[39561683854](https://github.com/snapetech-org/chaptarrng/actions/runs/39561683854)
passed Linux, macOS, Windows, PostgreSQL migration, and runner-receipt checks.
The latest matching compatibility change is commit
[`97c1284f`](https://github.com/snapetech-org/chaptarrng/commit/97c1284fb3c3ce8670546a1059dd8fa1aefeeb92).
It keeps BookshelfNG's numeric `BookImportMinimumMatchPercent` active for the
fuzzy RSS-title fallback and restores its weighted score gate for migrated
installs, using BookshelfNG's 70% default when the setting was never saved.
Fresh ChaptarrNG installs leave that compatibility gate disabled. The mapped
`BookMatchingStrictness` remains an additional safety policy, and ChaptarrNG's
newer identity and ambiguity checks mean the combined decision pipeline is not
identical in every case. Focused matching, importer, and RSS regression tests
passed 68/68 locally. All eight PostgreSQL migration fixture cases also passed
against an isolated local PostgreSQL 16 container; coverage includes normal
startup, source schemas 1, 13, 22, 33, and 158, restore, and rejection of newer
or view-based sources. The fixture verifies the compatibility marker and
preserved BookshelfNG backup. This remains synthetic migration evidence, not
operator-data acceptance. Hosted cross-platform exact-head and standalone-asset
validation remain pending. The current source is unpublished; `v0.9.977`
predates these changes and the retirement release freeze remains active.

The canonical
[audit report](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md)
records exact source and target revisions, path dispositions, and remaining
review. A private rehearsal on current ChaptarrNG `main` has now passed preview,
migration, restore preview, and rollback for two populated migration-158 SQLite
snapshots; see the [rehearsal record](MIGRATE_FROM_BOOKSHELFNG.md#populated-sqlite-migration-and-restore-rehearsal-2026-10-21).
This closes conversion and database rollback evidence for those two data sets.
Runtime retirement stays deferred while broader normal-startup acceptance,
feature parity, provider fulfillment, SeerrNG workflows, and installed
update-channel handoff remain open. Maintainer/operator sign-off is recorded
above; keep BookshelfNG in service until the technical readiness gates close.

The current source and target pins, disposition counts, and remaining path
review live in the canonical BookshelfNG
[consolidation ledger](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md).
That audit suite passes 31/31. Path dispositions organize the review; they do
not establish runtime parity or migration acceptance.

The AUR and RPM source package paths now map to ChaptarrNG's legacy identity
templates. Their local package rehearsals used synthetic migration data;
representative data, live-feed handoff, and real-host service acceptance remain
open.

Standalone archive work covers all ten BookshelfNG runtime identifiers in the
ChaptarrNG source release matrix. The exact-head no-publication run
[39582093706](https://github.com/snapetech-org/chaptarrng/actions/runs/39582093706)
passed eight archive targets; Linux x86 and FreeBSD initially failed while
copying custom runtime packs over preexisting SDK files because `cp -a` tried to
restore their ownership and timestamps. ChaptarrNG `main` skips those
attributes. The follow-up no-publication matrix
[39595241427](https://github.com/snapetech-org/chaptarrng/actions/runs/39595241427)
then failed because the builder expected SDK 10.0.402 to include reference packs
at 10.0.12. The current source change collects the installed SDK pack versions
and pins custom-runtime publishing to 10.0.12; focused checks pass locally, and
hosted validation is pending. The matrix creates only temporary CI artifacts; no
release, package, image, or migration bridge is published. Keep BookshelfNG in
service until the full migration and channel acceptance gates close.

The next exact-head archive run,
[39628741566](https://github.com/snapetech-org/chaptarrng/actions/runs/39628741566),
exposed two additional build defects: the runtime installer replaced the Linux
x64 SDK host with the Linux x86 or FreeBSD apphost, and the custom NuGet mapping
routed every RID's runtime packages to a local feed that contains only those
two targets. The source now preserves the SDK host and maps only the three
runtime package IDs for the selected RID. The focused installer and mapping
tests pass, and local Linux x86 and FreeBSD x64 archives built with valid
checksums; hosted exact-head confirmation is pending.
The current path ledger records the candidate Debian bridge at its exact pinned
source and target revisions. The published `v0.9.977` assets remain unchanged;
no compatibility package, release, or channel handoff is authorized.

The current migration fix is ChaptarrNG commit
`c2535e712aa7c1ecbf29347a7e1e9d477bb870e3`. It maps legacy
`MetadataFiles.Consumer=Readarr` rows to `LegacyAudiobookshelfMetadata`; the
regression fixture covers the mapping. A released BookshelfNG `0.4.21.71`
binary and the Linux artifact from Build
[39561683854](https://github.com/snapetech-org/chaptarrng/actions/runs/39561683854)
completed a synthetic schema-158 SQLite preview, apply, and restore rehearsal.
The target passed integrity and foreign-key checks, preserved the fixture's
linked records, and rolled back to the retained source database. The backup is
logically equivalent to the source across its SQLite dump and all 39 table row
counts; its file hash differs after SQLite checkpoint/WAL normalization.
Build run 39561683854 completed successfully on Linux, Windows, macOS, and
PostgreSQL; native-runner receipt reconciliation passed, and the release-note
contract was skipped for this internal fix. This closes the reproduced sidecar blocker for
the candidate fixture only; operator data, provider fulfillment, semantic
parity, and installed-channel acceptance remain open, so the release freeze
continues.

An expanded local source-build rehearsal on 2026-10-21 used the repeatable
multi-record schema-158 fixture. Preview left the source hash and logical rows
unchanged; explicit apply preserved provider settings, format-specific
monitoring, unresolved works, sidecars, history, and linked records; integrity
and foreign-key checks passed; and rollback restored the source while retaining
the converted database separately. A conflicting selected-edition case failed
before promotion. This is synthetic Linux SQLite evidence only. It does not
close representative operator data, PostgreSQL operator data, complete
semantic parity, provider fulfillment, or installed-channel acceptance. See
the [expanded fixture results](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md#expanded-multi-record-source-build-rehearsal-2026-10-21).

**Historical source validation (2026-10-20)**

ChaptarrNG PR #131 merged to `main` as
`d2bcba7953a14d2d726f47005a660314cc5e4c26`. The source head at this
2026-10-20 checkpoint was `f30dcec8c882c0169a544eee337f1d628e76e5d4`. Runtime application code includes
the related-format book actions and combined Bookshelf **Formats** view from
`c75ecbb37c5708441219887f7180d5ab10ee194d`, plus the Books index **Both**
filter with per-record format status, monitoring, and quality profiles. The
latest changes reuse an existing matching catalog book during local add,
preserve an explicit manual edition selection, resolve bare numeric
Hardcover/Goodreads lookup IDs through the Readarr facade, and pass the
selected row format into the author editor. Root Folders now offers a
read-only preview and separate confirmation for filling eligible existing
author paths; confirmation rechecks current settings, queues refreshes, and
does not move files. Author profile reset is unavailable when a selected root
has no configured default. Frontend regression tests cover row-format editing,
missing defaults, preview/confirmation, empty results, and retryable errors.

Exact-head Build
[39520269403](https://github.com/snapetech-org/chaptarrng/actions/runs/39520269403)
passed Linux, Windows, and macOS build/test jobs, PostgreSQL migration smoke,
and native-runner receipt reconciliation. Security Analysis, Semgrep CE,
Trivy, and NuGet submission passed. No CodeQL run is attached to this source
commit; the release-note contract was skipped on the main push. Local frontend
CJS tests pass 57/57, typecheck and translation-key checks pass, and changed
CSS passes Stylelint. The current source is unpublished and does not approve
production cutover.

At the 2026-10-20 checkpoint, BookshelfNG `0.4.21.71` and ChaptarrNG
`v0.9.977` were the latest public releases. No release, package, image,
installer, or migration bridge had been published. The source was not
approved for production cutover.

Current ChaptarrNG `main` applies the author freshness policy to book-added
events while retaining the author-scoped rescan when metadata is skipped or
unchanged. This keeps the remote metadata fetch bounded without dropping the
scan that can discover files already on disk. The exact-head build above
includes this behavior; see the
[author metadata refresh guide](AUTHOR_METADATA_REFRESH.md). The legacy
source's complete author metadata policy is documented in
[BookshelfNG's author refresh guide](https://github.com/snapetech-org/bookshelfng/blob/main/docs/AUTHOR_METADATA_REFRESH.md).

The current ChaptarrNG `main` source also ports BookshelfNG's HTTP boundary
controls: explicitly trusted proxy IPs/CIDRs, opt-in CORS origins, per-client
fixed-window API rate limits, a configurable 16 MiB default request-body cap,
the request-correlation response header, and a switch to disable API-key query
authentication. `CHAPTARR__AUTH__ALLOWAPIKEYQUERY=false` rejects `?apikey=`;
when unset, `BOOKSHELF_ALLOW_API_KEY_QUERY=false` remains supported. ChaptarrNG
settings take precedence while the legacy `Readarr:Server` keys remain
accepted. The backup restore endpoint keeps its separate 1 GiB upload
allowance. The restricted SeerrNG key cannot read `/config/host`,
`/config/hardcover`, `/config/development`, or `/system/health`; history and
book-file reads require one positive book ID. The ChaptarrNG service key takes
precedence over `BOOKSHELF_SEERRNG_API_KEY`, which stays supported as a
fallback when the new key is unset. This source change is not in the public
`.977` release; the release
freeze and production acceptance gates remain in force. See the
[HTTP security settings](HTTP_SECURITY.md).

The same candidate now maps BookshelfNG's `BOOKSHELF_OIDC_*` environment
settings into ChaptarrNG when saved ChaptarrNG OIDC values are empty. It
preserves the provider credentials, scopes, allow-lists, explicit
allow-any-verified choice, private-issuer setting, and a validated HTTPS
`/signin-oidc` callback. Without an allow-list or the explicit
allow-any-verified option, sign-in is denied, matching BookshelfNG's default.
Commit `f3eaaa53` also retains BookshelfNG's required `email` scope when a
custom scope list omits it. The focused OIDC regression suite passed 26/26 and
staged Semgrep found no issues. This source-only compatibility change is not in
`.977`; preserve the variables through any deployment migration and keep the
release freeze in place.

Current ChaptarrNG `main` also matches BookshelfNG's OIDC backchannel target
policy: issuer hosts resolving to private or reserved addresses are rejected
unless `BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true`. The configured proxy and
certificate path is retained, redirects remain disabled, and response bodies
are capped at 1 MiB. Its paged books route now defaults to 50 records, sorts
by stored title, and includes each author and book links when callers omit
`include`. For migrated installations, two audiobook release candidates also
use BookshelfNG's quality/revision, custom-format, protocol, indexer, peer,
book-count, age, and rounded-size order. Fresh installs and mixed-format
comparisons retain ChaptarrNG's priority. These candidate changes are not
present in public `.977`; exact-head hosted CI and installed-channel acceptance
remain separate gates.

Current ChaptarrNG `main` also accepts raw `.db` and `.xml` BookshelfNG backup
uploads. The `.xml` suffix is a legacy filename; the contents must still be a
SQLite database. Before staging, ChaptarrNG checks SQLite integrity and
database lineage, then applies the BookshelfNG source-version and table
boundary checks. This closes the upload-extension gap in the current source;
the raw formats are not supported by published `v0.9.977`, and the retirement
release freeze remains active.

The current source also includes a combined Bookshelf **Formats** view. It
loads audiobook and ebook records together, groups only records with matching
Hardcover, Goodreads, or Open Library work IDs, and keeps per-format progress,
details, and monitoring. This source is not in `v0.9.977`; browser checks on a
representative library remain open.

Earlier exact-head result for `771a26e63111114990b926df0f8bd0918ff9cf0a` was Build
[39428515452](https://github.com/snapetech-org/chaptarrng/actions/runs/39428515452),
attempt 4: all platform suites, PostgreSQL migration smoke, and receipt
reconciliation passed. The release-note check was skipped because no release
was prepared. Publication remains frozen; a passing source build does not close
the operator-data, parity, or installation-channel gates.

The regression fixture first failed on exact-head commit `36623faf` because
of missing imports and an incorrect attribute property. Follow-up `aac32822`
fixed the imports but exposed the explicit request-size metadata interface;
`29852b13` now reads `IRequestSizeLimitMetadata.MaxRequestBodySize`. Exact-head
Build [39394240337](https://github.com/snapetech-org/chaptarrng/actions/runs/39394240337)
passed Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. Windows passed the installer fingerprint, service upgrade,
rollback, cleanup, and selected-data-directory checks. Security analysis,
Semgrep CE, Trivy, and dependency submission passed; no release-note job ran
because no release was prepared.

The Linux artifact from that exact head converted the checked-in synthetic
schema-158 database on normal startup to schema 121 and retained an
integrity-checked source backup. SeerrNG read both migrated formats, verified
Hardcover identities and restricted-key boundaries, and completed a synthetic
pending-import cancellation. The fixture media files are placeholders; this
is not operator-data or media-processing acceptance.

**Native Hardcover provider parity review (2026-10-20):** A line-by-line
comparison of the BookshelfNG and ChaptarrNG proxy implementations found
matching request pacing, retries, rate-limit cooldown, daily quota, caching,
paging, batching, and GraphQL mapping logic. Adaptations are limited to product
identity, the narrator resource shape, error text, and ChaptarrNG's saved
`HardcoverApiToken` fallback versus BookshelfNG's `HardcoverAuth`. ChaptarrNG
commit `1f56de51` adds regression tests for a 429/`Retry-After` pause, the
interactive-search quota reserve, and work-detail cache reuse. Exact-head Build
[39413811593](https://github.com/snapetech-org/chaptarrng/actions/runs/39413811593)
passed Linux, macOS, Windows, PostgreSQL migration smoke, and native-runner
receipt reconciliation. Windows also passed the installer fingerprint,
service upgrade/rollback, cleanup, and selected-data-directory checks. This
closes the source comparison only. Live Hardcover calls and representative
converted-data acceptance remain open, and no release or migration bridge is
enabled.

ChaptarrNG application snapshot `3254c15b5e964dae513c6d16758e8b749a77db4b`
passed exact-head Build
[39387591772](https://github.com/snapetech-org/chaptarrng/actions/runs/39387591772)
on Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. Security analysis, Semgrep CE, Trivy, dependency submission,
and push checks passed; the release-note contract was skipped for that
docs-only source update. The separate GHAS Semgrep OSS check on PR #131 remains
failed with 12 reviewed SQL-format alerts and 3 fixed-fixture path warnings;
these are documented exceptions, not a passing check. BookshelfNG audit-tool
commit `9fcc0aea` passed Build
[39386658595](https://github.com/snapetech-org/bookshelfng/actions/runs/39386658595).
Unraid main `296caed2` passed CI
[39387608787](https://github.com/snapetech-org/bookshelfng-unraid/actions/runs/39387608787)
and package-sync, security, and release-note checks.

The current BookshelfNG path inventory, exact revisions, dispositions, and
review maps are maintained in the
[consolidation report](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation.md).
The current inventory's revision pins, dispositions, and open review are
recorded in the canonical report; these path-level decisions are not proof of
feature behavior. No
runtime feature has been approved for retirement. The old contributor wiki stub is covered by
ChaptarrNG's guide, and the obsolete Readarr Azure Pipelines definition is
retired; both paths have explicit dispositions.

Unraid transition PR [#5](https://github.com/snapetech-org/bookshelfng-unraid/pull/5)
remains draft at `1261a9f8`. Its exact-head build, template, package-sync,
dependency, release-note, Semgrep, and Trivy checks passed; publication jobs
were skipped. Real Community Applications update/rollback and representative
data migration/rollback remain unaccepted. BookshelfNG issue
[#144](https://github.com/snapetech-org/bookshelfng/issues/144) was closed by
the maintainer on 2026-10-22. Its final body still says to keep BookshelfNG in
service and withholds release and channel authorization until the listed gates
close; closing the issue did not close those gates. Keep BookshelfNG running
and do not apply migration or start ChaptarrNG against BookshelfNG appdata
until technical readiness is complete.

**Prior application-code validation (2026-10-19):** ChaptarrNG draft PR #131
contains code commit `0e881d921dd55aa89e12747557ccaadbb2352a21`. Exact-head Build
[39313568727](https://github.com/snapetech-org/chaptarrng/actions/runs/39313568727)
passed Linux (4,460 passed, 0 failed, 13 skipped, 2 inconclusive), macOS
(4,455 passed, 0 failed, 18 skipped, 2 inconclusive), and Windows (4,421
passed, 0 failed, 21 skipped, 33 inconclusive) suites. PostgreSQL migration,
release-note, native-runner receipt, and Linux pinned-tool M4B checks passed.
CodeQL [118269449163](https://github.com/snapetech-org/chaptarrng/runs/118269449163),
security/dependency scan
[39313569305](https://github.com/snapetech-org/chaptarrng/actions/runs/39313569305),
dependency review
[39313568981](https://github.com/snapetech-org/chaptarrng/actions/runs/39313568981),
NuGet submission
[39313556294](https://github.com/snapetech-org/chaptarrng/actions/runs/39313556294),
and Helm resource-identity validation
[39313569136](https://github.com/snapetech-org/chaptarrng/actions/runs/39313569136)
passed. Disposable-fork validation
[39313569934](https://github.com/snapetech-org/chaptarrng/actions/runs/39313569934)
was skipped by its external-fork gate.

The exact-head Linux artifact also passed a populated synthetic migration and
SeerrNG smoke against SeerrNG `86c6908e53777fd6cdab525bab56abf27176e3b4`:
normal startup converted the migration-158 fixture; SeerrNG read both migrated
formats and their Hardcover identities; the restricted key was denied
administrative and unrelated command routes; and the real `ReadarrAPI` created,
polled, and cancelled a synthetic pending request. See the detailed evidence
below. This does not close successful provider fulfillment, representative
operator data, semantic parity, real media/M4B acceptance, installed rollback,
or installer/channel handoff. Maintainer/operator sign-off is recorded above.
No release or bridge was published.

The Windows standalone handoff remains candidate-only. BookshelfNG's WPF
installer still installs its own Readarr service. The release workflow now
builds a versioned ChaptarrNG migration installer from the exact Windows
release payload and attaches it only when the migration-bridge and both
release-readiness gates are enabled; no such asset is currently published.
The installer supports SQLite and has a source path for PostgreSQL. Commit
`43f80580` adds preservation of the existing service identity for built-in
accounts, local or domain users, and group Managed Service Accounts; it securely
prompts for credentials when required and rejects Readarr-specific virtual
service accounts. Exact-head Build 39365402673 passed the SQLite service
round-trip with injected-health-failure rollback, retry, explicit restore,
deferred service cleanup, and selected data-directory preservation. SQLite
uses a retained integrity-checked backup and holds a read-only PRAGMA
data_version guard through preview review. PostgreSQL uses an independent
schema dump and client-session preflight, but its installed Windows round-trip
is still unvalidated end to end.

**Latest Windows validation (2026-10-24):** ChaptarrNG commit `db110883`
passed exact-head hosted Build [40087832094](https://github.com/snapetech-org/chaptarrng/actions/runs/40087832094)
on Linux, Windows, and macOS, including PostgreSQL migrations and all three
Docker compatibility candidates. The Windows installer smoke passed automatic
rollback after an injected `/ping` failure, a successful migration, deferred
removal of the old service, and explicit restore after handoff cleanup. It
verified the restored BookshelfNG data directory, database lineage and records,
and original `NetworkService` identity. This run exposed and then verified the
fix for a PowerShell 7 `Add-Type` compilation failure caused by referencing
`Win32Exception` from the dynamic helper. The smoke uses synthetic data;
representative Windows data, PostgreSQL installed rollback, live providers,
operator acceptance, and channel handoff remain open. The push-triggered
Release run stopped at its readiness preflight, and all publication jobs were
skipped; no release, package, image, or bridge was published.

The no-publication desktop-candidate check passed for AppImage/Flatpak staging,
and the container-platform check passed for the digest-pinned Cloudron, Co-op
Cloud, self-hosting Compose, and StartOS bundle. The StartOS candidate now pins
SDK 3.1.0 and requires StartOS 0.4.0.2 or newer. The candidate passed `npm ci`,
the TypeScript check, the `ncc` build, and `npm audit` with zero vulnerabilities;
the legacy-channel contract suite passed 10/10. Synthetic tool stubs verified
staged identities, image-digest propagation, and artifact checksums. These
source/build checks do not establish an external StartOS package build,
installation, update, backup, or rollback.

Keep the release freeze in place.

**Exact-head artifact-based migration-to-SeerrNG smoke (2026-10-19):** The
Linux app files from Build [39313568727](https://github.com/snapetech-org/chaptarrng/actions/runs/39313568727)
were run with the checked-in Compose fixture against disposable config and
library volumes. SeerrNG `ReadarrAPI` at `86c6908e53777fd6cdab525bab56abf27176e3b4`
read the migrated synthetic audiobook and ebook; the restricted key accessed
capabilities and both libraries while requests to `/config/host` and
`/command` were denied. The SeerrNG adapter created a synthetic unavailable-
author request, polled it, cancelled it, and observed the terminal
`Failed` / `Cancelled by user` state. The ChaptarrNG candidate reported
`0.9.978.0`. The test used disposable synthetic records and did not exercise
successful provider fulfillment, representative user data, full SeerrNG
history/availability workflows, or installed rollback. Test containers,
images, and volumes were removed after the run.

The diagnostics handoff now has startup-path fixtures for default-off behavior,
legacy opt-in only with the archived `Bookshelf.Diagnostics.dll` marker,
explicit ChaptarrNG enable/disable precedence, and a missing replacement module.
Existing reporter fixtures cover the aggregate event payload and legacy
endpoint/token fallback. These tests verify source-level selection; installed
package environment transfer remains open.

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
This validates application code only. PR #131 later merged on 2026-10-20.
It does not establish retirement acceptance; representative data, semantic
parity, installed rollback, and channel handoff remain open.

**Candidate code CI (2026-10-19):** ChaptarrNG PR #131 code commit `12f290bd` passed [Build 39256254892](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254892): Linux, macOS, Windows, PostgreSQL migration, release-note, and native-runner receipt checks passed. CodeQL, [security scan 39256254713](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254713), [dependency review 39256254710](https://github.com/snapetech-org/chaptarrng/actions/runs/39256254710), and [NuGet submission 39256277391](https://github.com/snapetech-org/chaptarrng/actions/runs/39256277391) passed. Disposable-fork validation was skipped by its external-fork gate. This is source validation, not migration or release acceptance.

**SeerrNG pending-request lifecycle probe (2026-10-19):** On the isolated Linux candidate from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), SeerrNG's real `ReadarrAPI` (`b810153c`) submitted an ebook request for a synthetic work whose author was unavailable. `addBook` received a pending ID, `getPendingAuthorImport` read it, `cancelPendingAuthorImport` cancelled it, and a second poll observed the expected terminal cancellation state: `OverallStatus=Failed` with `LastError=Cancelled by user`. The cancelled record remains queryable rather than being removed. This proves request creation, polling, and cancellation across the adapter/API boundary. It used a disposable database and synthetic IDs; provider-success behavior, retries, availability, history, migrated records, production settings, full SeerrNG operator workflow, and operator acceptance remain open. The tested app artifact is code snapshot `0612cb4c`; PR commit `d9713604` added the repeatable helper and docs, and the intervening app change is Calibre selector tie-breaking rather than a SeerrNG API change. The probe is now repeatable through `integration/seerrng-request-lifecycle.mjs` and is wired after the authorization smoke in `integration/seerrng-smoke.compose.yml`. On 2026-10-19, both scripts passed when run against the isolated artifact from Build 39239940373 and SeerrNG source `b810153c`; `node --check`, Prettier, and Compose configuration validation also passed. The earlier Compose invocation used a temporary runtime image with `--no-build`. A follow-up source-build run on 2026-10-19 built the candidate through the checked-in `Dockerfile.build` from PR head `7cfb5d87` and passed both scripts after the harness prepared its disposable `/books` volume as UID/GID `99:100`. Successful fulfillment, retries, availability/history, converted records, saved operator settings, and acceptance remain open.

**Populated migration-to-SeerrNG source smoke (2026-10-19):** Candidate
`12f290bd` built through `Dockerfile.build`, started with a shared synthetic
BookshelfNG migration-158 fixture, and converted it during normal startup.
The target reached schema 121, retained its schema-158 source backup, and
persisted `HardcoverEnabled=true` for the source `HARDCOVER=true` mode.
SeerrNG `ReadarrAPI` commit `fdc439aa9` read both migrated audiobook and ebook
records with their Hardcover identities. Capability discovery, restricted
service-key authorization, and pending add/poll/cancel also passed. The
focused importer suite passed 43/43. This is populated synthetic integration
evidence, not a private operator-data rehearsal. The `.m4b`/`.epub` files in
this migration fixture are placeholders. A separate real-tool importer check
passed six synthetic multipart M4B cases, including merge metadata, chapter
ordering, Move/Copy handling, and failure fallback. PR #131 adds synthetic embedded-chapter Move/Copy cases and a TagLib write/reprobe check. They passed on c4fa in exact-head Linux Build 39288736177. Representative audiobooks and
restart/resume remain unvalidated. The older empty-library check is superseded
only for this synthetic conversion path;
provider success, older schemas, PostgreSQL operator data, full fulfillment,
installed rollback, operator sign-off, feature parity, and channel handoff
remain open. See the [maintainer fixture report](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md)
and [SeerrNG integration guide](SEERRNG_INTEGRATION.md).

## Current releases

### Provider configuration blockers

The migration candidate preserves provider settings only when a matching
ChaptarrNG implementation, settings contract, and populated saved fields exist.
It includes the legacy Hardcover public-list and reading-status provider and
now ports BookshelfNG's OAuth-backed Goodreads bookshelf and owned-book lists
and bookshelf/owned-book write notifications. The importer preserves complete
saved credentials and blocks missing or partial OAuth settings without showing
their values. Public Goodreads shelves remain available without OAuth when a
valid user ID is saved. Goodreads API acceptance with existing credentials is
still unverified, and no new developer keys are available; this remains a
feature-parity and migration-readiness gate. See the
[migration guide](MIGRATE_FROM_BOOKSHELFNG.md#provider-configuration-blockers)
and [Goodreads API deprecation notice](https://www.goodreads.com/topic/show/21788520-api-deprecation).

The current source candidate preserves BookshelfNG's MyAnonaMouse
`useFreeleechWedge=2` (`Required`) value. ChaptarrNG now offers the same choice,
requests a wedge for eligible releases, and fails the grab if MAM does not
return a usable torrent rather than retrying unwedged. Live MAM account
acceptance remains open.

The host environment bridge also carries forward legacy Readarr and Audioarr
configuration when the matching Chaptarr value is unset. It preserves the
configured metadata catalog URL and optional Direct Download Chromium
executable override while leaving primary catalog selection explicit.

The candidate ports BookshelfNG's legacy `AudiobookshelfMetadata` OPF
consumer as ChaptarrNG's distinct `LegacyAudiobookshelfMetadata` provider.
Provider and sidecar IDs are mapped while preserving the exact `NullConfig`
contract and file/book references. Unknown metadata providers or sidecar
consumers block apply. Representative OPF libraries and live migration
acceptance remain open.

- ChaptarrNG `v0.9.977` is published with the standard image, migration tools,
  the embedded BookshelfNG SQLite migration chain, and standalone release
  archives.
- The published `.977` converter still rejects source schemas before
  migration 14 because they contain `Blacklist`, which the embedded chain
  later renames to `Blocklist`. PR #126 merged the version-aware guard and
  authentic migration-1 coverage to `main`; hosted Linux, macOS, Windows, and
  PostgreSQL migration checks passed. This fix is source-only until a later
  ChaptarrNG release.
- ChaptarrNG releases are frozen while retirement gates remain open. The
  merged release, image, and distribution workflows now require both
  `CHAPTARRNG_RELEASE_READY=true` and `BOOKSHELFNG_RELEASE_READY=true`. Keep
  both false until parity, migration/rollback, and channel acceptance are
  complete. The current `main` version bump to `.978` is source-only.
- ChaptarrNG PR #127 merged to `main` as `b3089a39`, adding selectable
  supplemental catalogs, per-field preferences, and conservative
  cross-catalog unmapped-file recovery through exact ISBN, ASIN, provider ID,
  or normalized title/author evidence. It remains source-only and is not
  available in `.977`; converted-data and settings acceptance remain open.
- ChaptarrNG PR #129 merged to `main` as `949ae3dd`, adding opt-in BookshelfNG
  M4B merge settings, tagged track ordering and chapter titles, original-track
  fallback, and safe move/copy source handling. Hosted Linux, macOS, Windows,
  PostgreSQL migration, CodeQL, dependency/security, and release-note checks
  passed on its clean head. The retirement candidate passed a
  tool-enabled synthetic two-track importer smoke using the real converter; it
  verifies track order, chapter titles and offsets, metadata, one registered
  file, Move-mode source cleanup, and Copy-mode source retention. Focused
  importer tests also cover merge selection and fallback with a mocked
  converter. The six-case real-tool smoke also verifies fallback to separate
  original-track imports in Move and Copy modes when one source cannot be
  probed, and when `m4b-tool` fails after a controlled FFmpeg conversion error.
  Synthetic embedded-chapter Move/Copy and TagLib-write cases pass on c4fa; representative M4B media and restart or resume remain open. This source is not in `.977`.
- The retirement implementation is merged in ChaptarrNG
  [PR #131](https://github.com/snapetech-org/chaptarrng/pull/131). It adds
  older SQLite support, one migration-158 PostgreSQL source schema, native
  Hardcover identity handling, and BookOrbit Book Dock as a separate
  destination. Previous exact-head run
  [39182438062](https://github.com/snapetech-org/chaptarrng/actions/runs/39182438062)
  passed on PR head `bdd7518`; it validated the candidate before the
  populated-data importer fixes and edition-catalog addition described below.
  Code snapshot `2c0401fc` passed exact-head run
  [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646),
  and docs-only checkpoint `ddec3e81` passed run
  [39199730309](https://github.com/snapetech-org/chaptarrng/actions/runs/39199730309).
  These checks do not close operator-data, feature-parity, installed-rollback,
  or channel acceptance.
  The candidate now also validates and stages recognized BookshelfNG ZIP
  backup databases for normal-startup conversion, retaining the untouched
  archive and original BookshelfNG database for rollback. The previous
  ChaptarrNG database is restored if conversion fails; its temporary
  `.pre-restore` copy is removed after success. The first hosted run exposed an
  incomplete migration history in the synthetic fixture; the corrected
  fixture matches schema 158. Exact-head Build run
  [39211491613](https://github.com/snapetech-org/chaptarrng/actions/runs/39211491613)
  passed on code head `6f57d355`: Linux, macOS, Windows, PostgreSQL migration,
  and release-note checks passed. CodeQL, dependency review, dependency/secret
  scan, and NuGet checks also passed. Local tests remain blocked before
  compilation by the workstation's ImageSharp license requirement. This
  source change is not in public `.977`, which still rejects BookshelfNG ZIPs.
  The earlier synthetic provider-mode, startup, and rollback evidence is in the
  [detailed fixture report](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).
  This is not operator-data or channel acceptance.
- On 2026-10-19, the unpublished PR #131 candidate (build version
  `0.9.978.0`) passed preview, explicit apply, rollback preview, and explicit
  rollback against a task-scoped copy of populated migration-158 operator data
  with `HARDCOVER=true`. The live BookshelfNG service remained running, and
  candidate commands targeted only the separate copy; this did not test an
  installed service handoff. Migration preview left the source copy unchanged;
  apply produced schema 121 and retained the source. Restore preview left the
  converted database and source backup unchanged; rollback restored a
  byte-identical schema-158 backup and preserved the converted database
  separately. Integrity and foreign-key checks passed, and the original
  private snapshot remained unchanged. Unresolved books were retained in both
  catalogs as unmonitored and recorded in the review sidecar; their media types
  still need human review. Configured provider rows in the snapshot passed
  migration compatibility checks without blockers; this does not test live
  provider calls. With native Hardcover mode enabled, every migrated author,
  book, and edition retained its Hardcover identity; none were assigned
  Goodreads IDs. A fresh copy of the restored database also opened in the
  BookshelfNG `hardcover` image on Docker's isolated `none` network, and its
  configured `/ping` endpoint returned HTTP 200. This checks database reopen
  and startup health only, not complete library behavior or an installed image
  switch. The `.978` Linux app artifact from Build
  [39267876585](https://github.com/snapetech-org/chaptarrng/actions/runs/39267876585)
  also returned HTTP 200 from `/ping` against a fresh converted schema-121 copy,
  running on the published `.977` runtime image with empty scratch mounts for
  configured roots and an internal-only network. This checks candidate app
  startup, not candidate image packaging or live image switching. The importer
  fixes prompted by this rehearsal preserve
  missing-edition files as unmapped and retain
  conflicting history on its source book without attaching an unsafe file or
  edition. Focused importer, edition-catalog, and capability tests pass 52/52
  locally. Exact-head run
  [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646)
  passed on earlier PR #131 head 2c0401fc: Linux, macOS, and Windows build/tests,
  PostgreSQL migrations, release-note validation, CodeQL, dependency and
  secret checks, NuGet submission, and runner-receipt reconciliation. The
  preceding run [39195460843](https://github.com/snapetech-org/chaptarrng/actions/runs/39195460843)
  exposed four Windows fixtures using POSIX-only paths; the follow-up uses
  native temporary paths and passes. Disposable-fork validation was skipped
  by its gate. This validates hosted candidate code, not operator cutover.
  Ambiguous format review, PostgreSQL operator data, other source shapes,
  complete feature parity, installed rollback, and channel acceptance remain
  open.

  On 2026-10-21, normal startup also passed on separate copies of two populated
  BookshelfNG SQLite snapshots using the Linux candidate built from `main`
  commit `69abed74`. Automatic conversion reached schema 121; `/ping` returned
  HTTP 200 on the saved audiobook and ebook ports, and both databases passed
  integrity and foreign-key checks. The copied `config.xml` retained Forms
  authentication, required authentication, and each existing API key. This is
  isolated startup evidence only; no media roots were mounted and no installed
  service, package, image, or provider handoff was tested. See the
  [migration guide](MIGRATE_FROM_BOOKSHELFNG.md#populated-sqlite-normal-startup-and-configuration-preservation-2026-10-21).
- BookshelfNG `main` at `6e1a62e12` now exposes
  `GET /api/v1/book/{id}/editions/catalog` and advertises `editionCatalog` for
  SeerrNG edition cards. ChaptarrNG `main` now includes the matching route and
  capability, an **Editions and Narrations** book-details tab, and a series
  narrator comparison. Unstated details remain visibly unprovided.
  BookshelfNG source commit `6e1a62e12` passed hosted CI, release-note
  validation, and CodeQL; the manual run skipped release and image-publication
  jobs. The source change is not a new release.
  Earlier candidate code snapshot `8b4c6e23` integrated then-current ChaptarrNG `main`,
  including its Editions tab and series narrator comparison, and retains the
  edition labels for unknown and not-applicable metadata. Local translation,
  type, targeted ESLint, and Stylelint checks pass. Exact-head Build run
  [39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341)
  passed Linux, macOS, Windows, PostgreSQL migration, release-note, and runner-
  receipt checks. CodeQL [39205449712](https://github.com/snapetech-org/chaptarrng/actions/runs/39205449712),
  dependency review [39205452346](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452346),
  security scan [39205452337](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452337),
  and NuGet submission [39205449409](https://github.com/snapetech-org/chaptarrng/actions/runs/39205449409)
  also passed. Disposable-fork validation was skipped by its gate. The workflow
  produced Actions validation artifacts only; it did not publish a release or
  package. The route and catalog implementation at code snapshot `2c0401fc`
  passed run
  [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646).
  An earlier source candidate also passed the isolated SeerrNG Compose smoke
  against PR #131 head `ddec3e81` and SeerrNG `10f06eab9`: capability discovery,
  empty paged-library access, and restricted-key denials passed. A later
  source-built run on 2026-10-23 converted the populated synthetic schema-158
  fixture and used SeerrNG's `ReadarrAPI` to read both formats, verify one
  migrated file per format, and confirm an ebook history row stayed out of
  audiobook history. The restricted-key boundary and pending-import lifecycle
  also passed. This narrows the local evidence gap but does not establish full
  SeerrNG availability/history parity, live provider fulfillment, retries,
  operator-data acceptance, or complete request workflow acceptance; see
  [the integration evidence](SEERRNG_INTEGRATION.md).
- The merged source also ports the native Hardcover metadata client and routes `hc:`/legacy Hardcover work, author, and edition IDs to it. The importer maps a saved `HardcoverAuth` value to ChaptarrNG's `HardcoverApiToken` setting; environment credentials remain external to the database. Every migration requires an explicit provider mode (`HARDCOVER=true` or `false`, or `HARDCOVER_NATIVE=false` for Goodreads) so the importer can persist the source dialect; a missing or invalid mode fails closed even when IDs are namespaced. The prior plan to build separate softcover and Hardcover Docker targets is withdrawn. All legacy aliases use the same standard image digest, and channel adapters or operator tooling must preserve the source provider setting. These source changes are not in `.977`, and converted-data acceptance remains open.
- BookshelfNG's BookLore notification settings and `OnReleaseImport` trigger are copied unchanged into ChaptarrNG, and remain in the retained source backup. A separate BookOrbit Book Dock destination is candidate work in this branch; it does not rewrite BookLore URLs or credentials. Mock tests cover login, Book Dock permission, streamed uploads, redirects, rejected credentials/files, and partial-batch failure. An isolated BookOrbit v3.2.0 live API check passed setup, login, permission check, EPUB upload/listing, and unsupported-file rejection. That does not verify downstream processing of a production book or a real-user handoff. The integration is not in `.977`, and the broader retirement gate remains open.
- Merged PR #131 adds ChaptarrNG's format-aware author-media move workflow:
  preview and confirm a single-author or bulk move, move only registered media
  and linked book sidecars, check destination conflicts and copy space, and
  leave unknown files and author-wide extras in place. The UI and documented ChaptarrNG CLI helpers require a reviewed preview
  token before queueing, the generic command endpoint rejects
  direct move commands, and the worker stops if a source changes after preview.
  Sidecars resolve against the linked book's format root, retain paths relative
  to the destination format root, and fail closed when a registered relative
  path escapes the author folders. Focused tests cover format isolation,
  shared-root and format-root sidecars, path traversal, untracked files,
  conflicts, stale sources, and bulk preview. Run
  [39196795646](https://github.com/snapetech-org/chaptarrng/actions/runs/39196795646)
  passed on earlier head 2c0401fc, including Windows tests after replacing the
  POSIX-only fixture paths identified by run
  [39195460843](https://github.com/snapetech-org/chaptarrng/actions/runs/39195460843).
  This implementation is on `main` but not in `.977`; representative library,
  cross-device, partial-failure rollback, and channel acceptance remain open.
- BookshelfNG `0.4.21.71` is published as the final source release.
- BookshelfNG issue [#114](https://github.com/snapetech-org/bookshelfng/issues/114) requests an opt-in, disabled-by-default
  new-author path setting. ChaptarrNG `main` now provides it in
  Settings > Media Management. When enabled, it initializes missing format
  roots for new authors only. Migration carries the saved flag and maps the
  BookshelfNG format roots to ChaptarrNG defaults when those defaults are
  empty. Explicit author roots and existing files are preserved. This is not
  included in the published `.977`; the older `.974`–`.977` behavior initialized
  new-author roots automatically.

## Database migration contract

**Current operator decision: preview only.** The published `.977` converter
can assess the exact migration-158 SQLite schema without changing the source,
but applying migration or starting ChaptarrNG against BookshelfNG appdata is
not approved until data, parity, rollback, and channel acceptance close.
Normal ChaptarrNG startup automatically converts a supported source database,
so keep BookshelfNG in service and do not point a ChaptarrNG install at its
appdata yet.

The published ChaptarrNG `v0.9.977` converter accepts the BookshelfNG
migration-158 SQLite database and bundles the BookshelfNG SQLite migration
chain. It upgrades a disposable copy before import and retains the untouched
snapshot as the rollback source. **Do not use `.977` for native Hardcover
source databases:** its importer maps bare numeric IDs to Goodreads even when
the source used `HARDCOVER=true`; integrity checks and read-only preview do not
detect that identity error. The unpublished candidate requires explicit
provider mode. The `.977` converter still rejects versions
1–13 at its pre-upgrade table check; those versions retain `Blacklist` until
the embedded chain renames it. PR #126 merged the version-aware guard and
synthetic migration-1 rollback coverage into `main`, and its hosted migration
checks passed on Linux, macOS, Windows, and PostgreSQL. Neither the source fix
nor synthetic fixtures establish behavior on real older-schema operator data
or every historical release. Schema 159 is rejected without changing the
source. PostgreSQL source databases and merging two populated BookshelfNG
databases from one appdata directory remain unsupported by published
ChaptarrNG releases. The current unpublished worktree now includes a
transactional `readarr.db` + `audioarr.db` merge path for normal startup and
explicit preview/apply. It preserves the `readarr.db` identity source and both
original files, and stops before target promotion on conflicting identities,
settings, or unique references. This code pass has not had its bulk validation
run, so it does not change the published-release limitation. Independently
operated BookshelfNG instances can be migrated one at a time with separate
appdata directories; co-located databases with merge conflicts still need
their ownership and settings resolved before migration. Current `main` also
supports PostgreSQL source conversion and
upgrades supported older source schemas on a disposable snapshot. Local
PostgreSQL 17 fixtures cover versions 1, 13, 22, 33, and 158; these are
synthetic and do not establish operator-data acceptance. See the
[PostgreSQL migration notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md). A private
populated migration-158 SQLite snapshot completed preview, apply, and rollback
with public `.977` on Linux/amd64 on 2026-10-18. That proved structural
conversion and rollback only; it did not validate native Hardcover identity.
The separate 2026-10-19 candidate rehearsal with explicit Hardcover mode passed
the same four-step path on a disposable copy and restored the source database
byte-for-byte from its retained backup. Neither run resolves ambiguous media
classifications, PostgreSQL operator-data acceptance, feature parity, or
installation-channel handoff; details are in the [operator guide](MIGRATE_FROM_BOOKSHELFNG.md).
The SQLite path stages a native database, accounts for source rows, checks
SQLite integrity and foreign keys, and promotes only after validation. Preview
does not modify the source. Apply and rollback are explicit operations, and
normal startup can run the same converter when it finds a supported source
database. The merged PostgreSQL candidate upgrades older sources only on
temporary schemas, checks row accounting and both schema lineages, and retains
the original tables in a backup schema. Local PostgreSQL 17 fixtures passed
8/8 on 2026-10-21; a prior hosted exact-head fixture passed all 4 migration cases in run
[39175250100](https://github.com/snapetech-org/chaptarrng/actions/runs/39175250100),
including rollback and normal-startup conversion with backup retention; this
remains synthetic evidence. Operator-data acceptance remains open; see the
[PostgreSQL migration notes](BOOKSHELFNG_POSTGRESQL_MIGRATION.md).

The current PostgreSQL source candidate also handles BookshelfNG's implicit
`readarr-main`, `readarr-log`, and `readarr-cache` defaults, which differ from
ChaptarrNG's unsaved `chaptarr-*` defaults. It selects the recognized legacy
main database when the implicit ChaptarrNG main database is empty, preserves
the selected legacy log/cache databases across restart using the migration
marker, honors explicit database names, and stops on conflicting or
unsupported populated data. This closes a source-selection gap only; it does
not establish PostgreSQL operator-data acceptance or published-release
support.

Some BookshelfNG books do not contain enough data to infer ebook versus
audiobook. Preview writes a private review file. Operators can provide a
complete, source-matched media map before conversion. Current ChaptarrNG source
keeps each unresolved work in both catalogs and maps its source book-level
monitoring choice to both: monitored source books remain monitored in both
formats, while unmonitored source books remain unmonitored. The published
`.977` release predates this fallback change and leaves ambiguous works
unmonitored in both formats. This avoids guessing a media type but does not
infer a work's true format. A single monitored edition of each format is
preserved on its corresponding ChaptarrNG record; multiple monitored editions
of the same format still fail closed. The populated-library rehearsal
accounted for every source row; some book classifications still required
human review, and test choices were not reviewed as correct library decisions.
A separate 2026-10-18
Docker image-switch smoke used an empty database created by BookshelfNG `.71`:
ChaptarrNG `.977` converted it on normal startup, retained the original database,
and restored it through rollback; the BookshelfNG image started again on port
8787. This does not validate real user data or the Unraid Community Apps flow.

A follow-up 2026-10-18 image-switch rehearsal used a populated synthetic
database from the BookshelfNG `.71` Hardcover image. The previous Chaptarr
candidate, started without `HARDCOVER=true`, assigned bare numeric provider IDs
to Goodreads. Repeating the switch with the source provider mode preserved
Hardcover IDs, converted data and API results, and passed SQLite integrity and
foreign-key checks. Rollback preview was byte-preserving; rollback restored the
source database byte-for-byte, kept the converted database as a backup, and the
`.71` image reopened the restored source. The migration importer now requires
an explicit provider mode for every conversion and fails closed before source
mutation when it is absent or invalid. The former separate Goodreads and
Hardcover image targets were withdrawn; all legacy aliases use the standard
image, and channel configuration must carry the source mode. Those changes are
source-only. Exact-head run [39175250100](https://github.com/snapetech-org/chaptarrng/actions/runs/39175250100)
passed the provider-mode regression and PostgreSQL fixture checks. The final
Linux artifact refused conversion without a provider mode, preserved Goodreads
and Hardcover identities with their explicit modes, converted during normal
startup, and restored the source byte-for-byte during rollback. Local BuildKit
target checks and hosted alias/bridge regression scripts also pass; the actual
GHCR migration alias dispatch has not been run or published. This synthetic
rehearsal does not establish operator-data acceptance or validate the Unraid
template handoff; details are in the
[fixture evidence](maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).

BookshelfNG's `BookImportMinimumMatchPercent` setting (default 70, valid range
50–99) remains active for ChaptarrNG's fuzzy RSS-title fallback and now gates
release/file candidate scores for migrated BookshelfNG installs. The importer
enables `BookshelfMatchCompatibility` only for converted installs, including
when the legacy 70% default had no saved row; fresh ChaptarrNG installs keep it
disabled. When no explicit ChaptarrNG mode is present, the converter also maps
the nearest policy band: 50–66 to Aggressive, 67–82 to Balanced, and 83–99 to
Strict. The migration report names these behaviors, and invalid values stop
conversion. ChaptarrNG retains additional identity-proof and ambiguity checks,
so this is a closer compatibility path rather than an identical matching
pipeline. Review it against converted data. The source change is not in public
`.977`.

The same migration-only compatibility marker restores BookshelfNG's audiobook
release ranking when both candidates are audiobooks: quality and revision,
custom-format score, preferred protocol, indexer priority, torrent peers, book
count, Usenet age, then size rounded to 200 MB. Fresh installs and mixed
ebook/audiobook comparisons keep ChaptarrNG's current ranking. This source
change is also not included in public `.977`.

For a source earlier than migration 14, first update BookshelfNG to its final
`0.4.21.71` release so it can bring the database to migration 158. The `.977`
converter does not include the later `main` fix, and no follow-up release is
available while the release freeze is active. The published `.977` converter
does not accept PostgreSQL databases or merge two populated BookshelfNG
databases from one appdata directory. Independently operated instances can be
migrated separately with isolated appdata directories. Current `main` also
upgrades supported older PostgreSQL source schemas on a disposable snapshot
before conversion; local PostgreSQL 17 fixtures cover source versions 1, 13,
22, 33, and 158. This candidate remains unreleased and must not be used by
operators until representative-data conversion, independent backup and restore,
and rollback acceptance gates close. The
BookshelfNG helper archives appdata, but
media/download volumes and settings stored outside appdata need separate
handling. Keep the BookshelfNG service stopped, retain an independent backup,
and follow the
[migration guide](MIGRATE_FROM_BOOKSHELFNG.md).

**BookshelfNG channel build wiring — 2026-10-24:** Docker compatibility-image
pushes and provenance attestations now require the ChaptarrNG release,
BookshelfNG retirement, and migration-bridge gates together. A gated stable
release builds checksum-verified AppImage and Flatpak candidates from the exact
Linux release archive, then packages Cloudron, Co-op Cloud, self-hosting
Compose, and StartOS sources with the exact multi-architecture Hardcover image
digest from that release. The StartOS source preserves the `bookshelfng`
package and volume identities and is type-checked and bundled, but no `.s9pk`
has been submitted or installed. The Windows installer, Debian package, and
RPM handoffs remain in their existing gated paths. These artifacts are still
release candidates: they do not update external catalogs or close
representative-data, live provider, installed-update, or platform rollback
acceptance.

## Legacy install channels

**Local candidate acceptance update (2026-10-26):** The detailed 0.9.978
No/Yes, continuity, health, source-backup, and rollback results are in the
[channel acceptance matrix](maintainers/BOOKSHELFNG_CHANNEL_ACCEPTANCE_2026-10-26.md).
Installed local acceptance now includes Debian, Fedora, AUR, Chocolatey,
Docker, Helm, AppImage, Flatpak, FreeBSD standalone archives, Co-op Cloud
Swarm, and synthetic StartOS test-package updates on x86_64 and aarch64. The
matrix tracks Cloudron feed/catalog publication, StartOS official-package
delivery and package-manager rollback, Unraid, canonical Co-op Cloud recipe acceptance,
and external-catalog installs as open, and keeps publisher/feed delivery
separate. No candidate was published; this update does not close the release
gate.

| Channel | Published ChaptarrNG state | BookshelfNG handoff state |
| --- | --- | --- |
| Debian / Ubuntu `.deb` and PPA | No ChaptarrNG package is published. | The gated `bookshelfng` `.deb` release asset and Launchpad source-package publisher preserve the service, environment file, and `/var/lib/bookshelfng` data path. The checksum-verified public `.71` package upgraded to the local `.977` candidate and back in Debian Trixie with real dependency resolution and no conffile prompt. A systemd-running container upgraded an active `.71` service and passed `/ping`; after restoring the pre-upgrade data copy, downgrade to `.71` restarted the service and passed `/ping`. This used a fresh empty `.71` database and synthetic canaries, not representative data. External PPA credentials, archive, and enabled series remain unvalidated, and no PPA package has been uploaded. Real-host acceptance and representative-data rollback remain open. |
| Docker / GHCR | `ghcr.io/snapetech-org/chaptarrng:0.9.977`, `:latest`, and `:main` resolve to the same published multi-architecture standard-image digest. | Legacy aliases remain unchanged. The rejected provider-specific image targets are removed in the current source candidate; every old alias must resolve to the same standard digest. The standard image keeps `8789/tcp` for fresh installs and advertises `8787/tcp` for migrated BookshelfNG installs whose saved config or adapter selects the legacy port. A single image cannot infer the old tag's provider mode, so every migration requires an explicit setting: `HARDCOVER=true` or `false`, or `HARDCOVER_NATIVE=false` for Goodreads. The bridge stays disabled; no multi-architecture image rebuild or alias dispatch has been published, and pinned version tags/digests need an operator update. |
| AUR | `chaptarrng-bin 0.9.977-1` is published. | `bookshelfng-bin 0.4.21.71-1` remains public. A local `.977-1` candidate passed isolated pacman upgrade/downgrade, synthetic populated migration/restore, and active systemd service handoff checks. The test used a network-isolated Arch container and a synthetic migration-158 database; representative operator data and host-specific settings remain open. |
| Fedora COPR | `chaptarrng 0.9.977-1` built successfully for Fedora 43, 44, 45, and Rawhide x86_64. | The BookshelfNG bridge trigger was skipped; current `slskdn/bookshelfng` repo metadata has no packages for Fedora 43, 44, 45, or Rawhide x86_64. A local Fedora 44 RPM transaction upgraded the published `.71` package to a `.977` candidate and downgraded back, preserving config/data canaries. `systemctl` was mocked and RPM dependencies bypassed; live COPR/DNF and real-systemd handoff remain untested. |
| Chocolatey | The [standard `chaptarrng` package](https://community.chocolatey.org/packages/chaptarrng) `0.9.973` is public (approved October 20); it trails ChaptarrNG `v0.9.977`. A `.977` package is not on the feed. | The [BookshelfNG package identity](https://community.chocolatey.org/packages/bookshelfng) is public at `0.4.21.76` (approved October 26; the final GitHub release is `0.4.21.76`). The bridge is unpublished; a previous `.977` push returned HTTP 403. The source bridge preserves the BookshelfNG service identity, configured data path and NSSM arguments, previews migration, preserves or explicitly accepts provider mode, backs up PostgreSQL sources, checks `/ping`, and restores data and app files on startup failure. It leaves the service stopped if rollback cannot be verified. Update the standard package, resolve publisher authorization, then validate populated SQLite/PostgreSQL conversion, rollback, and the installed old-identity handoff. |
| Unraid | The ChaptarrNG template is maintained separately. | The legacy-template handoff is in [BookshelfNG Unraid PR #5](https://github.com/snapetech-org/bookshelfng-unraid/pull/5); its XML and settings-preservation check passed, but the Community Applications upgrade and rollback flow has not been tested. Existing installs still need a template update. |
| StartOS | No ChaptarrNG package update is published. | Synthetic StartOS 0.4.0.3 SDK3 test packages on x86_64 and aarch64 preserved package ID `bookshelfng`, volume IDs `main`, `books`, and `downloads`, mounts `/config`, `/books`, and `/downloads`, and port `8787`. ARM64 passed Manual defer, Console preview/apply, Automatic conversion, `/ping`, SQLite integrity and foreign-key checks, and automatic source rollback after an injected health failure; the source returned to schema 158 and the failed schema-124 target was retained. The ARM64 predecessor is a direct-service wrapper around BookshelfNG Readarr 0.4.21.37, not the public `.71` StartOS package. Official signing, update from `.71`, external repository delivery, and package-manager rollback remain open. |
| Helm | A same-name compatibility chart is in ChaptarrNG `main`; its versioned publisher targets the existing BookshelfNG OCI chart identity and requires both release-readiness gates, the migration bridge gate, and an explicit bridge request. The publisher has not run during the freeze. | Local build and chart contract checks cover the preserved release, StatefulSet, Service, selectors, port, `/config` mount, PVC, both provider modes, and pinned image. No chart is published; installed PVC upgrade, GitOps sync, and rollback remain open. Existing installs can keep the same chart source after acceptance, while pinned/offline archives need a manual update. |
| AppImage | ChaptarrNG Linux x64 standalone archives are available. | Source-only launcher candidate keeps desktop ID `com.snapetech.bookshelfng`, `$XDG_DATA_HOME/bookshelfng`, and launches `Chaptarr` with the legacy data path. The current public `.977` archive predates native Hardcover identity handling and is rejected by the candidate staging helper. The rebuilt local `.978` AppImage passed a same-path update, path-preservation, and rollback rehearsal from the checksum-verified public `.71` package using a synthetic schema-158 fixture. Representative-data and live-channel acceptance remain open. |
| Flatpak | ChaptarrNG Linux x64 standalone archives are available. | Source-only manifest keeps app ID `com.snapetech.BookshelfNG`, desktop launcher, and sandbox config path `bookshelfng`; updates use Flatpak. The current public `.977` archive predates native Hardcover identity handling and is rejected by the candidate staging helper. The rebuilt local `.978` bundle passed an in-place app-ID update, path-preservation, and rollback rehearsal from the checksum-verified public `.71` bundle using a synthetic schema-158 fixture. Representative-data and live-channel acceptance remain open. |
| Cloudron | No ChaptarrNG package update is published. | Cloudron 10.1.6 installed `.4.21` to local candidate `.4.23`, preserving app ID `tech.snapetech.bookshelfng`, `/config`, `/books`, `/downloads`, port `8787`, and `/ping`. Manual defer, native backup/restore after mutation, Automatic conversion, source-database restore, and return to the old package passed. Candidate/feed publication remains open. The legacy `.4.21` pre-update backup omits persistent directories because it has no backup hooks. |
| Co-op Cloud | No ChaptarrNG recipe is accepted upstream. | An isolated Debian 13 / Docker 26.1 Swarm upgraded the pinned BookshelfNG .71 source image to the local exact-head .978 candidate. Manual kept the schema-158 database unchanged; Automatic migrated, retained its source backup and media canaries, passed SQLite checks, and served `/ping`. A forced unhealthy update paused; restoring the three-volume snapshot and redeploying .71 returned healthy BookshelfNG. Official Backupbot II CLI and Abra 0.13.0-beta backup/restore orchestration recovered the schema-158 database byte-for-byte and restored all three volume canaries. Canonical recipe review, published digest, and maintainer acceptance remain open. |
| Self-hosting catalogs | ChaptarrNG has a manual Compose candidate. | The candidate preserves the `bookshelfng` container and volume identities. CasaOS, ZimaOS, Umbrel, TrueNAS, Cosmos, CapRover, Portainer, and other catalog records live in external repositories and have not been updated or accepted. |
| Other historical channels | ChaptarrNG publishes its standard release artifacts. | PPA, BookshelfNG's in-app updater, pinned/offline installs, and manually managed services still need an approved update path or explicit retirement decision. |

The rebuilt desktop candidates on 2026-10-22 contain the shared-root path fix.
Their `.978` Linux x64 standalone archive SHA-256 is
`b564ebfc4ca1d17ed1edaf1a794ddb374d41d226a3925dc3a99fbb1d980102e9`. The
BookshelfNG-identity AppImage SHA-256 is
`1709cbdfb6b965e7858c8bb68c2a10397f310e2d91fa101bd74600225df3350b`; the
Flatpak bundle SHA-256 is
`25015108e0f7ce3d7305e98a4b93174a0a8969333db7bb5f4f3394c5abfbc6f9` and its
local ref is `app/com.snapetech.BookshelfNG/x86_64/stable` at commit
`2dae189b1af9a8339984659226ddea0a0e4c484d2a794982a201ec5897a62378`. The
candidate payloads returned HTTP 200 from `/ping` during installed-channel
rehearsals. The
checksum-verified public `.71` sources used for the installed-channel rehearsal
were AppImage SHA-256
`c116df08ddd9609e19d9b7d83f9206bb5a2940b99ce7dd5259fda44c991b06db` and
Flatpak SHA-256
`858d7ee8a9e0893becb07246a5bcdbf5d015f72eaf69f92e024fd2b080cf63f6` (Flatpak
commit `75e3178f521c35788154508072786a2a61b366302b30f62480d947fb16ffd35b`).
The candidates were installed over those packages at the same desktop path/app
ID. Both source databases were restored byte-for-byte from the retained
BookshelfNG backup, then the public `.71` apps started and returned `/ping` 200.
These artifacts remain unsigned and unpublished.

On 2026-10-22, both rebuilt `.978` packaged executables passed an
installed-channel migration and rollback rehearsal using the synthetic
schema-158 fixture. Preview left the source unchanged; apply reached Chaptarr
schema 121 with four authors and nine books, SQLite integrity `ok`, and no
foreign-key violations. The three authors whose shared root equaled their
author path retained distinct ebook and audiobook directories; startup logged
no path repairs. Restore preview left the databases unchanged, and explicit
rollback recovered the BookshelfNG database byte-for-byte from its retained
source backup. The old `.71` AppImage and Flatpak then each started against
schema 158 and returned `/ping` HTTP 200. Flatpak ran inside its sandbox with a
task-scoped data path. An earlier candidate build exposed the path collapse;
the rebuilt payloads contain the regression fix. Exact-head hosted validation
and representative-data review remain open. This rehearsal does not validate
operator data, provider fulfillment, or a live published update channel.

Release, image, and package publishing require both
`CHAPTARRNG_RELEASE_READY` and `BOOKSHELFNG_RELEASE_READY`. The legacy package,
image, and Helm bridges additionally require
`CHAPTARRNG_MIGRATION_BRIDGE_ENABLED` and an explicit bridge request; the
migration bridge gate remains `false`. The scheduled BookshelfNG bridge
workflow confirmed that it found a stable ChaptarrNG release but skipped
publishing because the gate is off. Do not turn the gate on until the feature
and channel acceptance items below are closed.

### Audiobook merge compatibility

ChaptarrNG main contains an unpublished compatibility implementation for
BookshelfNG's opt-in `BOOKSHELF_M4B_MERGE` behavior. It is limited
to tracked audiobook downloads with at least two approved, non-M4B tracks for
the same local book and edition, and it keeps the feature disabled unless that
environment variable is explicitly `true`. The candidate preserves disc/track
tag ordering and track-title chapters, uses the legacy 128 kbps AAC stereo
44.1 kHz defaults (with the existing bitrate override), honors a configured
FFmpeg path when the executable is named `ffmpeg` (`ffmpeg.exe` on Windows),
and falls back to importing the original tracks if merging fails. After a
successful import it removes only the consumed source tracks in move mode and
keeps them in copy mode.

This is source-only and is not in `v0.9.977`. The current retirement candidate
passes a six-case tool-enabled importer smoke with generated two-track input
and the real converter. The run on candidate `12f290bd` passed 6/6 on `kspls0`,
using `m4b-tool` 0.5.2, `mp4v2` 2.1.1, and Debian Bookworm FFmpeg in the
isolated tool image. The successful cases check track ordering, chapter titles
and offsets, album and title metadata, library registration, source deletion
in Move mode, and source retention in Copy mode. Failure cases verify fallback
to separate original-track imports in both modes when a source cannot be probed
and when `m4b-tool` fails after a controlled FFmpeg conversion error. The Linux
build workflow runs the real-tool merge checks in a pinned tool-enabled
container, including synthetic inputs with embedded chapters in Move and Copy
modes. The candidate also retags generated output with TagLibSharp and verifies
chapter titles and offsets survive; those added cases passed in exact-head
Linux Build [39288736177](https://github.com/snapetech-org/chaptarrng/actions/runs/39288736177).
Representative files and restart/resume behavior remain open. It
does not close the Audiobook processing and tags row in
[the feature matrix](FORK_FEATURES.md). Keep the legacy merge setting unset
until a build containing the accepted implementation is installed.

BookshelfNG's in-app updater checks the Readarr-owned update service, which
Snapetech cannot redirect. No single server-side change can move every pinned,
offline, third-party, or manually installed copy automatically.

## Retirement acceptance

BookshelfNG is not ready to retire solely because the converter and a
ChaptarrNG release exist. No new ChaptarrNG release or legacy redirect is
authorized while these gates remain open. The remaining work is:

1. Close each BookshelfNG feature row with behavioral and converted-data
   evidence, or record an explicit maintainer decision to retire that behavior.
   The saved numeric matching threshold is now exercised through the current
   matcher with synthetic data; representative converted-library acceptance
   remains open. Routine configured-catalog fallback is now in ChaptarrNG
   `main` at `30dcab13`; the Release Core suite covers identifier and search
   tiers, exact media/work identity, ambiguity and scope guards, filename
   fallback, and cancellation. Published `.977` does not contain this code.
   Current source also carries BookshelfNG's `BookFiles.LastRemoteSearchTime`
   through database conversion, routine scan filtering, unmapped retries,
   staged imports, and successful relinking. An unchanged file with a completed
   catalog search no longer repeats that search on every scan; changed files
   and failed provider searches remain eligible, and a successful link clears
   the marker. Release Core passed 4,806 tests (25 skipped, 0 failed), and the
   Release solution build passed with 0 warnings and errors. Published `.977`
   does not contain this source change. Live-provider behavior,
   representative-file review through Manual Import, and end-to-end
   pending-author fulfillment remain open.
2. Keep the migration-1-to-13 fix on `main` unreleased until the full gate
   passes; validate older-schema support on representative operator data,
   then validate
   old-identity AUR, COPR, Chocolatey, Helm, StartOS, container, and Unraid
   upgrades through install, conversion, restart, backup retention, and
   rollback. The local Fedora RPM transaction rehearsal proves package/file
   handling only; validate a live COPR/DNF handoff if that identity is kept.
3. Update the approved Chocolatey `chaptarrng 0.9.973` package to `.977` and
   resolve the HTTP 403 for the legacy `bookshelfng` bridge. The `.977` COPR
   build is verified. The old-identity RPM package transaction was rehearsed
   locally, but the legacy COPR repo currently has no packages and no live DNF
   handoff was tested; verify each published package checksum or image digest
   against the tested artifact.
4. Repeat AppImage and Flatpak migration and rollback checks with representative
   operator data, including the custom-root path case now covered by the local
   regression tests and rebuilt `.978` candidates. The candidates pass local
   same-identity update, backup, path-preservation, and rollback checks with a
   synthetic schema-158 fixture. Update and validate
   the external StartOS package, Cloudron feed, Co-op Cloud recipe collection, and
   supported catalog entries after a digest is published. Preserve a tested
   manual migration path for pinned and offline installs.
5. Review unresolved media records from the populated SQLite rehearsals and
   verify normal startup and external clients against the converted library
   before enabling automatic aliases. Preview/apply/restore on two populated
   SQLite snapshots is recorded in the [migration guide](MIGRATE_FROM_BOOKSHELFNG.md#populated-sqlite-migration-and-restore-rehearsal-2026-10-21);
   this does not validate each user-facing workflow or package channel. Current
   `main` now persists `BookshelfLegacyIdentityMap` rows for source author,
   book, edition, and file IDs, including both targets of a media split, and
   resolves old book IDs across format-scoped reads, book-scoped workflows, and
   the legacy Bookshelf monitoring action.
   Synthetic fixtures cover those mappings; representative converted-data and
   external-client acceptance remain open.

The conversion preview, source-side archive helper, synthetic conversion checks, and package templates are present. Source-only AppImage, Flatpak, Cloudron, Co-op Cloud, and catalog candidates preserve their legacy identities and data paths. The rebuilt local `.978` AppImage and Flatpak payloads have passed same-identity installed-channel update, custom-root path-preservation, and rollback rehearsals with public `.71` packages and synthetic schema-158 data. Cloudron also passed local installed platform update, native backup/restore, migration, and source/package rollback; feed publication and the legacy `.4.21` backup gap remain. Representative-data acceptance and provider fulfillment, external-channel acceptance, an in-place deployment cutover, full feature parity, all legacy package publications, and a universal automatic update path are not established. See the
[BookshelfNG operator guide](https://github.com/snapetech-org/bookshelfng/blob/main/docs/migrate-from-bookshelfng-to-chaptarrng.md)
and the linked parity roadmap for the detailed evidence ledger.
