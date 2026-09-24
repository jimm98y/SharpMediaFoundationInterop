# SharpSpatialVideo

Converts Apple spatial video (MV-HEVC) into something Windows can play, by rewriting the two
coded layers into a single conformant HEVC stream. See the commit that introduced the project
for how the rewrite works.

## Verifying against ffmpeg

Windows has no MV-HEVC decoder, so the only independent answer to what the dependent view should
contain comes from ffmpeg. The `truth` command decodes the rewritten stream through Media
Foundation and compares every frame of both views against per-frame hashes ffmpeg produced from
the original:

```
dotnet run --project src/SharpSpatialVideo -- truth "C:\Temp\IMG_7881.MOV" "C:\Temp\truth" 760
```

The reference hashes are generated on first use and cached in the given directory; `reference`
regenerates them after changing the input or the ffmpeg build. Both commands need an ffmpeg built
with MV-HEVC support - it is looked for in `SHARPSPATIAL_FFMPEG`, then on `PATH`, then in
`%USERPROFILE%\Downloads\ffmpeg-master-latest-winarm64-gpl\bin`. Without one, or without the input
file, the command says so and exits 2 rather than reporting a pass.

Two traps if you run ffmpeg by hand: `-pix_fmt gray` silently converts limited range to full, so
every frame then differs for no reason - compare in `yuv420p`; and framemd5 interleaves the audio
track, so only stream 0 is the video.

Last run: 760/760 frames of both views bit identical to the reference.
