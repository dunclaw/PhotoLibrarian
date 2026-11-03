# Photo Fuse

Select **two to four burst stills in the grid**, then choose **Photo Fuse** in the
bottom command area. The first shot in the grid's display order is the base.
Choose a donor, drag a rectangle on its aligned preview (or enter whole-pixel
coordinates), then **Add region**. Include unchanged background around the entire
face or subject. Up to eight nonoverlapping regions can come from different shots.
Remove a region to replace it; overlapping layers are deliberately unsupported.

Choose **Build composite preview**, compare the base, aligned donor and composite,
and use **1:1** plus scrolling to inspect every region and its border. Confirm the
review before **Save new PNG** enables. The folder picker is cancelable. Changes to
the rectangle or accepted regions invalidate the reviewed preview. There is no
automatic face selection, identity inference, or reuse of cached/MWG/Microsoft
face rectangles: the user chooses the spatial contribution in base-image
coordinates, even when a face is missing or different between shots.

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
  reject a usable burst; selecting another shot/region is safer than weakening them.
- Output is **at most 1600 pixels on the longest edge**, never enlarged, opaque
  8-bit sRGB PNG. The base is not globally cropped or warped. Donor samples are
  inverse-mapped into the base, bilinearly interpolated, and feathered in linear
  light over 2-16 pixels. Pixels outside the selected rectangles are unchanged.
  The aligned donor preview uses base pixels outside common coverage; such
  uncovered pixels cannot be included in a region.
- Compressed sources are limited to 100 MiB and source dimensions to 100
  megapixels. At most four 1600-by-1600 BGRA snapshots are retained (about 39 MiB
  total), plus bounded analysis planes, preview/export buffers, codec working
  memory and UI bitmaps. WIC may need source-resolution decode memory depending on
  the codec. Frames are processed sequentially; no full-resolution burst stack or
  external process/model is used.

## Persistence and safety

The source files and existing same-stem/image-qualified XMP sidecars are held
read-only until the dialog closes. A busy, missing, unreadable, unsupported, or
unregisterable shot fails preparation. Source bytes and sidecars are never written,
including on failure or cancellation. A newly appearing source sidecar invalidates
export. Any PhotoLibrarian or Camera Raw edit recipe is rejected conservatively:
finish/bake it into a separate copy first, rather than silently ignoring a crop,
rotation, unknown property, or incomplete edit.

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
misleading for a multi-source composite; the dialog discloses this policy before
export. There is no output sidecar or catalog-only lineage to lose when moving the
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
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -- -class PhotoLibrarian.Tests.PhotoFuseTests -class PhotoLibrarian.Tests.StabilizedVideoLibraryTests -class PhotoLibrarian.Tests.PhotoPrintLayoutTests -parallelMode none -reporter verbose -showliveoutput
```

Fixtures cover translation/subpixel rotation, scale/parallax/occlusion rejection,
flat/localized/repeating texture, color disagreement, independent region choices,
bad seams, bounds/overlap, missing/unsupported/corrupt sources, all nonidentity
EXIF orientations, transparency/high bit depth/animation rejection, stale metadata,
leases, cancellation, concurrent naming collisions, immutable preview bytes,
lossless stored output pixels, portable lineage, and library reveal/selection.

On the development Windows x64 machine in Debug, the 384-pixel synthetic pairs
registered in approximately **20-23 ms**, with maximum landmark errors under
**0.004 pixels**. A 1600-pixel rotated/translated fixture also stayed under
**0.004 pixels**. Four 1601-square PNG sources, including fixture encoding,
downsampling, registration, preview and tagged export, took approximately
**1.2-1.35 seconds**; the whole test process reported a **233-282 MiB** peak working set.
These are synthetic measurements, not guarantees for real faces, image codecs,
hardware, memory pressure or arbitrary handheld bursts.

The isolated `PhotoLibrarian.AccessibilityTests` host accepts `--photo-fuse` for
dialog state tests or `--photo-fuse --preview` for interactive synthetic-only
review (`--dark` selects Dark). It creates/cleans its own fixtures and never
initializes the user's library. Launch it through `winapp run` with an isolated
test package identity.

The isolated UI run passed eight real-dialog state assertions: fitting at current
DPI, no premature save
or confirmation, accepting a manual donor region, preview creation without export
authorization, requiring both 1:1 review and confirmation, and invalidating that
authorization after an edit.

**Manual visual review with a representative, consented local burst is still
needed.** Inspect eyes, hair, occluding hands, shoulders, fine edges, expression
changes, exposure/white-balance boundaries, and all feather bands at 1:1. Check
Light/Dark/High Contrast, keyboard/numeric region entry, pointer drawing, scrolling,
picker cancellation and filesystem permissions. No identity-matching or
photometric quality metric can prove a natural-looking composite; do not approve
an output with visible seams or geometry errors.
