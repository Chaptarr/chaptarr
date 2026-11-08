# BookshelfNG channel acceptance — 2026-10-26

## Current release and channel state — 2026-11-08

[ChaptarrNG v0.9.986](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.986)
is public. [Release run 42515404591](https://github.com/snapetech-org/chaptarrng/actions/runs/42515404591)
passed native validation, all ten standalone archives, the standard image,
and final asset publication. [Distribution run 42523595619](https://github.com/snapetech-org/chaptarrng/actions/runs/42523595619)
passed ChaptarrNG AUR and Chocolatey publication. Public AUR lists
`chaptarrng-bin 0.9.986-1`, and the public
[COPR build 11288368](https://copr.fedorainfracloud.org/coprs/slskdn/chaptarrng/build/11288368/)
reports `chaptarrng 0.9.986-1` succeeded. The Chocolatey publisher job passed;
public index propagation was not independently verified. Legacy BookshelfNG
package identities, external catalogs and production cutover remain separate
gates; the migration bridge was disabled for this release. The dated sections
below are historical evidence and may describe earlier publication status.
The public standard image `ghcr.io/snapetech-org/chaptarrng:0.9.986` resolves
to OCI index digest
`sha256:5d295630d3120109c73616cb34c939264b10ad5c42ebdc6813bb149ab825e2ed`.

The [published-archive installed run](https://github.com/snapetech-org/chaptarrng/actions/runs/42524674959)
passed No, headless Manual, Yes with source retention, target health, and
forced-failure rollback with restored source health on five Linux RIDs. The
same release tag `v0.9.986` and commit `665e89a7` supplied every archive.
The follow-up [run 42525035012](https://github.com/snapetech-org/chaptarrng/actions/runs/42525035012)
recorded kernel `7.0.0-1017-azure`: four targets passed first attempt; the
immutable BookshelfNG x86 predecessor exited before Yes in the first x86
attempt, after No and Manual passed. The x86 failed-job retry passed all four
cases. This is retained as an intermittent source-runtime/emulation finding,
not attributed to the target package. x86 used preinitialized source
auxiliary databases and excluded the unsupported legacy MAM indexer; the
candidate executable was 32 bit ELF machine 3, even though the Python harness
reported a 64 bit host process.

| Published `.986` archive | SHA-256 | Installed result |
| --- | --- | --- |
| `linux-x64` | `8af5e4d148de231725e7b3f4daa0c272003e30f1b4274a77bab4e46b858bf21e` | Pass |
| `linux-arm64` | `bef23cec42511c9c2a3b132ba6e2619bf582b0a093581d797ea2740dc3f5dd0d` | Pass |
| `linux-musl-x64` | `1c80b65f472fa74957a662a8fb75b61795ca100ba73ddc4b59c71e09db1dd8a3` | Pass |
| `linux-musl-arm64` | `a3e779beb6808428b9a373a6cb8b82c43d864f72a464935df7273a8d85b68ac0` | Pass |
| `linux-x86` | `19e5254b91940c5521403f501126c91341ff0a2ff36e74fc185912f733874db3` | Pass; source-only hosted flake retained |

The old identity feeds remain unbridged in a live 2026-11-08 check. Public
[AUR `bookshelfng-bin`](https://aur.archlinux.org/packages/bookshelfng-bin)
still lists `0.4.21.71-1`. The two latest public
[BookshelfNG COPR builds](https://copr.fedorainfracloud.org/coprs/slskdn/bookshelfng/build/11165173/)
failed before recording a source version; no `0.9.986` old-identity RPM is
present. These feed states do not affect the standard ChaptarrNG release, but
they prevent a claim that every existing package identity has a working
automatic upgrade path. Keep the bridge disabled until the old channel
publishers and installed handoff are accepted.

## Runner-policy update — 2026-11-03

The entries below record evidence and runner policy as of their dated
checkpoints. Public GitHub-hosted runner minutes are available again, and
trusted jobs may use compatible public runners or private ephemeral JIT.
Earlier instructions in this report to replay on private JIT are superseded;
runner choice does not close installed-channel, external-delivery, or operator
acceptance gates.

## Latest verification — 2026-11-05

Application-code `6f40dbf9`, full-server harness `5a9965ee`, and packaging
`bba3ae3f` remain unpublished. Full Build
[42049556701](https://github.com/snapetech-org/chaptarrng/actions/runs/42049556701),
strict API [42049562786](https://github.com/snapetech-org/chaptarrng/actions/runs/42049562786),
and all ten archives [42049568015](https://github.com/snapetech-org/chaptarrng/actions/runs/42049568015)
pass at `5a9965ee`. The strict run retains the documented source-error and verified
runtime/security dispositions; it does not turn them into exact matches.

Both fresh populated migrations and rollbacks pass again. Every managed author,
work and file in both real snapshots matches its predecessor API response:
88,405 records, zero field differences and no missing, extra or duplicate IDs.
All 23,981 cached image URLs return HTTP 200 with the original source bytes,
including TIFF originals. These bulk image checks use the explicitly recorded
test-only rate limit and cooldowns. Both full Seerr web format workflows pass
request, approval, transfer, import, scan, restart, identity and profile audits
with the identified repaired client test image. Source/native new-author profile
inputs and legacy readback also pass. This does not claim the unchanged deployed
Seerr image repaired or authorize publishing its merged client fix.

The native musl x64 archive test exposed missing generic handoff launchers in
Linux bundles. `bba3ae3f` adds the guarded launcher/supervisor/journal to all Linux
RIDs, preserving the glibc systemd bootstrap. Against the checksum-pinned public
`.76` musl predecessor, No, headless Manual, Yes, schema-126 health, retained source
backup, injected failure and healthy source rollback now pass. File identities,
Unicode paths, values above 4 GiB and part values remain intact. Evidence:
`/var/tmp/chaptarrng-native-standalone-20261105/musl-x64-wide-files-bba3ae3f/`.
The earlier HDD run timed out while migrations progressed and remains retained;
the SSD run closes that test without changing the application timeout.

Native retained-archive follow-up uses producer
[42071881875](https://github.com/snapetech-org/chaptarrng/actions/runs/42071881875)
at `750c3158` (application `6f40dbf9`, portable supervisor fix `f26fa1bc`). All ten
archive jobs succeed. Native Linux glibc x64/ARM64 and musl x64/ARM64 pass installed
No/Manual/Yes, target health, file preservation, injected failure and source
rollback. The earlier glibc failures exposed `/bin/sh` rejecting the supervisor's
`kill -- -PGID` syntax; the portable correction passes both full installed checks
and real process-group TERM/KILL tests. `AGENTS.md` records that boundary.

Retained consumer
[42072757567](https://github.com/snapetech-org/chaptarrng/actions/runs/42072757567)
passes Windows x64, Windows x86, macOS x64 and native macOS ARM64. Windows now explicitly selects the
source backup whose logical contents match the predecessor. macOS tests the
cross-built release archive without replacing or re-signing it. The earlier
ARM64 job was cancelled before allocation; the new run closes that gate. GitHub
acknowledged an Actions-start incident after several dispatches returned 500/502;
these infrastructure outcomes are not runtime acceptance.

The exact retained `750c3158` FreeBSD archive passes all four installed cases in a
new isolated FreeBSD 15.1-p6 VM, including wide/Unicode file values and rollback.
The public `.76` predecessor needs libiconv and OpenSSL ABI 17 in this guest.
A separate migrated-target launch with caller runtime overrides removed confirms
that the packaged launcher selects OpenSSL/SQLite and serves `/ping` successfully.
Evidence is retained under
`/var/tmp/chaptarrng-native-standalone-20261105/freebsd/` on `kspld0`.

Fresh source-built producer
[42074073639](https://github.com/snapetech-org/chaptarrng/actions/runs/42074073639)
at `e3ad5bab` succeeds for all ten archives and all five installed Linux jobs,
including native x86. Retained consumer
[42086393906](https://github.com/snapetech-org/chaptarrng/actions/runs/42086393906)
passes Windows x64/x86 and macOS x64/native ARM64 against those rebuilt payloads.
The exact rebuilt FreeBSD archive also passes all four installed cases in the
isolated FreeBSD VM. These give installed handoff passes across all ten runtime
targets; the Linux x86 host discrepancy below remains unresolved.

The Unix x86 runtime's delegate return-buffer helper used EDI/ESI instead of
managed ECX/EDX arguments. `e3ad5bab` applies an exact-source guarded correction
and qualifies the built runtime with four million nullable-return/validation
checks before producer success. The public `.76` x86 predecessor needs explicitly
recorded, checksum-pinned schema-158 auxiliary fixtures for the existing-installation
baseline; fresh auxiliary initialization remains a source limitation.

The unchanged x86 archive additionally passes No/Manual/Yes, schema-126 health,
wide/Unicode file preservation, retained backup, injected failure, rollback and
restored source health in an independent Debian 12 VM (kernel `6.1.0-54`). Its
SHA-256 is `d547254d21fb9f1eba72a2ec8a86720865f14594f37dcebb953e1a736d57c30b`.
ELF32 and application architecture establish native x86 execution; the harness
manifest's `processPointerBits` describes Python and is 64 in that guest.

On the desktop's Arch kernel `7.2.11`, the same target still fails first startup
with missing DryIoc registrations. A container diagnostic without seccomp filtering
also fails. Single-CPU and unrestricted-resource container diagnostics crash the
original BookshelfNG binary before target acceptance; a 1-GiB container still
fails target startup. The first native-host diagnostic mixed Arch's loader with
Debian libraries and its SIGFPE is excluded. A corrected task-owned diagnostic
changes only the extracted apphosts' ELF interpreter to the matching Debian
loader and passes the full suite. This modified diagnostic is not canonical
archive acceptance. The container discrepancy remains unresolved; it does not
establish a kernel defect or a passing target in that environment. The source-built
runtime differs from the earlier instruction-corrected diagnostic only in build
metadata, so a source rebuild alone did not eliminate this host failure.
Evidence is retained under
`/var/tmp/chaptarrng-native-standalone-20261105/` on `kspld0`, including both VM
passes and desktop failures. Headed UI still lacks Desktop Pool capacity;
publication, external channel acceptance and production cutover remain separate.

## Current campaign — 2026-11-05

Application candidate `3651598e` includes the legacy API base-path/flag repairs,
provider creation defaults, source-omitted provider defaults, retained Plex
master-switch behavior and native settings, and original-author-cover fallback. Later repairs preserve imported author metadata arrays and legacy work-date/page-count/percentage responses.
It is unpublished. CodeQL remains disabled; trusted public runners are authorized.

| Evidence | Revision and result |
| --- | --- |
| Provider conversion | `842ff7c9`: 93/93 importer fixtures pass, including sparse settings, effective categories/tags, MAM defaults and Plex protocol. Explicit values remain intact. |
| Plex API and notifications | `8dcd4900`: 34/34 focused checks pass. Actual packaged HTTP tests preserve the disabled master switch and source HTTP protocol, save both switch states through native requests, read them through both APIs, and retain the masked token. |
| Authentication | Source `.76` and migrated target pass valid Basic UI login (200), wrong/missing credentials (401), and API requests without API keys (401). The existing password is retained and upgraded to a salted target hash at login. Synthetic credentials only. |
| Linux x64 systemd installed handoff | Archive `8dcd4900`, SHA-256 `d18968542acf0c4474bd6acfee667a92c36cca343b4605c3e07e2c8ad54df45c`: No retains the active source; Yes converts and serves health; injected failure after schema-125 conversion and HTTP 200 restores healthy schema-158 BookshelfNG and retains the failed target. Both databases pass integrity/FK checks; source counts 4/6/4 become target 4/9/4. |
| Alpine/musl x64 runtime | Pinned `.76` Alpine 3.24.2 container runs the self-contained `8dcd4900` archive: conversion, retained source, account login, API key/port/auth settings, media canary, integrity and FKs pass. Updating this disposable installation to `2badb8c2` passes authentication and cover HTTP checks. This is runtime acceptance, not an Alpine service/package handoff. |
| Cover behavior | `2badb8c2`: 60/60 focused cover checks pass after two missing/empty-rendition regressions reproduced the old 404. Packaged HTTP now serves the original JPEG for the missing author rendition. Production ImageResizer decodes/resizes 80×120 to 40×60 in the Alpine runtime. FreeBSD still serves originals without native resizing. |
| Strict API differential | [42021059988](https://github.com/snapetech-org/chaptarrng/actions/runs/42021059988), `f3ab4cb2`: 94 cases, 88 pass, 3 failures, 1 source error, 2 expected route-policy differences. All provider schemas, metadata defaults, localization and instance-name reads match. The added settings readback exposed a valid-Readarr-name validation defect, repaired at `3651598e` (32 focused configuration checks); follow-up [42022250332](https://github.com/snapetech-org/chaptarrng/actions/runs/42022250332) confirms 89 pass, two strict differences, one source error and two route-policy differences. Settings save returns 202 and original/wrong password probes pass. |
| macOS standalone | [41971294040](https://github.com/snapetech-org/chaptarrng/actions/runs/41971294040), `8dcd4900`: x64 and arm64 No/Manual/Yes, health, source backup and failed-start rollback pass. |
| Helm installed handoff | [41971352113](https://github.com/snapetech-org/chaptarrng/actions/runs/41971352113), `8dcd4900`: pass. |
| Fedora 44 RPM installed handoff | [41971349047](https://github.com/snapetech-org/chaptarrng/actions/runs/41971349047), `8dcd4900`: pass. |
| Full current-source CI | [42016737304](https://github.com/snapetech-org/chaptarrng/actions/runs/42016737304), `13d490b6`: Linux, Windows, macOS, PostgreSQL and Docker pass. Follow-up [42021064161](https://github.com/snapetech-org/chaptarrng/actions/runs/42021064161), `f3ab4cb2`, is running. |
| All ten standalone archives | [42021069122](https://github.com/snapetech-org/chaptarrng/actions/runs/42021069122), `f3ab4cb2`: all ten pass, including ebook/audio acquisition and cover fetching. Project-scoped runtime metadata and reused-producer provenance are verified. Payload builds do not establish installed handoff for every RID. |
| Migrated acquisition | Same run: original EPUB and synthesized MP3 pass schema-158 conversion, actual deployed SeerrNG adapter request, Torznab search/grab, real local qBittorrent transfer, import/history, format-only availability, restart and content/source-backup integrity. Full SeerrNG web request/approval fulfillment, real trackers and Usenet remain open. |
| Registered image proxy | `1b8865ee` repairs API authentication for safe registered image paths; 104 focused checks pass. Packaged `8a9908b1` passes actual Seerr book/author cover methods and authentication boundaries using an original public fixture. Real-data clones remain offline; 40 cached-cover reads retain source bytes. |
| Linux x86 native runtime | Clean-SDK archive starts schema 125 and `/ping` 200 on Debian 12 i386; packaged production ImageResizer passes JPEG decode/resize. Desktop Arch runtime initialization crash remains open. |

Fresh packaged `f3ab4cb2` repeats migration and rollback on both operator
snapshots: 56,058 files, every source user column, provider settings and author
metadata arrays are preserved. All source tables and SQLite identity counters
match after rollback; integrity and foreign keys pass. Captured source metadata
fallback matches privately and resolves through native/legacy HTTP with the
image's `METADATA_URL` cleared. Production PIDs/start times are unchanged.

The remaining strict differences concern write-only host passwords and actual
replacement runtime facts. The comparator now retains those differences while
requiring successful settings/authentication proofs or independent native-API
confirmation before a policy disposition. It never waives instance names,
authentication, API paths or unlisted fields. One source paged request still
returns HTTP 500 for an author without monitored editions; valid-page cases
remain independently verified. Latest policy-classification execution is pending.

Evidence is retained on `kspld0` under
`/mnt/disks/scratch/task-worktrees/chaptarrng-parity-handoffs-20261104/`, including
`linux-systemd-a8f5af1b/guest-evidence-8dcd4900`, API JSON, TRX files, runtime logs,
archive checksums and `skia-musl-probe-2badb8c2`. The systemd VM is powered off;
earlier synthetic HTTP containers and networks have been removed. Separate
private operator-data runtime containers remain active during validation. Its retained overlay is
`/var/tmp/chaptarrng-systemd-a8f5af1b/system.qcow2`.

Both live operator SQLite snapshots have since passed isolated conversion and
rollback. The author-metadata repair passes repeat conversion and 101 importer
checks; eight real-media formats pass bounded reads and range downloads. See the
[operator rehearsal](BOOKSHELFNG_OPERATOR_REHEARSAL_2026-11-05.md). Live-provider
and complete SeerrNG fulfillment, media writes/imports and recovery remain open. Headed UI acceptance is blocked because
all three Desktop Pool v2 slots are quarantined for other tasks' artifact-export
recovery; no session was taken over or reset. External feeds/platform delivery
and production acceptance remain open. No publication, bridge activation,
cutover or retirement follows from these results.

## Earlier campaign — application `0d711ce1`, 2026-11-05

Application head 0d711ce1a9828675e879b2ba97f36fc3b9544188 passed the public full Build [41955009417](https://github.com/snapetech-org/chaptarrng/actions/runs/41955009417): Linux x64 4,997 passed / 26 skipped, macOS Intel 4,994 / 29, and Windows x64 4,957 / 33. The Linux M4B smoke passed 10/10. PostgreSQL migration and Docker image jobs passed. Windows passed the installer service upgrade/rollback, selected data-directory, and public BookshelfNG 0.4.21.76 Chocolatey No/Yes/rollback acceptance.

The exact-head standalone macOS handoff [41955575531](https://github.com/snapetech-org/chaptarrng/actions/runs/41955575531) passed on x64 and arm64. Exact-head Helm [41955974821](https://github.com/snapetech-org/chaptarrng/actions/runs/41955974821), Fedora 44 RPM [41956031349](https://github.com/snapetech-org/chaptarrng/actions/runs/41956031349), and legacy distribution/runner contracts [41956034314](https://github.com/snapetech-org/chaptarrng/actions/runs/41956034314) passed. Standalone asset run [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789) passed for all ten runtime archives, including Linux x86 and FreeBSD. This verifies payload creation and checksums, not installed handoff for every runtime.

The API differential [41955732272](https://github.com/snapetech-org/chaptarrng/actions/runs/41955732272) remains a strict failure: 67/79 matched, 9 strict mismatches, 1 inconclusive source error, and 2 expected policy differences. The three write/readback cases passed. The inconclusive paged-books case returns HTTP 500 in BookshelfNG when the fixture author has no monitored editions and HTTP 200 in ChaptarrNG. Review found product identity, build/runtime/OS, localization, help text, schema ordering, metadata endpoint, provider defaults/catalog, and the target's write-only host-password response among the strict differences. The populated fixture's saved MAM indexer reads back as expected. The initial review assumed migration 087 covered Plex's global switch; later investigation found that BookshelfNG rows are imported after that migration. The current-source correction is recorded above. ChaptarrNG also supports delete-event triggers unavailable in the source. These are not comparator passes or blanket parity waivers. The test sample does not establish exhaustive route or command parity.

These hosted runs validate the exact source candidate without publishing packages or changing any installed channel. Representative operator-data conversion/rollback, live provider and media fulfillment, external feed/update delivery, and production acceptance remain open. Older JIT-only routing instructions in the dated entries below are superseded by the runner-policy update above; historical evidence still has the scope and candidate described at its checkpoint.

## Result

The local 0.9.978 candidate passed installed handoff acceptance on Debian, Fedora, Arch, Windows Chocolatey, Linux x64 systemd standalone, FreeBSD standalone, Docker, Helm, AppImage, Flatpak, and Co-op Cloud Swarm paths described below. The current public BookshelfNG `0.4.21.76` Chocolatey predecessor also passed an installed Windows handoff to the exact candidate on public GitHub-hosted Windows run [41785474409](https://github.com/snapetech-org/chaptarrng/actions/runs/41785474409), code head `d12798d1`. The run passed manual defer, injected migration-health rollback with source recovery, and automatic migration, along with the full Windows .NET suite, installer build, SQLite fingerprint, service upgrade/rollback, and selected-data-directory preservation. This validates installed package behavior on a hosted Windows runner; feed publication and a broader set of operator installations remain separate. The shared Linux launcher harness also passed No, headless Manual, Yes, health, source backup, and forced rollback; it is separate from the Linux tarball’s systemd bootstrap, which is tested below. Cloudron 10.1.6 passed an installed .4.21 to .4.23 package update, Manual defer, native backup/restore, Automatic migration, and source-database plus old-package rollback. StartOS passed x86_64 and aarch64 test-package updates covering Manual defer, automatic migration, health, and source rollback; these remain synthetic, test-key rehearsals. A local Unraid-template Docker rehearsal passed manual defer, automatic migration, health, and forced rollback; it does not count as Community Applications or host acceptance. Channels expose the migration choice through a package prompt, terminal-backed launch, or platform setting where supported; pacman uses a deferred two-transaction handoff because its scriptlets cannot cancel an active transaction.

This is **not 100% release-channel acceptance**. The current 0.9.978 BookshelfNG replacement artifacts have not been published, and several package feeds contain no BookshelfNG predecessor. StartOS still needs the official signed package, external repository delivery, and package-manager rollback acceptance. Cloudron feed/catalog publication, Unraid Community Applications/host update and rollback acceptance, canonical Co-op Cloud recipe acceptance, and external catalog installations remain open. The old Cloudron `.4.21` package also cannot back up its persistent directories; that legacy backup gap is covered only after the new package with backup hooks is installed. Release gates remain closed.

## Choice behavior

| Channel | Does it support y/n? | No behavior | Yes behavior | Acceptance |
| --- | --- | --- | --- | --- |
| Debian / APT | Yes | Pre-install hook exits before staging; apt/dpkg cancels the candidate transaction and leaves the predecessor active. | Installs the same-name candidate, migrates during service startup, keeps the source backup, and supports tested rollback. | Installed Debian 13 VM, public BookshelfNG 0.4.21.71 to local 0.9.978; No, Yes, health, data, and rollback passed. |
| Fedora / DNF / RPM | Yes | RPM pre-install hook exits before staging; DNF cancels the transaction and leaves the predecessor active. Detached ask mode fails closed before staging. | Installs the same-name candidate, migrates under the service environment, checks HTTP/database health, and keeps the source backup. | Fedora 44 VM with SELinux enforcing, BookshelfNG 0.4.21.71 to local 0.9.978; interactive No/Yes, headless rejection, service health, data, and rollback passed. RPM scriptlets read the controlling terminal because DNF redirects scriptlet stdin even during terminal-backed installs. |
| Linux x64 standalone / systemd | Yes; the one-time bootstrap now asks `[y/N]` before acquiring its handoff lock. | No, blank, or EOF returns before the lock, service stop, or file writes. Noninteractive Ask fails closed; unattended replacement requires `--migration-mode automatic`. | Yes stages the checksum-verified archive, migrates the existing service data, switches the executable, checks health, and retains rollback files. | Historical Debian 12 VM acceptance passed migration, health, data, and forced rollback before this prompt was added. The exact-head public Build [41955009417](https://github.com/snapetech-org/chaptarrng/actions/runs/41955009417) passed the Linux x64 suite and the systemd standalone No/Yes prompt contract. The installed Linux VM handoff was not replayed on this candidate. |
| Arch / AUR / pacman | Yes | pacman cannot cancel a transaction from a package scriptlet. The package transaction completes, then the hook restores the saved BookshelfNG app, unit, and launcher, leaves the database untouched, and records a deferred marker. The next package transaction asks again. | The next accepted transaction installs the ChaptarrNG runtime, converts the database, retains the BookshelfNG source backup, and starts the existing service. | Arch systemd VM: 0.4.21.71 to 0.9.978-2 with No, then 0.9.978-3 with Yes; schema 158 to 124, health and restore checks passed. |
| Windows / Chocolatey | Yes | Chocolatey cancels before the service is stopped or application files are staged; the installed predecessor remains active. | Upgrades the bookshelfng identity and replaces the executable while retaining the NSSM service name, LocalSystem identity, data directory, service arguments, and source database backup. | Windows Server 2025 VM: public 0.4.21.70 to local 0.9.978; No and Yes passed. Chocolatey exited 0 for Yes; `/ping` and migration health check passed. The exact-head public Build [41955009417](https://github.com/snapetech-org/chaptarrng/actions/runs/41955009417) passed the 0.4.21.76 No/Yes/rollback handoff, installer service upgrade/rollback, and selected-data-directory checks. |
| AppImage | Yes in its terminal-backed launcher | Defers database conversion; it does not undo a downloaded or replaced AppImage. The user can retry later. | Converts on launch, retains the source database, and supports restore. | The desktop entry sets `Terminal=true`. Local same-identity .71 to .978 rehearsal passed with synthetic schema-158 data, custom paths, health, and rollback. No is a data deferral. The candidate is unsigned and unpublished. |
| Flatpak | Yes in its terminal-backed launcher | Defers database conversion; it does not undo a Flatpak deployment. The user can retry later. | Converts on launch, retains the source database, and supports restore inside the sandbox. | The desktop entry sets `Terminal=true`. Local same-app-ID .71 to .978 rehearsal passed with synthetic schema-158 data, custom paths, health, and rollback. No is a data deferral. The candidate is unpublished. |
| Helm / Kubernetes | No interactive terminal in the pod | Ask mode defers conversion and keeps the old data in place; operator action and a pod restart are required to retry. | The candidate pod converts, starts, and passes the configured health check; a rollback Job restores the source database. | Kind installed upgrade from the existing BookshelfNG chart tag main-v0.4.21.71 passed No/defer, Yes, and explicit rollback. Candidate chart and image are not published. |
| Docker / Compose | Yes with an attached TTY; no in headless/Compose startup | TTY `n` defers conversion. Headless Ask writes a deferred marker, leaves the database and config byte-identical, and does not serve app `/ping`; the new container waits for operator action. | TTY `y` or a restart in automatic mode converts on first start, checks migration and app health, retains the source backup, and serves the existing endpoint. | Exact-head local candidate image passed interactive TTY No/Yes, headless Ask/defer, automatic handoff, `/ping`, migration check, and injected health-failure rollback. Image aliases and external pull/recreate delivery remain unpublished. |
| StartOS | No y/n prompt; package configuration uses an automatic/manual choice | Set Manual through the package action and start the candidate; conversion is deferred, the app remains unavailable, and the schema-158 source stays unchanged. Run the documented Console preview/apply, then switch back to Automatic and restart. | Set Automatic and start the candidate; the replacement previews, converts, retains its source backup, passes database checks, and serves `/ping`. A failed health check restores the source database and leaves ChaptarrNG stopped. | StartOS 0.4.0.3 x86_64 and aarch64 VMs passed test-key SDK3 package updates. ARM64 used synthetic predecessor `.4.21:3` to candidate `.9.979:1`; Manual defer, Console preview/apply, Automatic conversion, and injected health-failure rollback all passed with `/books` and `/downloads` canaries. The predecessor fixture wraps the pinned BookshelfNG Readarr 0.4.21.37 ARM64 image with a direct service command because its s6 `/init` requires PID 1. Official signed `.s9pk`, public `.71` package, external repository, and package-manager rollback remain unaccepted. |
| Unraid | No y/n prompt in the template update flow | Set migration mode to Manual in the template before applying the image update; conversion is deferred until Automatic is selected and the container is restarted. | Set Automatic and restart; the image performs migration on startup. | Local Docker rehearsal against BookshelfNG 0.4.21.71 and a local ChaptarrNG 0.9.978 image passed manual defer, migration, `/ping`, retained source data, settings and media canaries, and forced rollback/recovery using the legacy template's mount and environment contract. This was not run in Unraid Community Applications or on an Unraid host; image publication remains open. |
| Cloudron | No terminal prompt; the administrator can hold the app update or set manual/automatic mode | Set `BOOKSHELFNG_MIGRATION_MODE=manual` before applying the package update; the proxy keeps `/ping` healthy and shows recovery instructions while the database remains unchanged. | Apply the update with automatic mode to convert on startup; Cloudron custom app environment can override the image default. | Cloudron 10.1.6 installed `.4.21` to `.4.23`; Manual defer, native backup/restore, Automatic migration, and source-database plus old-package rollback passed. The `.4.21` pre-update backup does not include persistent directories; `.4.23` adds backup/restore hooks. Feed/catalog publication remains open. |
| Co-op Cloud | No interactive y/n in Swarm. To choose No, keep the BookshelfNG stack deployed. Deploying the candidate with `manual` defers conversion but the new service does not serve `/ping` until the operator switches to Automatic and redeploys. | A Manual candidate update keeps the schema-158 database byte-identical and records `deferred`; the app endpoint is unavailable while held. | Redeploy the same stack in Automatic mode; the compatibility image converts, validates, retains a source backup, and serves `/ping`. | Isolated Debian 13 / Docker 26.1 Swarm upgraded the pinned BookshelfNG .71 source image to the exact-head .978 candidate. `bookshelfng_app` and all three volume names stayed stable. Manual preserved the database SHA-256 `f126ce2ae615dd6e898a446a2b122bc90ae9d10ea9a9ab2090cf0ca90830a1a1`, schema 158, and media canaries. Automatic reached schema 124, passed SQLite checks, kept the source backup and canaries, and served `/ping`. An injected unhealthy update paused as configured; restoring the three-volume snapshot and redeploying .71 returned healthy BookshelfNG at schema 158. The official Backupbot II 2.4.0 image (`sha256:870277cdba4b91ad6771184a7e208ba29151b951e39c68b4a3fd6f8d35252feb`) backed up those same volume identities using the recipe label. Its restore recovered a corrupted schema-158 database byte-for-byte, passed SQLite checks, restored all three volume canaries, and left the service mounts unchanged. Abra 0.13.0-beta deployed an isolated recipe with the same service and volume identities, created and listed the snapshot, then restored the corrupted database byte-for-byte and restored all three canaries after the app was scaled to zero. The app restarted with unchanged mounts. Canonical recipe acceptance remains open. |
| Self-hosting catalogs | No common y/n contract | Depends on each catalog's UI and image replacement behavior. | Compose candidate preserves the bookshelfng service and volume identities. | Local Compose contract only; CasaOS, ZimaOS, Umbrel, TrueNAS, Cosmos, CapRover, Portainer, and other external listings were not updated or accepted. |
| macOS standalone installs | No BookshelfNG-owned update channel to redirect; the bundled launcher asks when run in a terminal. | Answer No or set `BOOKSHELFNG_MIGRATION_MODE=manual`; keep the prior app and service path. | Answer Yes or set `automatic`; the wrapper stages migration, starts ChaptarrNG, and checks health. The operator switches the launcher/service after acceptance. | Exact-head run [41955575531](https://github.com/snapetech-org/chaptarrng/actions/runs/41955575531) passed on x64 and arm64 against the public .76 predecessor: No, Manual defer, Yes, health, source backup, and forced rollback. It publishes no assets; the one-time app/service path switch remains manual. |
| FreeBSD standalone installs | No BookshelfNG-owned update channel to redirect; the bundled launcher asks when run in a terminal. | Answer No; the candidate exits without changing predecessor config/data. Restart BookshelfNG if it was stopped for the handoff. | Answer Yes; migration completes, ChaptarrNG starts, and health passes. The operator switches the launcher/service after acceptance. | FreeBSD 15.1 VM: installed BookshelfNG 0.4.21.71 to the exact local 0.9.978 archive; No, Yes, health, source backup, and forced-failure rollback passed. |
| Windows standalone migration installer | Yes, through its explicit Migrate BookshelfNG action and the migration preview's Yes/No confirmation; closing the window defers | Close without selecting Migrate, or answer No at preview; BookshelfNG remains installed. | Select Migrate, review the preview, then answer Yes to run backup, conversion, service handoff, and health checks. | Hosted Windows run [40218131519](https://github.com/snapetech-org/chaptarrng/actions/runs/40218131519) installed the checksum-pinned .71 service, forced automatic rollback, completed the real installer handoff, and exercised explicit restore. The installer source is unchanged since that run. Hosted Windows run [40274632625](https://github.com/snapetech-org/chaptarrng/actions/runs/40274632625) completed the No-at-preview service round-trip; No kept BookshelfNG installed, Yes completed the upgrade, and injected-failure rollback, explicit restore, service cleanup, and selected-data-directory checks passed. Chocolatey remains a separate channel. |

## Standalone release artifact coverage

These runtime identifiers are separate release payloads even when they share a
package channel. An installed handoff for one RID does not establish that the
other runtime payloads launch or migrate correctly. The Windows migration
installer is a separate x64 artifact.

All ten release RIDs passed exact-head archive build and checksum verification
in public run [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789).
Only `osx-x64` and `osx-arm64` have exact-head installed BookshelfNG handoff
evidence, from run [41955575531](https://github.com/snapetech-org/chaptarrng/actions/runs/41955575531).
Linux x64 has prompt-contract coverage, and Windows has package/installer
handoff coverage, but these do not exercise the standalone Linux or Windows
archive artifacts. FreeBSD has an earlier local installed handoff using a
0.9.978 candidate, not this exact application head. The Windows migration
installer is tracked separately from the `win-x64` archive. Public hosted
runners are authorized; a private-JIT replay is not required by policy.

| Release RID / artifact | Existing handoff evidence | Remaining acceptance |
| --- | --- | --- |
| `linux-x64` | Local 0.9.978 archive installed over public BookshelfNG .71 on Debian 12; migration, health, data preservation, and forced rollback passed before the new prompt. Exact-head public Build [41955009417](https://github.com/snapetech-org/chaptarrng/actions/runs/41955009417) passed the systemd standalone No/Yes prompt contract. | Run an installed handoff with the exact archive, including service health, data preservation, and rollback. |
| `linux-arm64` | StartOS aarch64 test-package handoff passed; it wraps a different StartOS package/runtime. Exact-head standalone archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). | Install and exercise the standalone `linux-arm64` archive over BookshelfNG. |
| `linux-musl-x64` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). | No installed BookshelfNG handoff is recorded. |
| `linux-musl-arm64` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). | No installed BookshelfNG handoff is recorded. |
| `linux-x86` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). | No installed BookshelfNG handoff is recorded. |
| `osx-x64` | Exact-head hosted handoff [41955575531](https://github.com/snapetech-org/chaptarrng/actions/runs/41955575531) built and checksum-verified the candidate archive, then passed No, Manual defer, Yes, health, source backup, and rollback. | No exact-head installed handoff gap is recorded; switching the local app/service path remains an operator action. |
| `osx-arm64` | Exact-head hosted handoff [41955575531](https://github.com/snapetech-org/chaptarrng/actions/runs/41955575531) built and checksum-verified the candidate archive, then passed No, Manual defer, Yes, health, source backup, and rollback. | No exact-head installed handoff gap is recorded; switching the local app/service path remains an operator action. |
| `win-x64` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). The Windows Chocolatey path passed the 0.4.21.76 handoff in [41955009417](https://github.com/snapetech-org/chaptarrng/actions/runs/41955009417). | Installed BookshelfNG handoff with the standalone `win-x64` archive is separate from Chocolatey and the migration installer; none is recorded. |
| `win-x86` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). | No installed BookshelfNG handoff is recorded. |
| `freebsd-x64` | Exact-head archive build and checksum passed in [41956084789](https://github.com/snapetech-org/chaptarrng/actions/runs/41956084789). An earlier local 0.9.978 archive passed No, Yes, health, source backup, and rollback on FreeBSD 15.1. | Install and exercise the exact-head archive over BookshelfNG on FreeBSD. |

The exact-head public results above supersede older JIT-only route caveats. Historical VM and hosted results below retain their recorded candidate and test scope.

## Windows Chocolatey evidence

The final local bridge package SHA-256 is 2bf0c0de3771266801aba5c0b3c6d970a323f537faa5eb4eeb1334bb373546d1. Its self-contained Windows archive SHA-256 is 16cd7eadbadf6d66a86aa68421de91dafe3986adb86b86539cc26c9d8ea1ac3a.

After Yes, the BookshelfNG service ran Chaptarr.Console.exe under the existing LocalSystem identity and data path, with the original -nobrowser and -data service arguments. The HTTP endpoint returned {"status":"OK"}, the migration health check exited 0, and the channel canary remained channel-canary=preserve-me.

The copied target database passed SQLite integrity_check with zero foreign-key violations and contained 4 authors, 9 format-specific books, and 4 book files at Chaptarr migration 124. The retained source backup passed the same checks with 4 authors, 6 books, 4 book files, and BookshelfNG schema 158. The target DB SHA-256 is a204610f73b14b2492af10d1cfe51406f859272b597aced853b0fe800f419201; the source backup SHA-256 is 03f78da296ba13c5d485f5590fcda875a432e65eea5910fb55820642a78d942e.

The Windows run exposed two bridge bugs and both are fixed: utility-mode errors could wait for Enter despite -exitimmediately, and captured utility stdout could be mistaken for the returned process exit code. The Console exit path now honors -exitimmediately for all failures, and the Chocolatey wrapper writes diagnostics separately while returning only the integer exit code. The PowerShell regression passed in the Windows VM.

An earlier test input archive was framework-dependent and lacked its .NET runtime. That failure is preserved as test-input evidence; the final test used the self-contained archive above and completed the real migration.

## Docker installed-image evidence

The exact-head local image is `localhost:15001/chaptarrng-channelqa:0.9.978-exact-head-ttyfinal`, image ID `sha256:4ba7f6b3fe858821db056d0370dc8b5ed8a45c9efae138b5b5202f7f488bf76d`. It layers the current `Chaptarr.dll` (SHA-256 `70e3881f696ab5879ff6e02839d2adff7f464eb81e0cf88ac3f57b49f94433f2`) and the final compatibility supervisor onto the dependency-matched local 0.9.978 image. The image was not pushed.

Interactive Docker `ask` displayed one `[y/N]` question. A single `n` wrote `manual-review`, left no pending or failed marker, kept the BookshelfNG database byte-identical at SHA-256 `53901555272388bc56157239ab674e3493dbcbcfd70557e0a468b0df9cf091b7`, preserved `config.xml` byte-for-byte, and did not serve app `/ping`. A single `y` on a fresh source fixture authorized conversion. The supervisor reported successful migration and startup health, `/ping` returned OK, and the read-only migration health check exited 0. The target passed SQLite integrity and foreign-key checks at schema 124 with 4 authors, 9 books, and 4 book files; its retained schema-158 source backup passed the same checks with 4 authors, 6 books, and 4 book files.

The final image was retested headless. Ask mode wrote `manual-review` and kept the database and config byte-identical; automatic mode converted the fixture, retained the source backup, passed the read-only migration check, and served `/ping`. The injected 30-second startup-health failure restored the schema-158 source database byte-for-byte, wrote `rolled-back`, retained the failed schema-124 target, and left `/ping` unavailable. Both database copies passed integrity checks with zero foreign-key violations. The complete current-image record is `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/docker-exact-head-ttyfinal-acceptance.json`.

A separate automatic-start fixture injected a port mismatch (`CHAPTARR_EXPECTED_PORT=8788`, saved port `8787`). The supervisor wrote `rolled-back`, stopped serving `/ping`, restored the source database byte-for-byte to the original SHA-256, and retained the converted target separately. Both databases passed integrity and foreign-key checks. The complete record is `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/docker-exact-head-fixed-acceptance.json`; the Ask, successful automatic, and rollback transcripts sit beside it.

Two TTY test harness issues were fixed before acceptance: GNU `timeout` needed `--foreground` to keep its child in the Docker terminal's process group, and Console utilities needed `-exitimmediately` to avoid waiting for Enter on a utility error before the y/n question. A later Cloudron-image test found that its replacement entrypoint had no inherited `CMD`; the candidate now passes the Chaptarr command explicitly. The final runs used fresh fixtures and waited for the explicit supervisor success or rollback message before collecting results.

## Cloudron image-wrapper evidence

A local Cloudron candidate image layered the package Dockerfile, nginx proxy, and Cloudron entrypoint onto the exact-head application image. With `BOOKSHELFNG_MIGRATION_MODE=manual`, the app wrote `manual-review`, preserved the source database byte-for-byte, returned HTTP 200 from the proxy `/ping`, and served the migration instructions for normal requests. With automatic mode, the inner app listened on port 8788 behind the stable 8787 proxy, migration health passed, `/ping` and the app route worked, and the read-only migration check passed. The target database passed integrity at schema 124 with 4 authors, 9 books, and 4 book files; the retained schema-158 source backup passed integrity. Cloudron supports custom per-app environment values, so the operator can select manual mode before update and automatic mode when accepting conversion. This records the wrapper-only rehearsal; installed platform acceptance is recorded in the next section.

## Cloudron installed platform evidence

Cloudron 10.1.6 updated app `tech.snapetech.bookshelfng` from the BookshelfNG
`.4.21` package to the local ChaptarrNG `.4.23` candidate, using the local
`0.9.978-cloudron-backup-hooks-v2` application image. The app ID, port, and
`/config`, `/books`, and `/downloads` mounts were preserved. Manual mode left
the schema-158 database unchanged, kept `/ping` at HTTP 200, and showed the
recovery page for normal requests.

Native Cloudron backup task 22 completed. Its archive contains consistent
SQLite snapshots for the active `readarr.db`, `logs.db`, `cache.db`, and
`staging.db`, records absent `audioarr.db` and `chaptarr.db`, and includes the
three persistent directories. The archive checksum and all SQLite integrity
checks passed. After changing databases, config, and media canaries and adding
stale database files, native restore task 25 restored the original config and
canary hashes, schema-158 database, and file layout; the stale files were
removed.

Automatic mode then migrated schema 158 to 124, passed integrity and
foreign-key checks, served `/ping`, and retained a BookshelfNG source backup.
The internal `-restore-bookshelf` operation restored schema 158 with the
original author/book counts, after which Cloudron restored the `.4.21` package
and returned BookshelfNG to healthy `/ping`. This tested the supported rollback
sequence while preserving later evidence files.

The legacy `.4.21` pre-update Cloudron backup did not contain `/config`,
`/books`, or `/downloads`: that package had no backup hooks. The new hooks only
protect backups created after `.4.23` is installed. Full task evidence,
including the backup ID and archive/database checks, is in the
[Cloudron platform test record](CLOUDRON_PLATFORM_ACCEPTANCE_2026-10-26.md).
Supporting VM logs and snapshots are retained under
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/`. The VM image and
registry were local QA infrastructure; no candidate or feed was published.

## FreeBSD standalone installed acceptance

The FreeBSD 15.1-RELEASE-p6 x86_64 VM installed the checksum-verified
BookshelfNG 0.4.21.71 predecessor and the standard-builder ChaptarrNG 0.9.978
archive. Candidate SHA-256:
`f60819a90a65c4deadd4eb44befa5ffd4f1dca0b7022fbe647ac2f2e9310398d`.

The bundled `scripts/bookshelfng-standalone-launch.sh` asked `[y/N]` on a
terminal. No preserved the predecessor's config and database byte-for-byte
and left the canary unchanged. After restarting the predecessor service,
BookshelfNG `/ping` returned OK.
Yes migrated the same data path; the replacement returned OK, reached schema
124, passed SQLite integrity and foreign-key checks, and retained a byte-exact
schema-158 source backup. The migrated database contained 4 authors, 9 books,
9 editions, and 2 series. A forced replacement health failure on a fresh source
fixture wrote `rolled-back`, restored the source database byte-for-byte, kept
the failed schema-124 target for diagnosis, and allowed the BookshelfNG service
to restart and pass `/ping`.

The runtime rehearsal exposed two FreeBSD-specific startup issues. The launcher
now selects an installed OpenSSL ABI (`.17` or `.35`) when the runtime's older
default ABI is absent, and repairs the bundle's `libe_sqlite3.so` link from the
system `sqlite3` library. The first missing-SQLite failure is preserved in
`freebsd-installed-candidate-preflight-missing-sqlite.log`; the final No/Yes,
rollback, and verification transcripts are retained in the task evidence
directory. The archive was rebuilt through the standard builder and its digest
matches the installed candidate.

## StartOS installed update acceptance

The x86_64 and aarch64 StartOS 0.4.0.3 VMs used test-only SDK3 `.s9pk` files
with package ID `bookshelfng`. The earlier x86_64 run updated test package
`.4.21:1` to `.9.979:1`; its candidate SHA-256 was
`312a0c956dac70d82623aecd866c0bfa2ae5b29f1749e4d1fdfbd5e9c4147bce`. The ARM64
run updated synthetic predecessor `.4.21:3` to `.9.979:1`. The predecessor
package SHA-256 was
`e5f9318d17dae104f99d06cda29bfe9f4d9dbe10ca25164e90178fb2bf758c23`; the
candidate SHA-256 was
`77a0f9e0c1d916ed7594d6b33366819f67269684fd132ec446cda7d2777f5bde`.

On ARM64, the package action persisted Manual, the package supervisor held
startup, and the schema-158 source remained unchanged. The Console migration
preview passed, explicit apply converted to schema 124 and retained the
BookshelfNG backup, and switching to Automatic restarted the same package with
healthy `/ping`. Re-seeding from the retained schema-158 backup then proved
the Automatic first-start path. SQLite integrity and foreign-key checks passed;
the final database had 4 authors, 9 books, and 1 download client. The `/books`
and `/downloads` canary hashes remained
`cf812181855dbf277ee62c0d3fbd17be04e0aa3a501f8a3dd529e6189f47a127` and
`d8b55e0867c52c71515b6261127404b0e9a37849fd7124fc528414ab2befcd34`.

A final test package `.9.980:1` deliberately expected port `8788` while the
candidate listened on `8787`. After the bounded health failure, StartOS
restored the schema-158 source to the retained backup hash
`5ee42780c008cf49d829c41f916383c91a8dc8936b867593739d2fedc4e6d260`, retained
the failed schema-124 target separately, and left `/ping` unavailable. Both
databases passed integrity and foreign-key checks. The failure candidate SHA
was `74651a849c7cad66ae3e65cdd70f8327a3952a6583859ca0d59f16ccca3f553d`.

The ARM64 predecessor fixture wraps the public BookshelfNG Readarr 0.4.21.37
image with a direct service command because its s6 `/init` must run as PID 1;
it is not the public `.71` StartOS package. All packages used a task-local
signing key. The exact guest transcript, package logs, hashes, and offline
SQLite checks are retained under
`/var/tmp/chaptarrng-channel-acceptance-20261026/vms/startos-acceptance/arm64/`;
the evidence summary is
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/startos-arm64-acceptance.md`.
This verifies synthetic package identity, update, migration choice, health,
and automatic source rollback on ARM64. It does not validate the external
maintainer's signed package, official `.71` predecessor, repository delivery,
or StartOS package-manager rollback. The candidate continues to inherit the
image entrypoint with `sdk.useEntrypoint()` and uses StartOS's port readiness
check.

## Other package checks

The 19-case legacy-channel Node contract suite passed after adding FreeBSD launcher/runtime, Windows standalone installer opt-in, macOS workflow, and StartOS wrapper checks. The publication-free hosted macOS handoff run passed on x64 and arm64. Docker compatibility defaults and the exact-head installed-image interactive/headless Ask/automatic/rollback acceptance passed. The Cloudron manual/automatic runtime rehearsal passed after fixing the missing command. The AUR prompt test passed No/defer/restore and a later Yes retry. The Fedora 44 RPM prompt transaction regression passed interactive No/Yes and detached headless Ask; Debian passed headless rejection, apt No/Yes, and downgrade. Linux shell syntax, workflow policy, and `git diff --check` passed.

The shared Linux standalone handoff harness used a checksum-verified public BookshelfNG .71 predecessor and an earlier local ChaptarrNG candidate. No preserved the predecessor and restarted /ping; headless Manual deferred without changing source files; Yes migrated to schema 124, passed integrity and foreign-key checks, retained a logical source backup, and returned healthy /ping. An injected replacement-health failure restored BookshelfNG and retained the failed target for diagnosis. The rerun also confirms the harness closes the PTY after a stalled Ctrl+C shutdown and reaps the candidate. This validates the shared launcher/supervisor only; the Linux archive has its own systemd bootstrap, tested in the following section. Full output: /var/tmp/chaptarrng-channel-acceptance-20261026/evidence/standalone-handoff-stop-fix-v6.log.

### Linux x64 standalone archive and systemd bootstrap

A Debian 12 x86_64 VM ran the installed BookshelfNG 0.4.21.71 service under systemd with schema-158 fixture data, settings, a synthetic API-key canary, and a filesystem canary. The local ChaptarrNG 0.9.978 archive was built from e9900d8d120e2a952c12ba45af1eb8394fa8bf34; it was not published.

The installed bootstrap migrated the target to schema 124, passed SQLite integrity and foreign-key checks, preserved settings and canaries, retained a logical backup matching the source baseline, switched the service path to Chaptarr, and returned healthy HTTP 200. A forced failure after the real migration restored the exact schema-158 source, predecessor executable and service, and health while retaining the valid schema-124 target copy. The run found and fixed a staging-directory collision and a service-user read-permission failure in the candidate payload.

The final archive SHA-256 is 14d30e6e65cd17e3e030a28d8368a0b3fb135268673d2e91dd25dc2e0efbd843. The embedded bootstrap helper SHA-256 matches source: e5d02515a5d1564f301a556845461ababbebbf5b2ee689dc40123c18c76a4c2d. The predecessor archive SHA-256 is 4197d53b757ebe99642fb8f4213f963dd54641401d1f188ab6daadcb3b024d5d. Logs, test packages, and checksums are retained in /var/tmp/chaptarrng-channel-acceptance-20261026/evidence/linux-standalone-current-20261026/.

Windows evidence is recorded at /var/tmp/chaptarrng-channel-acceptance-20261026/evidence/windows-final-acceptance.json and the full guest transcript is windows-server-2025-chocolatey-acceptance.log in the same evidence directory. Helm evidence is in helm-final2-acceptance.json and helm-final2-rollback-acceptance.json. Native VM package and service logs are retained beside them.

Co-op Cloud Swarm source, Manual defer, Automatic migration, paused failed
update, and volume restore evidence is under
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/coop-cloud-swarm-*`.
The Backupbot II acceptance log is
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/coop-cloud-backupbot-acceptance.log`.
It records image digest `sha256:870277cdba4b91ad6771184a7e208ba29151b951e39c68b4a3fd6f8d35252feb`,
an official `backup create` and `restore --noninteractive`, and successful
restore of the schema-158 fixture (SHA-256
`53901555272388bc56157239ab674e3493dbcbcfd70557e0a468b0df9cf091b7`) plus
all three volume canaries. The VM used the local checksum-pinned .71
predecessor and exact-head .978 candidate; temporary VM disks and keys were
removed after the runs.

Abra acceptance is recorded in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/coop-cloud-abra-backup-restore-acceptance.log`.
Abra 0.13.0-beta was checksum-verified and used local server mode to deploy a
test recipe with the `bookshelfng_app` identity, production volume names, and
`backupbot.backup=true`. `abra app backup create`, `snapshots`, and `list`
succeeded. After the database and volume canaries were corrupted and the app
was scaled to zero, `abra app restore` recovered the schema-158 fixture
byte-for-byte, passed SQLite integrity and row-count checks, restored all
canaries, preserved service mounts, and restarted the app. The test recipe and
helper are retained in
`/var/tmp/chaptarrng-channel-acceptance-20261026/vms/coop-cloud-abra/`.

## Current-worktree revalidation — 2026-10-26 follow-up

This section records code-level evidence for the modified worktree at d43a005.
At that follow-up cutoff it did not include a new package build and installed
upgrade across the platform VMs. The later current-source Linux x64 systemd
package and installed handoff acceptance is recorded below.

The focused .NET migration/database set passed 299 tests, with 11 PostgreSQL
cases skipped because their external database service is unavailable. The
BookshelfNG migration-journal fixture passed 4/4 cases. I added
`BookshelfMultiDatabaseConverterFixture` and verified 3/3 cases: populated
identical databases merge without duplication and retain both originals,
preview changes neither source, and a conflicting setting blocks promotion.
Those tests found and fixed a rejected journal-count format; the focused build
also found and fixed two C# compile errors in the current migration changes.

The legacy-channel contract suite passed 19/19, the private runner policy test
passed 10/10, all 17 workflow YAML files parsed, and `git diff --check` passed.
The full core suite and a final combined migration rerun were stopped after
10 minutes and 5.5 minutes respectively without a test summary; neither result
is counted as a pass or failure. Logs are retained at
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/` with the
`dotnet-bookshelf-*-20261026-followup.log`,
`dotnet-bookshelf-*-20261026-incomplete.log`,
`dotnet-core-full-20261026-incomplete.log`,
`node-channel-contract-20261026-followup.log`,
`workflow-runner-policy-20261026-followup.log`,
`workflow-yaml-20261026-followup.log`, and
`diffcheck-20261026-followup.log` names. The interrupted importer fixture at
`/var/tmp/chaptarrng-channel-acceptance-20261026/tmp/bookshelf-importer-7c37a74f/`
was retained for diagnosis.

At the time of this follow-up, private-only workflow edits were local and no CI
or canary had been started. Those route changes were later committed to main
in ca676141 and 7cbb1d2e; current runner state and the C0 gate are recorded
in ORG_WIDE_PRIVATE_JIT_AUDIT_2026-10-26.md. This follow-up does not close
the signed-package, feed/catalog, Unraid, canonical Co-op Cloud, external
catalog, or private JIT rollout gates.

## Remaining release blockers

### PostgreSQL source migration integration check

The current local workspace's full BookshelfNG PostgreSQL-focused suite passed
29/29 tests against an isolated PostgreSQL 17.4 server. It covered source
conversion and rollback, database selection, lineage checks, and external dump
backup/validation using the host's PostgreSQL 18.6 client tools. The fixtures
left no reserved test databases or migration schemas behind. Logs and container
provenance are retained in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/postgresql-source-migration-20261026/`.
This is populated database integration evidence, not an installed package or
deployment upgrade; it does not close any channel handoff gate.

### Shared journal portability follow-up

A follow-up fingerprint-journal change exposed a portability regression: the
intermediate implementation required Bash, GNU `stat`, and `sha256sum`, which
are absent from the FreeBSD base used by the standalone archive. The journal is
now POSIX `/bin/sh` and selects the platform's native metadata and SHA-256
commands. The Linux fingerprint/desktop-supervisor regression passed, and a
FreeBSD 15.1-RELEASE-p6 guest passed source-directory fingerprinting, relocation
stability with a nested newline-containing filename, content-change refresh,
and symbolic-link rejection. Logs are retained in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/freebsd-journal-portability-0b0c38c8/`.
The AUR choice container, Debian Trixie bridge package/upgrade/downgrade, Fedora
44 RPM bridge transaction, 19-case legacy-channel contract suite, 9-case
runner-policy suite, and 98 focused database importer, multi-database
converter, and migration-journal tests also passed against the current
workspace.

The FreeBSD 0.9.978 x64 standalone archive was rebuilt with the portable
journal and tested in a FreeBSD 15.1-RELEASE-p6 guest. Its SHA-256 is
`36585cde8193bfa4641552fda72b368f7c63536196cec4c2a8fbaa2bd387ab77`; the
bundled journal passed the same fingerprint, relocation, refresh, symlink, and
state checks. This closes the exact-archive journal portability check. It
does not rerun the full app handoff against this rebuilt archive, and the exact
candidate archive check on macOS remains open. Evidence is retained in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/freebsd-standalone-portability-20261026/`.

### Unraid template Docker rehearsal

A local Docker rehearsal evaluated the legacy template contract against the
BookshelfNG 0.4.21.71 source image and a local ChaptarrNG 0.9.978 image. The
synthetic fixture's `/library` paths were mapped to the template's `/data`
mount, with four media files created at the database's `BookFiles.Path`
entries. Manual mode deferred without changing the source database or config.
Automatic mode migrated schema 158 to 124, passed `/ping` and SQLite
integrity/foreign-key checks, reported no missing media watch paths, preserved
the config settings and all four media files plus `/data`/`/download` canaries,
and retained a backup whose schema and rows matched all 39 source tables. A
forced health failure restored all source tables and rows plus the config byte
for byte, preserved the media files, then the BookshelfNG image returned
healthy at `/ping`. The target database contained 4 authors, 9
format-specific books, 9 editions, and 4 book files.

This tests the local image/container handoff and the settings/mount contract;
it does not test the Unraid Community Applications UI, an Unraid host update,
published image delivery, or an official package rollback. Those remain open.
Image IDs, the reproducible harness, checksums, and filtered milestones are in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/unraid-template-docker-d43a005/`.

### Private Windows runner handoff

The Windows package and installer results above are historical hosted runs;
they establish package behavior but do not satisfy the current private-JIT
runner requirement. ChaptarrNG's local build, migration-installer, and
Chocolatey workflow jobs now target the historically approved private route
`[self-hosted, Windows, X64, packer-windows]`. The organization currently has no
online Windows registration carrying that label, so the private route and its
release jobs are still unverified. Keep those jobs queued until the
Packer/runner-policy owner restores the existing runner group/labels and
broker/workflow access and passes a Windows canary after C0 clearance.
Restore the approved route in place; do not move Windows
jobs to `windows-latest`, Linux JIT, or another runner. Keep them queued until
the original runner group/labels and broker/workflow access are restored and
the private Windows canary has passed.

At the 2026-10-26 14:08 UTC follow-up, runner group ID 11 still selected only
`snapetech-org/packer`; no runner carried `packer-windows`, and ChaptarrNG was
not selected. The API showed zero busy registrations, but BookshelfNG CodeQL
run `40332133668` still had three jobs queued. ChaptarrNG Release preflight
`40334519893` and Helm handoff `40334519631` were queued at main commit
`440a5644`. The C0 slot owner had not confirmed the drain, so the shared pool
and these runs were left untouched. The Release run is a potential publication
path for version `0.9.978` (latest published stable tag: `v0.9.977`); preserve
it and do not dispatch a duplicate or push a new head until its readiness and
side effects are reviewed.

The current organization-wide directive requires every build, test, security
scan, package build, and release workflow across all 65 organization
repositories to use approved private JIT capacity, with no hosted fallback.
The VM and hosted run records above remain behavioral evidence, but they must
be replayed through the approved private route before this campaign can be
called complete under the current execution policy.

At 2026-10-26 14:14 UTC the runner API still showed no Windows or public-fork
run-scoped registration, while the ChaptarrNG Windows and fork workflow edits
were local and untested. The Release preflight and Helm handoff remained
queued, and the C0 slot-owner clearance was still missing. No new CI or
publication run was started.

At 14:22 UTC, the live GitHub API showed that `ng-shared-main-jit` group ID 10
now selects all 65 organization repositories, including ChaptarrNG. No busy
runner registration or online ChaptarrNG Linux, Windows Packer, or public-fork
JIT runner was present in the runner API snapshot. Runner group ID 11 still
selects only `snapetech-org/packer`, so ChaptarrNG remains excluded from the
Windows route; group ID 5 selects seven repositories, including ChaptarrNG,
but has no registered fork runner. BookshelfNG CodeQL run `40332133668` still
has its Python, JavaScript/TypeScript, and Actions jobs queued. ChaptarrNG
Release preflight `40334519893` and Helm handoff `40334519631` remain queued at
main SHA `440a5644`. The older route-candidate run `40313259957` remains
incomplete: Docker and PostgreSQL jobs succeeded, Linux x64 failed, and Intel
macOS plus Windows x64 remain queued. This snapshot was read-only; the C0
handoff is still unconfirmed, and no queue, runner group, broker, or host was
changed.

- Debian/PPA and Fedora/COPR publisher paths remain closed; direct VM package acceptance does not prove a PPA or COPR feed upgrade. The public BookshelfNG COPR repository had no packages for the tested Fedora series.
- The Chocolatey legacy bridge handoff remains unresolved. A live Community Repository check on 2026-10-26 shows the public `bookshelfng` package at 0.4.21.76, whose install script still installs the BookshelfNG `Readarr.exe` archive, and a separate `chaptarrng` package at 0.9.973. Neither listing establishes publication or acceptance of the current 0.9.978 same-identity BookshelfNG replacement bridge. The local 0.9.978 package passed, but that bridge artifact has not been published. The recorded full Windows VM No/Yes run used predecessor 0.4.21.70; repeat the full handoff against current public 0.4.21.76 and the exact 0.9.978 bridge on approved private Windows JIT. The release-distribution workflow is configured to exercise the latest public BookshelfNG package's Yes path and a Manual defer, but it has not run on private Windows JIT. [BookshelfNG package](https://community.chocolatey.org/packages/bookshelfng) · [ChaptarrNG package](https://community.chocolatey.org/packages/chaptarrng)
  The current local `release-distribution.yml` change adds a real Chocolatey `MigrationMode=manual` update attempt before the automatic handoff and checks that the service, database, data canary, service executable, and staging/handoff paths remain unchanged. It also requires the package's exact manual-cancel diagnostic, so an unrelated nonzero Chocolatey failure cannot count as a successful defer. This is not an interactive No response; the separate PowerShell regression checks the prompt's No/Yes choices. The database hash is sampled while the service is running, so ordinary service writes could fail the check; the fresh package VM has no seeded user tasks or data. Neither change has run on private Windows JIT yet.
- The AUR update candidate is local and unpublished.

At 2026-10-26 14:32 UTC, the read-only GitHub Actions configuration check
returned no repository variables. The release workflows require
`CHAPTARRNG_MIGRATION_BRIDGE_ENABLED`, `CHAPTARRNG_RELEASE_READY`, and
`BOOKSHELFNG_RELEASE_READY`; the Launchpad publisher additionally requires
`LAUNCHPAD_PPA`, `LAUNCHPAD_SERIES`, and the `GPG_PRIVATE_KEY` secret. None of
those were configured in the observed repository state, so legacy publication
gates cannot be enabled from the current configuration.

- Docker alias/image delivery, Helm OCI chart delivery, AppImage/Flatpak publication, and external catalog updates remain gated.
- StartOS passed synthetic x86_64 and aarch64 VM handoffs, including Manual, Automatic, health, and automatic source rollback. Its official signed package, public `.71` predecessor update, external repository, and package-manager rollback remain open. Cloudron's local installed platform path passed, but candidate/feed publication remains open and the `.4.21` pre-update backup excludes persistent directories. Co-op Cloud passed local Swarm update, defer, failure-pause, full-volume restore, official Backupbot II CLI backup/restore, and Abra CLI backup/restore orchestration; canonical recipe review, published digest, and maintainer acceptance remain open. Unraid and external catalog platform handoffs still need their own update and rollback tests.
- The Windows standalone installer passed hosted Yes, No-at-preview defer, injected-failure rollback, explicit restore, service cleanup, and selected-data-directory checks. The old BookshelfNG updater cannot be redirected from this repository.
- macOS legacy installs need a one-time manual bootstrap; hosted x64 and arm64 runtime handoff acceptance now passes. The old BookshelfNG updater cannot be redirected from this repository. FreeBSD installed No/Yes/rollback acceptance is complete, but the one-time launcher/service path switch remains an operator action.
- Representative operator data and production provider/API workflows are outside this channel-package campaign.

Do not mark the global channel gate complete or publish these candidates until the blocked delivery and platform checks above are closed or the supported-channel scope is explicitly changed.

## Private JIT RPM failure diagnosis — 2026-10-26 14:47 UTC

Read-only inspection of the failed Linux x64 job in [run 40313259957](https://github.com/snapetech-org/chaptarrng/actions/runs/40313259957) confirmed that the runner was in the private `ng-shared-main-jit` group. The job reached the Fedora RPM bridge harness but failed before `rpmbuild` or the package transaction: `dnf install` could not fetch Fedora 44 updates metadata because the configured proxy returned HTTP 403 for the Fedora metalink. The Debian bridge and AUR choice steps had already passed in that job. This is a blocked dependency-source setup, not evidence of an RPM handoff regression or pass. A qualifying rerun needs an approved Fedora mirror or dependency cache reachable from private JIT; do not bypass the configured egress policy. The run remains incomplete and was not retried.

## Private JIT Helm handoff result — 2026-10-26 15:53 UTC

The existing Helm handoff run `40343577463` completed on the approved private `ng-shared-main-jit` group, using run-scoped labels `ng-shared-main-chaptarrng` and `ng-main-run-40343577463` on runner `gha-kspld0-40343577463-121644429659-809914`. Checkout, BookshelfNG history fetch, pinned-revision verification, Helm 3.20 setup, and chart lint passed. The run then failed in `ruby/setup-ruby` before `scripts/test_bookshelfng_helm_handoff.rb` could execute: the action received `EACCES: permission denied, mkdir '/opt/hostedtoolcache'`. The [official self-hosted runner requirements](https://github.com/ruby/setup-ruby#using-self-hosted-runners) require that default tool-cache path to be writable by the runner account. The chart behavior assertions therefore remain unverified on private JIT. Correct the ephemeral runner image/tool-cache permissions through the approved runner-owner handoff, then validate this job once; do not retry or change shared runner configuration before C0 clearance.

At the same recheck, ChaptarrNG Build `40343824614` and Release `40343577857` remained queued at `35889859fd96e2ffb2710ad95b84bd1d07034a03`; Packer PR #11 run `40351385087` remained queued at `852347d9cd246520b7163c2909589dfaa26a57a7`. No C0/slot-owner handoff was visible. Existing queue work and the Packer image build remain untouched.

## Private release gate and live queue recheck — 2026-10-26 16:10 UTC

Release run `40343577857` completed on private JIT and failed its release-readiness preflight because both `CHAPTARRNG_RELEASE_READY` and `BOOKSHELFNG_RELEASE_READY` were unset. Validation, package-build, release, and publish jobs were skipped; the run published nothing. Keep those gates closed until channel acceptance is complete.

The exact-head ChaptarrNG Build run `40343824614` remains queued at `35889859fd96e2ffb2710ad95b84bd1d07034a03`: Linux x64, Windows x64, Intel macOS, PostgreSQL migrations, and all three Docker matrix jobs are still queued. Its release-note contract job was skipped. BookshelfNG release-note run `40349225353` remains queued at `a41e25f92c6c24940973446faf6f671d427100e7`; trusted Packer PR #11 dispatch run `40351385087` remains queued at `852347d9cd246520b7163c2909589dfaa26a57a7`. No owner-thread update or C0/slot-owner handoff was present at this recheck. Preserve all queued work; no new CI or publication run was started.

## Channel and private JIT recheck — 2026-10-26 16:56 UTC

The current public Chocolatey predecessor is approved BookshelfNG `0.4.21.76`; its package still installs the BookshelfNG `Readarr.exe` archive into the BookshelfNG application and data paths. The separate public ChaptarrNG package remains `0.9.973`. Neither listing is the unpublished `0.9.978` same-identity BookshelfNG bridge. The recorded full Chocolatey VM handoff used predecessor `0.4.21.70`; testing `.76` against the exact bridge on private Windows JIT remains open. The [public BookshelfNG package listing](https://community.chocolatey.org/packages/bookshelfng) was updated October 26, 2026.

The local Chocolatey `MigrationMode=manual` step now rejects unrelated command failures by requiring the package's exact cancellation diagnostic before it accepts the unchanged service, database, executable path, canary, and no-staging checks. This workflow edit is uncommitted and has not run on private Windows JIT. The separate prompt regression has not run on private Windows JIT either.

At 16:56 UTC, GitHub still showed ChaptarrNG Build run [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) queued across its Linux, Windows, macOS, Docker, and PostgreSQL jobs. BookshelfNG release-note run [40349225353](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225353) remained queued. Release run [40343577857](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577857) had failed because `CHAPTARRNG_RELEASE_READY` and `BOOKSHELFNG_RELEASE_READY` were empty; all publication jobs were skipped, and nothing was published. Helm run [40343577463](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577463) failed at `ruby/setup-ruby` because the runner could not create `/opt/hostedtoolcache`, before the chart acceptance script ran.

Packer PR #11 merged at `881185c7587b776af3953237aafdd8e6502919ff` at 16:44 UTC, but its private-JIT dispatcher run `40351385087` remains queued. The merge is not a private-JIT validation or broker-deployment result. Packer PR #9 remains open and draft without a C0/slot-owner drain handoff. Its latest owner-PR run `40362638778` has a successful Packer validation job but its trusted-owner report job remains queued; the Windows channel canary has not passed. The 16:54 UTC operational snapshot reported two JIT guests active or starting; preserve both and leave the broker running.

Release readiness and migration-bridge gates remain closed. StartOS signed-package, repository and rollback acceptance; Cloudron and Unraid delivery; the canonical Co-op Cloud recipe; package-feed and catalog handoffs; and the remaining private-JIT replays are still open. Do not mark global channel acceptance complete.

The worktree is based on ChaptarrNG `main` at `ea225f3aa90a3a1216408cac536f5499993c589b`, which requires private-JIT validation without hosted fallback. The four task-file edits are still uncommitted and have not run on private JIT.

## Shared JIT queue update — 2026-10-26 17:02 UTC

BookshelfNG main CI run [40349225827](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225827) has a private-JIT job running on `kspls0`; its Chocolatey verifier `121664388901` and release-note, CodeQL, Semgrep, and Trivy jobs remain queued. This is not the ChaptarrNG private Windows `.76` bridge handoff, which still has no passing run.

The broker snapshot at 17:00:48 UTC reported four active guest reservations, 18 eligible jobs, zero free slots, and zero hosted recoveries; the unchanged memory reserve is blocking admission. Preserve the active jobs and queue. The Packer PR #11 cache fix is merged, but its exact private-JIT run `40351385087`, current-main validation `40364071163`, and PR owner validation `40364482118` remain queued. No broker deployment or Chocolatey `.76` handoff is established.

## BookshelfNG release-build policy failure and guard — 2026-10-26 17:09 UTC

BookshelfNG CI run [40349225827](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225827) completed with its `build` job failed in `Test private JIT runner policy`. The private-JIT job ran 12 policy tests and reported four failures: `validate`, `build-platform-runtime-packs`, `build-release-assets`, and `build-linux-packages` did not each include the required `inputs.tag != ''` condition. Release build and publication jobs were skipped. At the 17:09 GitHub API recheck, the same run's Chocolatey verifier `121664388901` and release-note run [40349225353](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225353) were still queued.

The correction is pushed to BookshelfNG branch `codex/release-build-empty-tag-guard-20261026` at `91d0115210579f403c82d600cb2047d22df40429`. It adds the empty-tag guard to all four release-build jobs. The branch has no status checks and has not been validated on private JIT; no PR or workflow dispatch was created. The four ChaptarrNG task-file changes were previously pushed to `codex/channel-acceptance-20261026` at `a1ecd427c419569b72679ac433b0bbb6550b5738`; that commit has no status checks or private-JIT validation. This 17:09 report refresh is being pushed as a follow-up commit. No tests, scans, publications, or shared-runner changes were started by this update.

## Final queue snapshot — 2026-10-26 17:11 UTC

The latest read-only GitHub status check still shows BookshelfNG Chocolatey verifier `121664388901` and release-note run [40349225353](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225353) queued. ChaptarrNG Build [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) still has all seven platform/migration jobs queued; Release [40343577857](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577857) failed its readiness preflight with every release/publish job skipped; Helm [40343577463](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577463) failed on the private runner's `/opt/hostedtoolcache` permissions before the acceptance script.

Packer PR #11 dispatcher run [40351385087](https://github.com/snapetech-org/packer/actions/runs/40351385087), current-main validation [40364071163](https://github.com/snapetech-org/packer/actions/runs/40364071163), and owner validation [40364482118](https://github.com/snapetech-org/packer/actions/runs/40364482118) remain queued. The cache fix is merged but is not validated or deployed. No C0/slot-owner handoff was available, so no run was dispatched or retried. The 65-repository/407-workflow audit was not repeated; private-route coverage remains incomplete. Neither task branch has private-JIT validation, and no tests, scans, publications, or shared-runner changes were started by the final recheck.

## Private-JIT queue monitor — 2026-10-26 17:24 UTC

ChaptarrNG Build [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) completed private-JIT job `postgres-migrations` (`121645321563`) successfully, including its build and fresh PostgreSQL migration smoke. Its other six platform and image jobs remain queued.

BookshelfNG PR [#213](https://github.com/snapetech-org/bookshelfng/pull/213), head `f8c06db28e1a314b0313e20d8460c49f10e1b19e`, is the active fix for the BookshelfNG policy-test failure. It tests the existing tag/readiness/ancestry guards and dependency chain instead of changing release workflow behavior, and fixes the fork workflow YAML/pin. Its trusted-owner dispatcher, CodeQL, Semgrep, Trivy, package-sync, and dependency-review jobs remain queued. My separate branch `codex/release-build-empty-tag-guard-20261026` adds workflow conditions and is an unvalidated alternative; PR #213 supersedes it, so do not merge that branch.

Packer main validation run [40364071163](https://github.com/snapetech-org/packer/actions/runs/40364071163) completed successfully, including shared-runner broker policy tests, ShellCheck, unattended XML validation, and Packer format. Packer PR #11 dispatcher [40351385087](https://github.com/snapetech-org/packer/actions/runs/40351385087) and owner validation [40364482118](https://github.com/snapetech-org/packer/actions/runs/40364482118) remain queued. The 17:17:46 broker snapshot reported three active guests and no spare slots; no newer host capacity or broker deployment readback was made. No C0/slot-owner handoff was found. No job was dispatched, canceled, or retried during monitoring.

## Release-note failure and queue recheck — 2026-10-26 17:27 UTC

BookshelfNG release-note run [40349225353](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225353) has now failed. Its diagnostic identifies missing required front matter in `release-notes/2026-10-26-public-fork-validation-jit.md`: `audience`, `area`, `action`, and `breaking`. The same check for PR [#213](https://github.com/snapetech-org/bookshelfng/pull/213) remains queued, as do that PR's trusted-owner dispatcher, CodeQL, Semgrep, Trivy, package-sync, and dependency-review jobs. I left the PR head unchanged while those checks are queued; its release-note fragment will need valid metadata before it can pass.

The BookshelfNG Chocolatey verifier `121664388901` remains queued. Packer main validation run `40364071163` and owner project validation `40364482118/121718395113` passed, but the Packer PR #11 dispatcher `40351385087/121672057828` and owner report `40364482118/121732498345` remain queued. ChaptarrNG's PostgreSQL migrations job passed; its other six Build jobs remain queued. No newer broker capacity readback or C0/slot-owner handoff is available. No job was dispatched, canceled, or retried.

## Packer owner-validation result — 2026-10-26 17:29 UTC

Packer owner-validation run [40364482118](https://github.com/snapetech-org/packer/actions/runs/40364482118) has now completed successfully: project validation job `121718395113` and its owner-validation report `121732498345` both passed. This does not clear the separate PR #11 dispatcher job `40351385087/121672057828`, which remains queued, or establish a broker deployment. The ChaptarrNG migration smoke is still the only completed job in its Build run; the other six remain queued. BookshelfNG PR #213 checks and the Chocolatey verifier remain queued, and the malformed release-note metadata failure is unresolved.

## Channel and private-JIT recheck — 2026-10-26 17:41 UTC

BookshelfNG `v0.4.21.76` is the final published GitHub release, with 38 assets; tag CI `40252457919` passed. The follow-on channel workflow `40253426242` failed at `gh release upload` with `fatal: not a git repository`, so it did not publish package channels or announce the release. AUR still lists `bookshelfng-bin` `0.4.21.71-1`; GHCR has the `.76` Softcover and Hardcover tags. The existing release-publication workflow is hosted-first and cannot be retried under the private-JIT-only policy. Do not run its manual retry until channel state and duplicate side effects are checked. No new stable tag is authorized while the release freeze remains in place.

The private Chocolatey `.76` handoff is still open. The local manual-defer verifier now requires the exact package cancellation diagnostic and compares stable fingerprints for `readarr.db`, its WAL, and rollback journal; the canary contents; service PID, identity, and start mode; and NSSM application, parameters, and directory. This is a source-level improvement only: it has not run on private Windows JIT, and the `.70` VM No/Yes rehearsal does not accept `.76`.

ChaptarrNG Build [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) has PostgreSQL migration job `121645321563` passed; its six platform/image jobs remain queued. Older archive run [40140157996](https://github.com/snapetech-org/chaptarrng/actions/runs/40140157996) is still in progress with `linux-x86` job `121248859010` failed during the second artifact-download step and `osx-arm64` job `121248859037` failed while building the archive; seven archive jobs remain queued. GitHub has not exposed logs while the run is still in progress, so the failure causes are not established. No retry was started.

BookshelfNG main run [40349225827](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225827) still has its policy-test build failed and Chocolatey verifier `121664388901` queued. Semgrep run [40349246909](https://github.com/snapetech-org/bookshelfng/actions/runs/40349246909) passed. Release-note run [40349225353](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225353) failed for missing `audience`, `area`, `action`, and `breaking` front matter. PR [#213](https://github.com/snapetech-org/bookshelfng/pull/213) remains at its existing head; its trusted-owner dispatcher `40366867930/121726843703` and release-note check `40366873759/121726858961` are queued.

Packer main validation [40365850712](https://github.com/snapetech-org/packer/actions/runs/40365850712) passed. Packer PR #9 run `40365815504` dispatched successfully but skipped its downstream validation and report; an earlier owner-validation run `40359521085/121717299050` passed. This is not a C0/slot-owner handoff. PR #11 dispatcher `40351385087/121672057828` remains queued; no broker deployment is established.

At 17:40:31 UTC the broker reported four active jobs, 31 eligible, and zero hosted recoveries. It had automatically dispatched Packer owner job `40370450408/121739449473` to kspld0 and ChaptarrNG archive job `40140157996/121248858975` to kspls0. The unchanged disk and memory admission guards remain active. No C0/slot-owner handoff arrived; preserve active guests and queued work, and do not restart or reconfigure the broker.

## Queue progress — 2026-10-26 17:47 UTC

ChaptarrNG archive run [40129504541](https://github.com/snapetech-org/chaptarrng/actions/runs/40129504541) is still in progress. `linux-x86` (`121246293816`), `freebsd-x64` (`121246293851`), `win-x86` (`121246293867`), and `win-x64` (`121246293944`) have failed; `osx-arm64` (`121246293841`) and `linux-arm64` (`121246293925`) passed; four other RIDs remain queued. Run [40140157996](https://github.com/snapetech-org/chaptarrng/actions/runs/40140157996) is also still in progress with `linux-x86` and `osx-arm64` failed and its other seven archive jobs queued. GitHub still withholds logs until each run completes. Do not retry either run; the failure causes are not yet established.

BookshelfNG CodeQL run `40349225947` and PR #213 dispatcher `40366867930/121726843703` remain queued; the main Chocolatey verifier `121664388901` remains queued. Packer owner-report job `40368692672/121737139070` passed, but PR #11 dispatcher `40351385087/121672057828` remains queued. PR #9's latest dispatcher run `40370613555` completed with downstream validation/report skipped; its owner thread has no response after 15:10 UTC.

At 17:46:51 UTC the broker reported four active jobs, 33 eligible, and zero hosted recoveries, with three active reservations on kspld0 and one on kspls0. Disk and memory reserve guards blocked additional admission. Runner group 11 had zero runners. No C0/slot-owner handoff arrived; no runner or broker changes, job retries, or manual dispatches were made.

## Private-JIT failure diagnosis and source fixes — 2026-10-26 17:57 UTC

Correction to the 17:47 note: the aggregate workflows are still open, but the MCP job-log endpoint returned logs for their completed jobs. The causes are now established from those logs; no retry was started.

In [archive run 40129504541](https://github.com/snapetech-org/chaptarrng/actions/runs/40129504541), Linux x86 and FreeBSD failed because `chaptarrng-platform-runtime-packs` had expired before the queued consumers ran. Its producer succeeded on October 24, while the failed consumers ran on October 26; the upload used one-day retention. The Windows x86 and x64 jobs failed because `build-standalone.sh` called `zip`, which is absent on the private Linux JIT image. The repository now retains dependent workflow artifacts for seven days and builds Windows ZIP files with Python's standard-library `zipfile` module. These source changes have not yet been validated by a private-JIT run.

[Archive run 40140157996](https://github.com/snapetech-org/chaptarrng/actions/runs/40140157996) used stale head `151a75228dd1f77644bd93ba06f4826612a3a233`, 64 commits behind current `main` (`ea225f3aa90a3a1216408cac536f5499993c589b`). Its Linux x86 job hit the same expired runtime-pack artifact. Its macOS ARM and Windows x64 jobs failed to compile because `DownloadService.cs` could not resolve `BookMediaType`; current `main` includes the `NzbDrone.Core.Books` import added by commit `6845361f6a98e39a639cb0d91e13dbfc3a4c28c5`. Treat those compile failures as stale-revision evidence, not a current-head regression.

Run 40129504541 now has three archive jobs passed, four failed, and three queued. Build run [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) has its PostgreSQL migration job passed and six platform/image jobs queued. Do not retry either run; the corrected source still needs an exact-head private-JIT validation after normal broker admission.

The Chocolatey stable-fingerprint helper now retries bounded transient I/O, sharing, and file-disappearance errors, then fails closed if it cannot obtain a stable snapshot. Its public `.76` package handoff remains unaccepted on private Windows JIT; this is an unvalidated source change, and the earlier `.70` VM rehearsal does not close that gate.

At 17:56:25 UTC the broker reported three active jobs, 19 eligible, and zero hosted recoveries. It had three active reservations on kspld0; disk reserve blocked further admission there, while kspls0 admission was blocked by CPU pressure. The latest runner-group read showed two online runners in group 10 and none in Windows group 11. The queued build, package, and release checks remain open; no C0/slot-owner handoff arrived, and no job was manually dispatched, canceled, or retried.

## Exact Chocolatey .76 candidate acceptance path — 2026-10-26 18:54 UTC

The private Windows candidate check now installs the exact public BookshelfNG `0.4.21.76` package, seeds the migration-158 schema and populated author, book, audio/ebook edition, file, path, and setting canaries, then tests `MigrationMode=manual`. The defer assertion compares logical SQLite fields and verifies the same running service process/account/start mode, NSSM executable/parameters/working directory, executable hash, source database, and absence of staging or handoff files. It no longer treats ordinary live SQLite byte changes as migration damage.

The same check then upgrades that installation automatically from the exact candidate Chocolatey package and checksum-pinned Windows archive. It verifies the migrated records, SQLite integrity and foreign keys, retained BookshelfNG source backup, BookshelfNG service identity and selected data path, candidate executable configuration and running child process, `/ping`, and data/backup survival after package uninstall. The fixture runs as its self-contained Windows executable. The private archive override is accepted only when its SHA-256 matches the checksum embedded in that exact package; normal installs still download and verify the release asset.

The check is wired into the Windows `workflow_dispatch` Build path on the private `packer-windows` route; setting `windows_chocolatey_only=true` runs only that Windows matrix entry and skips Linux, macOS, PostgreSQL, and Docker jobs. The gated release-distribution verifier reuses the same helper. Failure state is preserved for review; a runner with retained `ProgramData\BookshelfNG` data must be reviewed before reuse. This is source-level coverage only; no exact `.76` Windows run has passed. The prior `.70` VM rehearsal remains the only full Chocolatey No/Yes evidence. The new check does not yet inject an automatic-upgrade failure to prove transaction rollback from `.76`; bridge feed rejection, publication, and PostgreSQL installed acceptance also remain open.

At 18:52 UTC, ChaptarrNG Build run [40379070022](https://github.com/snapetech-org/chaptarrng/actions/runs/40379070022) was still queued at older source head `caca0b0e43925caa4eccf57e32814bcfb69f42a1`; none of its jobs can validate this change. The Packer PR #20 trusted-owner dispatcher passed, but its downstream project and Windows-canary jobs were skipped; that is not a Windows capacity clearance. BookshelfNG recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remains pending. No new build, publication, retry, cancellation, runner change, or broker restart was made. Exact-head private Windows evidence remains queued behind the old build and capacity confirmation.

## Live queue check — 2026-10-26 18:58 UTC

At 18:58:38 UTC, the broker had five active JIT guests across kspld0 and kspls0, 14 eligible jobs, zero hosted recoveries, and no spare slots. No C0 or slot-owner drain handoff was present. The existing ChaptarrNG Build run [40379070022](https://github.com/snapetech-org/chaptarrng/actions/runs/40379070022) remains queued on the older `caca0b0e` head; it cannot validate the current working tree. The active main Build [40343824614](https://github.com/snapetech-org/chaptarrng/actions/runs/40343824614) still has two Docker jobs in progress and several platform jobs queued. The BookshelfNG `.76` verifier [40349225827](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225827) is queued without a runner, and recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remains pending without labels or a runner. No additional validation or recovery run was dispatched.

At 19:00 UTC, the broker started Packer PR #20 exact-head validation run [40381632290](https://github.com/snapetech-org/packer/actions/runs/40381632290) on kspld0. This validates the separate broker change only; it is not ChaptarrNG Windows evidence. Preserve it and the other JIT guests. The ChaptarrNG `.76` candidate remains unvalidated and queued.

## Exact-head Windows validation queued — 2026-10-26 19:04 UTC

Committed the private Windows-only Build mode and dispatched [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) on exact head `d59c3709f67dcebd94dfd4ca01288dd90d55899a` with `windows_chocolatey_only=true`. Its only runnable job, `build (windows-x64)` (`121785472700`), is queued; PostgreSQL, Docker, and release-note jobs are skipped. This is not a test pass. The organization runner inventory has no online Windows runner at the time of dispatch. The older full Build [40379070022](https://github.com/snapetech-org/chaptarrng/actions/runs/40379070022) is still queued at `caca0b0e` and does not cover the new source.

Packer PR #20's exact-head validation [40381632290](https://github.com/snapetech-org/packer/actions/runs/40381632290) failed its broker policy test at 19:00:43 UTC; its author pushed correction `91f1ab3c`, and the new trusted dispatcher [40383172380](https://github.com/snapetech-org/packer/actions/runs/40383172380) is queued for private JIT with its downstream validation skipped so far. No C0/slot-owner handoff arrived. BookshelfNG `.76` Chocolatey job [40349225827](https://github.com/snapetech-org/bookshelfng/actions/runs/40349225827) remains queued, and release recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remains pending.

At 19:04:27 UTC, the private Windows route still had no online Windows runner, and the runbook's slot-owner handoff had not arrived. The exact-head Windows job remains queued with no steps; it has not started. Do not treat queue acceptance as migration evidence. The older BookshelfNG CodeQL run [40344623797](https://github.com/snapetech-org/bookshelfng/actions/runs/40344623797) was broker-attempted but also remains queued without a runner.

## Chocolatey rollback leg added — 2026-10-26 19:12 UTC

The Chocolatey `.76` helper now attempts a checksum-pinned candidate upgrade with a controlled failure injected only after ChaptarrNG has completed the BookshelfNG migration and passed its health/readiness checks. The verifier requires the exact injected diagnostic and a failed package update, then checks that Chocolatey still records public BookshelfNG `.76`, the original service identity/settings/executable are restored, the schema-158 `readarr.db` passes SQLite integrity and foreign-key checks with its original logical canaries, `/ping` is healthy, and the failed `chaptarr.db` target is retained for diagnosis. It then continues with the normal successful candidate upgrade and existing uninstall/data-retention checks.

The package template only accepts this failure injection with the private test-archive override, whose SHA-256 must match the exact candidate package. This adds deterministic source-level rollback coverage; it is not rollback acceptance evidence until the full helper passes on private Windows JIT. The smoke executable now has a source-only verifier for schema 158, the populated fixture canary, SQLite integrity, and foreign keys. The channel contract test and packaging README cover the new leg.

`git diff --check` passed. No local test, build, or scan ran; repository policy requires those checks on approved private JIT. Build run [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) remains queued at the earlier exact head `d59c3709f67dcebd94dfd4ca01288dd90d55899a` and therefore does not include this rollback addition. At 19:12 UTC, the organization runner API still showed no online Windows runner. Keep the existing queued check and in-flight JIT jobs intact; do not claim `.76` acceptance.

## Exact-head validation status after rollback update — 2026-10-26 19:17 UTC

The pushed source head is `bd228c86a84135d58d653df494721b89285d4591`. No Build run exists for this head yet. Existing run [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) is still queued at `d59c3709f67dcebd94dfd4ca01288dd90d55899a`; its Windows x64 job has no runner, and all other jobs in that targeted run are skipped. The runner API still shows no online Windows runner. Keep that job intact; queue state is not acceptance evidence.

Packer PR #20's trusted dispatcher completed and created validation run [40385069191](https://github.com/snapetech-org/packer/actions/runs/40385069191), but its Packer validation job is queued with no runner; its Windows canary is skipped. BookshelfNG channel recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remains pending with no runner or steps. No channel publication or migration acceptance occurred in this recheck.

## Chocolatey verifier path correction — 2026-10-26 19:32 UTC

Correction to the 19:12 rollback note: the single-source fixture does not create a retained `chaptarr.db` target. It seeds BookshelfNG at `readarr.db`; conversion replaces that file with the native Chaptarr schema in place. Chocolatey retains the failed candidate as `readarr.db.chaptarr-failed-<timestamp>` and restores the schema-158 backup to `readarr.db`. The separate multi-source merge path uses `chaptarr.db`.

Commit [`419ca23a498c34c5edb3de6cbfb90fde2d15d59c`](https://github.com/snapetech-org/chaptarrng/commit/419ca23a498c34c5edb3de6cbfb90fde2d15d59c) corrects the verifier: it identifies native Chaptarr databases by schema lineage across `chaptarr.db`, `readarr.db`, and `audioarr.db`; checks canaries, SQLite integrity, and foreign keys on the retained failed target; finds the BookshelfNG backup independent of its source filename; and verifies the restored migration after uninstall. `git diff --cached --check` passed before the commit. No local test, build, or scan ran; these checks must run on approved private JIT.

This is a source fix only. At the 19:30:50 UTC runner snapshot, there were 32 registered runners, six online, two busy, and no online `packer-windows` runner. Chocolatey run [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) remains queued at older SHA `d59c3709f67dcebd94dfd4ca01288dd90d55899a`, with its Windows job unassigned. No exact-head Windows execution exists for `419ca23a`. Keep `.76` handoff unaccepted until that run passes on private Windows JIT.

At the same snapshot, BookshelfNG recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remained pending with no labels, runner, or steps. Packer PR #20's trusted dispatcher passed but its downstream project validation and Windows canary were skipped. Run [40385069191](https://github.com/snapetech-org/packer/actions/runs/40385069191) is queued with workflow-source SHA `e53d977b37af09a23bdcd241028ac53411a2ae0e`; later display-title and workflow-source inspection confirmed its target is PR #20's exact head `91f1ab3cbe56ca99dbfd8d98406a45a0b46c6e9d`. Its validation job has not started. No C0/slot-owner handoff arrived. Preserve active guests and all queued work; do not infer a safe drain from mismatched broker, host, and runner counts.

## Rollback verifier sidecar filter — 2026-10-26 19:33 UTC

An independent source review found that the rollback glob could also select SQLite `-wal`, `-shm`, or `-journal` sidecars beside the retained failed database. Commit [`14b5ff09bbafeb2787d8901dce40bcbc2b42ab92`](https://github.com/snapetech-org/chaptarrng/commit/14b5ff09bbafeb2787d8901dce40bcbc2b42ab92) now accepts only the exact `readarr.db.chaptarr-failed-<UTC timestamp>` main-file shape before opening it for verification; the static channel contract asserts the timestamp filter. `git diff --cached --check` passed before the commit. No test/build/scan ran locally, and neither source commit has exact-head Windows execution yet.

## Public channel status snapshot (observed 19:30 UTC)

BookshelfNG's final release remains [`v0.4.21.76`](https://github.com/snapetech-org/bookshelfng/releases/tag/v0.4.21.76). Launchpad PPA and the public Chocolatey package are at `.76`; Chocolatey's public package checks pass, but the private Windows installed migration/rollback rehearsal is still not accepted. AUR is `0.4.21.71-1`, the two latest COPR builds (`11165173` and `11161314`) failed without recording a source version, and Helm OCI is still `0.4.21-71`. Recovery run [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) remains pending without steps; scheduled bridge run [40370734099](https://github.com/snapetech-org/bookshelfng/actions/runs/40370734099) remains queued without steps. The checked-in COPR helper accepts `.76` tags; the remaining COPR issue is project-side webhook/source configuration.

ChaptarrNG's latest public release is [`v0.9.977`](https://github.com/snapetech-org/chaptarrng/releases/tag/v0.9.977). GHCR, AUR, and COPR are at `.977`; Chocolatey is at `.973`. No `.978` tag or release exists, and release run [40343577857](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577857) failed before publication. The ChaptarrNG release and migration-bridge variables are absent; do not treat the bridge as eligible until the external acceptance gates are satisfied. The targeted Windows candidate run remains queued at the older SHA, and no exact-head test exists for the verifier fixes. No channel publishing, release retry, or Discord announcement occurred in this recheck.

The 19:23 blocker review also found no new StartOS package/repository, Cloudron, Unraid delivery, or canonical Co-op Cloud recipe acceptance evidence. These remain open alongside the organization-wide workflow-route audit; no fresh all-repository workflow count was collected in this recheck.

## Private Windows route check — observed 2026-10-26 19:42 UTC

The organization runner group API shows `packer-windows` restricted to the Packer repository and only `packer/.github/workflows/ci.yml@refs/heads/main`; the group currently has zero registered runners. ChaptarrNG's Windows Build job requests the `packer-windows` label, but its workflow is not allowlisted in that group. The exact-head Chocolatey run [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) is still queued at `d59c3709f67dcebd94dfd4ca01288dd90d55899a`, so commits `419ca23a` and `14b5ff09` have no Windows execution. Keep this handoff unaccepted until the runner route is restored and the rollback plus normal upgrade legs pass.

## Channel and exact-head acceptance recheck — 2026-10-26 19:48 UTC

BookshelfNG bridge run [40370734099](https://github.com/snapetech-org/bookshelfng/actions/runs/40370734099) completed successfully at 19:43:47 UTC as a disabled-state check. It recorded `ENABLED=false`; Helm, COPR, Chocolatey, Docker Hub, and the other publisher jobs were skipped. Recovery run [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) is still pending since 18:00:26 UTC with no steps. The release follow-up [40388458725](https://github.com/snapetech-org/bookshelfng/actions/runs/40388458725) also completed with all jobs skipped. No package publication occurred.

The BookshelfNG release remains `v0.4.21.76`; public Chocolatey is Approved at `.76`, while AUR is `0.4.21.71-1`, the latest COPR build `11165173` failed without a source version, and Helm OCI remains `0.4.21-71`. ChaptarrNG remains at `v0.9.977`: GHCR, AUR, and COPR are `.977`, and Chocolatey is `.973`. There is no `.978` tag or release. No release or channel was published during this recheck.

ChaptarrNG Windows run [40383424925](https://github.com/snapetech-org/chaptarrng/actions/runs/40383424925) remains queued at older SHA `d59c3709f67dcebd94dfd4ca01288dd90d55899a`; the branch head is `cc6e4a0a8bdd7aaa0afc5c6feb12e9d2b13b1f1a` and has no workflow run. The rollback-verifier commits `419ca23a` and `14b5ff09` therefore have no exact-head Windows evidence. Runner group `packer-windows` still has no registered runners and only allows Packer's main CI workflow. `.76` installed handoff and rollback remain unaccepted.

## Correction: Packer PR #20 validation target

Run [40385069191](https://github.com/snapetech-org/packer/actions/runs/40385069191) is configured to validate PR #20's exact head `91f1ab3cbe56ca99dbfd8d98406a45a0b46c6e9d`. Its display title contains `owner-pr:20:<that SHA>:40383172380`, and the Packer workflow checks out the `inputs.pr_sha` value; `e53d977b` is the main-pinned workflow-source SHA. The validation job remains queued without a runner or steps, so no validation pass exists yet.

## BookshelfNG `.76` recovery queue update — 2026-10-26 20:00 UTC

Stale release run [40280771274](https://github.com/snapetech-org/bookshelfng/actions/runs/40280771274) is now cancelled. All eight jobs show no runner and zero steps, so it performed no validation or publication and caused no package side effects. The scoped recovery [40373866659](https://github.com/snapetech-org/bookshelfng/actions/runs/40373866659) has moved from `pending` to `queued`; its release-validation job `121751520187` now has the expected run-scoped JIT labels but still has no runner and zero steps. It has not started.

The `.76` feeds are unchanged: Chocolatey is Approved at `.76`, AUR remains `0.4.21.71-1`, COPR build `11165173` failed without a source version, and Helm OCI remains `0.4.21-71`. Keep the recovery queued and verify its execution before claiming channel acceptance.

## Private JIT cache repair and exact-head queue — 2026-10-26 20:14 UTC

Packer PR [#20](https://github.com/snapetech-org/packer/pull/20)'s corrected exact-head validation run `40385069191` completed on private JIT but failed in `test_queue_policy.py`: two pagination fixtures exhausted their mocked GitHub responses after the broker began querying queued, pending, and in-progress runs. The 158 broker-policy tests passed. The fixture correction is now pushed as `0cd3ead78702161e296a640bb9d631bb9da567e9`; its owner-PR dispatcher `40393523152/121820413677` remains queued, and no validation run for that exact head has started.

The earlier Helm handoff failure is now tied to a runner-image defect. Job `121644429659` in run `40343577463` failed before the handoff assertions with `EACCES: permission denied, mkdir '/opt/hostedtoolcache'`. Packer PR [#24](https://github.com/snapetech-org/packer/pull/24), commit `a22fd852194a92b31355b19821f96415be169295`, prepares that cache as runner-owned in disposable Linux JIT guests and adds a generated-cloud-init regression assertion. `git diff --check` passed; tests and scans were not run locally. The PR is draft; its trusted dispatcher `40393513364/121820380140` remains queued, so the fix is not validated or deployed. The exact-head private-JIT run must pass before merging, and deployment still requires the existing safe idle cutover.

At 20:13:19 UTC, the broker scan reported 4 active jobs and 14 eligible jobs, with no safe slots: kspld0 was closed by memory and disk reserves, and kspls0 by memory pressure. It continued preserving the queued work. The BookshelfNG `.76` channel recovery `40373866659/121751520187` and ChaptarrNG Windows handoff `40383424925/121785472700` remain queued without runners or steps; the current online-runner snapshot had no Windows runner. No manual dispatch, hosted workflow, shared runner change, broker restart, package publication, or announcement was made.

## Private-JIT and Fedora recheck — observed 2026-10-31 04:18 UTC

ChaptarrNG `main` is now `dceb5df7`. Commit `51f6ecd6` added the Packer broker's required manual `action`/`runner_pool` claim to all six ChaptarrNG workflow paths in its source allowlist; the Build owner-PR dispatcher forwards `ci/private`, and Helm is manual-dispatch-only. Commit `dceb5df7` fixes a separate Fedora harness mismatch: the RPM test had hardcoded `.977` while `Directory.Build.props` is `.978`. The harness now derives the three-part version from `AssemblyVersion`, uses it for the tarball, RPM spec, package lookup and assertions, and records it in the evidence directory. Static `git diff --check` passed; local tests and scans were not run.

No private-JIT run has executed exact head `dceb5df7`. The prior Fedora private-JIT run `40313259957` stopped before `rpmbuild` or package transaction because the Fedora metadata request returned HTTP 403 through the configured proxy. The version drift is fixed in source; Fedora acceptance still requires an approved reachable mirror or cache and a successful exact-head private-JIT run. No egress restriction was bypassed.

The latest available capacity snapshot remains 04:11 UTC: no Linux x64 slot with memory above the 16 GiB reserve and no Windows x64 image/TCG route; group 11 had one offline Windows ARM64 runner. The prior `.76` Chocolatey run `41166442007` is queued on `eec975d`, and Helm `41167175002` is queued on `2531f3ee`; neither can validate the current source. The Helm handoff uses an ephemeral Kind cluster on the JIT runner; no production or external cluster is involved.

Chocolatey `.76` installed migration/rollback, official signed StartOS package and repository delivery, Cloudron and Unraid feed/catalog delivery, canonical Co-op Cloud recipe acceptance, and other package-feed/catalog handoffs remain open. Release publication remains gated; no candidate or compatibility bridge was published. The last full organization inventory is still 65 repositories and 419 workflow files from 2026-10-26, so organization-wide private-JIT coverage is unproven and the replacement is not 100% accepted.

## Direct main consolidation and current acceptance — 2026-10-31 04:35 UTC

The BookshelfNG single-image handoff from branch `codex/chaptarrng-retirement-status-20261030-scratch` was integrated directly into `main`; GitHub closed PR #183 as integrated, and its remote branch was deleted. The remaining unique patches on the localization task branch were also cherry-picked directly onto `main` (`886e8a95`, `d07c0880`, `bd589312`). The exact source snapshot is `bd5893120c24ada8ea492edc1ce21cd5cdc24d8b`. The handoff detects SQLite and Readarr/Audioarr/Bookshelf PostgreSQL sources, carries saved ports, advertises both legacy `8787` and fresh-install `8789`, and fails closed unless migration provider mode is explicit. No image, package, alias, or channel was published.

`git diff --check origin/main..HEAD` passed before the earlier source push; the additional localization-only cherry-picks have not had a private-JIT run. The local pre-push hook also passed the release-readiness gate and single-image alias-contract script for the earlier source. These local checks are not private-JIT acceptance. No exact-head private-JIT build, migration, platform, or distribution job has passed for `bd589312`.

Current Actions evidence remains on older revisions: Build run [41166424095](https://github.com/snapetech-org/chaptarrng/actions/runs/41166424095) has only the runner-contract job queued and its platform jobs skipped; Windows run [41166442007](https://github.com/snapetech-org/chaptarrng/actions/runs/41166442007) has its single `windows-x64` job queued without a runner; Helm run [41167175002](https://github.com/snapetech-org/chaptarrng/actions/runs/41167175002) has one queued handoff job without a runner or steps. The earlier owner-PR dispatch [41167825847](https://github.com/snapetech-org/chaptarrng/actions/runs/41167825847) was cancelled on pre-integration head `f63796dd`. The Helm workflow would create a disposable Kind cluster after the job starts; it did not start, so no cluster was created and no production cluster was involved.

Chocolatey BookshelfNG `.76` has no accepted private Windows installed No/Yes run. The Fedora `.978` RPM harness now derives the app version from `Directory.Build.props`, but its previous private-JIT attempt failed at Fedora metadata access with HTTP 403 before package build or transaction; an approved reachable mirror/cache and an exact-head pass remain required. Official StartOS package/repository acceptance, Cloudron and Unraid delivery, the canonical Co-op Cloud recipe, remaining package-feed/catalog handoffs, representative operator data, and rollback acceptance remain open. The public ChaptarrNG release is still `.977`; release readiness variables remain unset, and there has been no candidate publication or cutover.

## Queue and capacity recheck — 2026-10-31 04:36 UTC

The connected GitHub API shows no open ChaptarrNG PRs. The latest observed Build run [41168115692](https://github.com/snapetech-org/chaptarrng/actions/runs/41168115692) is queued at older SHA `64689f1f`; Linux, macOS, and Windows jobs have no runner or steps. macOS handoff [41168181470](https://github.com/snapetech-org/chaptarrng/actions/runs/41168181470), standalone assets [41168142569](https://github.com/snapetech-org/chaptarrng/actions/runs/41168142569), and Helm [41168121048](https://github.com/snapetech-org/chaptarrng/actions/runs/41168121048) are also queued at older revisions without runners. The current source snapshot `bd589312` has no associated exact-head run. Chocolatey `.76` Windows-only run [41166442007](https://github.com/snapetech-org/chaptarrng/actions/runs/41166442007) remains queued at `eec975d`; it is not current evidence.

The broker is active with three jobs and 14 eligible queue entries, but the 04:34 detailed capacity scan found no free kspld0 slot above the memory reserve, no Windows x64 native-image/TCG route, and Windows ARM64 slots occupied. Free Mac/Linux ARM64 capacity does not satisfy the queued x64 Windows acceptance. No broker restart, runner-group change, or dispatch was performed. Packer PR #36 says the older C0 handoff is superseded but still requires zero active guests and zero busy runners before shared-access cutover; no current slot owner or confirmation of those zero-count conditions was visible. Release publication and every unaccepted channel remain blocked.

## Exact main-head check — 2026-10-31 04:39 UTC

The application source snapshot is `bd5893120c24ada8ea492edc1ce21cd5cdc24d8b`; `c82f168c` and `79cccb61` are later evidence-only commits. The GitHub combined-status API returned no checks for either docs-only head. The authenticated runner and Actions interfaces available to this session do not expose a workflow-dispatch operation, and the shell GitHub CLI credential is expired/rate-limited. No job was fabricated or sent to a hosted runner. The latest queued Windows `.76` job remains on older source and cannot substitute for this head.

## Existing run progress — 2026-10-31 04:41 UTC

The broker automatically started standalone-assets run [41168142569](https://github.com/snapetech-org/chaptarrng/actions/runs/41168142569) at 04:38:25 UTC on stale SHA `64689f1f`. Its `Build frontend once` job completed successfully on private runner `gha-kspld0-41168142569-124440069145-2cbf0e`; ten remaining platform/archive jobs are still queued. This is partial older-head frontend evidence only. The full Build run [41168115692](https://github.com/snapetech-org/chaptarrng/actions/runs/41168115692), Chocolatey Windows `.76` run [41166442007](https://github.com/snapetech-org/chaptarrng/actions/runs/41166442007), Helm run [41168121048](https://github.com/snapetech-org/chaptarrng/actions/runs/41168121048), and macOS handoff [41168181470](https://github.com/snapetech-org/chaptarrng/actions/runs/41168181470) remain queued on older heads. The current application source has no exact-head result.

## Consolidated source and acceptance recheck — 2026-10-31 18:28 UTC

Remote main advanced to `b1f10c72` during this work; its private-fork validation policy change is preserved in the integration. The consolidated source commit `7e538f5c` integrates the private-JIT release-routing branch, the Windows Python and localization-browser build fixes, missing core locale catalogs with machine-draft provenance, and SeerrNG-related fixes for automatic book-number search, same-recording audiobook imports, and updates with multiple monitored editions. It also rechecks BookshelfNG `/ping` after Chocolatey `MigrationMode=manual` and before starting automatic migration. The candidate has no exact-head private-JIT result.

Local verification passed: private-JIT workflow policy (17/17), package-channel contracts (23/23), localization behavior (12/12), locale catalog/provenance validation, PowerShell syntax, Ruby YAML parsing for the three release workflows and the public-fork workflow, and validation of all three new release-note fragments. The initial .NET SDK `10.0.403` project build completed with zero warnings and errors before the SeerrNG request fixes. After those code changes, a filtered `dotnet test` rebuilt the affected project and passed tracked-download, book-edition update, Newznab automatic fallback, and MyAnonaMouse automatic fallback tests (61/61). These local checks do not replace private-JIT validation or installed-channel acceptance.

The latest live Actions snapshot has no exact-head pass for main `b1f10c72` or the integration. ChaptarrNG Build [41168115692](https://github.com/snapetech-org/chaptarrng/actions/runs/41168115692) remains queued at old SHA `64689f1f`; its Linux image-alias contract, Windows Python lookup, and PostgreSQL localization-browser compile jobs failed, and macOS remains queued. The dedicated Chocolatey `.76` run [41166442007](https://github.com/snapetech-org/chaptarrng/actions/runs/41166442007) remains queued at `eec975d2`, so it has not executed the updated handoff. Standalone-assets [41168142569](https://github.com/snapetech-org/chaptarrng/actions/runs/41168142569) completed frontend and most archive jobs on the old SHA, but its Linux x86/FreeBSD runtime-pack job remains queued without a runner. PR #186's run [41223605079](https://github.com/snapetech-org/chaptarrng/actions/runs/41223605079) skipped all jobs; it is not validation evidence.

Helm handoff [41168121048](https://github.com/snapetech-org/chaptarrng/actions/runs/41168121048) ran on private JIT, passed checkout, chart lint, writable-cache preparation, the handoff contract, and the pinned `kind` download/checksum, then failed because the runner proxy returned HTTP 403 for the pinned `kubectl` download from `dl.k8s.io`. The installed `.76` Helm acceptance script did not run; this workflow creates only an ephemeral Kind cluster on its JIT runner, not a production cluster.

Packer owns the shared broker cutover. Its latest private run [41229001428](https://github.com/snapetech-org/packer/actions/runs/41229001428) failed two broker-policy tests and skipped the Windows canary; the new Packer source has not passed its exact-main validation. The broker is active but its latest scan found six active jobs, 22 eligible queue entries, and no free kspld0 slots. Four busy Linux guests serve other repositories; the Windows runner is also occupied, and the idle Mac runner lacks ChaptarrNG labels. No C0/slot-owner handoff was visible. Do not dispatch or retry until the broker owner confirms a compatible slot. The latest organization catalog snapshot is 65 repositories and 389 workflow files from 08:12 UTC; the 17:54 group readback reported 65 repositories/393 refs in group 10 and runtime hash `de29ac9`. These different snapshots do not establish complete route coverage.

BookshelfNG `.76` Chocolatey No/Yes/rollback acceptance, official StartOS package and repository acceptance, Cloudron and Unraid delivery, canonical Co-op Cloud recipe acceptance, remaining package-feed/catalog handoffs, representative operator-data conversion, live-provider behavior, installed rollback, exact-head private-JIT validation, release publication, and operator cutover remain open. ChaptarrNG `v0.9.977` is still the latest public release; `.978` and its compatibility candidates remain unpublished. Keep BookshelfNG running and release gates closed.

## Current source and acceptance recheck — 2026-10-31 18:56 UTC

At the time of this 18:56 UTC snapshot, direct-to-main consolidation had
`main` at `333238f270c982a69941bb34fcecf5bb68a1623d`, no open pull requests,
and only `main` in the remote branch inventory. PRs #184, #185, and #186 were
merged. No ChaptarrNG application source changed after
`c59488f17040ef685b703f096cfb8c44cdd58522`; the later commits update
acceptance workflows and tests.

Two observed CI failures have narrow corrections on main:

- Commit `5c9db5ac` corrects the Debian release-gate contract test. The release
  workflow deliberately requires a private main-branch dispatch and exact tag
  ancestry; the old test incorrectly required automatic tag-push release. Its
  preflight-range assertion also included the later release job. The corrected
  test now passes locally and is included in the private-JIT channel-contract
  job.
- Commit `333238f2` sets `HARDCOVER=false` for historical PostgreSQL fixture
  cases that seed Hardcover credentials but no native Hardcover provider IDs.
  This supplies the explicit source mode required by the fail-closed importer.
  On a task-isolated PostgreSQL 16 instance and .NET SDK 10.0.403, all 8
  `BookshelfPostgresDatabaseConverterFixture` cases passed, including schema
  versions 1, 13, 22, and 33 with preview, conversion, and restoration. The
  full evidence note is retained at
  `/mnt/disks/scratch/task-worktrees/chaptarrng-finalize-20261031/evidence/postgres-fixture-local-20261031.md`.

Local verification on the corrected checkout passed: Debian release gate,
package-channel contracts (23/23), private runner policy (17/17), release
readiness gate, publisher preflight, stable-version checks (8/8), PPA gate,
Ruby YAML parsing, `git diff --check`, and the Semgrep hooks (0 findings).
These checks do not substitute for exact-head private-JIT or installed-channel
acceptance.

Private contract coverage now has one exact private-JIT pass: run
[41230976187](https://github.com/snapetech-org/chaptarrng/actions/runs/41230976187)
completed at 18:56:40 UTC on `ng-shared-main-jit` runner
`gha-kspld0-41230976187-124624942319-63ba83`, head `5c9db5ac`. The 23 legacy
distribution contracts, corrected Debian release-gate check, and 17 private
runner-policy tests all passed. This is the contract-only job; it is one commit
behind current main and does not cover a full platform build or installed
migration.

Build [41229894913] is at older head `c59488f1`; its Linux package-contract step
failed on the now-corrected stale assertion, and its PostgreSQL migration job
failed on the now-corrected fixture mode. Docker, Intel macOS, and Windows jobs
remain queued without a runner. Standalone BookshelfNG `.76` macOS handoff run
[41230571219] is queued at `4d02c9db` on both architectures; neither job has
started. There is no full Build result at current main `333238f2`.

At the 18:49 UTC read-only runner snapshot, no compatible ChaptarrNG Linux,
Windows, or macOS runner was online. Packer main `f80578c6` passed its private
source-validation run [41229730083], but the active broker still runs an older
binary; this does not establish broker deployment or route capacity. No C0 or
slot-owner clearance was visible. Do not use hosted runners or widen runner
access to bypass the missing capacity. The organization-wide private-JIT
inventory remains incomplete.

Live channel checks still show public ChaptarrNG `.977`, approved Chocolatey
ChaptarrNG `.973`, and BookshelfNG `.76`; `.978` has no public release or tag.
No `.76` private-Windows No/defer/Yes/rollback run has passed. Official signed
StartOS repository/package acceptance, Cloudron and Unraid delivery, canonical
Co-op Cloud recipe acceptance, Helm OCI handoff, and package-feed/catalog
handoffs remain open. Earlier Helm attempts used only an ephemeral Kind cluster
on the JIT runner; no production cluster was deployed. Representative operator
data, live providers, installed recovery, rollback, external delivery, and
operator acceptance also remain unproven. Release publication and BookshelfNG
cutover remain frozen.

## Current source and acceptance recheck — 2026-10-31 19:08 UTC

`main` and `origin/main` are at `8ade12c2fffda731605bb6b509419ad4f3eaa8dc`.
The committed twelve-locale semantic catalog correction was cherry-picked to
main as `fd7c0eba` and pushed before the two subsequent owner commits that pin
the Chaptarr scans and macOS jobs to private JIT. No ChaptarrNG migration or
packaging implementation changed after `c59488f1`; the intervening ChaptarrNG
source change is localization catalog and audit data. No open pull requests
remain.

At `fd7c0eba`, the localization catalog/provenance checker passed for 37 UI
locales, 2,950 source IDs, and 52 core catalogs. The focused localization suite
passed 16/16 tests. The remote main fast-forward also applied the user's
commits `9c70c242` and `8ade12c2`; this acceptance work did not edit their
workflow changes.

Private Windows Build run
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
is still running at older head `c59488f1`. Its Windows job is on the private
Packer runner, but as of 19:08 UTC it was still preparing Node/Yarn; the
Chocolatey `.76` handoff step had not started. This is not a pass. The job's
result will provide installed handoff evidence for its recorded candidate
head, not exact-`8ade12c2` source provenance. No exact-current-main platform
Build run is recorded, and no new run was dispatched.

There are no open pull requests. The committed UI catalog correction remains
on main at `fd7c0eba`. A separate remote branch,
`codex/chaptarr-server-l10n-20261031`, is three commits ahead of main, and its
scratch worktree contains an uncommitted expansion (16 tracked files and three
new files; no workflow files). The current worktree's standard UI catalog
check passes for 37 locales, 4,015 source IDs, and 52 catalogs. Its server
catalog check still reports 5,760 untranslated entries (5,712 server messages
and 48 notification failures); the provider/source coverage check reports
7,068 missing translations across provider fields/options, notifications, and
loading messages. The expansion is not ready for main or release acceptance.

Live release checks still show public ChaptarrNG `.977`, BookshelfNG `.76`, and
no public `.978` release. The `.76` private-Windows handoff, exact-head private
JIT platform validation, official StartOS signing/repository delivery,
Cloudron and Unraid feed/catalog delivery, canonical Co-op Cloud maintainer
acceptance, package-feed/catalog handoffs, representative operator data,
live-provider acceptance, and operational sign-off remain open. The release
readiness gates stay closed.

## CI failure triage — 2026-10-31 19:18 UTC

The completed jobs in Build [41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
all target old head `c59488f1` and do not show new current-main product defects:

- PostgreSQL job `124621739312` passed 11/15 tests. Its four historical
  fixture failures all reported the missing explicit `HARDCOVER` provider
  mode. Commit `333238f2` corrected that setup; the matching local PostgreSQL
  fixture suite passed 8/8.
- Linux job `124621739542` passed the image-alias and bridge contracts, then
  failed the Debian gate assertion fixed in `5c9db5ac`. The corrected gate
  passed in private-JIT contract run `41230976187`; the current-main local
  image-alias and bridge tests also pass.
- Docker job `124621739583` failed before building because the private JIT
  guest did not have the Docker executable. This remains a runner provisioning
  issue.

At 19:18 UTC the Windows job `124621739647` was still installing frontend
dependencies; Chocolatey `.76` handoff step 63 had not started. The run is
still against `c59488f1`, so it cannot establish exact-current-main provenance.
No run was retried or dispatched, and no runner or workflow configuration was
changed. The three completed job logs and this triage are retained under
`/mnt/disks/scratch/task-worktrees/chaptarrng-finalize-20261031/evidence/`.

At 19:29 UTC, Intel macOS job `124621739617` is still queued in that same run.
It uses the pre-`9c70c242` workflow revision and cannot validate the newer
private Apple route. The run remains on `c59488f1`; exact-current-main
platform validation is still absent.

At 19:30 UTC, Windows job `124621739647` failed at step 28, `Install frontend
dependencies`. Its log shows repeated Yarn network retries followed by
`ESOCKETTIMEDOUT` while fetching
`@fortawesome/free-solid-svg-icons@7.3.1` from `registry.yarnpkg.com`.
Chocolatey `.76` handoff step 63 was skipped; no installed handoff result
exists. This is a private-runner registry-egress failure, not a Chocolatey
assertion. The full log is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-finalize-20261031/evidence/windows-job-124621739647-logs.txt`.

## Local source corrections — 2026-10-31 19:57 UTC

The Chocolatey release staging and handoff fixture now include
`tools/bookshelfng-startup-recovery.ps1`, and the handoff verifier rejects a
package if that helper is absent or empty. This closes a stopped-service
migration defect in package construction. It has not yet been exercised in a
Windows package build: the only current Windows run failed while downloading a
Yarn dependency, before the `.76` handoff step.

The exact-head Core test run on SDK 10.0.403 completed with 4,928 passed, 29
skipped, and four failures. Three failures came from migration fixtures that
cleared the explicit `HARDCOVER` provider mode; the fourth was PDFsharp 6.5
rejecting PDF operations because the application had not selected a license.
The fixtures now declare the legacy Goodreads mode and isolate environment
changes. Application startup selects PDFsharp's AGPL license, and the PDF
metadata fixture selects it before creating its sample document. The affected
focused set then passed 5/5. The full Core suite was rerun after these
corrections on SDK 10.0.403 and passed: 4,932 passed, 29 skipped, and zero
failed (4,961 total). The TRX result is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-finalize-20261031/validation/core-after-fixes/core-after-fixes.trx`.

Focused package/channel contracts passed 23/23; PowerShell parsing passed for
all five Chocolatey scripts; the main localization check passed for 37 UI
locales and 52 catalogs; `git diff --check` passed. Logs are retained under
`/mnt/disks/scratch/task-worktrees/chaptarrng-finalize-20261031/evidence/`.
No Actions run was retried, dispatched, or canceled.

## Live repository and acceptance snapshot — 2026-10-31 20:04 UTC

The target repository `snapetech-org/chaptarrng` is on `main` at
`50a3e94fb56f42068e6ff8893ad962ef282b37e9`, with no open pull requests. Its
remote heads are `main` and `codex/chaptarr-server-l10n-20261031`; the latter
is three commits ahead and has no pull request. That localization branch and
its separate scratch expansion remain incomplete, so neither is represented
as merged or accepted. No other target-repository branch is outstanding.

The old Build run
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
remains queued at `c59488f1`; its completed Windows job failed on a Yarn
registry timeout before the Chocolatey `.76` handoff. The macOS Intel job in
that run remains queued. The separate `.76` macOS handoff
[41230571219](https://github.com/snapetech-org/chaptarrng/actions/runs/41230571219)
is also queued at `4d02c9db`, with no steps run. The passing private-JIT
contract run `41230976187` is at older commit `5c9db5ac`, not current main.
There is no exact-head private-JIT build for `50a3e94f`.

The latest recorded Helm handoff
[41168121048](https://github.com/snapetech-org/chaptarrng/actions/runs/41168121048)
failed before installed acceptance because the runner proxy returned HTTP 403
for the pinned `kubectl` download. No production cluster was involved; this
workflow's target is an ephemeral Kind cluster on its private runner. The
public ChaptarrNG release remains `.977`; `.978` is unpublished. StartOS
signing/repository delivery, Cloudron and Unraid delivery, canonical Co-op
Cloud acceptance, package-feed/catalog handoffs, representative operator-data
conversion, live-provider behavior, installed rollback, and operator sign-off
remain open.

No Actions run was dispatched, retried, or canceled while preserving the
existing queued work. No runner or workflow routing was changed. Local
validation on the corrected worktree passed the full Core suite (4,932/4,961;
29 skipped), the legacy package/channel contracts (23/23), five Chocolatey
PowerShell parser checks, and `git diff --check`. The exact-head installed
Windows handoff and the external platform/feed acceptance gates are still
unproven.

## Direct-main localization integration and validation — 2026-10-31 20:22 UTC

The committed localization branch was integrated directly into `main` and
pushed in delivery commit `ca1fd2b3`. Its latest source commit was `44b98118`;
the merge commit is `ec833cb7`. The 187-file change adds provider-field and provider-option
localization, notification and server-message rendering, loading-message
registries, source inventories, and matching tests. It changes no workflow or
runner routing. Its final two-file idempotence correction was included at
`44b98118`; the linked worktree is clean.

The merged code exposed a stale direct-download schema test that expected
English text where the provider field now carries a catalog ID. The fixture
now resolves the English catalog and restores the shared localization service
afterward. The affected fixture passed 7/7. The full Core suite then passed
4,941 tests, skipped 29, and failed zero (4,970 total) on SDK 10.0.403. This
full run preceded the final frontend-only commit `44b98118`; its added
idempotent-rendering regression test passed in the final localization suite.

Final merged-source checks passed: localization Node suite 51/51, frontend
typecheck, 37 UI locales/4,015 source IDs/52 catalogs, translation helper keys
across 1,633 files, package/channel contracts 23/23, and `git diff --check`.
Source-inventory checks have no structural issues, but strict direct locale
coverage remains incomplete across the 12 target locales: 5,760 server-message
entries and 7,008 provider/outbound-notification/option/loading entries are
pending (12,768 total). The source-capture `--allow-pending` checks report 486
server messages, 649 callsites, 657 bindings, 409 provider fields, 57 outbound
messages, 136 options, and 17 loading messages. This is a localization
acceptance gap; it is not counted as a pass.

## Direct-main delivery snapshot — 2026-10-31 20:23 UTC

Remote `main` is `ca1fd2b3d1d773415b939395727925551ad25bfd`. The merged
localization topic ref was deleted after verifying its tip is an ancestor of
main. The target repository now has only the remote `main` branch and no open
pull requests. The integration push did not start a build, release, or
installed-handoff run. The exact-head private-JIT build for `ca1fd2b3` is still
absent.

Build run
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
and macOS handoff run
[41230571219](https://github.com/snapetech-org/chaptarrng/actions/runs/41230571219)
remain queued on older heads. Run
[41237761781](https://github.com/snapetech-org/chaptarrng/actions/runs/41237761781)
is a queued trusted-owner metadata validation from former topic head
`44b98118`; every build, package, and platform job in it was skipped. It does
not validate the merged main candidate. No runs were canceled, retried, or
dispatched.

## Exact-source translation reuse — 2026-10-31 20:26 UTC

The existing catalogs contain five existing translations whose English
source exactly matches new outbound notification strings. Those five values
were reused across the 12 target catalogs (60 entries total), without machine
translation or changing any dynamic text. The catalog status report was
regenerated and `yarn check-localization` passed. The localization test suite
remains 51/51. Direct outbound coverage pending decreased from 684 to 624;
provider, option, loading, and server-message coverage remains open. The
reused values are tracked as unreviewed exact translations, not qualified
localization sign-off.

## Current-main handoff and runner snapshot — 2026-10-31 20:32 UTC

Remote `main` is `c9bff466a1a29d4fb12c34a0bd4aa3923710f80f`; the repository has
no open pull requests and only the remote `main` branch. No Actions run exists
for this exact head. The existing `.76` macOS acceptance run
[41230571219](https://github.com/snapetech-org/chaptarrng/actions/runs/41230571219)
is still queued on `4d02c9db` for both architectures. Build
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
is queued on `c59488f1`; its PostgreSQL, Linux, Docker, and Windows jobs failed,
and Intel macOS remains queued. Windows stopped at frontend dependency install:
Yarn timed out fetching `@fortawesome/free-solid-svg-icons-7.3.1.tgz`; the
Chocolatey `.76` handoff step was skipped, so this run supplies no installed
migration evidence. The standalone-assets and Helm jobs also remain queued on
older source heads. None validates the current candidate.

The 20:30 UTC private-JIT broker snapshot had seven active jobs and 35 eligible
queue entries, with no hosted recoveries or free kspld0 slots; Windows group
11 had no runners. No compatible ChaptarrNG runner was online. Existing queued
work was preserved, and no run was dispatched, retried, or canceled.

The generated container-platform bundle check was tightened on current main:
it compares the emitted Unraid XML with the source template, allowing only the
release digest-pinned image value, and updates two generated-README assertions
to the current contract. The exact 0.9.978 candidate bundle test passed,
including its checksum, Cloudron, Co-op Cloud, catalog, Unraid, and StartOS
checks plus `npm ci`, TypeScript check, and StartOS build. The run log is
retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-unraid-bundle-contract-20261031/evidence-main-test-20261031.log`.
This closes a generated-bundle regression-test gap; it does not establish
external publication or installed Unraid, Cloudron, StartOS, or Co-op Cloud
acceptance. Private Windows `.76` acceptance, external feed/repository
delivery, and operator-data/live-provider sign-off remain open.

## Old Build run failure triage — 2026-10-31 20:43 UTC

Job logs for the stale Build run
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913)
separate source/test corrections from runner/build blockers:

- PostgreSQL smoke ran 15 tests: 11 passed and four failed because those older
  fixtures did not set the required `HARDCOVER=true` or `HARDCOVER=false`
  migration mode. Commit `ece16c4b` corrected the fixtures and isolates their
  environment changes. The merged source's local Core suite passed 4,941 tests,
  with 29 skipped and none failed; this is not current-head private-JIT proof.
- The Linux image-alias and bridge tests passed. Its old Debian publish-gate
  assertion still expected tag-push publication after the release flow had
  moved to private main-branch dispatch. Commit `ece16c4b` updated that
  contract. Current Debian and Launchpad gate scripts both pass locally; logs
  are retained in the task scratch evidence directory.
- Docker stopped before the image build because the JIT guest had no `docker`
  executable. This requires a compatible private runner build capability.
- Windows stopped during Yarn dependency installation on a registry timeout;
  its `.76` service, data-canary, and migration-choice steps were skipped.

The follow-up run must use the current main SHA and compatible private
platform capacity. No rerun or runner change was made.

## Windows frontend-fetch timeout mitigation — 2026-10-31 20:51 UTC

Commit `0ed9d0adb35a6542b5f39318bcf995b2c7533658` raises the Yarn Classic
network timeout to 120 seconds in the Build workflow while retaining the
frozen lockfile. This addresses the bounded `ESOCKETTIMEDOUT` observed while
fetching Font Awesome 7.3.1 in the stale Windows build; it does not prove the
registry or egress path is healthy.

The updated workflow YAML parsed successfully, all 17 private-runner policy
tests passed, `git diff --check` passed, the commit hook's Semgrep scan found
zero issues, and the push-readiness plus image-alias gates passed. Logs are
retained under
`/mnt/disks/scratch/task-worktrees/chaptarrng-yarn-timeout-20261031/evidence/`.
There is still no exact-head Actions run and no compatible Windows JIT runner
is registered, so the BookshelfNG `.76` Chocolatey No/Yes/rollback handoff
remains unaccepted. No run was dispatched or retried.

## Packer capability validation update — 2026-10-31 21:14 UTC

Packer `main` contains the Docker capability mapping fix at
`f931c985fa65f77a484c5f4733b982ef664a947b`. Its exact-head private validation
[41240630688](https://github.com/snapetech-org/packer/actions/runs/41240630688)
passed the Packer project checks. The Windows private-runner canary and trusted
owner PR validation were skipped; the source fix has not been deployed to the
broker.

ChaptarrNG remains at `dd9ce2f669faa6550cc86dba13f88b5c8f913cdc` with no
exact-head Actions run. The latest organization runner registration snapshot
has no Windows runner; the registered Apple runner is persistent rather than
ephemeral JIT, and the Linux JIT runners are busy. The Packer check does not
validate the ChaptarrNG Windows `.76` handoff or establish compatible capacity.

## Pinned narration import correction — 2026-10-31 21:17 UTC

The one-commit change from ChaptarrNG PR #188 was reviewed and integrated
directly into `main` as `8045f6827d9b4a1c83377c146bb7a5a7cfc7f6e8`; the redundant
PR was closed. For a pinned audiobook edition, an untagged file can now match
by duration only within the existing tolerance and when every other known
narration is at least 120 seconds farther away. Conflicting narrator tags,
missing duration, ambiguous lengths, and title/author mismatches still reject.
The user-facing release-note fragment is included in the commit.

The PR description reports a full Core run of 4,952 passed, 29 skipped, and
zero failed on base `c9bff466` plus the change. Its GitHub Actions run
[41242252070](https://github.com/snapetech-org/chaptarrng/actions/runs/41242252070)
skipped the build and test jobs. No exact-head private-JIT run exists for
`8045f682`; the reported local suite is not private-JIT or installed-channel
acceptance evidence.

## Exact-main validation and channel boundary — 2026-10-31 22:31 UTC

The direct-main code head is `6e0fb29f4f3b17740905caf3cbb7929dedacceb3`.
The exact-head local Core suite passed **4,952 tests, with 29 skipped and zero
failed**, using .NET SDK 10.0.403 and the confirmed local Six Labors license
file. The TRX result is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-core-test-957f1a2-20261031/results/core-final-6e0fb29.trx`.
The skipped cases require an external PostgreSQL configuration, Windows, live
provider/service access, browser tooling, or installed FFmpeg behavior; they
are not counted as passes. The separate disposable PostgreSQL 16 focused run
passed 37/37 migration checks on source head `957f1a2e`; that run is controlled
fixture evidence, not current-head private-JIT or operator-data acceptance.

Exact-main supporting checks passed: localization tests **62/62**;
`check-localization` for 37 UI locales, 4,084 source IDs, and 52 catalogs;
private-runner policy tests **17/17**; parsing all 16 Actions workflow files;
and `helm lint` for the compatibility chart. The pinned BookshelfNG chart
contract test also passed against the reference chart at
`adea130aeea2b6ea29c28a6ea8bf4706169d15e4`. It compared rendered resource
identities and retained values, validated bridge packaging and gates, and
checked that the real `.76` No/Yes/backup/restore/recovery script is wired.
Its output is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-helm-acceptance-20261031/helm-handoff-contract.log`.
That contract test did **not** run the installed handoff.

The additional strict direct-translation scan reports **6,372 missing
per-locale entries**. The catalog/provenance validator passes and permits the
documented English fallback; this does not mean every source string has a
direct translation in every locale.

For clarity, “Helm handoff” means the ChaptarrNG package acceptance workflow
`.github/workflows/helm-handoff.yml`. Its eventual installed test creates and
deletes a disposable Kind cluster inside the runner; it does not deploy to a
user or production cluster. Historical run
[40343577463](https://github.com/snapetech-org/chaptarrng/actions/runs/40343577463)
failed at `ruby/setup-ruby` with `EACCES` on `/opt/hostedtoolcache`, before the
acceptance script and before Kind creation. The writable-cache step is now in
the workflow, but has not been rerun on current main.

The read-only exact-head snapshot at 2026-10-31 22:30 UTC found no Actions run
or check run for `6e0fb29f`. Six older main runs remain queued and unchanged:
[41230571219](https://github.com/snapetech-org/chaptarrng/actions/runs/41230571219),
[41229894913](https://github.com/snapetech-org/chaptarrng/actions/runs/41229894913),
[41168142569](https://github.com/snapetech-org/chaptarrng/actions/runs/41168142569),
[41167175002](https://github.com/snapetech-org/chaptarrng/actions/runs/41167175002),
[41166424095](https://github.com/snapetech-org/chaptarrng/actions/runs/41166424095),
and [41166416822](https://github.com/snapetech-org/chaptarrng/actions/runs/41166416822).
Windows private JIT had zero runners; Linux had three online and all busy;
Apple had one idle persistent Mac, which is not an ephemeral JIT runner. No
job or shared runner state was changed.

Chocolatey listings checked on 2026-10-31 still show public BookshelfNG
`0.4.21.76` and ChaptarrNG `0.9.973`. The current `.76` Chocolatey No/Yes/
rollback handoff and unpublished `0.9.978` bridge therefore remain unaccepted.
The [BookshelfNG listing](https://community.chocolatey.org/packages/bookshelfng)
and [ChaptarrNG listing](https://community.chocolatey.org/packages/chaptarrng)
do not establish delivery or acceptance of the candidate bridge.

The global retirement gate remains open. Remaining evidence includes exact-head
private-JIT validation, current `.76` Windows handoff, official StartOS package
and repository rollback, Cloudron and Unraid delivery, canonical Co-op Cloud
recipe acceptance, affected external package feeds/catalogs, representative
operator data and rollback, and live provider/API workflows. No release
readiness variables were enabled, no package was published, and BookshelfNG
retirement is not accepted.

## Private-JIT route normalization update — 2026-10-31 22:55 UTC

The current source/workflow head is `754be09cd3264abe213c9de0f0d98ddda65d5ca8`.
This workflow-only commit standardizes dispatch `action`/`runner_pool` run
names and adds the `smoke`/`private` inputs to the main-only gVisor runner
check, while preserving its dedicated `gvisor-sandbox` target. Exact-head local
checks passed: all 16 workflow files parsed, the private-runner policy suite
passed 17/17, and the gVisor input and main-only target assertions passed.
This does not constitute an Actions or runner smoke result.

At 2026-10-31 22:55 UTC, exact-head Actions and check-runs were absent. The six
older main runs listed in the preceding section remained queued. Windows JIT
group 11 had zero runners; Linux group 10 had three online, all busy, and two
offline; Apple group 8 had one idle persistent runner, not ephemeral JIT; and
gVisor group 6 had zero runners. No compatible idle JIT capacity was available
and no job or runner state was changed.

The local consolidation audit found no safe unintegrated patch in the detached
security-sweep, release-preflight, public-fork, no-hosted-fallback, or PR #181
worktrees. Their useful changes are already present or superseded on `main`;
the old hosted fallback and any CodeQL workflow are excluded by current policy.
The local and remote branch inventories contain only `main`, and there are no
open ChaptarrNG pull requests. Detached worktree contents and evidence were
preserved.

This route update leaves the channel gate open: the `.76` Windows handoff,
external package/channel delivery, representative operator-data recovery,
production provider/API behavior, and current-head private-JIT validation are
still unaccepted. No packages were published and no release readiness gates
were enabled.

## Direct-main reliability and exact-head queue update — 2026-11-01 00:21 UTC

The current application head is `699b0a499c51617760e3b2809dc22e0a29a0e54b`.
Commit `6f73cd0d` filters excluded folders from bounded author rescans. Commit
`699b0a49` makes detached conversion scheduling retry after transient
iteration failures, maps SQLite `INTEGER`/`REAL`/`NULL` values consistently
for nullable decimal progress, and preserves provider chapter metadata when a
filesystem rejects symlinks by staging source copies after a space check.
Partial staging is removed and a failed fallback fails the conversion instead
of silently dropping the chapter data.

The combined exact-source focused Core run passed **109 tests, skipped 11, and
failed zero**. It covered the author rescan scope, conversion scheduling,
SQLite decimal row ordering, provider chapter fallback/cleanup, and conversion
import paths. The 11 skips require installed FFmpeg behavior or an external
crash-worker setup. The passing result is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-direct-main-integrations-20261031/results-combined/combined-regressions.trx`.
The full Core test project then passed **4,961 tests, skipped 29, and failed
zero** against the same source SHA; its TRX result is retained at
`/mnt/disks/scratch/task-worktrees/chaptarrng-direct-main-integrations-20261031/results-full/release-core-699b0a49.trx`.
The commit hook's Semgrep scan found zero findings. These are source-level
checks, not Windows package, installed-channel, or operator-data acceptance.

Exact-head private build run
[41255968485](https://github.com/snapetech-org/chaptarrng/actions/runs/41255968485)
was dispatched against this SHA with `action=ci` and `runner_pool=private`.
At 00:20 UTC its Linux x64, Windows x64, macOS Intel, PostgreSQL migration,
and Docker build jobs were all queued and none had started. GitHub's live
runner API showed no compatible free ephemeral JIT: Linux guests were busy on
other repositories, Windows group 11 had no runners, and the only idle Mac was
a persistent ARM64 runner that does not match the required route. No shared
runner was restarted or changed. This run builds and tests; it does not publish
an image or create a production cluster.

Current-head package contracts also passed locally: legacy channel contracts
passed **23/23**; release-note parser tests passed **7/7**; and the release-note
preview rendered the new conversion fix fragment. The container-platform
candidate test built and checksum-verified the `0.9.978` candidate bundle,
checked Cloudron, Co-op Cloud, Unraid, catalog, and StartOS contracts, and
passed StartOS install, TypeScript check, and build. That test uses a sentinel
image digest and deletes its generated bundle on success; it does not validate
the published image digest or establish channel delivery, installed rollback,
or operator acceptance.

The current public release remains ChaptarrNG `0.9.977`; the current public
Chocolatey listings remain BookshelfNG `0.4.21.76` and ChaptarrNG `0.9.973`.
The `.76` private Windows No/Yes/rollback handoff, `.978` bridge, StartOS
package and repository rollback, Cloudron and Unraid delivery, canonical
Co-op Cloud recipe, package-feed/catalog delivery, representative operator
data and recovery, and live provider/API behavior remain unaccepted. No
readiness variables were enabled and no package or release was published.

## Exact-head handoffs and Helm egress correction — 2026-11-01 01:16 UTC

The current application/workflow source head is
`ee1d583cb7f4a58ceb0ae7ccb98cc169601cd236`; report-only commit
`b18f5d9e529324cec2bfdcae1b3ff42749733afb` records this status. The source
tree matches the tested application source at
`699b0a499c51617760e3b2809dc22e0a29a0e54b`. There are no open ChaptarrNG PRs
and GitHub lists only the `main` branch. Queued validation for deleted PR
branches and older main candidates was cancelled so current-source evidence
can use the private JIT queue.

Build `41255968485` at `699b0a49` passed PostgreSQL migration smoke; Linux,
Windows, macOS, and Docker jobs remain queued. Current-head private-JIT runs
were dispatched at `ee1d583c`: Chocolatey `.76` No/Yes/rollback
`41260222596`, Fedora RPM No/Yes/rollback `41260224643`, channel contracts
`41260226517`, standalone release assets `41260228325`, and macOS standalone
installed handoff `41260230197`. Their required jobs are queued without
runners; none is acceptance evidence yet.

The pinned Helm run `41168121048` failed before the install script because the
JIT proxy denied `dl.k8s.io`; it created no Kind cluster. Packer `main` now
contains commit `dc258d562a47de0fea0e9fa677b5014114ff9be1`, which permits that
exact host over HTTPS only and tests rejection of HTTP and lookalike hosts.
Packer private-JIT validation `41260115705` is queued at that exact commit.
The proxy change is not deployed: the shared broker still has active guests,
so no broker restart or live policy cutover was attempted. Helm is not rerun
until the allowlist change reaches the JIT guests. No production or external
cluster is involved.

An organization CodeQL audit covered all 65 accessible `snapetech-org`
repositories: default setup is `not-configured` wherever GitHub exposes the
code-scanning endpoint, and the remaining repositories report that Advanced
Security is disabled. The Code Quality setup endpoint returned
`not-configured` for all 65 repositories. Code search found no
`github/codeql-action` workflow invocation. ChaptarrNG's generated Actions
registration remains listed by GitHub, but its code-scanning configuration is
`not-configured` and its most recent run was 2026-10-22. MileageNG also retains
a generated CodeQL Code Quality registration, but its current setup is
`not-configured` and its latest run was 2026-10-19. These are not current
CodeQL scans.

The global channel gate remains open. No release-readiness variables were
enabled, and no release or package was published. Windows `.76`, Fedora RPM,
macOS standalone, remaining installed-channel handoffs, external delivery,
representative operator data, rollback, and live provider/API acceptance still
need completed evidence.

## Packaged Chocolatey prompt coverage and current queue — 2026-11-01 01:30 UTC

ChaptarrNG commit `659a94fc01acde95c5b20183deaa11d6724507ff` extends the
Chocolatey migration-choice contract to extract `tools/chocolateyinstall.ps1`
from the built candidate `.nupkg`. It exercises the packaged parameter parser,
checks that the installer passes the parsed mode to the migration-choice gate,
and runs mocked default-No and explicit-Yes responses alongside the manual
defer and automatic default cases. `git diff --check` passed; no local build,
test, or scan ran under the private-JIT-only policy.

Focused Windows run
[`41261304264`](https://github.com/snapetech-org/chaptarrng/actions/runs/41261304264)
was dispatched at that exact head with `windows_chocolatey_only=true`. Its
required `build (windows-x64)` job remains queued without a runner, so the new
packaged-script contract and the `.76` installed No/Yes/rollback handoff are
not yet acceptance evidence. The previous same-purpose queued run
`41260222596` was cancelled after confirming it had no started job.

The live organization runner list still shows the registered `packer` Windows
runner offline; there is no compatible Windows x64 JIT runner online. Current
Packer main is `b6ce6cbc5ab37a436b4ba2b6bc1fb0a8c0cb8dca`, which keeps
`kspls0` excluded pending storage remediation. A read-only check on `kspls0`
found `/var/lib/gha-jit/jobs` on its root NVMe filesystem and no separate
`/mnt/disks/scratch` mount. Its raw capacity report is not broker-admissible
capacity. The old C0/slot-owner handoff gate is retired in current Packer
policy; broker deployment still requires zero active JIT guests and zero busy
JIT runner registrations.

No release readiness variables were enabled and no package, feed, or external
channel was published. StartOS, Cloudron, Unraid, Co-op Cloud, remaining
package-feed/catalog delivery, Fedora RPM, macOS standalone, representative
operator-data rollback, and live provider/API acceptance remain open. The
earlier pinned Helm acceptance run failed before its install script and
created no ephemeral Kind cluster or production cluster.

## Mainline consolidation and live JIT recheck — 2026-11-01 01:47 UTC

The `snapetech-org/chaptarrng` remote has only `main`, and GitHub reports no
open ChaptarrNG pull requests. I audited 23 non-main local worktree
registrations. Useful public-fork JIT, localization PR #181, Semgrep
suppression, PostgreSQL, release-preflight, owner-PR validation, and per-job
JIT-label changes are already represented on `main`, with newer guards. The
remaining old hosted-runner fallback is incompatible with the owner policy and
was not transplanted. The old Unraid worktree's package-template regression
matches the current `main` test. Worktree data and generated evidence were left
untouched; a separate uncommitted localization follow-up remains in its older
scratch worktree and was not merged wholesale because that tree's effective
diff also removes several thousand current-main lines.

The packaged Chocolatey prompt/.76 handoff run
[`41261304264`](https://github.com/snapetech-org/chaptarrng/actions/runs/41261304264)
still has no assigned runner. The only compatible private Windows runner is
busy in PlezyNG run
[`41257920222`](https://github.com/snapetech-org/PlezyNG/actions/runs/41257920222),
currently in `Setup Flutter`; it was left undisturbed. Fedora RPM, channel
contracts, standalone asset validation, and standalone macOS acceptance remain
queued. No release readiness variable was enabled, no package was published,
and no cluster was created.
