"""Compare a generated RAM++ ONNX bundle with pinned upstream PyTorch output.

The report is intentionally machine-readable.  It checks raw numeric parity,
exact binary-mask parity, and the label count before a bundle is considered
eligible for hosting.
"""

from __future__ import annotations

import argparse
import glob
import importlib
import json
from pathlib import Path
import sys
import time

import numpy as np
from PIL import Image

import export_ram_plus as export

RAW_TOLERANCE = {"rtol": 1e-3, "atol": 1e-4}


def pixels(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        image = image.convert("RGB").resize(
            (export.IMAGE_SIZE, export.IMAGE_SIZE), Image.Resampling.BILINEAR
        )
        values = np.asarray(image, dtype=np.float32) / 255.0
    values = (values - np.asarray([0.485, 0.456, 0.406], dtype=np.float32))
    values /= np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    return np.ascontiguousarray(values.transpose(2, 0, 1)[None])


def pytorch_output(model, image):
    import torch

    captured = []

    def capture_logits(_module, _inputs, output):
        captured.append(output.detach().squeeze(-1))

    # Execute upstream generate_tag, not a second copy of the export equations.
    handle = model.fc.register_forward_hook(capture_logits)
    try:
        with torch.no_grad():
            model.generate_tag(torch.from_numpy(image))
    finally:
        handle.remove()
    if len(captured) != 1:
        raise ValueError("Upstream generate_tag must produce exactly one logits tensor")
    with torch.no_grad():
        logits = captured[0]
        probabilities = torch.sigmoid(logits)
        targets = (probabilities > model.class_threshold).to(torch.float32)
        targets[:, model.delete_tag_index] = 0
        return tuple(value.cpu().numpy() for value in (logits, probabilities, targets))


def validate_contract(runtime) -> None:
    inputs = runtime.get_inputs()
    outputs = runtime.get_outputs()
    if (len(inputs) != 1 or inputs[0].name != "image" or
            inputs[0].type != "tensor(float)" or
            inputs[0].shape != [1, 3, export.IMAGE_SIZE, export.IMAGE_SIZE] or
            [item.name for item in outputs] != list(export.OUTPUT_NAMES) or
            any(item.type != "tensor(float)" or item.shape != [1, export.LABEL_COUNT]
                for item in outputs)):
        raise ValueError("Unexpected ONNX input/output names, dtypes or dimensions")


def compare_outputs(expected, actual) -> dict:
    for values in (expected, actual):
        if len(values) != len(export.OUTPUT_NAMES) or any(
            value.dtype != np.float32 or value.shape != (1, export.LABEL_COUNT)
            for value in values
        ):
            raise ValueError("Parity outputs must be float32 [1,4585]")
    finite = bool(all(np.isfinite(value).all() for value in (*expected, *actual)))
    logits_error = np.abs(expected[0].astype(np.float64) - actual[0]) if finite else None
    probabilities_error = np.abs(expected[1].astype(np.float64) - actual[1]) if finite else None
    return {
        "logitsMaxAbsoluteError": float(logits_error.max()) if finite else None,
        "logitsMeanAbsoluteError": float(logits_error.mean()) if finite else None,
        "probabilitiesMaxAbsoluteError": float(probabilities_error.max()) if finite else None,
        "probabilitiesMeanAbsoluteError": float(probabilities_error.mean()) if finite else None,
        "targetMismatchCount": int(np.count_nonzero(expected[2] != actual[2])),
        "finite": finite,
        "binaryTargets": bool(all(np.isin(values[2], [0, 1]).all()
                                  for values in (expected, actual))),
        "rawTolerancePassed": finite and bool(all(
            np.allclose(reference, candidate, **RAW_TOLERANCE)
            for reference, candidate in zip(expected[:2], actual[:2])
        )),
    }


def parity_passed(rows: list[dict]) -> bool:
    return bool(rows) and all(
        row["finite"] and row["binaryTargets"] and row["rawTolerancePassed"] and
        row["targetMismatchCount"] == 0 for row in rows
    )


def resolve_images(patterns: list[Path]) -> list[Path]:
    paths = []
    for pattern in patterns:
        matches = sorted(glob.glob(str(pattern)))
        if not matches:
            raise ValueError(f"No validation images match {pattern}")
        for match in matches:
            path = Path(match).resolve()
            if not path.is_file():
                raise ValueError(f"Validation image is not a file: {path}")
            if path not in paths:
                paths.append(path)
    return paths


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--images", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--provider", choices=["cpu", "directml"], default="cpu")
    args = parser.parse_args()
    export.verify_environment()
    export.verify_source(args.source_dir)
    export.verify_artifact(args.bundle, export.build_lock()["expectedModel"])
    images = resolve_images(args.images)
    checkpoint_size, checkpoint_hash = export.CHECKPOINTS["ram_plus_swin_large_14m.pth"]
    if (args.checkpoint.stat().st_size != checkpoint_size or
            export.sha256(args.checkpoint) != checkpoint_hash):
        raise ValueError("Validation checkpoint does not match the pinned upstream checkpoint")

    import torch
    import onnxruntime as ort
    provider = "CPUExecutionProvider" if args.provider == "cpu" else "DmlExecutionProvider"
    if provider not in ort.get_available_providers():
        raise ValueError(f"Required execution provider is unavailable: {provider}")
    sys.path.insert(0, str(args.source_dir))
    ram_plus_module = importlib.import_module("ram.models.ram_plus")

    class ExportTokenizer:
        def __len__(self) -> int:
            return 30_524

    ram_plus_module.init_tokenizer = lambda _: ExportTokenizer()

    labels, _ = export.load_labels(args.source_dir)
    labels_path = args.bundle.parent / "ram_plus_swin_large_14m_labels.txt"
    if labels_path.read_bytes() != ("\n".join(labels) + "\n").encode("utf-8"):
        raise ValueError("Bundle labels do not match the authoritative vocabulary serialization")
    torch.manual_seed(0)
    started = time.perf_counter()
    model = ram_plus_module.ram_plus(
        pretrained=str(args.checkpoint), image_size=export.IMAGE_SIZE, vit="swin_l"
    ).eval()
    pytorch_load_ms = (time.perf_counter() - started) * 1000
    started = time.perf_counter()
    options = ort.SessionOptions()
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.enable_mem_pattern = args.provider != "directml"
    runtime = ort.InferenceSession(str(args.bundle), sess_options=options, providers=[provider])
    runtime.disable_fallback()
    if runtime.get_providers()[0] != provider:
        raise ValueError(f"Runtime did not activate the requested provider: {provider}")
    runtime_load_ms = (time.perf_counter() - started) * 1000
    validate_contract(runtime)
    rows = []
    for image_path in images:
        image = pixels(image_path)
        started = time.perf_counter()
        expected = pytorch_output(model, image)
        pytorch_ms = (time.perf_counter() - started) * 1000
        started = time.perf_counter()
        actual = runtime.run(list(export.OUTPUT_NAMES), {"image": image})
        runtime_ms = (time.perf_counter() - started) * 1000
        rows.append({
            "image": str(image_path),
            "imageSha256": export.sha256(image_path),
            "labelCount": len(labels),
            "pytorchMilliseconds": pytorch_ms,
            "onnxMilliseconds": runtime_ms,
            **compare_outputs(expected, actual),
        })
    report = {
        "schemaVersion": 1,
        "sourceRevision": export.SOURCE_REVISION,
        "checkpointRevision": export.CHECKPOINT_REVISION,
        "checkpointSha256": checkpoint_hash,
        "modelSha256": export.sha256(args.bundle),
        "modelSizeBytes": args.bundle.stat().st_size,
        "validatorSha256": export.text_sha256(Path(__file__)),
        "labelsSha256": export.sha256(labels_path),
        "sourceLabelsSha256": export.sha256(args.source_dir / export.LABEL_PATH),
        "executionProvider": provider,
        "sessionProviders": runtime.get_providers(),
        "pytorchLoadMilliseconds": pytorch_load_ms,
        "onnxLoadMilliseconds": runtime_load_ms,
        "exportEnvironment": {"python": sys.version, "packages": export.package_versions()},
        "images": rows,
        "exactTargetParity": all(row["targetMismatchCount"] == 0 for row in rows),
        "finiteOutputs": all(row["finite"] for row in rows),
        "rawTolerance": RAW_TOLERANCE,
        "rawTolerancePassed": all(row["rawTolerancePassed"] for row in rows),
        "passed": parity_passed(rows),
        "note": "Numeric parity alone is not calibration, quality review or publication approval. Timings include cold inference; use ModelBench for warmed provider benchmarks.",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, allow_nan=False))
    if not report["passed"]:
        raise SystemExit("RAM++ parity failed; see the report for unmet gates")


if __name__ == "__main__":
    main()
