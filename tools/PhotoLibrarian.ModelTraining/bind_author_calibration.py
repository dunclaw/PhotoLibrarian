"""Bind a pretraining author reservation to its completed research model, without inference."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from pilot_data import exclude_training_references, load_downloads, reservation_members
from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash, fingerprint
from prepare_evaluation import group_partition


def calibration_rows(reservation: dict, records: list[dict]) -> list[dict]:
    development, calibration = reservation_members(reservation, records)
    reserved = development | set(calibration)
    grouped = group_partition([row for row in records if row["sourceId"] in reserved], reservation["seed"])
    expected = {
        row["sourceId"]: (row["group"], "calibration" if split == "calibration" else "test")
        for split, members in reservation["splits"].items() for row in members}
    actual = {row["sourceId"]: (row["evaluationGroup"], row["evaluationSplit"]) for row in grouped}
    if actual != expected:
        raise ValueError("Frozen reservation grouping or split changed")
    return [row for row in grouped if row["sourceId"] in calibration]


def check_run(run: Path, reservation: Path, download: Path, bundle: dict) -> dict:
    config_path = run / "run.json"
    config = json.loads(config_path.read_text())
    plan = json.loads(reservation.read_text())
    completion = json.loads((run / "completion.json").read_text())
    if (file_hash(config_path) != bundle["runConfigSha256"] or not completion["completed"]
            or completion["checkpointSha256"] != bundle["checkpointSha256"]
            or file_hash(run / "best.pt") != completion["checkpointSha256"]
            or config["labels"] != bundle["labels"]):
        raise ValueError("Completed training run/export mismatch")
    if (config["inputFiles"]["authorReservation"]["sha256"] != file_hash(reservation)
            or config["inputFiles"]["validation"]["sha256"] != file_hash(download)
            or plan["downloadSha256"] != file_hash(download)
            or any(config["inputFiles"][name]["sha256"] != plan[f"{name}Sha256"]
                   for name in ["vocabulary", "mapping"])):
        raise ValueError("Training reservation inputs changed")
    excluded = [value["sha256"] for name, value in config["inputFiles"].items()
                if name.startswith("excludedImages")]
    if sorted(excluded) != sorted(plan["excludedDownloadSha256"]):
        raise ValueError("Training did not exclude the frozen prior evaluation downloads")
    cohorts = {name: [json.loads(line) for line in (run / f"{name}-cohort.jsonl").read_text().splitlines()]
               for name in ["train", "validation"]}
    identity = fingerprint({
        name: [[row["sourceId"], row["sha256"], row["labels"]] for row in rows]
        for name, rows in cohorts.items()} | {"labels": bundle["labels"]})
    if identity != config["dataFingerprint"]:
        raise ValueError("Training cohort fingerprint changed")
    return cohorts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reservation", type=Path, required=True)
    parser.add_argument("--download", type=Path, required=True)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external protocol directory required")
    _, bundle = load_bundle(args.bundle)
    cohorts = check_run(args.run, args.reservation, args.download, bundle)
    reservation = json.loads(args.reservation.read_text())
    records = load_downloads(args.download, "validation")
    rows = calibration_rows(reservation, records)
    development, _ = reservation_members(reservation, records)
    if not {row["sourceId"] for row in cohorts["validation"]} <= development:
        raise ValueError("Training used development photos outside the frozen reservation")
    _, violations = exclude_training_references(cohorts["train"], records)
    if violations:
        raise ValueError("Training overlaps reserved identities/authors/near hashes")
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = args.output / "calibration.jsonl"
    manifest.write_text("".join(json.dumps(row, sort_keys=True) + "\n" for row in rows), encoding="utf-8")
    protocol = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": bundle["modelSha256"], "labels": bundle["labels"],
        "reservationSha256": file_hash(args.reservation), "downloadSha256": file_hash(args.download),
        "runConfigSha256": bundle["runConfigSha256"], "codeSha256": file_hash(Path(__file__)),
        "manifests": {"calibration": file_hash(manifest)},
        "images": {"calibration": len(rows)},
        "groups": {"calibration": len({row["evaluationGroup"] for row in rows})},
        "calibrationPolicy": {
            "targetPrecision": 0.9, "minimumWilsonLower95": 0.75, "minimumAccepted": 20,
            "minimumKnownPositive": 10, "minimumKnownNegative": 10, "minimumGroupsPerState": 5,
            "selection": "maximize true positives, then accepted count, then threshold; insufficient evidence abstains"},
        "calibrationCrossValidation": {"folds": 5, "seed": "expanded-calibration-five-fold-v1"},
        "limitations": reservation["limitations"] + [
            "Public labels are partial; balanced selection is not population-representative.",
            "Author/byte/dHash grouping is approximate, not verified event independence.",
            "Vocabulary was selected using calibration state/group counts before training, not model scores.",
            "Full-fit calibration is optimistic resubstitution; grouped cross-validation checks thresholds only.",
            "No final test is bound by this protocol; existing 1425-photo reserve remains unscored.",
            "No production approval or pretrained-weight redistribution clearance.",
        ]}
    write_json(args.output / "protocol.json", protocol)
    print(json.dumps({"images": protocol["images"], "groups": protocol["groups"],
                      "protocolSha256": file_hash(args.output / "protocol.json"),
                      "calibrationSha256": file_hash(manifest)}, indent=2))


if __name__ == "__main__":
    main()
