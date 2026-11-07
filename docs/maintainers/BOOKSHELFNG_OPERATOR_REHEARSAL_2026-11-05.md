# BookshelfNG operator-data rehearsal — 2026-11-05

These are isolated rehearsals using consistent snapshots of both current
BookshelfNG installations. Production services, databases, media and client
configuration were not changed. No release or cutover is authorized.

## Exhaustive response audit follow-up

Schema-126 candidate `833d6d63` passed full Build
[42031944612](https://github.com/snapetech-org/chaptarrng/actions/runs/42031944612),
all ten standalone archives
[42031945553](https://github.com/snapetech-org/chaptarrng/actions/runs/42031945553),
and strict API validation
[42031944507](https://github.com/snapetech-org/chaptarrng/actions/runs/42031944507).
Its functional identity-map indexes eliminate the real-library lookup scans.

Every managed record was then collected from both exact `.76` source clones and
fresh migrated candidates: 2,779 authors, 29,568 source works and 56,058 files.
All collection requests return HTTP 200 with no missing, extra or duplicate IDs.
The first comparison exposed response gaps outside the synthetic sample.
Candidate `fafb831a` fixes work titles, ratings, edition identities, links, book
cover URLs and most narrator/series differences. Both fresh populated migration
and rollback rehearsals pass again, including source user rows, provider settings,
author arrays, captured metadata defaults and all file counts; rollback matches
all source tables and retained backup bytes.

Application-code candidate `73d992f7` closes the remaining narrator, series tie,
author-cover identity, embedded-monitor and mapped-quality differences. Its
packaged comparison has **zero mismatching rows or fields across all 88,405
managed records**, with zero duplicate, missing or extra IDs. Both fresh
populated migration/rollback rehearsals pass again, and both full Seerr web
format workflows pass with source profile IDs and persisted identity/profile
audits. Keep earlier comparisons as failure evidence.

A further authenticated HTTP check compares every cached image URL with retained
source file bytes. The first completed check matched 23,977 images and found four
failures: the same two large TIFF originals in both libraries. Packaged
`6f40dbf9` repairs those failures: all 23,981 URLs return HTTP 200 and match source
byte counts and hashes. The bounded bulk test explicitly
uses a configured limit of 6,000 requests/minute, batches of 5,000 and 61-second
cooldowns. An earlier source-versus-target HTTP attempt is inconclusive because
source Forms redirects returned login HTML and the default rate limiter returned
429s; it is not counted as image parity.

New-add regressions also identify source ebook profile IDs being interpreted as
native audiobook IDs on migrated format-specific routes, plus native profile IDs
leaking into legacy author readback. The repair translates only clients accepting
legacy IDs, follows the requested format and reverses mapped IDs on compatibility
readback. Explicit native hints retain native IDs. The synthetic acquisition
fixture now includes new-author checks using an explicit Hardcover V5 metadata
proxy. Packaged `6f40dbf9` passes both full format workflows and creates both new
authors with the expected ebook profile: source client ID `2` and explicit native
ID `4` both persist target `4`, while compatibility readback returns source `2`.
Read-only database checks after consumers exit confirm both new authors retain
quality profile `4` and metadata profile `2`. The harness now records and audits
these new-author identities after restart; broader new-book metadata fulfillment
remains a separate check.
Raw bodies, configurations and source metadata remain private under
`/var/tmp/chaptarrng-operator-data-20261105/` on `kspld0`.

The full Seerr ebook workflow also passes with unchanged source profile IDs and
the repaired, explicitly identified client test image. Post-consumer database
audits verify both migrated format quality/metadata profiles and their types.
The acquisition harness now defaults to source profile IDs and records those
audits after import and restart. This validates existing migrated author settings;
new-author configuration and other client paths need their own acceptance.
Seerr repair [PR 527](https://github.com/snapetech-org/seerrng/pull/527) passed
trusted owner CI and is merged as `7b013338`; no client image was published.

## Latest compatibility verification

The metadata handoff now captures only the source container's effective
`METADATA_URL` in a private mode-0600 sidecar and persists its fallback during
conversion. The 109 importer/configuration tests and five capture guard tests
pass; an isolated capture matches the retained source runtime setting. Strict
HTTP run [42016737210](https://github.com/snapetech-org/chaptarrng/actions/runs/42016737210)
compares 91 cases: 83 pass, five fail, one is inconclusive because the source
returns HTTP 500, and two retain explicit production-route policy differences.
The metadata configuration and all five catalog comparisons pass.

Subsequent provider-template repairs pass 30 focused checks. Migrated rTorrent
instances retain `readarr_imported`, so existing scripts continue seeing the
imported view; fresh native instances keep their native view. Legacy schema
responses preserve predecessor Download Station names/messages, public template
password defaults and Plex template flags without changing saved credentials
or native template defaults.

NZB.su's [official retirement notice](https://www.nzb.su/) states that old API
endpoints no longer redirect and existing accounts/keys move to `api.nzb.life`.
Request-boundary routing covers capabilities, search/RSS, pending download links
and narrator metadata. It preserves saved settings and encoded request data;
lookalike hosts, credentials in URLs and custom ports are excluded. Legacy
clients still receive their source preset, while native presets use the current
endpoint. The 81 focused provider/Newznab checks and localization checks pass.
This does not establish live acceptance using a paid NZB.su account.

Archive run [42016737332](https://github.com/snapetech-org/chaptarrng/actions/runs/42016737332)
passes all ten platforms, synthetic ebook/audiobook acquisition and authenticated
cover fetching. These results do not constitute a global parity claim.

Fresh packaged `f3ab4cb2` repeats preview, apply, migration checks, restore
preview and rollback on both frozen operator snapshots. Preview leaves each
source unchanged. All original user columns, provider settings and author
metadata arrays match; all 56,058 files remain represented. Captured source
metadata fallback values match privately. Both converted targets pass integrity
and foreign-key checks; rollback matches every source table including SQLite
identity counters and matches each retained backup byte for byte. Production
source PIDs and start times remain unchanged. Private evidence is retained at
`/var/tmp/chaptarrng-operator-data-20261105/rehearsal-f3ab4cb2/` on `kspld0`.

Strict follow-up [42021059988](https://github.com/snapetech-org/chaptarrng/actions/runs/42021059988)
at `f3ab4cb2` has 94 cases: 88 pass, three fail, one source error and two expected
route-policy differences. All provider schemas, localization, metadata defaults
and legacy instance names match. A new unchanged-client host-settings GET/PUT
check catches rejection of valid `Readarr` instance names by the native brand
validator. Two regression cases reproduce that rejection before repair; the
follow-up repair passes all 32 configuration checks. The original password still
works before and after the attempted save; a successful save remains to be
verified. Stored password hashes remain private and actual runtime diagnostics
remain truthful, so those two strict differences are still explicit.

Follow-up [42022250332](https://github.com/snapetech-org/chaptarrng/actions/runs/42022250332)
at `3651598e` verifies the repaired settings flow: GET/PUT returns HTTP 202,
readback matches, the original password authenticates and a wrong password is
rejected. Of 94 cases, 89 pass, two strict differences remain, one source error
is inconclusive and two route-policy differences are recorded. The two strict
differences are password-hash concealment and actual runtime facts. Policy
classification now requires these authentication proofs or typed facts matching
a separate native status request, retaining all original mismatch details.

An isolated `f3ab4cb2` real-data runtime additionally clears the image's inherited
`METADATA_URL`: native and legacy HTTP configuration still resolves the captured
source fallback; legacy host/status instance names match and password hashes
remain hidden. The probe runtime has exited; private results and logs remain
with the rehearsal evidence. No original password login is claimed for real
operator accounts.

## Source and conversion

Both sources run BookshelfNG `0.4.21.76`, immutable image
`ghcr.io/snapetech/bookshelfng@sha256:e89afba0fd4708721e580c75233cf4d32af36a9234d598039c5b55fc72fc3e80`.
Their effective mode is Hardcover: `HARDCOVER=true`, with no native-mode override.
An absent `HARDCOVER_NATIVE` must not be converted to `false`; explicit false
would select Goodreads. Snapshots use SQLite's backup API against read-only
connections. Source process IDs remained unchanged during collection.
Transferred databases/configurations were checked against source SHA-256 values.

| Library | Authors | Source works | Target format books | Files retained | Port |
| --- | ---: | ---: | ---: | ---: | ---: |
| Primary | 1,379 | 14,605 | 22,376 | 27,559 | 8787 |
| Audiobooks | 1,400 | 14,963 | 22,982 | 28,499 | 8788 |

Both schema-158 snapshots passed preview, explicit apply to private copies,
schema-125 migration checks, integrity and foreign-key validation at `2badb8c2`.
Preview did not change the input database. Explicit indexer, download-client and
notification settings, original account rows/hashes, API keys, authentication
settings, URL bases and ports were preserved. Both instances have different
API keys: the drop-in route is two independently migrated instances, preserving
their endpoints. This rehearsal does not establish an unchanged-client merge
into a single endpoint.

The real-data API comparison exposed an importer defect: four author metadata
arrays were copied and then overwritten with empty defaults. `53d76bcc` preserves
populated arrays and defaults only absent/null values. All 101 importer tests
pass, including four populated-array regressions that failed before repair.
A fresh packaged archive at `69ec4a3d` repeated both conversions and checks:
all managed author images, links, aliases and genres match the source arrays,
with zero mismatches, integrity errors or foreign-key violations.

`bd23e039` subsequently repairs legacy work release dates, selected-edition page
counts and the predecessor's file-based percentage response. Native responses
retain edition dates, audiobook page-count suppression and availability-based
percentages. The 64 focused mapper/paging/identity checks pass. Packaged primary
HTTP comparison confirms the repaired values in three details and 25 paged
records. `42d174af` also restores editions omitted from the unscoped legacy work
catalog after format splitting. Six sampled works across both libraries now
return the source's 7, 15 and 4 editions, with matching edition/provider IDs,
publishers and release dates. These are bounded samples, not all-record proofs.

## Rollback and runtime

Both original `2badb8c2` conversions passed restore preview and rollback. All 39
logical source tables and SQLite identity counters match the original snapshots.
Restored files match their retained source-backup files byte for byte. They are
not claimed byte-identical to the initial backup-API snapshot; logical equality
was checked independently. Both restored schema-158 sources and retained native
targets pass integrity and foreign-key checks. Frozen snapshots remain retained.

Foreground runtime checks on both converted copies passed health/schema,
author/provider counts, full legacy work counts and native format-book counts.
Missing/wrong API keys return 401. Original Forms account hashes are retained;
this does not claim a real-password browser login. Source-image clones use the
same snapshots for comparison, with no production service stop or data write.

Runtime copies use no external network, no published host ports, dropped
capabilities and read-only media mounts. Notification/client configurations
remain present for fidelity without reaching live services. SeerrNG library
reads using the exact deployed `v3.72.3` image pass for both libraries: primary
14,377 ebooks/7,999 audiobooks; audio 14,790 ebooks/8,192 audiobooks. Paging returns
each record once, sampled file classifications agree and restricted-key admin
boundaries hold. Sampled primary history includes both formats; the sampled audio
library audiobook history is empty and is not claimed positive coverage. Full
web request-to-availability fulfillment remains open.

Cached source cover images were copied into private runtime caches. At
`f9f209ee`, 40 root cover requests across both libraries and full/restricted keys
return HTTP 200 with source-byte hashes. Fourteen boundary checks retain denials
for absent/wrong keys, UI-root API-key access and unsafe/nonimage paths. The
authenticated API cover routes also match source bytes. Original Forms account
hash retention does not establish browser login; the source-image harness needs
a writable temporary directory for cookie key storage before that test.

Calling the deployed SeerrNG cover methods exposed a second authentication gap:
registered `/MediaCoverProxy/{sha256}/{image}` paths returned 401. `1b8865ee`
adds API authentication only for safe registered raster-image paths. All 104
focused policy/controller/proxy checks pass. Packaged `8a9908b1` changes the
offline clones' registered paths to 404 because remote images cannot be fetched;
it also retains all 40 cached-cover hash matches. A separate synthetic Forms
installation with no operator data permits HTTPS fetches of an original fixture
PNG from this public repository. The deployed SeerrNG client returns the exact
original book PNG and the author's matching original/cached resized rendition.
Missing/wrong keys, UI access and nonimage requests remain denied. This proves
the positive proxy/authentication path without enabling egress on real-data
clones; it does not assert live operator-provider image acceptance.

Synthetic acquisition exposed a persisted-monitoring defect: the release
specification read an ignored legacy property instead of the stored format flag.
`5c6ef346` repairs it with eight regressions. `310eae07` retains valid schemeless
EPUB ISBN URNs and legacy scheme attributes; `4772c317` refreshes stale persisted
tag records when they contain recoverable ISBN evidence. Focused metadata/cache
checks pass. The failed download subsequently imports on retry without changing
its downloaded bytes. Import writes the configured series tags while preserving
all reading content. The clean repeatable rehearsal in
`integration/run-acquisition-smoke.py` passes with the `4772c317` packaged
application: source-158 conversion, actual deployed SeerrNG adapter request,
Torznab search/grab, real local qBittorrent transfer, normal completed-download
import, ebook-only availability and history, unchanged reading content with
configured series tags, restart availability, and retained source schema/row
equality. Both databases pass integrity/FK checks. A second format-specific run
also passes with an original synthesized MP3: scoped audiobook request, native
profile/edition identity, search/grab, real transfer, audiobook-only availability,
grab/import history, restart, exact imported audio bytes, and source-backup
schema/row integrity. The generalized ebook path passes again afterward. These
use isolated loopback peers; they do not establish live trackers, Usenet, other
audio formats or SeerrNG web-request database fulfillment.

## Real-media reads

Production readers from the packaged `2badb8c2` candidate read one real file per
format: EPUB, KEPUB, AZW3, MOBI, PDF, MP3, M4B and M4A. All eight completed, with
nonempty tag maps and unchanged before/after SHA-256. The PDF has no title tag;
the MP3 has no title tag but yields a trustworthy four-second duration. M4B and
M4A yield 41 and 10,345 seconds. Missing optional metadata is retained as missing.
The initial standalone PDF probe omitted the application's AGPL initialization;
repeating with the initialization used by production Bootstrap passed.

The `69ec4a3d` runtime serves all eight through authenticated book-file range
requests: HTTP 206, correct full file size and 256 bytes matching the original
file. Library views are SSHFS read-only mounts and container read-only binds.
These checks do not establish tagging, rename/move, import, acquisition, M4B
merge, crash recovery, playback or DRM support for the full operator libraries.

## Evidence and remaining work

Private evidence, frozen snapshots and rollback backups are retained under
`/var/tmp/chaptarrng-operator-data-20261105` on `kspld0`, mode 0700. Raw databases,
configuration, provider credentials, account hashes, filenames and API payloads
must stay outside Git and shared memory. Summary JSON includes table fidelity,
author-array comparison, runtime API checks, range hashes and media-read results.
Remote task snapshot staging was removed after transfer verification. Task-owned
read-only media mounts must be unmounted after consumers exit, before cleanup.

Live providers, complete SeerrNG acquisition, media import/write/recovery,
representative PostgreSQL operator data, headed UI, external distribution and
production acceptance remain open. Desktop Pool v2 still has no available lease;
other tasks' quarantined artifacts/profiles have not been taken over or reset.
Full CI passed at `69ec4a3d` and `c15d5ac9`. Later `4772c317` CI stopped on stale
localization callsite line references; `1ff8feb6` updates those references and all
67 localization tests pass locally. Archive validation at `c15d5ac9` passed nine
platforms but exposed missing Linux x86 SDK metadata. `317c5b43` supplies project
metadata without replacing SDK files; `1ff8feb6` confines it to Core/ASP.NET so
Linux no longer requests a Windows Desktop runtime. A complete Linux x86 archive
built with clean SDK metadata starts on Debian 12 i386, migrates an empty database
to 125 and returns `/ping` 200. The same runtime crashes during initialization on
the desktop's Arch Linux installation; that distribution issue remains open.
All ten hosted archive jobs subsequently pass at `8a9908b1` in
[42005003595](https://github.com/snapetech-org/chaptarrng/actions/runs/42005003595),
including both ebook and audiobook acquisition/restart rehearsals. Runtime packs
were reused from the successful producer in `41998364417` after checking trusted
main provenance and matching producer input Git blobs; application archives were
built at the current revision. The strict expanded API comparison at `02071e40`
has 90 cases: 80 pass, seven strict differences, one source error and two expected
policy differences. All four new catalog cases pass after `2d86b444`. Full CI at
`8a9908b1` is running. Each result retains its tested revision and bounded scope.

## Full Seerr request and database-identity rehearsal

The deployed SeerrNG image's full HTTP workflow initially passed user login,
pending request, denied self-approval, owner approval, real acquisition/import,
availability and restart. A stricter SQLite audit then found two media records
for the same imported book. Availability alone was an insufficient acceptance
check.

A subsequent baseline runs the unchanged deployed client against the unmodified
public BookshelfNG `.76` musl executable. User request/approval, local torrent
transfer, EPUB import and grab/import history pass. After a real library scan,
the stopped Seerr database has two media records for the same source book while
the original request/requester remain completed. This establishes that duplicate
scan records precede ChaptarrNG migration. Separate source audiobook dispatch
leaves two unowned identifiers; its transfer completes but source import does
not complete within the bounded test, so it is not a full audiobook baseline pass.
The EPUB reading-content checks pass before a missing series-tag assertion caused
by the fixture metadata refresh; no complete source tagging pass is claimed.

The source baseline uses a normal shared work with one selected-format edition,
an existing author directory, source-compatible Readarr metadata resources and
the single synthetic source API key. It does not use real accounts or providers.
Preparatory fixture failures and the final aggregate are retained under
`/var/tmp/chaptarrng-seerr-web-acquisition-20261105/source-76-baseline/`.
The duplicate and identifier defects belong to the existing client; they are
not established migration regressions.

[SeerrNG PR #527](https://github.com/snapetech-org/seerrng/pull/527), source
`389bdafd6`, attaches dispatch identifiers through their actual entity relation
and matches server-scoped catalog identities during scans. Four catalog
regressions and a relation-ownership assertion fail before repair. All 73
scanner/base-scanner/dispatch tests, server compilation and focused lint pass.

Both full-server formats pass with a local test-only image containing those
compiled repairs and ChaptarrNG archive `f060374a`. Each retains exactly one
media record, zero orphaned identifiers, original request/media/requester IDs,
completed status, correct format links, content and source backup across
restart. No source service or production Seerr data is changed. These candidate
results do not imply the Seerr repair has been published or deployed. Source,
image/module provenance and synthetic evidence remain under
`/var/tmp/chaptarrng-seerr-web-acquisition-20261105/` on `kspld0`.

## Large-library identity lookup regression

The all-record source collection succeeds for both libraries: 1,379/1,400
authors, 14,605/14,963 works and all 27,559/28,499 files, with HTTP 200 for every
file batch. Native full collection is stopped for repair after repeatedly
scanning the migration map. The primary copy contains 206,823 mappings. The old
case-insensitive entity predicate could use only the index's `Source` prefix;
resource projection repeated that scan for individual IDs.

Migration 126 adds functional indexes for source and target lookups and removes
the redundant old nonunique prefix index. The service sorts selected rows after
the query, preserving SQLite/PostgreSQL null ordering; the previous SQL sort
made SQLite choose a source-order scan over a target-ID seek even with a target
index. Four real-migration query-plan checks reproduce the missing seeks before
repair. All 112 focused migration/importer/identity checks now pass, including a
mixed/null ordering regression.

On an isolated primary-data copy, 16 warm SQL lookups per direction change from
12.7/12.8 ms to 0.0034/0.0023 ms per source/target lookup, with identical result
hashes and row counts. This measures SQL lookup cost, not complete HTTP latency.
New schema-126 packaged migration/rollback and all-record HTTP comparison remain
to be run. Original snapshots and production services are unchanged; stopped
probe logs and benchmark evidence remain private with the operator rehearsal.

## Nonempty live-provider response replay

Read-only checks of the three distinct enabled Torznab endpoints in the saved
operator configurations return two valid capability documents and one HTTP 410.
Four bounded book searches against the two responding endpoints return HTTP 200:
one provider returns two releases for each library; the other returns empty RSS.
No release is grabbed, no notification is sent and production services remain
running. The unavailable endpoint is already present in the source configuration;
this check does not repair or enable it.

The captured responses are replayed through the exact `.76` source image and the
`6f40dbf9` target on their full real-data offline copies. The old target has 43
response differences per library, including an added title/author rejection for
a release the source accepts. The migration compatibility flag had left the
native matcher active. The repair restores source parser order, selected-edition
fuzzy fallback and identification, then avoids applying the native title filter
to that result. A legacy response projection preserves the source field set,
empty torrent info-hash representation and optional grab request identities.

The final local diagnostic derivative repeats both library searches: two source
and two target releases, **zero differences** after excluding only time-derived
age fields. Commit `e816db99` contains the repair. Full cross-platform Build
[42102844753](https://github.com/snapetech-org/chaptarrng/actions/runs/42102844753)
passes at `340c9718`, which changes only localization callsite line references.
Strict HTTP differential run
[42101205257](https://github.com/snapetech-org/chaptarrng/actions/runs/42101205257)
reports 94 cases: 89 pass, zero fail, four documented policy differences and one
inconclusive source error. Canonical real-response replay remains separate from
the local rebuilt-assembly comparison.

All ten canonical archives from producer
[42101210462](https://github.com/snapetech-org/chaptarrng/actions/runs/42101210462)
have verified retained checksums. Its five native Linux installed migration and
forced rollback suites pass. Windows x64/x86 and macOS x64/ARM64 retained-payload
consumer [42103515268](https://github.com/snapetech-org/chaptarrng/actions/runs/42103515268)
passes; the exact FreeBSD archive passes the same installed suite on the retained
FreeBSD 15.1 VM. Both complete Seerr ebook and audiobook workflows pass against
the untouched canonical musl x64 payload with the identified, unpublished client
repair, including approval, acquisition, import, scan, restart, profile identity,
one persisted media row and no orphan identifiers. These passes do not close
headed GUI, external feeds, live download-client fulfillment or production gates.

Replay prerequisites are explicit: download clients, import lists, RSS and
notifications are disabled; queued commands and saved provider retry cooldowns
are cleared only in the disposable copies. The earlier cooldown-blocked source
runs are inconclusive setup attempts. Captured network responses are served by
loopback proxies in network-none namespaces, so replay does not repeat tracker
searches. All four clones' original database files are restored with matching
hashes afterward. Raw responses, API keys, query/book metadata, source copies,
patch/assembly hashes and restoration receipts remain private under
`/var/tmp/chaptarrng-operator-data-20261105/live-provider-readonly-20261105/`.


## Ongoing native Hardcover refresh regression

A bounded, read-only GraphQL query succeeds with the preserved source token;
adding the application's reading-format field also returns zero GraphQL errors.
The saved V5 fallback URL instead returns a non-JSON HTTP 200 response. Lookup
routes native Hardcover IDs directly, but that alone did not prove refresh:
canonical `e816db99` single-author refresh reports command completion while
`RefreshAuthorInfo` contacts V5 and records an error without updating metadata.
The exact source `.76` refresh contacts GraphQL and updates the existing work.

The repair routes native refresh and bulk scheduling through the native provider,
creates independent audio/ebook snapshots, carries the provider reading-format
ID into edition retention, and preserves predecessor metadata presentation and
undeclared-format handling in migrated libraries. The final local derivative
passes both full real-data library replays: each source and target contacts the
captured GraphQL endpoint once, updates the existing work's genres and preserves
its ID. Every compared source author/book field matches after excluding only
`author/lastBook/lastInfoSync`, which records separate refresh times. No author,
book or provider refresh errors remain. Each bounded author snapshot contains
one work; this is ongoing refresh evidence, not an exhaustive upstream-catalog
refresh or a new live acquisition pass. All raw bodies remain private.

The full Core suite passes 5,222 tests with 29 skips and no failures; the final
affected provider/lookup/refresh/bulk suite passes 121 tests. Localization tests
pass, and source-catalog validation passes in its existing `--allow-pending`
mode; the repository's pending translations remain pending. Canonical rebuild
and installed validation for the new application code remain separate follow-up
checks. Do not count the earlier completed commands as successful refresh
acceptance. The Desktop Pool reservation still times out without acquiring a
lease; no headed GUI pass is claimed.

Both source download-client connections currently return HTTP 502 before
successful authentication, including read-only requests from the actual source
containers. No live download was attempted. Each library's current source
settings match its own retained snapshot; an earlier comparison that deduplicated
the connection and compared the audiobook settings to the primary settings was
invalid and superseded. The HTTP 410 indexer and HTTP 502 client failures remain
pre-existing source availability issues, not migration acceptance passes.


## Real-media mutation on disposable copies

The current local Release production services (`fdc59ea7` Core assembly) operate
on private copies of all eight sampled formats; originals remain on read-only
mounts and retain their full SHA-256 hashes. EPUB and KEPUB series metadata
writes read back correctly, with every non-OPF archive entry retaining its hash.
MP3, M4B and M4A synthetic title tags read back through production extraction;
FFmpeg compressed-audio payload hashes and FFprobe chapter records are unchanged.
AZW3, MOBI and PDF follow the existing non-Calibre no-op path and retain every
byte. Both actual source installations have zero Calibre-library root folders;
this check does not claim Calibre-server writes or native Kindle/PDF tagging.

The first audio verifier incorrectly required an unprefixed `TITLE` field;
production extraction exposes container/compatibility keys. The corrected
verifier checks the unique synthetic title value. Both attempts are retained;
this harness correction required no application change. Results, copies, build
and runtime logs and exact Core hash remain private under
`/var/tmp/chaptarrng-operator-data-20261105/real-media-write-fdc59ea7/`.
This is a production-service copy test, not canonical installed import, rename,
process-crash recovery or production-library mutation acceptance.


## Real-data rename differential and restart

Actual `RenameFiles` commands against `.76` and the local current target rename
private copies of five mapped ebook formats and two mapped audio formats. The
first comparison finds different destination basenames for all five ebooks:
source naming counts all files in an edition, while native part assignment
normalizes ebook alternatives to one part. The saved pattern includes the part
number, so a successful command alone hid the changed filenames.

The compatibility repair preserves persisted source `Part` values and counts
all author files sharing the source edition, including managed and unselected
files. It resolves media-specific edition copies through the legacy identity
map and keeps unmapped target IDs in a separate namespace. Fresh native part
assignment remains unchanged. Scoped previews use the same complete counts as
execution. Focused regressions cover preview/execution agreement, media splits,
managed files, existing zero/nonsequential parts and ID namespace collisions.

The corrected comparison has identical destination paths and file IDs for EPUB,
KEPUB, AZW3, MOBI, PDF, MP3 and M4B. All 14 source/target cases preserve complete
file hashes and every original. Fresh application processes read back the same
renamed paths, IDs and bytes in another 14 checks after restart. The initial
audio samples were unassociated files, so that attempt is inconclusive; mapped
MP3 and M4B samples replace them. There are no mapped M4A source records, and
M4A is not claimed as a mapped-rename pass. Its unassociated record/content and
separate tag-copy checks remain distinct evidence.

All four disposable instances use network-none namespaces, private config copies
and only the bounded sample directory as a writable library. Logs, database
copies, API bodies and source filenames stay private under
`/var/tmp/chaptarrng-operator-data-20261105/real-media-rename-source-count-fix/`.
The first filename-difference and unmapped-audio attempts are retained under
`real-media-rename-fdc59ea7/`. Canonical installed validation remains a follow-up
for this new application change; no production library was renamed.


The same naming review finds three additional predecessor choices that native
naming changes: disabled renaming returned the original filename immediately;
a pattern without a part token used its rendered filename even for multiple
files; and an absent release group defaulted to `Readarr`. The migrated mode now
preserves each choice. Fresh native defaults retain their existing organization,
filename fallback and branding. Six focused cases cover both modes.

Actual source/target HTTP command replays apply all three controlled naming
settings to the same seven mapped real-media formats. All 21 path/ID comparisons
match, with 42 source/target file-integrity checks passing. The disabled case
correctly leaves each file in place. Metadata and file bodies remain private
under `real-media-rename-patterns/`; these variants change only disposable copied
configuration. The full prior count-fix suite passed 5,224 tests with 29 skips;
the final broader naming fix passes 85 affected tests and the full Core suite
passes 5,231 tests, with 29 skips and no failures. Localization tests pass.
Canonical build and installed checks remain separate follow-up validation.


## Canonical naming candidate and wider metadata refresh

Canonical `cb16f8102ce504606b93e1852da710ee8558869f` passes Full Build,
strict API differential, all ten standalone archive builds, all five native
Linux installed suites, all four Windows/macOS retained-archive consumers and
the native FreeBSD handoff suite. FreeBSD verifies No/Manual/Yes, healthy
migration, source backup and forced-failure recovery. The harness accepts Linux
runtime selectors only; the failed invocation with a FreeBSD selector is
retained separately from the corrected native pass.

The untouched canonical musl archive also reproduces the seven-format real-data
rename pass: seven identical source/target paths and IDs, 14 file-integrity
checks and 14 fresh-process restart checks. Both format-specific nonempty RSS
replays have zero field differences apart from moving age fields. Both bounded
actual metadata captures match source-projected author/book fields apart from
refresh timestamps. Complete Seerr web acquisition/import/scan/restart smoke
passes for ebook and audiobook with the recorded repaired client test image.
These results qualify this candidate for those checks, not publication or
production cutover.

Broader provider fixtures expose remaining metadata gaps in that candidate:
native automatic filtering drops otherwise usable catalogs over 300 works;
representative-edition pruning drops source-allowed editions; an unusable
edition can abort the author refresh; imported `hc:<id>` editions can duplicate
when provider refresh returns `hc:edition:<id>`. Successful commands and stable
book counts conceal the edition losses and duplicates.

The repair keeps source-compatible title fallback, slugs, image URLs and link
labels; skips unusable editions; retains every profile-allowed edition; and
matches unique provider-scoped aliases while preserving local edition IDs.
Shared ISBN/ASIN values are insufficient identity, ambiguous alias matches do
not merge rows, and consumed rows cannot match a second remote edition. Fresh
native filtering and representative-edition selection remain covered separately.

A bounded actual Hardcover response augmented with two explicitly synthetic
print editions yields three editions and three provider identities in `.76`.
Untouched `cb16f810` yields two rows for one identity, losing both synthetic
additions. The intermediate retention-only repair yields four rows for three
identities. With alias matching, the local production-Core derivative yields
three rows for three identities, matching the source. This is controlled
synthetic augmentation, not a claim about the original upstream catalog.
Private captures, database copies, command bodies and binary provenance remain
under `live-provider-readonly-20261105/` in the private operator evidence root.

The expanded focused suite passes 127 tests. Full Core passes 5,242 tests,
with 29 skips and no failures. A second actual refresh command in each isolated
application retains exactly three editions and three provider identities, with
unchanged row IDs; both original imported row IDs survive the first refresh.
Localization inventory checks pass in existing `--allow-pending` mode; pending
translations remain pending. The new code requires its own canonical build and
installed acceptance. Desktop Pool still times out on capacity without
an owned lease, so no headed GUI pass is claimed.


## New-work legacy identities after refresh

A larger controlled replay expands the captured one-work response with 300
explicitly synthetic print works lacking dates and ISBNs. The unchanged operator
profiles correctly exclude those additions in both applications. With shared
profile flags explicitly made permissive in the disposable copies, `.76` exposes
301 works while `2a928f57` exposes 602 format rows for 301 provider identities:
newly discovered counterparts do not receive the legacy identity maps created
at import. This is a legacy API projection/route gap, separate from retention.

The repair registers persistent work mappings after refresh and book addition,
before other synchronous event consumers. It pairs only unambiguous provider
identities within one author, extends an existing legacy identity when a format
is added, preserves conflicting imported identities and same-format pockets,
and avoids occupied legacy IDs. Newly discovered IDs are stable target-owned
legacy API identities; this does not predict which numeric IDs BookshelfNG would
have allocated in a separate installation. Fresh native installations skip it.

The production-Core derivative returns 301 distinct legacy works while retaining
602 native format rows. A second HTTP author refresh preserves all 301 legacy
IDs. Single-work GET resolves a newly assigned ID, and an actual legacy monitor
mutation unmonitors both target format counterparts. Source and target both
retain the requested unmonitored state after the second refresh. Raw bodies and
database evidence are private under `metadata-large-catalog-lifecycle/`.

## Real Debian package transaction

A separate task-owned Debian 13 guest installs the actual public `.76` Debian
package (SHA-256 `dc5d33b575684e5667111510735ba043572978ad85f1516f6f335566156938e4`)
under real systemd. The source has schema 158, four authors, six works, nine
editions and four files. The candidate-only package wraps the untouched canonical
`2a928f57` Linux x64 archive; package SHA-256 is
`960ee0a536f3750ab4fe822139e4c40a81f904456612c651fe6edbb8f6a98fdd`.

Answering No preserves the installed predecessor version, live process ID,
database counts, environment and media canary. Yes installs the same package
identity, migrates to schema 126 with four authors/nine format rows/nine
editions/four files, serves health and survives an actual service restart.
An independent forced-health-failure case restores schema 158, source counts,
original application/service, environment and canary, with healthy source HTTP.
The guest checkpoint resets only the test fixture between independent cases;
the failure case itself exercises application rollback rather than VM recovery.

Automatic application rollback leaves dpkg's candidate half-configured after
the deliberately failed post-install script. Restoring the package database's
old version is a separate explicit downgrade, not an automatic package rollback
claim. Synthetic package transaction checks are narrower evidence: the current
Fedora RPM CI uses stub executables and a simulated systemctl, so it does not
establish real Fedora application migration/health/recovery. This Debian runtime
check does not close Fedora or external feed delivery gates.

Evidence, the checksum-verified Debian 13 base manifest, package build log,
source fixture/setup failures and corrected runs are retained under
`/var/tmp/chaptarrng-native-standalone-20261105/debian13-package/`.
The explicit downgrade then restores package version `0.4.21.76-1` to dpkg's
installed state. The public predecessor's disabled unit needs an explicit start;
after that, the restored service is active and source HTTP is healthy. Both
commands and outcomes are retained rather than claiming automatic package
metadata rollback.

The final post-refresh mapping repair passes the full Core suite: 5,246 passed,
29 skipped, no failures. The earlier run's only failure was a new assertion
expecting insertion order; legacy resolution intentionally orders audio before
ebook. The corrected assertion preserves that existing contract. The repair
has now completed its canonical qualification described below.

## Canonical application qualification at `3caff177`

The full cross-platform Build (`42127922607`), strict API suite (`42127926493`),
ten-archive producer and five installed Linux cases (`42127930955`), four retained
Windows/macOS consumers (`42129843754`), and Helm rehearsal (`42127935076`) all
passed. The consumers ran at repository head `4dfb8c9a`, with the unchanged
`3caff177` application inputs verified before archive reuse. Native FreeBSD
acceptance also passed No/Manual/Yes, health, backup and forced rollback.

Fresh frozen source copies and the canonical target compared 88,405 author,
book and file records across the two operator libraries with zero missing,
extra or duplicate identities and zero source-value differences. Target-only
fields and omitted source-null fields were accounted for separately; this is
not a byte-identical JSON claim. Both fresh conversions preserved source user
columns, provider settings and authentication hashes, passed integrity and
foreign-key checks, and restored all logical tables and sequences on rollback.
Neither production library was changed.

Both SeerrNG web request-to-acquisition/import/restart cases passed against the
canonical application using the repaired, unpublished client fixture. Actual
rename and restart replay preserved IDs, paths and bytes for EPUB, KEPUB, AZW3,
MOBI, PDF, MP3 and M4B. No mapped M4A source file was available for this replay.

The canonical Debian package also passed its real systemd transaction, restart,
forced failure recovery and explicit predecessor downgrade. Fedora 44 passed
the actual DNF No/Yes transaction, converted application health and restart with
SELinux enforcing. Its subsequent SIGKILL test reached schema 126 and HTTP 200,
then exposed a health-hook defect: the listener wait could outlive the main
process and delay rollback until the full startup deadline. Canceling that wait
restored a healthy predecessor; that intervention is not automatic crash
recovery acceptance. Process-death detection and bounded HTTP probes pass two
regression cases and the rebuilt real RPM's after-readiness SIGKILL case. With
SELinux enforcing, DNF exits 1, the hook releases automatic rollback, and the
predecessor returns healthy with schema 158, all original counts, environment
and canary preserved. RPM package metadata still names the candidate until an
explicit downgrade; application recovery is not package-database rollback.
The repaired package hash is
`61d8fc0aec49262f0fdba466c2a88681945fc60c042e8a70378a4914c6be3a90`;
it combines the unchanged canonical application with the repaired health hook.
The same repaired package also passes No/Yes conversion, HTTP health and a real
service restart. Explicit predecessor downgrade restores the package-manager
version and healthy source separately. Both cases' evidence and the repaired
RPM are retained on the creating host.

The three security scans at `4dfb8c9a` passed (`42129828890`, `42129834139`,
`42129838969`). The scoped StartOS dependency override fixes CVE-2026-118764
without upgrading its SDK. CodeQL remains disabled.

## Remaining low-memory refresh investigation

The three-edition canonical repeat replay passes at a 2 GiB container limit.
The 301-work repeat refresh and legacy-ID lifecycle pass at 6 GiB, but fail with
managed allocation errors at 2 GiB on a fresh converted copy. Corrected inputs
exclude unrelated SQLite sidecars; metadata-only replay still reproduces the
failure. Command completion alone does not qualify the failed API readback.

A diagnostic cold-start allocation trace identifies substantial temporary
allocation in matching-title backfill and author-event edition loading. A forced
diagnostic collection leaves a much smaller live heap; neither a retained leak
nor the precise allocation failure mechanism has yet been established. A
separate probe using the candidate runtime confirms that a 2 GiB container is
recognized as a 1.5 GiB managed-heap budget. No GC workaround has been shipped.
The traced 2 GiB case fails during initial book readback before submitting the
refresh command, narrowing this reproduction to startup/API materialization.
The completed trace records a 1,589,896,760-byte live-heap peak and repeated
full collections running out of small-object-heap space. Joined edition loading
retains a separate book and author graph per edition; the copied library has an
author with 18,439 edition rows. `FindByBook` now shares book and author parents
by database identity within each result, including across SQLite parameter
batches. It retains separate format-specific book IDs and every edition row.

The local application derivative passes the corrected 2 GiB cold-start and
301-work lifecycle replay: 301 unique legacy IDs and provider identities, source
provider-set equality, original and repeated ID stability, both format rows
unmonitored through the new legacy ID, database integrity and no allocation
errors. Eight focused repository tests pass, including shared parents and
distinct format copies across parameter batches. The initial test-schema column
order did not match native `splitOn=Id` layout; correcting that fixture restored
the intended joined mapping. The full Core suite and canonical archive/installed
qualification of this new application revision remain pending.
Private traces, failure logs and coherent input copies are retained for the
investigation. Publication, external channel acceptance, headed UI acceptance
and production cutover remain open.

## Canonical `60d892da` follow-up — 2026-11-06 UTC

The parent-graph fix is pushed as
`60d892da58bcc167a75fe837345cefa5e9f0a059`. Full Core passes 5,248 tests with
29 skipped and no failures. Canonical validation is now green:

- Full Build: `42139610355`, including Linux, Windows, macOS, PostgreSQL and Docker.
- Strict API: `42139653526`; existing explicit runtime/security dispositions
  and source-error boundaries remain separate from exact matches.
- Ten archives and all five installed Linux handoffs: `42139657879`.
- Four retained Windows/macOS consumers: `42140808219`.
- Helm: `42139661768`; security/Semgrep/Trivy: `42139665980`, `42139670886`,
  `42139675203`.

All ten downloaded archives match their producer checksums. The untouched musl
x64 archive repeats the full two-library comparison at 2 GiB: 88,405 records,
zero source-value mismatches, and no missing, extra or duplicate identities.
The canonical 301-work cold-start/refresh/repeat/new-ID monitor lifecycle also
passes at 2 GiB with zero allocation errors and both format counterparts
preserved. The earlier local derivative is diagnostic evidence; these new
results qualify the retained producer payload itself.

Both frozen full operator snapshots pass fresh preview, conversion, health,
restore-preview and rollback at 2 GiB. Source user columns, provider settings,
author metadata and all 56,058 files are preserved. Rollback matches all logical
tables and SQLite sequences; restored bytes match the retained source backup.
Frozen originals remain unchanged. Both canonical Seerr web format cases pass
request, approval, local torrent acquisition, import, scan, restart and identity
audits with the identified repaired client fixture.

Fourteen actual rename commands and fourteen restart readbacks pass across
EPUB, KEPUB, AZW3, MOBI, PDF, MP3 and M4B. Source and target produce the same
paths; IDs, copied bytes and originals are preserved. No mapped M4A rename case
is claimed. The untouched FreeBSD archive passes No/Manual/Yes, health, backup
and forced rollback in the native guest. SSH transport readiness needed separate
diagnosis and a task-owned control connection; application harness deadlines
were unchanged. Failed readiness attempts and network evidence remain retained.

Real Debian 13 and Fedora 44 package transactions both pass No/Yes, conversion,
HTTP health, service restart and forced application/source rollback. Fedora's
case kills the exact target after schema 126 and HTTP 200 while SELinux remains
enforcing. Both cases preserve environment and canary. Separate explicit
downgrades restore the predecessor package-manager version and healthy source.
Candidate package hashes are:

- Debian: `e141525147fd0fc29ddf1aa102abdbcc7cf6118eede5c608b3d726b020163b99`.
- Fedora: `eee9c1e3f9949fc32f90ab8605c6381de6cda1f581fd2dd35f7161454521f419`.

The owner authorized Desktop Pool v2 repair after repeated capacity timeouts,
with an explicit requirement to preserve sessions in use. Inspection confirmed
three stale quarantined slots, four non-ended historical reservations, no live
leases, zero Kasm sessions and no desktop containers. Coordinator state and
artifacts were backed up; authenticated Finish recovery cleared only those
stale rows and preserved profile verification. Another task's FIFO reservation
was preserved. Recovery is recorded in the owner repository at
`docs/runbooks/agent-desktop-pool-v2-recovery-20261106.md` (`0310642`).

A fresh independent Chrome lease is now exercising synthetic source and
candidate services on its own loopback namespace. Migrated Forms login and the
candidate's populated four-author library render successfully. The public `.76`
source renders a blank main page with React selector `TypeError` errors; its
pinned source imports `defaultMemoize` with reselect 5.3.0, while the candidate
uses `lruMemoize`. That source failure is retained, not treated as a candidate
pass or a reason to modify BookshelfNG. Initial fixture setup failures (source
schema flags and the direct-launch temporary directory) remain recorded.
Headed workflow acceptance is still in progress. No publication, production
migration, external-channel delivery or cutover has occurred.

### Headed book-card follow-up

The candidate's Both-format poster list rendered all nine migrated format/copy
records but omitted their format badges. The shared paged-item connector did
not pass `bookIndex.selectedMediaType` to its view. It now forwards that state
independently of each record's own format. Behavioral connector-to-view tests
reproduce the missing property before the fix and pass afterward for audiobook
and ebook rows, including a later change back to a single-format filter.

All 192 frontend CJS tests, TypeScript checking and focused lint pass. The
production UI bundle builds with 47 CSS ordering warnings; no styles were
changed. A clearly identified UI-only derivative over the unchanged canonical
backend visibly shows Audiobook/eBook badges on the correct cards. It preserves
the nine-row count, quality-profile labels and the existing migrated login.
The original canonical archive remains untouched. The new frontend revision
still requires its own retained canonical bundle qualification after the
remaining headed workflow checks.

Book details and the Files tab also render the migrated ebook, series,
400-byte synthetic file, original path and import history. Required screenshots
are persisted through the pool artifact API before any desktop release.


### Narrow Books-page follow-up

Headed testing at 250% standard Chrome zoom (approximately 410 CSS pixels,
not a touch-device simulation) exposed two usability failures. The format
selector was counted as a single fixed-width toolbar action, clipping controls;
the fixed statistics footer also grew tall enough to cover the toolbar and book
list. The format selector now has its own responsive group, ordinary actions
retain their working overflow menu, and View/Sort/Filter remain original nested
menus. Their group has a stable initial width for the measurement lifecycle.
The toolbar can wrap, and statistics use document flow on small screens while
remaining pinned on ordinary desktop views outside the editor.

The footer regression fails before the fix and passes afterward across narrow,
desktop-resize and editor transitions. All 193 frontend CJS tests, TypeScript,
application JS lint and focused CSS lint pass. The production bundle builds with
the existing 47 CSS ordering warnings. A headed UI derivative over canonical
`60d892da` visibly shows the complete narrow toolbar and book cards; screenshot
`338b62cb-5e78-4fb4-b950-a21dbe7e104f` is persisted. Initial layout attempts and
logs remain retained, including the fixed-footer obstruction that inspection
identified. New canonical archive validation is still required.

The first clean desktop ended during an MCP reconnect; the coordinator reported
its idempotency key as ended and its container was absent before a new isolated
QA phase was acquired. Only the two task-owned synthetic backends were rebound
to the new desktop namespace. Existing profiles, other reservations and original
candidate payloads were preserved. Screenshot updates can lag accepted inputs;
visual checks wait for a confirmed new state before acting.


### Imported profile edit follow-up

Headed settings validation found that changing only a migrated generic profile's
name failed native validation: its preserved PDF quality and cutoff were rejected
on the audiobook clone. Validation now permits unchanged imported choices only
on PUT, with a persistent profile identity mapping and unchanged profile type.
Creates, unmapped updates, newly incompatible choices, changed conversion targets
and new incompatible group-cutoff children remain rejected.

Both audiobook/ebook retention regressions fail before the fix. All 34 focused
controller cases pass afterward, including update/readback and the rejection
boundaries. Full Core passes 5,257 tests with 29 skipped and zero failures. The
isolated headed service uses a clearly identified API-assembly derivative over
canonical `60d892da`, retaining the original archive and the verified UI bundle.
Actual Chrome save, full page reload and reopening show the temporary profile
name persisted. A SQLite observer running as application UID 99 with a read-only
config mount confirms only profile 1's Name changed across all columns of all
four profiles. Restoring the name through Chrome leaves zero profile differences
from the baseline. Screenshot `00344c80-eca6-410e-ba1f-4381d6552a7c` is persisted.
This qualifies the isolated derivative; new canonical payload acceptance remains
required. The baseline observer first used the wrong database basename; that
failed attempt is retained. The fixture uses converted `readarr.db` in place.

The `17dc081d` canonical campaign includes the preceding UI/dependency fixes,
with API, Helm, Security, Semgrep and Trivy successful; full Build and producer
validation were still running when this new profile defect was found. Those
runs do not qualify this later backend correction. Existing profile list/schema
comparisons also do not establish complete unchanged-client profile mutation
coverage; the same-input create/update/delete lifecycle remains a review item.


### Generic quality profile lifecycle follow-up

The source `.76` accepts a generic profile containing all nine quality entries,
with EPUB, MP3 and M4B allowed and M4B as cutoff. The candidate previously rejected
that same POST with ProfileType, Items and Cutoff errors. Isolated source rows
were cleaned up after recording this mismatch.

Generic legacy CRUD now creates and updates two native counterparts atomically,
using persistent legacy IDs and an ID high-water counter retained after deletion.
Shared updates preserve native conversion preferences and format-specific custom
format scores. Deletion checks both counterparts for author, import-list and
root-folder references before removing either. Missing deletes remain no-ops;
ambiguous or incomplete mapped identities fail closed. Native validation remains
strict outside generic compatibility requests.

All 52 focused SQLite/controller cases pass. Twelve PostgreSQL 16 cases separately
pass, including concurrent creation, native/legacy ID collision, reference guards
and rollback when the second counterpart's insert/update/delete fails. Full Core
passes 5,275 tests, with 41 skipped and zero failures. PostgreSQL cases skipped in
the ordinary local suite are covered by that separate database run.

The real `.76` source and an explicitly derived candidate pass nine strict HTTP
lifecycle cases with zero source-field mismatches. A separate ten-case run
restarts the candidate process between create and update: wire/native IDs and
native conversion settings survive restart, then the generic update preserves
those native settings. Cleanup leaves the original source and native profile
rows unchanged. Only generated top-level profile IDs are normalized from their
actual create responses. These cases are now part of the disposable differential
harness; new canonical archive and exact-head CI qualification remain required.

Private evidence is retained under
`/var/tmp/chaptarrng-desktop-repair-20261105/` on the desktop-pool guest and
`/var/tmp/chaptarrng-native-standalone-20261105/release-api-fix/` on kspld0.
The services use synthetic fixtures, not operator databases.

### x86 canonical campaign failure

The `17dc081d` full Build, API, Helm and security checks succeeded. Producer run
42151309562 built all ten archives, but its installed Linux x86 handoff failed
(job 127687941185). The candidate migrated to schema 126 and listened on the
health endpoint, then exited before acceptance completed. Automatic source
restore passed. The retained log does not establish the exit cause. The exact
x86 archive SHA-256 is
`26647f6bed7eea57e5711eb5eb713b1e8531958493b5adaf90e208fd9d9c6a30`.
Failure evidence is retained at
`/var/tmp/chaptarrng-native-standalone-20261105/producer-17dc081d-x86-failure/`.
This gate is open; previous x86 passes do not qualify the failed payload, and
that campaign does not qualify later backend changes. Publication and cutover
remain frozen.


### Hosted generic-profile validation follow-up

API differential run [42159528647](https://github.com/snapetech-org/chaptarrng/actions/runs/42159528647)
completed at `2a9e0902`: 103 cases, 98 passes, zero failures, four explicit
expected policy differences and one inconclusive predecessor paging request.
The earlier zero-inconclusive summary was incorrect; the retained JSON records
BookshelfNG HTTP 500 for `books.paged` while ChaptarrNG returns HTTP 200. All nine new generic profile lifecycle
cases pass. Full Build run 42160855848 passes PostgreSQL migration/profile checks,
but its Linux, Windows and macOS jobs stop at the source-message review check:
the profile validator's unchanged message moved and its call now chains a
compatibility condition. The reviewed callsite line and fingerprint are updated;
all 67 localization tests pass locally afterward. Translation text, source-message
identity and immutable predecessor catalogs are unchanged. This metadata correction
requires a new workflow revision; rerunning the old revision cannot validate it.

The exact failing x86 archive also passes a complete local 32-bit Debian container
No/Manual/Yes/health/forced-rollback run, but repeated startup then reproduces an
intermittent candidate composition failure. An explicitly diagnostic Host-assembly
override reproduces managed errors and native SIGABRT/SIGSEGV crashes; a core and
backtrace are retained privately. Those diagnostic copies are not release payloads.
A separate Debian VM run fails earlier when the unchanged `.76` predecessor
segfaults during Manual restart; its source core and evidence are retained
separately. Neither result closes the hosted x86 failure or establishes its cause.
A basic probe finds zero parent-method-table offset mismatches across 5,872
classes and passes seven million inherited type checks, narrowing that hypothesis.


### Whole-product M4B hard-kill recovery

`integration/test-m4b-whole-app-recovery.py` now exercises the native Linux x64
product with the pinned real-tool image. The source-build inputs were compared
byte-for-byte with `802e3b8b` (zero backend/build-source differences), then the
console product built with zero warnings/errors. The final reusable harness
passes against that output: native HTTP health, a real FFmpeg gate, SIGKILL of
the product container, exit of every original application/tool process, durable
Converting/attempt-1 state, restart health, and ReadyToImport/attempt-2 recovery.
The resulting AAC/M4B has the expected duration, chapter titles and offsets, a
matching manifest digest, and unchanged source hashes. The injected exit is 137
with OOMKilled false. The harness removes only its owned stopped containers;
private evidence remains under
`/var/tmp/chaptarrng-native-standalone-20261105/m4b-whole-app-general-802e3b8b-final/evidence/`.

The fixture inserts a queued job offline into a fresh native database and stages
chapter-sidecar inputs as the importer does. It does not establish HTTP enqueue,
acquisition/import completion, representative operator-media acceptance, installed
service recovery or final canonical archive acceptance. The hosted Linux Build
now runs it in addition to the existing component real-tool cases and uploads
only its synthetic evidence directory, excluding the generated configuration
and database. Exact-head CI for this new harness remains required.

Initial test attempts are retained separately: one compared uppercase/lowercase
SHA-256 strings literally, and another supplied chapter metadata without the
importer's chapter-sidecar staging. Correcting those test fixtures required no
application behavior changes. Earlier full-product derivative checks also pass,
but they remain identified derivatives rather than canonical archives.


### Quality-profile identity boundary follow-up

A new regression creates and deletes source profile 127 before migration. It
fails against the previous importer because no legacy allocator high-water mark
survives conversion. Import now reserves the source SQLite sequence as well as
current IDs. PostgreSQL captures its allocator state before the row-only snapshot
is created, including the distinction between `setval(..., true)` and
`setval(..., false)`. The compatibility counter is imported transactionally;
out-of-range source allocators stop conversion without changing the source.

Three further regressions reproduce an unexposed native audiobook ID acting as
a second legacy address for another profile: GET incorrectly returned it, PUT
mutated it, and DELETE removed it. The native-ID fallback now excludes rows
already owned by a legacy mapping. Missing legacy reads/updates reject, and
missing deletes remain no-ops without altering either counterpart.

All 125 focused SQLite/importer cases and 25 separate PostgreSQL cases pass.
Full Core passes 5,280 tests with 46 skipped and zero failures; all 67
localization tests also pass. The disposable HTTP fixture now creates/deletes
profile 127 before the source snapshot and requires new source/target wire IDs
above that boundary. It also checks hidden native counterpart IDs return 404
and cannot delete the new legacy profile. The fixture's two rows, allocator 127
and SQLite integrity are verified locally. New exact-head HTTP and hosted Build
validation remain required; the earlier HTTP receipts do not cover these added
boundary cases.

Failure and passing logs are retained under
`/var/tmp/chaptarrng-native-standalone-20261105/release-api-fix/` (the
`profile-source-sequence-*`, `profile-hidden-native-alias-before-fix.log` and
`profile-identity-boundaries-*` receipts). A fixture-preparation attempt first
omitted its required empty config/library directories; that failed attempt is
retained alongside the successful preparation.

Hosted Build [42161823059](https://github.com/snapetech-org/chaptarrng/actions/runs/42161823059)
completed successfully at `802e3b8b` on Linux, Windows and macOS, plus PostgreSQL
and Docker. It predates the new full-product M4B harness and these later identity
corrections. The x86 startup cause, new canonical archive qualification and the
remaining representative-media/external delivery gates remain open. Publication
and production cutover remain frozen.


### Missing-profile HTTP diagnostic classification

Hosted Build [42167374852](https://github.com/snapetech-org/chaptarrng/actions/runs/42167374852)
passes at `899c1681` on all three platforms, Docker and PostgreSQL, including the
new native full-product M4B hard-kill recovery case. API run 42167371589 reaches
the added hidden-native-ID PUT, then stops on an error-body comparison.

A separate isolated `.76`/candidate HTTP probe confirms both missing-profile PUTs
return 404, their messages and other fields match, and every original profile
row remains unchanged. Only `description` differs: the predecessor exposes its
exception stack; the existing Chaptarr error pipeline sets Description to null.
This is now an explicitly recorded diagnostic-redaction policy difference for
those known missing-profile requests, rather than a business-field mismatch.
The classifier requires the exact requested ID/message, 404 on both sides, the
known source ModelNotFound diagnostic, and equality of all remaining source
fields. Tests reject changed status, IDs, messages, public fields and unrelated
descriptions; the classifier also passes replay of the protected real receipt.
Native rows are compared immediately after the negative mutations, and failure
messages include statuses and mismatch paths. The updated HTTP run remains
required. Application error handling and its redaction behavior are unchanged.


### Verified API accounting and native x86 diagnostics, November 6

API run [42177965215](https://github.com/snapetech-org/chaptarrng/actions/runs/42177965215)
completes at `d232a58c`: 109 cases, 102 passes, six explicit policy differences,
zero strict failures and one inconclusive source error. All 15 profile lifecycle
cases complete (13 strict passes and two diagnostic-redaction policy cases).
The aggregate provider-presentation difference count is 578. The GitHub success
status does not mean every request supplied a valid predecessor response.

The full-page source request fails in `LinkAuthorStatistics`: authors without
monitored editions yield an empty statistics object whose BookStatistics is null,
then SelectMany dereferences it. This intentionally populated migration fixture
contains such authors. The source failure remains recorded; it is neither a
parity pass nor a reason to discard those data pockets. Earlier small-page
comparisons and target success do not replace the missing full-page comparison.

The exact native x86 runtime passes 15 profile transaction tests using the signed
Microsoft.TestPlatform.Portable 18.10.1 AnyCPU driver and the candidate's x86 SQLite
library in a private test-output copy. Full-suite execution then exits 139,
OOMKilled false, on the same runtime. Its retained core shows a delegate call
through UTF-16 string bytes and several registers pointing inside strings; this
is evidence of invalid managed references, not a proven root cause. Both baseline
and nonconcurrent-GC test discovery complete; further runtime diagnosis continues.

Initial driver attempts used an incompatible ReadyToRun driver or x64 SQLite;
those setup failures are preserved separately and do not establish application
failures. The private runtimeconfig selects 10.0.12 because the build SDK's output
requests 10.0.14; no shared SDK, cache or source test output was modified. Logs,
invocations, provenance and the full-suite core are retained under
`/var/tmp/chaptarrng-native-standalone-20261105/x86-core-tests/`.

### Linux x86 runtime correction and exact installed acceptance

The x86 failure was corrected at `57bc7d9f` by keeping the guarded Unix
delegate return-buffer fix and relocating exception funclet roots after compacting
GC. The native qualification now requires compacting collections both inside and
after catch handlers, including the nested FluentValidation delegate shape that
reproduced the failure. This supersedes the unresolved-cause status above.

The first rebuilt archive was not acceptable: restore reused a stale custom
runtime package from the shared NuGet cache. Standalone restore and intermediates
are now task-local, and the build fails unless both packaged `libcoreclr.so`
copies match the selected custom runtime package. A standalone build followed by
the ordinary no-restore Core test path passes, proving that the isolated build no
longer poisons repository project metadata or shared restore state.

The retained corrected Linux x86 archive is
`ChaptarrNG-0.0.0-linux-x86.tar.gz`, SHA-256
`f8e8748258bbe566ca09c5ebc027e06924ae83eba45eecdc62e6d5f0c055d78c`.
Its main and updater runtimes both have SHA-256
`7c2687a326b48cb10a1a669e87e565c834419955e5f39fa1397d07cec986ba2c`.
The archive was reused at final head `5a96ccf6` only because the intervening
change affected the external evidence harness and did not change application,
build, launcher or supervisor inputs.

Native `linux/386` installed acceptance passes No, headless Manual and Yes,
source and target health, retained source backup and forced-failure automatic
rollback. The result records a 32-bit ELF executable with machine value 3 rather
than relying on the Python harness process pointer width. The structured result
is retained under
`/var/tmp/chaptarrng-native-standalone-20261106/container-evidence/verified-5a96ccf6/`;
`result.json` has SHA-256
`fc7866576fc0d4870c6fd0b9aaaf3352ed81afca91b50db877d6fe8fdf045eae`.
Task-owned raw crash and register traces were removed after the correction and
structured evidence were secured. Publication and cutover remain frozen pending
the exact-head hosted runs and the remaining external delivery decisions.

Exact application-head Build
[42272702808](https://github.com/snapetech-org/chaptarrng/actions/runs/42272702808)
passes Linux, Windows, macOS, PostgreSQL and Docker at `5a96ccf6`, including the
Windows `.76` Chocolatey handoff and rollback. Standalone run
[42272707424](https://github.com/snapetech-org/chaptarrng/actions/runs/42272707424)
builds and verifies every archive, including the corrected Linux x86 payload and
native runtime qualification. Its public runner remains in the musl acquisition
step far beyond the approximately three-minute prior canonical duration, with no
failure annotation. The exact uploaded musl artifact independently passes the
ebook and audiobook migration, local qBittorrent transfer, import and restart
checks plus the cover-proxy authentication check on `kspld0`. The workflow now
bounds the Docker build, optional package installation and each smoke command at
ten minutes so runner or mirror stalls fail explicitly instead of consuming the
six-hour default. A new workflow revision must supply the final hosted receipt;
rerunning the old revision would not validate that bound.

### Final exact-head installed qualification, November 7

The remaining x86 installed-handoff failure was an application utility-startup
race rather than another migration-data defect. Utility commands were resolved
inside `ConfigureContainer`, before the host had finished populating framework
services. Repeated rollback therefore exposed incomplete service graphs. The
utility path at `b5c25742` now populates a direct service collection and DryIoc
container before resolving the utility router. The failed health probe also
waits for HTTP readiness before opening the first-start migrated database.

The corrected local native archive passed the complete No, headless Manual,
interactive Yes, health, source-backup, forced-rollback and predecessor-restart
sequence, followed by ten consecutive rollback repetitions. Public exact-head
Build run
[42308704533](https://github.com/snapetech-org/chaptarrng/actions/runs/42308704533)
and standalone run
[42308701409](https://github.com/snapetech-org/chaptarrng/actions/runs/42308701409)
both passed with no failed jobs. The hosted x86 archive SHA-256 is
`822962e3e412186e687f44aa91e63da542a01fee4bfe512cfa7761cc27c1207d`.
Its structured result records revision
`b5c257429dad31bc16d5324dd7af95fe0d12be2e`, 32-bit ELF execution, all four
installed cases and success. That result is retained at
`/var/tmp/chaptarrng-native-standalone-20261106/container-evidence/x86-installed-b5c25742-ci/result.json`;
raw diagnostic copies were not retained.

Application and migration qualification is complete. Publication remains an
explicit decision: `v0.9.978` is not tagged and both repository readiness
variables are unset. ChaptarrNG publication does not by itself authorize legacy
bridge/feed publication, production migration or BookshelfNG retirement.
