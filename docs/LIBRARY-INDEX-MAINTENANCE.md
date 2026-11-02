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

The current implementation does not yet prove that every stored field can be
reconstructed. In particular, keyword import does not restore tag provenance
or confidence; these must not silently be treated as recovered values. Legacy
pending metadata exports can also contain changes not yet saved to the files.
Retaining those values until they are safely persisted is a compatibility
safeguard, not permission to make SQLite authoritative. Maintenance retains database rows
for confirmed missing files and for locations that cannot be accessed. The
report distinguishes confirmed missing files from unavailable folders and
access errors; it never removes a record just because a filesystem existence
check returns false. Symbolic links and other reparse points are skipped and
reported rather than followed.

A move or rename is repaired only when an existing duplicate-scan SHA-256
cache entry still matches the indexed file size and modification time, and
exactly one newly found file has the same full content hash. Ambiguous matches,
stale hashes, and files with sidecar state that cannot be verified are left
unmatched and their original records remain available. No filename-only
matching is used.

Canceling or encountering an error keeps already completed per-file work.
Retrying scans the same watched folders and safely resumes; incomplete or
unavailable locations are reported rather than presented as a successful
complete scan. Filesystem watcher delete and missing-directory notifications
also retain index records so transient storage loss cannot cascade-delete
database-only information.

The maintenance action never modifies or deletes photo originals or writes
sidecars. Files with a pending people-metadata export are reported and skipped
so maintenance cannot cause a sidecar write. Any database-only data remains
attached to its original image record unless a unique exact-hash move is
safely repaired, in which case the same record and its related data follow
the new path.

## Person picture portability

Choosing a person's picture now saves the selection in that photo's
PhotoLibrarian XMP face-review metadata before changing the cache. Choosing a
different picture also clears the previous file's selection, with the existing
metadata batch backup/rollback behavior. Import locates the selected face by its
portable region metadata rather than copying SQLite IDs. A fresh database can
therefore restore the choice after the photos and sidecars move.

Older face-review packets without this property remain readable. Their absence
does not clear an existing cached choice. Existing database-only picture choices
are not automatically exported by index maintenance, which remains read-only
toward originals and sidecars; reselecting a picture writes it portably. Until
legacy state has been persisted and all provenance gaps are closed, deleting the
database is not a verified lossless recovery procedure.

## Remaining persistence-contract gaps

| Cached field | Current reconstruction behavior | Required before claiming lossless clean rebuild |
|---|---|---|
| `tags.source` | Keywords imported from files are labeled `Metadata`, regardless of their original `Manual` or `AutoML` source. | Persist and import tag provenance without changing user tags or falsely promoting suggestions. |
| `tags.confidence` | Keyword import assigns `1.0`, not the original generated confidence. | Preserve source confidence in associated metadata where it is retained as meaningful provenance. |
| `images.auto_tag_scan_version` | Not exported with keywords or restored by indexing. | Persist/restore provenance where required, or explicitly recompute derived scan state rather than claim the old state was recovered. |
| Legacy `persons.representative_face_region_id` | Previously saved only in SQLite; new selections now round-trip through XMP. | Explicitly persist historical choices before removing their cache. Do not copy database IDs into portable metadata. |
| `images.face_metadata_export_required` and associated pending face state | Indicates cached face information still needs export; maintenance skips it and reports it. | Complete a deliberate, verified portable export, reporting unavailable files and write errors without discarding the pending state. |

These are not reasons to make the cache the source of truth. They are remaining
compatibility work. The Settings command offered here is **cache-preserving
repair**, not a clean rebuild or a certification that an existing database can
be discarded. It neither exports historical state nor bypasses the gaps above.
