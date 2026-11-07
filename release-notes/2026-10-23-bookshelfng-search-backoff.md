---
category: fixed
audience: users, operators
area: import
action: none
breaking: false
---
Unmapped files with a successful catalog search are no longer searched again on every unchanged scan. ChaptarrNG preserves this state when migrating from BookshelfNG, retries after a failed provider search or changed file, and clears it when a file is linked.
