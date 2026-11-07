# HTTP security and reverse-proxy settings

ChaptarrNG applies conservative HTTP defaults and lets operators configure
reverse-proxy trust and cross-origin browser access explicitly. These settings
accept `CHAPTARR` keys first and fall back to the matching legacy `READARR` key
when the ChaptarrNG key is unset. This keeps existing BookshelfNG and Readarr
configuration usable during a migration.

Set these values as environment variables; double underscores map to config
section separators:

```text
CHAPTARR__SERVER__TRUSTEDPROXIES=10.0.0.8;192.168.1.0/24
CHAPTARR__SERVER__ALLOWEDORIGINS=https://books.example;https://admin.example
CHAPTARR__SERVER__REQUESTSPERMINUTE=600
CHAPTARR__SERVER__MAXREQUESTBODYSIZE=16777216
```

If both namespaces define a value, the `CHAPTARR` value takes precedence.
`READARR__SERVER__TRUSTEDPROXIES`, `READARR__SERVER__ALLOWEDORIGINS`,
`READARR__SERVER__REQUESTSPERMINUTE`, and
`READARR__SERVER__MAXREQUESTBODYSIZE` remain accepted for migration.

## API key query compatibility

API keys are accepted from the `X-Api-Key` or `Authorization: Bearer` header.
For older clients, `?apikey=` query authentication remains enabled by default.
Set `CHAPTARR__AUTH__ALLOWAPIKEYQUERY=false` to reject API keys in query
strings. If that ChaptarrNG setting is unset, the legacy
`BOOKSHELF_ALLOW_API_KEY_QUERY=false` variable is honored. An explicit
ChaptarrNG setting takes precedence. This switch only disables the `apikey`
query parameter; SignalR's `access_token` query remains supported. Query-string
credentials can persist in browser history and proxy logs, so use a header and
HTTPS when possible.

The optional `CHAPTARR__AUTH__SEERRAPIKEY` credential has a separate
route allowlist, cannot read global configuration or health endpoints, and
must scope book-file and history reads to one book ID. The legacy
`BOOKSHELF_SEERRNG_API_KEY` remains a fallback when the ChaptarrNG key is
unset. See the [SeerrNG integration guide](SEERRNG_INTEGRATION.md) for its
allowed routes and migration behavior.

## Trusted proxies

`TRUSTEDPROXIES` is empty by default. Each entry must be an IP address or CIDR
network. ChaptarrNG reads one `X-Forwarded-For` and one `X-Forwarded-Proto`
value only when the immediate peer is trusted, and requires the forwarded
headers to be symmetrical. Framework loopback proxy defaults remain available.
`X-Forwarded-Host` is not consumed. Keep the trusted list limited to the
reverse-proxy addresses that connect directly to ChaptarrNG.

## Cross-origin requests

Cross-origin CORS access is disabled by default; same-origin browser access is
unchanged. `ALLOWEDORIGINS` accepts comma- or semicolon-separated origins,
including scheme and port when present. Use origins such as
`https://books.example:8443`; do not include a path or wildcard. Configured
origins are allowed for the versioned API and the static GET/OPTIONS policy.
The API policy allows its existing methods and headers, while the static policy
allows only GET and OPTIONS. The API policy exposes the legacy
`X-Bookshelf-Request-Id` response header for request correlation.

## Bundled UI assets

The generated UI accepts webpack JavaScript and stylesheet URLs only when they
resolve to the same origin. The template emits only the URL path, query, and
fragment, rejects invalid or external URLs, and limits each asset URL to 2,048
characters.

## Request limits

`REQUESTSPERMINUTE` sets a global fixed-window limit per client IP. It defaults
to 600 requests per minute, is clamped to 60–6,000, queues no excess requests,
and returns HTTP 429 with `Retry-After: 60` when a client exceeds the limit.

`MAXREQUESTBODYSIZE` sets Kestrel's general request-body limit in bytes. It
defaults to 16 MiB and is clamped to 1–128 MiB. The backup upload endpoint has
its own explicit 1 GiB request and multipart limits, so large ChaptarrNG and
BookshelfNG backup restores remain supported.

## OIDC backchannel destinations

OIDC discovery, user-info, and token requests reject hosts that resolve to
private or reserved addresses. Set
`BOOKSHELF_OIDC_ALLOW_PRIVATE_ISSUER=true` when an internal identity provider
needs those addresses. This opt-in also permits HTTP metadata, matching the
BookshelfNG setting. ChaptarrNG continues to use the configured proxy and
certificate validation, does not follow redirects, and limits each response
body to 1 MiB.

## JSON-only mutation endpoints

The BookshelfNG-compatible Author Editor, Bookshelf, Book Editor, and Manual
Import update actions accept JSON request bodies only. Clients calling those
actions must send `Content-Type: application/json`; other request media types
are rejected.

These controls are included in the current ChaptarrNG source. They are not in
the published `v0.9.977` release and do not by themselves close the BookshelfNG
migration, release, or cutover gates.
