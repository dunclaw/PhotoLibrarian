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
limit. Each donor is decoded independently, one pair at a time, and each proposed
face replacement is aligned and seam-checked separately.
The search reports progress and incompatible shots; clicking Fuse again cancels a
search and exits (saving any previously accepted changes).

Click a face box to see thumbnails of faces near its registered position in the
other shots. Proposals must have similar size and lie within 1.25 target-face widths/
heights of the inverse-mapped center. They are **spatial suggestions, not verified
identities**. Choose one to preview the replacement on the full image, or choose
**Original** to undo it. A bounded crop around the face leaves room for an adaptive
seam through matching hair, clothing or background. Discovery checks that seam before offering a
replacement, so the flyout does not advertise a face that cannot actually be
composited. Overlapping actual masks or a failed revalidation leave the accepted preview
unchanged. Ordinary registration, seam, unsupported-format and pending-recipe
rejections are explicit results, not exceptions used to control discovery.

Use the viewer's normal zoom/1:1/pan controls to inspect eyes and seams. Click
**Photo Fuse again** (the button reads **Save and exit Fuse**) to save the accepted
preview as a new copy in the **base photo's format** beside that photo and leave
Fuse mode: JPEG stays JPEG (including `.jpg`/`.jpeg`), and PNG stays PNG. The original is
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
- Prefer the same viewpoint, exposure and white balance, with enough matching
  detail around each head. Whole-frame motion is only a **search/initialization
  hint**, never an accepted output warp. It may be absent. A face replacement must
  independently measure translation, rotation and scale on local detail. Small
  expression/blink changes use facial detail first; changed head poses can instead
  use surrounding stationary hair, clothing and background, excluding the chosen
  facial pixels and other people's face boxes from that fit. Unrelated movement or
  parallax elsewhere in the image need not invalidate a locally reliable swap.
  Perspective/nonrigid changes around the chosen head remain unsupported.
- Local analysis is at most 256 pixels on its longest edge, with at most 4096
  spatially distributed fitting samples and 30 robust iterations per seed. Faces
  must be at least 24 preview pixels across both dimensions. Similarity scale is
  bounded to 0.75-1.33; correction motion is bounded to 35% of the smaller face
  dimension and 15 degrees around its initializer. At least 80 textured samples
  and 80% robust inliers are needed. Independent full-resolution RGB checks require
  at least 400 samples and 70% agreement within 24/255 in every channel, on the
  facial detail or surrounding context used by that mode. Competing local fits
  with average clipped RGB errors within 0.5/255 but face-corner mappings more than
  1.5 pixels apart are rejected. No guessed transform is rendered.
- A minimum-cut seam searches a crop extending one face width/height on each side,
  on a grid of at most 192 pixels per dimension. Its costs include the entire
  possible feather neighborhood, so it can route outside changed facial pixels
  rather than placing a cut immediately beside them and fading back into them.
  Target and mapped donor elliptical face cores must remain opaque. Other base
  face boxes, image/crop edges and uncovered donor pixels stay original. A neighbor
  obscuring the protected core rejects the swap rather than partially replacing
  that person. The **actual** 2-8-pixel feather band must still have 90% agreement
  within 24/255 in every channel, with measured coverage proportional to face size.
  Overlapping crop rectangles are allowed; overlapping nonzero masks are not.
- Output is **at most 1600 pixels on the longest edge**, never enlarged, opaque
  8-bit sRGB in the base photo's format. JPEG uses the app's existing **95% quality,
  4:4:4 (no chroma subsampling)** export settings; PNG is lossless.
  The base is not globally cropped or warped. Donor samples are
  inverse-mapped into the base, bilinearly interpolated, and feathered in linear
  light over 2-8 pixels. Pixels outside the actual nonzero masks are unchanged.
  Uncovered donor pixels cannot be included in a replacement region.
- Compressed sources are limited to 100 MiB and source dimensions to 100
  megapixels. Discovery holds only one base/donor pair at once, regardless of burst
  size. Each selected-donor session has a 192 MiB decoded-memory budget. Changing a
  selection retains the previous accepted session until the new one passes, so
  at most two sessions' decoded snapshots coexist (384 MiB), plus
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

The saved copy encodes the **accepted preview pixel buffer**, not a second render.
PNG preserves those pixels exactly; JPEG introduces normal lossy compression.
JPEG/PNG encoding and ICC/XMP tagging finish in uniquely named temporary files in the
destination folder before a no-overwrite rename publishes the image. Existing
same-stem files, other image extensions, and metadata sidecars cause a numbered
name to be chosen. Failed/canceled work removes only its own staging files.
Cancellation after the publish commit is a successful save, not a false canceled
result. An indexing failure reports the saved path; it does not delete the copy.

The new copy contains its own sRGB ICC profile and embedded PhotoLibrarian XMP
(`https://photolibrarian.app/ns/photo-fuse/1.0/`, payload version 2): ordered source
basenames and SHA-256 digests, actual per-face donor-to-base similarity transforms,
chosen crop coordinates/frame indices (zero is the base), dimensions, normalized
orientation and a baked-composite marker. Each crop includes its zlib/base64
row-major alpha8 mask. Transforms are centered on `((width-1)/2,(height-1)/2)`;
translation uses preview pixels, rotation radians, and scale is `1+ScaleDelta`.
Source-level motion estimates are explicitly labeled search hints, separate from
the measured rendering transforms. Complete UTF-8 lineage is limited to 4 MiB
before a preview can be accepted; no arbitrary face-count cap replaces that
portable-metadata budget. JPEG packets larger than a single APP1 segment use
standard Adobe Extended XMP chunks so masks/lineage remain embedded, not truncated
or moved to a separate sidecar. No absolute source paths are stored.
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

The standard automated fixtures are original deterministic generated data in per-test
temporary directories. They do not touch a live library, user photos, or network
services. No proprietary Photo Gallery code, UI assets, or algorithms are used.

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -- -class PhotoLibrarian.Tests.PhotoFuseTests -class PhotoLibrarian.Tests.PhotoFuseSubjectTests -class PhotoLibrarian.Tests.FaceLibraryProcessorTests -class PhotoLibrarian.Tests.ImageViewerNavigationTests -class PhotoLibrarian.Tests.StabilizedVideoLibraryTests -parallelMode none -reporter verbose -showliveoutput
```

Fixtures cover independent subject translation/subpixel rotation/scale despite
unrelated background movement, pose changes requiring surrounding-context fits,
adaptive feather-neighborhood seams, opaque facial cores, exact neighboring-face
preservation, disjoint masks in overlapping crops, actual mask overlap rejection,
decompressed portable masks/actual transforms, metadata budgets, forbidden
search-hint rendering, saveable accepted previews after a failed replacement,
source-format/extension preservation with mixed-format donors, JPEG container and
quality checks (95%/4:4:4 RMS error at most 3/255 on these fixtures), large embedded
Extended XMP roundtrips, and whole-frame scale/parallax/occlusion rejection,
flat/localized/repeating texture, color disagreement, localized moving foregrounds,
nonthrowing rejection/lease cleanup, exclusion of broken replacement choices, independent region choices,
bad seams, bounds/overlap, missing/unsupported/corrupt sources, all nonidentity
EXIF orientations, transparency/high bit depth/animation rejection, stale metadata,
leases, cancellation, concurrent naming collisions, immutable preview bytes,
lossless stored output pixels, portable lineage, and library reveal/selection.

On the development Windows x64 machine in the isolated `FuseReview` configuration,
the 384-pixel subject similarity fixtures aligned and seam-checked in approximately
**64-78 ms**, with maximum landmark errors under **0.009 pixels**, including
scale factors 0.92/1.08 and unrelated background movement. The retained whole-frame
fixtures registered in approximately **43-45 ms** and under **0.003 pixels**;
a 1600-pixel rotated/translated fixture stayed under **0.002 pixels**.
The four-source fixture also exercises downsampling, registration,
preview and tagged export and records elapsed time and peak process working set.
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

### Real-burst reproduction

The reported six-face group-photo case was reproduced locally, without opening
the live library or changing source photos/sidecars. Previously all 11 capture-time
neighbors were rejected: some by per-tile RGB agreement, some by fitting unused
rolling-shutter shear, and most before refinement of broad coarse-search minima.
The conservative whole-frame/rectangular-seam revision allowed two of six faces
from one neighbor, which still made the feature inadequate for this case.

The subject-local/adaptive-seam revision now produces **32 accepted face previews
from nine of the eleven neighbors, covering all six detected base faces**. Both
nearest later shots offer all six faces. The formerly blocked turned-head
replacement is accepted through the independently measured context path.
Two face replacements were also rendered together with disjoint masks. The
accepted output's stored PNG pixels equaled the reviewed buffer in the original
rework checks; the source-format regression now checks JPEG compression error
separately rather than claiming lossless JPEG pixels. Neighboring face
pixels remain exactly original, and source/sidecar hashes and absence of new
sidecars are checked. Private local before/donor/composite crops were visually
reviewed without uploads and are not committed.

In this run, one base/donor preparation took approximately **0.19-1.36 seconds**,
and measuring/rendering all six face proposals took **1.44-2.02 seconds** per
neighbor, excluding detection, diagnostics, export and visual-capture overhead.
The fitting sample budget roughly halved the reproduction's elapsed time without
loosening its RGB/seam gates. These are development-machine observations, not
performance guarantees.

This resolves the supplied burst's no-usable-replacement blocker, not every
possible handheld burst. Several later faces/shots remain rejected for local
mismatch, ambiguity, occlusion or unsafe seams. User visual approval is still
essential, especially for large head-pose changes and close neighboring people;
inspect each offered composite before saving.

An optional diagnostic regression reads only the explicitly supplied local photo
and JPEG capture-time neighbors, uses already-installed local models without
downloads, renders in memory, exports one accepted preview to a disposable test
directory, and verifies unchanged source/sidecar hashes. It does not initialize a
library or persist a face scan. It is not enabled by the standard test command:

```powershell
$env:PHOTOLIBRARIAN_FUSE_TEST_PHOTO = 'C:\TestPhotos\burst\base.jpg'
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -p:Configuration=FuseReview --no-restore -- -class PhotoLibrarian.Tests.PhotoFuseLocalBurstTests -parallelMode none -reporter verbose -showliveoutput
Remove-Item Env:\PHOTOLIBRARIAN_FUSE_TEST_PHOTO
```

No user-photo bytes or identity data are included in committed fixtures or
documentation.
