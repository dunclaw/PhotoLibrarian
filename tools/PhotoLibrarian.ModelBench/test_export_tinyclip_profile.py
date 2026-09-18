"""Offline tests for exporting deployable TinyCLIP calibration without review data."""

import copy
import hashlib
import json
from pathlib import Path
import unittest

from export_tinyclip_profile import export_profile


class ExportTinyClipProfileTests(unittest.TestCase):
    def setUp(self):
        self.vocabulary = json.loads(
            (Path(__file__).parent / "home-photo-vocabulary.review-v3.json").read_text(
                encoding="utf-8"))
        fingerprint = hashlib.sha256(json.dumps(
            self.vocabulary["labels"], ensure_ascii=False, separators=(",", ":")).encode()).hexdigest()
        self.calibration = {"privatePhotoPath": "private-photo.jpg", "models": {"tinyclip": {
            "metadata": {
                "provider": "Cpu", "scoreKind": "cosine", "precision": "int8",
                "modelSha256": "a" * 64, "vocabularySha256": fingerprint,
            },
            "operatingPoints": [{
                "requestedPrecision": 0.75,
                "experimentalFullDataFit": {
                    "global": {"threshold": 0.27452028},
                    "labels": {
                        "bird": {"status": "fitted", "threshold": 0.21320139},
                        "lake": {"status": "abstain", "threshold": None},
                        "cat": {"status": "global_fallback_insufficient_evidence", "threshold": 0.9},
                    },
                },
            }],
            "humanAnnotations": ["private-review"],
        }}}

    def test_exports_fixed_candidate_policy_without_personal_data(self):
        profile = export_profile(self.calibration, self.vocabulary)
        self.assertEqual(10, profile["candidateLimit"])
        self.assertEqual(247, len(profile["labels"]))
        self.assertNotIn("beak", profile["labels"])
        self.assertEqual(0.27452028, profile["fallbackThreshold"])
        self.assertEqual({"bird": 0.21320139, "lake": None}, profile["thresholds"])
        self.assertNotIn("private", json.dumps(profile))

    def test_exports_selected_broader_policy_without_changing_the_default(self):
        broader = copy.deepcopy(self.calibration["models"]["tinyclip"]["operatingPoints"][0])
        broader["requestedPrecision"] = 0.6
        broader["experimentalFullDataFit"]["global"]["threshold"] = 0.3033498
        self.calibration["models"]["tinyclip"]["operatingPoints"].append(broader)
        profile = export_profile(self.calibration, self.vocabulary, 0.6)
        self.assertEqual(0.6, profile["requestedPrecision"])
        self.assertEqual(0.3033498, profile["fallbackThreshold"])
        self.assertEqual({"bird": 0.21320139, "lake": None}, profile["thresholds"])
        self.assertEqual(10, profile["candidateLimit"])
        self.assertEqual(0.75, export_profile(self.calibration, self.vocabulary)["requestedPrecision"])

    def test_rejects_missing_or_invalid_operating_points(self):
        for target in (0.6, 0, -1, 1.1, float("nan"), float("inf"), True, "0.75"):
            with self.subTest(target=target), self.assertRaises(ValueError):
                export_profile(self.calibration, self.vocabulary, target)

    def test_rejects_incompatible_scoring_identity(self):
        for key, value in (("provider", "DirectML"), ("scoreKind", "softmax"),
                           ("precision", "fp32"), ("vocabularySha256", "b" * 64)):
            with self.subTest(key=key):
                calibration = copy.deepcopy(self.calibration)
                calibration["models"]["tinyclip"]["metadata"][key] = value
                with self.assertRaisesRegex(ValueError, "CPU INT8 vocabulary"):
                    export_profile(calibration, self.vocabulary)

    def test_partial_review_evidence_stays_explicit_without_photo_details(self):
        self.calibration["models"]["tinyclip"]["rankingQuality"] = {
            "reviewedCandidates": 120, "pendingCandidates": 1440, "fullyReviewedRows": 0,
            "privateAnnotationRows": ["do not export"],
        }
        evidence = export_profile(self.calibration, self.vocabulary)["evidence"]
        self.assertEqual(120, evidence["reviewedCandidates"])
        self.assertEqual(1440, evidence["pendingCandidates"])
        self.assertEqual(0, evidence["fullyReviewedRows"])
        self.assertNotIn("privateAnnotationRows", evidence)
        self.assertIn("provisional", evidence["warning"])

    def test_rejects_changed_vocabulary(self):
        for labels in (["bird", "beak"], ["bird", "bird"], ["cat"]):
            with self.subTest(labels=labels):
                with self.assertRaises(ValueError):
                    export_profile(self.calibration, {"labels": labels})

    def test_rejects_unfitted_or_invalid_global_threshold(self):
        for threshold in (None, -0.1, 1.1, float("nan"), float("inf")):
            with self.subTest(threshold=threshold):
                calibration = copy.deepcopy(self.calibration)
                calibration["models"]["tinyclip"]["operatingPoints"][0][
                    "experimentalFullDataFit"]["global"]["threshold"] = threshold
                with self.assertRaisesRegex(ValueError, "fitted global fallback"):
                    export_profile(calibration, self.vocabulary)

    def test_rejects_invalid_per_label_cutoffs(self):
        for label, threshold in (("beak", 0.3), ("bird", -0.1),
                                 ("bird", float("nan")), ("bird", float("inf"))):
            with self.subTest(label=label, threshold=threshold):
                calibration = copy.deepcopy(self.calibration)
                calibration["models"]["tinyclip"]["operatingPoints"][0][
                    "experimentalFullDataFit"]["labels"][label] = {
                        "status": "fitted", "threshold": threshold}
                with self.assertRaisesRegex(ValueError, "per-label cutoff"):
                    export_profile(calibration, self.vocabulary)


if __name__ == "__main__":
    unittest.main()
