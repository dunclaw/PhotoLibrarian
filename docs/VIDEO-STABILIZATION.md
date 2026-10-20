# Video stabilization

Open a video, select **Stabilize video...**, select a trusted `ffmpeg.exe`,
and choose a new MP4 filename. Install FFmpeg separately using the links at
https://ffmpeg.org/download.html; this app neither downloads nor bundles it.
The selected build must provide `deshake`, `libx264`, and the AAC encoder.
Selecting an executable explicitly authorizes running that program locally.

Export runs in the background with processed-time status and cancellation.
The dialog stays open until cancellation has stopped FFmpeg and removed its
temporary output. A completed copy is published only after FFmpeg succeeds;
originals and existing output files are never overwritten. Output in a watched
library folder appears through normal folder indexing.

This is a basic, single-pass stabilizer for small handheld movements, not an
action-camera/gyro stabilizer. It uses a 16-pixel motion search and mirrored
edges rather than cropping/zooming. Mirroring can be noticeable; severe shake,
rolling shutter, motion blur and intentional pans may not improve. Review the
copy before keeping it. There is no in-app preview or adjustable strength.

Output is H.264 (CRF 20, medium preset, 8-bit YUV 4:2:0) with AAC audio at
192 kbit/s. The first video and first audio stream (if any) are exported.
Odd dimensions are padded by at most one pixel for encoder compatibility.
FFmpeg copies compatible container metadata, but this is not an archival
metadata round-trip: subtitles, additional tracks, HDR fidelity, specialized
metadata and XMP sidecars are not preserved.

## Open-source options investigated

Reviewed against upstream documentation/source on October 19, 2026:

| Option | License considerations | Fit |
|---|---|---|
| [FFmpeg deshake](https://ffmpeg.org/ffmpeg-filters.html#deshake) | [Filter source](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_deshake.c) is LGPL-2.1-or-later. The complete executable's license depends on build options; builds with libx264 are GPL. | Selected: single pass, no motion-analysis files, simple process boundary and cancellation, and mature decoding/encoding/audio support. |
| [vid.stab](https://github.com/georgmartius/vid.stab) / FFmpeg vidstabdetect + vidstabtransform | [Current upstream license](https://github.com/georgmartius/vid.stab/blob/master/LICENSE) is LGPL-2.1-or-later; releases through 1.1.2 were GPL. Check the actual distributed FFmpeg build, not just current library licensing. | Better future option for two-pass trajectory smoothing and automatic zoom; more processing, filter availability and temporary transform-file management. |
| [OpenCV videostab](https://docs.opencv.org/4.x/d5/d50/group__videostab.html) | [OpenCV 4.5+](https://opencv.org/license/) is Apache-2.0; audit selected wrappers and native binaries separately. | Useful optical-flow/global-motion ideas, but adds native platform packaging and requires a separate audio/container pipeline. |
| [Gyroflow](https://github.com/gyroflow/gyroflow) | [GPL-3.0](https://github.com/gyroflow/gyroflow/blob/master/LICENSE) | Strong gyro-assisted stabilization, but requires compatible motion data/lens profiles and is disproportionate to this basic feature. |

No upstream code or native binaries are copied, linked, or redistributed.
PhotoLibrarian remains MIT licensed. Users obtain and run FFmpeg separately
under that build's license. Any future bundled distribution needs its own
license/notices/source-offer review; this is not a claim that all FFmpeg builds
are LGPL.

## Developer checks

Unit tests cover path safety and process arguments. To additionally run the
FFmpeg integration tests, set `PHOTOLIBRARIAN_TEST_FFMPEG` to the absolute path
of a trusted FFmpeg executable with the above filters/encoders and run
`dotnet run --project src\PhotoLibrarian.Tests -p:Platform=x64 -- -class PhotoLibrarian.Tests.VideoStabilizationTests`.
Integration tests generate their own fixture and check shake reduction,
audio/no-audio handling, original preservation, errors and cancellation cleanup.
