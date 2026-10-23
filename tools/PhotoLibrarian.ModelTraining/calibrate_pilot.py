"""Fit on frozen public calibration data, then score held-out test without retuning."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
import onnxruntime as ort

from pilot_inference import infer, load_bundle, prepare_tensor, validate_session, write_json
from prepare_dataset import file_hash, fingerprint


def wilson(success: int, count: int) -> list[float] | None:
    if count == 0:
        return None
    p = success / count
    z = 1.959963984540054
    denominator = 1 + z * z / count
    middle = (p + z * z / (2 * count)) / denominator
    delta = z * math.sqrt(p * (1 - p) / count + z * z / (4 * count * count)) / denominator
    return [max(0.0, middle - delta), min(1.0, middle + delta)]


def choose_threshold(scores: np.ndarray, targets: np.ndarray, groups: list[str], policy: dict) -> dict:
    known = targets >= 0
    positive, negative = targets == 1, targets == 0
    counts = {"knownPositive": int(positive.sum()), "knownNegative": int(negative.sum()),
              "positiveGroups": len({g for g, state in zip(groups, positive) if state}),
              "negativeGroups": len({g for g, state in zip(groups, negative) if state})}
    if (counts["knownPositive"] < policy["minimumKnownPositive"]
            or counts["knownNegative"] < policy["minimumKnownNegative"]
            or min(counts["positiveGroups"], counts["negativeGroups"]) < policy["minimumGroupsPerState"]):
        return {**counts, "threshold": None, "reason": "insufficient-known-state-or-group-evidence"}
    choices = []
    for threshold in np.unique(scores[known]):
        accepted = known & (scores >= threshold)
        n = int(accepted.sum())
        tp = int((accepted & positive).sum())
        if n < policy["minimumAccepted"]:
            continue
        interval = wilson(tp, n)
        if tp / n >= policy["targetPrecision"] and interval[0] >= policy["minimumWilsonLower95"]:
            choices.append((tp, n, float(threshold)))
    if not choices:
        return {**counts, "threshold": None, "reason": "no-threshold-meets-research-precision-policy"}
    tp, n, threshold = max(choices)
    return {**counts, "threshold": threshold, "acceptedKnownEntries": n, "tp": tp,
            "precision": tp / n, "descriptiveWilson95": wilson(tp, n), "reason": "enabled"}


def summarize(scores: np.ndarray, targets: np.ndarray, labels: list[str],
              thresholds: list[float | None]) -> dict:
    rows = []
    all_emitted = reviewed_emitted = 0
    any_tag = np.zeros(len(scores), dtype=bool)
    for i, (label, threshold) in enumerate(zip(labels, thresholds, strict=True)):
        predicted = np.zeros(len(scores), dtype=bool) if threshold is None else scores[:, i] >= threshold
        known = targets[:, i] >= 0
        actual = targets[:, i] == 1
        tp = int((predicted & actual).sum())
        fp = int((predicted & known & ~actual).sum())
        fn = int((~predicted & actual).sum())
        all_emitted += int(predicted.sum())
        reviewed_emitted += tp + fp
        any_tag |= predicted
        rows.append({"label": label, "threshold": threshold, "tp": tp, "fp": fp, "fn": fn,
                     "knownPositive": int(actual.sum()), "knownNegative": int((targets[:, i] == 0).sum()),
                     "precision": tp / (tp + fp) if tp + fp else None,
                     "recall": tp / (tp + fn) if tp + fn else None,
                     "descriptivePrecisionWilson95": wilson(tp, tp + fp)})
    tp, fp, fn = (sum(row[key] for row in rows) for key in ["tp", "fp", "fn"])
    return {"images": len(scores), "knownEntries": int((targets >= 0).sum()),
            "enabledLabels": sum(t is not None for t in thresholds),
            "tp": tp, "fp": fp, "fn": fn,
            "microPrecision": tp / (tp + fp) if tp + fp else None,
            "microRecall": tp / (tp + fn) if tp + fn else None,
            "descriptivePrecisionWilson95": wilson(tp, tp + fp),
            "emittedTags": all_emitted, "reviewedEmittedTags": reviewed_emitted,
            "unknownEmittedTags": all_emitted - reviewed_emitted,
            "photosWithAnyTag": int(any_tag.sum()), "perLabel": rows}


def calibration_cross_validation(scores: np.ndarray, targets: np.ndarray, labels: list[str],
                                 groups: list[str], policy: dict, folds: int = 5) -> dict:
    if (folds != 5 or scores.ndim != 2 or scores.shape != targets.shape
            or scores.shape != (len(groups), len(labels)) or not np.isfinite(scores).all()
            or not ((scores >= 0) & (scores <= 1)).all() or not np.isin(targets, [-1, 0, 1]).all()
            or any(not group for group in groups)):
        raise ValueError("Invalid grouped calibration arrays or fold count")
    seed = "expanded-calibration-five-fold-v1"
    assignments = np.asarray([int(fingerprint([seed, group]), 16) % folds for group in groups])
    if set(assignments) != set(range(folds)):
        raise ValueError("Every grouped calibration fold must contain photos")
    decisions = np.zeros_like(scores)
    reports = []
    for fold in range(folds):
        check = assignments == fold
        fit = ~check
        fit_groups = [group for group, include in zip(groups, fit) if include]
        fitted = [choose_threshold(scores[fit, i], targets[fit, i], fit_groups, policy)
                  for i in range(len(labels))]
        thresholds = [entry["threshold"] for entry in fitted]
        for i, threshold in enumerate(thresholds):
            if threshold is not None:
                decisions[check, i] = scores[check, i] >= threshold
        reports.append({"fold": fold, "fitImages": int(fit.sum()), "checkImages": int(check.sum()),
                        "fitGroups": len(set(fit_groups)),
                        "checkGroups": len({group for group, include in zip(groups, check) if include}),
                        "perLabelFit": fitted,
                        "check": summarize(scores[check], targets[check], labels, thresholds)})
    pooled = summarize(decisions, targets, labels, [0.5] * len(labels))
    pooled["enabledLabels"] = None
    for i, row in enumerate(pooled["perLabel"]):
        row.pop("threshold")
        row["enabledFolds"] = sum(report["perLabelFit"][i]["threshold"] is not None for report in reports)
    return {"folds": folds, "seed": seed, "perFold": reports, "pooledOutOfFold": pooled,
            "limitations": [
                "Threshold-only group cross-validation within calibration, not an untouched final test.",
                "Each check photo uses thresholds fit without its entire approximate group.",
                "Enabled labels and thresholds differ per fold; pooled enabledLabels is intentionally null.",
                "No model, policy, evidence threshold or fold seed is tuned using this diagnostic.",
                "Unknown emissions are not counted as false positives; labels are not exhaustive.",
            ]}


def score_rows(session, rows: list[dict], labels: list[str]) -> tuple[np.ndarray, np.ndarray]:
    scores, targets = [], []
    for row in rows:
        path = Path(row["path"])
        if file_hash(path) != row["sha256"]:
            raise ValueError("Frozen evaluation image changed")
        scores.append(infer(session, prepare_tensor(path, row["provenance"]["Rotation"]), len(labels)))
        targets.append([{"positive": 1, "negative": 0}.get(row["labels"].get(label), -1) for label in labels])
    return np.asarray(scores), np.asarray(targets)


def validate_test_calibration(calibration: dict, protocol: dict, protocol_sha: str,
                              calibration_sha: str, bundle: dict) -> None:
    binding_fields = {"calibrationProtocolSha256", "frozenCalibrationSha256"}
    bound = binding_fields & protocol.keys()
    if bound and (bound != binding_fields
                  or protocol["frozenCalibrationSha256"] != calibration_sha):
        raise ValueError("Frozen test calibration binding changed")
    expected_protocol = protocol["calibrationProtocolSha256"] if bound else protocol_sha
    if (calibration["protocolSha256"] != expected_protocol
            or calibration["modelSha256"] != bundle["modelSha256"]
            or calibration["labels"] != bundle["labels"] or calibration["operation"] != "fit"
            or calibration["thresholdPolicy"] != protocol["calibrationPolicy"]):
        raise ValueError("Thresholds do not match the frozen calibration protocol")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["fit", "test"])
    parser.add_argument("--evaluation", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--calibration", type=Path, help="Frozen calibration.json required for test")
    parser.add_argument("--calibration-folds", type=int, choices=[0, 5], default=0,
                        help="Optional predeclared calibration-only grouped threshold diagnostic; fit only")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if (args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2])
            or (args.operation == "test" and args.calibration is None)
            or (args.operation == "test" and args.calibration_folds)
            or (args.operation == "fit" and args.calibration is not None)):
        parser.error("New external output required; --calibration is for test only")
    model, bundle = load_bundle(args.bundle)
    protocol_path = args.evaluation / "protocol.json"
    protocol = json.loads(protocol_path.read_text())
    if protocol["modelSha256"] != bundle["modelSha256"] or protocol["labels"] != bundle["labels"]:
        raise ValueError("Frozen protocol/model mismatch")
    if args.operation == "fit" and "calibrationProtocolSha256" in protocol:
        raise ValueError("Bound test-only protocol cannot refit calibration")
    split = "calibration" if args.operation == "fit" else "test"
    manifest = args.evaluation / f"{split}.jsonl"
    if file_hash(manifest) != protocol["manifests"][split]:
        raise ValueError("Frozen evaluation manifest changed")
    rows = [json.loads(line) for line in manifest.read_text().splitlines()]
    if not rows or any(row["evaluationSplit"] != split for row in rows):
        raise ValueError("Empty or wrong evaluation split")
    calibration = None
    if args.operation == "test":
        calibration = json.loads(args.calibration.read_text())
        validate_test_calibration(calibration, protocol, file_hash(protocol_path),
                                  file_hash(args.calibration), bundle)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
    session.disable_fallback()
    validate_session(session, bundle["labels"])
    scores, targets = score_rows(session, rows, bundle["labels"])
    if args.operation == "fit":
        fitted = [choose_threshold(scores[:, i], targets[:, i], [row["evaluationGroup"] for row in rows],
                                   protocol["calibrationPolicy"]) for i in range(len(bundle["labels"]))]
        thresholds = [row["threshold"] for row in fitted]
    else:
        thresholds = calibration["thresholds"]
        if (len(thresholds) != len(bundle["labels"])
                or any(t is not None and (not isinstance(t, (int, float)) or not math.isfinite(t)
                                          or not 0 <= t <= 1) for t in thresholds)):
            raise ValueError("Invalid frozen thresholds")
    result = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False, "operation": args.operation,
        "protocolSha256": file_hash(protocol_path), "modelSha256": bundle["modelSha256"],
        "labels": bundle["labels"], "thresholds": thresholds, "codeSha256": file_hash(Path(__file__)),
        "ortVersion": ort.__version__, "provider": "CPUExecutionProvider",
        "thresholdPolicy": protocol["calibrationPolicy"],
        "fixed05": summarize(scores, targets, bundle["labels"], [0.5] * len(bundle["labels"])),
        "calibrated": summarize(scores, targets, bundle["labels"], thresholds),
        "limitations": protocol["limitations"] + [
            "Wilson intervals are descriptive binomial calculations; correlated/grouped labels violate independence.",
            "Calibration resubstitution is optimistic; test output evaluates frozen thresholds on known entries only.",
            "Recall denominator includes positives of disabled labels; missing labels are never negatives.",
        ],
    }
    if args.operation == "fit":
        result["perLabelFit"] = fitted
        if args.calibration_folds:
            result["calibrationCrossValidation"] = calibration_cross_validation(
                scores, targets, bundle["labels"], [row["evaluationGroup"] for row in rows],
                protocol["calibrationPolicy"], args.calibration_folds)
    else:
        result["calibrationSha256"] = file_hash(args.calibration)
    args.output.mkdir(parents=True, exist_ok=False)
    np.savez_compressed(args.output / "scores.npz", scores=scores, targets=targets)
    result["scoresSha256"] = file_hash(args.output / "scores.npz")
    write_json(args.output / ("calibration.json" if args.operation == "fit" else "test.json"), result)
    print(json.dumps({"operation": args.operation,
                      "fixed05": {k: v for k, v in result["fixed05"].items() if k != "perLabel"},
                      "calibrated": {k: v for k, v in result["calibrated"].items() if k != "perLabel"}},
                     indent=2), flush=True)


if __name__ == "__main__":
    main()
