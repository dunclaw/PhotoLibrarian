import copy
import importlib.util
import json
import math
from pathlib import Path
import tempfile
import unittest


HAS_RUNTIME = all(importlib.util.find_spec(name) is not None for name in ["numpy", "PIL", "onnxruntime"])


@unittest.skipUnless(HAS_RUNTIME, "Training comparison requires evaluation environment")
class TrainingComparisonTests(unittest.TestCase):
    def fixture(self, root):
        import numpy as np
        from prepare_dataset import file_hash, fingerprint
        cohorts = {
            "train": [{"sourceId": "t", "sha256": "t", "originalSplit": "train", "labels": {"cat": "positive"}}],
            "validation": [
                {"sourceId": "v1", "sha256": "v1", "originalSplit": "validation", "labels": {"cat": "positive"}},
                {"sourceId": "v2", "sha256": "v2", "originalSplit": "validation", "labels": {"cat": "negative"}},
                {"sourceId": "v3", "sha256": "v3", "originalSplit": "validation", "labels": {}}],
        }
        for split, rows in cohorts.items():
            (root / f"{split}-cohort.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows))
        config = {"labels": ["cat"], "dataFingerprint": fingerprint({
            **{split: [[r["sourceId"], r["sha256"], r["labels"]] for r in rows]
               for split, rows in cohorts.items()}, "labels": ["cat"]})}
        (root / "run.json").write_text(json.dumps(config))
        (root / "best.pt").write_bytes(b"synthetic checkpoint; never deserialized")
        np.savez_compressed(root / "validation-scores.npz", scores=np.array([[0.8], [0.2]]),
                            targets=np.array([[1], [0]]))
        completion = {"completed": True, "checkpointSha256": file_hash(root / "best.pt"),
                      "validationScoresSha256": file_hash(root / "validation-scores.npz")}
        (root / "completion.json").write_text(json.dumps(completion))
        return completion

    def test_load_development_verifies_frozen_cohorts_scores_and_checkpoint(self):
        import numpy as np
        from compare_training import load_development
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            self.fixture(root)
            config, _, scores, targets = load_development(root)
            self.assertEqual(["cat"], config["labels"])
            np.testing.assert_array_equal(targets, [[1], [0]])
            np.testing.assert_array_equal(scores, [[0.8], [0.2]])
            (root / "best.pt").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "changed training"):
                load_development(root)
            self.fixture(root)
            path = root / "validation-cohort.jsonl"
            path.write_text(path.read_text().replace('"positive"', '"negative"'))
            with self.assertRaisesRegex(ValueError, "cohorts changed"):
                load_development(root)

    def test_changed_ordered_targets_fail_even_if_score_receipt_is_updated(self):
        import numpy as np
        from compare_training import load_development
        from prepare_dataset import file_hash
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            completion = self.fixture(root)
            np.savez_compressed(root / "validation-scores.npz", scores=np.array([[0.8], [0.2]]),
                                targets=np.array([[0], [1]]))
            completion["validationScoresSha256"] = file_hash(root / "validation-scores.npz")
            (root / "completion.json").write_text(json.dumps(completion))
            with self.assertRaisesRegex(ValueError, "ordered targets"):
                load_development(root)

    def test_recipe_control_allows_only_the_predeclared_loss_difference(self):
        from compare_training import check_control
        fields = ["architecture", "labels", "initialization", "seed", "precision", "preprocessing",
                  "headEpochs", "finetuneEpochs", "headLearningRate", "finetuneLearningRate",
                  "batchSize", "workers", "batchNorm", "dataFingerprint", "inputFiles", "versions",
                  "device", "gpu"]
        baseline = dict.fromkeys(fields, "same")
        candidate = {**baseline, "loss": {"balance": "known-state", "weightCap": 4}}
        check_control(baseline, candidate)
        for key in fields:
            changed = copy.deepcopy(candidate)
            changed[key] = "different"
            with self.assertRaisesRegex(ValueError, "Controlled experiment"):
                check_control(baseline, changed)
        candidate["loss"]["weightCap"] = 8
        with self.assertRaisesRegex(ValueError, "capped known-state"):
            check_control(baseline, candidate)

    def test_summary_masks_unknowns_and_preserves_bce_and_ranking_semantics(self):
        import numpy as np
        from compare_training import development_summary
        scores = np.array([[0.8], [0.2], [0.99]])
        targets = np.array([[1], [0], [-1]])
        result = development_summary(scores, targets, ["cat"])
        self.assertAlmostEqual(-math.log(0.8), result["unweightedBceFromScores"])
        self.assertEqual(1, result["ranking"]["macroAveragePrecisionKnownEntries"])
        self.assertEqual(1, result["fixed05"]["unknownEmittedTags"])
        self.assertEqual(2, result["fixed05"]["knownEntries"])
        for bad in [np.full((3, 1), -1), np.full((3, 1), 2)]:
            with self.assertRaisesRegex(ValueError, "Invalid development"):
                development_summary(scores, bad, ["cat"])

    def test_expansion_requires_original_inputs_unweighted_recipe_and_exclusion_provenance(self):
        from compare_training import check_control
        fields = ["architecture", "initialization", "seed", "precision", "preprocessing",
                  "headEpochs", "finetuneEpochs", "headLearningRate", "finetuneLearningRate",
                  "batchSize", "workers", "batchNorm", "versions", "device", "gpu"]
        baseline = {**dict.fromkeys(fields, "same"), "labels": ["cat"], "dataFingerprint": "old",
                    "inputFiles": {"train": {"sha256": "train"}, "validation": {"sha256": "val"}}}
        candidate = {**baseline, "labels": ["cat", "dog"], "dataFingerprint": "new",
                     "loss": {"balance": "none"},
                     "inputFiles": {**baseline["inputFiles"], "additionalTrain0": {"sha256": "extra"},
                                    "excludedImages0": {"sha256": "reserved"}}}
        check_control(baseline, candidate, "expansion")
        for key in ["additionalTrain0", "excludedImages0"]:
            changed = copy.deepcopy(candidate)
            del changed["inputFiles"][key]
            with self.assertRaisesRegex(ValueError, "Expansion must"):
                check_control(baseline, changed, "expansion")
        changed = copy.deepcopy(candidate)
        changed["inputFiles"]["validation"] = {"sha256": "different"}
        with self.assertRaisesRegex(ValueError, "Expansion must"):
            check_control(baseline, changed, "expansion")
        changed = copy.deepcopy(candidate)
        changed["seed"] = "different"
        with self.assertRaisesRegex(ValueError, "Controlled experiment"):
            check_control(baseline, changed, "expansion")

    def test_common_label_alignment_handles_expanded_head_and_additional_supervised_rows(self):
        import numpy as np
        from compare_training import align_development
        rows = [{"sourceId": "a", "labels": {"cat": "positive"}},
                {"sourceId": "b", "labels": {"dog": "negative"}},
                {"sourceId": "c", "labels": {"cat": "negative", "dog": "positive"}}]
        a = {"labels": ["cat"]}
        b = {"labels": ["dog", "cat"]}
        base_scores, base_targets = np.array([[0.8], [0.1]]), np.array([[1], [0]])
        new_scores = np.array([[0.3, 0.7], [0.2, 0.3], [0.8, 0.4]])
        new_targets = np.array([[-1, 1], [0, -1], [1, 0]])
        labels, first, second, targets = align_development(
            a, b, rows, rows, base_scores, base_targets, new_scores, new_targets)
        self.assertEqual(["cat"], labels)
        np.testing.assert_array_equal(first, base_scores)
        np.testing.assert_array_equal(second, [[0.7], [0.4]])
        np.testing.assert_array_equal(targets, base_targets)
        with self.assertRaisesRegex(ValueError, "exactly unchanged"):
            align_development(a, b, rows, list(reversed(rows)), base_scores, base_targets,
                              new_scores, new_targets)
        with self.assertRaisesRegex(ValueError, "common targets differ"):
            align_development(a, b, rows, rows, base_scores, base_targets, new_scores,
                              np.array([[-1, 0], [0, -1], [1, 0]]))


if __name__ == "__main__":
    unittest.main()
