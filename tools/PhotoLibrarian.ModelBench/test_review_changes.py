"""Offline tests; set MODELBENCH_BROWSER_TESTS=1 for Playwright + installed Edge."""

import base64
import csv
from decimal import Decimal
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest

import calibrate_scores as cal
import review_changes as changes
from test_calibrate_scores import model_metadata, result


PIXEL = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j5WQAAAAASUVORK5CYII="
)


class ReviewFixture(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.prior = self.root / "prior" / "results.json"
        self.target = self.root / "target" / "results.json"
        self.scores = self.prior.with_name("scores.csv")
        self.output = self.root / "delta"
        self.old = {
            "Models": [model_metadata("one"), model_metadata("two")],
            "Results": [self.prediction("one", ["cat", "dog", "bird", "old-only"]),
                        self.prediction("two", ["cat", "dog", "bird", "old-only"])],
        }
        self.new = {
            "Models": [model_metadata("one"), model_metadata("two")],
            "Results": [self.prediction("one", ["dog", "cat", "bird", "new"]),
                        self.prediction("two", ["new", "cat", "dog", "bird"])],
        }
        self.score_rows = [["image.jpg", "one", "4", "1;3", ""],
                           ["image.jpg", "two", "4", "1", ""]]
        self.save()

    def prediction(self, model, labels, image="image.jpg", root=r"C:\photos", image_hash=None):
        scores = [(.8 - index * .1) for index in range(len(labels))]
        entry = result(image=image, model=model, predictions=list(zip(labels, scores)))
        entry["ImagePath"] = root + "\\" + image
        if image_hash is not None:
            entry["ImageSha256"] = image_hash
        return entry

    def save(self):
        for path, run in ((self.prior, self.old), (self.target, self.new)):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(cal.json_text(run), encoding="utf-8-sig")
            for entry in run["Results"]:
                thumb = changes.thumbnail_path(path, entry["ImagePath"])
                thumb.parent.mkdir(exist_ok=True)
                thumb.write_bytes(PIXEL)
        self.write_scores(self.scores, self.score_rows)

    def write_scores(self, path, rows, explicit=False):
        with path.open("w", encoding="utf-8-sig", newline="") as stream:
            writer = csv.writer(stream)
            writer.writerow(changes.EXPORT_FIELDS if explicit else cal.REVIEW_FIELDS)
            writer.writerows(rows)

    def build(self, pairs=None):
        return changes.build_review(self.target, pairs or [(self.prior, self.scores)])

    def generate(self):
        data, thumbnails, raw = self.build()
        changes.write_report(self.output, data, thumbnails, raw)
        return data

    def add_reviewed_photo(self):
        for run in (self.old, self.new):
            run["Results"].append(self.prediction("one", ["cat", "dog"], image="reviewed.jpg"))
        self.score_rows.append(["reviewed.jpg", "one", "5", "1", ""])
        self.save()


class IncrementalReviewTests(ReviewFixture):
    def test_explicit_spelling_corrections_reuse_judgments_without_implicit_aliases(self):
        for entry in self.new["Results"]:
            for prediction in entry["Predictions"]:
                if prediction["Label"] == "cat":
                    prediction["Label"] = "corrected cat"
        self.save()
        ordinary, _, _ = self.build()
        self.assertIsNone(next(item for item in ordinary["items"]
                               if item["label"] == "corrected cat")["initialDecision"])
        revised, _, _ = changes.build_review(self.target, [(self.prior, self.scores)],
                                             {"cat": "corrected cat"})
        self.assertEqual("correct", next(item for item in revised["items"]
                                         if item["label"] == "corrected cat")["initialDecision"])
        self.assertEqual({"cat": "corrected cat"}, revised["provenance"]["labelAliases"])
        self.assertNotEqual(ordinary["fingerprint"], revised["fingerprint"])

    def test_alias_chains_and_nonobject_aliases_fail_explicitly(self):
        for aliases in ([], {"cat": "cat"}, {"cat": "dog", "dog": "bird"},
                        {"cat": "dog", "dog": "cat"}, {"cat": ""}, {"cat": 1}):
            with self.subTest(aliases=aliases), self.assertRaises(ValueError):
                changes.build_review(self.target, [(self.prior, self.scores)], aliases)

    def test_conflicting_spelling_variants_remain_pending(self):
        for entry in self.old["Results"]:
            entry["Predictions"][0]["Label"] = "autumn leave"
            entry["Predictions"][1]["Label"] = "autumn leaf"
        for entry in self.new["Results"]:
            entry["Predictions"][1]["Label"] = "autumn leaves"
        self.save()
        revised, _, _ = changes.build_review(self.target, [(self.prior, self.scores)],
                                             {"autumn leave": "autumn leaves",
                                              "autumn leaf": "autumn leaves"})
        item = next(item for item in revised["items"] if item["label"] == "autumn leaves")
        self.assertIsNone(item["initialDecision"])
        self.assertEqual("conflict", item["reason"])

    def test_reuses_positive_and_negative_across_models_and_ranks(self):
        data, _, _ = self.build()
        labels = {item["label"]: item for item in data["items"]}
        self.assertEqual("correct", labels["cat"]["initialDecision"])
        self.assertEqual("incorrect", labels["dog"]["initialDecision"])
        self.assertEqual("conflict", labels["bird"]["reason"])
        self.assertIsNone(labels["bird"]["initialDecision"])
        self.assertEqual("new", labels["new"]["reason"])
        self.assertEqual(4, data["summary"]["uniquePhotoTags"])
        self.assertEqual(4, data["summary"]["reusedModelPredictions"])
        self.assertEqual(2, data["summary"]["pendingUniqueTags"])
        self.assertEqual(2, len(labels["new"]["occurrences"]))
        self.assertEqual(0, data["summary"]["overallQualityRatingsCopied"])

    def test_different_photo_paths_and_hashes_do_not_reuse(self):
        for path, image_hash in ((r"D:\different\image.jpg", None), (r"C:\photos\image.jpg", "a" * 64)):
            with self.subTest(path=path, image_hash=image_hash):
                for entry in self.new["Results"]:
                    entry["ImagePath"] = path
                    if image_hash:
                        entry["ImageSha256"] = image_hash
                self.save()
                data, _, _ = self.build()
                self.assertEqual(0, data["summary"]["reusedUniqueDecisions"])

    def test_windows_path_casing_matches_but_labels_are_exact(self):
        for entry in self.new["Results"]:
            entry["ImagePath"] = r"c:\PHOTOS\image.jpg"
            for tag in entry["Predictions"]:
                if tag["Label"] == "cat":
                    tag["Label"] = "Cat"
        self.save()
        data, _, _ = self.build()
        labels = {item["label"]: item for item in data["items"]}
        self.assertEqual("new", labels["Cat"]["reason"])
        self.assertEqual("incorrect", labels["dog"]["initialDecision"])

    def test_matching_content_hashes_match_and_changed_hashes_do_not(self):
        for run in (self.old, self.new):
            for entry in run["Results"]:
                entry["ImageSha256"] = "a" * 64
        self.save()
        self.assertEqual(2, self.build()[0]["summary"]["reusedUniqueDecisions"])
        for entry in self.new["Results"]:
            entry["ImageSha256"] = "b" * 64
        self.save()
        self.assertEqual(0, self.build()[0]["summary"]["reusedUniqueDecisions"])

    def test_newer_explicit_decisions_resolve_older_conflicts(self):
        latest = self.root / "latest.csv"
        self.write_scores(latest, [["image.jpg", "one", "", "3", "", "3"]], explicit=True)
        data, _, _ = self.build([(self.prior, self.scores), (self.prior, latest)])
        labels = {item["label"]: item for item in data["items"]}
        self.assertEqual("correct", labels["bird"]["initialDecision"])
        self.assertEqual("correct", labels["cat"]["initialDecision"])
        self.assertEqual("incorrect", labels["dog"]["initialDecision"])

    def test_legacy_unrated_candidates_never_become_rejections(self):
        self.old["Results"][1]["RelativeImagePath"] = "other.jpg"
        self.old["Results"][1]["ImagePath"] = r"C:\photos\other.jpg"
        self.score_rows = [["image.jpg", "one", "", "", ""],
                           ["other.jpg", "two", "4", "1", ""]]
        self.save()
        self.assertEqual(0, self.build()[0]["summary"]["reusedUniqueDecisions"])

    def test_prefilled_export_roundtrips_without_inventing_quality_or_negatives(self):
        data = self.generate()
        loaded = cal.load_inputs(self.output / "results.json", self.output / "human-scores.csv")
        for review in loaded["reviews"]:
            checked = {p.label: p.checked for p in review.candidates}
            self.assertEqual({"cat": True, "dog": False, "bird": None, "new": None}, checked)
            self.assertIsNone(review.quality)
        report = cal.analyze(loaded)
        for model in report["models"].values():
            self.assertEqual(2, model["baseline"]["evaluation"]["reviewedCandidates"])
            self.assertEqual(1, model["rankingQuality"]["checkedPositives"])
            self.assertIsNone(model["rankingQuality"]["averageQualityScore0To5"])
        self.assertEqual(data, json.loads(
            (self.output / "review-items.json").read_text(encoding="utf-8"), parse_float=Decimal))

    def test_completed_photos_need_no_repeated_decisions(self):
        self.add_reviewed_photo()
        data, _, _ = self.build()
        self.assertEqual(2, data["summary"]["photos"])
        self.assertEqual(1, data["summary"]["photosNeedingReview"])

    def test_preserves_inputs_refuses_existing_output_and_is_deterministic(self):
        before = {path: hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in (self.prior, self.target, self.scores)}
        first, thumbnails, raw = self.build()
        self.assertEqual(first, self.build()[0])
        changes.write_report(self.output, first, thumbnails, raw)
        with self.assertRaisesRegex(ValueError, "already exists"):
            changes.write_report(self.output, first, thumbnails, raw)
        for path, checksum in before.items():
            self.assertEqual(checksum, hashlib.sha256(path.read_bytes()).hexdigest())
        self.assertEqual(self.target.read_bytes(), (self.output / "results.json").read_bytes())

    def test_labels_cannot_escape_inert_json_script(self):
        label = "</script><script>window.hacked=1</script>&"
        self.new["Results"][0]["Predictions"][0]["Label"] = label
        self.save()
        self.generate()
        page = (self.output / "report.html").read_text(encoding="utf-8")
        self.assertNotIn(label, page)
        self.assertIn("\\u003c/script\\u003e", page)
        self.assertEqual(2, page.count("<script"))

    def test_invalid_identity_and_missing_thumbnail_fail_explicitly(self):
        self.new["Results"][0]["ImagePath"] = r"C:\different\image.jpg"
        self.save()
        with self.assertRaisesRegex(ValueError, "inconsistent photo identity"):
            self.build()
        self.new["Results"][0]["ImagePath"] = r"C:\photos\image.jpg"
        self.save()
        changes.thumbnail_path(self.target, r"C:\photos\image.jpg").unlink()
        with self.assertRaisesRegex(ValueError, "Missing cached thumbnail"):
            self.build()


@unittest.skipUnless(os.environ.get("MODELBENCH_BROWSER_TESTS") == "1",
                     "Set MODELBENCH_BROWSER_TESTS=1 with Playwright and Edge installed")
class IncrementalReviewBrowserTests(ReviewFixture):
    def setUp(self):
        super().setUp()
        from playwright.sync_api import sync_playwright
        self.add_reviewed_photo()
        self.data = self.generate()
        playwright = sync_playwright().start()
        self.addCleanup(playwright.stop)
        browser = playwright.chromium.launch(channel="msedge", headless=True)
        self.addCleanup(browser.close)
        self.context = browser.new_context(accept_downloads=True)
        self.page = self.context.new_page()
        self.errors = []
        self.page.on("pageerror", lambda error: self.errors.append(str(error)))
        self.page.goto((self.output / "report.html").as_uri())

    def export(self, name):
        with self.page.expect_download() as download:
            self.page.get_by_role("button", name="Export scored CSV", exact=True).click()
        path = self.root / name
        download.value.save_as(path)
        return path

    def test_pending_only_shared_decisions_persistence_and_export_roundtrip(self):
        self.assertEqual(["bird", "new"], self.page.locator(".tag h3").all_text_contents())
        self.assertEqual(1, self.page.locator("#photo-select option").count())
        self.assertTrue(self.page.locator("#photo").evaluate("(img) => img.complete && img.naturalWidth > 0"))
        initial = self.export("initial.csv")
        loaded = cal.load_inputs(self.output / "results.json", initial)
        image = [r for r in loaded["reviews"] if r.image == "image.jpg"]
        self.assertTrue(all(next(p for p in r.candidates if p.label == "bird").checked is None for r in image))
        self.page.get_by_role("button", name="Correct: bird", exact=True).click()
        self.page.reload()
        self.assertEqual(["new"], self.page.locator(".tag h3").all_text_contents())
        self.page.get_by_role("button", name="Reject: new", exact=True).click()
        self.assertTrue(self.page.locator("#complete").is_visible())
        self.page.locator("#show-reviewed").check()
        self.assertEqual(2, self.page.locator("#photo-select option").count())
        self.page.get_by_role("button", name="Not sure: dog", exact=True).click()
        exported = self.export("reviewed.csv")
        loaded = cal.load_inputs(self.output / "results.json", exported)
        for review in loaded["reviews"]:
            if review.image == "image.jpg":
                self.assertEqual({"cat": True, "dog": None, "bird": True, "new": False},
                                 {p.label: p.checked for p in review.candidates})
            self.assertIsNone(review.quality)
        self.assertEqual([], self.errors)

    def test_bulk_rejection_is_explicit_and_storage_errors_are_visible(self):
        self.page.on("dialog", lambda dialog: dialog.accept())
        self.page.get_by_role("button", name="Reject remaining pending tags on this photo").click()
        exported = self.export("rejected.csv")
        loaded = cal.load_inputs(self.output / "results.json", exported)
        for review in loaded["reviews"]:
            if review.image == "image.jpg":
                self.assertEqual({"cat": True, "dog": False, "bird": False, "new": False},
                                 {p.label: p.checked for p in review.candidates})
        key = "PhotoLibrarian.ModelBench.Changes:" + self.data["fingerprint"]
        self.page.evaluate("key => localStorage.setItem(key, 'broken json')", key)
        self.page.reload()
        self.assertTrue(self.page.get_by_role("alert").is_visible())
        self.assertIn("disabled", self.page.get_by_role("alert").inner_text())
        self.page.get_by_role("button", name="Correct: bird", exact=True).click()
        self.assertEqual("broken json", self.page.evaluate("key => localStorage.getItem(key)", key))
        self.assertEqual([], self.errors)


if __name__ == "__main__":
    unittest.main()
