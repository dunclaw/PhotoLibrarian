import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import prepare_zero_shot as prepare


class TinyClipReleaseTests(unittest.TestCase):
    def test_canonical_vocabulary_ignores_checkout_line_endings(self):
        source = Path(__file__).with_name("home-photo-vocabulary.general-v2.json")
        expected = source.read_bytes().replace(b"\r\n", b"\n")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vocabulary.json"
            path.write_bytes(expected.replace(b"\n", b"\r\n"))
            self.assertEqual(expected, prepare.canonical_tinyclip_vocabulary(path))

    def test_changed_vocabulary_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "vocabulary.json"
            path.write_bytes(b'{"labels":["different"]}')
            with self.assertRaisesRegex(ValueError, "exact calibrated"):
                prepare.canonical_tinyclip_vocabulary(path)

    def test_output_verification_rejects_missing_size_and_hash_changes(self):
        content = b"expected"
        pins = {"vision.onnx": (len(content), hashlib.sha256(content).hexdigest())}
        with tempfile.TemporaryDirectory() as directory, patch.object(prepare, "TINYCLIP_PROFILE_FILES", pins):
            root = Path(directory)
            with self.assertRaises(ValueError):
                prepare.verify_tinyclip_profile(root)
            path = root / "vision.onnx"
            path.write_bytes(content)
            prepare.verify_tinyclip_profile(root)
            path.write_bytes(b"wrong")
            with self.assertRaises(ValueError):
                prepare.verify_tinyclip_profile(root)
            path.write_bytes(b"tampered")
            with self.assertRaises(ValueError):
                prepare.verify_tinyclip_profile(root)


if __name__ == "__main__":
    unittest.main()
