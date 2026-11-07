---
category: fixed
audience: operators
area: migration
action: none
breaking: false
---
ChaptarrNG now checks database lineage before running migrations and refuses to start when a native database and a legacy Readarr or Bookshelf database both exist in the same app-data directory. This prevents a legacy library from being silently hidden behind `chaptarr.db`; verified data conversion is still required before switching applications.
