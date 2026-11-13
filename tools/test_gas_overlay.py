"""Checks that overlay verification rejects missing or mismatched capture evidence."""
import hashlib
import json
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch


class OverlayEvidenceTests(unittest.TestCase):
    def check(self, mutate=None):
        with tempfile.TemporaryDirectory() as directory:
            images = [Path(directory) / f"frame-{index}.png" for index in range(2)]
            for index, image in enumerate(images):
                image.write_bytes(b"image" + bytes([index]))
                record = dict(Run="same-run", Index=index, Deferred=bool(index),
                              Opening=bool(index), HasGas=True, Shields=0, Saved=True,
                              Sha256=hashlib.sha256(image.read_bytes()).hexdigest().upper())
                Path(str(image) + ".json").write_text(json.dumps(record))
            if mutate:
                mutate(images)
            with patch("sys.argv", ["check_gas_overlay.py", *map(str, images)]), patch(
                "subprocess.check_output", side_effect=["1000 600\n", "1000 600\n", "0"]
            ) as command:
                with self.assertRaises(SystemExit) as exit_result:
                    runpy.run_path(str(Path(__file__).with_name("check_gas_overlay.py")), run_name="__main__")
                return exit_result.exception.code, command.call_count

    def test_valid_evidence_reaches_pixel_check(self):
        self.assertEqual((0, 3), self.check())

    def test_missing_image_is_refused(self):
        code, calls = self.check(lambda paths: paths[1].unlink())
        self.assertNotEqual(code, 0)
        self.assertEqual(calls, 0)

    def test_missing_metadata_is_refused(self):
        code, calls = self.check(lambda paths: Path(str(paths[1]) + ".json").unlink())
        self.assertNotEqual(code, 0)
        self.assertEqual(calls, 0)

    def test_invalid_states_are_refused_before_pixel_check(self):
        for key, value in [("Deferred", False), ("Opening", False), ("HasGas", False),
                           ("Shields", 1), ("Saved", False), ("Index", 0),
                           ("Run", "different-run"), ("Sha256", "stale")]:
            with self.subTest(key=key):
                def mutate(paths):
                    path = Path(str(paths[1]) + ".json")
                    record = json.loads(path.read_text())
                    record[key] = value
                    path.write_text(json.dumps(record))
                code, calls = self.check(mutate)
                self.assertNotEqual(code, 0)
                self.assertEqual(calls, 0)


if __name__ == "__main__":
    unittest.main()
