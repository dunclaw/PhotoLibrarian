# Photo grid accessibility

The grouped library photo grid exposes a UI Automation **List** with **ListItem**
photo peers. Names are filenames, IDs are `Photo:` plus the full file path, and
selection is available through the standard Selection / SelectionItem patterns.
Invoke opens the identified photo, ScrollItem brings it into view, and the list
delegates Scroll to its existing ScrollViewer. Decorative thumbnail, progress,
and filename visuals are not separate screen-reader stops.

Tab enters/leaves the grid as one stop. Arrow keys, Home/End, and PageUp/PageDown
move logical photo focus and select; Shift extends a range. Ctrl+navigation moves
focus without changing selection. Space selects, Ctrl+Space toggles, Enter opens,
Ctrl+A selects all, Escape clears selection, and Shift+F10 / the Menu key request
the existing photo context menu. A separate inset outline shows focus without
replacing the selection border. Group headers, photo backgrounds, filename
overlays, and selection/focus/hover outlines use Windows system colors when a
contrast theme is active; a color change refreshes active and pooled visuals.

## Virtualization and limits

Only the existing viewport plus its 500-DIP loading buffer is exposed as list
children. Buffered photos outside the viewport report offscreen; unrealized photos
have no visible bounds. Peers hold an immutable photo identity, not a recycled
visual's DataContext. A retained peer can scroll its original photo back into view.
Dataset changes invalidate old peers with ElementNotAvailable rather than changing
their names to another photo. Group item edits and Items collection replacement
also invalidate peers and prune obsolete selection.

The grid retains peers only for realized items and the single logical focus.
Enumerating all selected photos
necessarily returns O(selected count) provider references, including logical
offscreen selections; it does not create controls, load thumbnails, or retain an
all-library peer cache. Existing layout and keyboard traversal still use the
grid's existing group/flat-item scans. This is not a new whole-library
ItemContainer/VirtualizedItem discovery implementation: use keyboard navigation
or Scroll to reach other photos.

This pass does not cover flat/template grids (for example face-review surfaces),
all ribbon commands, group-header announcement, or all application contrast
styling. Those remain follow-up slices of #49.

## Repeatable automated regression

On a Windows development machine with this repository's .NET/Windows SDK tools,
Developer Mode, and `winapp` installed:

```powershell
.\tools\Test-PhotoGridAccessibility.ps1
```

The isolated WinUI host uses 10,000 synthetic entries: no library database,
photo reads/writes, or thumbnail service. It tests actual peer objects and standard
provider interfaces on the UI thread, bounded child counts, recycling identity,
offscreen bounds, select-all behavior, disabled/stale actions, collection changes,
and the production keyboard handler with explicit key/modifier inputs. Tab
traversal uses WinUI FocusManager. It does **not** claim to test physical keyboard
delivery, Narrator speech, or the application's context-menu contents/viewer;
it asserts the grid's existing menu/open events.

The script uses a separate `PhotoLibrarian.AccessibilityTests` package identity and
unregisters it on exit. Results are in the test output's
`AppX\accessibility-results.json`. `-SkipBuild -Preview` keeps the synthetic grid
open for external UIA inspection until its window is closed.

## Manual Narrator and contrast smoke checklist

Use a disposable test library with multiple groups and enough photos to scroll.
Record Windows build, display scale, app revision, Narrator version, and chosen
contrast theme alongside pass/fail results. Do not exercise delete or rename.

- Start Narrator, Tab into Photos, and confirm the filename and list-item /
  selection semantics are announced, not the loading spinner and duplicate label.
- Use arrows, Home/End, and PageUp/PageDown across group and viewport boundaries.
  Confirm the announced filename, visible outline, metadata selection, and photo
  opened by Enter agree.
- Ctrl+arrow to an unselected photo, then Ctrl+Space. Confirm focus and selection
  are distinct and Shift+arrow extends from the selection anchor.
- Use Ctrl+A and Escape; confirm selection announcements and no lost focus. Tab
  and Shift+Tab out of and back into the grid; no per-thumbnail Tab trap.
- Open photo actions with Shift+F10 and the Menu key, navigate the menu, and press
  Escape. Confirm the menu targets the focused photo and focus returns to Photos.
- Scroll far away and back. Change a filter/grouping, then show an empty result.
  Confirm no old/recycled filenames or ghost selections are announced.
- Repeat in Light, Dark, and two Windows contrast themes. While Photos is open,
  switch contrast on/off and change its palette. Confirm headers and filenames
  remain readable, selected borders and focus outlines remain distinct, and
  newly recycled tiles match the current theme.
- At 100% and 200% display scaling, check the focused tile's filename is not
  crossed by the focus outline and the viewport clips partial rows normally.

### Evidence for this change

The x64 app and isolated host build successfully. The isolated host's peer,
keyboard-handler, and WinUI Tab checks pass. External `winapp ui` inspection
confirms named List/ListItem nodes, logical item focus, Invoke selection, and
ScrollItem on the synthetic grid; Light-theme focus/selection screenshots were
inspected. Initial foreground SendInput attempts were unreliable on the shared
desktop and are **not** counted as end-to-end keyboard evidence. The manual
Narrator, physical-key, Dark, and Windows contrast-theme checklist above is
**not executed**.
