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

- **Windows**: https://www.gyan.dev/ffmpeg/builds/ — put `ffmpeg.exe` in
  `Tools/ffmpeg/win-x64/` and `ffprobe.exe` in `Tools/ffprobe/win-x64/`
- **Linux**: https://johnvansickle.com/ffmpeg/ static builds — amd64 goes in
  `linux-x64`, arm64 in `linux-arm64`
- **macOS**: https://evermeet.cx/ffmpeg/ for `osx-x64`, or a native arm64 build
  for `osx-arm64`

On Unix systems, make the binaries executable:

```bash
chmod +x Tools/ffmpeg/*/ff* Tools/ffprobe/*/ff*
```

## Resolution order

Chaptarr checks the application startup folder `Tools/` first, then a `Tools/`
folder in the app data directory, then known system locations and the system
PATH. In the official Docker image, ffmpeg and ffprobe are provided by the image
itself, so nothing needs to be placed here.
