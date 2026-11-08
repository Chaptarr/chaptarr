---
category: added
audience: users, operators
area: bookshelf
action: none
breaking: false
---
ChaptarrNG now upgrades supported older BookshelfNG SQLite schemas on a temporary copy before conversion. The original database stays available for rollback, and preview leaves it unchanged. Newer schemas and PostgreSQL sources still fail closed.
