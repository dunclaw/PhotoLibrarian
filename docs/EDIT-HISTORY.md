# Shared edit history

The main toolbar's **Undo** and **Redo** are available while viewing a photo.
Adjust and Retouch put compact **Undo** and **Redo** buttons alongside their
existing adjustment and brush controls; there is no separate ribbon row. The
toolbar is hidden in the grid view, without reserving a row.
Hover an action to see the next edit and its photo or batch size.
Use **Ctrl+Z** to undo and **Ctrl+Y** or **Ctrl+Shift+Z** to redo, including while
browsing the grid.
Text boxes, rich-text controls, search fields, and dialogs retain their native
keyboard behavior; typing is not a photo edit.

## Editor before/after views

The editor's **Compare** menu offers **Before recipes** (**B**),
**Side by side** (**N**), and **Split** (**M**). Press the same key or select
the active mode again to return to **After**. Unmodified letter shortcuts do
not run in text/numeric inputs, sliders, curve editing, or dialogs; native
typing and modified shortcuts are left alone.

Before is the oriented source image loaded when this photo was opened,
without *any* saved or pending recipe, including curves, effects, rotation,
or retouch. It is not the saved-recipe baseline used by Undo. Opening source
pixels remain available through saves in the same editor session.
Earlier edits already baked into a file **before opening it** are part of
that source: this feature does not reconstruct historical originals.
The existing content-keyed original backups have no reliable edited-file
provenance map; comparison does not scan or guess among them.
Closing/reopening or navigating to another photo starts a new opening source
and returns to After.

Side by side uses a shared zoom and pan for both panes; split shows Before
on the left and After on the right, with an accessible split-position slider.
Both preserve original geometry rather than rotating Before to match After.
Retouch painting is disabled while comparing; switch to After to paint.
These are transient viewing choices: they create no Undo steps, recipe writes,
photo writes, or preferences. Saved recipes stay in their existing portable
storage.

RAW development currently disables Compare with an explicit explanation;
it does not mislabel a developed or embedded RAW preview as an unedited original.
Comparison reuses the full-resolution Win2D preview and its normal platform
resource errors, without adding dimension/megapixel limits or full-size
comparison render targets. Opening pixels share the existing source bitmap
and pixel buffer until a reload requires separate before/after sources.
The retained buffer also recreates Before after GPU device loss, rather than
silently switching it to newly baked pixels.

## What is included

One chronological history follows these operations across photos and navigation:

- Viewer effect selections, including None, and quick Auto brightness,
  Auto color and Rotate left/right recipes.
- Saved raster adjustments and spot-retouch strokes, Revert to original, and
  RAW **Keep recipe**.
- In-place rotation, crop, straighten, and red-eye removal.
- Ratings, color labels, flags, captions, descriptive tag adds/removals, capture dates,
  date/time offsets, and GPS changes made through the metadata panel or photo
  commands.

A multi-selection metadata operation is one all-or-nothing history entry.
Slider/histogram drags and brush strokes are single preview gestures, not a
step for each pointer movement. The history controls operate on pending adjustment
gestures while that adjustment transaction has undo or redo steps. Save/Keep
compacts those gestures into one persisted application-history entry; Discard
drops only the pending transaction. Resolve pending work before stepping back
into earlier application history. Revert itself is an undoable persisted edit.
Failed saves keep the editor and its pending changes open.

A successful new persisted edit clears Redo. A failed operation, conflict, or
no-op does not clear it or consume an Undo entry. History controls and competing
photo commands are disabled during publication/replay. The library cache,
thumbnails, viewer pixels, metadata pane, and relevant navigation/filter counts
are refreshed before the operation is considered complete.

## Explicit boundaries

History lasts for the current application session; it is not restored after a
restart and has no arbitrary edit-count limit. Moving between photos, grid,
viewer, and editor does not discard persisted history.

File rename/move/delete (including duplicate removal), face/person review and
manual face boxes, accepting automatic tag suggestions, background recognition,
index/library maintenance, preferences, and navigation changes are **not**
history commands. They may change the state an earlier entry expects; Undo
will refuse a conflicting entry rather than skip it or overwrite newer work.
Resolve the conflicting change, or restart to begin a new history. Removing a
watched root is not permission to delete or undo files.

Exports, resizing, RAW derivatives, Make a copy, read-only crop copies,
Photo Fuse, panoramas, and video edits create separate files. Undo does **not**
delete those copies or reverse a sharing/printing action. Video pixels and RAW
sensor bytes are never rewritten by this history.

## Storage, concurrency, and recovery

Edits are prepared on private copies. Before publishing, every affected photo
and either XMP-sidecar spelling is checked using a complete SHA-256 fingerprint.
Changed files are locked against concurrent writers and renames while they are
replaced. Fully written temporary siblings are published by locked-handle rename,
without overwriting a concurrently created destination; this also supports photos
whose old bytes are still memory-mapped by a viewer. Undo/Redo checks the exact
expected bytes again: a missing, busy,
externally modified, or newly introduced sidecar blocks the whole batch. No
entry is silently skipped. Background recognition is paused during each
transaction and its outstanding portable metadata is flushed first; automatic
changes made later are subject to the same conflict boundary.

Original-backup behavior remains separate from history. Non-RAW originals are
backed up before editing; RAW recipes and unsupported embedded metadata remain
portable XMP. Moving photos together with their sidecars preserves saved edits;
the SQLite cache and history folder are not required to interpret them.
Crop preserves embedded descriptive metadata as well as remapping existing
portable face regions.

Before/after checkpoints use local disk space under
`%LOCALAPPDATA%\PhotoLibrarian\EditHistory\<session>\<transaction>`.
Unchanged bytes are not retained twice. Disk/permission failures are reported
without consuming history. Unneeded checkpoints are removed when Redo is
invalidated or the app shuts down normally. This is not a crash-atomic
multi-file journal: process termination or power loss during publication can
leave partial work. Failed compensation reports the retained recovery directory;
abnormal termination can also leave checkpoint directories behind. Keep those
files and the original backups when investigating recovery, rather than blindly
deleting them.
Interrupted publication can also leave `.PhotoLibrarian-edit-<id>.tmp` recovery
siblings beside the affected photos. Successful transactions remove these names.

## Developer validation

`EditHistoryServiceTests` covers cross-feature chronology, labels, atomic command
ownership, redo invalidation, retry, and cleanup. `FileEditTransactionTests`
exercises exact file/sidecar restoration, batches, locks, full-file concurrency
guards, refresh compensation, RAW immutability, and sidecar companion ownership.
`UnifiedEditPersistenceTests` runs real library/cache/portable-metadata edits,
navigation, pixel cropping, and stale-editor rejection. Editor navigation,
gesture-history, effect, metadata, and shortcut suites cover the integration.
`ViewerQuickEditTests` covers automatic tone/color behavior, recipe preservation,
quarter-turn composition, cross-photo history, no-op redo preservation, and
unreadable-sidecar safety.

`EditorComparisonTests` checks source-pixel fidelity despite saved/pending
recipes, clipped split endpoints, side-by-side pixels, rotation/zoom/pan,
shortcuts, RAW/busy gates, and unchanged recipes/history/files.
`.\tools\Test-PhotoGridAccessibility.ps1 -EditorComparison` runs the real editor
against temporary synthetic photos and sidecars only, exercising menu modes,
accessible split position, painting gates, save/reopen, and RAW unavailability.
Add `-Preview` to inspect the synthetic split view; no live library or personal
preferences are loaded.

The isolated `PhotoLibrarian.AccessibilityTests --edit-history` suite uses the
real toolbar and shortcut classifier with synthetic history only. It checks
accessible labels, grid row removal, viewer/editor visibility, history retention
across navigation, toolbar invocation, busy state,
both redo shortcuts, native TextBox undo, and quick edit invocation, accessible
labels, disabled states and ribbon layout at 1280 DIPs without opening a
personal library.

Run it with `.\tools\Test-PhotoGridAccessibility.ps1 -EditHistory`; add
`-SkipBuild` after building the host, or `-Preview` to leave the synthetic window
open for visual inspection.
