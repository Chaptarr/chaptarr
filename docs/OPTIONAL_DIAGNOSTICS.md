# Optional diagnostics

ChaptarrNG can send a small set of aggregate event counts to an operator's
OpenTelemetry collector. It is disabled by default. Migration does not create
diagnostics settings; it preserves the prior explicit opt-in only when both
the legacy enable flag and installed-module marker are present.

To enable it, set all three environment variables and restart ChaptarrNG:

```yaml
environment:
  CHAPTARR_DIAGNOSTICS_ENABLED: "true"
  CHAPTARR_DIAGNOSTICS_OTLP_ENDPOINT: "https://collector.example/v1/metrics"
  CHAPTARR_DIAGNOSTICS_AUTH_TOKEN: "${CHAPTARR_DIAGNOSTICS_AUTH_TOKEN}"
```

The endpoint must be HTTPS, end in `/v1/metrics`, and contain no URL
credentials, query, or fragment. Redirects are disabled and normal TLS
certificate validation remains enabled. The token is sent only in the HTTP
Authorization header and is never included in the payload or logs.

The module aggregates these fixed, low-cardinality events every 15 minutes
and once during normal shutdown:

- `application.started`
- `book.grabbed`
- `book.imported`
- `download.failed`

Counts stay in memory for the current process and are cumulative. The payload
contains only event names, counts, reporting timestamps, `chaptarrng` as the
service name, and the application version. It contains no book or author data,
titles, paths, search terms, exception details, credentials, or persistent
installation identifier. The collector can still observe the source IP address
of the HTTPS request.

If a request fails, ChaptarrNG logs a generic warning and keeps the counters
available for a later retry. Diagnostics errors do not stop library work.

For compatibility, a migrated install keeps the legacy
`BOOKSHELF_DIAGNOSTICS_*` endpoint and token variables. The legacy enable flag
is honored only if the archived appdata contains
`plugins/Bookshelf.Diagnostics.dll`, which records that the optional module
was installed before migration. The ChaptarrNG diagnostics assembly is used;
the old BookshelfNG binary is not loaded. Setting
`CHAPTARR_DIAGNOSTICS_ENABLED=false` disables diagnostics even if the legacy
flag is still present.
