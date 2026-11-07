# BookshelfNG replacement: reassessment and completion order

Assessment date: **2026-10-24**. Requested outcome: **100% behavioral drop-in
replacement, including automatic backend migration for existing installations**.

**The source-review and code pass is complete for the listed C4–C6 behavior
families, but ChaptarrNG is not yet an accepted 1:1 drop-in.** The unchanged
client facade, one-work add behavior, provider dialect persistence, and default
Linux/Windows/desktop recovery paths now have code implementations. Confidence:
**high** for the source findings and reviewed code paths; **unknown** for
unexecuted provider, representative-data, and installed-channel outcomes. No
defensible overall completion percentage follows from file counts, test counts,
or route counts.

## Execution update (2026-10-25)

The exact-head hosted Build for `ebae13a7023892e84dfe2de8d96955fae684b7e6`
failed its Windows BookshelfNG service-upgrade/rollback test when the documented
read-only migration preview could not resolve `IMigrationController` in utility
mode. A local candidate fix registers NLog framework logging there and defers
`IConfigService` resolution until the health-check command, preventing utility
composition from opening the source database through normal startup migration.
The Release Console build passed, the focused composition regression passed,
and preview/apply/health/restore-preview/rollback passed on disposable expanded
schema-158 fixtures for explicit Goodreads false, default Goodreads unset, and
native Hardcover modes. The full Core suite passed 4,901 tests with 28 skipped;
the PostgreSQL 17 migration/backup smoke passed 15/15. These results are
recorded in the [migration fixture evidence](BOOKSHELFNG_MIGRATION_FIXTURE_EVIDENCE.md).

This fixes the reproduced explicit-CLI failure, but the hosted failure was on
the previous commit and the candidate has not yet passed exact-head hosted CI.
ChaptarrNG remains **not ready for users to cut over**: representative
operator-data and live-provider acceptance, end-to-end unchanged-client
fulfillment, installed-channel upgrade/rollback, exact-head cross-platform
validation, and external update delivery remain open. No release or cutover has
been authorized.

A further local code-only pass adds marker-aware Docker/Kubernetes readiness,
platform-specific container port contracts, a StartOS startup grace period,
standalone macOS/FreeBSD first-handoff instructions, and a Cloudron liveness
proxy that keeps the platform health route available while migration runs.
These additions have not yet had the bulk validation pass and do not close an
installed-channel or release gate.

The source compatibility profile is now version 2. Each converted database
records its BookshelfNG source schema, effective provider dialect, preserved
API base path, audiobook/ebook split semantics, identity-map version, and
conversion outcome. The unchanged API facade and migration health command
validate the profile before interpreting legacy IDs. Earlier candidate profile
versions remain readable. This closes the profile's missing source-context
fields at code level; outcome and upgrade compatibility still require the bulk
validation campaign.

The 2026-10-25 platform source review also completed the in-repository Co-op
Cloud recipe candidate: it preserves the prior stack/service/volume identities,
backs up the retained volumes, waits through migration health checks, serializes
Swarm updates, pauses failures instead of automatically starting an old binary
against potentially converted data, and emits a digest-pinned recipe bundle
with a valid Co-op Cloud version label. The local container-platform bundle,
StartOS build, desktop candidates, legacy-channel contracts, Docker migration
paths, and release/publisher gate batch passed on October 25. These are
source/candidate checks only; external recipe delivery and installed Swarm
acceptance remain open.

This document is the current **execution order** for this goal. The
[retirement record](../BOOKSHELFNG_RETIREMENT.md),
[workflow crosswalk](../BOOKSHELFNG_WORKFLOW_PARITY.md), and BookshelfNG's
[historical disposition ledger](https://github.com/snapetech-org/bookshelfng/blob/main/docs/maintainers/chaptarr-consolidation-inventory.json)
remain evidence inputs. Their historical milestones must not be treated as
today's missing-code list. This assessment does not approve a runtime feature
retirement, a public release, or an installed migration.

## 1. Scope and source pins

| Input | Exact revision / result |
| --- | --- |
| BookshelfNG source | `d42ec2aba1e411f4fb538d4d7201f6930898a91e` |
| ChaptarrNG source | `9bf2cf654ce01dfb25d41a59525ce8e4dca39a29` |
| Older path ledger's source | `685b31c93d7146258c02446b01cb34c6f5d962b5` |
| Older path ledger's target | `4b57b479d73c844db19278233e25efe94509e99b` |
| Latest public releases, checked through GitHub | BookshelfNG `main-v0.4.21.71`; ChaptarrNG `v0.9.977` |
| Application validation performed in this reassessment | Source inspection and static inventories; no .NET build, application execution, or live migration |

The two `codex/bookshelf-parity-batch*-20261024` branches have no commits outside
the inspected ChaptarrNG `main`. They are not hidden unmerged product work.
Runner fallback [PR #159](https://github.com/snapetech-org/chaptarrng/pull/159)
is separate infrastructure work; do not count it as a missing book-management
feature or merge it indiscriminately as part of this plan.

The inspected target's GitHub Build, CodeQL, Semgrep, security, standalone-asset,
and Trivy runs were queued; NuGet submission succeeded and Release reported
failure. These are observations, not a diagnosis of why the Release job failed.
The historical local test results in the retirement record were not rerun here.

During this assessment, `main` advanced to `3c1d1d3300c49fa20bff0c1e1d33dd512e68ab89`
with documentation only. That update records the inspected `9bf2cf6` application
passing the synthetic SeerrNG migration/read/history/key-boundary and pending
create/poll/cancel smoke against SeerrNG `f7769b65`. It changes none of the
application findings below and is preserved alongside this assessment. Successful
live fulfillment remains a separate acceptance case.

**Drop-in acceptance means** the same supported user tasks, saved data,
monitoring/acquisition decisions, external-client requests, credentials, service
identity, data paths, ports, URL base, and ordinary update mechanism continue to
work. Branding and additional features can differ. A client URL rewrite,
re-entered settings, silently changed format selection, lost queue/history, or
manual database conversion is a gap for the unattended path.

There are two separate deliverables:

1. **Application parity:** all supported BookshelfNG behavior remains available
   after conversion, including on a manually updated pinned/offline install.
2. **Update delivery:** every supported installation channel has a concrete
   update transaction that delivers that application and migrates automatically
   when its ordinary updater runs.

An immutable image digest, disconnected installation, or old executable polling
a service we do not control cannot be remotely redirected by changing this
repository. Those installations require a one-time artifact/update action.
The converter can still be automatic afterward. Report this boundary explicitly;
do not claim universal zero-action migration or remove these users from the
compatibility target to improve a percentage. A feature retirement would change
the stated 100% goal and cannot close a parity item under this plan.

## 2. What the source inventory actually says

The checked-in [snapshot](bookshelfng-replacement-snapshot-2026-10-24.json)
is generated by
[`audit-bookshelf-replacement-snapshot.py`](../../scripts/audit-bookshelf-replacement-snapshot.py).
It overlays current Git trees on the existing ledger without inventing new
equivalence or retirement decisions.

| Measure | Result | Meaning |
| --- | ---: | --- |
| BookshelfNG OpenAPI method/path pairs | 236 | All 236 are present in ChaptarrNG's checked-in OpenAPI document. |
| ChaptarrNG OpenAPI method/path pairs | 334 | 98 additional pairs; route presence is not payload or behavior parity. |
| Direct `Command` subclass names | 42 source / 64 target | No source name is absent from the class-declaration scan. This is not dispatch/execution coverage. |
| Historical reviewed dispositions | 758 of 1,151 paths | Previous source-review decisions, not 758 proven runtime features. |
| Reviewed paths with unchanged source/target fingerprints | 669 | Their cited blobs still match; wider runtime behavior can nevertheless change. |
| Reviewed paths needing fingerprint revalidation | 89 | Source or referenced target changed. These are not automatically regressions. |
| Previously undispositioned paths | 393 | Mixed review work, not 393 missing features. |
| BookshelfNG paths changed since ledger source | 42 | Overlaps the other lists; do not add these counts together. |

The **393** split into **129 backend-code paths, 64 frontend paths, 66 test
paths, 66 build/install/tooling paths, 35 documentation paths, and 33 other
build/manifests**. The report includes every path so the next pass can work a
bounded queue. The earlier mappings did not complete semantic review; the
behavior families now reviewed and their source/target fingerprints are
recorded in the [compatibility code handoff](BOOKSHELFNG_COMPATIBILITY_CODE_HANDOFF.md).
This does not claim runtime equivalence.

False gap examples already checked:

- `RetryUnmappedFilesCommand.cs` is absent by filename, but the command exists
  inside `RescanFoldersCommand.cs`, with a handler forwarding to the current
  matcher and command-controller coverage. Do not rebuild it.
- The three source-only `IConfigService` property names (`HardcoverAuth`,
  `NewAuthorAudiobookRootFolderPath`, `NewAuthorEbookRootFolderPath`) have
  migration/API aliases. Their renamed internal properties are not three
  missing settings.
- The old candidate/identification service filenames were replaced by newer
  matching services. The workflow crosswalk already identifies the replacements.
- Browser state has `readarr`/`audioarr` to `chaptarr` migration, and Add New
  search preference migration exists. Same-origin behavior needs validation;
  the migration should not be commissioned again as wholly absent code.

## 3. Confirmed gaps and bounded review questions

| ID | Finding and source evidence | Classification / confidence | Required disposition |
| --- | --- | --- | --- |
| F1 | [`RequiresProviderPrefix`](../../src/Chaptarr.Http/Middleware/ReadarrFacadeProviderIdTranslator.cs) rejected unqualified IDs when facade context was absent. The migration now persists the effective source dialect and applies it at the unchanged `/api/v1` address, while native requests keep canonical-ID validation. | **Code implemented; high source confidence, runtime unverified.** | Bulk acceptance verifies bare Goodreads and Hardcover IDs at the old address, native marker behavior, and numeric collisions against migrated data. |
| F2 | [`BookController.AddBook`](../../src/Chaptarr.Api.V1/Books/BookController.cs) previously added both formats for an unscoped request and could use the pending-import response. The legacy branch now selects the single monitored edition's format and disables the pending-import protocol. | **Code implemented; high source confidence, runtime unverified.** | Bulk acceptance verifies one-work creation, selected-edition identity, unavailable metadata errors, and unchanged native multi-format behavior. |
| F3 | BookshelfNG itself selects native Hardcover IDs only when `HARDCOVER=true` and `HARDCOVER_NATIVE` is not `false`; an unset mode is Goodreads. Debian/PPA, RPM/COPR, and AUR preserve the BookshelfNG service environment file and invoke conversion under that unit environment. Chocolatey reads the old NSSM environment and passes it into preview/migration. The importer persists the effective dialect. | **Code path present, unverified; high.** The previous fail-closed assessment treated an unset mode as unknown, but that contradicts BookshelfNG's runtime predicate. | Keep the exact runtime predicate and verify env-file/NSSM propagation for both explicit modes and the unset Goodreads default. Never infer mode from numeric IDs. |
| F4 | Linux Debian/PPA, RPM/COPR and AUR hooks preserve the prior app/unit, gate readiness, and restore SQLite/PostgreSQL source data after startup failure. Windows Chocolatey has protected deferred-start recovery, captures both NSSM environments and distinguishes BookshelfNG source rollback from a failed target-to-target update. AppImage/Flatpak supervise the first launch, require `setsid --wait`, stop the full app process group, recover source data after interruption, and record channel state. | **Recovery code implemented, unverified; high source confidence.** | Bulk acceptance exercises real package managers, Windows task registration/permissions, target-to-target failure preservation, desktop process-tree shutdown, PostgreSQL access, restart reconciliation and retry. |
| F5 | [`BookshelfBookMediaMapping`](../../src/NzbDrone.Core/Datastore/BookshelfBookMediaMapping.cs) preserves source book monitoring on each inferred target format row; the C4 compatibility path now gates RSS decisions through BookshelfNG's legacy author/book monitor columns. | **Source path reviewed; code implemented, runtime unverified.** | Test monitored and unmonitored source authors, selected editions, mixed-format rows, and ambiguous mappings. Keep the source work projection and mapped row IDs scoped correctly. |
| F6 | BookshelfNG [`ReadarrCloudRequestBuilder`](https://github.com/snapetech-org/bookshelfng/blob/d42ec2aba1e411f4fb538d4d7201f6930898a91e/src/NzbDrone.Common/Cloud/ReadarrCloudRequestBuilder.cs) points at `https://readarr.servarr.com/v1/`; its update provider requests that service. | **Confirmed delivery limitation; high.** ChaptarrNG `main` now defaults new installs to checksum-verified GitHub releases, and migration persists that route. Old BookshelfNG binaries still cannot discover it. | Keep the one-time package, installer, or archive bootstrap for installed native copies; maintain the ChaptarrNG update channel afterward. Explicitly distinguish old unreachable clients from clients already receiving owned package updates. |
| F7 | Mode-specific container aliases, Helm bridge, desktop candidates, Windows migration, and platform manifests exist. The legacy Compose, Co-op Cloud, and Unraid candidates forward the Bookshelf/Audioarr/Readarr application, authentication, server/security, logging, updater, and PostgreSQL sections, as well as the provider/security environment contract; the digest-pinned container bundle includes the Compose environment sample. Blank alias placeholders are ignored so persisted config and non-empty legacy aliases retain precedence. Several external channel handoffs are still candidates; the source workflow gates intentionally prevent publication. | **Mixed integration and qualification work; high** for source presence, **unknown** for untested live channels. | Validate environment preservation on the exact packaged candidate, finish missing external channel delivery, then publish the exact accepted artifacts. Do not equate an unflipped gate with missing application code. |
| F8 | The earlier source ledger left broad C4 acquisition/matching/filesystem/catalog, C5 provider/settings/auth/webhook, and C6 UI workflows for review. The current code handoff records pinned source/target fingerprints and a disposition for each behavior family, including the webhook source-ID projection fix. | **Source-level semantic reviews are complete for the listed C4–C6 families. Runtime equivalence, saved provider behavior, and UI outcomes remain unverified.** | Bulk acceptance validates same-input decisions, realistic files/libraries, live providers, legacy webhook payloads, and migrated/native frontend flows. Record mismatches as focused code fixes against the named contract. |

F1–F4 precede broad QA because they directly affect the stated drop-in and
automatic-migration contracts. The current continuation checkpoint implements
the listed API, provider-dialect, and channel-recovery changes. Runtime
acceptance remains open and is recorded in
[the code handoff](BOOKSHELFNG_COMPATIBILITY_CODE_HANDOFF.md).

## 4. Existing implementation to keep and finish

Confidence is **high for source presence** in this table. It is not a claim
that every platform/provider has passed acceptance.

| Capability | Existing implementation / evidence | Remaining type of work |
| --- | --- | --- |
| Normal-startup conversion | Lineage detection, embedded older Bookshelf migration chain, staged SQLite conversion, integrity checks, retained source copy, atomic database replacement, preview and restore | Recovery code is present across the listed local channels; validate populated data, process interruption, restart and rollback on installed hosts. |
| PostgreSQL conversion | Database selection, staged conversion/schema promotion, restore, external backup helper and synthetic fixtures | Supported-version/privilege matrix, real data, service-account access, interruption and installed-host validation. |
| Library identity and format split | Edition/file-format inference; `BookshelfLegacyIdentityMap`; book, edition, file, sidecar and history remapping | Source-level legacy projection/default semantics have dispositions in the code handoff; validate every mutation/response family with migrated and colliding IDs. |
| Import/search decisions | Numeric threshold compatibility, weighted legacy scoring, catalog fallback tiers, download ordering, same-file guard, persisted remote-search marker | Source-level review is recorded; compare same inputs, real providers and representative files. |
| Media processing | M4B compatibility, track/chapter/tag handling, failure fallback, copy/move handling, durable jobs, SHA-256 artifact manifests | Complete-host/process-tree interruption, representative media, other shipped platforms and end-to-end import validation. |
| Provider/settings ecosystem | Supplemental catalogs; Goodreads OAuth/list/write paths; MAM wedge behavior; legacy client categories; notification/settings contracts; legacy metadata consumer | Source-level contracts and migration aliases are reviewed; validate saved credentials, live requests, webhook IDs and provider limits. |
| External clients | Capabilities, scoped facades, restricted keys, legacy identity maps, pending-import lifecycle, SeerrNG smoke and populated-library smoke | F1/F2, complete successful acquisition, retry/shared-request races, availability/history and unchanged-client validation. |
| UI workflows | Workflow crosswalk, mixed-format views, monitoring, import/recovery, bulk actions, retry/error states, preference migration | Source-level review is recorded for the primary migrated/native flows; validate old bookmarks/state, keyboard/mobile and actual packaged-app behavior. |
| Installed packages | Windows/Chocolatey migration logic; Debian/AUR/RPM identity bridges; AppImage/Flatpak; Helm; container mode aliases; StartOS candidate; explicit old server/PostgreSQL/provider setting forwarding for Compose, Co-op Cloud, and Unraid | Recovery code is present for default Linux units, Windows Chocolatey and desktop first launch. The container environment adapter changes are source-only until exact-bundle acceptance. External catalog/feed delivery and installed update/rollback acceptance remain. |

The latest committed evidence reports a Release Core run of **4,888 passed,
28 skipped**, a **10-case real-tool conversion suite**, populated library API
checks and file/history reconciliation. These are historical reports at the
inspected source, not tests run by this assessment. The abrupt crash case kills
the process hosting the conversion service; it does **not** establish crash
recovery of the entire installed ChaptarrNG host. Preserve that distinction.

## 5. Code-first execution order

Use the IDs below in commits and status updates. Finish the identified source
changes across C1–C8 before starting the bulk build/test/refinement campaign.
Validate the consolidated candidate, then fix and refine the findings in bulk
before repeating the affected validation stages.

| Order | Work package | Depends on | Current state | Exit condition |
| ---: | --- | --- | --- | --- |
| 0 | C0 — Pin baseline and separate review/code/evidence queues | — | Delivered by this assessment | Source pins, reproducible inventory and explicit gap IDs available. |
| 1 | C1 — Preserve compatibility context and old API address | C0 | Version-2 profile now stores source product/schema, provider dialect, API base path, format and identity-map semantics, and conversion outcome; legacy API and health checks consume it. Code is implemented; runtime unverified. | Migrated installations persist source semantics and accept old-address legacy requests without client reconfiguration. |
| 2 | C2 — Complete legacy resource, mutation and lifecycle behavior | C1 | Code implemented for the listed request, response, command, and identity paths; source review complete for the named F2/F5 contracts; runtime unverified. | Old request/response/identity/monitoring contracts have implemented projections, including split works and pending metadata. |
| 3 | C3 — Make migration an unattended recoverable transaction | C1/C2 | Linux package handoff and converter journals exist; Windows deferred-start and desktop recovery now write channel outcomes to the shared journal; all unverified | Every promotion boundary is journaled or reconciled; failures retain or restore the correct app/data/service state. |
| 4 | C4 — Close acquisition, import and file behavior differences | C2 | Source-level semantic review is complete for the listed release/download, matching/import/filesystem, and catalog families; source behavior and target fingerprints/dispositions are recorded in the code handoff. Runtime equivalence remains unverified. | Every in-scope semantic difference has a concrete port or supported equivalence rationale; no unknown acquisition/import behavior remains. |
| 5 | C5 — Close saved settings, provider, auth and integration differences | C1/C2 | Source-level review is complete for the listed provider contracts, saved settings/auth behavior, and webhook identity. Recognized settings are mapped, unknown meaningful settings fail closed, and private OIDC issuers remain blocked by default. External integrations and runtime behavior remain unverified. | Supported saved configurations retain their meaning and all required implementations are wired. |
| 6 | C6 — Close user workflow and preference differences | C2/C4/C5 | Source-level review is complete for the listed search/add, bookshelf, unmapped-file retry, settings, request-identity, and primary migrated/native UI flows; the workflow crosswalk and code handoff record the dispositions. Runtime form, accessibility, and mixed-format outcomes remain unverified. | Each BookshelfNG user task has a reachable implemented path with retained intent/preferences and usable failure recovery. |
| 7 | C7 — Finish native installation adapters | C3/C5 | Source candidates now cover Linux package hooks, Chocolatey, AppImage/Flatpak, Docker aliases, Helm, Unraid, Cloudron, Co-op Cloud, and StartOS. The Compose, Co-op Cloud, and Unraid candidates explicitly forward the legacy application/authentication/server-security/logging/updater/PostgreSQL settings alongside provider credentials, metadata-source keys, path limits, and other provider/security values; the release bundle carries the Compose `.env.sample` and pins its image. The StartOS artifact builder's fixture regression passed, as did the extracted candidate's `npm ci`, type check, and ncc build; these historical checks do not validate the latest container adapter changes. Real SDK signing and installed behavior remain unverified. | Every maintained package/platform feed has a recoverable handoff path and passes installed upgrade/rollback acceptance. |
| 8 | C8 — Finish channel delivery and compatibility artifacts | C3/C7 | Linux systemd has a one-time bootstrap; Windows and package bridges exist; macOS/FreeBSD standalone copies have a documented one-time archive handoff; external identity/feed updates remain outside this repository | Every supported channel has a buildable adapter and exact delivery path, with unreachable historical installs explicitly identified. |
| 9 | V0 — Assemble differential/upgrade acceptance harness | C1–C8 | Extend existing harnesses | Reproducible old/new scenarios, artifact pins and machine-readable per-contract results. |
| 10 | V1 — Build and deterministic behavior campaign | V0 | Pending at final code candidate | Unit, contract, frontend and platform builds pass; all differences triaged and fixed. |
| 11 | V2 — Populated migration and destructive-fault campaign | V1 | Partial historical evidence | Data, interruption, retry, rollback and post-restart invariants pass on supported engines/platforms. |
| 12 | V3 — Live integration and unchanged-client campaign | V1/V2 | Partial historical evidence | Successful workflows pass with unchanged integration settings and real representative media/providers. |
| 13 | V4 — Installed-channel acceptance | V2/V3 | Partial historical evidence | Each supported shipped predecessor upgrades through its real channel and can recover/roll back. |
| 14 | R1 — Publish accepted artifacts and complete retirement | V1–V4 | Not ready | Exact validated artifacts delivered, channel receipts recorded, real cutover verified and rollback retained. |

### C1 — One persisted source compatibility profile

Extend the existing importer/configuration services with a versioned profile:
source product/schema, catalog dialect, original API base/format semantics,
identity-map version, and migration outcome. Obtain it from the old running
configuration or a verified channel manifest before replacement. Environment
fallbacks already exist; they should feed this persisted profile, not be the
only record of how numeric IDs were interpreted.

Apply the legacy projection at the **existing** `/api/v1` address for migrated
legacy requests. Reuse the current provider translator and identity map. Ensure
the native UI/canonical requests remain explicitly distinguishable; moving
internal native calls to a distinct route is acceptable if needed, while asking
installed clients to move is not. Do not globally interpret every bare ID as
Hardcover. Preserve API-key scopes and URL-base/proxy behavior.

Primary code: `BookshelfDatabaseImporter`, `LegacyConfigurationCompatibility`,
`ServarrMediaTypeScopeMiddleware`, `ReadarrFacadeProviderIdTranslator`, API
resource mappers and startup middleware order. First completed slice: unchanged
bare-ID author/book lookup and add using the old key/address, followed by
resource responses that the same client can consume.

### C2 — A legacy work remains one legacy work

Trace every old operation, not just route registration: authors/books/editions,
root/profile edits, monitoring, queues/history, manual import, rename/delete,
release grabs, command start/poll/cancel, files, pagination and SignalR events.
Specify status codes, payload types, defaults, nulls/enums, error shape,
pagination/filter/sort behavior and side effects. Include commands whose class
names match but whose arguments, cancellation or result shape changed.

Extend `BookshelfLegacyIdentityMapService` and resource/action projections where
needed. Retain source handles for a split work; distinguish old handles from
native format handles, and never broaden a single-format destructive operation.
Persist the original monitoring/acquisition intent. Resolve F5 by comparing
legacy selected-edition/any-edition behavior, not by blindly choosing either
"both monitored" or "both unmonitored."

The asynchronous metadata pipeline can remain internally. Its legacy adapter
must provide a valid stable legacy resource/result or the appropriate legacy
error; it must not hand an unaware client a different resource shape and count
that as a successful drop-in add. Keep native pending-import capabilities for
clients that explicitly negotiate them. Preserve durable retry and shared
request cancellation semantics.

### C3 — Finish the migration transaction around the converter

Reuse the staged SQLite/PostgreSQL converters and backups. Add a small shared
handoff coordinator with durable states: detected, preflighted, snapshotted,
converted, promoted, healthy, rolled back, or intervention required. Record
artifact/source/config fingerprints and idempotent step outcomes. On restart,
reconcile the actual state rather than starting another conversion blindly.

Preflight checks source ownership, permissions, schema/provider contracts,
numeric-ID dialect, free space and backup access, stop/quiescence, and exact
target capabilities. Preserve appdata/config, service identity/account/args,
environment, mounts and platform ownership. Verify a native application/data
read after startup; `/ping` alone does not prove converted-library health.

If format/provider/unsupported-setting decisions prevent a valid automatic
result, leave the old service usable and return a machine-readable reason plus
an actionable review flow. Known configurations should migrate without a
hand-authored JSON file. Unknown source data cannot be made correct by guessing.
Recovery must not overwrite newer data blindly. A rollback after users have
written to ChaptarrNG must preserve those writes separately and state that the
restored source is the pre-cutover state.

### C4–C6 — Source review complete; validate outcomes

The code handoff records the completed source comparisons and fingerprints for
these behavior families. This order is now the bulk acceptance sequence, not an
open source-review queue. It is not an instruction to rewrite all 129 backend
or 64 frontend files:

| Validation order | Behavior family | Acceptance evidence needed |
| ---: | --- | --- |
| 1 | Release decisions and download lifecycle | Identical releases, tags, queue state, adoption, completion, retries and cleanup on migrated and native profiles. |
| 2 | Matching and filesystem effects | Exact threshold/tie/fallback inputs; real roots, collisions, hardlinks/copy/move, sidecars, restart and no duplicate ownership. |
| 3 | Catalog refresh and saved providers | Goodreads/Hardcover/catalog requests, import-list state, credentials, preferences, rate limits and provider failure retry. |
| 4 | Settings/auth/external integrations | Saved config aliases, OIDC/reverse proxy, API keys, webhook IDs, notifications, backup/encryption, MQTT, metrics and diagnostics; retain current access controls. |
| 5 | User workflows | Books/Bookshelf/Author/Unmapped Files/Search/Settings/Store, old bookmarks, sorting/filtering, mixed formats, bulk selection, preference migration, retry/cancel/error and accessibility. |

For every reviewed item record source/target blobs, the user-observable contract,
one of `implemented-equivalent`, `code-change-required`, or `validation-only`,
and the associated work/acceptance ID. Preserve existing reviewed retirement of
non-runtime repository artifacts, but do not use retirement to close a required
runtime capability. Revalidate only affected contract families after a fix.

### C7/C8 — Complete delivery, not another converter

| Channel family | Code/integration remaining before acceptance | Final acceptance exercise |
| --- | --- | --- |
| GHCR and Docker Hub aliases | Reuse mode-specific digest publication and existing readiness gates; establish the source-mode/profile and failure-recovery contract for automatic container startup. | Pull/update the **old mutable reference** on amd64 and arm64 with unchanged mounts, key, port and URL; verify conversion, reads and recovery. Immutable pins get a documented one-time image change. |
| Debian/PPA, AUR, RPM/COPR | Shared source-mode preflight and journaled health/rollback hooks are present for default systemd units; custom overrides are preserved and require operator review. | Upgrade the actual old package using apt/pacman/dnf with real dependencies and systemd; inject start/stop/restart failures and verify data, environment, UID/GID, journal and rollback. |
| Windows installer and Chocolatey | The migration installer handles SQLite/PostgreSQL, preserves service account/start mode, and carries effective NSSM environment settings through preview and into the target service. Chocolatey registers deferred-start recovery, captures both NSSM environment blocks, checks configured HTTP plus database health, preserves a failed target database and records channel outcomes. Feed/identity delivery and installed PostgreSQL permission/TLS/access acceptance remain. | Upgrade active and stopped installed services with real NSSM settings; pass/fail source migration and target-to-target update; verify environments, arguments, start mode, URL base, SQLite/PostgreSQL recovery, task cleanup, preserved target DB and retry. |
| AppImage and Flatpak | Wrappers preserve desktop IDs/data paths, supervise HTTP plus database health, persist state, require `setsid --wait`, stop the app process group and restore source data after failure/interruption. Old artifact reinstall remains the rollback step. | Same-path/app-ID update, sandbox permissions, SQLite/PostgreSQL data, spaced/equal `-data`, custom port/URL base, failed/interrupted launch, process-tree cleanup and reopening the prior artifact. |
| Helm and Unraid | The Helm chart now has an explicit stopped-pod restore Job for rollback after a successful conversion, including the retained PostgreSQL log/cache dumps. The Unraid template preserves the old identity and paths; external catalog delivery remains outside this repository. | Existing release/PVC or CA installation updates normally; no replacement empty volume, new base URL or lost environment. Exercise GitOps reconciliation and rollback on the installed channel. |
| StartOS | The ChaptarrNG repo now includes `packaging/scripts/build-bookshelf-startos-artifacts.sh` to checksum-verify the release bundle and build signed `.s9pk` artifacts in the external StartOS workspace without importing its signing key. The external package repository/feed still needs its own update. | Run the builder, then exercise real device update/backup/restore for supported architectures and version, retaining package/volume IDs and `/init`. |
| Cloudron and Co-op Cloud | In-repository candidates preserve their existing app/stack and data identities and use the shared migration supervisor; Co-op Cloud has explicit backup, health, and single-writer Swarm update behavior. External feed/recipe delivery remains. | Real platform-managed install/update/backup/recovery and rollback with persistent volumes and reverse proxy. |
| Other catalogs | Inventory actual maintained BookshelfNG entries, then submit identity-preserving changes for supported CasaOS/ZimaOS/Umbrel/TrueNAS/Cosmos/CapRover/Portainer records as applicable. Do not claim a candidate Compose file updates external catalogs. | The real catalog update reaches an existing install and passes the same data/client checks. |
| Old in-app updater, manual services, pinned/offline copies | Build a one-time migration bootstrap/package plus a maintained future update route; old binaries pointing at Readarr's server cannot discover a new owned endpoint on their own. | Starting from the old artifact, execute the supported one-time update and let migration run automatically; document why zero-action delivery is unavailable for that class. |

Code in the main repository can prepare artifacts and adapters now. Feed access,
moderation, platform infrastructure and external repository integration are
delivery dependencies with named outcomes, not reasons to leave unrelated
application code incomplete. Track each separately. Do not turn off release
gates to make a channel test appear successful.

## 6. Test, refine and fix after the code pass

Extend the existing `src/Chaptarr.Core.Test`, frontend `.test.cjs`, `integration/`
and `packaging/scripts/test-*` suites. V0 is acceptance tooling, not another
product feature. Preserve executable comparisons against the pinned BookshelfNG
build so expected behavior is not reconstructed from memory.

| Stage | Required evidence | Fix loop |
| --- | --- | --- |
| V1: deterministic | Backend Release builds/tests on Linux, Windows and macOS; frontend tests/typecheck/production build; every shared API operation and command assigned a contract case; equivalent queries and inputs produce the expected normalized responses and side effects. Verify generated OpenAPI against the running app. | Fix concrete mismatches; rerun the affected contract plus relevant regression suite. Normalize only intentional branding, time and explicit identity-map differences, never monitoring/default/error changes. |
| V2: data and faults | SQLite versions across supported migration boundaries and PostgreSQL across supported configurations; both provider modes; empty, large, mixed, ambiguous and orphaned data; credentials, profiles, histories, sidecars and files reconciled; preview leaves source unchanged; restart is idempotent; rollback reopens with the old binary. | Inject failure at every journal boundary: process kill, host/service restart, disk full, unwritable mount, corrupt/truncated input, DB lock, missing tools, stale review map, conflicting source DBs and concurrent starts. Fix recovery, not just fixture expectations. |
| V2: media processing | Real M4B/MP3/EPUB and relevant supported formats; chapters/tags, split tracks, embedded metadata, long/unicode paths, hardlinks/copy/move and network mounts. Kill the **full application and tool tree**, then restart and finish import without duplicate/lost files. | Check imported content, metadata and source cleanup, not just a successful converter exit or `/ping`. |
| V3: end to end | Unchanged SeerrNG/client URL/key/provider configuration; successful request → search → grab → client completion → import → availability/history, plus failed-provider retry, shared requests/cancellation and restart. Live saved list/notification/catalog/auth settings produce the same intended outcomes. | Fix the owning C1–C6 contract. Existing create/poll/cancel or read-only smokes do not substitute for fulfillment. Keep fixtures/keys private and commit redacted result metadata. |
| V4: delivery | Upgrade through each supported old channel with its actual predecessor, package manager/service/platform, and exact candidate digest/checksum. Verify settings/data/client continuity, failure recovery, backup retention and rollback. | Fix the C3/C7/C8 adapter; repeat that channel and any shared coordinator regression. Do not repeat all unrelated provider tests for a packaging-only correction. |

Large-library qualification should compare migration time, peak memory, DB growth,
startup, scan/search latency, API paging and download/import throughput against
BookshelfNG on matched hardware/data. Establish acceptable bounds from the
baseline before testing; this assessment invents no timing target. Separate
provider throttling from application regressions.

R1 uses the exact accepted build/artifact set, records a per-channel publication
receipt, verifies the existing install receives it, and retains rollback assets.
A Git commit or merged PR is not that receipt. Keep an operator-visible migration
result and recovery path after cutover. Retire BookshelfNG service/build delivery
only when supported installs have an accepted transition; code merge is a
separate milestone from publication and operational retirement.

## 7. Definition of done and status discipline

**Code complete:** C1–C8 exit conditions hold; every required behavior and source
review item has a disposition; no known application or channel-adapter defect
remains. Evidence-only, external-access and immutable-delivery limits are
separately named. Do not call unknown semantic differences validation-only until
the code path has actually been reviewed.

**Replacement accepted:** V1–V4 pass for the supported matrix, required live
integrations work unchanged, and the ordinary update transaction succeeds.
**Delivered:** R1 receipts and post-cutover checks exist. These milestones must
not be collapsed into a single vague "not close" or "done."

Each progress report should state completed IDs/commits, remaining confirmed
code defects, remaining semantic reviews, validation results tied to revisions,
external delivery dependencies, and the next executable slice. Stop adding
unrelated enhancements. Do not gate the next code fix on unavailable operator
credentials or another full media test run.

**Next batch:** after the code pass closes, run the V1–V4 acceptance matrix in
the [code handoff](BOOKSHELFNG_COMPATIBILITY_CODE_HANDOFF.md), beginning with
same-input C1–C6 behavior and the implemented Windows, desktop, and Linux
recovery paths. Finish any remaining C7–C8 delivery adapters after their
required channel inputs are available. Turn concrete mismatches into focused
code fixes and rerun the owning acceptance cases.

## 8. Reproduce and refresh this assessment

Use clean clones with the four pinned commits available. The script reads Git
objects, so local uncommitted application edits cannot silently affect results.
Fetch the historical ledger commits if using shallow clones.

```sh
python3 scripts/audit-bookshelf-replacement-snapshot.py \
  --bookshelf ../bookshelfng \
  --source-ref d42ec2aba1e411f4fb538d4d7201f6930898a91e \
  --target-ref 9bf2cf654ce01dfb25d41a59525ce8e4dca39a29 \
  --output docs/maintainers/bookshelfng-replacement-snapshot-2026-10-24.json
```

For a later checkpoint, pass its explicit source/target SHAs and a new output
name. Reassess the concrete findings against changed code; do not merely copy
the old conclusion or promote every matching filename to a parity claim.

The source snapshot generator was executed for the pins above. Assessment
verification additionally checks deterministic output, method-sensitive route
inventory, changed-review detection, moved command declarations, JSON/document
links and `git diff --check`. Application build and runtime acceptance are
deliberately not claimed by this planning change.

These assessment checks passed: eight isolated audit assertions, byte-identical
snapshot regeneration, 37 local document links and count reconciliation. The
repository's staged Semgrep scan ran 290 applicable rules on the audit script
with zero findings; the pre-push image-alias/readiness regression script passed.
These regression scripts do not enable or change a live readiness gate.

## Operator experience and migration choices

The source now includes a persistent Simple / Advanced UI experience choice and explicit automatic, ask, and manual migration timing across the legacy Windows, Debian, RPM, AUR, AppImage, and Flatpak paths. BookshelfNG imports select Simple by default; new ChaptarrNG installs select Advanced. Package `ask` defaults to No and cancels before mutation. Manual mode points users to the Console preview/apply workflow, then back to the package transition. Bulk acceptance must still cover builds and installed-channel behavior.
