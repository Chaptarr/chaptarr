# BookshelfNG Debian PPA candidate

ChaptarrNG now has a gated Launchpad source-package builder and publisher for
the legacy `bookshelfng` Debian identity. It builds from the checksum-verified
Linux x64 release archive and preserves the existing service, environment
file, and `/var/lib/bookshelfng` data path.

PPA publishing requires the existing BookshelfNG migration and release
readiness gates, a signing key, a configured PPA, and explicit Ubuntu series.
This change does not publish a package; external PPA credentials, archive,
series, and channel acceptance remain to be validated.
