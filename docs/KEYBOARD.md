# Keyboard & mouse reference

Shortcuts and mouse interactions.

## Keyboard & mouse

| Action | Shortcut |
|---|---|
| Select item | Click |
| Range select | Shift+Click |
| Toggle item in selection | Ctrl+Click |
| Open viewer | Double-click |
| Pan/zoom viewer | Mouse wheel (zooms around cursor) |
| Actual size in viewer | A or the **1:1** button |
| Show / hide viewer information pane | I or the information toggle |
| Reset viewer zoom to fit | Ctrl+0 |
| Next / previous in viewer | ←  →  arrow keys |
| Close viewer | Esc |
| Previous / next while editing | Left / Right arrows (outside input controls); Ctrl+Page Up / Ctrl+Page Down |
| Flag / unflag selection | F (grid and viewer) |
| Start full-screen slideshow | F11 (grid and viewer) |
| Previous / next slideshow photo | Left / Up and Right / Down arrow keys |
| Exit slideshow | Esc or F11 |
| Rate selected photos (0 clears; 1-5 sets) | 0-5 or Ctrl+1-Ctrl+5 |
| Set / clear color label (Red / Yellow / Green / Blue) | 6 / 7 / 8 / 9 |
| Rotate selected photos clockwise / counter-clockwise | Ctrl+R / Ctrl+L |
| Focus the selected photos' tag entry | Ctrl+T, then type a tag and press Enter |
| Rename selected photos | F2 |
| Move selected photos to the Recycle Bin | Delete |
| Undo / redo pending editor changes | Ctrl+Z / Ctrl+Y |
| Print selected photos | Ctrl+P |

**Actual size** displays one decoded image pixel per physical display pixel, adjusting
for Windows display scaling. It is available once a still image is loaded (not for video).
**Fit to window** returns to a view that follows window and pane resizes. The information
toggle hides the right-hand pane and its splitter while viewing, restoring its resized
width when shown again. The choice is remembered only until the app closes; closing
the viewer always restores the normal grid's information pane. Both controls support
Tab and Enter/Space; A and I apply when viewer focus is outside text inputs.

Opening a photo from the grid transfers keyboard focus to the viewer, so Left,
Right and Escape work without clicking a navigation button. Clicking the image
or its title restores viewer focus; input controls keep their own keyboard behavior.
To check these paths without a live library, build
`src\PhotoLibrarian.AccessibilityTests\PhotoLibrarian.AccessibilityTests.csproj`
with Visual Studio MSBuild (`/p:Platform=x64 /restore`), then run
`.\tools\Test-PhotoGridAccessibility.ps1 -SkipBuild -ViewerFocus`.
The isolated host double-clicks generated grid photos and sends native keyboard
and mouse input, saving results and `viewer-focus.png` in its `AppX` output folder.
It tests the real grid/viewer controls, not the main window's other overlays.

Start a slideshow with F11, the **Slideshow** button at the left of the grid toolbar or in
the viewer ribbon, or **Slideshow** in a photo's right-click menu. Photos play in the order
the grid shows them (including grouping) and videos are excluded:

- **Viewer:** plays the viewer's photos, starting at the photo on screen.
- **Grid, two or more photos selected:** plays only the selected photos, starting at the first.
- **Grid, one photo selected:** plays the whole filtered set, starting at that photo.
- **Grid, nothing selected:** plays the whole filtered set from the beginning.

Photos wrap at either end. Use **Pause / Resume** and
**Seconds per photo** (1-60 seconds, default 5) in the slideshow toolbar to control
auto-advance. Each photo gets a full interval after loading or manual navigation.
The interval is remembered until the app closes. Exiting returns to the unchanged
grid or viewer; slideshow shortcuts are unavailable while editing or in settings.

Use **Slideshow theme** in the full-screen toolbar to change the style while
viewing photos, or the arrow beside **Slideshow** in the viewer ribbon to choose
it before starting. The choice is remembered until the app closes. Themes are
display-only; no photo bytes or metadata are changed.

- **Contemporary** (default): subtle colour/contrast enhancement, a gentle centered zoom, and a short crossfade.
- **Pan and Zoom**: cycles through zoom-in and zoom-out shots with left/right and
  up/down pans. Motion scales with the displayed photo size and stays clipped to
  the photo area.
- **Cinematic**: subdued colour/contrast with letterbox bars and a short crossfade.
- **Fade**: original colours with a short crossfade.
- **Black and White / Sepia**: monochrome or warm-toned display effects with a short crossfade.

The toolbar is a compact single row; control names are available
through accessibility names and tooltips rather than extra heading rows.
Pause freezes theme motion in place; Resume continues from that position.
Windows' animation setting disables slideshow motion and fades. Arrow keys
operate the theme and interval controls when those controls have focus, rather
than advancing photos.

Slideshow layout/selection regressions can be checked without a live library:
`.\tools\Test-PhotoGridAccessibility.ps1 -Slideshow`. This runs the production
slideshow window in the isolated WinUI host with generated landscape/portrait
images, checks the toolbar height, centered layout, live theme changes,
pause/resume rendered frames, repeated full-duration Pan and Zoom transitions,
rapid changes and close cancellation, and saves a Contemporary screenshot in the
host's `AppX` output folder. It does not test Narrator or physical keyboard input.

Rating, color labels, rotation, tagging, rename, delete, and print act on the current grid selection
(or the current photo when only one is selected). Rating and color-label shortcuts are ignored while an
input control is focused; text boxes keep their normal typing and undo behavior. Delete
uses the Windows Recycle Bin confirmation so removed photos remain recoverable.

---
