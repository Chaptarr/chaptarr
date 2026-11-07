---
category: fixed
audience: users, operators
area: migration
action: none
breaking: false
---
ChaptarrNG can restore a supported BookshelfNG database from a legacy ZIP backup. It validates and stages the database, then normal startup converts it and keeps the original BookshelfNG database as the rollback source. The source archive is left untouched.
