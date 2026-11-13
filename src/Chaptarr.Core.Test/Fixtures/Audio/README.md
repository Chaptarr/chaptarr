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
