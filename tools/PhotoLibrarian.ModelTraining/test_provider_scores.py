import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


@unittest.skipUnless(all(importlib.util.find_spec(name) is not None
                         for name in ["PIL", "numpy"]), "Optional array dependencies")
class ProviderScoreTests(unittest.TestCase):
    def test_fixed_thresholds_and_disabled_labels_are_compared_without_refitting(self):
        import numpy as np
        from compare_provider_scores import score_parity
        cpu = np.array([[0.8, 0.5], [0.2, 0.4]], dtype=np.float32)
        directml = cpu.copy()
        directml[0, 0] = 0.79999
        result = score_parity(cpu, directml, ["cat", "dog"], [0.8, None])
        self.assertTrue(result["withinTolerance"])
        self.assertEqual(1, result["calibratedFlips"])
        self.assertEqual(0, result["cutoff05Flips"])
        self.assertEqual(1, result["enabledLabels"])
        self.assertEqual(0, result["perLabel"][1]["calibratedFlips"])
        self.assertEqual(0, score_parity(cpu, cpu, ["cat", "dog"], [0.8, None])["calibratedFlips"])

    def test_invalid_shapes_ranges_thresholds_and_nonfinite_values_fail(self):
        import numpy as np
        from compare_provider_scores import score_parity
        cpu = np.array([[0.5]])
        for other in [np.array([[np.nan]]), np.array([[1.1]]), np.zeros((2, 1)), np.zeros((1, 2))]:
            with self.assertRaises(ValueError):
                score_parity(cpu, other, ["cat"], [0.7])
        for thresholds in [[], [float("nan")], [-0.1], [True]]:
            with self.assertRaises(ValueError):
                score_parity(cpu, cpu, ["cat"], thresholds)

    def test_benchmark_identity_and_array_shape_changes_fail(self):
        import numpy as np
        from compare_provider_scores import load_scores
        from prepare_dataset import file_hash
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            np.savez_compressed(root / "scores.npz", scores=np.array([[0.5]]))
            bundle = {"modelSha256": "model", "modelBytes": 10, "labels": ["cat"]}
            report = {**bundle, "requestedProvider": "CPUExecutionProvider",
                      "scoresSha256": file_hash(root / "scores.npz"),
                      "pythonRuntimeFallbackDisabled": True, "imageCount": 1,
                      "images": [{"image": "a.jpg", "sha256": "image"}]}
            path = root / "benchmark.json"
            path.write_text(json.dumps(report))
            self.assertEqual((1, 1), load_scores(root, "CPUExecutionProvider", bundle)[1].shape)
            for field, value in [("labels", ["dog"]), ("modelSha256", "other"),
                                 ("scoresSha256", "other"), ("requestedProvider", "DmlExecutionProvider"),
                                 ("imageCount", 2), ("pythonRuntimeFallbackDisabled", False)]:
                path.write_text(json.dumps({**report, field: value}))
                with self.assertRaises(ValueError):
                    load_scores(root, "CPUExecutionProvider", bundle)
            path.write_text(json.dumps(report))
            np.savez_compressed(root / "scores.npz", scores=np.array([[0.5, 0.6]]))
            report["scoresSha256"] = file_hash(root / "scores.npz")
            path.write_text(json.dumps(report))
            with self.assertRaisesRegex(ValueError, "shape mismatch"):
                load_scores(root, "CPUExecutionProvider", bundle)


if __name__ == "__main__":
    unittest.main()
