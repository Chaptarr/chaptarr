# BookshelfNG localization wire compatibility

These 45 catalogs are copied without editing from BookshelfNG v0.4.21.76,
commit f512a17f866d1fa8e5d92ae792d044de1fb45535. The original source and license
are retained in the project history; source-manifest.json records every file
SHA-256. They are compatibility data, not the native UI translation catalogs.

The migrated legacy API overlays this predecessor dictionary on a copy of the
native dictionary, retaining source keys/values plus additive Chaptarr keys.
Native requests and current login/bootstrap requests use the native catalogs.
Do not run native translation generation or rebranding over these fixed data.
Update them only for an explicitly selected new predecessor baseline.
