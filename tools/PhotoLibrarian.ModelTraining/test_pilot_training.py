import importlib.util
import math
from pathlib import Path
import tempfile
import unittest


HAS_PIL = importlib.util.find_spec("PIL") is not None
HAS_TORCH = importlib.util.find_spec("torch") is not None and importlib.util.find_spec("torchvision") is not None
HAS_EXPORT = HAS_TORCH and importlib.util.find_spec("onnxruntime") is not None and importlib.util.find_spec("onnx") is not None


@unittest.skipUnless(HAS_PIL, "Optional research tests require requirements-training.txt (Pillow)")
class PilotDataTests(unittest.TestCase):
    def setUp(self):
        from pilot_data import audit_and_gate
        self.audit = audit_and_gate
        self.concepts = [
            {"label": "animal", "parent": ""},
            {"label": "cat", "parent": "animal"},
            {"label": "computer", "parent": ""},
        ]
        self.mappings = [{"label": r["label"], "negativePolicy": "all" if r["label"] != "computer"
                          else "positive-only"} for r in self.concepts]

    def row(self, identity, labels=None, sha=None, dhash=0, author=None):
        return {
            "sourceId": identity, "labels": labels or {"animal": "positive", "cat": "positive"},
            "sha256": sha or identity, "dhash": dhash,
            "provenance": {"OriginalMD5": identity, "AuthorProfileURL": author},
        }

    def base(self):
        return [
            self.row("t1", dhash=0),
            self.row("t2", {"animal": "negative", "cat": "negative"}, dhash=(1 << 64) - 1),
        ], [
            self.row("v1", dhash=0xAAAAAAAAAAAAAAAA),
            self.row("v2", {"animal": "negative", "cat": "negative"}, dhash=0x5555555555555555),
        ]

    def test_label_gate_requires_known_positives_and_negatives_and_excludes_positive_only(self):
        a, b = self.base()
        for row in a + b:
            row["labels"]["computer"] = "positive"
        train, validation, labels, report = self.audit(
            a, b, self.concepts, self.mappings, [], 6, 1, 1)
        self.assertEqual(["animal", "cat"], labels)
        self.assertEqual(2, len(train))
        self.assertEqual(2, len(validation))
        self.assertFalse(report["productionApproved"])
        self.assertTrue(report["researchOnly"])
        with self.assertRaisesRegex(ValueError, "No usable pilot"):
            self.audit(a, b, self.concepts, self.mappings, [], 6, 2, 1)

    def test_source_byte_author_near_and_private_overlap_exclusions_are_recorded(self):
        a, b = self.base()
        b[0]["provenance"]["AuthorProfileURL"] = "held-author"
        additions = [
            self.row("duplicate", sha="v1", dhash=123),
            self.row("author-overlap", author="held-author", dhash=123),
            self.row("near-overlap", dhash=0xAAAAAAAAAAAAAAAB),
            self.row("private-overlap", dhash=0x00000000FFFFFFFF),
            self.row("copy", sha="t1", dhash=123),
            self.row("conflict", labels={"animal": "negative", "cat": "positive"}, dhash=123),
        ]
        train, _, _, report = self.audit(
            a + additions, b, self.concepts, self.mappings, [0x00000000FFFFFFFF], 6, 1, 1)
        self.assertEqual(["t1", "t2"], [row["sourceId"] for row in train])
        self.assertEqual({
            "validation-image-overlap": 1, "validation-author-overlap": 1,
            "validation-near-hash": 1, "private-regression-near-hash": 1,
            "duplicate-training-bytes": 1, "source-hierarchy-conflict": 1,
        }, report["quarantineCounts"])

    def test_invalid_states_do_not_become_unknown(self):
        a, b = self.base()
        a[0]["labels"]["cat"] = "yes"
        with self.assertRaisesRegex(ValueError, "label/state"):
            self.audit(a, b, self.concepts, self.mappings, [], 6, 1, 1)

    def test_rotation_is_explicit_and_hash_is_reproducible(self):
        from PIL import Image
        from pilot_data import difference_hash, oriented_image
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.png"
            image = Image.new("RGB", (32, 16), "red")
            image.save(path)
            rotated = oriented_image(path, "90")
            self.assertEqual((16, 32), rotated.size)
            self.assertEqual(difference_hash(image), difference_hash(Image.open(path)))
            for bad in ["nan", "45", "inf"]:
                with self.assertRaises(ValueError):
                    oriented_image(path, bad)

    def test_unknown_source_orientation_is_quarantined_not_guessed(self):
        a, b = self.base()
        a.append({"sourceId": "unknown-rotation", "decodeExclusion": "unknown-source-rotation"})
        train, _, _, report = self.audit(a, b, self.concepts, self.mappings, [], 6, 1, 1)
        self.assertEqual(2, len(train))
        self.assertEqual({"unknown-source-rotation": 1}, report["quarantineCounts"])

    def test_contradictory_duplicate_annotations_quarantine_all_copies(self):
        a, b = self.base()
        a.extend([
            self.row("copy-a", sha="duplicate", labels={"cat": "positive"}, dhash=1),
            self.row("copy-b", sha="duplicate", labels={"cat": "negative"}, dhash=2),
        ])
        train, _, _, report = self.audit(a, b, self.concepts, self.mappings, [], 6, 1, 1)
        self.assertEqual(["t1", "t2"], [r["sourceId"] for r in train])
        self.assertEqual({"duplicate-annotation-conflict": 2}, report["quarantineCounts"])

    def test_load_downloads_verifies_bytes_path_and_orientation(self):
        import hashlib
        import json
        from PIL import Image
        from pilot_data import load_downloads
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "photo.png"
            Image.new("RGB", (12, 20), "red").save(path)
            row = {"sourceId": "synthetic", "path": "photo.png", "originalSplit": "train",
                   "provenance": {"Rotation": ""}, "labels": {"cat": "positive"},
                   "download": {"sha256": hashlib.sha256(path.read_bytes()).hexdigest()}}
            manifest = root / "download-manifest.jsonl"
            manifest.write_text(json.dumps(row) + "\n", encoding="utf-8")
            loaded = load_downloads(manifest, "train")
            self.assertEqual("unknown-source-rotation", loaded[0]["decodeExclusion"])
            row["provenance"]["Rotation"] = "90"
            manifest.write_text(json.dumps(row) + "\n", encoding="utf-8")
            self.assertIn("dhash", load_downloads(manifest, "train")[0])
            with self.assertRaisesRegex(ValueError, "split mismatch"):
                load_downloads(manifest, "validation")
            path.write_bytes(b"corrupt")
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                load_downloads(manifest, "train")


@unittest.skipUnless(HAS_TORCH, "Optional training tests require requirements-training.txt (PyTorch)")
class MaskedTrainingTests(unittest.TestCase):
    def test_unknown_entries_have_no_loss_or_gradient(self):
        import torch
        from train_pilot import masked_loss
        logits = torch.tensor([[0.0, 100.0, 0.0]], requires_grad=True)
        targets = torch.tensor([[1.0, -1.0, 0.0]])
        loss, count = masked_loss(logits, targets)
        self.assertEqual(2, count)
        self.assertAlmostEqual(math.log(2), loss.item(), places=6)
        loss.backward()
        self.assertEqual(0, logits.grad[0, 1].item())
        self.assertLess(logits.grad[0, 0].item(), 0)
        self.assertGreater(logits.grad[0, 2].item(), 0)

    def test_all_unknown_batches_skip_update_and_invalid_inputs_fail(self):
        import torch
        from train_pilot import masked_loss
        loss, count = masked_loss(torch.zeros(1, 2), torch.full((1, 2), -1.0))
        self.assertIsNone(loss)
        self.assertEqual(0, count)
        for logits, targets in [
            (torch.zeros(1, 2), torch.tensor([[1.0, 0.5]])),
            (torch.tensor([[float("nan"), 0.0]]), torch.zeros(1, 2)),
            (torch.zeros(1, 2), torch.zeros(1, 3)),
        ]:
            with self.assertRaises(ValueError):
                masked_loss(logits, targets)

    def test_metrics_ignore_unknowns_and_undefined_precision_is_not_perfect(self):
        import numpy as np
        from train_pilot import metrics
        report = metrics(np.array([[0.9, 0.99], [0.8, 0.2], [0.1, 0.1]]),
                         np.array([[1, -1], [0, 1], [-1, 0]]), ["a", "b"])
        self.assertEqual(4, report["knownEntries"])
        self.assertEqual(0.5, report["microPrecision"])
        self.assertEqual(0.5, report["microRecall"])
        self.assertIsNone(report["perLabel"][1]["precision"])
        self.assertEqual(0, report["perLabel"][1]["recall"])
        self.assertFalse(report["thresholdCalibrated"])

    def test_dataset_excludes_all_unknown_examples_and_never_infers_ancestors(self):
        from train_pilot import PilotDataset
        dataset = PilotDataset([
            {"sourceId": "a", "labels": {"cat": "positive"}},
            {"sourceId": "b", "labels": {"computer": "positive"}},
        ], ["cat", "animal"], False)
        self.assertEqual(1, len(dataset))
        self.assertEqual({"cat": "positive"}, dataset.rows[0]["labels"])


@unittest.skipUnless(HAS_EXPORT, "Optional export tests require requirements-training.txt")
class ExportTests(unittest.TestCase):
    def test_development_scoring_preserves_unknowns_and_checks_images_and_outputs(self):
        import hashlib
        import numpy as np
        import torch
        from unittest.mock import Mock, patch
        from export_pilot import development_scores
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image"
            path.write_bytes(b"synthetic")
            row = {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                   "labels": {"cat": "positive"}}
            session = Mock()
            session.run.return_value = [np.array([[0.9, 0.1]])]
            with patch("export_pilot.image_tensor", return_value=torch.zeros(3, 224, 224)):
                scores, targets = development_scores(
                    session, [{"labels": {"other": "positive"}}, row], ["cat", "animal"])
                np.testing.assert_array_equal(targets, [[1, -1]])
                np.testing.assert_allclose(scores, [[0.9, 0.1]])
                self.assertEqual(1, session.run.call_count)
                session.run.return_value = [np.array([[float("nan"), 0.1]])]
                with self.assertRaisesRegex(ValueError, "output contract"):
                    development_scores(session, [row], ["cat", "animal"])
            path.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "image changed"):
                development_scores(session, [row], ["cat", "animal"])

    def test_int8_calibration_rejects_validation_and_empty_rows(self):
        from export_pilot import TrainingCalibration
        for rows in [[], [{"originalSplit": "validation"}]]:
            with self.assertRaisesRegex(ValueError, "training examples only"):
                TrainingCalibration(rows, 2)

    def test_calibration_hash_gate_and_limit(self):
        import hashlib
        import torch
        from unittest.mock import patch
        from export_pilot import TrainingCalibration
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image"
            path.write_bytes(b"synthetic")
            row = {"originalSplit": "train", "path": str(path),
                   "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            reader = TrainingCalibration([row, row], 1)
            with patch("export_pilot.image_tensor", return_value=torch.zeros(3, 224, 224)):
                self.assertEqual((1, 3, 224, 224), reader.get_next()["image"].shape)
            self.assertIsNone(reader.get_next())
            reader = TrainingCalibration([row], 1)
            path.write_bytes(b"corrupt")
            with self.assertRaisesRegex(ValueError, "image changed"):
                reader.get_next()


if __name__ == "__main__":
    unittest.main()
