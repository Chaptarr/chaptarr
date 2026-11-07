# BookshelfNG to ChaptarrNG Co-op Cloud handoff

<!-- metadata -->

* **Category**: Apps
* **Status**: 0, unpublished migration candidate
* **Image**: [ChaptarrNG](https://github.com/snapetech-org/chaptarrng)
* **Backups**: 1
* **Healthcheck**: 1
* **SSO**: N/A

<!-- endmetadata -->

This candidate keeps the existing `bookshelfng` recipe name, Swarm stack,
service name, and named volumes while changing the image to ChaptarrNG's
single standard ChaptarrNG image. The existing `/config`, `/books`, and
`/downloads` data remains mounted at the same paths. The recipe backs up all
three named volumes through the Co-op Cloud backup labels.

First startup runs the normal ChaptarrNG migration supervisor. The candidate
uses stop-first deployment and a long startup health window so two application
processes never open the same database and a long conversion is not mistaken
for a failed deployment. Swarm pauses a failed update instead of automatically
restarting an older application against a converted database. The supervisor
restores the BookshelfNG source automatically when first-start conversion or
health validation fails. Keep an independent Co-op Cloud backup before
upgrading; an operator-directed rollback after a successful migration must
restore the matching BookshelfNG data backup before starting the old image.

Swarm has no interactive y/n prompt. To choose No, keep the existing BookshelfNG
stack and image deployed. If the candidate is deployed with
`BOOKSHELFNG_MIGRATION_MODE=manual`, it preserves the source database and writes
a deferred marker, but ChaptarrNG does not serve `/ping` until the operator
switches to `automatic` and redeploys the stack. The Manual setting is a data
conversion hold, not a running BookshelfNG fallback.

The recipe explicitly defaults to the native Hardcover mode used by the
previous Co-op Cloud image. Set `HARDCOVER=false` only when the old service
used the softcover image. A standard image digest cannot encode this setting,
so keep the value in the deployment environment. The recipe's channel mapping keeps the existing Traefik service port. Saved
`config.xml` and `CHAPTARR__SERVER__PORT` determine the internal application
port; the single standard image does not add a Bookshelf-specific port
default. If the saved `config.xml` port differs, the health
check reports the mismatch instead of treating the instance as ready. After a
clean automatic rollback, set `BOOKSHELFNG_MIGRATION_MODE=retry` only after
correcting the cause. An intervention-required state needs manual recovery.
Keep configured legacy OIDC, SeerrNG, API-query, diagnostics, metadata,
Google Books, Europeana, Open Library, Hardcover, Apify, audiobook-merge,
path-limit, and Chromium environment values in `.env`.
The recipe passes through the `BOOKSHELF_OIDC_*`,
`BOOKSHELF_SEERRNG_API_KEY`, `BOOKSHELF_ALLOW_API_KEY_QUERY`,
`BOOKSHELF_DIAGNOSTICS_*`, `BOOKSHELF_METADATA_SOURCES`,
`GOOGLE_BOOKS_API_KEY`, `EUROPEANA_API_KEY`, `OPEN_LIBRARY_CONTACT_EMAIL`,
`HARDCOVER_*`, `BOOKSHELF_M4B_*`, `BOOKSHELF_FFMPEG_PATH`, `METADATA_URL`,
`MAX_PATH`, `MAX_NAME`, and `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` settings.
It also forwards legacy `BOOKSHELF__`, `AUDIOARR__`, and `READARR__`
application, authentication, server/security, logging, updater, and full
PostgreSQL settings. Leave unset fields blank so saved `config.xml` values
remain authoritative.

An isolated Debian 13 / Docker 26.1 Swarm rehearsal upgraded the pinned
BookshelfNG 0.4.21.71 source image to a local exact-head ChaptarrNG candidate.
Manual preserved the schema-158 database and `/books`/`/downloads` canaries;
Automatic migrated to schema 124, passed SQLite integrity checks, retained the
BookshelfNG database backup, and served `/ping`. An injected unhealthy update
paused. Restoring the full three-volume snapshot and redeploying the .71 image
returned healthy BookshelfNG. The `backupbot.backup=true` label and local
full-volume archive/restore were verified. The official Backupbot II 2.4.0
image (`sha256:870277cdba4b91ad6771184a7e208ba29151b951e39c68b4a3fd6f8d35252feb`)
then backed up the schema-158 test database and all three same-name volumes.
Its CLI restored after deliberate corruption; the database hash, integrity,
schema and row counts matched, all three canaries returned, and the service
mounts remained unchanged. See
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/coop-cloud-backupbot-acceptance.log`.

Abra 0.13.0-beta backup orchestration also passed in an isolated Debian 13 /
Docker 26.1 Swarm VM. Abra deployed a local QA recipe with the same
`bookshelfng_app` service, volume identities, and `backupbot.backup=true`
label, created and listed a Backupbot II snapshot, then restored the corrupted
schema-158 fixture and three volume canaries through `abra app restore` after
the app was scaled to zero. The database returned to its original SHA-256 and
passed SQLite integrity and row-count checks; the app restarted with the same
mounts. Evidence is in
`/var/tmp/chaptarrng-channel-acceptance-20261026/evidence/coop-cloud-abra-backup-restore-acceptance.log`.

The recipe has not been accepted into the canonical Co-op Cloud collection,
and the image target is not published. External maintainer acceptance also
remains open. Before deployment, use the generated recipe bundle pinned to
the exact published digest that passed migration and rollback validation.

The external `proxy` network and a reachable Docker Swarm host are required.
