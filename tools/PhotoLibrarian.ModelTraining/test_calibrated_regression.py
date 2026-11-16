import copy
import importlib.util
import unittest


@unittest.skipUnless(all(importlib.util.find_spec(name) is not None for name in ["numpy", "PIL"]),
                     "Optional array dependencies")
class CalibratedRegressionTests(unittest.TestCase):
    def fixture(self):
        import numpy as np
        report = {"modelSha256": "model", "labels": ["cat", "animal"],
                  "images": [{"image": "a.jpg"}, {"image": "b.jpg"}]}
        calibration = {"operation": "fit", "modelSha256": "model",
                       "labels": ["cat", "animal"], "thresholds": [0.8, None]}
        return report, np.array([[0.8, 0.99], [0.79, 0.99]]), calibration

    def test_uses_fixed_threshold_including_equality_and_disabled_labels(self):
        from evaluate_regression import calibrated_emissions
        report, scores, calibration = self.fixture()
        original = copy.deepcopy(calibration)
        self.assertEqual({("a.jpg", "cat")}, calibrated_emissions(report, scores, calibration))
        self.assertEqual(original, calibration)

    def test_rejects_changed_model_label_order_invalid_threshold_or_operation(self):
        from evaluate_regression import calibrated_emissions
        report, scores, calibration = self.fixture()
        for field, value in [("operation", "test"), ("modelSha256", "other"),
                             ("labels", ["animal", "cat"]), ("thresholds", [0.8]),
                             ("thresholds", [float("nan"), None]), ("thresholds", [True, None]),
                             ("thresholds", [1.1, None])]:
            with self.assertRaises(ValueError):
                calibrated_emissions(report, scores, {**calibration, field: value})

    def test_unreviewed_calibrated_emissions_do_not_become_true_or_false_positives(self):
        from evaluate_regression import calibrated_emissions, summarize
        report, scores, calibration = self.fixture()
        emitted = calibrated_emissions(report, scores, calibration)
        result = summarize({("b.jpg", "cat"): True}, emitted, report["labels"])
        self.assertEqual((0, 0, 1, 1), tuple(result[key] for key in
                                           ["tp", "fp", "fn", "unreviewedEmittedPairs"]))


if __name__ == "__main__":
    unittest.main()
