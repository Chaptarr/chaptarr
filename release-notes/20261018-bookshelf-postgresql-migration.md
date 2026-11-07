---
category: added
audience: operators
area: migration
action: Preview the conversion and retain the BookshelfNG source until the new library is verified.
breaking: false
---
ChaptarrNG now upgrades supported older BookshelfNG PostgreSQL schemas on a disposable snapshot before conversion. Preview leaves the source untouched, and successful conversion retains the original schema for rollback.
