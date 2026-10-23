"""Export a local research checkpoint to FP32 ONNX and verify actual CPU parity."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import time

import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.quantization import CalibrationDataReader, QuantFormat, QuantType, quantize_static
from onnxruntime.quantization.shape_inference import quant_pre_process
import torch
from torch import nn
from torchvision.models import mobilenet_v3_large

from prepare_dataset import file_hash
from train_pilot import image_tensor, metrics, write_json


class TrainingCalibration(CalibrationDataReader):
    def __init__(self, rows: list[dict], limit: int):
        if limit < 1 or not rows or any(row["originalSplit"] != "train" for row in rows):
            raise ValueError("INT8 calibration requires original training examples only")
        self.rows = rows[:limit]
        self.iterator = iter(self.rows)

    def get_next(self):
        row = next(self.iterator, None)
        if row is None:
            return None
        if file_hash(Path(row["path"])) != row["sha256"]:
            raise ValueError("Quantization calibration image changed")
        return {"image": image_tensor(row).unsqueeze(0).numpy()}


def development_scores(session, rows: list[dict], labels: list[str]) -> tuple[np.ndarray, np.ndarray]:
    scores = []
    targets = []
    for row in rows:
        if not any(label in row["labels"] for label in labels):
            continue
        if file_hash(Path(row["path"])) != row["sha256"]:
            raise ValueError("Development image changed after training")
        actual = session.run(["scores"], {"image": image_tensor(row).unsqueeze(0).numpy()})[0]
        if actual.shape != (1, len(labels)) or not np.isfinite(actual).all():
            raise ValueError("Invalid development output contract")
        scores.append(actual[0])
        targets.append([{"positive": 1, "negative": 0}.get(row["labels"].get(label), -1)
                        for label in labels])
    if not scores:
        raise ValueError("No supervised development examples")
    return np.asarray(scores), np.asarray(targets)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True, help="Completed train_pilot output")
    parser.add_argument("--output", type=Path, required=True, help="New external research bundle directory")
    parser.add_argument("--samples", type=int, default=16)
    parser.add_argument("--int8", action="store_true", help="Static QDQ export with training-only calibration")
    parser.add_argument("--calibration-samples", type=int, default=128)
    args = parser.parse_args()
    if args.output.exists() or not 1 <= args.samples <= 100 or not 1 <= args.calibration_samples <= 1000:
        parser.error("New output directory and 1..100 parity samples required")
    if args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("Research bundle output must be outside the source checkout")
    completion = json.loads((args.run / "completion.json").read_text())
    path = args.run / "best.pt"
    if completion["checkpointSha256"] != file_hash(path) or not completion["completed"]:
        raise ValueError("Run is incomplete or checkpoint identity changed")
    checkpoint = torch.load(path, map_location="cpu", weights_only=True)
    labels = checkpoint["labels"]
    model = mobilenet_v3_large(weights=None)
    model.classifier[-1] = nn.Linear(model.classifier[-1].in_features, len(labels))
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    model = nn.Sequential(model, nn.Sigmoid()).eval()
    torch.set_num_threads(4)
    args.output.mkdir(parents=True, exist_ok=False)
    model_path = args.output / "compact-pilot-fp32.onnx"
    sample = torch.zeros(1, 3, 224, 224)
    torch.onnx.export(model, sample, str(model_path), input_names=["image"], output_names=["scores"],
                      opset_version=17, dynamo=False, external_data=False)
    onnx.checker.check_model(onnx.load(str(model_path)))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    start = time.perf_counter()
    session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
    load_seconds = time.perf_counter() - start
    rows = [json.loads(line) for line in (args.run / "validation-cohort.jsonl").read_text().splitlines()]
    if not rows:
        raise ValueError("No held-out development examples available for parity")
    generator = torch.Generator().manual_seed(66)
    tensors = [sample, torch.ones_like(sample), torch.randn(sample.shape, generator=generator)]
    for row in rows[:args.samples]:
        if file_hash(Path(row["path"])) != row["sha256"]:
            raise ValueError("Parity input image changed after training")
        tensors.append(image_tensor(row).unsqueeze(0))
    maximum = 0.0
    flips = 0
    for tensor in tensors:
        with torch.inference_mode():
            expected = model(tensor).numpy()
        actual = session.run(["scores"], {"image": tensor.numpy()})[0]
        if actual.shape != (1, len(labels)) or not np.isfinite(actual).all():
            raise ValueError("Invalid ONNX output contract")
        maximum = max(maximum, float(np.max(np.abs(actual - expected))))
        flips += int(((actual >= 0.5) != (expected >= 0.5)).sum())
    if maximum > 1e-4 or flips:
        raise ValueError(f"FP32 parity failed: maxAbs={maximum}, .5 cutoff flips={flips}")
    array = tensors[-1].numpy()
    for _ in range(5):
        session.run(["scores"], {"image": array})
    timings = []
    for _ in range(30):
        start = time.perf_counter()
        session.run(["scores"], {"image": array})
        timings.append((time.perf_counter() - start) * 1000)
    report = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "modelSha256": file_hash(model_path), "modelBytes": model_path.stat().st_size,
        "checkpointSha256": completion["checkpointSha256"], "labels": labels,
        "runConfigSha256": file_hash(args.run / "run.json"),
        "exportCodeSha256": file_hash(Path(__file__)),
        "preprocessing": checkpoint["config"]["preprocessing"],
        "input": {"name": "image", "dtype": "float32", "shape": [1, 3, 224, 224]},
        "output": {"name": "scores", "dtype": "float32", "shape": [1, len(labels)],
                   "semantics": "independent sigmoid; no calibrated thresholds"},
        "parity": {"syntheticInputs": 3, "developmentPhotos": min(args.samples, len(rows)),
                   "maxAbsoluteError": maximum, "tolerance": 1e-4, "cutoff05Flips": flips},
        "cpuDiagnostic": {"ortVersion": ort.__version__, "providers": session.get_providers(),
                          "intraOpThreads": 4, "loadSeconds": load_seconds, "warmups": 5,
                          "iterations": 30, "p50InferenceMs": float(np.percentile(timings, 50)),
                          "p95InferenceMs": float(np.percentile(timings, 95)),
                          "scope": "single tensor; excludes decode/preprocessing; not app end-to-end latency"},
        "directMLMeasured": False, "int8Exported": False, "redistributionCleared": False,
        "limitations": checkpoint["config"]["limitations"],
    }
    development, targets = development_scores(session, rows, labels)
    report["development"] = {
        "purpose": "partial-label development/model selection only; not untouched test quality",
        "supervisedImages": len(targets),
        **metrics(development, targets, labels),
    }
    np.savez_compressed(args.output / "fp32-development-scores.npz", scores=development, targets=targets)
    if args.int8:
        train_rows = [json.loads(line) for line in (args.run / "train-cohort.jsonl").read_text().splitlines()]
        reader = TrainingCalibration(train_rows, args.calibration_samples)
        preprocessed = args.output / "quantization-preprocessed.onnx"
        quantized = args.output / "compact-pilot-int8.onnx"
        quant_pre_process(str(model_path), str(preprocessed))
        quantize_static(str(preprocessed), str(quantized), reader, quant_format=QuantFormat.QDQ,
                        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8, per_channel=True)
        onnx.checker.check_model(onnx.load(str(quantized)))
        quant_session = ort.InferenceSession(str(quantized), sess_options=options,
                                            providers=["CPUExecutionProvider"])
        errors = []
        quant_flips = 0
        for tensor in tensors:
            actual = quant_session.run(["scores"], {"image": tensor.numpy()})[0]
            reference = session.run(["scores"], {"image": tensor.numpy()})[0]
            if actual.shape != reference.shape or not np.isfinite(actual).all():
                raise ValueError("INT8 outputs violate finite score contract")
            errors.append(float(np.max(np.abs(actual - reference))))
            quant_flips += int(((actual >= 0.5) != (reference >= 0.5)).sum())
        report["int8Exported"] = True
        report["int8"] = {
            "modelSha256": file_hash(quantized), "modelBytes": quantized.stat().st_size,
            "format": "static QDQ; per-channel QInt8 weights; QUInt8 activations",
            "calibrationSource": "original train only",
            "calibrationImages": len(reader.rows),
            "calibrationManifestSha256": file_hash(args.run / "train-cohort.jsonl"),
            "maxAbsoluteErrorVersusFp32": max(errors), "cutoff05Flips": quant_flips,
            "checkedInputs": len(tensors), "providers": quant_session.get_providers(),
            "qualityApproved": False,
            "warning": "Diagnostic subset only. Needs full development comparison and independent recalibration.",
        }
        quant_development, quant_targets = development_scores(quant_session, rows, labels)
        if not np.array_equal(targets, quant_targets):
            raise ValueError("FP32/INT8 development targets differ")
        known = targets >= 0
        report["int8"]["development"] = {
            "supervisedImages": len(targets),
            "maxAbsoluteErrorVersusFp32": float(np.max(np.abs(quant_development - development))),
            "knownEntryCutoff05Flips": int((((quant_development >= 0.5) != (development >= 0.5)) & known).sum()),
            **metrics(quant_development, targets, labels),
        }
        report["int8"]["warning"] = (
            "Partial-label development comparison is not final test quality or threshold calibration.")
        np.savez_compressed(args.output / "int8-development-scores.npz",
                            scores=quant_development, targets=targets)
    write_json(args.output / "export-report.json", report)
    print(json.dumps({"bytes": report["modelBytes"], "maxAbsError": maximum,
                      "p50InferenceMs": report["cpuDiagnostic"]["p50InferenceMs"]}), flush=True)


if __name__ == "__main__":
    main()
