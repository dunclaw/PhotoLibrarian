"""Freeze a public calibration/test split, excluding all prior pilot development."""

from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

from pilot_data import difference_hash, load_downloads, near_hash
from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash, fingerprint, vocabulary


def group_partition(rows: list[dict], seed: str) -> list[dict]:
    """Union author, identity and approximate-neighbor constraints before splitting."""
    parents = list(range(len(rows)))

    def find(index):
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    seen = {}
    for i, row in enumerate(rows):
        keys = [("sha", row["sha256"])]
        for field in ["AuthorProfileURL", "OriginalMD5"]:
            value = row["provenance"].get(field, "")
            if value.strip().lower() not in {"", "nan", "none"}:
                keys.append((field, value))
        for key in keys:
            if key in seen:
                parents[find(i)] = find(seen[key])
            else:
                seen[key] = i
        for j in range(i):
            if (row["dhash"] ^ rows[j]["dhash"]).bit_count() <= 6:
                parents[find(i)] = find(j)
    components = {}
    for i, row in enumerate(rows):
        components.setdefault(find(i), []).append(row)
    output = []
    for members in components.values():
        identity = fingerprint(sorted(row["sourceId"] for row in members))
        split = "calibration" if int(fingerprint([seed, identity]), 16) % 2 == 0 else "test"
        output.extend({**row, "evaluationSplit": split, "evaluationGroup": identity} for row in members)
    return sorted(output, key=lambda row: row["sourceId"])


def filter_fresh(rows: list[dict], prior: list[dict], regression_hashes: list[int],
                 concepts: list[dict]) -> tuple[list[dict], list[dict]]:
    identities = {r["sourceId"] for r in prior}
    hashes = {r["sha256"] for r in prior}
    authors = {r["provenance"].get("AuthorProfileURL") for r in prior} - {"", "nan", None}
    md5 = {r["provenance"].get("OriginalMD5") for r in prior} - {"", "nan", None}
    dhashes = [r["dhash"] for r in prior if "dhash" in r]
    hierarchy = {row["label"]: row["parent"] for row in concepts}
    duplicate_states = {}
    conflicts = set()
    for row in rows:
        for label, state in row["labels"].items():
            key = row["sha256"], label
            if key in duplicate_states and duplicate_states[key] != state:
                conflicts.add(row["sha256"])
            duplicate_states[key] = state
    kept, rejected = [], []
    seen = set()
    for row in rows:
        reason = row.get("decodeExclusion")
        if not set(row["labels"]) <= set(hierarchy) or not set(row["labels"].values()) <= {"positive", "negative"}:
            raise ValueError("Unknown public label/state")
        for label, state in row["labels"].items():
            parent = hierarchy[label]
            while state == "positive" and parent:
                if row["labels"].get(parent) == "negative":
                    reason = reason or "source-hierarchy-conflict"
                parent = hierarchy[parent]
        if row["sha256"] in conflicts:
            reason = reason or "duplicate-annotation-conflict"
        metadata = row["provenance"]
        if (row["sourceId"] in identities or row["sha256"] in hashes
                or metadata.get("OriginalMD5") in md5 or metadata.get("AuthorProfileURL") in authors):
            reason = reason or "prior-development-identity-or-author"
        if not reason and near_hash(row["dhash"], dhashes, 6):
            reason = "prior-development-near-hash"
        if not reason and near_hash(row["dhash"], regression_hashes, 6):
            reason = "private-regression-near-hash"
        if row["sha256"] in seen:
            reason = reason or "duplicate-evaluation-bytes"
        if reason:
            rejected.append({"sourceId": row["sourceId"], "reason": reason})
        else:
            kept.append(row)
            seen.add(row["sha256"])
    return kept, rejected


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", type=Path, required=True)
    parser.add_argument("--prior-run", type=Path, required=True)
    parser.add_argument("--exclude-download", type=Path, action="append", default=[],
                        help="Earlier original-validation downloads; identities/near hashes only, never outcomes")
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--regression-images", type=Path, required=True)
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external frozen evaluation directory required")
    _, bundle = load_bundle(args.bundle)
    completion = json.loads((args.prior_run / "completion.json").read_text())
    if not completion["completed"] or completion["checkpointSha256"] != bundle["checkpointSha256"]:
        raise ValueError("Prior completed run and export must match")
    prior = []
    cohorts = {}
    prior_files = [args.prior_run / f"{name}-cohort.jsonl" for name in ["train", "validation"]]
    for path in prior_files:
        records = [json.loads(line) for line in path.read_text().splitlines()]
        cohorts[path.name] = records
        prior.extend(records)
    config_path = args.prior_run / "run.json"
    config = json.loads(config_path.read_text())
    if file_hash(config_path) != bundle["runConfigSha256"]:
        raise ValueError("Prior run configuration changed")
    identity = fingerprint({
        "train": [[r["sourceId"], r["sha256"], r["labels"]] for r in cohorts["train-cohort.jsonl"]],
        "validation": [[r["sourceId"], r["sha256"], r["labels"]] for r in cohorts["validation-cohort.jsonl"]],
        "labels": bundle["labels"],
    })
    if identity != config["dataFingerprint"]:
        raise ValueError("Prior audited cohort identities changed")
    for manifest in args.exclude_download:
        prior.extend(load_downloads(manifest, "validation"))
    regression_hashes = []
    from PIL import Image, ImageOps
    for path in sorted(args.regression_images.rglob("*")):
        if path.suffix.lower() in {".jpg", ".jpeg", ".png"}:
            with Image.open(path) as image:
                regression_hashes.append(difference_hash(ImageOps.exif_transpose(image)))
    if not regression_hashes:
        raise ValueError("No private regression exclusions found")
    concepts, _ = vocabulary(args.vocabulary)
    records = load_downloads(args.download, "validation")
    kept, rejected = filter_fresh(records, prior, regression_hashes, concepts)
    rows = group_partition(kept, "open-images-independent-evaluation-v1")
    if {row["evaluationSplit"] for row in rows} != {"calibration", "test"}:
        raise ValueError("Both grouped evaluation splits are required")
    args.output.mkdir(parents=True, exist_ok=False)
    manifests = {}
    for split in ["calibration", "test"]:
        path = args.output / f"{split}.jsonl"
        path.write_text("".join(json.dumps(row, sort_keys=True) + "\n" for row in rows
                                if row["evaluationSplit"] == split), encoding="utf-8")
        manifests[split] = file_hash(path)
    protocol = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": bundle["modelSha256"], "labels": bundle["labels"],
        "manifests": manifests, "selectionSource": "additional original Open Images validation images",
        "priorCohortSha256": [file_hash(path) for path in prior_files],
        "excludedDownloadSha256": [file_hash(path) for path in args.exclude_download],
        "downloadSha256": file_hash(args.download), "vocabularySha256": file_hash(args.vocabulary),
        "codeSha256": file_hash(Path(__file__)), "seed": "open-images-independent-evaluation-v1",
        "images": dict(Counter(row["evaluationSplit"] for row in rows)),
        "groups": {split: len({r["evaluationGroup"] for r in rows if r["evaluationSplit"] == split})
                   for split in ["calibration", "test"]},
        "quarantineCounts": dict(Counter(row["reason"] for row in rejected)),
        "privateRegressionImagesChecked": len(regression_hashes),
        "calibrationPolicy": {
            "targetPrecision": 0.9, "minimumWilsonLower95": 0.75, "minimumAccepted": 20,
            "minimumKnownPositive": 10, "minimumKnownNegative": 10, "minimumGroupsPerState": 5,
            "selection": "maximize true positives, then accepted count, then threshold; insufficient evidence abstains",
        },
        "testComparisons": ["fixed 0.5 baseline", "frozen calibration-only thresholds"],
        "limitations": [
            "Public labels are partial and selection is balanced, not population-representative.",
            "Additional original validation images are held out from this pilot, not from ImageNet pretraining.",
            "Author/byte/dHash grouping is approximate, not verified event independence.",
            "Calibration targets are provisional research choices, not agreed production acceptance.",
            "Fit on calibration only, freeze thresholds, then evaluate test once without retuning.",
        ],
    }
    write_json(args.output / "quarantine.json", {"rows": rejected})
    write_json(args.output / "protocol.json", protocol)
    print(json.dumps({k: protocol[k] for k in ["images", "groups", "quarantineCounts", "calibrationPolicy"]},
                     indent=2), flush=True)


if __name__ == "__main__":
    main()
