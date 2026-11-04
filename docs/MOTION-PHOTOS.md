# Motion photos

Open a supported JPEG in the full-photo viewer to automatically play its motion
on a loop. A small play/pause icon appears in the header alongside the other
viewer controls; no bottom button row or video transport panel is added.
**Pause motion and show still photo**, or clicking the playing image, stops
playback and displays the original high-resolution still, not a frozen video
frame. The header's **Play motion** action restarts looping playback from the
beginning. A paused photo stays still until Play is chosen or the photo is
opened again. Navigation, close, leaving the viewer, and photo edit modes stop
motion and release its resources. Playback includes audio when present.

## Supported layouts

Google/Pixel JPEG motion photos with a standard JPEG APP1 XMP packet:

- Legacy Microvideo v1: camera namespace `MicroVideo=1`,
  `MicroVideoVersion=1`, and `MicroVideoOffset` measured back from EOF.
- Motion Photo v1: `MotionPhoto=1`, `MotionPhotoVersion=1`, and an ordered
  container directory beginning with a JPEG `Primary` item and ending with a
  `MotionPhoto` item with MIME `video/mp4` and a positive `Length`.
  Optional primary padding and intermediate JPEG `GainMap` items are supported.
  Secondary items may explicitly specify `Padding="0"` (as Pixel files do);
  nonzero secondary padding is not supported.
  Video offset is calculated from the final item's length, not by scanning for
  MP4 signatures. Modern directories take precedence over obsolete legacy
  offsets.

XMP prefixes can vary; namespace URIs identify properties. Attribute and
element property forms are read. Ambiguous properties, unknown versions,
shared-resource/zero-length secondary items, or invalid offsets are rejected,
not guessed. A disabled/missing camera motion flag is not an invitation to play
arbitrary trailing bytes.

The original still is always used as the cover, rather than a lossy video
frame. The reader retains the optional presentation timestamp in microseconds
(`-1`/missing means unspecified). It does not rewrite, round, or use that value
as an offset into the file. Motion playback starts at the beginning;
this feature does not implement timestamp-track scoring, automatic key-frame
selection, or motion editing.

## Safety and limits

All work is local using the Windows media stack. There are no downloads,
external executable requirements, exports, or library writes. The embedded
MP4 is copied into a read-only in-memory stream when the full viewer starts playback;
no extracted file is created anywhere. Source metadata is revalidated using
the same read-only file handle used for extraction.

Header scanning is limited to 4 MiB; standard XMP packets to JPEG APP1 capacity
(at most about 64 KiB); MP4 top-level box validation to 4096 boxes; and embedded
video to 64 MiB. Detection, extraction, and native media opening each have a
10-second timeout. Playback accepts at most 30 seconds and 4096 pixels per side.
Navigation, close, unload, and photo edit modes cancel pending motion work and
dispose native playback. Results and queued native events are checked against
the current request/player before changing the view.

Windows supplies the actual codecs. H.264 MP4 is covered by generated playback
tests; other codecs (including HEVC) depend on the machine and are not promised.
Structural MP4 validation is not a full decoder: a bounded but damaged media
payload can still fail when Windows opens it. Decode errors, unsupported
metadata, missing/truncated media, size limits, and timeouts show an inline
limitation without replacing or changing the still photo.

This feature does not alter existing still-image editing/export semantics.
Re-encoding or metadata-writing software may discard appended motion data;
playback support is not a promise of motion-preserving editing.

## Apple Live Photos and other unsupported variants

**Apple Live Photo pairing/playback is not supported by this implementation.**
Apple uses separate HEIC/JPEG still and MOV video resources with pairing/content
identifiers and timed still-image metadata. It is not the Google appended-MP4
layout. Matching basenames alone would risk playing the wrong clip. Proper
identifier-based pairing, missing/ambiguous partner handling, and portable
library behavior are tracked separately in [#174](https://github.com/dunclaw/PhotoLibrarian/issues/174).
The image and MOV continue to open independently through their ordinary paths.

HEIC/AVIF motion-photo containers (`mpvd`), embedded QuickTime/MOV motion items,
extended XMP, vendor-specific Samsung trailers, and nonstandard/shared-resource
directories are not implemented. Ordinary HEIC/JPEG stills and standalone
videos retain their existing viewer paths. Unsupported HEIC/AVIF motion variants
are treated as stills, not advertised as detected motion photos.

Format references:
[Android Motion Photo format](https://developer.android.com/media/platform/motion-photo-format),
[Apple PHLivePhoto](https://developer.apple.com/documentation/photos/phlivephoto),
[Apple Live Photo display](https://developer.apple.com/documentation/photokit/displaying-live-photos).

## Generated regression fixtures

No user's photo library is read or modified by these tests.

An explicitly supplied local sample can also be validated read-only by passing
`-MotionPhotoValidationPath 'D:\path\sample.jpg'` with `-MotionPhoto`. This opt-in
check verifies detection, advancing native playback, and unchanged source hash
and timestamp; the file is never copied into fixtures or written by the test.

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -- -class PhotoLibrarian.Tests.MotionPhotoReaderTests -class PhotoLibrarian.Tests.MotionPhotoViewerTests -class PhotoLibrarian.Tests.VideoWorkflowTests -class PhotoLibrarian.Tests.ImageViewerNavigationTests -class PhotoLibrarian.Tests.ImageViewerOverlayFocusTests -parallelMode none
.\tools\Test-PhotoGridAccessibility.ps1 -MotionPhoto
```

The reader fixtures cover offsets, padding, gain maps, namespace/property forms,
timing, byte/header/box limits, truncated inputs, malformed XML/containers,
cancellation, unchanged source hash/timestamp, and absence of extracted files.
The isolated WinUI suite uses Windows-generated JPEG and H.264/AAC MP4 fixtures
and an injected viewer ViewModel, not `MainViewModel`, a library database, or
default user preference stores. It exercises automatic playback, looping across
clip boundaries, header play/pause, image-tap still return, navigation, close
during extraction, stale detection,
malformed metadata, decoder failure, standalone video, unload, and source
invariance. It does not establish compatibility with every physical Pixel model,
HEVC codec configuration, Apple pair, or unsupported container variant.
