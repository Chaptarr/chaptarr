These fixtures contain generated mono silence, not copyrighted recordings. The M4B and FLAC sources are 0.05 seconds long.
They exercise the real TagLib read/write round trip and hardlink preservation without requiring FFmpeg during tests.

Generated with:

```sh
ffmpeg -f lavfi -i anullsrc=r=22050:cl=mono -t 0.05 -c:a aac -map_metadata -1 silence.m4b
ffmpeg -f lavfi -i anullsrc=r=22050:cl=mono -t 0.05 -c:a flac -metadata_header_padding 0 -map_metadata -1 silence.flac
```

Additional fixtures (`silence.mp3`, `.ogg`, `.opus`, `.wma`, `.wv`) contain 0.3 seconds of generated mono silence at 48 kHz. The WMA container reports 0.342 seconds (codec framing), not exactly 0.3 seconds. They were generated with the same lavfi source and codecs `libmp3lame`, `libvorbis`, `libopus`, `wmav2`, and `wavpack`, respectively:

```sh
ffmpeg -f lavfi -i anullsrc=r=48000:cl=mono -t 0.3 -c:a CODEC silence.EXT
```

The WavPack fixture exercises the APE tag family; this does not add `.wv` to Chaptarr's import extension list. Opus corruption assertions follow Lidarr/taglib-sharp commits `ac03b7bade` and `15d9f9276e`; no upstream audio recording is redistributed.

Generator checked: `ffmpeg version 5.1.9-0+deb12u1`. Container durations can include encoder padding.

SHA256:

```text
43b9037f6e6a09457f3595c1c134aca93b3edab0bb1ea263f863ef9bac8fbccc  silence.flac
425a8149b33e3706d2e94bb3b88c5b5b37b5760d2fae469cbece7e13cb31fff6  silence.m4b
ddec49b66215be7f2dc9fa789142680783e7a32e20f43bdd7262b6defc3280bf  silence.mp3
e7baaf29a2217c0658daa6b89f68b7a8062da663faa9095f571f8f82919b4d3e  silence.ogg
1ddc347f521de09bffcdc426bc361c78f6e661265b0c1f881de4483e2a45776a  silence.opus
63f957147f75097ccefd94e5b52e93c92aa5d76eaafeb4cccab1e5be353be394  silence.wma
abf5a6066fa99f0bf88f7715c4974a7ce97cecde7acfaaddccd382887aa32bc6  silence.wv
```


`silence.wav` uses the same 0.3-second, 48 kHz mono source and `pcm_s16le`.

The five `chapters.*` files contain two seconds of generated mono silence at
44.1 kHz, with chapters at 0 and 1 second. No library recording is included.
Generate the metadata input as:

```text
;FFMETADATA1
comment=Ripped by fixture
[CHAPTER]
TIMEBASE=1/1000
START=0
END=1000
title=Opening
[CHAPTER]
TIMEBASE=1/1000
START=1000
END=2000
title=Conclusion
```

```sh
ffmpeg -f lavfi -i anullsrc=r=44100:cl=mono -f ffmetadata -i chapters.txt \
  -map_metadata 1 -map_chapters 1 -t 2 -c:a CODEC chapters.EXT
```

Use `libmp3lame`/mp3 (also `-id3v2_version 3`), `libopus`/opus,
`libvorbis`/ogg, `aac`/m4b and `flac`/flac. For Ogg and FLAC also pass
`-metadata CHAPTER000=00:00:00.000 -metadata CHAPTER000NAME=Opening
-metadata CHAPTER001=00:00:01.000 -metadata CHAPTER001NAME=Conclusion`.
The M4B has both a QuickTime chapter track/reference and a Nero `chpl` atom.
Tests compare the native structures before/after a tag write; decoded audio and
ffprobe chapter output were also compared during fixture validation.

The FLAC additionally has a constructed native CUESHEET metadata block (type 5),
following RFC 9639 section 8.7: zero catalog/lead-in/flags/reserved header,
three track records at samples 0, 44100 and 88200, numbered 1, 2 and 255
(lead-out). Tracks 1 and 2 each have index 1 at offset zero; the lead-out has
no indices. The block was inserted after STREAMINFO without modifying audio
frames and checked by libFLAC's `FLAC__metadata_object_cuesheet_is_legal` in
non-CD mode. This tests the binary structure as well as chapter comments.

Chapter/WAV fixture SHA256:

```text
8140d534bd4f18e9f828a89ac40c89c095894123db8f41351281a3f619b51503  chapters.flac
53c3f74070d40afc729d70af67a07a2ebfb90e1cd2ff1efe5dbbb04ba8af52f0  chapters.m4b
147724ecf15fc8d1cd5e7db7c954badd44a722014ef2ebb04e93238257da6324  chapters.mp3
06c574a49d9aca57bb757acb4a3cec158e4e93f2aa41163b5ad153c32d774973  chapters.ogg
7f0ef750eebc3fb8b8497fec12205d44917f6ba608ecc33574d00aa1f64c3178  chapters.opus
8a039a3c0cabb181014fd000971cc33d520f47185b0022d9552facda628f7b67  silence.wav
```
