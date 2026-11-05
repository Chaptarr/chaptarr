# Integration smoke tests

## BookshelfNG API differential smoke

The bookshelfng-api-differential Compose file starts the digest-pinned public
BookshelfNG 0.4.21.76 image beside the ChaptarrNG image built from the current
checkout. It seeds one expanded schema-158 fixture, replaces placeholder
provider IDs with valid numeric Hardcover and typed Open Library identities,
normalizes the fixture generator's current-time migration timestamps, then
starts BookshelfNG and waits for `/ping`. The harness snapshots the running
source database with SQLite's online backup API, copies its generated
`config.xml` and synthetic library files, and adds a valid schema-158
BookshelfNG migration profile to the target. Runtime-seeded defaults and saved
source settings therefore participate in the comparison. This makes ChaptarrNG
serve the same compatibility mode used by an installed migrated database. It
compares legacy API lists and detail routes, then exercises focused monitoring
and deletion writes. The script records
the source image digest and image ID,
target commit and image ID, source database hash, per-case response hashes, and
JSON mismatch paths. Legacy fields, saved values, selector values, and collection
identity must remain equivalent; ChaptarrNG-only fields and records are reported
as additive. Objects with IDs are matched by ID and provider fields by name.
Provider schema entries are matched by stable implementation name, with missing
implementations kept visible and schema ordering reported as presentation
evidence instead of comparing shifted entries by array position.
Provider labels, help, hints, links, control types, and display order are counted
and reported separately because ChaptarrNG uses its own provider forms. The
BookshelfNG compatibility API projects DirectDownload URLs back to the legacy
ordered array shape; array order and contents remain strict while the native
ChaptarrNG UI keeps its newline-delimited text control. Source collection order
is still checked. Secret-valued response fields are compared as empty or
present and are never written into the report.

Run it with an empty task-owned directory. The generated database, config,
library placeholders, and sanitized report stay under that directory. The
comparator runs in a pinned Node container on the same internal network, so
the app ports are not exposed and the services have no outbound network path.
The script removes only its uniquely named containers and network when it exits.

    export BOOKSHELFNG_DIFFERENTIAL_WORK_DIR=/path/to/task-scratch/bookshelfng-api-differential
    export BOOKSHELFNG_SOURCE_IMAGE=docker.io/snapetech/bookshelfng@sha256:40a53ac528b6789ca78686b4303bb6c436cd191cd813f915ea171d72b47a8c38
    export CHAPTARRNG_TARGET_COMMIT="$(git rev-parse HEAD)"
    bash integration/run-bookshelfng-api-differential.sh

The workflow dispatch defaults to a GitHub-hosted Ubuntu runner and retains an
explicit private JIT option. The read comparison covers authors, books, series,
files, history, roots, profiles, indexers, download clients, and notifications,
including author/book detail and book-file lookups. The isolated read-only
sweep also covers blocklist and paged books/history, settings and provider
schemas, import lists, wanted and queue state, release profiles, tags, system
status, and route inventory. The source BookshelfNG `v0.4.21.76` OpenAPI
document declares 236 method/path pairs; the target OpenAPI document declares
334 and contains every source pair, with 98 target-only pairs. This is route
declaration evidence only. The write cases still cover only a small part of
the mutation surface. The suite remains synthetic and partial; it does not
establish all mutation or command semantics, provider fulfillment,
representative operator data, media decoding, rollback, or installed-channel
acceptance.

The Node contract comparator is mounted read-only beside the runner. Its
focused checks are in `test-bookshelfng-api-contract.mjs`; the workflow runs
them before building the disposable services. Review both mismatches and
additive differences in the report. Additive API output still needs an
old-client compatibility review even when it does not fail the source-contract
comparison.

The differential also sends two legacy writes to both isolated copies: it
disables monitoring for a seeded work and deletes a mixed-format work without
deleting files. It compares the write responses and readback state, including
that one legacy delete removes both ChaptarrNG format rows. This is a focused
mutation smoke, not coverage of every BookshelfNG API operation or command.

For local triage, `CHAPTARRNG_DIFFERENTIAL_SKIP_BUILD=true` may reuse an
already-built image only when `HEAD` matches the requested commit and all
application build inputs are clean. `BOOKSHELFNG_DIFFERENTIAL_DEBUG_VALUES=true`
adds bounded, sanitized scalar values to the report; it is off by default.

The SeerrNG smoke stack builds the ChaptarrNG candidate, seeds a disposable
BookshelfNG migration-158 SQLite fixture, and starts ChaptarrNG on an isolated
Compose network. The app listens on port 8789 for the SeerrNG probe. The default
network is internal, so the containers can reach each other but cannot contact
public providers or other external services. Normal startup converts the
source fixture. A local metadata stub returns an empty author payload for the
synthetic author so the pending import lifecycle can run without the real
metadata service. The smoke then
checks both migrated formats through SeerrNG, capability discovery, the
restricted service key, and a real SeerrNG adapter add/poll/cancel cycle for
an unavailable synthetic author.

## Exact-head artifact-based migration-to-SeerrNG smoke (2026-10-19)

The Linux app artifact from ChaptarrNG application-code commit
`0e881d921dd55aa89e12747557ccaadbb2352a21` passed Build
[39313568727](https://github.com/snapetech-org/chaptarrng/actions/runs/39313568727)
and the checked-in populated migration/SeerrNG Compose smoke. The fixture
converted synthetic BookshelfNG records; SeerrNG `ReadarrAPI` at
`86c6908e53777fd6cdab525bab56abf27176e3b4` read both formats and their
Hardcover identities. The restricted key accessed capabilities and both
libraries while admin and unrelated command routes were denied. The pending
request test created, polled, and cancelled an unavailable-author request and
observed the terminal cancellation state. The test used only disposable
records and volumes; successful provider fulfillment, history/availability
workflows, representative operator data, installed rollback, and channel
handoff remain unverified. The local test image, containers, and volumes were
removed after the run.

Set the inputs in the shell; the Compose file does not need a `.env` file:
the SeerrNG checkout must have dependencies installed and its server built
first (`pnpm install --frozen-lockfile && pnpm build:server` from that checkout).
The smoke container mounts both the source tree and its installed `node_modules`
read-only so it can run SeerrNG's integration script with the same dependencies.
It also mounts this integration directory read-only; its second script imports
the compiled SeerrNG `ReadarrAPI` adapter from `/seerrng/dist` and sends an
isolated pending-request lifecycle through ChaptarrNG's HTTP API. The ChaptarrNG
container receives named config and library volumes. A Python initializer
refuses non-empty volumes, creates a BookshelfNG schema-158 database from the
checked-in shared test fixture, creates tiny synthetic `.m4b`/`.epub`
placeholders, and sets the volume owner to UID/GID `99:100`. The placeholders
are not valid media and do not exercise M4B decoding or merging. A separate
tool-enabled importer test validates synthetic multipart M4B creation and
fallback; it does not use representative operator files.

### Expanded BookshelfNG migration fixture

The seed helper's `--expanded-fixture` option keeps the SeerrNG smoke's default
small fixture unchanged and adds a larger source for explicit migration
rehearsals. It creates four authors, six works, nine editions, four empty
placeholder files, alternate audiobook and ebook editions, a work with no
editions, an unselected mixed-format work, history, series, a `Readarr` metadata
sidecar, provider settings, and synthetic credentials. The media files are not
decodable content. ChaptarrNG may attempt startup checks for migrated provider
settings, but the internal Compose network blocks public egress during the
application and SeerrNG checks. The SeerrNG pending-import probe uses a local
metadata stub that marks only its synthetic author as unavailable.

Create empty task-scoped directories, then run:

```sh
python3 integration/prepare-bookshelfng-smoke.py \
  --expanded-fixture \
  --config /tmp/chaptarrng-migration/config \
  --library /tmp/chaptarrng-migration/library \
  --schema src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.Schema.158.sql \
  --seed src/Chaptarr.Core.Test/Datastore/Fixtures/BookshelfNG.PopulatedMigration158.sql
```

The helper refuses non-empty config or library directories. Use the operator
migration guide's explicit preview, apply, and restore commands against this
disposable config directory; do not point it at an installed appdata directory.
Detailed results from a multi-record preview/apply/rollback run are recorded in
the [migration fixture evidence](../docs/maintainers/BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).

## Real-tool M4B importer check

The Linux build workflow builds `integration/Dockerfile.m4b-test` and runs
`real_m4b_tool_should_merge_and_import_legacy_multipart_audiobook_when_installed`
against the built test assembly. The image pins `m4b-tool` 0.5.2 and MP4v2
2.1.1 and supplies FFmpeg, PHP, and the MP4v2 utilities. The test generates
two synthetic MP3 tracks and covers merge ordering, chapter titles and offsets,
metadata, registration, Move/Copy source handling, and fallback when a source
is unreadable or FFmpeg fails. It also merges two synthetic M4B inputs carrying
embedded chapters in both Move and Copy modes. Successful merges rewrite the
generated M4B title with TagLibSharp and verify that chapter titles and offsets
survive the tag write. The original six MP3 cases passed locally on 2026-10-19.
The embedded-chapter Move/Copy cases and TagLib write/reprobe check passed in
exact-head Linux Build [39288736177](https://github.com/snapetech-org/chaptarrng/actions/runs/39288736177)
on candidate commit `c4fa8125`; that run also passed the full Linux suite.
Windows build and test steps passed, while macOS was cancelled before runner
acquisition. Representative audiobooks and restart/resume remain open. The
later exact-head Build [39292223717](https://github.com/snapetech-org/chaptarrng/actions/runs/39292223717)
on application-code commit `b7e900d4` passed all three platform suites and the
Linux pinned-tool M4B smoke, confirming these synthetic checks on that code
snapshot. PR #131 merged to `main` on 2026-10-20. Its exact-head Build
[39374140961](https://github.com/snapetech-org/chaptarrng/actions/runs/39374140961)
passed Linux, macOS, Windows, PostgreSQL migration, release-note, and native
runner receipt checks. This is source and synthetic validation only; the
separate GHAS Semgrep OSS findings and remaining acceptance gates are recorded
in the [retirement status](../docs/BOOKSHELFNG_RETIREMENT.md). Representative
audiobooks and restart/resume remain open, and release publication stays
frozen.

On 2026-10-23, the pinned-tool run passed all eight cases against the current
application source on `main` (`6c3477f7`; the .NET application and test source
is unchanged from `9d2948bc`). Six cases covered Move/Copy import, merge,
unprobeable-part fallback, and simulated tool failure; two covered embedded
chapter preservation in Move and Copy modes. The test generated synthetic
audio. Representative audiobooks and interrupted restart/resume remain open.

On 2026-10-24, the pinned-tool filter passed 10/10 against the current working
tree. The conversion-job case pauses FFmpeg launched by m4b-tool, requests
application shutdown, checks that the process exits and the durable job is
requeued, then starts a new worker over the same SQLite job table and verifies
a successful retry, output promotion, and matching manifest SHA-256. The
filter now also includes a separate child-process case: it kills the process
hosting `ConversionJobService` while real FFmpeg is blocked, reopens the same
SQLite job table without a shutdown callback, and verifies startup reconciliation
retries the job and produces a valid digest-checked artifact. The media is
synthetic; this exercises the conversion service across abrupt process death,
not the complete ChaptarrNG host or an installed service. Representative
audiobooks, full-host crash recovery, and deployment acceptance remain open.

After building the test assembly as in `.github/workflows/build.yml`, run it
locally with:

```sh
docker build -f integration/Dockerfile.m4b-test \
  -t chaptarrng-m4b-test:local integration
docker run --rm --network none \
  --volume "$PWD:/workspace" \
  --workdir /workspace/_tests/net10.0 \
  chaptarrng-m4b-test:local \
  dotnet vstest Chaptarr.Core.Test.dll \
    --TestCaseFilter:"FullyQualifiedName~real_m4b_tool_should_"
```

This validates synthetic conversion, input and output chapter handling,
graceful shutdown/restart recovery, and conversion-service recovery after its
hosting process is killed. It does not close representative M4B, full-host
crash recovery, or installed-service acceptance.

```sh
export SEERRNG_SOURCE="/path/to/seerrng"
export SEERRNG_NODE_MODULES="$(realpath "$SEERRNG_SOURCE/node_modules")"
export CHAPTARR_GLOBAL_API_KEY="$(openssl rand -hex 24)"
export CHAPTARR_SEERR_API_KEY="$(openssl rand -hex 24)"

docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml \
  down -v --remove-orphans
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml up --build -d chaptarrng
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml run --rm --no-deps smoke
docker compose -p chaptarrng-seerrng-smoke \
  -f integration/seerrng-smoke.compose.yml down -v --remove-orphans
```

Use distinct test API keys. The application build requires no private
image-processing license.

This small checked-in fixture has native Hardcover IDs; its Compose service
intentionally runs ChaptarrNG with `HARDCOVER=true`. The populated-library
smoke below accepts `CHAPTARR_HARDCOVER_MODE` from the copied source
configuration instead.

The lifecycle probe uses synthetic provider IDs that are expected to be
unavailable. It verifies that the adapter receives a pending ID, can poll it,
and can cancel it; cancellation remains queryable as a terminal `Failed` state
with `LastError` set to `Cancelled by user`. The ChaptarrNG configuration and
book volumes are disposable; remove them with the `down -v` command above.

## Populated BookshelfNG library smoke

`seerrng-populated-library-smoke.compose.yml` runs the current source build
against a private copy of a populated BookshelfNG SQLite config. It performs
the normal schema conversion on startup, then uses SeerrNG's compiled
`ReadarrAPI` to read every ebook and audiobook page, check format-scoped file
availability, reconcile format statistics against each sampled file's path
extension, sample files and book history, and confirm the restricted key
cannot access host configuration, command, or global unmapped-file routes. It
uses a separate disposable admin key to verify ebook and audiobook unmapped
filters return recognized file types without assigning book relationships.
The app and smoke runner
share an internal-only Docker network; the app has no published host port or
provider/network egress. The probe makes no writes through the API. Its output
contains aggregate counts only, with no titles, identifiers, paths, or
credentials.

Stop BookshelfNG and copy its config directory, including `readarr.db` and any
SQLite journal files, into a private task-scoped location. Never mount the
installed appdata or the original database. Keep both source and converted
backups private because they can contain credentials and user library
metadata.

```sh
export CHAPTARR_POPULATED_CONFIG="/tmp/chaptarrng-populated/config"
install -d -m 700 "$CHAPTARR_POPULATED_CONFIG"
cp -a "/path/to/stopped-bookshelfng-config/." \
  "$CHAPTARR_POPULATED_CONFIG/"
chmod -R go-rwx "$CHAPTARR_POPULATED_CONFIG"

export SEERRNG_SOURCE="/path/to/seerrng"
export SEERRNG_NODE_MODULES="$(realpath "$SEERRNG_SOURCE/node_modules")"
export CHAPTARR_GLOBAL_API_KEY="$(openssl rand -hex 24)"
export CHAPTARR_SEERR_API_KEY="$(openssl rand -hex 24)"
export CHAPTARR_HARDCOVER_MODE=false # true only if the source ran native Hardcover

docker compose -p chaptarrng-populated-library-smoke \
  -f integration/seerrng-populated-library-smoke.compose.yml \
  build chaptarrng
docker compose -p chaptarrng-populated-library-smoke \
  -f integration/seerrng-populated-library-smoke.compose.yml \
  up -d chaptarrng
docker compose -p chaptarrng-populated-library-smoke \
  -f integration/seerrng-populated-library-smoke.compose.yml \
  run --rm --no-deps smoke
docker compose -p chaptarrng-populated-library-smoke \
  -f integration/seerrng-populated-library-smoke.compose.yml \
  down --remove-orphans
```

After all containers stop, remove only the task-owned config copy and image:

```sh
docker image rm chaptarrng-populated-library-smoke:local
```

Do not retain or publish app logs without reviewing them for source metadata.
The smoke validates migrated database/API behavior; it does not prove that
media files are present or playable, that providers successfully fulfill
requests, or that an installed update channel can roll back.

During conversion, `IsEbook=false` is not sufficient to classify a BookshelfNG
edition as an audiobook because it also covers print editions. ChaptarrNG uses
explicit ebook, audiobook, narrator, and print-format metadata for editions,
and routes linked files by recognized path extension. A physical edition stays
physical in the ebook catalog; when edition metadata and linked files identify
different formats, or its files span both formats, conversion creates a second
media-specific target edition without duplicating its provider identity. Each
file, sidecar, and file-specific history event follows the matching target
edition. If neither edition metadata nor a linked file identifies the format,
the default conversion retains the ambiguous edition in both catalogs and
records the legacy provider identity on the audiobook row. The private media
map can narrow that edition to one format or keep both unmonitored.

For files whose source `EditionId` is zero or points to a missing edition, a
recognized path extension sets only `BookFiles.MediaType`; the row remains
unlinked from any book or edition. Sidecars and history that already identify
the source book use that inferred file format to select the corresponding
split book row. Unknown extensions stay unclassified.

If an existing media map contains a choice for a source book that is now
unambiguous, conversion keeps the map file but ignores that stale choice. A map
entry for a book ID absent from the current source still stops conversion.

Set `CHAPTARR_HARDCOVER_MODE` from the source deployment, not by guessing from
numeric IDs. BookshelfNG's default is Goodreads when `HARDCOVER` is unset;
native Hardcover mode must be explicitly enabled in the source deployment.

## Populated migration source-build run (2026-10-19)

ChaptarrNG commit `12f290bde5c04d29202775c0e9eeafb5fc20608f` was built through
the checked-in `Dockerfile.build`, and SeerrNG was at commit
`fdc439aa9` (the migrated-record assertion added by that commit). The shared
fixture created one author, one mixed-format book, two editions, and synthetic
library files. ChaptarrNG started normally and converted the source from schema
158 to schema 121, retaining the source backup. With `HARDCOVER=true`, its
persisted config contained `HardcoverEnabled=true`.

SeerrNG's real `ReadarrAPI` read both the audiobook and ebook through their
format-specific facades and checked their Hardcover book identities. The run
also verified restricted-key access, denied administrative and unrelated
command routes, and completed the pending add/poll/cancel lifecycle. ChaptarrNG
reported `0.9.978.0`; the importer suite passed 43/43, Compose config
validation, `node --check`, and Prettier checks passed. The `.m4b` and `.epub`
files are placeholders, not playable samples. The separate six-case M4B test
validates synthetic processing and fallback only. This is synthetic
source-build evidence; it does not establish operator-data acceptance,
representative M4B compatibility, pre-14 schemas, PostgreSQL operator data, or
installed channel/rollback acceptance. No release or bridge was published.

## Exact-head artifact migration and SeerrNG smoke (2026-10-20)

Build [39394240337](https://github.com/snapetech-org/chaptarrng/actions/runs/39394240337)
passed Linux, macOS, Windows, PostgreSQL migrations, and native-runner receipt
reconciliation. The Windows job passed the migration installer upgrade and
rollback smoke. Its Linux artifact ran from application snapshot
`29852b1334ee049e30de84b44806932a3e9364f4`. The checked-in schema-158
BookshelfNG fixture converted on normal startup to schema 121, and the retained
schema-158 backup and converted database both passed SQLite integrity checks.

SeerrNG `ce632a6e6834fe094eb24ceda9b8a388472cc23e` read both migrated formats
through `ReadarrAPI`, checked their Hardcover identities, exercised the
restricted/global API-key boundary, and ran the synthetic pending-import
create/poll/cancel lifecycle. This does not test real media, successful
provider fulfillment, operator records, PostgreSQL operator data, or installed
rollback. The `.m4b` and `.epub` fixture files are placeholders. No release or
bridge was published.

## Exact-head source-build migration and SeerrNG smoke (2026-10-23)

The checked-in Compose smoke built ChaptarrNG directly from commit
`70167aec1578cf7e5c9871f2b34e78317bb6af63` and ran against SeerrNG
`bc580332ab49a7efa87e3d78b680a2a0cf1abbd0`. The schema-158 BookshelfNG
fixture converted during normal startup. SeerrNG's `ReadarrAPI` verified the
legacy book identity mapping and format-scoped reads. A legacy `BookSearch`
request using source book ID `30` expanded internally to both migrated IDs
(`30` and `31`); the BookshelfNG-compatible response projected them back to
source ID `30`, while a native-client inspection exposed both target IDs. The
adapter then read the audiobook and ebook through their format-specific
facades, observed each migrated file, and confirmed the ebook history row did
not leak into audiobook history. The restricted service-key boundary passed,
and SeerrNG created, polled, and cancelled a synthetic pending import through
the adapter. The per-book statistics reported the source-compatible ebook and
audiobook file counts and sizes while retaining aggregate values. ChaptarrNG
reported `0.9.978.0`.

The Docker build and both smoke scripts completed successfully; the frontend
build reported the existing 22 CSS-order warnings, and container publishing
reported missing Git metadata for SourceLink. The test used synthetic records
and placeholder media, not operator data or live providers. It does not cover
successful acquisition, playable M4B behavior, representative PostgreSQL
data, installed rollback, or package-channel acceptance. The uniquely named
Compose volumes, image, and override were removed after the run. No release,
bridge, or cutover was performed.

## Current-source migration and SeerrNG smoke (2026-10-24)

The checked-in Compose smoke built the application source now committed as
ChaptarrNG `364791577626448882daf8eba6320272ce754a7f` and ran against SeerrNG
`14539294113215ef7dd00f204098e3f53d109e1b`. The schema-158 BookshelfNG
fixture converted during normal startup, and the retained source backup was
present. SeerrNG's `ReadarrAPI` read both migrated formats and their files and
history through the format-specific facades. The test confirmed that the
legacy `BookSearch` response projects the split audiobook/ebook rows back to
source ID `30`, while native command inspection returns target IDs `30` and
`31`. A legacy source ID sent through the scoped ebook facade resolved to the
ebook target ID `31`. The restricted service-key boundary and synthetic
pending-import create/poll/cancel lifecycle passed.

The Release Core suite passed with PostgreSQL 17 enabled: 4,914 passed, 14
skipped, zero failed. The focused PostgreSQL migration/backup smoke passed
15/15, including normal-startup conversion, older schema snapshots, rollback,
future-schema rejection, and a real disposable `pg_dump`/`pg_restore` probe.
The run used synthetic records and placeholder media; successful provider
acquisition, operator-data acceptance, complete installed-channel rollback,
and external update delivery remain unverified. No release or cutover was
performed.

## Earlier source-build request-lifecycle run (2026-10-19)

The prior checked-in Compose run built ChaptarrNG PR #131 head
`7cfb5d877fd2a49ea92840693ca8e57457e4dfc2` (application snapshot
`0612cb4c90b75818e5349eb868cc0638825de02a`) and SeerrNG source
`b810153c7d180a1d50388c9db6ae72195d1ab56e`. It started from an empty
disposable database and validated the checked-in `Dockerfile.build` path and
synthetic add/poll/cancel flow. The populated migration run above supersedes
that empty-library check for synthetic converted records.

## Earlier artifact-based run (2026-10-19)

Both `scripts/chaptarrng-integration-smoke.mjs` and
`integration/seerrng-request-lifecycle.mjs` passed against the isolated Linux
candidate from Build run
[39239940373](https://github.com/snapetech-org/chaptarrng/actions/runs/39239940373)
and SeerrNG source commit `b810153c`. The run verified the empty paged library,
the restricted-key boundary, and SeerrNG adapter add/poll/cancel behavior. Both
scripts also passed through the isolated Compose stack using a temporary
runtime image assembled from that prebuilt artifact with `--no-build`. That
earlier run did not validate the checked-in `Dockerfile.build` path; the later
source-build run above does.
