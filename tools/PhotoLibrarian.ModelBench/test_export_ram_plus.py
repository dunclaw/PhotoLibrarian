"""Offline tests for the pinned RAM++ export inputs and output contract."""

import hashlib
import tempfile
from pathlib import Path
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from PIL import Image

import export_ram_plus as export
import validate_ram_plus


class RamPlusExportTests(unittest.TestCase):
    def test_pins_official_sources_and_checkpoint_inputs(self):
        self.assertEqual(4585, export.LABEL_COUNT)
        self.assertEqual(2, len(export.CHECKPOINTS))
        self.assertTrue(export.SOURCE_REVISION.startswith("7cb804a8"))
        self.assertTrue(export.CHECKPOINT_REVISION.startswith("84d4aee3"))
        self.assertEqual(
            "49c840b71915f639fb79cd83ac4a3e313cfbc2b1",
            export.LABEL_GIT_BLOB_SHA1,
        )
        self.assertEqual(
            "0472b23c25903900c0dde68fffc9a6a6755f5117",
            export.THRESHOLD_GIT_BLOB_SHA1,
        )
        self.assertTrue(export.CHECKPOINTS["ram_plus_swin_large_14m.pth"][1].isalnum())

    def test_load_labels_rejects_wrong_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)
            labels = source / export.LABEL_PATH
            thresholds = source / export.THRESHOLD_PATH
            labels.parent.mkdir(parents=True)
            labels.write_text("wrong\n", encoding="utf-8")
            thresholds.write_text("0.65\n", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "vocabulary hash"):
                export.load_labels(source)

    def test_load_labels_rejects_wrong_threshold_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)
            labels = source / export.LABEL_PATH
            thresholds = source / export.THRESHOLD_PATH
            labels.parent.mkdir(parents=True)
            labels.write_text("3D CG rendering\n", encoding="utf-8")
            thresholds.write_text("0.65\n", encoding="utf-8")
            with patch.object(
                export,
                "LABEL_GIT_BLOB_SHA1",
                export.git_blob_sha1(labels),
            ), self.assertRaisesRegex(ValueError, "threshold vector hash"):
                    export.load_labels(source)

    def test_git_blob_hash_matches_git_object_definition(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "labels.txt"
            path.write_bytes(b"bird\n")
            expected = hashlib.sha1(b"blob 5\0bird\n").hexdigest()
            self.assertEqual(expected, export.git_blob_sha1(path))

    def test_output_names_preserve_application_targets_contract(self):
        self.assertEqual(("logits", "probabilities", "targets"), export.OUTPUT_NAMES)
        self.assertEqual("targets", export.OUTPUT_NAMES[-1])

    def test_preprocessing_is_fixed_to_upstream_contract(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "white.png"
            Image.new("RGB", (12, 8), (255, 255, 255)).save(path)
            actual = validate_ram_plus.pixels(path)
        self.assertEqual((1, 3, 384, 384), actual.shape)
        np.testing.assert_allclose(
            actual[:, :, 0, 0],
            [[(1 - 0.485) / 0.229, (1 - 0.456) / 0.224, (1 - 0.406) / 0.225]],
            rtol=0,
            atol=1e-6,
        )

    def test_modification_notice_preserves_conversion_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            export.write_modifications(Path(directory))
            notice = (Path(directory) / "MODIFICATIONS.txt").read_text(encoding="utf-8")
        self.assertIn(export.SOURCE_REVISION, notice)
        self.assertIn(export.CHECKPOINT_REVISION, notice)
        self.assertIn("No training, fine-tuning, quantization", notice)
        self.assertIn("third-party notices", notice)

    def test_parity_enforces_numeric_tolerance_even_with_exact_targets(self):
        expected = [np.zeros((1, export.LABEL_COUNT), dtype=np.float32) for _ in range(3)]
        actual = [value.copy() for value in expected]
        actual[0][0, 0] = 0.1
        row = validate_ram_plus.compare_outputs(expected, actual)
        self.assertEqual(0, row["targetMismatchCount"])
        self.assertFalse(row["rawTolerancePassed"])
        self.assertFalse(validate_ram_plus.parity_passed([row]))

    def test_parity_accepts_tolerance_and_rejects_one_target_mismatch(self):
        expected = [np.zeros((1, export.LABEL_COUNT), dtype=np.float32) for _ in range(3)]
        actual = [value.copy() for value in expected]
        actual[0][0, 0] = 1e-5
        row = validate_ram_plus.compare_outputs(expected, actual)
        self.assertTrue(validate_ram_plus.parity_passed([row]))
        actual[2][0, 0] = 1
        row = validate_ram_plus.compare_outputs(expected, actual)
        self.assertEqual(1, row["targetMismatchCount"])
        self.assertFalse(validate_ram_plus.parity_passed([row]))

    def test_parity_rejects_nonfinite_reference_and_nonbinary_masks(self):
        expected = [np.zeros((1, export.LABEL_COUNT), dtype=np.float32) for _ in range(3)]
        actual = [value.copy() for value in expected]
        expected[0][0, 0] = np.inf
        row = validate_ram_plus.compare_outputs(expected, actual)
        self.assertFalse(row["finite"])
        self.assertIsNone(row["logitsMaxAbsoluteError"])
        self.assertFalse(validate_ram_plus.parity_passed([row]))
        expected[0][0, 0] = 0
        expected[2][0, 0] = actual[2][0, 0] = 0.5
        row = validate_ram_plus.compare_outputs(expected, actual)
        self.assertFalse(row["binaryTargets"])
        self.assertFalse(validate_ram_plus.parity_passed([row]))
        self.assertFalse(validate_ram_plus.parity_passed([]))

    def test_parity_rejects_wrong_output_dtype_or_shape(self):
        outputs = [np.zeros((1, export.LABEL_COUNT), dtype=np.float32) for _ in range(3)]
        outputs[0] = outputs[0].astype(np.float64)
        with self.assertRaisesRegex(ValueError, "float32"):
            validate_ram_plus.compare_outputs(outputs, outputs)
        outputs[0] = np.zeros((export.LABEL_COUNT,), dtype=np.float32)
        with self.assertRaisesRegex(ValueError, "float32"):
            validate_ram_plus.compare_outputs(outputs, outputs)

    def test_contract_validates_names_types_and_all_dimensions(self):
        image = SimpleNamespace(name="image", type="tensor(float)", shape=[1, 3, 384, 384])
        outputs = [SimpleNamespace(name=name, type="tensor(float)", shape=[1, 4585])
                   for name in export.OUTPUT_NAMES]
        runtime = SimpleNamespace(get_inputs=lambda: [image], get_outputs=lambda: outputs)
        validate_ram_plus.validate_contract(runtime)
        image.name = "wrong"
        with self.assertRaisesRegex(ValueError, "Unexpected ONNX"):
            validate_ram_plus.validate_contract(runtime)
        image.name = "image"
        outputs[2].type = "tensor(int64)"
        with self.assertRaisesRegex(ValueError, "Unexpected ONNX"):
            validate_ram_plus.validate_contract(runtime)
        outputs[2].type = "tensor(float)"
        outputs[2].shape = [1, 4584]
        with self.assertRaisesRegex(ValueError, "Unexpected ONNX"):
            validate_ram_plus.validate_contract(runtime)

    def test_image_globs_are_expanded_and_empty_matches_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "white.png"
            Image.new("RGB", (12, 8), (255, 255, 255)).save(path)
            self.assertEqual(
                [path.resolve()],
                validate_ram_plus.resolve_images([Path(directory) / "*.png", path]),
            )
            with self.assertRaisesRegex(ValueError, "No validation images"):
                validate_ram_plus.resolve_images([Path(directory) / "*.jpg"])

    def test_reference_executes_upstream_entry_point_and_removes_hook(self):
        import torch

        class ReferenceModel(torch.nn.Module):
            def __init__(self):
                super().__init__()
                self.fc = torch.nn.Identity()
                self.class_threshold = torch.full((export.LABEL_COUNT,), 0.65)
                self.delete_tag_index = [1]
                self.called = False

            def generate_tag(self, image):
                self.called = True
                self.fc(torch.ones((1, export.LABEL_COUNT, 1)))

        model = ReferenceModel()
        logits, probabilities, targets = validate_ram_plus.pytorch_output(
            model, np.zeros((1, 3, 384, 384), dtype=np.float32),
        )
        self.assertTrue(model.called)
        self.assertEqual((1, export.LABEL_COUNT), logits.shape)
        self.assertGreater(probabilities[0, 0], 0.65)
        self.assertEqual(1, targets[0, 0])
        self.assertEqual(0, targets[0, 1])
        self.assertFalse(model.fc._forward_hooks)


if __name__ == "__main__":
    unittest.main()
