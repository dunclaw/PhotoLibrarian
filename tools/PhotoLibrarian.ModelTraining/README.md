# Compact home-photo tagger: research foundation

This is the **data-readiness stage of #66**, not a trained model, shipping
profile, or completion of that issue. No training run, ONNX export, or scored
custom-model comparison is claimed. The application, RAM++/TinyCLIP profiles,
existing approvals, and photo metadata are unchanged.

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

## Initial model decision and license inventory

Source review: 2026-10-19. URLs can change; the actual training run must pin
package versions, source commits, checkpoint URLs/revisions and full SHA-256.
This stage downloads no weights and approves none for redistribution.

| Component | Evidence and proposal | License / release condition |
| --- | --- | --- |
| MobileNetV3-Large | First candidate: [TorchVision V2][mobilenet] lists 5,483,032 parameters, 0.22 GFLOPS, 21.1 MB checkpoint; 224 crop after resize 232 with bilinear interpolation | TorchVision code is BSD-3-Clause; [weight/dataset terms require separate review][tv-models]. Not a weight redistribution approval |
| EfficientNet-B0 | Challenger: [TorchVision V1][efficientnet] lists 5,288,548 parameters, 0.39 GFLOPS, 20.5 MB checkpoint; 224 crop after resize 256 with bicubic interpolation | Same separate code/weight/dataset review; no assumption that a code license grants dataset rights |
| Private library | Only consented, locally exported records; personal tags require explicit mapping | Owner permission/annotation rights must be recorded; no photo/identifier redistribution by default |
| Open Images V7 | Human-verified labels and bounded class-specific subsets | Google annotations CC BY 4.0; images listed CC BY 2.0, individually verify and retain attribution. Training and redistribution are separate rights reviews |
| RAM++, SigLIP 2, TinyCLIP | Existing local reference models; do not reuse their human-reviewed cohort as final test | See [existing model asset provenance](../../MODEL-ASSETS.md) and pinned benchmark preparation scripts; record each exact revision/notice in a run |
| Apple MobileCLIP | Excluded from this product-training proposal | [Model terms][mobileclip] exclude product development; MIT code is not permission for the weights |

Upstream checkpoint size/parameter counts are **not** measurements of our
multi-label ONNX. Prefer MobileNetV3's lower published compute as a starting
hypothesis, not proof of CPU/DirectML speed or quality. Initial export targets
remain 15-30 MB FP32 or 5-15 MB INT8; none has been achieved or measured here.
Neither ImageNet top-1 accuracy nor an upstream classifier is a home-photo
multi-label quality result.

## Training/export and evaluation gates still to implement

1. Review the pilot with diverse users; map and audit private data, verified Open
   Images subsets and cross-source duplicates. Collect independent positive
   **and reviewed negative** groups. A provisional data-collection goal is at
   least 50 positive training groups per concept, with separately reviewed
   calibration/test coverage; it is not a scientifically established minimum.
   Drop unsupported labels deliberately and version the vocabulary.
2. Clear pretrained-weight/image/annotation terms before a training run. Pin the
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
5. Export FP32 ONNX with fixed float32 NCHW input and ordered independent scores.
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
7. Measure actual Windows Release CPU and DirectML cold load, warm-up, p50/p95
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

[oi-download]: https://storage.googleapis.com/openimages/web/download_v7.html
[oi-facts]: https://storage.googleapis.com/openimages/web/factsfigures_v7.html#licenses
[mobilenet]: https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.mobilenet_v3_large.html
[efficientnet]: https://docs.pytorch.org/vision/stable/models/generated/torchvision.models.efficientnet_b0.html
[tv-models]: https://docs.pytorch.org/vision/stable/models.html
[mobileclip]: https://github.com/apple-aiml-research/ml-mobileclip/blob/main/LICENSE_MODELS
