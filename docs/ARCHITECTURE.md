# Architecture, conventions and metadata policy

Cross-feature ownership map, dialog conventions and the metadata storage policy. Update the map when ownership changes.

### Cross-feature architecture map

These are current extension points and representative callers, not a proposal to
consolidate every workflow. Update this map when ownership changes.

| Concern | Where to start and what to preserve |
| --- | --- |
| Portable metadata | [MetadataWriterService](../src/PhotoLibrarian.Core/Services/MetadataWriterService.cs) owns the embedded/sidecar decision. [GeotagMetadataWriterService](../src/PhotoLibrarian.Core/Services/GeotagMetadataWriterService.cs) and [FaceMetadataStore](../src/PhotoLibrarian.Core/Services/FaceMetadataStore.cs) delegate that decision even though they handle property-specific writes. [GeotagEditService](../src/PhotoLibrarian/Services/GeotagEditService.cs) handles geotag batch writes and direct-write cache updates; the metadata panel retains transaction and display orchestration. Follow the [metadata-handling policy](#metadata-handling-philosophy); don't mistake the index for durable storage. |
| Backups and history | [OriginalBackupService](../src/PhotoLibrarian.Core/Services/OriginalBackupService.cs) preserves original files separately from undo/redo. [MainViewModel's file-edit path](../src/PhotoLibrarian/ViewModels/MainViewModel.EditHistory.cs) uses [FileEditTransaction](../src/PhotoLibrarian.Core/Services/FileEditTransaction.cs) to stage and checkpoint changes and [EditHistoryService](../src/PhotoLibrarian.Core/Services/EditHistoryService.cs) to order committed edits. Keep pending editor gestures separate from committed history; see [Edit History](EDIT-HISTORY.md) for scope and recovery semantics. |
| Edit rendering | [EditEffectGraph](../src/PhotoLibrarian/Services/EditEffectGraph.cs) builds the Win2D edit graph. [ImageEditRenderPass](../src/PhotoLibrarian/Services/ImageEditRenderPass.cs) shares drawing across the editor, editor comparison, and [ImageEditRenderer](../src/PhotoLibrarian/Services/ImageEditRenderer.cs). Extend effect behavior there rather than in a surface; keep surface-specific loading, sizing, and presentation separate. |
| Navigation and commands | [MainWindow](../src/PhotoLibrarian/MainWindow.xaml.cs) coordinates shell keyboard handling and uses [OverlayNavigationState](../src/PhotoLibrarian/OverlayNavigationState.cs) to resolve the active overlay. Check the target surface and [TopRibbon](../src/PhotoLibrarian/Views/TopRibbon.xaml) before adding commands; [App.xaml](../src/PhotoLibrarian/App.xaml) provides shared toolbar and ribbon styles. Preserve focus and selection when returning to the previous surface. |
| Confirmations and errors | Use [DialogService](../src/PhotoLibrarian/Services/DialogService.cs) for simple confirmations and error summaries, as in [DuplicateReviewView](../src/PhotoLibrarian/Views/DuplicateReviewView.xaml.cs) and [SettingsPanel](../src/PhotoLibrarian/Views/SettingsPanel.xaml.cs). Follow the [dialog conventions](#dialog-conventions) below; specialized dialogs retain their own lifecycle. |
| Status, progress, and cancellation | [FeedbackService](../src/PhotoLibrarian/Services/FeedbackService.cs) presents non-blocking informational, warning, and error messages on caller-owned `InfoBar` slots. Photo import uses separate slots for actionable device prompts, errors, and status; the motion-photo viewer uses its own warning slot. Call it on the UI thread and do not replace unrelated notices. Other surfaces still use their existing paths: [DuplicateReviewViewModel](../src/PhotoLibrarian/ViewModels/DuplicateReviewViewModel.cs) exposes review status and scan progress, while [PhotoImportDialog](../src/PhotoLibrarian/Views/PhotoImportDialog.xaml.cs) shows a cancellable batch with per-item results from [PhotoImportService](../src/PhotoLibrarian.Core/Services/PhotoImportService.cs). Keep progress and work in the operation service and distinguish cancellation and partial success from total failure. |
| Settings and localization | [AppDataPaths](../src/PhotoLibrarian.Core/Services/AppDataPaths.cs) owns the local app-data locations for settings, models, the cache database, original backups, edit history, share copies, and the preset catalog. [AppSettingsFile](../src/PhotoLibrarian.Core/Services/AppSettingsFile.cs) provides atomic writes and typed JSON backup recovery; [GridLayoutSettingsStore](../src/PhotoLibrarian/Services/GridLayoutSettingsStore.cs) and [MapSettingsStore](../src/PhotoLibrarian/Services/MapSettingsStore.cs) own their settings and validation. Other stores still have their own read logic. For UI text use [Resources.resw](../src/PhotoLibrarian/Strings/en-US/Resources.resw) via `x:Uid` in XAML or [ResourceStrings](../src/PhotoLibrarian/Services/ResourceStrings.cs) and its feature helpers in code. |
| Pickers | [FolderPickerService](../src/PhotoLibrarian/Services/FolderPickerService.cs) is shared by library navigation and settings. Some dialogs, such as [PhotoImportDialog](../src/PhotoLibrarian/Views/PhotoImportDialog.xaml.cs), instead use a picker tied to the dialog's host via its `XamlRoot`. Match the invoking surface's ownership; don't assume the shared service covers every picker. |

### Dialog conventions

Use `IDialogService` (`DialogService.Instance`) for simple confirmations and error summaries.
Pass the invoking, loaded control on every call; the service resolves its current `XamlRoot`
and theme rather than retaining a window. Pass caught exceptions to its error overload to show
their message through the same presentation path. Prefer localized labels: primary names the
action, confirmation close means Cancel, and destructive confirmations always default to Cancel.
Only `ContentDialogResult.Primary` authorizes an action; handle presentation failures in the
caller's existing error surface. Progress, nested dialogs and save/discard/cancel forms remain
separate until their cancellation and validation semantics can be migrated intact.
This first migration preserves existing text, including the geotag error summary's English
title and Close label; resource-backed geotag error text remains a localization follow-up.
The isolated accessibility host's `--dialogs` suite checks native keyboard cancellation,
results, scrollable error content, and moving the same owner between windows.


## Metadata-handling philosophy

PhotoLibrarian routes photo metadata changes through one storage policy so edits remain portable:

1. **Embedded in the image file** whenever that format can store the requested property. Writes preserve unrelated metadata and verify the embedded value before migrating an owned legacy-sidecar value. Tags use XMP `dc:subject` plus the format's keyword fields; ratings use XMP `xmp:Rating`; captions use standard title/comment fields; capture dates and GPS use their EXIF fields; color labels use XMP `xmp:Label`; flags, face regions, and pending edit recipes use PhotoLibrarian XMP properties.
2. **XMP sidecar** only when the image format cannot store that property (for example, RAW originals). A failed write to a supported format is reported; it is never redirected to a sidecar. Keep any required sidecar with its photo. RAW/raster companion sidecars remain distinct, and unrelated or ambiguous legacy values are preserved rather than overwritten.
3. **SQLite cache** at `%LOCALAPPDATA%\PhotoLibrarian\cache.db` is an index only, never the sole portable copy. Wipe and re-index any time.

Move a JPEG to another drive or another machine and every edit goes with it.

---
