"""Audit the full public validation annotation pool without downloading or scoring photos."""

from __future__ import annotations

import argparse
from collections import Counter
import csv
import json
import math
from pathlib import Path

from prepare_dataset import file_hash, vocabulary
from select_open_images import (
    eligible_provenance, excluded_provenance, mapping, prior_identities,
    provenance_rows, target_states, verified_rows,
)


STAGES = ["eligible", "training_disjoint", "development_disjoint", "unused"]


def audit(annotations: Path, metadata: Path, mappings: list[dict], concepts: list[dict],
          exclusions: list[dict[str, set[str]]]) -> tuple[list[dict], dict]:
    if len(exclusions) != 3:
        raise ValueError("Training, development and reserved exclusion tiers required")
    states = {}
    mids = {mid for row in mappings for mid in row["midList"]}
    for image, mid, state in verified_rows(annotations, mids):
        source = states.setdefault(image, {})
        if mid in source and source[mid] != state:
            raise ValueError("Conflicting verified source states")
        source[mid] = state
    parents = {row["label"]: row["parent"] for row in concepts}
    counts = Counter()
    authors = {}
    missing_authors = Counter()
    reasons = Counter()
    images = Counter()
    found = set()
    for row in provenance_rows(metadata, "validation"):
        identity = row["ImageID"]
        if identity not in states:
            continue
        if identity in found:
            raise ValueError("Duplicate validation metadata identity")
        found.add(identity)
        if not eligible_provenance(row):
            reasons["ineligible-provenance"] += 1
            continue
        try:
            rotation = float(row.get("Rotation", "nan"))
        except ValueError:
            rotation = float("nan")
        if not math.isfinite(rotation) or rotation not in {0, 90, 180, 270}:
            reasons["unknown-or-unsupported-rotation"] += 1
            continue
        labels = target_states(states[identity], mappings)
        if not labels:
            reasons["no-transferable-labels"] += 1
            continue
        conflict = False
        for label, state in labels.items():
            parent = parents[label]
            while state == "positive" and parent:
                conflict |= labels.get(parent) == "negative"
                parent = parents[parent]
        if conflict:
            reasons["hierarchy-conflict"] += 1
            continue
        author = row.get("AuthorProfileURL", "")
        has_author = author.strip().lower() not in {"", "nan", "none"}
        for i, stage in enumerate(STAGES):
            if i and excluded_provenance(row, exclusions[i - 1]):
                reasons[f"blocked-at-{stage}"] += 1
                break
            images[stage] += 1
            for label, state in labels.items():
                key = stage, label, state
                counts[key] += 1
                if has_author:
                    authors.setdefault(key, set()).add(author)
                else:
                    missing_authors[key] += 1
    rows = []
    for item in mappings:
        result = {"label": item["label"], "policy": item["negativePolicy"]}
        for stage in STAGES:
            for state in ["positive", "negative"]:
                key = stage, item["label"], state
                result[f"{stage}_{state}"] = counts[key]
                result[f"{stage}_{state}_authors"] = len(authors.get(key, set()))
                result[f"{stage}_{state}_missing_author"] = missing_authors[key]
        rows.append(result)
    reasons["metadata-unavailable"] = len(states) - len(found)
    return rows, {"annotatedImages": len(states), "images": dict(images),
                  "excludedAtFirstMatchingTier": dict(reasons)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--annotations", type=Path, required=True)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--classes", type=Path, required=True)
    parser.add_argument("--mapping", type=Path, default=Path(__file__).with_name("open-images-mapping.csv"))
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--training-selection", type=Path, action="append", required=True)
    parser.add_argument("--development-selection", type=Path, action="append", required=True)
    parser.add_argument("--reserved-selection", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external metadata-only report directory required")
    concepts, _ = vocabulary(args.vocabulary)
    mappings = mapping(args.mapping, args.classes, concepts)
    tiers = [args.training_selection, args.development_selection, args.reserved_selection]
    rows, report = audit(args.annotations, args.metadata, mappings, concepts,
                         [prior_identities(paths) for paths in tiers])
    report.update({
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "stages": STAGES, "coverage": rows,
        "inputs": {name: file_hash(path) for name, path in [
            ("annotations", args.annotations), ("metadata", args.metadata), ("classes", args.classes),
            ("mapping", args.mapping), ("vocabulary", args.vocabulary)]},
        "exclusionSha256": [[file_hash(path) for path in paths] for paths in tiers],
        "codeSha256": file_hash(Path(__file__)),
        "limitations": [
            "Full original-validation annotation scan; no per-state sampling cap, downloads or inference.",
            "Counts apply to exact mapped known human states, eligible provenance, rotation and hierarchy only.",
            "Distinct author URLs are not certified independent events; unknown authors counted separately.",
            "Exclusions use entire selected cohorts conservatively, including images not accepted for training.",
            "No decode, byte-duplicate or dHash audit: metadata counts are upper bounds on usable photos.",
            "Eligibility metadata does not establish current image rights; no shipping approval.",
            "Reserved selections contribute only exclusion identities, never model scores or outcomes.",
        ],
    })
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "coverage.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n")
    with (args.output / "coverage.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(json.dumps({key: report[key] for key in ["annotatedImages", "images",
                                                 "excludedAtFirstMatchingTier"]}, indent=2))


if __name__ == "__main__":
    main()
