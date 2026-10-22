# Video stabilization

Open a video, select **Stabilize** in the top editing ribbon, choose a new MP4 filename, and
select **Export stabilized copy**. There is no tool to find, configure or install,
and no runtime downloads. Everything runs locally inside PhotoLibrarian.

Export runs in the background with percentage progress and cancellation.
The dialog stays open until cancellation stops the export and removes its
temporary output. A completed copy is published only after encoding succeeds;
originals and existing output files are never overwritten. Output in a watched
library folder appears through normal folder indexing. On success the dialog
closes and the copy opens immediately in the existing player, even when saved
outside the library or before indexing finishes. The video ribbon shows video
editing tools only; photo-only commands and image zoom controls are hidden.

## How it works

Windows Media Foundation decodes the first video track to NV12 and the first
audio track, if present, to PCM. PhotoLibrarian estimates horizontal/vertical
motion and small rotations from textured patches distributed across successive
luma frames. Robust consensus rejects independently moving foreground subjects
and ambiguous matches; subpixel refinement avoids whole-pixel stepping.
The accumulated rigid camera trajectory is smoothed, and both luma and chroma
are resampled with bilinear interpolation. Insufficient reliable matches reset
the trajectory rather than carrying a correction across a scene cut.
Edge pixels are mirrored and correction is bounded to 8% of each dimension and
about 3.4 degrees; there are no blank borders or ever-increasing zoom.

A `MediaStreamSource` feeds those frames and their source timestamps/durations
to Windows' H.264/AAC MP4 encoder. Only a few frames are held in memory, not the
entire clip. The decoding bindings
([Vortice.MediaFoundation](https://github.com/amerkoleci/Vortice.Windows), MIT)
and their managed dependencies ship automatically with the app on x64 and
ARM64. There are no separate executables, native codec bundles, PATH settings,
or user-provided executable paths. New dependency notices are shipped in
`Assets/VIDEO-STABILIZATION-NOTICES.txt`.

## Limitations

This is a basic, single-pass, translation/rotation stabilizer for small handheld
movements, not an action-camera/gyro stabilizer. Mirroring can be noticeable;
severe shake, large rotations, rolling shutter, motion blur, large moving foreground
subjects and intentional pans may not improve. Review the copy before keeping
it. There is no before/after preview or adjustable strength.

Inputs must be decodable by Windows' installed media support; standard
H.264/AAC MP4 works without any additional codec installation. Unsupported
formats, odd-sized decoded frames and changing-resolution streams fail with an explicit error rather
than silently exporting an unstabilized copy. This feature does not prompt
users to install codecs or third-party tools.

Output is 8-bit H.264 with a resolution/frame-rate-dependent bitrate, retaining
the source's even frame dimensions, nominal frame rate,
pixel aspect ratio and rotation metadata. Audio is re-encoded to AAC at
192 kbit/s and 48 kHz, mono or stereo. Subtitles, additional tracks, HDR fidelity,
and arbitrary metadata embedded in the source container are not preserved.
The source XMP sidecar is copied to the export, preserving PhotoLibrarian ratings,
flags, captions, capture dates, tags and other XMP properties. The copy is
re-indexed using those sidecar values when it is added to a library. Keep the
original for archival purposes.

## Options investigated

Reviewed against upstream documentation/source on October 19, 2026:

| Option | License considerations | Decision |
|---|---|---|
| Windows Media Foundation + an integrated rigid-motion stabilizer | Windows codecs are platform components. Vortice.MediaFoundation and its Vortice/SharpGen managed dependencies are MIT. | Selected: no downloads, extra installs or native codec distribution; the app owns motion estimation and smoothing. |
| [Windows video stabilization effect](https://learn.microsoft.com/en-us/windows/win32/medfound/video-stabilization-mft) | Windows platform component. | Investigated with `MediaTranscoder`; on the test host it encoded successfully but did not meet the measurable shake-reduction check. Not used as a success-shaped fallback. |
| [FFmpeg deshake](https://ffmpeg.org/ffmpeg-filters.html#deshake) | [Filter source](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_deshake.c) is LGPL-2.1-or-later; the complete executable's license depends on build options, and builds with libx264 are GPL. | Useful motion-estimation ideas, but requiring users to install a command-line tool is unacceptable. Not invoked or shipped. |
| [vid.stab](https://github.com/georgmartius/vid.stab) | [Current upstream](https://github.com/georgmartius/vid.stab/blob/master/LICENSE) is LGPL-2.1-or-later; releases through 1.1.2 were GPL. | Strong candidate for a bundled two-pass L1-optimal camera path implementation; requires native x64/ARM64 builds and LGPL redistribution/relinking review. |
| [OpenCV videostab](https://docs.opencv.org/4.x/d5/d50/group__videostab.html) | [OpenCV 4.5+](https://opencv.org/license/) is Apache-2.0; wrappers/native binaries require their own audit. | The native module includes RANSAC motion estimation and two-pass/L1 stabilizers. OpenCvSharp publishes full Windows x64 and ARM64 runtimes, but its managed `CvVideoStab` helper wraps only the one-pass Gaussian stabilizer; using native two-pass/L1 classes requires a custom bridge. Package size and module availability on both architectures also need validation. |
| [Gyroflow](https://github.com/gyroflow/gyroflow) | [GPL-3.0](https://github.com/gyroflow/gyroflow/blob/master/LICENSE) | Gyro-assisted stabilization needs compatible motion data/lens profiles and is beyond this basic feature. |

The strongest next candidate is a **bundled, in-process `vid.stab` integration**:
its two-pass detection/transform pipeline and L1-optimal camera-path smoothing
directly address the current single-pass estimator's temporal-jitter weakness,
and the library can operate on decoded frames without requiring FFmpeg or any
user-installed tool. Before replacing the current estimator, validate an
x64/ARM64 Windows build, LGPL relinking/source-distribution obligations, and
the output on real handheld footage.

The current motion estimator is implemented in this repository, not copied from
FFmpeg, vid.stab, OpenCV or Gyroflow. PhotoLibrarian remains MIT licensed.

## Developer checks

Run:

```powershell
dotnet run --project src\PhotoLibrarian.Tests -p:Platform=x64 -- -class PhotoLibrarian.Tests.VideoStabilizationTests
```

The tests generate video/audio fixtures with Windows' own media APIs and run
without environment variables or external programs. They measure at least
20% reduction in central temporal differences, check audio/silent clips,
profile/rotation settings, path safety, original preservation, output races,
errors and cancellation cleanup. Natural-texture coverage includes subpixel
translation, rotation and an independently moving foreground. Workflow tests
cover the separate video ribbon and immediate playback before library indexing.

Library refreshes retain thumbnails for unchanged files instead of clearing all
video previews after an export. Thumbnail provider and UI-decode waits honor
cancellation and a per-item timeout so one stalled operation cannot indefinitely
block the queue; failures are logged and loading indicators are cleared on the
UI thread. Playback releases old media sources and ignores obsolete open requests.
