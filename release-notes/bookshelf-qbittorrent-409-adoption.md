---
category: fixed
audience: operators
area: downloads
action: none
breaking: false
---
When qBittorrent reports HTTP 409 because a torrent is already present, ChaptarrNG now checks for the matching hash in its configured categories and continues with the existing download. Torrents outside those categories remain rejected.
