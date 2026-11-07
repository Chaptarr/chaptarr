# Documentation

Use the [main README](../README.md) for the project overview, quick start, and
installation choices.

## BookshelfNG migration

- [Replacement reassessment and completion order](maintainers/BOOKSHELFNG_REPLACEMENT_REASSESSMENT_2026-10-24.md): current source findings and the code-first plan for 1:1 compatibility and automatic installed migration.
- [Retirement status](BOOKSHELFNG_RETIREMENT.md): current release state, validation, and open acceptance gates.
- [Move an installation](MIGRATE_FROM_BOOKSHELFNG.md): supported source schemas, backup, preview, apply, rollback, and channel limits.
- [Workflow parity](BOOKSHELFNG_WORKFLOW_PARITY.md): BookshelfNG user workflows and the remaining operator checks.
- [Portable BookshelfNG backups](BOOKSHELFNG_ENCRYPTED_BACKUPS.md): encrypted backup format and restore compatibility.
- [PostgreSQL migration candidate](BOOKSHELFNG_POSTGRESQL_MIGRATION.md): source support boundary and validation requirements.

## Features and integrations

- [OpenID Connect authentication](oidc-authentication.md): provider registration, exact callback and proxy path, verified-email admission, local-session behavior, secret lifecycle, recovery, Apple setup, and troubleshooting.
- [Multipart audiobook merging](AUDIOBOOK_M4B_MERGING.md): legacy settings, conversion behavior, fallback, and current acceptance limits.
- [Media moves](MEDIA_MOVES.md): preview and move registered files between author folders.
- [Unmapped files](UNMAPPED_FILES.md): retry matching after catalog or metadata changes.
- [Author metadata refresh](AUTHOR_METADATA_REFRESH.md): freshness rules, book-added behavior, and Hardcover request pacing.
- [Fork feature guide](FORK_FEATURES.md): ChaptarrNG capabilities, compatibility notes, and retirement matrix.
- [SeerrNG integration](SEERRNG_INTEGRATION.md): discovery, supported API routes, service-key restrictions, and validation.
- [BookLore and BookOrbit](BOOKLORE_INTEGRATION.md): BookDrop uploads and the separate Book Dock destination.
- [External library integrations](EXTERNAL_LIBRARY_INTEGRATIONS.md): AudioBookShelf, Grimmory, Calibre, Emby, and Jellyfin behavior.
- [Supplemental metadata](SUPPLEMENTAL_METADATA.md): catalogs, credentials, field preferences, and migrated provider IDs.
- [Import-list preview and history](IMPORT_LIST_SYNC.md): dry-run behavior and saved sync outcomes.
- [MQTT gateway](MQTT_GATEWAY.md): discovery, lifecycle events, and webhook requests.
- [Prometheus metrics](PROMETHEUS_METRICS.md): listener setup and metric definitions.
- [Optional diagnostics](OPTIONAL_DIAGNOSTICS.md): explicit opt-in and aggregate event boundaries.

## Operations and development

- [Reporting issues](REPORTING_ISSUES.md): useful logs and environment details, with privacy guidance.
- [HTTP security](HTTP_SECURITY.md): API keys, proxy trust, CORS, bundled UI asset URLs, and request limits.
- [Standalone archive builds](development/standalone-archives.md): supported runtime IDs, custom runtime packs, and no-publication validation.
- [Setup experience contract](development/setup-experience-contract.md): Quickstart flow and completion rules.
- [Localization roadmap](localization-roadmap.md): supported locales and translation priorities.
- [API identity and lifecycle](API_IDENTITY_AND_LIFECYCLE.md): Readarr-compatible identity and command lifecycle.
