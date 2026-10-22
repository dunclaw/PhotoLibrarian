# Video stabilization

Open a video, select **Stabilize** in the top editing ribbon, choose a new MP4 filename, and
leave **Auto-crop to remove unstable frame edges** checked (the default) or turn it off,
then select **Export stabilized copy**. There is no tool to find, configure or install,
and no runtime downloads. Everything runs locally inside PhotoLibrarian.

Export runs in the background with percentage progress and cancellation.
The dialog stays open until cancellation stops the export and removes its
temporary output. A completed copy is published only after encoding succeeds;
originals and existing output files are never overwritten. The suggested name
is `<name>-stabilized.mp4`, or the first free `<name>-stabilized (1).mp4`,
`(2)`, ... when that video or its metadata sidecar already exists. Output in a watched
library folder appears through normal folder indexing. Before playback, the export
waits until the published file can be opened and parsed, plus a short settle delay,
so the player does not read a file that is still being finalized. On success the dialog
closes and the copy opens immediately in the existing player, even when saved
outside the library or before indexing finishes. The video ribbon shows video
editing tools only; photo-only commands and image zoom controls are hidden.

## How it works

Stabilization is two passes over the clip, like the two-pass stabilizers in
vid.stab and OpenCV's `TwoPassStabilizer`, but implemented in this repository:

1. **Motion analysis.** Windows Media Foundation decodes the first video track
   to NV12. Each frame is reduced to a small (about 640-pixel-wide) luma image,
   and PhotoLibrarian estimates horizontal/vertical motion and small rotations
   from textured patches spread across the frame. Robust consensus rejects
   independently moving foreground subjects and ambiguous matches; subpixel
   refinement avoids whole-pixel stepping. The search is centered first on the
   previous frame's motion, then on a coarse whole-frame estimate, so fast,
   steady pans stay trackable. When motion blur defeats the patch matcher, it
   retries at half that resolution. Only these small images are kept, never
   full frames.
2. **Path planning.** The per-frame motions are accumulated into the camera path
   for the whole clip, which is then smoothed with a centered Gaussian (about
   half a second, 0.5 × frame rate frames). Because the smoother sees frames
   before and after each frame, intentional pans are followed instead of being
   held back and then released in a jump. The clip ends are extended by
   reflecting about a local straight-line fit, so a pan at the start or end is
   preserved too. Corrections that would exceed 8% translation, 0.06 rad of
   rotation, or (with auto-crop) the valid crop area are scaled back and
   re-smoothed, so limits never introduce a sudden step. Dropouts of up to 0.2 s
   are bridged by interpolating the neighboring motion; longer ones, such as
   scene cuts or flat frames, start a new path segment.
3. **Render.** The clip is decoded again (with audio, decoded to PCM) and each
   frame's planned correction is applied to luma and chroma in one bilinear
   resampling pass.

With auto-crop enabled, the outer 8% of each edge is cropped and rescaled to the
original output dimensions. Corrections are limited so every pixel in that
retained area samples valid source data, which avoids mirrored-edge artifacts.
Large shakes may therefore receive less correction.
Turning auto-crop off retains the full frame and uses mirrored edge pixels.
Motion search and the combined warp/crop/resample pass are spread across all
CPU cores. On a 32-core test machine, 10-second 4K clips export in about 7 s,
including the roughly 2 s analysis pass. Progress shows analysis as the first
30%.

A `MediaStreamSource` feeds the rendered frames and their source timestamps/durations
to Windows' H.264/AAC MP4 encoder. Only a few full-size frames are held in memory, not the
entire clip. The decoding bindings
([Vortice.MediaFoundation](https://github.com/amerkoleci/Vortice.Windows), MIT)
and their managed dependencies ship automatically with the app on x64 and
ARM64. There are no separate executables, native codec bundles, PATH settings,
or user-provided executable paths. New dependency notices are shipped in
`Assets/VIDEO-STABILIZATION-NOTICES.txt`.

## Limitations

This is a basic, two-pass, translation/rotation stabilizer for handheld
movements, not an action-camera/gyro stabilizer. With auto-crop off, mirrored
edges can be noticeable; severe shake, large rotations, rolling shutter, heavy motion blur and large moving foreground
subjects may not improve. Review the copy before keeping
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

Reviewed against upstream documentation/source on October 22, 2026:

| Option | License considerations | Decision |
|---|---|---|
| Windows Media Foundation + an integrated rigid-motion stabilizer | Windows codecs are platform components. Vortice.MediaFoundation and its Vortice/SharpGen managed dependencies are MIT. | Selected: no downloads, extra installs or native codec distribution; the app owns motion estimation and smoothing. |
| [Windows video stabilization effect](https://learn.microsoft.com/en-us/windows/win32/medfound/video-stabilization-mft) | Windows platform component. | Investigated with `MediaTranscoder`; on the test host it encoded successfully but did not meet the measurable shake-reduction check. Not used as a success-shaped fallback. |
| [FFmpeg deshake](https://ffmpeg.org/ffmpeg-filters.html#deshake) | [Filter source](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_deshake.c) is LGPL-2.1-or-later; the complete executable's license depends on build options, and builds with libx264 are GPL. | Useful motion-estimation ideas, but requiring users to install a command-line tool is unacceptable. Not invoked or shipped. |
| [vid.stab](https://github.com/georgmartius/vid.stab) | [Current upstream](https://github.com/georgmartius/vid.stab/blob/master/LICENSE) is LGPL-2.1-or-later; releases through 1.1.2 were GPL. | Strong candidate for a bundled two-pass L1-optimal camera path implementation; requires native x64/ARM64 builds and LGPL redistribution/relinking review. |
| [OpenCV videostab](https://docs.opencv.org/4.13.0/d5/d50/group__videostab.html) | [OpenCV](https://github.com/opencv/opencv/blob/4.x/LICENSE) and [OpenCvSharp](https://github.com/shimat/opencvsharp/blob/master/LICENSE) are Apache-2.0; bundled third-party notices still need review. | The native module includes RANSAC estimation and `TwoPassStabilizer`. `LpMotionStabilizer` requires the separately enabled CLP dependency; stock builds without CLP throw instead of providing L1 smoothing. OpenCvSharp's [managed helper](https://github.com/shimat/opencvsharp/blob/master/src/OpenCvSharp/Modules/videostab/CvVideoStab.cs) only exposes one-pass Gaussian filtering and materializes all input/output frames. A custom native bridge is required for the two-pass API. |
| [Gyroflow](https://github.com/gyroflow/gyroflow) | [GPL-3.0](https://github.com/gyroflow/gyroflow/blob/master/LICENSE) | Gyro-assisted stabilization needs compatible motion data/lens profiles and is beyond this basic feature. |

A native OpenCV 4.13 x64 prototype was built with the contrib `videostab`
module and run on a local handheld clip through OpenCV's Media Foundation
backend. At 960x540 analysis resolution, RANSAC rigid-motion estimation plus
two-pass Gaussian path smoothing reduced measured frame-to-frame translation
acceleration from 0.771 px/frame² to 0.220 at radius 15 and 0.180 at radius 30.
Angular acceleration fell from 0.00114 deg/frame² to 0.000227 and 0.000191,
respectively. These are motion-estimator metrics, not a perceptual quality
assessment or a full-resolution encoded export.

The native `TwoPassStabilizer` resets its frame source after the motion pass.
It retains per-frame motion and correction matrices, but keeps only a bounded
frame cache (twice the radius plus one), which supports a reduced-resolution
analysis pass followed by a second full-resolution pass in the existing encoder
pipeline. This avoids the all-input/all-output-frame memory behavior of the
managed `CvVideoStab.Stabilize` helper. The first tested build had
`WITH_CLP=OFF`; calling `LpMotionStabilizer` without CLP throws, so those video
measurements use Gaussian smoothing rather than L1 path optimization. A
separate synthetic-motion smoke test with `WITH_CLP=ON`
successfully ran `LpMotionStabilizer` on x86. OpenCV 4.13's `opencv_videostab`
module was also cross-built with CLP for ARM64, but the ARM64 binary could not
be run on this x64 host. The synthetic smoke test confirms solver wiring only;
it does not demonstrate improved stabilization on real footage.

This is a feasibility result, not an app dependency or a production replacement.
The CLP-enabled x86 and ARM64 test builds required more than the MSVC toolsets:
vcpkg, the `coin-or-clp` package and its transitive COIN-OR/math/build
dependencies, OpenCV 4.13 plus `opencv_contrib`, and CMake configuration for
each target. OpenCV's default CMake configuration initially mistook the ARM64
target for the x64 host; the successful cross-build used a toolchain file that
sets `CMAKE_SYSTEM_PROCESSOR=ARM64`. A production bridge still needs
reproducible x64, ARM64 and x86 builds/tests, an audit of all bundled licenses
and notices, and package-size measurements. CLP is EPL-2.0 licensed and brings
native dependencies that also need to be distributed and reviewed. No OpenCV,
CLP or native binary has been added to the app. The Apache-2.0 OpenCV license
is simpler for this MIT application than `vid.stab`'s LGPL terms, but this
prototype has not resolved the production integration, packaging, or quality
concerns.

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
translation, rotation and an independently moving foreground. Planner tests
cover a fast pan beyond the fine search window (followed without jumps),
bridging short tracking dropouts, and preserving linear pans at clip ends. Workflow tests
cover the separate video ribbon and immediate playback before library indexing.

Library refreshes retain thumbnails for unchanged files instead of clearing all
video previews after an export. Thumbnail provider and UI-decode waits honor
cancellation and a per-item timeout so one stalled operation cannot indefinitely
block the queue; failures are logged and loading indicators are cleared on the
UI thread. Playback releases old media sources and ignores obsolete open requests.
