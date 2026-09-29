# Maintained branch reconciliation — 2026-09-28

ChaptarrNG's maintained source and stable release line are consolidated on
`main`.

| Branch | Outcome |
| --- | --- |
| `origin/main` | Its newer scoped book lookup media-type fix is retained in the final merge. |
| `origin/develop` | Its full application history and the fork-owned release/image workflow are merged into `main`. This was the active development line, not an experiment; no code from it was discarded. The branch is deleted after the completed push. |

The repository default branch is set to `main`. The former develop prerelease
channel is retired: CI, release validation, Docker publication, the update
selector, and update checks now use the stable fork release on `main`. Existing
`develop`, `master`, and `nightly` updater settings are mapped to `main`, so
installations following the old channel continue receiving fork releases.

The update client now reads releases from `snapetech/chaptarrng`; the old
`chaptarr/chaptarr` owner and develop-branch asset URLs are removed. Copyright,
license, and contributor attribution to Readarr and the Servarr family remain
in place. No upstream Git remote is configured.

GitHub SourceLink now uses `10.0.400` instead of the vulnerable `8.0.0` build
package. This also lets restore complete with the current .NET SDK's NuGet
audit enabled.
