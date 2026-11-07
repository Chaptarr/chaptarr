# Import-list preview and sync history

ChaptarrNG adds a read-only preview and durable per-list history to the import
list workflow. These tools let you inspect how the next sync would classify a
list before its entries are applied.

## Preview the next sync

Open **Settings → Import Lists** and select **Preview Next Sync** on a list.
The preview fetches that list, resolves the provider identities needed to map
its entries, compares them with the local library, and displays these counts:

| Count | Meaning |
| --- | --- |
| Processed | Entries considered by this sync. |
| New | Entries with no matching local author or book. |
| Existing | Entries that match a local author or book. |
| Changed | Existing entries whose monitoring state this list would change. |
| Excluded | Entries matched by an import-list exclusion. |
| Unresolved | Entries that could not be mapped to a supported provider identity. |
| Search eligible | New or changed entries that meet the list's monitoring and search settings. |

Preview performs provider reads and metadata lookups, so it needs network
access and can take time for a large list. It does not add authors or books,
queue searches, commit the provider cursor, update the list's last-sync status,
or write the normal provider-response cache. ChaptarrNG records the preview
itself in sync history; its `dryRun` value is `true` and
`providerStateCommitted` is `false`.

The preview is a snapshot. A list or local library can change before the next
scheduled or manual sync, so its counts are informative rather than a promise
that the later run will make exactly the same decisions.

## Review sync history

The same **Preview Next Sync** dialog shows the list's recent syncs and
previews. Each record includes its start time, duration, result, failure stage
when applicable, counts, and whether provider state was committed. Scheduled
and manual syncs are recorded separately for each list. A failed run identifies
the stage where it stopped, such as `fetch`, `mapping`, `classification`,
`apply`, or `state-commit`.

History is available through the Chaptarr API:

```http
POST /api/v1/importlist/{id}/preview
GET  /api/v1/importlist/{id}/history
```

The history endpoint returns the newest records first. ChaptarrNG retains up
to 100 records per list and 5,000 records across all lists. The preview
endpoint returns the newly recorded preview result, including the counts and
dry-run/state-commit flags.

## Provider state and failed runs

Normal syncs commit provider-owned progress, such as a Hardcover cursor, only
after ChaptarrNG has processed the fetched entries. If processing fails, the
run is recorded as failed and the provider cursor is left uncommitted. The
state is committed on the same list instance that was fetched, avoiding a
cursor update against a stale or different list object.

Finalizing a history record is best-effort: if updating or pruning a record
fails, that history write alone does not turn a successful sync into a failure.
The preview depends on read-only provider-cache and list-status scopes; if
those scopes are unavailable, it reports a failed preview instead of silently
falling back to a mutating run.

## Goodreads bookshelf refresh interval

Goodreads bookshelf lists retain a 12-hour default refresh interval. The
interval is configurable, but cannot be less than 15 minutes. ChaptarrNG
validates this minimum when saving and enforces the same floor when scheduling
the refresh. The interval is a setting on the Goodreads bookshelf list; the
provider-owned `minRefreshInterval` value returned by the general import-list
API is read-only.
