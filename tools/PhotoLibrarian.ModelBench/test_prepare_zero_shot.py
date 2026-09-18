"""Offline tests: python -m unittest discover -s tools\\PhotoLibrarian.ModelBench -p test_prepare_zero_shot.py"""

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np
import onnx
import onnxruntime as ort
from PIL import Image
from transformers import CLIPImageProcessor, SiglipImageProcessor

import prepare_zero_shot as prep


class PreparationTests(unittest.TestCase):
    def test_frozen_shared_vocabulary(self):
        vocabulary, checksum = prep.load_vocabulary(Path(prep.__file__).with_name("home-photo-vocabulary.json"))
        self.assertEqual(234, len(vocabulary["labels"]))
        self.assertEqual("3416fef2285b5ffc11a927eba270c902aa69856fb8fd397e564cefb85b5723c3", checksum)
        encoded = json.dumps(vocabulary["labels"], ensure_ascii=False, separators=(",", ":")).encode()
        self.assertEqual(hashlib.sha256(encoded).hexdigest(), checksum)

    def test_full_model_vocabulary_is_not_limited_to_trial_size(self):
        vocabulary, checksum = prep.load_vocabulary(
            Path(prep.__file__).with_name("home-photo-vocabulary.general-v1.json"))
        self.assertEqual(3635, len(vocabulary["labels"]))
        self.assertEqual("dc9fa1e4890cf2ce518f9ad0128534926678e28506239488491c009125d748ed", checksum)
        self.assertIn("sax", vocabulary["labels"])
        self.assertIn("snowmobile", vocabulary["labels"])
        self.assertNotIn("beak", vocabulary["labels"])

    def test_extreme_vocabulary_size_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vocabulary.json"
            path.write_text(json.dumps({"labels": [str(i) for i in range(10_001)]}))
            with self.assertRaisesRegex(ValueError, "150-10000"):
                prep.load_vocabulary(path)

    def test_embedding_normalization_and_invalid_values(self):
        np.testing.assert_allclose(prep.normalize([[3, 4], [0, -2]]), [[0.6, 0.8], [0, -1]])
        for value in ([[0, 0]], [[np.inf, 1]], [[np.nan, 1]], [1, 2]):
            with self.subTest(value=value), self.assertRaises(ValueError):
                prep.normalize(value)

    def test_review_vocabulary_preserves_baseline_and_declares_additions(self):
        directory = Path(prep.__file__).parent
        baseline, baseline_hash = prep.load_vocabulary(directory / "home-photo-vocabulary.json")
        expanded, expanded_hash = prep.load_vocabulary(directory / "home-photo-vocabulary.review-v2.json")
        self.assertEqual(248, len(expanded["labels"]))
        self.assertEqual(baseline["labels"], expanded["labels"][:234])
        self.assertEqual(expanded["addedLabels"], expanded["labels"][234:])
        self.assertEqual(baseline["prompts"], expanded["prompts"])
        self.assertNotEqual(baseline_hash, expanded_hash)
        self.assertFalse(expanded["productionApproved"])

    def test_preprocessing_matches_official_processors(self):
        for model_id, processor_class, mean, std, resample in (
            ("siglip2", SiglipImageProcessor, [0.5] * 3, [0.5] * 3, 2),
            ("tinyclip", CLIPImageProcessor, [0.48145466, 0.4578275, 0.40821073], [0.26862954, 0.26130258, 0.27577711], 3),
        ):
            config = {
                "do_resize": True,
                "do_rescale": True,
                "do_normalize": True,
                "do_convert_rgb": True,
                "rescale_factor": 1 / 255,
                "resample": resample,
                "image_mean": mean,
                "image_std": std,
                "size": {"height": 224, "width": 224} if model_id == "siglip2" else {"shortest_edge": 224},
            }
            if model_id == "tinyclip":
                config.update(do_center_crop=True, crop_size={"height": 224, "width": 224})
            contract = prep.preprocessing(model_id, config)
            self.assertEqual("linear" if model_id == "siglip2" else "cubic", contract["interpolation"])
            rng = np.random.default_rng(0)
            for width, height in ((320, 241), (241, 320), (97, 151), (224, 224)):
                with self.subTest(model=model_id, size=(width, height)):
                    image = Image.fromarray(rng.integers(0, 256, (height, width, 3), dtype=np.uint8))
                    actual = prep.manual_pixels(image, config, model_id)
                    expected = processor_class.from_dict(config)(images=image, return_tensors="np")["pixel_values"]
                    np.testing.assert_array_equal(expected, actual)

    def test_usefulness_revision_excludes_beak_without_relabeling_or_other_changes(self):
        directory = Path(prep.__file__).parent
        previous, previous_hash = prep.load_vocabulary(directory / "home-photo-vocabulary.review-v2.json")
        revised, revised_hash = prep.load_vocabulary(directory / "home-photo-vocabulary.review-v3.json")
        self.assertEqual(247, len(revised["labels"]))
        self.assertEqual([label for label in previous["labels"] if label != "beak"], revised["labels"])
        self.assertEqual([label for label in previous["addedLabels"] if label != "beak"], revised["addedLabels"])
        self.assertEqual(previous["prompts"], revised["prompts"])
        self.assertEqual(["beak"], list(revised["excludedLabels"]))
        self.assertEqual(1, revised["labels"].count("bird"))
        self.assertNotEqual(previous_hash, revised_hash)
        self.assertFalse(revised["productionApproved"])

    def test_integer_convolution_recentering_is_exact_and_cpu_executable(self):
        pixels = np.asarray([0, 1, 255, 200, 30, 128, 50, 250, 100], dtype=np.uint8).reshape(1, 1, 3, 3)
        weight = np.asarray([-128, -1, 0, 127], dtype=np.int8).reshape(1, 1, 2, 2)
        input_zero, weight_zero = 100, -3
        graph = onnx.helper.make_graph(
            [onnx.helper.make_node("ConvInteger", ["pixels", "weight", "input_zero", "weight_zero"], ["result"], name="conv")],
            "integer-convolution-test",
            [onnx.helper.make_tensor_value_info("pixels", onnx.TensorProto.UINT8, [1, 1, 3, 3])],
            [onnx.helper.make_tensor_value_info("result", onnx.TensorProto.INT32, [1, 1, 2, 2])],
            initializer=[
                onnx.numpy_helper.from_array(weight, "weight"),
                onnx.numpy_helper.from_array(np.asarray(input_zero, dtype=np.uint8), "input_zero"),
                onnx.numpy_helper.from_array(np.asarray(weight_zero, dtype=np.int8), "weight_zero"),
            ],
        )
        model = onnx.helper.make_model(graph, opset_imports=[onnx.helper.make_opsetid("", 13)], ir_version=10)
        changes = prep.recenter_integer_convolutions(model)
        self.assertEqual(1, len(changes))
        self.assertEqual([], prep.recenter_integer_convolutions(model))
        onnx.checker.check_model(model)
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        runtime = ort.InferenceSession(model.SerializeToString(), sess_options=options, providers=["CPUExecutionProvider"])
        actual = runtime.run(["result"], {"pixels": pixels})[0]
        expected = np.zeros((1, 1, 2, 2), dtype=np.int32)
        for y in range(2):
            for x in range(2):
                expected[0, 0, y, x] = np.sum(
                    (pixels[0, 0, y:y + 2, x:x + 2].astype(np.int32) - input_zero)
                    * (weight[0, 0].astype(np.int32) - weight_zero)
                )
        np.testing.assert_array_equal(expected, actual)

    def test_refuses_model_assets_inside_repository(self):
        with patch("sys.argv", ["prepare_zero_shot.py", "--output", str(Path(prep.__file__).parent)]):
            with self.assertRaises(SystemExit) as error:
                prep.parse_args()
            self.assertEqual(2, error.exception.code)

    def test_source_cache_reuses_verified_downloads_without_network(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            cache = root / "cache"
            directory = cache / "example--model" / "revision"
            directory.mkdir(parents=True)
            content = b"model data"
            checksum = hashlib.sha256(content).hexdigest()
            (directory / "model.onnx").write_bytes(content)
            (directory / "repository-metadata.json").write_text(json.dumps({
                "sha": "revision",
                "siblings": [{
                    "rfilename": "model.onnx", "size": len(content),
                    "lfs": {"sha256": checksum}
                }]
            }), encoding="utf-8")
            with patch.object(prep, "read_url", side_effect=AssertionError("Unexpected network request")):
                source = prep.Source(root / "new-output", "example/model", "revision", cache)
                self.assertEqual(directory / "model.onnx", source.download("model.onnx"))
                self.assertTrue(source.records[0]["checksumVerified"])
            self.assertFalse((root / "new-output").exists())

    def test_refuses_source_cache_inside_repository(self):
        with tempfile.TemporaryDirectory() as temporary:
            with patch("sys.argv", [
                "prepare_zero_shot.py", "--output", temporary,
                "--source-cache", str(Path(prep.__file__).parent)
            ]):
                with self.assertRaises(SystemExit) as error:
                    prep.parse_args()
                self.assertEqual(2, error.exception.code)


if __name__ == "__main__":
    unittest.main()
