# Updating the ChaptarrNG runtime image

This package keeps the StartOS `bookshelfng` identity and wraps ChaptarrNG's
standard ChaptarrNG image with the package's Hardcover provider mode. The
release candidate builder injects the exact multi-architecture image digest
into
`startos/manifest/index.ts`.

## Updating the package

- Build from the `startos/` source emitted in the ChaptarrNG
  `bookshelfng-container-platform-candidates` bundle using
  `packaging/scripts/build-bookshelf-startos-artifacts.sh` from the ChaptarrNG
  repository. The helper verifies the bundle checksum and emits both signed
  architectures with checksums and a release manifest.
- Keep package ID `bookshelfng`, version ordering, volume IDs, and mount paths.
- Use only the digest supplied by the ChaptarrNG release workflow; do not
  replace it with a floating tag.

- Sideload the resulting package and validate install, backup, update, health,
  and rollback before submitting an external package update.
