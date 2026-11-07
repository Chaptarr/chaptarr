---
category: security
audience: users, operators
area: authentication
action: none
breaking: false
---
Query API keys stay enabled by default; set `CHAPTARR__AUTH__ALLOWAPIKEYQUERY=false` to block them. `BOOKSHELF_ALLOW_API_KEY_QUERY` and `BOOKSHELF_SEERRNG_API_KEY` remain supported. Restricted SeerrNG keys cannot read configuration or health routes, and file/history reads must target one book. Header and SignalR authentication are unchanged.
