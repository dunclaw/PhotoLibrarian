"""Fetch selected official V7 metadata, never the full image dataset."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
from pathlib import Path
import urllib.request

from prepare_dataset import file_hash


BASE = "https://storage.googleapis.com/openimages/"
SOURCES = {
    "classes": "v7/oidv7-class-descriptions.csv",
    "train-labels": "v7/oidv7-train-annotations-human-imagelabels.csv",
    "validation-labels": "v7/oidv7-val-annotations-human-imagelabels.csv",
    "train-metadata": "2018_04/train/train-images-boxable-with-rotation.csv",
    "validation-metadata": "2018_04/validation/validation-images-with-rotation.csv",
}
PINNED_SHA256 = {
    "classes": "84a4373a0efb7fd6d93fe19b0e7ceb6c1b855c233d13b9b78a9a33655c9fdce3",
    "train-labels": "0237e1e4080291b959ceb99484a8f2d723aeeb7d15b2adf60337f2c027174116",
    "validation-labels": "92ddbdfceb3626e044df5e89100b24f6c22a79c1888a4bddd00a6f231d86d56a",
    "train-metadata": "05f3d68dbbb03728d1a37e51479f4f35c062b871e1a6cae8c4cefbe0e0c80ed0",
    "validation-metadata": "ed93a0e121fe345effdfc7359b848dbc64a1ff6778c8c73563157cb500b33a17",
}


def fetch(key: str, directory: Path) -> dict:
    url = BASE + SOURCES[key]
    path = directory / Path(SOURCES[key]).name
    receipt = path.with_suffix(path.suffix + ".source.json")
    if path.exists() or receipt.exists():
        if not (path.exists() and receipt.exists()):
            raise ValueError(f"Incomplete cache entry: {path}; inspect it before retrying")
        record = json.loads(receipt.read_text(encoding="utf-8"))
        if record["url"] != url or record["sha256"] != file_hash(path) or record["sha256"] != PINNED_SHA256[key]:
            raise ValueError(f"Cached source identity/hash mismatch: {path}")
        return record
    directory.mkdir(parents=True, exist_ok=True)
    pending = path.with_suffix(path.suffix + ".downloading")
    if pending.exists():
        raise ValueError(f"Unfinished download exists: {pending}; inspect before retrying")
    with urllib.request.urlopen(url, timeout=120) as response:
        size = int(response.headers["Content-Length"])
        hashes = dict(item.strip().split("=", 1)
                      for header in response.headers.get_all("x-goog-hash", [])
                      for item in header.split(",") if "=" in item)
        if "md5" not in hashes:
            raise ValueError(f"No server MD5 for {url}")
        md5 = hashlib.md5()
        sha256 = hashlib.sha256()
        received = 0
        with pending.open("xb") as stream:
            for block in iter(lambda: response.read(1024 * 1024), b""):
                received += len(block)
                if received > size:
                    raise ValueError(f"Source exceeded declared size: {url}")
                stream.write(block)
                md5.update(block)
                sha256.update(block)
        if received != size or base64.b64encode(md5.digest()).decode() != hashes["md5"]:
            raise ValueError(f"Incomplete/corrupt download: {url}")
        if sha256.hexdigest() != PINNED_SHA256[key]:
            raise ValueError(f"Source changed from the reviewed SHA-256: {url}")
        record = {
            "url": url, "bytes": received, "sha256": sha256.hexdigest(),
            "serverMd5Base64": hashes["md5"], "etag": response.headers.get("ETag"),
            "lastModified": response.headers.get("Last-Modified"),
            "generation": response.headers.get("x-goog-generation"),
        }
    pending.replace(path)
    receipt.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    return record


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sources", nargs="+", choices=sorted(SOURCES),
                        default=["classes", "validation-labels", "validation-metadata"])
    args = parser.parse_args()
    for key in args.sources:
        record = fetch(key, args.output)
        print(f"{key}: {record['bytes']} bytes; SHA-256 {record['sha256']}", flush=True)


if __name__ == "__main__":
    main()
