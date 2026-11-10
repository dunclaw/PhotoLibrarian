# Spot retouch

In full-photo view, click **Retouch** in the main toolbar (beside Adjust and
Red eye). This opens a full-width retouch workspace without the Adjust sidebar.
Brush controls, Undo/Redo, Save and Close are in the top toolbar; opening Adjust
is not required. Choose a brush radius of 2-24 **source pixels**, then click a
blemish or paint a stroke that completely covers it. Dragging shows one continuous,
translucent painted selection, not a series of rings. **Release to heal** the
entire selection from its surroundings. Zoom in for precise
placement; middle-drag pans while
retouch is enabled. A contrasting circular brush cursor follows the pointer
before and during a stroke, showing the actual radius at the current zoom/DPI.
Changing the brush radius or zoom updates the outline immediately.
Turn the mode off to return to normal left-drag panning.

This is a local **small-blemish inpainting tool**, not general object reconstruction.
Good candidates are small spots on walls, sky, or locally repeating surfaces.
Ordinary photo noise, JPEG artifacts and uneven shading do not prevent an
operation. The fill copies matching patches from nearby unpainted pixels and
blends their colors into the boundary, retaining local texture and continuing
simple edges instead of only smoothing them. This is not a generative model:
unique hair, faces, text, complex structures, or large missing objects cannot
be reconstructed reliably. Repeated patterns, mismatched detail, seams or
smoothed areas are still possible, especially without a suitable nearby source.
Inspect the result and undo it if it looks unnatural. Paint over the
whole unwanted feature: anything left outside the brush remains a source for
the fill, including any unpainted part of the blemish.

## What is supported

- JPEG, PNG, TIFF and BMP use the editor's existing oriented, full-resolution
  decode and save paths. GIF and other unsupported formats are not enabled.
  RAW is not retouched: first export an SDR JPEG/PNG derivative, then edit that.
- Strokes address EXIF-oriented source pixels, **before** editor rotation,
  tone/detail effects and viewport scaling. Zoom, pan, display DPI and rotation
  do not change the brush's source-pixel radius.
- Each completed stroke is one undo/redo step. Reset removes all pending strokes;
  Undo can restore them. An unsupported or cancelled gesture creates no history.
- Escape, pointer cancellation/capture loss, leaving the image, resizing,
  changing adjustments/zoom, navigation and closing cancel the pending gesture.
  Wheel zoom is suppressed while drawing. Escape also cancels a stroke still
  being evaluated. Processing runs off the UI thread and blocks competing edits.
- Save uses the same deterministic source-resolution CPU stage as the preview,
  then the existing Win2D graph and encoder. JPEG re-encoding remains lossy.
  Saving backs up the source through `OriginalBackupService` before rewriting,
  clears baked raster recipes and clears pending history. **Make a copy before
  editing** remains available when a separate editable variant is preferred.
  Original backups use the existing application backup location, not the cache DB.
- Versioned `pledit:SpotRetouchV1` recipes participate in existing XMP sidecar
  serialization, deep cloning and recipe clearing. Pending recipes must travel
  with their image; baked retouch lives in the image pixels. No critical edit
  state is stored only in SQLite. A mismatched source size or malformed recipe
  fails explicitly; a failed loaded retouch preview disables Save until reset/undo.

## Algorithm and supported behavior

The mask is the union of circular brush dabs connected by capsules, including
between sparse pointer events. A deterministic exemplar fill advances inward
from the boundary, prioritizing patches with known context and strong edges.
It matches 7 x 7 color patches against fully unpainted, opaque sources within
four brush radii plus three pixels of each target. Matching subtracts the mean
color difference, allowing the same structure under gradually changing lighting.
Copied pixels are shifted by that mean difference. Newly filled pixels can guide
later matching, but **never become source exemplars**; the entire original mask
is excluded, regardless of how much has been filled. No color in the unwanted
interior is read as source content.

Thin images or heavily painted surroundings use smaller source patches, down
to individual available pixels. If a target is deep inside a wide mask, matching
can search around a previous patch's donor rather than imposing a selection-size
limit. Image-edge footprints are clipped to available pixels. Sources with
transparency are skipped; transparency elsewhere in the search area does not
reject an otherwise opaque selection.

A Poisson solve blends the synthesized gradients into the real, fixed unpainted
boundary. Interior gradients come from the assembled patches; outward boundary
gradients come from unpainted donor neighbours when available. The existing
preconditioned conjugate-gradient solver is reused. Flat/linear surfaces remain
smooth, while copied edges and texture are no longer forced into a harmonic
blur. Earlier strokes are applied in order before filling a later stroke.
Existing `SpotRetouchV1` masks remain compatible and pending recipes replay with
the improved algorithm; already baked pixels are unchanged.

### Library choice

The app already bundles a minimal OpenCV 4.12.0 build for panorama stitching,
but not its `photo` module. OpenCV's standard
[`inpaint`](https://docs.opencv.org/4.12.0/df/d3d/tutorial_py_inpainting.html)
offers Telea weighted-neighbour filling and Navier-Stokes edge/PDE continuation,
not exemplar texture copying. Adding that module or a new wrapper would not
directly address the missing texture. This managed CPU implementation adds no
native modules, packages, models or architecture-specific deployment changes.
It is a local patch heuristic, not a claim of parity with model-based object
removal or a measured comparison against OpenCV on real photographs.

The former planar-fit residual/texture rejection has been removed. There is no
texture/edge classifier that refuses otherwise valid painting. Image-edge
strokes use the available neighbours. The painted pixels and their immediate
boundary must be opaque, and some unpainted source pixels must remain. These
expected unsupported cases return a typed result and show an inline message;
neither they nor normal out-of-image gesture cancellation throw exceptions.
Malformed persisted recipes, actual allocation/GPU/decoder failures and numerical
solver failures are errors, never silently saved as unretouched success.
There is no model, network request, download or external executable dependency.

There is **no megapixel/image-size gate or stroke-count limit**, and no recipe
length limit that indirectly caps the number of edits. There are also no fixed
point-count, stroke-length, local-extent or selected-area caps. Actual allocation,
decoder or GPU platform errors are reported explicitly, not replaced with
arbitrary image-size cutoffs.

The brush radius control is 2-24 source pixels. Preview caches a source-sized retouched bitmap, reused
when tone/detail/rotation controls change. Each new stroke builds on the cached
result rather than replaying all previous strokes; Undo/reload replays the
stored recipe. Rebuilding needs an owned BGRA8 scratch buffer (four bytes per
source pixel) in addition to the editor's existing source, histogram pixels and
GPU bitmap resources. Synthesis and blending use arrays proportional to the
painted area and its bounding rectangle, not a second full-photo search image.
Work and memory increase with the actual selection, not an imposed image
dimension limit. Patch matching costs more CPU time than boundary-only filling,
particularly for wide/detailed masks or sparse available sources; very large
strokes can be slow. Cancellation is checked while rasterizing, preparing the
fill, searching each donor row, and iterating the solver.

## Verification

`SpotRetouchTests` uses only generated images and temporary backup/sidecar
directories. Flat and planar-gradient fixtures, both click and drag, must return
within one 8-bit channel level of the clean reference with exact preservation
outside the mask. Generated noisy/nonplanar backgrounds with a painted red
stoplight and dark housing must heal within 7 RMS 8-bit levels of the clean
reference, for both sparse and >128-point gestures. JPEG compression/noise is
also tested through preview and safe save. GPU-rendered selection feedback must
be a uniformly filled capsule, including self-overlaps, with no internal rings.
High-contrast vertical, horizontal and diagonal edges and a repeating checker
texture must reconstruct within 12 RMS 8-bit levels while leaving every pixel
outside the mask and all alpha values unchanged. Differently colored painted
interiors and sparse/dense representations of the same mask must produce
identical results. Narrow images, a broad mask with only one unpainted source
row, and transparency outside the boundary exercise clipped/adaptive patches,
donor propagation and source exclusion.
The four structure fixtures were also run against the original harmonic-only
fill: all failed the 12-level threshold, with approximately 39 RMS levels on the
three edges and 78 on the checker texture. These are generated, repeatable
regressions, not a representative photographic-quality benchmark.
Other tests cover image-edge filling, typed
transparency/no-source outcomes, long strokes, recipes, cancellation, history, RAW/format gating, rotated and
EXIF-oriented preview/save equality before encoding, lossless PNG reopen and
original-backup recovery. A generated 8256 x 5504 (~45 MP) full-resolution source
is retouched through the shared renderer. An 80-stroke history/sidecar (>160 KB)
round-trips, undoes/redoes and saves, exercising both former caps.

```powershell
dotnet build src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 --no-build -- -class PhotoLibrarian.Tests.SpotRetouchTests
```

For safe UI inspection, the existing isolated accessibility host has a
`--retouch` fixture. It injects an editor-only ViewModel, a generated noisy-sky
PNG with a small stoplight, and a
temporary backup directory. It never constructs the library MainViewModel or
loads user preferences. Use a distinct test package identity to avoid interfering
with another grid-accessibility test session:

```powershell
dotnet build src\PhotoLibrarian.AccessibilityTests\PhotoLibrarian.AccessibilityTests.csproj -c Release -p:Platform=x64
Push-Location src\PhotoLibrarian.AccessibilityTests\bin\x64\Release\net8.0-windows10.0.22621.0
winapp manifest generate . --package-name PhotoLibrarian.SpotRetouchTests --publisher-name 'CN=PhotoLibrarianTests' --executable .\PhotoLibrarian.AccessibilityTests.exe --if-exists Overwrite
winapp run . --manifest .\Package.appxmanifest --exe PhotoLibrarian.AccessibilityTests.exe --args '--retouch' --unregister-on-exit
Pop-Location
```

UIA IDs are `RibbonRetouch`, `EditorRetouch`, `EditorRetouchRadius`, `EditorPreview`,
`EditorRetouchStatus`, `EditorUndo`, `EditorRedo`, `EditorReset` and `EditorSave`.
Click `RibbonRetouch`, then the center blemish, inspect the brush cursor at
different radii and Undo/Redo, and save, then close the **fixture
window**. The host writes `AppX\retouch-ui-results.json` with save/backup/recipe
checks, saved blemish-area RMS error (`healedAreaRms`, `centerHealed`), and
unchanged pixels outside the actual painted mask. It removes its temporary image directory. This fixture has also been
exercised with real pointer clicks, Undo/Redo and Save, with before/after
screenshots inspected for layout and the removed spot.
