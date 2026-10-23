"""Compare a controlled retraining on prior development only, never calibration/test."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from calibrate_pilot import choose_threshold, summarize
from compare_pilot import rank_diagnostic, score_model
from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash, fingerprint


def load_development(run: Path) -> tuple[dict, dict, np.ndarray, np.ndarray]:
    config = json.loads((run / "run.json").read_text())
    completion = json.loads((run / "completion.json").read_text())
    if (not completion["completed"]
            or file_hash(run / "best.pt") != completion["checkpointSha256"]
            or file_hash(run / "validation-scores.npz") != completion["validationScoresSha256"]):
        raise ValueError("Incomplete or changed training run")
    labels = config["labels"]
    if not labels or len(set(labels)) != len(labels):
        raise ValueError("Invalid ordered training labels")
    cohorts = {}
    for split in ["train", "validation"]:
        rows = [json.loads(line) for line in (run / f"{split}-cohort.jsonl").read_text().splitlines()]
        if (not rows or any(row["originalSplit"] != split for row in rows)
                or any(not set(row["labels"].values()) <= {"positive", "negative"} for row in rows)):
            raise ValueError("Training/development source split mismatch")
        cohorts[split] = rows
    identity = fingerprint({**{split: [[r["sourceId"], r["sha256"], r["labels"]] for r in rows]
                               for split, rows in cohorts.items()}, "labels": labels})
    if identity != config["dataFingerprint"]:
        raise ValueError("Audited training/development cohorts changed")
    expected = np.asarray([[{"positive": 1, "negative": 0}.get(row["labels"].get(label), -1)
                            for label in labels] for row in cohorts["validation"]
                           if any(label in row["labels"] for label in labels)])
    with np.load(run / "validation-scores.npz") as arrays:
        scores, targets = arrays["scores"], arrays["targets"]
    if (not np.array_equal(targets, expected) or scores.shape != targets.shape
            or not np.isfinite(scores).all() or not ((scores >= 0) & (scores <= 1)).all()):
        raise ValueError("Development arrays do not match audited ordered targets")
    return config, completion, scores, targets


def check_control(baseline: dict, candidate: dict) -> None:
    fields = ["architecture", "labels", "initialization", "seed", "precision", "preprocessing",
              "headEpochs", "finetuneEpochs", "headLearningRate", "finetuneLearningRate",
              "batchSize", "workers", "batchNorm", "dataFingerprint", "inputFiles", "versions",
              "device", "gpu"]
    if any(baseline[key] != candidate[key] for key in fields):
        raise ValueError("Controlled experiment changed data, initialization or training recipe")
    if (baseline.get("loss", {"balance": "none"})["balance"] != "none"
            or candidate["loss"]["balance"] != "known-state"
            or candidate["loss"]["weightCap"] != 4):
        raise ValueError("Expected unweighted baseline and capped known-state candidate")


def development_summary(scores: np.ndarray, targets: np.ndarray, labels: list[str]) -> dict:
    if (scores.ndim != 2 or scores.shape != targets.shape or scores.shape[1] != len(labels)
            or not np.isfinite(scores).all() or not ((scores >= 0) & (scores <= 1)).all()
            or not np.isin(targets, [-1, 0, 1]).all() or not (targets >= 0).any()):
        raise ValueError("Invalid development score/target arrays")
    known = targets >= 0
    values, actual = scores[known].astype(np.float64), targets[known]
    # Match BCE's finite log clamp, without converting unknowns into negative labels.
    loss = -np.mean(actual * np.maximum(np.log(np.maximum(values, np.finfo(np.float64).tiny)), -100)
                    + (1 - actual) * np.maximum(np.log(np.maximum(1 - values, np.finfo(np.float64).tiny)), -100))
    return {"unweightedBceFromScores": float(loss),
            "fixed05": summarize(scores, targets, labels, [0.5] * len(labels)),
            "ranking": rank_diagnostic(scores, targets, labels)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--candidate-bundle", type=Path, required=True)
    parser.add_argument("--comparison", type=Path, required=True)
    parser.add_argument("--baseline-scores", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external experiment report directory required")
    baseline, base_done, base_scores, base_targets = load_development(args.baseline)
    candidate, done, candidate_scores, candidate_targets = load_development(args.candidate)
    check_control(baseline, candidate)
    if not np.array_equal(base_targets, candidate_targets):
        raise ValueError("Baseline/candidate ordered targets differ")
    model, bundle = load_bundle(args.candidate_bundle)
    if bundle["runConfigSha256"] != file_hash(args.candidate / "run.json"):
        raise ValueError("Candidate export does not belong to training run")
    protocol_path = args.comparison / "protocol.json"
    protocol = json.loads(protocol_path.read_text())
    manifest = args.comparison / "cohort.jsonl"
    baseline_asset = protocol["assets"]["pilot"]
    baseline_metadata = Path(baseline_asset["metadata"])
    if (file_hash(manifest) != protocol["cohortSha256"]
            or file_hash(baseline_metadata) != baseline_asset["metadataSha256"]
            or json.loads(baseline_metadata.read_text())["runConfigSha256"] != file_hash(args.baseline / "run.json")):
        raise ValueError("Matched protocol no longer belongs to baseline run")
    rows = [json.loads(line) for line in manifest.read_text().splitlines()]
    if any(row["originalSplit"] != "validation" for row in rows):
        raise ValueError("Matched diagnostic must use original development only")
    labels = protocol["sharedLabels"]
    receipt_path = args.baseline_scores / "completion.json"
    receipt = json.loads(receipt_path.read_text())
    path = args.baseline_scores / "pilot-scores.npz"
    if (not receipt["completed"] or receipt["protocolSha256"] != file_hash(protocol_path)
            or file_hash(path) != receipt["scoresSha256"]["pilot"]
            or next(m for m in receipt["models"] if m["model"] == "pilot")["modelSha256"] != baseline_asset["sha256"]):
        raise ValueError("Baseline matched predictions changed")
    with np.load(path) as arrays:
        matched_baseline = arrays["scores"]
    targets = np.asarray([[{"positive": 1, "negative": 0}.get(row["labels"].get(label), -1)
                           for label in labels] for row in rows])
    if (matched_baseline.shape != targets.shape or not np.isfinite(matched_baseline).all()
            or not ((matched_baseline >= 0) & (matched_baseline <= 1)).all()):
        raise ValueError("Invalid baseline matched scores")
    args.output.mkdir(parents=True, exist_ok=False)
    asset = {"model": str(model), "sha256": file_hash(model),
             "metadata": str(args.candidate_bundle / "export-report.json"),
             "metadataSha256": file_hash(args.candidate_bundle / "export-report.json")}
    write_json(args.output / "execution-protocol.json", {
        "codeSha256": file_hash(Path(__file__)), "protocolSha256": file_hash(protocol_path),
        "baselineRunSha256": file_hash(args.baseline / "run.json"),
        "candidateRunSha256": file_hash(args.candidate / "run.json"), "candidateAsset": asset})
    matched_candidate, timing = score_model("pilot", asset, rows, labels)
    np.savez_compressed(args.output / "candidate-matched-scores.npz", scores=matched_candidate)
    fit = np.asarray([row["comparisonSplit"] == "fit" for row in rows])
    if {row["comparisonSplit"] for row in rows} != {"fit", "check"}:
        raise ValueError("Both frozen development fit/check partitions required")
    groups = [row["comparisonGroup"] for row in rows if row["comparisonSplit"] == "fit"]
    reports = []
    for name, config, completion, full_scores, matched in [
            ("unweighted", baseline, base_done, base_scores, matched_baseline),
            ("known-state", candidate, done, candidate_scores, matched_candidate)]:
        thresholds = [choose_threshold(matched[fit, i], targets[fit, i], groups, protocol["precisionPolicy"])
                      for i in range(len(labels))]
        reports.append({
            "objective": name, "checkpointSha256": completion["checkpointSha256"],
            "selectedValidationLoss": completion["bestValidationLoss"],
            "fullDevelopment": development_summary(full_scores, base_targets, config["labels"]),
            "matchedDevelopment": development_summary(matched, targets, labels),
            "precisionPolicyFit": thresholds,
            "precisionPolicyCheck": summarize(matched[~fit], targets[~fit], labels,
                                              [item["threshold"] for item in thresholds])})
    write_json(args.output / "comparison.json", {
        "researchOnly": True, "productionApproved": False, "models": reports,
        "candidateTiming": timing, "codeSha256": file_hash(Path(__file__)),
        "candidateMatchedScoresSha256": file_hash(args.output / "candidate-matched-scores.npz"),
        "baselineCompletionSha256": file_hash(args.baseline / "completion.json"),
        "candidateCompletionSha256": file_hash(args.candidate / "completion.json"),
        "limitations": ["Same-seed development ablation, not independent generalization or repeated-seed evidence.",
                        "Known entries only; calibration, consumed test and private judgments were not accessed.",
                        "Checkpoint selection remains unweighted development BCE for both objectives.",
                        "Different selected stages reflect the predeclared checkpoint-selection rule.",
                        "Licensing, broad reliable coverage and fresh calibration/test gates remain unresolved."]})
    for report in reports:
        print(json.dumps({key: report[key] for key in ["objective", "selectedValidationLoss", "precisionPolicyCheck"]}))


if __name__ == "__main__":
    main()
