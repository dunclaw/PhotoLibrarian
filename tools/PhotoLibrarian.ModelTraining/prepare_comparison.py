"""Freeze a matched DEVELOPMENT-only model comparison; never reopen consumed test."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

import numpy as np

from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash, fingerprint
from prepare_evaluation import group_partition


def ram_labels(path: Path) -> list[str]:
    # Same legacy indexed-label contract used by ModelBench.LabelResolver.
    pattern = r"""['"]?(?P<index>\d+)['"]?\s*[:=]\s*(?P<quote>['"])(?P<label>(?:\\.|.)*?)(?P=quote)(?=\s*(?:[,}\]\r\n]|$))"""
    indexed = {}
    for match in re.finditer(pattern, path.read_text(encoding="utf-8")):
        index = int(match["index"])
        if index in indexed:
            raise ValueError("Duplicate RAM label index")
        indexed[index] = (match["label"].replace("\\'", "'").replace('\\"', '"')
                          .replace("\\\\", "\\").replace("_", " ").strip())
    if set(indexed) != set(range(4585)) or any(not value for value in indexed.values()):
        raise ValueError("Expected 4,585 contiguous RAM labels")
    return [indexed[i] for i in range(4585)]


def zero_bundle(path: Path, expected: str) -> tuple[Path, dict]:
    bundle = json.loads(path.read_text(encoding="utf-8"))
    model = (path.parent / bundle["modelFile"]).resolve()
    if not model.is_relative_to(path.parent.resolve()) or bundle["modelId"] != expected:
        raise ValueError("Zero-shot model escaped its bundle or model ID differs")
    if file_hash(model) != bundle["modelSha256"]:
        raise ValueError("Zero-shot model identity mismatch")
    labels = bundle["labels"]
    embeddings = np.asarray([entry["embedding"] for entry in labels], dtype=np.float32)
    if (len({entry["label"] for entry in labels}) != len(labels)
            or embeddings.ndim != 2 or not np.isfinite(embeddings).all()
            or (np.linalg.norm(embeddings, axis=1) <= 0).any()
            or bundle["scoreKind"] not in {"sigmoid", "cosine"}
            or bundle["resizeMode"] not in {"squash", "shortest-center-crop"}
            or bundle["interpolation"] not in {"linear", "cubic"}):
        raise ValueError("Invalid zero-shot vocabulary/embeddings/preprocessing")
    return model, bundle


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--ram-model", type=Path, required=True)
    parser.add_argument("--ram-labels", type=Path, required=True)
    parser.add_argument("--tinyclip", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=1000)
    args = parser.parse_args()
    if (args.output.exists() or not 100 <= args.limit <= 3000
            or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2])):
        parser.error("New external output and limit 100..3000 required")
    model, bundle = load_bundle(args.bundle)
    tiny_model, tiny = zero_bundle(args.tinyclip, "tinyclip")
    raw_ram = ram_labels(args.ram_labels)
    tiny_names = [entry["label"] for entry in tiny["labels"]]
    common = [label for label in bundle["labels"] if label in raw_ram and label in tiny_names]
    if not common:
        raise ValueError("No exact common labels")
    config = json.loads((args.run / "run.json").read_text())
    if file_hash(args.run / "run.json") != bundle["runConfigSha256"]:
        raise ValueError("Pilot development run configuration changed")
    cohorts = {}
    for split in ["train", "validation"]:
        cohorts[split] = [json.loads(line) for line in (args.run / f"{split}-cohort.jsonl").read_text().splitlines()]
    if fingerprint({**{split: [[r["sourceId"], r["sha256"], r["labels"]] for r in rows]
                       for split, rows in cohorts.items()}, "labels": bundle["labels"]}) != config["dataFingerprint"]:
        raise ValueError("Pilot audited cohorts changed")
    seed = "matched-development-v1"
    rows = sorted(cohorts["validation"], key=lambda row: fingerprint([seed, row["sourceId"]]))[:args.limit]
    rows = group_partition(rows, seed)
    for row in rows:
        row["comparisonSplit"] = "fit" if row.pop("evaluationSplit") == "calibration" else "check"
        row["comparisonGroup"] = row.pop("evaluationGroup")
    if {row["comparisonSplit"] for row in rows} != {"fit", "check"}:
        raise ValueError("Both development diagnostic partitions required")
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = args.output / "cohort.jsonl"
    manifest.write_text("".join(json.dumps(row, sort_keys=True) + "\n" for row in rows), encoding="utf-8")
    vocabulary = {
        "schemaVersion": 1, "name": "compact-pilot-comparison-v1",
        "description": "Installed TinyCLIP vocabulary, also used for SigLIP2 development comparison.",
        "reviewPolicy": "Existing general vocabulary; no vocabulary optimization on held-out test.",
        "prompts": {"siglip2": "This is a photo of {label}.", "tinyclip": "a photo of {label}"},
        "labels": tiny_names,
    }
    write_json(args.output / "zero-shot-vocabulary.json", vocabulary)
    protocol = {
        "schemaVersion": 1, "purpose": "matched prior-development diagnostic only; NOT untouched test",
        "researchOnly": True, "productionApproved": False, "seed": seed,
        "cohortSha256": file_hash(manifest), "images": len(rows),
        "splitImages": {split: sum(row["comparisonSplit"] == split for row in rows) for split in ["fit", "check"]},
        "sharedLabels": common, "pilotLabels": bundle["labels"],
        "unsupportedPilotLabels": [label for label in bundle["labels"] if label not in common],
        "mapping": "exact canonical spelling only; no synonym, ancestor or cross-class negative inference",
        "assets": {
            "pilot": {"model": str(model.resolve()), "sha256": file_hash(model),
                      "metadata": str((args.bundle / "export-report.json").resolve()),
                      "metadataSha256": file_hash(args.bundle / "export-report.json")},
            "ram-plus": {"model": str(args.ram_model.resolve()), "sha256": file_hash(args.ram_model),
                         "metadata": str(args.ram_labels.resolve()), "metadataSha256": file_hash(args.ram_labels)},
            "tinyclip": {"model": str(tiny_model), "sha256": tiny["modelSha256"],
                         "metadata": str(args.tinyclip.resolve()), "metadataSha256": file_hash(args.tinyclip)},
        },
        "nativeCutoffs": {"pilot": 0.5, "ram-plus": 0.5, "tinyclip": 0.25, "siglip2": 0.1},
        "rankLimit": None,
        "precisionPolicy": {"targetPrecision": 0.9, "minimumWilsonLower95": 0.75, "minimumAccepted": 20,
                            "minimumKnownPositive": 10, "minimumKnownNegative": 10, "minimumGroupsPerState": 5},
        "zeroShotVocabularySha256": file_hash(args.output / "zero-shot-vocabulary.json"),
        "limitations": [
            "Prior validation selected the pilot checkpoint; both diagnostic partitions are development data.",
            "RAM++/zero-shot pretraining overlap with public sources is unknown; not a clean generalization ranking.",
            "Native preprocessing differs by model; original photo bytes/orientation and known label pool are matched.",
            "RAM++ installed graph exposes a binary mask, not continuous scores; threshold fit is not comparable.",
            "TinyCLIP and SigLIP2 use the same installed ordered vocabulary and their existing model-specific prompts.",
            "Independent scores do not compete across label count; prompts/precision/model variants still differ.",
            "No human useful-tag/whole-photo ratings or exhaustively reviewed annotation are inferred.",
        ],
        "codeSha256": file_hash(Path(__file__)),
    }
    write_json(args.output / "protocol.json", protocol)
    print(json.dumps({key: protocol[key] for key in ["images", "splitImages", "sharedLabels",
                                                   "unsupportedPilotLabels"]}, indent=2), flush=True)


if __name__ == "__main__":
    main()
