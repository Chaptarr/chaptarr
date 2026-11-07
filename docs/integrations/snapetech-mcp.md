# SnapeTech MCP

ChaptarrNG does not run an MCP server inside the application. The shared
`snapetech-mcp` process reads the existing book API over MCP stdio transport.
Its quickstart is in the sibling `snapetech-mcp/README.md`.

## Tools and API access

| MCP tool | ChaptarrNG endpoint | Result |
| --- | --- | --- |
| `chaptarrng_status` | `GET /api/v1/system/status` | Reachability only. |
| `chaptarrng_list_books` | `GET /api/v1/book/paged` | Up to twenty book summaries with optional format and library-state filters. |
| `chaptarrng_search_authors` | `GET /api/v1/author/lookup` | Bounded author metadata search. |
| `chaptarrng_search_book_catalog` | `GET /api/v1/book/lookup` | Bounded title search against configured book metadata providers. |
| `chaptarrng_list_download_queue` | `GET /api/v1/queue` | Bounded queue status, optionally filtered by ebook/audiobook format. |
| `chaptarrng_list_history` | `GET /api/v1/history` | Bounded recent book history, optionally filtered by format. |

Use the dedicated SeerrNG service key, configured as
`CHAPTARR__AUTH__SEERRAPIKEY`, rather than ChaptarrNG's regular administrator
API key. Configure `SNAPETECH_MCP_APPS=chaptarrng`,
`SNAPETECH_MCP_CHAPTARRNG_URL=<ChaptarrNG base URL>`, and
`SNAPETECH_MCP_CHAPTARRNG_API_KEY=<service key>` in the MCP host's protected
environment. The bridge sends the key in `X-Api-Key`.

After moving from BookshelfNG, set `SNAPETECH_MCP_APPS=chaptarrng`, update the
ChaptarrNG URL and service key, and remove the BookshelfNG media-type selector.
See the migration guide's
[MCP connection steps](../MIGRATE_FROM_BOOKSHELFNG.md#update-the-local-mcp-connection).

## Credential and data boundaries

The SeerrNG service key is restricted to supported book, author, queue,
history, and pending-import routes. That access policy allows selected writes,
including book additions or updates and pending-import cancellation; it is not
strictly read-only. The MCP server calls GET routes for status, books, catalog search,
queue, and history.
Use a dedicated key, protect it like an application credential, and rotate it
through the secret manager and service restart if exposed.

The MCP result omits file paths, provider secrets, release names, download IDs, client
names, and raw command/event data. Author and book catalog searches may contact
configured metadata providers. Keep the process attached to a trusted local client and
never expose its stdio transport through a TCP bridge or reverse proxy.

## Competitor feature coverage

The active [Chaptarr project](https://github.com/Chaptarr/chaptarr/blob/develop/README.md) covers combined ebook/audiobook libraries, narrator and edition
metadata, series organization, metadata/quality profiles, flexible naming,
Arr-compatible indexers and clients, automatic upgrades, and audiobook format
conversion. The retired [Readarr project](https://github.com/Readarr/Readarr) established the author/book monitoring, RSS/manual
search, profiles, downloads, imports, renaming, and Calibre workflows. ChaptarrNG adds
the SeerrNG capability and pending-import contracts; its own [feature list](../../README.md#key-features) tracks the
maintained extensions.

Snapetech MCP exposes bounded library pages, author/catalog search, queue state, and
book-history summaries. The service key is route-limited but still allows selected
write operations outside this GET-only bridge. The adapter omits paths, release names,
download identifiers, client details, and raw event/command data. Book additions and
pending-import cancellation remain outside the read-only baseline.

**Confidence:** high for published Chaptarr/Readarr features and the checked MCP route/key policy.

Source contract: `docs/SEERRNG_INTEGRATION.md`,
`src/Chaptarr.Http/Authentication/SeerrApiKeyAccessPolicy.cs`, and
`src/Chaptarr.Http/Authentication/ApiKeyAuthenticationHandler.cs`.
