"""Export the official RAM++ checkpoint to PhotoLibrarian's ONNX contract.

This is deliberately a source-based export, not a download of an ONNX file from
an unrelated project.  The command downloads the pinned upstream source and
checkpoint, verifies every input, exports the numeric inference graph with
external ONNX data, and writes a provenance manifest beside the result.

The resulting graph has one input, ``image`` (float32 NCHW, 384x384), and three
outputs: ``logits``, ``probabilities``, and ``targets``.  ``targets`` retains
the application's existing binary-mask contract; the other outputs make parity
and future threshold review possible without another model export.

The export requires Python 3.12, PyTorch, torchvision, transformers, timm, and
the packages in requirements-ram-plus-export.txt.  It intentionally does not
use remote Python code or an ONNX file from a third party.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import json
from pathlib import Path
import shutil
import sys
import urllib.request
import zipfile


SOURCE_REVISION = "7cb804a8609e9f4b1a50b7f31436d2df40bb9481"
SOURCE_REPOSITORY = "https://github.com/xinyu1205/recognize-anything"
CHECKPOINT_REVISION = "84d4aee3a0265c4e0df1f714f0572011d1bf2ec3"
CHECKPOINT_REPOSITORY = "https://huggingface.co/xinyu1205/recognize-anything-plus-model"
LABEL_PATH = "ram/data/ram_tag_list.txt"
THRESHOLD_PATH = "ram/data/ram_tag_list_threshold.txt"
LABEL_GIT_BLOB_SHA1 = "49c840b71915f639fb79cd83ac4a3e313cfbc2b1"
THRESHOLD_GIT_BLOB_SHA1 = "0472b23c25903900c0dde68fffc9a6a6755f5117"
CHECKPOINTS = {
    "ram_plus_swin_large_14m.pth": (
        3_010_210_801,
        "497c178836ba66698ca226c7895317e6e800034be986452dbd2593298d50e87d",
    ),
    "ram_plus_tag_embedding_class_4585_des_51.pth": (
        478_895_553,
        "072918604bfd95a8b45596e2677453cfb4335e9dffca2a1b2cb9834decb4dba8",
    ),
}
LABEL_COUNT = 4_585
IMAGE_SIZE = 384
OUTPUT_NAMES = ("logits", "probabilities", "targets")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def git_blob_sha1(path: Path) -> str:
    content = path.read_bytes()
    header = f"blob {len(content)}\0".encode("ascii")
    return hashlib.sha1(header + content).hexdigest()


def download(url: str, destination: Path, size: int, checksum: str) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists() and (
        destination.stat().st_size != size or sha256(destination) != checksum
    ):
        destination.unlink()
    if not destination.exists():
        pending = destination.with_suffix(destination.suffix + ".download")
        request = urllib.request.Request(
            url, headers={"User-Agent": "PhotoLibrarian-RAM-Plus-Exporter/1"}
        )
        with urllib.request.urlopen(request, timeout=300) as response, pending.open("wb") as stream:
            shutil.copyfileobj(response, stream, length=1024 * 1024)
        if pending.stat().st_size != size or sha256(pending) != checksum:
            pending.unlink(missing_ok=True)
            raise ValueError(f"Downloaded file failed verification: {url}")
        pending.replace(destination)


def download_source(destination: Path) -> Path:
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination / "recognize-anything-source.zip"
    url = f"{SOURCE_REPOSITORY}/archive/{SOURCE_REVISION}.zip"
    # GitHub archive checksums are not stable metadata, so retain the immutable
    # commit URL in the manifest and verify the files used by the export below.
    request = urllib.request.Request(
        url, headers={"User-Agent": "PhotoLibrarian-RAM-Plus-Exporter/1"}
    )
    with urllib.request.urlopen(request, timeout=300) as response, archive.open("wb") as stream:
        shutil.copyfileobj(response, stream, length=1024 * 1024)
    source = destination / "source"
    if source.exists():
        shutil.rmtree(source)
    source.mkdir()
    with zipfile.ZipFile(archive) as bundle:
        bundle.extractall(source)
    roots = list(source.iterdir())
    if len(roots) != 1 or not (roots[0] / "ram").is_dir():
        raise ValueError("Unexpected recognize-anything source archive")
    return roots[0]


def load_labels(source: Path) -> tuple[list[str], list[float]]:
    labels_path = source / LABEL_PATH
    thresholds_path = source / THRESHOLD_PATH
    if git_blob_sha1(labels_path) != LABEL_GIT_BLOB_SHA1:
        raise ValueError("Pinned upstream label vocabulary hash does not match")
    if git_blob_sha1(thresholds_path) != THRESHOLD_GIT_BLOB_SHA1:
        raise ValueError("Pinned upstream threshold vector hash does not match")
    labels = labels_path.read_text(encoding="utf-8").splitlines()
    thresholds = [
        float(line)
        for line in thresholds_path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(labels) != LABEL_COUNT or len(thresholds) != LABEL_COUNT:
        raise ValueError("RAM++ label and threshold files must contain 4,585 entries")
    if any(not label.strip() for label in labels):
        raise ValueError("RAM++ labels must not contain blank entries")
    return labels, thresholds


def copy_notices(source: Path, output: Path) -> list[dict[str, str]]:
    notices = []
    for name in ("LICENSE", "NOTICE.txt"):
        path = source / name
        if not path.is_file():
            raise ValueError(f"Pinned upstream source is missing {name}")
        destination = output / f"recognize-anything-{name}"
        shutil.copyfile(path, destination)
        notices.append({"file": destination.name, "sha256": sha256(destination)})
    return notices


def package_versions() -> dict[str, str]:
    modules = {
        "torch": "torch",
        "torchvision": "torchvision",
        "onnx": "onnx",
        "onnxruntime": "onnxruntime",
        "transformers": "transformers",
        "tokenizers": "tokenizers",
        "timm": "timm",
        "fairscale": "fairscale",
    }
    return {
        package: getattr(importlib.import_module(module), "__version__", "unknown")
        for package, module in modules.items()
    }


def export_model(source: Path, checkpoint: Path, output: Path, thresholds: list[float]) -> None:
    import torch
    from torch import nn

    sys.path.insert(0, str(source))
    ram_plus_module = importlib.import_module("ram.models.ram_plus")

    class ExportTokenizer:
        """The tagging graph deletes token embeddings, so no tokenizer is needed."""

        def __len__(self) -> int:
            return 30_524

    # RAM++ initializes a tokenizer only to resize an embedding layer that it
    # immediately deletes. Avoid an unpinned transformers download at export.
    ram_plus_module.init_tokenizer = lambda _: ExportTokenizer()

    class ExportWrapper(nn.Module):
        def __init__(self, model: nn.Module, threshold_values: list[float]) -> None:
            super().__init__()
            self.model = model
            self.register_buffer("thresholds", torch.tensor(threshold_values))

        def forward(self, image: torch.Tensor) -> tuple[torch.Tensor, ...]:
            image_embeds = self.model.image_proj(self.model.visual_encoder(image))
            image_atts = torch.ones(
                image_embeds.size()[:-1], dtype=torch.long, device=image.device
            )
            image_cls = image_embeds[:, 0, :]
            image_cls = image_cls / image_cls.norm(dim=-1, keepdim=True)
            scale = self.model.reweight_scale.exp()
            logits = (scale * image_cls @ self.model.label_embed.t()).reshape(
                image.shape[0], self.model.num_class, -1
            )
            weights = torch.softmax(logits, dim=2)
            embeddings = self.model.label_embed.reshape(-1, 51, 512)
            label_embed = (weights.unsqueeze(-1) * embeddings).sum(dim=2)
            label_embed = torch.relu(self.model.wordvec_proj(label_embed))
            tagging = self.model.tagging_head(
                encoder_embeds=label_embed,
                encoder_hidden_states=image_embeds,
                encoder_attention_mask=image_atts,
                return_dict=False,
                mode="tagging",
            )
            logits = self.model.fc(tagging[0]).squeeze(-1)
            probabilities = torch.sigmoid(logits)
            targets = (probabilities > self.thresholds).to(torch.float32)
            return logits, probabilities, targets

    torch.manual_seed(0)
    model = ram_plus_module.ram_plus(
        pretrained=str(checkpoint),
        image_size=IMAGE_SIZE,
        vit="swin_l",
    ).eval()
    wrapper = ExportWrapper(model, thresholds).eval()
    sample = torch.zeros(1, 3, IMAGE_SIZE, IMAGE_SIZE, dtype=torch.float32)
    output.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(
        wrapper,
        sample,
        str(output),
        input_names=["image"],
        output_names=list(OUTPUT_NAMES),
        opset_version=17,
        dynamo=False,
        external_data=True,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--checkpoint-dir", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    source = (
        args.source_dir.resolve()
        if args.source_dir
        else download_source(output / "source-cache")
    )
    checkpoint_dir = (args.checkpoint_dir or output / "source-cache" / "checkpoint").resolve()
    for name, (size, checksum) in CHECKPOINTS.items():
        download(
            f"{CHECKPOINT_REPOSITORY}/resolve/{CHECKPOINT_REVISION}/{name}",
            checkpoint_dir / name,
            size,
            checksum,
        )
    labels, thresholds = load_labels(source)
    model_path = output / "ram_plus_swin_large_14m.onnx"
    export_model(source, checkpoint_dir / "ram_plus_swin_large_14m.pth", model_path, thresholds)
    (output / "ram_plus_swin_large_14m_labels.txt").write_text(
        "\n".join(labels) + "\n", encoding="utf-8", newline="\n"
    )
    notices = copy_notices(source, output)
    external_data = sorted(
        path.name for path in output.iterdir()
        if path.name.startswith(model_path.name + ".data")
    )
    manifest = {
        "schemaVersion": 1,
        "modelFile": model_path.name,
        "modelSha256": sha256(model_path),
        "externalDataFiles": [
            {"file": name, "sha256": sha256(output / name)}
            for name in external_data
        ],
        "labelsFile": "ram_plus_swin_large_14m_labels.txt",
        "labelsSha256": sha256(output / "ram_plus_swin_large_14m_labels.txt"),
        "labelCount": len(labels),
        "input": {"name": "image", "dtype": "float32", "shape": [1, 3, 384, 384]},
        "outputs": [
            {"name": name, "dtype": "float32", "shape": [1, 4585]}
            for name in OUTPUT_NAMES
        ],
        "preprocessing": {
            "resize": [384, 384],
            "normalizationMean": [0.485, 0.456, 0.406],
            "normalizationStd": [0.229, 0.224, 0.225],
            "layout": "NCHW",
        },
        "sourceRepository": SOURCE_REPOSITORY,
        "sourceRevision": SOURCE_REVISION,
        "checkpointRepository": CHECKPOINT_REPOSITORY,
        "checkpointRevision": CHECKPOINT_REVISION,
        "labelGitBlobSha1": LABEL_GIT_BLOB_SHA1,
        "thresholdGitBlobSha1": THRESHOLD_GIT_BLOB_SHA1,
        "license": "Apache-2.0",
        "redistributionNotices": notices,
        "derivation": "PhotoLibrarian ONNX conversion of the pinned upstream RAM++ PyTorch source and checkpoint.",
        "exportEnvironment": {
            "python": sys.version,
            "packages": package_versions(),
            "opset": 17,
        },
    }
    (output / "ram_plus_manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
