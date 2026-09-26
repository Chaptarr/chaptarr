# Chaptarr API Identity and Lifecycle Contract

This document is the public-facing contract for clients building against Chaptarr and the Chaptarr Metadata Server. It describes identity semantics that the generated OpenAPI schema cannot express by itself.

## Series Membership and Labels

Series are shared across authors and scoped by media type. Goodreads-backed `SeriesBookLink` memberships supply the book resource's `seriesTitle`: all memberships, each with its own position, joined by semicolons. The book-level summary pair is not a fallback label when memberships are missing. Naming and single-series metadata formats select one membership and keep its name/position together. Positions, including decimals, ranges and lists, are presentation data, never book identity or a unique slot key.

## Identity Model

Provider IDs are durable identity. Local database row IDs are not.

Use provider IDs such as `hc:149559`, `gr:173491`, `az:B001IOF7SC`, `ol:OL...`, and `gb:...` for matching, crosswalks, caches, sync state, and external integrations. Chaptarr local row IDs are only handles inside one user's database. A local ID can change if metadata is repaired, merged, split, or reimported.

### Books Are Provider Pockets

A Chaptarr book row represents a server-defined pocket of provider identities, not a single upstream record. One row can carry multiple provider IDs and aliases because the metadata server may absorb singleton records, translations, duplicate works, or editions into one canonical pocket.

Any ID in the pocket may resolve to the row. Public resources should expose the provider identity set rather than forcing clients to use whichever ID happens to be primary today.

Example:

```json
{
  "providerId": "hc:2514970",
  "providerIdsAll": ["hc:2514970", "gr:246950336", "gr:94932951"]
}
```

Both Goodreads IDs above are valid lookup keys for the same server pocket. Neither should be treated as a local row ID.

### Media-Scoped Rows

Chaptarr can have separate local rows for audiobook and eBook instances of the same provider pocket. A provider ID can therefore match up to one audiobook row and one eBook row in a user's library.

When an API accepts a provider ID and a media type, the media type scopes the match. When a mutation receives a provider ID that matches more than one eligible local row and cannot be safely scoped, Chaptarr returns an ambiguity error instead of guessing.

### Interactive Author Searches

`GET /api/v1/release?authorId={localAuthorId}&mediaType=audiobook` restricts the
search targets to monitored audiobook Book rows; `mediaType=ebook` does the same for
ebooks. Scope is applied before per-book indexer dispatch, not only to the returned
results. Each target retains its own media-scoped sibling catalog and per-book search timestamp.
An omitted media type or `all` retains the existing all-enabled-sides behavior; an
unknown value returns HTTP 400 before searching. `bookId` searches continue to use
the explicitly selected row's own media type.

The author detail Search tab supplies the selected media type and starts only after
an explicit Search action, with confirmation above five eligible rows on that side.
The toolbar Search Monitored command still searches all enabled sides sequentially.
Interactive searches do not automatically grab. Author searches without `searchId` retain
the existing single flat `ReleaseSearchResponse`, returned after a successful batch completes. A request to
`/api/v1/release` without `bookId`, `authorId`, or `searchId` retains the existing
recent RSS `ReleaseResource[]` response.

The author Search tab adds a client-generated UUID `searchId` to opt into grouped
results. This requires an explicit `audiobook` or `ebook` media type; missing scope,
`all`, or an invalid UUID returns HTTP 400 before searching. The response shape
is shown below; `authorId` and `bookId` are integer local request handles:

```json
{
  "searchId": "<request UUID>",
  "authorId": 123,
  "mediaType": "ebook",
  "bookResults": [
    {
      "bookId": 456,
      "title": "<displayed edition title>",
      "mediaType": "ebook",
      "status": "pending | complete | failed",
      "releases": [],
      "hiddenReleases": []
    }
  ],
  "isComplete": true,
  "isCancelled": false
}
```

`AuthorSearchResults` messages on the existing authenticated SignalR connection
carry this envelope: the initial eligible roster, then one completed book group at
a time, then completion. The final HTTP response contains all groups. A book with
no returned releases still has a group. Indexer failures can currently return an
empty list; an empty group is not proof that no releases exist.

Each group is ranked independently with the existing prioritizer. `rank` and
`isPreferredChoice` describe the backend ordering and do not change when the UI
sorts rows. Preferred means the top approved, downloadable interactive result;
automatic searches still apply their own cutoff and upgrade eligibility checks. A repeated release remains in each searched book's group. New grouped
responses omit download and magnet URL values; existing flat responses retain them.

Grouped release resources carry `searchId` and `searchedBookId`. A grab must echo
both alongside the existing `guid`, `indexerId`, `authorId`, and mapped `bookId`.
The existing decision cache is keyed by this request/book context before each
result is published. Missing cache entries return 404; a cached target inconsistent
with the requested book returns 409 without queuing a download. Local book handles
here identify request targets, not cross-provider identity.

The UI merges incremental and final results while preserving grab state. Search
remains mounted but hidden when another tab on the same author page is selected;
results and request ownership are separate from single-book search modals.

`DELETE /api/v1/release?searchId={UUID}` requests an explicit stop for an active
grouped search. Its `200` response is `{ "isStopping": true }` when the request
reaches an active search, or `{ "isStopping": false }` when that search is no longer
active. Missing or malformed IDs return `400`. Repeated stops are idempotent. A
second grouped GET with the same active UUID returns `409`; UUID spelling/casing
does not create a second identity. Stop uses the existing API authentication.

The Stop button becomes available once the server has published the book roster.
It keeps the original GET open and shows “Stopping after the current book” while
waiting. The current book finishes and publishes its results; no remaining books
are searched. The final GET response and terminal SignalR message carry
`isComplete: true` and `isCancelled: true` when remaining books were stopped. If
the active book was already the last one, the search completes normally. Completed
results and grab state remain available. A failed Stop request is shown as an error and can be retried; it must
not be displayed as a completed stop.

Leaving the author page or changing media scope requests the same stop before
aborting the browser request. RequestAborted remains a second cancellation signal
for grouped and legacy flat requests when the server observes a disconnect. No
caller cancellation enters the shared indexer HTTP failure path. Active stop
handles exist only for the lifetime of the grouped request and are removed on
success, cancellation, or error. Interrupted results remain partial; neither
reconnection nor a missed update restarts tracker searches automatically.

### Ambiguous Provider Identity

Chaptarr-native mutations return HTTP `409` when a provider ID maps to multiple candidate rows and the request does not specify enough information to choose one.

The response body uses `ProviderAmbiguityResource` and includes the candidate rows. Clients should show the choices to the user or retry with a narrower media type/provider identity.

Legacy Readarr/Seerr compatibility paths are documented exceptions. Those routes keep their compatibility picks so existing clients do not break, but new Chaptarr-native clients should use the explicit provider-ID fields and ambiguity contract.

### Amazon and Audible ASINs

`az:` IDs are intentional for Amazon/Audible only. Sometimes the metadata server cannot yet pocket an Amazon or Audible singleton into a stable work identity, so Chaptarr may use the ASIN/Audible ASIN as the best available work surrogate.

An `az:` ID may later be superseded as the primary provider ID if the metadata server pockets the singleton properly. The old `az:` remains resolvable through the provider identity set; clients should not assume primary IDs are immutable.

### Narrators

Narrators are not durable public identities in Chaptarr today. Upstream providers did not give us stable narrator IDs, so narrator values are display and matching strings derived from edition metadata.

Use narrator strings for UI display, matching evidence, and edition preference logic. Do not build external caches or API integrations that depend on narrator local row IDs or narrator provider IDs.

### Unmapped Import Units

For an unmapped `BookFileResource`, `importUnitKey` is an opaque, transient control token that groups files for the Unmapped page and exact manual-import preview. `importUnitRoot` is the server-selected filesystem root for that preview. Neither field is provider identity, a durable cache key, or proof that the files already belong to a matched Book or Edition.

The values can change when file inventory or persisted tag evidence changes. Clients may group rows that share the key and submit their local `bookFileIds` together, but the server recomputes current membership before preview. Missing or stale membership fails closed; clients must not reconstruct these units from parent folders.

### BookFile Match Provenance

`BookFileResource.matchProvenance` is an optional, schema-versioned explanation of the successful decision that linked a file. It is not a percentage and must not be used as a release-preference score. The four signal arrays are `supportingSignals`, `conflictingSignals`, `neutralSignals`, and `excludedSignals`; substantive signals state whether they concern book or edition identity and record the source/logical field used by that decision, while excluded signals use metadata scope and never copy the ignored value.

Schema version 2 adds `evidenceValues`. Each item contains one unique raw embedded-tag, folder, or filename value, its technical source-field names, and decision-time `ranges` into the stored `value`. Range offsets use zero-based, end-exclusive UTF-16 indexes, matching JavaScript string indexing. Every range records its `disposition` (`supporting`, `conflicting`, or `neutral`), semantic evidence `type`, Book-versus-Edition `scope`, and human detail. A client may highlight these server-authored ranges but must not recompute them from current metadata. Overlapping ranges are legal; clients should give conflicting evidence visual precedence, then supporting, then neutral. Unannotated text is ordinary context, not an implied disposition.

Repeated extractor aliases with the same raw value are represented once; their field names remain available only as technical details. `route` says whether the decision used embedded tags, folder/file names, an exact filename identifier, or manual selection, and `mode` records the strictness active at decision time. These fields describe historical evidence only and must not feed release scoring, search, or later rematching.

The payload's `authorProviderIds`, `bookProviderIds`, and `editionProviderIds` are the provider-owned identities of the final linked destination. They are stamped after import resolves the actual destination. No local database row ID is embedded as semantic match identity. Current import paths preserve the matcher/user-selected Edition unchanged; historical records created before that invariant may contain an Edition-scoped `edition_retarget` conflict, which clients should continue to render as recorded evidence.

Files linked before provenance version 1 can return `matchProvenance: null`. Version-1 records remain valid but have no `evidenceValues`; clients must render their semantic signal buckets without inventing token highlights. Clients must show null history as unavailable rather than infer evidence from current tags or the current catalog row.

## Organize Preview

`GET /api/v1/rename` uses local database handles because it previews mutations against files in this Chaptarr instance:

- `authorId` is the local Author handle.
- `bookId` is the local Book handle accepted by the optional book-scoped query.
- `RenameBookResource.editionId` is the local Edition handle used to group files that share one physical edition folder.
- `RenameBookResource.bookFileId` and inherited `id` are both the local BookFile handle used for exact row selection.

`moveToCanonicalAuthorFolder` defaults to `false`. It is supported only for author-scoped previews where `bookId` is omitted. Combining `bookId` with `moveToCanonicalAuthorFolder=true` returns HTTP `400`; clients must not treat the flag as silently ignored.

Execution still accepts the flag with an exact list of selected BookFile handles. This allows an author-scoped preview to move only the rows the user selected, without expanding that selection to every file in the Book or Edition. These local IDs are mutation handles only and must not be cached or treated as provider identity.

The generic `/api/v1/command` OpenAPI schema does not enumerate command-specific fields. A canonical execution request has this shape:

```json
{
  "name": "RenameFiles",
  "authorId": 344,
  "files": [123, 456],
  "moveToCanonicalAuthorFolder": true
}
```

## Search and Lookup Behavior

Search results include provider identity fields and, when applicable, an `existingLocalId`. Treat `existingLocalId` as a local database handle only. Treat `providerId` and provider identity arrays as durable semantic identity.

Do not infer that a missing local row means a provider item is unknown globally. It may be queued, pending, pruned by local profiles, or available only under another provider alias.

## Media-Scoped Author Import Settings

Root-folder POST and PUT both require quality and metadata profiles for every format supported by the folder type: both for Mixed, only the corresponding side for Audiobook or Ebook. Existing incomplete Mixed roots are not automatically narrowed or assigned profiles; saving them returns field-specific validation errors until corrected. Creation-only monitoring defaults are unchanged.

For book requests, an explicitly supplied root must exist, support the requested format, and have both required profiles. An unusable explicit destination returns a validation error; it is never silently replaced with another root. Omitting the root retains the existing default-selection behavior, restricted to usable roots. These checks do not reassign existing authors.

Chaptarr-native author-import requests carry monitoring and tags independently for the audiobook and eBook sides. A supplied tag array replaces the tags for that requested side: `null` or an omitted field means "not supplied," while `[]` explicitly means "no tags." The compatibility `tags` field remains a shared fallback for older clients, but new clients should send the media-scoped fields.

Root-folder defaults are creation defaults, not continuing policy. Chaptarr fills an unset media side when that side is first created or hydrated and never overwrites an existing value, including an explicit empty tag set. An audiobook-only request does not initialize or mutate eBook settings, and the reverse is also true.

If an author is waiting in the pending-import queue, Chaptarr persists the two tag sets separately. Concurrent requests union tags within the same media side so a later request cannot erase an earlier caller's pending intent; tags can be removed normally after the author exists. Once the author is materialized, the legacy combined `Author.tags` projection is the union of its audiobook and eBook tag sets.

## Metadata Server V5 Lifecycle

The metadata server uses V5 endpoints such as:

- `GET /api/v5/author?id={providerId}`
- `POST /api/v5/authors/diff`
- `GET /api/v5/work/{providerId}`
- `GET /api/v5/edition/{providerId}`
- `GET /api/v5/book/{providerId}` as an edition-scoped compatibility alias
- `GET|POST /api/v5/match`

`/api/v5/match` can return an author-only match item when connected author/title proof identifies one provider Author but cannot distinguish among multiple works owned by that Author. In that case `author` and `author_id` are present and work/edition identity fields are omitted. Clients may use the provider Author identity, but must not infer or persist a specific work or edition from that response. A complete same-value title/series-position span may first narrow the candidate set. Otherwise, a Work identity is returned only when exact canonical work-title evidence uniquely identifies one provider Work; exact proof of a generic Edition title cannot claim a more-specific Work. Ranking is then applied only within that identity boundary. If independently successful connected proofs still name different provider Authors, the endpoint returns no match instead of ranking one Author over another.

### HTTP 202 Means Not Ready

A V5 lookup may return:

```http
HTTP/1.1 202 Accepted
Retry-After: 60
X-Author-Ready: 0
Content-Type: application/json

null
```

or, for work/edition paths:

```http
HTTP/1.1 202 Accepted
Retry-After: 60
X-Work-Ready: 0

null
```

This means the item is queued or not ready. It is not a final not-found response, and it is not a full resource.

For author imports, Chaptarr keeps the request active without an arbitrary attempt ceiling. It uses its ordinary retry cadence (roughly one minute for the first three attempts, then roughly five minutes with jitter) until the metadata server returns a served payload or a typed declared-stop outcome. `PendingAuthorImportResource.maxAttempts` is `0` for this unbounded lifecycle; `attemptCount` remains an observational counter, not a failure threshold.

When a book add queues an author that is not available yet, `addOptions.searchForNewBook=true` remains attached to that exact media-specific request. `PendingAuthorImportResource.audiobookBooksToSearch` and `ebookBooksToSearch` expose the queued provider IDs; they are durable provider identities, not local Book row IDs. Once the authoritative author catalog imports, Chaptarr resolves those IDs within the requested media type and queues an exact Book search. If the imported catalog does not contain a requested provider ID, Chaptarr logs the missing target and does not broaden the search or synthesize a Book.

For Readarr/Seerr facade book requests, first setup of a media side inherits the selected root folder's initial book selection and new-book policy. Successful request completion enables the requested author media side and selects the requested book, even when the root's gate is off or its initial selection is `None`. `All` may initially select siblings but does not search them. Later requests preserve profiles, paths, new-book policy, sibling selections and the opposite media side; resuming the requested side also makes its previously selected siblings eligible for automatic acquisition. Requested books retain a manual-add preservation marker, not an edition pin. Search-on-add is optional, user-invoked and limited to exact requested targets. Queued requests retain these settings and provider identities through the existing pending-import lifecycle.

An explicit facade `PUT /book` with `monitored: true` applies this request contract even when the book is already selected. Lookup GETs and search-only commands never change saved monitoring settings. Clients that skip the update because a book is already monitored therefore do not request that its paused author side be resumed. In particular, Chaptarr does not infer an approved request from Seerr's lookup or change the meaning of the resource's `monitored` field to induce a client update.

Command duplicate handling is first-command-wins: a later manual request matching an already queued or running automatic command reuses it without upgrading its trigger. For newly queued commands, an explicitly supplied trigger is propagated to the executable command body. Omitting that argument preserves any trigger already set by the command itself. This does not change monitoring settings or broaden search targets.

All non-`200` responses are negative/transitional state and must never be stored as reusable metadata. HTTP `200` is cacheable only when the endpoint confirms a real positive result; semantic-empty bodies such as `null`, empty arrays, or empty search/match result containers are also `BYPASS`. The metadata-server origin sends `Cache-Control: no-store, no-cache, must-revalidate` plus CDN no-store directives for negative results. Chaptarr evicts historical non-`200` and endpoint-defined semantic-empty entries, while successful positive `200` responses retain normal cache reuse. A later positive `200` must never be hidden by an earlier empty, pending, or blocked response.

### Typed Author Stop Outcomes

The public author contract is `200` / `202` / narrow `404` — there is **no public `409`**. A typed stop carries a closed JSON `code` and `retryable: false`: clients stop automatic retries and show the declared reason. These outcomes are distinct from `202` pending.

The metadata server currently emits exactly one typed stop:

| HTTP | `code` | `reopenable` | Meaning |
|---|---|---:|---|
| 404 | `author_provider_record_missing` | true | The provider is proven to no longer have this author record. |

It is emitted only after durable provider-side proof (fresh live check, redirect, alias/crosswalk, and match-evidence reroute all exhausted) and only when the requested author has never had a served payload. If any payload has previously been served for the requested or resolved identity, the keep-serving contract wins: refresh, re-evaluation, or a newly observed terminal condition cannot replace yesterday's good `200` with a blank response.

For compatibility with earlier server revisions, Chaptarr additionally tolerates the legacy codes `author_provider_unsupported`, `author_identifier_not_author`, `author_no_primary_works` (as `404`) and `author_identity_ambiguous` (as `409`); an unknown code or a code/status mismatch is rejected loudly rather than treated as a stop.

Ambiguous-identity and provider-redirect repair states are deliberately **not** public stop outcomes. They present as `202` pending: the server owns the recovery path, publishes the proven seed, holds only the unproven attachment, and reopens the hold on real evidence (record reappears, redirect appears, alias proven, genuinely different match evidence, process-version bump). Polling bumps priority only — it never clears a hold.

Example:

```http
HTTP/1.1 404 Not Found
Cache-Control: no-store, no-cache, must-revalidate
CDN-Cache-Control: no-store
X-Author-Ready: 0
X-Author-Terminal-Code: author_provider_record_missing
Content-Type: application/json

{
  "code": "author_provider_record_missing",
  "providerId": "hc:123456",
  "message": "The provider no longer has this author record.",
  "retryable": false,
  "reopenable": true
}
```

### Author Diff

Use `POST /api/v5/authors/diff` for bulk author sync. Do not use stale `/api/v5/authors/changes` names.

ETags are opaque. Store and replay exactly what the server returns. Current author payload ETags use the provider-ID-aware form, for example `W/"v703-providerids-v2"`.

### Search Is Deprecated on SMS V5

`GET /api/v5/search` is deprecated. It returns an empty array with `X-Deprecated: true` and does not enqueue imports.

Clients that need provider discovery should search the upstream provider directly, then request the chosen author/work/edition provider ID through the correct V5 lookup path. Chaptarr's add/import paths handle pending queue state after a provider ID has been selected.

## Route Scope Rules

Goodreads work IDs and edition IDs live in separate namespaces and can collide numerically. Do not send a Goodreads edition ID to `/api/v5/work/`. Use:

- work IDs with `/api/v5/work/{providerId}`
- edition/book IDs with `/api/v5/edition/{providerId}` or the `/api/v5/book/{providerId}` compatibility alias

The split route contract prevents wrong-book cache poisoning and is deliberately stricter than a generic lookup endpoint.

## Author deletion scope

Both `DELETE /api/v1/author/{id}` and `DELETE /api/v1/author/editor` accept explicit deletion scope. The single endpoint takes `mediaType` and `applyToBothFormats` as query parameters; the bulk resource includes both fields and also accepts a `mediaType` query parameter. Existing `/audiobook/`, `/ebook/`, and provider-specific Readarr facade prefixes supply scope. Supplied route, query, and body media types must agree; conflicts return 400 before mutation.

| Request | Behavior |
| --- | --- |
| `mediaType=audiobook` or `ebook`, `applyToBothFormats=false` (default) | Remove that side's book/edition rows and author settings. Preserve the other side. |
| Scoped request with `deleteFiles=true` | Delete tracked files of that format, authorized replicas and exclusively owned extras; clean up empty folders using the existing setting. Shared/untracked contents remain. Removing the final side never widens this permission. |
| Unprefixed API with `applyToBothFormats=true` | Explicit whole-author permission, including when a query/body media scope is also supplied. With `deleteFiles=true`, the existing folder-and-contents contract applies. |
| Media-prefixed compatibility API with `applyToBothFormats=true` | 409 before any mutation. The prefix is a firm media boundary; use the unprefixed API for an explicit whole-author action. |
| No scope, `deleteFiles=true`, dual-format author | 409 identifying the ambiguous authors and folders. The whole batch refuses before changes. Supply a concrete scope or explicit both-format permission. |
| No scope, `deleteFiles=false` | Historical whole-author metadata removal; files remain. Re-adding does not automatically restore edition selections or monitoring choices. |

`addImportListExclusion=true` uses provider-owned author IDs with the same media scope. Exclusions are validated before physical work and persisted synchronously after successful physical deletion, before metadata teardown. A physical failure keeps author metadata; an exclusion persistence failure after successful physical deletion also retains the author and reports failure. Files already moved/deleted cannot be rolled back by a later I/O failure.

For Freida McFadden's *The Boyfriend* (`hc:1325795`), an ebook-scoped request removes the EPUB and ebook records while retaining the M4B and shared `metadata.json`, even when both files occupy one folder. Calibre libraries use the existing format-removal operation for scoped requests; absent library-local identity or a retained owner of the same format causes a preflight 409.

`readdAuthor=true` remains whole-author Purge & Re-add. It cannot combine with file deletion or an import-list exclusion; a caller with a query/body media scope must explicitly opt into both formats through the unprefixed API. A media-prefixed compatibility caller cannot purge and re-add the whole author. The GUI uses one shared single/bulk dialog with Audiobooks, eBooks, and Both formats in one selector, preserves failed requests for correction, and refreshes surviving author state after scoped success.

The same prefix boundary applies to book deletion. `DELETE /api/v1/book/{id}` rejects both-format permission or a book row of another media type on a prefixed route. `DELETE /api/v1/book/editor` checks the entire selection against the route/body scope before deleting its first book; a mixed selection returns 409. Unprefixed native requests retain their existing explicit selections and both-format option.

Deletion scopes catalog rows by `Book.MediaType` and physical media by quality-derived `BookFile.MediaType`. These are separate classifications, independent of edition reading format. A read-only review found no disagreement across 1,507 joined files; that observation is not a database constraint.


## Book-cover cache scope

`GET/PUT /api/v1/config/mediamanagement` exposes `coverDownloadScope`: `allBooks` (default) or `libraryBooks`. The latter retains cache artwork for a book row if either of its monitoring flags is true or it owns a file through an edition. Author-side monitoring and files on a separate sibling row do not establish eligibility. Switching to `libraryBooks` schedules only the book-cover cleanup through the existing command queue, without unrelated housekeeping or database compaction; author portraits and media-folder sidecars are unaffected.

`GET /api/v1/config/mediamanagement/covercleanup` provides a read-only preview of the cache cleanup for `libraryBooks`, regardless of the currently saved scope. It returns `{ "bytes": 0, "bookCount": 0 }` for no removable cache folders. Otherwise, `bytes` estimates logical file bytes from at most 500 randomly selected removable folders, and `bookCount` is the candidate folder count at enumeration time. The sample is repeatable for an unchanged folder population and includes orphaned canonical cache folders. It uses one batched library-ID lookup and the cleanup task's canonical book-folder selection; it does not save settings, delete images, or download covers. This is an approximate snapshot, not guaranteed allocated disk space reclaimed or a fixed accuracy guarantee. A three-second budget and request cancellation are checked between synchronous reads; an already-blocked filesystem or database call cannot be interrupted by that check. An expired budget or failed estimate returns an error rather than a partial total. Clients should keep confirmation usable while loading, and display the result as a rounded approximation. The Media Management confirmation offers an optional **Calculate savings** button when saving a change from `allBooks` to `libraryBooks`. Opening the confirmation or saving without calculating sends no preview request.

Book resources in `libraryBooks` mode expose excluded covers with `images[].url = null` and a non-null `temporaryUrl`. Lists must show a placeholder, without falling back to `remoteUrl`. A book details view may request a nonempty `temporaryUrl` through the existing privacy proxy; an empty value means no usable temporary image. These requests do not store image bytes in Chaptarr's cover cache. `remoteUrl` remains provider metadata. Temporary URLs are resource-only and are not persisted in edition metadata. Cached eligible covers retain their local URLs. Uncached saved-book covers in `allBooks`, and detail-only previews in `libraryBooks`, use `/MediaCoverProxy/book/{localBookId}/{filename}?v={imageVersion}`. The authenticated image route resolves the monitored edition with the same selector as cover downloads and performs no persistent image write or temporary URL registration. It remains valid across scope changes and process restarts while the book and monitored image exist. Browser caching is private for 24 hours; changing the source image URL changes the version token. Switching back to `allBooks` does not queue a library-wide download: ordinary author refreshes refill the cache.
