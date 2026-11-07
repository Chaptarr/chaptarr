---
category: fixed
audience: users
area: naming
action: none
breaking: false
---

Audio MediaInfo naming tokens now use available file metadata and leave unknown values empty. Audiobook imports retain extracted audio details, and older files can be inspected on demand, so an unavailable bitrate no longer inserts a misleading `0 kbps` segment.
