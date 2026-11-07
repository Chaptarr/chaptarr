# BookshelfNG legacy distribution candidates

These files are source candidates for existing BookshelfNG identities. They
preserve desktop IDs, Cloudron app identity, container names, service ports,
data directories, and named volumes where the channel allows an in-place
handoff. They do not publish packages, change external catalogs, or approve
operator migration.

The AppImage and Flatpak launchers now use `desktop/bookshelfng-supervisor.sh`.
It records first-start state, checks the configured local `/ping` and migrated
database, and restores the BookshelfNG source data after a failed or interrupted
startup. A failed cutover requires reopening the old artifact: the AppImage
file or Flatpak deployment is not replaced by the data supervisor. These
channel recovery records also write their result and channel identity into the
shared migration journal in the data directory. The desktop artifacts remain
source candidates until the bulk failure/restart acceptance is complete.

The Chocolatey `bookshelfng` upgrade also arms a SYSTEM scheduled task when the
existing service is stopped. On its next start it checks the configured local
health URL and migrated database; failure restores the prior app, data, NSSM
arguments/settings, both environment blocks, and start mode when safe. Its
recovery task unregisters itself after a healthy handoff, rollback, or recorded
intervention, and records its outcome in the shared migration journal. Its
PostgreSQL credentials and task-trigger behavior still need installed Windows
acceptance.

The container recipes use the single standard ChaptarrNG image. The release
workflow pins each generated platform bundle to that release's verified
multi-architecture digest; there is no provider-specific ChaptarrNG image.
Every applicable channel still needs its own install, update, backup, and
rollback acceptance before redirecting users. Preserve the old
`HARDCOVER=true` or `false` provider setting in the channel configuration; the
standard image cannot infer it from an old tag. AppImage and
Flatpak candidates use the verified ChaptarrNG Linux x64 standalone archive
staged by `packaging/scripts/prepare-bookshelf-desktop-candidates.sh`:

```sh
bash packaging/scripts/prepare-bookshelf-desktop-candidates.sh \
  "$RUNNER_TEMP/ChaptarrNG-${VERSION}-linux-x64.tar.gz" \
  "$RUNNER_TEMP/bookshelfng-desktop-candidates"
```

The staging helper requires the archive's adjacent `.sha256` sidecar, rejects
archive links and unsafe paths, and refuses to overwrite its output directory
or accept ChaptarrNG `v0.9.977`, whose published converter predates native
Hardcover identity handling. Use version `0.9.978` or newer. To build both
packages locally, install `appimagetool`, `flatpak-builder`, and the Flatpak
26.08 runtime and SDK, then run:

```sh
bash packaging/scripts/build-bookshelf-desktop-candidates.sh \
  "$RUNNER_TEMP/ChaptarrNG-${VERSION}-linux-x64.tar.gz" \
  "$RUNNER_TEMP/bookshelfng-desktop-candidate-build"
```

The builder writes checksummed AppImage and Flatpak artifacts plus a local
Flatpak repository. It does not install or publish the bundle. The runtime and
SDK must already be installed; the builder does not change the host's Flatpak
installation. The gated stable-release workflow now builds both candidates
from the checksum-verified Linux release archive, using a pinned and
checksum-verified AppImage builder and runtime, and attaches the checksummed
files to the release only when the ChaptarrNG readiness, BookshelfNG
retirement, and migration-bridge gates are all enabled. This creates
version-matched candidates under the existing desktop identities; it does not
publish an AppImage update feed or a signed Flatpak remote.

The same gated release also packages the Cloudron, Co-op Cloud, and generic
self-hosting Compose candidates with the exact multi-architecture standard
ChaptarrNG image digest produced by that release's Docker build.
Cloudron's app ID and persistent directories, Co-op Cloud's stack and volumes,
and Compose's container and volumes stay unchanged. The bundle is named
ChaptarrNG-<version>-bookshelfng-container-platform-candidates.tar.gz and has a
SHA-256 sidecar; the build script rejects missing or malformed image digests.
These sources still need their respective platform maintainers and real
install, update, backup, and rollback acceptance.

StartOS package code lives in the separate `bookshelfng-startos` repository.
The StartOS note here documents its package and image contract; it does not
replace that external package update or device validation.
