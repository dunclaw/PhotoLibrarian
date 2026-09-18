"""Offline tests: python -m unittest discover -s tools\\PhotoLibrarian.ModelBench -p test_calibrate_scores.py"""

import contextlib
import copy
import csv
from decimal import Decimal
import hashlib
import io
import json
from pathlib import Path
import shutil
import unittest
from uuid import uuid4

import calibrate_scores as cal


def sample(index, score, checked, label="cat", model="tinyclip"):
    return cal.Candidate(f"image-{index}.jpg", model, 1, label, Decimal(str(score)), checked)


def model_metadata(model="tinyclip", kind="cosine", threshold=Decimal(".25")):
    return {"ModelId": model, "Provider": "Cpu", "ScoreKind": kind, "Threshold": threshold,
            "Precision": "int8", "ModelSha256": "a" * 64, "VocabularySha256": "b" * 64}


def result(image="image.jpg", model="tinyclip", predictions=None, threshold=Decimal(".25")):
    if predictions is None:
        predictions = [("cat", Decimal(".8")), ("dog", Decimal(".3")), ("bird", Decimal(".2"))]
    return {"RelativeImagePath": image, "ModelId": model, "Provider": "Cpu",
            "Task": "ZeroShotTagging", "ScoreKind": "cosine", "Threshold": threshold, "Error": None,
            "Predictions": [{"Rank": rank, "Label": label, "Confidence": score,
                             "AboveThreshold": score >= threshold}
                            for rank, (label, score) in enumerate(predictions, 1)]}


class CalibrationTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(__file__).parent / (".calibration-tests-" + uuid4().hex)
        self.root.mkdir()
        self.addCleanup(shutil.rmtree, self.root)
        self.results = self.root / "results.json"
        self.scores = self.root / "scores.csv"
        self.groups = self.root / "groups.csv"
        self.run = {"Models": [model_metadata()], "Results": [result()]}
        self.rows = [["image.jpg", "tinyclip", "4", "1;3", ""]]
        self.save()

    def write_csv(self, path, header, rows):
        with path.open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(header)
            writer.writerows(rows)

    def save(self, header=None):
        self.results.write_text(cal.json_text(self.run), encoding="utf-8-sig")
        self.write_csv(self.scores, cal.REVIEW_FIELDS if header is None else header, self.rows)

    def load(self, groups=False):
        return cal.load_inputs(self.results, self.scores, self.groups if groups else None)

    def test_review_quality_and_candidate_semantics(self):
        data = self.load()
        self.assertEqual([True, False, True], [p.checked for p in data["reviews"][0].candidates])
        report = cal.analyze(data)
        model = report["models"]["tinyclip"]
        self.assertEqual(4, model["rankingQuality"]["averageQualityScore0To5"])
        self.assertEqual(2, model["rankingQuality"]["checkedPositives"])
        baseline = model["baseline"]["evaluation"]
        self.assertEqual((1, 1, 2, 2), tuple(baseline[k] for k in ("tp", "fp", "accepted", "checked")))
        self.assertEqual(.5, baseline["precision"])
        self.assertEqual(.5, baseline["checkedCandidateRetention"])
        self.assertEqual(1, baseline["coverage"])
        self.assertEqual(2, baseline["acceptedPerImage"])
        self.assertFalse(report["productionApproved"])
        self.assertIn("event/near-duplicate/subject leakage", " ".join(report["warnings"]))

    def test_binary_masks_accept_detected_tags_but_reject_zero_score_padding(self):
        self.run["Models"][0]["ScoreKind"] = "binary"
        self.run["Results"][0]["ScoreKind"] = "binary"
        for prediction in self.run["Results"][0]["Predictions"]:
            prediction["Confidence"] = 1
            prediction["AboveThreshold"] = True
        self.save()
        self.assertEqual(3, len(self.load()["reviews"][0].candidates))
        self.run["Results"][0]["Predictions"][0]["Confidence"] = 0
        self.save()
        with self.assertRaisesRegex(ValueError, "never zero-score padding"):
            self.load()

    def test_missing_duplicate_and_unknown_csv_headers(self):
        for header in (cal.REVIEW_FIELDS[:-1], cal.REVIEW_FIELDS + ["Extra"],
                       cal.REVIEW_FIELDS[:-1] + ["Image"], ["image"] + cal.REVIEW_FIELDS[1:]):
            with self.subTest(header=header):
                self.write_csv(self.scores, header, [])
                with self.assertRaisesRegex(ValueError, "fields must be exactly"):
                    self.load()

    def test_malformed_or_missing_csv_row_fields(self):
        for rows in ([self.rows[0][:-1]], [self.rows[0] + ["extra"]]):
            with self.subTest(rows=rows):
                self.write_csv(self.scores, cal.REVIEW_FIELDS, rows)
                with self.assertRaisesRegex(ValueError, "malformed or missing"):
                    self.load()
        self.scores.write_text(",".join(cal.REVIEW_FIELDS) + '\n"unterminated', encoding="utf-8")
        with self.assertRaises(csv.Error):
            self.load()

    def test_malformed_quality_and_checked_ranks(self):
        for quality in ("-1", "6", "2.5", "1.0", "NaN", "Infinity", "false"):
            with self.subTest(quality=quality):
                self.rows[0][2] = quality
                self.save()
                with self.assertRaisesRegex(ValueError, "quality"):
                    self.load()
        self.rows[0][2] = "3"
        for ranks in ("0", "-1", "1.0", "1,2", "1;", "1; 2", "1;1", "99", "NaN"):
            with self.subTest(ranks=ranks):
                self.rows[0][3] = ranks
                self.save()
                with self.assertRaisesRegex(ValueError, "rank"):
                    self.load()

    def test_checked_unrated_is_rejected_but_zero_rating_is_reviewed(self):
        self.rows[0][2] = ""
        self.save()
        with self.assertRaisesRegex(ValueError, "unrated.*ambiguous"):
            self.load()
        self.rows[0][2:4] = ["0", ""]
        self.save()
        data = self.load()
        self.assertTrue(all(p.checked is False for p in data["reviews"][0].candidates))
        self.assertIsNone(cal.analyze(data)["models"]["tinyclip"]["operatingPoints"][0]
                          ["experimentalFullDataFit"]["global"]["threshold"])

    def test_duplicate_and_unknown_review_keys(self):
        for rows in (self.rows * 2, [["unknown.jpg", "tinyclip", "4", "", ""]],
                     [["image.jpg", "unknown", "4", "", ""]], [["", "tinyclip", "4", "", ""]]):
            with self.subTest(rows=rows):
                self.write_csv(self.scores, cal.REVIEW_FIELDS, rows)
                with self.assertRaises(ValueError):
                    self.load()

    def test_missing_unrated_and_unseen_never_supply_negative_evidence(self):
        self.run["Results"] += [result("unrated.jpg", predictions=[("unseen", Decimal(".9"))]),
                                result("missing.jpg", predictions=[("also unseen", Decimal(".8"))]),
                                result("empty.jpg", predictions=[])]
        self.rows += [["unrated.jpg", "tinyclip", "", "", "a note is not a rating"],
                      ["empty.jpg", "tinyclip", "1", "", ""]]
        self.save()
        data = self.load()
        all_candidates = [p for r in data["reviews"] for p in r.candidates]
        policy = cal.fit_policy(all_candidates, Decimal(".6"))
        self.assertEqual({"cat", "dog", "bird"}, set(policy["labels"]))
        self.assertEqual(3, policy["global"]["reviewedAppearances"])
        self.assertEqual(1, policy["global"]["uncheckedCandidates"])
        self.assertEqual(0, cal.label_choice(policy, "unseen")["reviewedAppearances"])
        report = cal.analyze(data)
        baseline = report["models"]["tinyclip"]["baseline"]["evaluation"]
        self.assertEqual(2, baseline["imagesReviewed"])
        self.assertEqual(.5, baseline["coverage"])
        _, evidence, predictions = cal.csv_rows(data, report)
        self.assertTrue(all(row["reviewedAppearances"] == 0 for row in evidence if row["Label"] == "unseen"))
        self.assertTrue(all(row["Checked"] is None for row in predictions if row["Image"] == "missing.jpg"))

    def test_no_rated_rows_is_an_explicit_error(self):
        self.rows = []
        self.save()
        with self.assertRaisesRegex(ValueError, "no explicitly reviewed"):
            self.load()

    def test_explicit_positive_negative_pending_without_quality(self):
        self.run["Results"][0]["Predictions"][2].update(Confidence=Decimal(".99"), AboveThreshold=True)
        self.rows = [["image.jpg", "tinyclip", "", "1", "tag-only review", "1;2"]]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        data = self.load()
        review = data["reviews"][0]
        self.assertIsNone(review.quality)
        self.assertEqual("unrated", review.status)
        self.assertEqual("partial", cal.tag_review_status(review))
        self.assertEqual([True, False, None], [p.checked for p in review.candidates])
        self.assertEqual(2, len(cal.reviewed_candidates(review)))
        report = cal.analyze(data)
        model = report["models"]["tinyclip"]
        quality, baseline = model["rankingQuality"], model["baseline"]["evaluation"]
        self.assertEqual(0, quality["ratedRows"])
        self.assertIsNone(quality["averageQualityScore0To5"])
        self.assertEqual((1, 0, 1, 0, 2, 1), tuple(quality[k] for k in
                         ("reviewedRows", "fullyReviewedRows", "partiallyReviewedRows", "pendingRows",
                          "reviewedCandidates", "pendingCandidates")))
        self.assertEqual((1, 1, 2, 1, 2), tuple(baseline[k] for k in ("tp", "fp", "accepted", "checked", "reviewedCandidates")))
        self.assertEqual(.5, baseline["precision"])
        self.assertEqual(1, baseline["checkedCandidateRetention"])
        self.assertEqual(1, baseline["imagesReviewed"])
        self.assertEqual(1, baseline["coverage"])
        self.assertEqual(2, baseline["acceptedPerImage"])
        full = model["operatingPoints"][0]["experimentalFullDataFit"]
        self.assertEqual({"cat", "dog"}, set(full["labels"]))
        self.assertEqual(1, full["global"]["uncheckedCandidates"])
        sweeps, labels, predictions = cal.csv_rows(data, report)
        self.assertTrue(all(row["reviewedAppearances"] == 0 for row in labels if row["Label"] == "bird"))
        self.assertNotIn(Decimal(".99"), [row["threshold"] for row in sweeps])
        for row in predictions:
            self.assertEqual("partial", row["TagReviewStatus"])
            if row["Rank"] == 3:
                self.assertEqual("pending", row["CandidateReviewStatus"])
                for field in ("Checked", "BaselineAccepted", "GlobalOOFAccepted", "PerLabelOOFAccepted"):
                    self.assertIsNone(row[field])
            else:
                self.assertEqual("reviewed", row["CandidateReviewStatus"])

    def test_explicit_quality_never_implicitly_reviews_candidates(self):
        self.run["Results"] += [result("quality-only.jpg"), result("missing.jpg")]
        self.rows = [["image.jpg", "tinyclip", "4", "1", "", "1"],
                     ["quality-only.jpg", "tinyclip", "5", "", "", ""]]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        data = self.load()
        reviews = {r.image: r for r in data["reviews"]}
        self.assertEqual([True, None, None], [p.checked for p in reviews["image.jpg"].candidates])
        self.assertTrue(all(p.checked is None for p in reviews["quality-only.jpg"].candidates))
        self.assertEqual("rated", reviews["quality-only.jpg"].status)
        self.assertEqual("pending", cal.tag_review_status(reviews["quality-only.jpg"]))
        report = cal.analyze(data)["models"]["tinyclip"]
        quality = report["rankingQuality"]
        self.assertEqual(2, quality["ratedRows"])
        self.assertEqual(4.5, quality["averageQualityScore0To5"])
        self.assertEqual(1, quality["reviewedRows"])
        self.assertEqual(2, quality["pendingRows"])
        self.assertEqual(8, quality["pendingCandidates"])
        self.assertEqual(1, report["baseline"]["evaluation"]["imagesReviewed"])
        self.assertEqual(1, report["baseline"]["evaluation"]["reviewedCandidates"])

    def test_explicit_review_with_zero_positives_is_valid_negative_evidence(self):
        self.rows = [["image.jpg", "tinyclip", "", "", "", "1;2"]]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        data = self.load()
        self.assertEqual([False, False, None], [p.checked for p in data["reviews"][0].candidates])
        model = cal.analyze(data)["models"]["tinyclip"]
        baseline = model["baseline"]["evaluation"]
        self.assertEqual(0, baseline["checked"])
        self.assertEqual(2, baseline["fp"])
        self.assertEqual(0, baseline["precision"])
        self.assertIsNone(baseline["checkedCandidateRetention"])
        for point in model["operatingPoints"]:
            self.assertIsNone(point["experimentalFullDataFit"]["global"]["threshold"])

    def test_explicit_rank_subset_and_validation(self):
        for checked, reviewed in (("1", ""), ("2", "1"), ("99", "1;2;3"), ("1;1", "1;2")):
            with self.subTest(checked=checked, reviewed=reviewed):
                self.rows = [["image.jpg", "tinyclip", "", checked, "", reviewed]]
                self.save(cal.PARTIAL_REVIEW_FIELDS)
                with self.assertRaisesRegex(ValueError, "rank"):
                    self.load()
        for reviewed in ("0", "-1", "1.0", "01", "1,2", "1;", "1; 2", "1;1", "1;99", "NaN"):
            with self.subTest(reviewed=reviewed):
                self.rows = [["image.jpg", "tinyclip", "", "1", "", reviewed]]
                self.save(cal.PARTIAL_REVIEW_FIELDS)
                with self.assertRaisesRegex(ValueError, "reviewed.*rank"):
                    self.load()
        for quality in ("NaN", "6", "1.5"):
            self.rows = [["image.jpg", "tinyclip", quality, "1", "", "1;2"]]
            self.save(cal.PARTIAL_REVIEW_FIELDS)
            with self.subTest(quality=quality), self.assertRaisesRegex(ValueError, "quality"):
                self.load()

    def test_explicit_headers_are_exact_optional_and_complete(self):
        self.assertEqual(["Image", "Model", "QualityScore0To5", "CorrectPredictionRanks", "Notes"], cal.REVIEW_FIELDS)
        self.assertEqual(cal.REVIEW_FIELDS + ["ReviewedPredictionRanks"], cal.PARTIAL_REVIEW_FIELDS)
        for header in (cal.PARTIAL_REVIEW_FIELDS + [cal.REVIEWED_RANKS_FIELD],
                       cal.REVIEW_FIELDS[:-1] + [cal.REVIEWED_RANKS_FIELD],
                       cal.REVIEW_FIELDS + ["reviewedPredictionRanks"],
                       cal.PARTIAL_REVIEW_FIELDS + ["Extra"]):
            with self.subTest(header=header):
                self.write_csv(self.scores, header, [])
                with self.assertRaisesRegex(ValueError, "fields must be exactly"):
                    self.load()
        for row in (["image.jpg", "tinyclip", "", "1", ""],
                    ["image.jpg", "tinyclip", "", "1", "", "1;2", "extra"]):
            self.write_csv(self.scores, cal.PARTIAL_REVIEW_FIELDS, [row])
            with self.assertRaisesRegex(ValueError, "malformed or missing"):
                self.load()
        row = ["image.jpg", "tinyclip", "", "1", "", "1;2"]
        self.write_csv(self.scores, list(reversed(cal.PARTIAL_REVIEW_FIELDS)), [list(reversed(row))])
        self.assertEqual([True, False, None], [p.checked for p in self.load()["reviews"][0].candidates])

    def test_prediction_only_load_is_validated_and_never_opens_scores(self):
        self.scores.unlink()
        data = cal.load_inputs(self.results)
        self.assertEqual({"results"}, set(data["provenance"]))
        self.assertEqual("missing", data["reviews"][0].status)
        self.assertEqual("pending", cal.tag_review_status(data["reviews"][0]))
        self.assertIsNone(data["reviews"][0].quality)
        self.assertTrue(all(p.checked is None for p in data["reviews"][0].candidates))
        with self.assertRaisesRegex(ValueError, "no explicitly reviewed"):
            cal.analyze(data)
        self.write_csv(self.groups, ["Image", "Group"], [["image.jpg", "event"]])
        grouped = cal.load_inputs(self.results, groups_path=self.groups)
        self.assertEqual({"results", "groups"}, set(grouped["provenance"]))
        self.assertEqual(cal.digest_fold("event"), grouped["folds"]["image.jpg"])
        self.write_csv(self.groups, ["Image", "Group"], [["unknown.jpg", "event"]])
        with self.assertRaises(ValueError):
            cal.load_inputs(self.results, groups_path=self.groups)
        self.run["Results"][0]["Predictions"][0]["Rank"] = 0
        self.results.write_text(cal.json_text(self.run), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "rank"):
            cal.load_inputs(self.results)

    def test_wholly_pending_explicit_scores_are_rejected_even_with_quality(self):
        for quality in ("", "4"):
            self.rows = [["image.jpg", "tinyclip", quality, "", "", ""]]
            self.save(cal.PARTIAL_REVIEW_FIELDS)
            with self.subTest(quality=quality), self.assertRaisesRegex(ValueError, "no explicitly reviewed"):
                self.load()
        args = ["--results", str(self.results), "--scores", str(self.scores),
                "--output", str(self.root / "pending")]
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            cal.main(args)
        self.assertEqual(2, error.exception.code)
        self.assertFalse((self.root / "pending").exists())
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            cal.main(["--results", str(self.results), "--output", str(self.root / "prediction-only")])
        self.assertEqual(2, error.exception.code)

    def test_explicit_export_load_preserves_quality_notes_and_pending_tags(self):
        self.run["Results"] += [result("empty.jpg", predictions=[]), result("pending.jpg")]
        self.rows = [["image.jpg", "tinyclip", "", "1", 'kept, "quoted"\nnotes', "2;1"],
                     ["empty.jpg", "tinyclip", "2", "", "", ""],
                     ["pending.jpg", "tinyclip", "5", "", "no tag review", ""]]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        before = self.load()
        report = cal.analyze(before)
        exported = [cal.review_to_csv_row(r) for r in before["reviews"]]
        self.assertEqual("1;2", next(row for row in exported if row["Image"] == "image.jpg")[cal.REVIEWED_RANKS_FIELD])
        self.write_csv(self.scores, cal.PARTIAL_REVIEW_FIELDS,
                       [[row[field] for field in cal.PARTIAL_REVIEW_FIELDS] for row in exported])
        after = self.load()
        self.assertEqual(before["reviews"], after["reviews"])
        self.assertEqual(report["models"], cal.analyze(after)["models"])
        self.assertEqual(2, report["models"]["tinyclip"]["baseline"]["evaluation"]["imagesReviewed"])
        output = self.root / "explicit"
        cal.write_outputs(output, after, report)
        with (output / "predictions-calibrated.csv").open(encoding="utf-8", newline="") as stream:
            predictions = list(csv.DictReader(stream))
        for row in predictions:
            if row["CandidateReviewStatus"] == "pending":
                self.assertEqual("", row["Checked"])
                self.assertTrue(all(row[field] == "" for field in ("BaselineAccepted", "GlobalOOFAccepted", "PerLabelOOFAccepted")))
        page = (output / "calibration.html").read_text(encoding="utf-8")
        self.assertIn("independently of quality ratings", page)
        self.assertIn("pending candidates", page)

    def test_explicit_conflicts_ignore_quality_and_pending_tags(self):
        self.run["Models"] += [model_metadata("other"), model_metadata("pending")]
        self.run["Results"] += [result(model="other"), result(model="pending")]
        self.rows = [["image.jpg", "tinyclip", "", "1", "", "1"],
                     ["image.jpg", "other", "", "", "", "1"],
                     ["image.jpg", "pending", "5", "", "", ""]]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        data = self.load()
        summary, rows = cal.annotation_conflicts(data)
        self.assertEqual((1, 1, 3, 2), tuple(summary[k] for k in
                         ("sharedReviewedPairs", "conflictingPairs", "exportedAnnotations", "reviewedConflictingAnnotations")))
        self.assertEqual([False, None, True], [row["checked"] for row in rows])
        self.assertTrue(all(row["label"] == "cat" for row in rows))
        self.assertEqual(1, cal.analyze(data)["annotationConflicts"]["conflictingPairs"])

    def test_annotation_conflicts_export_original_marks_without_changing_metrics(self):
        self.run["Models"].append(model_metadata("other"))
        self.run["Results"].append(result(model="other"))
        self.rows.append(["image.jpg", "other", "3", "3", ""])
        self.save()
        data = self.load()
        original = copy.deepcopy(data)
        report = cal.analyze(data)
        counts, rows = cal.annotation_conflicts(data)
        self.assertEqual(3, counts["sharedDisplayedPairs"])
        self.assertEqual(3, counts["sharedReviewedPairs"])
        self.assertEqual(1, counts["conflictingPairs"])
        self.assertEqual(2, counts["exportedAnnotations"])
        self.assertEqual(2, counts["reviewedConflictingAnnotations"])
        self.assertEqual([{"image": "image.jpg", "label": "cat", "model": "other", "checked": False},
                          {"image": "image.jpg", "label": "cat", "model": "tinyclip", "checked": True}], rows)
        self.assertEqual(counts, report["annotationConflicts"])
        self.assertIn("1 of 3 image/exact-label pairs", " ".join(report["warnings"]))
        self.assertEqual(original, data)
        for model in ("other", "tinyclip"):
            expected = cal.metrics([r for r in original["reviews"] if r.model == model], lambda p: Decimal(".25"))
            self.assertEqual(expected, report["models"][model]["baseline"]["evaluation"])
        self.assertEqual(1, report["models"]["other"]["rankingQuality"]["checkedPositives"])
        self.assertEqual(2, report["models"]["tinyclip"]["rankingQuality"]["checkedPositives"])
        output = self.root / "conflicts"
        cal.write_outputs(output, data, report)
        with (output / "annotation-conflicts.csv").open(encoding="utf-8", newline="") as stream:
            reader = csv.DictReader(stream)
            self.assertEqual(["image", "label", "model", "checked"], reader.fieldnames)
            exported = list(reader)
        self.assertEqual(["False", "True"], [row["checked"] for row in exported])
        self.assertIn("1 conflicting pairs", (output / "calibration.html").read_text(encoding="utf-8"))
        data["reviews"].reverse()
        self.assertEqual((counts, rows), cal.annotation_conflicts(data))

    def test_conflicts_match_exact_image_label_and_do_not_turn_unreviewed_into_negatives(self):
        self.run["Models"] += [model_metadata(name) for name in ("other", "missing", "unrated")]
        self.run["Results"] += [
            result(model="other", predictions=[("cat", Decimal(".8")), ("Cat", Decimal(".8")),
                                                ("bird", Decimal(".8"))]),
            result(model="missing"), result(model="unrated"),
            result("different.jpg", predictions=[("bird", Decimal(".8"))]),
        ]
        self.rows += [["image.jpg", "other", "4", "2;3", ""],
                      ["image.jpg", "unrated", "", "", ""],
                      ["different.jpg", "tinyclip", "0", "", ""]]
        self.save()
        data = self.load()
        counts, rows = cal.annotation_conflicts(data)
        self.assertEqual(3, counts["sharedDisplayedPairs"])
        self.assertEqual(2, counts["sharedReviewedPairs"])
        self.assertEqual(1, counts["conflictingPairs"])
        self.assertEqual(4, counts["exportedAnnotations"])
        self.assertEqual(2, counts["reviewedConflictingAnnotations"])
        self.assertTrue(all(row["image"] == "image.jpg" and row["label"] == "cat" for row in rows))
        self.assertEqual([None, False, True, None], [row["checked"] for row in rows])
        output = self.root / "partial"
        report = cal.analyze(data)
        cal.write_outputs(output, data, report)
        with (output / "annotation-conflicts.csv").open(encoding="utf-8", newline="") as stream:
            exported = list(csv.DictReader(stream))
        self.assertEqual(["", "False", "True", ""], [row["checked"] for row in exported])
        self.assertIsNone(report["models"]["missing"]["baseline"]["evaluation"]["precision"])
        self.assertEqual(0, report["models"]["unrated"]["rankingQuality"]["checkedPositives"])

    def test_wholly_unreviewed_model_has_no_fit_or_evaluation_evidence(self):
        self.run["Models"].append(model_metadata("unreviewed"))
        self.run["Results"].append(result(model="unreviewed"))
        self.save()
        report = cal.analyze(self.load())
        model = report["models"]["unreviewed"]
        self.assertEqual(0, model["rankingQuality"]["ratedRows"])
        self.assertEqual(0, model["rankingQuality"]["checkedPositives"])
        self.assertIsNone(model["rankingQuality"]["averageQualityScore0To5"])
        for point in model["operatingPoints"]:
            self.assertEqual({}, point["experimentalFullDataFit"]["labels"])
            self.assertIsNone(point["experimentalFullDataFit"]["global"]["threshold"])
            for metrics in point["outOfFold"].values():
                self.assertEqual(0, metrics["reviewedCandidates"])
                self.assertIsNone(metrics["precision"])
                self.assertIsNone(metrics["coverage"])

    def test_duplicate_json_keys_and_invalid_result_identity(self):
        raw = self.results.read_text(encoding="utf-8-sig")
        self.results.write_text(raw.replace('"Rank":1', '"Rank":1,"Rank":1', 1), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "duplicate key"):
            self.load()
        original = copy.deepcopy(self.run)
        for action in ("model", "result", "unknown", "rank", "label", "missing"):
            with self.subTest(action=action):
                self.run = copy.deepcopy(original)
                entry = self.run["Results"][0]
                if action == "model":
                    self.run["Models"].append(copy.deepcopy(self.run["Models"][0]))
                elif action == "result":
                    self.run["Results"].append(copy.deepcopy(entry))
                elif action == "unknown":
                    entry["ModelId"] = "unknown"
                elif action in ("rank", "label"):
                    key = action.title()
                    entry["Predictions"][1][key] = entry["Predictions"][0][key]
                else:
                    del entry["Threshold"]
                self.save()
                with self.assertRaises(ValueError):
                    self.load()

    def test_failed_inconsistent_detection_and_unsupported_results(self):
        original = copy.deepcopy(self.run)
        mutations = [
            ("result", "Error", "inference failed"), ("model", "Error", "load failed"),
            ("model", "FailedImages", 1), ("model", "SuccessfulImages", 2),
            ("result", "Threshold", Decimal(".26")), ("result", "Provider", "DirectML"),
            ("model", "Provider", "DirectML"), ("result", "ScoreKind", "confidence"),
            ("model", "ScoreKind", "logit"), ("result", "Task", "ObjectDetection"),
            ("result", "Task", []), ("model", "Task", "ObjectDetection"),
            ("model", "Task", "MultiLabelTagging"), ("model", "ModelSha256", "bad hash"),
        ]
        for location, key, value in mutations:
            with self.subTest(location=location, key=key, value=value):
                self.run = copy.deepcopy(original)
                self.run["Results" if location == "result" else "Models"][0][key] = value
                self.save()
                with self.assertRaises(ValueError):
                    self.load()
        for key, value in (("X", 0), ("AboveThreshold", False), ("AboveThreshold", 1),
                           ("Rank", 0), ("Rank", True), ("Label", "")):
            with self.subTest(key=key, value=value):
                self.run = copy.deepcopy(original)
                self.run["Results"][0]["Predictions"][0][key] = value
                self.save()
                with self.assertRaises(ValueError):
                    self.load()

    def test_malformed_and_nonfinite_scores(self):
        for value in (None, True, ".8", Decimal("1.01"), Decimal("-1.01")):
            with self.subTest(value=value):
                self.run["Results"][0]["Predictions"][0]["Confidence"] = value
                self.save()
                with self.assertRaises(ValueError):
                    self.load()
        for value in ("NaN", "Infinity", "-Infinity", "1e999"):
            self.run["Results"][0]["Predictions"][0]["Confidence"] = Decimal(".8")
            self.save()
            raw = self.results.read_text(encoding="utf-8-sig")
            self.results.write_text(raw.replace('"Confidence":0.8', '"Confidence":' + value), encoding="utf-8")
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.load()
        for kind in ("sigmoid", "confidence"):
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                cal.number(Decimal("-.01"), kind, "test")
        self.assertEqual(Decimal("-1"), cal.number(-1, "cosine", "test"))

    def test_ties_minimum_accepted_and_impossible_targets(self):
        tied = [sample(i, ".8", i < 3) for i in range(6)]
        self.assertEqual(1, len(cal.threshold_sweep(tied)))
        self.assertIsNone(cal.fit_threshold(tied, Decimal(".6"))["threshold"])
        self.assertEqual(Decimal(".8"), cal.fit_threshold(tied, Decimal(".5"))["threshold"])
        for candidates in ([], [sample(i, ".9", False) for i in range(10)],
                           [sample(i, ".9", True) for i in range(4)]):
            with self.subTest(candidates=candidates):
                fit = cal.fit_threshold(candidates, Decimal(".6"))
                self.assertIsNone(fit["threshold"])
                self.assertEqual("abstain_no_eligible_threshold", fit["status"])

    def test_maximize_retention_then_precision(self):
        candidates = ([sample(i, ".9", True) for i in range(5)]
                      + [sample(i + 5, ".8", False) for i in range(3)]
                      + [sample(i + 8, ".7", True) for i in range(2)])
        self.assertEqual(Decimal(".7"), cal.fit_threshold(candidates, Decimal(".6"))["threshold"])
        self.assertEqual(Decimal(".9"), cal.fit_threshold(candidates[:8], Decimal(".6"))["threshold"])
        self.assertEqual(Decimal(".9"), cal.fit_threshold(candidates, Decimal(".9"))["threshold"])

    def test_label_support_fallback_and_supported_abstention(self):
        candidates = [sample(i, ".8" if i < 5 else ".1", i < 5, "supported") for i in range(10)]
        candidates += [sample(i + 20, ".8", i < 5, "tied-supported") for i in range(10)]
        candidates += [sample(i + 40, ".99", True, "nine-appearances") for i in range(9)]
        candidates += [sample(i + 60, ".9" if i < 4 else ".1", i < 4, "four-positives") for i in range(10)]
        policy = cal.fit_policy(candidates, Decimal(".9"))
        self.assertEqual(Decimal(".8"), policy["labels"]["supported"]["threshold"])
        self.assertEqual("fitted", policy["labels"]["supported"]["status"])
        self.assertIsNone(policy["labels"]["tied-supported"]["threshold"])
        for label in ("nine-appearances", "four-positives"):
            self.assertEqual("global_fallback_insufficient_evidence", policy["labels"][label]["status"])
            self.assertEqual(policy["global"]["threshold"], policy["labels"][label]["threshold"])
        unseen = cal.label_choice(policy, "never reviewed")
        self.assertEqual(policy["global"]["threshold"], unseen["threshold"])
        self.assertEqual("global_fallback_unseen_label", unseen["status"])
        unsupported = cal.fit_policy([sample(0, ".9", True)], Decimal(".9"))
        self.assertIsNone(unsupported["labels"]["cat"]["threshold"])
        self.assertEqual("global_fallback_insufficient_evidence", unsupported["labels"]["cat"]["status"])

    def grouped_data(self):
        groups = {}
        for i in range(1000):
            group = f"event-{i}"
            groups.setdefault(cal.digest_fold(group), group)
            if len(groups) == cal.FOLDS:
                break
        self.run = {"Models": [model_metadata("a"), model_metadata("b")], "Results": []}
        self.rows, mappings = [], []
        for fold, group in sorted(groups.items()):
            for i in range(5):
                image = f"fold-{fold}-{i}.jpg"
                mappings.append([image, group])
                for model in ("a", "b"):
                    label = "heldout-only" if fold == 0 else "training"
                    self.run["Results"].append(result(image, model, [(label, Decimal(".8"))]))
                    self.rows.append([image, model, "4", "1", ""])
        self.save()
        self.write_csv(self.groups, ["Image", "Group"], mappings)
        return self.load(groups=True)

    def test_deterministic_grouped_oof_and_heldout_label_exclusion(self):
        data = self.grouped_data()
        report = cal.analyze(data)
        self.assertEqual(report, cal.analyze(data))
        for review in data["reviews"]:
            self.assertEqual(int(review.image[5]), data["folds"][review.image])
        for model in ("a", "b"):
            point = report["models"][model]["operatingPoints"][0]
            fit = point["foldFits"][0]
            self.assertEqual(20, fit["trainingImages"])
            self.assertEqual(20, fit["policy"]["global"]["reviewedAppearances"])
            self.assertNotIn("heldout-only", fit["policy"]["labels"])
            self.assertEqual("global_fallback_unseen_label", cal.label_choice(fit["policy"], "heldout-only")["status"])
            self.assertEqual(25, point["outOfFold"]["perLabel"]["tp"])
            self.assertEqual(1, point["outOfFold"]["global"]["coverage"])
            self.assertIn("resubstitutionOnly", point)
        for row in self.rows:
            if row[0].startswith("fold-0-"):
                row[3] = ""
        self.rows.reverse()
        self.run["Results"].reverse()
        self.run["Models"].reverse()
        self.save()
        changed = cal.analyze(self.load(groups=True))
        self.assertEqual(["a", "b"], list(changed["models"]))
        self.assertEqual(report["validation"], changed["validation"])
        for model in ("a", "b"):
            before = report["models"][model]["operatingPoints"][0]
            after = changed["models"][model]["operatingPoints"][0]
            self.assertEqual(before["foldFits"][0], after["foldFits"][0])
            self.assertNotEqual(before["experimentalFullDataFit"], after["experimentalFullDataFit"])
            self.assertEqual(20, after["outOfFold"]["global"]["checked"])

    def test_explicit_grouped_oof_uses_only_reviewed_tags_without_quality(self):
        self.grouped_data()
        for entry in self.run["Results"]:
            entry["Predictions"] += [
                {"Rank": 2, "Label": "rejected", "Confidence": Decimal(".1"), "AboveThreshold": False},
                {"Rank": 3, "Label": "new-unreviewed", "Confidence": Decimal("1"), "AboveThreshold": True}]
        self.rows = [[row[0], row[1], "", "1", "", "1;2"] for row in self.rows]
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        data = self.load(groups=True)
        report = cal.analyze(data)
        for model in ("a", "b"):
            quality = report["models"][model]["rankingQuality"]
            self.assertEqual((0, 25, 25, 50, 25), tuple(quality[k] for k in
                             ("ratedRows", "reviewedRows", "partiallyReviewedRows", "reviewedCandidates", "pendingCandidates")))
            self.assertIsNone(quality["averageQualityScore0To5"])
            for point in report["models"][model]["operatingPoints"]:
                fit = point["foldFits"][0]
                self.assertEqual(20, fit["trainingImages"])
                self.assertEqual(40, fit["policy"]["global"]["reviewedAppearances"])
                self.assertEqual(20, fit["policy"]["global"]["uncheckedCandidates"])
                self.assertNotIn("heldout-only", fit["policy"]["labels"])
                self.assertNotIn("new-unreviewed", point["experimentalFullDataFit"]["labels"])
                for metrics in point["outOfFold"].values():
                    self.assertEqual((25, 0, 25, 25, 50, 25), tuple(metrics[k] for k in
                                     ("tp", "fp", "accepted", "checked", "reviewedCandidates", "imagesReviewed")))
                    self.assertEqual(1, metrics["precision"])
                    self.assertEqual(1, metrics["coverage"])
        for entry in self.run["Results"]:
            if entry["RelativeImagePath"].startswith("fold-0-"):
                entry["Predictions"][0]["Confidence"] = Decimal(".99")
                entry["Predictions"][2]["Label"] = "another-pending-label"
        self.save(cal.PARTIAL_REVIEW_FIELDS)
        changed = cal.analyze(self.load(groups=True))
        for model in ("a", "b"):
            before = report["models"][model]["operatingPoints"][0]
            after = changed["models"][model]["operatingPoints"][0]
            self.assertEqual(before["foldFits"][0], after["foldFits"][0])
            self.assertNotEqual(before["experimentalFullDataFit"], after["experimentalFullDataFit"])

    def test_complete_legacy_and_explicit_evaluation_fields_are_byte_identical(self):
        self.grouped_data()
        for index, row in enumerate(self.rows):
            if index % 4 == 0:
                row[3] = ""
        self.save()
        legacy = cal.analyze(self.load(groups=True))
        self.rows = [row + ["1"] for row in self.rows]
        self.save(cal.REVIEW_FIELDS + ["ReviewedPredictionRanks"])
        explicit = cal.analyze(self.load(groups=True))
        self.assertEqual(cal.json_text(legacy["models"]).encode(), cal.json_text(explicit["models"]).encode())

    def test_invalid_group_mappings(self):
        for rows in ([], [["unknown.jpg", "event"]], [["image.jpg", "event"], ["image.jpg", "event"]],
                     [["image.jpg", ""]]):
            with self.subTest(rows=rows):
                self.write_csv(self.groups, ["Image", "Group"], rows)
                with self.assertRaises(ValueError):
                    self.load(groups=True)
        self.write_csv(self.groups, ["Image", "Group", "Extra"], [])
        with self.assertRaises(ValueError):
            self.load(groups=True)

    def test_exact_threshold_inclusivity_and_decimal_serialization(self):
        score = Decimal("0.25000000000000000000000000000000001")
        lower = Decimal("0.25000000000000000000000000000000000")
        candidates = [sample(i, score, True) for i in range(5)] + [sample(5, lower, False)]
        fit = cal.fit_threshold(candidates, Decimal(".9"))
        self.assertEqual(score, fit["threshold"])
        self.assertTrue(cal.accepts(candidates[0], fit["threshold"]))
        self.assertFalse(cal.accepts(candidates[-1], fit["threshold"]))
        self.assertEqual(score, json.loads(cal.json_text(fit), parse_float=Decimal)["threshold"])
        self.run["Results"][0]["Predictions"][0]["Confidence"] = score
        self.save()
        self.assertEqual(score, self.load()["reviews"][0].candidates[0].score)

    def test_outputs_are_reproducible_escaped_and_preserve_sources(self):
        data = self.grouped_data()
        data["models"]["a"]["modelId"] = "<script>alert(1)</script>"
        report = cal.analyze(data)
        report["warnings"].append("<script>alert('not executable')</script>")
        original = {path: path.read_bytes() for path in (self.results, self.scores, self.groups)}
        first, second = self.root / "out", self.root / "other"
        cal.write_outputs(first, data, report)
        cal.write_outputs(second, data, report)
        expected = {"calibration.json", "calibration.html", "threshold-sweep.csv",
                    "label-evidence.csv", "predictions-calibrated.csv", "annotation-conflicts.csv"}
        self.assertEqual(expected, {path.name for path in first.iterdir()})
        for name in expected:
            self.assertEqual((first / name).read_bytes(), (second / name).read_bytes())
        page = (first / "calibration.html").read_text(encoding="utf-8")
        self.assertNotIn("<script>", page)
        self.assertIn("&lt;script&gt;", page)
        self.assertNotIn("<img", page)
        self.assertIn("default-src 'none'", page)
        self.assertIn("OOF", page)
        saved = json.loads((first / "calibration.json").read_text(encoding="utf-8"))
        self.assertEqual(hashlib.sha256(original[self.results]).hexdigest(), saved["provenance"]["results"]["sha256"])
        self.assertEqual("a" * 64, saved["models"]["a"]["metadata"]["modelSha256"])
        self.assertEqual(0, saved["annotationConflicts"]["conflictingPairs"])
        self.assertEqual(25, saved["annotationConflicts"]["sharedReviewedPairs"])
        with (first / "predictions-calibrated.csv").open(encoding="utf-8", newline="") as stream:
            predictions = list(csv.DictReader(stream))
        self.assertEqual(150, len(predictions))
        self.assertTrue(all(p["GlobalOOFAccepted"] == "True" for p in predictions))
        snapshots = {path: path.read_bytes() for path in first.iterdir()}
        with self.assertRaisesRegex(ValueError, "already exists"):
            cal.write_outputs(first, data, report)
        self.assertEqual(snapshots, {path: path.read_bytes() for path in first.iterdir()})
        self.assertEqual(original, {path: path.read_bytes() for path in original})
        with self.assertRaisesRegex(ValueError, "already exists"):
            cal.write_outputs(self.results, data, report)

    def test_empty_candidates_still_have_stable_schemas_and_nullable_metrics(self):
        self.run["Results"][0]["Predictions"] = []
        self.rows[0][3] = ""
        self.save()
        data = self.load()
        report = cal.analyze(data)
        metrics = report["models"]["tinyclip"]["baseline"]["evaluation"]
        self.assertEqual(1, metrics["imagesReviewed"])
        self.assertEqual(0, metrics["coverage"])
        self.assertIsNone(metrics["precision"])
        self.assertIsNone(metrics["checkedCandidateRetention"])
        cal.write_outputs(self.root / "out", data, report)
        for filename, required in (("predictions-calibrated.csv", "PerLabelOOFAccepted"),
                                   ("threshold-sweep.csv", "eligibleAt0.90"),
                                   ("label-evidence.csv", "reviewedAppearances"),
                                   ("annotation-conflicts.csv", "checked")):
            with (self.root / "out" / filename).open(encoding="utf-8", newline="") as stream:
                reader = csv.DictReader(stream)
                self.assertIn(required, reader.fieldnames)
                self.assertEqual([], list(reader))

    def test_cli_and_existing_output_failure_leave_originals_intact(self):
        args = ["--results", str(self.results), "--scores", str(self.scores),
                "--output", str(self.root / "cli")]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(0, cal.main(args))
            with self.assertRaises(SystemExit) as error:
                cal.main(args)
        self.assertEqual(2, error.exception.code)
        self.rows[0][2] = "bad"
        self.save()
        args[-1] = str(self.root / "invalid")
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
            cal.main(args)
        self.assertEqual(2, error.exception.code)
        self.assertFalse((self.root / "invalid").exists())


if __name__ == "__main__":
    unittest.main()
