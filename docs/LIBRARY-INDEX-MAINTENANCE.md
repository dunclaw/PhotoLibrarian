# Library index maintenance

Use **Settings > Library index maintenance > Repair library index** to scan
the configured watched folders. The operation reads local files and updates
the existing SQLite database incrementally; it does not drop, recreate, or
vacuum the database. Newly discovered or changed media is indexed through the
normal indexing service.

The persistence contract is **files and associated metadata are authoritative;
SQLite is a rebuildable index/cache**, not a separate library that must be
copied to move the photos. Tags, flags, and people assignments are written to
the media or its XMP sidecar; duplicate hashes, thumbnails, and recognition
embeddings are derived caches. Always move associated sidecars with the photos.

Repair removes stale entries for files verified missing from accessible watched
folders, after attempting exact-identity move repair and indexing available
files. A second folder scan confirms that candidates are still absent and
storage is still accessible. The image rows and related tag, face, and hash
cache rows are removed in one transaction; person face counts and representative
references are updated consistently. No photos or sidecars are deleted.

Entries on unavailable storage, in unreadable directories, or outside the
watched scan scope remain indexed. A false filesystem existence check alone is
never sufficient evidence for removal. Symbolic links and other reparse points
are skipped and reported rather than followed.

A move or rename is repaired only when an existing duplicate-scan SHA-256
cache entry still matches the indexed file size and modification time, and
exactly one newly found file has the same full content hash. Ambiguous matches,
stale hashes, and files with sidecar state that cannot be verified are not
treated as moves. Available destination files are indexed independently, and
the old missing paths are eligible for cleanup. No filename-only matching is
used.

Canceling or encountering an error keeps already completed per-file indexing
and relocation work. Cancellation or failure during missing-entry cleanup rolls
back the entire cleanup batch, including cascaded rows and person updates.
The result reports only committed removal counts and includes any cleanup
failure. Retrying scans the same watched folders and safely resumes; incomplete
or unavailable locations are reported rather than presented as a successful
complete scan. Filesystem watcher delete and missing-directory notifications
retain entries until explicit repair can verify absence; a transient storage
loss must not empty the library.

The maintenance action never modifies or deletes photo originals or writes
sidecars. Files with a pending people-metadata export are reported and skipped
so maintenance cannot cause a sidecar write. A unique exact-hash move retains
the image ID and its associated cache rows. Confirmed deletions remove cache
rows regardless of whether an old development database contains unexported
values. There is no legacy-export prerequisite or automatic whole-cache backup
for this cleanup.

## Incremental recognition

Startup still enumerates watched files to discover additions and ordinary external
changes. Cached files whose photo and associated face/tag sidecar size/timestamp
snapshots are unchanged skip metadata parsing and tag-row comparisons. These
snapshots are an optimization, not proof of content identity: watcher notifications,
**Refresh**, and **Repair library index** verify file and sidecar hashes, including
edits that preserve size and timestamps. Changes made while the app was closed
that preserve every snapshot require an explicit Refresh or repair.

Recognition uses a separate, versioned source identity. For JPEG, PNG, TIFF, BMP
and other embedded-writable formats (including HEIC/JPEG-XR) with working Windows
codecs, this is a SHA-256 of full-resolution, oriented,
color-managed BGRA8 pixels and dimensions, not a thumbnail or perceptual hash.
Descriptive metadata edits therefore do not invalidate recognition. Initial or
changed sources require local decoding to establish this identity; stable startup
does not. RAW and other formats conservatively use a full-file SHA-256. Their
sidecar-only adjustment/metadata edits do not invalidate recognition, but embedded
changes do. No claim is made that every camera/codec supports metadata-independent
identity. Changes invisible in the recognition decoder's BGRA8 input are treated
as the same recognition input.

Face metadata refresh preserves embeddings and completion for unchanged face
geometry, including name/review changes. New or moved regions still require
embeddings. Face detection and embeddings are derived cache data; rebuilding a
cache can rerun face inference without losing portable people decisions.

The existing schema-1 `plib:TagMetadata` document has an optional `sourceIdentity`
field. Its scan version is restored only when that identity matches the current
source; stale or older unbound completion does not suppress inference. Tag values,
sources and confidence still import independently. Background tagging, selected
review and manual keyword edits retain this binding. No bookkeeping keywords
are added. Embedded provenance is read back before accepting success; codecs
that fail to retain it use the existing sidecar fallback. An explicit portable
tag-sidecar packet is authoritative, and subsequent tag writes keep it current
until migration, rather than leaving stale sidecar provenance behind.
Tagging completion also includes non-default output settings and model
directory, in addition to the model/pipeline version. Metadata-write failure and
cancellation leave unfinished work retryable; unavailable photos retain their
cache entries.

## Composite descriptive tags

A slash-separated value such as `Auto/animal/mammal/dog` is one explicit
keyword in the photo (or its XMP sidecar) and one item in the Information
panel's descriptive tags. The SQLite cache stores that explicit value with its
source and confidence. A separate, indexed path-membership table derives
`Auto`, `Auto/animal`, and `Auto/animal/mammal` for navigation and refinement;
those paths are never exported as extra keywords. Tree counts are distinct
photos at each level, including when a photo has several sibling tags.
Removing the composite keyword removes its derived memberships, but a sibling
or a genuinely independent parent keyword still supplies its own membership.

There is no lossy prefix-collapse migration. Older files may already contain
both a parent keyword and a longer composite keyword; even their source and
confidence cannot prove whether the parent was generated or intentionally
entered. Every serialized value therefore remains an explicit, editable tag.
Deleting an explicitly stored parent does not break searching for descendants:
their parent memberships are derived independently. Fresh imports and restarts
preserve all these values. An explicit Refresh reconciles stale expanded cache
rows to a portable `plib:TagMetadata` packet containing only the composite
value, without rewriting the file. Without such authoritative provenance,
ambiguous old cache rows are retained rather than guessed away.

Temporary-library tests use deterministic fake inference, real SQLite and generated
images to assert inference-call counts across restarts, metadata/pixel edits,
model/pipeline/settings changes, cancellation, persistence failures, watched scope
and offline storage. A 100-photo synthetic scoped-startup fixture reports elapsed
time but is not a representative camera-library performance benchmark.

## Person picture portability

Choosing a person's picture now saves the selection in that photo's
PhotoLibrarian XMP face-review metadata before changing the cache. Choosing a
different picture also clears the previous file's selection, with the existing
metadata batch backup/rollback behavior. Import locates the selected face by its
portable region metadata rather than copying SQLite IDs. A fresh database can
therefore restore the choice after the photos and sidecars move.

Older face-review packets without this property remain readable. Their absence
does not clear an existing cached choice. Index maintenance does not migrate
old development-only picture choices; reselecting a picture writes it portably.

## Pre-release persistence-contract audit

The app has not shipped a release. Compatibility with old development caches
must not turn SQLite into an authority or prevent stale-entry cleanup. No
historical cache backfill/export is required by this repair feature. Any current
writer that saves a unique user decision only in SQLite is a persistence bug
to fix at that writer, not a reason to retain invalid paths.

| Field identified in earlier development code | Portable persistence requirement / status |
|---|---|
| Automatic tag values, `tags.source`, `tags.confidence` | Ordinary keywords alone cannot recover provenance. PR #152 adds a versioned `plib:TagMetadata` snapshot and fresh-cache import, covering selected review and background writes. It is not part of this standalone branch. |
| `images.auto_tag_scan_version` | PR #152 carries it in the same portable snapshot; otherwise this is derived scan state, not evidence that the old value was recovered. |
| `persons.representative_face_region_id` | This PR persists new/reselected person-picture choices through portable face metadata and restores the chosen face without copying database IDs. |
| `images.face_metadata_export_required` | This is export-work status, not portable library content. Explicit face-review actions write metadata first. Derived recognition data is regenerable; pending exports for available files are reported/skipped by maintenance to keep it read-only toward photos. |

This Settings command repairs the existing index rather than providing a
drop-and-recreate operation. The standalone branch must not claim tag
provenance round trips until the #152 persistence path is integrated. That
integration requirement does not justify keeping verified-invalid entries.
No live library migration or maintenance was used for development or tests.

## Combined tag-metadata integration evidence

The selected-tag worker verified an isolated combination of these exact refs:

- PR #151: `74661d1d83c953bde66a23c7aa64444ebc5ffbe5`
- PR #152: `caa071e58f733a015298fc57effd3a50677290db`
- Local integration merge: `811135024b8eefca64a322f65bf695b098257672`

`SelectedTagRelocationIntegrationTests.Reconcile_RefreshesChangedSelectedTagSnapshotAndFreshCacheAfterMove`
applies selected-photo tags, revises only their XMP snapshot to source
`Imported`, confidence `0.76`, and scan version `relocated-scan-v2`, then moves
the unchanged JPEG and its separate tag sidecar. In that combination,
`ReconcileAsync` repairs the unique-hash move and imports the revised snapshot
in the **same pass**, preserving the original image ID and manual family tag.
Indexing the moved files into a fresh cache restores that provenance as well.

The focused combined run passed **44 tests, zero failures, zero skipped**:

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -p:Platform=x64 --no-build -- -class PhotoLibrarian.Tests.SelectedTagRelocationIntegrationTests -class PhotoLibrarian.Tests.LibraryIndexMaintenanceServiceTests -class PhotoLibrarian.Tests.LibraryIndexingServiceTests -class PhotoLibrarian.Tests.AutoTagReviewViewModelTests -class PhotoLibrarian.Tests.BackgroundAutoTagPortabilityTests -class PhotoLibrarian.Tests.AutoTagRepositoryTests -class PhotoLibrarian.Tests.BatchTagProcessorTests -class PhotoLibrarian.Tests.RecognitionPipelineTests
```

That run also covers background tag-writing round trips, metadata-write
failure/retry, and newer-sidecar races in separate tests. The relocation
scenario itself exercises **selected review**, not background-tag relocation.
It does not export historical development-cache values. Those values do not
block verified-missing cleanup in this pre-release app.

The combined-only test requires both PR heads and is deliberately not compiled
into either standalone PR. Its patch and results are preserved as
`files/selected-tag-relocation-integration.patch` and
`files/selected-tag-relocation-integration-results.txt` in the selected-tag
session's artifacts (session `d838d0e6-1eb8-4af4-ac1c-8df94733ecc3`).
These are local session artifacts, not public downloadable links; apply the
patch only to the combined refs when reproducing. No live library was used.
