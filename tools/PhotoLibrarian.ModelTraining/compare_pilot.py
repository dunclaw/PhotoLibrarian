"""Score frozen prior-development photos with pilot, installed RAM++/TinyCLIP and pinned SigLIP2."""

from __future__ import annotations

import argparse
import importlib.metadata
import json
from pathlib import Path
import platform
import time

import numpy as np
import onnxruntime as ort
from PIL import Image

from calibrate_pilot import choose_threshold, summarize
from pilot_data import oriented_image
from pilot_inference import prepare_tensor, write_json
from prepare_comparison import ram_labels, zero_bundle
from prepare_dataset import file_hash


def zero_tensor(image: Image.Image, bundle: dict) -> np.ndarray:
    size = bundle["inputSize"]
    interpolation = {"linear": Image.Resampling.BILINEAR, "cubic": Image.Resampling.BICUBIC}[bundle["interpolation"]]
    if bundle["resizeMode"] == "squash":
        image = image.resize((size, size), interpolation)
    else:
        width, height = image.size
        shape = (size, int(size * height / width)) if width <= height else (int(size * width / height), size)
        image = image.resize(shape, interpolation)
        left, top = (shape[0] - size) // 2, (shape[1] - size) // 2
        image = image.crop((left, top, left + size, top + size))
    # Match ModelBench.prepare_zero_shot.manual_pixels, including rescale rounding.
    pixels = (np.asarray(image, dtype=np.float64) * (1 / 255)).astype(np.float32)
    pixels = ((pixels - np.asarray(bundle["normalizationMean"], dtype=np.float32))
              / np.asarray(bundle["normalizationStd"], dtype=np.float32))
    return np.ascontiguousarray(pixels.transpose(2, 0, 1)[None])


def zero_scores(embedding: np.ndarray, text: np.ndarray, bundle: dict) -> np.ndarray:
    if (embedding.shape != (1, text.shape[1]) or not np.isfinite(embedding).all()
            or np.linalg.norm(embedding) == 0):
        raise ValueError("Invalid zero-shot image embedding")
    vector = embedding.astype(np.float64)[0]
    cosine = np.clip(text @ (vector / np.linalg.norm(vector)), -1, 1)
    if bundle["scoreKind"] == "sigmoid":
        logits = bundle["logitScale"] * cosine + bundle["logitBias"]
        cosine = np.exp(-np.logaddexp(0, -logits))
    return cosine.astype(np.float32)


def score_model(model_id: str, asset: dict, rows: list[dict], shared: list[str]) -> tuple[np.ndarray, dict]:
    model = Path(asset["model"])
    metadata = Path(asset["metadata"])
    if file_hash(model) != asset["sha256"] or file_hash(metadata) != asset["metadataSha256"]:
        raise ValueError("Frozen baseline asset changed")
    bundle = None
    if model_id in {"tinyclip", "siglip2"}:
        _, bundle = zero_bundle(metadata, model_id)
        native_labels = [item["label"] for item in bundle["labels"]]
        text = np.asarray([item["embedding"] for item in bundle["labels"]], dtype=np.float64)
        text /= np.linalg.norm(text, axis=1, keepdims=True)
        input_name, output_name = bundle["inputName"], bundle["outputName"]
    elif model_id == "ram-plus":
        native_labels = ram_labels(metadata)
        input_name, output_name = "img", "targets"
    else:
        native_labels = json.loads(metadata.read_text())["labels"]
        input_name, output_name = "image", "scores"
    if any(label not in native_labels for label in shared):
        raise ValueError("Model lacks frozen shared labels")
    indices = [native_labels.index(label) for label in shared]
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    start = time.perf_counter()
    session = ort.InferenceSession(str(model), sess_options=options, providers=["CPUExecutionProvider"])
    session.disable_fallback()
    load_ms = (time.perf_counter() - start) * 1000
    if len(session.get_inputs()) != 1 or session.get_inputs()[0].name != input_name:
        raise ValueError("Unexpected baseline input")
    first_shape = [1, 3, 384, 384] if model_id == "ram-plus" else [1, 3, 224, 224]
    if any(isinstance(a, int) and a != b for a, b in zip(session.get_inputs()[0].shape, first_shape, strict=True)):
        raise ValueError("Unexpected baseline dimensions")
    for _ in range(3):
        session.run([output_name], {input_name: np.zeros(first_shape, dtype=np.float32)})
    outputs, times = [], []
    native_emissions = 0
    cutoff = {"pilot": 0.5, "ram-plus": 0.5, "tinyclip": 0.25, "siglip2": 0.1}[model_id]
    for index, row in enumerate(rows):
        start = time.perf_counter()
        path = Path(row["path"])
        if file_hash(path) != row["sha256"]:
            raise ValueError("Comparison input bytes changed")
        verified = time.perf_counter()
        if model_id == "pilot":
            tensor = prepare_tensor(path, row["provenance"]["Rotation"])
        else:
            image = oriented_image(path, row["provenance"]["Rotation"])
            if bundle:
                tensor = zero_tensor(image, bundle)
            else:
                values = np.asarray(image.resize((384, 384), Image.Resampling.BILINEAR),
                                    dtype=np.float32).transpose(2, 0, 1) / np.float32(255)
                tensor = np.ascontiguousarray(
                    ((values - np.array([0.485, 0.456, 0.406], dtype=np.float32)[:, None, None])
                     / np.array([0.229, 0.224, 0.225], dtype=np.float32)[:, None, None])[None])
        prepared = time.perf_counter()
        raw = session.run([output_name], {input_name: tensor})[0]
        inferred = time.perf_counter()
        scores = zero_scores(raw, text, bundle) if bundle else raw[0]
        if scores.shape != (len(native_labels),) or not np.isfinite(scores).all():
            raise ValueError("Baseline violated finite ordered-score contract")
        if model_id == "ram-plus" and not np.isin(scores, [0, 1]).all():
            raise ValueError("RAM++ must return binary targets, not assumed probabilities")
        low = -1 if model_id == "tinyclip" else 0
        if not ((scores >= low) & (scores <= 1)).all():
            raise ValueError("Baseline scores outside declared numeric range")
        native_emissions += int((scores >= cutoff).sum())
        outputs.append(scores[indices])
        completed = time.perf_counter()
        times.append([(prepared - verified) * 1000, (inferred - prepared) * 1000,
                      (completed - inferred) * 1000, (completed - verified) * 1000])
        if (index + 1) % 100 == 0:
            print(f"{model_id}: {index + 1}/{len(rows)} development photos", flush=True)
    report = {
        "model": model_id, "modelSha256": asset["sha256"], "metadataSha256": asset["metadataSha256"],
        "modelBytes": model.stat().st_size, "metadataBytes": metadata.stat().st_size,
        "nativeLabelCount": len(native_labels), "nativeEmissionsAtProvisionalCutoff": native_emissions,
        "nativeCutoff": cutoff, "nativeScoreKind": bundle["scoreKind"] if bundle else
            ("binary" if model_id == "ram-plus" else "independent sigmoid"),
        "loadMs": load_ms, "warmups": 3, "threads": 4, "provider": session.get_providers(),
        "timingMs": {name: {"p50": float(np.percentile(np.asarray(times)[:, i], 50)),
                           "p95": float(np.percentile(np.asarray(times)[:, i], 95))}
                     for i, name in enumerate(["decodePreprocess", "inference", "postprocess", "total"])},
        "preprocessing": {"orientation": "recorded Open Images CCW rotation; no additional EXIF",
                          "recipe": {key: bundle[key] for key in ["inputSize", "resizeMode", "interpolation",
                                                                "normalizationMean", "normalizationStd"]}
                          if bundle else {"inputSize": first_shape[-1], "resize": "full-image PIL bilinear",
                                          "normalization": "ImageNet"}},
    }
    return np.asarray(outputs), report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["score", "report"])
    parser.add_argument("--comparison", type=Path, required=True)
    parser.add_argument("--siglip2", type=Path, help="Prepared SigLIP2 JSON; score only")
    parser.add_argument("--scores", type=Path, help="Completed score directory; report only")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.output.resolve().is_relative_to(Path(__file__).resolve().parents[2]):
        parser.error("New external comparison output required")
    protocol_path = args.comparison / "protocol.json"
    protocol = json.loads(protocol_path.read_text())
    manifest = args.comparison / "cohort.jsonl"
    if file_hash(manifest) != protocol["cohortSha256"]:
        raise ValueError("Frozen development manifest changed")
    rows = [json.loads(line) for line in manifest.read_text().splitlines()]
    shared = protocol["sharedLabels"]
    args.output.mkdir(parents=True, exist_ok=False)
    if args.operation == "score":
        if args.siglip2 is None or args.scores is not None:
            parser.error("Score requires --siglip2 and no --scores")
        sig_model, sig_bundle = zero_bundle(args.siglip2, "siglip2")
        tiny_asset = protocol["assets"]["tinyclip"]
        _, tiny_bundle = zero_bundle(Path(tiny_asset["metadata"]), "tinyclip")
        if [r["label"] for r in tiny_bundle["labels"]] != [r["label"] for r in sig_bundle["labels"]]:
            raise ValueError("SigLIP2/TinyCLIP ordered native vocabularies must match")
        assets = {**protocol["assets"], "siglip2": {
            "model": str(sig_model), "sha256": sig_bundle["modelSha256"],
            "metadata": str(args.siglip2.resolve()), "metadataSha256": file_hash(args.siglip2)}}
        # Persist added SigLIP2 identity BEFORE inference; do not mutate the original protocol.
        write_json(args.output / "execution-protocol.json",
                   {"protocolSha256": file_hash(protocol_path), "assets": assets,
                    "codeSha256": file_hash(Path(__file__))})
        models = []
        identities = {}
        for model_id in ["pilot", "tinyclip", "siglip2", "ram-plus"]:
            values, model_report = score_model(model_id, assets[model_id], rows, shared)
            path = args.output / f"{model_id}-scores.npz"
            np.savez_compressed(path, scores=values)
            identities[model_id] = file_hash(path)
            models.append(model_report)
            write_json(args.output / f"{model_id}.json", model_report)
        write_json(args.output / "completion.json", {
            "completed": True, "protocolSha256": file_hash(protocol_path), "models": models,
            "scoresSha256": identities, "ortVersion": ort.__version__, "python": platform.python_version(),
            "environment": dict(sorted((d.metadata["Name"], d.version) for d in importlib.metadata.distributions())),
        })
        return
    if args.scores is None or args.siglip2 is not None:
        parser.error("Report requires --scores and no --siglip2")
    completion = json.loads((args.scores / "completion.json").read_text())
    if not completion["completed"] or completion["protocolSha256"] != file_hash(protocol_path):
        raise ValueError("Comparison scoring incomplete or protocol mismatch")
    targets = np.array([[{"positive": 1, "negative": 0}.get(row["labels"].get(label), -1)
                         for label in shared] for row in rows])
    fit = np.array([r["comparisonSplit"] == "fit" for r in rows])
    check = ~fit
    groups = [r["comparisonGroup"] for r in rows if r["comparisonSplit"] == "fit"]
    reports = []
    for model in completion["models"]:
        model_id = model["model"]
        path = args.scores / f"{model_id}-scores.npz"
        if file_hash(path) != completion["scoresSha256"][model_id]:
            raise ValueError("Comparison score arrays changed")
        with np.load(path) as arrays:
            scores = arrays["scores"]
        low = -1 if model_id == "tinyclip" else 0
        if (scores.shape != targets.shape or not np.isfinite(scores).all()
                or not ((scores >= low) & (scores <= 1)).all()):
            raise ValueError("Invalid matched score array")
        result = {**model, "nativeCutoffSharedAllDevelopment": summarize(
            scores, targets, shared, [model["nativeCutoff"]] * len(shared)),
            "nativeCutoffSharedDevelopmentCheck": summarize(
                scores[check], targets[check], shared, [model["nativeCutoff"]] * len(shared))}
        if model_id == "ram-plus":
            result["precisionPolicyFit"] = {"supported": False,
                                            "reason": "Installed graph exposes binary targets only."}
        else:
            fitted = [choose_threshold(scores[fit, i], targets[fit, i], groups, protocol["precisionPolicy"])
                      for i in range(len(shared))]
            result["precisionPolicyFit"] = {"supported": True, "perLabel": fitted}
            result["precisionPolicySharedDevelopmentCheck"] = summarize(
                scores[check], targets[check], shared, [item["threshold"] for item in fitted])
        reports.append(result)
    report = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "purpose": protocol["purpose"], "sharedLabels": shared, "images": len(rows),
        "protocolSha256": file_hash(protocol_path), "scoringCompletionSha256": file_hash(args.scores / "completion.json"),
        "codeSha256": file_hash(Path(__file__)), "models": reports,
        "limitations": protocol["limitations"] + [
            "Both numeric fit/check partitions are prior development; not a fresh test or deployment ranking.",
            "Known entries only; unreviewed emissions are counted separately, never called false positives.",
            "Native whole-vocabulary emission counts describe coverage breadth, not measured useful/correct tags.",
            "CPU-only diagnostic; timing includes native preprocessing and text-score postprocessing, not application latency.",
        ],
    }
    write_json(args.output / "comparison.json", report)
    print(json.dumps({"images": len(rows), "sharedLabels": len(shared), "models": [
        {"model": r["model"], "nativeCutoff": {
            k: v for k, v in r["nativeCutoffSharedAllDevelopment"].items() if k != "perLabel"},
         "precisionPolicyCheck": {k: v for k, v in r.get("precisionPolicySharedDevelopmentCheck", {}).items()
                                  if k != "perLabel"}} for r in reports]}, indent=2))


if __name__ == "__main__":
    main()
