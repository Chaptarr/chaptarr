# Changelog

ChaptarrNG release notes highlight user-facing changes and operating guidance.
Each release combines curated entries from release-notes/ with technical
history grouped from conventional commits.

## [0.9.977](https://github.com/snapetech-org/chaptarrng/compare/v0.9.976..v0.9.977) - 2026-10-18

### User-facing changes

#### Added

- **Bookshelf:** ChaptarrNG now upgrades supported older BookshelfNG SQLite schemas on a temporary copy before conversion. The original database stays available for rollback, and preview leaves it unchanged. Newer schemas and PostgreSQL sources still fail closed.
- **Diagnostics:** Operators can opt in to send aggregate counts for application starts, grabs, imports, and failed downloads to an HTTPS OpenTelemetry collector. Diagnostics stay disabled by default and never include book details or file paths.

### Features
- *(diagnostics)* Add opt-in aggregate diagnostics - ([ba1c7ab](https://github.com/snapetech-org/chaptarrng/commit/ba1c7ab312223473047af0ccd61393ba0d74fab0))
- *(migration)* Upgrade older Bookshelf SQLite schemas - ([0b79087](https://github.com/snapetech-org/chaptarrng/commit/0b790871518e9da7e3063940e1156cadeed702ad))

### Documentation
- *(migration)* Update older Bookshelf schema handoff - ([23ba03a](https://github.com/snapetech-org/chaptarrng/commit/23ba03a70a06cbc770313e1c8bfa6e736e0c2976))
- *(migration)* Refresh Bookshelf retirement status - ([3de609b](https://github.com/snapetech-org/chaptarrng/commit/3de609b931abab21555e741de3dd06eb9b1bbf38))

### Testing
- *(diagnostics)* Cover invalid collector endpoints - ([605c66e](https://github.com/snapetech-org/chaptarrng/commit/605c66ec00b535e98250f5a7d246f1aaf202d9de))
- *(migration)* Cover Bookshelf profile conversion - ([21201d4](https://github.com/snapetech-org/chaptarrng/commit/21201d4f179376018529efb7332b5ac4a716a83b))

## [0.9.976](https://github.com/snapetech-org/chaptarrng/compare/v0.9.975..v0.9.976) - 2026-10-18

### User-facing changes

#### Changed

- **Migration:** BookshelfNG migration keeps format-unknown books in both ChaptarrNG catalogs, unmonitored, and writes a private review file. Startup logs show its path. Conversion can continue without speculative searches; operators can classify records before or after startup. One selected audiobook and ebook edition transfer separately. Same-format conflicts stop conversion. No media files move.
  - **Action required:** Review the generated media review file and choose the correct format before enabling monitoring for unresolved books.

### Bug Fixes
- *(migration)* Report unresolved review path - ([387d94f](https://github.com/snapetech-org/chaptarrng/commit/387d94f1013b6f2a5082dec53a2b4ccb65d2e547))
- *(migration)* Preserve unresolved Bookshelf records - ([3014c70](https://github.com/snapetech-org/chaptarrng/commit/3014c701a564a0a90e1feb0cfb3e280c404a81bf))

### Documentation
- *(migration)* Complete BookshelfNG handoff guidance - ([23fe1f0](https://github.com/snapetech-org/chaptarrng/commit/23fe1f0a986163f1507055e5e70145172422cff7))
- *(migration)* Update v0.9.975 package handoff status - ([c922fe7](https://github.com/snapetech-org/chaptarrng/commit/c922fe79682e5c51ea259365546a707aec6419c7))

### Testing
- *(migration)* Assert per-format monitoring fields - ([b7916f4](https://github.com/snapetech-org/chaptarrng/commit/b7916f41f7ca5d9f2509d857404933e2b6c43fbe))

## [0.9.975](https://github.com/snapetech-org/chaptarrng/compare/v0.9.974..v0.9.975) - 2026-10-17

### User-facing changes

#### Changed

- **Unraid:** The ChaptarrNG Unraid template now uses the organization package's moving main tag and follows the latest validated main release. Its persistent app configuration remains unchanged.

### Features
- *(unraid)* Add Community Apps template to main repo - ([69d3844](https://github.com/snapetech-org/chaptarrng/commit/69d384491cb4b554ce51b094b1ad59b829f655c8))

### Bug Fixes
- *(packaging)* Unblock Fedora builds for both identities - ([6ed4972](https://github.com/snapetech-org/chaptarrng/commit/6ed4972a806fe5e64e14126d7dfc9a81019cdb3c))
- *(release)* Pass Chocolatey API key explicitly - ([d336f7d](https://github.com/snapetech-org/chaptarrng/commit/d336f7d864cc47d8d3f7486259588462d177e006))
- *(unraid)* Remove unrelated XML from CA scan - ([abb1c26](https://github.com/snapetech-org/chaptarrng/commit/abb1c262e079fb744d6bce8e6195db9e4114e3c7))

### Documentation
- *(unraid)* Correct template image guidance - ([9cca295](https://github.com/snapetech-org/chaptarrng/commit/9cca295966815e317b7db4212c5ce501cf835233))

### Testing
- Cover Bookshelf maintenance startup flags - ([d051a11](https://github.com/snapetech-org/chaptarrng/commit/d051a115ce83549458f1e032e72062d16f92ebe4))

### Maintenance
- Version bump to 0.9.975 - ([5574b5f](https://github.com/snapetech-org/chaptarrng/commit/5574b5f15780e0b76998f962e14f64abe5b88804))
- Pin and scope public fork validation workflow - ([1c63d96](https://github.com/snapetech-org/chaptarrng/commit/1c63d96ee178d6c6f753bc904846f43de6bd59e7))
- Add isolated public fork PR checks - ([af8ede1](https://github.com/snapetech-org/chaptarrng/commit/af8ede1056645039a27975566ba1c356c54d00d7))

## [0.9.974](https://github.com/snapetech-org/chaptarrng/compare/v0.9.973..v0.9.974) - 2026-10-17

### Documentation
- Consolidate v0.9.973 changelog - ([4899390](https://github.com/snapetech-org/chaptarrng/commit/4899390773f316257236be474d65212aa7ffb8bf))

### Maintenance
- Version bump to 0.9.974 - ([7e41182](https://github.com/snapetech-org/chaptarrng/commit/7e411823c58d37f3bf58d4a73fb752d872198a84))
- Restore platform-specific hosted runners - ([5684b17](https://github.com/snapetech-org/chaptarrng/commit/5684b17677ebf7e430d5f1518ebc62b354cc3a1c))
- Retire private GitHub Actions runners - ([27694da](https://github.com/snapetech-org/chaptarrng/commit/27694dae8c0bb49c2aa20c1c7a416f326f8807e5))

## [0.9.973](https://github.com/snapetech-org/chaptarrng/compare/v0.9.972..v0.9.973) - 2026-10-16

### User-facing changes

#### Security

- **Dependencies:** The frontend build and lint dependency tree now rejects brace and parenthesis patterns nested deeper than 256 levels, preventing the recursive parser from exhausting the JavaScript call stack while the upstream package has no published fix.

### Bug Fixes

- *(docker)* Copy local braces package before dependency install - ([b97fdec](https://github.com/snapetech-org/chaptarrng/commit/b97fdec12704f2edcbbe49592da3d4dd78bb9697))
- *(docker)* Include local braces dependency in cross build - ([702215d](https://github.com/snapetech-org/chaptarrng/commit/702215db08be1f07419644dd40400799bada6b12))
- *(security)* Bound frontend braces parser nesting - ([f990f64](https://github.com/snapetech-org/chaptarrng/commit/f990f64c4f4bdb1072eb034e5b95a3470bb37be8))

## [0.9.972](https://github.com/snapetech-org/chaptarrng/compare/v0.9.971..v0.9.972) - 2026-10-16

### User-facing changes

#### Fixed

- **Migration:** Migration preview now rejects incomplete, extra, or stale media-map entries and reports which source book records need review before conversion can continue.
  - **Action required:** Re-run preview after updating a media map or source database.
- **Migration:** The BookshelfNG migration guide now identifies the first released ChaptarrNG image with conversion and rollback commands, replacing its stale v0.9.969 notice.
  - **Action required:** Use ChaptarrNG v0.9.970 or later for BookshelfNG conversion.

### Bug Fixes
- *(migration)* Reject stale media maps and document handoff - ([e1e95d3](https://github.com/snapetech-org/chaptarrng/commit/e1e95d3227810c2c227eb0831be0dbeca85132e5))
- *(migration)* Reject stale maps and refresh docs - ([c1579b5](https://github.com/snapetech-org/chaptarrng/commit/c1579b5a60c6ac1ee67216e20dbd9d7eef7661fa))

## [0.9.971](https://github.com/snapetech-org/chaptarrng/compare/v0.9.970..v0.9.971) - 2026-10-16

### User-facing changes

#### Fixed

- **Settings:** Reordering delay profiles now stores the correct position, so your updated order still appears after the settings page reloads.

### Documentation
- *(release)* Note delay profile reorder fix - ([e0de021](https://github.com/snapetech-org/chaptarrng/commit/e0de021d231b1eb03ba6f1934662af1f4f6c09dd))

## [0.9.970](https://github.com/snapetech-org/chaptarrng/compare/v0.9.970..v0.9.970) - 2026-10-15

### User-facing changes

#### Added

- **Bookshelf:** Authors that have only an audiobook or only an ebook root folder can now be filled in from your default root folders with one request to POST /api/v1/author/editor/backfill-roots, with dryRun=true to preview counts first. Hand-set authors are skipped and no files are moved.
- **Bookshelf:** Book files can now be downloaded directly from the file actions in the book view, limited to files inside your configured root folders.
- **Indexers:** System health now warns when a MyAnonaMouse indexer with unsatisfied-slot protection has no fresh account status, naming the indexer, so you can see why its grabs are being blocked.
- **Indexers:** A new optional Missing Book Search Interval in indexer options runs a recurring search for all missing monitored books. It is off by default and accepts 60 minutes or more.
- **Metadata:** Audible now has a per-instance marketplace setting for the US, UK, France, Germany, Italy, Spain, Canada, Australia, India, Japan, and Brazil. Catalog searches and ASIN lookups use the selected storefront and language headers, and result links point back to that marketplace.
- **Integrations:** BookLore connections can upload imported books to BookDrop, preserving the BookshelfNG connection settings and streaming uploads without loading entire books into memory.
- **Metadata:** Metadata profiles can rank preferred edition terms and select preferred series for filenames, ebook tags, Calibre, and AudioBookShelf metadata. Stronger match evidence remains authoritative.
- **Import Lists:** Import list settings now show durable sync history and offer a read-only preview of the next sync, so operators can inspect additions and exclusions before provider state or library changes are committed.
- **Localization:** The UI language list now includes Indonesian and Traditional Chinese. Their AudioBookShelf root-mapping labels, guidance, loading states, and errors are translated; other untranslated messages continue to fall back to English.
- **Localization:** The interface, help text, and notifications are now available in 12 additional languages, with the existing English fallback behavior preserved.
- **Api:** The book list API accepts optional offset and limit parameters for the full-library listing and reports the total in an X-Total-Count header.
- **Database:** PostgreSQL connections can now enforce TLS with a configurable SSL mode and root certificate, and no longer issue a session reset after every pooled query.

#### Changed

- **Integrations:** The fork guide now separates Chaptarr features shared upstream from its SeerrNG contract and standalone additions, with concrete criteria for replacing ChaptarrNG.
- **Metadata:** The metadata profile editor now offers a single identifier filter with clear choices for skipping editions missing both an ISBN and an ASIN, missing either, or missing an ASIN.
- **Ui:** The ebook and audiobook toggle is more compact on narrow screens so it leaves more room for the rest of the toolbar.
- **Bookshelf:** Library rescans now report which folder is being scanned, such as "Scanning folder 2/5", in the command status, so API clients and the activity view show progress instead of an empty message.
- **Search:** Book and interactive release searches now retry a small set of known alternate edition titles when the monitored edition title produces no eligible result. Chaptarr still applies the selected book's normal matching, language, media type, and quality rules.
- **Performance:** The frontend now loads route-specific code on demand, reducing the JavaScript fetched for the initial screen and loading optional sections when opened.
- **Importing:** Manual-import responses now include match provenance for automatically matched items, with the route and supporting, conflicting, neutral, and excluded signals. API clients can review the evidence behind a suggested book and edition instead of treating every automatic match as equally certain.
- **Metadata:** When an allowed-language change broadens a metadata profile, Chaptarr now queues a forced metadata refresh for authors using that profile. Newly permitted edition titles can then be discovered for release searches without rescanning library folders.
- **Localization:** AudioBookShelf library options now keep their audio-only and audio-plus-eBooks labels translated in every selectable UI language, so users can map roots without falling back to English.
- **Validation:** Independent validation tasks can now run together in a bounded pool, shortening waits while preserving task dependencies, native test accounting, and isolated output files.
- **Localization:** UI labels and help text now use translated catalog values across all 12 supported locales. The Naming Builder translates token labels, separators, and validation messages, while search examples retain their literal filter syntax.
- **Localization:** The Spanish, Simplified and Traditional Chinese, Hindi, German, Japanese, French, Brazilian Portuguese, Arabic, Korean, Vietnamese, and Indonesian Core catalogs now contain all 2,295 English-source keys. Existing locale strings are preserved; missing keys use exact English fallbacks. This completes key coverage, not translation review. Native-speaker translation and review remain open.
- **Localization:** The remaining interface labels, validation messages, and action text are now localized across the maintained catalogs. Naming Builder corrections, locale contamination fixes, and matching placeholders make the translated UI more consistent.
- **Validation:** Ubuntu validation on main can use the restricted organization runner pool after an operator enables it. Pull requests, Windows and macOS matrix jobs, and other refs stay hosted.
- **Distribution:** ChaptarrNG releases map the mutable BookshelfNG latest, hardcover, and softcover GHCR tags to the standard multi-architecture image; Docker Hub mirrors them when credentials are configured. The workflow checks each published alias digest.
  - **Action required:** Pull and recreate containers that use a moving BookshelfNG image tag; pinned tags and digests still need a manual update.
- **Migration:** ChaptarrNG now includes BookshelfNG conversion and rollback tools in the standard app. Rollback preserves the current ChaptarrNG database, including later writes, and validates both database lineages before replacing files.
  - **Action required:** Preview the source database and resolve any per-book media review before applying.
- **Import:** Manual import previews load each candidate book once per preview instead of once per file, which speeds up previews of large multi-file audiobooks.
- **Import:** When a Manual Import sends files to a new copy of the book instead of the one you selected, the log now records a warning that names both books and the reason.
- **Import:** Manual Import now warns in the preview when a suggested match belongs to a library author but its work is missing from that author's catalog and cannot be added from metadata, so you can pick a local book instead.
- **Repository:** ChaptarrNG source, support, and updater links now use the snapetech-org repository address. The Unraid post-commit sync now clears Git’s source context and validates its transferred companion under the organization. Existing container image names and compatibility identifiers stay the same.
- **Localization:** More of the interface now appears in the selected language across the 12 translated locales. This includes history details, import flows and validation, queue statuses, settings dialogs and validation, backup labels, book-file counts, notification fallbacks, keyboard-shortcut descriptions, and bundle-recovery messages.
- **Distribution:** **Breaking:** The standard ChaptarrNG container image is now published as ghcr.io/snapetech-org/chaptarrng. New releases are no longer pushed to ghcr.io/snapetech/chaptarrng, so existing installs must switch to the new path to receive updates.
  - **Action required:** Change the image reference to ghcr.io/snapetech-org/chaptarrng:latest in your compose file, Unraid template, or run command, then pull the new image.

#### Fixed

- **Bookshelf:** Adding an author through the API with ebook or audiobook profiles and monitoring options but no root folder now keeps those settings, using the default root folder, instead of silently dropping them.
- **Metadata:** Looking up an author by a provider-prefixed ID such as hc:252214 now returns that author instead of running the ID through the book lookup.
- **Calibre:** Imports into a Calibre library no longer fail, and no longer risk duplicate Calibre entries on retry, when the downloaded source file cannot be removed afterwards.
- **Import Lists:** Goodreads import lists now add the missing ebook or audiobook side of an author that was previously added for only one format, so the listed book is imported and monitored instead of skipped.
- **Indexers:** Saving an indexer whose category-filtered feed returns no results now shows a warning you can save anyway, instead of blocking the save.
- **Import:** Importing a regular audiobook no longer replaces a dramatized or full-cast recording of the same edition, or the other way around, unless you choose to replace it. File details and manual import now expose the dramatized flag.
- **Import:** Audiobooks released in several parts, such as "(1 of 2)" and "(2 of 2)", now import side by side instead of each part replacing the previous one. New {ReleasePart} and {ReleaseParts} naming tokens are available. Parts already imported before this update are not re-identified, so only new imports are protected.
- **Ui:** Toolbar buttons with longer or changing labels, such as those showing a count, now grow and wrap so the text stays readable.
- **Naming:** Audio MediaInfo naming tokens now use available file metadata and leave unknown values empty. Audiobook imports retain extracted audio details, and older files can be inspected on demand, so an unavailable bitrate no longer inserts a misleading `0 kbps` segment.
- **Importing:** Audio-file matching now prefers a native audiobook over an ebook/print candidate when title, narrator, duration, author, and series evidence is otherwise tied. Previews for newly matched files can use audiobook metadata despite incomplete catalog details; representative editions remain the fallback when no suitable audiobook exists. Existing file links remain unchanged until corrected.
- **Library:** Interactive book searches now distinguish edition subtitles from base titles, and routine scans retry unmatched books. Large library views honor clean-title sorting; missing author folders remove stale file records, and deletion avoids duplicate Calibre requests. Duration extraction runs only for audio files, and successfully queued commands remain visible when a later database insert fails.
- **Migration:** ChaptarrNG now checks database lineage before running migrations and refuses to start when a native database and a legacy Readarr or Bookshelf database both exist in the same app-data directory. This prevents a legacy library from being silently hidden behind `chaptarr.db`; verified data conversion is still required before switching applications.
- **Importing:** Conversion-job progress remains readable when SQLite stores whole-number values such as `0` as integers. Incomplete-import notifications also tolerate missing download titles and linked author metadata instead of failing while building the message.
- **Importing:** Rescans can now reconnect a renamed EPUB to its existing book when its prior file is gone, the matched edition is unchanged, and both paths are under the same accessible non-Calibre library root. A live copy, a different edition, or a second incoming file still gets its own book copy.
- **Containers:** Container images now use the bundled wget client to check the anonymous /ping endpoint, so Docker health status reflects application availability without relying on curl or other tools that are absent from the runtime image.
- **Monitoring:** An explicit `monitored` change on an author API update now reaches its format-specific monitoring gates. Setting an author to unmonitored no longer gets undone by unchanged audiobook or ebook fields echoed in the request.
- **Monitoring:** The author-monitoring migration now preserves disabled authors from older releases. Libraries that already ran the faulty migration must restore a prior backup or re-unmonitor affected authors because the removed legacy flags cannot be recovered automatically.
  - **Action required:** Restore a pre-upgrade backup or re-unmonitor affected authors if migration 104 already ran.
- **User Interface:** Queue quality labels now retain their readable foreground color when table styling is applied. Interactive Search gives indexer names more room, wraps long names in the column, and exposes the full name on hover. On narrow screens, the results table scrolls horizontally while keeping the title column readable.
- **Navigation:** Toolbar actions now move into a visible More menu when the screen is too narrow, instead of being clipped offscreen. Manual Import and other actions remain available on mobile.
- **Search:** Newznab searches now warn when the indexer has selected categories but none match the searched book's media type. An empty result can no longer look like an indexer query that ran and found nothing.
- **Localization:** The browser now receives the selected UI locale and applies its language and reading direction to the document. Arabic and Hebrew use right-to-left direction.
- **Build:** ChaptarrNG source builds now use the published MQTTnet patch version, avoiding a NuGet restore warning that blocked Windows builds with warnings treated as errors.
- **Audio Bookshelf:** The AudioBookShelf root-folder mapping screen now has translated labels, guidance, and errors in Spanish, German, Simplified Chinese, Japanese, French, Brazilian Portuguese, Arabic, Korean, Hindi, and Vietnamese.
- **Validation:** Queued pull request checks now fetch the pull request head before validating release notes, so merging or deleting its source branch no longer causes a missing-commit failure.
- **Commands:** Queued per-author refreshes no longer occupy every command thread, so RSS sync, imports and other short tasks keep running during large refresh backlogs.
- **Authors:** The bulk root folder backfill now skips a missing format that has no quality profile and reports how many authors it skipped for that reason.
- **Api:** Importing a book through the API no longer fails with a database constraint error when the title has no cleaned form, and unexpected failures now return a server error instead of a validation error.
- **Database:** A cache database that fails to migrate is now discarded and rebuilt at startup instead of preventing the app from starting.
- **Audiobooks:** Updating tags on an M4B now preserves its existing chapter markers.
- **Import:** Manual Import now accepts a foreignEditionId when no numeric editionId is given, as long as the edition belongs to the selected book, and the missing-edition error now explains which identifiers are accepted.
- **Import:** Manual Import previews now log a short warning instead of an error with a stack trace when a file disappears or cannot be read while it is being scanned.
- **Import:** A multi-part audiobook download is no longer marked as imported when one of its parts failed or disappeared during Manual Import, even if the other parts of the same book imported successfully.
- **Import:** A queued author import that is still retrying no longer re-adds an author you removed and excluded from imports.
- **Search:** Release matching now recognizes titles whose number is followed by a dotted year, such as Fahrenheit.451.2018, and treats zero-padded numbers like 01 and 1 as the same volume.
- **Import:** Unmapped files in an author folder are no longer rescanned after every author import when nothing has changed, which stops sustained high CPU use. Repeat rescans back off from one hour up to a day and resume at once when the files or author catalog change.
- **Validation:** Local Linux validation now keeps nested sandbox test sockets under a short private temporary path, preventing the engine contract task from hanging on long runner paths.

#### Security

- **Dependencies:** Updates System.Security.Cryptography.Pkcs to 10.0.13 to address two reported denial-of-service vulnerabilities.
- **Security:** Maintainer backlog snapshots no longer include signed attachment credentials, reducing exposure of time-limited media URLs in the public repository.
- **Validation:** Local test runs now require a trusted Linux sandbox that hides operator files, ambient secrets, service sockets, and network access from candidate test code while preserving an explicitly selected read-only Playwright cache. Dependency roots that resolve outside the candidate tree are rejected before they can be mounted into the sandbox.
  - **Action required:** run local tests through trusted launcher
- **Validation:** Validation task and runtime-probe processes now use restricted environment variables and private temporary settings, reducing exposure of unrelated credentials and configuration during validation.
- **Validation:** The isolated NUnit task now receives the run-specific Playwright browser cache it needs for Linux direct-download tests, without restoring access to the operator's home or unrelated environment variables.
- **Ci Security:** Local validation excludes dotted and extensionless credentials, private/API keys, access/refresh-token and client-secret material, suffixed dotenv credentials, and non-source files under credential folders. Source modules such as TokenStore.ts and safe templates such as tokens.env.example remain available. The sandbox pins inputs and caps task logs.
- **Ci Security:** Pull request validation now uses GitHub-hosted runners for every author, keeping persistent local runners on main and reducing exposure of local files. Editing a PR description reruns the release-note check without rebuilding.
- **Logging:** Provider-controlled book titles and identifiers are now cleansed before logging, preventing embedded line breaks from forging additional log entries. OIDC rejection entries use fixed policy codes instead of free-form messages.

#### Removed

- **Distribution:** **Breaking:** ChaptarrNG no longer maintains its YunoHost package or attaches YunoHost release bundles. Operators using YunoHost should confirm the package's supported update source before upgrading ChaptarrNG.
  - **Action required:** Before upgrading ChaptarrNG, confirm the YunoHost package's supported update source.

### Documentation
- Make pull request guidance conditional - ([9894a61](https://github.com/snapetech-org/chaptarrng/commit/9894a6157001ef83b2e3516216071d58bb280286))
- Align merge and release-note rules - ([89897d8](https://github.com/snapetech-org/chaptarrng/commit/89897d84418996db87d5df62b8694e2e0a677a92))
## [0.9.970](https://github.com/snapetech-org/chaptarrng/compare/v0.9.969..v0.9.970) - 2026-10-15

### Security
- *(ci)* Keep pull request jobs on hosted runners (#69) - ([02d38e1](https://github.com/snapetech-org/chaptarrng/commit/02d38e186c56554662de2b62d0f24c7b4b1da4f1))
- *(security)* Preserve isolated Playwright browser cache (#67) - ([dd9f0ec](https://github.com/snapetech-org/chaptarrng/commit/dd9f0ec0da245fad140eaf6babe3c3f60b62042a))

### Features
- *(frontend)* Add React Query for server state management - ([5296afe](https://github.com/snapetech-org/chaptarrng/commit/5296afee686104c70ef570801c9c839fd34ca19f))
- *(i18n)* Localize naming builder labels and errors - ([777f217](https://github.com/snapetech-org/chaptarrng/commit/777f2174f488436198fff26a81c331b2bbcb2931))
- *(i18n)* Complete target locale catalogs (#79) - ([ad610a4](https://github.com/snapetech-org/chaptarrng/commit/ad610a40b4259061f066adacf529d8eab6078cbc))
- *(i18n)* Localize audiobook library modes - ([4b9d3b6](https://github.com/snapetech-org/chaptarrng/commit/4b9d3b68f9a2c30984680581b38c6c200257a1bb))
- *(i18n)* Add Indonesian and Traditional Chinese - ([b43855f](https://github.com/snapetech-org/chaptarrng/commit/b43855fe01e5389e37ac3cff3b437d770b47ce34))
- *(i18n)* Expose RTL locale metadata - ([b20b371](https://github.com/snapetech-org/chaptarrng/commit/b20b371bfe6e4d2519ab54b77ca17cef3e81c832))
- *(i18n)* Translate AudioBookShelf root mappings - ([5377439](https://github.com/snapetech-org/chaptarrng/commit/537743906a5261afcb16aac60a8de3de831bde55))
- *(import)* Adopt safe existing destinations - ([f3d8105](https://github.com/snapetech-org/chaptarrng/commit/f3d81054ff1040c0cf329a1a8f4804c3030b1c0c))
- *(import)* Expose manual match provenance - ([6ed1f89](https://github.com/snapetech-org/chaptarrng/commit/6ed1f89389858a1a8d5be54bac43181e7e9a6248))
- *(import-lists)* Add durable sync history and preview - ([108511d](https://github.com/snapetech-org/chaptarrng/commit/108511dde089aa62d8e6bed94aaf46d4721fa253))
- *(media)* Extract archived downloads (.zip, .tgz, .tar.gz) during import when no media files are found - ([e66cafd](https://github.com/snapetech-org/chaptarrng/commit/e66cafd132d900d98dead7305ddedf6c79425d82))
- *(metadata)* Add RanobeDB light novel support - ([1b4918b](https://github.com/snapetech-org/chaptarrng/commit/1b4918b9859708c2e6024c092715d8a32357c754))
- *(metadata-profile)* Add UI for IgnoredGenres, with autocomplete from library genres - ([5aeb3d5](https://github.com/snapetech-org/chaptarrng/commit/5aeb3d5d2e4c82bb15a03f5a77fc6c96c12c2c12))
- *(metadata-profile)* Add IgnoredGenres filter (e.g. exclude comics/graphic novels) - ([4b70af4](https://github.com/snapetech-org/chaptarrng/commit/4b70af49500ca75ab4a773d1771f5abea51ca05f))
- *(metrics)* Expose low-cardinality Prometheus metrics - ([2b4b1f4](https://github.com/snapetech-org/chaptarrng/commit/2b4b1f43f1c9b9f7f6a062f41e7dea32cd4e8e53))
- *(mqtt)* Complete gateway and canonical events - ([d1e4df5](https://github.com/snapetech-org/chaptarrng/commit/d1e4df59e91b92c2e21f0c1b3592a8dbc2eba00c))
- *(mqtt)* Add gateway connection foundation - ([59b07bc](https://github.com/snapetech-org/chaptarrng/commit/59b07bcb2577c7129738197041219bc772dc96bb))
- *(notifications)* Add Emby and Jellyfin refresh provider - ([5ae1487](https://github.com/snapetech-org/chaptarrng/commit/5ae14872f02440db6bea18fb81b0a07718a779d6))
- *(search)* Retry alternate edition title searches - ([8272e5a](https://github.com/snapetech-org/chaptarrng/commit/8272e5a40d89c197f2028d93592032727d8e8303))
- *(validation)* Report live task progress - ([b37b982](https://github.com/snapetech-org/chaptarrng/commit/b37b982538fb5a5989943c635849b1af91140aee))
- Migrate BookshelfNG into standard ChaptarrNG (#100) - ([fa5384e](https://github.com/snapetech-org/chaptarrng/commit/fa5384e80d13157bf805278e900c510c189bc69b))
- Add Audible marketplace setting - ([95b56be](https://github.com/snapetech-org/chaptarrng/commit/95b56be46991179999cb856f48e88dd57db212fb))
- Add BookshelfNG metadata preferences - ([d763525](https://github.com/snapetech-org/chaptarrng/commit/d763525c46f13b5d6bcff11e52e449cceec7bae6))
- Preserve BookLore uploads and protect legacy databases - ([8a231dd](https://github.com/snapetech-org/chaptarrng/commit/8a231dd8d31202a6be3c7f95828ddc9cd3c2344a))

### Bug Fixes
- *(MyAnonaMouse)* Persist refreshed mam_id cookies - ([f4e6c27](https://github.com/snapetech-org/chaptarrng/commit/f4e6c27f53559b71477a649a5dd3a74dea4c99fa))
- *(abs)* Expose full library paths in mappings - ([c6ab3df](https://github.com/snapetech-org/chaptarrng/commit/c6ab3df5a845464210bcb04b542740cb092582f1))
- *(aliases)* Retry ReplaceAliases when a concurrent writer wins the unique-index race - ([0b514cd](https://github.com/snapetech-org/chaptarrng/commit/0b514cdd6c3610b83012a2d8ca2c032d88e07c84))
- *(api)* Persist explicit author monitoring changes - ([43e3faa](https://github.com/snapetech-org/chaptarrng/commit/43e3faabf1c9333444711e64453deb37c90a9fca))
- *(audiobookshelf)* Push the cover after the folder cover.jpg is written - ([e62474e](https://github.com/snapetech-org/chaptarrng/commit/e62474ece93ae76592537d615bfa350ff19f7401))
- *(audiobookshelf)* Send only the cover on a cover-updated event - ([17a678e](https://github.com/snapetech-org/chaptarrng/commit/17a678e2b1b1b0b5108f97215ea898892c4e6dcf))
- *(author)* Stop ApplyChanges wiping stored paths when the update omits them - ([408d58a](https://github.com/snapetech-org/chaptarrng/commit/408d58a98d0b2f057645b40b9f21344263520b26))
- *(books)* Retain format four audiobook fallback - ([59e8c7f](https://github.com/snapetech-org/chaptarrng/commit/59e8c7fc4bc9c77472d09c139018d29189bc3b78))
- *(books)* Use the author-refresh match instead of independently re-deriving it (#182) - ([2262f84](https://github.com/snapetech-org/chaptarrng/commit/2262f84ce2e915ad9bc2fac3fb2837d4f8a47aaa))
- *(books)* Make remote-item match-claim order deterministic during author refresh (#179) - ([30750b3](https://github.com/snapetech-org/chaptarrng/commit/30750b3824d77050200301cfc246c44c3ab72aff))
- *(books)* Compare against the simulated update result, not the raw remote payload (#183) - ([52bdf56](https://github.com/snapetech-org/chaptarrng/commit/52bdf5677dc0ba01c5b54baa073844b6b6e46d2e))
- *(build)* Repair backlog compile failures - ([22a9db6](https://github.com/snapetech-org/chaptarrng/commit/22a9db6de4d533a7b7f897417ef0b9748e9ffff7))
- *(build)* Close SQL verbatim string literal - ([c1592f6](https://github.com/snapetech-org/chaptarrng/commit/c1592f6e780f78a3fad156fca482b67d68e05440))
- *(build)* Pin MQTTnet to published patch version - ([3705a9b](https://github.com/snapetech-org/chaptarrng/commit/3705a9b40c29e1db632082029f6544b72457e0fe))
- *(cache)* Serialize expiry cancellation and release timers on removal - ([3aa4a95](https://github.com/snapetech-org/chaptarrng/commit/3aa4a95de7f513c4a9b536084f1bbe7ba6df4548))
- *(ci)* Repair trusted runner expression (#102) - ([9f736b9](https://github.com/snapetech-org/chaptarrng/commit/9f736b9984eb026d53a91492788ec4cc4ca789da))
- *(ci)* Capture validation receipts before frontend build (#77) - ([ee2efc5](https://github.com/snapetech-org/chaptarrng/commit/ee2efc555f6b11a2d3ade66261d4e625aab24380))
- *(ci)* Run self-hosted PowerShell under private Aqua root - ([22e5f82](https://github.com/snapetech-org/chaptarrng/commit/22e5f82a58e90895951ab68d4d6cad3e807178e6))
- *(ci)* Isolate Aqua package cache per runner job - ([cdc1a39](https://github.com/snapetech-org/chaptarrng/commit/cdc1a3987d53ea4c5c86452d8332f8d7e00dd1d7))
- *(ci)* Fetch merged pull request head for notes - ([a22ea02](https://github.com/snapetech-org/chaptarrng/commit/a22ea02e9067b20fac0561053291795658a782a4))
- *(ci)* Make self-hosted runner temp private - ([be6dfb2](https://github.com/snapetech-org/chaptarrng/commit/be6dfb237f25a73404deecbf3acc744475788703))
- *(commands)* Keep a cancelled running command Started until its handler exits - ([c443d65](https://github.com/snapetech-org/chaptarrng/commit/c443d655b666a7d4e271622ceb7cea4269c52e7c))
- *(commands)* Stop disk-access groups from globally blocking each other - ([05854a8](https://github.com/snapetech-org/chaptarrng/commit/05854a8bc8125f4425c448e1a0a1237a4bcc1d7b))
- *(conversion)* Handle integer progress and incomplete metadata - ([911d194](https://github.com/snapetech-org/chaptarrng/commit/911d194c13b358d75c49f84956c6321272ae2607))
- *(docker)* Restore an arm/v7-capable Node image for the browser stage (#121) - ([1a8304d](https://github.com/snapetech-org/chaptarrng/commit/1a8304dc04c5322cfcc5c52f8c3ceca2cd5b0bc6))
- *(docker)* Use the configurable health check in local-build images (#112) - ([6dde720](https://github.com/snapetech-org/chaptarrng/commit/6dde720a15703f7c33bf56abf813ccb361fb592e))
- *(docker)* Report container health with wget - ([ef6fec6](https://github.com/snapetech-org/chaptarrng/commit/ef6fec66c70a3f106de66b836dc3a000e533e37c))
- *(download)* Bound how long ProcessMonitoredDownloads yields the disk slot - ([b624a08](https://github.com/snapetech-org/chaptarrng/commit/b624a08a2492ec1d67b1f4eb27786ef0b5255026))
- *(download)* Let ProcessMonitoredDownloads yield the disk slot to waiting commands - ([713722d](https://github.com/snapetech-org/chaptarrng/commit/713722d66126e47249ef9484bf096e47eccac363))
- *(download)* Make ProcessMonitoredDownloads honor cancellation - ([44b9f61](https://github.com/snapetech-org/chaptarrng/commit/44b9f61062ac7e921b0303a9d642b2015a0baedf))
- *(downloads)* Close completed import refresh race - ([4fd9696](https://github.com/snapetech-org/chaptarrng/commit/4fd9696732adf50a2d4fcac46ae9510bd9e97eb5))
- *(frontend)* Migrate to react-dnd 16 and react-dnd-multi-backend 8 (#111) - ([cade6a2](https://github.com/snapetech-org/chaptarrng/commit/cade6a21a34fb170a44aca2c282b628ef93b3897))
- *(frontend)* Keep default CSS module exports with css-loader 7 (#110) - ([014d2c1](https://github.com/snapetech-org/chaptarrng/commit/014d2c1b085edbc6e2ff9f756523e433b5440279))
- *(frontend)* Keep search columns usable on mobile - ([c5beffe](https://github.com/snapetech-org/chaptarrng/commit/c5beffe9c09dd4ca9c30a78b4f3c849b2db2d83e))
- *(frontend)* Improve quality labels and indexer names - ([223a874](https://github.com/snapetech-org/chaptarrng/commit/223a874ca2987d30106bb406cc2b97caeb97b97e))
- *(frontend)* Sync direct download contract with docs - ([1c9f53a](https://github.com/snapetech-org/chaptarrng/commit/1c9f53af853a3d95b7eed35d7684ec268672e4e1))
- *(frontend)* Make merged dependency build reproducible - ([df9385c](https://github.com/snapetech-org/chaptarrng/commit/df9385ccbf77640f38495b27c04e52bb9f12ebd4))
- *(frontend)* Add ambient process type for webpack NODE_ENV - ([85291e9](https://github.com/snapetech-org/chaptarrng/commit/85291e93746a5f5c767c23d56789d16d753705eb))
- *(frontend)* Scope service worker to a webworker tsconfig - ([5b286a3](https://github.com/snapetech-org/chaptarrng/commit/5b286a328dd8d1aba959e88f1c3b9a3eab340b39))
- *(frontend)* Register dayjs isBetween plugin used by isInNextWeek - ([e357b0d](https://github.com/snapetech-org/chaptarrng/commit/e357b0d07a91f167083f168bb9b828f83296306f))
- *(frontend)* Wrap backup-save request with Promise.resolve for jQuery Deferred compat - ([a393f47](https://github.com/snapetech-org/chaptarrng/commit/a393f47fb07fb30bdded591f70b4f8e05c03756e))
- *(i18n)* Correct reviewed locale strings and placeholders - ([15a5d6d](https://github.com/snapetech-org/chaptarrng/commit/15a5d6d36960a49eb28d35834c002b2a4ad24075))
- *(import)* Use explicit series tags to verify edition positions - ([fa9e26e](https://github.com/snapetech-org/chaptarrng/commit/fa9e26ecacafbb34570e0640f381b441283aae7e))
- *(import)* Prefer audiobook editions in fallback ties - ([e3c9eff](https://github.com/snapetech-org/chaptarrng/commit/e3c9effa7efa9cb5adf4492293b1a4a3f37e8408))
- *(import)* Reattach renamed EPUBs - ([1ab2354](https://github.com/snapetech-org/chaptarrng/commit/1ab235456092e98e7b6be0e6d613fad1672ea4cf))
- *(import)* Warn about an existing-file conflict in Manual Import preview (#184) - ([7d19f1a](https://github.com/snapetech-org/chaptarrng/commit/7d19f1a791d7f800f1d2c7ad972a3bc6b3c0c867))
- *(import)* Report why a manual import imported nothing - ([9e6e12f](https://github.com/snapetech-org/chaptarrng/commit/9e6e12f8d9f8e6f05546a7c8e93d8666c562ccc4))
- *(import)* Don't let an explicit Move override "this torrent's files cannot be moved" - ([95e47ed](https://github.com/snapetech-org/chaptarrng/commit/95e47edfdf0de8f26f75ed53a5b44986ae69b775))
- *(import-lists)* Commit cursor on the fetching list instance after processing - ([925fd50](https://github.com/snapetech-org/chaptarrng/commit/925fd507a6252d7cb468ce013d2a8ce5729153dd))
- *(import-lists)* Defer Hardcover library sync state persistence until list processing succeeds - ([da77b3f](https://github.com/snapetech-org/chaptarrng/commit/da77b3f62135f60e8984b2a8fc39dfe011d1ff4d))
- *(manual-import)* Match an inferred edition structurally when it carries no provider edition id - ([237fd00](https://github.com/snapetech-org/chaptarrng/commit/237fd002b481a1f7aa26b47159b1b81804e424a9))
- *(manual-import)* Keep the plain exception type for explicit edition ids - ([1104c0e](https://github.com/snapetech-org/chaptarrng/commit/1104c0e86ff7dd0f5a89a269be63bc029e24e191))
- *(manual-import)* Use the reconciled book's monitored edition when a suggested work has many editions - ([0f99a61](https://github.com/snapetech-org/chaptarrng/commit/0f99a61e4ef62d58ccd22a576e6419b25d7ade56))
- *(manual-import)* Fall back to the provider's default edition when a suggested work has many editions - ([32133b4](https://github.com/snapetech-org/chaptarrng/commit/32133b4c8ecfaa44b437c7d1088be5fe6c88a062))
- *(manual-import)* Materialize a suggested work when the suggestion has no edition id - ([79da2f8](https://github.com/snapetech-org/chaptarrng/commit/79da2f8472a6ee4154434cd03333d48b77ef1bbd))
- *(media)* Handle missing root folder during upgrade import with RootFolderNotFoundException - ([ee7ebc7](https://github.com/snapetech-org/chaptarrng/commit/ee7ebc7cd8d6e294bbe55b3813a03b97ac03be59))
- *(metrics)* Disambiguate queue and health-check models - ([cf84c7d](https://github.com/snapetech-org/chaptarrng/commit/cf84c7d477abc100b4deecb732c99a578d87a818))
- *(migration)* Preserve legacy author monitoring choice - ([1261261](https://github.com/snapetech-org/chaptarrng/commit/12612611c096238e60e5c9b2e157a09510a6f9e0))
- *(migrations)* Retain fork cache migration 108 and number query indexes 109 - ([6464c53](https://github.com/snapetech-org/chaptarrng/commit/6464c53d542353b0e149bd6d4ee1095500ed0d7f))
- *(monitoring)* Keep specific-book adds from monitoring the whole catalogue - ([8ed8199](https://github.com/snapetech-org/chaptarrng/commit/8ed819935da1784392f1167d41fd64d888f1a6ba))
- *(naming)* Populate audiobook media info tokens - ([75e9bb8](https://github.com/snapetech-org/chaptarrng/commit/75e9bb89d9c8697fc7c8ba1190035e5454f8afdb))
- *(notifications)* Trace release import dispatch - ([a23e3c6](https://github.com/snapetech-org/chaptarrng/commit/a23e3c6719f22cec6fd55707185bbc37e68993b5))
- *(perf)* Address large-library review follow-ups - ([281fe16](https://github.com/snapetech-org/chaptarrng/commit/281fe16649409d6b306a6c3fbdba5a7c39423f77))
- *(queue)* Enforce torrent preservation on client removal - ([9de8444](https://github.com/snapetech-org/chaptarrng/commit/9de84446a295f96ede90bb746bf6ca1c3ead5deb))
- *(repo)* Remove a stray node_modules link (#119) - ([7918713](https://github.com/snapetech-org/chaptarrng/commit/79187131e40b8c782a0c35da4fbc05be6add69e4))
- *(search)* Warn when indexer lacks media categories - ([0f3050c](https://github.com/snapetech-org/chaptarrng/commit/0f3050c4d2ebf45a631bbf7d1c2ad67ca78c5a76))
- *(search)* Refresh editions after language profile expansion - ([55eddba](https://github.com/snapetech-org/chaptarrng/commit/55eddba12ef6395adabcf2b8bfc4944d26d3b1c3))
- *(security)* Update Pkcs dependency to patched version (#108) - ([cde05c8](https://github.com/snapetech-org/chaptarrng/commit/cde05c8335adc164fdabddf4af6ce6ca11ec35ce))
- *(security)* Mount dependencies read-only in place - ([ee36f13](https://github.com/snapetech-org/chaptarrng/commit/ee36f139554947e0aeb68ffe60d7209350cda8ea))
- *(security)* Require trusted test sandbox - ([e775662](https://github.com/snapetech-org/chaptarrng/commit/e77566240e792ef0c7435a24ad161abd092fdb2a))
- *(security)* Preserve isolated Playwright browser cache - ([d63f7e1](https://github.com/snapetech-org/chaptarrng/commit/d63f7e1097385c75935204cb810efb6162c0495d))
- *(security)* Preserve isolated Playwright browser cache - ([d6e4f23](https://github.com/snapetech-org/chaptarrng/commit/d6e4f2357b148dc30385f5680d00d61b628566ab))
- *(security)* Isolate validation task environments - ([9d9d7e9](https://github.com/snapetech-org/chaptarrng/commit/9d9d7e9ad657cca9acd668f07903156ca9d1c22a))
- *(security)* Isolate validation task environments - ([6e41c72](https://github.com/snapetech-org/chaptarrng/commit/6e41c7275d51719f58db02c513cdac2e83d6774d))
- *(ui)* Keep toolbar overflow actions accessible - ([9fd105f](https://github.com/snapetech-org/chaptarrng/commit/9fd105fbaef9b694169ae1eb4eed57559aa4244e))
- *(ui)* Stop the book page refetching forever for a book that does not exist - ([9b4fa4f](https://github.com/snapetech-org/chaptarrng/commit/9b4fa4f387084322d142ebc9a78239893ef59d4d))
- *(ui)* Read the author's real folder fields (audiobookFolder / ebookFolder) in the delete dialog - ([4707e05](https://github.com/snapetech-org/chaptarrng/commit/4707e052301e3dd9e9974f491f36e3d8ae68db26))
- *(ui)* Delete-author dialog lists every author folder, not only the legacy path - ([dc8b4a3](https://github.com/snapetech-org/chaptarrng/commit/dc8b4a39a6fe4c9d4d284fbf05eb046128ce5ffd))
- *(ui)* Don't open a blank confirmation when a manual import replaces no local book - ([58a40ad](https://github.com/snapetech-org/chaptarrng/commit/58a40ad61ac1d513b9a32d4ae2286854f863819a))
- *(validation)* Prevent nested sandbox contract hangs (#103) - ([9b7b1d1](https://github.com/snapetech-org/chaptarrng/commit/9b7b1d157cc84e2f496fdaa76a58739c8c22d419))
- *(validation)* Expose isolated Playwright browser cache - ([d8b8e76](https://github.com/snapetech-org/chaptarrng/commit/d8b8e76f4dcc6c42daa125d46d15de4128676228))
- *(wanted)* Use edition release date - ([ff74478](https://github.com/snapetech-org/chaptarrng/commit/ff74478f3c3c65c3c04e5e57f445ec2fc8c1d7a2))
- Restore M4B chapter release note and package update-mechanism tests (#113) - ([0c05812](https://github.com/snapetech-org/chaptarrng/commit/0c058129bf7bfb60f5da0e1a46968d9151668c17))
- Retry unmatched files during scans - ([a8bd0ba](https://github.com/snapetech-org/chaptarrng/commit/a8bd0baabc9e551803b5e77573bf9ce8bb05defe))
- Sequence combined database migrations - ([1702049](https://github.com/snapetech-org/chaptarrng/commit/17020494db6c3ee322dc6cb3c0a0a18375384eac))
- Resolve book matching edge cases and validation inventory drift - ([197597b](https://github.com/snapetech-org/chaptarrng/commit/197597b75e6657748418204c4c65210ec448e3dd))
- Harden import matching and conversion cancellation - ([4582c13](https://github.com/snapetech-org/chaptarrng/commit/4582c13317d0d0c4574e2f968a3e78f9baa96821))
- Reconcile migration versions and stale test fixtures - ([dd09594](https://github.com/snapetech-org/chaptarrng/commit/dd0959432ebb2de58b097b73d0e609bda4d7bc7d))
- Preserve cancellation and fatal M4B errors - ([5fc61e4](https://github.com/snapetech-org/chaptarrng/commit/5fc61e4d73336347379ff3fa6548306b47a458d7))
- Keep overlapping casts and dramatized editions as distinct wanted variants - ([e03b995](https://github.com/snapetech-org/chaptarrng/commit/e03b995023fd2db2d886cd3c92e6aeee97d226ec))
- Reject cross-format imports without an author root and preserve cancellation - ([8a9d759](https://github.com/snapetech-org/chaptarrng/commit/8a9d7590177b7a55a8c004732d791875878b56fe))
- Bound cover backoff and evict tag cache entries without clearing hot data - ([1efc938](https://github.com/snapetech-org/chaptarrng/commit/1efc93813529c2dfb86f35a7b8a8c19c82fe8dcd))
- Expose shelf load retries and discard stale count responses - ([846bd74](https://github.com/snapetech-org/chaptarrng/commit/846bd740820091364c6afd6b7184b1f13390e805))
- Reconcile format-scoped list and shelf query interfaces - ([8f3bf84](https://github.com/snapetech-org/chaptarrng/commit/8f3bf84a9b1201c3f0c81aafef89e0246b61edba))
- Reconcile background deletion tests and metadata migration ordering - ([7b3e5f5](https://github.com/snapetech-org/chaptarrng/commit/7b3e5f564bbaa8a84f404a5ed8c2c5ff40d29bdc))
- Align ebook tags with authoritative series and preserve file permissions - ([8a5b58a](https://github.com/snapetech-org/chaptarrng/commit/8a5b58aef71d803cf599ba33cbbff390104b961f))
- Rotate download sweeps fairly without unbounded disk-slot retention - ([ba4263d](https://github.com/snapetech-org/chaptarrng/commit/ba4263d637b44ddd539057685d19b789527fb721))
- Serialize queue snapshots and propagate fatal preview failures - ([037928d](https://github.com/snapetech-org/chaptarrng/commit/037928d146742a1360e407fbca641d47237bbccc))
- Deduplicate command batches and preserve ebook reader failure semantics - ([0229587](https://github.com/snapetech-org/chaptarrng/commit/02295879d7e5e136ee1909a3693cf17fb7405474))

### Documentation
- *(backlog)* Correct HTTP cache migration version - ([46fa890](https://github.com/snapetech-org/chaptarrng/commit/46fa8908a81df4093801c29b6edb4f45fb7e4f53))
- *(backlog)* Record Prometheus metrics implementation - ([f256058](https://github.com/snapetech-org/chaptarrng/commit/f2560586df45a251585fdfd41009aca19bd794b1))
- *(backlog)* Record SABnzbd race fix - ([fe5c003](https://github.com/snapetech-org/chaptarrng/commit/fe5c003a11f67a2a442d8d50f708fb4e4e749777))
- *(backlog)* Record MQTT gateway implementation - ([5a42069](https://github.com/snapetech-org/chaptarrng/commit/5a42069dd265add5eeb7821e7041a96b0abb9d08))
- *(backlog)* Track MQTT gateway implementation - ([ebe2c52](https://github.com/snapetech-org/chaptarrng/commit/ebe2c524e0f6c1801b529004951efff0d4f9ffd7))
- *(backlog)* Record Emby Jellyfin integration - ([cdaa7dd](https://github.com/snapetech-org/chaptarrng/commit/cdaa7dd52587228abc38a7f7f91d855d818ff2f3))
- *(backlog)* Note Proxmox eligibility hold - ([f0b030b](https://github.com/snapetech-org/chaptarrng/commit/f0b030b3f2cba68af40b43f709ad0a3fb1b06327))
- *(backlog)* Record mobile search title fix - ([7dcb551](https://github.com/snapetech-org/chaptarrng/commit/7dcb5518b0965f86bd509a1a2a80b2f1006da6a2))
- *(backlog)* Record audiobook fallback fix - ([ed0ffcb](https://github.com/snapetech-org/chaptarrng/commit/ed0ffcb22ed5ad812abba45a594710e39410a9c2))
- *(backlog)* Record wanted release-date fix - ([47c9412](https://github.com/snapetech-org/chaptarrng/commit/47c941279bd95e882f4889321a6138ca8958e7bd))
- *(backlog)* Disposition series primary filter reports - ([d55aee2](https://github.com/snapetech-org/chaptarrng/commit/d55aee2941cb46c01f04f65b1aad0ed66c37a837))
- *(backlog)* Disposition author progress reports - ([5be115b](https://github.com/snapetech-org/chaptarrng/commit/5be115b55f36350ed650c8ce046c4617fa38b16f))
- *(backlog)* Record author merge coverage - ([d842f1d](https://github.com/snapetech-org/chaptarrng/commit/d842f1d5eef2d24014ed210db4b0b4c81ac0339f))
- *(i18n)* Document localized catalog review - ([68e1330](https://github.com/snapetech-org/chaptarrng/commit/68e1330edf68abdb95f20914b0c8c703b7e129f3))
- Clarify cumulative upstream blocker set - ([24b340f](https://github.com/snapetech-org/chaptarrng/commit/24b340fffc43eb0883995a21665d3d051eb66abe))
- Pin latest ChaptarrNG blocker snapshot - ([fc1e0cc](https://github.com/snapetech-org/chaptarrng/commit/fc1e0ccc8b678952ae77cb5c1a0b02c7bd17e5c6))
- Refresh cumulative blocker snapshot - ([22078e0](https://github.com/snapetech-org/chaptarrng/commit/22078e0289949dbc41ee603cd10b80c2510f1e78))
- Itemize cumulative upstream retirement blockers - ([066258f](https://github.com/snapetech-org/chaptarrng/commit/066258f6f96d20dd5ab2552ce1b16437c541a971))
- Preserve shipped YunoHost release note - ([c416406](https://github.com/snapetech-org/chaptarrng/commit/c4164065c1d1d188549d5444699b13e1feae3c16))
- Document current fork additions and integrations - ([2a8532e](https://github.com/snapetech-org/chaptarrng/commit/2a8532e232b4c3fae6fce16354959d4bf76839a5))
- Record import adoption IO safeguard - ([b57bbd8](https://github.com/snapetech-org/chaptarrng/commit/b57bbd8b8476ad8f406e84b2ba80f496ad3f31cb))
- Disposition occupied import destination requests - ([c7dd0fc](https://github.com/snapetech-org/chaptarrng/commit/c7dd0fc927bcd793a4ac85a5a091d68212c91eb6))
- Disposition closed metadata outage report - ([46b7182](https://github.com/snapetech-org/chaptarrng/commit/46b718208f2404e94d6daed9f9db76c15704f0ac))
- Disposition Readarr book add regression - ([ef6b804](https://github.com/snapetech-org/chaptarrng/commit/ef6b804fdbc3cfd672172ce858cfc5e7303ae782))
- Disposition merged book deletion fix - ([b295203](https://github.com/snapetech-org/chaptarrng/commit/b295203cae77e5e50598bdb12538885b67980bbc))
- Address README backlog requests - ([d5465f2](https://github.com/snapetech-org/chaptarrng/commit/d5465f2ee98fab49cf40a321187f7ab8430ada70))
- Audit compatibility and build PRs - ([8f3b00c](https://github.com/snapetech-org/chaptarrng/commit/8f3b00ccc64b575616d43500a2ae0cd2dcc4fc7b))
- Audit import webhook issue - ([5a8243e](https://github.com/snapetech-org/chaptarrng/commit/5a8243e090cfb78bdb79582c4f74cae91efd3a8a))
- Audit bookshelf monitoring issues - ([fa80bc4](https://github.com/snapetech-org/chaptarrng/commit/fa80bc4738014374e020300a302f7512743572e9))
- Audit author monitoring issues - ([548f31f](https://github.com/snapetech-org/chaptarrng/commit/548f31f950dba63d22bbcecc5ef24a61240eec31))
- Audit book added timestamp coverage - ([467ddfb](https://github.com/snapetech-org/chaptarrng/commit/467ddfb84501d0ae362381f60bc8d4969db8a72c))
- Record empty media category warning coverage - ([503a20f](https://github.com/snapetech-org/chaptarrng/commit/503a20fecd0ad86591b8e611f2a0ecbe1e32f816))
- Record conversion scheduler issue coverage - ([3a0c7ec](https://github.com/snapetech-org/chaptarrng/commit/3a0c7ec2dba3fabc1d69eb5579c6ac0485673133))
- Record alternate edition search completion - ([8699ed7](https://github.com/snapetech-org/chaptarrng/commit/8699ed72f5173fff6bae81f84a7983c532fd4a92))
- Record mobile toolbar issue fix - ([3f57d9c](https://github.com/snapetech-org/chaptarrng/commit/3f57d9cf839d46f06e9aba6694ecc2270eaefdea))
- Validate English edition selection coverage - ([cacf9e2](https://github.com/snapetech-org/chaptarrng/commit/cacf9e27078eb714b772b7b89503951e25bc55ff))
- Record manual import match evidence - ([565ed91](https://github.com/snapetech-org/chaptarrng/commit/565ed915eb4906512a9d64eca2ca46667f0e1dff))
- Record subtitle title matching coverage - ([40f0140](https://github.com/snapetech-org/chaptarrng/commit/40f01402199af7dbb974f92d4ed7564b13bf4e32))
- Record audiobook edition matching fix - ([7b510ee](https://github.com/snapetech-org/chaptarrng/commit/7b510eefb41f0ed3b7ba1a0c7cac28476cd46bf7))
- Validate audiobook selection release note - ([a7fc8b5](https://github.com/snapetech-org/chaptarrng/commit/a7fc8b5d830681a040a85dc927d8f215f17f58cc))
- Record EPUB rename backlog fix - ([76c45c4](https://github.com/snapetech-org/chaptarrng/commit/76c45c4ea7087074a4b03dc4159d49ad02a8f679))
- Record Audible backlog dispositions - ([a0cad97](https://github.com/snapetech-org/chaptarrng/commit/a0cad97adcfc8974d2702a14a586b7a2ab303824))
- Record media info naming token fix - ([5be5bca](https://github.com/snapetech-org/chaptarrng/commit/5be5bca95ee10f10f9e703014d321ca3e951f0ca))
- Record responsive interactive search issue fix - ([1e4ef06](https://github.com/snapetech-org/chaptarrng/commit/1e4ef063a00012a3a5a946c496631f2e8b067b71))
- Record interactive search UI issue fixes - ([6bd6bd6](https://github.com/snapetech-org/chaptarrng/commit/6bd6bd6b362ddf7884c00070904cb80ee478abf3))
- Record direct download PR coverage - ([e49cf0d](https://github.com/snapetech-org/chaptarrng/commit/e49cf0d91f453b94788e9873f0f004ae7ef42bdb))
- Record container and image-audit fixes - ([9f8e02a](https://github.com/snapetech-org/chaptarrng/commit/9f8e02a96dd515fce5fc6be4f67178f38566af40))
- Record migration 104 backlog resolution - ([0d8a0ae](https://github.com/snapetech-org/chaptarrng/commit/0d8a0ae5923e8fa595b854d5b36f6ac4167ad825))
- Refresh upstream backlog through PR19 - ([6495a8e](https://github.com/snapetech-org/chaptarrng/commit/6495a8e564aee552368ab34e77d1c54e4ad41e3b))
- Clarify Chaptarr upstream boundary - ([ddb79d6](https://github.com/snapetech-org/chaptarrng/commit/ddb79d63d028d5a4f71a9f63ab7f66dcb271902e))
- Add ChaptarrNG agent guidance - ([2271d59](https://github.com/snapetech-org/chaptarrng/commit/2271d593748fbbe267a9ea01a2f89af07868e484))
- Pin migration regression validation status - ([5f52032](https://github.com/snapetech-org/chaptarrng/commit/5f52032d29cb19ce969642dbec535d06d6ea353c))
- Detail Bookshelf data migration blockers - ([a4d8ad9](https://github.com/snapetech-org/chaptarrng/commit/a4d8ad95c64daa50896094eec0bedbc3dc29cb4a))
- Clarify localization parity scope - ([5702db2](https://github.com/snapetech-org/chaptarrng/commit/5702db296420f8c091d270864b815f69308b2c2a))
- Include active upstream retirement blockers - ([f8f0c4a](https://github.com/snapetech-org/chaptarrng/commit/f8f0c4adb8d13c6817053d9d39b5c9bb40b42cde))
- Refresh ChaptarrNG retirement inventory - ([af3be47](https://github.com/snapetech-org/chaptarrng/commit/af3be47be45d8b41f5ce807eb4269537a9bad3d8))
- Make README Ko-fi link discreet - ([a6be38d](https://github.com/snapetech-org/chaptarrng/commit/a6be38dd5551b513b22e2efd4504a5ee3e73895b))
- Add Ko-fi sponsor link - ([aa54820](https://github.com/snapetech-org/chaptarrng/commit/aa5482087db4d86930dd7cb43101aa9a83e57a23))
- Record AudioBookShelf propagation audit evidence - ([2095cd7](https://github.com/snapetech-org/chaptarrng/commit/2095cd746106b12c16a81ca0d3323aa8d4f57b70))
- Track upstream backlog integration progress - ([d1e2a5c](https://github.com/snapetech-org/chaptarrng/commit/d1e2a5c95d011cf2af5d29d86719c86735de81f3))
- Update upstream coverage after import and cache integration - ([a965abb](https://github.com/snapetech-org/chaptarrng/commit/a965abb30377bc71421d70017d1298ca1ff31d35))
- Clarify indexer seed time and torrent preservation settings - ([6763dca](https://github.com/snapetech-org/chaptarrng/commit/6763dca10dd7716ee2d8d49fc94a434f14af2136))
- Record integrated upstream patches and validation checkpoints - ([6b4fb08](https://github.com/snapetech-org/chaptarrng/commit/6b4fb08edd07dd37da256880bec72c158a4fae5f))
- Capture upstream discussions and record first integrated fixes - ([1731fcb](https://github.com/snapetech-org/chaptarrng/commit/1731fcba4d1a850f71f95d1e0723a1cfbc889b5d))
- Track complete upstream issue and PR inventory - ([ad0be6d](https://github.com/snapetech-org/chaptarrng/commit/ad0be6d367cf3f25137dbbe33e6476d70cee18de))
- Explain ChaptarrNG fork boundary - ([47fa5fe](https://github.com/snapetech-org/chaptarrng/commit/47fa5fe84e24a04cd1254a5733c5092641d21445))

### Performance
- *(books)* Load editions in bulk before format-sync work grouping instead of one query per book - ([eda1eaa](https://github.com/snapetech-org/chaptarrng/commit/eda1eaa8e3a212d02e19a33715358a4b205dc958))
- *(books)* Integrate upstream PR 218 with scoped refresh caches - ([8247df9](https://github.com/snapetech-org/chaptarrng/commit/8247df96ef93701369c8d579fcf6f26a159a8257))
- *(books)* Replace BuildWorkGroups's O(N^2) pairwise matching with a token-bucketed union-find (#163, #173) - ([775edaf](https://github.com/snapetech-org/chaptarrng/commit/775edaf8e1c986593a667c40da43e5384462623c))
- *(books)* Stop re-fetching RootFolders per work group during monitoring sync (#163) - ([73a7b26](https://github.com/snapetech-org/chaptarrng/commit/73a7b269ea4adf8b3a70ddb7b1bdf51e5abdda52))
- *(books)* Also stop CloneStoredBook eagerly resolving edition-level ASIN - ([05da1d6](https://github.com/snapetech-org/chaptarrng/commit/05da1d629c534fe3e9e3ae2bc46ccfff731f1bcf))
- *(books)* Stop CloneStoredBook eagerly loading Author on every book save (#163) - ([fb51f08](https://github.com/snapetech-org/chaptarrng/commit/fb51f088deabb291923ed5e036271cfdd745ec6a))
- *(commands)* Throttle progress-message persistence to once per second per command - ([677feb4](https://github.com/snapetech-org/chaptarrng/commit/677feb4360ab807777e4314104cdce5210bae477))
- *(covers)* Skip per-book cover reconciliation when a refresh made no changes (#178) - ([394e840](https://github.com/snapetech-org/chaptarrng/commit/394e84080206ae68bd83f6f6cefe927116a4387e))
- *(covers)* Stop enumerating MediaCover/Books for every book without a cover folder - ([5f4d9b5](https://github.com/snapetech-org/chaptarrng/commit/5f4d9b568d131bca3dd4a2a7a9d346016283ddf7))
- *(db)* Index MetadataFiles/ExtraFiles by (AuthorId, BookId) - ([f04f995](https://github.com/snapetech-org/chaptarrng/commit/f04f995097fd0c7ace76a9f72a922af809ed3b0a))
- *(extras)* Batch the post-scan metadata-file cleanup and probe the right base path first - ([0477a7a](https://github.com/snapetech-org/chaptarrng/commit/0477a7aaca40c98c3cbf45a91f0e2b3fdff22964))
- *(frontend)* Integrate upstream bundle optimizations - ([d7bc6be](https://github.com/snapetech-org/chaptarrng/commit/d7bc6be3fd2a611fa5a23f8e9ae576c99315858d))
- *(frontend)* Code-split routes with React.lazy and webpack splitChunks - ([4fa4ced](https://github.com/snapetech-org/chaptarrng/commit/4fa4ced1e56eba168ec9201380a84042d135d5e6))
- *(import)* Avoid hashing opted-in destinations - ([076b539](https://github.com/snapetech-org/chaptarrng/commit/076b539c18eec55b6a1c548b1d5b23aa95ca312c))
- *(import-lists)* Optimize local library lookup during import list sync - ([c15fce3](https://github.com/snapetech-org/chaptarrng/commit/c15fce397c4cefb489e7b2ae7ba02a7faf694081))
- *(manual-import)* Reuse an existing local work instead of reconciling the whole author catalogue - ([d1ce838](https://github.com/snapetech-org/chaptarrng/commit/d1ce838453e6d5c1a61f20b2eb130588a6636889))
- *(matching)* Don't use bare function words as Postgres FTS recall keys - ([d246e1d](https://github.com/snapetech-org/chaptarrng/commit/d246e1d85532ee80730d1d85fa158fcbc7f0060c))
- *(refresh)* Stop refetching Book+Author+Editions+Files per book in author refresh (#163) - ([4922412](https://github.com/snapetech-org/chaptarrng/commit/49224120faf5a93f9f07f640781a1b5e3cb5c634))
- Clone monitoring snapshots only for changed work groups - ([3b804d9](https://github.com/snapetech-org/chaptarrng/commit/3b804d960b5a477a519706c524d0f981e3f6e394))
- Batch the author-list N+1 queries, the queue conversion-job lookups and bound the Postgres FTS recall candidates - ([a415889](https://github.com/snapetech-org/chaptarrng/commit/a41588968836d73f1df28abc84bb12d89a6911c6))
- Prune common tokens from the Postgres FTS book recall - ([2340b99](https://github.com/snapetech-org/chaptarrng/commit/2340b992166c63cf9b7c26c0b1d371032cc7d691))
- Count missing books with a semi-join and bound per-author file statistics - ([76c7c22](https://github.com/snapetech-org/chaptarrng/commit/76c7c224927297b0cef38c064de033db02df0417))

### Refactoring
- *(frontend)* Replace moment.js with dayjs - ([9751a80](https://github.com/snapetech-org/chaptarrng/commit/9751a809bdf4189687d4546a0db7a6fce8ef7b17))
- *(frontend)* Use cherry-picked lodash imports - ([98e43db](https://github.com/snapetech-org/chaptarrng/commit/98e43dbd9562f5a3c2d5abb0ad413a8fb3ddaa96))

### Testing
- *(MyAnonaMouse)* Cover mam_id rotation and persistence - ([3d6923b](https://github.com/snapetech-org/chaptarrng/commit/3d6923b8bc3cb55ff19b76892fad6e38552be284))
- *(author-stats)* Cover future ebook progress - ([ec1308f](https://github.com/snapetech-org/chaptarrng/commit/ec1308f8f02828bba68e75d8134ec97c7e51c44e))
- *(authors)* Cover RanobeDB ID handoff - ([1de1ef7](https://github.com/snapetech-org/chaptarrng/commit/1de1ef7370a327db7ebe059c5e7d021cb50bb9c7))
- *(ci)* Match trusted runner selector grouping (#104) - ([6731a43](https://github.com/snapetech-org/chaptarrng/commit/6731a436d890e2b56ebdb0875726e6250e720080))
- *(ci)* Cover retained pull request head fetch - ([0faa887](https://github.com/snapetech-org/chaptarrng/commit/0faa8876abe564fb563d65cdc230d11651297056))
- Preserve native paths in rename fixtures - ([2eb6cfc](https://github.com/snapetech-org/chaptarrng/commit/2eb6cfc54e6dbc4a0a8f9f9e2d8551b6ff548736))
- Remove duplicate controller fixture arguments - ([085e519](https://github.com/snapetech-org/chaptarrng/commit/085e5198523a5eb120ab094cd13e4fd48d63ddce))
- Use isolated temp paths for rename fixtures - ([f921cea](https://github.com/snapetech-org/chaptarrng/commit/f921ceacf50f628a5af12b5a5e6dde3b5a3efddf))
- Normalize workflow line endings in contract - ([5e51bf7](https://github.com/snapetech-org/chaptarrng/commit/5e51bf70bdf2e22ceffbaa0f6b3e857ba3e385d1))
- Preserve native paths in rename fixtures - ([c5c14d8](https://github.com/snapetech-org/chaptarrng/commit/c5c14d8d9b58dbd4ec2e1c5bf2bd1eef213a1415))
- Use isolated temp paths for rename fixtures - ([81d5789](https://github.com/snapetech-org/chaptarrng/commit/81d578938bdf5424da60f82b50570c745b273d14))
- Use fully qualified rename fixture paths - ([5ad2d33](https://github.com/snapetech-org/chaptarrng/commit/5ad2d33e5cdd216bb17bf500d77a8233e5526aa9))
- Use rooted paths for rename fixtures - ([cd8d534](https://github.com/snapetech-org/chaptarrng/commit/cd8d53469d2b6e3cac6b706f31861b83ff2b7b95))
- Update localization service stubs - ([54a5efa](https://github.com/snapetech-org/chaptarrng/commit/54a5efa649b9dd75caf4dfb5bfcac981f784024d))
- Make rename fixture paths platform-native - ([6e4211d](https://github.com/snapetech-org/chaptarrng/commit/6e4211dd3eb58ff51963c8d327274fb36c56ca60))
- Normalize fake paths across platforms - ([6fa426c](https://github.com/snapetech-org/chaptarrng/commit/6fa426ca8461e28ac343a111939108c7a04b794c))
- Cover bookless download history - ([2a0c2e4](https://github.com/snapetech-org/chaptarrng/commit/2a0c2e471b5268bc544540f5fdf23fa25a70c651))
- Guard Bookshelf database before migrations - ([35ca5cd](https://github.com/snapetech-org/chaptarrng/commit/35ca5cd563468337fb806b0a73ea09a230ddb65c))
- Adapt wanted narration repository to lifecycle persistence - ([1e84bd2](https://github.com/snapetech-org/chaptarrng/commit/1e84bd288d6c91da263924a5a41f39270ca33d4f))
- Adapt tag prefetch fixtures to current filesystem abstraction - ([b383b6b](https://github.com/snapetech-org/chaptarrng/commit/b383b6b88c990a2ed7791b20b7d0ab4b59a27374))
- Support bulk edition loading in cross-format insert fixture - ([7fa62a6](https://github.com/snapetech-org/chaptarrng/commit/7fa62a6c59c000f1f5e69cbcfb0b0af6a6b5d4ad))
- Construct malformed UTF-16 cases outside attribute metadata - ([bebf3cd](https://github.com/snapetech-org/chaptarrng/commit/bebf3cd29b007340efd41da260f9916b4176f8f3))

### Maintenance
- *(distribution)* Remove YunoHost publishing support - ([c26dd7f](https://github.com/snapetech-org/chaptarrng/commit/c26dd7f366bb0c7af1c7c96455ad0dec020ac158))
- *(distribution)* Retire YunoHost packaging - ([ee048be](https://github.com/snapetech-org/chaptarrng/commit/ee048be417b84f049f5e2d41f726d6b869fed807))
- *(frontend)* Remove moment dependency - ([7bb5ccf](https://github.com/snapetech-org/chaptarrng/commit/7bb5ccf4ccab07f9c54d0bd6dd810623446ecec9))
- *(repository)* Migrate GitHub owner links (#109) - ([8b228fd](https://github.com/snapetech-org/chaptarrng/commit/8b228fdccbd8c11fe4ebbd25753b8034a87f05b7))
- Route trusted main validation through shared pool (#101) - ([55ce02f](https://github.com/snapetech-org/chaptarrng/commit/55ce02f37319859ffb3a9ce62ac10541cd08b864))
- Run independent validation tasks in a bounded pool (#99) - ([4364742](https://github.com/snapetech-org/chaptarrng/commit/4364742e7811aca8c501c13897b5dd6daabe1710))## [0.9.970](https://github.com/snapetech-org/chaptarrng/compare/v0.9.969..v0.9.970) - 2026-10-15

### Security
- *(ci)* Keep pull request jobs on hosted runners (#69) - ([02d38e1](https://github.com/snapetech-org/chaptarrng/commit/02d38e186c56554662de2b62d0f24c7b4b1da4f1))
- *(security)* Preserve isolated Playwright browser cache (#67) - ([dd9f0ec](https://github.com/snapetech-org/chaptarrng/commit/dd9f0ec0da245fad140eaf6babe3c3f60b62042a))

### Features
- *(frontend)* Add React Query for server state management - ([5296afe](https://github.com/snapetech-org/chaptarrng/commit/5296afee686104c70ef570801c9c839fd34ca19f))
- *(i18n)* Localize naming builder labels and errors - ([777f217](https://github.com/snapetech-org/chaptarrng/commit/777f2174f488436198fff26a81c331b2bbcb2931))
- *(i18n)* Complete target locale catalogs (#79) - ([ad610a4](https://github.com/snapetech-org/chaptarrng/commit/ad610a40b4259061f066adacf529d8eab6078cbc))
- *(i18n)* Localize audiobook library modes - ([4b9d3b6](https://github.com/snapetech-org/chaptarrng/commit/4b9d3b68f9a2c30984680581b38c6c200257a1bb))
- *(i18n)* Add Indonesian and Traditional Chinese - ([b43855f](https://github.com/snapetech-org/chaptarrng/commit/b43855fe01e5389e37ac3cff3b437d770b47ce34))
- *(i18n)* Expose RTL locale metadata - ([b20b371](https://github.com/snapetech-org/chaptarrng/commit/b20b371bfe6e4d2519ab54b77ca17cef3e81c832))
- *(i18n)* Translate AudioBookShelf root mappings - ([5377439](https://github.com/snapetech-org/chaptarrng/commit/537743906a5261afcb16aac60a8de3de831bde55))
- *(import)* Adopt safe existing destinations - ([f3d8105](https://github.com/snapetech-org/chaptarrng/commit/f3d81054ff1040c0cf329a1a8f4804c3030b1c0c))
- *(import)* Expose manual match provenance - ([6ed1f89](https://github.com/snapetech-org/chaptarrng/commit/6ed1f89389858a1a8d5be54bac43181e7e9a6248))
- *(import-lists)* Add durable sync history and preview - ([108511d](https://github.com/snapetech-org/chaptarrng/commit/108511dde089aa62d8e6bed94aaf46d4721fa253))
- *(media)* Extract archived downloads (.zip, .tgz, .tar.gz) during import when no media files are found - ([e66cafd](https://github.com/snapetech-org/chaptarrng/commit/e66cafd132d900d98dead7305ddedf6c79425d82))
- *(metadata)* Add RanobeDB light novel support - ([1b4918b](https://github.com/snapetech-org/chaptarrng/commit/1b4918b9859708c2e6024c092715d8a32357c754))
- *(metadata-profile)* Add UI for IgnoredGenres, with autocomplete from library genres - ([5aeb3d5](https://github.com/snapetech-org/chaptarrng/commit/5aeb3d5d2e4c82bb15a03f5a77fc6c96c12c2c12))
- *(metadata-profile)* Add IgnoredGenres filter (e.g. exclude comics/graphic novels) - ([4b70af4](https://github.com/snapetech-org/chaptarrng/commit/4b70af49500ca75ab4a773d1771f5abea51ca05f))
- *(metrics)* Expose low-cardinality Prometheus metrics - ([2b4b1f4](https://github.com/snapetech-org/chaptarrng/commit/2b4b1f43f1c9b9f7f6a062f41e7dea32cd4e8e53))
- *(mqtt)* Complete gateway and canonical events - ([d1e4df5](https://github.com/snapetech-org/chaptarrng/commit/d1e4df59e91b92c2e21f0c1b3592a8dbc2eba00c))
- *(mqtt)* Add gateway connection foundation - ([59b07bc](https://github.com/snapetech-org/chaptarrng/commit/59b07bcb2577c7129738197041219bc772dc96bb))
- *(notifications)* Add Emby and Jellyfin refresh provider - ([5ae1487](https://github.com/snapetech-org/chaptarrng/commit/5ae14872f02440db6bea18fb81b0a07718a779d6))
- *(search)* Retry alternate edition title searches - ([8272e5a](https://github.com/snapetech-org/chaptarrng/commit/8272e5a40d89c197f2028d93592032727d8e8303))
- *(validation)* Report live task progress - ([b37b982](https://github.com/snapetech-org/chaptarrng/commit/b37b982538fb5a5989943c635849b1af91140aee))
- Migrate BookshelfNG into standard ChaptarrNG (#100) - ([fa5384e](https://github.com/snapetech-org/chaptarrng/commit/fa5384e80d13157bf805278e900c510c189bc69b))
- Add Audible marketplace setting - ([95b56be](https://github.com/snapetech-org/chaptarrng/commit/95b56be46991179999cb856f48e88dd57db212fb))
- Add BookshelfNG metadata preferences - ([d763525](https://github.com/snapetech-org/chaptarrng/commit/d763525c46f13b5d6bcff11e52e449cceec7bae6))
- Preserve BookLore uploads and protect legacy databases - ([8a231dd](https://github.com/snapetech-org/chaptarrng/commit/8a231dd8d31202a6be3c7f95828ddc9cd3c2344a))

### Bug Fixes
- *(MyAnonaMouse)* Persist refreshed mam_id cookies - ([f4e6c27](https://github.com/snapetech-org/chaptarrng/commit/f4e6c27f53559b71477a649a5dd3a74dea4c99fa))
- *(abs)* Expose full library paths in mappings - ([c6ab3df](https://github.com/snapetech-org/chaptarrng/commit/c6ab3df5a845464210bcb04b542740cb092582f1))
- *(aliases)* Retry ReplaceAliases when a concurrent writer wins the unique-index race - ([0b514cd](https://github.com/snapetech-org/chaptarrng/commit/0b514cdd6c3610b83012a2d8ca2c032d88e07c84))
- *(api)* Persist explicit author monitoring changes - ([43e3faa](https://github.com/snapetech-org/chaptarrng/commit/43e3faabf1c9333444711e64453deb37c90a9fca))
- *(audiobookshelf)* Push the cover after the folder cover.jpg is written - ([e62474e](https://github.com/snapetech-org/chaptarrng/commit/e62474ece93ae76592537d615bfa350ff19f7401))
- *(audiobookshelf)* Send only the cover on a cover-updated event - ([17a678e](https://github.com/snapetech-org/chaptarrng/commit/17a678e2b1b1b0b5108f97215ea898892c4e6dcf))
- *(author)* Stop ApplyChanges wiping stored paths when the update omits them - ([408d58a](https://github.com/snapetech-org/chaptarrng/commit/408d58a98d0b2f057645b40b9f21344263520b26))
- *(books)* Retain format four audiobook fallback - ([59e8c7f](https://github.com/snapetech-org/chaptarrng/commit/59e8c7fc4bc9c77472d09c139018d29189bc3b78))
- *(books)* Use the author-refresh match instead of independently re-deriving it (#182) - ([2262f84](https://github.com/snapetech-org/chaptarrng/commit/2262f84ce2e915ad9bc2fac3fb2837d4f8a47aaa))
- *(books)* Make remote-item match-claim order deterministic during author refresh (#179) - ([30750b3](https://github.com/snapetech-org/chaptarrng/commit/30750b3824d77050200301cfc246c44c3ab72aff))
- *(books)* Compare against the simulated update result, not the raw remote payload (#183) - ([52bdf56](https://github.com/snapetech-org/chaptarrng/commit/52bdf5677dc0ba01c5b54baa073844b6b6e46d2e))
- *(build)* Repair backlog compile failures - ([22a9db6](https://github.com/snapetech-org/chaptarrng/commit/22a9db6de4d533a7b7f897417ef0b9748e9ffff7))
- *(build)* Close SQL verbatim string literal - ([c1592f6](https://github.com/snapetech-org/chaptarrng/commit/c1592f6e780f78a3fad156fca482b67d68e05440))
- *(build)* Pin MQTTnet to published patch version - ([3705a9b](https://github.com/snapetech-org/chaptarrng/commit/3705a9b40c29e1db632082029f6544b72457e0fe))
- *(cache)* Serialize expiry cancellation and release timers on removal - ([3aa4a95](https://github.com/snapetech-org/chaptarrng/commit/3aa4a95de7f513c4a9b536084f1bbe7ba6df4548))
- *(ci)* Repair trusted runner expression (#102) - ([9f736b9](https://github.com/snapetech-org/chaptarrng/commit/9f736b9984eb026d53a91492788ec4cc4ca789da))
- *(ci)* Capture validation receipts before frontend build (#77) - ([ee2efc5](https://github.com/snapetech-org/chaptarrng/commit/ee2efc555f6b11a2d3ade66261d4e625aab24380))
- *(ci)* Run self-hosted PowerShell under private Aqua root - ([22e5f82](https://github.com/snapetech-org/chaptarrng/commit/22e5f82a58e90895951ab68d4d6cad3e807178e6))
- *(ci)* Isolate Aqua package cache per runner job - ([cdc1a39](https://github.com/snapetech-org/chaptarrng/commit/cdc1a3987d53ea4c5c86452d8332f8d7e00dd1d7))
- *(ci)* Fetch merged pull request head for notes - ([a22ea02](https://github.com/snapetech-org/chaptarrng/commit/a22ea02e9067b20fac0561053291795658a782a4))
- *(ci)* Make self-hosted runner temp private - ([be6dfb2](https://github.com/snapetech-org/chaptarrng/commit/be6dfb237f25a73404deecbf3acc744475788703))
- *(commands)* Keep a cancelled running command Started until its handler exits - ([c443d65](https://github.com/snapetech-org/chaptarrng/commit/c443d655b666a7d4e271622ceb7cea4269c52e7c))
- *(commands)* Stop disk-access groups from globally blocking each other - ([05854a8](https://github.com/snapetech-org/chaptarrng/commit/05854a8bc8125f4425c448e1a0a1237a4bcc1d7b))
- *(conversion)* Handle integer progress and incomplete metadata - ([911d194](https://github.com/snapetech-org/chaptarrng/commit/911d194c13b358d75c49f84956c6321272ae2607))
- *(docker)* Use the configurable health check in local-build images (#112) - ([6dde720](https://github.com/snapetech-org/chaptarrng/commit/6dde720a15703f7c33bf56abf813ccb361fb592e))
- *(docker)* Report container health with wget - ([ef6fec6](https://github.com/snapetech-org/chaptarrng/commit/ef6fec66c70a3f106de66b836dc3a000e533e37c))
- *(download)* Bound how long ProcessMonitoredDownloads yields the disk slot - ([b624a08](https://github.com/snapetech-org/chaptarrng/commit/b624a08a2492ec1d67b1f4eb27786ef0b5255026))
- *(download)* Let ProcessMonitoredDownloads yield the disk slot to waiting commands - ([713722d](https://github.com/snapetech-org/chaptarrng/commit/713722d66126e47249ef9484bf096e47eccac363))
- *(download)* Make ProcessMonitoredDownloads honor cancellation - ([44b9f61](https://github.com/snapetech-org/chaptarrng/commit/44b9f61062ac7e921b0303a9d642b2015a0baedf))
- *(downloads)* Close completed import refresh race - ([4fd9696](https://github.com/snapetech-org/chaptarrng/commit/4fd9696732adf50a2d4fcac46ae9510bd9e97eb5))
- *(frontend)* Migrate to react-dnd 16 and react-dnd-multi-backend 8 (#111) - ([cade6a2](https://github.com/snapetech-org/chaptarrng/commit/cade6a21a34fb170a44aca2c282b628ef93b3897))
- *(frontend)* Keep default CSS module exports with css-loader 7 (#110) - ([014d2c1](https://github.com/snapetech-org/chaptarrng/commit/014d2c1b085edbc6e2ff9f756523e433b5440279))
- *(frontend)* Keep search columns usable on mobile - ([c5beffe](https://github.com/snapetech-org/chaptarrng/commit/c5beffe9c09dd4ca9c30a78b4f3c849b2db2d83e))
- *(frontend)* Improve quality labels and indexer names - ([223a874](https://github.com/snapetech-org/chaptarrng/commit/223a874ca2987d30106bb406cc2b97caeb97b97e))
- *(frontend)* Sync direct download contract with docs - ([1c9f53a](https://github.com/snapetech-org/chaptarrng/commit/1c9f53af853a3d95b7eed35d7684ec268672e4e1))
- *(frontend)* Make merged dependency build reproducible - ([df9385c](https://github.com/snapetech-org/chaptarrng/commit/df9385ccbf77640f38495b27c04e52bb9f12ebd4))
- *(frontend)* Add ambient process type for webpack NODE_ENV - ([85291e9](https://github.com/snapetech-org/chaptarrng/commit/85291e93746a5f5c767c23d56789d16d753705eb))
- *(frontend)* Scope service worker to a webworker tsconfig - ([5b286a3](https://github.com/snapetech-org/chaptarrng/commit/5b286a328dd8d1aba959e88f1c3b9a3eab340b39))
- *(frontend)* Register dayjs isBetween plugin used by isInNextWeek - ([e357b0d](https://github.com/snapetech-org/chaptarrng/commit/e357b0d07a91f167083f168bb9b828f83296306f))
- *(frontend)* Wrap backup-save request with Promise.resolve for jQuery Deferred compat - ([a393f47](https://github.com/snapetech-org/chaptarrng/commit/a393f47fb07fb30bdded591f70b4f8e05c03756e))
- *(i18n)* Correct reviewed locale strings and placeholders - ([15a5d6d](https://github.com/snapetech-org/chaptarrng/commit/15a5d6d36960a49eb28d35834c002b2a4ad24075))
- *(import)* Use explicit series tags to verify edition positions - ([fa9e26e](https://github.com/snapetech-org/chaptarrng/commit/fa9e26ecacafbb34570e0640f381b441283aae7e))
- *(import)* Prefer audiobook editions in fallback ties - ([e3c9eff](https://github.com/snapetech-org/chaptarrng/commit/e3c9effa7efa9cb5adf4492293b1a4a3f37e8408))
- *(import)* Reattach renamed EPUBs - ([1ab2354](https://github.com/snapetech-org/chaptarrng/commit/1ab235456092e98e7b6be0e6d613fad1672ea4cf))
- *(import)* Warn about an existing-file conflict in Manual Import preview (#184) - ([7d19f1a](https://github.com/snapetech-org/chaptarrng/commit/7d19f1a791d7f800f1d2c7ad972a3bc6b3c0c867))
- *(import)* Report why a manual import imported nothing - ([9e6e12f](https://github.com/snapetech-org/chaptarrng/commit/9e6e12f8d9f8e6f05546a7c8e93d8666c562ccc4))
- *(import)* Don't let an explicit Move override "this torrent's files cannot be moved" - ([95e47ed](https://github.com/snapetech-org/chaptarrng/commit/95e47edfdf0de8f26f75ed53a5b44986ae69b775))
- *(import-lists)* Commit cursor on the fetching list instance after processing - ([925fd50](https://github.com/snapetech-org/chaptarrng/commit/925fd507a6252d7cb468ce013d2a8ce5729153dd))
- *(import-lists)* Defer Hardcover library sync state persistence until list processing succeeds - ([da77b3f](https://github.com/snapetech-org/chaptarrng/commit/da77b3f62135f60e8984b2a8fc39dfe011d1ff4d))
- *(manual-import)* Match an inferred edition structurally when it carries no provider edition id - ([237fd00](https://github.com/snapetech-org/chaptarrng/commit/237fd002b481a1f7aa26b47159b1b81804e424a9))
- *(manual-import)* Keep the plain exception type for explicit edition ids - ([1104c0e](https://github.com/snapetech-org/chaptarrng/commit/1104c0e86ff7dd0f5a89a269be63bc029e24e191))
- *(manual-import)* Use the reconciled book's monitored edition when a suggested work has many editions - ([0f99a61](https://github.com/snapetech-org/chaptarrng/commit/0f99a61e4ef62d58ccd22a576e6419b25d7ade56))
- *(manual-import)* Fall back to the provider's default edition when a suggested work has many editions - ([32133b4](https://github.com/snapetech-org/chaptarrng/commit/32133b4c8ecfaa44b437c7d1088be5fe6c88a062))
- *(manual-import)* Materialize a suggested work when the suggestion has no edition id - ([79da2f8](https://github.com/snapetech-org/chaptarrng/commit/79da2f8472a6ee4154434cd03333d48b77ef1bbd))
- *(media)* Handle missing root folder during upgrade import with RootFolderNotFoundException - ([ee7ebc7](https://github.com/snapetech-org/chaptarrng/commit/ee7ebc7cd8d6e294bbe55b3813a03b97ac03be59))
- *(metrics)* Disambiguate queue and health-check models - ([cf84c7d](https://github.com/snapetech-org/chaptarrng/commit/cf84c7d477abc100b4deecb732c99a578d87a818))
- *(migration)* Preserve legacy author monitoring choice - ([1261261](https://github.com/snapetech-org/chaptarrng/commit/12612611c096238e60e5c9b2e157a09510a6f9e0))
- *(migrations)* Retain fork cache migration 108 and number query indexes 109 - ([6464c53](https://github.com/snapetech-org/chaptarrng/commit/6464c53d542353b0e149bd6d4ee1095500ed0d7f))
- *(monitoring)* Keep specific-book adds from monitoring the whole catalogue - ([8ed8199](https://github.com/snapetech-org/chaptarrng/commit/8ed819935da1784392f1167d41fd64d888f1a6ba))
- *(naming)* Populate audiobook media info tokens - ([75e9bb8](https://github.com/snapetech-org/chaptarrng/commit/75e9bb89d9c8697fc7c8ba1190035e5454f8afdb))
- *(notifications)* Trace release import dispatch - ([a23e3c6](https://github.com/snapetech-org/chaptarrng/commit/a23e3c6719f22cec6fd55707185bbc37e68993b5))
- *(perf)* Address large-library review follow-ups - ([281fe16](https://github.com/snapetech-org/chaptarrng/commit/281fe16649409d6b306a6c3fbdba5a7c39423f77))
- *(queue)* Enforce torrent preservation on client removal - ([9de8444](https://github.com/snapetech-org/chaptarrng/commit/9de84446a295f96ede90bb746bf6ca1c3ead5deb))
- *(repo)* Remove a stray node_modules link (#119) - ([7918713](https://github.com/snapetech-org/chaptarrng/commit/79187131e40b8c782a0c35da4fbc05be6add69e4))
- *(search)* Warn when indexer lacks media categories - ([0f3050c](https://github.com/snapetech-org/chaptarrng/commit/0f3050c4d2ebf45a631bbf7d1c2ad67ca78c5a76))
- *(search)* Refresh editions after language profile expansion - ([55eddba](https://github.com/snapetech-org/chaptarrng/commit/55eddba12ef6395adabcf2b8bfc4944d26d3b1c3))
- *(security)* Update Pkcs dependency to patched version (#108) - ([cde05c8](https://github.com/snapetech-org/chaptarrng/commit/cde05c8335adc164fdabddf4af6ce6ca11ec35ce))
- *(security)* Mount dependencies read-only in place - ([ee36f13](https://github.com/snapetech-org/chaptarrng/commit/ee36f139554947e0aeb68ffe60d7209350cda8ea))
- *(security)* Require trusted test sandbox - ([e775662](https://github.com/snapetech-org/chaptarrng/commit/e77566240e792ef0c7435a24ad161abd092fdb2a))
- *(security)* Preserve isolated Playwright browser cache - ([d63f7e1](https://github.com/snapetech-org/chaptarrng/commit/d63f7e1097385c75935204cb810efb6162c0495d))
- *(security)* Preserve isolated Playwright browser cache - ([d6e4f23](https://github.com/snapetech-org/chaptarrng/commit/d6e4f2357b148dc30385f5680d00d61b628566ab))
- *(security)* Isolate validation task environments - ([9d9d7e9](https://github.com/snapetech-org/chaptarrng/commit/9d9d7e9ad657cca9acd668f07903156ca9d1c22a))
- *(security)* Isolate validation task environments - ([6e41c72](https://github.com/snapetech-org/chaptarrng/commit/6e41c7275d51719f58db02c513cdac2e83d6774d))
- *(ui)* Keep toolbar overflow actions accessible - ([9fd105f](https://github.com/snapetech-org/chaptarrng/commit/9fd105fbaef9b694169ae1eb4eed57559aa4244e))
- *(ui)* Stop the book page refetching forever for a book that does not exist - ([9b4fa4f](https://github.com/snapetech-org/chaptarrng/commit/9b4fa4f387084322d142ebc9a78239893ef59d4d))
- *(ui)* Read the author's real folder fields (audiobookFolder / ebookFolder) in the delete dialog - ([4707e05](https://github.com/snapetech-org/chaptarrng/commit/4707e052301e3dd9e9974f491f36e3d8ae68db26))
- *(ui)* Delete-author dialog lists every author folder, not only the legacy path - ([dc8b4a3](https://github.com/snapetech-org/chaptarrng/commit/dc8b4a39a6fe4c9d4d284fbf05eb046128ce5ffd))
- *(ui)* Don't open a blank confirmation when a manual import replaces no local book - ([58a40ad](https://github.com/snapetech-org/chaptarrng/commit/58a40ad61ac1d513b9a32d4ae2286854f863819a))
- *(validation)* Prevent nested sandbox contract hangs (#103) - ([9b7b1d1](https://github.com/snapetech-org/chaptarrng/commit/9b7b1d157cc84e2f496fdaa76a58739c8c22d419))
- *(validation)* Expose isolated Playwright browser cache - ([d8b8e76](https://github.com/snapetech-org/chaptarrng/commit/d8b8e76f4dcc6c42daa125d46d15de4128676228))
- *(wanted)* Use edition release date - ([ff74478](https://github.com/snapetech-org/chaptarrng/commit/ff74478f3c3c65c3c04e5e57f445ec2fc8c1d7a2))
- Restore M4B chapter release note and package update-mechanism tests (#113) - ([0c05812](https://github.com/snapetech-org/chaptarrng/commit/0c058129bf7bfb60f5da0e1a46968d9151668c17))
- Retry unmatched files during scans - ([a8bd0ba](https://github.com/snapetech-org/chaptarrng/commit/a8bd0baabc9e551803b5e77573bf9ce8bb05defe))
- Sequence combined database migrations - ([1702049](https://github.com/snapetech-org/chaptarrng/commit/17020494db6c3ee322dc6cb3c0a0a18375384eac))
- Resolve book matching edge cases and validation inventory drift - ([197597b](https://github.com/snapetech-org/chaptarrng/commit/197597b75e6657748418204c4c65210ec448e3dd))
- Harden import matching and conversion cancellation - ([4582c13](https://github.com/snapetech-org/chaptarrng/commit/4582c13317d0d0c4574e2f968a3e78f9baa96821))
- Reconcile migration versions and stale test fixtures - ([dd09594](https://github.com/snapetech-org/chaptarrng/commit/dd0959432ebb2de58b097b73d0e609bda4d7bc7d))
- Preserve cancellation and fatal M4B errors - ([5fc61e4](https://github.com/snapetech-org/chaptarrng/commit/5fc61e4d73336347379ff3fa6548306b47a458d7))
- Keep overlapping casts and dramatized editions as distinct wanted variants - ([e03b995](https://github.com/snapetech-org/chaptarrng/commit/e03b995023fd2db2d886cd3c92e6aeee97d226ec))
- Reject cross-format imports without an author root and preserve cancellation - ([8a9d759](https://github.com/snapetech-org/chaptarrng/commit/8a9d7590177b7a55a8c004732d791875878b56fe))
- Bound cover backoff and evict tag cache entries without clearing hot data - ([1efc938](https://github.com/snapetech-org/chaptarrng/commit/1efc93813529c2dfb86f35a7b8a8c19c82fe8dcd))
- Expose shelf load retries and discard stale count responses - ([846bd74](https://github.com/snapetech-org/chaptarrng/commit/846bd740820091364c6afd6b7184b1f13390e805))
- Reconcile format-scoped list and shelf query interfaces - ([8f3bf84](https://github.com/snapetech-org/chaptarrng/commit/8f3bf84a9b1201c3f0c81aafef89e0246b61edba))
- Reconcile background deletion tests and metadata migration ordering - ([7b3e5f5](https://github.com/snapetech-org/chaptarrng/commit/7b3e5f564bbaa8a84f404a5ed8c2c5ff40d29bdc))
- Align ebook tags with authoritative series and preserve file permissions - ([8a5b58a](https://github.com/snapetech-org/chaptarrng/commit/8a5b58aef71d803cf599ba33cbbff390104b961f))
- Rotate download sweeps fairly without unbounded disk-slot retention - ([ba4263d](https://github.com/snapetech-org/chaptarrng/commit/ba4263d637b44ddd539057685d19b789527fb721))
- Serialize queue snapshots and propagate fatal preview failures - ([037928d](https://github.com/snapetech-org/chaptarrng/commit/037928d146742a1360e407fbca641d47237bbccc))
- Deduplicate command batches and preserve ebook reader failure semantics - ([0229587](https://github.com/snapetech-org/chaptarrng/commit/02295879d7e5e136ee1909a3693cf17fb7405474))

### Documentation
- *(backlog)* Correct HTTP cache migration version - ([46fa890](https://github.com/snapetech-org/chaptarrng/commit/46fa8908a81df4093801c29b6edb4f45fb7e4f53))
- *(backlog)* Record Prometheus metrics implementation - ([f256058](https://github.com/snapetech-org/chaptarrng/commit/f2560586df45a251585fdfd41009aca19bd794b1))
- *(backlog)* Record SABnzbd race fix - ([fe5c003](https://github.com/snapetech-org/chaptarrng/commit/fe5c003a11f67a2a442d8d50f708fb4e4e749777))
- *(backlog)* Record MQTT gateway implementation - ([5a42069](https://github.com/snapetech-org/chaptarrng/commit/5a42069dd265add5eeb7821e7041a96b0abb9d08))
- *(backlog)* Track MQTT gateway implementation - ([ebe2c52](https://github.com/snapetech-org/chaptarrng/commit/ebe2c524e0f6c1801b529004951efff0d4f9ffd7))
- *(backlog)* Record Emby Jellyfin integration - ([cdaa7dd](https://github.com/snapetech-org/chaptarrng/commit/cdaa7dd52587228abc38a7f7f91d855d818ff2f3))
- *(backlog)* Note Proxmox eligibility hold - ([f0b030b](https://github.com/snapetech-org/chaptarrng/commit/f0b030b3f2cba68af40b43f709ad0a3fb1b06327))
- *(backlog)* Record mobile search title fix - ([7dcb551](https://github.com/snapetech-org/chaptarrng/commit/7dcb5518b0965f86bd509a1a2a80b2f1006da6a2))
- *(backlog)* Record audiobook fallback fix - ([ed0ffcb](https://github.com/snapetech-org/chaptarrng/commit/ed0ffcb22ed5ad812abba45a594710e39410a9c2))
- *(backlog)* Record wanted release-date fix - ([47c9412](https://github.com/snapetech-org/chaptarrng/commit/47c941279bd95e882f4889321a6138ca8958e7bd))
- *(backlog)* Disposition series primary filter reports - ([d55aee2](https://github.com/snapetech-org/chaptarrng/commit/d55aee2941cb46c01f04f65b1aad0ed66c37a837))
- *(backlog)* Disposition author progress reports - ([5be115b](https://github.com/snapetech-org/chaptarrng/commit/5be115b55f36350ed650c8ce046c4617fa38b16f))
- *(backlog)* Record author merge coverage - ([d842f1d](https://github.com/snapetech-org/chaptarrng/commit/d842f1d5eef2d24014ed210db4b0b4c81ac0339f))
- *(i18n)* Document localized catalog review - ([68e1330](https://github.com/snapetech-org/chaptarrng/commit/68e1330edf68abdb95f20914b0c8c703b7e129f3))
- Clarify cumulative upstream blocker set - ([24b340f](https://github.com/snapetech-org/chaptarrng/commit/24b340fffc43eb0883995a21665d3d051eb66abe))
- Pin latest ChaptarrNG blocker snapshot - ([fc1e0cc](https://github.com/snapetech-org/chaptarrng/commit/fc1e0ccc8b678952ae77cb5c1a0b02c7bd17e5c6))
- Refresh cumulative blocker snapshot - ([22078e0](https://github.com/snapetech-org/chaptarrng/commit/22078e0289949dbc41ee603cd10b80c2510f1e78))
- Itemize cumulative upstream retirement blockers - ([066258f](https://github.com/snapetech-org/chaptarrng/commit/066258f6f96d20dd5ab2552ce1b16437c541a971))
- Preserve shipped YunoHost release note - ([c416406](https://github.com/snapetech-org/chaptarrng/commit/c4164065c1d1d188549d5444699b13e1feae3c16))
- Document current fork additions and integrations - ([2a8532e](https://github.com/snapetech-org/chaptarrng/commit/2a8532e232b4c3fae6fce16354959d4bf76839a5))
- Record import adoption IO safeguard - ([b57bbd8](https://github.com/snapetech-org/chaptarrng/commit/b57bbd8b8476ad8f406e84b2ba80f496ad3f31cb))
- Disposition occupied import destination requests - ([c7dd0fc](https://github.com/snapetech-org/chaptarrng/commit/c7dd0fc927bcd793a4ac85a5a091d68212c91eb6))
- Disposition closed metadata outage report - ([46b7182](https://github.com/snapetech-org/chaptarrng/commit/46b718208f2404e94d6daed9f9db76c15704f0ac))
- Disposition Readarr book add regression - ([ef6b804](https://github.com/snapetech-org/chaptarrng/commit/ef6b804fdbc3cfd672172ce858cfc5e7303ae782))
- Disposition merged book deletion fix - ([b295203](https://github.com/snapetech-org/chaptarrng/commit/b295203cae77e5e50598bdb12538885b67980bbc))
- Address README backlog requests - ([d5465f2](https://github.com/snapetech-org/chaptarrng/commit/d5465f2ee98fab49cf40a321187f7ab8430ada70))
- Audit compatibility and build PRs - ([8f3b00c](https://github.com/snapetech-org/chaptarrng/commit/8f3b00ccc64b575616d43500a2ae0cd2dcc4fc7b))
- Audit import webhook issue - ([5a8243e](https://github.com/snapetech-org/chaptarrng/commit/5a8243e090cfb78bdb79582c4f74cae91efd3a8a))
- Audit bookshelf monitoring issues - ([fa80bc4](https://github.com/snapetech-org/chaptarrng/commit/fa80bc4738014374e020300a302f7512743572e9))
- Audit author monitoring issues - ([548f31f](https://github.com/snapetech-org/chaptarrng/commit/548f31f950dba63d22bbcecc5ef24a61240eec31))
- Audit book added timestamp coverage - ([467ddfb](https://github.com/snapetech-org/chaptarrng/commit/467ddfb84501d0ae362381f60bc8d4969db8a72c))
- Record empty media category warning coverage - ([503a20f](https://github.com/snapetech-org/chaptarrng/commit/503a20fecd0ad86591b8e611f2a0ecbe1e32f816))
- Record conversion scheduler issue coverage - ([3a0c7ec](https://github.com/snapetech-org/chaptarrng/commit/3a0c7ec2dba3fabc1d69eb5579c6ac0485673133))
- Record alternate edition search completion - ([8699ed7](https://github.com/snapetech-org/chaptarrng/commit/8699ed72f5173fff6bae81f84a7983c532fd4a92))
- Record mobile toolbar issue fix - ([3f57d9c](https://github.com/snapetech-org/chaptarrng/commit/3f57d9cf839d46f06e9aba6694ecc2270eaefdea))
- Validate English edition selection coverage - ([cacf9e2](https://github.com/snapetech-org/chaptarrng/commit/cacf9e27078eb714b772b7b89503951e25bc55ff))
- Record manual import match evidence - ([565ed91](https://github.com/snapetech-org/chaptarrng/commit/565ed915eb4906512a9d64eca2ca46667f0e1dff))
- Record subtitle title matching coverage - ([40f0140](https://github.com/snapetech-org/chaptarrng/commit/40f01402199af7dbb974f92d4ed7564b13bf4e32))
- Record audiobook edition matching fix - ([7b510ee](https://github.com/snapetech-org/chaptarrng/commit/7b510eefb41f0ed3b7ba1a0c7cac28476cd46bf7))
- Validate audiobook selection release note - ([a7fc8b5](https://github.com/snapetech-org/chaptarrng/commit/a7fc8b5d830681a040a85dc927d8f215f17f58cc))
- Record EPUB rename backlog fix - ([76c45c4](https://github.com/snapetech-org/chaptarrng/commit/76c45c4ea7087074a4b03dc4159d49ad02a8f679))
- Record Audible backlog dispositions - ([a0cad97](https://github.com/snapetech-org/chaptarrng/commit/a0cad97adcfc8974d2702a14a586b7a2ab303824))
- Record media info naming token fix - ([5be5bca](https://github.com/snapetech-org/chaptarrng/commit/5be5bca95ee10f10f9e703014d321ca3e951f0ca))
- Record responsive interactive search issue fix - ([1e4ef06](https://github.com/snapetech-org/chaptarrng/commit/1e4ef063a00012a3a5a946c496631f2e8b067b71))
- Record interactive search UI issue fixes - ([6bd6bd6](https://github.com/snapetech-org/chaptarrng/commit/6bd6bd6b362ddf7884c00070904cb80ee478abf3))
- Record direct download PR coverage - ([e49cf0d](https://github.com/snapetech-org/chaptarrng/commit/e49cf0d91f453b94788e9873f0f004ae7ef42bdb))
- Record container and image-audit fixes - ([9f8e02a](https://github.com/snapetech-org/chaptarrng/commit/9f8e02a96dd515fce5fc6be4f67178f38566af40))
- Record migration 104 backlog resolution - ([0d8a0ae](https://github.com/snapetech-org/chaptarrng/commit/0d8a0ae5923e8fa595b854d5b36f6ac4167ad825))
- Refresh upstream backlog through PR19 - ([6495a8e](https://github.com/snapetech-org/chaptarrng/commit/6495a8e564aee552368ab34e77d1c54e4ad41e3b))
- Clarify Chaptarr upstream boundary - ([ddb79d6](https://github.com/snapetech-org/chaptarrng/commit/ddb79d63d028d5a4f71a9f63ab7f66dcb271902e))
- Add ChaptarrNG agent guidance - ([2271d59](https://github.com/snapetech-org/chaptarrng/commit/2271d593748fbbe267a9ea01a2f89af07868e484))
- Pin migration regression validation status - ([5f52032](https://github.com/snapetech-org/chaptarrng/commit/5f52032d29cb19ce969642dbec535d06d6ea353c))
- Detail Bookshelf data migration blockers - ([a4d8ad9](https://github.com/snapetech-org/chaptarrng/commit/a4d8ad95c64daa50896094eec0bedbc3dc29cb4a))
- Clarify localization parity scope - ([5702db2](https://github.com/snapetech-org/chaptarrng/commit/5702db296420f8c091d270864b815f69308b2c2a))
- Include active upstream retirement blockers - ([f8f0c4a](https://github.com/snapetech-org/chaptarrng/commit/f8f0c4adb8d13c6817053d9d39b5c9bb40b42cde))
- Refresh ChaptarrNG retirement inventory - ([af3be47](https://github.com/snapetech-org/chaptarrng/commit/af3be47be45d8b41f5ce807eb4269537a9bad3d8))
- Make README Ko-fi link discreet - ([a6be38d](https://github.com/snapetech-org/chaptarrng/commit/a6be38dd5551b513b22e2efd4504a5ee3e73895b))
- Add Ko-fi sponsor link - ([aa54820](https://github.com/snapetech-org/chaptarrng/commit/aa5482087db4d86930dd7cb43101aa9a83e57a23))
- Record AudioBookShelf propagation audit evidence - ([2095cd7](https://github.com/snapetech-org/chaptarrng/commit/2095cd746106b12c16a81ca0d3323aa8d4f57b70))
- Track upstream backlog integration progress - ([d1e2a5c](https://github.com/snapetech-org/chaptarrng/commit/d1e2a5c95d011cf2af5d29d86719c86735de81f3))
- Update upstream coverage after import and cache integration - ([a965abb](https://github.com/snapetech-org/chaptarrng/commit/a965abb30377bc71421d70017d1298ca1ff31d35))
- Clarify indexer seed time and torrent preservation settings - ([6763dca](https://github.com/snapetech-org/chaptarrng/commit/6763dca10dd7716ee2d8d49fc94a434f14af2136))
- Record integrated upstream patches and validation checkpoints - ([6b4fb08](https://github.com/snapetech-org/chaptarrng/commit/6b4fb08edd07dd37da256880bec72c158a4fae5f))
- Capture upstream discussions and record first integrated fixes - ([1731fcb](https://github.com/snapetech-org/chaptarrng/commit/1731fcba4d1a850f71f95d1e0723a1cfbc889b5d))
- Track complete upstream issue and PR inventory - ([ad0be6d](https://github.com/snapetech-org/chaptarrng/commit/ad0be6d367cf3f25137dbbe33e6476d70cee18de))
- Explain ChaptarrNG fork boundary - ([47fa5fe](https://github.com/snapetech-org/chaptarrng/commit/47fa5fe84e24a04cd1254a5733c5092641d21445))

### Performance
- *(books)* Load editions in bulk before format-sync work grouping instead of one query per book - ([eda1eaa](https://github.com/snapetech-org/chaptarrng/commit/eda1eaa8e3a212d02e19a33715358a4b205dc958))
- *(books)* Integrate upstream PR 218 with scoped refresh caches - ([8247df9](https://github.com/snapetech-org/chaptarrng/commit/8247df96ef93701369c8d579fcf6f26a159a8257))
- *(books)* Replace BuildWorkGroups's O(N^2) pairwise matching with a token-bucketed union-find (#163, #173) - ([775edaf](https://github.com/snapetech-org/chaptarrng/commit/775edaf8e1c986593a667c40da43e5384462623c))
- *(books)* Stop re-fetching RootFolders per work group during monitoring sync (#163) - ([73a7b26](https://github.com/snapetech-org/chaptarrng/commit/73a7b269ea4adf8b3a70ddb7b1bdf51e5abdda52))
- *(books)* Also stop CloneStoredBook eagerly resolving edition-level ASIN - ([05da1d6](https://github.com/snapetech-org/chaptarrng/commit/05da1d629c534fe3e9e3ae2bc46ccfff731f1bcf))
- *(books)* Stop CloneStoredBook eagerly loading Author on every book save (#163) - ([fb51f08](https://github.com/snapetech-org/chaptarrng/commit/fb51f088deabb291923ed5e036271cfdd745ec6a))
- *(commands)* Throttle progress-message persistence to once per second per command - ([677feb4](https://github.com/snapetech-org/chaptarrng/commit/677feb4360ab807777e4314104cdce5210bae477))
- *(covers)* Skip per-book cover reconciliation when a refresh made no changes (#178) - ([394e840](https://github.com/snapetech-org/chaptarrng/commit/394e84080206ae68bd83f6f6cefe927116a4387e))
- *(covers)* Stop enumerating MediaCover/Books for every book without a cover folder - ([5f4d9b5](https://github.com/snapetech-org/chaptarrng/commit/5f4d9b568d131bca3dd4a2a7a9d346016283ddf7))
- *(db)* Index MetadataFiles/ExtraFiles by (AuthorId, BookId) - ([f04f995](https://github.com/snapetech-org/chaptarrng/commit/f04f995097fd0c7ace76a9f72a922af809ed3b0a))
- *(extras)* Batch the post-scan metadata-file cleanup and probe the right base path first - ([0477a7a](https://github.com/snapetech-org/chaptarrng/commit/0477a7aaca40c98c3cbf45a91f0e2b3fdff22964))
- *(frontend)* Integrate upstream bundle optimizations - ([d7bc6be](https://github.com/snapetech-org/chaptarrng/commit/d7bc6be3fd2a611fa5a23f8e9ae576c99315858d))
- *(frontend)* Code-split routes with React.lazy and webpack splitChunks - ([4fa4ced](https://github.com/snapetech-org/chaptarrng/commit/4fa4ced1e56eba168ec9201380a84042d135d5e6))
- *(import)* Avoid hashing opted-in destinations - ([076b539](https://github.com/snapetech-org/chaptarrng/commit/076b539c18eec55b6a1c548b1d5b23aa95ca312c))
- *(import-lists)* Optimize local library lookup during import list sync - ([c15fce3](https://github.com/snapetech-org/chaptarrng/commit/c15fce397c4cefb489e7b2ae7ba02a7faf694081))
- *(manual-import)* Reuse an existing local work instead of reconciling the whole author catalogue - ([d1ce838](https://github.com/snapetech-org/chaptarrng/commit/d1ce838453e6d5c1a61f20b2eb130588a6636889))
- *(matching)* Don't use bare function words as Postgres FTS recall keys - ([d246e1d](https://github.com/snapetech-org/chaptarrng/commit/d246e1d85532ee80730d1d85fa158fcbc7f0060c))
- *(refresh)* Stop refetching Book+Author+Editions+Files per book in author refresh (#163) - ([4922412](https://github.com/snapetech-org/chaptarrng/commit/49224120faf5a93f9f07f640781a1b5e3cb5c634))
- Clone monitoring snapshots only for changed work groups - ([3b804d9](https://github.com/snapetech-org/chaptarrng/commit/3b804d960b5a477a519706c524d0f981e3f6e394))
- Batch the author-list N+1 queries, the queue conversion-job lookups and bound the Postgres FTS recall candidates - ([a415889](https://github.com/snapetech-org/chaptarrng/commit/a41588968836d73f1df28abc84bb12d89a6911c6))
- Prune common tokens from the Postgres FTS book recall - ([2340b99](https://github.com/snapetech-org/chaptarrng/commit/2340b992166c63cf9b7c26c0b1d371032cc7d691))
- Count missing books with a semi-join and bound per-author file statistics - ([76c7c22](https://github.com/snapetech-org/chaptarrng/commit/76c7c224927297b0cef38c064de033db02df0417))

### Refactoring
- *(frontend)* Replace moment.js with dayjs - ([9751a80](https://github.com/snapetech-org/chaptarrng/commit/9751a809bdf4189687d4546a0db7a6fce8ef7b17))
- *(frontend)* Use cherry-picked lodash imports - ([98e43db](https://github.com/snapetech-org/chaptarrng/commit/98e43dbd9562f5a3c2d5abb0ad413a8fb3ddaa96))

### Testing
- *(MyAnonaMouse)* Cover mam_id rotation and persistence - ([3d6923b](https://github.com/snapetech-org/chaptarrng/commit/3d6923b8bc3cb55ff19b76892fad6e38552be284))
- *(author-stats)* Cover future ebook progress - ([ec1308f](https://github.com/snapetech-org/chaptarrng/commit/ec1308f8f02828bba68e75d8134ec97c7e51c44e))
- *(authors)* Cover RanobeDB ID handoff - ([1de1ef7](https://github.com/snapetech-org/chaptarrng/commit/1de1ef7370a327db7ebe059c5e7d021cb50bb9c7))
- *(ci)* Match trusted runner selector grouping (#104) - ([6731a43](https://github.com/snapetech-org/chaptarrng/commit/6731a436d890e2b56ebdb0875726e6250e720080))
- *(ci)* Cover retained pull request head fetch - ([0faa887](https://github.com/snapetech-org/chaptarrng/commit/0faa8876abe564fb563d65cdc230d11651297056))
- Preserve native paths in rename fixtures - ([2eb6cfc](https://github.com/snapetech-org/chaptarrng/commit/2eb6cfc54e6dbc4a0a8f9f9e2d8551b6ff548736))
- Remove duplicate controller fixture arguments - ([085e519](https://github.com/snapetech-org/chaptarrng/commit/085e5198523a5eb120ab094cd13e4fd48d63ddce))
- Use isolated temp paths for rename fixtures - ([f921cea](https://github.com/snapetech-org/chaptarrng/commit/f921ceacf50f628a5af12b5a5e6dde3b5a3efddf))
- Normalize workflow line endings in contract - ([5e51bf7](https://github.com/snapetech-org/chaptarrng/commit/5e51bf70bdf2e22ceffbaa0f6b3e857ba3e385d1))
- Preserve native paths in rename fixtures - ([c5c14d8](https://github.com/snapetech-org/chaptarrng/commit/c5c14d8d9b58dbd4ec2e1c5bf2bd1eef213a1415))
- Use isolated temp paths for rename fixtures - ([81d5789](https://github.com/snapetech-org/chaptarrng/commit/81d578938bdf5424da60f82b50570c745b273d14))
- Use fully qualified rename fixture paths - ([5ad2d33](https://github.com/snapetech-org/chaptarrng/commit/5ad2d33e5cdd216bb17bf500d77a8233e5526aa9))
- Use rooted paths for rename fixtures - ([cd8d534](https://github.com/snapetech-org/chaptarrng/commit/cd8d53469d2b6e3cac6b706f31861b83ff2b7b95))
- Update localization service stubs - ([54a5efa](https://github.com/snapetech-org/chaptarrng/commit/54a5efa649b9dd75caf4dfb5bfcac981f784024d))
- Make rename fixture paths platform-native - ([6e4211d](https://github.com/snapetech-org/chaptarrng/commit/6e4211dd3eb58ff51963c8d327274fb36c56ca60))
- Normalize fake paths across platforms - ([6fa426c](https://github.com/snapetech-org/chaptarrng/commit/6fa426ca8461e28ac343a111939108c7a04b794c))
- Cover bookless download history - ([2a0c2e4](https://github.com/snapetech-org/chaptarrng/commit/2a0c2e471b5268bc544540f5fdf23fa25a70c651))
- Guard Bookshelf database before migrations - ([35ca5cd](https://github.com/snapetech-org/chaptarrng/commit/35ca5cd563468337fb806b0a73ea09a230ddb65c))
- Adapt wanted narration repository to lifecycle persistence - ([1e84bd2](https://github.com/snapetech-org/chaptarrng/commit/1e84bd288d6c91da263924a5a41f39270ca33d4f))
- Adapt tag prefetch fixtures to current filesystem abstraction - ([b383b6b](https://github.com/snapetech-org/chaptarrng/commit/b383b6b88c990a2ed7791b20b7d0ab4b59a27374))
- Support bulk edition loading in cross-format insert fixture - ([7fa62a6](https://github.com/snapetech-org/chaptarrng/commit/7fa62a6c59c000f1f5e69cbcfb0b0af6a6b5d4ad))
- Construct malformed UTF-16 cases outside attribute metadata - ([bebf3cd](https://github.com/snapetech-org/chaptarrng/commit/bebf3cd29b007340efd41da260f9916b4176f8f3))

### Maintenance
- *(distribution)* Remove YunoHost publishing support - ([c26dd7f](https://github.com/snapetech-org/chaptarrng/commit/c26dd7f366bb0c7af1c7c96455ad0dec020ac158))
- *(distribution)* Retire YunoHost packaging - ([ee048be](https://github.com/snapetech-org/chaptarrng/commit/ee048be417b84f049f5e2d41f726d6b869fed807))
- *(frontend)* Remove moment dependency - ([7bb5ccf](https://github.com/snapetech-org/chaptarrng/commit/7bb5ccf4ccab07f9c54d0bd6dd810623446ecec9))
- *(repository)* Migrate GitHub owner links (#109) - ([8b228fd](https://github.com/snapetech-org/chaptarrng/commit/8b228fdccbd8c11fe4ebbd25753b8034a87f05b7))
- Route trusted main validation through shared pool (#101) - ([55ce02f](https://github.com/snapetech-org/chaptarrng/commit/55ce02f37319859ffb3a9ce62ac10541cd08b864))
- Run independent validation tasks in a bounded pool (#99) - ([4364742](https://github.com/snapetech-org/chaptarrng/commit/4364742e7811aca8c501c13897b5dd6daabe1710))## [0.9.969](https://github.com/snapetech-org/chaptarrng/compare/v0.9.969..v0.9.969) - 2026-10-15

### User-facing changes

#### Added

- **Bookshelf:** Authors that have only an audiobook or only an ebook root folder can now be filled in from your default root folders with one request to POST /api/v1/author/editor/backfill-roots, with dryRun=true to preview counts first. Hand-set authors are skipped and no files are moved.
- **Bookshelf:** Book files can now be downloaded directly from the file actions in the book view, limited to files inside your configured root folders.
- **Indexers:** System health now warns when a MyAnonaMouse indexer with unsatisfied-slot protection has no fresh account status, naming the indexer, so you can see why its grabs are being blocked.
- **Indexers:** A new optional Missing Book Search Interval in indexer options runs a recurring search for all missing monitored books. It is off by default and accepts 60 minutes or more.
- **Metadata:** Audible now has a per-instance marketplace setting for the US, UK, France, Germany, Italy, Spain, Canada, Australia, India, Japan, and Brazil. Catalog searches and ASIN lookups use the selected storefront and language headers, and result links point back to that marketplace.
- **Integrations:** BookLore connections can upload imported books to BookDrop, preserving the BookshelfNG connection settings and streaming uploads without loading entire books into memory.
- **Metadata:** Metadata profiles can rank preferred edition terms and select preferred series for filenames, ebook tags, Calibre, and AudioBookShelf metadata. Stronger match evidence remains authoritative.
- **Import Lists:** Import list settings now show durable sync history and offer a read-only preview of the next sync, so operators can inspect additions and exclusions before provider state or library changes are committed.
- **Localization:** The UI language list now includes Indonesian and Traditional Chinese. Their AudioBookShelf root-mapping labels, guidance, loading states, and errors are translated; other untranslated messages continue to fall back to English.
- **Localization:** The interface, help text, and notifications are now available in 12 additional languages, with the existing English fallback behavior preserved.
- **Api:** The book list API accepts optional offset and limit parameters for the full-library listing and reports the total in an X-Total-Count header.
- **Database:** PostgreSQL connections can now enforce TLS with a configurable SSL mode and root certificate, and no longer issue a session reset after every pooled query.

#### Changed

- **Integrations:** The fork guide now separates Chaptarr features shared upstream from its SeerrNG contract and standalone additions, with concrete criteria for replacing ChaptarrNG.
- **Metadata:** The metadata profile editor now offers a single identifier filter with clear choices for skipping editions missing both an ISBN and an ASIN, missing either, or missing an ASIN.
- **Ui:** The ebook and audiobook toggle is more compact on narrow screens so it leaves more room for the rest of the toolbar.
- **Bookshelf:** Library rescans now report which folder is being scanned, such as "Scanning folder 2/5", in the command status, so API clients and the activity view show progress instead of an empty message.
- **Search:** Book and interactive release searches now retry a small set of known alternate edition titles when the monitored edition title produces no eligible result. Chaptarr still applies the selected book's normal matching, language, media type, and quality rules.
- **Performance:** The frontend now loads route-specific code on demand, reducing the JavaScript fetched for the initial screen and loading optional sections when opened.
- **Importing:** Manual-import responses now include match provenance for automatically matched items, with the route and supporting, conflicting, neutral, and excluded signals. API clients can review the evidence behind a suggested book and edition instead of treating every automatic match as equally certain.
- **Metadata:** When an allowed-language change broadens a metadata profile, Chaptarr now queues a forced metadata refresh for authors using that profile. Newly permitted edition titles can then be discovered for release searches without rescanning library folders.
- **Localization:** AudioBookShelf library options now keep their audio-only and audio-plus-eBooks labels translated in every selectable UI language, so users can map roots without falling back to English.
- **Validation:** Independent validation tasks can now run together in a bounded pool, shortening waits while preserving task dependencies, native test accounting, and isolated output files.
- **Localization:** UI labels and help text now use translated catalog values across all 12 supported locales. The Naming Builder translates token labels, separators, and validation messages, while search examples retain their literal filter syntax.
- **Localization:** The Spanish, Simplified and Traditional Chinese, Hindi, German, Japanese, French, Brazilian Portuguese, Arabic, Korean, Vietnamese, and Indonesian Core catalogs now contain all 2,295 English-source keys. Existing locale strings are preserved; missing keys use exact English fallbacks. This completes key coverage, not translation review. Native-speaker translation and review remain open.
- **Localization:** The remaining interface labels, validation messages, and action text are now localized across the maintained catalogs. Naming Builder corrections, locale contamination fixes, and matching placeholders make the translated UI more consistent.
- **Validation:** Ubuntu validation on main can use the restricted organization runner pool after an operator enables it. Pull requests, Windows and macOS matrix jobs, and other refs stay hosted.
- **Distribution:** ChaptarrNG releases map the mutable BookshelfNG latest, hardcover, and softcover GHCR tags to the standard multi-architecture image; Docker Hub mirrors them when credentials are configured. The workflow checks each published alias digest.
  - **Action required:** Pull and recreate containers that use a moving BookshelfNG image tag; pinned tags and digests still need a manual update.
- **Migration:** ChaptarrNG now includes BookshelfNG conversion and rollback tools in the standard app. Rollback preserves the current ChaptarrNG database, including later writes, and validates both database lineages before replacing files.
  - **Action required:** Preview the source database and resolve any per-book media review before applying.
- **Import:** Manual import previews load each candidate book once per preview instead of once per file, which speeds up previews of large multi-file audiobooks.
- **Import:** When a Manual Import sends files to a new copy of the book instead of the one you selected, the log now records a warning that names both books and the reason.
- **Import:** Manual Import now warns in the preview when a suggested match belongs to a library author but its work is missing from that author's catalog and cannot be added from metadata, so you can pick a local book instead.
- **Repository:** ChaptarrNG source, support, and updater links now use the snapetech-org repository address. The Unraid post-commit sync now clears Git’s source context and validates its transferred companion under the organization. Existing container image names and compatibility identifiers stay the same.
- **Localization:** More of the interface now appears in the selected language across the 12 translated locales. This includes history details, import flows and validation, queue statuses, settings dialogs and validation, backup labels, book-file counts, notification fallbacks, keyboard-shortcut descriptions, and bundle-recovery messages.

#### Fixed

- **Bookshelf:** Adding an author through the API with ebook or audiobook profiles and monitoring options but no root folder now keeps those settings, using the default root folder, instead of silently dropping them.
- **Metadata:** Looking up an author by a provider-prefixed ID such as hc:252214 now returns that author instead of running the ID through the book lookup.
- **Calibre:** Imports into a Calibre library no longer fail, and no longer risk duplicate Calibre entries on retry, when the downloaded source file cannot be removed afterwards.
- **Import Lists:** Goodreads import lists now add the missing ebook or audiobook side of an author that was previously added for only one format, so the listed book is imported and monitored instead of skipped.
- **Indexers:** Saving an indexer whose category-filtered feed returns no results now shows a warning you can save anyway, instead of blocking the save.
- **Import:** Importing a regular audiobook no longer replaces a dramatized or full-cast recording of the same edition, or the other way around, unless you choose to replace it. File details and manual import now expose the dramatized flag.
- **Import:** Audiobooks released in several parts, such as "(1 of 2)" and "(2 of 2)", now import side by side instead of each part replacing the previous one. New {ReleasePart} and {ReleaseParts} naming tokens are available. Parts already imported before this update are not re-identified, so only new imports are protected.
- **Ui:** Toolbar buttons with longer or changing labels, such as those showing a count, now grow and wrap so the text stays readable.
- **Naming:** Audio MediaInfo naming tokens now use available file metadata and leave unknown values empty. Audiobook imports retain extracted audio details, and older files can be inspected on demand, so an unavailable bitrate no longer inserts a misleading `0 kbps` segment.
- **Importing:** Audio-file matching now prefers a native audiobook over an ebook/print candidate when title, narrator, duration, author, and series evidence is otherwise tied. Previews for newly matched files can use audiobook metadata despite incomplete catalog details; representative editions remain the fallback when no suitable audiobook exists. Existing file links remain unchanged until corrected.
- **Library:** Interactive book searches now distinguish edition subtitles from base titles, and routine scans retry unmatched books. Large library views honor clean-title sorting; missing author folders remove stale file records, and deletion avoids duplicate Calibre requests. Duration extraction runs only for audio files, and successfully queued commands remain visible when a later database insert fails.
- **Migration:** ChaptarrNG now checks database lineage before running migrations and refuses to start when a native database and a legacy Readarr or Bookshelf database both exist in the same app-data directory. This prevents a legacy library from being silently hidden behind `chaptarr.db`; verified data conversion is still required before switching applications.
- **Importing:** Conversion-job progress remains readable when SQLite stores whole-number values such as `0` as integers. Incomplete-import notifications also tolerate missing download titles and linked author metadata instead of failing while building the message.
- **Importing:** Rescans can now reconnect a renamed EPUB to its existing book when its prior file is gone, the matched edition is unchanged, and both paths are under the same accessible non-Calibre library root. A live copy, a different edition, or a second incoming file still gets its own book copy.
- **Metadata:** Goodreads searches for titles with umlauts or other special characters now complete successfully when multiple requests arrive at the same time.
- **Containers:** Container images now use the bundled wget client to check the anonymous /ping endpoint, so Docker health status reflects application availability without relying on curl or other tools that are absent from the runtime image.
- **Monitoring:** An explicit `monitored` change on an author API update now reaches its format-specific monitoring gates. Setting an author to unmonitored no longer gets undone by unchanged audiobook or ebook fields echoed in the request.
- **Monitoring:** The author-monitoring migration now preserves disabled authors from older releases. Libraries that already ran the faulty migration must restore a prior backup or re-unmonitor affected authors because the removed legacy flags cannot be recovered automatically.
  - **Action required:** Restore a pre-upgrade backup or re-unmonitor affected authors if migration 104 already ran.
- **User Interface:** Queue quality labels now retain their readable foreground color when table styling is applied. Interactive Search gives indexer names more room, wraps long names in the column, and exposes the full name on hover. On narrow screens, the results table scrolls horizontally while keeping the title column readable.
- **Navigation:** Toolbar actions now move into a visible More menu when the screen is too narrow, instead of being clipped offscreen. Manual Import and other actions remain available on mobile.
- **Search:** Newznab searches now warn when the indexer has selected categories but none match the searched book's media type. An empty result can no longer look like an indexer query that ran and found nothing.
- **Localization:** The browser now receives the selected UI locale and applies its language and reading direction to the document. Arabic and Hebrew use right-to-left direction.
- **Build:** ChaptarrNG source builds now use the published MQTTnet patch version, avoiding a NuGet restore warning that blocked Windows builds with warnings treated as errors.
- **Audio Bookshelf:** The AudioBookShelf root-folder mapping screen now has translated labels, guidance, and errors in Spanish, German, Simplified Chinese, Japanese, French, Brazilian Portuguese, Arabic, Korean, Hindi, and Vietnamese.
- **Validation:** Queued pull request checks now fetch the pull request head before validating release notes, so merging or deleting its source branch no longer causes a missing-commit failure.
- **Commands:** Queued per-author refreshes no longer occupy every command thread, so RSS sync, imports and other short tasks keep running during large refresh backlogs.
- **Authors:** The bulk root folder backfill now skips a missing format that has no quality profile and reports how many authors it skipped for that reason.
- **Api:** Importing a book through the API no longer fails with a database constraint error when the title has no cleaned form, and unexpected failures now return a server error instead of a validation error.
- **Database:** A cache database that fails to migrate is now discarded and rebuilt at startup instead of preventing the app from starting.
- **Audiobooks:** Updating tags on an M4B now preserves its existing chapter markers.
- **Import:** Manual Import now accepts a foreignEditionId when no numeric editionId is given, as long as the edition belongs to the selected book, and the missing-edition error now explains which identifiers are accepted.
- **Import:** Manual Import previews now log a short warning instead of an error with a stack trace when a file disappears or cannot be read while it is being scanned.
- **Import:** A multi-part audiobook download is no longer marked as imported when one of its parts failed or disappeared during Manual Import, even if the other parts of the same book imported successfully.
- **Import:** A queued author import that is still retrying no longer re-adds an author you removed and excluded from imports.
- **Search:** Release matching now recognizes titles whose number is followed by a dotted year, such as Fahrenheit.451.2018, and treats zero-padded numbers like 01 and 1 as the same volume.
- **Import:** Unmapped files in an author folder are no longer rescanned after every author import when nothing has changed, which stops sustained high CPU use. Repeat rescans back off from one hour up to a day and resume at once when the files or author catalog change.
- **Validation:** Local Linux validation now keeps nested sandbox test sockets under a short private temporary path, preventing the engine contract task from hanging on long runner paths.

#### Security

- **Dependencies:** Updates System.Security.Cryptography.Pkcs to 10.0.13 to address two reported denial-of-service vulnerabilities.
- **Security:** Maintainer backlog snapshots no longer include signed attachment credentials, reducing exposure of time-limited media URLs in the public repository.
- **Validation:** Local test runs now require a trusted Linux sandbox that hides operator files, ambient secrets, service sockets, and network access from candidate test code while preserving an explicitly selected read-only Playwright cache. Dependency roots that resolve outside the candidate tree are rejected before they can be mounted into the sandbox.
  - **Action required:** run local tests through trusted launcher
- **Validation:** Validation task and runtime-probe processes now use restricted environment variables and private temporary settings, reducing exposure of unrelated credentials and configuration during validation.
- **Validation:** The isolated NUnit task now receives the run-specific Playwright browser cache it needs for Linux direct-download tests, without restoring access to the operator's home or unrelated environment variables.
- **Ci Security:** Local validation excludes dotted and extensionless credentials, private/API keys, access/refresh-token and client-secret material, suffixed dotenv credentials, and non-source files under credential folders. Source modules such as TokenStore.ts and safe templates such as tokens.env.example remain available. The sandbox pins inputs and caps task logs.
- **Ci Security:** Pull request validation now uses GitHub-hosted runners for every author, keeping persistent local runners on main and reducing exposure of local files. Editing a PR description reruns the release-note check without rebuilding.
- **Logging:** Provider-controlled book titles and identifiers are now cleansed before logging, preventing embedded line breaks from forging additional log entries. OIDC rejection entries use fixed policy codes instead of free-form messages.

#### Removed

- **Distribution:** **Breaking:** ChaptarrNG no longer maintains its YunoHost package or attaches YunoHost release bundles. Operators using YunoHost should confirm the package's supported update source before upgrading ChaptarrNG.
  - **Action required:** Before upgrading ChaptarrNG, confirm the YunoHost package's supported update source.

### Security
- *(ci)* Keep pull request jobs on hosted runners (#69) - ([02d38e1](https://github.com/snapetech-org/chaptarrng/commit/02d38e186c56554662de2b62d0f24c7b4b1da4f1))
- *(security)* Preserve isolated Playwright browser cache (#67) - ([dd9f0ec](https://github.com/snapetech-org/chaptarrng/commit/dd9f0ec0da245fad140eaf6babe3c3f60b62042a))

### Features
- *(frontend)* Add React Query for server state management - ([5296afe](https://github.com/snapetech-org/chaptarrng/commit/5296afee686104c70ef570801c9c839fd34ca19f))
- *(i18n)* Localize naming builder labels and errors - ([777f217](https://github.com/snapetech-org/chaptarrng/commit/777f2174f488436198fff26a81c331b2bbcb2931))
- *(i18n)* Complete target locale catalogs (#79) - ([ad610a4](https://github.com/snapetech-org/chaptarrng/commit/ad610a40b4259061f066adacf529d8eab6078cbc))
- *(i18n)* Localize audiobook library modes - ([4b9d3b6](https://github.com/snapetech-org/chaptarrng/commit/4b9d3b68f9a2c30984680581b38c6c200257a1bb))
- *(i18n)* Add Indonesian and Traditional Chinese - ([b43855f](https://github.com/snapetech-org/chaptarrng/commit/b43855fe01e5389e37ac3cff3b437d770b47ce34))
- *(i18n)* Expose RTL locale metadata - ([b20b371](https://github.com/snapetech-org/chaptarrng/commit/b20b371bfe6e4d2519ab54b77ca17cef3e81c832))
- *(i18n)* Translate AudioBookShelf root mappings - ([5377439](https://github.com/snapetech-org/chaptarrng/commit/537743906a5261afcb16aac60a8de3de831bde55))
- *(import)* Adopt safe existing destinations - ([f3d8105](https://github.com/snapetech-org/chaptarrng/commit/f3d81054ff1040c0cf329a1a8f4804c3030b1c0c))
- *(import)* Expose manual match provenance - ([6ed1f89](https://github.com/snapetech-org/chaptarrng/commit/6ed1f89389858a1a8d5be54bac43181e7e9a6248))
- *(import-lists)* Add durable sync history and preview - ([108511d](https://github.com/snapetech-org/chaptarrng/commit/108511dde089aa62d8e6bed94aaf46d4721fa253))
- *(media)* Extract archived downloads (.zip, .tgz, .tar.gz) during import when no media files are found - ([e66cafd](https://github.com/snapetech-org/chaptarrng/commit/e66cafd132d900d98dead7305ddedf6c79425d82))
- *(metadata)* Add RanobeDB light novel support - ([1b4918b](https://github.com/snapetech-org/chaptarrng/commit/1b4918b9859708c2e6024c092715d8a32357c754))
- *(metadata-profile)* Add UI for IgnoredGenres, with autocomplete from library genres - ([5aeb3d5](https://github.com/snapetech-org/chaptarrng/commit/5aeb3d5d2e4c82bb15a03f5a77fc6c96c12c2c12))
- *(metadata-profile)* Add IgnoredGenres filter (e.g. exclude comics/graphic novels) - ([4b70af4](https://github.com/snapetech-org/chaptarrng/commit/4b70af49500ca75ab4a773d1771f5abea51ca05f))
- *(metrics)* Expose low-cardinality Prometheus metrics - ([2b4b1f4](https://github.com/snapetech-org/chaptarrng/commit/2b4b1f43f1c9b9f7f6a062f41e7dea32cd4e8e53))
- *(mqtt)* Complete gateway and canonical events - ([d1e4df5](https://github.com/snapetech-org/chaptarrng/commit/d1e4df59e91b92c2e21f0c1b3592a8dbc2eba00c))
- *(mqtt)* Add gateway connection foundation - ([59b07bc](https://github.com/snapetech-org/chaptarrng/commit/59b07bcb2577c7129738197041219bc772dc96bb))
- *(notifications)* Add Emby and Jellyfin refresh provider - ([5ae1487](https://github.com/snapetech-org/chaptarrng/commit/5ae14872f02440db6bea18fb81b0a07718a779d6))
- *(search)* Retry alternate edition title searches - ([8272e5a](https://github.com/snapetech-org/chaptarrng/commit/8272e5a40d89c197f2028d93592032727d8e8303))
- *(validation)* Report live task progress - ([b37b982](https://github.com/snapetech-org/chaptarrng/commit/b37b982538fb5a5989943c635849b1af91140aee))
- Migrate BookshelfNG into standard ChaptarrNG (#100) - ([fa5384e](https://github.com/snapetech-org/chaptarrng/commit/fa5384e80d13157bf805278e900c510c189bc69b))
- Add Audible marketplace setting - ([95b56be](https://github.com/snapetech-org/chaptarrng/commit/95b56be46991179999cb856f48e88dd57db212fb))
- Add BookshelfNG metadata preferences - ([d763525](https://github.com/snapetech-org/chaptarrng/commit/d763525c46f13b5d6bcff11e52e449cceec7bae6))
- Preserve BookLore uploads and protect legacy databases - ([8a231dd](https://github.com/snapetech-org/chaptarrng/commit/8a231dd8d31202a6be3c7f95828ddc9cd3c2344a))

### Bug Fixes
- *(MyAnonaMouse)* Persist refreshed mam_id cookies - ([f4e6c27](https://github.com/snapetech-org/chaptarrng/commit/f4e6c27f53559b71477a649a5dd3a74dea4c99fa))
- *(abs)* Expose full library paths in mappings - ([c6ab3df](https://github.com/snapetech-org/chaptarrng/commit/c6ab3df5a845464210bcb04b542740cb092582f1))
- *(aliases)* Retry ReplaceAliases when a concurrent writer wins the unique-index race - ([0b514cd](https://github.com/snapetech-org/chaptarrng/commit/0b514cdd6c3610b83012a2d8ca2c032d88e07c84))
- *(api)* Persist explicit author monitoring changes - ([43e3faa](https://github.com/snapetech-org/chaptarrng/commit/43e3faabf1c9333444711e64453deb37c90a9fca))
- *(audiobookshelf)* Push the cover after the folder cover.jpg is written - ([e62474e](https://github.com/snapetech-org/chaptarrng/commit/e62474ece93ae76592537d615bfa350ff19f7401))
- *(audiobookshelf)* Send only the cover on a cover-updated event - ([17a678e](https://github.com/snapetech-org/chaptarrng/commit/17a678e2b1b1b0b5108f97215ea898892c4e6dcf))
- *(author)* Stop ApplyChanges wiping stored paths when the update omits them - ([408d58a](https://github.com/snapetech-org/chaptarrng/commit/408d58a98d0b2f057645b40b9f21344263520b26))
- *(books)* Retain format four audiobook fallback - ([59e8c7f](https://github.com/snapetech-org/chaptarrng/commit/59e8c7fc4bc9c77472d09c139018d29189bc3b78))
- *(books)* Use the author-refresh match instead of independently re-deriving it (#182) - ([2262f84](https://github.com/snapetech-org/chaptarrng/commit/2262f84ce2e915ad9bc2fac3fb2837d4f8a47aaa))
- *(books)* Make remote-item match-claim order deterministic during author refresh (#179) - ([30750b3](https://github.com/snapetech-org/chaptarrng/commit/30750b3824d77050200301cfc246c44c3ab72aff))
- *(books)* Compare against the simulated update result, not the raw remote payload (#183) - ([52bdf56](https://github.com/snapetech-org/chaptarrng/commit/52bdf5677dc0ba01c5b54baa073844b6b6e46d2e))
- *(build)* Repair backlog compile failures - ([22a9db6](https://github.com/snapetech-org/chaptarrng/commit/22a9db6de4d533a7b7f897417ef0b9748e9ffff7))
- *(build)* Close SQL verbatim string literal - ([c1592f6](https://github.com/snapetech-org/chaptarrng/commit/c1592f6e780f78a3fad156fca482b67d68e05440))
- *(build)* Pin MQTTnet to published patch version - ([3705a9b](https://github.com/snapetech-org/chaptarrng/commit/3705a9b40c29e1db632082029f6544b72457e0fe))
- *(cache)* Serialize expiry cancellation and release timers on removal - ([3aa4a95](https://github.com/snapetech-org/chaptarrng/commit/3aa4a95de7f513c4a9b536084f1bbe7ba6df4548))
- *(ci)* Repair trusted runner expression (#102) - ([9f736b9](https://github.com/snapetech-org/chaptarrng/commit/9f736b9984eb026d53a91492788ec4cc4ca789da))
- *(ci)* Capture validation receipts before frontend build (#77) - ([ee2efc5](https://github.com/snapetech-org/chaptarrng/commit/ee2efc555f6b11a2d3ade66261d4e625aab24380))
- *(ci)* Run self-hosted PowerShell under private Aqua root - ([22e5f82](https://github.com/snapetech-org/chaptarrng/commit/22e5f82a58e90895951ab68d4d6cad3e807178e6))
- *(ci)* Isolate Aqua package cache per runner job - ([cdc1a39](https://github.com/snapetech-org/chaptarrng/commit/cdc1a3987d53ea4c5c86452d8332f8d7e00dd1d7))
- *(ci)* Fetch merged pull request head for notes - ([a22ea02](https://github.com/snapetech-org/chaptarrng/commit/a22ea02e9067b20fac0561053291795658a782a4))
- *(ci)* Make self-hosted runner temp private - ([be6dfb2](https://github.com/snapetech-org/chaptarrng/commit/be6dfb237f25a73404deecbf3acc744475788703))
- *(commands)* Keep a cancelled running command Started until its handler exits - ([c443d65](https://github.com/snapetech-org/chaptarrng/commit/c443d655b666a7d4e271622ceb7cea4269c52e7c))
- *(commands)* Stop disk-access groups from globally blocking each other - ([05854a8](https://github.com/snapetech-org/chaptarrng/commit/05854a8bc8125f4425c448e1a0a1237a4bcc1d7b))
- *(conversion)* Handle integer progress and incomplete metadata - ([911d194](https://github.com/snapetech-org/chaptarrng/commit/911d194c13b358d75c49f84956c6321272ae2607))
- *(docker)* Use the configurable health check in local-build images (#112) - ([6dde720](https://github.com/snapetech-org/chaptarrng/commit/6dde720a15703f7c33bf56abf813ccb361fb592e))
- *(docker)* Report container health with wget - ([ef6fec6](https://github.com/snapetech-org/chaptarrng/commit/ef6fec66c70a3f106de66b836dc3a000e533e37c))
- *(download)* Bound how long ProcessMonitoredDownloads yields the disk slot - ([b624a08](https://github.com/snapetech-org/chaptarrng/commit/b624a08a2492ec1d67b1f4eb27786ef0b5255026))
- *(download)* Let ProcessMonitoredDownloads yield the disk slot to waiting commands - ([713722d](https://github.com/snapetech-org/chaptarrng/commit/713722d66126e47249ef9484bf096e47eccac363))
- *(download)* Make ProcessMonitoredDownloads honor cancellation - ([44b9f61](https://github.com/snapetech-org/chaptarrng/commit/44b9f61062ac7e921b0303a9d642b2015a0baedf))
- *(downloads)* Close completed import refresh race - ([4fd9696](https://github.com/snapetech-org/chaptarrng/commit/4fd9696732adf50a2d4fcac46ae9510bd9e97eb5))
- *(frontend)* Migrate to react-dnd 16 and react-dnd-multi-backend 8 (#111) - ([cade6a2](https://github.com/snapetech-org/chaptarrng/commit/cade6a21a34fb170a44aca2c282b628ef93b3897))
- *(frontend)* Keep default CSS module exports with css-loader 7 (#110) - ([014d2c1](https://github.com/snapetech-org/chaptarrng/commit/014d2c1b085edbc6e2ff9f756523e433b5440279))
- *(frontend)* Keep search columns usable on mobile - ([c5beffe](https://github.com/snapetech-org/chaptarrng/commit/c5beffe9c09dd4ca9c30a78b4f3c849b2db2d83e))
- *(frontend)* Improve quality labels and indexer names - ([223a874](https://github.com/snapetech-org/chaptarrng/commit/223a874ca2987d30106bb406cc2b97caeb97b97e))
- *(frontend)* Sync direct download contract with docs - ([1c9f53a](https://github.com/snapetech-org/chaptarrng/commit/1c9f53af853a3d95b7eed35d7684ec268672e4e1))
- *(frontend)* Make merged dependency build reproducible - ([df9385c](https://github.com/snapetech-org/chaptarrng/commit/df9385ccbf77640f38495b27c04e52bb9f12ebd4))
- *(frontend)* Add ambient process type for webpack NODE_ENV - ([85291e9](https://github.com/snapetech-org/chaptarrng/commit/85291e93746a5f5c767c23d56789d16d753705eb))
- *(frontend)* Scope service worker to a webworker tsconfig - ([5b286a3](https://github.com/snapetech-org/chaptarrng/commit/5b286a328dd8d1aba959e88f1c3b9a3eab340b39))
- *(frontend)* Register dayjs isBetween plugin used by isInNextWeek - ([e357b0d](https://github.com/snapetech-org/chaptarrng/commit/e357b0d07a91f167083f168bb9b828f83296306f))
- *(frontend)* Wrap backup-save request with Promise.resolve for jQuery Deferred compat - ([a393f47](https://github.com/snapetech-org/chaptarrng/commit/a393f47fb07fb30bdded591f70b4f8e05c03756e))
- *(i18n)* Correct reviewed locale strings and placeholders - ([15a5d6d](https://github.com/snapetech-org/chaptarrng/commit/15a5d6d36960a49eb28d35834c002b2a4ad24075))
- *(import)* Use explicit series tags to verify edition positions - ([fa9e26e](https://github.com/snapetech-org/chaptarrng/commit/fa9e26ecacafbb34570e0640f381b441283aae7e))
- *(import)* Prefer audiobook editions in fallback ties - ([e3c9eff](https://github.com/snapetech-org/chaptarrng/commit/e3c9effa7efa9cb5adf4492293b1a4a3f37e8408))
- *(import)* Reattach renamed EPUBs - ([1ab2354](https://github.com/snapetech-org/chaptarrng/commit/1ab235456092e98e7b6be0e6d613fad1672ea4cf))
- *(import)* Warn about an existing-file conflict in Manual Import preview (#184) - ([7d19f1a](https://github.com/snapetech-org/chaptarrng/commit/7d19f1a791d7f800f1d2c7ad972a3bc6b3c0c867))
- *(import)* Report why a manual import imported nothing - ([9e6e12f](https://github.com/snapetech-org/chaptarrng/commit/9e6e12f8d9f8e6f05546a7c8e93d8666c562ccc4))
- *(import)* Don't let an explicit Move override "this torrent's files cannot be moved" - ([95e47ed](https://github.com/snapetech-org/chaptarrng/commit/95e47edfdf0de8f26f75ed53a5b44986ae69b775))
- *(import-lists)* Commit cursor on the fetching list instance after processing - ([925fd50](https://github.com/snapetech-org/chaptarrng/commit/925fd507a6252d7cb468ce013d2a8ce5729153dd))
- *(import-lists)* Defer Hardcover library sync state persistence until list processing succeeds - ([da77b3f](https://github.com/snapetech-org/chaptarrng/commit/da77b3f62135f60e8984b2a8fc39dfe011d1ff4d))
- *(manual-import)* Match an inferred edition structurally when it carries no provider edition id - ([237fd00](https://github.com/snapetech-org/chaptarrng/commit/237fd002b481a1f7aa26b47159b1b81804e424a9))
- *(manual-import)* Keep the plain exception type for explicit edition ids - ([1104c0e](https://github.com/snapetech-org/chaptarrng/commit/1104c0e86ff7dd0f5a89a269be63bc029e24e191))
- *(manual-import)* Use the reconciled book's monitored edition when a suggested work has many editions - ([0f99a61](https://github.com/snapetech-org/chaptarrng/commit/0f99a61e4ef62d58ccd22a576e6419b25d7ade56))
- *(manual-import)* Fall back to the provider's default edition when a suggested work has many editions - ([32133b4](https://github.com/snapetech-org/chaptarrng/commit/32133b4c8ecfaa44b437c7d1088be5fe6c88a062))
- *(manual-import)* Materialize a suggested work when the suggestion has no edition id - ([79da2f8](https://github.com/snapetech-org/chaptarrng/commit/79da2f8472a6ee4154434cd03333d48b77ef1bbd))
- *(media)* Handle missing root folder during upgrade import with RootFolderNotFoundException - ([ee7ebc7](https://github.com/snapetech-org/chaptarrng/commit/ee7ebc7cd8d6e294bbe55b3813a03b97ac03be59))
- *(metrics)* Disambiguate queue and health-check models - ([cf84c7d](https://github.com/snapetech-org/chaptarrng/commit/cf84c7d477abc100b4deecb732c99a578d87a818))
- *(migration)* Preserve legacy author monitoring choice - ([1261261](https://github.com/snapetech-org/chaptarrng/commit/12612611c096238e60e5c9b2e157a09510a6f9e0))
- *(migrations)* Retain fork cache migration 108 and number query indexes 109 - ([6464c53](https://github.com/snapetech-org/chaptarrng/commit/6464c53d542353b0e149bd6d4ee1095500ed0d7f))
- *(monitoring)* Keep specific-book adds from monitoring the whole catalogue - ([8ed8199](https://github.com/snapetech-org/chaptarrng/commit/8ed819935da1784392f1167d41fd64d888f1a6ba))
- *(naming)* Populate audiobook media info tokens - ([75e9bb8](https://github.com/snapetech-org/chaptarrng/commit/75e9bb89d9c8697fc7c8ba1190035e5454f8afdb))
- *(notifications)* Trace release import dispatch - ([a23e3c6](https://github.com/snapetech-org/chaptarrng/commit/a23e3c6719f22cec6fd55707185bbc37e68993b5))
- *(perf)* Address large-library review follow-ups - ([281fe16](https://github.com/snapetech-org/chaptarrng/commit/281fe16649409d6b306a6c3fbdba5a7c39423f77))
- *(queue)* Enforce torrent preservation on client removal - ([9de8444](https://github.com/snapetech-org/chaptarrng/commit/9de84446a295f96ede90bb746bf6ca1c3ead5deb))
- *(repo)* Remove a stray node_modules link (#119) - ([7918713](https://github.com/snapetech-org/chaptarrng/commit/79187131e40b8c782a0c35da4fbc05be6add69e4))
- *(search)* Warn when indexer lacks media categories - ([0f3050c](https://github.com/snapetech-org/chaptarrng/commit/0f3050c4d2ebf45a631bbf7d1c2ad67ca78c5a76))
- *(search)* Refresh editions after language profile expansion - ([55eddba](https://github.com/snapetech-org/chaptarrng/commit/55eddba12ef6395adabcf2b8bfc4944d26d3b1c3))
- *(security)* Update Pkcs dependency to patched version (#108) - ([cde05c8](https://github.com/snapetech-org/chaptarrng/commit/cde05c8335adc164fdabddf4af6ce6ca11ec35ce))
- *(security)* Mount dependencies read-only in place - ([ee36f13](https://github.com/snapetech-org/chaptarrng/commit/ee36f139554947e0aeb68ffe60d7209350cda8ea))
- *(security)* Require trusted test sandbox - ([e775662](https://github.com/snapetech-org/chaptarrng/commit/e77566240e792ef0c7435a24ad161abd092fdb2a))
- *(security)* Preserve isolated Playwright browser cache - ([d63f7e1](https://github.com/snapetech-org/chaptarrng/commit/d63f7e1097385c75935204cb810efb6162c0495d))
- *(security)* Preserve isolated Playwright browser cache - ([d6e4f23](https://github.com/snapetech-org/chaptarrng/commit/d6e4f2357b148dc30385f5680d00d61b628566ab))
- *(security)* Isolate validation task environments - ([9d9d7e9](https://github.com/snapetech-org/chaptarrng/commit/9d9d7e9ad657cca9acd668f07903156ca9d1c22a))
- *(security)* Isolate validation task environments - ([6e41c72](https://github.com/snapetech-org/chaptarrng/commit/6e41c7275d51719f58db02c513cdac2e83d6774d))
- *(ui)* Keep toolbar overflow actions accessible - ([9fd105f](https://github.com/snapetech-org/chaptarrng/commit/9fd105fbaef9b694169ae1eb4eed57559aa4244e))
- *(ui)* Stop the book page refetching forever for a book that does not exist - ([9b4fa4f](https://github.com/snapetech-org/chaptarrng/commit/9b4fa4f387084322d142ebc9a78239893ef59d4d))
- *(ui)* Read the author's real folder fields (audiobookFolder / ebookFolder) in the delete dialog - ([4707e05](https://github.com/snapetech-org/chaptarrng/commit/4707e052301e3dd9e9974f491f36e3d8ae68db26))
- *(ui)* Delete-author dialog lists every author folder, not only the legacy path - ([dc8b4a3](https://github.com/snapetech-org/chaptarrng/commit/dc8b4a39a6fe4c9d4d284fbf05eb046128ce5ffd))
- *(ui)* Don't open a blank confirmation when a manual import replaces no local book - ([58a40ad](https://github.com/snapetech-org/chaptarrng/commit/58a40ad61ac1d513b9a32d4ae2286854f863819a))
- *(validation)* Prevent nested sandbox contract hangs (#103) - ([9b7b1d1](https://github.com/snapetech-org/chaptarrng/commit/9b7b1d157cc84e2f496fdaa76a58739c8c22d419))
- *(validation)* Expose isolated Playwright browser cache - ([d8b8e76](https://github.com/snapetech-org/chaptarrng/commit/d8b8e76f4dcc6c42daa125d46d15de4128676228))
- *(wanted)* Use edition release date - ([ff74478](https://github.com/snapetech-org/chaptarrng/commit/ff74478f3c3c65c3c04e5e57f445ec2fc8c1d7a2))
- Restore M4B chapter release note and package update-mechanism tests (#113) - ([0c05812](https://github.com/snapetech-org/chaptarrng/commit/0c058129bf7bfb60f5da0e1a46968d9151668c17))
- Retry unmatched files during scans - ([a8bd0ba](https://github.com/snapetech-org/chaptarrng/commit/a8bd0baabc9e551803b5e77573bf9ce8bb05defe))
- Sequence combined database migrations - ([1702049](https://github.com/snapetech-org/chaptarrng/commit/17020494db6c3ee322dc6cb3c0a0a18375384eac))
- Resolve book matching edge cases and validation inventory drift - ([197597b](https://github.com/snapetech-org/chaptarrng/commit/197597b75e6657748418204c4c65210ec448e3dd))
- Harden import matching and conversion cancellation - ([4582c13](https://github.com/snapetech-org/chaptarrng/commit/4582c13317d0d0c4574e2f968a3e78f9baa96821))
- Reconcile migration versions and stale test fixtures - ([dd09594](https://github.com/snapetech-org/chaptarrng/commit/dd0959432ebb2de58b097b73d0e609bda4d7bc7d))
- Preserve cancellation and fatal M4B errors - ([5fc61e4](https://github.com/snapetech-org/chaptarrng/commit/5fc61e4d73336347379ff3fa6548306b47a458d7))
- Keep overlapping casts and dramatized editions as distinct wanted variants - ([e03b995](https://github.com/snapetech-org/chaptarrng/commit/e03b995023fd2db2d886cd3c92e6aeee97d226ec))
- Reject cross-format imports without an author root and preserve cancellation - ([8a9d759](https://github.com/snapetech-org/chaptarrng/commit/8a9d7590177b7a55a8c004732d791875878b56fe))
- Bound cover backoff and evict tag cache entries without clearing hot data - ([1efc938](https://github.com/snapetech-org/chaptarrng/commit/1efc93813529c2dfb86f35a7b8a8c19c82fe8dcd))
- Expose shelf load retries and discard stale count responses - ([846bd74](https://github.com/snapetech-org/chaptarrng/commit/846bd740820091364c6afd6b7184b1f13390e805))
- Reconcile format-scoped list and shelf query interfaces - ([8f3bf84](https://github.com/snapetech-org/chaptarrng/commit/8f3bf84a9b1201c3f0c81aafef89e0246b61edba))
- Reconcile background deletion tests and metadata migration ordering - ([7b3e5f5](https://github.com/snapetech-org/chaptarrng/commit/7b3e5f564bbaa8a84f404a5ed8c2c5ff40d29bdc))
- Align ebook tags with authoritative series and preserve file permissions - ([8a5b58a](https://github.com/snapetech-org/chaptarrng/commit/8a5b58aef71d803cf599ba33cbbff390104b961f))
- Rotate download sweeps fairly without unbounded disk-slot retention - ([ba4263d](https://github.com/snapetech-org/chaptarrng/commit/ba4263d637b44ddd539057685d19b789527fb721))
- Serialize queue snapshots and propagate fatal preview failures - ([037928d](https://github.com/snapetech-org/chaptarrng/commit/037928d146742a1360e407fbca641d47237bbccc))
- Deduplicate command batches and preserve ebook reader failure semantics - ([0229587](https://github.com/snapetech-org/chaptarrng/commit/02295879d7e5e136ee1909a3693cf17fb7405474))

### Documentation
- *(backlog)* Correct HTTP cache migration version - ([46fa890](https://github.com/snapetech-org/chaptarrng/commit/46fa8908a81df4093801c29b6edb4f45fb7e4f53))
- *(backlog)* Record Prometheus metrics implementation - ([f256058](https://github.com/snapetech-org/chaptarrng/commit/f2560586df45a251585fdfd41009aca19bd794b1))
- *(backlog)* Record SABnzbd race fix - ([fe5c003](https://github.com/snapetech-org/chaptarrng/commit/fe5c003a11f67a2a442d8d50f708fb4e4e749777))
- *(backlog)* Record MQTT gateway implementation - ([5a42069](https://github.com/snapetech-org/chaptarrng/commit/5a42069dd265add5eeb7821e7041a96b0abb9d08))
- *(backlog)* Track MQTT gateway implementation - ([ebe2c52](https://github.com/snapetech-org/chaptarrng/commit/ebe2c524e0f6c1801b529004951efff0d4f9ffd7))
- *(backlog)* Record Emby Jellyfin integration - ([cdaa7dd](https://github.com/snapetech-org/chaptarrng/commit/cdaa7dd52587228abc38a7f7f91d855d818ff2f3))
- *(backlog)* Note Proxmox eligibility hold - ([f0b030b](https://github.com/snapetech-org/chaptarrng/commit/f0b030b3f2cba68af40b43f709ad0a3fb1b06327))
- *(backlog)* Record mobile search title fix - ([7dcb551](https://github.com/snapetech-org/chaptarrng/commit/7dcb5518b0965f86bd509a1a2a80b2f1006da6a2))
- *(backlog)* Record audiobook fallback fix - ([ed0ffcb](https://github.com/snapetech-org/chaptarrng/commit/ed0ffcb22ed5ad812abba45a594710e39410a9c2))
- *(backlog)* Record wanted release-date fix - ([47c9412](https://github.com/snapetech-org/chaptarrng/commit/47c941279bd95e882f4889321a6138ca8958e7bd))
- *(backlog)* Disposition series primary filter reports - ([d55aee2](https://github.com/snapetech-org/chaptarrng/commit/d55aee2941cb46c01f04f65b1aad0ed66c37a837))
- *(backlog)* Disposition author progress reports - ([5be115b](https://github.com/snapetech-org/chaptarrng/commit/5be115b55f36350ed650c8ce046c4617fa38b16f))
- *(backlog)* Record author merge coverage - ([d842f1d](https://github.com/snapetech-org/chaptarrng/commit/d842f1d5eef2d24014ed210db4b0b4c81ac0339f))
- *(i18n)* Document localized catalog review - ([68e1330](https://github.com/snapetech-org/chaptarrng/commit/68e1330edf68abdb95f20914b0c8c703b7e129f3))
- Clarify cumulative upstream blocker set - ([24b340f](https://github.com/snapetech-org/chaptarrng/commit/24b340fffc43eb0883995a21665d3d051eb66abe))
- Pin latest ChaptarrNG blocker snapshot - ([fc1e0cc](https://github.com/snapetech-org/chaptarrng/commit/fc1e0ccc8b678952ae77cb5c1a0b02c7bd17e5c6))
- Refresh cumulative blocker snapshot - ([22078e0](https://github.com/snapetech-org/chaptarrng/commit/22078e0289949dbc41ee603cd10b80c2510f1e78))
- Itemize cumulative upstream retirement blockers - ([066258f](https://github.com/snapetech-org/chaptarrng/commit/066258f6f96d20dd5ab2552ce1b16437c541a971))
- Preserve shipped YunoHost release note - ([c416406](https://github.com/snapetech-org/chaptarrng/commit/c4164065c1d1d188549d5444699b13e1feae3c16))
- Document current fork additions and integrations - ([2a8532e](https://github.com/snapetech-org/chaptarrng/commit/2a8532e232b4c3fae6fce16354959d4bf76839a5))
- Record import adoption IO safeguard - ([b57bbd8](https://github.com/snapetech-org/chaptarrng/commit/b57bbd8b8476ad8f406e84b2ba80f496ad3f31cb))
- Disposition occupied import destination requests - ([c7dd0fc](https://github.com/snapetech-org/chaptarrng/commit/c7dd0fc927bcd793a4ac85a5a091d68212c91eb6))
- Disposition closed metadata outage report - ([46b7182](https://github.com/snapetech-org/chaptarrng/commit/46b718208f2404e94d6daed9f9db76c15704f0ac))
- Disposition Readarr book add regression - ([ef6b804](https://github.com/snapetech-org/chaptarrng/commit/ef6b804fdbc3cfd672172ce858cfc5e7303ae782))
- Disposition merged book deletion fix - ([b295203](https://github.com/snapetech-org/chaptarrng/commit/b295203cae77e5e50598bdb12538885b67980bbc))
- Address README backlog requests - ([d5465f2](https://github.com/snapetech-org/chaptarrng/commit/d5465f2ee98fab49cf40a321187f7ab8430ada70))
- Audit compatibility and build PRs - ([8f3b00c](https://github.com/snapetech-org/chaptarrng/commit/8f3b00ccc64b575616d43500a2ae0cd2dcc4fc7b))
- Audit import webhook issue - ([5a8243e](https://github.com/snapetech-org/chaptarrng/commit/5a8243e090cfb78bdb79582c4f74cae91efd3a8a))
- Audit bookshelf monitoring issues - ([fa80bc4](https://github.com/snapetech-org/chaptarrng/commit/fa80bc4738014374e020300a302f7512743572e9))
- Audit author monitoring issues - ([548f31f](https://github.com/snapetech-org/chaptarrng/commit/548f31f950dba63d22bbcecc5ef24a61240eec31))
- Audit book added timestamp coverage - ([467ddfb](https://github.com/snapetech-org/chaptarrng/commit/467ddfb84501d0ae362381f60bc8d4969db8a72c))
- Record empty media category warning coverage - ([503a20f](https://github.com/snapetech-org/chaptarrng/commit/503a20fecd0ad86591b8e611f2a0ecbe1e32f816))
- Record conversion scheduler issue coverage - ([3a0c7ec](https://github.com/snapetech-org/chaptarrng/commit/3a0c7ec2dba3fabc1d69eb5579c6ac0485673133))
- Record alternate edition search completion - ([8699ed7](https://github.com/snapetech-org/chaptarrng/commit/8699ed72f5173fff6bae81f84a7983c532fd4a92))
- Record mobile toolbar issue fix - ([3f57d9c](https://github.com/snapetech-org/chaptarrng/commit/3f57d9cf839d46f06e9aba6694ecc2270eaefdea))
- Validate English edition selection coverage - ([cacf9e2](https://github.com/snapetech-org/chaptarrng/commit/cacf9e27078eb714b772b7b89503951e25bc55ff))
- Record manual import match evidence - ([565ed91](https://github.com/snapetech-org/chaptarrng/commit/565ed915eb4906512a9d64eca2ca46667f0e1dff))
- Record subtitle title matching coverage - ([40f0140](https://github.com/snapetech-org/chaptarrng/commit/40f01402199af7dbb974f92d4ed7564b13bf4e32))
- Record audiobook edition matching fix - ([7b510ee](https://github.com/snapetech-org/chaptarrng/commit/7b510eefb41f0ed3b7ba1a0c7cac28476cd46bf7))
- Validate audiobook selection release note - ([a7fc8b5](https://github.com/snapetech-org/chaptarrng/commit/a7fc8b5d830681a040a85dc927d8f215f17f58cc))
- Record EPUB rename backlog fix - ([76c45c4](https://github.com/snapetech-org/chaptarrng/commit/76c45c4ea7087074a4b03dc4159d49ad02a8f679))
- Record Audible backlog dispositions - ([a0cad97](https://github.com/snapetech-org/chaptarrng/commit/a0cad97adcfc8974d2702a14a586b7a2ab303824))
- Record media info naming token fix - ([5be5bca](https://github.com/snapetech-org/chaptarrng/commit/5be5bca95ee10f10f9e703014d321ca3e951f0ca))
- Record responsive interactive search issue fix - ([1e4ef06](https://github.com/snapetech-org/chaptarrng/commit/1e4ef063a00012a3a5a946c496631f2e8b067b71))
- Record interactive search UI issue fixes - ([6bd6bd6](https://github.com/snapetech-org/chaptarrng/commit/6bd6bd6b362ddf7884c00070904cb80ee478abf3))
- Record direct download PR coverage - ([e49cf0d](https://github.com/snapetech-org/chaptarrng/commit/e49cf0d91f453b94788e9873f0f004ae7ef42bdb))
- Record container and image-audit fixes - ([9f8e02a](https://github.com/snapetech-org/chaptarrng/commit/9f8e02a96dd515fce5fc6be4f67178f38566af40))
- Record migration 104 backlog resolution - ([0d8a0ae](https://github.com/snapetech-org/chaptarrng/commit/0d8a0ae5923e8fa595b854d5b36f6ac4167ad825))
- Refresh upstream backlog through PR19 - ([6495a8e](https://github.com/snapetech-org/chaptarrng/commit/6495a8e564aee552368ab34e77d1c54e4ad41e3b))
- Clarify Chaptarr upstream boundary - ([ddb79d6](https://github.com/snapetech-org/chaptarrng/commit/ddb79d63d028d5a4f71a9f63ab7f66dcb271902e))
- Add ChaptarrNG agent guidance - ([2271d59](https://github.com/snapetech-org/chaptarrng/commit/2271d593748fbbe267a9ea01a2f89af07868e484))
- Pin migration regression validation status - ([5f52032](https://github.com/snapetech-org/chaptarrng/commit/5f52032d29cb19ce969642dbec535d06d6ea353c))
- Detail Bookshelf data migration blockers - ([a4d8ad9](https://github.com/snapetech-org/chaptarrng/commit/a4d8ad95c64daa50896094eec0bedbc3dc29cb4a))
- Clarify localization parity scope - ([5702db2](https://github.com/snapetech-org/chaptarrng/commit/5702db296420f8c091d270864b815f69308b2c2a))
- Include active upstream retirement blockers - ([f8f0c4a](https://github.com/snapetech-org/chaptarrng/commit/f8f0c4adb8d13c6817053d9d39b5c9bb40b42cde))
- Refresh ChaptarrNG retirement inventory - ([af3be47](https://github.com/snapetech-org/chaptarrng/commit/af3be47be45d8b41f5ce807eb4269537a9bad3d8))
- Make README Ko-fi link discreet - ([a6be38d](https://github.com/snapetech-org/chaptarrng/commit/a6be38dd5551b513b22e2efd4504a5ee3e73895b))
- Add Ko-fi sponsor link - ([aa54820](https://github.com/snapetech-org/chaptarrng/commit/aa5482087db4d86930dd7cb43101aa9a83e57a23))
- Record AudioBookShelf propagation audit evidence - ([2095cd7](https://github.com/snapetech-org/chaptarrng/commit/2095cd746106b12c16a81ca0d3323aa8d4f57b70))
- Track upstream backlog integration progress - ([d1e2a5c](https://github.com/snapetech-org/chaptarrng/commit/d1e2a5c95d011cf2af5d29d86719c86735de81f3))
- Update upstream coverage after import and cache integration - ([a965abb](https://github.com/snapetech-org/chaptarrng/commit/a965abb30377bc71421d70017d1298ca1ff31d35))
- Clarify indexer seed time and torrent preservation settings - ([6763dca](https://github.com/snapetech-org/chaptarrng/commit/6763dca10dd7716ee2d8d49fc94a434f14af2136))
- Record integrated upstream patches and validation checkpoints - ([6b4fb08](https://github.com/snapetech-org/chaptarrng/commit/6b4fb08edd07dd37da256880bec72c158a4fae5f))
- Capture upstream discussions and record first integrated fixes - ([1731fcb](https://github.com/snapetech-org/chaptarrng/commit/1731fcba4d1a850f71f95d1e0723a1cfbc889b5d))
- Track complete upstream issue and PR inventory - ([ad0be6d](https://github.com/snapetech-org/chaptarrng/commit/ad0be6d367cf3f25137dbbe33e6476d70cee18de))
- Explain ChaptarrNG fork boundary - ([47fa5fe](https://github.com/snapetech-org/chaptarrng/commit/47fa5fe84e24a04cd1254a5733c5092641d21445))

### Performance
- *(books)* Load editions in bulk before format-sync work grouping instead of one query per book - ([eda1eaa](https://github.com/snapetech-org/chaptarrng/commit/eda1eaa8e3a212d02e19a33715358a4b205dc958))
- *(books)* Integrate upstream PR 218 with scoped refresh caches - ([8247df9](https://github.com/snapetech-org/chaptarrng/commit/8247df96ef93701369c8d579fcf6f26a159a8257))
- *(books)* Replace BuildWorkGroups's O(N^2) pairwise matching with a token-bucketed union-find (#163, #173) - ([775edaf](https://github.com/snapetech-org/chaptarrng/commit/775edaf8e1c986593a667c40da43e5384462623c))
- *(books)* Stop re-fetching RootFolders per work group during monitoring sync (#163) - ([73a7b26](https://github.com/snapetech-org/chaptarrng/commit/73a7b269ea4adf8b3a70ddb7b1bdf51e5abdda52))
- *(books)* Also stop CloneStoredBook eagerly resolving edition-level ASIN - ([05da1d6](https://github.com/snapetech-org/chaptarrng/commit/05da1d629c534fe3e9e3ae2bc46ccfff731f1bcf))
- *(books)* Stop CloneStoredBook eagerly loading Author on every book save (#163) - ([fb51f08](https://github.com/snapetech-org/chaptarrng/commit/fb51f088deabb291923ed5e036271cfdd745ec6a))
- *(commands)* Throttle progress-message persistence to once per second per command - ([677feb4](https://github.com/snapetech-org/chaptarrng/commit/677feb4360ab807777e4314104cdce5210bae477))
- *(covers)* Skip per-book cover reconciliation when a refresh made no changes (#178) - ([394e840](https://github.com/snapetech-org/chaptarrng/commit/394e84080206ae68bd83f6f6cefe927116a4387e))
- *(covers)* Stop enumerating MediaCover/Books for every book without a cover folder - ([5f4d9b5](https://github.com/snapetech-org/chaptarrng/commit/5f4d9b568d131bca3dd4a2a7a9d346016283ddf7))
- *(db)* Index MetadataFiles/ExtraFiles by (AuthorId, BookId) - ([f04f995](https://github.com/snapetech-org/chaptarrng/commit/f04f995097fd0c7ace76a9f72a922af809ed3b0a))
- *(extras)* Batch the post-scan metadata-file cleanup and probe the right base path first - ([0477a7a](https://github.com/snapetech-org/chaptarrng/commit/0477a7aaca40c98c3cbf45a91f0e2b3fdff22964))
- *(frontend)* Integrate upstream bundle optimizations - ([d7bc6be](https://github.com/snapetech-org/chaptarrng/commit/d7bc6be3fd2a611fa5a23f8e9ae576c99315858d))
- *(frontend)* Code-split routes with React.lazy and webpack splitChunks - ([4fa4ced](https://github.com/snapetech-org/chaptarrng/commit/4fa4ced1e56eba168ec9201380a84042d135d5e6))
- *(import)* Avoid hashing opted-in destinations - ([076b539](https://github.com/snapetech-org/chaptarrng/commit/076b539c18eec55b6a1c548b1d5b23aa95ca312c))
- *(import-lists)* Optimize local library lookup during import list sync - ([c15fce3](https://github.com/snapetech-org/chaptarrng/commit/c15fce397c4cefb489e7b2ae7ba02a7faf694081))
- *(manual-import)* Reuse an existing local work instead of reconciling the whole author catalogue - ([d1ce838](https://github.com/snapetech-org/chaptarrng/commit/d1ce838453e6d5c1a61f20b2eb130588a6636889))
- *(matching)* Don't use bare function words as Postgres FTS recall keys - ([d246e1d](https://github.com/snapetech-org/chaptarrng/commit/d246e1d85532ee80730d1d85fa158fcbc7f0060c))
- *(refresh)* Stop refetching Book+Author+Editions+Files per book in author refresh (#163) - ([4922412](https://github.com/snapetech-org/chaptarrng/commit/49224120faf5a93f9f07f640781a1b5e3cb5c634))
- Clone monitoring snapshots only for changed work groups - ([3b804d9](https://github.com/snapetech-org/chaptarrng/commit/3b804d960b5a477a519706c524d0f981e3f6e394))
- Batch the author-list N+1 queries, the queue conversion-job lookups and bound the Postgres FTS recall candidates - ([a415889](https://github.com/snapetech-org/chaptarrng/commit/a41588968836d73f1df28abc84bb12d89a6911c6))
- Prune common tokens from the Postgres FTS book recall - ([2340b99](https://github.com/snapetech-org/chaptarrng/commit/2340b992166c63cf9b7c26c0b1d371032cc7d691))
- Count missing books with a semi-join and bound per-author file statistics - ([76c7c22](https://github.com/snapetech-org/chaptarrng/commit/76c7c224927297b0cef38c064de033db02df0417))

### Refactoring
- *(frontend)* Replace moment.js with dayjs - ([9751a80](https://github.com/snapetech-org/chaptarrng/commit/9751a809bdf4189687d4546a0db7a6fce8ef7b17))
- *(frontend)* Use cherry-picked lodash imports - ([98e43db](https://github.com/snapetech-org/chaptarrng/commit/98e43dbd9562f5a3c2d5abb0ad413a8fb3ddaa96))

### Testing
- *(MyAnonaMouse)* Cover mam_id rotation and persistence - ([3d6923b](https://github.com/snapetech-org/chaptarrng/commit/3d6923b8bc3cb55ff19b76892fad6e38552be284))
- *(author-stats)* Cover future ebook progress - ([ec1308f](https://github.com/snapetech-org/chaptarrng/commit/ec1308f8f02828bba68e75d8134ec97c7e51c44e))
- *(authors)* Cover RanobeDB ID handoff - ([1de1ef7](https://github.com/snapetech-org/chaptarrng/commit/1de1ef7370a327db7ebe059c5e7d021cb50bb9c7))
- *(ci)* Match trusted runner selector grouping (#104) - ([6731a43](https://github.com/snapetech-org/chaptarrng/commit/6731a436d890e2b56ebdb0875726e6250e720080))
- *(ci)* Cover retained pull request head fetch - ([0faa887](https://github.com/snapetech-org/chaptarrng/commit/0faa8876abe564fb563d65cdc230d11651297056))
- Preserve native paths in rename fixtures - ([2eb6cfc](https://github.com/snapetech-org/chaptarrng/commit/2eb6cfc54e6dbc4a0a8f9f9e2d8551b6ff548736))
- Remove duplicate controller fixture arguments - ([085e519](https://github.com/snapetech-org/chaptarrng/commit/085e5198523a5eb120ab094cd13e4fd48d63ddce))
- Use isolated temp paths for rename fixtures - ([f921cea](https://github.com/snapetech-org/chaptarrng/commit/f921ceacf50f628a5af12b5a5e6dde3b5a3efddf))
- Normalize workflow line endings in contract - ([5e51bf7](https://github.com/snapetech-org/chaptarrng/commit/5e51bf70bdf2e22ceffbaa0f6b3e857ba3e385d1))
- Preserve native paths in rename fixtures - ([c5c14d8](https://github.com/snapetech-org/chaptarrng/commit/c5c14d8d9b58dbd4ec2e1c5bf2bd1eef213a1415))
- Use isolated temp paths for rename fixtures - ([81d5789](https://github.com/snapetech-org/chaptarrng/commit/81d578938bdf5424da60f82b50570c745b273d14))
- Use fully qualified rename fixture paths - ([5ad2d33](https://github.com/snapetech-org/chaptarrng/commit/5ad2d33e5cdd216bb17bf500d77a8233e5526aa9))
- Use rooted paths for rename fixtures - ([cd8d534](https://github.com/snapetech-org/chaptarrng/commit/cd8d53469d2b6e3cac6b706f31861b83ff2b7b95))
- Update localization service stubs - ([54a5efa](https://github.com/snapetech-org/chaptarrng/commit/54a5efa649b9dd75caf4dfb5bfcac981f784024d))
- Make rename fixture paths platform-native - ([6e4211d](https://github.com/snapetech-org/chaptarrng/commit/6e4211dd3eb58ff51963c8d327274fb36c56ca60))
- Normalize fake paths across platforms - ([6fa426c](https://github.com/snapetech-org/chaptarrng/commit/6fa426ca8461e28ac343a111939108c7a04b794c))
- Cover bookless download history - ([2a0c2e4](https://github.com/snapetech-org/chaptarrng/commit/2a0c2e471b5268bc544540f5fdf23fa25a70c651))
- Guard Bookshelf database before migrations - ([35ca5cd](https://github.com/snapetech-org/chaptarrng/commit/35ca5cd563468337fb806b0a73ea09a230ddb65c))
- Adapt wanted narration repository to lifecycle persistence - ([1e84bd2](https://github.com/snapetech-org/chaptarrng/commit/1e84bd288d6c91da263924a5a41f39270ca33d4f))
- Adapt tag prefetch fixtures to current filesystem abstraction - ([b383b6b](https://github.com/snapetech-org/chaptarrng/commit/b383b6b88c990a2ed7791b20b7d0ab4b59a27374))
- Support bulk edition loading in cross-format insert fixture - ([7fa62a6](https://github.com/snapetech-org/chaptarrng/commit/7fa62a6c59c000f1f5e69cbcfb0b0af6a6b5d4ad))
- Construct malformed UTF-16 cases outside attribute metadata - ([bebf3cd](https://github.com/snapetech-org/chaptarrng/commit/bebf3cd29b007340efd41da260f9916b4176f8f3))

### Maintenance
- *(distribution)* Remove YunoHost publishing support - ([c26dd7f](https://github.com/snapetech-org/chaptarrng/commit/c26dd7f366bb0c7af1c7c96455ad0dec020ac158))
- *(distribution)* Retire YunoHost packaging - ([ee048be](https://github.com/snapetech-org/chaptarrng/commit/ee048be417b84f049f5e2d41f726d6b869fed807))
- *(frontend)* Remove moment dependency - ([7bb5ccf](https://github.com/snapetech-org/chaptarrng/commit/7bb5ccf4ccab07f9c54d0bd6dd810623446ecec9))
- *(repository)* Migrate GitHub owner links (#109) - ([8b228fd](https://github.com/snapetech-org/chaptarrng/commit/8b228fdccbd8c11fe4ebbd25753b8034a87f05b7))
- Route trusted main validation through shared pool (#101) - ([55ce02f](https://github.com/snapetech-org/chaptarrng/commit/55ce02f37319859ffb3a9ce62ac10541cd08b864))
- Run independent validation tasks in a bounded pool (#99) - ([4364742](https://github.com/snapetech-org/chaptarrng/commit/4364742e7811aca8c501c13897b5dd6daabe1710))
## [0.9.969](https://github.com/snapetech-org/chaptarrng/compare/v0.9.968..v0.9.969) - 2026-10-15

### Bug Fixes
- *(http-cache)* Enforce unique cached response URLs - ([82b81bf](https://github.com/snapetech-org/chaptarrng/commit/82b81bf270c10cfadff53ee4a2b6b62f3a52f315))## [0.9.968](https://github.com/snapetech/chaptarrng/compare/v0.9.967..v0.9.968) - 2026-10-12

### User-facing changes

#### Changed

- **Build:** **Breaking:** ImageSharp 4.1.3 requires a valid Six Labors license when ChaptarrNG is built from source. Official images already include a licensed build, so no build key must be configured at runtime. All use remains subject to Six Labors' license terms.
  - **Action required:** Configure SIXLABORS_LICENSE_KEY when building from source; official images need no build key at runtime.
- **Validation:** ChaptarrNG CI reconciles source-bound receipts across Linux, Windows, macOS, and disposable PostgreSQL runners before completing validation. Fingerprints remain consistent across Git-normalized Windows line endings.

#### Fixed

- **Validation:** Local validation selects macOS and Windows test tasks correctly, and hosted reconciliation preserves receipts from every runner.
- **Distribution:** Manual ChaptarrNG releases now forward the private ImageSharp build license to validation and image publishing, allowing both reusable build workflows to run under this repository's account settings.
- **Release Workflow:** Grant release validation the read-only Actions permission needed to reconcile its test receipts, allowing GitHub to start the reusable workflow.

#### Security

- **Image Processing:** ChaptarrNG updates the licensed ImageSharp dependency to v4.1.3 and removes the audit suppressions previously required for v3.2.0 advisories.

### Security
- *(release-notes)* Update ImageSharp security note - ([93718b9](https://github.com/snapetech/chaptarrng/commit/93718b92baf0bdc48995466b493a4f4554f0bffb))

### Features
- *(testing)* Add source-bound validation engine - ([7c3637d](https://github.com/snapetech/chaptarrng/commit/7c3637d025af7307bcc28040cccb3094400375e0))

### Bug Fixes
- *(deps)* Update ImageSharp to 4.1.3 - ([6613a51](https://github.com/snapetech/chaptarrng/commit/6613a51d35f60b75fec42d88d2df5224e05f74de))
- *(release)* Pass receipt read permission - ([9b180d5](https://github.com/snapetech/chaptarrng/commit/9b180d5818b42906eaab25d185f04bb1af105bf7))
- *(release)* Forward ImageSharp license explicitly - ([2b202d8](https://github.com/snapetech/chaptarrng/commit/2b202d884cff0b777b2adf0f8fd608b17373bb1c))
- *(security)* Update ImageSharp to patched 3.2.0 - ([f5096e3](https://github.com/snapetech/chaptarrng/commit/f5096e3323b86c3ff85d4a45316fef9151f098f2))
- *(validation)* Preserve runner receipts during reconciliation - ([5308ac7](https://github.com/snapetech/chaptarrng/commit/5308ac7a28c5686b76fa0c35750bf948bf8a4fa0))
- *(validation)* Map local platforms to CI node IDs - ([cf3e111](https://github.com/snapetech/chaptarrng/commit/cf3e11142ce493e55928c98f52b4847e54a647d6))
- *(validation)* Assign test tasks to CI nodes - ([fd59dd3](https://github.com/snapetech/chaptarrng/commit/fd59dd3666aca541c5c184007193f398295360e7))
- *(validation)* Normalize fingerprints across platforms - ([6ced970](https://github.com/snapetech/chaptarrng/commit/6ced9702b30abe58dd70fac46a1a4d20926d795d))

### Documentation
- *(imagesharp)* Clarify runtime license terms - ([91d63f5](https://github.com/snapetech/chaptarrng/commit/91d63f584d0022f98e583045dce2ba09f7868be3))
- *(imagesharp)* Explain build licensing - ([733a6de](https://github.com/snapetech/chaptarrng/commit/733a6dea7b16202a6f212cc93f2e1a98cce22f1c))
- *(security)* Clarify remaining ImageSharp advisories - ([a1754ee](https://github.com/snapetech/chaptarrng/commit/a1754ee2f37bcbc8a0fbbb094e9842e89ab7c01b))

### Testing
- *(validation)* Isolate local plan node fixtures - ([db048b5](https://github.com/snapetech/chaptarrng/commit/db048b54a0cdb62f808a4610ba4b5f9295b04348))

## [0.9.967](https://github.com/snapetech/chaptarrng/compare/v0.9.966..v0.9.967) - 2026-10-07

### User-facing changes

#### Added

- **Integration:** SeerrNG capability discovery now advertises the ebook and audiobook availability statistics already exposed by ChaptarrNG, so integrations can select the format-aware data reliably.

## [0.9.966](https://github.com/snapetech/chaptarrng/compare/v0.9.942..v0.9.966) - 2026-10-06

### User-facing changes

#### Fixed

- **Navigation:** Fresh installations now reach the sign-in screen instead of failing while React Router initializes.

#### Security

- **Logging:** Log messages now escape line separators and redact sensitive values from download titles, filenames, and browser errors. The build also uses patched CSS selector and source-map parsers.

### Bug Fixes
- *(security)* Cleanse remaining import log values - ([9495f80](https://github.com/snapetech/chaptarrng/commit/9495f804d80d9f57cd93376d4a2c28d12294a1fb))
- *(security)* Update vulnerable frontend parsers - ([54be4d9](https://github.com/snapetech/chaptarrng/commit/54be4d9f763bc58db7dbd8edb8ee26af01c6dbc1))
- *(security)* Cleanse untrusted values before logging - ([0765225](https://github.com/snapetech/chaptarrng/commit/07652259b0719510eca30600eb88a96dc66595a7))
- *(web)* Restore router context and modernize client types - ([69f021f](https://github.com/snapetech/chaptarrng/commit/69f021f7b254c1681452cc4b4c92a13cdba131bb))

### Documentation
- Update fork history and download platform support (release-note: none) - ([839ef20](https://github.com/snapetech/chaptarrng/commit/839ef20c0338a1c8eb9bc0dc2ddd0051da5bf986))

### Testing
- *(search)* Model monitored ebook state - ([b66cb97](https://github.com/snapetech/chaptarrng/commit/b66cb97082b84483c8d92e9a870ac4afab5f93ba))

## [0.9.942](https://github.com/snapetech/chaptarrng/compare/v0.9.942..v0.9.942) - 2026-10-05

### User-facing changes

#### Added

- **Indexers:** Configure prioritized ebook download sources with API-key grabs and browser-assisted fallback. Chaptarr tracks transfer progress and imports supported book files directly, without requiring a separate download client.

#### Changed

- **Documentation:** The README and fork guide now map ChaptarrNG's format-aware request lifecycle, Direct Download flow, backup protection, and maintained packages to their setup and API references.

#### Fixed

- **Container Image:** The container build now installs only Playwright's headless Chromium shell on AMD64 and ARM64, avoiding OS dependency detection under emulation. ARMv7 images omit Chromium, while API-resolved direct downloads remain available.

### Bug Fixes
- *(docker)* Use Noble runtime library package names - ([8568c9c](https://github.com/snapetech/chaptarrng/commit/8568c9c170341f089aa522fa60c5461691f52d12))
## [0.9.942](https://github.com/snapetech/chaptarrng/compare/v0.9.941..v0.9.942) - 2026-10-05

### Bug Fixes
- *(release)* Build headless browser images across architectures - ([298f33a](https://github.com/snapetech/chaptarrng/commit/298f33a32aaf4274a70ce2a3c54cbefcc3f08171))
- Serialize direct download state persistence - ([521627e](https://github.com/snapetech/chaptarrng/commit/521627e29271197a3bc9b8a8df6fa7385d5f8b26))
- Wait for direct download cancellation cleanup - ([0aef210](https://github.com/snapetech/chaptarrng/commit/0aef210ca377f5d105b466bd9df0da0fb1943d1e))
- Close download stream before promoting staged file - ([13c1b65](https://github.com/snapetech/chaptarrng/commit/13c1b653882ea19850234cbd85f33a35038efd6a))
- Harden browser-assisted direct downloads - ([17ef911](https://github.com/snapetech/chaptarrng/commit/17ef9119ebbb69bf7c1cdd1912a1a378b6d57903))

### Documentation
- Complete fork feature history - ([4833664](https://github.com/snapetech/chaptarrng/commit/4833664364ccdbf54dbcf5e8fe76f55d22f85e9b))
- Format fork feature guide - ([c38b23a](https://github.com/snapetech/chaptarrng/commit/c38b23ad5d2ea50c1423016822bcf5c38daa7420))

### Testing
- Await direct download redownload before cleanup - ([472cded](https://github.com/snapetech/chaptarrng/commit/472cdedbf676e619128d9eb088a57b5c638f78e5))
- Gate Playwright browser fixtures explicitly - ([4519bbf](https://github.com/snapetech/chaptarrng/commit/4519bbfd61b692cb14b2413402e7c1e70882f9aa))

### Maintenance
- Isolate test assembly compilation - ([cbee1cb](https://github.com/snapetech/chaptarrng/commit/cbee1cb10dd38b142f0f5d845f7af9ef4c2de8d6))## [0.9.942](https://github.com/snapetech/chaptarrng/compare/v0.9.941..v0.9.942) - 2026-10-05

### Bug Fixes
- Serialize direct download state persistence - ([521627e](https://github.com/snapetech/chaptarrng/commit/521627e29271197a3bc9b8a8df6fa7385d5f8b26))
- Wait for direct download cancellation cleanup - ([0aef210](https://github.com/snapetech/chaptarrng/commit/0aef210ca377f5d105b466bd9df0da0fb1943d1e))
- Close download stream before promoting staged file - ([13c1b65](https://github.com/snapetech/chaptarrng/commit/13c1b653882ea19850234cbd85f33a35038efd6a))
- Harden browser-assisted direct downloads - ([17ef911](https://github.com/snapetech/chaptarrng/commit/17ef9119ebbb69bf7c1cdd1912a1a378b6d57903))

### Documentation
- Complete fork feature history - ([4833664](https://github.com/snapetech/chaptarrng/commit/4833664364ccdbf54dbcf5e8fe76f55d22f85e9b))
- Format fork feature guide - ([c38b23a](https://github.com/snapetech/chaptarrng/commit/c38b23ad5d2ea50c1423016822bcf5c38daa7420))

### Testing
- Await direct download redownload before cleanup - ([472cded](https://github.com/snapetech/chaptarrng/commit/472cdedbf676e619128d9eb088a57b5c638f78e5))
- Gate Playwright browser fixtures explicitly - ([4519bbf](https://github.com/snapetech/chaptarrng/commit/4519bbfd61b692cb14b2413402e7c1e70882f9aa))

### Maintenance
- Isolate test assembly compilation - ([cbee1cb](https://github.com/snapetech/chaptarrng/commit/cbee1cb10dd38b142f0f5d845f7af9ef4c2de8d6))## [0.9.941](https://github.com/snapetech/chaptarrng/compare/v0.9.940..v0.9.941) - 2026-10-04

### User-facing changes

#### Changed

- **Donations:** The Donations page now directs Ko-fi contributions to the Snapetech fork maintainer, clearly separating this fork from upstream Chaptarr.

#### Security

- **Integrations:** ChaptarrNG now supports a SeerrNG-specific service key that is limited to book requests, searches, library status, and pending author imports while keeping the normal API key available for full administrative access.
  - **Action required:** Configure a separate CHAPTARR__AUTH__SEERRAPIKEY value in the ChaptarrNG container and enter that value in SeerrNG's Bookshelf service settings.

### Features
- Add restricted SeerrNG service API key - ([113c5ff](https://github.com/snapetech/chaptarrng/commit/113c5ff4e4bf02b1e4bdf9ea857b62e632af592a))

### Maintenance
- Use Snapetech Ko-fi support link - ([756e012](https://github.com/snapetech/chaptarrng/commit/756e012b264ef4faf3591f539afb1888b51e1677))

## [0.9.940](https://github.com/snapetech/chaptarrng/compare/v0.9.939..v0.9.940) - 2026-10-04

### User-facing changes

#### Added

- **Integrations:** ChaptarrNG now exposes a versioned system capabilities endpoint that reports the Bookshelf API's provider-ID dialect and supported media types to compatible request managers such as SeerrNG.

#### Changed

- **Donations:** The Donations page now directs Ko-fi contributions to the Snapetech fork maintainer, clearly separating this fork from upstream Chaptarr.

#### Security

- **Backups:** Full backups can now be stored as authenticated encrypted archives when a backup passphrase is configured, while existing ZIP backups remain restorable.
  - **Action required:** Mount a secret file containing a long random passphrase and set CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE to its in-container path. Protect or remove existing plain ZIP backups separately.

### Features

- Add SeerrNG capability discovery and encrypted backups - ([865e0b1](https://github.com/snapetech/chaptarrng/commit/865e0b14b6b26a4eeda8d8ce381d94ed4dba4346))

### Bug Fixes

- *(ci)* Rely on repository default CodeQL setup - ([319bceb](https://github.com/snapetech/chaptarrng/commit/319bcebbb3ec43b4558c737a8bcb9fd77ddea31c))

### Maintenance

- Use Snapetech Ko-fi support link - ([756e012](https://github.com/snapetech/chaptarrng/commit/756e012b264ef4faf3591f539afb1888b51e1677))

## [0.9.939](https://github.com/snapetech/chaptarrng/compare/v0.9.938..v0.9.939) - 2026-10-01

### User-facing changes

#### Fixed

- **App Experience:** NG-branded icons and bundled frontend assets refresh the app's presentation and downloads. Cookie-authenticated users can open the web UI, and unusually long Transmission ETAs no longer break queue responses.

### Features
- *(release)* Add changelog and Discord announcements (#39) - ([65887a0](https://github.com/snapetech/chaptarrng/commit/65887a0963626d9bb2545a2c9919208ea0ad0a49))
- Improve package branding and downloads - ([13d5e53](https://github.com/snapetech/chaptarrng/commit/13d5e53c6089ba092a5d437f8e6b2b4747653735))
- Publish pending ChaptarrNG updates - ([1171a87](https://github.com/snapetech/chaptarrng/commit/1171a870bf1f44dac799eae53f44507562ed78f8))

### Bug Fixes
- *(security)* Enumerate only fixture test files - ([4d60eba](https://github.com/snapetech/chaptarrng/commit/4d60eba5e02c72646a0bc144d6bf4b74679e1bce))
- *(security)* Constrain test fixture file paths - ([14dfae5](https://github.com/snapetech/chaptarrng/commit/14dfae526a54fb3c742bf116927be2afda60f859))
- Align OpenAPI security scopes with new type - ([9c2d5fe](https://github.com/snapetech/chaptarrng/commit/9c2d5fe93d0157e10e45debc0809a8e4801377bb))
- Migrate built-in FluentValidation calls to v12 - ([53a09f4](https://github.com/snapetech/chaptarrng/commit/53a09f4ce70f216ccf1223efa4ac462b3e57a256))
- Migrate configuration change handler for NLog 6 - ([7f8e9f2](https://github.com/snapetech/chaptarrng/commit/7f8e9f2edabefdb5789ac6d790035255e2fde3c0))
- Resolve CI compatibility blockers - ([accc257](https://github.com/snapetech/chaptarrng/commit/accc257c8938981b20fa276340d2afc19d393b3a))

### Maintenance
- Update security dependencies and compatibility - ([63afb7f](https://github.com/snapetech/chaptarrng/commit/63afb7f6b309003fc7d53f8a3772c3a1d271968f))## [0.9.938](https://github.com/snapetech/chaptarrng/compare/v0.9.937..v0.9.938) - 2026-09-29

### Changed

- **Distribution:** ChaptarrNG's Unraid setup now points to its dedicated package repository, keeping the application release and Community Apps listing independently maintained.

## [0.9.937](https://github.com/snapetech/chaptarrng/compare/v0.9.936..v0.9.937) - 2026-09-29

### Added

- **Books:** Scoped book lookups now retain the requested ebook or audiobook format through metadata searches, so SeerrNG receives matching catalog results.
- **Distribution:** The release adds ChaptarrNG's maintained fork identity and Unraid template.

## [0.9.936](https://github.com/snapetech/chaptarrng/releases/tag/v0.9.936) - 2026-09-29

### Added

- **Books:** ChaptarrNG adds format-scoped requests and a durable pending-import lifecycle, allowing SeerrNG to resume requests after author metadata preparation and safely share queued work.
- **Identity:** ChaptarrNG is a standalone book manager that keeps the Chaptarr API identity for Readarr-compatible clients.
