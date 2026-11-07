# BookshelfNG user workflow parity map

For the current code-first completion sequence and concrete unchanged-client
gaps, use the [2026-10-24 replacement reassessment](maintainers/BOOKSHELFNG_REPLACEMENT_REASSESSMENT_2026-10-24.md).
This crosswalk records implementation and acceptance evidence; its older
milestones are not a current missing-code count.

This map compares the everyday work a BookshelfNG user needs to do in ChaptarrNG.
It describes source affordances, not a published image. BookshelfNG retirement
is not yet accepted, so ChaptarrNG releases and migration channels remain
frozen.

“Available in source” means a user-facing route or action is present. It does not
mean the workflow has been exercised against a real provider, download client,
representative library, or installed build.

| User task | ChaptarrNG source and affordance | Remaining end-user acceptance |
| --- | --- | --- |
| Get a new instance ready | First-run account setup is reachable even when authentication was configured but the installation has no local user; existing accounts keep their configured authentication mode. Startup errors identify the failed app-data source and offer Retry, while Local Storage errors explain the browser issue separately. System > Quickstart checks format roots, profiles, indexers, clients, and optional integrations. It has direct setup flows for MyAnonamouse and Hardcover and shows Calibre and AudioBookShelf when relevant. Settings now groups destinations by Library, Search, Connect, General, and System, with Quickstart linked from both Settings and System. | Walk through a clean and migrated setup on desktop and mobile, including first-account setup, existing-user authentication, missing roots, an empty indexer list, and a failed provider test followed by retry. |
| Choose the default format for new items | Add New dialogs share a saved default format. Users can change it in Settings > UI or with **Set as default** in an add dialog; individual items can still override it. | Verify persistence after reload and confirm author, book, and series dialogs all start with the saved value. |
| Set how new authors get format folders | Settings > Media Management > New Author Folders has an opt-in switch for initializing missing ebook and audiobook paths from their matching default roots. It is off by default. Migration preserves the saved flag and maps BookshelfNG's configured new-author roots to ChaptarrNG's per-format defaults; explicit ChaptarrNG values take precedence. Existing author paths and files are unchanged. This follows [BookshelfNG issue #114](https://github.com/snapetech-org/bookshelfng/issues/114). | Verify the toggle saves and reloads, each chosen format keeps its explicit root, unselected formats stay unassigned when disabled, and existing authors and files are untouched. Check both migrated enabled and disabled settings with configured roots. |
| Add an author, book, or series | All three flows support audiobook, ebook, or both where relevant; root folders and quality profiles are visible first, with monitoring, metadata, and tags under **More options**. Missing setup links to Quickstart. Root-folder loading failures show a retry. Author profile editors can apply each format's current root-folder quality and metadata profiles; the help text clarifies this copies the current defaults and later changes require applying them again. A missing default is identified and its reset is unavailable. | Exercise each format, both formats, saved defaults, one-item overrides, profile reset for one format while preserving the other, missing roots/profiles/defaults, existing-author book adds, and add failures. |
| Fill a missing format root on existing authors | Settings > Media Management > Root Folders offers a read-only preview and skip reasons, then separate confirmation. It rechecks current settings, fills only eligible missing paths, queues author refreshes, and never moves book files. Failed preview or queue requests remain retryable; the dialog cannot be dismissed while queueing, duplicate submissions are ignored, fresh previews clear stale queue errors, and invalid or inconsistent counts cannot dispatch an update. | Check zero-candidate and skipped states, eligible updates, manual overrides, missing defaults/profiles, malformed counts, queue failure/retry, and that files stay in place. |
| Choose a storage path | The shared File Browser announces directory-load errors with available details and retries the path that failed to load. | Exercise root and nested directory loads, permission failures, retry, and typed path changes. |
| Find a title in the catalog | Add New has provider selection, result filtering, and submit-to-search by default; Search as you type is optional. Query changes and clearing cancel stale requests. Failed searches show an announced error and an explicit Retry action when search-as-you-type is enabled. Results that cannot be added because provider IDs are missing show a visible explanation and suggest trying another result or source. | Check first search, Enter, repeated same-query search, preference persistence, retry after provider errors in both search modes, non-addable result guidance, and clearing during a slow request. |
| Search Hardcover catalogs and handle rate limits | Native lookup, fallback catalog search, author-book validation, and search enrichment share the Hardcover GraphQL executor. It applies one-second request pacing, a shared serialized request gate, `Retry-After` and rate-limit-header cooldowns, daily quota protection, and the configured API URL. User-driven Add New searches pass the interactive quota flag through each request. | Verify native and fallback search against a configured account, including exhausted daily quota, HTTP 429 with both forms of `Retry-After`, retry after cooldown, and provider token or endpoint changes. |
| Browse and understand the library | Authors, Books, Bookshelf, Calendar, and Unmapped Files are reachable from Library. Calendar load failures retry the current view. The Books index has a saved **Both** filter with a format label on each record; editing an author from a book row opens the matching format tab. Bookshelf groups recognized same-work records. Book details include edition and narration information; series details compare narrators and volumes. Author history and book-edit edition failures offer retry. Failed bookshelf loads and desktop book-overview cards have a retry action; small screens skip hidden overview requests. Edition lists and narrator comparisons discard responses from a previous title; delete confirmation only uses sibling-format details for the selected book. | Browse a representative mixed-format library on narrow and wide screens; check empty, loading, retry after a failed request, partial-metadata, and no-file states. |
| See ebooks and audiobooks together in Books | The Books index **Both** filter lists every audiobook and ebook as its own record and shows its format label. Monitoring, status, narrator controls, and quality profiles use that record's format. | Verify paging, custom filters, sort order, per-format profiles, monitoring labels, selected-row edits, and reload persistence across both formats. |
| See related ebooks and audiobooks together in Bookshelf | Bookshelf's **Formats** view loads both formats in one pass, groups records only when canonical Hardcover, Goodreads, or Open Library work IDs match, and leaves unrelated or unidentified records separate. A help popover beside the format toggle explains this rule and directs users to Refresh the book and author pages when IDs are missing; its trigger has a 44px touch target. Each format keeps its own progress, detail link, and monitor toggle; bulk monitoring applies to both. Bulk failures show the server error and preserve the selection for retry; per-book failures produce an error notification. | Check matched, unmatched, duplicate-edition, missing-ID, and provider-alias records; confirm the popover works by mouse, touch, and keyboard; switch formats repeatedly, reload the page, retry failures, and confirm bulk and per-book monitoring. |
| Move between the ebook and audiobook for the same work | Search results link to tracked records in each format. Book details label both formats, show a loading status while related formats are checked, link to same-work records, and offer the missing format in the standard add modal when the work and author have provider IDs. The add action appears only after the server confirms no same-work record exists; when IDs are missing, the disabled action explains the refresh needed. Related-format matches reload when catalog identities change, and lookup failures have a visible retry action. | Confirm identity matching never links unrelated works; test duplicate editions, loading feedback, missing identity guidance, metadata refresh, endpoint failure/retry, and successful add against real catalog data. |
| Decide what to monitor | Add flows and author/book editors expose per-format monitoring, initial-book monitoring, and future-book policies. The Book editor retries a failed edition list without discarding selected monitoring settings. In **Formats**, each book keeps its own monitor toggle and the bulk action applies to both formats. Wanted has Missing and Cutoff Unmet pages, both with retry after load failures. | Confirm per-book changes affect only one format, bulk changes affect both, and automatic searches honor the chosen monitoring state. |
| Search and grab a release | Book actions offer automatic and interactive release search. Series rows can search for a pack and send files to Manual Import. Direct Download provides an ebook-only source path. The nine BookshelfNG built-in download clients keep the shared legacy category as a fallback when either format-specific category is blank. | With a supported indexer and client, search, inspect, grab, retry, and import representative audiobook and ebook releases. Include a migrated shared category with one blank format category, then verify queue visibility and post-import behavior. |
| Reuse a torrent already in the client | Duplicate-hash detection can offer **Adopt Existing Torrent**; automatic searches skip matching torrents. Adoption tracks the existing item instead of starting a second download. | Check wrong categories, completed and seeding states, stale results, client outages, restart recovery, and final import. |
| Track or stop a download | Activity groups Queue, History, Blocklist, and Ignored Downloads. Queue removal explains ignore and remove choices. Blocklist and Ignored Downloads announce list errors and retry the current list. | Confirm ignore leaves the client item untouched; verify retry, blocklist, client removal, and history after restart. |
| Import or recover files | Manual Import and Unmapped Files provide review paths. The author and book pickers explain empty and no-match states; book-list failures offer retry and hide stale rows. Edition and quality selectors keep modal controls visible on failure, offer retry, and explain empty results; empty quality lists cannot be submitted. Manual Import load and preview failures announce the error, hide stale rows, disable submission until the list is current, and offer Retry. File Browser failures announce available error details and retry the current directory. File details and book-file quality-profile failures also expose Retry. Failed Unmapped Files loads show a direct retry action; Refresh Files and Retry Match stay disabled until a successful load has files; row controls have accessible names. Pending-author imports retain requested books and formats while author preparation retries; retry and cancellation are exposed. The CLI helper can queue a match retry across both formats; see [Unmapped files](UNMAPPED_FILES.md). | Validate author/book/edition/quality picker empty and failure states, Manual Import retry and stale-row safety, File Browser and book-file retry, then Unmapped Files initial-load failure/retry, ambiguous matches, multi-file assignment, provider failure, shared requests, retry, and cancellation against representative files. |
| Configure conversion | Settings > Media Management > Conversion retries the current settings request after a load error and shows available server details. | Check initial-load failure, retry, and recovery with saved conversion settings unchanged. |
| Organize files safely | Media-management settings configure roots and naming. Format-aware move/retag workflows preview affected files, check destination conflicts, and preserve linked sidecars. Failed organize, retag, and convert previews expose available error details and a Retry action. ChaptarrNG also provides single-author and bulk command-line helpers; see [Media moves](MEDIA_MOVES.md). | Exercise preview and retry after a failed preview, copy/move, mixed roots, collisions, interrupted moves, external files, and a large library. |
| Automate from lists | Import Lists supports Preview Next Sync and per-list sync history, including read-only preview counts and provider-state commit status. Preview and history errors show available API details and include retry actions; closing the modal during a preview cancels stale results and resets progress on reopen. Hardcover list setup shows connection loading and account status; a failed status check explains the problem and can be retried before changing credentials. | Test supported list providers with an unchanged saved configuration, then compare preview with a later scheduled sync. |
| Connect external tools and services | Settings > Connect, Metadata, Indexers, Download Clients, and Import Lists expose providers. Quickstart opens MyAnonamouse and Hardcover setup. Calibre-backed roots, Calibre Content Server, AudioBookShelf library mappings, and per-format AudioBookShelf sidecars are documented in [external library integrations](EXTERNAL_LIBRARY_INTEGRATIONS.md). BookLore and BookOrbit remain separate destinations with separate login guidance. The SeerrNG contract has capability discovery and format-aware operations; request dispatch qualifies numeric provider work IDs, and Editions/Narrations failures offer a visible retry. | Focused regressions cover bare numeric Hardcover lookup IDs through the format facade and plus-encoded series titles through the restricted-key policy. Still verify saved credentials, notifications, request fulfillment with live providers, reverse proxies, and unchanged clients. |
| Deliver imported books to reader apps | Settings > Connect offers BookLore BookDrop and BookOrbit Book Dock as separate upload destinations. Each has its own URL and account; imports retain their source files, and uploads are sequential. The [BookLore integration guide](BOOKLORE_INTEGRATION.md) explains partial-batch behavior and keeps migrated BookLore credentials attached to BookLore. | Verify each destination with a real account, including rejected files and partial retries. Confirm a migrated BookLore connection is left intact when adding BookOrbit. |
| Preserve webhook consumers | Standard migrated webhook events project mapped book rows back to their BookshelfNG source work ID; author and imported file IDs retain their source values. Native-only ChaptarrNG books keep canonical target IDs. Optional canonical lifecycle events remain a separate ChaptarrNG contract. | Send grab, import, book-added, book-delete, and file-delete events for a split migrated work and a new target-only work; confirm event payload IDs and saved endpoint credentials. |
| Back up and restore | System > Backup supports the app’s scheduled and portable backup paths; current source includes BookshelfNG archive restore and encrypted export/restore. Backup-list failures show available details and a retry action. | Restore a representative backup, verify profiles and settings, test wrong-passphrase and rollback paths, and confirm no secret values appear in logs. |
| Diagnose and maintain the service | System exposes Status, Tasks, Updates, Events, and Log Files; MQTT is configured in Settings > Connect, Prometheus uses a separate listener enabled through container settings, and aggregate diagnostics require explicit opt-in. Update details render distinct change notes and link issue numbers. | Check service restart, task failure, log access, metrics, MQTT discovery, diagnostics opt-in, and recovery on the target installation. |

## Feature-level crosswalk

This crosswalk covers the user-visible BookshelfNG feature families called out
in the retirement feature inventory. It maps each one to a ChaptarrNG surface
and keeps source presence separate from end-user acceptance.

| User-facing capability | ChaptarrNG path | End-user acceptance still needed |
| --- | --- | --- |
| Complete first-account setup after install or migration | Startup detects configured authentication with no local account and temporarily switches to the initial setup flow. When an account already exists, it preserves the configured authentication mode. | In a fresh disposable database, Chromium reached the authentication gate over a non-loopback bridge, showed the security warning, and returned to the app after saving a temporary Forms setup. Still verify the resulting account can sign in and check a migrated database with an existing account. |
| Set up audiobook and ebook libraries independently | System > Quickstart, Settings > Media Management, and the per-format author editor configure roots, quality profiles, metadata profiles, and defaults. Edit Author actions from Overview, Posters, and Table rows open with that row's media type. Author profile editors can reset one format to its selected root folder's current defaults; unavailable defaults are identified and cannot clear an explicit profile. ChaptarrNG stores formats as separate records; Bookshelf > Formats brings recognized same-work records together. | Walk through ebook-only, audiobook-only, and mixed setups. Open Edit Author from audiobook and ebook rows in each view and verify the correct format is selected and its roots/profiles stay independent. Confirm default-root/profile behavior, explicit per-author overrides, reset one format while preserving the other, missing individual defaults, and that one format's settings never change the other. |
| Fill a missing format root for existing authors | Settings > Media Management > Root Folders has **Review missing author roots**. It previews eligible authors and skip reasons; confirmation rechecks settings and queues the existing backfill and refresh command without moving files. Failed preview and queue requests can be retried; invalid counts cannot dispatch, pending submission cannot be dismissed, and duplicate clicks are ignored. | Verify zero-candidate, eligible, manual-override, missing-default, missing-profile, malformed-count, and queue-failure cases; confirm refreshes complete and files stay in place. |
| Choose automatic format-folder assignment for new authors | Settings > Media Management > New Author Folders controls whether a newly added author receives configured default roots for formats not selected during that add. It is opt-in and off by default; existing author folders are not backfilled or moved by this setting. | Turn the setting on and off, add ebook-only and audiobook-only authors, and confirm only the intended format paths are initialized without changing existing authors or files. |
| Choose catalog sources and edition-matching preferences | Settings > Metadata and Settings > Profiles > Metadata configure enabled catalogs, field preferences, language filters, weighted edition terms, and preferred series. | Save and reload choices, then compare real catalog results for language, preferred series, narrator/publisher evidence, and missing metadata. |
| Search light novels and choose an Audible storefront | Global search and Add New can search RanobeDB light-novel records; Settings > Metadata > Providers selects the Audible marketplace used for search and ASIN lookup. | Search light novels with provider IDs and verify pacing/failure handling; confirm storefront, language header, and edition identity for non-US Audible accounts. |
| Search by title or identifier | Add New supports provider selection, submit-to-search, optional search-as-you-type, and ISBN/ASIN/provider-ID matching. Existing work and edition IDs retain their provider namespace. Interactive release-search failures show an announced error and a Retry action that repeats the current search. | Exercise first and repeated searches, clearing a slow query, same-query resubmission, identifier collisions, provider errors and retry in both search surfaces, and preference persistence in a browser. |
| Review editions and narrators before choosing | Book details expose Editions and Narrations; series details compare narrators across volumes. Edition cards retain language, format, narrator, ownership, and monitoring context when the provider supplies them. Narrator discovery errors are announced and can be retried; retry loading hides stale errors. Edit Book offers retry when its edition list fails to load. | Use real provider records with incomplete and conflicting metadata. Confirm unknown details remain unknown and retries recover after an endpoint failure. |
| Recover an existing file that did not match | Library > Unmapped Files and Manual Import provide review and retry paths. Unmapped Files reports a failed initial load with an accessible alert and direct retry. Manual Import author/book pickers explain empty and no-match results; edition and quality pickers retain Cancel and offer Retry on failure, and empty quality results disable submission. Failed loads do not expose stale choices. Routine matching, Manual Import preview, and eligible download imports query the configured catalog after local and V5 matching miss: ISBN, ASIN, Goodreads/provider IDs, author/title, title-only, a single-file filename when earlier searches find no candidates, then author fallback. Conflicting identifier results and ambiguous search results stop broader fallback. Suggestions require the exact requested media type and one stable author/work identity; author-scoped searches remain within that provider identity, and book-scoped searches do not widen. Successful no-match searches are persisted so unchanged files do not repeat the same remote search on every scan; changed files and failed provider searches remain eligible for retry, and linking a file clears the marker. Results use the existing review and pending-import workflow rather than attaching a file directly. Row actions have explicit accessible names, and ebook/audiobook symbols include text alternatives. | Test author/book/edition/quality picker empty states, retry and stale-choice safety; then test Unmapped Files initial-load failure and retry, exact and conflicting ISBN/ASIN/provider IDs, wrong format, duplicate titles, ambiguous results, failed providers, and cancellation with representative files. |
| Understand why an imported file matched | Book Details > **Matching** shows the persisted route, mode, and supporting, conflicting, and neutral evidence for each file. Manual choices remain review history and are not fed back into automatic scoring. | Confirm evidence is readable for manual and automatic matches, remains attached through moves/conversions, and is absent or clearly empty when no provenance was recorded. |
| Avoid overwriting files during import | Automatic and Manual Import check the final destination before writing. Manual Import previews destination conflicts; forcing an import still cannot overwrite an untracked file or a file owned by another edition. On converted BookshelfNG installations, both preview and final import also reject a candidate whose size matches an existing file of a compatible format, even when it is assigned to another edition. Fresh ChaptarrNG libraries retain their existing multi-edition behavior. | Confirm an occupied untracked file and a file owned by another edition are never overwritten, including forced imports, multi-file imports, cancellation, and destination races. Check same-size compatible files in both preview and final import on migrated settings, and verify fresh-library behavior is unchanged. |
| Monitor titles and search series packs | Author and book editors set per-format monitoring and future-book policies. Wanted and book actions expose searches; paged Books views fetch the full filtered ID list for Select All and mass search, keep the confirmation open while resolving IDs and queueing the search, show errors with retry actions, and cancel/reopen confirmation if the filter or format changes. Search retries reuse the resolved IDs. Bulk Updates and RSS Sync show queue failures with inline retry actions; update retries repeat the same selected records and format. Individual author/book refresh and search actions report queue failures through the standard error notification, and late queue responses are ignored after leaving the list. Series rows provide pack search with per-file Manual Import context. | With a configured indexer, confirm monitored and unmonitored books behave correctly, exercise select-all, mass-search, update, and RSS Sync failure/retry/cancel/filter-change paths on a large library, and verify row-level queue errors are visible and bulk errors do not produce duplicate notifications. Also verify pack results can be assigned to the right volumes. |
| Download an ebook from a direct source | Add New and the built-in Direct Download indexer/client handle configured sources, API-key downloads, staged transfer state, and the optional browser fallback. Catalog searches include EPUB, KEPUB, MOBI, AZW3, and PDF; mirror sources can return other importer-supported ebook types. | Use a real source and client to check format coverage, ordering, credentials, interrupted transfers, redirects, restart recovery, and final import on each supported browser architecture. |
| Download a managed file from the library | Book Details > Files provides a download action for the selected book file; the server limits downloads to configured roots. File details and the quality-profile choices in the file editor offer Retry after a failed load; quality changes stay disabled until fresh choices load. | Download ebook and audiobook files through the browser, then verify missing files and paths outside configured roots fail safely; test file-detail and quality-choice retries. |
| Reuse a torrent already in the download client | Interactive search can adopt a matching torrent by hash, while automatic searches avoid starting a duplicate. The existing client item remains tracked for seeding. | Check wrong categories, completed and active seeds, client restarts, stale matches, and final import against supported clients. |
| Track imports and retry or cancel preparation | Activity and pending-author import status expose progress and terminal states; retry and cancellation preserve requests shared by multiple users. | Exercise successful fulfillment, partial failure, retry, shared requests, cancellation races, availability/history updates, and restart recovery with the unchanged SeerrNG client. |
| Merge and preserve audiobook chapters | Quality profiles can opt into multipart M4B merging; conversion preserves tagged track order and chapter metadata, reports progress, and has a safe fallback. Settings > Conversion offers Retry after a failed settings load. Core fixtures cover startup recovery from a persisted conversion job with a valid retained artifact. The pinned real-tool integration test also interrupts the actual FFmpeg child process, restarts a worker over the same SQLite job store, and verifies retry, artifact promotion, and importability with synthetic media. | Verify representative M4B and MP3 media in Move and Copy modes, including chapter edits, settings-load retry, conversion failure recovery, cancellation, and an interrupted conversion resumed through an installed-service restart. Synthetic media does not close the representative-media check. |
| Import archives and keep separately released parts together | The normal import path can extract supported ZIP, TGZ, and TAR.GZ downloads when no media file is present at the archive root. Numbered audiobook parts import side by side; Media Management > Naming exposes `{ReleasePart}` and `{ReleaseParts}` tokens, while conversion can merge eligible parts. | Check archive limits and nested layouts, separate releases with the same title, naming-token output, collisions, chapter offsets, source retention, and failure recovery with real files. |
| Move files or change format-specific storage safely | Author and bulk editors provide previewable format-aware moves, collision checks, root containment, and registered-sidecar handling. Unknown files stay outside the move. | Review preview completeness, cross-device moves, collisions, partial-failure rollback, external files, and a large mixed-format library. |
| Automate additions from lists and notifications | Settings > Import Lists supports Goodreads and Hardcover sources, preview, exclusions, and durable sync history; notification settings include list and import events. | Verify existing Goodreads credentials and Hardcover list responses, private/owned-list behavior, notification delivery, rollback, and secret redaction. |
| Set recurring library searches and list refresh cadence | Settings > Indexers > Options controls optional scheduled searches for missing monitored books; Goodreads bookshelf lists expose a configurable refresh interval. System health identifies a MyAnonamouse account state that blocks grabs. | Confirm off-by-default behavior, minimum intervals, scheduled results, Goodreads rate limits, and actionable indexer health after restart. |
| Configure providers and media-library services | Quickstart offers MyAnonamouse and Hardcover setup; Settings > Indexers and Metadata hold the saved provider choices. Calibre-backed roots and Calibre Content Server are separate workflows. AudioBookShelf maps each ChaptarrNG root and format to a library, with optional metadata/cover sidecars and push controls. See [external library integrations](EXTERNAL_LIBRARY_INTEGRATIONS.md). | Use existing saved connections and fresh setup to verify test failures are actionable, ebook/audiobook destinations stay distinct, and each provider keeps its data-ownership direction. |
| Deliver imports to reader apps | BookLore sends imported files to BookDrop; BookOrbit sends them to Book Dock through a separately configured connection. The [integration guide](BOOKLORE_INTEGRATION.md) describes authentication, supported actions, partial-batch errors, and why BookLore settings are not rewritten as BookOrbit credentials. | Verify upload, file retention, partial rejection, retry, and migrated settings against real BookLore and BookOrbit accounts; do not treat API-level checks as full book-processing acceptance. |
| Request and monitor books through SeerrNG | The restricted SeerrNG API exposes capabilities, format-scoped libraries, edition/narrator data, per-format file counts and disk sizes, and pending-import actions without granting global administration. | Synthetic migrated-record reads, format-specific availability statistics, and request create/poll/cancel pass. Still verify successful acquisition, retries, availability/history updates, reverse proxies, and unchanged production settings with live services. |
| Restore a portable or encrypted backup | System > Backup handles scheduled archives, encrypted portable archives, and recognized BookshelfNG restores; unsupported or unsafe inputs fail closed. | Restore a representative backup and profiles, check wrong passphrases and tampering, exercise rollback, and confirm secrets stay out of logs. |
| Diagnose and operate the installation | Startup logs include build/runtime and HTTP binding details and report whether the packaged UI entry point exists. System > Status, Tasks, Events, and Log Files expose operational state; MQTT is configured in Settings > Connect, Prometheus uses a separate listener enabled through container settings, and aggregate diagnostics require documented opt-in environment variables. | Check startup diagnostics, installed-service permissions, restart and failure recovery, metrics/events, diagnostics opt-in, and log redaction. |
| Work with large libraries and long-running operations | Library views page or virtualize results; background scans expose per-folder progress and supported bulk work can be cancelled. The MQTT Gateway is configured in Settings > Connect; Prometheus uses a separate listener enabled through container environment settings. | Check completeness and ordering on a large library, progress updates, cancellation consistency, bounded metric labels, MQTT discovery, and restart recovery. |
| Use the interface in a preferred language and on mobile | Settings > UI selects the language; untranslated strings fall back to English. Layouts support narrow screens and keyboard focus is visible. | Review locale fallback and right-to-left layout, then exercise common add, search, and file flows on a phone and with keyboard-only navigation. |

The detailed database migration, installer and release-channel checks remain in
the [retirement status](BOOKSHELFNG_RETIREMENT.md). They are not considered
accepted by this user-workflow map.

## Known review gaps

Completed-download audiobook conversions persist their request and recover at
startup. Each retry now uses an isolated output path; only a successfully
validated artifact with a matching version 2 manifest and output SHA-256
fingerprint is eligible for import. Tool-reported failures and output changed
after validation are excluded from reuse. The pinned real-tool test now
interrupts FFmpeg during conversion, verifies that its process exits and no
artifact is promoted, then starts a new worker over the same SQLite job store
and verifies retry, promotion, and importability. This covers graceful worker
restart recovery with synthetic media; abrupt whole-application crash recovery
and representative operator media remain acceptance work.

The source map includes substantial user-facing coverage, but full live parity
acceptance is still open. A disposable source build was exercised on
2026-10-20 with the checked-in BookshelfNG schema-158 fixture. SeerrNG's
`ReadarrAPI` read the migrated audiobook and ebook records; the restricted key
could read capabilities and both libraries but could not access host
configuration or unrelated commands; the global key retained that access. A
second run created, polled, and cancelled a synthetic pending author request
through the adapter. A separate HTTP regression probe for the bare numeric
Hardcover ID fix resolved `2459845` through both audiobook and ebook facades and
returned the canonical `hc:2459845` identity.

A later source-built Compose run on 2026-10-23 used ChaptarrNG checkout
`00db3253348fbfc9bd49453b923789c25b4a4763` (application-code snapshot
`a2d0af9fc31a94369bf091189b10b63f8ab1dc7e`) and SeerrNG
`bc580332ab49a7efa87e3d78b680a2a0cf1abbd0`. After normal startup converted a
populated synthetic schema-158 database, SeerrNG's `ReadarrAPI` read both
formats, observed one book file per format, and read an ebook history row that
remained absent from audiobook history. The restricted-key boundary and
pending-import create, poll, and cancel lifecycle also passed. This is scoped
synthetic evidence; full history and availability parity, live provider
fulfillment, and real-data acceptance remain open. See
[the SeerrNG integration record](SEERRNG_INTEGRATION.md).

Historical local validation on 2026-10-20 used a preview based on `ac85470a`.
The Core suite passed 4,515 tests with 15 skipped. The frontend CJS suite passed
86/86, typecheck and translation checks passed, focused ESLint passed, and the
production frontend build completed with 22 CSS-order warnings. That preview's
new-author setting behavior predates the final disabled-by-default contract.

An additional source-build UI smoke used a fresh temporary data directory on
2026-10-20. In Settings > Media Management, the new-author folder switch was
visible with advanced settings off and started checked. Turning it off enabled
Save Changes; after saving and reloading it stayed off. Turning it back on,
saving, and reloading kept it on. The Console app built with zero warnings or
errors using SDK 10.0.401, and the production UI bundle compiled with the same
22 CSS-order warnings noted above. The temporary instance was bound to
loopback, used temporary local Forms credentials, and was removed after the
check. No real library or provider was configured; author-root selection and
preservation remain covered by the Core fixtures above.

A later local validation on `codex/bookshelf-parity-completion-20261021`, based
on `e3759f7a`, passed the full Core suite (4,515 passed, 15 skipped), frontend
CJS (87/87), typecheck, translation-key validation, focused ESLint, and a
production frontend build with 22 CSS-order warnings. A fresh source-built
instance reached the first-run account gate; browser save/reload of the toggle
and author creation remain unverified because no credentials were created and
authentication was not weakened.

The author-path implementation commit `83f66c36` passed focused author and
importer coverage (11/11). The configuration-persistence fixture passes 1/1
and verifies the production default is off, the choice persists, and the Media
Management API resource reflects it. Current source also pages through
Goodreads series results, de-duplicates work IDs across pages, and preserves
the configured import limit; focused pagination coverage passes 5/5. Focused
format-profile refresh, Calibre language, Calibre proxy/series, Hardcover
metadata/search, book-file move planning, and author-move fixtures pass 5/5,
4/4, 6/6, 45/45, 15/15, and 3/3 respectively. The current candidate's full
cross-platform run is tracked in [ChaptarrNG
Actions](https://github.com/snapetech-org/chaptarrng/actions). No release or
migration bridge is published.

A production UI was checked in Chromium 154 against the disposable migrated
fixture before the latest profile-reset, missing-default, and failure-feedback
changes; that browser run predates this setting. In Books,
**Both** showed separate audiobook and ebook cards.
Opening **Edit Author** from either record selected that format's settings in
Posters, Table, and Overview (all six cases passed). In Bookshelf, **Both**
showed separate format links under their shared work; opening the ebook link
loaded its book details, formats, history, and file row without file or history
errors. At a 390-pixel viewport, the Books page retained both records without
document-width overflow, and the browser reported no failed API requests. The
fixture initially had empty quality-profile items; I populated valid allowed
qualities through its local API before checking detail history. That was a
fixture correction, not a product-code change.

These checks cover synthetic integration and user-facing rendering, not every
workflow in the table. No live metadata provider or download client was
configured, so real acquisition and import acceptance remains open.

The production frontend bundle from source snapshot `8c9dc0c0` was also reviewed
in headless Chromium 154 at a 1440-pixel viewport against a disposable local
database. Settings > Media Management > Review missing author roots opened the
preview, showed the zero-author empty state, and exposed one **Close** footer
action; the browser reported no page errors. Eligible, skipped, and retry states
remain covered by component tests rather than a browser run with seeded author
records.

Earlier isolated frontend validation (application snapshot `031b118e`) passed
the full frontend CJS suite (97/97),
focused ESLint and Stylelint, translation checks (1,629 files), both frontend
TypeScript projects, and a production webpack build. The bundle completed with
the known 22 CSS-order warnings and a non-blocking Babel deprecation warning.
Component render tests cover related-format loading, failure, retry, and
success; missing-catalog-ID guidance; Unmapped Files load failure and retry; and
the Bookshelf grouping popover. An isolated Chromium review against disposable
app data loaded the current production UI bundle at a 1440-pixel desktop width.
Bookshelf **Both** showed the empty-library state; keyboard focus opened its
grouping popover, and the info control had a visible focus outline and 28px
target. Unmapped Files showed its empty success state. The backend binary in
this harness was built from `main` and predates the new localization keys, so
the popover displayed key names; the intended English copy was checked in the
current `en.json`. This run had no seeded books, did not exercise the related
format or Unmapped Files failure states in the browser, and does not cover live
provider/download behavior. Installed-build, mobile, and representative-library
acceptance remain open.

At the time of this earlier validation, ChaptarrNG `main` was at docs-only
`9588a35e`; application snapshot `031b118e` restored the startup guard for configured
authentication with no local account. Its Core fixture covers
disabling authentication for first-account setup, preserving the configured
mode when a user exists, and leaving already-disabled authentication untouched.
The fixture passed 3/3 in a disposable source archive using SDK 10.0.401; the
repository pins 10.0.402, so this was not an exact-pinned SDK run. A fresh
disposable browser run reached the authentication gate, showed the non-loopback
warning, and returned to the app after saving temporary Forms credentials.
The resulting sign-in and migrated-database behavior still need acceptance.

Current ChaptarrNG source is on [main](https://github.com/snapetech-org/chaptarrng/tree/main). Manual Import's author, book, edition,
and quality selectors now explain empty results, retry failures, hide stale
choices, and disable submission when no quality is available. The full
all 36 frontend CJS regression files passed on 2026-10-23: 126 passed,
zero skipped, and zero failed. Startup errors identify failed data sources and
retry setup requests, while browser storage failures explain the issue without
a misleading retry. Add New and interactive release searches expose retryable provider errors, narrator-discovery failures announce and retry, and organize, retag, and convert previews offer retry on failure. Manual Import, File Browser, book-file details and quality loading, and conversion settings also recover from failed loads. Activity lists, author history, Edit Book edition lookup, Calendar, backup lists, and Wanted pages retry load failures. Exact-head CI status for current `main` is tracked by the [Build ChaptarrNG workflow](https://github.com/snapetech-org/chaptarrng/actions/workflows/build.yml). These checks do not close installed-build or operator-data
acceptance.

The combined shelf is a presentation over ChaptarrNG's separate audiobook and
ebook records. It does not turn them into one database record or combine their
monitoring and file state. Cross-format work links rely on canonical work IDs;
records without shared work identity remain ungrouped.

The two production paths in the BookshelfNG import-matching inventory have now
been reviewed by behavior. ChaptarrNG's `FileMatchingService` performs the local
identity and tag-based candidate pass, then consults
`UnmappedCatalogSuggestionHelper` for the ordered catalog search: ISBN, ASIN,
Goodreads ID, author plus title, title, a single-file filename title, then
author. It preserves the legacy 85% remote-recheck threshold and only replaces
a local candidate when the remote result scores higher. Candidate ties keep the
original source order. Routine import and review suggestions use this fallback;
manual import preview retains its exact-query boundary. ChaptarrNG also keeps
its stricter media-type, identity, and ambiguity checks. This resolves the
`CandidateService` and `IdentificationService` semantic-review entries at the
source level; it does not claim that a path-name match alone proves equivalence.
This review follows BookshelfNG's [`CandidateService`](https://github.com/snapetech-org/bookshelfng/blob/main/src/NzbDrone.Core/MediaFiles/BookImport/Identification/CandidateService.cs), [`IdentificationService`](https://github.com/snapetech-org/bookshelfng/blob/main/src/NzbDrone.Core/MediaFiles/BookImport/Identification/IdentificationService.cs), and [`AudioTag`](https://github.com/snapetech-org/bookshelfng/blob/main/src/NzbDrone.Core/MediaFiles/AudioTag.cs) source.

For audiobook scoring, the compatibility projection now preserves BookshelfNG's
null-only `Album ?? AlbumSort` and `Title ?? TitleSort` selection, including an
explicit empty album, and uses the selected book title when nonblank or the
track title otherwise. The projection survives when FFprobe supplies the final
tag result, so container-tag normalization cannot erase the distinction. The
internal projection is retained for matching and kept out of public tag output.

## Import and media source-path crosswalk

BookshelfNG runtime paths in the historical consolidation inventory were
reviewed by behavior against BookshelfNG source, then mapped to the current
ChaptarrNG implementation. The filename inventory is a review aid, not a count
of missing features.

| BookshelfNG source path | ChaptarrNG behavior path | Review result |
| --- | --- | --- |
| `AudioTag.cs` | `TagLibSharpExtractor`, `TagExtractionService`, `BookshelfAudioTagFields` | Null-only album/title sort fallbacks are preserved through extraction and matching; the private projection is removed from public tag output. |
| `AddAuthorService.cs` | `AuthorController.AddAuthor`, `AuthorLibraryService.AddAuthorAsync`, and `ImportListSyncService.QueueAuthorForImportList` | The v1 route hydrates authoritative provider data, applies root/profile/monitor settings, disambiguates generated paths, and deduplicates provider identity. Import-list adds preserve their settings in ChaptarrNG's durable pending-import flow; format settings remain independent. |
| `AudiobookM4bMergeService.cs` | `M4bConversionService` and the import conversion workflow | Opt-in multi-track merge, chapter ordering/metadata, Move/Copy handling, and safe fallback are implemented; representative media and restart/resume acceptance remain open. |
| `CandidateService.cs`, `IdentificationService.cs` | `FileMatchingService`, `UnmappedCatalogSuggestionHelper`, `BookshelfMatchCompatibilityScorer` | Legacy candidate scoring and ordered catalog fallback are represented in the migrated-install path; stricter identity and ambiguity checks remain active. |
| `Distance.cs`, `DistanceCalculator.cs` | `BookshelfMatchCompatibilityScorer` and shared string-distance helpers | The legacy weighted scorer and its normalized edit-distance evidence are implemented for migrated installations. |
| `ImportDecisionMaker.cs` | Automatic: `DownloadedBooksImportService` and `ImportOrchestratorV2`; manual: `SimpleImportDecisionMaker`; finalization: `ImportApprovedBooks` | These target services split the old combined decision pipeline across automatic matching, manual selection, and import finalization. |
| `AlbumUpgradeSpecification.cs` | `UpgradableSpecification`, quality/cutoff specifications, and import replacement checks | The reviewed BookshelfNG specification accepts all candidates; its former rejection logic is inactive. Active upgrade policy is covered by the remaining specifications and final replacement checks. |
| `CloseAlbumMatchSpecification.cs` | `FileMatchingService` and `BookshelfMatchCompatibilityScorer` | Migrated candidates are scored against the saved BookshelfNG minimum (default 70) before acceptance; ChaptarrNG still applies its independent identity and ambiguity checks. |
| `AuthorPathInRootFolderSpecification.cs` | `StagingQueueFileDispositionHelper`, `ImportOrchestratorV2`, and `ImportApprovedBooks` destination/root handling | Root-folder media-type scope and destination containment are enforced at the target staging and import boundaries. |
| `FreeSpaceSpecification.cs` | `ImportApprovedBooks.GetImportFreeSpaceRejectionReason` | Configured minimum free space and the skip setting are checked before existing files are staged. |
| `SameFileSpecification.cs` | `BookshelfSameFileCompatibility`, called by `SimpleImportDecisionMaker` and `ImportApprovedBooks` | Migrated installations reject same-size compatible files in preview and final import, including files assigned to another edition. Fresh installs and generated conversions are not changed. |
| `UpgradeSpecification.cs` | `UpgradableSpecification`, `GetDuplicateImportRejectionReason`, and quality/cutoff specifications | Quality and custom-format upgrade policy is checked before a replacement can displace existing files. |
| `RetryUnmappedFilesCommand.cs` | `RetryUnmappedFilesCommand` and `RetryUnmappedMatchCommandHandler` | The legacy API command name remains available and dispatches through ChaptarrNG's all-format unmapped match and import workflow. |
| `ParsedTrackInfo.cs` | `src/NzbDrone.Core/Parser/Model/ParsedTrackInfo.cs` | The corresponding parser model remains present at the target path. |
| `OidcBackchannelHandler.cs` | `OidcBackchannelHandlerFactory`, `OidcBackchannelTargetPolicyHandler`, `OidcResponseLimitHandler`, and `ManagedSocketsHttpHandlerFactory` | Redirects remain disabled, private and reserved issuer destinations are blocked by default, and responses are capped at 1 MiB. ChaptarrNG also uses its configured proxy and certificate policy; its connection cap and five-minute pool lifetime now match BookshelfNG. |

This crosswalk closes the listed path-review entries at source level. It does
not establish acceptance against representative converted libraries, live
providers, installed channels, or rollback on operator data. The BookshelfNG
same-file rule follows its compatible-format and file-size predicate
([`SameFileSpecification.cs`](https://github.com/snapetech-org/bookshelfng/blob/685b31c93d7146258c02446b01cb34c6f5d962b5/src/NzbDrone.Core/MediaFiles/BookImport/Specifications/SameFileSpecification.cs)).

These are source-level parity findings. Migration, installed update channels,
real-provider fulfillment, representative-library acceptance, and release
readiness remain tracked separately in [the retirement status](BOOKSHELFNG_RETIREMENT.md).
