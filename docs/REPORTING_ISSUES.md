# Reporting issues

Open an issue in the [ChaptarrNG repository](https://github.com/snapetech-org/chaptarrng/issues)
with the steps that reproduce the problem, what you expected, and what
happened. Include the application version and commit when available, install
type, OS and architecture, and the image tag or package version.

## UI or API failures

Include the URL path and response status, the time and timezone, and the
`X-Bookshelf-Request-Id` response header when one is present. The header keeps
its BookshelfNG name for compatibility. It lets maintainers find the matching
request in the application log.

For containers, the debug log is usually
`/config/logs/chaptarr.debug.txt`. For a standalone install, find
`chaptarr.debug.txt` in the configured data directory's `logs` folder. Send a
small excerpt around the request time and ID. For a UI problem, include the
browser console error and the failed request path, without cookies or request
headers that contain credentials.

If the debug log does not show enough detail, temporarily switch to trace
logging, reproduce once, and return to debug afterward. Review trace excerpts
before sharing: they may contain remote IP addresses, User-Agent values, or
private file paths.

## SeerrNG or migration failures

For a SeerrNG issue, include the SeerrNG and ChaptarrNG versions, the selected
media format, the request or command ID, and matching log lines from both
services. For a migration issue, include the preview command and sanitized
summary output, the source database type and schema version, and whether the
service was stopped. Follow the [migration guide](MIGRATE_FROM_BOOKSHELFNG.md)
and keep the original appdata backup while migration acceptance remains open.

Never attach API keys, passwords, cookies, authorization headers, a full
database, or an unredacted appdata archive. Remove private book titles,
authors, and filesystem paths when they are not needed to explain the failure.
