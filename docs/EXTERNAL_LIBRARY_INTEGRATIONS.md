# External library integrations

ChaptarrNG can refresh external media libraries, publish selected metadata and
covers, and forward edits between supported connections. Add
notification-style connections under **Settings → Connect**. The connections
have different directions and ownership rules, so configure only the actions
you want ChaptarrNG to perform.

| Connection | Main role | Data direction |
| --- | --- | --- |
| AudioBookShelf | Notify library watchers, publish selected metadata and covers, and accept forwarded edits from another connected library. | ChaptarrNG → AudioBookShelf; optional forwarded edits from compatible connections. |
| Grimmory | Refresh mapped libraries, push metadata and covers, and optionally forward Grimmory sidecar edits to other connections. | Both directions when forwarding is enabled. |
| Calibre Content Server | Publish ebook records and optionally mirror selected ChaptarrNG edits and deletions. | Primarily ChaptarrNG → Calibre. Calibre-side deletions are not imported back. |
| Emby / Jellyfin | Ask the server to refresh its library after a grabbed release is imported. | ChaptarrNG → Emby or Jellyfin. |

Keep each server reachable from the ChaptarrNG container. Use the service name
and container port for services on a shared Docker network. Store credentials
in the connection's password or API-key fields rather than putting them in a
URL.

## AudioBookShelf

Add an **AudioBookShelf** connection with its host, port, optional URL base,
TLS setting, and API key. The default port is `13378`. The connection editor
can also authenticate through AudioBookShelf OpenID Connect when the server's
mobile redirect settings allow ChaptarrNG's callback.

Map each ChaptarrNG root folder and media type to an AudioBookShelf library.
This keeps ebook and audiobook files routed to the right library even when one
ChaptarrNG connection serves both formats. After imports and renames,
ChaptarrNG sends targeted watcher updates for mapped paths; if a targeted
update fails, it falls back to a full scan of the affected library. The
**Remove Missing Items After Delete** option asks AudioBookShelf to remove
items whose files were deleted by ChaptarrNG rather than leaving them marked
missing.

Enable **Push Library Edits** to publish supported metadata and cover changes
to matched AudioBookShelf items, including edits forwarded from another
connected library. **Ignore AudioBookShelf Tags** refers to tags on the remote
AudioBookShelf item, not ChaptarrNG tags. Matching is case-insensitive.
Automatic pushes skip items with any configured ignored tag; a manual edit
forwarded from another connected library can still update them.

Root-folder settings can separately write AudioBookShelf metadata JSON and a
cover image alongside local files. Those sidecars are independent of the
server API connection and can be enabled per media type.

## Grimmory

Add a **Grimmory** connection with its URL and an account that can manage the
selected libraries. Grimmory must see the same files as ChaptarrNG, through a
shared mount or identically mounted storage. Select the ebook and audiobook
libraries independently; leave a side unset to ignore that media type.

The connection can refresh a library after imports, renames, deletes, or
retags. Turn on **Push Metadata** and/or **Push Covers** to publish ChaptarrNG
changes. Refreshes are queued so Grimmory can index files before metadata is
pushed. A manual push dialog can send selected metadata and cover fields.

**Ignore Grimmory Tags** refers to Grimmory's tags, not ChaptarrNG tags. Tag
matching is case-insensitive. Automatic pushes and forwarded edits skip items
with a configured tag, while an explicit manual push remains available.
Grimmory field locks are respected; if a pushed field is locked, unlock it in
Grimmory before expecting a new value to replace it.

Turn on **Forward Grimmory Edits** to watch for metadata sidecars written by
Grimmory and pass those edits to connected libraries that accept external
edits. This requires Grimmory's **write on update** sidecar option and
`DISK_TYPE=LOCAL`. Grimmory does not write the sidecars ChaptarrNG watches when
it runs with network storage mode, where that option is hidden. ChaptarrNG
suppresses its own echoed writes so a push does not create a forwarding loop;
later human edits can still be forwarded.

## Calibre Content Server

ChaptarrNG has two related Calibre settings surfaces:

1. A **Calibre-backed root folder** uses the server's library and output
   settings when ChaptarrNG imports an ebook. Configure it from the root-folder
   editor with the server host, port, optional credentials, library, output
   format, and output profile. ChaptarrNG sends the book through Calibre so
   Calibre controls its placement. A Calibre library can only be configured
   once as a root folder; do not configure the same Calibre library twice.
2. A **Calibre Content Server** connection acts as a mirror for ebooks already
   managed by ChaptarrNG. It can receive grabbed imports, optional library-scan
   imports, and optional metadata edits. **Push Library Scan Imports** includes
   ebooks discovered by a library scan; **Push Library Edits** sends selected
   ChaptarrNG metadata changes. **Allow Edits & Deletes** controls whether
   ChaptarrNG changes matching Calibre records or propagates ChaptarrNG-side
   deletions.

Both flows are ChaptarrNG-led. Deleting a book inside Calibre is not reflected
back into ChaptarrNG, and the mirror may publish that book again later. Use
the explicit edit/delete option only if ChaptarrNG should own those changes.
Title or author changes that can move a Calibre file require explicit opt-in
in the push flow. Empty metadata is omitted so it does not erase curated
Calibre values.

## Emby and Jellyfin

Add **Emby / Jellyfin**, choose the server type, and enter the host, port,
optional URL base, TLS setting, and API key. The connection test checks server
authentication. When its **On Release Import** notification is enabled,
ChaptarrNG requests a library refresh after a grabbed book is imported. This
is a server-wide refresh request; it does not create or move media files.

## Related automation

The [MQTT Gateway](./MQTT_GATEWAY.md) publishes library snapshots and
canonical lifecycle events, with optional Home Assistant discovery and
canonical events for existing Webhook connections. The separate
[Prometheus listener](./PROMETHEUS_METRICS.md) exposes operational metrics;
it is disabled by default and should stay on a trusted network.
