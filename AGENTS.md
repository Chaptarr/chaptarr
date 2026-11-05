## Runner policy — owner directive, 2026-11-03

Public GitHub-hosted runner minutes are available again. Compatible
GitHub-hosted runners are authorized for trusted build, test, scan, packaging,
and release jobs; private ephemeral JIT remains available but is no longer
mandatory. Choose the runner OS and architecture to match each job. Use an
explicit route when a workflow offers both pools; do not add a silent fallback.

Keep untrusted pull-request code on an isolated disposable VM-backed runner
with no secrets, minimal read access, and restricted egress. Do not widen
trusted runner-group access to admit untrusted code. Preserve active builds and
other agents' work. Do not restart the broker while it owns running guests.

Runner choice does not satisfy release readiness. Keep publication, migration,
and cutover behind their existing fail-closed acceptance gates. Treat exact-head
validation, installed acceptance, operator data, and production readiness as
separate evidence.

## CodeQL — owner directive, 2026-10-30

CodeQL is disabled across snapetech-org repositories. Do not add, enable,
invoke, restore, or reroute CodeQL workflows unless the owner explicitly
reverses this directive. Other build, test, scan, packaging, and release work
follows the runner policy above.

# ChaptarrNG build notes

## Six Labors license — check these paths first

Before diagnosing, retrying, or asking Keith about a ChaptarrNG build that needs the Six Labors license, check these confirmed local paths first:

- The license key is recorded in the gitignored repository-root file `.env.sixlabors`. Check that the file exists without printing or copying its contents.
- The license file exists at `/home/keith/Documents/code/chaptarrng/sixlabors.lic` and `/home/keith/Downloads/sixlabors.lic`.
- For local builds, pass `-p:SixLaborsLicenseFile=/home/keith/Documents/code/chaptarrng/sixlabors.lic`; use the Downloads copy if the repository copy is unavailable.

Do not print or expose the key, add `.env.sixlabors` or either `.lic` file to Git, or ask Keith to locate these files unless both license-file paths have been checked and are actually unavailable.

## Extra-platform local builds

Use an explicit task-owned `DOTNET_ROOT` for Linux x86 or FreeBSD archive
builds, with the pinned SDK already installed. Verify the selected root before
writing. Target runtimes must come from the local NuGet packs; never unpack
foreign runtime or hostfxr binaries into a shared build SDK. Preserve existing
SDKs, native runtimes and shared caches.

## Standalone installed acceptance

Archive verification does not prove installed migration or recovery. Exercise the
retained payload with the native OS/architecture, source health, migration,
target health and forced-failure rollback. Record exact archive hashes and any
source fixture prerequisites. Reuse archive artifacts only when application,
build and bundled launcher/supervisor inputs are unchanged.

Keep supervisor signals compatible with the runtime `/bin/sh`; Debian/Ubuntu
`dash` rejects `kill -TERM -- -PGID`. Run
`packaging/scripts/test-desktop-supervisor-signals.py` under the actual shell,
including its forced-KILL case. Bash syntax checks alone do not verify recovery.

## Linux x86 runtime qualification

Keep the guarded Unix delegate return-buffer source correction and native runtime
qualification together. A runtime probe or archive build alone does not prove
installed startup and rollback. Record kernel, archive hash, source auxiliary
fixture prerequisites and actual executable architecture. The harness manifest's
`processPointerBits` describes Python, so use ELF/application architecture for the
target. Preserve failing desktop-host evidence alongside passing VM/CI results;
do not infer that rebuilding a package fixes an environment discrepancy.

## Localization compatibility data

`src/NzbDrone.Core/Localization/BookshelfCompatibility/` contains immutable
BookshelfNG `.76` response catalogs and their source-byte manifest. Keep native
translation generation/rebranding scoped to `Localization/Core`; do not rewrite
the predecessor catalogs. Their `-text` Git attribute preserves the recorded
hashes on Windows. Native UI/bootstrap/login requests must retain the explicit
`X-ChaptarrNG-Client: native` hint when reading localization.

## Metadata endpoint handoff

Capture source-container metadata defaults before removing the old container.
The host helper's `--source-container` validates its `/config` mount and writes
a private metadata environment file; preview/apply accepts `--metadata-env-file`.
Treat that file as secret because a custom URL can contain credentials. Never
print its values or commit it. Preserve precedence: saved setting, current
environment, captured migrated fallback. Fresh installations keep native defaults.

Native lookup alone does not establish native refresh routing. Exercise the
single-author refresh and scheduled bulk diff paths with V5 unavailable, and
verify both ebook and audiobook snapshots reach the normal profile/edition
filters. A completed command can contain provider errors; inspect sync outcomes
and resulting metadata rather than treating command completion as success.

## Seerr request acceptance

An adapter request or an available status alone does not prove full SeerrNG
fulfillment. Exercise user submission, approval, acquisition, library scan and
restart through the server HTTP API. Audit persisted media/request/requester
identities, duplicate media counts and orphaned identifiers after consumers exit.
Record exact client/candidate revisions and derived test-image provenance.
Exercise unchanged source profile IDs and new author/book additions as well as
existing-book monitoring; verify native and compatibility profile readback.

## Bookshelf release decisions

`BookshelfMatchCompatibility` preserves source parsing and identification as well
as quality ordering. Do not apply the native title scorer after source-compatible
identification. Validate nonempty provider responses and accepted/rejected results;
empty search arrays cannot establish release parity. Legacy wire checks include
empty torrent hashes, quality fields and optional grab request identities.
Keep captured provider responses and query metadata private. For read-only replay,
disable clients/RSS/notifications, record any reset of transient backoff state,
and restore task-owned clone databases afterward.

## Bookshelf rename numbering

Migrated rename previews and commands retain source `Part` values and count all
files belonging to the source edition, including files not selected for rename.
Media-specific edition copies must be grouped through the legacy identity map;
keep unmapped target IDs in a separate namespace. Native installs retain their
normal part assignment. Preserve the source choices for disabled renaming,
patterns without a part disambiguator and absent release groups. Test filenames
and bytes after actual commands and
restart; command completion alone is insufficient.

## SQLite-heavy local validation

Some Core fixtures create databases under the test output directory and execute
many durable schema statements. Use a task-owned SSD checkout/output for the full
suite; preserve shared package caches. Record an interrupted storage-bound run
and stop its consumers before moving or cleaning its outputs. Do not relax test
or application timeouts to hide slow scratch storage.

## Full-library compatibility acceptance

Synthetic API samples do not establish response parity. Compare every managed
source author, work and file after a fresh migration; check duplicate, missing
and extra IDs separately from field comparisons. Preserve raw predecessor
metadata at import boundaries, and keep native display transformations scoped
to native responses. Save real-data response bodies and configuration privately;
commit only aggregate results and synthetic regressions.
