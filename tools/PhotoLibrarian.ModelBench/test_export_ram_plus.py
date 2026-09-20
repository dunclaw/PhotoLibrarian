"""Offline tests for the pinned RAM++ export inputs and output contract."""

import hashlib
import tempfile
from pathlib import Path
import unittest
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


if __name__ == "__main__":
    unittest.main()
