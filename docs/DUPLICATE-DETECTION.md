# Duplicate photo review

Hover the overlapping-photos icon in the grid header for the **Find duplicates**
tooltip. It uses all photos currently shown in the grid (after folder selection,
filters, and search) as seeds, and looks for matching still photos anywhere in
the indexed library. It does not depend on which tiles are highlighted. To
review the full library, select the folder root and clear filters/search.
The adjacent people-review icon reviews face candidates only in the displayed
photos. Grouping/sorting and review icons occupy the central header, followed by the
right-aligned zoom slider and filter. Search fills the header over the right
information pane. Review actions are no longer in the status bar.

Duplicate scanning is local and cancellable, reports files that could not be
read or changed, and excludes video. An empty grid starts no library scan.

Exact candidates are grouped by a full SHA-256 content hash, calculated only
for files whose byte lengths match a displayed seed photo. This avoids reading
every large original solely for exact matching. Near-match candidates use a
63-bit perceptual hash built from a Windows Imaging Component thumbnail-size
decode; pairs with a Hamming distance of 8 bits or fewer are shown separately.
Near matches are pairwise suggestions, not proof, and may include false
positives. Near matching is available only when Windows can decode the still
image; unsupported RAW/HEIC codecs and damaged files appear in scan diagnostics.
Exact matching does not require image decoding.

The comparison indexes only seed hashes into nine perceptual-hash bands and
queries the remaining library against those seeds, avoiding unrelated
outside-folder comparisons. Exact groups and near pairs must include a seed.
Work is capped at 2,000,000 hash comparisons, 50,000 candidates per image, and
100,000 near-match results. If a cap is reached, the review reports that
near-match results may be incomplete. A synthetic one-seed/200,000-candidate
comparison regression took 334 ms on the development machine; this measures
matching, not disk reads or image decoding.

SHA-256 and perceptual hashes are stored in the rebuildable SQLite cache,
keyed by image, byte length, modified time and algorithm version. Unchanged
files reuse these hashes, including across app restarts; a changed file is
rehashable on the next duplicate scan, not immediately by the watcher.
The table is `duplicate_hash_cache` in the existing
`%LOCALAPPDATA%\PhotoLibrarian\cache.db`; it joins images by ID and is deleted
with those image records. No authoritative photo metadata is stored in this cache. The first
uncached similarity scan still needs to decode library images to discover
outside-folder matches and can take time on large collections. Subsequent
scans reuse the cache. Progress distinguishes file inspection, exact hashing,
similarity hashing, comparison and content verification, with updates limited
to about ten per second. A decode times out after 15 seconds; after two decode
timeouts the scan stops new decoding and explicitly reports incomplete near
results rather than starting an unbounded number of stalled decode jobs.
Similarity hashing now uses up to four workers and prefers the embedded WIC
image thumbnail when present, falling back to a scaled decode otherwise.
The algorithm version is bumped so old full-image hashes aren't mixed with
embedded-preview hashes. This causes a one-time rebuild of the earlier cache.
The face/automatic-tag recognition pipeline is stopped before each duplicate
scan (including rescans), prevented from restarting during it, and restored
after completion or cancellation without changing the user's enabled stages.
Large images without previews still require decoding; synthetic thumbnail
tests are not a guarantee of throughput for RAWs, network disks, or a real
hundred-thousand-photo collection.

Review each group and select the photo to keep. No photo is selected as the
keeper automatically. Only after explicit confirmation are the other files
sent through the app's existing Recycle Bin operation, together with any
associated XMP sidecar. The selected keeper is verified and held read-only
while the operation runs so it cannot be replaced or removed mid-cleanup.
Before each candidate removal, the app verifies the scan-time file size,
modified time, and full SHA-256 content hash; changed or missing files are
left untouched and reported. A candidate is counted as moved only after the
Recycle Bin operation confirms that its original path is gone. Sidecar recycle
failures are reported separately from successful photo moves.
