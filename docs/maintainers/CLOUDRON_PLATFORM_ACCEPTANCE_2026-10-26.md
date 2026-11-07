# Cloudron installed platform acceptance — 2026-10-26

## Result

Cloudron 10.1.6 accepted a local BookshelfNG `.4.21` to ChaptarrNG `.4.23` package update for app `tech.snapetech.bookshelfng`. The app data directories and app identity were preserved. Manual defer, native backup, mutation plus native restore, Automatic database migration, internal source-database rollback, and restoration of the old `.4.21` package all completed successfully. The candidate was local QA only; no package or feed was published.

## Tested candidate and source

- Cloudron package version: `0.4.23`.
- ChaptarrNG app image: `0.9.978-cloudron-backup-hooks-v2`.
- Local guest registry digest: `sha256:240f13c564a5b2a11b1e013aa795c111f0162757501cb2dc9b93e0792cf29153`.
- Cloudron app: `tech.snapetech.bookshelfng`, UUID `00067ceb-3be5-4973-8fc7-2013aa484aea`.
- Preserved mounts: `/config`, `/books`, `/downloads`; app port 8787 and `/ping`.
- Source package: BookshelfNG `.4.21`; starting database schema 158, 4 authors, 6 books.

The candidate manifest sets `minBoxVersion` to `9.1.0` and declares `backupCommand` and `restoreCommand`. Cloudron excludes `persistentDirs` from its filesystem backup unless a package writes a logical backup into `/app/data`; these commands require Box 9.1.0 or newer. See [Cloudron's manifest documentation](https://docs.cloudron.io/packaging/manifest/). The backup hook uses SQLite online backups for the six possible active databases, records present/absent state and ownership, and archives the three persistent directories. Restore checks its checksum and SQLite integrity before replacing persistent data, restores the directories in place, removes stale sidecars, and checks integrity after restore.

## Installed run

1. Updated BookshelfNG `.4.21` to candidate `.4.23` in Manual mode. Schema 158 stayed unchanged, `/ping` returned HTTP 200, and normal requests received the recovery page.
2. Cloudron backup task 22 succeeded. Backup ID: `app_00067ceb-3be5-4973-8fc7-2013aa484aea_v0.4.23_81030e35`. The outer archive was 258,760 bytes with SHA-256 `182f817e2763de377cf2a0ca0f8025c3bd0815d6820de754b7b4800371d78f49`. Its persistent-directory archive SHA-256 was `b011f798848121d83f3141f0209832aa4cc412c2cf50dcd253aa6c0e546b5c43`; the embedded checksum matched.
3. The backup state recorded `readarr.db`, `logs.db`, `cache.db`, and `staging.db` as present, and `audioarr.db` and `chaptarr.db` as absent. All four database snapshots passed SQLite integrity and foreign-key checks. Raw snapshot SHA-256 values:

   | Database | SHA-256 | Integrity | FK violations |
   | --- | --- | --- | ---: |
   | `readarr.db` | `c5b43f24d3d042b0389135903f434d347aab802ea97229b4d47381d669c35ee0` | ok | 0 |
   | `logs.db` | `5f8b4d17264d878e272d5e2b7b2dff8fab54691c2834843a105cd009d08ec29c` | ok | 0 |
   | `cache.db` | `f5aa712f9edb7c4bca768a43eee45077a75552d8f194b1e65425bb74ab3348dc` | ok | 0 |
   | `staging.db` | `5a13be3cf5c27a77f10ceba880c8aec7f6025446701b7e4b431609b6d75228ed` | ok | 0 |

4. Mutated the candidate databases, config, books/downloads canaries, and added stale database files, sidecars, and files after backup. Cloudron restore task 25 succeeded. It restored config and all three canaries to their original hashes, restored schema 158, and removed the stale files.
5. Automatic mode task 26 migrated schema 158 to 124. The app returned `/ping` HTTP 200, SQLite integrity passed with zero foreign-key violations, the library had 4 authors and 9 books, and a BookshelfNG source backup remained available.
6. With ChaptarrNG stopped, `-restore-bookshelf` restored the retained schema-158 source database (4 authors, 6 books; integrity ok; zero foreign-key violations). Cloudron then restored package `.4.21` in task 30; BookshelfNG returned healthy `/ping` with config and media canaries intact.

## Legacy backup limitation

The `.4.21` pre-update Cloudron backup was only 1,271 bytes and contained app metadata plus an empty `/app/data`; it did not contain `/config`, `/books`, or `/downloads` because the old package had no backup hooks. The new hooks protect Cloudron backups created after `.4.23` is installed. For a converted legacy app, keep and use ChaptarrNG's retained BookshelfNG database backup before returning to the old package. This is an observed limitation of the predecessor backup, not a failure of the candidate's native backup/restore test.

## Evidence retained

- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/cloudron-v2-backup-snapshot-verification.log`
- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/cloudron-v2-backup-snapshots/`
- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/cloudron-internal-rollback-v2.log`
- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/cloudron-exact-head-acceptance.json` (earlier image-wrapper rehearsal)
- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/temporary-test-logs-and-databases.tar.gz` (116 temporary log/database files preserved before removing build outputs)
- `/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/container-platform-candidate-test-failure.md` (stale contract assertion and fix)

The disposable VM disk, local registry image archives, temporary SSH key, and test provisioning seed were removed after the run. The downloaded OS/base images remain as reusable caches. Public Cloudron feed/catalog acceptance remains open.
