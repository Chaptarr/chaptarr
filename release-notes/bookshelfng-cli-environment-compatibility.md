---
category: changed
audience: users, operators
area: migration
action: none
breaking: false
---
The unmapped-file retry helper accepts the existing `BOOKSHELF_URL` and `BOOKSHELF_API_KEY` environment variables during migration. `CHAPTARR_URL` and `CHAPTARR_API_KEY` take precedence when both are set.
