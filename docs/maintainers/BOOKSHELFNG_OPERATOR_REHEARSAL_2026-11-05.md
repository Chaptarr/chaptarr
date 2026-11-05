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
