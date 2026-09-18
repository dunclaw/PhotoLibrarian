"""Prepare reproducible, CPU-verified zero-shot bundles without reading photos.

Use Python 3.12 and requirements-prepare.txt in an isolated virtual environment:
    python prepare_zero_shot.py --output <external-assets-folder> --models siglip2,tinyclip
    python prepare_zero_shot.py --output <other-external-folder> --precision fp32

Downloads use immutable Hugging Face revisions, verify LFS SHA-256 (or Git blob
SHA-1 for ordinary files), and never execute remote code. Original learned
SigLIP scalars use exact HTTP ranges, not a falsely claimed full-file checksum.
Signed ConvInteger weights are recentered to equivalent unsigned weights for
ONNX Runtime CPU compatibility; the source models are never modified. Source models and
tokenizers remain in output/source-cache for reuse; only the image model and
modelId.json are needed for inference. No training, photo scanning or uploads.

The vocabulary fingerprint hashes the ordered label array serialized as compact
UTF-8 JSON (ensure_ascii=False). The full vocabulary file has a separate hash.
Thresholds are provisional: SigLIP sigmoid .1, TinyCLIP cosine .25, not softmax.
Preparation does not calibrate thresholds or validate a vocabulary against human scores.

reference.json describes generated synthetic PNGs, full little-endian float32
NCHW input tensors and Python CPU embeddings/scores for cross-runtime checks.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import shutil
import struct
import sys
import time
import urllib.request

import numpy as np
import onnx
import onnxruntime as ort
from PIL import Image
from transformers import AutoTokenizer, CLIPImageProcessor, SiglipImageProcessor


SPECS = {
    "siglip2": {
        "repo": "onnx-community/siglip2-base-patch16-224-ONNX",
        "revision": "ba1f3b0843f24bc5417d38e19c37b287d719b2f4",
        "originalRepo": "google/siglip2-base-patch16-224",
        "originalRevision": "75de2d55ec2d0b4efc50b3e9ad70dba96a7b2fa2",
        "license": "Apache-2.0",
        "int8": {
            "onnx/vision_model_int8.onnx": (
                94553333, "0dd31785a2713f1113ef2272472165c69d580473dae38d7b47568ac587795e70"
            ),
            "onnx/text_model_int8.onnx": (
                283438275, "3a0603d3a00c05a80a6ded4743c16aaac7b1e62cdcc7e362e7ce418659b96400"
            ),
        },
        "fp32": {
            "onnx/vision_model.onnx": (
                371807752, "c0573e3f4140c3a7c4e9cc5912bd6b26a033b46a6a8e8af26cbea262b163bcad"
            ),
            "onnx/text_model.onnx": (
                1129469657, "baf12d941beabafafb14f7b4adb38dc15be18681b964a84410ec53d9d65e6293"
            ),
        },
    },
    "tinyclip": {
        "repo": "onnx-community/TinyCLIP-ViT-40M-32-Text-19M-LAION400M-ONNX",
        "revision": "737108a175dc6c043d9e64cf738baa91a272f7cb",
        "originalRepo": "wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M",
        "originalRevision": "95ec8197b3f2fe7f747865c61ca556cf0768b2f7",
        "license": "MIT",
        "int8": {
            "onnx/model_int8.onnx": (
                85618595, "0843578fa3e1c386a3be5250ccb13cc8836d062876a34756f380a2e371e86a75"
            ),
        },
        "fp32": {
            "onnx/model.onnx": (
                337222823, "893cc6ae59654fb23d22747d3af35442bab5ed995a493834f65a58fc4af4cf84"
            ),
        },
    },
}
ORIGINAL_SIGLIP_SHA256 = "612923381c76ec5a9bed335d1c48827e3f2e506ac31b044b63b2031fadee6a0b"
HTTP_HEADERS = {"User-Agent": "PhotoLibrarian-ModelBench-asset-preparation/1"}


def digest(path: Path, algorithm: str = "sha256", git_blob: bool = False) -> str:
    result = hashlib.new(algorithm)
    if git_blob:
        result.update(f"blob {path.stat().st_size}\0".encode("ascii"))
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def write_json(path: Path, value: object) -> None:
    pending = path.with_name(path.name + ".writing")
    pending.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    pending.replace(path)


def read_url(url: str, headers: dict | None = None):
    return urllib.request.urlopen(
        urllib.request.Request(url, headers={**HTTP_HEADERS, **(headers or {})}), timeout=120
    )


class Source:
    def __init__(self, output: Path, repo: str, revision: str, cache: Path | None = None):
        self.repo = repo
        self.revision = revision
        self.directory = (cache if cache is not None else output / "source-cache") / repo.replace("/", "--") / revision
        self.directory.mkdir(parents=True, exist_ok=True)
        metadata_path = self.directory / "repository-metadata.json"
        if metadata_path.exists():
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        else:
            with read_url(f"https://huggingface.co/api/models/{repo}/revision/{revision}?blobs=true") as response:
                metadata = json.load(response)
            write_json(metadata_path, metadata)
        if metadata["sha"] != revision:
            raise ValueError(f"Repository revision mismatch: {repo}")
        self.files = {item["rfilename"]: item for item in metadata["siblings"]}
        self.records: list[dict] = []

    def url(self, name: str) -> str:
        return f"https://huggingface.co/{self.repo}/resolve/{self.revision}/{name}"

    def download(self, name: str, expected: tuple[int, str] | None = None) -> Path:
        metadata = self.files[name]
        lfs = metadata.get("lfs")
        size = metadata["size"]
        checksum = lfs["sha256"] if lfs else metadata["blobId"]
        algorithm = "sha256" if lfs else "sha1"
        if expected and (size, checksum) != expected:
            raise ValueError(f"Pinned size/checksum does not match Hugging Face metadata: {name}")
        path = self.directory.joinpath(*name.split("/"))
        path.parent.mkdir(parents=True, exist_ok=True)

        def verified(candidate: Path) -> bool:
            return (
                candidate.exists()
                and candidate.stat().st_size == size
                and digest(candidate, algorithm, git_blob=not lfs) == checksum
            )

        if not verified(path):
            pending = path.with_name(path.name + ".download")
            for attempt in range(3):
                try:
                    print(f"Downloading {self.repo}/{name} ({size:,} bytes)", flush=True)
                    with read_url(self.url(name)) as response, pending.open("wb") as stream:
                        shutil.copyfileobj(response, stream, length=1024 * 1024)
                    if not verified(pending):
                        raise ValueError(f"Downloaded file failed size/checksum verification: {name}")
                    pending.replace(path)
                    break
                except Exception:
                    pending.unlink(missing_ok=True)
                    if attempt == 2:
                        raise
                    time.sleep(2 ** attempt)
        self.records.append({
            "repository": self.repo,
            "revision": self.revision,
            "file": name,
            "url": self.url(name),
            "sizeBytes": size,
            "sha256": digest(path),
            "upstreamChecksumAlgorithm": "sha256" if lfs else "git-blob-sha1",
            "upstreamChecksum": checksum,
            "checksumVerified": True,
        })
        return path


def load_vocabulary(path: Path) -> tuple[dict, str]:
    vocabulary = json.loads(path.read_text(encoding="utf-8"))
    labels = vocabulary["labels"]
    if not 150 <= len(labels) <= 10_000:
        raise ValueError("The shared vocabulary must have 150-10000 labels")
    if any(not isinstance(label, str) or not label.strip() or label != label.strip() for label in labels):
        raise ValueError("Labels must be nonempty, trimmed strings")
    if len({label.casefold() for label in labels}) != len(labels):
        raise ValueError("Vocabulary labels must be unique")
    for model_id in SPECS:
        prompt = vocabulary["prompts"][model_id]
        if prompt.count("{label}") != 1:
            raise ValueError(f"Expected exactly one label placeholder for {model_id}")
    canonical = json.dumps(labels, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return vocabulary, hashlib.sha256(canonical).hexdigest()


def read_siglip_scalars(source: Source) -> tuple[float, float, dict]:
    """Read only public scalar tensor bytes; never claim the whole weight was verified."""
    name = "model.safetensors"
    metadata = source.files[name]
    if metadata["lfs"]["sha256"] != ORIGINAL_SIGLIP_SHA256:
        raise ValueError("Unexpected original SigLIP weight checksum")
    ranges = []

    def read_range(start: int, end: int) -> bytes:
        cache = source.directory / f"model.safetensors.range-{start}-{end}.bin"
        # A distinct query avoids a CDN reusing the first signed range redirect.
        with read_url(source.url(name) + f"?range={start}-{end}", {"Range": f"bytes={start}-{end}"}) as response:
            expected = f"bytes {start}-{end}/{metadata['size']}"
            if response.status != 206 or response.headers.get("Content-Range") != expected:
                raise ValueError("Server did not honor exact safetensors HTTP Range request")
            data = response.read(end - start + 2)
        if len(data) != end - start + 1:
            raise ValueError("Incomplete safetensors range")
        cache.write_bytes(data)
        ranges.append({"start": start, "endInclusive": end, "sha256": hashlib.sha256(data).hexdigest()})
        return data

    header_length = struct.unpack("<Q", read_range(0, 7))[0]
    if not 0 < header_length <= 8 * 1024 * 1024:
        raise ValueError("Unexpected safetensors header length")
    header = json.loads(read_range(8, header_length + 7))
    tensors = {}
    for key in ("logit_scale", "logit_bias"):
        info = header[key]
        start, end = info["data_offsets"]
        if info["dtype"] != "F32" or info["shape"] not in ([1], []) or end - start != 4:
            raise ValueError(f"Unexpected scalar tensor format: {key}")
        offset = header_length + 8
        value = struct.unpack("<f", read_range(offset + start, offset + end - 1))[0]
        if not math.isfinite(value):
            raise ValueError(f"Nonfinite scalar: {key}")
        tensors[key] = {"value": value, **info}
    raw_scale = tensors["logit_scale"]["value"]
    bias = tensors["logit_bias"]["value"]
    # ONNX applies Exp to an F32 initializer, not a Python float64 value.
    multiplier = float(np.exp(np.float32(raw_scale)))
    provenance = {
        "repository": source.repo,
        "revision": source.revision,
        "url": source.url(name),
        "fullFileExpectedLfsSha256": ORIGINAL_SIGLIP_SHA256,
        "fullFileChecksumVerified": False,
        "verification": "Exact HTTPS 206 ranges from an immutable revision; range SHA-256 recorded. Full weights not downloaded.",
        "ranges": ranges,
        "tensors": tensors,
        "rawLogitScale": raw_scale,
        "exponentiation": "float32 exp(rawLogitScale); bundle logitScale is already exponentiated",
    }
    write_json(source.directory / "learned-logit-scalars.json", provenance)
    return multiplier, bias, provenance


def session(path: Path, threads: int) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = threads
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    return ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])


def embedding_output(runtime: ort.InferenceSession, candidates: tuple[str, ...]) -> str:
    outputs = {item.name: item for item in runtime.get_outputs()}
    for candidate in candidates:
        if candidate in outputs and len(outputs[candidate].shape) == 2:
            return candidate
    raise ValueError(f"No supported pooled embedding output: {outputs}")


def normalize(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float32)
    if values.ndim != 2 or not np.isfinite(values).all():
        raise ValueError("Expected a finite 2D embedding matrix")
    norms = np.linalg.norm(values, axis=1, keepdims=True)
    if (norms <= 0).any() or not np.isfinite(norms).all():
        raise ValueError("Invalid embedding norm")
    return values / norms


def compatible_integer_convolutions(path: Path) -> list[dict]:
    """Recenter signed constant ConvInteger weights for ORT's U8/U8 CPU kernel."""
    graph = onnx.load(str(path), load_external_data=False)
    changes = recenter_integer_convolutions(graph)
    if changes:
        onnx.checker.check_model(graph)
        onnx.save(graph, str(path))
    return changes


def recenter_integer_convolutions(graph: onnx.ModelProto) -> list[dict]:
    initializers = {item.name: item for item in graph.graph.initializer}
    changes = []
    for node in graph.graph.node:
        if node.op_type != "ConvInteger":
            continue
        if len(node.input) != 4 or node.input[1] not in initializers or node.input[3] not in initializers:
            raise ValueError("Expected explicit constant ConvInteger weights and weight zero point")
        weight = onnx.numpy_helper.to_array(initializers[node.input[1]])
        zero = onnx.numpy_helper.to_array(initializers[node.input[3]])
        if weight.dtype == np.uint8:
            continue
        if weight.dtype != np.int8 or zero.dtype != np.int8:
            raise ValueError("Unsupported ConvInteger quantization type")
        for index, original in ((1, weight), (3, zero)):
            name = node.input[index]
            shifted = (original.astype(np.int16) + 128).astype(np.uint8)
            # Both offsets shift by 128, so (w - zeroPoint) is unchanged exactly.
            references = sum(list(other.input).count(name) for other in graph.graph.node)
            if references == 1:
                initializers[name].CopyFrom(onnx.numpy_helper.from_array(shifted, name=name))
            else:
                new_name = name + "_conv_uint8"
                if new_name in initializers:
                    raise ValueError(f"Initializer name collision: {new_name}")
                tensor = onnx.numpy_helper.from_array(shifted, name=new_name)
                graph.graph.initializer.append(tensor)
                initializers[new_name] = tensor
                node.input[index] = new_name
        changes.append({
            "node": node.name,
            "transformation": "ConvInteger constant weights and weight zero point recentered from INT8 to UINT8 by adding 128",
            "reason": "ONNX Runtime 1.22 CPU does not implement UINT8-input/INT8-weight ConvInteger; UINT8/UINT8 is supported",
            "equivalence": "Exact integer identity: (uint8(w+128) - uint8(z+128)) == (int8(w) - int8(z)); scales and integer accumulation unchanged",
        })
    return changes


def prepare_graphs(model_id: str, precision: str, source: Source, output: Path) -> tuple[Path, Path, list[dict]]:
    suffix = "_int8" if precision == "int8" else ""
    image_path = output / f"{model_id}-vision-{precision}.onnx"
    if model_id == "siglip2":
        name = f"onnx/vision_model{suffix}.onnx"
        vision = source.download(name, SPECS[model_id][precision][name])
        name = f"onnx/text_model{suffix}.onnx"
        text = source.download(name, SPECS[model_id][precision][name])
        shutil.copyfile(vision, image_path)
    else:
        name = f"onnx/model{suffix}.onnx"
        combined = source.download(name, SPECS[model_id][precision][name])
        text = source.directory / f"text-extracted-{precision}.onnx"
        graph = onnx.load(str(combined), load_external_data=False)
        text_inputs = [item.name for item in graph.graph.input if item.name != "pixel_values"]
        if not text_inputs or not set(text_inputs) <= {"input_ids", "attention_mask"}:
            raise ValueError(f"Unexpected TinyCLIP text inputs: {text_inputs}")
        del graph
        onnx.utils.extract_model(str(combined), str(image_path), ["pixel_values"], ["image_embeds"], infer_shapes=False)
        onnx.utils.extract_model(str(combined), str(text), text_inputs, ["text_embeds"], infer_shapes=False)
    modifications = compatible_integer_convolutions(image_path)
    onnx.checker.check_model(str(image_path))
    return image_path, text, modifications


def text_embeddings(
    model_id: str, text_path: Path, source: Source, vocabulary: dict, threads: int
) -> tuple[np.ndarray, dict]:
    tokenizer = AutoTokenizer.from_pretrained(str(source.directory), local_files_only=True, trust_remote_code=False, use_fast=True)
    runtime = session(text_path, threads)
    output_name = embedding_output(runtime, ("text_embeds", "pooler_output"))
    inputs = runtime.get_inputs()
    if any(item.type != "tensor(int64)" for item in inputs):
        raise ValueError("Expected int64 token inputs")
    length = 64 if model_id == "siglip2" else 77
    prompt = vocabulary["prompts"][model_id]
    rows = []
    # Batch one makes dynamic-quantization scales independent of vocabulary order.
    for index, label in enumerate(vocabulary["labels"]):
        encoded = tokenizer(
            prompt.format(label=label), padding="max_length", max_length=length,
            truncation=True, return_tensors="np",
        )
        feed = {item.name: np.asarray(encoded[item.name], dtype=np.int64) for item in inputs}
        rows.append(runtime.run([output_name], feed)[0])
        if (index + 1) % 25 == 0:
            print(f"{model_id}: encoded {index + 1}/{len(vocabulary['labels'])} labels", flush=True)
    embeddings = normalize(np.concatenate(rows, axis=0))
    return embeddings, {
        "promptTemplate": prompt,
        "sequenceLength": length,
        "padding": "max_length",
        "truncation": True,
        "batchSize": 1,
        "tokenizerClass": type(tokenizer).__name__,
        "inputs": [{"name": item.name, "type": item.type, "shape": item.shape} for item in inputs],
        "outputName": output_name,
        "modelSha256": digest(text_path),
        "executionProvider": "CPUExecutionProvider",
    }


def preprocessing(model_id: str, config: dict) -> dict:
    if not all(config.get(key) for key in ("do_resize", "do_rescale", "do_normalize")):
        raise ValueError("Unsupported preprocessing switches")
    if config["rescale_factor"] != 1 / 255:
        raise ValueError("Expected rescaling by 1/255")
    if model_id == "siglip2":
        if config["size"] != {"height": 224, "width": 224} or config["resample"] != 2:
            raise ValueError("Unexpected SigLIP preprocessing")
        resize_mode, interpolation = "squash", "linear"
        rounding = "Resize RGB directly to 224x224 using Pillow BILINEAR."
    else:
        if config["size"] != {"shortest_edge": 224} or config["resample"] != 3:
            raise ValueError("Unexpected TinyCLIP preprocessing")
        if not config["do_center_crop"] or config["crop_size"] != {"height": 224, "width": 224}:
            raise ValueError("Expected 224x224 TinyCLIP center crop")
        resize_mode, interpolation = "shortest-center-crop", "cubic"
        rounding = (
            "Resize shortest dimension to 224; long dimension = int(224 * long / short), "
            "i.e. floor. Pillow BICUBIC with antialiasing when downsampling; center-crop "
            "left=(resizedWidth-224)//2, top=(resizedHeight-224)//2. WIC kernels can differ."
        )
    return {
        "inputSize": 224,
        "resizeMode": resize_mode,
        "interpolation": interpolation,
        "normalizationMean": config["image_mean"],
        "normalizationStd": config["image_std"],
        "preprocessingDetails": {
            "colorMode": "RGB",
            "tensorLayout": "NCHW",
            "tensorType": "float32",
            "rescaleFactor": config["rescale_factor"],
            "resizeAndCrop": rounding,
            "normalization": "(RGB byte / 255 - mean[channel]) / std[channel]",
            "sourceConfig": config,
        },
    }


def manual_pixels(image: Image.Image, config: dict, model_id: str) -> np.ndarray:
    image = image.convert("RGB")
    if model_id == "siglip2":
        image = image.resize((224, 224), Image.Resampling.BILINEAR)
    else:
        width, height = image.size
        if width <= height:
            size = (224, int(224 * height / width))
        else:
            size = (int(224 * width / height), 224)
        image = image.resize(size, Image.Resampling.BICUBIC)
        left, top = (size[0] - 224) // 2, (size[1] - 224) // 2
        image = image.crop((left, top, left + 224, top + 224))
    values = (np.asarray(image, dtype=np.float64) * config["rescale_factor"]).astype(np.float32)
    values = (values - np.asarray(config["image_mean"], dtype=np.float32)) / np.asarray(config["image_std"], dtype=np.float32)
    return np.ascontiguousarray(values.transpose(2, 0, 1)[None])


def create_fixtures(output: Path) -> list[dict]:
    fixtures = []
    for width, height in ((320, 241), (241, 320)):
        y, x = np.indices((height, width), dtype=np.uint32)
        pixels = np.stack((
            (x * 255 // (width - 1)),
            (y * 255 // (height - 1)),
            ((x // 19 + y // 23) % 2) * 192 + 31,
        ), axis=2).astype(np.uint8)
        path = output / f"synthetic-rgb-{width}x{height}.png"
        Image.fromarray(pixels).save(path)
        fixtures.append({
            "imageFile": path.name,
            "width": width,
            "height": height,
            "imageSha256": digest(path),
            "description": "Synthetic horizontal red and vertical green gradients with a blue checkerboard; no private photos.",
            "models": [],
        })
    return fixtures


def reference_results(output: Path, fixture: dict, bundle: dict, config: dict, runtime: ort.InferenceSession) -> dict:
    model_id = bundle["modelId"]
    with Image.open(output / fixture["imageFile"]) as image:
        processor_class = SiglipImageProcessor if model_id == "siglip2" else CLIPImageProcessor
        processor = processor_class.from_dict(config)
        expected = np.asarray(processor(images=image, return_tensors="np")["pixel_values"], dtype=np.float32)
        actual = manual_pixels(image, config, model_id)
    difference = float(np.max(np.abs(actual - expected)))
    if not np.allclose(actual, expected, rtol=0, atol=1e-6):
        raise ValueError(f"Manual preprocessing differs from Hugging Face processor: {model_id}, {difference}")
    tensor_path = output / f"{Path(fixture['imageFile']).stem}-{model_id}-pixels.f32"
    expected.astype("<f4").tofile(tensor_path)
    embedding = normalize(runtime.run([bundle["outputName"]], {bundle["inputName"]: expected})[0])
    text = np.asarray([entry["embedding"] for entry in bundle["labels"]], dtype=np.float32)
    scores = (embedding @ text.T)[0]
    if bundle["scoreKind"] == "sigmoid":
        logits = np.float32(bundle["logitScale"]) * scores + np.float32(bundle["logitBias"])
        scores = np.exp(-np.logaddexp(np.float32(0), -logits))
    if not np.isfinite(scores).all():
        raise ValueError("Nonfinite reference scores")
    ranked = np.argsort(-scores, kind="stable")
    return {
        "modelId": model_id,
        "bundleFile": f"{model_id}.json",
        "modelSha256": bundle["modelSha256"],
        "vocabularySha256": bundle["vocabularySha256"],
        "pixelValuesFile": tensor_path.name,
        "pixelValuesSha256": digest(tensor_path),
        "pixelValuesFormat": "little-endian float32, row-major NCHW",
        "pixelValuesShape": list(expected.shape),
        "pixelValuesSamples": [
            {"index": [0, c, y, x], "value": float(expected[0, c, y, x])}
            for c in range(3) for y, x in ((0, 0), (0, 223), (111, 111), (223, 0), (223, 223))
        ],
        "manualVsTransformersMaxAbsoluteError": difference,
        "imageEmbedding": embedding[0].tolist(),
        "scores": scores.tolist(),
        "scoreOrder": "same as bundle labels",
        "top10": [{"label": bundle["labels"][int(i)]["label"], "score": float(scores[i])} for i in ranked[:10]],
        "executionProvider": "CPUExecutionProvider",
    }


def prepare(model_id: str, args: argparse.Namespace, vocabulary: dict, vocabulary_sha: str, fixtures: list[dict]) -> dict:
    spec = SPECS[model_id]
    source = Source(args.output, spec["repo"], spec["revision"], args.source_cache)
    original = Source(args.output, spec["originalRepo"], spec["originalRevision"], args.source_cache)
    original.download("README.md")
    for name in ("config.json", "preprocessor_config.json", "tokenizer_config.json", "tokenizer.json", "special_tokens_map.json"):
        source.download(name)
    for name in ("vocab.json", "merges.txt"):
        if name in source.files:
            source.download(name)
    config = json.loads((source.directory / "preprocessor_config.json").read_text(encoding="utf-8"))
    image_path, text_path, modifications = prepare_graphs(model_id, args.precision, source, args.output)
    embeddings, text_info = text_embeddings(model_id, text_path, source, vocabulary, args.threads)
    scale, bias, scalar_info = read_siglip_scalars(original) if model_id == "siglip2" else (1.0, 0.0, None)
    runtime = session(image_path, args.threads)
    inputs = runtime.get_inputs()
    if len(inputs) != 1 or inputs[0].name != "pixel_values" or inputs[0].type != "tensor(float)":
        raise ValueError(f"Unexpected image model inputs: {inputs}")
    output_name = embedding_output(runtime, ("image_embeds", "pooler_output"))
    bundle = {
        "modelId": model_id,
        "modelFile": image_path.name,
        "inputName": inputs[0].name,
        "outputName": output_name,
        **preprocessing(model_id, config),
        "scoreKind": "sigmoid" if model_id == "siglip2" else "cosine",
        "logitScale": scale,
        "logitBias": bias,
        "labels": [
            {"label": label, "embedding": vector.tolist()}
            for label, vector in zip(vocabulary["labels"], embeddings, strict=True)
        ],
        "vocabularySha256": vocabulary_sha,
        "vocabularySha256Method": "SHA256(UTF8(JSON ordered label array, compact separators, ensure_ascii=False))",
        "vocabularyFile": "home-photo-vocabulary.json",
        "vocabularyFileSha256": digest(args.vocabulary),
        "modelSha256": digest(image_path),
        "sources": source.records + original.records,
        "precision": args.precision,
        "license": spec["license"],
        "licenseSource": f"https://huggingface.co/{spec['originalRepo']}/blob/{spec['originalRevision']}/README.md",
        "textPreparation": text_info,
        "provisionalThreshold": 0.1 if model_id == "siglip2" else 0.25,
        "thresholdWarning": "Uncalibrated exploratory threshold. Cosine is not probability; sigmoid is not human-calibrated confidence.",
        "modelSizeBytes": image_path.stat().st_size,
        "inputShape": inputs[0].shape,
        "outputShape": next(item.shape for item in runtime.get_outputs() if item.name == output_name),
        "embeddingDimension": embeddings.shape[1],
        "imageModelDerivation": "Original separate vision ONNX" if model_id == "siglip2" else "onnx.utils.extract_model reachability: pixel_values -> image_embeds; no text inputs or text weights",
        "onnxModifications": modifications,
    }
    if scalar_info is not None:
        bundle["learnedLogitParameters"] = scalar_info
    graph = onnx.load(str(image_path), load_external_data=False)
    bundle["onnxOpsets"] = {item.domain or "ai.onnx": item.version for item in graph.opset_import}
    bundle["operatorTypes"] = sorted({(node.domain + "::" if node.domain else "") + node.op_type for node in graph.graph.node})
    del graph
    for fixture in fixtures:
        fixture["models"].append(reference_results(args.output, fixture, bundle, config, runtime))
    write_json(args.output / f"{model_id}.json", bundle)
    print(f"READY {model_id}: {image_path.stat().st_size:,} bytes, {inputs[0].shape} -> {output_name}, {embeddings.shape}", flush=True)
    return {
        "modelId": model_id,
        "bundleFile": f"{model_id}.json",
        "bundleSha256": digest(args.output / f"{model_id}.json"),
        "modelFile": image_path.name,
        "modelSha256": bundle["modelSha256"],
        "modelSizeBytes": image_path.stat().st_size,
        "precision": args.precision,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output", required=True, type=Path, help="Asset directory outside the source repository")
    parser.add_argument("--models", default="siglip2,tinyclip", help="Comma-separated subset of siglip2,tinyclip")
    parser.add_argument("--precision", choices=("int8", "fp32"), default="int8")
    parser.add_argument("--vocabulary", type=Path, default=Path(__file__).with_name("home-photo-vocabulary.json"))
    parser.add_argument("--source-cache", type=Path, help="Reuse verified source downloads (default: OUTPUT/source-cache)")
    parser.add_argument("--threads", type=int, default=min(4, os.cpu_count() or 1))
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.source_cache = (args.source_cache or args.output / "source-cache").resolve()
    args.vocabulary = args.vocabulary.resolve()
    args.models = args.models.split(",")
    if not args.models or any(model not in SPECS for model in args.models) or len(set(args.models)) != len(args.models):
        parser.error("--models must be a unique comma-separated subset of siglip2,tinyclip")
    if args.threads < 1:
        parser.error("--threads must be positive")
    repository = next((parent for parent in Path(__file__).resolve().parents if (parent / ".git").exists()), None)
    if repository and any(path == repository or repository in path.parents for path in (args.output, args.source_cache)):
        parser.error("Model assets must not be written inside the source repository")
    return args


def main() -> None:
    args = parse_args()
    vocabulary, vocabulary_sha = load_vocabulary(args.vocabulary)
    args.output.mkdir(parents=True, exist_ok=True)
    target_vocabulary = args.output / "home-photo-vocabulary.json"
    if args.vocabulary != target_vocabulary:
        shutil.copyfile(args.vocabulary, target_vocabulary)
    fixtures = create_fixtures(args.output)
    prepared = []
    for model_id in args.models:
        prepared.append(prepare(model_id, args, vocabulary, vocabulary_sha, fixtures))
        write_json(args.output / "reference.json", {
            "schemaVersion": 1,
            "purpose": "Synthetic fixtures only; compare C# inference on exact pixelValuesFile before assessing WIC resize differences.",
            "vocabularySha256": vocabulary_sha,
            "fixtures": fixtures,
        })
    write_json(args.output / "preparation-manifest.json", {
        "schemaVersion": 1,
        "scriptSha256": digest(Path(__file__).resolve()),
        "requirementsSha256": digest(Path(__file__).with_name("requirements-prepare.txt")),
        "pythonVersion": sys.version,
        "packages": {
            name: importlib.metadata.version(name)
            for name in ("numpy", "Pillow", "onnx", "onnxruntime", "transformers", "tokenizers", "huggingface-hub")
        },
        "executionProvider": "CPUExecutionProvider",
        "threads": args.threads,
        "sourceCache": str(args.source_cache),
        "vocabularySha256": vocabulary_sha,
        "vocabularyFileSha256": digest(target_vocabulary),
        "labelCount": len(vocabulary["labels"]),
        "models": prepared,
        "referenceSha256": digest(args.output / "reference.json"),
        "providerWarning": "Only CPU verified. Dynamic INT8 MatMulInteger/DynamicQuantizeLinear support on DirectML is not guaranteed; use --precision fp32 in a separate output folder if necessary.",
    })
    print(f"Prepared {len(prepared)} bundles with {len(vocabulary['labels'])} shared labels in {args.output}", flush=True)


if __name__ == "__main__":
    main()
