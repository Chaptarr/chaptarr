---
category: added
audience: users, operators
area: bookshelf
action: none
breaking: false
---
Authors that have only an audiobook or only an ebook root folder can now be filled in from your default root folders with one request to POST /api/v1/author/editor/backfill-roots, with dryRun=true to preview counts first. Hand-set authors are skipped and no files are moved.
