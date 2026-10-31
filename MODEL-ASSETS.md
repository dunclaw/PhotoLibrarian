# Automatic tagging model assets

PhotoLibrarian runs automatic-tagging models locally. Model files are not
currently bundled with the application. In **Settings**, select a profile,
follow its source link if desired, then choose **Import model assets** and
select a folder containing the exact compatible files below. Imports verify
the files before copying them into managed local storage.

## TinyCLIP

Required files:

- `tinyclip-vision-int8.onnx`
- `tinyclip.json`

The compatible bundle is generated from
[`wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M`](https://huggingface.co/wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M)
and its pinned
[`onnx-community` conversion](https://huggingface.co/onnx-community/TinyCLIP-ViT-40M-32-Text-19M-LAION400M-ONNX).
Run the repository preparation tool outside the source tree:

```powershell
python tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\TinyCLIP `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.general-v2.json `
  --models tinyclip --precision int8
```

TinyCLIP is published under the
[MIT license](https://github.com/microsoft/Cream/blob/4a13c4091e78f9abd2160e7e01c02e48c1cf8fb9/TinyCLIP/LICENSE).
Its weights and PhotoLibrarian's derived ONNX/embedding bundle can be hosted or
redistributed with the Microsoft copyright and MIT permission notice. The model
was trained on LAION-400M; its provenance and content caveats still apply.

## RAM++

Required files:

- `ram_plus_swin_large_14m.onnx`
- `ram_plus_swin_large_14m_labels.txt`

PhotoLibrarian owns the conversion because upstream publishes PyTorch, not ONNX.
The reproducible exporter is
[`tools/PhotoLibrarian.ModelBench/export_ram_plus.py`](tools/PhotoLibrarian.ModelBench/export_ram_plus.py).
It pins upstream source commit
`7cb804a8609e9f4b1a50b7f31436d2df40bb9481`, checkpoint repository revision
`84d4aee3a0265c4e0df1f714f0572011d1bf2ec3`, verifies both 3 GB checkpoint
inputs, verifies the authoritative 4,585-label vocabulary, and emits the
application-compatible `targets` output plus raw logits and probabilities. The
fixed contract is `image` float32 `[1,3,384,384]` (direct resize with ImageNet
normalization) and three float32 `[1,4585]` outputs. Keep
`ram_plus_manifest.json` and the generated `recognize-anything-LICENSE` and
`recognize-anything-NOTICE.txt`, including its embedded third-party notices,
and `MODIFICATIONS.txt` with any hosted bundle. The exporter records actual
model/labels sizes, checkpoint hashes, exporter/requirements hashes and
Python/package versions. If a future
export requires ONNX external data, retain every `.onnx.data*` file beside the
model. Such an export is not eligible for the current two-file app profile or
the release gate in Azure hosting PR #128.

Prepare the export outside the repository:

```powershell
tools\PhotoLibrarian.ModelBench\New-RamPlusExportEnvironment.ps1 `
  -EnvironmentDirectory D:\Models\RAM-Plus-ExportEnv
D:\Models\RAM-Plus-ExportEnv\Scripts\python.exe tools\PhotoLibrarian.ModelBench\export_ram_plus.py `
  --output D:\Models\RAM-Plus-PhotoLibrarian
```

The supported byte-reproducible build is **CPython 3.12.0, Windows x64, CPU-only
PyTorch 2.8.0 and torchvision 0.23.0**. The environment script creates a new venv,
installs the pinned fairscale build backend, restores all 38 direct/transitive
distributions using SHA-256-locked requirements, and checks the installed graph.
It never modifies an existing environment. For offline restoration, retain the
downloaded package distributions and add `-PackageCache <folder>`; this uses
`--no-index`, verifies the same hashes and builds fairscale from its pinned source.
To populate the cache after bootstrapping the environment:

```powershell
D:\Models\RAM-Plus-ExportEnv\Scripts\python.exe -m pip download `
  --require-hashes --no-deps --no-build-isolation `
  -r tools\PhotoLibrarian.ModelBench\requirements-ram-plus-export.txt `
  --dest D:\Models\RAM-Plus-Packages
```

`ram-plus-build-lock.json` verifies all 46 upstream Python, configuration,
vocabulary and notice files before importing source, including a supplied
`--source-dir`. The exporter rejects interpreter/package drift, sets deterministic
CPU execution and verifies the final model and labels against their locked
sizes/hashes before replacing the model or writing a success manifest. There is
no option to bypass the identity check. Text fingerprints use canonical LF
endings, with matching Git attributes, so manifests are stable across checkouts.
Changing the supported build or artifact requires a reviewed lock update.

The manifest records every source revision, input/output contract, generated
hash, and external-data hash. Before hosting a generated bundle, run the
parity validation against the pinned upstream PyTorch implementation:

```powershell
D:\Models\RAM-Plus-ExportEnv\Scripts\python.exe tools\PhotoLibrarian.ModelBench\validate_ram_plus.py `
  --bundle D:\Models\RAM-Plus-PhotoLibrarian\ram_plus_swin_large_14m.onnx `
  --source-dir D:\Models\RAM-Plus-PhotoLibrarian\source-cache\source\recognize-anything-7cb804a8609e9f4b1a50b7f31436d2df40bb9481 `
  --checkpoint D:\Models\RAM-Plus-PhotoLibrarian\source-cache\checkpoint\ram_plus_swin_large_14m.pth `
  --images D:\Models\RAM-Plus-Validation\*.jpg `
  --output D:\Models\RAM-Plus-PhotoLibrarian\parity-report.json
```

Image wildcards are expanded by the validator, including on Windows. Unmatched
patterns are errors. Use pinned public upstream demo photos and/or deterministic
synthetic images; do not upload personal library photos for validation.

The validator executes upstream `generate_tag`, captures its logits, and checks
the complete named input/output contract, dtype, labels serialization, checkpoint
hash, finite reference/runtime outputs, binary masks, exact `targets` parity and
elementwise raw tolerances (`rtol=1e-3`, `atol=1e-4`). It writes a report tied to the
model, labels, checkpoint and image hashes, then exits unsuccessfully on any
parity failure. A `passed: true` report is numeric evidence, **not** quality,
calibration or publication approval.

CPU is the default. For full DirectML parity, keep the hash-locked CPU environment
unchanged and install the validation-only runtime into a separate overlay:

```powershell
D:\Models\RAM-Plus-ExportEnv\Scripts\python.exe -m pip install `
  --require-hashes --no-deps --target D:\Models\RAM-Plus-DirectML `
  -r tools\PhotoLibrarian.ModelBench\requirements-ram-plus-directml.txt
$env:PYTHONPATH = 'D:\Models\RAM-Plus-DirectML'
# Run the validator above with:
# --provider directml --output <folder>\parity-report-directml.json
Remove-Item Env:\PYTHONPATH
```

Use a new overlay directory, not one shared with another Python environment.
The overlay can also be restored from a package cache using `--no-index
--find-links <folder>`. A missing
provider is an error, not a silent CPU fallback. DirectML sessions use sequential
execution with memory patterns disabled. Retain both provider reports and record
warmed CPU/DirectML benchmark results separately with the existing C# ModelBench.
The old X-AnyLabeling-derived ONNX must not be used as provenance or as the quality
reference. These are developer-only tools; the installed application does not
depend on Python.

### Reproduced release bundle, 2026-10-31

The durable, **review-ready but not published** bundle is outside Git at
`D:\Models\PhotoLibrarian\RAM-Plus\reproducible-20261031\release-bundle`.
Its parent retains two independently created export environments, the
SHA-256-verified package cache, a freshly downloaded verified upstream source,
public/synthetic validation provenance, original-vs-generated comparison,
`reproducibility-report.json`, runtime reports and `HANDOFF.md`. No weights or
photos are committed.

| Artifact | Actual bytes | Actual SHA-256 |
|---|---:|---|
| `ram_plus_swin_large_14m.onnx` | 1,849,873,814 | `FF29E0E18E80B8F2FDC2566B9368BD904664CDC1FCE380F57E73DCF03F4ADDA3` |
| `ram_plus_swin_large_14m_labels.txt` | 41,905 | `1A6C943DD251993770E7CF6FED23A38B7AC068F4C8FBC7A0DB85CBE0FE5221B3` |

Both independent exports produced identical model, labels, notices **and
provenance manifest**. The second environment was restored entirely offline
with hash verification and exported using a freshly downloaded source archive.
The model is byte-for-byte identical to the original official export from #74,
which was recovered at `D:\Models\RAM-Plus-PhotoLibrarian`. There is no external
data. Eight safe inputs (four pinned
upstream demo photos and black/white/gradient/seeded-noise synthetic images) pass
full exact-mask parity on CPU and DirectML. Maximum absolute logit/probability
errors are `1.33515e-5`/`2.02656e-6` on CPU and
`3.29018e-5`/`1.84775e-6` on DirectML. Warmed C# inference averaged approximately
381 ms CPU and 20 ms DirectML on this machine. Sampled peak process working sets
were approximately 2.68 GiB CPU and 2.28 GiB DirectML, excluding dedicated GPU
allocations. `runtime-evidence.json` records hardware, sampling and timings;
these are local diagnostics, not cross-hardware performance guarantees.

The existing app import/checksum/load/predict integration test and related
catalog/import tests pass on this exact bundle. Their results are retained in
`app-integration-tests.xml`.

**The hash mismatch is resolved without changing the model or calibrated
profile.** The earlier session exported with PyTorch 2.8.0 despite committing
requirements for 2.7.1. Direct comparison found that the `4F91...` export differs
only in ONNX producer-version metadata: the graph and all 372 weight tensors are
identical. Running the real 2.8.0 exporter, not editing metadata afterwards,
reproduces the catalog's `FF29...` hash. The original manifest's development
ONNX and CUDA packages are unnecessary; the locked stable CPU build reproduces
the exact bytes.

No catalog hash/version substitution or recalibration is needed because the
authoritative model is unchanged. This bundle supplies the artifact/provenance
prerequisite for the reviewed Azure release path. Publication still requires its
own review and deployment approval; this work does not enable release gates,
provision Azure resources or authorize publishing. TinyCLIP remains out of scope.

The upstream
[`recognize-anything-plus-model`](https://huggingface.co/xinyu1205/recognize-anything-plus-model)
checkpoint and
[`recognize-anything`](https://github.com/xinyu1205/recognize-anything) source are
[Apache-2.0](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/LICENSE).
Hosting or redistribution must retain the upstream
[`LICENSE`](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/LICENSE)
and
[`NOTICE.txt`](https://github.com/xinyu1205/recognize-anything/blob/7cb804a8609e9f4b1a50b7f31436d2df40bb9481/NOTICE.txt),
and identify the ONNX as a PhotoLibrarian conversion.

## Developer benchmark

The profile benchmark is a development diagnostic and is hidden from the normal
Settings UI. With Settings open, press **Ctrl+Shift+B** to show or hide it.
Benchmarking never writes generated tags to photos or the library.
