# Automatic tagging model assets

PhotoLibrarian runs automatic-tagging models locally. Model files are not
bundled with the application. In **Settings**, choose a model download where
one is available, or choose **Import model assets** and select a folder
containing the exact compatible files below. Downloads and imports verify the
profile's expected byte count and SHA-256 before activating files in managed
local storage. Photos and generated tags remain on-device.

## Azure distribution status

The application manifest downloader and release/deployment tooling use Azure
Blob Storage in **East US 2**, Hot LRS. Direct Blob HTTPS avoids CDN/Front Door
minimums and extra egress charges for the current low-volume distribution
channel. The public container is read-only to anonymous clients; publishing
uses GitHub Actions OIDC and Azure role assignment, with storage account keys
disabled. CORS permits anonymous `GET`, `HEAD`, and `OPTIONS`; model blobs are
served with one-year immutable cache headers, TLS 1.2 or newer, and HTTPS-only
access. The publisher role is scoped to the blob container and excludes blob
deletion. The release script refuses to overwrite an existing versioned blob;
Azure Blob versioning remains available for operator recovery.

The operator reports creating the role and storage account in
`rg_photo_librarian` / West US 2; live deployment verification and publication
remain on hold. RAM++ now has a locally verified reproducible bundle at
`D:\Models\PhotoLibrarian\RAM-Plus\reproducible-20261031\release-bundle`.
Its exact model/labels identity matches the catalog, and provenance and CPU/
DirectML report fingerprints are pinned under `reviewEvidence`. TinyCLIP's
unchanged calibrated model and embedding bundle have also been reproduced,
under `D:\Models\PhotoLibrarian\TinyCLIP\reproducible-20261031\release-bundle`.
Both profiles have version-pinned Settings download support and verified
local import fallback. Neither manifest has been published by this workstream:
both publication flags stay disabled while the operator's cloud-write hold
is in effect. Release evidence checks are automated internal steps of #68,
not separate user approval milestones or replacement model versions.

### Provisioning

The default storage account name is `photolibrarianassets`; storage names are
globally unique. If it is unavailable, choose another valid name and update
`ModelAssetBaseUri` in `AutoTagModelCatalog.cs` and each profile's `manifestUri`
in `tools\ModelAssets\release-profiles.json` before shipping an app build.
Create a dedicated resource group and deploy the storage account/container:

```powershell
az group create --name photolibrarian-model-assets --location eastus2
$publisherRoleId = az deployment sub create `
  --location eastus2 `
  --template-file infra\azure\model-assets-publisher-role.bicep `
  --query properties.outputs.roleDefinitionId.value `
  --output tsv
if ($LASTEXITCODE -ne 0) { throw 'Publisher role deployment failed.' }
az deployment group create `
  --resource-group photolibrarian-model-assets `
  --template-file infra\azure\model-assets.bicep `
  --parameters storageAccountName=photolibrarianassets `
    publisherRoleDefinitionId=$publisherRoleId `
    publisherPrincipalObjectId=<github-actions-service-principal-object-id>
az deployment sub create `
  --location eastus2 `
  --template-file infra\azure\model-assets-budget.bicep `
  --parameters resourceGroupName=photolibrarian-model-assets `
    alertEmail=<cost-alert-email> monthlyBudgetUsd=25
```

Configure a GitHub Actions environment named `model-assets-production`, with
required reviewers, and create an Azure federated identity credential for the
subject
`repo:dunclaw/PhotoLibrarian:environment:model-assets-production`. The storage
deployment assigns the publisher service principal the custom
`PhotoLibrarian Model Asset Publisher` role on the `model-assets` container;
it permits blob reads and writes but not deletion. Do not grant the broader
built-in `Storage Blob Data Contributor` role. Configure these environment
variables, not secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`, and `AZURE_MODEL_ASSET_STORAGE_ACCOUNT`. No client
secret or storage key is needed.

### Preparing and publishing a release

Run the read-only readiness check even while `publicationReady=false`:

```powershell
.\tools\ModelAssets\Test-ModelAssetRelease.ps1 `
  -BundleDirectory D:\Models\PhotoLibrarian\RAM-Plus\reproducible-20261031\release-bundle
.\tools\ModelAssets\Test-ModelAssetEvidence.ps1 `
  -VerifiedBundleDirectory D:\Models\PhotoLibrarian\RAM-Plus\reproducible-20261031\release-bundle
```

This checks exact artifact bytes, provenance and notice bindings, tensor/
preprocessing contracts, pinned validator/checkpoint identities, and both
eight-input parity reports with exact masks, finite outputs and the reviewed
numeric tolerance. It does not rerun inference, create a cloud manifest, or
authorize publication. The regressions use small temporary fixtures, never
altering the preserved bundle or bypassing the production publication gate.

**Two different manifests:** `ram_plus_manifest.json` is the pinned exporter
provenance, with build inputs and contracts. `model-assets-manifest.json` is
the separately generated hosting manifest, with immutable URLs and the app
profile/version. The latter contains only catalog evidence fingerprints, not
the reports or their developer paths. Release preparation validates the
actual local reports before writing it. The publisher requires exactly the
catalog's evidence receipt and independently checks the public artifacts,
profile identity and URLs; it does not fetch or rerun local reports. The
receipt is a reviewer-controlled binding, not a cryptographic attestation
that an arbitrary manifest author executed the validator.

**Remaining external action:** authorize resuming Azure verification and
publication once, then enable the relevant publication flags as part of that
release operation. Normal code review and the production environment's
deployment approval still apply; no separate artifact/readiness issues or
repeated evidence approvals are required. Do not regenerate fingerprints to
accommodate changed artifacts. While the hold remains, builder and publisher
fail before writing or uploading release output.

Export assets outside the repository and validate them with the profile's
documented exporter/parity tests. The release builder refuses unexpected hashes
or sizes, missing source provenance, required license notices, and profile
versions that are not marked ready in `tools\ModelAssets\release-profiles.json`.
It writes an immutable, versioned manifest and records source/build revisions,
licenses, notice hashes, asset hashes, and byte sizes:

```powershell
$profile = 'ram-plus.swin-large-14m'
$bundle = 'D:\Models\PhotoLibrarian\RAM-Plus\reproducible-20261031\release-bundle'
$build = git rev-parse HEAD
.\tools\ModelAssets\New-ModelAssetRelease.ps1 `
  -ProfileId $profile `
  -BundleDirectory $bundle `
  -StorageAccountName photolibrarianassets `
  -BuildRevision $build
$publicFiles = @(
  'ram_plus_swin_large_14m.onnx',
  'ram_plus_swin_large_14m_labels.txt',
  'recognize-anything-LICENSE',
  'recognize-anything-NOTICE.txt',
  'MODIFICATIONS.txt',
  'model-assets-manifest.json'
) | ForEach-Object { Join-Path $bundle $_ }
Compress-Archive -LiteralPath $publicFiles `
  -DestinationPath (Join-Path (Split-Path $bundle -Parent) "$profile-bundle.zip")
```

Run the preparation commands only after the reviewed gate change, from a
clean committed checkout. The six-file public archive/upload allowlist
excludes exporter provenance and both raw parity reports. Those are local
review evidence; reports contain absolute developer image paths and must not
be attached raw to a public release. Never recursively package the durable
parent's checkpoints, package caches, environments or validation images.

Attach `<profile-id>-bundle.zip` to a reviewed GitHub release. Then dispatch
**Publish model asset release** against that release tag/profile. The workflow
authenticates with OIDC, verifies every file against the manifest, uploads with
`--overwrite false`, and refuses an existing versioned blob with different
content. The application only accepts the exact profile version, source
revisions, hashes, and HTTPS asset paths built into its catalog. A failed or
interrupted download leaves a `.partial` file for Range-based retry; the model
is not activated until the exact size and SHA-256 pass. The Settings download
can be canceled, and verified local import remains an offline fallback.

### Cost, monitoring, and rollback

Use East US 2 Hot LRS and no CDN until observed volume justifies it. The Azure
Retail Prices API (`https://prices.azure.com/api/retail/prices`), checked
2026-10-23, lists Hot LRS capacity at about **$0.0184/GB-month** and standard
internet egress at **$0.087/GB after the first 100 GB/month free allowance**.
At the currently catalogued asset sizes (~1.95 GB total for RAM++ and TinyCLIP),
storage is about **$0.04/month**. One hundred full installs per month would
transfer about 195 GB; estimated egress is about **$8.30/month** after the
allowance. Transaction charges, taxes, discounts, and price changes are not
included; use the Azure Pricing Calculator before rollout. The subscription
budget template alerts the configured address at 50%, 80%, 100%, and forecast
100% of a **$25/month** resource-group budget.

Azure Monitor's aggregate Blob service metrics (availability, transactions,
ingress/egress) provide basic service/download telemetry without enabling
request logs or collecting IP addresses, file names, photo paths, user IDs, or
library data. Review egress and budget alerts monthly.

Never overwrite or delete a published version to roll it back. Blob versioning
and 30-day soft delete support operator recovery. To revert an application
release, ship a build whose catalog pins the prior manifest/profile hashes;
to retire an asset, remove references in a later app release and retain the
old blob for rollback. The publisher workflow is append-only by policy and
requires a reviewed release bundle.

## TinyCLIP

Required files:

- `tinyclip-vision-int8.onnx`
- `tinyclip.json`

The upstream model is
[`wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M`](https://huggingface.co/wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M)
and the repository's current pinned ONNX conversion is
[`onnx-community` conversion](https://huggingface.co/onnx-community/TinyCLIP-ViT-40M-32-Text-19M-LAION400M-ONNX).
The catalogued PhotoLibrarian profile requires model SHA-256
`9aa0c0c8d4abdb4f80e8f26e74236bb1bf13d43ffaab4e17fb03e48c4e01be0d` and
40,622,561 bytes. The 85,618,595-byte ONNX with SHA-256
`0843578fa3e1c386a3be5250ccb13cc8836d062876a34756f380a2e371e86a75` is the
pinned combined image/text **source**, not the runtime artifact. The preparation
tool extracts the vision graph and recenters integer convolution weights for
CPU support; this reproduces the exact catalogued model. No changed weights,
embeddings, thresholds, vocabulary, hierarchy or approval identity are needed.

The original `tinyclip.json` used canonical LF vocabulary bytes. A Windows
CRLF checkout otherwise changes only its `vocabularyFileSha256` metadata and
therefore the bundle's overall fingerprint. The verified profile mode
canonicalizes that input before generating embeddings, and refuses to write
a success preparation manifest unless both runtime asset sizes and hashes
match the calibrated profile exactly:

```powershell
# Developer-only Python 3.12 environment; never required by app users.
python -m pip install --require-hashes `
  -r tools\PhotoLibrarian.ModelBench\requirements-tinyclip-release.txt
python tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\TinyCLIP-NewBuild `
  --models tinyclip --precision int8 --threads 4 `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.general-v2.json `
  --verify-tinyclip-profile
.\tools\ModelAssets\Test-ModelAssetRelease.ps1 `
  -ProfileId tinyclip.vit-40m-32.int8 `
  -BundleDirectory D:\Models\PhotoLibrarian\TinyCLIP\reproducible-20261031\release-bundle
```

The release environment is fully hash-pinned, including 29 transitive/tooling
distributions, in `requirements-tinyclip-release.txt` for Windows x64 and
CPython 3.12.0. Exact runtime bytes were reproduced in two preparation
environments and a fresh environment restored fully offline from those wheels.
Output verification rejects environment drift that changes runtime bytes.
Retained sources, wheels, tooling and generated evidence are independent of
Copilot worktrees under the permanent TinyCLIP artifact root noted above.
Downloads retain verified license/notice files alongside the model.
The generated `preparation-manifest.json` and synthetic `reference.json`
remain local evidence and their fingerprints are catalog-pinned. The public
TinyCLIP allowlist is `tinyclip-vision-int8.onnx`, `tinyclip.json`, `LICENSE`,
`NOTICE.txt`, and the separate `model-assets-manifest.json`. Copy the retained
upstream MIT license verbatim (including its third-party references) and
`tools\ModelAssets\TinyCLIP-NOTICE.txt` as `NOTICE.txt`; do not publicly package
source caches, Python environments, synthetic tensors or reference reports.
The app's explicit trust step remains required after installation.

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
`recognize-anything-NOTICE.txt`, and the generated `MODIFICATIONS.txt` with
any hosted bundle. The upstream notice, including its retained third-party
attributions, is copied verbatim. The exporter records actual model/labels
sizes, checkpoint hashes, exporter/requirements hashes, and Python/package
versions. If a future export requires ONNX external data, retain every
`.onnx.data*` file beside the model. Such an export is not eligible for the
current two-file app profile or Azure release gate because app loading does
not support external data.

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
