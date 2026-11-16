"""Audit fixed test evidence and candidate separation without model inference."""

from __future__ import annotations

import argparse
import csv
import json
from pathlib import Path

from bind_author_calibration import calibration_rows, check_run
from pilot_data import difference_hash, exclude_training_references, load_downloads, oriented_image
from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash


def state_coverage(rows: list[dict], labels: list[str], thresholds: list[float | None]) -> list[dict]:
    if (not labels or len(set(labels)) != len(labels) or len(labels) != len(thresholds)
            or not rows or any(not row.get("evaluationGroup") for row in rows)):
        raise ValueError("Invalid fixed labels, thresholds or test groups")
    coverage = []
    for label, threshold in zip(labels, thresholds, strict=True):
        counts = {}
        for state in ["positive", "negative"]:
            known = [row for row in rows if row["labels"].get(label) == state]
            counts[state] = len(known)
            counts[f"{state}Groups"] = len({row["evaluationGroup"] for row in known})
        coverage.append({
            "label": label, "calibrationEnabled": threshold is not None, **counts,
            "unknown": len(rows) - counts["positive"] - counts["negative"],
            "descriptiveStateGroupSupport": (
                min(counts["positive"], counts["negative"]) >= 10
                and min(counts["positiveGroups"], counts["negativeGroups"]) >= 5)})
    return coverage


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evaluation", type=Path, required=True)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--reservation", type=Path, required=True)
    parser.add_argument("--download", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--calibration", type=Path, required=True)
    parser.add_argument("--calibration-protocol", type=Path, required=True)
    parser.add_argument("--bind-test", action="store_true",
                        help="Explicitly freeze a test-only protocol using the unchanged fitted thresholds")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external audit directory required")
    _, bundle = load_bundle(args.bundle)
    cohorts = check_run(args.run, args.reservation, args.download, bundle)
    plan = json.loads(args.reservation.read_text())
    records = load_downloads(args.download, "validation")
    calibration_rows(plan, records)
    reserved_ids = {row["sourceId"] for members in plan["splits"].values() for row in members}
    references = cohorts["train"] + [row for row in records if row["sourceId"] in reserved_ids]
    protocol_path = args.evaluation / "protocol.json"
    protocol = json.loads(protocol_path.read_text())
    manifest = args.evaluation / "test.jsonl"
    if file_hash(manifest) != protocol["manifests"]["test"]:
        raise ValueError("Original frozen test manifest changed")
    rows = [json.loads(line) for line in manifest.read_text().splitlines()]
    if (not rows or len({row["sourceId"] for row in rows}) != len(rows)
            or any(row["evaluationSplit"] != "test" for row in rows)):
        raise ValueError("Invalid original test rows")
    for row in rows:
        path = Path(row["path"])
        if (file_hash(path) != row["sha256"]
                or difference_hash(oriented_image(path, row["provenance"]["Rotation"])) != row["dhash"]):
            raise ValueError("Original test image identity changed")
    _, violations = exclude_training_references(rows, references)
    if violations:
        raise ValueError(f"Test overlaps new training/development/calibration: {len(violations)} photos")
    calibration = json.loads(args.calibration.read_text())
    cal_protocol = json.loads(args.calibration_protocol.read_text())
    if (calibration["operation"] != "fit" or calibration["modelSha256"] != bundle["modelSha256"]
            or calibration["labels"] != bundle["labels"]
            or calibration["protocolSha256"] != file_hash(args.calibration_protocol)
            or cal_protocol["reservationSha256"] != file_hash(args.reservation)
            or cal_protocol["modelSha256"] != bundle["modelSha256"]
            or cal_protocol["labels"] != bundle["labels"]
            or calibration["thresholdPolicy"] != cal_protocol["calibrationPolicy"]):
        raise ValueError("Frozen calibration/model/reservation mismatch")
    coverage = state_coverage(rows, bundle["labels"], calibration["thresholds"])
    enabled = [row for row in coverage if row["calibrationEnabled"]]
    report = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "inferencePerformed": False, "testConsumed": False,
        "originalProtocolSha256": file_hash(protocol_path), "testManifestSha256": file_hash(manifest),
        "modelSha256": bundle["modelSha256"], "calibrationSha256": file_hash(args.calibration),
        "reservationSha256": file_hash(args.reservation), "codeSha256": file_hash(Path(__file__)),
        "images": len(rows), "groups": len({row["evaluationGroup"] for row in rows}),
        "overlapViolations": len(violations), "labels": len(coverage), "enabledLabels": len(enabled),
        "labelsWithDescriptiveSupport": sum(row["descriptiveStateGroupSupport"] for row in coverage),
        "enabledLabelsWithDescriptiveSupport": sum(row["descriptiveStateGroupSupport"] for row in enabled),
        "enabledLabelsWithNoKnownNegative": sum(row["negative"] == 0 for row in enabled),
        "coverage": coverage,
        "limitations": [
            "No test predictions or outcomes accessed; labels/provenance/bytes only.",
            "10 positive/negative images and five groups per state describe support, not statistical power.",
            "Do not select labels or tune thresholds/model using this audit; report every fixed-head label.",
            "Original reserve was sampled for an earlier head, not newly representative of this head.",
            "Partial labels and approximate author/dHash groups cannot certify exhaustive quality/event independence.",
            "ImageNet pretraining overlap is unknown; no pretrained-weight redistribution clearance.",
            "This audit does not authorize test consumption or constitute a new frozen inference protocol.",
        ]}
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "audit.json", report)
    with (args.output / "coverage.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(coverage[0]))
        writer.writeheader()
        writer.writerows(coverage)
    if args.bind_test:
        (args.output / "test.jsonl").write_bytes(manifest.read_bytes())
        write_json(args.output / "protocol.json", {
            "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
            "modelSha256": bundle["modelSha256"], "labels": bundle["labels"],
            "manifests": {"test": file_hash(manifest)},
            "originalProtocolSha256": file_hash(protocol_path),
            "calibrationProtocolSha256": file_hash(args.calibration_protocol),
            "frozenCalibrationSha256": file_hash(args.calibration),
            "calibrationPolicy": cal_protocol["calibrationPolicy"],
            "auditSha256": file_hash(args.output / "audit.json"),
            "codeSha256": file_hash(Path(__file__)),
            "testComparisons": ["fixed 0.5 baseline", "unchanged calibration-only thresholds"],
            "reporting": {
                "labels": "all 42 fixed-head labels, including disabled and sparse evidence",
                "primary": "known-entry precision and recall of the unchanged full-fit policy",
                "secondary": "fixed 0.5, per-label TP/FP/FN, unknown emissions and descriptive intervals",
                "acceptance": "no production approval; no post-test label/policy/model tuning"},
            "limitations": [
                "User explicitly approved a limited one-time evaluation after metadata-only feasibility audit.",
                "Model/ordered vocabulary and calibration-only thresholds were frozen before test inference.",
                "Vocabulary was chosen using calibration state/group counts before model training.",
                "Partial public labels and balanced selection are not representative household annotation.",
                "Author/byte/dHash grouping is approximate, not certified event independence.",
                "Unknown ImageNet pretraining overlap and unresolved pretrained-weight/image licensing remain.",
                "Only nine of 17 enabled labels have ten known positives/negatives and five groups per state.",
                "Test was selected for an earlier head; support was inspected before inference.",
                "Sparse and unknown states prevent broad 17-label precision/exhaustive quality claims.",
                "This consumes the original 1425-photo reserve; no future tuning treats it as untouched.",
            ]})
    print(json.dumps({key: value for key, value in report.items() if key != "coverage"}, indent=2))


if __name__ == "__main__":
    main()
