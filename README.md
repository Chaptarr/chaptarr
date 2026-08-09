<div align="center">

<img src="Logo/chaptarr.png" width="160" alt="Chaptarr logo">

# Chaptarr

A book collection manager for audiobooks and eBooks.

[![Discord](https://img.shields.io/discord/1376676460647022752?logo=discord&logoColor=white&label=Discord)](https://discord.gg/G9ZbgWS5rp)
[![License](https://img.shields.io/github/license/Chaptarr/chaptarr)](https://github.com/Chaptarr/chaptarr/blob/main/LICENSE)

</div>

<div align="center"><a href='https://ko-fi.com/chaptarr' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a></div>

> **Chaptarr is beta software.** It is under active development. Bugs are likely; breaking changes less so, but still possible. Full disclosure: the last data loss event was in pre-alpha, with a tester group of roughly 30 people, about 12 months ago. Over the last 6 months we've grown past 11,000 active users with no reported data loss events. Still, follow good backup practice and avoid pointing it at a library you can't afford to lose.

## What is Chaptarr?

Chaptarr is a Readarr fork built to accommodate audiobook and eBook libraries in one instance. It helps you organize and maintain a collection of books with rich metadata, including narrator and edition information that many general-purpose media managers struggle to track, or don't try to at all.

Chaptarr is an independent project. It is **not affiliated with the Servarr team** or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects.

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

Chaptarr is not compatible with Readarr's metadata sources. It uses its own modular pipeline that resolves entities across metadata providers and aggregates their data through automated refinement and consensus. As anyone in the book metadata space would expect, this is an ongoing effort to deliver the best, most accurate data possible. It is the core of our mission.

## Getting Started

> **Docker is currently the only supported way to run Chaptarr.** Chaptarr is developed and tested in Docker. Releases do not include native install packages; earlier zip attachments were incomplete build artifacts and have been withdrawn. Native support (starting with an experimental Windows build) is being worked on.

### Docker

Pull the image:
```bash
docker pull chaptarr/chaptarr:latest
```

Run with Docker:
```bash
docker run -d \
  --name chaptarr \
  -p 8789:8789 \
  -e PUID=1000 \
  -e PGID=1000 \
  -v /path/to/config:/config \
  -v /path/to/audiobooks:/audiobooks \
  -v /path/to/ebooks:/ebooks \
  -v /path/to/downloads:/downloads \
  --restart unless-stopped \
  chaptarr/chaptarr:latest
```

Note: if `PUID`/`PGID` are not set, the image defaults to `99:100`. If `/path/to/config` doesn't exist, Docker will create it as `root:root`. Create it first (or fix ownership) so it matches `PUID`/`PGID`. Avoid setting `user:` in Compose; it bypasses the entrypoint permission setup. On Unraid, media folders commonly use `99:100`, so use `PUID=99` and `PGID=100` unless your share is owned differently. If multiple containers/users share the same media group, add `-e UMASK=002`. When testing permissions with `docker exec`, test as the app user, not root, for example: `docker exec -u 99:100 chaptarr sh -c 'id; touch /audiobooks/.chaptarr-write-test && rm /audiobooks/.chaptarr-write-test'`.

Or use Docker Compose:
```bash
wget https://raw.githubusercontent.com/chaptarr/chaptarr/develop/docker-compose.yml
# Edit paths in docker-compose.yml
docker compose up -d
```

### PostgreSQL (Optional)

Chaptarr supports using an external PostgreSQL database instead of the default SQLite `chaptarr.db` file. To enable it, set at least `Chaptarr__Postgres__Host` (and credentials).

Environment variables:
- `Chaptarr__Postgres__Host`
- `Chaptarr__Postgres__Port` (default: `5432`)
- `Chaptarr__Postgres__User`
- `Chaptarr__Postgres__Password`
- `Chaptarr__Postgres__MainDb` (default: `chaptarr-main`)
- `Chaptarr__Postgres__LogDb` (default: `chaptarr-log`)
- `Chaptarr__Postgres__CacheDb` (default: `chaptarr-cache`)

Note: Chaptarr does not create PostgreSQL databases automatically; create the databases and grant the configured user access.

### Testing from Unraid
This process requires the "Docker Compose Manager" plugin.

1. Open the Unraid terminal and type:
   ```bash
   cd /YOUR/APPDATA/PATH/HERE
   ```
   For example:
   ```bash
   cd /mnt/user/appdata/
   ```
2. Create the appdata folder:
   ```bash
   mkdir Chaptarr
   ```
3. Clone the repository:
   ```bash
   git clone https://github.com/chaptarr/chaptarr.git
   ```

#### After cloning Git repository
1. Now go to Docker tab and then the Compose tab in Unraid
2. Make a new stack
3. Choose "Chaptarr" as stack name
4. Click Advanced, enter the folder path that leads to Chaptarr, for example `/mnt/user/appdata/Chaptarr/`, and click "OK"
5. Click the gear icon next to the Chaptarr Stack -> "Edit Stack" -> Compose File
6. Replace the uncommented top section with the following, using your real media paths, for example `/mnt/user/YOURAUDIOBOOKSHERE:/audiobooks`
``` yaml
services:
  chaptarr:
    build:
      context: ./
      dockerfile: Dockerfile.build
    container_name: chaptarr
    network_mode: bridge
    environment:
      - PUID=99
      - PGID=100
      - UMASK=002
      - TZ=America/New_York  # Change to your timezone
    volumes:
      - /mnt/user/appdata/Chaptarr:/config
      - /mnt/user/data/media/books/audiobooks:/audiobooks
      - /mnt/user/data/media/books/ebooks:/ebooks
      - /mnt/user/data:/downloads
    ports:
      - 8789:8789
    restart: unless-stopped 
```
Paths are case sensitive.

7. Save the compose file. Leave the generated UI fields empty, then click Compose Up.

Chaptarr should now be available at the IP address of your Unraid server on port 8789, for example `http://192.168.xxx.xxx:8789/`.

If you experience an error, check folder ownership and permissions, then run compose down and rebuild Chaptarr.

### Building from Source
Building from source requires the .NET 10 SDK, Node.js, and Yarn.

```bash
# Clone the repository
git clone https://github.com/chaptarr/chaptarr.git
cd chaptarr

# Build the backend
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -o _output/publish

# Build the frontend
yarn install
yarn build
cp -r _output/UI _output/publish/UI

# Run Chaptarr
dotnet _output/publish/Chaptarr.dll
```

## Documentation

- Default web UI: http://localhost:8789
- Default username/password: Set on first launch

## Contributing

Chaptarr is a community project, and we welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Bug Reports & Features

Found a bug or have a feature request? Please open an issue on GitHub with:
- Clear description of the issue/feature
- Steps to reproduce (for bugs)
- Logs if applicable
- Your environment details

## Security

Chaptarr is a fork of Readarr, so much of the operational model is familiar to *arr users. A few areas have been updated:

- Constant-time API-key comparison
- Login brute-force throttling
- Inbound security headers including HSTS, CSP, X-Frame-Options, X-Content-Type-Options, and Referrer-Policy
- Image proxy target validation before fetching remote images
- API responses redact provider secrets
- No analytics or crash reporting is enabled
- Optional passphrase-encrypted Quickstart Settings Backups
- Update binaries are verified by SHA256 before install

Traditional full backups contain the database and config file, including credentials, in an unencrypted zip. Do not share them or upload them to public cloud storage unless you have encrypted them separately.

## Privacy

Chaptarr uses `api2.chaptarr.com` for metadata and matching. Metadata requests may include provider IDs, search text, media type, selected audio/eBook tags, and the file name being matched. Full file paths, user identity, and indexer or download-client credentials are not sent. Update checks send version, OS, architecture, and runtime information.

Please see [SECURITY.md](SECURITY.md) for reporting security vulnerabilities.

## Acknowledgments

Chaptarr is an independent fork of [Readarr](https://github.com/Readarr/Readarr), an application from the [Servarr](https://wiki.servarr.com/) project family. It builds on years of work by the Servarr team and the contributors to Readarr, Sonarr, Radarr, Lidarr, and Prowlarr, whose shared codebase makes this project possible. Thank you.

Audiobook conversion is powered by [m4b-tool](https://github.com/sandreas/m4b-tool) (by sandreas), which builds on [FFmpeg](https://ffmpeg.org/) and [mp4v2](https://github.com/enzo1982/mp4v2). These are bundled in the official Chaptarr Docker image under their respective licenses.

See [COPYRIGHT.md](COPYRIGHT.md) for full attribution and copyright details.

## Disclaimer

Chaptarr is an independent, community-run project. It is **not affiliated with, endorsed by, or supported by** the Servarr team or the Readarr, Sonarr, Radarr, Lidarr, or Prowlarr projects — please don't send Chaptarr support requests their way.

Chaptarr is provided under the GNU GPL v3 with no warranty (see the [License](#license) section below).

## AI Development Disclosure

Chaptarr is developed and maintained with the assistance of AI tools.

## License

- [GNU GPL v3](https://www.gnu.org/licenses/gpl.html)
- Copyright © 2026 Chaptarr contributors
- Portions copyright © 2010–2026 the Servarr team and contributors

See the [LICENSE](LICENSE) file for the full license text and [COPYRIGHT.md](COPYRIGHT.md) for attribution details.
