# BookshelfNG on StartOS

This wrapper preserves the existing BookshelfNG StartOS package ID and volume
contract while running ChaptarrNG's standard image in BookshelfNG
compatibility mode on x86_64 and aarch64 systems. The release handoff builder replaces the image
placeholder in `startos/manifest/index.ts` with the exact published
multi-architecture digest. The package persists the application database at
`/config`, the library at `/books`, and download files at `/downloads`.

This candidate uses StartOS SDK 3 and requires StartOS 0.4.0.2 or newer. Update
StartOS before installing it on an older device.

`icon.png` is the ChaptarrNG project logo from this repository. The daemon
explicitly enables compatibility startup, keeps port `8787`, and selects the
Hardcover provider mode used by this package candidate.

```sh
OUTPUT_DIR=/path/to/new/startos-artifacts ./build-startos-package.sh
```

The release handoff bundle supplies a digest-pinned source candidate and a
StartOS package version derived from the matching ChaptarrNG release. The
ChaptarrNG repository's
`packaging/scripts/build-bookshelf-startos-artifacts.sh` helper verifies that
bundle and builds both supported architectures inside the configured StartOS
packaging workspace. It checks the signing-key path without reading or copying
the key and writes signed `.s9pk` files, SHA-256 sidecars, and a release
manifest to a new output directory. The ChaptarrNG release builder does not
hold or use the external package signing key. Inspect each generated package,
then sideload and test install, backup, update, `/ping`, and restore to the
prior BookshelfNG image before marketplace submission.

Automatic first-start migration is enabled by default. Use the package's
**Configure BookshelfNG migration** action to defer conversion for a manual
preview and apply; the setting is saved in the StartOS backup volume. In
Manual mode the supervisor holds the service and `/ping` is unavailable until
you finish the documented migration and restart in Automatic mode.

### Manual migration

With the BookshelfNG package updated and the candidate held in Manual mode,
preview the conversion from the running service container. The preview stages
a temporary database and leaves `/config/readarr.db` unchanged:

```sh
start-cli package attach bookshelfng -- sh -lc \
  'HARDCOVER=true HARDCOVER_NATIVE=true dotnet /app/Chaptarr.dll -preview-bookshelf-migration -data=/config'
```

Review the preview report. If it passes, make sure no BookshelfNG or
ChaptarrNG application process is using the data directory and apply:

```sh
start-cli package attach bookshelfng -- sh -lc \
  'HARDCOVER=true HARDCOVER_NATIVE=true dotnet /app/Chaptarr.dll -migrate-bookshelf -confirm-bookshelf-service-stopped -data=/config'
```

If both `readarr.db` and `audioarr.db` contain data, add
`-merge-bookshelf-databases` to both commands. Automatic startup selects the
same validated merge path; conflicting identities or settings leave the
original databases intact and the package held for review.

The apply command retains a schema-158 source backup under `/config`. Then use
the package action to select Automatic and restart `bookshelfng`. The candidate
starts from the converted database and serves `/ping`. Do not start the
BookshelfNG image against the converted database; use the retained source
backup and the documented restore command if you need to return to BookshelfNG.

Application source: <https://github.com/snapetech-org/chaptarrng>

## Security scanning

Run scripts/security-scan-local.sh for local Semgrep Community Edition and applicable Trivy scans. Public GitHub Actions workflows run these scans on changes and weekly, alongside any existing CodeQL workflow. See docs/security-scanning.md for offline setup and updates. Run scripts/install-security-hooks.sh once to enable the staged Semgrep pre-commit hook.


The SDK 3 toolchain uses a scoped `@typescript-eslint/typescript-estree`
`minimatch` override to `11.0.1` for
[CVE-2026-118764](https://github.com/isaacs/minimatch/security/advisories/GHSA-g3hv-75wg-m249).
Keep the lockfile aligned with that override. Generate the package version before
running the TypeScript check: the source version placeholder is deliberately not
a valid SDK version. Fresh `npm ci`, the generated-version TypeScript check and
NCC bundle build validate this dependency repair; they do not establish installed
StartOS handoff or external repository delivery.
