# Changelog

ChaptarrNG release notes highlight user-facing changes and operating guidance.
Each release combines curated entries from release-notes/ with technical
history grouped from conventional commits.

## [0.9.938](https://github.com/snapetech/chaptarrng/compare/v0.9.937..v0.9.938) - 2026-09-29

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

