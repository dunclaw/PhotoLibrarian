import csv
from pathlib import Path
import tempfile
import unittest

from audit_calibration_coverage import audit


class CalibrationCoverageTests(unittest.TestCase):
    def fixture(self, root):
        annotations = root / "annotations.csv"
        with annotations.open("w", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(["ImageID", "Source", "LabelName", "Confidence"])
            for i in range(8):
                writer.writerow([f"{i:016x}", "verification", "/cat", "1" if i % 2 == 0 else "0"])
            writer.writerow(["0000000000000007", "machine", "/cat", "1"])
            writer.writerow(["0000000000000006", "verification", "/animal", "0"])
        metadata = root / "metadata.csv"
        with metadata.open("w", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=["ImageID", "Subset", "License", "Author",
                                                        "OriginalLandingURL", "OriginalMD5",
                                                        "AuthorProfileURL", "Rotation"])
            writer.writeheader()
            for i in range(8):
                writer.writerow({"ImageID": f"{i:016x}", "Subset": "validation",
                                 "License": "https://creativecommons.org/licenses/by/2.0/",
                                 "Author": "author", "OriginalLandingURL": "https://example.test/image",
                                 "OriginalMD5": f"md5-{i}", "AuthorProfileURL": f"author-{i}" if i != 7 else "",
                                 "Rotation": "nan" if i == 5 else "0"})
        concepts = [{"label": "animal", "parent": ""}, {"label": "cat", "parent": "animal"},
                    {"label": "pet", "parent": ""}]
        mappings = [{"label": "animal", "midList": ["/animal"], "negativePolicy": "all"},
                    {"label": "cat", "midList": ["/cat"], "negativePolicy": "all"},
                    {"label": "pet", "midList": ["/cat", "/dog"], "negativePolicy": "positive-only"}]
        return annotations, metadata, mappings, concepts

    def test_full_scan_counts_known_states_and_cumulative_identity_author_exclusions(self):
        with tempfile.TemporaryDirectory() as folder:
            paths = self.fixture(Path(folder))
            exclusions = [
                {"ids": {"0000000000000000"}, "authors": {"author-1"}, "md5": set()},
                {"ids": set(), "authors": set(), "md5": {"md5-2"}},
                {"ids": {"0000000000000003"}, "authors": set(), "md5": set()},
            ]
            rows, report = audit(*paths, exclusions)
            cat = next(row for row in rows if row["label"] == "cat")
            self.assertEqual((3, 3), (cat["eligible_positive"], cat["eligible_negative"]))
            self.assertEqual((2, 2), (cat["training_disjoint_positive"], cat["training_disjoint_negative"]))
            self.assertEqual((1, 2), (cat["development_disjoint_positive"], cat["development_disjoint_negative"]))
            self.assertEqual((1, 1), (cat["unused_positive"], cat["unused_negative"]))
            self.assertEqual(1, cat["unused_positive_authors"])
            self.assertEqual(0, cat["unused_negative_authors"])
            self.assertEqual(1, cat["unused_negative_missing_author"])
            pet = next(row for row in rows if row["label"] == "pet")
            self.assertEqual(0, pet["eligible_negative"])
            self.assertEqual(1, report["excludedAtFirstMatchingTier"]["hierarchy-conflict"])
            self.assertEqual(1, report["excludedAtFirstMatchingTier"]["unknown-or-unsupported-rotation"])
            self.assertEqual(2, report["images"]["unused"])
            self.assertEqual((rows, report), audit(*paths, exclusions))

    def test_conflicting_verified_states_and_wrong_exclusion_contract_fail(self):
        with tempfile.TemporaryDirectory() as folder:
            paths = self.fixture(Path(folder))
            with self.assertRaisesRegex(ValueError, "exclusion tiers"):
                audit(*paths, [])
            with paths[0].open("a", newline="") as stream:
                csv.writer(stream).writerow(["0000000000000000", "verification", "/cat", "0"])
            empty = {"ids": set(), "authors": set(), "md5": set()}
            with self.assertRaisesRegex(ValueError, "Conflicting verified"):
                audit(*paths, [empty, empty, empty])

    def test_multi_mid_negatives_require_all_states_and_missing_rows_stay_unknown(self):
        with tempfile.TemporaryDirectory() as folder:
            annotations, metadata, _, _ = self.fixture(Path(folder))
            with annotations.open("a", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerow(["0000000000000001", "crowdsource-verification", "/dog", "0.0"])
                writer.writerow(["0000000000000002", "machine", "/dog", "0"])
            mappings = [{"label": "pet", "midList": ["/cat", "/dog"], "negativePolicy": "all"}]
            empty = {"ids": set(), "authors": set(), "md5": set()}
            rows, _ = audit(annotations, metadata, mappings, [{"label": "pet", "parent": ""}],
                            [empty, empty, empty])
            row = rows[0]
            self.assertEqual(4, row["eligible_positive"])
            self.assertEqual(1, row["eligible_negative"])
            self.assertEqual(1, row["unused_negative"])

    def test_audit_is_read_only_and_rejects_duplicate_metadata(self):
        with tempfile.TemporaryDirectory() as folder:
            paths = self.fixture(Path(folder))
            before = [(path.read_bytes(), path.stat().st_mtime_ns) for path in paths[:2]]
            empty = {"ids": set(), "authors": set(), "md5": set()}
            audit(*paths, [empty, empty, empty])
            self.assertEqual(before, [(path.read_bytes(), path.stat().st_mtime_ns) for path in paths[:2]])
            with paths[1].open(newline="") as stream:
                reader = csv.DictReader(stream)
                fields, row = reader.fieldnames, next(reader)
            with paths[1].open("a", newline="") as stream:
                csv.DictWriter(stream, fieldnames=fields).writerow(row)
            with self.assertRaisesRegex(ValueError, "Duplicate validation"):
                audit(*paths, [empty, empty, empty])


if __name__ == "__main__":
    unittest.main()
