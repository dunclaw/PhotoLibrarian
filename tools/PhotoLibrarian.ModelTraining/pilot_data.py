"""Local decode, overlap exclusion and label gating for a research-only pilot."""

from __future__ import annotations

from collections import Counter
import json
import math
from pathlib import Path

from PIL import Image

from prepare_dataset import file_hash, fingerprint, unique_object, vocabulary


def oriented_image(path: Path, rotation: str) -> Image.Image:
    angle = float(rotation)
    if not math.isfinite(angle) or angle not in {0, 90, 180, 270}:
        raise ValueError(f"Missing/unsupported source rotation for {path}: {rotation}")
    with Image.open(path) as image:
        image.load()
        result = image.convert("RGB")
    # Open Images' recorded CCW orientation is authoritative, not a second
    # correction on top of possibly stale EXIF orientation.
    return result.rotate(angle, expand=True) if angle else result


def difference_hash(image: Image.Image) -> int:
    pixels = image.convert("L").resize((9, 8), Image.Resampling.BILINEAR).tobytes()
    result = 0
    for y in range(8):
        for x in range(8):
            result = (result << 1) | (pixels[y * 9 + x] > pixels[y * 9 + x + 1])
    return result


def load_downloads(manifest: Path, split: str) -> list[dict]:
    records = []
    ids = set()
    with manifest.open(encoding="utf-8") as stream:
        for line in stream:
            row = json.loads(line, object_pairs_hook=unique_object)
            if row["originalSplit"] != split:
                raise ValueError(f"Original source split mismatch in {manifest}")
            if row["sourceId"] in ids:
                raise ValueError(f"Duplicate source ID in {manifest}")
            ids.add(row["sourceId"])
            path = (manifest.parent / row["path"]).resolve(strict=True)
            if not path.is_relative_to(manifest.parent.resolve()):
                raise ValueError("Image path escaped download root")
            digest = file_hash(path)
            if digest != row["download"]["sha256"]:
                raise ValueError(f"Image checksum mismatch: {path}")
            rotation = row["provenance"].get("Rotation", "nan")
            if rotation.strip().lower() in {"", "nan", "none"}:
                records.append({**row, "path": str(path), "sha256": digest,
                                "decodeExclusion": "unknown-source-rotation"})
                continue
            image = oriented_image(path, rotation)
            records.append({**row, "path": str(path), "sha256": digest,
                            "dhash": difference_hash(image)})
    if not records:
        raise ValueError(f"No downloaded examples in {manifest}")
    return sorted(records, key=lambda row: row["sourceId"])


def near_hash(value: int, others: list[int], distance: int) -> bool:
    return any((value ^ other).bit_count() <= distance for other in others)


def audit_and_gate(train: list[dict], validation: list[dict], concepts: list[dict],
                   mappings: list[dict], regression_hashes: list[int], distance: int,
                   minimum_train: int, minimum_validation: int) -> tuple[list[dict], list[dict], list[str], dict]:
    if not 0 <= distance <= 10 or minimum_train < 1 or minimum_validation < 1:
        raise ValueError("Invalid distance or label evidence thresholds")
    parents = {row["label"]: row["parent"] for row in concepts}
    quarantine = []
    duplicate_states = {}
    conflicting_hashes = set()
    for row in train + validation:
        if row.get("decodeExclusion"):
            continue
        for label, state in row["labels"].items():
            key = row["sha256"], label
            if key in duplicate_states and duplicate_states[key] != state:
                conflicting_hashes.add(row["sha256"])
            duplicate_states[key] = state

    def valid(row: dict) -> bool:
        if row.get("decodeExclusion"):
            quarantine.append({"id": row["sourceId"], "reason": row["decodeExclusion"]})
            return False
        if row["sha256"] in conflicting_hashes:
            quarantine.append({"id": row["sourceId"], "reason": "duplicate-annotation-conflict"})
            return False
        labels = row["labels"]
        if not set(labels) <= set(parents) or not set(labels.values()) <= {"positive", "negative"}:
            raise ValueError("Unknown pilot label/state in downloaded manifest")
        conflicts = []
        for label, state in labels.items():
            if state != "positive":
                continue
            parent = parents[label]
            while parent:
                if labels.get(parent) == "negative":
                    conflicts.append([label, parent])
                parent = parents[parent]
        if conflicts:
            quarantine.append({"id": row["sourceId"], "reason": "source-hierarchy-conflict",
                               "conflicts": conflicts})
            return False
        return True

    validation = [row for row in validation if valid(row)]
    train = [row for row in train if valid(row)]
    filtered_validation = []
    seen = set()
    for row in validation:
        if near_hash(row["dhash"], regression_hashes, distance):
            quarantine.append({"id": row["sourceId"], "reason": "private-regression-near-hash"})
        elif row["sha256"] in seen:
            quarantine.append({"id": row["sourceId"], "reason": "duplicate-validation-bytes"})
        else:
            filtered_validation.append(row)
            seen.add(row["sha256"])
    validation = filtered_validation
    val_ids = {row["sourceId"] for row in validation}
    val_sha = {row["sha256"] for row in validation}
    val_md5 = {row["provenance"].get("OriginalMD5") for row in validation}
    val_md5 -= {"", "nan", None}
    val_authors = {row["provenance"].get("AuthorProfileURL") for row in validation}
    val_authors -= {"", "nan", None}
    val_hashes = [row["dhash"] for row in validation]
    train_sha = set()
    filtered_train = []
    for row in train:
        metadata = row["provenance"]
        if row["sourceId"] in val_ids or row["sha256"] in val_sha or metadata.get("OriginalMD5") in val_md5:
            reason = "validation-image-overlap"
        elif metadata.get("AuthorProfileURL") in val_authors:
            reason = "validation-author-overlap"
        elif near_hash(row["dhash"], val_hashes, distance):
            reason = "validation-near-hash"
        elif near_hash(row["dhash"], regression_hashes, distance):
            reason = "private-regression-near-hash"
        elif row["sha256"] in train_sha:
            reason = "duplicate-training-bytes"
        else:
            filtered_train.append(row)
            train_sha.add(row["sha256"])
            continue
        quarantine.append({"id": row["sourceId"], "reason": reason})
    train = filtered_train
    labels = []
    coverage = []
    policies = {row["label"]: row["negativePolicy"] for row in mappings}
    for item in concepts:
        label = item["label"]
        a = Counter(row["labels"].get(label, "unknown") for row in train)
        b = Counter(row["labels"].get(label, "unknown") for row in validation)
        enabled = (policies[label] == "all" and min(a["positive"], a["negative"]) >= minimum_train
                   and min(b["positive"], b["negative"]) >= minimum_validation)
        if enabled:
            labels.append(label)
        coverage.append({"label": label, "enabled": enabled, "policy": policies[label],
                         "trainPositive": a["positive"], "trainNegative": a["negative"],
                         "validationPositive": b["positive"], "validationNegative": b["negative"]})
    report = {
        "schemaVersion": 1, "labels": labels, "coverage": coverage,
        "trainImages": len(train), "validationImages": len(validation),
        "minimumTrainPositiveAndNegative": minimum_train,
        "minimumValidationPositiveAndNegative": minimum_validation,
        "quarantine": quarantine, "quarantineCounts": dict(Counter(row["reason"] for row in quarantine)),
        "nearHashDistance": distance, "privateRegressionImagesChecked": len(regression_hashes),
        "datasetFingerprint": fingerprint({
            "train": [[row["sourceId"], row["sha256"], row["labels"]] for row in train],
            "validation": [[row["sourceId"], row["sha256"], row["labels"]] for row in validation],
            "labels": labels,
        }),
        "researchOnly": True, "productionApproved": False,
        "limitations": [
            "Difference-hash screening is approximate; does not certify event/near-duplicate independence.",
            "Author-held-out validation is not an untouched final test or population-representative sample.",
            "Metadata licenses do not establish current image rights or pretrained-weight redistribution rights.",
            "Only all-policy mappings with known positives and negatives are eligible; others abstain.",
            "Partial-label metrics assess known entries, not exhaustive recall.",
        ],
    }
    if not labels or not train or not validation:
        raise ValueError(f"No usable pilot after evidence gates: {json.dumps(report)}")
    return train, validation, labels, report


def build_audit(train_manifest: Path, validation_manifest: Path, vocab: Path, mapping_path: Path,
                regression: Path | None, minimum_train: int, minimum_validation: int,
                additional_train: list[Path] | None = None,
                exclude_images: list[Path] | None = None):
    import csv
    train = load_downloads(train_manifest, "train")
    for manifest in additional_train or []:
        train.extend(load_downloads(manifest, "train"))
    if len({row["sourceId"] for row in train}) != len(train):
        raise ValueError("Duplicate source IDs across training manifests")
    train.sort(key=lambda row: row["sourceId"])
    validation = load_downloads(validation_manifest, "validation")
    references = []
    for manifest in exclude_images or []:
        references.extend(load_downloads(manifest, "validation"))
    train, reference_quarantine = exclude_training_references(train, references)
    concepts, _ = vocabulary(vocab)
    with mapping_path.open(encoding="utf-8", newline="") as stream:
        mappings = list(csv.DictReader(stream))
    if len(mappings) != len(concepts) or {r["label"] for r in mappings} != {r["label"] for r in concepts}:
        raise ValueError("Mapping does not exactly cover the pilot vocabulary")
    regression_hashes = []
    if regression is not None:
        from PIL import ImageOps
        photos = sorted(path for path in regression.rglob("*") if path.suffix.lower() in {".jpg", ".jpeg", ".png"})
        if not photos:
            raise ValueError("Supplied regression folder contains no supported photos")
        for path in photos:
            with Image.open(path) as image:
                regression_hashes.append(difference_hash(ImageOps.exif_transpose(image)))
    train, validation, labels, audit = audit_and_gate(
        train, validation, concepts, mappings, regression_hashes, 6, minimum_train, minimum_validation)
    audit["referenceImagesChecked"] = len(references)
    audit["quarantine"].extend(reference_quarantine)
    audit["quarantineCounts"] = dict(Counter(row["reason"] for row in audit["quarantine"]))
    return train, validation, labels, audit


def exclude_training_references(train: list[dict], references: list[dict]) -> tuple[list[dict], list[dict]]:
    identities = {row["sourceId"] for row in references}
    hashes = {row["sha256"] for row in references}
    authors = {row["provenance"].get("AuthorProfileURL") for row in references} - {"", "nan", None}
    md5 = {row["provenance"].get("OriginalMD5") for row in references} - {"", "nan", None}
    perceptual = [row["dhash"] for row in references if "dhash" in row]
    accepted, quarantine = [], []
    for row in train:
        if (row["sourceId"] in identities or row["sha256"] in hashes
                or row["provenance"].get("AuthorProfileURL") in authors
                or row["provenance"].get("OriginalMD5") in md5):
            reason = "reserved-reference-identity-or-author"
        elif "dhash" in row and near_hash(row["dhash"], perceptual, 6):
            reason = "reserved-reference-near-hash"
        else:
            accepted.append(row)
            continue
        quarantine.append({"id": row["sourceId"], "reason": reason})
    return accepted, quarantine
