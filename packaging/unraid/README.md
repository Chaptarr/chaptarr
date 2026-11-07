# ChaptarrNG for Unraid

This repository contains ChaptarrNG's standalone Unraid Community Applications
package: its repository profile and Docker template. The application source,
releases, and GHCR images are maintained in
[ChaptarrNG](https://github.com/snapetech-org/chaptarrng).

ChaptarrNG is Snapetech's maintained Chaptarr fork for ebook and audiobook
libraries, built on **.NET 10 LTS**. Its SeerrNG-specific changes add
format-scoped book requests and monitoring, durable tracking for imports delayed
by author metadata preparation, and request-safe retry and cancellation.
ChaptarrNG remains a standalone book manager; SeerrNG is optional.

## Submit to Community Applications

Submit the main ChaptarrNG repository in the
[Unraid Community Apps portal](https://ca.unraid.net/submit):

`https://github.com/snapetech-org/chaptarrng`

The portal reads `ca_profile.xml` from the repository root and the template
from `templates/chaptarrng.xml`. Run Validate and Scan in the portal before
submitting. This companion repository remains available as a manual template
mirror; it is not the Community Apps submission target.

## Manual installation

Use the main repository's [ChaptarrNG template XML](https://github.com/snapetech-org/chaptarrng/blob/main/templates/chaptarrng.xml) with
Unraid's Docker template workflow. It installs `ghcr.io/snapetech-org/chaptarrng:latest`,
which stable releases keep in sync with `:main`, and configures persistent
appdata, audiobook, ebook, and download paths.

- Web UI: port `8789`
- Appdata: `/mnt/user/appdata/chaptarrng` mapped to `/config`
- Audiobooks: `/mnt/user/media/audiobooks` mapped to `/audiobooks`
- eBooks: `/mnt/user/media/ebooks` mapped to `/ebooks`
- Downloads: `/mnt/user/downloads` mapped to `/downloads`
- Default file ownership: `PUID=99`, `PGID=100`

Set the mapped paths and ownership to match your Unraid shares. For app
configuration and SeerrNG integration, see the
[ChaptarrNG README](https://github.com/snapetech-org/chaptarrng#readme) and the
[SeerrNG Bookshelf backend guide](https://github.com/snapetech-org/seerrng/blob/main/docs/using-seerr/bookshelf-backend.md).

For builds that include the current main HTTP controls, see the
[HTTP security settings](https://github.com/snapetech-org/chaptarrng/blob/main/docs/HTTP_SECURITY.md). These controls are not
available in the published `v0.9.977` image.

## BookshelfNG migration status

ChaptarrNG is the intended destination for BookshelfNG, but the migration and
Unraid handoff are still release-frozen. BookshelfNG `0.4.21.71` and ChaptarrNG
`v0.9.977` remain the latest public releases. Keep BookshelfNG running; do not
point ChaptarrNG at its `/config` directory or apply the published `.977`
converter to BookshelfNG appdata.

The current ChaptarrNG source candidate is
[`f30dcec8`](https://github.com/snapetech-org/chaptarrng/commit/f30dcec8c882c0169a544eee337f1d628e76e5d4).
Exact-head Build
[`39520269403`](https://github.com/snapetech-org/chaptarrng/actions/runs/39520269403)
passed Linux, Windows, and macOS build/test jobs, PostgreSQL migration smoke,
and native-runner receipt reconciliation. Security Analysis, Semgrep CE,
Trivy, and NuGet submission passed. No CodeQL run is attached to this source
commit; the release-note contract was skipped on the main push. Local frontend
tests pass 57/57, and typecheck, translation-key validation, and changed CSS
lint pass. Current
source includes a preview/confirm flow for missing author roots, safe profile
reset controls, local-add edition preservation, numeric Hardcover/Goodreads
facade lookup, and row-format-aware author editing. It is not in the published
image. BookshelfNG issue
[#144](https://github.com/snapetech-org/bookshelfng/issues/144) remains open,
and the legacy Unraid handoff [PR #5](https://github.com/snapetech-org/bookshelfng-unraid/pull/5)
is still a draft. Community Applications update/rollback and
representative-data migration/rollback acceptance remain outstanding. Follow
the [migration guide](https://github.com/snapetech-org/chaptarrng/blob/main/docs/MIGRATE_FROM_BOOKSHELFNG.md) for the supported
schema, backup, preview, apply, and rollback limits.

## Source and support

- [ChaptarrNG application source](https://github.com/snapetech-org/chaptarrng)
- [Unraid package support and SeerrNG integration](https://github.com/snapetech-org/seerrng/issues)

## Security scanning

The companion repository runs Semgrep Community Edition and Trivy workflows
against its shell scripts and GitHub Actions configuration. Run the same local
checks with `./scripts/security-scan-local.sh`. There are no application
dependency manifests in this checkout, so Trivy does not run its dependency
vulnerability scanner. See the
[security scanning guide](docs/security-scanning.md).

This template repository is distributed under GPLv3; see [LICENSE](LICENSE).
