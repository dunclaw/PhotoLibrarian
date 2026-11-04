# Spot retouch

Open a supported raster photo in the editor and turn on **Spot retouch**. Choose a
brush radius of 2-24 **source pixels**, then click a blemish or drag a short stroke
that completely covers it. Zoom in for precise placement; middle-drag pans while
retouch is enabled. The outline shows the selected radius and stroke path.
Turn the mode off to return to normal left-drag panning.

This is a local **smooth-surface blemish tool**, not general content-aware fill.
Good candidates are small spots on a plain wall, smooth sky or another locally
flat/gradually shaded surface. It does not reconstruct hair, fabric, foliage,
skin texture, text, structural edges or large objects. Inspect the result and
undo it if it looks unnatural. A smooth surrounding ring cannot establish
whether the selected interior contains meaningful detail.

## What is supported

- JPEG, PNG, TIFF and BMP use the editor's existing oriented, full-resolution
  decode and save paths. GIF and other unsupported formats are not enabled.
  RAW is not retouched: first export an SDR JPEG/PNG derivative, then edit that.
- Strokes address EXIF-oriented source pixels, **before** editor rotation,
  tone/detail effects and viewport scaling. Zoom, pan, display DPI and rotation
  do not change the brush's source-pixel radius.
- Each accepted stroke is one undo/redo step. Reset removes all pending strokes;
  Undo can restore them. A rejected or cancelled gesture creates no history.
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

## Algorithm and explicit limits

The mask is the union of circular brush dabs connected by capsules, including
between sparse pointer events. A separate two-pixel ring surrounds the entire
mask. Each BGRA color channel is fit to a least-squares plane using only that
ring; the plane fills only masked pixels. Earlier accepted strokes are applied
in order before evaluating a later stroke.

All selected and ring pixels must be fully opaque and the complete ring must
lie inside the image. Each channel's ring fit must have RMS residual at most
4/255, maximum residual at most 12/255, and slope at most 4/255 per pixel on
either axis. Otherwise the **whole stroke** is rejected without any pixel writes,
and an inline warning explains why. These gates reject tested high-contrast
edges and texture; they are not a semantic or perceptual quality guarantee.
There is no texture synthesis, model, network request, download or external
executable dependency.

Work is bounded to 24 million source pixels, 32 strokes per save, 128 recorded
points per stroke, a 192 x 192 pixel local work extent (including the ring), and
8,192 masked pixels per stroke. Exceeding a limit rejects the stroke rather than
silently truncating it. Preview caches a source-sized retouched bitmap, reused
when tone/detail/rotation controls change. Rebuilding needs an owned BGRA8
scratch buffer (up to 96 MB) in addition to the editor's existing source,
histogram pixels and GPU bitmap resources. Cancellation is checked per mask row.

## Verification

`SpotRetouchTests` uses only generated images and temporary backup/sidecar
directories. Flat and planar-gradient fixtures, both click and drag, must return
within one 8-bit channel level of the clean reference with exact preservation
outside the mask. Other tests cover edge/texture/alpha/boundary rejection,
resource bounds, recipes, cancellation, history, RAW/format gating, rotated and
EXIF-oriented preview/save equality before encoding, lossless PNG reopen and
original-backup recovery.

```powershell
dotnet build src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 --no-build -- -class PhotoLibrarian.Tests.SpotRetouchTests
```

For safe UI inspection, the existing isolated accessibility host has a
`--retouch` fixture. It injects an editor-only ViewModel, a generated PNG and a
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

UIA IDs are `EditorRetouch`, `EditorRetouchRadius`, `EditorPreview`,
`EditorRetouchStatus`, `EditorUndo`, `EditorRedo`, `EditorReset` and `EditorSave`.
Click the center blemish, inspect Undo/Redo and save, then close the **fixture
window**. The host writes `AppX\retouch-ui-results.json` with save/backup/recipe
checks and removes its temporary image directory. This fixture has also been
exercised with real pointer clicks, Undo/Redo and Save, with before/after
screenshots inspected for layout and the removed spot.
