"""Reserve public development/calibration groups before a new research training run."""

from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path

from PIL import Image, ImageOps

from pilot_data import difference_hash, load_downloads
from pilot_inference import write_json
from prepare_dataset import file_hash, vocabulary
from prepare_evaluation import filter_fresh, group_partition


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", type=Path, required=True)
    parser.add_argument("--exclude-download", type=Path, action="append", required=True)
    parser.add_argument("--regression-images", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--mapping", type=Path, default=Path(__file__).with_name("open-images-mapping.csv"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external reservation directory required")
    rows = load_downloads(args.download, "validation")
    prior = [row for path in args.exclude_download for row in load_downloads(path, "validation")]
    private = []
    for path in sorted(args.regression_images.rglob("*")):
        if path.suffix.lower() in {".jpg", ".jpeg", ".png"}:
            with Image.open(path) as image:
                private.append(difference_hash(ImageOps.exif_transpose(image)))
    if not private:
        raise ValueError("Private overlap references required")
    concepts, _ = vocabulary(args.vocabulary)
    kept, quarantine = filter_fresh(rows, prior, private, concepts)
    seed = "open-images-authors-first-v1"
    grouped = group_partition(kept, seed)
    splits = {
        name: [{"sourceId": row["sourceId"], "group": row["evaluationGroup"]}
               for row in grouped if row["evaluationSplit"] == split]
        for name, split in [("calibration", "calibration"), ("development", "test")]}
    if any(not values for values in splits.values()):
        raise ValueError("Both reserved development and calibration groups required")
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "reservation.json", {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "downloadSha256": file_hash(args.download),
        "vocabularySha256": file_hash(args.vocabulary), "mappingSha256": file_hash(args.mapping),
        "excludedDownloadSha256": [file_hash(path) for path in args.exclude_download],
        "codeSha256": file_hash(Path(__file__)), "seed": seed, "splits": splits,
        "calibrationEvidence": {"minimumPositive": 20, "minimumNegative": 10, "minimumGroupsPerState": 5},
        "quarantineCounts": dict(Counter(row["reason"] for row in quarantine)),
        "limitations": [
            "Both cohorts are from original-validation public images; prior analyst/model exposure may exist.",
            "Development is model selection, calibration is threshold fitting, neither is a blind final test.",
            "Retrain from reviewed initialization, never continue earlier photo-trained checkpoints.",
            "Exclude all downloaded reservation identities/authors/near hashes from new training.",
            "Previous evaluation selections are excluded; current 1425-photo final reserve remains unscored.",
            "Minimum counts/groups select an evidence-supported head, not a production quality guarantee.",
        ]})
    write_json(args.output / "quarantine.json", {"rows": quarantine})
    print({name: len(values) for name, values in splits.items()})


if __name__ == "__main__":
    main()
