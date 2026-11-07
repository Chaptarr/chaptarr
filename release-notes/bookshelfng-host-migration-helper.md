---
category: added
audience: operators
area: migration
action: Use the ChaptarrNG host helper for a separately backed-up BookshelfNG migration preview and confirmed handoff.
breaking: false
---
ChaptarrNG now carries a host-side Docker helper for BookshelfNG snapshot, migration preview and apply, and restore preview and rollback. Future Linux standalone archives will include it. The helper saves a private full-appdata archive outside the source directory before each action, requires explicit provider mode for migration, and requires stopped-service confirmation before apply or restore.
