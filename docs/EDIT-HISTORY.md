# Shared edit history

The main toolbar's **Undo** and **Redo** are available while browsing, viewing a
photo, and adjusting it. Hover an action to see the next edit and its photo or
batch size. Use **Ctrl+Z** to undo and **Ctrl+Y** or **Ctrl+Shift+Z** to redo.
Text boxes, rich-text controls, search fields, and dialogs retain their native
keyboard behavior; typing is not a photo edit.

## What is included

One chronological history follows these operations across photos and navigation:

- Viewer effect selections, including None.
- Saved raster adjustments and spot-retouch strokes, Revert to original, and
  RAW **Keep recipe**.
- In-place rotation, crop, straighten, and red-eye removal.
- Ratings, flags, captions, descriptive tag adds/removals, capture dates,
  date/time offsets, and GPS changes made through the metadata panel or photo
  commands.

A multi-selection metadata operation is one all-or-nothing history entry.
Slider/histogram drags and brush strokes are single preview gestures, not a
step for each pointer movement. The main toolbar operates on pending adjustment
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

The isolated `PhotoLibrarian.AccessibilityTests --edit-history` suite uses the
real toolbar and shortcut classifier with synthetic history only. It checks
accessible labels, browsing/editor visibility, toolbar invocation, busy state,
both redo shortcuts, and native TextBox undo without opening a personal library.

Run it with `.\tools\Test-PhotoGridAccessibility.ps1 -EditHistory`; add
`-SkipBuild` after building the host, or `-Preview` to leave the synthetic window
open for visual inspection.
