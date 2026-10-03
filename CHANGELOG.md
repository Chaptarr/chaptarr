# Changelog

ChaptarrNG release notes highlight user-facing changes and operating guidance.
Each release combines curated entries from release-notes/ with technical
history grouped from conventional commits.

## [0.9.940](https://github.com/snapetech/chaptarrng/compare/v0.9.939..v0.9.940) - 2026-10-03

### User-facing changes

#### Added

- **Integrations:** ChaptarrNG now exposes a versioned system capabilities endpoint that reports the Bookshelf API's provider-ID dialect and supported media types to compatible request managers such as SeerrNG.

#### Security

- **Backups:** Full backups can now be stored as authenticated encrypted archives when a backup passphrase is configured, while existing ZIP backups remain restorable.
  - **Action required:** Mount a secret file containing a long random passphrase and set CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE to its in-container path. Protect or remove existing plain ZIP backups separately.

### Features
- Add SeerrNG capability discovery and encrypted backups - ([865e0b1](https://github.com/snapetech/chaptarrng/commit/865e0b14b6b26a4eeda8d8ce381d94ed4dba4346))

### Bug Fixes
- *(ci)* Rely on repository default CodeQL setup - ([319bceb](https://github.com/snapetech/chaptarrng/commit/319bcebbb3ec43b4558c737a8bcb9fd77ddea31c))

## [0.9.939](https://github.com/snapetech/chaptarrng/compare/v0.9.938..v0.9.939) - 2026-10-01

### User-facing changes

#### Added

- **Distribution:** ChaptarrNG now offers amd64 and arm64 packages for YunoHost, giving operators a native installation path alongside its existing container and Unraid distributions.
  - **Action required:** Run sudo yunohost app install https://github.com/YunoHost-Apps/chaptarrng_ynh --debug.

#### Fixed

- **App Experience:** NG-branded icons and bundled frontend assets refresh the app's presentation and downloads. Cookie-authenticated users can open the web UI, and unusually long Transmission ETAs no longer break queue responses.

### Features
- *(release)* Add changelog and Discord announcements (#39) - ([65887a0](https://github.com/snapetech/chaptarrng/commit/65887a0963626d9bb2545a2c9919208ea0ad0a49))
- Improve package branding and downloads - ([13d5e53](https://github.com/snapetech/chaptarrng/commit/13d5e53c6089ba092a5d437f8e6b2b4747653735))
- Publish pending ChaptarrNG updates - ([1171a87](https://github.com/snapetech/chaptarrng/commit/1171a870bf1f44dac799eae53f44507562ed78f8))

### Bug Fixes
- *(security)* Enumerate only fixture test files - ([4d60eba](https://github.com/snapetech/chaptarrng/commit/4d60eba5e02c72646a0bc144d6bf4b74679e1bce))
- *(security)* Constrain test fixture file paths - ([14dfae5](https://github.com/snapetech/chaptarrng/commit/14dfae526a54fb3c742bf116927be2afda60f859))
- Align OpenAPI security scopes with new type - ([9c2d5fe](https://github.com/snapetech/chaptarrng/commit/9c2d5fe93d0157e10e45debc0809a8e4801377bb))
- Migrate built-in FluentValidation calls to v12 - ([53a09f4](https://github.com/snapetech/chaptarrng/commit/53a09f4ce70f216ccf1223efa4ac462b3e57a256))
- Migrate configuration change handler for NLog 6 - ([7f8e9f2](https://github.com/snapetech/chaptarrng/commit/7f8e9f2edabefdb5789ac6d790035255e2fde3c0))
- Resolve CI compatibility blockers - ([accc257](https://github.com/snapetech/chaptarrng/commit/accc257c8938981b20fa276340d2afc19d393b3a))

### Maintenance
- Update security dependencies and compatibility - ([63afb7f](https://github.com/snapetech/chaptarrng/commit/63afb7f6b309003fc7d53f8a3772c3a1d271968f))## [0.9.938](https://github.com/snapetech/chaptarrng/compare/v0.9.937..v0.9.938) - 2026-09-29

### Changed

- **Distribution:** ChaptarrNG's Unraid setup now points to its dedicated package repository, keeping the application release and Community Apps listing independently maintained.

## [0.9.937](https://github.com/snapetech/chaptarrng/compare/v0.9.936..v0.9.937) - 2026-09-29

### Added

- **Books:** Scoped book lookups now retain the requested ebook or audiobook format through metadata searches, so SeerrNG receives matching catalog results.
- **Distribution:** The release adds ChaptarrNG's maintained fork identity and Unraid template.

## [0.9.936](https://github.com/snapetech/chaptarrng/releases/tag/v0.9.936) - 2026-09-29

### Added

- **Books:** ChaptarrNG adds format-scoped requests and a durable pending-import lifecycle, allowing SeerrNG to resume requests after author metadata preparation and safely share queued work.
- **Identity:** ChaptarrNG is a standalone book manager that keeps the Chaptarr API identity for Readarr-compatible clients.
