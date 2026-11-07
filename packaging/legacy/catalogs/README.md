# Self-hosting catalog and Compose handoff

This Compose service is the ChaptarrNG candidate for legacy BookshelfNG catalog
entries that allow an existing user to edit the image field. It keeps the
`bookshelfng` container name, the channel's configured host port, and the
original named volumes for `/config`, `/books`, and `/downloads`. It uses the
single standard ChaptarrNG image; candidate generation pins `CHAPTARRNG_IMAGE`
to that release's immutable digest before an update is accepted.
The candidate enables the first-start migration supervisor so an unsuccessful
conversion restores the retained BookshelfNG database and configuration.
Preserve the source provider mode as `HARDCOVER=true` or `false`; the
single image cannot infer it from a tag. Set `BOOKSHELFNG_MIGRATION_MODE=retry`
only after a clean rollback and after
correcting the cause of the first failure; an intervention-required state needs
manual recovery.
The old catalog Compose recipe used the native Hardcover image, and the
candidate preserves that default. Set `HARDCOVER=false` when migrating a
prior softcover image so bare provider IDs keep their Goodreads meaning.
Set `BOOKSHELFNG_PORT` to the existing container port and
`BOOKSHELFNG_HOST_PORT` to the existing host port when they differ from
`8787`. The fresh-install app default follows the container port. A saved
`config.xml` port remains authoritative, and the health check keeps the
container unhealthy if that port no longer matches the Compose mapping.
Copy `.env.sample` to `.env` before starting the candidate and keep the old
port, provider, credentials, and other configured values. The sample defaults
match the previous catalog's Hardcover image and volume identities; the
release builder replaces the source image alias with the accepted digest.
Keep the legacy OIDC, SeerrNG key, API-query, diagnostics, metadata,
Google Books, Europeana, Open Library, Hardcover, Apify, audiobook-merge,
path-limit, and Chromium environment values in the Compose
environment when configured. The candidate explicitly passes through the
`BOOKSHELF_OIDC_*`, `BOOKSHELF_SEERRNG_API_KEY`, `BOOKSHELF_ALLOW_API_KEY_QUERY`,
`BOOKSHELF_DIAGNOSTICS_*`, `BOOKSHELF_METADATA_SOURCES`,
`GOOGLE_BOOKS_API_KEY`, `EUROPEANA_API_KEY`, `OPEN_LIBRARY_CONTACT_EMAIL`,
`HARDCOVER_*`, `BOOKSHELF_M4B_*`, `BOOKSHELF_FFMPEG_PATH`, `METADATA_URL`,
`MAX_PATH`, `MAX_NAME`, and `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` settings.
It also forwards the legacy `BOOKSHELF__`, `AUDIOARR__`, and `READARR__`
application, authentication, server/security, logging, updater, and full
PostgreSQL settings. Leave unset fields blank so saved `config.xml` values
remain authoritative.

CasaOS, ZimaOS, Umbrel, TrueNAS, Cosmos, CapRover, Portainer, and other catalog
records are maintained in their respective external repositories. This file
does not claim that those listings have been changed or that their UI update
and rollback flows have been tested. Keep each catalog entry on BookshelfNG
until its owner updates and accepts the same identity, mount, update, backup,
and rollback contract.
