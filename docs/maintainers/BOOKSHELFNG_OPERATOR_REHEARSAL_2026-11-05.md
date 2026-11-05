# BookshelfNG operator-data rehearsal — 2026-11-05

These are isolated rehearsals using consistent snapshots of both current
BookshelfNG installations. Production services, databases, media and client
configuration were not changed. No release or cutover is authorized.

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
percentages. The 64 focused mapper/paging/identity checks pass. Packaged HTTP
confirmation of these later response repairs remains pending.

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
reads using the exact deployed `v3.72.3` image are running separately; full
request-to-availability fulfillment remains open.

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
Full CI and all ten archive builds passed at `2badb8c2`; the `69ec4a3d` full CI
follow-up is running. Each result retains its tested revision and bounded scope.
