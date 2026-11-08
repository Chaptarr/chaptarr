---
category: added
audience: operators
area: distribution
action: none
breaking: false
---

ChaptarrNG now has a gated Launchpad source-package builder and publisher for
the legacy `bookshelfng` Debian identity. It builds from the checksum-verified
Linux x64 archive while preserving the service, environment file, and data
path. Publishing remains gated by migration readiness, credentials, configured
Ubuntu series, and channel acceptance; this change does not publish a package.
