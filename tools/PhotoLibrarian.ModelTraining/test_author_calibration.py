import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


@unittest.skipUnless(all(importlib.util.find_spec(name) is not None
                         for name in ["PIL", "numpy", "onnxruntime"]), "Optional inference dependencies")
class AuthorCalibrationTests(unittest.TestCase):
    def test_binding_rejects_changed_run_reservation_exclusions_and_cohorts(self):
        from bind_author_calibration import check_run
        from prepare_dataset import file_hash, fingerprint
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            download, reservation = root / "download.jsonl", root / "reservation.json"
            download.write_text("frozen download")
            plan = {"downloadSha256": file_hash(download), "vocabularySha256": "vocab",
                    "mappingSha256": "mapping", "excludedDownloadSha256": ["prior"]}
            reservation.write_text(json.dumps(plan))
            cohorts = {"train": [{"sourceId": "t", "sha256": "t", "labels": {}}],
                       "validation": [{"sourceId": "v", "sha256": "v", "labels": {}}]}
            for name, rows in cohorts.items():
                (root / f"{name}-cohort.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows))
            (root / "best.pt").write_bytes(b"synthetic checkpoint identity only")
            checkpoint = file_hash(root / "best.pt")
            (root / "completion.json").write_text(json.dumps({"completed": True, "checkpointSha256": checkpoint}))
            config = {"labels": ["cat"], "inputFiles": {
                "authorReservation": {"sha256": file_hash(reservation)},
                "validation": {"sha256": file_hash(download)}, "vocabulary": {"sha256": "vocab"},
                "mapping": {"sha256": "mapping"}, "excludedImages0": {"sha256": "prior"}},
                "dataFingerprint": fingerprint({
                    name: [[r["sourceId"], r["sha256"], r["labels"]] for r in rows]
                    for name, rows in cohorts.items()} | {"labels": ["cat"]})}
            config_path = root / "run.json"
            config_path.write_text(json.dumps(config))
            bundle = {"runConfigSha256": file_hash(config_path), "checkpointSha256": checkpoint, "labels": ["cat"]}
            self.assertEqual(cohorts, check_run(root, reservation, download, bundle))
            for field in ["authorReservation", "validation", "mapping", "excludedImages0"]:
                changed = copy.deepcopy(config)
                changed["inputFiles"][field]["sha256"] = "changed"
                config_path.write_text(json.dumps(changed))
                changed_bundle = {**bundle, "runConfigSha256": file_hash(config_path)}
                with self.assertRaises(ValueError):
                    check_run(root, reservation, download, changed_bundle)
            config_path.write_text(json.dumps(config))
            with self.assertRaisesRegex(ValueError, "run/export mismatch"):
                check_run(root, reservation, download, {**bundle, "runConfigSha256": "changed"})
            (root / "train-cohort.jsonl").write_text(json.dumps({"sourceId": "other", "sha256": "t", "labels": {}}))
            with self.assertRaisesRegex(ValueError, "cohort fingerprint changed"):
                check_run(root, reservation, download, bundle)

    def test_binding_recomputes_transitive_groups_and_split_not_just_group_strings(self):
        from bind_author_calibration import calibration_rows
        from prepare_evaluation import group_partition
        rows = [{"sourceId": str(i), "sha256": str(i), "dhash": value,
                 "provenance": {"AuthorProfileURL": f"author-{i}"}}
                for i, value in enumerate([0, (1 << 64) - 1, int("aa" * 8, 16), int("55" * 8, 16)])]
        seed = "open-images-authors-first-v1"
        grouped = group_partition(rows, seed)
        plan = {"seed": seed, "splits": {
            name: [{"sourceId": row["sourceId"], "group": row["evaluationGroup"]}
                   for row in grouped if row["evaluationSplit"] == split]
            for name, split in [("development", "test"), ("calibration", "calibration")]}}
        self.assertTrue(all(plan["splits"].values()))
        expected = [row for row in grouped if row["evaluationSplit"] == "calibration"]
        self.assertEqual(expected, calibration_rows(plan, rows))
        changed = copy.deepcopy(plan)
        changed["splits"]["calibration"][0]["group"] = "invented-independent-group"
        with self.assertRaisesRegex(ValueError, "grouping or split changed"):
            calibration_rows(changed, rows)
        changed_rows = copy.deepcopy(rows)
        for row in changed_rows:
            row["provenance"]["AuthorProfileURL"] = "shared-author"
        with self.assertRaisesRegex(ValueError, "grouping or split changed"):
            calibration_rows(plan, changed_rows)


if __name__ == "__main__":
    unittest.main()
