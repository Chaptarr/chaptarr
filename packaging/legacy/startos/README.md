# StartOS BookshelfNG identity handoff

`candidate/` contains the StartOS wrapper source for the existing BookshelfNG
package identity. It keeps package ID `bookshelfng`, volume IDs `main`,
`books`, and `downloads`, the `/config`, `/books`, and `/downloads` mounts,
the upstream image entrypoint and command, port `8787`, and Hardcover
compatibility mode while moving the runtime to ChaptarrNG. The SDK inherits the
image entrypoint with `sdk.useEntrypoint()`; it does not launch `/init` as a
child process.

The release candidate builder copies this source into the platform handoff
bundle and replaces its image placeholder with the exact ChaptarrNG
multi-architecture Hardcover digest and sets the StartOS package version to
the matching ChaptarrNG release plus revision `:1`. Do not build or publish
the source template directly. Build and sign the generated candidate inside the
external maintainer's StartOS packaging workspace; this repository does not
hold that workspace key. The external StartOS package repository is not
modified by this candidate. Synthetic x86_64 and aarch64 test-package updates
passed in local StartOS VMs, including Manual defer, Automatic conversion, and
automatic source rollback on ARM64. The ARM64 predecessor used a direct
Readarr service wrapper around the pinned BookshelfNG 0.4.21.37 image because
the public image's s6 `/init` requires PID 1. These tests do not validate the
official signed package, update from the public `.71` StartOS package, external
repository delivery, or package-manager rollback.

## Manual migration

The **Configure BookshelfNG migration** package action defaults to Automatic.
Manual leaves the candidate service held and its `/ping` unavailable until you
preview and apply the migration, then switch the setting back to Automatic and
restart the package. While Manual is holding the service, run the preview and
apply commands from the
[candidate README](candidate/README.md#manual-migration). Apply only after the
preview passes and BookshelfNG is stopped.

To turn a released, digest-pinned handoff bundle into signed packages without
manually unpacking it, provide the bundle and its `.sha256` sidecar, point at
the existing StartOS packaging workspace, and choose a new output directory:

```sh
STARTOS_WORKSPACE=/path/to/startos-workspace \
CANDIDATE_BUNDLE=/path/to/ChaptarrNG-0.9.978-bookshelfng-container-platform-candidates.tar.gz \
OUTPUT_DIR=/path/to/new/startos-artifacts \
./packaging/scripts/build-bookshelf-startos-artifacts.sh
```

The helper checks the release bundle checksum and package identity, extracts
only the StartOS source, builds the signed x86_64 and aarch64 `.s9pk` files,
and writes package checksums plus a release manifest. It uses the existing
workspace signing key without reading or copying its contents. It does not
submit packages to the external repository or install them on a device.
