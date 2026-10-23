import importlib.util
import tempfile
from pathlib import Path
import unittest


HAS_ARRAYS = all(importlib.util.find_spec(name) is not None for name in ["PIL", "numpy"])
HAS_BENCH = HAS_ARRAYS and all(importlib.util.find_spec(name) is not None
                             for name in ["onnxruntime", "psutil"])
HAS_TRAIN = HAS_ARRAYS and all(importlib.util.find_spec(name) is not None
                             for name in ["torch", "torchvision"])
HAS_CALIBRATION = HAS_ARRAYS and importlib.util.find_spec("onnxruntime") is not None


@unittest.skipUnless(HAS_ARRAYS, "Optional evaluation tests require Pillow/numpy")
class EvaluationTests(unittest.TestCase):
    def test_prior_download_unknown_rotation_still_excludes_identity_without_inventing_hash(self):
        from prepare_evaluation import filter_fresh
        prior = [{"sourceId": "old", "sha256": "old-bytes", "decodeExclusion": "unknown-source-rotation",
                  "provenance": {"AuthorProfileURL": "reserved-author", "OriginalMD5": "reserved-md5"}}]
        base = {"labels": {"animal": "positive"}, "provenance": {}, "dhash": 0xAAAAAAAAAAAAAAAA}
        rows = [{**base, "sourceId": "copy", "sha256": "old-bytes"},
                {**base, "sourceId": "author", "sha256": "other",
                 "provenance": {"AuthorProfileURL": "reserved-author"}},
                {**base, "sourceId": "good", "sha256": "good"}]
        kept, rejected = filter_fresh(rows, prior, [], [{"label": "animal", "parent": ""}])
        self.assertEqual(["good"], [row["sourceId"] for row in kept])
        self.assertEqual(2, len(rejected))
        self.assertTrue(all(row["reason"] == "prior-development-identity-or-author" for row in rejected))

    def test_grouping_is_transitive_for_authors_bytes_and_near_neighbors(self):
        from prepare_evaluation import group_partition
        rows = [
            {"sourceId": "a", "sha256": "a", "dhash": 0,
             "provenance": {"AuthorProfileURL": "author-a"}},
            {"sourceId": "b", "sha256": "b", "dhash": 0x3f,
             "provenance": {"AuthorProfileURL": "author-b"}},
            {"sourceId": "c", "sha256": "c", "dhash": 0xfff,
             "provenance": {"AuthorProfileURL": "author-c"}},
            {"sourceId": "d", "sha256": "d", "dhash": (1 << 64) - 1,
             "provenance": {"AuthorProfileURL": "author-a"}},
        ]
        actual = group_partition(rows, "seed")
        self.assertEqual(1, len({row["evaluationGroup"] for row in actual}))
        self.assertEqual(1, len({row["evaluationSplit"] for row in actual}))
        self.assertEqual(actual, group_partition(list(reversed(rows)), "seed"))

    def test_fresh_filter_excludes_prior_identity_authors_near_matches_and_bad_labels(self):
        from prepare_evaluation import filter_fresh
        prior = [{"sourceId": "old", "sha256": "old-bytes", "dhash": 0,
                  "provenance": {"AuthorProfileURL": "old-author", "OriginalMD5": "old-md5"}}]
        base = {"labels": {"animal": "positive"}, "provenance": {}, "dhash": 0xAAAAAAAAAAAAAAAA}
        rows = [
            {**base, "sourceId": "author", "sha256": "a",
             "provenance": {"AuthorProfileURL": "old-author"}},
            {**base, "sourceId": "near", "sha256": "b", "dhash": 1},
            {**base, "sourceId": "private", "sha256": "c", "dhash": 0xFFFFFFFF00000000},
            {**base, "sourceId": "good", "sha256": "d"},
        ]
        kept, rejected = filter_fresh(rows, prior, [0xFFFFFFFF00000000],
                                      [{"label": "animal", "parent": ""}])
        self.assertEqual(["good"], [row["sourceId"] for row in kept])
        self.assertEqual({"prior-development-identity-or-author", "prior-development-near-hash",
                          "private-regression-near-hash"}, {r["reason"] for r in rejected})
        rows[-1]["labels"] = {"animal": "yes"}
        with self.assertRaisesRegex(ValueError, "Unknown public label"):
            filter_fresh(rows, prior, [], [{"label": "animal", "parent": ""}])

    def test_numpy_preprocessing_uses_source_rotation_or_private_exif_not_both(self):
        import numpy as np
        from PIL import Image
        from pilot_inference import prepare_tensor
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "photo.png"
            array = np.zeros((12, 20, 3), dtype=np.uint8)
            array[:, :10, 0] = 255
            image = Image.fromarray(array)
            exif = image.getexif()
            exif[274] = 6
            image.save(path, exif=exif)
            public = prepare_tensor(path, "270")
            private = prepare_tensor(path)
            np.testing.assert_array_equal(public, private)
            self.assertEqual((1, 3, 224, 224), public.shape)
            self.assertEqual(np.float32, public.dtype)
            self.assertTrue(public.flags.c_contiguous)
            self.assertFalse(np.array_equal(public, prepare_tensor(path, "0")))
            with self.assertRaises(ValueError):
                prepare_tensor(path, "nan")

    def review(self):
        return {"schemaVersion": 1, "photos": [{"id": 0, "image": "a.jpg"}],
                "items": [
                    {"id": 0, "photo": 0, "label": "cat", "initialDecision": "correct"},
                    {"id": 1, "photo": 0, "label": "animal", "initialDecision": "pending"},
                    {"id": 2, "photo": 0, "label": "bird", "initialDecision": "incorrect"},
                    {"id": 3, "photo": 0, "label": "kitten", "initialDecision": "incorrect"},
                ], "rows": [{"image": "a.jpg", "model": "old",
                             "predictions": [{"item": 0}, {"item": 2}]}]}

    def test_partial_judgments_never_infer_negatives_ancestors_or_aliases(self):
        from evaluate_regression import reviewed_pairs, summarize, baseline_emissions
        labels = ["cat", "animal", "bird"]
        decisions, coverage = reviewed_pairs(self.review(), labels)
        self.assertEqual({("a.jpg", "cat"): True, ("a.jpg", "bird"): False}, decisions)
        self.assertEqual(2, coverage["sharedReviewedPairs"])
        emitted = {("a.jpg", "cat"), ("a.jpg", "animal")}
        summary = summarize(decisions, emitted, labels)
        self.assertEqual(1, summary["tp"])
        self.assertEqual(0, summary["fp"])
        self.assertEqual(1, summary["unreviewedEmittedPairs"])
        self.assertEqual(1, summary["microPrecision"])
        self.assertEqual(2, len(baseline_emissions(self.review(), labels)["old"]))
        review = self.review()
        review["items"][1].update(initialDecision=None, reason="conflict")
        _, coverage = reviewed_pairs(review, labels)
        self.assertEqual(1, coverage["states"]["conflict"])
        review["items"][1]["reason"] = "new"
        _, coverage = reviewed_pairs(review, labels)
        self.assertEqual(1, coverage["states"]["pending"])

    def test_review_schema_duplicates_conflicts_and_bad_baseline_references_fail(self):
        from evaluate_regression import reviewed_pairs, baseline_emissions
        review = self.review()
        review["items"].append({"id": 4, "photo": 0, "label": "cat", "initialDecision": "incorrect"})
        with self.assertRaisesRegex(ValueError, "reviewed photo-label"):
            reviewed_pairs(review, ["cat"])
        review = self.review()
        review["items"][0]["initialDecision"] = "surprise"
        with self.assertRaisesRegex(ValueError, "Unknown review"):
            reviewed_pairs(review, ["cat"])
        review = self.review()
        review["rows"][0]["image"] = "wrong.jpg"
        with self.assertRaisesRegex(ValueError, "wrong reviewed photo"):
            baseline_emissions(review, ["cat"])

    def test_comparison_requires_identical_ordered_images_hashes_and_model(self):
        import copy
        import numpy as np
        from evaluate_regression import compare_providers
        reference = {"modelSha256": "hash", "labels": ["cat"],
                     "images": [{"image": "a.jpg", "sha256": "photo"}],
                     "requestedProvider": "CPU"}
        other = copy.deepcopy(reference)
        other["requestedProvider"] = "DML"
        report = compare_providers(reference, np.array([[0.49]]), other, np.array([[0.51]]))
        self.assertEqual(1, report["cutoff05Flips"])
        other["images"][0]["sha256"] = "changed"
        with self.assertRaisesRegex(ValueError, "identical model"):
            compare_providers(reference, np.array([[0.49]]), other, np.array([[0.51]]))

    def test_runtime_rejects_nonfinite_or_invalid_score_arrays(self):
        import numpy as np
        from unittest.mock import Mock
        from pilot_inference import infer
        session = Mock()
        for scores in [np.array([[float("nan")]]), np.array([[1.1]]), np.zeros((1, 2))]:
            session.run.return_value = [scores]
            with self.assertRaisesRegex(ValueError, "invalid independent"):
                infer(session, np.zeros((1, 3, 224, 224)), 1)


@unittest.skipUnless(HAS_TRAIN, "Optional parity test requires training environment")
class PreprocessingParityTests(unittest.TestCase):
    def test_numpy_preprocessing_is_bit_exact_to_training(self):
        import numpy as np
        from PIL import Image
        from pilot_inference import prepare_tensor
        from train_pilot import image_tensor
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "photo.png"
            Image.fromarray(np.random.default_rng(66).integers(0, 256, (73, 119, 3),
                                                             dtype=np.uint8)).save(path)
            for rotation in ["0", "90", "180", "270"]:
                expected = image_tensor({"path": str(path), "provenance": {"Rotation": rotation}})
                np.testing.assert_array_equal(expected.numpy()[None], prepare_tensor(path, rotation))
                scratch = image_tensor({"path": str(path), "provenance": {"Rotation": rotation}},
                                       [0.5] * 3, [0.5] * 3)
                np.testing.assert_array_equal(
                    scratch.numpy()[None], prepare_tensor(path, rotation, [0.5] * 3, [0.5] * 3))
            with self.assertRaisesRegex(ValueError, "normalization"):
                prepare_tensor(path, "0", [0.5, 0.5], [0.5, 0.5])


@unittest.skipUnless(HAS_BENCH, "Optional provider tests require evaluation environment")
class PlacementTests(unittest.TestCase):
    def test_profile_counts_provider_nodes_not_presence_in_provider_list(self):
        from benchmark_pilot import placement
        result = placement([
            {"name": "gpu", "cat": "Node", "args": {"provider": "DmlExecutionProvider"}, "dur": 10},
            {"name": "gpu", "cat": "Node", "args": {"provider": "DmlExecutionProvider"}, "dur": 12},
            {"name": "shape", "cat": "Node", "args": {"provider": "CPUExecutionProvider"}, "dur": 1},
            {"name": "session", "cat": "Session"},
        ])
        self.assertEqual({"DmlExecutionProvider": 1, "CPUExecutionProvider": 1},
                         result["uniqueNodesByProvider"])
        self.assertEqual(22, result["nodeDurationUsByProvider"]["DmlExecutionProvider"])
        self.assertEqual(["shape"], result["cpuNodes"])
        with self.assertRaisesRegex(ValueError, "no provider node"):
            placement([])


@unittest.skipUnless(HAS_CALIBRATION, "Optional calibration tests require evaluation environment")
class CalibrationTests(unittest.TestCase):
    def test_cross_validation_fits_without_check_groups_and_pools_masked_decisions(self):
        import numpy as np
        from unittest.mock import patch
        from calibrate_pilot import calibration_cross_validation
        from prepare_dataset import fingerprint
        seed = "expanded-calibration-five-fold-v1"
        chosen = {}
        for i in range(100):
            group = f"group-{i}"
            chosen.setdefault(int(fingerprint([seed, group]), 16) % 5, group)
        self.assertEqual(5, len(chosen))
        groups = [group for _, group in sorted(chosen.items()) for _ in range(3)]
        scores = np.tile([[0.9], [0.1], [0.99]], (5, 1))
        targets = np.tile([[1], [0], [-1]], (5, 1))
        def fit(values, actual, fit_groups, policy):
            self.assertEqual(4, len(set(fit_groups)))
            self.assertEqual(12, len(values))
            return {"threshold": 0.5}
        with patch("calibrate_pilot.choose_threshold", side_effect=fit) as mocked:
            result = calibration_cross_validation(scores, targets, ["cat"], groups, self.policy())
            self.assertEqual(5, mocked.call_count)
        summary = result["pooledOutOfFold"]
        self.assertEqual((5, 0, 0), (summary["tp"], summary["fp"], summary["fn"]))
        self.assertEqual(5, summary["unknownEmittedTags"])
        self.assertIsNone(summary["enabledLabels"])
        self.assertEqual(5, summary["perLabel"][0]["enabledFolds"])
        self.assertNotIn("threshold", summary["perLabel"][0])
        self.assertTrue(all(report["checkGroups"] == 1 for report in result["perFold"]))
        with patch("calibrate_pilot.choose_threshold", return_value={"threshold": None}):
            result = calibration_cross_validation(scores, targets, ["cat"], groups, self.policy())
            self.assertEqual(5, result["pooledOutOfFold"]["fn"])
            self.assertEqual(0, result["pooledOutOfFold"]["perLabel"][0]["enabledFolds"])
        with self.assertRaisesRegex(ValueError, "Every grouped"):
            calibration_cross_validation(scores, targets, ["cat"], ["same"] * 15, self.policy())
        with self.assertRaisesRegex(ValueError, "Invalid grouped"):
            calibration_cross_validation(scores, targets, ["cat"], groups[:-1], self.policy())

    def policy(self):
        return {"targetPrecision": 0.9, "minimumWilsonLower95": 0.75, "minimumAccepted": 20,
                "minimumKnownPositive": 10, "minimumKnownNegative": 10, "minimumGroupsPerState": 5}

    def test_fit_maximizes_known_true_positives_ignoring_unknown_targets(self):
        import numpy as np
        from calibrate_pilot import choose_threshold
        scores = np.array([0.95] * 20 + [0.4] * 10 + [0.1] * 10 + [0.99] * 100)
        targets = np.array([1] * 30 + [0] * 10 + [-1] * 100)
        fitted = choose_threshold(scores, targets, [str(i) for i in range(140)], self.policy())
        self.assertEqual(0.4, fitted["threshold"])
        self.assertEqual(30, fitted["acceptedKnownEntries"])
        self.assertEqual(1, fitted["precision"])
        self.assertGreater(fitted["descriptiveWilson95"][0], 0.75)

    def test_labels_with_sparse_or_correlated_evidence_abstain(self):
        import numpy as np
        from calibrate_pilot import choose_threshold
        scores = np.array([0.9] * 20 + [0.1] * 20)
        targets = np.array([1] * 20 + [0] * 20)
        self.assertIsNone(choose_threshold(scores, targets, ["one-group"] * 40,
                                           self.policy())["threshold"])
        self.assertIsNone(choose_threshold(np.ones(40), targets, [str(i) for i in range(40)],
                                           self.policy())["threshold"])

    def test_summary_keeps_disabled_positives_in_recall_and_unknown_emissions_separate(self):
        import numpy as np
        from calibrate_pilot import summarize, wilson
        result = summarize(np.array([[0.99, 0.9], [0.9, 0.1]]),
                           np.array([[-1, 1], [1, 0]]), ["a", "b"], [0.5, None])
        self.assertEqual(1, result["enabledLabels"])
        self.assertEqual(0.5, result["microRecall"])
        self.assertEqual(1, result["microPrecision"])
        self.assertEqual(1, result["unknownEmittedTags"])
        self.assertIsNone(wilson(0, 0))
        self.assertGreater(wilson(20, 20)[0], 0.8)


if __name__ == "__main__":
    unittest.main()
