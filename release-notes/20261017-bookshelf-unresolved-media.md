---
category: changed
audience: users, operators
area: migration
action: Review the generated media review file and choose the correct format before enabling monitoring for unresolved books.
breaking: false
---

BookshelfNG migration keeps format-unknown books in both ChaptarrNG catalogs, unmonitored, and writes a private review file. Startup logs show its path. Conversion can continue without speculative searches; operators can classify records before or after startup. One selected audiobook and ebook edition transfer separately. Same-format conflicts stop conversion. No media files move.
