"""Download an explicitly limited selected subset from the official image mirror.

Image-license metadata is retained, not treated as clearance for redistribution.
No personal data, arbitrary URLs, test images or model weights are accessed.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import urllib.request

from prepare_dataset import file_hash, unique_object
from select_open_images import IMAGE_ID, LICENSES


MAX_IMAGE_BYTES = 20 * 1024 * 1024
MIRROR = "https://open-images-dataset.s3.amazonaws.com"


def read_selection(path: Path) -> list[dict]:
    records = []
    ids = set()
    with path.open(encoding="utf-8") as stream:
        for line in stream:
            row = json.loads(line, object_pairs_hook=unique_object)
            image, split = row["sourceId"], row["originalSplit"]
            if not IMAGE_ID.fullmatch(image) or split not in {"train", "validation"}:
                raise ValueError("Only selected train/validation image IDs may be downloaded")
            if image in ids:
                raise ValueError(f"Repeated source ID: {image}")
            ids.add(image)
            metadata = row["provenance"]
            if metadata["License"] not in LICENSES or not metadata["Author"].strip():
                raise ValueError(f"Image lacks allowlisted license/attribution: {image}")
            if metadata["ImageID"] != image or metadata["Subset"] != split:
                raise ValueError(f"Image provenance identity mismatch: {image}")
            if not row["labels"] or not set(row["labels"].values()) <= {"positive", "negative"}:
                raise ValueError(f"Invalid partial labels for {image}")
            records.append(row)
    if not records:
        raise ValueError("Selection must not be empty")
    return records


def download(row: dict, output: Path) -> dict:
    image, split = row["sourceId"], row["originalSplit"]
    url = f"{MIRROR}/{split}/{image}.jpg"
    path = output / "images" / split / f"{image}.jpg"
    path.parent.mkdir(parents=True, exist_ok=True)
    receipt_path = path.with_suffix(".source.json")
    if path.exists() or receipt_path.exists():
        if not (path.exists() and receipt_path.exists()):
            raise ValueError(f"Incomplete image cache entry: {path}")
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        if receipt["url"] != url or receipt["sha256"] != file_hash(path):
            raise ValueError(f"Cached image identity/hash mismatch: {path}")
    else:
        pending = path.with_suffix(".downloading")
        if pending.exists():
            raise ValueError(f"Unfinished image download: {pending}; inspect before retrying")
        with urllib.request.urlopen(url, timeout=120) as response:
            size = int(response.headers["Content-Length"])
            if not 4 <= size <= MAX_IMAGE_BYTES:
                raise ValueError(f"Image outside byte limit: {url} ({size})")
            sha256 = hashlib.sha256()
            received = 0
            first = last = b""
            with pending.open("xb") as stream:
                for block in iter(lambda: response.read(1024 * 1024), b""):
                    received += len(block)
                    if received > size:
                        raise ValueError(f"Image exceeded declared size: {url}")
                    first = (first + block)[:2]
                    last = (last + block)[-2:]
                    stream.write(block)
                    sha256.update(block)
            if received != size or first != b"\xff\xd8" or last != b"\xff\xd9":
                raise ValueError(f"Incomplete/non-JPEG image: {url}")
            receipt = {"url": url, "bytes": size, "sha256": sha256.hexdigest(),
                       "etag": response.headers.get("ETag")}
        pending.replace(path)
        receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return {**row, "path": str(path.relative_to(output)), "download": receipt,
            "trainingReady": False}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--limit", type=int, required=True, help="Explicit bound, at most 10000 images")
    parser.add_argument("--workers", type=int, default=4)
    args = parser.parse_args()
    if not 1 <= args.limit <= 10000 or not 1 <= args.workers <= 8:
        parser.error("--limit must be 1..10000 and --workers 1..8")
    rows = read_selection(args.selection)[:args.limit]
    if not rows:
        raise ValueError("No images selected")
    identity = {"selectionSha256": file_hash(args.selection), "limit": args.limit,
                "selectedImages": len(rows)}
    args.output.mkdir(parents=True, exist_ok=True)
    identity_path = args.output / "selection-identity.json"
    if identity_path.exists():
        if json.loads(identity_path.read_text(encoding="utf-8")) != identity:
            raise ValueError("Output belongs to a different selection/limit; use a new directory")
    elif any(args.output.iterdir()):
        raise ValueError("Output must be empty or contain this selection's identity")
    else:
        identity_path.write_text(json.dumps(identity, indent=2) + "\n", encoding="utf-8")
    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        records = list(executor.map(lambda row: download(row, args.output), rows))
    pending = args.output / "download-manifest.jsonl.writing"
    with pending.open("w", encoding="utf-8", newline="\n") as stream:
        for record in records:
            stream.write(json.dumps(record, sort_keys=True) + "\n")
    pending.replace(args.output / "download-manifest.jsonl")
    print(f"Downloaded/verified {len(records)} images. Research data only; no training approval.")


if __name__ == "__main__":
    main()
