import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock, patch


HAS_ARRAYS = all(importlib.util.find_spec(name) is not None for name in ["numpy", "PIL"])
HAS_RUNTIME = HAS_ARRAYS and importlib.util.find_spec("onnxruntime") is not None


@unittest.skipUnless(HAS_ARRAYS, "Comparison preparation requires numpy/Pillow")
class ComparisonPreparationTests(unittest.TestCase):
    def test_ram_legacy_parser_requires_all_indices_and_preserves_labels(self):
        from prepare_comparison import ram_labels
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "labels.txt"
            entries = [f"'{i}': 'label_{i}'" for i in range(4585)]
            entries[5] = """'5': 'photographer\\'s camera'"""
            path.write_text("{" + ", ".join(entries) + "}")
            labels = ram_labels(path)
            self.assertEqual(4585, len(labels))
            self.assertEqual("photographer's camera", labels[5])
            self.assertEqual("label 4584", labels[-1])
            path.write_text("{'0': 'animal'}")
            with self.assertRaisesRegex(ValueError, "contiguous"):
                ram_labels(path)
            path.write_text("{'0': 'animal', '0': 'dog'}")
            with self.assertRaisesRegex(ValueError, "Duplicate RAM"):
                ram_labels(path)

    def test_zero_bundle_checks_model_identity_path_and_embeddings(self):
        import hashlib
        from prepare_comparison import zero_bundle
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "bundle.json"
            model = Path(folder) / "model.onnx"
            model.write_bytes(b"synthetic")
            bundle = {"modelFile": model.name, "modelId": "tinyclip",
                      "modelSha256": hashlib.sha256(model.read_bytes()).hexdigest(),
                      "labels": [{"label": "cat", "embedding": [1, 0]}],
                      "scoreKind": "cosine", "resizeMode": "shortest-center-crop", "interpolation": "cubic"}
            path.write_text(json.dumps(bundle))
            self.assertEqual(model.resolve(), zero_bundle(path, "tinyclip")[0])
            bundle["labels"][0]["embedding"] = [0, 0]
            path.write_text(json.dumps(bundle))
            with self.assertRaisesRegex(ValueError, "Invalid zero-shot"):
                zero_bundle(path, "tinyclip")
            bundle["modelFile"] = "..\\escape.onnx"
            path.write_text(json.dumps(bundle))
            with self.assertRaisesRegex(ValueError, "escaped"):
                zero_bundle(path, "tinyclip")


@unittest.skipUnless(HAS_RUNTIME, "Comparison inference requires evaluation environment")
class ComparisonInferenceTests(unittest.TestCase):
    def test_average_precision_groups_ties_and_ignores_unknown_entries(self):
        import numpy as np
        from compare_pilot import average_precision, grouped_uncertainty
        self.assertEqual(1, average_precision(np.array([0.9, 0.1, 0.99]), np.array([1, 0, -1])))
        self.assertEqual(0.5, average_precision(np.ones(4), np.array([1, 0, 1, 0])))
        self.assertEqual(0.5, average_precision(np.ones(4), np.array([0, 0, 1, 1])))
        self.assertIsNone(average_precision(np.ones(2), np.array([1, -1])))
        scores, targets = np.array([[0.9], [0.1]]), np.array([[1], [0]])
        first = grouped_uncertainty(scores, targets, ["a", "b"], 0.5)
        self.assertEqual(first, grouped_uncertainty(scores, targets, ["a", "b"], 0.5))
        self.assertEqual([1, 1], first["precisionPercentile95"])

    def test_zero_scores_normalize_both_sides_and_use_independent_sigmoid(self):
        import numpy as np
        from compare_pilot import zero_scores
        text = np.array([[1, 0], [0, 1], [-1, 0]], dtype=np.float64)
        cosine = zero_scores(np.array([[3, 0]]), text, {"scoreKind": "cosine"})
        np.testing.assert_array_equal(cosine, [1, 0, -1])
        scores = zero_scores(np.array([[3, 0]]), text,
                             {"scoreKind": "sigmoid", "logitScale": 2, "logitBias": -1})
        np.testing.assert_allclose(scores, [1 / (1 + np.exp(-1)), 1 / (1 + np.exp(1)),
                                           1 / (1 + np.exp(3))], rtol=1e-6)
        self.assertNotAlmostEqual(1, float(scores.sum()))
        with self.assertRaisesRegex(ValueError, "Invalid zero-shot image"):
            zero_scores(np.zeros((1, 2)), text, {"scoreKind": "cosine"})

    def test_native_preprocessing_matches_fixed_size_and_center_crop(self):
        import numpy as np
        from PIL import Image
        from compare_pilot import zero_tensor
        image = Image.fromarray(np.random.default_rng(66).integers(0, 256, (31, 73, 3), dtype=np.uint8))
        bundle = {"inputSize": 16, "interpolation": "cubic", "resizeMode": "shortest-center-crop",
                  "normalizationMean": [0.4, 0.5, 0.6], "normalizationStd": [0.2, 0.3, 0.4]}
        actual = zero_tensor(image, bundle)
        resized = image.resize((int(16 * 73 / 31), 16), Image.Resampling.BICUBIC)
        left = (resized.width - 16) // 2
        pixels = resized.crop((left, 0, left + 16, 16))
        expected = (np.asarray(pixels, dtype=np.float64) / 255).astype(np.float32)
        expected = (expected - np.array(bundle["normalizationMean"], np.float32)) / np.array(
            bundle["normalizationStd"], np.float32)
        np.testing.assert_array_equal(actual, expected.transpose(2, 0, 1)[None])

    def test_score_model_fails_if_frozen_asset_or_photo_changes(self):
        from compare_pilot import score_model
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            model = root / "model.onnx"
            metadata = root / "bundle.json"
            model.write_bytes(b"synthetic")
            metadata.write_text('{"labels":["cat"]}')
            from prepare_dataset import file_hash
            asset = {"model": str(model), "metadata": str(metadata),
                     "sha256": "wrong", "metadataSha256": file_hash(metadata)}
            with self.assertRaisesRegex(ValueError, "Frozen baseline asset changed"):
                score_model("pilot", asset, [], ["cat"])
            asset["sha256"] = file_hash(model)
            session = Mock()
            session.get_inputs.return_value = [Mock(name="input")]
            session.get_inputs.return_value[0].name = "image"
            session.get_inputs.return_value[0].shape = [1, 3, 224, 224]
            photo = root / "photo"
            photo.write_bytes(b"changed")
            with patch("compare_pilot.ort.InferenceSession", return_value=session):
                with self.assertRaisesRegex(ValueError, "input bytes changed"):
                    score_model("pilot", asset, [{"path": str(photo), "sha256": "old"}], ["cat"])


if __name__ == "__main__":
    unittest.main()
