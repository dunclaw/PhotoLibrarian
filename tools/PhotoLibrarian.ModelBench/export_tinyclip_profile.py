"""Export non-personal TinyCLIP vocabulary/threshold metadata for the app.

Does not include image paths, human annotations, or model weights. The output is
versioned with the application; rerun validation before changing the pipeline.
"""
import argparse
import hashlib
import json
from pathlib import Path


def export_profile(calibration, vocabulary, requested_precision=0.75):
    if type(requested_precision) not in (int, float) or not 0 < requested_precision <= 1:
        raise ValueError("Requested precision must be a finite number between zero and one")
    model = calibration["models"]["tinyclip"]
    metadata = model["metadata"]
    labels = vocabulary["labels"]
    fingerprint = hashlib.sha256(json.dumps(
        labels, ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
    if (metadata["provider"] != "Cpu" or metadata["scoreKind"] != "cosine"
            or metadata["precision"] != "int8" or metadata["vocabularySha256"] != fingerprint
            or len(set(labels)) != len(labels) or "beak" in labels or "bird" not in labels):
        raise ValueError("Calibration does not match the reviewed CPU INT8 vocabulary")
    operating = next((item for item in model["operatingPoints"]
                      if item["requestedPrecision"] == requested_precision), None)
    if operating is None:
        raise ValueError(f"Calibration has no operating point for requested precision {requested_precision}")
    policy = operating["experimentalFullDataFit"]
    fallback = policy["global"]["threshold"]
    if fallback is None or not 0 <= fallback <= 1:
        raise ValueError("Expected a fitted global fallback")
    overrides = {
        label: fit["threshold"] for label, fit in policy["labels"].items()
        if fit["status"] != "global_fallback_insufficient_evidence"
    }
    if (not overrides.keys() <= set(labels)
            or any(value is not None and not 0 <= value <= 1 for value in overrides.values())):
        raise ValueError("Invalid per-label cutoff")
    profile = {
        "modelId": "tinyclip", "provider": "Cpu", "scoreKind": "cosine", "precision": "int8",
        "modelSha256": metadata["modelSha256"], "vocabularySha256": fingerprint,
        "requestedPrecision": requested_precision, "candidateLimit": 10, "fallbackThreshold": fallback,
        "thresholds": overrides, "labels": labels,
        "warning": "Experimental full-data fit; not a probability or guarantee. Only calibrated among the top ten candidates.",
    }
    if "rankingQuality" in model:
        quality = model["rankingQuality"]
        profile["evidence"] = {
            key: quality[key] for key in ("reviewedCandidates", "pendingCandidates", "fullyReviewedRows")
        }
        if any(type(value) is not int or value < 0 for value in profile["evidence"].values()):
            raise ValueError("Invalid calibration evidence counts")
        profile["evidence"]["warning"] = (
            "Cutoffs fit only reviewed candidates. Unreviewed labels use the provisional global fallback; "
            "partial-review estimates do not establish accuracy for the full vocabulary."
        )
    return profile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--calibration", required=True, type=Path)
    parser.add_argument("--vocabulary", required=True, type=Path)
    parser.add_argument("--target-precision", type=float, default=0.75,
                        help="Existing fitted operating point to export; not a guaranteed accuracy")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    value = export_profile(
        json.loads(args.calibration.read_text(encoding="utf-8-sig")),
        json.loads(args.vocabulary.read_text(encoding="utf-8-sig")),
        args.target_precision)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n")


if __name__ == "__main__":
    main()
