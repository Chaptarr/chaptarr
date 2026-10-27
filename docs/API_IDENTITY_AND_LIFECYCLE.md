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

Chaptarr can have separate local rows for audiobook and eBook instances of the same provider pocket, including multiple copies within one format (for example, different narrations). A provider ID can therefore match several local rows even when the media type is specified.

When an API accepts a provider ID and a media type, the media type scopes the match. When a mutation receives a provider ID that matches more than one eligible local row and cannot be safely scoped, Chaptarr returns an ambiguity error instead of guessing.

### Existing-Book Import and Local Edition Lookup

`POST /api/v1/book/import` for an existing book on a configured author side completes the request using the stored row. It preserves metadata and the selected/pinned edition, leaves the author's monitoring gates unchanged, and monitors the requested book. `authorMonitoring.monitor: none` controls the author's other books; it does not disable monitoring for the requested book. Native `POST /book` accepts an explicit book-level `monitored: false` (or the media-side equivalent) to add without monitoring; omitted book monitoring selects the requested book. Initial author monitoring and the requested book's selection are independent. An explicit `searchForMissing` starts one Manual single-book search. Import-list exclusions are removed only after request completion succeeds. Native requests resolving to several eligible copies still return HTTP 409 with candidates; clients must choose a local row for an exact-row operation.

`GET /api/v1/book/lookup` returns every matching local copy in the requested media type, with each row's local `id`, editions, narrator and monitoring information. Omitting native `mediaType` returns all matching audiobook and ebook copies. `hc:123` and `gr:123` name **works**; `hc:edition:123`, `hc-ed:123` and `gr-ed:123` name **editions**. Hardcover and Goodreads work IDs are never tested against edition IDs, and an explicit edition lookup never falls back to a same-numbered work or an edition available only in the other media enum. Explicit Goodreads edition lookup recognizes dedicated edition columns and legacy `ForeignEditionId` values, including media suffixes. Native `edition:123` retains the legacy Goodreads edition lookup; native `work:123` requires a provider prefix. Under `/readarr/{hc|gr}/{ebook|audiobook}`, `work:123` and `edition:123` use that route's provider dialect and media scope. Local-copy summaries retain separate audiobook/ebook arrays and compare work evidence only with work identity and edition evidence only with edition identity.

### Existing-Book Edition Policy Updates

Native and media-scoped Readarr-compatible book PUTs persist an explicitly supplied `anyEditionOk` value, including when the book is already monitored. `false` preserves the selected edition against automatic switching; clients must not send it for a generic request unless they intend that restriction. Omitting the field leaves the saved policy unchanged when no explicit edition change is requested. When the field is omitted, `PUT /book/{id}` pins a newly selected edition; body-route `PUT /book` does not infer a pin merely from a changed selection. An explicit `true` selects the requested edition now while retaining the flexible policy, and does not relink existing files as a strict-edition correction. Independent stored `ManualAdd` retention remains protected under its existing policy; `anyEditionOk` does not erase it.

The book, changed editions, provider aliases and configured format synchronization save in one transaction across these routes. Events are published after that transaction commits. An escaping notification failure is logged as a postcommit error and does not undo the committed save. A compatibility request to monitor a book shares the existing media-scoped monitoring preparation, including root-folder checks, without changing the author's monitoring gates. An unavailable compatible root retains the existing warn-and-skip monitoring behavior. A concurrently removed edition fails with HTTP 409 and leaves the previous book policy and selection intact. Full edition arrays write only rows whose persisted fields changed; genuine edition metadata edits still save.

`PUT /book` and `PUT /book/{id}` accept only edition IDs belonging to the target local book row. Unknown IDs, editions from another row (including another copy or media type), and contradictory nonzero `bookId` values fail validation before any book, edition or file changes. An omitted/zero edition `bookId` retains its stored parent; provider identity fields remain server-owned. An omitted/empty edition list retains existing behavior; a partial list still requires valid edition resource fields. Partial edition lists merge into the already-loaded editions. Selecting one edition clears the previous selection within that local book; omitted editions remain present. Duplicate submitted edition IDs or multiple submitted monitored editions fail validation before mutation. These checks add no database lookup.

Metadata saves with an unchanged selected edition preserve independent `ManualAdd` retention on unselected editions. Omitting an edition's `manualAdd` preserves its stored value; an explicit `false` still releases that marker. Explicit displacement of the previously selected pin retains its existing unpin behavior. A zero-selection repair loads that book's file evidence once and uses the shared files-first selector.

Readarr-compatible edition responses expose one local representative for each projected provider edition ID within a book, preferring its current monitored edition and otherwise the lowest local edition ID. This makes equality-based selection unambiguous without merging provider identities, changing stored edition rows or collapsing copies in other local books. Native responses retain all local editions.

SQLite and PostgreSQL enforce at most one monitored edition per local `BookId` with a partial unique index. Zero monitored editions are allowed by the database; existing insertion and housekeeping select a representative through the shared selector. Edition inserts, updates and selection-only writes clear a displaced selection and save its replacement in the same transaction. A failed write, including an edition removed since it was read, restores the previous selection and pin. Other local copies and media sides remain independent.

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
`all`, or an invalid UUID returns HTTP 400 before searching. The GET queues an
`InteractiveAuthorSearch` command and immediately acknowledges the UUID, author and
media type, with empty `bookResults` and `isComplete: false`. It does not wait for
indexers. Subsequent SignalR results use the envelope below; `authorId` and `bookId` are integer local request handles:

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
  "isCancelled": false,
  "isFailed": false
}
```

`AuthorSearchResults` messages on the existing authenticated SignalR connection
carry this envelope: the initial eligible roster, then one completed book group at
a time, then completion. The terminal SignalR message contains all groups. A worker
failure sets `isFailed: true`, including failures before the roster is available. A book with
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

The Stop button becomes available once admission is acknowledged or the first
roster arrives. It shows “Stopping after the current book” while waiting. The current book finishes and publishes its results; no remaining books
are searched. The terminal SignalR message carries
`isComplete: true` and `isCancelled: true` when remaining books were stopped. If
the active book was already the last one, the search completes normally. Completed
results and grab state remain available. A failed Stop request is shown as an error and can be retried; it must
not be displayed as a completed stop.

Leaving the author page or changing media scope requests the same stop. If admission
is still pending, the browser lets that short request finish and requests Stop again
after acknowledgement, preventing a search admitted after the first Stop from running
unattended. Closing the admission HTTP connection does not cancel grouped work.
Legacy flat searches retain RequestAborted cancellation between books. No caller
cancellation enters the shared indexer HTTP failure path. Active stop handles belong
to the background operation and are removed on success, cancellation, or error.
Commands restored after an application restart cannot repeat an absent interactive
session. Interrupted results remain partial; neither reconnection nor a missed
update restarts tracker searches automatically.

The acknowledgement includes the existing native `commandId`; the command publishes
normal status updates. After reconnect the browser uses the existing command refresh,
with `GET /api/v1/command/{id}` when the command is absent from that list. A terminal
or expired command without a final result snapshot ends the spinner, preserves received
rows and displays the existing incomplete-results warning. It does not declare a
successful empty search. Search can be started again explicitly; a late final snapshot
for the same search is still accepted. Failed status HTTP requests do not establish
completion. No grouped-result recovery endpoint or snapshot cache is added.

### Ambiguous Provider Identity

Chaptarr-native mutations return HTTP `409` when a provider ID maps to multiple candidate rows and the request does not specify enough information to choose one.

The response body uses `ProviderAmbiguityResource` and includes the candidate rows. Clients should show the choices to the user or retry with a narrower media type/provider identity.

Readarr/Seerr compatibility base URLs retain their documented provider-ID dialect. Native clients use explicit provider-ID fields and the ambiguity contract. Shared legacy profile/root fields now also require an explicit media scope, as described below.

### Declared Metadata Refusals

`POST /api/v1/book` and `POST /api/v1/author` return HTTP `422` when the metadata server explicitly declares that the requested work or author cannot be imported. The response uses the standard `ApiErrorResource` shape with a `message` containing the declared reason; it has no candidate rows. Clients should display the reason and stop automatically retrying that refused add request. This is separate from local provider-identity ambiguity, which remains HTTP `409` with `ProviderAmbiguityResource` candidates.

Invalid provider identifiers remain HTTP `400`. Metadata that is still being prepared can use the existing HTTP `202` pending-import response; temporary metadata failures retain their existing retryable HTTP `503` behavior. Unexpected application faults remain HTTP `500`. The metadata server's own V5 status codes and typed-author-stop meanings are unchanged by this Chaptarr add-response distinction.

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

### Author monitoring sync

`POST /api/v1/author/import` accepts `mediaType: "both"` to configure both sides in one request. Supply each side's `audiobookRootFolderPath` / `ebookRootFolderPath`, quality and metadata profile IDs, tags, initial monitoring mode and new-book policy. Existing single-side requests and their shared compatibility fields remain supported.

For an existing author, omitted roots, profiles, monitoring choices and tags preserve the saved settings for each requested side. Changing a root explicitly does not reset omitted profile or monitoring overrides to that root's defaults. Initial monitoring and search intent awaiting metadata are retained. Reposting a pending author request without settings preserves its queued choices. For a genuinely new side, omitted roots use the configured default, or the only compatible root when no default is configured; an ambiguous choice returns a validation error. The selected root supplies unset creation settings, including tags. Explicit `false` and empty tag arrays remain meaningful values. Unrequested sides are not initialized.

`syncMonitoredAcrossFormats` is an optional author setting. It can be enabled only when the resulting author has compatible audiobook and ebook roots, including when a request adds the missing side to an existing author. A new single-side author does not become dual-format merely because another root exists. Omission preserves an existing choice; compatible root defaults supply an unset choice when the author first becomes eligible. Later changes to root defaults do not rewrite the author's saved setting.

For author setup with sync enabled, explicitly supplied audiobook and ebook initial monitoring modes must agree, as must their new-book policies. Conflicting choices return HTTP `400` with field validation errors before the add is saved or queued. A supplied shared choice can fill an omitted counterpart; omission alone is not a conflict. Initial monitoring selects books during setup. A later new-book-policy edit does not reapply that initial selection. Author monitoring gates, roots, profiles and tags remain independently configured for each side. Book-specific requests retain their selected book IDs and media scope; their initial selection is not converted into shared author setup, but contradictory supplied new-book policies still fail validation.

Author PUT and bulk author edits use the same new-book-policy rule. An unrelated partial edit does not migrate or reject old stored policy differences, including when already-enabled sync is echoed without either policy. Clients should omit unchanged policy fields instead of resending a contradictory pair. Enabling sync for the first time when saved policies differ requires one explicit shared policy. An explicit `syncMonitoredAcrossFormats: false` permits independent choices.

Pending author imports retain the sync choice and monitoring policies. Requested initial searches run after the normal initial monitoring and format reconciliation complete.

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

Author POST/PUT and book POST requests using shared legacy profile/root fields must identify their media scope: `?mediaType=audiobook|ebook`, the existing `/audiobook` or `/ebook` compatibility base URL, or explicit per-format fields. An unscoped single-profile request returns HTTP 400 with those choices; neither the root folder nor a profile's type implicitly selects a format. The query parameter selects media only and keeps native provider-ID requirements. Compatibility base URLs retain their existing provider-ID dialect. A conflicting query and compatibility base URL is rejected. Native author requests with explicit fields for both sides remain supported. Book adds with explicit fields for both sides retain the both-format path. Wrong-side profiles must pass the existing type validation; they cannot redirect the requested format. This is write-specific validation, not a global API requirement: reads and writes to an already identified local book row retain their existing scope rules. Ordinary author edits using explicit saved per-format fields do not need a mediaType query. For example, shared `qualityProfileId` and `rootFolderPath` need a declared side, whereas `ebookQualityProfileId` and `ebookRootFolderPath` already identify it. Author request validation rejects ambiguity before setup or update; resource mapping and folder validation do not infer a side from a profile or root.

Adding a book defaults to **only the requested book in the requested media type**. When setting up a new author or a previously unconfigured format, omitted native monitoring policies enable that author side, select only the requested book, and leave future-book monitoring at `none`. Chaptarr does not infer `all` from a missing policy or the root folder's monitoring defaults. Explicit native first-setup policies are retained; `all` must be supplied explicitly. Required root-folder and profile settings still need to be provided. Readarr/Seerr facade book adds initialize the requested format with initial selection `none`, then select the requested book, with future-book monitoring `none`.

For a format the author already has configured, adding a book preserves the saved author-side monitoring gate (including a pause), profiles, paths, future-book policy and unrelated book selections. A nested or round-tripped Author with `monitored: true` does not resume a paused, configured side. Use Edit Author or an explicit author update to change those settings. When monitoring sync is enabled, selecting the requested book also selects its matching sibling book rows; that does not select the whole author catalog.

Book selection is separate from the author's initial selection policy. `author.addOptions.monitor: "none"` does not mean “do not monitor this requested book.” Send `monitored: false` on the book resource to add it without monitoring, or the matching per-format field (`audiobookMonitored` / `ebookMonitored`), which takes precedence. Explicit `false` also unmonitors an already-present requested book; omission leaves the normal book-add selection in effect. Requested books retain a manual-add preservation marker, not an edition pin.

Optional `addOptions.searchForNewBook: true` queues a Manual `BookSearchCommand` for each exact requested target. This is independent of ongoing monitoring: it can request a one-time search while the author side is paused or the book is explicitly unmonitored. It does not search unrelated titles. Subsequent RSS and automatic acquisition remain gated by the saved author-side gate and book selection. Queued requests retain their setup settings, explicit book selection, search intent and provider identities through the existing pending-import lifecycle.

An explicit facade `PUT /book` with `monitored: true` selects the requested row even when it is already selected, including repair of an unmonitored matching counterpart when sync is enabled; it does not enable the author media-side gate. Lookup GETs and search-only commands never change saved monitoring settings. Clients must send the book update to request this behavior even for an already-selected book. Chaptarr does not infer an approved request from a lookup or change the meaning of the resource's `monitored` field to induce a client update.

A facade-scoped `PUT /author` updates the requested author side without queuing author-wide book-monitoring reconciliation, including when the client round-trips `syncMonitoredAcrossFormats: true`. The saved sync setting still applies to explicit book requests. Native, unscoped author updates and the native bulk editor retain their existing reconciliation behavior.

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

`GET /api/v1/config/mediamanagement/covercleanup` provides a read-only preview of the cache cleanup for `libraryBooks`, regardless of the currently saved scope. It returns `{ "bytes": 0, "bookCount": 0 }` for no removable cache folders. Otherwise, `bytes` estimates logical file bytes from at most 500 randomly selected removable folders, and `bookCount` is the candidate folder count at enumeration time. The sample is repeatable for an unchanged folder population and includes orphaned canonical cache folders. It uses one batched library-ID lookup and the cleanup task's canonical book-folder selection; it does not save settings, delete images, or download covers. This is an approximate snapshot, not guaranteed allocated disk space reclaimed or a fixed accuracy guarantee. A three-second budget and request cancellation are checked between synchronous reads; an already-blocked filesystem or database call cannot be interrupted by that check. An expired budget or failed estimate returns an error rather than a partial total. Clients should keep saving usable while loading, and display the result as a rounded approximation. Media Management offers an optional **Calculate savings** button beside **Download covers for** and shows the result below the setting; the confirmation shown when saving a change from `allBooks` to `libraryBooks` repeats a result only if one was already calculated. Loading the page, changing the option, opening the confirmation or saving without calculating sends no preview request.

Book resources in `libraryBooks` mode expose excluded covers with `images[].url = null` and a non-null `temporaryUrl`. Lists must show a placeholder, without falling back to `remoteUrl`. A book details view may request a nonempty `temporaryUrl` through the existing privacy proxy; an empty value means no usable temporary image. These requests do not store image bytes in Chaptarr's cover cache. `remoteUrl` remains provider metadata. Temporary URLs are resource-only and are not persisted in edition metadata. Cached eligible covers retain their local URLs. Uncached saved-book covers in `allBooks`, and detail-only previews in `libraryBooks`, use `/MediaCoverProxy/book/{localBookId}/{filename}?v={imageVersion}`. The authenticated image route resolves the monitored edition with the same selector as cover downloads and performs no persistent image write or temporary URL registration. It remains valid across scope changes and process restarts while the book and monitored image exist. Browser caching is private for 24 hours; changing the source image URL changes the version token. Switching back to `allBooks` does not queue a library-wide download: ordinary author refreshes refill the cache.

### Import-list monitoring options

`ImportListResource.enableAutomaticAdd` remains the list's master switch: disabling it retains its settings and existing library rows. `monitoringOptions.addMissingAuthors` independently allows author imports. `shouldMonitorExisting` controls repeated selection of listed books for existing authors, including requests for books or formats not yet local; `monitoringOptions.resumePausedAuthors` independently turns on monitoring for the matching author side. Selecting a book does not imply monitoring its author.

The optional `monitoringOptions` object is the runtime authority for independent audiobook/ebook author gates, initial catalog monitoring modes, and new-book policies. Listed-book requests add to the initial catalog selection; initial None plus listed-book monitoring selects only listed books. Unset per-format choices inherit the selected root when an author or missing format is initialized; explicit values, including None and false, take precedence. Saving a list does not turn inherited choices into a snapshot of the current root. Omitting the whole object on an API update preserves its saved options. `shouldMonitor` still distinguishes no listed-book request from a listed-book request when adding an author; the legacy EntireAuthor and `monitorNewItems` fields do not reconstruct catalog, future-book, author-gate or resume policies at runtime.

Migration 111 performs the one-time conversion of saved legacy choices into explicit per-format options using their current root folders and legacy list choices. Later root-folder changes do not alter those saved explicit values. Existing lists are not silently opted into repeated monitoring; legacy None lists retain Keep off and Resume off.

A book-only list entry is satisfied by any already-monitored matching row of the requested `BookMediaType`; otherwise one matching row is selected by lowest positive local ID. Provider work IDs establish identity; that local ID is only a tie-breaker. Edition-only entries resolve their owning work first. Book-only entries do not select a narrator or replace a pinned edition. A media-scoped Readarr lookup puts that preferred copy first while returning every eligible copy. Native lookup returns every candidate in local-ID order; an ambiguous native mutation returns HTTP 409 with candidates instead of applying the list's selection rule.

Explicit audiobook narration requests inspect all same-author, same-format rows matching the provider work or the explicit provider edition. An exact selected edition or an equivalent complete narration satisfies the request; that actual selection is pinned even when it was previously automatic. Otherwise an unpinned fileless row may select the requested edition, or the existing wanted-edition mechanism creates a pinned copy. Existing incompatible pins and downloaded narrations are retained. Repeated requests reuse the compatible row. A generic work request carries no narrator intent and does not alter pins. Repeated generic list requests for the same work and media side select one existing row. The selected work keeps the existing manual-work preservation marker through profile filtering and refresh; if filtering removes every supported edition, normal selection retains one representative from the authoritative catalog without pinning it. Unrequested works and the other media side still follow their profiles. Pending author imports preserve explicit edition intent through their existing versioned queue record; only the authoritative imported catalog supplies editions.

Edition-capable sources retain the supplied complete narration through mapping, cache and pending requests. The exact edition ID need not exist locally if the same established work has a compatible audiobook narration; the existing complete-cast comparison applies without a duration or provider-default test. Goodreads Series and LazyLibrarian automatic selections are generic work requests. Chaptarr-to-Chaptarr imports carry narration only from the sender's explicit pin. A missing parent work stays on the existing recovery path; it is not proof that the requested narration is unavailable.

Source removal does not unmonitor or delete local books. Re-monitoring applies when the provider returns the entry: Goodreads rereads its list, while Hardcover normally returns changes and performs a full refresh weekly. With searching enabled, newly selected existing rows on active author sides enter one automatic search batch that respects delay profiles. Paused author sides receive one immediate manual search per newly selected row; successful queue submission clears its saved search intent. Unchanged rows are not searched again on each sync. For newly added authors, the scan handler applies initial monitoring before submitting the stored missing-book search; paused sides retain only their explicitly listed search targets.


## Manual-import replacement confirmation

`POST /api/v1/manualimport/replacementfiles` accepts a body with `files`, each containing a selected filesystem `path` and local `bookId` (zero for an unresolved metadata suggestion). It returns a typed array of `{ id, bookId, path, associatedPaths }` for the primary BookFile rows that the selection would replace. Associated paths include existing managed ebook copies and tracked sidecars eligible for deletion under the existing ownership/shared-folder rules. The read-only preview batches file, extra-file and local-author lookups; it does not request provider metadata or create books. Selected source files and tracked in-place/relocation inputs are excluded from deletion.

The GUI copies that exact array to `confirmedReplacementFiles` on its `ManualImport` command. `[]` permits no primary BookFile replacement; a metadata-only selection that later resolves an existing recording therefore fails safely rather than silently replacing it. Changed IDs, local Book ownership, paths or newly discovered destination rows require a fresh preview. Null/omitted confirmation retains the legacy API boolean `replaceExistingFiles` behavior. Combine does not request replacement confirmation.

Execution checks the current associated paths before the primary database swap and passes the allowed paths to the existing deletion events. Newly discovered unconfirmed paths require another preview; event handlers also preserve any unconfirmed paths appearing afterward. A confirmed sidecar may still be retained if it serves a remaining media file. Independently owned ebooks sharing an audiobook folder are not replacement candidates. Confirmed manual imports defer managed ebook-copy reconciliation until after the primary save, using the existing mixed-root placement planner. Extra-file copies still respect Import Extra Files and its extensions, and cannot overwrite a surviving destination during a confirmed import. An omitted/null confirmation retains legacy API behavior.
