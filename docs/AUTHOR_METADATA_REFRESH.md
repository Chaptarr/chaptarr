# Author metadata refresh

ChaptarrNG stores author details, provider IDs, and `LastInfoSync` on the
persisted author record. The database is the durable catalog; provider response
caches are temporary and do not replace it. Old records are not deleted just
because they are stale. Keep the application's `/config` data persistent so
the database survives container replacement.

Unlike BookshelfNG's separate author-metadata record, ChaptarrNG keeps catalog
fields on its author record. Provider identities remain distinct: a Goodreads
ID is not interchangeable with a Hardcover or Open Library ID. Changing
metadata identity does not convert a library; use the
[BookshelfNG migration guide](MIGRATE_FROM_BOOKSHELFNG.md) for provider
conversion limits.

## Automatic refresh policy

ChaptarrNG evaluates these rules in order:

| Author state | Decision |
| --- | --- |
| `LastInfoSync` is missing | Refresh metadata. |
| Last sync was more than 30 days ago | Refresh metadata. |
| Last sync was within the past 12 hours | Skip metadata refresh. |
| Status is `Continuing` and last sync was more than 2 days ago | Refresh metadata. |
| Latest known book release is within the past 30 days | Refresh metadata. |
| No rule above matches | Wait for a later sweep or explicit refresh. |

The 30-day stale check runs before the 12-hour guard. The 12-hour guard runs
before status and release-date checks, so an author checked recently is not
fetched repeatedly because of a new book event.

## When a book is added

On current ChaptarrNG `main`, a book-added event checks the author freshness
policy before requesting remote author metadata. It still requests an
author-scoped folder rescan so files already on disk can be found, even when
the metadata check is skipped or returns unchanged data. The rescan follows
the configured **Rescan After Refresh** policy and requires usable folder
evidence for that author. Preview this source-only behavior separately from
the published `v0.9.977` release; the BookshelfNG retirement release freeze is
still active.

Adding a new author performs an initial metadata refresh and folder scan.
Changing an author's metadata profile or adding a format-specific root also
queues a forced metadata refresh so ChaptarrNG can apply the new profile or
hydrate the newly available format path.

When a refreshed book identifies an author already stored under the same
provider identity, ChaptarrNG reuses that local author record. It does not need
to fetch the author's full catalog a second time just to attach the book. A
missing local author is fetched and added before the book is attached.

## Manual refresh

A manually triggered refresh for one author bypasses the age check. Scheduled
and bulk work continues to use the freshness policy and provider change hints
when available. Initial author sync, metadata-profile changes, and new
format-specific roots also request the refresh needed to establish or update
the local catalog.

## Hardcover request pacing

The native Hardcover provider uses a process-local response cache:

| Response | Cache lifetime |
| --- | ---: |
| Author details | 30 minutes |
| Work details | 6 hours |
| Search results | 10 minutes |

The client spaces requests at one per second in each ChaptarrNG process. It
honors `Retry-After` and exhausted rate-limit buckets, and pauses all requests
in that process until the applicable reset. A 429 without a usable reset
causes a one-minute pause; requests made during cooldown are rejected locally
without another network call.

The client reads daily limits and remaining capacity from `RateLimit-Policy`
and `RateLimit`, with legacy daily headers as a fallback. Background catalog
work leaves the final 10% of the reported daily quota, rounded up to at least
one request, for interactive searches. Interactive searches may use that
reserve but still honor an active rate-limit pause or an exhausted bucket.
Background work can resume when the reported daily window resets.

Cache, pacing, and cooldown state are process-local. Separate ChaptarrNG
instances do not share response caches or coordinate request rates, even when
they use the same token or outbound IP. Use one metadata-active instance per
token/IP when shared pacing matters.

## Failed refreshes and local data

A network error, HTTP error, invalid response, or rate-limit response does not
replace the stored author catalog or make a failed lookup appear fresh. A
later eligible or manual refresh can retry it. A successful unchanged-provider
check may update `LastInfoSync` without replacing the catalog fields.

ChaptarrNG does not automatically delete a local author just because a
provider reports it missing or unavailable. The existing record and provider
IDs remain available for correction or a later refresh; removal requires an
explicit local action.

For a rate-limit pause, look for log messages saying that Hardcover is pausing
requests until a UTC reset time. A request rejected during that pause reports
that no network request was sent. For other failures, check the provider error
near the author-refresh command before retrying.

## Code references

- [Persisted author fields and provider IDs](../src/NzbDrone.Core/Books/Model/Author.cs)
- [Automatic freshness policy](../src/NzbDrone.Core/Books/Utilities/ShouldRefreshAuthor.cs)
- [Book refresh and local-author reuse](../src/NzbDrone.Core/Books/Services/RefreshBookService.cs)
- [Book-added event](../src/NzbDrone.Core/Books/Handlers/BookAddedHandler.cs)
- [New-author refresh](../src/NzbDrone.Core/Books/Handlers/AuthorAddedHandler.cs)
- [Profile and format-root refresh](../src/NzbDrone.Core/Books/Handlers/AuthorEditedHandler.cs)
- [Refresh and rescan handling](../src/NzbDrone.Core/Books/Services/RefreshAuthorService.cs)
- [Hardcover provider cache and request pacing](../src/NzbDrone.Core/MetadataSource/Hardcover/HardcoverMetadataProxy.cs)
