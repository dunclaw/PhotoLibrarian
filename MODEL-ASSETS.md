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

No Azure subscription is authenticated in the development environment, and no
generated model bundle is present in the repository. RAM++ publication is
disabled in the release catalog until the pinned export is generated, parity
validated, and its notices reviewed; the deployment workflow refuses to
publish while that gate is closed. TinyCLIP publication is explicitly
blocked: the app profile expects a 40,622,561-byte
ONNX with SHA-256
`9aa0c0c8d4abdb4f80e8f26e74236bb1bf13d43ffaab4e17fb03e48c4e01be0d`, while
the pinned preparation tool currently produces an 85,618,595-byte ONNX with
SHA-256
`0843578fa3e1c386a3be5250ccb13cc8836d062876a34756f380a2e371e86a75`. Do not
publish or relabel those files until the exact application bundle, calibration,
and provenance are reconciled. TinyCLIP remains available through verified
local import.

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

Export assets outside the repository and validate them with the profile's
documented exporter/parity tests. The release builder refuses unexpected hashes
or sizes, missing source provenance, required license notices, and profile
versions that are not marked ready in `tools\ModelAssets\release-profiles.json`.
It writes an immutable, versioned manifest and records source/build revisions,
licenses, notice hashes, asset hashes, and byte sizes:

```powershell
$profile = 'ram-plus.swin-large-14m'
$bundle = 'D:\Models\RAM-Plus-PhotoLibrarian'
$build = git rev-parse HEAD
.\tools\ModelAssets\New-ModelAssetRelease.ps1 `
  -ProfileId $profile `
  -BundleDirectory $bundle `
  -StorageAccountName photolibrarianassets `
  -BuildRevision $build
Compress-Archive -Path "$bundle\*" -DestinationPath "$bundle\$profile-bundle.zip"
```

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
40,622,561 bytes. The current repository preparation tool produces an
85,618,595-byte ONNX with SHA-256
`0843578fa3e1c386a3be5250ccb13cc8836d062876a34756f380a2e371e86a75`; it is
not interchangeable with the calibrated profile. Do not use that output for
the app or a release until the export, calibration, and profile are reconciled.
The exact catalogued files and required Microsoft MIT notice are not present in
this checkout.

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
`ram_plus_manifest.json`, the generated `recognize-anything-LICENSE` and
`recognize-anything-NOTICE.txt`, and the generated `MODIFICATIONS.txt` with
any hosted bundle. The upstream notice, including its retained third-party
attributions, is copied verbatim. If a future export requires ONNX external
data, retain every `.onnx.data*` file beside the model; the release builder
currently refuses such exports because app loading does not support them.

Prepare the export outside the repository:

```powershell
python -m pip install -r tools\PhotoLibrarian.ModelBench\requirements-ram-plus-export.txt
python tools\PhotoLibrarian.ModelBench\export_ram_plus.py `
  --output D:\Models\RAM-Plus-PhotoLibrarian
```

The manifest records every source revision, input/output contract, generated
hash, and external-data hash. Before hosting a generated bundle, run the
parity validation against the pinned upstream PyTorch implementation:

```powershell
python tools\PhotoLibrarian.ModelBench\validate_ram_plus.py `
  --bundle D:\Models\RAM-Plus-PhotoLibrarian\ram_plus_swin_large_14m.onnx `
  --source-dir D:\Models\RAM-Plus-PhotoLibrarian\source-cache\source\recognize-anything-7cb804a8609e9f4b1a50b7f31436d2df40bb9481 `
  --checkpoint D:\Models\RAM-Plus-PhotoLibrarian\source-cache\checkpoint\ram_plus_swin_large_14m.pth `
  --images D:\Models\RAM-Plus-Validation\*.jpg `
  --output D:\Models\RAM-Plus-PhotoLibrarian\parity-report.json
```

The report requires finite outputs, exact `targets` parity, and review of raw
logit/probability errors against the documented tolerance before hosting.
Record CPU/DirectML benchmark results separately; the old X-AnyLabeling-derived
ONNX must not be used as provenance or as the quality reference.

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
