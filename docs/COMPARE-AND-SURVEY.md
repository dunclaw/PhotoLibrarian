# Compare and survey

Select two or more still photos in the grid, then use **Compare selected photos**
in the grid header or the selection's right-click menu. There is one command,
not separate two-photo and survey modes.
Mixed photo/video selections are not silently filtered. Candidates follow the
grid's displayed sort/group order.

The read-only workspace fills the existing application window, replacing the
library panels and toolbar until you return, without opening or resizing a
second window. Two candidates appear side by side; larger selections
automatically use an adaptive equal-cell grid. Removing candidates adapts the
layout too, so the remaining photo fills the available area. Wheel zoom and drag pan in any loaded
pane affect all panes. **Fit all** fits each photo independently. **100%** shows
one decoded source pixel per physical display pixel; subsequent zoom uses the
same source-pixel scale and relative image center for each photo. This is not
automatic image registration: different crops, aspect ratios and camera
positions will not line up exactly.

Tab to a photo viewport for keyboard control: plus/minus zoom, arrow keys pan,
Home fits all, and 1 selects 100%. All toolbar commands and per-photo actions
also support keyboard focus and screen readers. Escape or **Back to library**
returns to the original grid with its selection and layout intact.
Library editing, printing and slideshow shortcuts do not act on hidden
library controls while comparison is open.

**Remove** hides only that workspace candidate and releases its displayed
pixels. It does not delete a file, change grid selection, flag, rating, recipe,
or edit history. **Restore all** reloads the original selection in its original
order. It is valid to remove every candidate. Ratings and edits remain in the
existing library UI. The workspace is a snapshot, not a live edit preview;
reopen it or restore all to reload externally changed files.

## Media and resources

- Raster stills use the full-photo viewer's shared loader and installed Windows
  codecs. JPEG, PNG, TIFF and BMP apply pending PhotoLibrarian raster recipes
  through the existing full-resolution renderer, including rotation, effects,
  and retouch. Other Windows-decodable still formats use their codec appearance.
- CR2, CR3, NEF, ARW, DNG, ORF and RW2 reuse the editor's local RAW development
  and SDR tone-mapping pipeline, camera white balance and saved RAW
  exposure/white-point recipe. No second white-point adaptation is added.
  Camera support remains subject to the bundled decoder.
- GIFs show a still frame, motion photos show their still image, and multi-frame
  raster containers show the codec's default frame. No video, GIF animation or
  embedded motion playback occurs.

No network service, new dependency, photo write, database, or persistent
comparison state is involved. No photo-count or image-dimension cap is added.
Full-resolution displayed images remain in memory, and large survey selections
produce smaller cells and can exhaust RAM or the existing codec/GPU/runtime
limits. Loading is serialized to avoid parallel decode spikes. Each failed photo
stays visible with its actual error and **Retry loading**; removing candidates
can free display memory. Closing/removing cancels queued/RAW/WinRT work and
discards late results. The existing raster recipe GPU render is not interruptible
mid-pass, so it may finish before its resources are released.

## Verification

`PhotoComparisonTests` covers selection rules (including videos and duplicate
paths), uncapped candidate counts, session-only removal/restoration, and
normalized center geometry at different resolutions.

Run `.\tools\Test-PhotoGridAccessibility.ps1 -Platform x64 -Comparison` for the
isolated WinUI test host. It uses generated photos beside its build output, not
the application library/settings. It exercises recipe rendering, two-pane
layout, DPI-correct 100%, linked keyboard zoom/pan, localized accessible
controls, same-window full-area layout, adaptive two/many candidate layout,
return-to-library focus, removal/restoration, unchanged photo/recipe hashes, serialized survey
loads, explicit failure/retry, and cancellation on close. It also captures
light/dark comparison and survey screenshots in that host's output.
