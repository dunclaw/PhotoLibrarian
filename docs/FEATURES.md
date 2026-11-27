# PhotoLibrarian features

Detailed feature descriptions and behavior. See the [README](../README.md) for the overview.

## Highlights

- **Compare selected photos** fills the existing window with synchronized zoom/pan and source-pixel 100% inspection. One command adapts automatically: two photos side by side, or a grid for larger selections. Temporarily remove candidates and restore them without deleting files or changing selection, ratings, recipes or edit history; **Back to library** returns to the original grid. Uses recipe-aware raster loading and local RAW development; see [media, controls and resource limits](COMPARE-AND-SURVEY.md).

- 🚀 **Custom virtualized grid** that handles 18 000+ items per folder smoothly. No `ItemsRepeater` layout cycles, no per-item bindings — just a `Canvas` with a recycled element pool.
- **Remembered grid layout** restores Group, Sort, and ascending/descending choices after restarting. Preferences stay local in `%LOCALAPPDATA%\PhotoLibrarian\grid-layout.json`; missing or invalid preferences use the existing defaults (no grouping, Date Taken, descending). Filters, search, and photo selection remain session-only.
- **Color labels and random sort**: use the information pane or 6–9 for Red, Yellow, Green, and Blue on the selection. Press the same key again to clear when all selected items already have that label; otherwise it applies to all. Filter by color in **Filter**. Labels use standard `xmp:Label` embedded in supported images, or in an XMP sidecar for formats such as RAW that cannot be updated in place. Move sidecars with their photos; unrecognized labels are displayed and preserved until changed. If an embedded write fails, the edit reports the error instead of creating a sidecar. Random sort keeps the relative order of existing paths across refreshes, filtering, search, and navigation; new paths join that order, renames receive a new position, and **Reshuffle** changes it. The sort choice is remembered, but a fresh random order is generated on each app restart. Grouping still takes precedence.
- 🗂️ **Windows-style folder tree** with multi-checkbox selection plus parallel **Date**, **People**, **Tag**, **Flag**, and **Color labels** trees; the sections combine as a union filter. Checked color labels are remembered and their counts cover the watched library; the Filter box and search further narrow the union.
- 🏷️ **Hierarchical tags** (`people/family/kids` indexes `people` + `people/family` + `people/family/kids` so you can filter at any level).
- **Not tagged** under Browse > Tags shows indexed photos and videos with no tags, using the same definition as Filter > Missing metadata > Untagged. Its live count is library-wide (like other Browse counts), independent of selection, refinement, and search. Checking it adds these items to the navigation union; refinement and search still restrict the union. The check is remembered across restarts and tag-tree refreshes.
- ⚡ **SQLite metadata index** + Windows native thumbnail cache for instant viewport-aware loads.
- **Search current photos** in the header finds filename, caption/title, tags, camera make/model, and people using a local SQLite FTS5 index. The left-pane selections build a union; header refinement filters and search further restrict it. Suggestions use that same scope, and results keep the grid's selected sort and grouping. Type for suggestions and press Enter or select a suggestion to apply search. Words match prefixes (case/accent insensitive); multiple words must all match. Field queries such as `rating:>3`, `flag:pick`, `iso:>800`, `camera:x2`, `date:2026-04`, `keyword:mountains`, and `person:"Jane Doe"` can be combined with ordinary search words; see [field syntax and matching rules](SEARCH.md). Clearing search restores the selected, refined set. For a global search, select all photos and clear refinement filters first. Existing libraries get filename, camera, tag, and people search immediately; the next background scan automatically populates captions/titles from photo metadata.
- ✏️ **Multi-select metadata panel** — rating, caption, tags, and capture date edit *every* selected image at once, with "(n of m)" hints when values differ and a date-shift mode for time-zone fix-ups. Selected videos also show available duration, dimensions, bitrate, frame rate, codec, capture/encoded dates, camera, GPS, title, album, artist, and year from local Windows properties.
- **Map links** — click **Open map** beneath the geotag editor, or valid latitude or longitude in the information panel, to open the saved coordinates in a browser. Choose Google Maps (default) or Bing Maps in Settings. Opening a link sends the coordinates to that provider; no map request is made until you click.
- 🎨 **Win2D real-time editor** with Exposure / Brightness / Contrast / Highlights / Shadows / Saturation / Temperature / Tint / Noise reduction / Clarity / Sharpness / Levels / Rotation.
- **Spot retouch** on the full-photo toolbar fills painted blemishes using locally matching, unpainted texture patches with boundary blending. A circular brush cursor shows the radius at the current zoom; dragging paints one continuous selection and releasing heals it. Nearby texture and simple edges can be retained instead of only smoothed; ordinary photo noise and uneven shading are accepted. Source-resolution preview/save with undo/redo and original backups, without image-size, stroke-count or stroke-length caps. Best for small spots; repeated/mismatched detail is possible, and unique objects cannot be reconstructed reliably. Entirely local, with no new dependency. See [supported formats, behavior and verification](SPOT-RETOUCH.md).
- **Shared Undo/Redo** in the main toolbar and standard Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z shortcuts covers supported photo edits and metadata batches chronologically across photos. Pending adjustment gestures use the same controls until Save/Keep compacts them into one persisted step. Text controls retain native undo; conflicts never overwrite newer files. History is session-only and excludes file deletion, exports, face review, and automatic changes. See [supported edits, boundaries, and recovery](EDIT-HISTORY.md).
- **Previous/Next while editing** follows the grid's current filters, sort, and grouping, skipping videos and unsupported editing formats and wrapping at either end. Use the editor toolbar, Left / Right arrows, or Ctrl+Page Up / Ctrl+Page Down. Arrow keys still adjust focused sliders and input fields rather than changing photos. Navigation and Close offer Save (Keep recipe for RAW), Discard, or Cancel for pending changes; failed saves leave the current photo and edits open. RAW saves keep the XMP preview recipe, never overwrite the original. Each photo starts with its own adjustments and empty undo history. Filter changes do not discard an open edit: if the current photo is excluded, Next starts at the first eligible result and Previous at the last; with no eligible results navigation is disabled.
- 🖼️ **Make a copy before editing** from the viewer to keep the original and editable variant as separate library items.
- **Simple video stabilization** in the video editing ribbon exports a separate H.264/AAC MP4 and opens it immediately in the player, with no downloads or additional installs. The integrated stabilizer reduces small shakes and rotations with mirrored edges, using Windows' video codecs. Progress and cancellation are available; originals are never overwritten. See [usage, limitations and open-source options](VIDEO-STABILIZATION.md).
- **Photo Fuse** works inside the full-photo viewer: click its top-ribbon command, choose a detected face, and preview independently aligned replacements from nearby group photos. Reuses local face scans and the existing right-rail add-face UI; searches a capture-time window without a four-photo cap. Click again to save a new copy in the original format and exit, leaving originals unchanged. Supports opaque JPEG/8-bit PNG with adaptive, conservatively validated seams. Output is at most 1600 pixels; source tags, GPS and people identities are not copied. See [workflow, safety and visual-review limits](PHOTO-FUSE.md).
- **Edit video brightness, contrast, rotation, speed, and audio** directly in the single, left-aligned main toolbar row shown whenever a video opens, alongside Stabilize and Trim; commands overflow into the toolbar's More menu at narrower widths. There are no modal video editing, stabilization or trimming dialogs. Edits automatically rebuild a local H.264/AAC preview after a short pause; changing an edit cancels obsolete work. Progress and errors appear in the existing status bar; each control's tooltip explains its own action. Choose **0.5×** or **0.75×** playback speed for a slow-motion copy; original frames are retimed without generated in-between frames, and retained source audio stays synchronized at a lower pitch. Toggle **Stabilize** to include stabilization; its options are in a compact nonmodal flyout. Toggle **Trim** to show a single bar with draggable start/end handles and read-only time labels in the same toolbar row. Tone, rotation, speed, audio changes, stabilization and trimming compose into one prepared file. **Reset edits** restores neutral controls and source playback. Explicit full-range and limited-range metadata is honored, including decoders that establish the range with their first sample; untagged clips are treated as standard limited-range video. **Export copy** saves the exact prepared preview bytes as a new `-edited.mp4` copy. Rotation composes with the source's orientation using standard MP4 rotation metadata (players must honor it); it does not bake rotation into the pixels. Preparation re-encodes the clip and supports cancellation and progress; it needs temporary disk space plus space for the saved copy. Windows reports unsupported dimensions, codecs, or resource failures rather than an application-defined size limit. The source and its sidecars remain untouched. Descriptive tags/ratings/captions are copied into a new XMP sidecar, not geometry or face regions. Extra tracks, subtitles, HDR fidelity and camera-specific metadata are not preserved. No external tools, downloads, cloud processing, AI interpolation, AI effects, face tracking or music mixing are involved.
- **Add music** in the video toolbar replaces the original audio with a local mono or stereo MP3 or 16-bit PCM WAV at 44.1 or 48 kHz. Music starts at the beginning of the edited video and plays at normal speed, even when the video is trimmed or slowed. It stops at the video's end; a shorter track leaves silence instead of looping. The music file is not changed. Open **Music selected → Clear music** to return to the previous **Remove audio** setting or restore the original soundtrack.
- **Pixel motion photos** automatically loop in the full-photo viewer for supported JPEG Microvideo/Motion Photo v1 embedded MP4 layouts. Pause using the small header play/pause control or click the playing image to display the original still. Playback is local and read-only, with bounded in-memory loading and still fallback. Apple Live Photo pairs and HEIC/AVIF motion containers are not supported; see [formats, limits, and generated tests](MOTION-PHOTOS.md).
- **Synchronized photo navigation** follows the grid's current filters, sort, and grouping. Returning from full view selects and reveals the displayed item. If a filter removes it, full view moves to the first matching item (or closes when no items match).
- **Copy photo names and paths** using the copy buttons beside Filename (including extension), Folder, and Path (folder plus filename) in the information pane for a single selected item. Right-click a single grid item or the full-photo viewer and choose **Copy full path** to copy its path as plain text. The viewer menu also opens with Shift+F10 or the Menu key; detail buttons support Tab and Enter/Space. This does not copy or modify photo files; the existing grid **Copy** command still copies files.
- **Remembered Browse state** restores expanded branches and checked folders, dates, people, tags, and flags when the app reopens. The same checked-node filters are reapplied after the trees load; unavailable or renamed paths/tags are ignored. Preferences stay local in `%LOCALAPPDATA%\PhotoLibrarian\navigation-pane.json` and do not change photo metadata.
- **Batch resize and export** from the grid's right-click menu: export selected still images as JPEG copies using a maximum dimension or percentage, JPEG quality, an output folder and optional metadata stripping. Images are never enlarged; originals and existing files are never overwritten. Transparency is flattened onto white and only the first frame is exported. Unless stripped, common embedded captions, tags, ratings, capture date, author, copyright and camera name are retained; other metadata (including GPS and face regions) and sidecars are not copied. Progress, cancellation and per-file failures are shown.
- **Batch rename with a live preview** from the grid's multi-select context menu. Use `Trip_{####}` for sequential names, `{original}` for the original basename, and `{yyyy}`, `{MM}`, `{dd}`, `{HH}`, `{mm}`, `{ss}` for capture-date parts (modified date when capture date is missing). Extensions are preserved, XMP sidecars move with their photos, and existing or duplicate photo/sidecar destinations block the operation.
- 💾 **In-place metadata writing** for JPEG / TIFF / PNG / HEIC / JPEG-XR via `BitmapEncoder.CreateForInPlacePropertyEncodingAsync` — image bytes are preserved exactly; only the metadata block is rewritten. RAW formats (CR2/CR3/NEF/ARW) fall back to XMP sidecars (industry-standard limitation).
- 🔍 **Background indexing** with restartable re-index and a manage-folders dialog.
- **Incremental recognition** skips unchanged photo metadata during startup reconciliation and initializes face/tag models only when their watched-scope queues contain stale work. Metadata-only changes preserve current recognition; source-content, model/pipeline, and tagging-setting changes requeue the affected stages. See [recognition identity and refresh limits](LIBRARY-INDEX-MAINTENANCE.md#incremental-recognition).
- **Remove a watched folder** to stop monitoring it and hide photos outside the remaining watched roots from the grid, search scope, and Browse counts. Photos covered by another root remain available, respecting that root's subfolder setting. Removing a folder never deletes photos or sidecars and retains cached tags, people, flags, and other metadata for re-adding it, including on offline storage. Index repair is separate: only it can remove verified missing entries in still-watched, accessible locations. An open viewer closes if its photo leaves the watched library; an editor's unsaved work is not discarded. A newly exported copy outside watched roots is still revealed temporarily, but is not included in library-wide Browse counts; add its output folder for continued library browsing.
- **Grid-header review actions** use the photos currently displayed: people review limits face candidates to that set; **Find duplicates** finds exact and possible resized/re-encoded matches to those photos anywhere in the library. Hover the icon buttons for tooltips. Duplicate hashes are cached for repeat scans, and every removal requires an explicit keeper choice and confirmation. See [duplicate detection limits and review safety](DUPLICATE-DETECTION.md).
- 🔄 **Watched-folder updates** detect Explorer copies, edits, and deletions automatically; filesystem events are batched and only affected files are re-indexed.
- 🧠 **Opt-in local automatic content tags** with pluggable, checksum-pinned ONNX model profiles. A profile must be benchmarked and explicitly approved before generated labels can enter the distinct `Auto` tag hierarchy.
- **Suggest automatic tags for selected photos** beside the descriptive tags in the metadata pane. Suggestions run locally for the captured selection and stay beside the existing tags while the photo remains visible in the grid; nothing is saved until you review the suggestions and choose **Add suggestions**. The resulting tags appear in the existing tag list, where any unwanted tag can be removed directly. Changing the photo selection dismisses suggestions for the previous selection. The background auto-tagging setting remains off unless you enable it separately. Applying and opt-in background tagging store the complete tag/source/confidence snapshot and model pipeline version in embedded PhotoLibrarian XMP or the existing XMP sidecar, so a fresh index can restore those values. Indexing also detects changed portable tag snapshots on an otherwise unchanged photo and refreshes the cache. Background metadata-write failures leave the scan pending for retry and restore the prior generated tags where no newer tag changes occurred. Older provenance that existed only in a development SQLite cache is not backfilled; because no release has shipped, this is not a release/repair blocker and requires no live-library migration. Writes from this change forward persist portable provenance.


### Full-photo quick edits

Beside **Straighten**, the full-photo ribbon offers one-click **Auto brightness**,
**Auto color**, and **Rotate left/right**. Brightness replaces brightness,
exposure, contrast, highlights and shadows using the existing conservative
auto-enhance analysis. Color replaces temperature, tint and saturation using
near-neutral midtone pixels for a restrained white balance correction. Scenes
without neutral candidates receive only the existing conservative saturation
estimate; this is not scene-aware or RAW white balance.

Automatic settings come from an EXIF-oriented source sample (up to 1024 pixels
on its longest side), not from previously adjusted output. Repeated clicks do
not accumulate corrections. Each action preserves the other adjustment group,
detail settings, retouch strokes and effects. Rotation composes a 90-degree
turn with any pending angle without resampling the original.

These controls work on JPEG, PNG, TIFF and BMP stills, not RAW, videos or other
unsupported formats. They stay in the viewer, save portable pending XMP recipes
beside the unchanged photo, and refresh the viewer and grid. Keep the XMP with
the photo when moving it. Each changed recipe is one shared Undo/Redo step;
no-op auto actions preserve Redo. **Adjust** can fine-tune or bake the recipe
later. The source and full-resolution render have no new image-size limit.

### Photo effects

The full-photo view's top toolbar **Effects** button, immediately after **Adjust**,
opens a dropdown of live previews. Selecting an effect updates the photo in the
existing viewer without entering Adjust, replacing toolbars, or changing the
information pane. The dropdown offers **None**, black
and white, red/orange/yellow monochrome filters, sepia, cyanotype, and warm/cool
tints. Selecting a preset replaces only the effect; **None** keeps your other
adjustments. Selection, Reset, undo/redo, and pending portable XMP recipes share
the same effect state. Old recipes without an effect retain their appearance.
Viewer selection backs up the original and keeps a portable pending XMP recipe,
without overwriting photo pixels. Reopening the viewer applies that recipe;
selecting another preset replaces it rather than stacking effects. The separate
Adjust editor normally bakes the recipe through its existing save flow, which clears
pending edits after success. When a tone curve is present or has just been reset,
Save instead keeps all current adjustments as a portable XMP sidecar recipe.

The main toolbar's **Undo** and **Redo** (Ctrl+Z / Ctrl+Y or Ctrl+Shift+Z)
restore effect selections in the same chronological history as other supported
photo and metadata edits, including across photos. Effect replay leaves other
tweaks unchanged and does not overwrite photo pixels. Choosing the same effect
is not a new edit; a new persisted edit after Undo clears Redo.

Grid thumbnails render pending raster recipes too, both immediately after an
effect change/undo and after restarting. Edited photos bypass Windows/embedded
source thumbnails; originals and their embedded JPEG thumbnails remain untouched.
Unedited photos still use the fast Windows thumbnail provider.
See [shared edit history](EDIT-HISTORY.md) for pending-editor transactions,
supported operations, external-change protection, and restart boundaries.

Previews are generated locally from EXIF-oriented photo pixels, including
current adjustments, not sample pictures. Thumbnail decoding and rendering
run off the UI thread, with a 160-pixel maximum dimension and one render at a
time. Changing photos or adjustments cancels pending work and rejects stale
results. These small previews approximate spatial detail filters; the editor's
main preview, baked saves and recipe exports evaluate the shared graph at source resolution.
The Adjust editor, effect/recipe thumbnails, and recipe rendering convert embedded
ICC v2/v4 RGB profiles to encoded sRGB before adjustments. Untagged pixels retain
their existing interpretation. Creative color matrices preserve alpha and do not
add another sRGB transfer. Invalid profiles and profiles Windows cannot transform
in both directions produce an explicit error, not an unmanaged preview.

Baked saves convert working pixels back to the original profile, restore that
exact ICC data through the shared metadata writer, and verify it on a private
copy before replacing the photo. Failed conversion, profile preservation, or
publication leaves the source unchanged; existing backup and edit-history
transactions still apply. This is an 8-bit SDR sRGB working pipeline, not
wide-gamut editing, monitor-profile management, soft proofing, or output-profile
selection. Embedded/linked BMP profiles are explicitly unsupported by the Windows
BMP codec; ordinary untagged BMPs are unchanged.

Effects are explicitly disabled for RAW preview and RAW export; only the RAW
controls apply there. Dormant raster recipe fields are retained, not applied to
the RAW development. To use effects on a RAW photo, export an SDR derivative
first and edit that copy. RAW sensor bytes are never changed.

### Tone curves

The raster Adjust panel offers RGB, red, green and blue monotone cubic splines.
Levels and the adjacent Tone curve section both start collapsed. Expand Tone
curve and click the graph to add or select a point; drag to adjust it, or release
it near another point or either endpoint to remove it. Endpoints
cannot be removed. The curve stays smooth and monotone without overshooting its
points. Reset in the curve section resets the selected channel, and the main
Reset resets all channels. With the graph focused, Page Up/Down selects points,
arrows adjust, Insert adds and Delete removes an interior point.
The graph overlays the editor's
live histogram of the edited render, sampled on large images. Curves operate on display-referred raster RGB after
levels, before sharpening and effects. The RGB curve is applied before the
individual channels. Curve edits participate in gesture undo and shared saved
edit history. Viewer and grid previews use the saved sidecar; resized and share
copies render the recipe into the copy. Raster formats supported by the editor
are JPEG, PNG, TIFF and BMP. Curves are unavailable for RAW development; any
dormant raster curves in a RAW sidecar are retained but are not rendered by the
RAW preview or SDR export. Export an SDR derivative first to edit its curve.

### Clipping overlays

Use **Clipping** beside **Compare** to independently show clipped shadows (blue)
and highlights (red). These are **SDR output** diagnostics: any straight RGB
channel rounding to 255 marks a highlight; all three rounding to 0 mark a shadow.
They do not measure sensor saturation or recover lost detail. Transparent pixels
are ignored and partially transparent pixels keep their alpha.

The image diagnostic evaluates the final edited graph (including retouch, curves,
color adjustments, effects and rotation) before zoom/pan, on the **After** image
only. Before remains untouched in all comparison layouts. Supported raster
editing formats are JPEG, PNG, TIFF and BMP. Supported RAW previews use the
already-developed SDR result, without applying dormant raster controls again.
RAW has an SDR histogram but no raster Levels anchors.

The Levels and tone-curve histograms use the same rendered content, rather than a
tone-only approximation. Histograms and their left blue/right red clipping bars
are sampled previews (up to 512 pixels on the longest edge); small clipped regions
can be missed. The Clipping menu also reports sampled clipping in text. This
sampling budget does not limit image size or the full-resolution image overlay.
Histogram data is cached until the source, retouch result or adjustments change.
Toggle choices are temporary view state: they never change recipes, undo/redo,
metadata, baked saves, resized copies or RAW derivative exports.

### Noise reduction

The editor's **Detail > Noise reduction** slider runs entirely locally with no
model download. Zero is an exact bypass; increasing the value blends in a
5-by-5 bilateral filter with a source-pixel radius of two. Spatial and RGB
similarity weights reduce small color/luminance fluctuations while retaining
strong edges. Strong settings can soften fine texture; this is not AI detail
recovery, deblurring, or RAW sensor denoising.

Filtering precedes tone/color adjustments and sharpening. Preview and save use
the same full-resolution graph; zoom only scales its result. Image dimensions
and alpha are unchanged by denoising. Undo/redo, Reset, pending XMP edits, and
the existing original-backup/save flow include the new amount.

### Sharing and email

Select still photos and use **Share selected photos** in the grid toolbar, or
**Share or email...** in the right-click menu. Windows Share is the primary
handoff: choose an installed app that accepts files. PhotoLibrarian does not
send messages or upload anything itself. Available share targets depend on
Windows and installed apps; opening the sheet does not mean a message was sent.

Copies are JPEGs, at most 1600 pixels on the longest side (no enlargement),
at 85% quality, limited to 20 photos and 20 MiB per batch. Mail providers may
impose smaller limits. The existing resize/export metadata policy applies:
common embedded descriptive metadata is retained, but GPS, face regions and
sidecars are not copied. Animated files use their first frame; RAW/HEIC and
other formats require a working Windows imaging codec. Unsupported, missing,
or unreadable photos are reported before any successful copies are handed off.
Original bytes, metadata, sidecars and grid selection are unchanged.

**Email (attach manually)** is also available if Windows Share is unavailable
or has no suitable mail target. Standard `mailto:` URIs cannot reliably attach
files in Outlook or other clients. This fallback opens a short email draft and
the copies folder; **attach the JPEG files yourself before sending**. No
nonstandard `attachment=` parameters or local paths are placed in the URI.
If there is no default mail handler or Explorer cannot open, the dialog reports
that and provides the folder path.

Copies live under `%LOCALAPPDATA%\PhotoLibrarian\ShareCopies`, separately from
the library. Cancelled or abandoned preparations are removed immediately;
copies handed to another app remain for seven days to support delayed reads
and drafts. Expired batches are cleaned on the next launch/share; locked files
are logged and retried later. Save copies elsewhere for drafts kept longer.
Only generated batches are cleaned; unrelated folders and nested content are
never recursively deleted.

### Full-photo context menu

Right-click a photo in the full viewer, or press **Shift+F10** / the **Menu key**,
to access the same applicable single-file actions as the grid: open with,
file location, slideshow, wallpaper, rotation, resize/export, sharing, flag,
copy, copy full path, rename, delete and properties. Actions target the file on
screen, not a previous grid multiselection. Image-only actions are disabled for
videos; rotation is limited to supported still-image formats. Finish or cancel
an active crop, straighten, red-eye or people-tagging operation before using
file actions.

Viewer deletion requires confirmation and moves the file to the Recycle Bin;
Cancel leaves it unchanged. Rotation uses the existing original-backup path,
and export/share continue to create copies rather than overwrite the source.

### Batch rename

Batch rename numbers files from 1 in selection order. Patterns specify the basename only
(for example, `{yyyy}-{MM}-{dd}_{original}_{###}`). Renaming into occupied names,
including those of other selected photos, is not supported; use a distinct prefix first.
If a filesystem or database error occurs during a batch, the failed photo's moves are
rolled back and processing stops; earlier successful renames remain in place and the
error reports how many completed.

### Migrate legacy XMP sidecars

In **Settings > Migrate XMP sidecars...**, choose a local folder, review the
recursive preview, check the authorization box, and choose **Migrate ready files**.
The operation does not use SQLite as a metadata source. JPEG and PNG are supported
through lossless XMP packet replacement, not WIC transcoding: all non-XMP bytes,
including compressed pixels, EXIF orientation, ICC profiles, and other metadata,
must compare byte-for-byte before committing. Whole XMP properties are retained,
including tags/source/confidence/scan version, faces/review/representative choices,
flags, and unknown namespaces. Neither side wins a conflicting property; the
complete file/sidecar pair is skipped instead. Native EXIF/IPTC are not rewritten
or reconciled with XMP. Both sidecar naming conventions are recognized, but shared
basenames or simultaneous `photo.xmp` and `photo.jpg.xmp` are ambiguous and skipped.

RAW, video, TIFF, HEIC, JPEG-XR, reparse/cloud-placeholder paths, extended/duplicate
XMP, unsupported RDF, orientation overrides, oversized files/packets, and
PhotoLibrarian edit recipes are visibly skipped with their sidecars intact.
Recipe packets are retained because the raster editor currently clears baked
recipes from sidecars only; embedding them could double-apply later edits.
Inspection is bounded to 256 MiB per media file and 1 MiB per XMP packet; JPEG
packets must fit a single APP1 segment. This is intentionally narrower than the
ordinary metadata writer's supported formats.

Original media and sidecar bytes are flushed to unique recovery folders under
`.photolibrarian-migration-backups` beside the photos before any live write.
Staging uses the existing atomic packet writer. The final live write uses a
write/replace-denying handle, rather than a path-based atomic replacement, to
preserve the Windows file ID and prevent newer edits from being overwritten.
It is **not power-loss atomic**: a crash during that write may require manually
restoring `filename.original` from the reported recovery folder. On ordinary
write/read-back failure, the locked original is restored and verified. The
locked sidecar is deleted only after complete embedded packet and non-XMP byte
read-back verification; an access/deletion failure retains it. Keep backups until
you have reviewed the photos; they are never automatically pruned. Cancellation
finishes a committing file safely and leaves the remaining files untouched.
Results show each path, skipped/failed reason, and recovery location. Migration
never runs automatically, and the user remains responsible for authorizing it.

### Portable people tags

Face edits write MWG Region XMP plus Microsoft Photo 1.2 People Tags
(`MP:RegionInfo/MPRI:Regions`, an unordered bag of `MPReg:PersonDisplayName`
and `MPReg:Rectangle` structures). Microsoft rectangles use normalized
top-left X, Y, width, height; MWG retains its normalized center-based areas.
Unnamed detections and portable review decisions remain in MWG/PhotoLibrarian
metadata and are not exported as named Microsoft tags.

JPEG and PNG store both representations in embedded XMP; other formats, or
images that cannot be updated in place, use the existing XMP sidecar fallback.
Renaming/removing people replaces both sets of face tags, and sidecar cropping
clips/removes rectangles in both schemas. Existing non-face MWG regions and
unrelated XMP fields are preserved. Microsoft-only named rectangles can also
be imported; matching MWG faces take precedence without duplicating them.

The Microsoft representation follows the
[WIC People Tagging schema](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-people-tagging)
and the HTTP namespace URIs used by
[Exiv2](https://exiv2.org/tags-xmp-MP.html) and Windows Photo Gallery.
Windows exposes embedded names through `System.Photo.PeopleNames` (Explorer's
People property); consumers must support XMP sidecars to see fallback metadata.
Photo Gallery is discontinued, so compatibility is checked at the schema and
Windows property-handler level rather than by running Photo Gallery itself.

## Status

Active personal project — usable for daily browsing, tagging, and rating today. See [Roadmap](../README.md#roadmap) for what's missing relative to Windows Live Photo Gallery.

The bilateral slider is the current denoising feature; no advanced denoiser
has passed the detail-retention evidence gate yet. Motion/defocus deblurring
and RAW-aware tone mapping are also research-stage, not current editor
features. See the
[advanced editing feasibility decision](ADVANCED-EDITING.md) for the
recommended implementation order, model/dependency costs, format boundaries,
quality/render-parity gates, and the #119 model-free comparison results.

---
