# SeerrNG integration

ChaptarrNG exposes its Bookshelf API to SeerrNG using the Readarr-compatible
API shape. The integration capability contract is available at
`GET /api/v1/system/capabilities`; SeerrNG uses it to select the provider-ID
dialect, format-scoped routes, paged library endpoint, edition identity rules,
pending author-import support, restricted-key support, and format-specific
availability statistics. It also advertises `bookshelfLegacyIdentityMap` so a
client can resolve BookshelfNG IDs retained by an older integration, and
`editionCatalog` for clients that show a person the known editions and
audiobook performances available for a book.

One ChaptarrNG instance can serve both ebook and audiobook entries. Configure
the SeerrNG services to use the same ChaptarrNG URL and dedicated service key,
then select the correct media type and root folder for each entry. ChaptarrNG
keeps format-specific library paths, monitoring, and profiles separate. Do not
point two ChaptarrNG instances at one SQLite database; separate BookshelfNG
databases require separate migrations because the migration tool does not
merge histories, profiles, or settings. See the
[BookshelfNG migration guide](MIGRATE_FROM_BOOKSHELFNG.md).

Book statistics preserve the Readarr-compatible aggregate fields and add
`ebookFileCount`, `audiobookFileCount`, `ebookSizeOnDisk`, and
`audiobookSizeOnDisk`. These values are derived from each file's supported
media extension, so integrations can distinguish ebook and audiobook
availability while older clients continue using `bookFileCount` and
`sizeOnDisk`.

For a broader overview of maintained behavior beyond the shared Chaptarr base,
see [ChaptarrNG fork changes and upstream replacement criteria](./FORK_FEATURES.md).
Chaptarr's current development branch has its own pending-import subsystem;
this guide focuses on the format, identity, capability, and service-key
contract SeerrNG uses.

## Edition catalog for edition cards

Clients can request `GET /api/v1/book/{id}/editions/catalog` to list the known
editions for one book. The response includes provider identity, title,
language and format, narrator names, publisher, release date, duration, cover,
monitoring state, and whether a local file is owned. `production` and
`abridged` use `unknown` unless the edition metadata gives clear evidence;
multiple narrator names alone do not imply a full-cast production. Facade
requests receive the edition ID for the selected provider dialect.

**Availability:** current ChaptarrNG source includes the edition catalog,
series narrator matrix, and wanted-edition variant capability. The merged
migration source also returns provider IDs in the selected facade dialect,
proxies remote cover URLs, and derives ownership from the media-file service. Public
ChaptarrNG `v0.9.977` predates these endpoints. PR #131 merged to `main` on
2026-10-20; release publication stays frozen until the BookshelfNG retirement
gates pass.

Merged PR #131 also adds an **Editions and Narrations** tab to the book
details page. It lists known editions with narrators, production, duration,
abridgement, language, publisher, and selected/file-owned status. Metadata
that does not state a value is shown as not provided rather than inferred.
The tab and its catalog route are source-only and are not available in public
`v0.9.977`.
Earlier integrated candidate code snapshot `8b4c6e23` incorporates current ChaptarrNG
`main`, including the Editions tab and series narrator comparison, and retains
explicit labels for unknown and not-applicable edition metadata. Local
translation, type, targeted ESLint, and Stylelint checks pass. Exact-head Build
run [39205452341](https://github.com/snapetech-org/chaptarrng/actions/runs/39205452341)
passed Linux, macOS, Windows, PostgreSQL migration, release-note, and
runner-receipt checks; CodeQL, dependency review, security scan, and NuGet
submission also passed. The UI is source-only in public `v0.9.977`. PR #131 is
merged, but operator-data, full SeerrNG workflow, rollback, and channel
acceptance remain open, so release publication stays frozen.

**Source-built compatibility recheck (2026-10-23):** ChaptarrNG checkout
`00db3253348fbfc9bd49453b923789c25b4a4763` (application-code snapshot
`a2d0af9fc31a94369bf091189b10b63f8ab1dc7e`, built version `0.9.978.0`) was
built through the checked-in `Dockerfile.build` and exercised with SeerrNG
checkout `bc580332ab49a7efa87e3d78b680a2a0cf1abbd0`. The Compose smoke converted
the populated synthetic BookshelfNG schema-158 fixture during normal startup.
SeerrNG's `ReadarrAPI` read the migrated audiobook and ebook, each format
reported its one migrated book file, and migrated history row 93 remained on
the ebook and absent from audiobook history. The restricted key was denied
administrative and unrelated commands. SeerrNG then created, polled, and
cancelled a synthetic pending import and observed its terminal state. The
unique Compose containers, network, and volumes were removed; the local build
image cache was retained.

This verifies those paths for the synthetic fixture only. It does not prove
full availability or history parity, live provider fulfillment, retry
behavior, real operator data, installed-channel acceptance, or complete SeerrNG
workflow parity.

**Prior PR #131 application-code validation (2026-10-19):** snapshot `0612cb4c90b75818e5349eb868cc0638825de02a` passed [Build 39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373): Linux, macOS, and Windows tests, PostgreSQL migration smoke, native-runner receipt reconciliation, and the release-note contract. [CodeQL 39239934583](https://github.com/snapetech-org/chaptarrng/actions/runs/39239934583), [security 39239940344](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940344), [NuGet 39239932165](https://github.com/snapetech-org/chaptarrng/actions/runs/39239932165), and [dependency review 39239940313](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940313) passed. Disposable-fork validation [39239940512](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940512) was skipped by its external-fork gate. The SeerrNG adapter and request-route unit suite at source commit `b810153c` passed 4 files / 283 tests. The built Linux artifact also passed the earlier empty-library API and service-key smoke. The populated source-build check for later code is recorded below.

**SeerrNG pending-request lifecycle probe (2026-10-19):** On the isolated Linux candidate from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), SeerrNG's real `ReadarrAPI` (`b810153c`) submitted an ebook request for a synthetic work whose author was unavailable. `addBook` received a pending ID, `getPendingAuthorImport` read it, `cancelPendingAuthorImport` cancelled it, and a second poll observed the expected terminal cancellation state: `OverallStatus=Failed` with `LastError=Cancelled by user`. The cancelled record remains queryable rather than being removed. This proves request creation, polling, and cancellation across the adapter/API boundary. It used a disposable database and synthetic IDs; provider-success behavior, retries, availability, history, migrated records, production settings, full SeerrNG operator workflow, and operator acceptance remain open. The tested app artifact is code snapshot `0612cb4c`; PR commit `d9713604` added the repeatable helper and docs, and the intervening app change is Calibre selector tie-breaking rather than a SeerrNG API change. The probe is now repeatable through `integration/seerrng-request-lifecycle.mjs` and is wired after the authorization smoke in `integration/seerrng-smoke.compose.yml`. On 2026-10-19, both scripts passed against the isolated artifact from Build 39239940373 and SeerrNG source `b810153c`; `node --check`, Prettier, and Compose configuration validation also passed. The earlier Compose invocation used a temporary runtime image with `--no-build`. A follow-up source-build run on 2026-10-19 built the candidate through the checked-in `Dockerfile.build` from PR head `7cfb5d87` and passed both scripts after the harness prepared its disposable `/books` volume as UID/GID `99:100`. Successful fulfillment, retries, availability/history, converted records, saved operator settings, and acceptance remain open.

## Configure a dedicated service key

ChaptarrNG `0.9.941` and later can use a dedicated credential for SeerrNG.
Configure `CHAPTARR__AUTH__SEERRAPIKEY` in the ChaptarrNG container or secret
manager, then use its value as the API key in SeerrNG under **Settings →
Services → Bookshelf**. Keep the regular ChaptarrNG API key separate; it keeps
its full administrative access.

`CHAPTARR__AUTH__SEERRAPIKEY` takes precedence. If it is unset, ChaptarrNG
also accepts the legacy `BOOKSHELF_SEERRNG_API_KEY`, so existing container
environment settings can survive a drop-in migration. Remove the old variable
to revoke that fallback; set the new value explicitly to rotate the key.

The SeerrNG credential can inspect book libraries, profiles, queue and history,
root folders, book files, edition catalogs, audiobook narrator comparisons,
the bounded BookshelfNG legacy identity map, and command status; look up books
and authors; add books; update an individual book; run a single book search;
set a series narrator without overriding existing book values; and read or
cancel pending author imports. Legacy identity queries are limited to the four
supported entity types and 500 positive source IDs per request.
It can access the native `/api/v1` routes and the advertised
`/readarr/{dialect}/{mediaType}/api/v1` compatibility routes. Media covers are
available so the SeerrNG UI can display book artwork.

The narrator matrix requires the single `mediaType=audiobook` query value. The
series narrator preference update also requires that media type and accepts
only `narrator`, `applyToAllBooks=true`, and `overrideExisting=false`. This
keeps the dedicated service credential from changing ebook-scoped narrator
state or overwriting an existing preference. SeerrNG's format-scoped Chaptarr
adapter supplies the audiobook query value for these requests.

The credential cannot read or change global settings or API-key endpoints,
delete books or authors, delete files, run other commands, or use author
media-move operations. This includes `/config/host`, `/config/hardcover`,
`/config/development`, and `/system/health`. Book-file and history reads must
include one positive `bookId`. Those routes remain available only through the
regular API key or an administrator session.
SeerrNG's standard request, search, status, and pending-import workflows use
the restricted route set.

Both credentials can use the `X-Api-Key` or `Authorization: Bearer` header.
For Readarr-compatible clients, `?apikey=` remains enabled by default. Set
`CHAPTARR__AUTH__ALLOWAPIKEYQUERY=false` to reject it; if that setting is
unset, `BOOKSHELF_ALLOW_API_KEY_QUERY=false` remains honored during migration.
SignalR's `access_token` query parameter is unaffected. Prefer a header over a
URL when possible; see [HTTP security settings](HTTP_SECURITY.md).

For older ChaptarrNG releases, use the regular API key. The separate service
key is an additive option and does not change existing authentication.

## Verify a local integration build

The SeerrNG repository includes `scripts/chaptarrng-integration-smoke.mjs`.
Build its server first with `pnpm build:server`, then run the integration
Compose file from the ChaptarrNG repository. It uses named disposable volumes;
the fixture seeds a BookshelfNG migration-158 SQLite database before
ChaptarrNG starts, so normal startup performs the conversion. Set the absolute
SeerrNG checkout and `node_modules` paths, then use distinct random values for
`CHAPTARR_GLOBAL_API_KEY` and `CHAPTARR_SEERR_API_KEY`:

```sh
export SEERRNG_SOURCE=/path/to/seerrng
export SEERRNG_NODE_MODULES=/path/to/seerrng/node_modules
export CHAPTARR_GLOBAL_API_KEY='use-a-separate-long-random-key'
export CHAPTARR_SEERR_API_KEY='use-another-long-random-key'

docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml down -v
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml up --build -d chaptarrng
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml run --rm --no-deps smoke
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml down -v
```

The fixture refuses non-empty config and library volumes. It contains one
author, one mixed-format book, audiobook and ebook editions, and tiny synthetic
`.m4b`/`.epub` placeholder files; they are not valid media and do not exercise
M4B decoding or merge behavior. A separate real-tool importer check passed six
synthetic multipart M4B cases; representative M4B media and restart/resume remain unverified; synthetic embedded-chapter preservation is covered on c4fa. The smoke checks that SeerrNG reads both
converted formats and their Hardcover identities, confirms status and
capability discovery, reads the split audiobook/ebook legacy-ID mappings with
the restricted service key, resolves legacy BookshelfNG IDs for individual
format-scoped book and book-file lookups, rejects that key for administrative
and unrelated command routes, and runs the compiled SeerrNG adapter through
an ebook add, pending-import poll, and cancellation. `SEERRNG_NODE_MODULES` must
point to the installed dependency directory so the separate smoke container
can resolve SeerrNG's runtime packages. Use `down -v` afterward to delete the
named test volumes; do not point this harness at operator appdata.

## Recorded smoke evidence

**Exact-head populated migration and lifecycle smoke (2026-10-24):** ChaptarrNG
commit `9bf2cf654ce01dfb25d41a59525ce8e4dca39a29` built through the checked-in
`Dockerfile.build` and passed both Compose smoke scripts against SeerrNG source
commit `f7769b65baa0b0d37ca9c5c37ecd860cde0f06a6`. Normal startup converted the
disposable populated BookshelfNG migration-158 fixture. SeerrNG's compiled
`ReadarrAPI` read the migrated ebook and audiobook libraries, format-specific
file counts, and history with the restricted service key; administrative and
unrelated command access was denied. The adapter created, polled, and cancelled
a synthetic pending import and observed its terminal cancellation state. The
fixture, media placeholders, and keys were synthetic. This does not establish
live provider fulfillment, retries, full availability and history parity,
production settings, real operator records, representative media, or full
SeerrNG operator acceptance. The unique Compose containers, network, volumes,
task image, temporary credentials, and isolated SeerrNG source worktree were
removed; Docker's shared build cache was retained.

**Current source-built populated migration, legacy-ID, and lifecycle smoke
(2026-10-22):** ChaptarrNG commit
`e5788975b0071aae0d1990460461b4321dde0e5a` built through the checked-in
`Dockerfile.build` and passed both Compose smoke scripts against SeerrNG
source commit `d0072cee5176554601108209296038f855ac740c`. Normal startup
converted the disposable populated BookshelfNG migration-158 fixture.
SeerrNG's compiled `ReadarrAPI` resolved legacy book ID 30 to audiobook ID 30
and ebook ID 31, then read their corresponding file IDs 50 and 51. Capability
discovery and restricted service-key access passed; `/config/host` and
unrelated `/command` access were denied to that key, while the global key
remained authorized. The adapter created a synthetic pending author import,
polled it, cancelled it, and read the terminal cancellation state. The Release
Core suite passed 4,663 tests with 25 skips and no failures. Records, keys,
profiles, and media placeholders were synthetic. This does not establish live
provider fulfillment, retries, availability or history parity, production
settings, real operator records, or full SeerrNG operator acceptance.

**Exact-head artifact-based populated migration and lifecycle smoke
(2026-10-19):** The Linux application artifact from ChaptarrNG PR #131 code
head `969fb20bdc21b876d71d844f40606b27e20dc8ec`, Build
[39353846035](https://github.com/snapetech-org/chaptarrng/actions/runs/39353846035),
passed both checked-in SeerrNG smoke scripts against SeerrNG source
`a2ae3cb234fb7b694e19e380e398b7dec141c4d2`. The temporary runtime image
started ChaptarrNG `0.9.978.0` with a disposable populated BookshelfNG
migration-158 database; normal startup converted it, and SeerrNG read one
audiobook and one ebook with their migrated Hardcover identities. The
restricted service key read capabilities and the converted format libraries
while administrative and unrelated command routes remained denied. SeerrNG's
compiled `ReadarrAPI` then created, polled, and cancelled a synthetic pending
author import and read its terminal cancellation state. The smoke used
synthetic records and keys, not operator data or live providers. The disposable
containers, network, volumes, image, and downloaded artifact were removed.
Successful provider fulfillment, retries, availability and history, real
operator records, representative M4B media, installer rollback, and channel
handoff remain unvalidated.

On 2026-10-19, `scripts/chaptarrng-integration-smoke.mjs` passed against the Linux artifact from Build run [39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373), reporting ChaptarrNG `0.9.978.0`. It discovered the SeerrNG capability contract and read the empty paged library using the restricted key; `/config/host` and an unrelated `/command` were denied, and the global key was accepted by `/config/host`. The smoke used a disposable SQLite database and synthetic keys. It establishes the empty-library API and authorization contract only. The separate lifecycle probe below covers one unavailable-author add/poll/cancel path; availability statistics, history, successful provider lookup, converted records, unchanged production settings, and a real BookshelfNG library remain unvalidated.

**Current source-built populated conversion smoke (2026-10-19):** ChaptarrNG
candidate commit `12f290bd` built through the checked-in `Dockerfile.build`
and started with a disposable BookshelfNG migration-158 SQLite fixture. The
fixture was seeded in named volumes, and normal startup converted it to schema
121 while retaining the schema-158 source backup. With `HARDCOVER=true`, the
candidate persisted `HardcoverEnabled=true`. SeerrNG's real `ReadarrAPI` at
commit `fdc439aa9` read both the migrated audiobook and ebook and verified
their provider identities. The restricted service key boundary and the
pending add/poll/cancel lifecycle also passed. The audiobook and ebook files
were synthetic placeholders, not valid media. Importer tests passed 43/43;
Compose configuration, `node --check`, and Prettier checks passed. This local
source-build smoke closes the earlier empty-library-only check for this
synthetic conversion path. It does not verify operator records, older source
schemas, PostgreSQL operator data, live provider behavior, representative chaptered M4B media, successful request fulfillment, installed rollback,
or deployment channels.
No release or bridge was published.

On 2026-10-18, `scripts/chaptarrng-integration-smoke.mjs` passed against
ChaptarrNG candidate `0.9.978.0` from PR #131 source head
`1a0a1cf78b404fe86d7b266facd8e843e435d9b4` (Build run
[39169786430](https://github.com/snapetech-org/chaptarrng/actions/runs/39169786430))
and SeerrNG source commit `0b267d526`. The candidate started with a populated
synthetic migration-158 fixture created by the published BookshelfNG `.71`
binary. The smoke verified status and capability discovery, one audiobook
through SeerrNG's format-scoped paged-library adapter, denial of administrative
and unrelated command routes using the restricted key, and administrator
access using the global key. The raw ChaptarrNG API exposed one author and
separate audiobook and ebook records with their migrated provider IDs.

This verifies the populated read path and key boundary. It does not exercise
end-to-end SeerrNG request creation, availability statistics, history,
pending-import retry/cancel operations, unchanged production settings, or a
real BookshelfNG library.

On 2026-10-18, the same SeerrNG harness also passed against the Linux artifact
from exact PR #131 head
[`0434148898576e1b220392163f18096682385400`](https://github.com/snapetech-org/chaptarrng/commit/0434148898576e1b220392163f18096682385400),
Actions run [39176823002](https://github.com/snapetech-org/chaptarrng/actions/runs/39176823002),
with SeerrNG source commit `0b267d5261524a2b0c233f59da8327180045c39e`. The
ChaptarrNG artifact reported version `0.9.978.0`. The run used a disposable,
empty SQLite database and synthetic SeerrNG runtime configuration. SeerrNG's
adapter read status, discovered the capability contract, and received the
empty paged book library. The restricted key was denied `/config/host` and
unrelated `/command`; the global key was accepted by `/config/host`.

That earlier-head smoke validates API and key behavior with an empty library;
it does not replace the earlier populated-fixture smoke above. Neither run
exercises end-to-end SeerrNG request creation, availability statistics,
history, pending-import retry/cancel operations, unchanged production
settings, or a real BookshelfNG library. The earlier populated fixture used
candidate head `1a0a1cf78b404fe86d7b266facd8e843e435d9b4`; the current-head
smoke does not establish converted-data acceptance.

On 2026-10-18, the local Compose smoke passed against the earlier PR #131
source head `ddec3e8104f02fe15e1885c617cdb621e2f71fc5`, whose application-code
snapshot is `2c0401fc`, and SeerrNG source commit `10f06eab9`. It built the
candidate from source and used a disposable config volume. SeerrNG discovered
the capability contract and read the empty paged library with its restricted
key; administrative and unrelated command routes were denied. This confirms
the earlier source candidate's API and key boundary. The empty-library run
does not establish populated migration acceptance or end-to-end SeerrNG
request, history, availability, or pending-import behavior.

## Narration and edition choices

These read endpoints and the existing wanted-edition endpoint let SeerrNG show
narration choices before a request. A client should read `features` from
`GET /api/v1/system/capabilities` first and hide a control when its flag is
absent, so older ChaptarrNG versions keep working.

| Capability flag | Endpoint | Purpose |
| --- | --- | --- |
| `editionCatalog` | `GET /api/v1/book/{id}/editions/catalog` | Every known edition with narrators, production, abridgement, length, language, publisher, release date, cover, whether it is the monitored selection, and whether a file exists |
| `seriesNarratorMatrix` | `GET /api/v1/series/narrators/matrix?title=…&authorId=…&mediaType=audiobook` | For each narrator: the volumes they cover, the volumes missing, and the volumes already owned; for each volume: the narrators known across all editions |
| `wantedEditionVariants` | `POST /api/v1/book/{id}/editions/wanted` with `asNewVariant: true` | Track the chosen narration alongside the current selection instead of replacing it |

Fields the metadata does not state are reported as `unknown` (`production`,
`abridged`) or left empty; they are never inferred from a file name. A narrator
is `full-cast` when the edition is flagged GraphicAudio or its own text says
full cast or dramatized, `solo` for exactly one narrator, and otherwise
`unknown`.

`PUT /api/v1/series/{seriesId}/narrators/preferred?mediaType=audiobook`
(already available) makes one narrator the series preference; SeerrNG uses it
for **Collect With**. The dedicated service key allows only the request that
leaves existing book narrator values unchanged.
