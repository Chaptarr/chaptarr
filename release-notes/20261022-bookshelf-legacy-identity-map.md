---
category: fixed
audience: users, operators
area: migration
action: none
breaking: false
---
Converted BookshelfNG libraries now retain source author, book, edition, and file IDs in a durable map. Format-scoped reads and legacy monitoring resolve split audiobook and ebook rows, and clients can fetch mappings through `/api/v1/bookshelf/legacy-identities`.

The bounded, read-only identity lookup is also available to the restricted SeerrNG service key.
