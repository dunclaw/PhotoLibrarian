# Photo Fuse

Open a group photo in the **full-photo viewer**, then click **Photo Fuse** in the
top command ribbon. The photo stays in the same viewer; there is no modal dialog
or grid multi-selection requirement. Fuse reuses the current face scan or runs the
app's existing local face scanner. Face boxes appear on the image and unnamed faces
are included in the existing right-rail people list. Use **Add people tags** there
to draw a missed face; its replacements are then searched again.

Fuse searches **all indexed stills within ±2 minutes of capture time**, nearest
first, including photos excluded by the current browsing filters. Without a capture
time, it uses modification time in the same folder. There is no candidate-count
limit. Each donor is decoded and registered independently, one pair at a time.
The search reports progress and incompatible shots; clicking Fuse again cancels a
search and exits (saving any previously accepted changes).

Click a face box to see thumbnails of faces near its registered position in the
other shots. Proposals must have similar size and lie within 0.75 target-face widths/
heights of the inverse-mapped center. They are **spatial suggestions, not verified
identities**. Choose one to preview the replacement on the full image, or choose
**Original** to undo it. The face box is expanded by 35% on each side to leave room
for a stationary feathered seam; incompatible borders and overlapping regions
are rejected without replacing the accepted preview.

Use the viewer's normal zoom/1:1/pan controls to inspect eyes and seams. Click
**Photo Fuse again** (the button reads **Save and exit Fuse**) to save the accepted
preview as a new PNG beside the base photo and leave Fuse mode. The original is
never overwritten. With no replacements, exiting writes nothing. A failed save
keeps the workspace and choices available for retry. Escape/Close in the viewer
also finish Fuse rather than silently discarding changes.
**Discard and exit** is an explicit recovery option if a source changes or you
decide not to keep the replacements; it never writes a composite.

## Conservative support

- Opaque JPEG SDR renditions and single-frame 8-bit PNG, with identical
  EXIF-oriented source dimensions. All eight EXIF orientations, including
  mirroring, are applied before registration. Embedded profiles are converted to
  sRGB by Windows Imaging Component. Transparency, animated PNG, higher-bit-depth
  input, RAW, video and other containers are rejected.
- Use the same zoom, exposure and white balance, a textured stationary background,
  and only small camera shifts. The existing local robust rigid aligner is reused,
  without the video stabilizer's motion-guess fallback. Translation is limited to
  3% of each analysis dimension, rotation to two degrees, and measured shear to
  0.35 analysis pixels. Zoom/scale changes, perspective correction, parallax and
  large subject movement are unsupported; unreliable registrations are rejected,
  not quietly replaced with identity transforms.
- Distributed texture is required in all nine analysis tiles. Competing coarse
  translations are rejected for repeating textures. At least 85% of the aligner's
  samples must agree, followed by an independent RGB holdout check in every tile
  (70% within 18/255 in every channel). A selected region must be entirely inside
  the donor's transformed bounds. Its feather band must agree with the base
  (90% within 24/255 in every channel). These deliberately restrictive checks can
  reject a usable burst; selecting another replacement is safer than weakening them.
- Output is **at most 1600 pixels on the longest edge**, never enlarged, opaque
  8-bit sRGB PNG. The base is not globally cropped or warped. Donor samples are
  inverse-mapped into the base, bilinearly interpolated, and feathered in linear
  light over 2-16 pixels. Pixels outside the selected rectangles are unchanged.
  Uncovered donor pixels cannot be included in a replacement region.
- Compressed sources are limited to 100 MiB and source dimensions to 100
  megapixels. Discovery holds only one base/donor pair at once, regardless of burst
  size. Final selected-donor buffers have a 192 MiB decoded-memory budget, plus
  analysis planes, preview/export buffers, codec memory and UI bitmaps. This is a
  memory safety bound, not a four-photo or eight-face cap. WIC may need
  source-resolution decode memory depending on the codec.

No new model is required: Fuse uses the app's existing detector/embedder. It never
downloads models or contacts a service. If models are not already available, a
clear message explains this; existing stored faces and explicit manual boxes remain
usable. A completed face scan is reused only for the current indexed file snapshot.

## Persistence and safety

Source files and existing XMP sidecars are leased read-only during registration
and accepted-preview review. Leases are released for an explicit manual face-tag
operation, then the accepted choices are rebuilt and validated before save.
Sources are hashed during discovery and checked again during composition. A
changed source, newly appearing sidecar, pending edit or failed registration
cannot silently publish a stale composite. Background recognition is paused while
Fuse is active, then resumed. Explicit face scans use the existing database/portable
face-metadata pipeline; Fuse never rewrites source pixels.

An XMP namespace declaration, default PhotoLibrarian recipe, or Adobe camera-profile
metadata is **not** treated as an unapplied edit. Actual nondefault/unknown
PhotoLibrarian edit properties and Adobe metadata explicitly marked as pending
settings/crop are rejected with actionable wording, not the vague “has an edit
recipe” error. No metadata is rewritten merely to clear that error.

The saved copy is the **exact accepted preview pixel buffer**, not a second render.
PNG encoding and ICC/XMP tagging finish in uniquely named temporary files in the
destination folder before a no-overwrite rename publishes the image. Existing
same-stem files, other image extensions, and metadata sidecars cause a numbered
name to be chosen. Failed/canceled work removes only its own staging files.
Cancellation after the publish commit is a successful save, not a false canceled
result. An indexing failure reports the saved path; it does not delete the copy.

The new PNG contains its own sRGB ICC profile and embedded PhotoLibrarian XMP
(`https://photolibrarian.app/ns/photo-fuse/1.0/`): ordered source basenames and
SHA-256 digests, donor-to-base transforms in oriented preview pixels/radians,
chosen region coordinates/frame indices (zero is the base), dimensions, normalized
orientation and a baked-composite marker. No absolute source paths are stored.
Tags, captions, ratings, copyright claims, capture dates, GPS, thumbnails, edit
recipes and face identities are **not copied** from any source. Those may be
misleading for a multi-source composite; the workspace discloses this policy.
There is no output sidecar or catalog-only lineage to lose when moving the
copy. Source metadata remains with its original photo.

The output is indexed, revealed and selected in the grid, even outside the current
filter or watched roots, using the existing transient derivative reveal. Changing
browsing filters ends that temporary reveal. Add the output folder as a watched
folder for continued library browsing.

## Reproducible checks and remaining visual review

All automated fixtures are original deterministic generated data in per-test
temporary directories. They do not touch a live library, user photos, or network
services. No proprietary Photo Gallery code, UI assets, or algorithms are used.

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -- -class PhotoLibrarian.Tests.PhotoFuseTests -class PhotoLibrarian.Tests.FaceLibraryProcessorTests -class PhotoLibrarian.Tests.ImageViewerNavigationTests -class PhotoLibrarian.Tests.StabilizedVideoLibraryTests -parallelMode none -reporter verbose -showliveoutput
```

Fixtures cover translation/subpixel rotation, scale/parallax/occlusion rejection,
flat/localized/repeating texture, color disagreement, independent region choices,
bad seams, bounds/overlap, missing/unsupported/corrupt sources, all nonidentity
EXIF orientations, transparency/high bit depth/animation rejection, stale metadata,
leases, cancellation, concurrent naming collisions, immutable preview bytes,
lossless stored output pixels, portable lineage, and library reveal/selection.

On the development Windows x64 machine in Debug, the 384-pixel synthetic pairs
registered in approximately **19-35 ms**, with maximum landmark errors under
**0.004 pixels**. A 1600-pixel rotated/translated fixture also stayed under
**0.004 pixels**. Four 1601-square PNG sources, including fixture encoding,
downsampling, registration, preview and tagged export, took approximately
**1.2-1.35 seconds**; the whole test process reported a **233-282 MiB** peak working set.
These are synthetic measurements, not guarantees for real faces, image codecs,
hardware, memory pressure or arbitrary handheld bursts.

The isolated `PhotoLibrarian.AccessibilityTests` host accepts `--photo-fuse` for
full-viewer state tests or `--photo-fuse --preview` for interactive synthetic-only
review (`--dark` selects Dark). It creates/cleans its own fixtures and never
initializes the user's library. Launch it through `winapp run` with an isolated
test package identity.

The isolated host exercises single-photo entry, discovered face boxes and nearby
replacement thumbnails, preview/Original undo and new-copy save on exit. Core
regressions additionally cover 12-candidate discovery, independently chosen faces
from different donors, manual misses, stale donors, default recipes and a
25-photo time-window query.

**Manual visual review with a representative, consented local burst is still
needed.** Inspect eyes, hair, occluding hands, shoulders, fine edges, expression
changes, exposure/white-balance boundaries, and all feather bands at 1:1. Check
Light/Dark/High Contrast, keyboard face selection, manual face drawing, scrolling
and filesystem permissions. No identity-matching or
photometric quality metric can prove a natural-looking composite; do not approve
an output with visible seams or geometry errors.
