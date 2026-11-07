---
category: changed
audience: users, operators
area: migration
action: none
breaking: false
---
Existing `Readarr__...` and `Audioarr__...` environment settings are carried into Chaptarr configuration when the matching `Chaptarr__...` value is unset. Explicit Chaptarr values take precedence per setting. A saved BookshelfNG `MetadataSource` URL migrates to ChaptarrNG `MetadataServerUrl`, and the legacy `METADATA_URL` supplies it when no saved Chaptarr URL exists; neither setting switches the primary provider. The optional Direct Download browser fallback retains `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH`. The BookshelfNG-compatible container aliases also preserve custom legacy server ports, and the health check follows legacy port and URL-base settings.
