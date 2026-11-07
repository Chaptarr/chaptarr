# Multipart audiobook merging

ChaptarrNG `main` contains compatibility support for BookshelfNG's optional
multipart audiobook merge. The latest public release, `v0.9.977`, does not
honor these settings. Keep them unset on released installs; the retirement
release freeze remains active.

When a release containing this support is available, set
`BOOKSHELF_M4B_MERGE=true` to opt in. The merge runs during completed-download
processing for an audiobook edition with at least two approved audio tracks.
Every track must belong to the same local book and edition. Existing M4B files,
library scans, and manual imports are left alone. A rejected track prevents
the candidate group from merging.

Track order uses embedded disc and track numbers when present, then numbered
file names. Chapter titles use each track's `TITLE` tag or, when it is absent,
the file name. The merged output uses stereo AAC at 44.1 kHz and 128 kbps by
default.

The legacy settings are:

| Setting | Purpose | Default |
| --- | --- | --- |
| `BOOKSHELF_M4B_MERGE` | Enable completed-download merging | `false` |
| `BOOKSHELF_M4B_AAC_BITRATE_KBPS` | AAC bitrate, from 48 to 320 kbps | `128` |
| `BOOKSHELF_FFMPEG_PATH` | Path used to locate the `ffmpeg` executable (`ffmpeg.exe` on Windows) | Search the process `PATH` |

The merge falls back to importing the original tracks if FFmpeg is unavailable,
source durations cannot be read, conversion fails, or the result is rejected
during import. In Move mode, source tracks are removed only after the merged
file imports successfully. Copy mode keeps them. A download client that does
not allow moving completed files also keeps the originals. Merging re-encodes
audio and uses additional CPU; the resulting quality depends on the AAC
bitrate.

Detached completed-download conversions save their request and recovery state.
Each retry writes to a unique staging path, and only a successful, validated
output is promoted to the import path. Version 2 of the manifest records the
source and conversion settings plus the output size, modification time, and
SHA-256 digest; recovery verifies the full output content before reuse.
Readable output from a tool-reported failure stays available for inspection
but cannot be imported or reused. Startup can resume queued work or hand a
validated output to the import sweep.

ChaptarrNG has a recorded six-case synthetic real-tool smoke covering tagged
track order, chapters and metadata, registration, Move and Copy handling, and
fallback after tool failures. Those generated files do not establish behavior
on representative audiobooks. On 2026-10-24, the pinned Linux tool suite passed
9/9 cases, including a real m4b-tool/FFmpeg interruption during application
shutdown, durable requeue, a new worker instance resuming from the same SQLite
job table, and verification of the promoted output's manifest hash. This
covers the graceful shutdown recovery path with synthetic audio; representative
media and abrupt whole-application process-crash recovery remain open. See the
[retirement status](BOOKSHELFNG_RETIREMENT.md#audiobook-merge-compatibility)
for the current evidence and remaining acceptance work. Leave the setting
unset until a supported release and that acceptance work are complete.

For the database and service handoff, use the
[BookshelfNG migration guide](MIGRATE_FROM_BOOKSHELFNG.md).

## Implementation references

- [Legacy setting and track-order compatibility](../src/NzbDrone.Core/MediaFiles/BookshelfM4bCompatibility.cs)
- [Completed-download import](../src/NzbDrone.Core/MediaFiles/DownloadedBooksImportService.cs)
- [M4B conversion service](../src/NzbDrone.Core/MediaFiles/M4bConversionService.cs)
