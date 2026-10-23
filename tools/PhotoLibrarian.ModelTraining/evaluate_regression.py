"""Reuse explicit existing judgments, never missing-label negatives or new human ratings."""

from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path

import numpy as np

from pilot_inference import write_json
from prepare_dataset import file_hash, unique_object


def reviewed_pairs(review: dict, labels: list[str]) -> tuple[dict, dict]:
    if review["schemaVersion"] != 1:
        raise ValueError("Unsupported review schema")
    photos = {}
    for photo in review["photos"]:
        if photo["id"] in photos or photo["image"] in photos.values():
            raise ValueError("Duplicate reviewed photo")
        photos[photo["id"]] = photo["image"]
    decisions = {}
    ids = set()
    states = Counter()
    for item in review["items"]:
        if item["id"] in ids or item["photo"] not in photos:
            raise ValueError("Duplicate item or unknown photo in review")
        ids.add(item["id"])
        decision = item["initialDecision"]
        if decision is None:
            decision = "conflict" if item.get("reason") == "conflict" else "pending"
        if decision not in {"correct", "incorrect", "pending", "conflict"}:
            raise ValueError("Unknown review decision")
        states[decision] += 1
        if decision in {"pending", "conflict"}:
            continue
        if item["label"] not in labels:
            continue
        key = photos[item["photo"]], item["label"]
        if key in decisions:
            raise ValueError("Duplicate/conflicting reviewed photo-label pair")
        decisions[key] = decision == "correct"
    if not decisions:
        raise ValueError("No exact canonical reviewed pairs for this head")
    return decisions, {"photos": len(photos), "items": len(ids), "states": dict(states),
                       "sharedReviewedPairs": len(decisions),
                       "sharedReviewedLabels": len({label for _, label in decisions})}


def summarize(decisions: dict, emitted: set, labels: list[str]) -> dict:
    rows = []
    for label in labels:
        pairs = {key: value for key, value in decisions.items() if key[1] == label}
        if not pairs:
            continue
        tp = sum(value and key in emitted for key, value in pairs.items())
        fp = sum(not value and key in emitted for key, value in pairs.items())
        fn = sum(value and key not in emitted for key, value in pairs.items())
        rows.append({"label": label, "knownPositive": sum(pairs.values()),
                     "knownNegative": sum(not value for value in pairs.values()),
                     "tp": tp, "fp": fp, "fn": fn,
                     "precision": tp / (tp + fp) if tp + fp else None,
                     "recall": tp / (tp + fn) if tp + fn else None})
    tp, fp, fn = (sum(row[key] for row in rows) for key in ["tp", "fp", "fn"])
    return {"knownEntries": len(decisions), "tp": tp, "fp": fp, "fn": fn,
            "microPrecision": tp / (tp + fp) if tp + fp else None,
            "microRecall": tp / (tp + fn) if tp + fn else None,
            "emittedPairs": len(emitted), "reviewedEmittedPairs": tp + fp,
            "unreviewedEmittedPairs": len(emitted - decisions.keys()), "perLabel": rows}


def load_scores(folder: Path):
    report = json.loads((folder / "benchmark.json").read_text(encoding="utf-8"),
                        object_pairs_hook=unique_object)
    if file_hash(folder / "scores.npz") != report["scoresSha256"]:
        raise ValueError("Benchmark scores changed")
    with np.load(folder / "scores.npz") as data:
        scores = data["scores"].copy()
    if (scores.shape != (len(report["images"]), len(report["labels"]))
            or not np.isfinite(scores).all() or not ((scores >= 0) & (scores <= 1)).all()
            or len({row["image"] for row in report["images"]}) != len(report["images"])):
        raise ValueError("Invalid score arrays or duplicate images")
    return report, scores


def compare_providers(reference: dict, scores: np.ndarray, other: dict, other_scores: np.ndarray) -> dict:
    if (reference["modelSha256"] != other["modelSha256"] or reference["labels"] != other["labels"]
            or reference["images"] != other["images"]):
        raise ValueError("Provider comparison requires identical model, ordered labels and image hashes")
    return {"referenceProvider": reference["requestedProvider"], "otherProvider": other["requestedProvider"],
            "maxAbsoluteError": float(np.max(np.abs(scores - other_scores))),
            "cutoff05Flips": int(((scores >= 0.5) != (other_scores >= 0.5)).sum()),
            "entries": int(scores.size)}


def baseline_emissions(review: dict, labels: list[str]) -> dict[str, set]:
    items = {item["id"]: item for item in review["items"]}
    result = {}
    seen = set()
    for row in review["rows"]:
        key = row["image"], row["model"]
        if key in seen:
            raise ValueError("Duplicate baseline image/model row")
        seen.add(key)
        emitted = result.setdefault(row["model"], set())
        for prediction in row["predictions"]:
            item = items[prediction["item"]]
            photo = next(photo for photo in review["photos"] if photo["id"] == item["photo"])
            if photo["image"] != row["image"]:
                raise ValueError("Baseline prediction refers to the wrong reviewed photo")
            if item["label"] in labels:
                emitted.add((row["image"], item["label"]))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--benchmark", type=Path, required=True)
    parser.add_argument("--compare-provider", type=Path)
    parser.add_argument("--review", type=Path, required=True, help="Existing review-items.json; never rewritten")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external output directory required")
    report, scores = load_scores(args.benchmark)
    review = json.loads(args.review.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)
    labels = report["labels"]
    decisions, coverage = reviewed_pairs(review, labels)
    image_names = [row["image"] for row in report["images"]]
    if {photo["image"] for photo in review["photos"]} != set(image_names):
        raise ValueError("Benchmark must exactly cover the reviewed photos")
    emitted = {(image, label) for i, image in enumerate(image_names)
               for j, label in enumerate(labels) if scores[i, j] >= 0.5}
    result = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": report["modelSha256"], "reviewSha256": file_hash(args.review),
        "benchmarkSha256": file_hash(args.benchmark / "benchmark.json"),
        "codeSha256": file_hash(Path(__file__)),
        "purpose": "previously reviewed development regression only; not independent calibration/test",
        "mapping": "exact canonical pilot labels only; no inferred ancestors or broader alias negatives",
        "coverage": coverage, "pilotThreshold": 0.5, "thresholdCalibrated": False,
        "pilot": summarize(decisions, emitted, labels),
        "historicalDisplayedOutputs": {model: summarize(decisions, pairs, labels)
                                       for model, pairs in baseline_emissions(review, labels).items()},
        "limitations": [
            "Reviewed candidates were selected by prior models, not exhaustive/random label annotation.",
            "Missing reviews stay unknown, so precision/recall only describe this reviewed pair pool.",
            "Historical outputs have their original top-K/vocabulary/thresholds; not a controlled leaderboard.",
            "No SigLIP 2 outputs or new whole-photo quality ratings are present in this review artifact.",
            "Do not tune thresholds here or claim generalization from photos already used in prior development.",
            "Review photo names match current files, but this artifact has no historical photo byte hashes.",
        ],
    }
    if args.compare_provider:
        other, other_scores = load_scores(args.compare_provider)
        result["providerParity"] = compare_providers(report, scores, other, other_scores)
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "regression.json", result)
    print(json.dumps({"coverage": coverage, "pilot": {key: value for key, value in result["pilot"].items()
                                                    if key != "perLabel"},
                      "providerParity": result.get("providerParity")}, indent=2))


if __name__ == "__main__":
    main()
