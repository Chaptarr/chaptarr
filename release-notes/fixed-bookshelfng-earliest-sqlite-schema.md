---
category: fixed
audience: users, operators
area: bookshelf
action: none
breaking: false
---
ChaptarrNG now accepts BookshelfNG SQLite databases that still use the historical `Blacklist` table name. The source is upgraded on a temporary copy, and the untouched database remains available for rollback.
