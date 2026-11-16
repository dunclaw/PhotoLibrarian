"""Compare immutable CPU/DirectML regression scores at fixed research thresholds."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np

from pilot_inference import load_bundle, write_json
from prepare_dataset import file_hash


def score_parity(cpu: np.ndarray, directml: np.ndarray, labels: list[str],
                 thresholds: list[float | None]) -> dict:
    if (not labels or len(set(labels)) != len(labels) or len(thresholds) != len(labels)
            or cpu.ndim != 2 or cpu.shape != directml.shape or not cpu.shape[0]
            or cpu.shape[1] != len(labels)
            or not np.isfinite(cpu).all() or not np.isfinite(directml).all()
            or not ((cpu >= 0) & (cpu <= 1)).all()
            or not ((directml >= 0) & (directml <= 1)).all()
            or any(t is not None and (isinstance(t, bool) or not isinstance(t, (int, float))
                                      or not np.isfinite(t) or not 0 <= t <= 1) for t in thresholds)):
        raise ValueError("Invalid ordered provider scores or frozen thresholds")
    error = np.abs(cpu - directml)
    flips = []
    for index, (label, threshold) in enumerate(zip(labels, thresholds, strict=True)):
        flips.append({
            "label": label, "threshold": threshold,
            "cutoff05Flips": int(((cpu[:, index] >= 0.5) != (directml[:, index] >= 0.5)).sum()),
            "calibratedFlips": (0 if threshold is None else int(
                ((cpu[:, index] >= threshold) != (directml[:, index] >= threshold)).sum()))})
    return {
        "images": len(cpu), "scoreEntries": int(cpu.size),
        "enabledLabels": sum(t is not None for t in thresholds),
        "maxAbsoluteError": float(error.max()), "tolerance": 1e-4,
        "withinTolerance": bool(error.max() <= 1e-4),
        "cutoff05Flips": sum(row["cutoff05Flips"] for row in flips),
        "calibratedFlips": sum(row["calibratedFlips"] for row in flips),
        "perLabel": flips}


def load_scores(folder: Path, provider: str, bundle: dict) -> tuple[dict, np.ndarray]:
    report = json.loads((folder / "benchmark.json").read_text())
    if (report["requestedProvider"] != provider or report["modelSha256"] != bundle["modelSha256"]
            or report["labels"] != bundle["labels"] or report["modelBytes"] != bundle["modelBytes"]
            or file_hash(folder / "scores.npz") != report["scoresSha256"]
            or not report["pythonRuntimeFallbackDisabled"]):
        raise ValueError("Benchmark provider/model/score identity mismatch")
    images = report["images"]
    if (not images or len(images) != report["imageCount"]
            or len({row["image"] for row in images}) != len(images)):
        raise ValueError("Invalid benchmark image identities")
    with np.load(folder / "scores.npz") as arrays:
        scores = arrays["scores"]
    if scores.shape != (len(images), len(bundle["labels"])):
        raise ValueError("Benchmark image/score shape mismatch")
    return report, scores


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cpu", type=Path, required=True)
    parser.add_argument("--directml", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--calibration", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external comparison directory required")
    _, bundle = load_bundle(args.bundle)
    calibration = json.loads(args.calibration.read_text())
    if (calibration["operation"] != "fit" or calibration["modelSha256"] != bundle["modelSha256"]
            or calibration["labels"] != bundle["labels"]):
        raise ValueError("Frozen calibration/model mismatch")
    cpu, cpu_scores = load_scores(args.cpu, "CPUExecutionProvider", bundle)
    directml, dml_scores = load_scores(args.directml, "DmlExecutionProvider", bundle)
    if (cpu["images"] != directml["images"] or cpu["preprocessing"] != directml["preprocessing"]
            or cpu["intraOpThreads"] != directml["intraOpThreads"]):
        raise ValueError("Benchmarks do not use identical ordered photos/preprocessing/threads")
    parity = score_parity(cpu_scores, dml_scores, bundle["labels"], calibration["thresholds"])
    report = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": bundle["modelSha256"], "calibrationSha256": file_hash(args.calibration),
        "cpuReportSha256": file_hash(args.cpu / "benchmark.json"),
        "directmlReportSha256": file_hash(args.directml / "benchmark.json"),
        "codeSha256": file_hash(Path(__file__)), "parity": parity,
        "directmlPlacement": directml["nodePlacement"],
        "strictNoCpuFallback": directml["strictNoCpuFallback"],
        "limitations": [
            "Previously exposed private regression images; no independent quality estimate.",
            "Frozen calibration thresholds unchanged; no test images or scores used.",
            "Python host diagnostics, not Release-application latency or isolated model/VRAM memory.",
            "One CPU-then-DirectML run; filesystem cache and host load uncontrolled.",
            "Provider index is recorded but GPU inventory alone does not identify the selected adapter.",
        ]}
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "comparison.json", report)
    print(json.dumps({key: value for key, value in parity.items() if key != "perLabel"}, indent=2))
    if not parity["withinTolerance"] or parity["cutoff05Flips"] or parity["calibratedFlips"]:
        raise ValueError("Provider parity gate failed; see persisted comparison report")


if __name__ == "__main__":
    main()
