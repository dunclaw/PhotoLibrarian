"""Export the official RAM++ checkpoint to PhotoLibrarian's ONNX contract.

This is deliberately a source-based export, not a download of an ONNX file from
an unrelated project.  The command downloads the pinned upstream source and
checkpoint, verifies every source/environment input, exports a self-contained
numeric inference graph, and verifies its byte identity before writing provenance.

The resulting graph has one input, ``image`` (float32 NCHW, 384x384), and three
outputs: ``logits``, ``probabilities``, and ``targets``.  ``targets`` retains
the application's existing binary-mask contract; the other outputs make parity
and future threshold review possible without another model export.

The byte-reproducible build requires CPython 3.12.0 on Windows AMD64 and the
complete hash-locked CPU environment prepared by New-RamPlusExportEnvironment.ps1.
It intentionally does not use remote Python code or an ONNX file from a third party.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib
import importlib.metadata
import json
from pathlib import Path
import platform
import re
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
BUILD_LOCK_PATH = Path(__file__).with_name("ram-plus-build-lock.json")
REQUIREMENTS_PATH = Path(__file__).with_name("requirements-ram-plus-export.txt")


def build_lock() -> dict:
    lock = json.loads(BUILD_LOCK_PATH.read_text(encoding="utf-8"))
    if lock["schemaVersion"] != 1 or lock["sourceRevision"] != SOURCE_REVISION:
        raise ValueError("RAM++ build lock does not match the pinned source")
    return lock


def locked_packages() -> dict[str, str]:
    packages = {}
    for line in REQUIREMENTS_PATH.read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        match = re.fullmatch(r"([A-Za-z0-9_.-]+)==(\S+) --hash=sha256:([a-f0-9]{64})", line)
        if match is None:
            raise ValueError(f"Invalid hash-locked requirement: {line}")
        packages[match[1]] = match[2]
    return packages


def verify_environment() -> dict[str, str]:
    expected = build_lock()["python"]
    actual = {
        "version": platform.python_version(),
        "implementation": platform.python_implementation(),
        "system": platform.system(),
        "machine": platform.machine(),
    }
    if actual != expected:
        raise ValueError(f"Unsupported RAM++ build interpreter/platform: {actual}; expected {expected}")
    packages = locked_packages()
    for package, version in packages.items():
        try:
            installed = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError as error:
            raise ValueError(f"Missing locked export dependency: {package}=={version}") from error
        if installed != version:
            raise ValueError(
                f"Export dependency drift: {package}=={installed}; required {version}. "
                "Create the hash-locked environment with New-RamPlusExportEnvironment.ps1."
            )
    return packages


def verify_source(source: Path) -> None:
    entries = build_lock()["sourceFiles"]
    expected = {entry["path"] for entry in entries}
    for entry in entries:
        path = source / entry["path"]
        if (not path.is_file() or path.is_symlink() or
                path.stat().st_size != entry["size"] or sha256(path) != entry["sha256"]):
            raise ValueError(f"Pinned RAM++ source file differs: {entry['path']}")
    # Module search must not pick up unpinned code beside or inside the ram package.
    for path in source.rglob("*.py"):
        relative = path.relative_to(source).as_posix()
        if ".git" in path.relative_to(source).parts:
            continue
        if (relative.startswith("ram/") or path.parent == source) and relative not in expected:
            raise ValueError(f"Unpinned Python source in export import path: {relative}")


def verify_artifact(path: Path, expected: dict) -> None:
    if path.stat().st_size != expected["sizeBytes"] or sha256(path) != expected["sha256"]:
        raise ValueError(
            f"Export did not reproduce the locked artifact: {path.name}; "
            f"actual size={path.stat().st_size}, SHA256={sha256(path)}"
        )


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def text_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


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
        for entry in bundle.infolist():
            path = Path(entry.filename)
            if path.is_absolute() or ".." in path.parts or (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError(f"Unsafe upstream archive entry: {entry.filename}")
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
    modification_notice = output / "MODIFICATIONS.txt"
    modification_notice.write_text(
        "PhotoLibrarian modification notice\n"
        "The upstream RAM++ PyTorch checkpoint was converted to ONNX by "
        "PhotoLibrarian using the pinned exporter and source/checkpoint "
        "revisions recorded in ram_plus_manifest.json. The exported graph "
        "preserves the upstream 4,585-label vocabulary and detection "
        "thresholds. It exposes logits, probabilities, and the application's "
        "binary targets mask. See the manifest for the input/output and "
        "preprocessing contract.\n",
        encoding="utf-8",
        newline="\n",
    )
    notices.append({
        "file": modification_notice.name,
        "sha256": sha256(modification_notice),
    })
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
        "numpy": "numpy",
        "scipy": "scipy",
        "Pillow": "PIL",
    }
    return {
        package: getattr(importlib.import_module(module), "__version__", "unknown")
        for package, module in modules.items()
    }


def export_model(source: Path, checkpoint: Path, output: Path, thresholds: list[float]) -> None:
    import torch
    from torch import nn
    if torch.version.cuda is not None:
        raise ValueError("Reproducible RAM++ export requires the locked CPU PyTorch distribution")
    torch.set_num_threads(1)
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)
    torch.set_default_dtype(torch.float32)

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


def write_modifications(output: Path) -> None:
    (output / "MODIFICATIONS.txt").write_text(
        "PhotoLibrarian RAM++ ONNX conversion\n"
        f"Upstream source: {SOURCE_REPOSITORY}, revision {SOURCE_REVISION}\n"
        f"Checkpoint: {CHECKPOINT_REPOSITORY}, revision {CHECKPOINT_REVISION}\n"
        "The upstream PyTorch inference graph was converted to ONNX opset 17.\n"
        "Tokenizer initialization was replaced for export only; the tagging graph "
        "does not use token embeddings.\n"
        "Description reweighting was vectorized for the fixed batch size of one.\n"
        "Named logits and probabilities outputs were added alongside the binary "
        "targets mask using the unchanged upstream per-class thresholds.\n"
        "Labels are the upstream vocabulary, serialized as UTF-8 with LF endings.\n"
        "No training, fine-tuning, quantization or threshold calibration was performed.\n"
        "Retain recognize-anything-LICENSE and recognize-anything-NOTICE.txt, "
        "including all embedded third-party notices, with this conversion.\n",
        encoding="utf-8", newline="\n",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--checkpoint-dir", type=Path)
    args = parser.parse_args()
    installed_packages = verify_environment()
    output = args.output.resolve()
    source = (
        args.source_dir.resolve()
        if args.source_dir
        else download_source(output / "source-cache")
    )
    verify_source(source)
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
    pending_model = output / "ram_plus_swin_large_14m.pending.onnx"
    export_model(source, checkpoint_dir / "ram_plus_swin_large_14m.pth", pending_model, thresholds)
    verify_artifact(pending_model, build_lock()["expectedModel"])
    pending_model.replace(model_path)
    (output / "ram_plus_swin_large_14m_labels.txt").write_text(
        "\n".join(labels) + "\n", encoding="utf-8", newline="\n"
    )
    verify_artifact(output / "ram_plus_swin_large_14m_labels.txt", build_lock()["expectedLabels"])
    notices = copy_notices(source, output)
    write_modifications(output)
    notices.append({
        "file": "MODIFICATIONS.txt",
        "sha256": sha256(output / "MODIFICATIONS.txt"),
    })
    external_data = sorted(
        path.name for path in output.iterdir()
        if path.name.startswith(model_path.name + ".data")
    )
    manifest = {
        "schemaVersion": 1,
        "modelFile": model_path.name,
        "modelSha256": sha256(model_path),
        "modelSizeBytes": model_path.stat().st_size,
        "externalDataFiles": [
            {"file": name, "sha256": sha256(output / name)}
            for name in external_data
        ],
        "labelsFile": "ram_plus_swin_large_14m_labels.txt",
        "labelsSha256": sha256(output / "ram_plus_swin_large_14m_labels.txt"),
        "labelsSizeBytes": (output / "ram_plus_swin_large_14m_labels.txt").stat().st_size,
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
        "checkpoints": [
            {"file": name, "sizeBytes": size, "sha256": checksum}
            for name, (size, checksum) in CHECKPOINTS.items()
        ],
        "labelGitBlobSha1": LABEL_GIT_BLOB_SHA1,
        "thresholdGitBlobSha1": THRESHOLD_GIT_BLOB_SHA1,
        "license": "Apache-2.0",
        "redistributionNotices": notices,
        "derivation": "PhotoLibrarian ONNX conversion of the pinned upstream RAM++ PyTorch source and checkpoint.",
        "exportEnvironment": {
            "python": sys.version,
            "packages": package_versions(),
            "opset": 17,
            "cpuOnly": True,
            "deterministicAlgorithms": True,
            "intraOpThreads": 1,
            "interOpThreads": 1,
            "installedDistributions": installed_packages,
            "textFingerprintEncoding": "UTF-8 with LF line endings",
            "buildLockSha256": text_sha256(BUILD_LOCK_PATH),
            "exporterSha256": text_sha256(Path(__file__)),
            "requirementsSha256": text_sha256(
                REQUIREMENTS_PATH
            ),
        },
    }
    (output / "ram_plus_manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n"
    )
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
