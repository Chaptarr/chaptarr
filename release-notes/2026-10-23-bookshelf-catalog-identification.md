---
category: fixed
audience: users, operators
area: migration
action: none
breaking: false
---
Routine matching now searches the configured catalog after local and V5 misses, using identifiers, author/title, title, a single-file filename fallback, then author-only search. Search stops at the first tier with results. Only one exact media type and stable author/work identity produces a reviewable suggestion; scoped imports cannot broaden.
