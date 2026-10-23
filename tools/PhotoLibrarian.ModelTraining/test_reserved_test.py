import importlib.util
import unittest


@unittest.skipUnless(all(importlib.util.find_spec(name) is not None
                         for name in ["PIL", "numpy", "onnxruntime"]), "Optional inference dependencies")
class ReservedTestAuditTests(unittest.TestCase):
    def test_separate_test_binding_requires_original_calibration_hash_policy_and_model(self):
        import copy
        from calibrate_pilot import validate_test_calibration
        bundle = {"modelSha256": "model", "labels": ["cat"]}
        protocol = {"calibrationPolicy": {"targetPrecision": 0.9}}
        calibration = {**bundle, "operation": "fit", "protocolSha256": "original",
                       "thresholdPolicy": protocol["calibrationPolicy"]}
        validate_test_calibration(calibration, protocol, "original", "calibration", bundle)
        with self.assertRaises(ValueError):
            validate_test_calibration(calibration, protocol, "other", "calibration", bundle)
        bound = {**protocol, "calibrationProtocolSha256": "original",
                 "frozenCalibrationSha256": "calibration"}
        validate_test_calibration(calibration, bound, "new-test-protocol", "calibration", bundle)
        for field in ["calibrationProtocolSha256", "frozenCalibrationSha256"]:
            changed = {key: value for key, value in bound.items() if key != field}
            with self.assertRaises(ValueError):
                validate_test_calibration(calibration, changed, "new-test-protocol", "calibration", bundle)
        for field, value in [("modelSha256", "other"), ("labels", ["dog"]),
                             ("operation", "test"), ("thresholdPolicy", {"targetPrecision": 0.8})]:
            changed = {**calibration, field: value}
            with self.assertRaises(ValueError):
                validate_test_calibration(changed, bound, "new-test-protocol", "calibration", bundle)
        changed = copy.deepcopy(bound)
        changed["frozenCalibrationSha256"] = "changed"
        with self.assertRaises(ValueError):
            validate_test_calibration(calibration, changed, "new-test-protocol", "calibration", bundle)

    def test_counts_preserve_unknowns_disabled_labels_and_distinct_groups(self):
        from audit_reserved_test import state_coverage
        rows = [{"labels": {"cat": "positive"}, "evaluationGroup": f"p-{i}"} for i in range(10)]
        rows += [{"labels": {"cat": "negative"}, "evaluationGroup": f"n-{i}"} for i in range(10)]
        rows += [{"labels": {}, "evaluationGroup": "unknown"} for _ in range(20)]
        coverage = state_coverage(rows, ["cat", "dog"], [0.7, None])
        self.assertEqual((10, 10, 20), tuple(coverage[0][key] for key in ["positive", "negative", "unknown"]))
        self.assertTrue(coverage[0]["descriptiveStateGroupSupport"])
        self.assertFalse(coverage[1]["calibrationEnabled"])
        self.assertEqual(40, coverage[1]["unknown"])
        for row in rows:
            row["evaluationGroup"] = "one"
        self.assertFalse(state_coverage(rows, ["cat"], [0.7])[0]["descriptiveStateGroupSupport"])

    def test_rejects_invalid_groups_or_label_threshold_alignment(self):
        from audit_reserved_test import state_coverage
        rows = [{"labels": {}, "evaluationGroup": "g"}]
        for labels, thresholds, records in [(["cat"], [], rows), (["cat", "cat"], [None, None], rows),
                                            (["cat"], [None], []),
                                            (["cat"], [None], [{"labels": {}}])]:
            with self.assertRaises(ValueError):
                state_coverage(records, labels, thresholds)


if __name__ == "__main__":
    unittest.main()
