<div align="center">

<img src="Logo/chaptarr.png" width="160" alt="ChaptarrNG logo">

# ChaptarrNG

A Snapetech-maintained Chaptarr fork for audiobook and eBook libraries.

[![License](https://img.shields.io/github/license/snapetech/chaptarrng)](https://github.com/snapetech/chaptarrng/blob/main/LICENSE)
[![.NET 10 LTS](https://img.shields.io/badge/.NET-10%20LTS-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/platform/support/policy)

</div>

<div align="center"><a href='https://ko-fi.com/snapetech' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Support Snapetech on Ko-fi' /></a></div>

> **ChaptarrNG is beta software.** It is under active development, and breaking changes are possible. Keep regular backups and avoid pointing it at a library you cannot afford to lose.

## What is ChaptarrNG?

ChaptarrNG is Snapetech's maintained fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself an independent Readarr-derived audiobook and eBook library manager. ChaptarrNG keeps that standalone library-management experience while maintaining the request and format APIs used by SeerrNG.

We forked Chaptarr so SeerrNG can rely on a book backend with explicit ebook/audiobook routing and durable handling for imports that Chaptarr queues while preparing author metadata. Maintaining the API contract here lets SeerrNG track a request through that asynchronous work without depending on upstream changes landing first. ChaptarrNG runs on its own and does not require SeerrNG.

The application continues to identify itself as `Chaptarr` through its Readarr-compatible API so SeerrNG can detect it. **ChaptarrNG** is the name of this maintained repository, its container image, and its Unraid template.

ChaptarrNG is independently maintained and is not affiliated with the Servarr team or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects.

The backend is built on .NET 10 LTS. Docker images include the ASP.NET Core
runtime, while source builds require the .NET 10 SDK.

### What makes this fork different

- **Format-scoped requests and monitoring:** SeerrNG can configure one service for ebooks and another for audiobooks, both pointing to one ChaptarrNG instance. Adds, lookups, and monitoring preserve the selected format and book.
- **Durable pending imports:** When author metadata preparation delays an add, the API returns a pending import ID and supports reading, retrying, and cancelling that work. SeerrNG can keep a request waiting and resume it after the import completes.
- **Safe shared-request handling:** Retry and cancellation are fenced against concurrent work, so cancelling one SeerrNG request does not cancel a pending import still used by another request.
- **Provider-aware compatibility:** The fork preserves the requested media type through scoped lookups and exposes the API behavior SeerrNG needs to retain provider work and edition IDs.
- **Fork-owned releases:** Stable releases and GHCR images are built from `main` with curated fork release notes, so the maintained API changes ship together as a versioned image.

See the [SeerrNG Bookshelf backend guide](https://github.com/snapetech/seerrng/blob/main/docs/using-seerr/bookshelf-backend.md) for setup and the integration contract.

## Key Features

### Audiobooks
* **Narrator Aware** - Can help recognize and organize your books with narrator info
* **Multi-Edition Support** - Keep both audiobook and eBook versions of the same title
* **Publisher Aware** - Special handling for dramatized audiobooks and multi-part releases
* **Audio Formats** - Handles M4B, MP3 chapters, and multi-file audiobooks
* **MP3 → M4B Conversion** - Optionally convert MP3 audiobooks into a single chaptered M4B, with chapter preservation or insertion if missing (powered by [m4b-tool](https://github.com/sandreas/m4b-tool))

### Organization
* **Dual Media Libraries** - You can have separate root folders for audiobooks and eBooks or choose to have your eBooks placed alongside your audiobooks
* **Matching** - Match audiobook and eBook files using tags, folders, and release metadata
* **Series Management** - Automatically organize books by series
* **Metadata Profiles** - Control which languages are allowed in your library, so foreign and non-English content works the way you want
* **Flexible Renaming** - Customizable file naming with audiobook-specific tokens

### Integration & Automation
* **Standard *arr Integrations** - Works with the usual *arr-family download clients and indexer protocols

### Quality & Upgrades
* **Automatic Upgrades** - Replace lower quality versions automatically
* **Profile Flexibility** - Create custom quality profiles for different libraries

## Metadata

ChaptarrNG is not compatible with Readarr's metadata sources. It uses Chaptarr's modular pipeline to resolve entities across metadata providers and aggregate their data through automated refinement and consensus. Metadata work remains an ongoing effort.

## Getting Started

> **Docker is currently the only supported way to run ChaptarrNG.** The fork is developed and tested in Docker. Releases do not include native install packages; native support is not currently offered.

### Docker

Pull the image:
```bash
docker pull ghcr.io/snapetech/chaptarrng:latest
```

Run with Docker:
```bash
docker run -d \
  --name chaptarrng \
  -p 127.0.0.1:8789:8789 \
  -e PUID=1000 \
  -e PGID=1000 \
  -v /path/to/config:/config \
  -v /path/to/audiobooks:/audiobooks \
  -v /path/to/ebooks:/ebooks \
  -v /path/to/downloads:/downloads \
  --restart unless-stopped \
  ghcr.io/snapetech/chaptarrng:latest
```

Note: if `PUID`/`PGID` are not set, the image defaults to `99:100`. If `/path/to/config` doesn't exist, Docker will create it as `root:root`. Create it first (or fix ownership) so it matches `PUID`/`PGID`. Avoid setting `user:` in Compose; it bypasses the entrypoint permission setup. On Unraid, media folders commonly use `99:100`, so use `PUID=99` and `PGID=100` unless your share is owned differently. If multiple containers/users share the same media group, add `-e UMASK=002`. When testing permissions with `docker exec`, test as the app user, not root, for example: `docker exec -u 99:100 chaptarrng sh -c 'id; touch /audiobooks/.chaptarr-write-test && rm /audiobooks/.chaptarr-write-test'`.

The host port is bound to loopback by default. To expose ChaptarrNG directly on
your LAN, change the mapping to `-p 8789:8789` and restrict access with your
firewall or reverse proxy. If SeerrNG runs in another container, attach both
containers to a shared Docker network and use `http://chaptarrng:8789`; do not
use `localhost` from inside SeerrNG.

Or use Docker Compose:
```bash
wget https://raw.githubusercontent.com/snapetech/chaptarrng/main/docker-compose.yml
# Edit paths in docker-compose.yml
docker compose up -d
```

### PostgreSQL (Optional)

ChaptarrNG supports using an external PostgreSQL database instead of the default SQLite `chaptarr.db` file. To enable it, set at least `Chaptarr__Postgres__Host` (and credentials).

Environment variables:
- `Chaptarr__Postgres__Host`
- `Chaptarr__Postgres__Port` (default: `5432`)
- `Chaptarr__Postgres__User`
- `Chaptarr__Postgres__Password`
- `Chaptarr__Postgres__MainDb` (default: `chaptarr-main`)
- `Chaptarr__Postgres__LogDb` (default: `chaptarr-log`)
- `Chaptarr__Postgres__CacheDb` (default: `chaptarr-cache`)

Note: ChaptarrNG does not create PostgreSQL databases automatically; create the databases and grant the configured user access.

## Building from Source
Building from source requires the .NET 10 SDK, Node.js, and Yarn. When running ChaptarrNG natively, install FFmpeg for your operating system and make sure both `ffmpeg` and `ffprobe` are available on the `PATH` used to start the application. Normal source builds do not bundle them automatically; the ChaptarrNG Docker image already includes them.

**Linux / macOS:**
```bash
# Clone the repository
git clone https://github.com/snapetech/chaptarrng.git
cd chaptarrng

# Build the backend
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish

# Build the frontend
yarn install
yarn build
cp -r _output/UI _output/publish/UI

# Run ChaptarrNG
dotnet _output/publish/Chaptarr.dll
```

**Windows (Command Prompt):**
```cmd
:: Clone the repository
git clone https://github.com/snapetech/chaptarrng.git
cd chaptarrng

:: Build the backend
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish

:: Build the frontend
yarn install
yarn build
xcopy _output\UI _output\publish\UI /E /I

:: Run ChaptarrNG
dotnet _output/publish/Chaptarr.Console.dll
```

Note: the inherited assembly is named `Chaptarr.dll` on Linux/macOS and `Chaptarr.Console.dll` on Windows.

### Unraid Community Applications

ChaptarrNG's Unraid template is maintained in the dedicated
[ChaptarrNG Unraid package repository](https://github.com/snapetech/chaptarrng-unraid).
It installs the maintained fork image, maps the web interface on port `8789`,
and provides separate paths for appdata, audiobooks, ebooks, and downloads.
The image uses `PUID=99` and `PGID=100` by default; change those values to match
the ownership of your media shares.

To make ChaptarrNG searchable in Community Applications, submit the package
repository URL `https://github.com/snapetech/chaptarrng-unraid` through
Unraid's Community Apps submission flow and complete its validation scan.
Stable releases from the application repository's `main` branch publish the
`latest` image to GHCR.


## Documentation

- Default ChaptarrNG web UI: http://localhost:8789
- Default username/password: Set on first launch
- [SeerrNG Bookshelf backend guide](https://github.com/snapetech/seerrng/blob/main/docs/using-seerr/bookshelf-backend.md): setup and request lifecycle
- [API identity and lifecycle](docs/API_IDENTITY_AND_LIFECYCLE.md): compatibility details for maintainers

## Contributing

ChaptarrNG is a community project, and we welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Bug Reports & Features

Found a bug or have a feature request? Please open an issue on GitHub with:
- Clear description of the issue/feature
- Steps to reproduce (for bugs)
- Logs if applicable
- Your environment details

## Security

ChaptarrNG inherits the Readarr-derived operational model and security work from Chaptarr. Its current protections include:

- Constant-time API-key comparison
- Login brute-force throttling
- Inbound security headers including HSTS, CSP, X-Frame-Options, X-Content-Type-Options, and Referrer-Policy
- Image proxy target validation before fetching remote images
- API responses redact provider secrets
- No analytics or crash reporting is enabled
- Optional passphrase-encrypted Quickstart Settings Backups
- Optional authenticated encryption for full backup archives
- Update binaries are verified by SHA256 before install

CI runs CodeQL and Trivy dependency/secret scans. Published container images
include a BuildKit SBOM and provenance metadata, plus a GitHub build-provenance
attestation. The Compose example also enables `no-new-privileges` and binds
the published web port to loopback by default.

Use the `X-Api-Key` header for API clients. ChaptarrNG still accepts the legacy
`apikey` query parameter for compatibility, so reverse proxies should redact
that parameter from access logs.

Traditional full backups contain the database and config file, including credentials. Without the setting below they are stored as plain ZIP files; protect them as secrets.

To encrypt scheduled, update, and manual full backups, store a randomly
generated passphrase with at least 16 non-whitespace characters in a secret
file, mount it read-only in the ChaptarrNG container, and set
`CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE` to its in-container path (for example,
`/run/secrets/chaptarr_backup_passphrase`). The Compose example includes a
commented Docker secret setup. Backups are then written as authenticated,
chunk-encrypted `.zip.enc` files; existing `.zip` backups remain restorable.
Mount the same secret file on a replacement instance before restoring an
encrypted backup. Keep the secret outside `Config.xml` and the backup archive;
losing it makes those encrypted backups unrecoverable. Enabling encryption does
not convert existing `.zip` backups, so protect or remove those separately.
With the file path unset, new backups retain the legacy plain ZIP format.
Protect the secret file through your container secret manager, and retain old
secret files if you need to restore archives made with them.
On POSIX systems, backup staging, temporary archives, and restore extraction
directories are restricted to the application owner while the files are in use.

The Compose example publishes its web port on `127.0.0.1` by default. Set
`CHAPTARRNG_BIND_ADDRESS=0.0.0.0` in the host environment to publish it on all
host interfaces, or use a shared Docker network for container-to-container
access without publishing the port.

## Privacy

ChaptarrNG uses `api2.chaptarr.com` for metadata and matching. Metadata requests may include provider IDs, search text, media type, selected audio/eBook tags, and the file name being matched. Full file paths, user identity, and indexer or download-client credentials are not sent. Update checks send version, OS, architecture, and runtime information.

Please see [SECURITY.md](SECURITY.md) for reporting security vulnerabilities.

## Acknowledgments

ChaptarrNG is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself an independent fork of [Readarr](https://github.com/Readarr/Readarr), from the [Servarr](https://wiki.servarr.com/) project family. This project builds on work by Chaptarr, Readarr, and the Servarr contributors to Readarr, Sonarr, Radarr, Lidarr, and Prowlarr. Thank you.

Audiobook conversion is powered by [m4b-tool](https://github.com/sandreas/m4b-tool) (by sandreas), which builds on [FFmpeg](https://ffmpeg.org/) and [mp4v2](https://github.com/enzo1982/mp4v2). These are bundled in the ChaptarrNG Docker image under their respective licenses.

See [COPYRIGHT.md](COPYRIGHT.md) for full attribution and copyright details.

## Disclaimer

ChaptarrNG is independently maintained. It is **not affiliated with, endorsed by, or supported by** the Servarr team or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects — please don't send ChaptarrNG support requests their way.

ChaptarrNG is provided under the GNU GPL v3 with no warranty (see the [License](#license) section below).

## AI Development Disclosure

ChaptarrNG is developed and maintained with the assistance of AI tools.

## License

- [GNU GPL v3](https://www.gnu.org/licenses/gpl.html)
- Copyright © 2026 Snapetech and SeerrNG contributors for fork-specific changes
- Portions copyright © 2010–2026 the Servarr team and contributors

See the [LICENSE](LICENSE) file for the full license text and [COPYRIGHT.md](COPYRIGHT.md) for attribution details.
