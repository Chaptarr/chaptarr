# External Tools for Chaptarr

This directory can hold bundled ffmpeg and ffprobe binaries for installs where they
are not otherwise available. Chaptarr looks each tool up under its own directory:

```
Tools/
├── ffmpeg/
│   └── <platform>-<arch>/
│       └── ffmpeg (ffmpeg.exe on Windows)
└── ffprobe/
    └── <platform>-<arch>/
        └── ffprobe (ffprobe.exe on Windows)
```

`<platform>` is `win`, `linux`, or `osx`. `<arch>` is the running process
architecture: `x64`, `x86`, `arm64`, or `arm`.

Examples: `Tools/ffmpeg/win-x64/ffmpeg.exe`, `Tools/ffprobe/linux-arm64/ffprobe`,
`Tools/ffmpeg/osx-arm64/ffmpeg` (Apple Silicon).

Note that ffmpeg and ffprobe live in separate trees. An ffprobe binary placed
inside the ffmpeg directory will not be found.

## Downloading

Normal source builds do not download these binaries. Install FFmpeg through your
operating system and expose both `ffmpeg` and `ffprobe` on `PATH`, or place
the binaries in the directories below. Any native archive must include both
tools for its target runtime rather than silently shipping an incomplete
package.

The automatic download scripts pin immutable archives and committed SHA-256
values for Windows x64 (Gyan FFmpeg 8.1.2) and Linux x64/arm64 (John Van
Sickle FFmpeg 7.0.2). Updating FFmpeg is an explicit source-and-hash change,
not a rolling `latest` download.

- **Windows**: https://www.gyan.dev/ffmpeg/builds/ — put `ffmpeg.exe` in
  `Tools/ffmpeg/win-x64/` and `ffprobe.exe` in `Tools/ffprobe/win-x64/`
- **Linux**: https://johnvansickle.com/ffmpeg/ static builds — amd64 goes in
  `linux-x64`, arm64 in `linux-arm64`
- **macOS**: automatic bundling is not currently supported. Install FFmpeg
  through Homebrew or another trusted package source for normal source builds.
  Native packages require manually staged, verified binaries for `osx-x64`
  or `osx-arm64`.

The pinned third-party builds are GPLv3. Chaptarr does not currently publish
native archives. Before native archives are published, their FFmpeg license,
build attribution, and corresponding-source distribution must be reviewed and
shipped with the package; a checksum and a link alone do not settle those
redistribution obligations.

On Unix systems, make the binaries executable:

```bash
chmod +x Tools/ffmpeg/*/ff* Tools/ffprobe/*/ff*
```

## Resolution order

Chaptarr checks the application startup folder `Tools/` first, then a `Tools/`
folder in the app data directory, then known system locations and the system
PATH. In the official Docker image, ffmpeg and ffprobe are provided by the image
itself, so nothing needs to be placed here.
