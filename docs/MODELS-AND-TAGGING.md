# First run, automatic tagging and model comparison

First-run setup plus automatic tagging and model comparison details.

### First run

1. Click the gear icon → **Manage folders** and add the root(s) you want to index.
2. The background indexer populates the cache; the grid starts filling as soon as the first folder's metadata is ready.
3. Automatic content tagging is off by default. The recommended RAM++ profile
   requires `ram_plus_swin_large_14m.onnx` and
   `ram_plus_swin_large_14m_labels.txt`. Settings supports version-pinned,
   verified downloads for RAM++ and TinyCLIP. Settings links to
   [compatible model setup instructions](../MODEL-ASSETS.md)
   and each upstream model/license page. **Import model assets** remains
   available offline to verify and copy
   those exact files into managed local storage. Source files are never
   modified. Review the profile information, then explicitly trust it before
   enabling background processing.
   RAM++ uses a general-purpose eligibility policy derived from its complete
   vocabulary, rather than the labels present in a small test set. Useful
   subjects such as boats are available again; non-visual concepts, ambiguous
   terms, sensitive personal inferences and unhelpful fragments are excluded
   from automatic predictions, not erased from the taxonomy.

   The pinned RAM++ export returns a **binary detected/not-detected mask** in
   its `targets` output, not confidence probabilities. Its decision cutoffs are
   baked into the model, so Settings does not offer an adjustable confidence
   slider. Reports show **detected**, and inactive classes are always omitted:
   requesting ten tags can correctly return fewer than ten. Active detections
   tie, so a tag cap uses stable source-label order, not a confidence ranking.
   Earlier standalone
   reports incorrectly padded these results with zero-score classes when showing
   below-cutoff candidates; those entries were not RAM++ detections.

### Lightweight tagging with TinyCLIP

Settings also offers **TinyCLIP (lightweight, experimental)** beside RAM++.
It uses approximately **99 MB** of runtime assets: the unchanged 40.6 MB image
encoder plus embeddings for **3,632 general-purpose concepts**. TinyCLIP has no
fixed native class vocabulary; these concepts are supplied to its image-text
matching pipeline. The earlier 247-label test vocabulary remains available as
a historical benchmark fixture, not the application's current vocabulary.
RAM++ remains the default selection and
existing saved profiles are preserved. Selecting TinyCLIP does not enable or
approve it.

Prepare the exact tested assets outside the repository:

```powershell
.\.venv-modelbench\Scripts\python.exe tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\TinyCLIP `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.general-v2.json `
  --models tinyclip --precision int8
```

See the preparation environment instructions below if needed. In Settings,
select TinyCLIP, then **Import model assets** and choose the folder containing
`tinyclip-vision-int8.onnx` and `tinyclip.json`. The broader profile requires the
General V2 embedding bundle; the earlier General V1 and Review V3 JSON files will not match.
Both files must match pinned checksums. Imports copy, never modify, the source;
the same managed storage-location and cleanup controls apply as for RAM++.
Weights are not distributed with the app. The upstream TinyCLIP model uses
the MIT license; preparation records the original model and ONNX conversion sources.

Review the profile information before choosing **Trust and enable selected
profile**. TinyCLIP always uses **CPU execution**, matching
the calibration rather than the numerically different DirectML path. The app
and comparison utility share bicubic center-crop preprocessing and independent
cosine scoring. Cosine scores are not confidence percentages, so the global
confidence control is presented as a **minimum cosine similarity** cutoff. The
broader calibrated default is `30.33498%`. Lowering it increases suggestions;
raising it returns fewer, stronger matches. A user adjustment shifts both the
shared cutoff and supported per-label adjustments by the same amount, preserving
their calibrated relative difference. Changing it clears approval and requires
another benchmark. The profile first
ranks the top ten candidates, then applies the versioned cutoff policy,
and returns up to the selected maximum (eight by default). It does not backfill
from lower ranks that were not calibrated.

**Expanded-vocabulary calibration is provisional.** The completed review covers
all 1,560 displayed TinyCLIP predictions across 156 photos. The current
broader-coverage profile uses the fitted 60% requested-precision operating point,
with a shared fallback of `0.3033498` (previously `0.3160873`) and the unchanged
`wildlife` override of `0.22960028`. These are cosine cutoffs, not probabilities.
At the default eight-tag maximum, this increases coverage on that library from
50 to 86 of 156 photos; 99 of 156 returned suggestions match the prior approvals,
versus 61 of 77 under the stricter policy. This deliberately trades some precision
for more tagged photos. The requested 60% target is not a guarantee; image-group
held-out calibration measured about 60% precision and 48% coverage before the
runtime tag cap. This small-library evidence does not establish accuracy for the
full vocabulary or unseen libraries.
Pending predictions are never counted as rejected or validated. The app never
preapproves the model. The checked-in calibration resource contains only labels,
thresholds, aggregate evidence counts and model/vocabulary identities, not private
photos or annotation rows. `export_tinyclip_profile.py --target-precision 0.6`
reproduces the current policy from the experimental calibration report; the
exporter's default remains 0.75 for reproducing earlier policies.
Changing vocabulary, embeddings, thresholds or preprocessing requires a new
pipeline version and renewed approval.
This cutoff-only revision changes only TinyCLIP's approval identity. RAM++,
the hierarchy, and imported model/embedding files are unchanged; no asset
reimport is needed.

The benchmark is a developer diagnostic rather than part of ordinary setup.
With Settings open, press **Ctrl+Shift+B** to reveal or hide it.

### Hierarchical automatic tags

RAM++ and TinyCLIP share a versioned hierarchy of **4,657 entries**, covering all
**4,585 RAM++ output labels**, the supplied TinyCLIP concepts and parent nodes,
including labels excluded from automatic inference. The shared candidate vocabulary
contains 3,632 canonical concepts; 3,661 raw RAM++ labels map into it. This taxonomy
is semantically curated independently of which concepts occurred in test photos.
Generated tags remain under the separate **Auto** branch:
`eagle` becomes `Auto/animal/bird/eagle`, `bald eagle` becomes
`Auto/animal/bird/eagle/bald eagle`, and `bird` becomes `Auto/animal/bird`.
A broad prediction never invents a more specific species. Genuine synonyms
share a canonical path, while ambiguous labels are not forced into a misleading
subtype.

The General V2 revision excludes `3d glasses`, `abacus`, and the named landmark
`eiffel tower`. Breed names are explicit: `newfoundland dog`, `labrador retriever`,
and `chihuahua dog`, not geographic-location tags. `autumn leave` becomes
`autumn leaves`. The raw RAM++ names remain mapped aliases. `abalone` and
`monastery` remain eligible: their frequent appearances in the earlier RAM++
review were inactive zero-score padding, not repeated positive detections.

Hierarchy mapping happens **after** each model's eligibility filters,
thresholds and candidate limit. The full-vocabulary expansion regenerates
TinyCLIP text embeddings and recalibrates its cutoff without changing the image
encoder or preprocessing. The in-app benchmark shows hierarchical paths.
Standalone raw-label reports remain unchanged; `--labels mapped` can use the
generated RAM++ mapping to exclude ineligible outputs and merge synonyms before
selecting its top candidates. Review transfer uses exact photo/label judgments,
not inferred judgments for new species or arbitrary synonyms.

Storage indexes every ancestor, so selecting `Auto/animal` or `Auto/animal/bird`
finds a photo tagged only with an eagle prediction. Ancestors are deduplicated
per photo and inherit the strongest contributing score, not a separately
measured confidence. Manual and imported tags, including any existing paths
inside `Auto`, are preserved.

This changes the RAM++ and TinyCLIP pipeline versions. Benchmark and approve
the updated profile again; its next background pass replaces old flat generated
tags with hierarchical tags. Existing tags are not rewritten merely by opening
Settings. The portable curation source is
`tools\PhotoLibrarian.ModelBench\full-model-taxonomy.v2.json`: every source label
has an explicit parent or canonical synonym, an automatic-eligibility decision,
and an explanation if excluded. It records the pinned RAM++ label source hash.
The compiler validates complete coverage, relationships, cycles and stable
ancestors before generating runtime resources:

```powershell
python tools\PhotoLibrarian.ModelBench\build_general_vocabulary.py `
  --taxonomy tools\PhotoLibrarian.ModelBench\full-model-taxonomy.v2.json `
  --overrides tools\PhotoLibrarian.ModelBench\general-v2-overrides.json `
  --hierarchy src\PhotoLibrarian.ML\Assets\auto-tag-hierarchy-v3.json `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.general-v2.json `
  --profile src\PhotoLibrarian.ML\Assets\general-photo-profile-v2.json `
  --ram-mapping D:\Models\ram_plus_swin_large_14m_labels_Mapping.txt
```

Add `--check` (and omit `--ram-mapping` if not generated locally) to verify
reproducibility without writing. New vocabulary or taxonomy revisions require
new prepared assets, validation and pipeline-version/approval changes.
The override file also declares the sole permitted historical-review spelling
alias, `autumn leave` to `autumn leaves`. Pass it as `review_changes.py
--label-aliases` to reuse that judgment; breed disambiguations receive no
implicit judgment transfer. Alias provenance participates in the review
fingerprint, and conflicting merged judgments still require review.


## Compare tagging models

For the separate purpose-trained compact-model workstream, see the
[compact-model research pipeline](../tools/PhotoLibrarian.ModelTraining/README.md).
It includes pinned Open Images acquisition, partial-label-aware balanced
selection, overlap/label audits, staged MobileNetV3 training and verified
FP32/INT8 ONNX export. Training and exports are local research only, with
unresolved pretrained-weight rights and no production approval. No model
weights are included and the shipping profiles are unchanged.
The research results now include frozen public calibration/held-out testing,
reuse of existing private review judgments, and measured CPU/DirectML node
placement, timing and host memory. A matched 1,000-photo development comparison
against RAM++, TinyCLIP and SigLIP 2 separates native cutoffs from ranking and
asset/latency budgets without reopening the consumed test. Precision/breadth
limitations still prevent choosing this pilot as a shipping default.

`PhotoLibrarian.ModelBench` evaluates the discussed EfficientNet, YOLOv8
classification/detection, YOLOv10 detection, JoyTag, and RAM++ ONNX models
against the same controlled image set. It does not write tags or modify source
photos. Model weights are not included in this repository.
SigLIP 2 Base and TinyCLIP ViT-40M/32 can also be compared using prepared
image-only encoders and a shared, provisional home-photo vocabulary.

```powershell
dotnet run --project tools\PhotoLibrarian.ModelBench -p:Platform=x64 -c Release -- `
  --images D:\Photos\ModelComparison `
  --models-dir D:\Models `
  --output D:\ModelComparisonResults `
  --models all `
  --provider directml
```

The output directory contains:

- `report.html` — thumbnails beside every model's predictions, editable 0–5
  quality scores, per-prediction correctness checkboxes, and an **Export scored
  CSV** button.
- `scorecard.csv` — one editable row per image/model.
- `predictions.csv` — confidence and optional detection box for every result.
- `summary.csv` — model load and average preprocessing/inference times.
- `results.json` — complete machine-readable results.

Expected filenames in `--models-dir`:

| Model ID | Model file | External labels when required |
|---|---|---|
| `efficientnet` | `efficientnet-lite4-11.onnx` | `efficientnet-lite4-11_labels.txt` |
| `yolov8-cls` | `yolov8x-cls.onnx` | Labels are read from ONNX metadata |
| `yolov8-detect` | `yolov8x.onnx` | Labels are read from ONNX metadata |
| `yolov10-detect` | `yolov10x.onnx` | Labels are read from ONNX metadata |
| `joytag` | `joytagmodel.onnx` | `joytagmodel_labels.txt` |
| `ram-plus` | `ram_plus_swin_large_14m.onnx` | `ram_plus_swin_large_14m_labels.txt` |
| `siglip2` | Image encoder named in `siglip2.json` | Prepared vocabulary embeddings in `siglip2.json` |
| `tinyclip` | Image encoder named in `tinyclip.json` | Prepared vocabulary embeddings in `tinyclip.json` |

Raw labels are used by default so unsuitable vocabulary is visible during
evaluation. Use `--labels mapped` to load matching
`*_labels_Mapping.txt` files when present. Run with `--help` for threshold,
image-limit, warm-up, and model-selection options.

### Lightweight image-text comparison

`prepare_zero_shot.py` downloads pinned ONNX assets and computes text
embeddings once. It does not upload photos, train a model, or modify the app's
selected production profile. Python is needed only for preparation; subsequent
comparison runs use the C# ONNX runner without a text encoder.
Runtime assets are the image-only ONNX file and its JSON embedding bundle.
The preparation `source-cache` also contains text encoders and original models;
its larger disk usage is not the runtime deployment size.

```powershell
python -m venv .venv-modelbench
.\.venv-modelbench\Scripts\python.exe -m pip install -r tools\PhotoLibrarian.ModelBench\requirements-prepare.txt
.\.venv-modelbench\Scripts\python.exe tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\Lightweight --models siglip2,tinyclip --precision int8

dotnet run --project tools\PhotoLibrarian.ModelBench -p:Platform=x64 -c Release -- `
  --images D:\Photos\ModelComparison --models-dir D:\Models\Lightweight `
  --output D:\ModelComparisonResults\Lightweight-CPU `
  --models siglip2,tinyclip --provider cpu --limit 500
```

Repeat with `--provider directml` and a **different output folder**. To include
RAM++ as a fresh reference, supply its model and labels in the same asset folder
and add `ram-plus` to `--models`. Keep prior scored reports separate.
Use **Release** builds for timings: the matching antialiased resize includes
managed loops that are substantially slower in Debug.

Both small models score the same labels from `home-photo-vocabulary.json`.
This is an experimental general-content list, not a finalized product taxonomy
or a claim that all labels have been validated. RAM++ retains its own much
larger vocabulary, so coverage differs. SigLIP uses independent sigmoid scores
with the learned scale/bias; TinyCLIP uses normalized cosine similarity without
softmax. Their default cutoffs (0.10 and 0.25 respectively) are **provisional,
not calibrated confidence probabilities**. Ranked candidates below the cutoff
remain visible for review. `--labels raw|mapped` applies to the older models;
the prepared image-text vocabulary is fixed at preparation time.

Input resizing matches the reference processors' antialiased bilinear/bicubic
byte-image behavior, including shortest-edge resize and center crop for
TinyCLIP. An explicit test checks synthetic tensors and CPU scores against
`reference.json` from preparation; set `MODELBENCH_ASSETS` and run
`ZeroShotModelTests` with the xUnit runner's `-explicit on` to include it.

Reports record model/bundle bytes, preprocessing, inference, postprocessing,
total per-photo time, score type, and cutoff. The provider column denotes the
requested ONNX execution provider; DirectML may execute unsupported nodes on
CPU. Compare actual timings instead of assuming INT8 is faster on a GPU.
Quantized scores and rankings can differ between CPU and DirectML, particularly
near a cutoff; use the CPU report as the reference for quality scoring.
Neither a successful run nor high match scores approve a production profile.

### Calibrating reviewed suggestions

Use the report's **Export scored CSV** after rating photos and checking correct
suggestions. The standard-library Python tool joins those ranks to the original
saved scores; inference does not need to run again:

```powershell
python tools\PhotoLibrarian.ModelBench\calibrate_scores.py `
  --results D:\ModelComparisonResults\Lightweight-CPU\results.json `
  --scores D:\ModelComparisonResults\Lightweight-CPU\human-scores.csv `
  --output D:\ModelComparisonResults\Calibration
```

The output directory must be new. `calibration.html` and `calibration.json`
compare the original cutoff with experimental global and per-label thresholds
at several precision targets. CSV files contain the threshold sweep, per-label
evidence, and held-out acceptance decisions. In the original five-column score
format, explicitly rated rows review all displayed predictions: unchecked tags
are negatives, but unrated rows and unseen labels are not. Incremental exports
add `ReviewedPredictionRanks`: only those ranks supply evidence, independently
of the optional overall quality rating. Correct ranks must be a subset of the
reviewed ranks; all other candidates stay unknown. "Checked-candidate retention"
is not recall over everything visible in a photo.
Conflicting judgments of the same image/label across models are exported to
`annotation-conflicts.csv`; model-specific marks are preserved, not silently
merged into new ground truth.

Thresholds are evaluated in five image-group folds, with each fold's thresholds
fitted without its held-out photos. Supply `--groups D:\ReviewGroups.csv` with
`Image,Group` columns to keep known events or near-duplicates together.
Otherwise only identical image paths are grouped; similar subjects can still
leak across folds. Reported precision targets are fitting goals, not guarantees.
Full-data fitted profiles are experimental artifacts, separate from held-out
metrics; neither changes the app or certifies general-purpose automatic tagging.
Labels with little evidence use the global fallback rather than being removed
from the vocabulary.

`home-photo-vocabulary.review-v2.json` is a separate review-informed experiment.
It preserves the original 234 concepts and adds 14 missing object/scene concepts
motivated by checked RAM++ tags, including water, sky, person, building, and fog.
These additions require fresh zero-shot review: existing positive examples do
not establish their accuracy across other photos. Do not reuse calibration metrics as an
accuracy claim for this expanded vocabulary or transfer thresholds between
providers/model versions without validation.

```powershell
.\.venv-modelbench\Scripts\python.exe tools\PhotoLibrarian.ModelBench\prepare_zero_shot.py `
  --output D:\Models\Lightweight-ReviewV2 `
  --source-cache D:\Models\Lightweight\source-cache `
  --vocabulary tools\PhotoLibrarian.ModelBench\home-photo-vocabulary.review-v2.json `
  --models siglip2,tinyclip --precision int8
```

`--source-cache` reuses checksum-verified source downloads without replacing the
original bundles. Run the benchmark with the new asset directory and a fresh
report directory. Its cutoff markers remain provisional; review the full ranked
suggestions, not only those above the cutoff.

The subsequent `home-photo-vocabulary.review-v3.json` preserves V2 except for
removing `beak`, which review found unhelpful for home-library search compared
with `bird`. This is a usefulness exclusion, not a rewrite of historical
correctness marks or a rule that turns beak predictions into bird predictions.
Use the V3 file with a new preparation directory for the 247-label variant;
V2 assets and completed reviews remain reproducible.

### Reviewing only new or changed tags

Reuse prior human judgments without rerunning models or rescoring every card:

```powershell
python tools\PhotoLibrarian.ModelBench\review_changes.py `
  --results D:\ModelComparisonResults\Lightweight-ReviewV2\results.json `
  --prior-results D:\ModelComparisonResults\Lightweight-CPU\results.json `
  --prior-scores D:\ModelComparisonResults\Lightweight-CPU\human-scores.csv `
  --output D:\ModelComparisonResults\ReviewChanges
```

The target report's cached `thumbnails` folder must still exist. The new
`report.html` shows pending tags one photo at a time, hiding previous approvals
and rejections by default. A photo/tag shared by models needs only one decision.
Matching uses the exact label and absolute photo path (and recorded image hash,
when present), not the rank, score, model, or a guessed synonym. Older results
have no photo hashes, so path-based reuse assumes photos have not been replaced.
Conflicting judgments remain pending rather than being decided by majority vote.
**Show reviewed tags and photos** lets you inspect or correct inherited decisions.

Repeat `--prior-results` and `--prior-scores` in matching order, oldest to newest,
to include further review history. A newer explicit photo/tag judgment supersedes
older judgments; pending or absent judgments do not. An exported delta review
can be paired with its copied `results.json` as history for the next iteration.

The initial `human-scores.csv` already includes all reused tag decisions.
Browser edits persist locally when available; use **Export scored CSV** to save
the current state as `human-scores-delta.csv`. Browser export does not overwrite
the initial file on disk. **Not sure** leaves a tag unknown, not rejected.
Overall model quality scores are not invented or copied. Both the prefilled
and browser-exported CSVs work with `calibrate_scores.py`, including partial
reviews. Earlier reports, photos, and original score files are unchanged.

Run focused offline tests with
`python -B -m unittest discover -s tools\PhotoLibrarian.ModelBench -p test_review_changes.py`.
Optional interactive tests use installed Microsoft Edge and Python Playwright;
set `MODELBENCH_BROWSER_TESTS=1` to exercise pending-only presentation, shared
decisions, persistence, and export/import roundtrips in an isolated headless browser.

---
