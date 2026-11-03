# Offline coordinate map

Open **Offline coordinate map** (the map icon beside Share in the grid toolbar).
First select a library scope in Browse. The map uses the current union of Browse
selections, narrowed by refinement and search. Indexed still photos and videos
with complete, finite GPS latitude/longitude are plotted; unindexed files and
missing, incomplete or invalid coordinate pairs are excluded with a separate
count. Zero coordinates and both poles are valid.

Select a pin to show only that group's items in the grid, retaining other filters
and the chosen sort/group order. The first matching result is selected and
revealed. Viewer/editor navigation uses that same restricted set. A group is a
snapshot of its paths, not a geographic region: new files do not silently enter
an existing selection. Other filter changes can leave zero matching items.
**Clear map filter** restores the pre-map results without clearing other filters;
closing the map also clears its filter. Nothing about the map is persisted.
Explicitly revealing a newly exported copy also clears the map filter so the
copy is not hidden (exports may omit GPS).

## What this map is (and is not)

This is an equirectangular **coordinate-space plot**, north up and east right,
with a quarter-interval grid and explicit viewport latitude/longitude bounds.
Distances, area and aspect ratio are not geographically faithful, especially at
high latitudes. There are **no continents, roads, imagery or resolved place
names**. Pin labels show group counts and centroid coordinates, not named places
or the precise position of every member of a group.

**Fit all** uses the shortest circular longitude interval, so photos on either
side of the antimeridian stay near each other. Longitude bounds run eastward and
may cross 180 degrees. Latitude includes -90 through +90 without Mercator
clamping. Zoom is centred on the viewport; West/East/North/South pan by half its
span. Zoom in and pan to separate nearby groups. Very close or coincident
positions can remain grouped. Fit all restores the full current scope.

Only visible points are clustered into screen cells (at least 64 logical pixels,
up to 16 columns by 8 rows). At most 128 pin/list targets are created, regardless
of library size. Index snapshots and geometry run in memory; clustering is
linear in valid points, and fitting sorts longitudes. Scope/viewport changes and
closing the map cancel older work; superseded results cannot replace newer ones.
Selection does not reload/index files or write metadata.

## Keyboard, accessibility and appearance

Tab to the **Visible GPS groups** list and use arrow keys to select a group.
It performs the same filtering as the pin buttons. Tab to Clear map filter to
remove that restriction. Pins are standard named toggle buttons; controls have
localized labels, stable automation IDs and native focus/selection semantics.
The map uses WinUI theme resources and standard controls for light, dark and
system high-contrast appearance. Colour is not the only selection indicator.

## Data strategy, privacy, licensing and updates

The only data source is the GPS pair already read locally from photo metadata
into the existing library index. The map has no HTTP client, WebView, external
launcher, map SDK, service token, telemetry, geocoder, tile cache or download path.
Coordinates are not transmitted. There are no additional dependencies and no
photo, sidecar or library-index writes from map interaction.

No third-party geographic dataset is bundled or downloaded, so there is no
basemap storage footprint, data attribution requirement or map-data update
schedule. The plot implementation follows this repository's license; photo
metadata remains the user's data. GPS cache updates come from the normal local
indexing workflow. Map snapshots and selections live only in session memory.

The available WinUI
[MapControl](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/map-control)
is Azure Maps-backed and requires an account/service token; it is deliberately
not instantiated. A token-backed online map is not an offline fallback.
Windows offline-map downloads are not used or assumed to supply this control.
A future real offline basemap would require a separately reviewed data source,
redistribution license, attribution, bounded installation/storage and explicit
update policy; this feature does not promise one. There is no GPS editing or
resolved-name change in this feature.
