# Chaptarr API Identity and Lifecycle Contract

This document is the public-facing contract for clients building against Chaptarr and the Chaptarr Metadata Server. It describes identity semantics that the generated OpenAPI schema cannot express by itself.

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
