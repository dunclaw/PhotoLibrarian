import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from prepare_dataset import load_records, prepare, vocabulary


HERE = Path(__file__).resolve().parent


class DatasetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.images = self.root / "images"
        self.images.mkdir()
        self.input = self.root / "inventory.jsonl"
        self.concepts, self.aliases = vocabulary(HERE / "pilot-vocabulary.csv")

    def record(self, identifier, *, content=None, groups=None, labels=None,
               prior=False, source="private"):
        name = f"{identifier}.jpg"
        (self.images / name).write_bytes(
            content if content is not None else f"synthetic-test-bytes:{identifier}".encode())
        return {
            "id": identifier, "path": name, "source": source, "sourceId": identifier,
            "groups": groups or [f"event:{identifier}"], "priorEvaluation": prior,
            "labels": labels or {},
            "provenance": {
                "imageLicense": "private-no-redistribution",
                "annotationLicense": "private-no-redistribution",
                "attribution": "synthetic test fixture",
            },
        }

    def load(self, rows):
        self.input.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        return load_records(self.input, self.images, self.aliases)

    def test_vocabulary_has_review_rules_and_valid_hierarchy(self):
        self.assertEqual(100, len(self.concepts))
        self.assertEqual("dog", self.aliases["puppy"])
        self.assertEqual("sunset or sunrise", self.aliases["sunrise"])
        self.assertEqual(10, len({item["category"] for item in self.concepts}))

    def test_partial_labels_do_not_become_negatives_or_synthetic_ancestors(self):
        rows = self.load([self.record("one", labels={
            "puppy": "positive", "cat": "negative", "bird": "unknown"})])
        output, report = prepare(rows, self.concepts, "seed")
        states = dict(zip(report["labels"], output[0]["targets"]))
        self.assertEqual(1, states["dog"])
        self.assertEqual(0, states["cat"])
        self.assertEqual(-1, states["bird"])
        self.assertEqual(-1, states["animal"])
        self.assertEqual(0.02, report["knownLabelFraction"])
        self.assertFalse(report["trainingReady"])
        self.assertFalse(report["productionApproved"])

    def test_transitive_event_and_duplicate_groups_never_leak(self):
        rows = self.load([
            self.record("a", groups=["event:1"], content=b"duplicate", labels={"dog": "positive"}),
            self.record("b", groups=["event:2"], content=b"duplicate", labels={"dog": "positive"}),
            self.record("c", groups=["event:2"], prior=True),
        ])
        output, report = prepare(rows, self.concepts, "seed")
        self.assertEqual(1, report["independentGroups"])
        self.assertEqual(2, report["exactUniqueImages"])
        self.assertEqual({"train"}, {row["split"] for row in output})
        self.assertEqual(1, len({row["groupId"] for row in output}))
        self.assertEqual(1, report["perLabel"]["dog"]["train"]["positiveGroups"])
        self.assertEqual(2, report["perLabel"]["dog"]["train"]["positive"])
        self.assertEqual(1, report["priorEvaluationGroupsForcedToTrain"])

    def test_source_id_also_connects_different_encodings(self):
        a = self.record("a")
        b = self.record("b", prior=True)
        b["sourceId"] = a["sourceId"]
        output, report = prepare(self.load([a, b]), self.concepts, "seed")
        self.assertEqual(1, report["independentGroups"])
        self.assertEqual({"train"}, {row["split"] for row in output})

    def test_order_independent_reproducibility_and_four_split_coverage(self):
        rows = self.load([self.record(str(index)) for index in range(200)])
        output, report = prepare(rows, self.concepts, "seed")
        reordered, other = prepare(list(reversed(rows)), self.concepts, "seed")
        self.assertEqual(output, reordered)
        self.assertEqual(report, other)
        self.assertTrue(all(report["splitGroups"].values()))
        _, changed = prepare(rows, self.concepts, "different-seed")
        self.assertNotEqual(report["datasetSha256"], changed["datasetSha256"])

    def test_source_holdout_and_prior_evaluation_overlap(self):
        rows = self.load([
            self.record("held", source="new-source"),
            *[self.record(str(index)) for index in range(40)],
        ])
        output, _ = prepare(rows, self.concepts, "seed", {"new-source"})
        self.assertEqual({"held"}, {row["id"] for row in output if row["split"] == "test"})
        next(row for row in rows if row["id"] == "held")["priorEvaluation"] = True
        with self.assertRaisesRegex(ValueError, "overlaps prior evaluation"):
            prepare(rows, self.concepts, "seed", {"new-source"})
        with self.assertRaisesRegex(ValueError, "must occur"):
            prepare(rows, self.concepts, "seed", {"missing"})

    def test_holdout_overlap_is_transitive_across_sources(self):
        rows = self.load([
            self.record("a", source="held-source", groups=["event:shared"]),
            self.record("b", groups=["event:shared"], prior=True),
        ])
        with self.assertRaisesRegex(ValueError, "overlaps prior evaluation"):
            prepare(rows, self.concepts, "seed", {"held-source"})

    def test_conflicting_duplicate_annotations_rejected(self):
        rows = self.load([
            self.record("a", content=b"same", labels={"dog": "positive"}),
            self.record("b", content=b"same", labels={"dog": "negative"}),
        ])
        with self.assertRaisesRegex(ValueError, "Contradictory annotations"):
            prepare(rows, self.concepts, "seed")

    def test_transitive_duplicate_identities_must_agree_but_event_photos_may_differ(self):
        a = self.record("a", content=b"duplicate", labels={"dog": "positive"})
        b = self.record("b", content=b"duplicate")
        c = self.record("c", labels={"dog": "negative"})
        c["sourceId"] = b["sourceId"]
        with self.assertRaisesRegex(ValueError, "Contradictory annotations"):
            prepare(self.load([a, b, c]), self.concepts, "seed")
        a["groups"] = c["groups"] = ["event:shared"]
        c["sourceId"] = "c"
        output, report = prepare(self.load([a, c]), self.concepts, "seed")
        self.assertEqual(1, report["independentGroups"])
        self.assertEqual(2, len(output))

    def test_negative_ancestor_conflicts_even_across_duplicate_records(self):
        rows = self.load([
            self.record("a", content=b"same", labels={"duck": "positive"}),
            self.record("b", content=b"same", labels={"animal": "negative"}),
        ])
        with self.assertRaisesRegex(ValueError, "negative ancestor"):
            prepare(rows, self.concepts, "seed")

    def test_invalid_input_fails_explicitly(self):
        valid = self.record("a")
        for field, value in [
            ("labels", {"unreviewed-personal-tag": "positive"}),
            ("labels", {"dog": True}),
            ("labels", {"dog": "positive", "puppy": "negative"}),
            ("groups", []), ("groups", "event:a"),
            ("priorEvaluation", 0), ("provenance", {}),
            ("path", "..\\inventory.jsonl" if sys.platform == "win32" else "../inventory.jsonl"),
            ("path", str((self.images / "a.jpg").resolve())),
        ]:
            with self.subTest(field=field, value=value):
                row = copy.deepcopy(valid)
                row[field] = value
                with self.assertRaises(ValueError):
                    self.load([row])
        with self.assertRaisesRegex(ValueError, "Duplicate record"):
            self.load([valid, valid])
        with self.assertRaisesRegex(ValueError, "empty"):
            self.load([])

    def test_duplicate_json_keys_rejected(self):
        self.input.write_text('{"id": "one", "id": "two"}\n', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate JSON key"):
            load_records(self.input, self.images, self.aliases)

    def test_vocabulary_alias_parent_and_cycle_validation(self):
        source = (HERE / "pilot-vocabulary.csv").read_text(encoding="utf-8")
        for old, new, error in [
            ("kitten", "puppy", "Duplicate vocabulary spelling"),
            ("cat,animals,animal,", "cat,animals,missing,", "Unknown parent"),
            ("animal,animals,,", "animal,animals,duck,", "Hierarchy cycle"),
        ]:
            with self.subTest(error=error):
                path = self.root / "invalid.csv"
                path.write_text(source.replace(old, new), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, error):
                    vocabulary(path)
        for malformed in (source + "short,row\n", source + "a,b,c,d,e,f,extra\n"):
            path = self.root / "malformed.csv"
            path.write_text(malformed, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "exactly the declared columns"):
                vocabulary(path)

    def test_cli_writes_reproducible_outputs_without_changing_sources(self):
        self.load([self.record("a", labels={"dog": "positive"})])
        before = {path: (path.read_bytes(), path.stat().st_mtime_ns)
                  for path in [self.input, *self.images.iterdir()]}
        outputs = []
        for name in ("first", "second"):
            destination = self.root / name
            command = [
                sys.executable, str(HERE / "prepare_dataset.py"),
                "--input", str(self.input), "--images-root", str(self.images),
                "--output", str(destination),
            ]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            outputs.append({path.name: path.read_bytes() for path in destination.iterdir()})
            again = subprocess.run(command, capture_output=True, text=True)
            self.assertNotEqual(0, again.returncode)
            self.assertIn("must not already exist", again.stderr)
        self.assertEqual(outputs[0], outputs[1])
        for path, expected in before.items():
            self.assertEqual(expected, (path.read_bytes(), path.stat().st_mtime_ns))
        result = subprocess.run([
            sys.executable, str(HERE / "prepare_dataset.py"), "--input", str(self.input),
            "--images-root", str(self.images), "--output", str(self.images / "audit"),
        ], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("outside --images-root", result.stderr)
        self.assertFalse((self.images / "audit").exists())


if __name__ == "__main__":
    unittest.main()
