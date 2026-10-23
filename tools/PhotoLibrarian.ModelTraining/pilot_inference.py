"""Exact pilot preprocessing without PyTorch, for isolated CPU/DirectML evaluation."""

from __future__ import annotations

import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageOps

from prepare_dataset import file_hash, unique_object


def prepare_tensor(path: Path, rotation: str | None = None,
                   mean: list[float] | None = None, std: list[float] | None = None) -> np.ndarray:
    with Image.open(path) as image:
        image.load()
        if rotation is None:
            image = ImageOps.exif_transpose(image)
        else:
            angle = float(rotation)
            if not math.isfinite(angle) or angle not in {0, 90, 180, 270}:
                raise ValueError("Unsupported public-source rotation")
            image = image.convert("RGB")
            if angle:
                image = image.rotate(angle, expand=True)
        image = image.convert("RGB").resize((224, 224), Image.Resampling.BILINEAR)
        pixels = np.asarray(image, dtype=np.float32).transpose(2, 0, 1) / np.float32(255)
    mean = np.asarray(mean or [0.485, 0.456, 0.406], dtype=np.float32)
    std = np.asarray(std or [0.229, 0.224, 0.225], dtype=np.float32)
    if (mean.shape != (3,) or std.shape != (3,) or not np.isfinite(mean).all()
            or not np.isfinite(std).all() or (std <= 0).any()):
        raise ValueError("Image normalization requires three finite means and positive standard deviations")
    mean, std = mean[:, None, None], std[:, None, None]
    return np.ascontiguousarray(((pixels - mean) / std)[None])


def load_bundle(bundle: Path) -> tuple[Path, dict]:
    report = json.loads((bundle / "export-report.json").read_text(encoding="utf-8"),
                        object_pairs_hook=unique_object)
    model = bundle / "compact-pilot-fp32.onnx"
    labels = report["labels"]
    if (not labels or len(set(labels)) != len(labels)
            or any(not isinstance(label, str) or not label for label in labels)):
        raise ValueError("Invalid ordered pilot labels")
    if model.stat().st_size != report["modelBytes"] or file_hash(model) != report["modelSha256"]:
        raise ValueError("Exported model identity changed")
    if (report["input"] != {"name": "image", "dtype": "float32", "shape": [1, 3, 224, 224]}
            or report["output"]["shape"] != [1, len(labels)]
            or report["output"]["name"] != "scores" or report["output"]["dtype"] != "float32"):
        raise ValueError("Unsupported pilot input/output contract")
    preprocessing = report["preprocessing"]
    mean, std = preprocessing.get("mean"), preprocessing.get("std")
    if (not isinstance(mean, list) or not isinstance(std, list) or len(mean) != 3 or len(std) != 3
            or any(not isinstance(value, (int, float)) or not math.isfinite(value) for value in mean + std)
            or any(value <= 0 for value in std)):
        raise ValueError("Export does not declare valid image normalization")
    return model, report


def validate_session(session, labels: list[str]) -> None:
    inputs, outputs = session.get_inputs(), session.get_outputs()
    if (len(inputs) != 1 or inputs[0].name != "image" or inputs[0].shape != [1, 3, 224, 224]
            or inputs[0].type != "tensor(float)" or len(outputs) != 1
            or outputs[0].name != "scores" or outputs[0].shape != [1, len(labels)]
            or outputs[0].type != "tensor(float)"):
        raise ValueError("Runtime graph violates pilot contract")


def infer(session, tensor: np.ndarray, count: int) -> np.ndarray:
    scores = session.run(["scores"], {"image": tensor})[0]
    if (scores.shape != (1, count) or not np.isfinite(scores).all()
            or not ((scores >= 0) & (scores <= 1)).all()):
        raise ValueError("Runtime produced invalid independent scores")
    return scores[0]


def write_json(path: Path, data: dict) -> None:
    pending = path.with_suffix(path.suffix + ".writing")
    pending.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    pending.replace(path)
