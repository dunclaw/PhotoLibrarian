# Duplicate photo review

The **Find duplicate photos** button in the status bar scans indexed still
photos locally. It is cancellable and reports files that could not be read or
changed during the scan. Video files are excluded.

Exact candidates are grouped by a full SHA-256 content hash, calculated only
for files whose byte lengths match another indexed photo. This avoids reading
every large original solely for exact matching. Near-match candidates use a
63-bit perceptual hash built from a Windows Imaging Component thumbnail-size
decode; pairs with a Hamming distance of 8 bits or fewer are shown separately.
Near matches are pairwise suggestions, not proof, and may include false
positives. Near matching is available only when Windows can decode the still
image; unsupported RAW/HEIC codecs and damaged files appear in scan diagnostics.
Exact matching does not require image decoding.

The comparison uses nine perceptual-hash bands to avoid an all-pairs scan.
Work is capped at 2,000,000 hash comparisons, 50,000 candidates per image, and
100,000 near-match results. If a cap is reached, the review reports that
near-match results may be incomplete. Hashes are calculated for the current
scan only; the SQLite library cache is not extended with duplicate data.

Review each group and select the photo to keep. No photo is selected as the
keeper automatically. Only after explicit confirmation are the other files
sent through the app's existing Recycle Bin operation, together with any
associated XMP sidecar. Before each removal, the app verifies the scan-time
file size, modified time, and full SHA-256 content hash; changed or missing
files are left untouched and reported.
