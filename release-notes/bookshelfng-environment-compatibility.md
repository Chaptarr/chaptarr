---
category: changed
audience: users, operators
area: migration
action: none
breaking: false
---
Legacy `Readarr__...` and `Audioarr__...` environment values fill unset Chaptarr settings, while explicit Chaptarr values win. Metadata URLs, the optional Playwright browser path, custom server ports, and URL-base health checks carry forward without changing the selected primary metadata provider.
