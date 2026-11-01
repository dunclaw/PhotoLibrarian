# Library index maintenance

Use **Settings > Library index maintenance > Repair library index** to scan
the configured watched folders. The operation reads local files and updates
the existing SQLite database incrementally; it does not drop, recreate, or
vacuum the database. Newly discovered or changed media is indexed through the
normal indexing service.

The database is not merely disposable indexing data. It also contains
user-applied tags, people assignments and face data, flags, and cached
duplicate hashes. A full database rebuild would lose information that may not
be present in a photo or sidecar. Maintenance therefore retains database rows
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
