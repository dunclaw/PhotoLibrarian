"""Measure verified-label coverage and select a bounded, provenance-filtered subset.

No image downloads or photo writes. Original train/validation membership is
retained; no test annotations are read. Selection is not training approval.
"""

from __future__ import annotations

import argparse
from collections import Counter
import csv
import hashlib
import heapq
import json
from pathlib import Path
import re
from urllib.parse import urlparse

from prepare_dataset import file_hash, vocabulary


VERIFIED = {"verification", "crowdsource-verification"}
IMAGE_ID = re.compile(r"[0-9a-f]{16}")
LICENSES = {"http://creativecommons.org/licenses/by/2.0/",
            "https://creativecommons.org/licenses/by/2.0/"}


def mapping(path: Path, classes: Path, concepts: list[dict]) -> list[dict]:
    with classes.open(encoding="utf-8-sig", newline="") as stream:
        catalog = dict(csv.reader(stream))
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        fields = {"label", "mids", "sourceNames", "negativePolicy", "rationale"}
        if set(reader.fieldnames or []) != fields or len(reader.fieldnames) != len(fields):
            raise ValueError("Unexpected mapping columns")
        rows = list(reader)
    if len(rows) != len(concepts) or {row["label"] for row in rows} != {c["label"] for c in concepts}:
        raise ValueError("Mapping must cover each pilot label exactly once")
    for row in rows:
        if set(row) != fields or any(not isinstance(v, str) for v in row.values()):
            raise ValueError("Malformed mapping row")
        if not row["rationale"] or row["negativePolicy"] not in {"all", "positive-only", "defer"}:
            raise ValueError("Every mapping needs an explicit transfer policy and rationale")
        mids = row["mids"].split("|") if row["mids"] else []
        names = row["sourceNames"].split("|") if row["sourceNames"] else []
        if row["negativePolicy"] == "defer":
            if mids or names:
                raise ValueError("Deferred concepts must not transfer source labels")
        elif not mids or len(mids) != len(names) or len(mids) != len(set(mids)):
            raise ValueError("Mapped concepts need distinct MIDs and corresponding names")
        for mid, name in zip(mids, names):
            if catalog.get(mid) != name:
                raise ValueError(f"Class catalog mismatch for {mid}: expected {name}")
        row["midList"] = mids
    return rows


def verified_rows(path: Path, mids: set[str]):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != ["ImageID", "Source", "LabelName", "Confidence"]:
            raise ValueError("Expected official image-level annotation columns")
        for row in reader:
            if row["Source"] not in VERIFIED or row["LabelName"] not in mids:
                continue
            if row["Confidence"] not in {"0", "1", "0.0", "1.0"} or not IMAGE_ID.fullmatch(row["ImageID"]):
                raise ValueError("Invalid verified annotation ID/confidence")
            yield row["ImageID"], row["LabelName"], int(float(row["Confidence"]))


def target_states(source: dict[str, int], mappings: list[dict]) -> dict[str, str]:
    labels = {}
    for item in mappings:
        mids = item["midList"]
        if not mids:
            continue
        if any(source.get(mid) == 1 for mid in mids):
            labels[item["label"]] = "positive"
        elif item["negativePolicy"] == "all" and all(source.get(mid) == 0 for mid in mids):
            labels[item["label"]] = "negative"
    return labels


def select(annotations: Path, metadata: Path, mappings: list[dict], split: str,
           per_state: int, max_images: int, seed: str) -> tuple[list[dict], dict]:
    if per_state < 1 or max_images < 1 or split not in {"train", "validation"}:
        raise ValueError("Positive limits and original train/validation split are required")
    mids = {mid for row in mappings for mid in row["midList"]}
    negative_mids = {mid for row in mappings if row["negativePolicy"] == "all"
                     for mid in row["midList"]}
    counts: Counter = Counter()
    heaps: dict[tuple[str, int], list] = {}
    members: dict[tuple[str, int], set] = {}

    def rank(image: str) -> int:
        return int(hashlib.sha256(f"{seed}:{split}:{image}".encode()).hexdigest(), 16)

    for image, mid, state in verified_rows(annotations, mids):
        key = mid, state
        counts[key] += 1
        if state == 0 and mid not in negative_mids:
            continue
        heap = heaps.setdefault(key, [])
        kept = members.setdefault(key, set())
        if image in kept:
            continue
        entry = -rank(image), image
        if len(heap) < per_state:
            heapq.heappush(heap, entry)
            kept.add(image)
        elif entry > heap[0]:
            removed = heapq.heapreplace(heap, entry)
            kept.remove(removed[1])
            kept.add(image)
    candidates = {image for kept in members.values() for image in kept}
    states: dict[str, dict[str, int]] = {image: {} for image in candidates}
    for image, mid, state in verified_rows(annotations, mids):
        if image not in states:
            continue
        source = states[image]
        if mid in source and source[mid] != state:
            raise ValueError(f"Conflicting human labels on image {image}, MID {mid}")
        source[mid] = state

    eligible: dict[str, dict] = {}
    rejection: Counter = Counter()
    found = set()
    with metadata.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        required = {"ImageID", "Subset", "License", "Author", "OriginalLandingURL", "OriginalMD5"}
        if not required <= set(reader.fieldnames or []):
            raise ValueError("Expected original image provenance metadata columns")
        for row in reader:
            image = row["ImageID"]
            if image not in candidates:
                continue
            if image in found:
                raise ValueError(f"Duplicate metadata for {image}")
            found.add(image)
            if row["Subset"] != split:
                raise ValueError(f"Metadata source split mismatch for {image}")
            if row["License"] not in LICENSES:
                rejection["licenseNotAllowlisted"] += 1
                continue
            if not row["Author"].strip() or urlparse(row["OriginalLandingURL"]).scheme not in {"http", "https"}:
                rejection["missingAttribution"] += 1
                continue
            labels = target_states(states[image], mappings)
            if not labels:
                rejection["noTransferableKnownLabels"] += 1
                continue
            eligible[image] = {
                "id": f"open-images-v7:{image}", "sourceId": image, "originalSplit": split,
                "labels": labels, "sourceLabels": dict(sorted(states[image].items())),
                "provenance": {key: row[key] for key in sorted(required | {
                    "Rotation", "OriginalURL", "OriginalSize", "AuthorProfileURL", "Title"})
                               if key in row},
                "licenseVerifiedAtSource": False, "groupReviewComplete": False,
            }
    selected = [eligible[image] for image in sorted(eligible, key=lambda x: (rank(x), x))[:max_images]]
    selected.sort(key=lambda row: row["sourceId"])
    rejection["metadataUnavailable"] = len(candidates - found)
    rejection["globalCap"] = max(0, len(eligible) - max_images)
    coverage = []
    for item in mappings:
        label = item["label"]
        coverage.append({
            "label": label, "policy": item["negativePolicy"], "mids": item["midList"],
            "sourcePositiveAnnotationRows": sum(counts[mid, 1] for mid in item["midList"]),
            "sourceNegativeAnnotationRows": sum(counts[mid, 0] for mid in item["midList"]),
            "selectedPositiveImages": sum(row["labels"].get(label) == "positive" for row in selected),
            "selectedNegativeImages": sum(row["labels"].get(label) == "negative" for row in selected),
            "selectedUnknownImages": sum(label not in row["labels"] for row in selected),
        })
    report = {
        "schemaVersion": 1, "originalSplit": split, "seed": seed,
        "perMidStateCandidateCap": per_state, "maxImages": max_images,
        "candidateImages": len(candidates), "selectedImages": len(selected),
        "rejected": dict(sorted(rejection.items())), "coverage": coverage,
        "trainingReady": False, "productionApproved": False,
        "warnings": [
            "Source counts are annotation rows, not deduplicated independent examples.",
            "Candidate sampling precedes metadata filtering; missing metadata can reduce coverage.",
            "Train metadata covers the boxable subset, not all V7 image-level examples.",
            "License metadata is a filter, not verification of current original-image rights.",
            "Near-duplicates/events and cross-split overlap still require checks before training.",
            "Positive-only activity and ambiguous source mappings need a small visual quality sample.",
            "Unknown labels are omitted, not negative; selection is not a representative prevalence sample.",
        ],
    }
    return selected, report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--annotations", type=Path, required=True)
    parser.add_argument("--metadata", type=Path, required=True)
    parser.add_argument("--classes", type=Path, required=True)
    parser.add_argument("--mapping", type=Path, default=Path(__file__).with_name("open-images-mapping.csv"))
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("pilot-vocabulary.csv"))
    parser.add_argument("--split", choices=["train", "validation"], required=True)
    parser.add_argument("--per-state", type=int, default=100)
    parser.add_argument("--max-images", type=int, default=5000)
    parser.add_argument("--seed", default="open-images-pilot-v1")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("--output must be new; do not overwrite a frozen selection")
    concepts, _ = vocabulary(args.vocabulary)
    mappings = mapping(args.mapping, args.classes, concepts)
    records, report = select(args.annotations, args.metadata, mappings, args.split,
                             args.per_state, args.max_images, args.seed)
    if not records:
        raise ValueError("No eligible images; inspect source coverage and provenance")
    report["inputs"] = {
        name: {"file": path.name, "sha256": file_hash(path)}
        for name, path in [("annotations", args.annotations), ("metadata", args.metadata),
                           ("classes", args.classes), ("mapping", args.mapping),
                           ("vocabulary", args.vocabulary)]
    }
    args.output.mkdir(parents=True, exist_ok=False)
    with (args.output / "selection.jsonl").open("w", encoding="utf-8", newline="\n") as stream:
        for record in records:
            stream.write(json.dumps(record, sort_keys=True) + "\n")
    (args.output / "coverage.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    with (args.output / "coverage.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(report["coverage"][0]))
        writer.writeheader()
        writer.writerows(report["coverage"])
    (args.output / "image-list.txt").write_text(
        "".join(f"{args.split}/{row['sourceId']}\n" for row in records), encoding="utf-8")
    print(f"Selected {len(records)} {args.split} IDs; see coverage.json. No photos downloaded.")


if __name__ == "__main__":
    main()
