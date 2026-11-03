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

## Viewer and frequent ribbon command follow-up

The viewer's own focus stop now has a localized name and keyboard help.
Its zoom, navigation, close, and information-pane controls already use named
native Button/Invoke or ToggleButton/Toggle peers. The viewer path context menu
already exposes a native MenuItem/Invoke action. The six photo ribbon commands
(Make a copy, Crop, Straighten, Adjust, Red eye, Slideshow) likewise retain native
named Button/Invoke peers; this follow-up does not replace them with custom peers
or add IDs to decorative content.

Tab / Shift+Tab remain available while crop, straighten, red-eye, or manual
people-tagging mode is active. The viewer no longer treats a child control's
unhandled letters, arrows, or Enter as photo-viewing shortcuts. Escape remains
available to cancel the mode or close the viewer when the child has not handled
it. The opening-time focus recovery stops on Tab, so intentional movement to
the ribbon or another pane is not pulled back into the viewer.

Entering Crop from its focused ribbon command moves keyboard focus to Proportion;
entering Straighten moves it to Apply. Leaving either mode while focus is still
in that ribbon group returns it to its original command. A mode change initiated
outside the ribbon does not steal focus from the image viewport. The window's
existing async apply/save flow is unchanged.

The viewer's canvas, top-bar background, title, and secondary text resolve through
Light / Dark / HighContrast resource dictionaries. Contrast colors use Windows
Window/WindowText brushes. Icon buttons inherit their native foreground (including
disabled, hover, pressed, and contrast states), and navigation buttons no longer
fade their content with opacity. The ribbon keeps its standard WinUI button
templates, focus visuals, and theme brushes.

This is **not full-app accessibility completion**. Pointer-drawn crop/face/red-eye
regions, editor adjustment controls/effects, map controls, other ribbon surfaces,
and slideshow themes are outside this slice. No grid virtualization code changed.

## Repeatable automated regression

On a Windows development machine with this repository's .NET/Windows SDK tools,
Developer Mode, and `winapp` installed:

```powershell
.\tools\Test-PhotoGridAccessibility.ps1
```

The isolated WinUI host uses 10,000 synthetic entries: no library database,
photo reads/writes, or thumbnail service. It also hosts the production viewer and
top ribbon with **no application view model or photo loaded**; command events are
observed without executing edits or file operations. It tests actual peer objects and standard
provider interfaces on the UI thread, bounded child counts, recycling identity,
offscreen bounds, select-all behavior, disabled/stale actions, collection changes,
and the production grid keyboard handler with explicit key/modifier inputs.
Viewer checks cover names and Invoke/Toggle patterns, ribbon command invocation,
bidirectional Tab reachability, disabled-control skipping, crop/straighten ribbon
focus transitions, and runtime Light/Dark switching. Tab traversal uses WinUI
FocusManager. It does **not** claim to test physical keyboard delivery, Narrator
speech, context-menu keyboard opening/dismissal, or edit/save operations.

The keyboard ownership policy and contrast-resource contracts have focused xUnit
regressions (the repository uses the executable runner with .NET 10):

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 -- -class PhotoLibrarian.Tests.ViewerCommandAccessibilityTests -class PhotoLibrarian.Tests.ImageViewerOverlayFocusTests -class PhotoLibrarian.Tests.ImageViewerNavigationTests -class PhotoLibrarian.Tests.ImageEditorNavigationTests
```

The script uses a separate `PhotoLibrarian.AccessibilityTests` package identity and
unregisters it on exit. Results are in the test output's
`AppX\accessibility-results.json`. `-SkipBuild -Preview` keeps the synthetic grid
open for external UIA inspection until its window is closed. Use
`-SkipBuild -ViewerPreview` instead to run the regression and leave the synthetic
viewer/ribbon open (initially Dark). Its test-only "Switch preview theme" button
switches Light/Dark without changing Windows settings. Close its window to
unregister the test host.

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

### Viewer/ribbon follow-up smoke checklist

Use the isolated `-ViewerPreview` host for peer/visual inspection. Use a disposable
Windows test account/VM and copied sample photos for full application behavior;
never use a live library. Do not change global contrast settings on a shared
desktop. Record Windows/Narrator build, app revision, scaling, theme, and results.

- On opening a photo, immediately Shift+Tab into the ribbon and wait at least
  one second. Confirm focus stays on the chosen command rather than returning
  to the viewer. Tab forward into the named Photo viewer stop and through zoom,
  information-pane, close, Previous, and Next controls; reverse with Shift+Tab.
- Activate the six photo ribbon commands with Enter and Space. For operations
  that would edit a file, use only copied samples and cancel instead of applying.
  Confirm each name matches its action. Verify slideshow separately in the test
  account; its themes are outside this change.
- Enter Crop: confirm Proportion receives focus, arrows select proportions,
  Tab reaches Apply and Cancel, and Cancel restores focus to Crop. Enter
  Straighten: Tab between Apply/Cancel and its angle/Auto controls; arrows on
  the angle slider must not navigate away or lose the edit. Cancel and check focus.
- In the manual people-tag flyout, type names containing A, I, and F; the viewer
  must not zoom, toggle the information pane, or flag a photo. Tab out of each
  edit mode; Escape cancels the mode. Region drawing itself remains pointer-only.
- Open the viewer path menu with Shift+F10 and the Menu key; confirm its name,
  command availability, and Escape focus return. Clipboard contents must match
  the copied test photo. Confirm Escape in an open child popup dismisses that
  popup before closing/cancelling the viewer.
- With Narrator, confirm button names, information-pane toggle state, crop
  proportion selection, and focus transitions are announced without duplicate
  decorative icons. Include disabled Actual size before image load.
- In Light, Dark, and two Windows contrast themes, inspect viewer title/index,
  all touched button icons, and ribbon focus/hover/pressed/disabled states at
  100% and 200% scaling. Change contrast on/off and change its palette while
  the isolated host is open; verify canvas/title/chrome update immediately,
  icons remain legible, and focus remains distinct from the button border.

Automated follow-up evidence: x64 app/test-host builds; 58 focused xUnit cases
and 89 isolated UI-thread grid/viewer/ribbon checks pass, including runtime
Light/Dark switching. Contrast dictionaries are checked structurally against
system brush keys. Physical keyboard delivery, Narrator speech, real image
editing/save flows, and actual Windows contrast-theme/palette switching are
**not executed** and must not be inferred from these automated results.
