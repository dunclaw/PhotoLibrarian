"""Compare a generated RAM++ ONNX bundle with pinned upstream PyTorch output.

The report is intentionally machine-readable.  It checks raw numeric parity,
exact binary-mask parity, and the label count before a bundle is considered
eligible for hosting.
"""

from __future__ import annotations

import argparse
import importlib
import json
from pathlib import Path
import sys

import numpy as np
from PIL import Image

import export_ram_plus as export


def pixels(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        image = image.convert("RGB").resize(
            (export.IMAGE_SIZE, export.IMAGE_SIZE), Image.Resampling.BILINEAR
        )
        values = np.asarray(image, dtype=np.float32) / 255.0
    values = (values - np.asarray([0.485, 0.456, 0.406], dtype=np.float32))
    values /= np.asarray([0.229, 0.224, 0.225], dtype=np.float32)
    return np.ascontiguousarray(values.transpose(2, 0, 1)[None])


def pytorch_output(model, image):
    import torch

    with torch.no_grad():
        image = torch.from_numpy(image)
        image_embeds = model.image_proj(model.visual_encoder(image))
        image_atts = torch.ones(image_embeds.size()[:-1], dtype=torch.long)
        image_cls = image_embeds[:, 0, :]
        image_cls = image_cls / image_cls.norm(dim=-1, keepdim=True)
        logits = (model.reweight_scale.exp() * image_cls @ model.label_embed.t())
        logits = logits.reshape(image.shape[0], model.num_class, -1)
        weights = torch.softmax(logits, dim=2)
        embeddings = model.label_embed.reshape(-1, 51, 512)
        label_embed = (weights.unsqueeze(-1) * embeddings).sum(dim=2)
        label_embed = torch.relu(model.wordvec_proj(label_embed))
        tagging = model.tagging_head(
            encoder_embeds=label_embed,
            encoder_hidden_states=image_embeds,
            encoder_attention_mask=image_atts,
            return_dict=False,
            mode="tagging",
        )
        logits = model.fc(tagging[0]).squeeze(-1)
        probabilities = torch.sigmoid(logits)
        targets = (probabilities > model.class_threshold).to(torch.float32)
        return tuple(value.cpu().numpy() for value in (logits, probabilities, targets))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--images", type=Path, nargs="+", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    import onnxruntime as ort
    sys.path.insert(0, str(args.source_dir))
    ram_plus_module = importlib.import_module("ram.models.ram_plus")

    class ExportTokenizer:
        def __len__(self) -> int:
            return 30_524

    ram_plus_module.init_tokenizer = lambda _: ExportTokenizer()

    labels, _ = export.load_labels(args.source_dir)
    model = ram_plus_module.ram_plus(
        pretrained=str(args.checkpoint), image_size=export.IMAGE_SIZE, vit="swin_l"
    ).eval()
    runtime = ort.InferenceSession(
        str(args.bundle), providers=["CPUExecutionProvider"]
    )
    input_name = runtime.get_inputs()[0].name
    input_shape = runtime.get_inputs()[0].shape
    output_names = [item.name for item in runtime.get_outputs()]
    output_shapes = [item.shape for item in runtime.get_outputs()]
    if (output_names != list(export.OUTPUT_NAMES) or
            input_shape != [1, 3, export.IMAGE_SIZE, export.IMAGE_SIZE] or
            output_shapes != [[1, export.LABEL_COUNT]] * len(export.OUTPUT_NAMES)):
        raise ValueError(
            f"Unexpected ONNX contract: input={input_name} {input_shape}, "
            f"outputs={list(zip(output_names, output_shapes))}"
        )
    rows = []
    for image_path in args.images:
        image = pixels(image_path)
        expected = pytorch_output(model, image)
        actual = runtime.run(list(export.OUTPUT_NAMES), {input_name: image})
        logits_error = np.abs(expected[0] - actual[0])
        probabilities_error = np.abs(expected[1] - actual[1])
        expected_targets = expected[2].astype(np.float32)
        actual_targets = actual[2].astype(np.float32)
        rows.append({
            "image": str(image_path),
            "labelCount": len(labels),
            "logitsMaxAbsoluteError": float(logits_error.max()),
            "logitsMeanAbsoluteError": float(logits_error.mean()),
            "probabilitiesMaxAbsoluteError": float(probabilities_error.max()),
            "probabilitiesMeanAbsoluteError": float(probabilities_error.mean()),
            "targetMismatchCount": int(np.count_nonzero(expected_targets != actual_targets)),
            "finite": bool(all(np.isfinite(value).all() for value in actual)),
        })
    report = {
        "schemaVersion": 1,
        "sourceRevision": export.SOURCE_REVISION,
        "checkpointRevision": export.CHECKPOINT_REVISION,
        "executionProvider": "CPUExecutionProvider",
        "images": rows,
        "exactTargetParity": all(row["targetMismatchCount"] == 0 for row in rows),
        "finiteOutputs": all(row["finite"] for row in rows),
        "rawTolerance": {"rtol": 1e-3, "atol": 1e-4},
        "note": "Review raw errors and threshold-near classes before hosting; exact targets are required.",
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
