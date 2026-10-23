# Compact home-photo tagger: research foundation

This is the **research pilot for #66**, including measured 44-, 68- and
author-first 42-label models,
not a shipping profile or completion of that issue. Training/export tools are
separate from the application; RAM++/TinyCLIP profiles, existing approvals and
photo metadata are unchanged. Local exploratory artifacts are not licensed or
quality-approved shipping bundles.

**Latest status:** the user-approved one-time author-first test is complete.
Fixed calibrated known-entry precision/recall is **92.71% / 49.70%** on 1,425
photos. The reserve is now **consumed**, not available for further tuning or
untouched-test claims. Earlier "unscored" statements below record the protocol
chronology before this evaluation. See the final-test results and limitations;
this is not a production approval or exhaustive emitted-tag precision claim.

**Current trainer:** new runs initialize MobileNetV3-Large from random weights;
the default does not download or load ImageNet checkpoints. Full-network
training, including BatchNorm running statistics, uses the fixed 20-epoch
scratch recipe below. Historical ImageNet-initialized results remain research
history only and are not evidence for a scratch-trained model. The author-first
development cohort is previously exposed and is used only for model selection;
both calibration and test cohorts are consumed and must not be rescored or used
to tune this candidate.

The maintainer approved the pilot vocabulary and chose **public Open Images
training data**. A personally labeled collection or hand-written JSONL inventory
is **not a prerequisite**. Use the public-data workflow below; the local audit
remains available for optional consented private data. Existing reviewed
comparison photos should stay out of training and remain a regression set,
not be relabeled from scratch or claimed as an untouched final test.

## Vocabulary proposal and rationale

`pilot-vocabulary.csv` proposes **100 concepts across 10 categories**. This is a
semantic review proposal, not a user-study finding, validated search-demand
ranking, or subset selected using the existing human-scored comparison. It is
separate from both personal tags and the application's 3,632-concept zero-shot
vocabulary. No runtime vocabulary is replaced.

| Category | Concepts | Proposed home-photo retrieval need |
| --- | ---: | --- |
| animals | 15 | Pets, farm visits and common wildlife; broad labels when species is uncertain |
| nature | 15 | Plants, gardens, scenery and water; no named places |
| buildings | 10 | Homes, streets, play areas, markets and temporary shelters |
| transport | 10 | Everyday travel and visible mobility equipment |
| home | 10 | Rooms, furniture, books and common devices |
| food | 10 | Ingredients, prepared meals and drinks across varied cuisines |
| activities | 10 | Visibly supported actions, not inferred intention |
| people | 5 | People together and visually supported gatherings, not kinship |
| weather | 5 | Visible weather across climates; no calendar-season inference |
| objects | 10 | Common belongings, play objects and instruments |

Every concept has a description, exclusion rule, optional parent and explicitly
reviewable aliases. Aliases are accepted only as written; no fuzzy matching,
case folding, path stripping, or automatic personal-tag interpretation occurs.
Some aliases intentionally fit a broader pilot scope, such as `pony` under
`horse` and `stream` under `river`; they are not claims of biological equivalence.
`sunset or sunrise` deliberately avoids guessing time of day. Categories are
organizational, not extra prediction targets. A positive child does not imply
that its parent was separately annotated; ancestors remain unknown unless
reviewed. Positive-child/negative-ancestor contradictions are errors.

Global annotation rules:

- Label visible subjects, not inferred identities, relationships, diagnoses,
  sensitive traits, exact places, dates, ownership, emotions, occupations or
  religious/cultural affiliation. An observable wheelchair is an object, not a
  diagnosis. `celebration` does not license inferring a holiday or religion.
- Use broad labels when detail is insufficient; omit uncertain specific labels.
  Do not label depictions on clothing/screens as real objects. Toys/statues do
  not establish the depicted animal/vehicle; use the appropriate visible-object
  concept instead.
- Review positives and negatives against each concept's scope. Absence of a
  personal tag, detector box, or prediction is **unknown**, never a negative.
  Crop/occlusion ambiguity can remain unknown.
- Keep private identities and personal extensions out of this general head.
  Preserve original tags locally in the source system; export only reviewed
  pilot mappings, not free-form personal names.

Before freezing V1, seek review from households beyond the maintainer's library:
urban/rural homes, different regions/climates, cuisines, celebrations, ages,
mobility needs, indoor/night/action photography, phone/camera sources and
landscape/portrait orientations. Record missing concepts and ambiguous rules
without adding sensitive inferences. This list underrepresents rare wildlife,
specific foods, sports and event types intentionally; expand toward 100-300
concepts only with usable annotations and new untouched evaluation data.

## Local read-only inventory and audit

Python 3.12+; standard library only. Start with a consented export from the
maintainer's library, not an automatic filesystem/database scan. The tool reads
only explicitly listed files, hashes their bytes, and never opens photos or
metadata for writing. It does not decode images, discover near duplicates,
infer events, contact a server, or verify legal rights.

Supply UTF-8 JSONL, one record per photo. The following is a **synthetic contract
example**, not observed training data:

```json
{"id":"photo-001","path":"trip\\001.jpg","source":"private-library-a","sourceId":"local-001","groups":["event:a-001","near-duplicate:a-001"],"priorEvaluation":false,"labels":{"dog":"positive","cat":"negative","bird":"unknown"},"provenance":{"imageLicense":"private-no-redistribution","annotationLicense":"private-no-redistribution","attribution":"local consent record reference"}}
```

All top-level fields are required; extra top-level fields are rejected to catch
schema drift. IDs must be unique. `sourceId` identifies the original item within
its `source` (e.g. Open Images ID), independently of a filename/encoding.
`path` is relative to `--images-root` and must resolve inside it, including
symlink resolution. Unknown/unmapped labels, conflicting aliases, invalid
states, missing provenance, duplicate JSON keys and duplicate IDs fail explicitly.
`provenance` may carry additional local details such as original URL, author,
license URL, annotation source, dataset version and mapping revision; these are
retained verbatim in the private manifest.

`groups` must contain at least one **reviewed** grouping ID. Namespace group IDs
globally (e.g. `event:library-a:2026-trip-1`); every shared ID connects records
even across sources. Include event, burst, edited-copy and near-duplicate links.
Use a singleton only after reviewing isolation, not to bypass event review.
The tool unions these links transitively with identical SHA-256 bytes and
repeated `(source, sourceId)`, before assigning any split. Byte equality alone
does not detect resized/recompressed duplicates. Review cross-source overlap,
including downloads or exports of the same personal photo.

`priorEvaluation` must explicitly mark **any** prior use for model selection,
vocabulary changes, threshold tuning or human comparison. This includes the
existing 299-photo comparison and its human-rated subset. All connected records
then go to training only; none can enter validation, calibration or test.
This cannot establish that the remaining files are genuinely untouched: the
maintainer must attest that history and reserve a fresh test set.

```powershell
python tools\PhotoLibrarian.ModelTraining\prepare_dataset.py `
  --input D:\PrivateTraining\inventory.jsonl `
  --images-root D:\PrivatePhotos `
  --output D:\PrivateTraining\audit-v1 `
  --seed compact-pilot-v1
```

Outputs are `manifest.jsonl` (paths, hashes, provenance, group IDs, split and
ordered targets) and `audit.json` (vocabulary/input/dataset fingerprints, per-tag
positive/negative/unknown counts and positive/negative independent-group counts
per split, known-label fraction, duplicate counts, warnings). Target states are **1 positive, 0
negative, -1 unknown**, in `audit.json`'s ordered `labels` array. Unknowns must
be masked by any future trainer/evaluator. A duplicate byte/source identity with
conflicting known annotations fails rather than voting or silently choosing a
row; this also applies transitively across encodings and source identities.

Inspect independent-group counts, not raw burst/photo counts, for imbalance.
The same bytes can occur in multiple inventory rows; these stay in one split
and count as one positive group. A future training sampler must deduplicate
identical bytes or group-weight them rather than overweight repeated exports.
The audit does not judge annotation correctness/completeness from tag counts.
Review a stratified sample manually, including untagged photos and negatives.
All-unknown rows are retained for auditing, not labeled training examples.

Group assignment hashes the seed and sorted member IDs. With no source holdout,
the approximate group ratios are train/validation/calibration/test =
70/10/10/10; they are not stratified or guaranteed exact. Prior-reviewed groups
are forced into training. For an independent source holdout, add repeatable
`--test-source new-library-b`: any connected group touching that source is test;
other groups split 80/10/10 across train/validation/calibration. A held-out group
touching prior evaluation is a hard error. The test-source name must exist.
Cross-source connected groups are moved wholly to test, never fragmented.

Reordering input rows does not change assignments; adding/removing members of a
group can. Freeze and archive the entire manifest, vocabulary, input and seed
before training. Do not retry seeds to improve test results or use a new audit
to silently redefine a frozen evaluation cohort. Empty splits, unseen labels
and sparse per-tag groups require manual correction and additional data.
`trainingReady` and `productionApproved` are **always false**: this audit is not
an automatic quality, license, independence or release gate.

Output must be a new directory outside the photo root; previous outputs are
never overwritten. **Both files are private**, even the aggregate audit (source
names and rare label counts can be identifying). Keep inventories, photos,
checkpoints and reports outside the checkout; never upload them in a PR.

## Reproducible Open Images V7 workflow

The public-data tools use only the standard library and keep all downloaded
data outside the checkout. `open-images-mapping.csv` explicitly covers all 100
pilot concepts: 96 have candidate MID mappings and four are deferred (`playing
music`, `dancing`, `group of people`, `celebration`). These are conservative
source-mapping proposals, not proof of source-annotation accuracy. Each MID is
checked against its exact official class name. No fuzzy matching or mapping
from object presence to an activity is permitted.

`negativePolicy=all` transfers a positive if any listed MID is verified present,
but transfers a negative **only when every listed MID is explicitly absent**.
This protects union concepts such as `sunset or sunrise`. `positive-only`
transfers no negatives: a narrower class such as Laptop cannot establish that
all computers are absent. Ambiguous activity/event/food mappings require a
small visual quality sample before inclusion in the trained head; they are not
automatically cleared because a matching class name exists.

### Acquire the official metadata, not the complete image dataset

```powershell
python tools\PhotoLibrarian.ModelTraining\fetch_open_images.py `
  --output D:\Training\OpenImages\metadata `
  --sources classes validation-labels validation-metadata train-labels train-metadata
```

This explicit command fetches about **3.42 GB of metadata**, mostly the V7
training image-label CSV, **not millions of photos**. The default without
`--sources` fetches only classes and validation metadata/labels (~44 MB).
Each transfer checks Content-Length, the server's MD5 and a checked-in reviewed
SHA-256; a source receipt records URL, generation, ETag and modification date.
Cached data is rehashed before reuse. Failed/incomplete downloads are retained
as `.downloading` files and never published as valid sources: inspect the named
file before removing it to retry. No automatic overwrite or silent source
revision change occurs.

V7's full human image-level labels are used, not only the 600 boxable labels.
The official training **image provenance** CSV used here covers the 1.7M
boxable-image subset, not all ~9M images. IDs lacking corresponding license and
attribution metadata are excluded and counted; they are not silently treated
as licensed. Source coverage and selected coverage must be reported separately.

### Measure coverage and choose a capped subset

```powershell
$metadata = 'D:\Training\OpenImages\metadata'
python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$metadata\oidv7-train-annotations-human-imagelabels.csv" `
  --metadata "$metadata\train-images-boxable-with-rotation.csv" `
  --classes "$metadata\oidv7-class-descriptions.csv" `
  --split train --per-state 100 --max-images 5000 `
  --output D:\Training\OpenImages\selection-train-v1

python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$metadata\oidv7-val-annotations-human-imagelabels.csv" `
  --metadata "$metadata\validation-images-with-rotation.csv" `
  --classes "$metadata\oidv7-class-descriptions.csv" `
  --split validation --per-state 25 --max-images 1000 `
  --output D:\Training\OpenImages\selection-validation-v1
```

The selector makes two streaming passes over annotations with bounded candidate
memory. First it retains the lowest seeded SHA-256 ranks per source MID/state;
then it joins **all** mapped human annotations for those image IDs. Verified
confidence `1`/`1.0` and `0`/`0.0` are accepted; machine/other sources never
transfer ground truth. Contradictory verified states on a selected image/MID
fail explicitly. Missing target labels remain unknown. Duplicate rows cannot
fill a candidate reservoir with repeated copies of the same ID.

Then it filters to metadata-listed CC BY 2.0 images with author/landing-page
attribution and applies the global image cap deterministically. Metadata
filtering after candidate sampling may reduce coverage; the tool reports that
shortfall instead of silently downloading more images. Per-state is a
**candidate cap**, not a guarantee of 100 independent positives/negatives.
`coverage.json`/`coverage.csv` distinguish source annotation-row counts from
actual selected positive/negative/unknown image counts and report rejections.
All input file hashes, source split, seed and limits are recorded.

`selection.jsonl` retains source labels, original split and image provenance
including original MD5 and rotation metadata; `image-list.txt` is compatible
with the upstream subset downloader. Selection files contain public metadata
but should remain local rather than become repository fixtures.
No original test annotations or images are fetched. Original validation photos
must **never** become training photos; validation/calibration partitioning and
cross-split duplicate exclusion still need to be frozen before model fitting.

### Download only the explicit selection

```powershell
python tools\PhotoLibrarian.ModelTraining\download_open_images.py `
  --selection D:\Training\OpenImages\selection-train-v1\selection.jsonl `
  --output D:\Training\OpenImages\download-train-smoke-v1 --limit 128
```

The initial 128-image cap is a download/decode smoke sample, not a training run.
Use a new output and `--limit 5000` for the complete capped training selection
after reviewing coverage. There is no uncapped download mode (maximum 10,000,
maximum 8 workers and 20 MiB per image). Downloads use the fixed official
Open Images S3 mirror, not arbitrary URLs from metadata, verify byte counts and
JPEG framing, and retain each image's URL/ETag/size/SHA-256 in a sidecar.
These checks do not replace a full image decoder. The original-image MD5 may
differ from mirror/resized bytes and is retained as provenance, not misused as
a checksum of the mirror JPEG.

Rerunning the identical selection/limit verifies and reuses downloaded files;
different selections/limits need a different directory. Download failures
produce errors, never placeholder photos or a success manifest. A complete
`download-manifest.jsonl` is published only after every requested image succeeds.
The selection, sidecars and manifest all retain licensing and unresolved review
status; downloading does not confer rights or approve a shipping model.

### Expand rare concepts without losing them to the global cap

The initial rank-truncated selections below are reproducible historical
measurements, not the preferred training cohort. Add **both** `--eligible-first`
and `--balanced` for an expanded cohort. The first pass loads eligible metadata
IDs into memory (roughly 1.7M short IDs for training), so unlicensed or
metadata-unavailable images cannot consume per-MID sampling slots. The final
cap round-robins target/state buckets, rarest first, instead of dropping rare
concepts through a single global rank cutoff. Source annotation-row counts
remain over all verified source rows, not just provenance-eligible rows.

Use new output names and retain the original seed:

```powershell
python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$metadata\oidv7-train-annotations-human-imagelabels.csv" `
  --metadata "$metadata\train-images-boxable-with-rotation.csv" `
  --classes "$metadata\oidv7-class-descriptions.csv" `
  --split train --per-state 200 --max-images 10000 --eligible-first --balanced `
  --output D:\Training\OpenImages\selection-train-balanced-v1

python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$metadata\oidv7-val-annotations-human-imagelabels.csv" `
  --metadata "$metadata\validation-images-with-rotation.csv" `
  --classes "$metadata\oidv7-class-descriptions.csv" `
  --split validation --per-state 60 --max-images 3000 --eligible-first --balanced `
  --output D:\Training\OpenImages\selection-validation-balanced-v1
```

These runs select 10,000 training and 3,000 development-validation IDs.
**94 concepts** have at least 50 training positives and ten validation positives
(up from 32 in the first capped cohorts). `gardening` has no eligible validation
positive and `picnic` has eight. Requiring known negatives as well, **69
all-policy concepts** have at least 50 training positives/negatives and ten
validation positives/negatives *before* decode, overlap and orientation
exclusions. These are image counts, not certified independent event counts.
Positive-only mappings are intentionally ineligible for the first supervised
head rather than training a classifier without negative evidence.

Download these explicit selections with `--limit 10000` and `--limit 3000`,
respectively, into new directories. JPEG framing permits extra MPO frames or
trailing data after the first EOI marker; full research decoding uses the first
frame. The downloader still checks byte count, SOI and EOI presence, and the
training gate fully decodes each file before accepting it.

### Observed initial coverage (2026-10-22)

Using the pinned files, default seed and the train/validation commands above:

| Measurement | Training selection | Validation selection |
| --- | ---: | ---: |
| Unique candidate IDs before provenance/cap filtering | 14,074 | 3,449 |
| Candidate IDs missing image metadata | 6,509 | 0 |
| Candidate IDs without transferable known target labels | 31 | 21 |
| Eligible IDs excluded by the global cap | 2,534 | 2,428 |
| Final selected images | 5,000 | 1,000 |
| Concepts with at least one selected positive | 96 | 92 |
| Concepts with at least one transferred negative | 76 | 67 |

No metadata-matched candidate was rejected for license/attribution in this run.
The four deliberately deferred concepts have no positives in either selection.
`rice`, `gardening`, `rain` and `rainbow` additionally have no positives in the
capped validation selection; this is **not** evidence they are absent from the
complete dataset. Initial selected positive counts range from 2 to 2,034 in
training and 1 to 185 in validation (among concepts with positives).

Only 52 concepts have at least 50 selected training-positive images; 41 have at
least ten validation-positive images, and only 32 meet both illustrative
thresholds. These are **image counts, not independent event counts**. Thus this
first capped sample demonstrates feasibility and pinpoints imbalance, not
readiness to train a validated 100-label model. Expand targeted source sampling
or defer sparse concepts rather than request wholesale personal relabeling.
There was no cross-split source-ID or nonempty original-MD5 overlap in the
selected cohorts; perceptual/edited-copy/event overlap is not ruled out.

An actual bounded smoke run downloaded **128 training and 32 validation JPEGs**
(52,726,515 bytes total). All 160 passed SHA-256 verification and full Pillow
decode; each cohort had distinct byte hashes. Repeating selection reproduced
the same IDs, labels and coverage, and repeating the download reused the
hash-verified cache. These are data-pipeline checks, **not training or quality
results**. No original test data or private regression photos were downloaded,
uploaded, modified or added to training.

Before training: decode files, check SHA-256 and perceptual/near-duplicate/event
overlap (including against the existing private regression set, locally), honor
source splits, review a small stratified sample of mapping quality, resolve
license/weight terms and freeze the final head. None requires asking the
maintainer to manually annotate hundreds of photos again. The existing local
audit's reviewed grouping contract must not be bypassed by auto-generating
purportedly reviewed singleton groups for public images.

### Source semantics and provenance

Use the official [V7 download/format documentation][oi-download] and
[license/statistics documentation][oi-facts]. Select image IDs for reviewed pilot
concepts first, then use the official subset downloader with an explicit cap;
do not fetch all 9M images.

Prepare a reviewed MID-to-pilot mapping with concept scope and version, rather
than matching class descriptions as strings. Not every scene, action or event
has a reliable matching Open Images class; unsupported concepts need local
reviewed examples or exclusion from the first trained head. Do not treat an
object class such as `Bicycle` as evidence of `cycling`.

Prefer the **human-verified image-label files**. For `verification` and
`crowdsource-verification`, map confidence 1 to positive and 0 to negative.
Missing rows are unknown. Machine-generated fractional scores are not verified
negatives or ground truth; do not round them into labels. Do not convert point
labels or missing boxes into image-level absence. If multiple source classes
map to one broader concept, one positive can support presence, but one negative
subclass cannot establish absence of that broader concept. Review conflicts
and mapping semantics before exporting `labels`.

Record original source split, image ID, author, landing URL, license URL,
annotation file/version/hash, annotation source, mapping revision and image byte
hash locally. Preserve attribution text and any notices; verify each image's
license, since the dataset explicitly disclaims guaranteeing image rights.
Retain original Open Images test data as test (a separate source identifier can
be used with `--test-source`), not training; Open Images' original split does not
alone establish independence from pretrained weights or other libraries.

## Historical ImageNet-initialized research and license review

Source review: 2026-10-23, including the official ImageNet agreement,
Open Images V7 license notice, TorchVision pretrained-model disclaimer and
MobileNetV3 weight metadata/source. URLs and terms can change; each actual
training run must pin package versions, source commits, checkpoint
URLs/revisions and full SHA-256.
Earlier research used the official TorchVision MobileNetV3-Large V2 checkpoint
(`mobilenet_v3_large-5c1a4163.pth`, SHA-256
`5c1a416349c4cf298f2a6a5e2600ed0ee55e604713578f5e74e6bc8bcaef7997`,
22,132,113 bytes), verified in full **before** weights-only deserialization.
**Release disposition: no research-derived model is cleared for product use,
hosting or redistribution. This is a conservative project gate, not a legal
opinion that every such use is prohibited.** ImageNet's [official about page][imagenet-about] says ImageNet does not own the
images' copyrights and provides access for non-commercial research and
education; its [access agreement][imagenet-agreement] limits use of the
database to those purposes, disclaims warranties, and binds an employed
researcher's for-profit employer as well. That is not a blanket CC BY 3.0
license for ImageNet images. A CC BY grant permits commercial reuse only for
the material the identified licensor actually licensed; an attribution or
taxonomy license alone does not establish rights to all underlying photographs
or TorchVision checkpoints. TorchVision's current [pretrained-model
documentation][tv-models] explicitly warns that models may have dataset-derived
terms and instructs users to determine permission for their use case. The
MobileNetV3 V2 [upstream weight entry and source][mobilenet-source] identify
the checkpoint and metrics but do **not** declare a separate weight license.
The TorchVision repository's BSD-3-Clause license covers its code; it is not
evidence by itself that this ImageNet-derived checkpoint or a fine-tune is
cleared for product distribution. Any change requires documented rights review
for the intended use/distribution, not an inference from the code license.

Open Images V7's [official license notice][oi-facts] says Google annotations
are CC BY 4.0 and that images are *listed* as CC BY 2.0, but explicitly
disclaims any representation or warranty about an individual image's license
and directs users to verify each image. The [CC BY 2.0 deed][cc-by-20] permits
commercial sharing and adaptation subject to attribution and other terms; the
[CC BY 4.0 deed][cc-by-40] likewise requires attribution for annotations. Both
deeds warn that other rights (including privacy/publicity) can still apply.
The project's metadata
and retained author/landing-page/attribution fields are provenance aids, not
proof that the image's actual licensor authorized the specific use. The
annotation license does not replace image rights. Whether model training,
derived parameters, attribution and distribution trigger obligations for the
selected images needs case-specific legal review.

The runs verified exact checkpoint hashes and preserved training/download
provenance, but did not individually re-confirm present-day image ownership,
author authority, publicity/privacy rights, or resolve derived-model terms.
Private regression photos are not distributed. No model binaries, checkpoints
or photos are committed or hosted. Until those questions are resolved, use
these artifacts only for local research under applicable agreements; do not
ship the model, add it to model hosting, or label it commercially cleared.

| Component | Evidence and proposal | License / release condition |
| --- | --- | --- |
| MobileNetV3-Large | First candidate: [TorchVision V2][mobilenet] lists 5,483,032 parameters, 0.22 GFLOPS, 21.1 MB checkpoint; exact V2 weight URL/metadata in upstream [model source][mobilenet-source] | TorchVision code is BSD-3-Clause; no distinct V2 checkpoint license is declared in its weight metadata; [TorchVision requires case-specific permission review][tv-models]. Not cleared for product use/redistribution |
| EfficientNet-B0 | Challenger: [TorchVision V1][efficientnet] lists 5,288,548 parameters, 0.39 GFLOPS, 20.5 MB checkpoint; 224 crop after resize 256 with bicubic interpolation | Same separate code/weight/dataset review; no assumption that a code license grants dataset rights |
| Private library | Only consented, locally exported records; personal tags require explicit mapping | Owner permission/annotation rights must be recorded; no photo/identifier redistribution by default |
| Open Images V7 | Human-verified labels and bounded class-specific subsets | Annotations CC BY 4.0; images listed CC BY 2.0, with explicit no-warranty/verify-each-image warning. Preserve attribution/provenance; resolve image and derived-model rights before product use |
| RAM++, SigLIP 2, TinyCLIP | Existing local reference models; do not reuse their human-reviewed cohort as final test | See [existing model asset provenance](../../MODEL-ASSETS.md) and pinned benchmark preparation scripts; record each exact revision/notice in a run |
| Apple MobileCLIP | Excluded from this product-training proposal | [Model terms][mobileclip] exclude product development; MIT code is not permission for the weights |

Upstream checkpoint size/parameter counts are **not** measurements of our
multi-label ONNX. Prefer MobileNetV3's lower published compute as a starting
hypothesis, not proof of CPU/DirectML speed or quality. Initial export targets
remain 15-30 MB FP32 or roughly 5-15 MB INT8; measure each actual pilot head.
Neither ImageNet top-1 accuracy nor an upstream classifier is a home-photo
multi-label quality result.

## Local research training and export

`requirements-training.txt` pins a separate **Python 3.12** research environment.
The public-data preparation scripts remain dependency-free. Use an external
virtual environment; the application does not acquire Python or these packages.
CPU is supported. For a compatible CUDA 12.8 GPU, preinstall the matching
official GPU wheels before the remaining requirements:

```powershell
py -3.12 -m venv D:\Training\OpenImages\venv-training
$python = 'D:\Training\OpenImages\venv-training\Scripts\python.exe'
& $python -m pip install torch==2.9.1 torchvision==0.24.1 `
  --index-url https://download.pytorch.org/whl/cu128
& $python -m pip install -r tools\PhotoLibrarian.ModelTraining\requirements-training.txt
```

For CPU only, omit the GPU preinstallation and install the requirements.
The exact resolved versions including CUDA suffix, device, seed, optimizer,
preprocessing, manifests and initialization identities are recorded in `run.json`.
Deterministic Torch algorithms are required; explicitly requested CUDA must be
available or the run fails rather than falling back silently. CUDA convolution
and matrix-multiplication TF32 are explicitly disabled to preserve FP32
cross-runtime parity. Resolved environment versions and source-code hashes are
recorded, not just the dependency manifest.

```powershell
$root = 'D:\Temp\PhotoLibrarian-OpenImages'
& $python tools\PhotoLibrarian.ModelTraining\train_pilot.py `
  --train "$root\download-train-balanced-v1\download-manifest.jsonl" `
  --additional-train "$root\download-train-expansion-v1\download-manifest.jsonl" `
  --validation "$root\download-authors-first-v1\download-manifest.jsonl" `
  --exclude-images "$root\download-evaluation-v1\download-manifest.jsonl" `
  --exclude-images "$root\download-expanded-evaluation-v1\download-manifest.jsonl" `
  --author-reservation "$root\author-reservation-v1\reservation.json" `
  --regression-images D:\Temp\Pictures\Diverse `
  --output "$root\run-authors-first-scratch-v1" `
  --research-only --device cuda --initialization random --scratch-epochs 20
```

This is **local exploratory research**, not product-weight/license approval.
It never uploads data or publishes weights. Output/checkpoint caches must be
outside the checkout. Each new run needs a new directory; there is no implicit
resume that changes the frozen data/head or overwrites previous evidence.

Before training, `pilot_data.py` verifies every downloaded byte hash and fully
decodes accepted images. It uses Open Images' recorded counterclockwise rotation
as authoritative, without applying EXIF a second time; unknown rotations are
explicitly quarantined, not guessed. Hierarchy-conflicting source annotations
are quarantined with their public image IDs and contradictory label pairs.
The private regression folder is optional and read-only: only perceptual hashes
and the aggregate photo count are used; private images/filenames do not enter
the exported research card or supervised training.

Training drops source-ID/original-MD5/byte duplicates across validation,
validation authors by exact author-profile URL, and approximate near matches
(64-bit grayscale difference hash, Hamming distance <=6). Train/validation
near matches to the supplied private regression set are also excluded, as are
duplicate bytes within each cohort. `data-audit.json` records every quarantine
reason and the post-filter per-tag counts. A difference hash can miss edits and
can overexclude visually simple unrelated images; this conservative screen
**does not certify event independence**. No rows receive a fabricated
`groupReviewComplete=true`.

The ordered head admits only `negativePolicy=all` concepts with at least
**50 known positives and 50 known negatives in training**, and at least
**ten of each in development validation**, after exclusions. These are
provisional pilot evidence gates over images, not statistically justified
minimum independent events. The other approved vocabulary concepts remain
unsupported/abstaining, not secretly negative. Positive-only activity/ambiguous
mappings are excluded until their visual semantics/negative evidence are
resolved. Unsupported concepts are listed in the audit.

The current default creates a randomly initialized MobileNetV3-Large and trains
the complete network and BatchNorm running statistics for 20 epochs with AdamW
(learning rate 0.001, weight decay 0.0001) and cosine decay to 0.00001.
Legacy ImageNet initialization remains opt-in solely to reproduce archived
experiments; it verifies the pinned checkpoint hash and is not used by the
current protocol. Both paths replace the final classifier with independent
logits. Loss is unweighted BCE **only on known entries**; unknown -1 entries
have no loss or gradient, all-unknown examples are excluded, and all-unknown
batches perform no optimizer update.

Inputs use the complete rotated RGB image resized directly to 224x224 with PIL
bilinear interpolation. Scratch training uses channel mean/std `[0.5, 0.5, 0.5]`;
historical ImageNet runs use the documented ImageNet statistics. Horizontal
flips are the sole augmentation. This deliberately differs from an upstream
center-crop recipe so cropping cannot silently remove an annotated positive
object.
`baseline.json`, `history.json`, `best-validation.json` and
`validation-scores.npz` retain actual development loss and known-entry metrics.
Threshold 0.5 is **uncalibrated**, and undefined precision is null, not perfect.
The best checkpoint is chosen by masked development loss, never private
regression scores or an untouched test. `completion.json` is written only after
both stages finish and fingerprints the checkpoint and selected validation
score arrays.

```powershell
& $python tools\PhotoLibrarian.ModelTraining\export_pilot.py `
  --run D:\Training\OpenImages\run-balanced-pilot-v1 `
  --output D:\Training\OpenImages\export-balanced-pilot-v1 --samples 16 --int8
```

Export loads a matching completed checkpoint with weights-only deserialization.
FP32 ONNX has fixed `image` float32 `[1,3,224,224]` input and `scores` float32
`[1,labelCount]` independent sigmoid output. ONNX checker and actual CPU
inference are required; parity uses three synthetic tensors plus up to 16
development photos, plus the entire supervised development cohort against the
selected PyTorch score arrays. Both require predeclared maximum absolute error
<=0.0001 and zero 0.5 decision flips. The pinned legacy TorchScript exporter is explicitly selected
(`dynamo=False`), so this small fixed graph does not need extra exporter
packages; its deprecation warning is not hidden.

Optional INT8 uses static QDQ with per-channel signed weights and unsigned
activations, calibrated on at most 128 **original training** images, never
validation/private/test. It records score errors and threshold flips against
FP32 on the same parity inputs, without claiming those errors are acceptable
for production. Full development metrics and known-entry flips are also
recorded; independently calibrated thresholds and quality approval remain
required. The preprocessed ONNX is an intermediate diagnostic,
not an additional runtime dependency.

`export-report.json` records runtime bytes/hashes, score contract, preprocessing,
FP32 parity, optional INT8 diagnostics, ORT CPU load time and a warm 30-iteration
single-tensor inference p50/p95 diagnostic. It explicitly excludes decode and
preprocessing and does not stand in for app end-to-end latency, working memory,
DirectML fallback verification or a quality benchmark. No runtime profile is
automatically created or approved.

## Measured research pilot and model card

The local `run-balanced-pilot-v2` / `export-balanced-pilot-v2` artifacts were
actually trained/exported with the commands above, using new output directories.
This is an object/scene retrieval feasibility baseline, **not a replacement
default, exhaustive photo description, identity/sensitive inference model, or
redistribution-approved bundle**. The full 100-concept proposal remains the
target vocabulary; only **44 concepts** passed the declared post-filter
positive/negative gates. The other 56 abstain. The head order and per-concept
counts/metrics are published in [`pilot-results-v2.csv`](pilot-results-v2.csv).

Public acquisition downloaded and hash-verified 10,000 original training and
3,000 original validation images: **4,221,291,827 bytes**. The audit excluded
1,800 unknown source rotations, 115 hierarchy conflicts, 2,272 validation-author
overlaps, 88 approximate validation near-matches, and 25 approximate private
regression near-matches. It checked the existing 156 private regression photos
read-only; none entered training. After exclusions, 6,107 training / 2,593
validation images remained; 5,936 / 2,390 had supervision for the gated head.
Counts are images, not independently reviewed events, and perceptual hashes can
overexclude or miss matches.

Training used Python 3.12.0, Torch 2.9.1+cu128, TorchVision 0.24.1+cu128,
CUDA 12.8 and an RTX 5080 on Windows 11. The documented seed-66 recipe ran
three head epochs and two fine-tuning epochs in **147.48 seconds** excluding
the data audit and baseline evaluation. Masked validation BCE improved from
**0.69596 to 0.59450**. The first fine-tuning epoch was best; the second worsened
to 0.63376 and was **not** selected. No claim of convergence or optimal recipe
is made.

All metrics below use the same **2,390 development photos / 5,450 known entries**
at the **uncalibrated 0.5 cutoff**. Another 99,710 entries remain unknown and
are ignored, not treated as negative. Validation was used for checkpoint and
quantization development, so these are **not untouched-test accuracy or
exhaustive recall**, and cannot be compared to prior RAM++ human ratings.

| Measurement | FP32 | Static INT8 diagnostic |
| --- | ---: | ---: |
| ONNX bytes | 17,024,708 | 4,768,694 |
| Known-entry micro precision | 69.34% | 69.15% |
| Known-entry micro recall | 85.51% | 75.08% |
| Macro precision (44 defined labels) | 69.30% | 68.39% |
| Macro recall (44 defined labels) | 77.20% | 56.02% |

The FP32 file meets the initial 15-30 MB engineering target. It passed ONNX
checking and PyTorch parity across all **105,160 development scores**, maximum
absolute error **0.000003160**, with **zero 0.5 decision flips**. Three synthetic
and 16-photo checks also passed. An initial exploratory run exposed CUDA's
default TF32 convolution drift (up to 0.005823 against CPU ONNX); the published
v2 run disables TF32 and enforces full-cohort parity rather than relying on a
small sample.

**INT8 is rejected as a usable pilot replacement.** Calibration used 128
original training images only. Maximum full-development score error versus
FP32 was **0.939996**, with **1,049 known-entry decision flips** and substantial
recall loss. A separate Conv/Gemm/MatMul-only quantization experiment did not
fix this (1,109 flips; 73.96% micro recall). Keep FP32 for further research;
revisit quantization/calibration before considering INT8. No tolerances or
label gates were relaxed to manufacture acceptance.

FP32 ORT 1.23.2 CPU diagnostic: four intra-op threads, 5 warmups / 30 iterations,
**2.79 ms p50 / 2.98 ms p95**, **27.74 ms session load**. This is one input tensor
on this host, excludes decoding/preprocessing, and is not app end-to-end latency
or a CPU-versus-GPU benchmark. Subsequent real-photo CPU/DirectML execution and
host-memory diagnostics are below; a matched RAM++/SigLIP 2/TinyCLIP quality
comparison remains outstanding.

Artifact identities (SHA-256; binaries and photo-level records stay local):

| Artifact | SHA-256 |
| --- | --- |
| Post-filter dataset fingerprint | `7d1e5cc5bf1c78bce336f1f27d7e2a16f154c1133e6c376fdbc277ff553b6d0c` |
| Selected PyTorch checkpoint | `02b1ba18d9d2fb432cb2427797d50d3ab97817fc0abce8939d2a74229461c9ac` |
| FP32 ONNX | `3b7d32bc041442ae84ced7a9273d64323ae0805acb3993fb3339aa9568b34938` |
| INT8 diagnostic ONNX | `b5251dc701654f3e44c7f59108cf47990e3c86f02bf67f46c101849029698cce` |

Sources/rights remain as documented above: Open Images annotation CC BY 4.0,
image-level CC BY 2.0 metadata with retained attribution but no guarantee of
current individual rights, TorchVision BSD-3-Clause code, and separately
unresolved ImageNet-derived initialization rights. No product/redistribution
clearance or model hosting approval is implied. Sparse reviewed labels, source
selection, geographic/cultural representation, small objects and visually
similar classes remain important limitations; for example FP32 camera
precision was only 41.67% on 18 known positives and 18 negatives. The additional
calibration/test below does not clear licensing or matched-model evaluation.
Further progress does **not require wholesale relabeling of the maintainer's
collection**.

## Additional evaluation and remaining release gates

### Frozen additional public evaluation protocol

Use a **separate** evaluation environment: `onnxruntime-directml` and
`onnxruntime` share a Python module and must not be installed over each other.
The DirectML wheel also exposes the CPU provider, so both provider measurements
use the same ORT **1.23.0** build (no 1.23.2 DirectML wheel exists for this
interpreter). Evaluation preprocessing uses only Pillow/numpy; a bit-exact
test against the training implementation protects the input contract.

```powershell
py -3.12 -m venv D:\Training\OpenImages\venv-evaluation
$evalPython = 'D:\Training\OpenImages\venv-evaluation\Scripts\python.exe'
& $evalPython -m pip install -r tools\PhotoLibrarian.ModelTraining\requirements-evaluation.txt
```

The next evaluation uses 4,000 additional Open Images original-validation
images, selected with seed `open-images-independent-evaluation-v1` and
`--exclude-selection` for **both** prior 10,000-training / 3,000-development
selections. Source IDs, original MD5 and author-profile URLs are excluded
before sampling, so changing the seed cannot accidentally reuse pilot authors.
`prepare_evaluation.py` additionally removes approximate near matches to all
accepted training/development images and the 156 private regression photos.
It quarantines missing orientation/hierarchy conflicts and unions author,
original-MD5, byte-hash and dHash-neighbor constraints **transitively** before
assigning components 50:50 by deterministic hash. No score is used to split.

The frozen cohort has **1,745 calibration images / 1,535 approximate groups**
and **1,659 test images / 1,457 approximate groups**. Quarantine counts: 513
unknown source rotations, 74 prior-development near-matches, four hierarchy
conflicts and five private-regression near-matches. This is fresh relative to
the fitted pilot, not a guarantee of independence from ImageNet pretraining or
of manually verified event groups.

The **provisional research policy**, fixed before test scoring, targets 90%
known-entry calibration precision, requires at least 20 accepted known
entries, descriptive 95% Wilson lower bound >=75%, ten known positives and
ten known negatives, and five distinct approximate groups for each state.
Fit per-tag thresholds to maximize known true positives, then accepted count,
then threshold; disable unsupported labels rather than use a global fallback.
The binomial interval is descriptive, **not** a guarantee under group/selection
dependence. These choices are not production acceptance criteria. Nine labels
qualify on calibration: animal, dog, bird, flower, tree, car, table, food, person.
The remaining 35 model labels abstain.

Two test comparisons are fixed in advance: the uncalibrated 0.5 baseline and
the **unchanged calibration-only thresholds**. No model, mapping, policy or
threshold is fitted on test or private regression judgments.

| Frozen artifact, before first test inference | SHA-256 |
| --- | --- |
| `evaluation-protocol-v1\protocol.json` | `22582fa22eb92243d5f88a193646e9bab2752f26456ef76def76cfc3accf59fa` |
| `calibration-public-v1\calibration.json` | `f6801502ac6700e1389a50bca5ec22f347a8914e024e1d34adb056b5698085e4` |

The files and image manifests remain outside git. Reproduce with new external
directories; `fit` never reads the test manifest or test photos:

```powershell
& $python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations D:\Training\OpenImages\metadata\oidv7-val-annotations-human-imagelabels.csv `
  --metadata D:\Training\OpenImages\metadata\validation-images-with-rotation.csv `
  --classes D:\Training\OpenImages\metadata\oidv7-class-descriptions.csv `
  --split validation --per-state 100 --max-images 4000 --eligible-first --balanced `
  --seed open-images-independent-evaluation-v1 `
  --exclude-selection D:\Training\OpenImages\selection-train-balanced-v1\selection.jsonl `
  --exclude-selection D:\Training\OpenImages\selection-validation-balanced-v1\selection.jsonl `
  --output D:\Training\OpenImages\selection-evaluation-v1
& $python tools\PhotoLibrarian.ModelTraining\download_open_images.py `
  --selection D:\Training\OpenImages\selection-evaluation-v1\selection.jsonl `
  --output D:\Training\OpenImages\download-evaluation-v1 --limit 4000 --workers 8
& $evalPython tools\PhotoLibrarian.ModelTraining\prepare_evaluation.py `
  --download D:\Training\OpenImages\download-evaluation-v1\download-manifest.jsonl `
  --prior-run D:\Training\OpenImages\run-balanced-pilot-v2 `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --regression-images D:\Temp\Pictures\Diverse `
  --output D:\Training\OpenImages\evaluation-protocol-v1
& $evalPython tools\PhotoLibrarian.ModelTraining\calibrate_pilot.py fit `
  --evaluation D:\Training\OpenImages\evaluation-protocol-v1 `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --output D:\Training\OpenImages\calibration-public-v1
# Freeze/review calibration.json before this first test run. Do not retune on test.
& $evalPython tools\PhotoLibrarian.ModelTraining\calibrate_pilot.py test `
  --evaluation D:\Training\OpenImages\evaluation-protocol-v1 `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --calibration D:\Training\OpenImages\calibration-public-v1\calibration.json `
  --output D:\Training\OpenImages\test-public-v1
```

### First held-out research test: precision improves, breadth is not ready

The policy, code and artifact hashes above were committed in **`52d7aac` before
the first test inference**. The frozen thresholds were then evaluated once,
without retuning, on **1,659 held-out photos / 3,366 known label entries**.
The public download added 1,255,728,923 bytes. Full per-tag results and exact
frozen thresholds are in
[`pilot-test-results-v1.csv`](pilot-test-results-v1.csv); blank thresholds mean
disabled/abstaining, not zero.

| Held-out known-entry measurement | Fixed 0.5 baseline | Calibration-only thresholds |
| --- | ---: | ---: |
| Enabled labels | 44 | 9 |
| Micro precision | 71.97% | 88.65% |
| Micro recall, including disabled labels' positives | 84.80% | 37.18% |
| True positives / false positives / false negatives | 1,674 / 652 / 300 | 734 / 94 / 1,240 |
| Photos with any emitted tag | 1,659 | 1,128 |
| Total emitted tags / unreviewed emitted tags | 21,058 / 18,732 | 1,846 / 1,018 |

**Do not promote this pilot as a general-purpose default.** The observed
held-out precision is below the provisional 90% calibration target, and only
nine labels have support for the fitted policy. For example, calibrated
`table` precision fell to 69.57% on 23 accepted known test entries. The
descriptive micro-precision Wilson interval is 86.31-90.63%; group correlation,
balanced selection and incomplete annotation preclude a population guarantee.
More than half of calibrated emitted tags are unreviewed, so the results do
not establish full prediction precision, exhaustive recall, useful distinct
tags per photo or human quality ratings.

The local `test-public-v1\test.json` SHA-256 is
`14e22290b7902a154493306ffa7d9e905267e2674092e8d2866a46a2b1d7bf5d`.
This research test is now **consumed**: changing the model, mapping, sampling
or policy based on these findings requires a new untouched test for any
subsequent generalization claim. Do not repeatedly optimize this score.

### Real-photo CPU/DirectML and memory diagnostics

`benchmark_pilot.py` verifies the export identity/contract, fully decodes local
photos and uses bit-exact pilot preprocessing. `--provider` is mandatory;
unavailable/failed providers are errors, and Python's runtime retry fallback is
disabled. DirectML's graph-level CPU fallback is permitted only for the first
diagnostic, recorded from an actual ORT node profile, then explicitly disabled
in a separate strict session. A provider-list entry alone is not execution
evidence.

Measured sequentially in separate processes on the same 156 Diverse files:
Python 3.12 / ORT DirectML 1.23.0 / four intra-op CPU threads / five warmups,
AMD Ryzen 9 9950X3D, Windows 11, RTX 5080 driver **32.0.16.1088**.
DirectML device index **0** was explicit; a separate DXGI `EnumAdapters1`
inspection identified it as the RTX 5080 (index 1 is the integrated AMD GPU).
The report also records the full Windows CIM CPU/video-driver inventory.

| Measurement | CPU | DirectML adapter 0 |
| --- | ---: | ---: |
| Session construction | 33.36 ms | 375.46 ms |
| First inference, before remaining warmups | 3.02 ms | 2.69 ms |
| Warm inference p50 / p95 | 3.14 / 3.94 ms | 0.82 / 0.97 ms |
| Decode + preprocess + inference p50 / p95 | 35.93 / 181.57 ms | 35.02 / 183.98 ms |
| Sampled process peak host RSS | 551.99 MiB | 752.02 MiB |
| Host RSS increase over pre-session baseline | 496.94 MiB | 697.25 MiB |

DirectML executed one **fused GPU partition**, with **no CPU execution nodes**
in the profiled graph; the strict CPU-fallback-disabled session also succeeded.
CPU profiling recorded 188 optimized execution nodes. Across all **6,864 private
regression scores**, CPU/DirectML max absolute difference was **0.000001014**
with **zero 0.5 cutoff flips**. This does not certify every GPU/driver or all
possible calibrated-threshold behavior.

The larger DirectML startup/RSS matters: faster inference barely changes total
photo latency when decoding dominates. Memory is the entire Python/decoder/
runtime **host process RSS**, sampled every 5 ms; it is neither isolated model
memory nor GPU VRAM, and brief peaks can be missed. Timings include decoding,
orientation and normalization but exclude hashing/report writing and do not
measure application scheduling, metadata writes or UI latency. File-cache
state and unrelated host workloads are uncontrolled. These are diagnostics,
not a shipping performance promise.

```powershell
& $evalPython tools\PhotoLibrarian.ModelTraining\benchmark_pilot.py `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --images D:\Temp\Pictures\Diverse --provider cpu `
  --output D:\Training\OpenImages\benchmark-diverse-cpu-v4
& $evalPython tools\PhotoLibrarian.ModelTraining\benchmark_pilot.py `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --images D:\Temp\Pictures\Diverse --provider directml --device-id 0 `
  --output D:\Training\OpenImages\benchmark-diverse-directml-v4
& $evalPython tools\PhotoLibrarian.ModelTraining\evaluate_regression.py `
  --benchmark D:\Training\OpenImages\benchmark-diverse-cpu-v4 `
  --compare-provider D:\Training\OpenImages\benchmark-diverse-directml-v4 `
  --review D:\Temp\DiverseLabeling\Diverse-General-ReviewComplete-V2-20260917\review-items.json `
  --output D:\Training\OpenImages\regression-diverse-v5
```

Local CPU / DirectML `benchmark.json` SHA-256:
`74a5a1e37bce0cc6d9d7e2602272073cef947fed0c36b112db06795435e58ece` /
`255f73b80a7cbb04c0ec15c0b58d24df4d26f0e56498d247e003db5cfd3f9d5f`.

### Existing manual reviews reused, without new labeling

The completed review has 2,472 judgments (1,137 correct, 1,335 incorrect).
Exact canonical-label matching to the gated 44-label head yields **181 known
photo/tag pairs across 21 labels**. Broad aliases and ancestors do not inherit
negative judgments from narrower tags. On those known pairs, the pilot's
unchanged 0.5 cutoff gives **113 TP / 25 FP / 28 FN**, or **81.88% precision /
80.14% recall**. Only 138 of its 1,933 emitted tags have matching reviewed
judgments; the other 1,795 remain unknown, not falsely counted as correct or
incorrect. No threshold was fitted on these photos.

The script also records historical displayed RAM++/TinyCLIP emissions against
the same reviewed pool (respectively 139/39/2 and 36/2/105 TP/FP/FN). These are
**not a fair leaderboard**: review candidates came from those models and their
original vocabularies, cutoffs and top-K limits differ. SigLIP 2 and new
whole-photo quality ratings are absent. Existing review filenames match the
current 156 files, but the review artifact has no historical byte hashes, so
unchanged content since the original review cannot be certified. This remains
development regression evidence, not a new final test.

### Matched existing-model development diagnostic

`prepare_comparison.py` freezes a capped **1,000-photo subset of the original
pilot development validation**, ranked by a fixed seed without using model
scores. It verifies the audited run fingerprint and installed asset identities.
Exact canonical label intersection yields **40 shared labels** across the
pilot, installed RAM++ and TinyCLIP; `rocks`, `sofa`, `clouds` and
`musical instrument` are excluded from this shared pool. No synonyms,
hierarchical positives or ambiguous negatives are invented. Native vocabulary
breadth and shared-label results are separate quantities.

RAM++ and TinyCLIP are read **in place** from the application model cache;
no production file or approval is rewritten. SigLIP 2 uses the existing
`ModelBench\prepare_zero_shot.py` pinned-source workflow in a separate
preparation environment. The installed TinyCLIP **3,632-label ordered
vocabulary** is also used for SigLIP 2, with each model's existing prompt
template. This preparation downloads public model/configuration/tokenizer
assets, never sends photos to a model service, and verifies source identities.
The installed RAM++ graph exposes only a binary mask, not the newer
exporter's continuous probabilities; its embedded upstream decisions are
preserved rather than pretending a 0/1 mask is calibrated confidence.

`compare_pilot.py` then runs the **same photo bytes and authoritative source
rotation**, with model-native preprocessing: pilot bilinear 224/ImageNet,
RAM++ bilinear 384/ImageNet, TinyCLIP bicubic shortest-edge/center-crop with
CLIP normalization, and SigLIP 2 bilinear 224/minus-one-to-one. Every model
uses CPU ORT, four intra-op threads and three untimed warmups. Full native
scores are computed before selecting shared columns; no top-K truncation
or suppression of unreviewed predictions influences known-entry metrics.
TinyCLIP returns cosine, not probability; SigLIP 2 uses its learned sigmoid
scale and bias. Native provisional cutoffs are preserved.

For a second, precision-oriented diagnostic, union author/identity/dHash
constraints before dividing these development photos into **525 fit / 475
check** images. Apply the same previously documented research policy using
fit entries only, then score unchanged per-label thresholds on check.
Unsupported labels abstain. RAM++ cannot participate in this continuous
threshold fitting because its installed graph exposes only binary targets.
Both partitions were already development data for selecting the pilot
checkpoint: **this is not an independent test, a deployment ranking or a
second use of the consumed research test**. Public-data overlap with any
model's pretraining is unknown.

```powershell
$assets = Join-Path $env:LOCALAPPDATA 'PhotoLibrarian\Models\AutoTagging'
& $evalPython tools\PhotoLibrarian.ModelTraining\prepare_comparison.py `
  --run D:\Training\OpenImages\run-balanced-pilot-v2 `
  --bundle D:\Training\OpenImages\export-balanced-pilot-v2 `
  --ram-model "$assets\ram-plus.swin-large-14m\ram_plus_swin_large_14m.onnx" `
  --ram-labels "$assets\ram-plus.swin-large-14m\ram_plus_swin_large_14m_labels.txt" `
  --tinyclip "$assets\tinyclip.vit-40m-32.int8\tinyclip.json" `
  --output D:\Training\OpenImages\matched-development-v2 --limit 1000
py -3.12 -m venv D:\Training\OpenImages\venv-comparison-prepare
$preparePython = 'D:\Training\OpenImages\venv-comparison-prepare\Scripts\python.exe'
& $preparePython -m pip install -r tools\PhotoLibrarian.ModelBench\requirements-prepare.txt
& $preparePython tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Training\OpenImages\comparison-siglip2-v1 --models siglip2 `
  --vocabulary D:\Training\OpenImages\matched-development-v2\zero-shot-vocabulary.json `
  --precision int8 --threads 4
$env:OPENBLAS_NUM_THREADS = '4'
$env:OMP_NUM_THREADS = '4'
& $evalPython tools\PhotoLibrarian.ModelTraining\compare_pilot.py score `
  --comparison D:\Training\OpenImages\matched-development-v2 `
  --siglip2 D:\Training\OpenImages\comparison-siglip2-v1\siglip2.json `
  --output D:\Training\OpenImages\matched-development-scores-v1
& $evalPython tools\PhotoLibrarian.ModelTraining\compare_pilot.py report `
  --comparison D:\Training\OpenImages\matched-development-v2 `
  --scores D:\Training\OpenImages\matched-development-scores-v1 `
  --output D:\Training\OpenImages\matched-development-report-v2
```

#### Measured matched development results

All four runs completed on the same **1,000 photos / 1,998 known entries**
(1,124 positive, 874 negative). The aggregate
[`comparison-results-v1.csv`](comparison-results-v1.csv) records model and
metadata bytes separately, counts, ranking and rounded timing. Detailed local
`comparison.json` retains per-label counts/AP, fitted thresholds, unknown
emissions and provenance. Inference/preparation source commit `18a9c1d`
exactly matches both source hashes recorded by the actual completed runs;
the subsequent report-only extension adds ranking and uncertainty without
rerunning or changing inference.

| Model and unchanged native cutoff | Precision | Recall | Macro AP, 40 shared labels | ONNX bytes | CPU inference p50 / p95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compact FP32 pilot, sigmoid 0.5 | 67.87% | 86.83% | 0.806 | 17,024,708 | 1.78 / 2.36 ms |
| RAM++, embedded binary decisions | 87.52% | 59.88% | 0.766 | 1,863,487,162 | 770.09 / 808.15 ms |
| TinyCLIP INT8, cosine 0.25 | 78.24% | 18.24% | 0.753 | 40,622,561 | 3.54 / 4.14 ms |
| SigLIP 2 INT8, sigmoid 0.1 | 88.89% | 0.71% | 0.789 | 94,553,333 | 16.50 / 17.98 ms |

These are **different operating points, not a quality leaderboard**.
SigLIP 2's provisional 0.1 cutoff accepts only nine known entries (8 TP,
1 FP); shared-score median is about 0.000026 and the 99th percentile 0.0038.
Its respectable threshold-independent ranking shows why near-zero native
recall must not be called a general failure of its visual representation.
Neither this diagnostic nor the upstream preparation checks isolate INT8
degradation against FP32. AP groups equal-score thresholds, so RAM++ binary
ties cannot receive an optimistic arbitrary ordering; binary-only AP still
cannot measure the unavailable continuous ranking.

The pilot trades more false positives for recall: 976 TP / 462 FP / 148 FN,
versus RAM++ 673 / 96 / 451. Weak pilot discrimination includes `beach`
(12 TP / 13 FP, AP 0.575) and `house` (20 TP / 17 FP, AP 0.634).
The dominant known false-positive counts are `person` (138), `building`
(43), `tree` (37) and `food` (27). These partial-label findings prioritize
public-data sampling/annotation-semantic review, not new wholesale private
labeling or final-test-driven threshold changes.

Approximate-group bootstrap (950 groups, 1,000 seeded resamples) gives
descriptive native precision intervals of 65.51-70.42% for the pilot and
85.00-89.96% for RAM++; SigLIP 2's nine accepted known entries give the much
wider 63.64-100%. These intervals do not remove development selection,
pretraining overlap, incomplete annotation or imperfect event grouping bias.
AP likewise depends on the known-entry sampling/prevalence; it is not
exhaustive vocabulary recall.

Applying the **same** precision-oriented policy to the 525 fit photos and
leaving thresholds unchanged on the 475 check photos yields:

| Model | Enabled labels | Check TP / FP / FN | Check precision | Check recall, including disabled-label positives |
| --- | ---: | ---: | ---: | ---: |
| Compact pilot | 4 | 152 / 17 / 400 | 89.94% | 27.54% |
| TinyCLIP | 1 | 19 / 8 / 533 | 70.37% | 3.44% |
| SigLIP 2 | 2 | 40 / 7 / 512 | 85.11% | 7.25% |

RAM++ is deliberately omitted from threshold fitting. Sparse evidence disables
most labels; **none of these policies is a broad shipping default**. The
check pool has 950 known entries / 552 positives and is reused development,
not another independent held-out result.

The same Ryzen 9 9950X3D host used ORT DirectML 1.23.0's **CPU provider**,
four ORT/BLAS threads, sequential model runs and three warmups. Median / p95
decode-preprocess-infer-postprocess times were 7.48 / 10.48 ms (pilot),
10.57 / 13.42 ms (TinyCLIP), 23.08 / 26.43 ms (SigLIP 2) and
779.35 / 818.80 ms (RAM++). File hashing is outside these timed stages;
application scheduling, UI and metadata work are absent. This is not
comparable to historical RAM++ timing with different environments.

Native whole-vocabulary emission totals are 12,705 / 11,659 / 32,904 / 459
for pilot / RAM++ / TinyCLIP / SigLIP 2, with vocabularies of
44 / 4,585 / 3,632 / 3,632. They are **not measured correct/useful tags**.
TinyCLIP/SigLIP metadata containing text embeddings adds 58,298,021 /
87,959,822 bytes respectively; ONNX size alone understates their asset budget.
No whole-photo human rating or exhaustive precision claim follows from this
comparison.

The next model-development experiment is bounded public-data expansion and
training-only imbalance/semantic diagnosis, using development evidence to
choose changes. Broader calibration evidence and a **new untouched test**
are required before another generalization claim; do not reopen the consumed
research test. Licensing clearance and actual application benchmarks remain
separate release gates.

### Controlled training-only state-balance experiment

The original audited training pool has strongly unequal **known** state
frequencies: `person` has 2,257 positive / 157 negative entries, `animal`
834 / 94 and `food` 527 / 126. This is not population prevalence and does not
make unknown entries negative. Before expanding downloads, isolate whether
the unweighted objective's state imbalance contributes to development false
positives with one controlled, same-seed retraining.

`train_pilot.py --loss-balance known-state` computes each label/state weight
from **audited original training rows only**: known total divided by twice
that state's count, capped at four. Both states must have evidence.
Unknown entries contribute neither frequencies, loss nor gradients.
Weighted loss is divided by the number of known entries, not the weight sum;
capped weights are not renormalized. Only the training objective changes.
Default `--loss-balance none`, architecture, initialization, head, data,
seed 66, five-epoch recipe, augmentation, optimizer and learning rates stay
unchanged. **Unweighted** development BCE still selects the best checkpoint,
so weighted and unweighted losses are not confused.

```powershell
& $python tools\PhotoLibrarian.ModelTraining\train_pilot.py `
  --train D:\Training\OpenImages\download-train-balanced-v1\download-manifest.jsonl `
  --validation D:\Training\OpenImages\download-validation-balanced-v1\download-manifest.jsonl `
  --regression-images D:\Temp\Pictures\Diverse `
  --output D:\Training\OpenImages\run-state-balanced-v1 `
  --weights-cache D:\Training\OpenImages\weights-cache `
  --research-only --device cuda --initialization imagenet1k-v2 --head-epochs 3 --finetune-epochs 2 `
  --loss-balance known-state
```

Predeclared diagnostic: compare the selected checkpoint against
`run-balanced-pilot-v2` on the **same ordered full prior-development cohort
and 44 labels**, checking fingerprints and target equality before masked BCE,
0.5 precision/recall and tie-aware per-label/macro AP. Use the existing
matched-development fit/check protocol for a secondary same-policy coverage
diagnostic, without changing its requirements. A lower fixed-cutoff false
positive count alone is not sufficient: report lost recall and ranking as
well. Export only after the completed checkpoint is verified, and apply the
unchanged FP32 parity gates. Retain a negative result rather than selecting
another seed or repeatedly adjusting the cap/recipe in response.

Do not open the consumed calibration/test or private judgments for this
experiment. It cannot establish independent generalization, approve a new
production model or resolve weight/image licensing. Wider data and separately
frozen calibration plus a new untouched test remain necessary.

#### Completed same-seed result

The protocol and training source were committed as `75c91f2` **before**
retraining. Both runs used the identical audited fingerprint, 5,936 supervised
training images and 2,390 supervised development images / 44 labels.
Initial unweighted development BCE matched exactly at 0.6959589017421828.
All five epochs completed (139.74 seconds excluding audit/baseline).
The predeclared selection rule chose **head epoch three**, rather than
fine-tuning: development BCE was 0.60699, 0.58232, 0.57348, 0.58185, 0.59623.
Both fine-tuning epochs were worse; neither the schedule nor cap was changed
after observing this.

| Full prior-development metric, 5,450 known entries | Original unweighted | Known-state balanced |
| --- | ---: | ---: |
| Selected unweighted BCE | 0.59450 | 0.57348 |
| Precision at unchanged 0.5 | 69.34% | 73.42% |
| Recall at unchanged 0.5 | 85.51% | 76.68% |
| TP / FP / FN | 2,673 / 1,182 / 453 | 2,397 / 868 / 729 |
| Macro AP, 44 labels | 0.7855 | 0.7869 |

Balancing reduced known false positives by **314 (26.6%)**, but lost
**276 true positives**. This mostly changes the operating point, rather
than clearly improving ranking. Per-label evidence is published in
[`state-balance-results-v1.csv`](state-balance-results-v1.csv). For example,
`person` false positives fell from 334 to 162, while true positives fell from
548 to 475; `tree` AP fell from 0.9125 to 0.8608. Some rare concepts improved,
but the aggregate is not a general quality win.

The existing 1,000-photo / 40-label matched development subset gives macro AP
**0.8059 to 0.7903**. Fitting the unchanged precision policy on its 525 fit
photos still enables the same four labels (`animal`, `tree`, `food`,
`person`). On the 475 check photos, precision/recall worsens from
**89.94% / 27.54% to 87.90% / 25.00%** (152 / 17 / 400 TP/FP/FN versus
138 / 19 / 414). This is reused development, not an independent test.
**Do not promote this candidate over the original research baseline.**
State balancing alone does not solve the reliable-coverage problem.

The candidate's FP32 export is still **17,024,708 bytes**, SHA-256
`68f897bd60fc807c158775b38208917894692cd362c2009d71b8b6205400c6ef`.
Selected checkpoint SHA-256:
`7e56d97cd93895f8d5bc21b286498ec345e3fb27e26a33eda182bf649b411920`.
All **105,160** development scores pass the unchanged FP32 parity gate:
max absolute error 0.000003338, zero 0.5 flips. The three synthetic / 16-photo
check also passes (max error 0.000008464). This verifies export fidelity,
not generalization or production approval. No new INT8 or DirectML claim
is made for this rejected candidate.

Reproduce export and comparison after training:

```powershell
& $python tools\PhotoLibrarian.ModelTraining\export_pilot.py `
  --run D:\Training\OpenImages\run-state-balanced-v1 `
  --output D:\Training\OpenImages\export-state-balanced-v1 --samples 16
& $evalPython tools\PhotoLibrarian.ModelTraining\compare_training.py `
  --baseline D:\Training\OpenImages\run-balanced-pilot-v2 `
  --candidate D:\Training\OpenImages\run-state-balanced-v1 `
  --candidate-bundle D:\Training\OpenImages\export-state-balanced-v1 `
  --comparison D:\Training\OpenImages\matched-development-v2 `
  --baseline-scores D:\Training\OpenImages\matched-development-scores-v1 `
  --output D:\Training\OpenImages\state-balanced-comparison-v1
```

`compare_training.py` verifies completed checkpoint/score hashes, audited
data fingerprints, ordered targets, fixed recipe and candidate export
identity. It reuses frozen baseline scores and scores only the candidate
on the exact prior-development photos, retaining both full and shared
per-label evidence. It does not read the consumed test or calibration files.
The next experiment should address public-data coverage and state/semantic
sampling rather than repeat tuning of this weight cap.

### Bounded public training expansion

The next experiment adds at most **10,000 original-train** Open Images photos
using a fresh fixed seed, 300 candidates per source MID/state and the existing
provenance-first rare-state-balanced cap. Exclude **all** previous training,
development and reserved evaluation selection IDs, author-profile URLs and
original MD5 before sampling. These exclusions require identities/provenance,
not test predictions or outcomes. The selected expansion is not automatically
accepted training data; retain source labels, unknown masks and licensing.

```powershell
$root = 'D:\Training\OpenImages'
python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$root\metadata\oidv7-train-annotations-human-imagelabels.csv" `
  --metadata "$root\metadata\train-images-boxable-with-rotation.csv" `
  --classes "$root\metadata\oidv7-class-descriptions.csv" `
  --split train --per-state 300 --max-images 10000 --eligible-first --balanced `
  --seed open-images-expansion-v1 `
  --exclude-selection "$root\selection-train-balanced-v1\selection.jsonl" `
  --exclude-selection "$root\selection-validation-balanced-v1\selection.jsonl" `
  --exclude-selection "$root\selection-evaluation-v1\selection.jsonl" `
  --output "$root\selection-train-expansion-v1"
python tools\PhotoLibrarian.ModelTraining\download_open_images.py `
  --selection "$root\selection-train-expansion-v1\selection.jsonl" `
  --output "$root\download-train-expansion-v1" --limit 10000 --workers 8
& $python tools\PhotoLibrarian.ModelTraining\train_pilot.py `
  --train "$root\download-train-balanced-v1\download-manifest.jsonl" `
  --additional-train "$root\download-train-expansion-v1\download-manifest.jsonl" `
  --validation "$root\download-validation-balanced-v1\download-manifest.jsonl" `
  --exclude-images "$root\download-evaluation-v1\download-manifest.jsonl" `
  --regression-images D:\Temp\Pictures\Diverse `
  --output "$root\run-expanded-pilot-v1" --weights-cache "$root\weights-cache" `
  --research-only --device cuda --initialization imagenet1k-v2 --head-epochs 3 --finetune-epochs 2
```

`--additional-train` verifies each manifest inside its own download root before
combining, rejects duplicate source IDs and sorts the union deterministically.
The full original-training and added cohorts are audited together. Reserved
`--exclude-images` original-validation downloads are verified/decoded **only
for exclusion**: source IDs, bytes, MD5, author and dHash distance <=6. They
never provide training loss, model selection, thresholds or comparison
outcomes. This covers edited/near copies that selection metadata cannot catch.
Each exclusion is counted explicitly. The existing private-image overlap
check remains local/read-only.

Predeclare the same unweighted five-epoch seed-66 recipe, unchanged original
development manifest, mapping and eligibility gates. Expanded training may
admit additional labels, but no validation evidence gate is relaxed. Compare
baseline/candidate on the **identical common-label supervised development
photos**; show own-head coverage separately rather than claim that differently
sized heads have directly comparable loss. Added examples plus a changed head
are a coupled experiment, not a clean architecture ablation. Use the already
frozen 1,000-photo fit/check diagnostic as a secondary coverage check without
tuning its policy. Do not access the consumed evaluation outcomes or private
judgments, change production defaults, or infer licensing clearance.

#### Completed expansion result

Protocol/training/comparison source was committed in `3e9a437` before fitting.
All 10,000 additional selected public photos downloaded and hash-verified.
Combined auditing accepted **14,541 training / 2,593 development images**,
with **14,404 / 2,526 supervised images for a 68-label head**. Every original
44 label remains eligible; 24 additional labels now pass the unchanged gates:
`duck`, `goat`, `deer`, `insect`, `butterfly`, `grass`, `leaves`, `mushroom`,
`ocean`, `bridge`, `fountain`, `truck`, `helicopter`, `wheelchair`, `bed`,
`kitchen`, `bathroom`, `television`, `bread`, `cake`, `pizza`, `hat`, `shoes`,
`sign`. The remaining 32 proposed concepts still abstain.

The 4,000 reserved download references were checked only for overlap.
The audit excluded 293 training near matches to those references; other
quarantines were 3,021 unknown rotations, 197 hierarchy conflicts, 31 private
near matches, 2,240 development-author overlaps and 84 development near
matches. These screens are approximate, not certification of event independence.
Applying the new reference exclusion also changes which original training
photos survive; this and the larger head are coupled with added data.

All five epochs completed in 293.45 seconds excluding audit/baseline.
The same unweighted development selection rule chose head epoch three:
losses 0.59956, 0.58478, **0.57782**, 0.59901, 0.60116.
The full original development cohort is byte-for-byte unchanged in the
recorded manifests. Because the head expanded, compare the **same 2,390
supervised photos / 44 common labels / 5,450 known entries**, not different
own-head losses:

| Common-label development metric | Original | Expanded |
| --- | ---: | ---: |
| Unweighted BCE from scores | 0.59450 | 0.58106 |
| Macro AP | 0.7855 | 0.7936 |
| Precision at unchanged 0.5 | 69.34% | 68.22% |
| Recall at unchanged 0.5 | 85.51% | 87.33% |
| TP / FP / FN | 2,673 / 1,182 / 453 | 2,730 / 1,272 / 396 |

The new head adds genuine supported breadth and modest common-label ranking
improvement, but **does not solve precision**. The frozen 1,000-photo shared
diagnostic has macro AP 0.80594 to 0.80645; its unchanged precision policy
still enables four labels. Development check precision/recall falls from
89.94% / 27.54% to **86.98% / 26.63%** (147 TP / 22 FP / 405 FN).
Do not promote this candidate as a better automatic-tagging default.
Own-head development metrics (68 labels, 6,526 known entries) are reported
separately in [`expansion-results-v1.csv`](expansion-results-v1.csv), not
used to exaggerate gains against the narrower baseline.

The 68-label FP32 export is **17,147,684 bytes**, SHA-256
`a0b790c4431c2ab5dffeb49c639a3ab5ef511935b305c3d2bc7681eec7ef5d70`;
checkpoint SHA-256
`70c7efd84aa58d09be3bf27ab9e875d72bbb4013eb338a1fddac29152556b372`.
All **171,768** supervised development scores pass FP32 parity (max error
0.000003219, zero 0.5 flips); the synthetic/16-photo check passes at
0.000013114 maximum error. No INT8 or DirectML claim is made for this head.

```powershell
& $python tools\PhotoLibrarian.ModelTraining\export_pilot.py `
  --run "$root\run-expanded-pilot-v1" --output "$root\export-expanded-pilot-v1" --samples 16
& $evalPython tools\PhotoLibrarian.ModelTraining\compare_training.py `
  --experiment expansion --baseline "$root\run-balanced-pilot-v2" `
  --candidate "$root\run-expanded-pilot-v1" `
  --candidate-bundle "$root\export-expanded-pilot-v1" `
  --comparison "$root\matched-development-v2" `
  --baseline-scores "$root\matched-development-scores-v1" `
  --output "$root\expanded-comparison-v1"
```

The report retains all common/own-head per-label evidence, admitted/dropped
labels, fitted thresholds and identities locally. Next work needs broader
independent calibration evidence and targeted review of public annotation
semantics/negative sampling, rather than assuming more images alone will
clear a precision gate. The consumed evaluation outcomes were not opened;
no new independent-test, licensing or shipping claim follows.

### Fresh calibration for the fixed expanded head

Freeze the 68-label export unchanged. Select at most 8,000 additional
original-validation photos with seed `open-images-expanded-calibration-v1`,
150 source MID/state candidates, provenance-first balanced selection, and
**all four** previous selections excluded (original training, expanded
training, development, first evaluation). The available eligible pool yields
only **3,396 photos under these per-state candidate caps**, not the requested
maximum. This does not prove that all remaining original-validation photos
are exhausted. Do not relax exclusions or invent negative labels to fill the cap.

```powershell
python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$root\metadata\oidv7-val-annotations-human-imagelabels.csv" `
  --metadata "$root\metadata\validation-images-with-rotation.csv" `
  --classes "$root\metadata\oidv7-class-descriptions.csv" `
  --split validation --per-state 150 --max-images 8000 --eligible-first --balanced `
  --seed open-images-expanded-calibration-v1 `
  --exclude-selection "$root\selection-train-balanced-v1\selection.jsonl" `
  --exclude-selection "$root\selection-train-expansion-v1\selection.jsonl" `
  --exclude-selection "$root\selection-validation-balanced-v1\selection.jsonl" `
  --exclude-selection "$root\selection-evaluation-v1\selection.jsonl" `
  --output "$root\selection-expanded-evaluation-v1"
python tools\PhotoLibrarian.ModelTraining\download_open_images.py `
  --selection "$root\selection-expanded-evaluation-v1\selection.jsonl" `
  --output "$root\download-expanded-evaluation-v1" --limit 8000 --workers 8
& $evalPython tools\PhotoLibrarian.ModelTraining\prepare_evaluation.py `
  --download "$root\download-expanded-evaluation-v1\download-manifest.jsonl" `
  --prior-run "$root\run-expanded-pilot-v1" `
  --bundle "$root\export-expanded-pilot-v1" `
  --exclude-download "$root\download-evaluation-v1\download-manifest.jsonl" `
  --regression-images D:\Temp\Pictures\Diverse `
  --output "$root\expanded-evaluation-protocol-v1"
```

`--exclude-download` adds earlier original-validation downloads to identity,
author, byte, MD5 and approximate-neighbor exclusion, **never their evaluation
outcomes**. A prior photo with unknown rotation still excludes identities,
but cannot supply an invented perceptual hash. Existing audit/model identities
remain mandatory. Additional screening leaves **1,467 calibration / 1,425
reserved test photos**, with 1,346 / 1,292 approximate groups. Quarantine:
402 unknown rotations, 100 prior near matches, one hierarchy conflict and
one private near match.

Before inference, retain protocol/manifests/model identities and the unchanged
90%-precision research policy in source history. Fit thresholds on calibration
only. Do not change architecture, mapping, policy or evidence requirements
based on this cohort. Calibration resubstitution is not independent quality;
report disabled labels and unknown emissions. **Keep the 1,425-photo reserve
unscored** while determining whether calibration supplies useful evidence.
Creating its grouped manifest does not mean it has been evaluated. The old
consumed test is never reused. No production approval follows from calibration.

Frozen identities before calibration inference:

- Protocol SHA-256: `6f643772f9ec996ff96fad6b7fcb5e67181d779216e1d9021cfe52a6cd6b6d97`
- Calibration manifest: `4ac7c4180ee0f2efe0117e85ae73c0ef317345d4b5ba679d5ee470b8185a8e18`
- Unopened reserve manifest: `7c4716b77deeb791c8ee078c30d88c16e6405e27f34ef5214312b1c02ac03a01`
- Fixed model: `a0b790c4431c2ab5dffeb49c639a3ab5ef511935b305c3d2bc7681eec7ef5d70`

Predeclare one **five-fold, approximate-group threshold cross-validation**
diagnostic inside calibration. Each group is assigned by the hash of
`["expanded-calibration-five-fold-v1", group]` modulo five; no fold-seed search.
Each check photo uses per-label thresholds fit on the other four folds under
the identical research policy. Pool its out-of-fold decisions with unknowns
masked and all disabled-label positives in recall. Store per-fold fitted
thresholds and enabled-label counts; pooled `enabledLabels` is null because
the policies differ. This checks threshold stability without consuming a
second final test and is **not an untouched-test quality claim**.

```powershell
& $evalPython tools\PhotoLibrarian.ModelTraining\calibrate_pilot.py fit `
  --evaluation "$root\expanded-evaluation-protocol-v1" `
  --bundle "$root\export-expanded-pilot-v1" --calibration-folds 5 `
  --output "$root\calibration-expanded-public-v1"
```

#### Completed calibration-only result

Protocol, hashes, fixed model, policy and five-fold diagnostic were committed
in **`525334e` before calibration inference**. There are **3,020 known entries**
(1,917 positives, 1,103 negatives) across the 1,467 calibration photos.
The full calibration fit enables just seven labels: `animal`, `flower`,
`tree`, `building`, `car`, `food`, `person`. **51 labels lack required
known-state/group evidence; another ten have no qualifying threshold.**
None of the newly admitted 24 head labels qualifies on the full calibration
fit. This is a data-evidence shortfall as well as a discrimination problem;
do not describe unsupported labels as proven inaccurate.

| Same calibration photos / known entries | Fixed 0.5 | Full-fit resubstitution | Five-fold grouped out-of-fold |
| --- | ---: | ---: | ---: |
| TP / FP / FN | 1,695 / 543 / 222 | 612 / 67 / 1,305 | 599 / 70 / 1,318 |
| Precision on known emissions | 75.74% | 90.13% | 89.54% |
| Recall, including disabled-label positives | 88.42% | 31.92% | 31.25% |
| Emitted tags / unknown emissions | 27,487 / 25,249 | 1,750 / 1,071 | 1,735 / 1,066 |

The full-fit 90.13% is an optimistic resubstitution result, **not** a passed
independent precision gate. Fold policies enable seven labels in four folds,
eight in one; `table` qualifies in only one fold and not in the full fit.
Pooled out-of-fold decisions are therefore **not one deployable policy**.
Per-fold precision ranges 83.45-93.89%. The descriptive pooled Wilson
precision interval is 86.99-91.63%, not a group-corrected confidence guarantee
or evidence of exceeding 90%. Correlated labels, partial annotation,
balanced selection and unknown pretraining overlap remain limitations.

All 68 label counts, fit statuses/thresholds and out-of-fold counts are in
[`expanded-calibration-results-v1.csv`](expanded-calibration-results-v1.csv).
Examples of the actual missing evidence: `cat` has 25 known positives and
**zero negatives**, `dog` 77 / 1, and `ocean` 44 / 0. Missing source entries
cannot fill those negative counts. `house` has 35 / 31 known states but no
threshold meets the fixed policy, so it needs discrimination/semantic review
rather than merely lowering the minimum evidence. These examples distinguish
annotation scarcity from model-quality failures.

Frozen full-fit calibration SHA-256:
`7a8309b1b547689ee8ffd92873c48403b607afaebac4372545d568ff082d1c2e`;
score-array SHA-256:
`cf54d36dff444c0e6cc5a8d03489a00e633e0d74e4f0303f9f1386562cecf201`.
The **1,425-photo reserve remains unscored**; no `test` inference was run for
this protocol. Neither the model nor policy was adjusted after this fit.
No production profile, private annotation or approved asset changed.

The next public-data acquisition needs **targeted known-negative and positive
coverage from authors not used for training**, potentially reserving such
authors before a future training selection instead of allowing rare-class
authors to be consumed by training. Increasing the per-state cap is not proof
it can supply the missing states; measure source/author coverage first.
Do not ask for hundreds of new private labels or treat unannotated public
images as negative. Any later training/policy changes require fresh
calibration and an independently justified untouched test; do not reuse this
fit as a blind quality claim.

### Full-source calibration availability audit

`audit_calibration_coverage.py` scans **all** pinned original-validation
human annotation rows for the 100 mapped concepts, with **no candidate cap,
image downloads or model inference**. It reuses exact MID transfer semantics,
provenance/rotation eligibility and hierarchy conflict screening. It reports
cumulative tiers: eligible source, disjoint from both selected training
cohorts, additionally disjoint from selected development, then additionally
unused by both earlier evaluation selections. Exclusion selections contribute
IDs, author-profile URLs and original MD5 only, never predictions/outcomes.
Counts use whole selected cohorts conservatively, even photos later rejected.

```powershell
python tools\PhotoLibrarian.ModelTraining\audit_calibration_coverage.py `
  --annotations "$root\metadata\oidv7-val-annotations-human-imagelabels.csv" `
  --metadata "$root\metadata\validation-images-with-rotation.csv" `
  --classes "$root\metadata\oidv7-class-descriptions.csv" `
  --training-selection "$root\selection-train-balanced-v1\selection.jsonl" `
  --training-selection "$root\selection-train-expansion-v1\selection.jsonl" `
  --development-selection "$root\selection-validation-balanced-v1\selection.jsonl" `
  --reserved-selection "$root\selection-evaluation-v1\selection.jsonl" `
  --reserved-selection "$root\selection-expanded-evaluation-v1\selection.jsonl" `
  --output "$root\calibration-source-audit-v1"
```

The complete scan finds 36,217 annotated source images with mapped MIDs;
31,480 remain metadata-eligible after 4,552 unsupported/unknown rotations,
157 without transferable labels and 28 hierarchy conflicts. None lacks
metadata. Of these, 16,922 are blocked at the selected-training
identity/author tier, a further 3,315 at development, and 9,375 at the
earlier-evaluation tier, leaving **1,868 unused metadata candidates**.
Those totals are images, not sums over label pairs.

For the **fixed 68-label head**:

| Cumulative metadata tier | Images | Labels with >=10 known positives AND negatives | Labels with >=40 positives AND >=20 negatives |
| --- | ---: | ---: | ---: |
| Eligible full source | 31,480 | 68 | 58 |
| Selected-training disjoint | 14,558 | 61 | 41 |
| Additionally development disjoint | 11,243 | 45 | 31 |
| Additionally earlier-evaluation disjoint | 1,868 | 6 | 4 |

The last column is only an illustrative **necessary count budget** for two
cohorts, not proof that author-disjoint groups can satisfy precision evidence.
Even the full eligible source cannot provide that count budget for all 68
labels. After all current exclusions, 59 head labels have **zero remaining
positives** and 62 have **zero remaining negatives**. Only `flower`, `tree`,
`building`, `car`, `food`, `person` retain ten of each; these are already
calibrated concepts, not a route to recover the 24 new labels by simply
increasing the selection cap.

For example, `cat` has 305 / 60 eligible positive/negative images, 156 / 31
after training exclusions, but zero unused after all exclusions. `ocean`
has 584 / **11** eligible states, 315 / **7** training-disjoint, and zero
unused. This separates original source scarcity from evidence consumed by
the current cohort/author allocation. It does not suggest dropping author
exclusions or treating unannotated photos as negatives.

[`calibration-source-coverage-v1.csv`](calibration-source-coverage-v1.csv)
publishes all 100 concepts' cumulative image counts, distinct nonempty
author-profile counts and missing-author counts. The local JSON retains
source and exclusion hashes; SHA-256:
`d031b1b819778444b2bfac0f854d1176c2cf3b639c33003fcbc20cb62a48c78c`.
The source hashes match the pinned annotation/metadata catalog. No photo IDs,
author URLs or model scores are in the committed aggregate.

These are **upper bounds before decoder, byte and perceptual-duplicate
auditing**, not ready-to-use data counts. Distinct author URLs do not certify
independent events; metadata eligibility does not clear individual rights.
Do not weaken current exclusions based on these numbers. The new 1,425-photo
reserve stays unscored.

**Next actionable research design:** freeze a narrower, evidence-supported
vocabulary and jointly reserve public calibration/test author groups **before**
selecting any future training cohort; then exclude those authors/identities
from the original-train source and retrain from the reviewed initialization.
Count both states/groups per split before downloads and preserve the current
reserve. This is a new protocol/model version, not a retroactive relabeling
of current development data as unseen. A model trained only against the
remaining current calibration pool cannot establish broad reliable coverage.
Labels with inherently insufficient source evidence need an independently
licensed annotated source or explicit deferral, not invented negatives.

### User-approved author-first model version

The maintainer chose **reserve evaluation authors first, then retrain an
evidence-supported, potentially narrower vocabulary**. This is a new research
model version, not a continued fine-tune of photo-trained checkpoints.
Both earlier evaluation selections (including the current unscored reserve)
are excluded before selecting 6,000 original-validation candidates with seed
`open-images-authors-first-v1`, 100 candidates per MID/state, and the existing
provenance-first balanced cap. Previous training/development authors are
allowed into this **new** reservation; all such authors are then excluded
from new training. Prior analyst/model exposure exists, so neither new
development nor calibration is advertised as a fresh blind test.

`prepare_author_reservation.py` verifies/decodes all candidates, excludes both
prior evaluation downloads' identities/authors/near hashes plus private
near matches, and groups author/identity/dHash constraints transitively.
The frozen plan reserves **2,525 development / 2,519 calibration photos**
before training. Its SHA-256 is
`aa3ff2b531b26660f8a135b1dd1c7889c8ad14bb7277ebd2bc99844c20226809`.
The existing 1,425-photo reserve is unchanged and unscored.

```powershell
python tools\PhotoLibrarian.ModelTraining\select_open_images.py `
  --annotations "$root\metadata\oidv7-val-annotations-human-imagelabels.csv" `
  --metadata "$root\metadata\validation-images-with-rotation.csv" `
  --classes "$root\metadata\oidv7-class-descriptions.csv" `
  --split validation --per-state 100 --max-images 6000 --eligible-first --balanced `
  --seed open-images-authors-first-v1 `
  --exclude-selection "$root\selection-evaluation-v1\selection.jsonl" `
  --exclude-selection "$root\selection-expanded-evaluation-v1\selection.jsonl" `
  --output "$root\selection-authors-first-v1"
python tools\PhotoLibrarian.ModelTraining\download_open_images.py `
  --selection "$root\selection-authors-first-v1\selection.jsonl" `
  --output "$root\download-authors-first-v1" --limit 6000 --workers 8
& $evalPython tools\PhotoLibrarian.ModelTraining\prepare_author_reservation.py `
  --download "$root\download-authors-first-v1\download-manifest.jsonl" `
  --exclude-download "$root\download-evaluation-v1\download-manifest.jsonl" `
  --exclude-download "$root\download-expanded-evaluation-v1\download-manifest.jsonl" `
  --regression-images D:\Temp\Pictures\Diverse --output "$root\author-reservation-v1"
& $python tools\PhotoLibrarian.ModelTraining\train_pilot.py `
  --train "$root\download-train-balanced-v1\download-manifest.jsonl" `
  --additional-train "$root\download-train-expansion-v1\download-manifest.jsonl" `
  --validation "$root\download-authors-first-v1\download-manifest.jsonl" `
  --author-reservation "$root\author-reservation-v1\reservation.json" `
  --exclude-images "$root\download-evaluation-v1\download-manifest.jsonl" `
  --exclude-images "$root\download-expanded-evaluation-v1\download-manifest.jsonl" `
  --regression-images D:\Temp\Pictures\Diverse `
  --output "$root\run-authors-first-v1" --weights-cache "$root\weights-cache" `
  --research-only --device cuda --initialization imagenet1k-v2 --head-epochs 3 --finetune-epochs 2
```

All 6,000 downloaded reservation references, not just accepted calibration
photos, exclude identities, authors, bytes, MD5 and near hashes from training.
New development uses only the frozen development IDs. Original train-only
sources, original initialization, seed 66, unweighted five-epoch recipe,
preprocessing and original train/development evidence gates are unchanged.
An additional **pre-training head gate** requires 20 known calibration
positives, ten negatives and five approximate groups per state. Calibration
states determine evidence-supported vocabulary **before** model fitting,
never the selected checkpoint or loss. Missing states remain unknown.
Reserve membership and input hashes are verified before the run begins.

The new development cohort differs, so its losses/precision must not be
presented as a paired win against earlier models' old-cohort metrics.
After verified FP32 export, fit the unchanged research precision policy and
the same predeclared calibration-only five-fold diagnostic. Do not tune
label gates, model, schedule or fold seed after observing those outcomes;
keep the existing final reserve unscored.

#### Frozen author-first calibration binding

Training was started only after protocol commit `6f78a33`. The head contains
42 evidence-supported labels; the selected checkpoint is head epoch 3
(unweighted development BCE 0.57251417). Both fine-tuning epochs completed
but did not improve checkpoint selection. The audited cohorts contain 11,639
training / 2,525 development images, of which 11,186 / 2,328 have known states
for the final head. The FP32 export is **17,014,460 bytes**; all 97,776
development scores agree with PyTorch within 2.474e-6, with no 0.5 decision
flips. New-cohort development precision/recall at 0.5 is 68.48% / 87.34%;
these are model-selection diagnostics, not a paired improvement claim.

`bind_author_calibration.py` verifies the completed checkpoint/export/run,
reservation/download/mapping identities, exact prior evaluation exclusions
and train/development cohort fingerprint. It recomputes the frozen
transitive groups/split from actual decoded reservation rows and checks
all 6,000 reservation references against training. It writes only a
calibration manifest; no new test is manufactured or existing test scored.
The frozen calibration has **2,519 photos / 1,623 approximate groups**.

```powershell
& $python tools\PhotoLibrarian.ModelTraining\export_pilot.py `
  --run "$root\run-authors-first-v1" --output "$root\export-authors-first-v1"
& $evalPython tools\PhotoLibrarian.ModelTraining\bind_author_calibration.py `
  --reservation "$root\author-reservation-v1\reservation.json" `
  --download "$root\download-authors-first-v1\download-manifest.jsonl" `
  --run "$root\run-authors-first-v1" --bundle "$root\export-authors-first-v1" `
  --output "$root\author-calibration-protocol-v1"
& $evalPython tools\PhotoLibrarian.ModelTraining\calibrate_pilot.py fit `
  --evaluation "$root\author-calibration-protocol-v1" `
  --bundle "$root\export-authors-first-v1" --calibration-folds 5 `
  --output "$root\author-calibration-results-v1"
```

Identities frozen **before calibration inference**:

| Artifact | SHA-256 |
| --- | --- |
| Selected checkpoint | `4a6b52e2f1556ade0d515253a18df62bc9791351f628bd92b1e72bd04192bd4c` |
| FP32 export | `b6b0afbcd00c92e020d56ba644b15a7d79ca71ec6c142dffefe042c49969f92f` |
| Calibration protocol | `d44b53931d833e28961792a9c73d82bb7234142a6881c4716684011a6b4728c3` |
| Calibration manifest | `8739a537a0e2b97d52220134f2106d01f8834cc61a680e9b74c6688237394ec0` |

#### Author-first results: evidence coverage restored, not production approval

Binding code and the above hashes were committed in `28de1c9` **before
calibration inference**. The unchanged policy and five-fold seed were used;
there was no outcome-driven head/threshold-policy/schedule/seed tuning.
All 42 head labels now have the predeclared full-calibration state/group
evidence. **17 qualify for a full-fit threshold; 25 have no threshold meeting
the fixed precision/support policy**, rather than missing-state evidence.

| 2,519 calibration photos; 5,467 known entries | Fixed 0.5 | Full-fit resubstitution | Grouped out-of-fold |
| --- | ---: | ---: | ---: |
| TP / FP / FN | 2,681 / 1,289 / 420 | 1,355 / 142 / 1,746 | 1,219 / 162 / 1,882 |
| Known-entry precision | 67.53% | 90.51% | 88.27% |
| Recall including disabled-label positives | 86.46% | 43.70% | 39.31% |
| Emitted tags with unknown source state | 36,178 | 4,727 | 4,664 |

Full-fit labels: animal, dog, bird, horse, flower, tree, grass, leaves,
rocks, car, bicycle, boat, chair, table, food, person and snow. Ten labels
qualify in all five fold fits; fold policies are not one deployable policy.
All 42 labels' known states/groups, full-fit thresholds/counts and OOF counts
are published in `author-calibration-results-v1.csv`. Cat has 23 positives
and 23 negatives but no qualifying threshold, illustrating a remaining
discrimination/support limitation rather than an absent-negative shortcut.

**Decision: do not promote this model or change the shipping default.**
Reserving authors first makes a broader calibratable head feasible, but
full-fit precision is optimistic and grouped OOF precision misses the 90%
research target. Recall remains limited. Old-cohort seven-label results
and this narrower head have different photos, groups and recall denominators:
17 versus seven enabled labels is an allocation/coverage observation,
not a matched accuracy gain. Fold cross-validation checks threshold fitting
only, not the entire vocabulary/model-selection procedure. Neither
development nor calibration is a new blind test, and partial annotations
do not establish exhaustive precision, recall or useful-tag quality.

The original consumed test was not reused; the newer **1,425-photo reserve
remains unscored**. Photos, private identifiers and model binaries remain
external. Further production claims still require a separately justified,
frozen evaluation plan, licensing clearance and actual application evidence;
another ad-hoc tuning run is not a substitute.

| Measured artifact | SHA-256 |
| --- | --- |
| Calibration result JSON | `4dc4ba39e0b208f1cb9bc2feab7e2a44f5001bd97a50e1d8124cda1888f50aa9` |
| Calibration score arrays | `fd7ecce98c17f1b8ee9ef9457bd7471975cb071ce1f30ce4f4947514f37bac24` |
| Published aggregate CSV | `7b5eeccf92fb35c4e481db33d4b8aac0d6d58409c445c9b7e709850a6ff6c301` |

#### Reserved-test feasibility audit: no predictions

`audit_reserved_test.py` checks the existing reserve against the fixed
author-first export/calibration/run and actual reserved groups. It verifies
the original test manifest and each test image's bytes/decoded dHash, then
checks identities/authors/MD5/near hashes against new training and all
accepted new development/calibration reservations. **Zero overlap violations**
were found across **1,425 photos / 1,292 approximate groups**. It does not
create a runtime session, infer scores, fit thresholds or consume the test.

Only **16 of 42 head labels**, including **nine of 17 enabled labels**,
have at least ten known positives and negatives and five groups per state.
These counts describe evidence, not a statistical-power guarantee. The nine
enabled labels are animal, flower, tree, grass, leaves, car, table, food and
person. Dog has 76 positives / three negatives, bird 55 / two, horse 14 /
zero, rocks 37 / zero and snow 15 / zero. All 42 rows, including disabled
and unsupported labels, are in `author-reserved-test-coverage-v1.csv`.
Unknowns remain unknown.

The reserve is disjoint from this model's photo training/development and
threshold fitting, but its earlier selection cannot broadly verify this
head. A one-time evaluation could measure fixed-policy performance on known
entries only, with every head label reported and sparse evidence explicit.
It cannot establish 17-label production precision, exhaustive recall or
representative household quality. Do not prune labels, change thresholds
or tune the model based on this support audit. Test-support labels have now
been inspected for feasibility; **predictions/outcomes remain unaccessed**.
The audit alone does not authorize consumption; the user subsequently
approved the limited evaluation below.

```powershell
& $evalPython tools\PhotoLibrarian.ModelTraining\audit_reserved_test.py `
  --evaluation "$root\expanded-evaluation-protocol-v1" `
  --run "$root\run-authors-first-v1" `
  --reservation "$root\author-reservation-v1\reservation.json" `
  --download "$root\download-authors-first-v1\download-manifest.jsonl" `
  --bundle "$root\export-authors-first-v1" `
  --calibration "$root\author-calibration-results-v1\calibration.json" `
  --calibration-protocol "$root\author-calibration-protocol-v1\protocol.json" `
  --output "$root\author-reserved-test-audit-v1"
```

The external audit JSON SHA-256 is
`45927627c8df4f2dddad935aef13f937c8795f1ace78989149881b04d4cb0ef3`.
The original test manifest remains
`7c4716b77deeb791c8ee078c30d88c16e6405e27f34ef5214312b1c02ac03a01`.
No original protocol, model or threshold artifact is modified.

#### User-approved one-time test protocol

After seeing the evidence limitations, the user explicitly chose
**"Run the limited one-time evaluation (Recommended)"**. This is permission
to spend the existing reserve, not to tune on it or claim production quality.
`audit_reserved_test.py --bind-test` repeats the identity/separation checks
and copies the original test manifest byte-for-byte into a new test-only
protocol. It binds the unchanged 42-label model, original calibration
protocol and exact full-fit threshold artifact. No threshold is refit, no
label is pruned and no earlier protocol is rewritten. The test runner still
accepts original same-protocol evaluation; a separate test binding additionally
requires the exact original calibration artifact hash and unchanged policy.

**Frozen before test inference:**

| Artifact | SHA-256 |
| --- | --- |
| Test-only protocol | `27e0ae50f372b4db81adb6785a50aa9e051b6de63080f8a80bed91ab23d38cc3` |
| Original test manifest, unchanged | `7c4716b77deeb791c8ee078c30d88c16e6405e27f34ef5214312b1c02ac03a01` |
| Fixed model | `b6b0afbcd00c92e020d56ba644b15a7d79ca71ec6c142dffefe042c49969f92f` |
| Original calibration-only thresholds | `4dc4ba39e0b208f1cb9bc2feab7e2a44f5001bd97a50e1d8124cda1888f50aa9` |

Primary reporting is known-entry precision/recall for the fixed calibrated
policy, with fixed 0.5 as a secondary baseline. Report **all 42 head labels**,
including disabled labels' false negatives, sparse known states/groups,
TP/FP/FN, unknown emissions and descriptive intervals. No supported-label-only
leaderboard, comparison against different cohorts, production approval,
post-test policy changes or reuse as untouched testing is permitted.
The nine-label support observation is disclosed, not a new selected head.

```powershell
# Repeat the feasibility command above with:
# --bind-test --output "$root\author-final-test-protocol-v1"
& $evalPython tools\PhotoLibrarian.ModelTraining\calibrate_pilot.py test `
  --evaluation "$root\author-final-test-protocol-v1" `
  --bundle "$root\export-authors-first-v1" `
  --calibration "$root\author-calibration-results-v1\calibration.json" `
  --output "$root\author-final-test-results-v1"
```

#### One-time test results: promising known-entry precision, limited coverage

Protocol, exact model/calibration/test hashes and executed source were
committed in **`309be14` before the first test inference**. The fixed
17-threshold policy was applied once; no threshold refitting or label pruning
occurred. All 42 head labels are reported in
`author-final-test-results-v1.csv`. Saved score arrays were independently
re-summarized and aggregate counts reconciled without rerunning inference.

| 1,425 photos; 2,816 known entries | Fixed 0.5 | Frozen calibrated policy |
| --- | ---: | ---: |
| TP / FP / FN | 1,487 / 592 / 201 | 839 / 66 / 849 |
| Known-entry precision | 71.52% | **92.71%** |
| Recall including disabled-label positives | 88.09% | **49.70%** |
| Emitted tags | 22,265 | 3,354 |
| Unknown-state emitted tags, not counted as FP | 20,186 | 2,449 |
| Photos receiving any tag | 1,425 | 1,287 |

The frozen calibrated policy has 526 fewer known false positives than fixed
0.5, at the cost of 648 fewer true positives. Its descriptive binomial
precision Wilson interval is 90.83%-94.23%; correlated/grouped labels mean
this is **not** a cluster-aware confidence guarantee. This is an independent
known-entry check of the frozen policy, not a matched comparison with older
heads evaluated on different photos.

**Do not interpret 92.71% as precision for all 3,354 emitted tags.** Only
905 emissions have a known source state; 2,449 remain unreviewed/unknown.
Only nine of 17 enabled labels had the predeclared descriptive state/group
support. Apparent 100% precision for horse, rocks and snow has zero known
negative examples; dog and bird have only three and two. Even among better
supported labels, grass is 12 TP / four FP (75%), food 87 / 14 (86.14%),
tree 73 / nine (89.02%) and car 103 / 13 (88.79%). Do not remove or retune
these labels after seeing the test. Disabled labels' positives remain in
the recall denominator.

**Decision:** retain as a promising research candidate, not a shipping
default. Aggregate known-entry precision clears the provisional 90% target
on this sample, but label-specific reliability, coverage, unknown emissions,
population representativeness, pretraining overlap and redistribution rights
remain unresolved. Actual Release-application performance and useful-tag
human evidence are also outstanding. No new personal relabeling was needed.

The **1,425-photo reserve is now consumed** and must never be reused as
untouched evidence for a changed model/policy. The original earlier consumed
test was not revisited. Preserve both raw protocol/calibration artifacts and
the new binding/results; do not rewrite the earlier "unscored" records.

| Measured artifact | SHA-256 |
| --- | --- |
| Test result JSON | `240343450132a50111f0716bc806694e63f24d0be8f28b4f8881fe8a0480d0ae` |
| Test score arrays | `31090a444e4e5328d2c19e35b952c750249f58764503b625872758ab66e519d6` |
| Published all-label results CSV | `90b9c965a1027dfcff5a8c16931ac1b3e144adac1e010c5929f7e89616dd4f7f` |

#### Author-first candidate: actual CPU/DirectML diagnostics

The unchanged 42-label FP32 export was measured on all **156 existing private
regression photos**, not either consumed test. `benchmark_pilot.py` was reused
unchanged in separate CPU-then-DirectML processes, with four intra-op threads
and five warmups. ORT DirectML 1.23.0 / Python 3.12.0, Ryzen 9 9950X3D,
Windows 11; video inventory reports RTX 5080 driver 32.0.16.1088 and integrated
AMD graphics driver 32.0.21042.62. **DirectML device index 0** was explicitly
requested; inventory alone does not identify which adapter that index selects.
Do not attribute these measurements to a particular GPU without adapter proof.

| Host/Python measurement | CPU | DirectML device 0 |
| --- | ---: | ---: |
| Session creation, ms | 33.97 | 579.90 |
| First inference, ms | 2.72 | 3.25 |
| Warm inference p50 / p95, ms | 2.84 / 3.71 | 1.04 / 1.28 |
| Decode/preprocess p50 / p95, ms | 34.46 / 192.51 | 31.72 / 184.53 |
| Decode/preprocess/inference p50 / p95, ms | 37.21 / 195.39 | 32.87 / 185.56 |
| Sampled host-process peak RSS, MiB | 550.46 | 771.95 |
| Host-process RSS delta, MiB | 495.49 | 716.95 |

DirectML profiling shows **one fused DirectML partition, zero CPU nodes**;
the separate strict session with CPU execution fallback disabled succeeds.
The CPU graph profiles 188 CPU nodes. Fused partitions are not comparable
operator counts. Python runtime retry fallback is disabled in both runs.

`compare_provider_scores.py` verifies model/ordered-label/score identities
and requires identical ordered image identities, preprocessing and threads.
All **6,552 CPU/DirectML scores** agree within **1.491e-6** (fixed tolerance
1e-4), with **zero flips at 0.5 and zero at the 17 unchanged calibrated
thresholds**. Disabled labels remain disabled; no threshold fitting, private
judgment changes or test-score access occurs. Synthetic tests check that a
small score error can still cause a calibrated decision flip and must not be
hidden by the numeric tolerance.

**Interpretation:** this candidate retains compact, working DirectML
execution, but decode/preprocessing dominates and GPU session setup is
substantially slower. These are one sequential local diagnostic per provider,
not controlled repeated speedup estimates or Release-application measurements.
File cache, power/host load and adapter choice are not controlled. Host RSS
includes Python, decoding and provider allocations; it is not isolated model
memory or GPU VRAM. No application changes or production approval follow from
these measurements. The next integration gate remains a reviewed, legally
cleared model and actual application measurements, not another tuning run on
either consumed test.

```powershell
& $evalPython tools\PhotoLibrarian.ModelTraining\benchmark_pilot.py `
  --bundle "$root\export-authors-first-v1" --images D:\Temp\Pictures\Diverse `
  --provider cpu --threads 4 --limit 156 `
  --output "$root\benchmark-authors-first-cpu-v1"
& $evalPython tools\PhotoLibrarian.ModelTraining\benchmark_pilot.py `
  --bundle "$root\export-authors-first-v1" --images D:\Temp\Pictures\Diverse `
  --provider directml --device-id 0 --threads 4 --limit 156 `
  --output "$root\benchmark-authors-first-directml-v1"
& $evalPython tools\PhotoLibrarian.ModelTraining\compare_provider_scores.py `
  --cpu "$root\benchmark-authors-first-cpu-v1" `
  --directml "$root\benchmark-authors-first-directml-v1" `
  --bundle "$root\export-authors-first-v1" `
  --calibration "$root\author-calibration-results-v1\calibration.json" `
  --output "$root\benchmark-authors-first-parity-v1"
```

Privacy-safe aggregate measurements: `author-provider-results-v1.csv`.
Photo-level reports, profiles and arrays remain external:

| Artifact | SHA-256 |
| --- | --- |
| CPU report | `1637450087c55ddb871c7c87f3964a47476afce7f0de45372a8a7a97d197c33f` |
| DirectML report | `3a6da5414a069a564c8f889aad9fe7c53469a6b5903486a24ceff20cde74881e` |
| Provider parity report | `54723b8158b42f4c517543208107ea59f969a78f7e102f62f87179cd25875194` |

#### Author-first candidate against existing manual judgments

No new photo labeling, training, inference or test access was required.
`evaluate_regression.py --calibration` reuses the already saved candidate
CPU/DirectML regression scores and the **unchanged public-calibration
thresholds**. The original no-calibration 0.5 path remains unchanged.
The optional path verifies model, ordered labels and valid thresholds;
disabled labels emit nothing, and missing judgments remain unknown.

The existing 156-photo review contains 2,472 completed judgments. Exact
canonical-label intersection with this 42-label head provides **174 reviewed
pairs across 19 labels**: 133 correct and 41 incorrect. No aliases or ancestors
inherit judgments. This pool differs from the original 44-label head's
181-pair/21-label pool; cross-head totals are not a matched improvement claim.

| Same 174 previously reviewed pairs | Fixed 0.5 | Frozen calibrated policy |
| --- | ---: | ---: |
| TP / FP / FN | 113 / 22 / 20 | 57 / 6 / 76 |
| Reviewed-pair precision | 83.70% | 90.48% |
| Reviewed-positive recall, including disabled labels | 84.96% | 42.86% |
| All emitted photo/tag pairs | 2,192 | 379 |
| Emitted pairs with an explicit judgment | 135 | 63 |
| Emitted pairs without a judgment | 2,057 | 316 |

On this fixed pool, calibration avoids 16 reviewed-incorrect tags but also
loses 56 reviewed-correct tags. Of 156 photos, 143 receive at least one
calibrated tag; 53 have at least one explicitly reviewed-correct calibrated
tag. The other photos/tags are **not** inferred incorrect or useless.
All 42 head labels, including labels with no exact reviewed evidence, are
listed in `author-regression-results-v1.csv`.

**Interpretation:** the public precision policy also trades coverage for
fewer reviewed mistakes on the maintainer's previously exposed photos.
This is not independent generalization, exhaustive precision, a useful-tag
score or a new blinded human study. Prior model outputs selected review
candidates; most new emissions have no judgment, and semantically overlapping
parent/child tags are not deduplicated as distinct useful tags. The review
artifact has names but no historical photo hashes, so historical byte identity
cannot be certified. Historical model outputs retain their old top-K,
vocabulary and thresholds and are not a controlled leaderboard.
Neither thresholds nor labels were adjusted after seeing these results.
Both consumed test cohorts remain unchanged and unused here.

```powershell
& $evalPython tools\PhotoLibrarian.ModelTraining\evaluate_regression.py `
  --benchmark "$root\benchmark-authors-first-cpu-v1" `
  --compare-provider "$root\benchmark-authors-first-directml-v1" `
  --review D:\Temp\DiverseLabeling\Diverse-General-ReviewComplete-V2-20260917\review-items.json `
  --calibration "$root\author-calibration-results-v1\calibration.json" `
  --output "$root\regression-authors-first-v1"
```

Photo-level artifacts stay external. The source review checksum was verified
unchanged after analysis:

| Artifact | SHA-256 |
| --- | --- |
| Existing completed review | `2cd6353c566aea09e48516256d05ef10eaba630b722b1b04442985f18e72205d` |
| Regression result JSON | `a12021d47708056e15b70dd5f64cc892a6c60bd317fd2566b2190fe84119c52a` |
| Published all-label aggregate CSV | `262f40322c1b01b16fafc2635f357caa46fc2610e441206bdc1efb542c182327` |

### Outstanding production gates

1. Review the pilot and public-data mapping/coverage with diverse users; audit
   verified Open Images subsets and cross-source duplicates. Consented private
   data remains optional. Collect independent positive
   **and reviewed negative** groups. A provisional data-collection goal is at
   least 50 positive training groups per concept, with separately reviewed
   calibration/test coverage; it is not a scientifically established minimum.
   Drop unsupported labels deliberately and version the vocabulary.
2. Clear pretrained-weight/image/annotation terms before product training or redistribution. Pin the
   full environment and checkpoint SHA-256. Log device, seeds, augmentations,
   optimizer, schedules and manifest/vocabulary identities. Replace the ImageNet
   classifier with an ordered multi-label head; use independent sigmoid scores,
   not softmax. Freeze the encoder while training the head, then unfreeze with a
   smaller learning rate. Start at 224; assess small-object failures before 320.
3. Use unreduced binary cross-entropy on known labels only, masking -1 before
   reduction. An all-unknown sample/batch contributes no supervised update.
   Compute imbalance weights from **training known labels only**; cap extreme
   weights and compare against unweighted masked loss on validation. Do not
   interpret negative-unlabeled data as completely annotated. Record event-aware
   sampling and deduplication.
4. Select architecture/hyperparameters on validation; calibrate per-tag
   thresholds on separate calibration data. Target precision and minimum
   evidence must be agreed before opening the test. Allow disabled labels and
   zero tags/abstention. Thresholds without sufficient reviewed evidence must
   not become production defaults.
5. Validate the research FP32/INT8 ONNX with fixed float32 NCHW input and ordered independent scores.
   Record actual preprocessing including EXIF orientation, color conversion,
   resize/crop/interpolation/normalization. Check finite outputs, all per-tag
   score errors and threshold-decision flips against PyTorch on synthetic and
   consented local images. Predeclare tolerances. Quantize using training-only
   representative data; compare INT8 against FP32 on the same validation photos,
   recalibrate separately, and keep all ONNX/external-data hashes and byte sizes.
6. Freeze decisions, then run **one untouched test** on custom FP32/INT8,
   RAM++, SigLIP 2 and TinyCLIP with matched photos and preprocessing records.
   Record exact concept mappings and unmapped/unsupported concepts; keep native
   coverage separate from shared-label scores. Report per-tag precision/recall,
   macro/micro metrics and uncertainty, prediction coverage/abstention, useful
   distinct correct tags per photo, error patterns, and blinded 0-5 human
   ratings. Do not double-count ancestors as extra useful tags. Partial-label
   metrics cover reviewed entries only: full recall claims require an
   exhaustively reviewed test vocabulary, not missing-label-as-negative scoring.
7. Extend the measured Python CPU/DirectML diagnostics above to actual Windows
   Release application cold load, warm-up, p50/p95
   preprocessing/inference/end-to-end latency, peak working set, model bytes,
   machine/driver/thread settings, provider availability and node fallback.
   Record failed or unsupported provider runs explicitly. INT8 on DirectML is
   not presumed faster. Extend ModelBench only once the custom output contract
   and a real export exist; the current runner does not support this head.
8. Publish only aggregate consented comparison evidence, license inventory and
   a model card: intended/excluded uses, vocabulary/rules/version, data sources
   and coverage gaps, private-data policy, known biases/failures, training recipe,
   checkpoint/export hashes, measured metrics, provider limitations and notices.
   Choose no shipping default before maintainer review. Runtime integration must
   retain generated-tag provenance and vocabulary/pipeline approval invalidation.
   Hosting #68 must not advertise these research files as a custom model bundle.

## Verification

```powershell
python -m unittest discover -s tools\PhotoLibrarian.ModelTraining -p "test_*.py" -v
```

Tests use synthetic byte fixtures, not training evidence. They cover
unknown-state preservation, alias/hierarchy validation, transitive leakage
constraints, reviewed-cohort exclusion, source holdout conflicts, reproducibility,
invalid inputs and unchanged source contents/modification times. Public-data
tests mock all network access and cover duplicate checksum headers, source
pinning, truncated/corrupt payloads, exact cache reuse, union/narrow-label
semantics, machine-label exclusion, license/attribution gates and selection caps.
Optional research tests additionally check decode/rotation and overlap
quarantine, positive/negative label gates, masked gradients, all-unknown batches,
partial-label metrics, training-only INT8 calibration and export input/output
integrity. Run the same command with the isolated training environment's Python
to execute these tests. CI first runs the dependency-free subset, then installs
the isolated evaluation requirements and runs those optional tests without
models or a GPU. PyTorch/export tests still explicitly skip there; CI does not
train a model. Locally, all **97 distinct tests** were exercised across the
training environment (one provider-test skip) and evaluation environment (13
training/export-test skips). The base Python run has 60 optional skips.
Comparison tests cover legacy RAM label integrity, frozen model/photo
identities, zero-shot preprocessing/scoring, unknown-masked tie-aware AP and
deterministic grouped uncertainty.
The controlled retraining tests additionally cover training-only state counts,
weight caps and weighted unknown gradients, immutable run/array identities,
ordered targets, recipe equality and masked BCE/ranking comparisons.
Author-first tests additionally cover reservation membership/group separation,
known-state head gating, recomputed transitive grouping/splits, and immutable
run/export/checkpoint/download/exclusion/cohort bindings.

Exports also score every supervised development image with FP32 and, when
requested, INT8. Reports retain known-entry metrics and quantized decision flips
at the uncalibrated 0.5 cutoff, with local score arrays for follow-up analysis.
These are development results, not final-test quality or production acceptance.

[oi-download]: https://storage.googleapis.com/openimages/web/download_v7.html
[oi-facts]: https://storage.googleapis.com/openimages/web/factsfigures_v7.html#licenses
[mobilenet]: https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.mobilenet_v3_large.html
[mobilenet-source]: https://github.com/pytorch/vision/blob/main/torchvision/models/mobilenetv3.py
[efficientnet]: https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.efficientnet_b0.html
[tv-models]: https://docs.pytorch.org/vision/stable/models.html
[imagenet-about]: https://image-net.org/about.php
[imagenet-agreement]: https://image-net.org/accessagreement.php
[cc-by-20]: https://creativecommons.org/licenses/by/2.0/
[cc-by-40]: https://creativecommons.org/licenses/by/4.0/
[mobileclip]: https://github.com/apple-aiml-research/ml-mobileclip/blob/main/LICENSE_MODELS
