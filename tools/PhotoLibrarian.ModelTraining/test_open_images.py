import base64
import copy
import csv
from email.message import Message
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from fetch_open_images import fetch
from download_open_images import download, read_selection
from select_open_images import balanced_ids, mapping, select, target_states, verified_rows


class Response(io.BytesIO):
    def __init__(self, content, *, md5=None, length=None):
        super().__init__(content)
        self.headers = Message()
        self.headers["Content-Length"] = str(len(content) if length is None else length)
        self.headers["x-goog-hash"] = "crc32c=unused"
        self.headers["x-goog-hash"] = "md5=" + (
            md5 if md5 is not None else base64.b64encode(hashlib.md5(content).digest()).decode())


class FetchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pins = patch("fetch_open_images.PINNED_SHA256", {"classes": hashlib.sha256(b"catalog").hexdigest()})
        self.pins.start()
        self.addCleanup(self.pins.stop)

    def test_verifies_duplicate_hash_headers_and_reuses_exact_cache(self):
        with patch("fetch_open_images.urllib.request.urlopen", return_value=Response(b"catalog")) as request:
            receipt = fetch("classes", self.root)
        self.assertEqual(hashlib.sha256(b"catalog").hexdigest(), receipt["sha256"])
        request.assert_called_once()
        with patch("fetch_open_images.urllib.request.urlopen") as request:
            self.assertEqual(receipt, fetch("classes", self.root))
        request.assert_not_called()
        (self.root / "oidv7-class-descriptions.csv").write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            fetch("classes", self.root)

    def test_bad_md5_truncated_and_oversized_sources_never_publish(self):
        for name, options in [
            ("checksum", {"md5": "incorrect"}),
            ("truncated", {"length": 100}),
            ("oversized", {"length": 1}),
        ]:
            with self.subTest(name=name):
                directory = self.root / name
                with patch("fetch_open_images.urllib.request.urlopen", return_value=Response(b"abc", **options)):
                    with self.assertRaises(ValueError):
                        fetch("classes", directory)
                self.assertFalse((directory / "oidv7-class-descriptions.csv").exists())
                self.assertFalse((directory / "oidv7-class-descriptions.csv.source.json").exists())
                with self.assertRaisesRegex(ValueError, "Unfinished download"):
                    fetch("classes", directory)

    def test_unreviewed_source_change_fails_even_with_matching_server_md5(self):
        with patch("fetch_open_images.urllib.request.urlopen", return_value=Response(b"changed catalog")):
            with self.assertRaisesRegex(ValueError, "reviewed SHA-256"):
                fetch("classes", self.root)
        self.assertFalse((self.root / "oidv7-class-descriptions.csv").exists())


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.row = {
            "sourceId": "0000000000000001", "originalSplit": "train",
            "labels": {"ocean": "positive"},
            "provenance": {
                "License": "https://creativecommons.org/licenses/by/2.0/",
                "Author": "Synthetic author", "ImageID": "0000000000000001", "Subset": "train",
            },
        }

    def test_fixed_mirror_stream_hash_and_exact_cache_reuse(self):
        content = b"\xff\xd8synthetic-jpeg-fixture\xff\xd9"
        with patch("download_open_images.urllib.request.urlopen", return_value=Response(content)) as request:
            result = download(self.row, self.root)
        self.assertEqual("https://open-images-dataset.s3.amazonaws.com/train/0000000000000001.jpg",
                         request.call_args.args[0])
        self.assertEqual(hashlib.sha256(content).hexdigest(), result["download"]["sha256"])
        self.assertFalse(result["trainingReady"])
        with patch("download_open_images.urllib.request.urlopen") as request:
            self.assertEqual(result, download(self.row, self.root))
        request.assert_not_called()
        (self.root / result["path"]).write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            download(self.row, self.root)

    def test_invalid_jpeg_and_truncated_payloads_never_publish(self):
        for name, response in [
            ("html", Response(b"<html>failure</html>")),
            ("truncated", Response(b"\xff\xd8data\xff\xd9", length=40)),
        ]:
            with self.subTest(name=name):
                output = self.root / name
                with patch("download_open_images.urllib.request.urlopen", return_value=response):
                    with self.assertRaisesRegex(ValueError, "Incomplete/non-JPEG"):
                        download(self.row, output)
                self.assertFalse((output / "images" / "train" / "0000000000000001.jpg").exists())

    def test_jpeg_markers_can_span_network_read_boundaries(self):
        class SmallReads(Response):
            def read(self, size=-1):
                return super().read(1)

        content = b"\xff\xd8data\xff\xd9"
        with patch("download_open_images.urllib.request.urlopen", return_value=SmallReads(content)):
            result = download(self.row, self.root)
        self.assertEqual(len(content), result["download"]["bytes"])

    def test_jpeg_frame_can_have_trailing_mpo_data(self):
        content = b"\xff\xd8first-frame\xff\xd9trailing-mpo-data"
        with patch("download_open_images.urllib.request.urlopen", return_value=Response(content)):
            result = download(self.row, self.root)
        self.assertEqual(len(content), result["download"]["bytes"])

    def test_selection_id_split_and_rights_validation(self):
        path = self.root / "selection.jsonl"
        path.write_text(json.dumps(self.row) + "\n", encoding="utf-8")
        self.assertEqual([self.row], read_selection(path))
        for field, value in [("sourceId", "../escape"), ("originalSplit", "test"),
                             ("labels", {}), ("labels", {"dog": "unknown"})]:
            row = copy.deepcopy(self.row)
            row[field] = value
            path.write_text(json.dumps(row) + "\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                read_selection(path)
        row = copy.deepcopy(self.row)
        row["provenance"]["License"] = "https://creativecommons.org/licenses/by-nc/4.0/"
        path.write_text(json.dumps(row) + "\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "allowlisted"):
            read_selection(path)
        path.write_text((json.dumps(self.row) + "\n") * 2, encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Repeated"):
            read_selection(path)


class SelectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.annotations = self.root / "annotations.csv"
        self.metadata = self.root / "metadata.csv"
        self.mappings = [
            {"label": "ocean", "midList": ["/m/ocean", "/m/sea"], "negativePolicy": "all"},
            {"label": "computer", "midList": ["/m/laptop"], "negativePolicy": "positive-only"},
            {"label": "celebration", "midList": [], "negativePolicy": "defer"},
        ]

    def write_csv(self, path, fields, rows):
        with path.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(rows)

    def labels(self, rows):
        self.write_csv(self.annotations, ["ImageID", "Source", "LabelName", "Confidence"], [
            {"ImageID": image, "Source": source, "LabelName": mid, "Confidence": state}
            for image, mid, state, source in rows])

    def provenance(self, ids):
        rows = [{
            "ImageID": image, "Subset": "train",
            "License": "https://creativecommons.org/licenses/by/2.0/",
            "Author": "Synthetic author", "OriginalLandingURL": f"https://example.org/{image}",
            "OriginalMD5": "", "Rotation": "0",
        } for image in ids]
        self.write_csv(self.metadata, list(rows[0]), rows)
        return rows

    def test_union_absence_requires_all_components_and_narrow_negatives_stay_unknown(self):
        self.assertEqual({}, target_states({"/m/ocean": 0, "/m/laptop": 0}, self.mappings))
        self.assertEqual({"ocean": "negative"}, target_states(
            {"/m/ocean": 0, "/m/sea": 0}, self.mappings))
        self.assertEqual({"ocean": "positive", "computer": "positive"}, target_states(
            {"/m/ocean": 0, "/m/sea": 1, "/m/laptop": 1}, self.mappings))

    def test_machine_labels_never_transfer_and_decimal_verified_states_work(self):
        self.labels([
            ("0000000000000001", "/m/ocean", "1.0", "verification"),
            ("0000000000000002", "/m/ocean", "0.9", "machine"),
            ("0000000000000003", "/m/ocean", "0.0", "crowdsource-verification"),
        ])
        self.assertEqual([
            ("0000000000000001", "/m/ocean", 1), ("0000000000000003", "/m/ocean", 0)],
            list(verified_rows(self.annotations, {"/m/ocean"})))
        self.labels([("0000000000000001", "/m/ocean", "0.9", "verification")])
        with self.assertRaisesRegex(ValueError, "Invalid verified"):
            list(verified_rows(self.annotations, {"/m/ocean"}))

    def test_all_selected_annotations_are_joined_not_just_reservoir_members(self):
        image = "0000000000000001"
        self.labels([
            (image, "/m/ocean", "0.0", "verification"),
            (image, "/m/sea", "1.0", "verification"),
            (image, "/m/laptop", "0.0", "verification"),
        ])
        self.provenance([image])
        selected, report = select(self.annotations, self.metadata, self.mappings, "train", 1, 1, "seed")
        self.assertEqual({"ocean": "positive"}, selected[0]["labels"])
        self.assertEqual(0, selected[0]["sourceLabels"]["/m/laptop"])
        self.assertFalse(selected[0]["licenseVerifiedAtSource"])
        self.assertFalse(report["trainingReady"])
        self.assertEqual(1, report["coverage"][1]["selectedUnknownImages"])
        self.assertEqual("defer", report["coverage"][2]["policy"])

    def test_order_independent_unique_candidates_and_caps(self):
        ids = [f"{index:016x}" for index in range(40)]
        rows = [(image, "/m/ocean", "1", "verification") for image in ids]
        self.labels(rows + rows)
        self.provenance(ids)
        first, report = select(self.annotations, self.metadata, self.mappings, "train", 10, 5, "seed")
        self.labels(list(reversed(rows + rows)))
        second, other = select(self.annotations, self.metadata, self.mappings, "train", 10, 5, "seed")
        self.assertEqual(first, second)
        self.assertEqual(report, other)
        self.assertEqual(10, report["candidateImages"])
        self.assertEqual(5, report["selectedImages"])
        self.assertEqual(80, report["coverage"][0]["sourcePositiveAnnotationRows"])
        self.assertEqual(5, report["rejected"]["globalCap"])
        changed, _ = select(self.annotations, self.metadata, self.mappings, "train", 10, 5, "new-seed")
        self.assertNotEqual(first, changed)

    def test_balanced_cap_retains_rare_target_and_state_evidence(self):
        rows = {str(i): {"labels": {"common": "positive"}} for i in range(100)}
        rows["98"]["labels"]["rare"] = "positive"
        rows["99"]["labels"]["rare"] = "negative"
        chosen = balanced_ids(rows, 3, int)
        self.assertEqual({"0", "98", "99"}, set(chosen))
        self.assertEqual(chosen, balanced_ids(dict(reversed(list(rows.items()))), 3, int))
        self.assertEqual(100, len(balanced_ids(rows, 1000, int)))

    def test_eligible_first_refills_reservoir_after_provenance_filter(self):
        ids = [f"{index:016x}" for index in range(30)]
        self.labels([(image, "/m/ocean", "1", "verification") for image in ids])
        rows = self.provenance(ids)
        # Only the highest hash-ranked ID is eligible; a size-one prefilter
        # reservoir would otherwise discard it.
        import hashlib
        keep = max(ids, key=lambda image: int(hashlib.sha256(f"seed:train:{image}".encode()).hexdigest(), 16))
        for row in rows:
            if row["ImageID"] != keep:
                row["License"] = "not-allowlisted"
        self.write_csv(self.metadata, list(rows[0]), rows)
        selected, report = select(self.annotations, self.metadata, self.mappings, "train", 1, 3, "seed",
                                  eligible_first=True, balanced=True)
        self.assertEqual([keep], [row["sourceId"] for row in selected])
        self.assertTrue(report["eligibleFirst"])
        self.assertTrue(report["balanced"])
        self.assertEqual(30, report["coverage"][0]["sourcePositiveAnnotationRows"])

    def test_rights_attribution_missing_metadata_and_split_gates(self):
        ids = [f"{index:016x}" for index in range(4)]
        self.labels([(image, "/m/ocean", "1", "verification") for image in ids])
        rows = self.provenance(ids[:3])
        rows[0]["License"] = "https://creativecommons.org/licenses/by-nc/4.0/"
        rows[1]["Author"] = ""
        self.write_csv(self.metadata, list(rows[0]), rows)
        selected, report = select(self.annotations, self.metadata, self.mappings, "train", 10, 10, "seed")
        self.assertEqual([ids[2]], [row["sourceId"] for row in selected])
        self.assertEqual(1, report["rejected"]["licenseNotAllowlisted"])
        self.assertEqual(1, report["rejected"]["missingAttribution"])
        self.assertEqual(1, report["rejected"]["metadataUnavailable"])
        with self.assertRaisesRegex(ValueError, "split mismatch"):
            select(self.annotations, self.metadata, self.mappings, "validation", 10, 10, "seed")
        with self.assertRaisesRegex(ValueError, "train/validation"):
            select(self.annotations, self.metadata, self.mappings, "test", 10, 10, "seed")

    def test_conflicting_annotations_and_invalid_limits_fail(self):
        image = "0000000000000001"
        self.labels([
            (image, "/m/ocean", "1", "verification"),
            (image, "/m/ocean", "0", "crowdsource-verification"),
        ])
        self.provenance([image])
        with self.assertRaisesRegex(ValueError, "Conflicting human labels"):
            select(self.annotations, self.metadata, self.mappings, "train", 10, 10, "seed")
        with self.assertRaisesRegex(ValueError, "Positive limits"):
            select(self.annotations, self.metadata, self.mappings, "train", 0, 10, "seed")

    def test_mapping_requires_complete_vocabulary_and_exact_catalog_names(self):
        classes = self.root / "classes.csv"
        classes.write_text("/m/ocean,Ocean\n/m/sea,Sea\n", encoding="utf-8")
        path = self.root / "mapping.csv"
        fields = ["label", "mids", "sourceNames", "negativePolicy", "rationale"]
        row = {"label": "ocean", "mids": "/m/ocean|/m/sea", "sourceNames": "Ocean|Sea",
               "negativePolicy": "all", "rationale": "Union of two concepts"}
        self.write_csv(path, fields, [row])
        result = mapping(path, classes, [{"label": "ocean"}])
        self.assertEqual(["/m/ocean", "/m/sea"], result[0]["midList"])
        for field, value, expected in [
            ("sourceNames", "Ocean|Wrong", "catalog mismatch"),
            ("negativePolicy", "defer", "must not transfer"),
            ("negativePolicy", "anything", "explicit transfer policy"),
        ]:
            modified = copy.deepcopy(row)
            modified[field] = value
            self.write_csv(path, fields, [modified])
            with self.assertRaisesRegex(ValueError, expected):
                mapping(path, classes, [{"label": "ocean"}])
        self.write_csv(path, fields, [row, row])
        with self.assertRaisesRegex(ValueError, "exactly once"):
            mapping(path, classes, [{"label": "ocean"}])


if __name__ == "__main__":
    unittest.main()
