"""Staged, partial-label MobileNetV3 research training. Never a shipping approval.

Requires requirements-training.txt. Downloads only the named TorchVision
initialization checkpoint. Model/results stay in a new external output folder.
"""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import random
import sys
import time

os.environ.setdefault("CUBLAS_WORKSPACE_CONFIG", ":4096:8")

import numpy as np
import torch
from torch import nn
from torch.utils.data import DataLoader, Dataset
from torchvision.models import MobileNet_V3_Large_Weights, mobilenet_v3_large
from torchvision.transforms import functional as TF, InterpolationMode

from pilot_data import build_audit, oriented_image
from prepare_dataset import file_hash


WEIGHTS = MobileNet_V3_Large_Weights.IMAGENET1K_V2
WEIGHTS_SHA256 = "5c1a416349c4cf298f2a6a5e2600ed0ee55e604713578f5e74e6bc8bcaef7997"
MEAN = [0.485, 0.456, 0.406]
STD = [0.229, 0.224, 0.225]


def image_tensor(row: dict) -> torch.Tensor:
    image = oriented_image(Path(row["path"]), row["provenance"]["Rotation"])
    # Full-image resize preserves annotated objects that a crop could remove.
    image = TF.resize(image, [224, 224], interpolation=InterpolationMode.BILINEAR, antialias=True)
    return TF.normalize(TF.to_tensor(image), MEAN, STD)


class PilotDataset(Dataset):
    def __init__(self, rows: list[dict], labels: list[str], augment: bool):
        self.rows = [row for row in rows if any(label in row["labels"] for label in labels)]
        self.labels = labels
        self.augment = augment
        if not self.rows:
            raise ValueError("Dataset contains no supervised entries for this head")

    def __len__(self):
        return len(self.rows)

    def __getitem__(self, index):
        row = self.rows[index]
        image = image_tensor(row)
        if self.augment and torch.rand(()) < 0.5:
            image = torch.flip(image, dims=[2])
        states = {"positive": 1.0, "negative": 0.0, "unknown": -1.0}
        targets = torch.tensor([states[row["labels"].get(label, "unknown")] for label in self.labels])
        return image, targets


def training_state_weights(rows: list[dict], labels: list[str]) -> tuple[torch.Tensor, list[dict]]:
    if not rows or not labels or any(row["originalSplit"] != "train" for row in rows):
        raise ValueError("State weights require original training examples only")
    evidence = []
    for label in labels:
        positive = sum(row["labels"].get(label) == "positive" for row in rows)
        negative = sum(row["labels"].get(label) == "negative" for row in rows)
        if not positive or not negative:
            raise ValueError("State balancing requires both known states for every label")
        total = positive + negative
        evidence.append({"label": label, "knownPositive": positive, "knownNegative": negative,
                         "negativeWeight": min(4.0, total / (2 * negative)),
                         "positiveWeight": min(4.0, total / (2 * positive))})
    weights = torch.tensor([[item["negativeWeight"], item["positiveWeight"]] for item in evidence])
    return weights, evidence


def masked_loss(logits: torch.Tensor, targets: torch.Tensor,
                state_weights: torch.Tensor | None = None) -> tuple[torch.Tensor | None, int]:
    if logits.shape != targets.shape or not torch.isfinite(logits).all():
        raise ValueError("Logit shape mismatch or nonfinite model outputs")
    if not torch.all((targets == -1) | (targets == 0) | (targets == 1)):
        raise ValueError("Targets must be positive 1, negative 0 or unknown -1")
    if state_weights is not None and (
            logits.ndim != 2 or state_weights.shape != (logits.shape[1], 2)
            or not torch.isfinite(state_weights).all()
            or not torch.all((state_weights > 0) & (state_weights <= 4))):
        raise ValueError("State weights must be finite positive label-by-state weights capped at four")
    mask = targets >= 0
    count = int(mask.sum().item())
    if not count:
        return None, 0
    losses = nn.functional.binary_cross_entropy_with_logits(logits[mask], targets[mask], reduction="none")
    if state_weights is not None:
        weights = torch.where(targets == 1, state_weights[:, 1], state_weights[:, 0])
        losses = losses * weights[mask]
    return losses.mean(), count


def metrics(scores: np.ndarray, targets: np.ndarray, labels: list[str]) -> dict:
    if (scores.shape != targets.shape or scores.ndim != 2 or scores.shape[1] != len(labels)
            or not np.isfinite(scores).all() or not np.isin(targets, [-1, 0, 1]).all()
            or not ((scores >= 0) & (scores <= 1)).all()):
        raise ValueError("Invalid score/target array")
    known = targets >= 0
    actual = targets == 1
    predicted = scores >= 0.5
    tp = ((predicted & actual) & known).sum(axis=0)
    fp = ((predicted & ~actual) & known).sum(axis=0)
    fn = ((~predicted & actual) & known).sum(axis=0)

    def ratio(a, b):
        return float(a / b) if b else None

    per_label = []
    for i, label in enumerate(labels):
        per_label.append({
            "label": label, "knownPositive": int(actual[:, i].sum()),
            "knownNegative": int(((targets[:, i] == 0)).sum()),
            "tp": int(tp[i]), "fp": int(fp[i]), "fn": int(fn[i]),
            "precision": ratio(tp[i], tp[i] + fp[i]), "recall": ratio(tp[i], tp[i] + fn[i]),
        })
    ps = [row["precision"] for row in per_label if row["precision"] is not None]
    rs = [row["recall"] for row in per_label if row["recall"] is not None]
    return {
        "threshold": 0.5, "thresholdCalibrated": False,
        "microPrecision": ratio(tp.sum(), tp.sum() + fp.sum()),
        "microRecall": ratio(tp.sum(), tp.sum() + fn.sum()),
        "macroPrecisionDefinedLabels": float(np.mean(ps)) if ps else None,
        "macroRecallDefinedLabels": float(np.mean(rs)) if rs else None,
        "precisionDefinedLabels": len(ps), "recallDefinedLabels": len(rs),
        "knownEntries": int(known.sum()), "unknownEntries": int((~known).sum()),
        "perLabel": per_label,
        "warning": "Reviewed source-label entries only; not exhaustive recall or calibrated production quality.",
    }


def evaluate(model: nn.Module, loader: DataLoader, device: torch.device):
    model.eval()
    total = known = 0
    scores, targets = [], []
    with torch.inference_mode():
        for images, labels in loader:
            logits = model(images.to(device))
            loss, count = masked_loss(logits, labels.to(device))
            if loss is not None:
                total += float(loss.item()) * count
                known += count
            scores.append(torch.sigmoid(logits).cpu().numpy())
            targets.append(labels.numpy())
    if not known:
        raise ValueError("Validation contains no known entries")
    return total / known, np.concatenate(scores), np.concatenate(targets)


def write_json(path: Path, data: dict) -> None:
    pending = path.with_suffix(path.suffix + ".writing")
    pending.write_text(json.dumps(data, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    pending.replace(path)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--train", type=Path, required=True)
    parser.add_argument("--additional-train", type=Path, action="append", default=[],
                        help="Additional original-train downloads; independently hash/decoder checked")
    parser.add_argument("--exclude-images", type=Path, action="append", default=[],
                        help="Reserved original-validation downloads used only for training leakage exclusion")
    parser.add_argument("--validation", type=Path, required=True)
    parser.add_argument("--regression-images", type=Path)
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--mapping", type=Path, default=Path(__file__).with_name("open-images-mapping.csv"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--weights-cache", type=Path, required=True)
    parser.add_argument("--research-only", action="store_true", required=True,
                        help="Acknowledge local research only; no product/redistribution clearance")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--head-epochs", type=int, default=3)
    parser.add_argument("--finetune-epochs", type=int, default=2)
    parser.add_argument("--batch-size", type=int, default=64)
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--minimum-train", type=int, default=50)
    parser.add_argument("--minimum-validation", type=int, default=10)
    parser.add_argument("--seed", type=int, default=66)
    parser.add_argument("--loss-balance", choices=["none", "known-state"], default="none",
                        help="Opt-in inverse known-state training frequencies, capped at four")
    args = parser.parse_args()
    if min(args.head_epochs, args.finetune_epochs, args.batch_size) < 1 or not 0 <= args.workers <= 8:
        parser.error("Both stages and batch size must be positive; workers 0..8")
    if args.output.exists():
        parser.error("--output must be new; previous research runs are immutable")
    repository = Path(__file__).resolve().parents[2]
    if args.output.resolve().is_relative_to(repository) or args.weights_cache.resolve().is_relative_to(repository):
        parser.error("Run output and checkpoint cache must be outside the source checkout")
    if args.device == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA explicitly requested but unavailable; no silent CPU fallback")
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    torch.use_deterministic_algorithms(True)
    torch.set_num_threads(8)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.allow_tf32 = False
    torch.backends.cuda.matmul.allow_tf32 = False
    train, validation, labels, audit = build_audit(
        args.train, args.validation, args.vocabulary, args.mapping, args.regression_images,
        args.minimum_train, args.minimum_validation, args.additional_train, args.exclude_images)
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "data-audit.json", audit)
    for name, rows in [("train", train), ("validation", validation)]:
        with (args.output / f"{name}-cohort.jsonl").open("w", encoding="utf-8", newline="\n") as stream:
            for row in rows:
                stream.write(json.dumps(row, sort_keys=True) + "\n")
    args.weights_cache.mkdir(parents=True, exist_ok=True)
    weight_file = args.weights_cache / "checkpoints" / WEIGHTS.url.rsplit("/", 1)[-1]
    weight_file.parent.mkdir(parents=True, exist_ok=True)
    if not weight_file.exists():
        pending = weight_file.with_suffix(".downloading")
        if pending.exists():
            raise ValueError("Unfinished initialization checkpoint download; inspect before retrying")
        torch.hub.download_url_to_file(WEIGHTS.url, str(pending), hash_prefix=WEIGHTS_SHA256, progress=False)
        pending.replace(weight_file)
    if file_hash(weight_file) != WEIGHTS_SHA256:
        raise ValueError("Initialization checkpoint does not match reviewed full SHA-256")
    model = mobilenet_v3_large(weights=None)
    model.load_state_dict(torch.load(weight_file, map_location="cpu", weights_only=True), strict=True)
    model.classifier[-1] = nn.Linear(model.classifier[-1].in_features, len(labels))
    device = torch.device(args.device)
    model.to(device)
    initialization = {
        "url": WEIGHTS.url, "enum": str(WEIGHTS), "sha256": file_hash(weight_file),
        "bytes": weight_file.stat().st_size,
        "terms": "TorchVision code BSD-3-Clause; pretrained ImageNet weight/dataset rights are separate.",
        "redistributionCleared": False,
    }
    train_data = PilotDataset(train, labels, augment=True)
    val_data = PilotDataset(validation, labels, augment=False)
    state_weights, state_evidence = None, None
    if args.loss_balance == "known-state":
        state_weights, state_evidence = training_state_weights(train, labels)
        state_weights = state_weights.to(device)
    generator = torch.Generator().manual_seed(args.seed)
    train_loader = DataLoader(train_data, batch_size=args.batch_size, shuffle=True, generator=generator,
                              num_workers=args.workers, persistent_workers=args.workers > 0)
    val_loader = DataLoader(val_data, batch_size=args.batch_size, num_workers=args.workers,
                            persistent_workers=args.workers > 0)
    config = {
        "schemaVersion": 1, "researchOnly": True, "productionApproved": False,
        "labels": labels, "architecture": "torchvision.mobilenet_v3_large",
        "initialization": initialization, "seed": args.seed,
        "device": str(device), "gpu": torch.cuda.get_device_name(device) if device.type == "cuda" else None,
        "python": sys.version, "platform": platform.platform(), "cudaRuntime": torch.version.cuda,
        "precision": {"dtype": "float32", "cudnnAllowTF32": False, "matmulAllowTF32": False},
        "environment": dict(sorted((distribution.metadata["Name"], distribution.version)
                                  for distribution in importlib.metadata.distributions())),
        "codeSha256": {name: file_hash(Path(__file__).with_name(name)) for name in [
            "train_pilot.py", "pilot_data.py", "prepare_dataset.py", "requirements-training.txt"]},
        "versions": {name: importlib.metadata.version(name) for name in [
            "torch", "torchvision", "numpy", "Pillow", "onnx", "onnxruntime"]},
        "inputFiles": {name: {"sha256": file_hash(path)} for name, path in [
            ("train", args.train), ("validation", args.validation), ("vocabulary", args.vocabulary),
            ("mapping", args.mapping)] + [
                (f"additionalTrain{i}", path) for i, path in enumerate(args.additional_train)] + [
                (f"excludedImages{i}", path) for i, path in enumerate(args.exclude_images)]},
        "preprocessing": {"size": [224, 224], "resize": "full-image PIL bilinear",
                          "orientation": "Open Images Rotation degrees CCW, EXIF not additionally applied",
                          "mean": MEAN, "std": STD, "augmentation": "horizontal flip p=0.5 only"},
        "optimizer": "AdamW; weight_decay=0.0001",
        "loss": {"balance": args.loss_balance, "weightCap": 4 if state_weights is not None else None,
                 "trainingEvidence": state_evidence,
                 "reduction": "weighted sum divided by known-entry count; no weight renormalization",
                 "validationSelection": "unweighted masked BCE, unchanged from baseline"},
        "headEpochs": args.head_epochs, "finetuneEpochs": args.finetune_epochs,
        "headLearningRate": 0.001, "finetuneLearningRate": 0.0001,
        "batchSize": args.batch_size, "workers": args.workers,
        "batchNorm": "running statistics frozen in both stages; affine parameters fine-tuned in stage two",
        "dataFingerprint": audit["datasetFingerprint"],
        "trainSupervisedImages": len(train_data), "validationSupervisedImages": len(val_data),
        "limitations": audit["limitations"],
    }
    write_json(args.output / "run.json", config)
    print(f"Research pilot: {len(labels)} labels; {len(train_data)} train, {len(val_data)} validation", flush=True)
    history = []
    baseline_loss, baseline_scores, baseline_targets = evaluate(model, val_loader, device)
    write_json(args.output / "baseline.json", {"loss": baseline_loss,
               **metrics(baseline_scores, baseline_targets, labels)})
    best = float("inf")
    start = time.perf_counter()
    for stage, epochs, learning_rate in [
            ("head", args.head_epochs, 0.001), ("finetune", args.finetune_epochs, 0.0001)]:
        for parameter in model.parameters():
            parameter.requires_grad = stage == "finetune"
        for parameter in model.classifier[-1].parameters():
            parameter.requires_grad = True
        optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad],
                                     lr=learning_rate, weight_decay=0.0001)
        for epoch in range(1, epochs + 1):
            model.train()
            for module in model.modules():
                if isinstance(module, (nn.BatchNorm1d, nn.BatchNorm2d, nn.BatchNorm3d)):
                    module.eval()
            total = known = updates = 0
            for images, targets in train_loader:
                optimizer.zero_grad(set_to_none=True)
                loss, count = masked_loss(model(images.to(device)), targets.to(device), state_weights)
                if loss is None:
                    continue
                if not torch.isfinite(loss):
                    raise ValueError("Training loss is nonfinite")
                loss.backward()
                optimizer.step()
                total += float(loss.item()) * count
                known += count
                updates += 1
            if not updates:
                raise ValueError("No supervised training updates in epoch")
            val_loss, scores, targets = evaluate(model, val_loader, device)
            result = {"stage": stage, "epoch": epoch, "trainLoss": total / known,
                      "validationLoss": val_loss, "updates": updates,
                      "elapsedSeconds": time.perf_counter() - start, **metrics(scores, targets, labels)}
            history.append(result)
            write_json(args.output / "history.json", {"epochs": history})
            if val_loss < best:
                best = val_loss
                torch.save({"state_dict": {key: value.detach().cpu() for key, value in model.state_dict().items()},
                            "labels": labels, "config": config,
                            "stage": stage, "epoch": epoch}, args.output / "best.pt")
                np.savez_compressed(args.output / "validation-scores.npz", scores=scores, targets=targets)
                write_json(args.output / "best-validation.json", result)
            print(f"{stage} {epoch}: train={total / known:.5f} validation={val_loss:.5f}", flush=True)
    write_json(args.output / "completion.json", {
        "completed": True, "researchOnly": True, "productionApproved": False,
        "bestValidationLoss": best, "initialValidationLoss": baseline_loss,
        "checkpointSha256": file_hash(args.output / "best.pt"),
        "checkpointBytes": (args.output / "best.pt").stat().st_size,
        "validationScoresSha256": file_hash(args.output / "validation-scores.npz"),
        "trainingSeconds": time.perf_counter() - start,
        "headEpochsCompleted": args.head_epochs, "finetuneEpochsCompleted": args.finetune_epochs,
        "validationPurpose": "development/model selection only, not final test",
    })


if __name__ == "__main__":
    main()
