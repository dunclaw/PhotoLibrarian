# Tiled codec/resource feasibility for #195 (P1)

This is a **test-only feasibility gate**, not an enabled editor feature or a
replacement for the existing renderer. No mask UI, recipe schema, adjustment
graph, RAW pipeline or production quick-edit behavior changes in this stage.
The branch was refreshed to master after #180/#209 merged
(`9f42b8c3e8c7664837abf67a2b966062bca51e6e`).

## Result and boundary

A photo does not need to fit one GPU texture or one managed byte array:
the prototype reads source tiles, uploads/draws/readbacks two bounded Win2D
textures at a time, writes a row-addressable disk surface, then encodes from
small complete-row bands with native WIC `WritePixels`. Output is not returned
as a managed full-frame buffer. There is no input megapixel/dimension gate.

**Conditional go for this architecture, not a universal codec-memory guarantee.**
Sequential staging to disk is the appropriate compressed-input boundary.
WIC's internal allocation and cancellation behavior is not controlled by the
caller buffer size. In particular, interlaced PNG is a measured counterexample
to a hard bounded-memory claim. Existing LibRaw demosaic still has its existing
full-frame native allocations; P1 does not change or benchmark it.

If #195 requires a strict total process-memory ceiling including every input
codec, the current WIC-only design **does not meet that requirement**. That
would require another reviewed codec strategy (for example a decoder with
explicit disk-backed interlace/progressive reconstruction), not an image-size
cap or silently downsampling the source. Otherwise retain bounded
application-owned tiles, report actual codec/allocation failures, and describe
unavoidable codec memory honestly. This stage does not authorize later stages.

## Prototype contracts

`src\PhotoLibrarian.Tests\TileCodecFeasibility.cs` contains working experimental
`IBgraTileSource` / `IBgraTileSink` contracts and a checked `PixelTile`:

- Stored-pixel, top-left coordinates; tightly packed premultiplied BGRA8.
  The caller owns the tile buffer. EXIF orientation, ICC management, geometry,
  halos, metadata and edit recipes are deliberately not implemented here.
- `BgraScratchSurface` uses checked 64-bit byte offsets and positional file
  I/O, with cancellation checked each row. Its disposable file is private,
  created without overwrite and deleted on close. A sparse fixture verifies
  round-trip access past 4 GiB without allocating a corresponding array.
- `WicTileSource` wraps native `CopyPixels` on the first frame with on-demand
  metadata. The experiment demonstrates sequential bands, forward tiles and
  reverse tile reads separately. It does not infer cache behavior from the API.
- `Materialize` decodes complete row bands to the disk surface once; GPU and
  later random access can use that surface instead of restarting a compressed
  decode for every tile.
- GPU copy reuses two managed arrays and has at most two live texture objects.
  Last-row/column tiles retain their actual dimensions. `AsBuffer` exposes only
  the current tile's bytes without a new full-image copy.
- Encoding reads complete row bands from a source, converts native WIC pixel
  formats by band, and writes PNG/JPEG/TIFF/BMP with
  `WICBitmapEncoderNoCache`. A unique staging file is published by a
  non-overwriting move only after successful commit and a final cancellation
  check. Existing destinations survive, and cancellation removes staging.
  Interlaced PNG encoding exists only to measure the codec counterexample;
  ordinary PNG output is non-interlaced.

`native\PhotoLibrarian.TileCodecProbe` uses Windows WIC/COM only, no third-party
library or runtime download. `tools\Build-TileCodecProbe.ps1` builds it for the
test project only. Neither its DLL nor its measurement executable is referenced
or packaged by the app. The native source/sink lifetime is synchronous and
apartment-affine: open, use and dispose on the same thread. These are prototype
contracts, not yet a production asynchronous/native ownership API.

## Resource evidence

Measurements below are synthetic, local x64 Release observations on Windows
11, .NET 8 and Win2D 1.4, not promises for every codec, photo, GPU or computer.
No personal library images were accessed. Codec fixtures use repeating opaque
color rows; PNG interlace is explicitly set and checked in the IHDR.
High-entropy photos, progressive JPEG, higher-bit-depth/paletted/CMYK inputs,
third-party codecs and all TIFF compression variants are not established by
these measurements.

### Application-owned resources

The actual GPU reported a maximum bitmap dimension of 16,384. A generated
16,401 x 131 source rendered through **258** 128-pixel tiles, persisted to disk,
encoded to PNG, then reopened at the same dimensions and sampled against the
source. It never created a 16,401-pixel-wide GPU bitmap.

Generated 8,256 x 5,504 (~45 MP) and 12,288 x 8,192 (~101 MP) sources completed
187 and 384 tiles respectively, were written to scratch, incrementally encoded
as PNG and reopened at their full dimensions. Every rendered tile was checked
against the synthetic source, with additional reopened-output samples. Each used
the same two 512 x 512 managed pipeline pixel buffers
(2 MiB total) and no more than 2 MiB of **live texture pixel payload**.
The four-times-area fixture also checks the same buffer/texture envelope and
four times as many tiles. Alpha, irregular edge tiles and lossless PNG reopen
are covered separately.

These counters describe buffers allocated by the prototype and the logical
pixel payload of its live textures, **not measured total VRAM**. Driver heaps,
alignment, COM objects, framework caches and temporary codec allocations are
not represented by the texture counter. GPU operations are serialized; no
unbounded work queue is created.
The test oracle has its own single reusable tile buffer; it is not counted as
pipeline memory and does not allocate a full-resolution reference image.

Sequential decode/encode uses a managed band of approximately 256 KiB, or one
complete row if that row is larger. This is a work budget, not an image-width
limit. Native encoding also has the WIC band bitmap and converted band. Scratch
storage is four bytes per pixel per surface and scales with image area;
available disk space is a real resource requirement, not an arbitrary size
gate. P1 does not implement a production scratch-quota/recovery manager.

### Codec-owned resources

`PhotoLibrarian.CodecMemoryProbe.exe` runs one operation per fresh native process,
outside CLR and GPU caches. It samples process private bytes every millisecond,
also samples at row/tile boundaries, and reports open/processed/commit values,
peak working set, caller-buffer size and longest individual synchronous call.
The JSON is emitted by the focused tests. Private-byte growth includes COM/WIC
setup, conversion buffers and codec internals; it is not attribution to a
particular codec allocation. Shorter-lived peaks can escape sampling.

Representative 8,192 x 6,144 (~50 MP) results, subtracting each process's baseline:

| Codec | Sequential encode private growth | Sequential decode private growth | Sequential decode / tile traversal |
| --- | ---: | ---: | ---: |
| PNG, non-interlaced | ~1.8 MiB | ~0.8 MiB | ~32 ms / ~6.5 s |
| JPEG, baseline | ~1.4 MiB | ~0.6 MiB | ~115 ms / ~36 s |
| TIFF, WIC default | ~1.5 MiB | ~0.9 MiB | ~238 ms / ~8 s |
| BMP, WIC default | ~1.2 MiB | ~0.7 MiB | ~51 ms / ~0.8 s |
| PNG, interlaced | **~194 MiB** | **~97 MiB** | ~81 ms / ~21 s |

The same probe runs at 2,048 x 1,536 to expose scaling. Interlaced PNG encode
growth increased from ~13.5 MiB to ~194 MiB when area increased sixteenfold;
decode grew from ~6.4 MiB to ~97 MiB. A separate 12,288 x 8,192 non-interlaced
PNG run used a 245,760-byte caller band and ~1.9 MiB encode / ~1.0 MiB decode
private growth. These are measured cases, not supported-size ceilings.

This proves both that incremental I/O is usable and that `NoCache` plus a
small caller buffer is insufficient to promise low codec memory. Direct
compressed random access was especially expensive for baseline JPEG and
interlaced PNG even when allocations remained modest: use sequential staging,
not repeated WIC region decode for viewport tiles.

## Cancellation and safety evidence

The tests cancel after active GPU work, after actual codec reads, during
encoding, and after codec commit immediately before publication. They check
typed cancellation, no published partial result, no overwrite of an existing
destination, scratch/staging cleanup and suppression of stale output when a
source returns after cancellation.

WIC `CopyPixels`, `WritePixels`, codec open/commit and a GPU draw/readback are
synchronous: a cancellation token cannot interrupt an in-flight call. Work
checks cancellation before and after those boundaries and between rows/tiles.
The probe reports the longest individual codec call; that is an observed
cooperative-cancellation blocking interval, not a guaranteed future deadline.
Interlaced decode may do substantial whole-image work on the first request.
In the recorded ~50 MP run, the longest synchronous interlaced PNG encode call
was about 317 ms; sequential interlaced decode reached about 23 ms, and direct
JPEG tile access about 94 ms. These are observations on synthetic files, not
timeouts or limits imposed by the application.
No thread abort, forced termination of the app, or success-shaped fallback is
used. The test runner terminates only its own measurement child process if the
test is cancelled.

Production promotion still needs the planned source/metadata identity checks,
orientation/color policy, disk-error/recovery handling, async ownership, codec
corpus/device coverage and resource-pressure behavior. P1 is not a replacement
for those P2-P8 requirements.

## Reproduce

The existing Windows SDK/Visual C++ build prerequisites are sufficient.

```powershell
dotnet run --project src\PhotoLibrarian.Tests\PhotoLibrarian.Tests.csproj -c Release -p:Platform=x64 -- -class PhotoLibrarian.Tests.TileCodecFeasibilityTests -class PhotoLibrarian.Tests.ViewerQuickEditTests -parallelMode none
```

The quick-edit tests cover the integrated #180 baseline. Inspection confirms
its Auto brightness/Auto color and Rotate left/right still update portable
pending recipes and use shared history. They are **not** pixel-baking commands,
and must remain distinct from future local-recipe baking guards.

The final combined x64 run passed **26 tests**. Existing repository build
warnings remain; the feasibility code introduced no new build warnings.

The native probe also cross-builds for ARM64 with:

```powershell
.\tools\Build-TileCodecProbe.ps1 -Architecture ARM64 -OutputDirectory .\src\PhotoLibrarian.Tests\obj\TileCodecProbe\ARM64\Release
```

ARM64 compilation does not establish ARM64 execution or allocation behavior.
No application UI or user photo data is needed for either command.
