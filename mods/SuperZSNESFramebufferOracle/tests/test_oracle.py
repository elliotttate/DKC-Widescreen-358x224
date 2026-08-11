from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

from PIL import Image

PROJECT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT))

from oracle_common import RAW_ORACLE_FILES, file_record, load_recipe, write_json
from oracle_compare import compare_images, compare_runs


class ImageComparisonTests(unittest.TestCase):
    def test_exact_images_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = root / "stock.png"
            candidate = root / "candidate.png"
            Image.new("RGBA", (3, 2), (10, 20, 30, 255)).save(stock)
            Image.new("RGBA", (3, 2), (10, 20, 30, 255)).save(candidate)
            report = compare_images(stock, candidate, root / "report")
            self.assertTrue(report["passed"])
            self.assertTrue(report["exactMatch"])
            self.assertEqual(report["differingPixels"], 0)

    def test_one_pixel_reports_bbox_and_fails(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = root / "stock.png"
            candidate = root / "candidate.png"
            Image.new("RGBA", (3, 2), (0, 0, 0, 255)).save(stock)
            changed = Image.new("RGBA", (3, 2), (0, 0, 0, 255))
            changed.putpixel((2, 1), (7, 0, 0, 255))
            changed.save(candidate)
            report = compare_images(stock, candidate, root / "report")
            self.assertFalse(report["passed"])
            self.assertEqual(report["differingPixels"], 1)
            self.assertEqual(report["differenceBoundingBox"], [2, 1, 3, 2])
            self.assertEqual(report["maxAbsoluteChannelDelta"], 7)
            self.assertTrue((root / "report" / "diff.png").is_file())

    def test_channel_tolerance_can_accept_small_delta(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = root / "stock.png"
            candidate = root / "candidate.png"
            Image.new("RGBA", (1, 1), (20, 20, 20, 255)).save(stock)
            Image.new("RGBA", (1, 1), (22, 20, 20, 255)).save(candidate)
            report = compare_images(stock, candidate, root / "report", channel_tolerance=2)
            self.assertTrue(report["passed"])
            self.assertFalse(report["exactMatch"])
            self.assertEqual(report["differingPixels"], 0)

    def test_dimension_mismatch_is_never_resized(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = root / "stock.png"
            candidate = root / "candidate.png"
            Image.new("RGBA", (2, 2), (0, 0, 0, 255)).save(stock)
            Image.new("RGBA", (3, 2), (0, 0, 0, 255)).save(candidate)
            report = compare_images(stock, candidate, root / "report")
            self.assertFalse(report["compatibleDimensions"])
            self.assertFalse(report["passed"])


def make_run(root: Path, image_color: tuple[int, int, int, int], vram_value: bytes) -> Path:
    raw = root / "cases" / "case" / "f000000" / "raw"
    raw.mkdir(parents=True)
    image = raw / "frame-composed.png"
    Image.new("RGBA", (2, 2), image_color).save(image)
    for name in RAW_ORACLE_FILES:
        path = raw / name
        path.write_bytes(vram_value if name == "vram.bin" else b"same")
    raw_files = {name: file_record(raw / name, root) for name in RAW_ORACLE_FILES}
    manifest = {
        "schemaVersion": 1,
        "suiteId": "test-suite",
        "completed": True,
        "rom": {"sha256": "rom"},
        "recipe": {"sha256": "recipe"},
        "cases": [
            {
                "id": "case",
                "state": {"sha256": "state"},
                "macro": "0=NONE",
                "controller": 1,
                "checkpoints": [
                    {
                        "relativeFrame": 0,
                        "image": file_record(image, root),
                        "rawFiles": raw_files,
                    }
                ],
            }
        ],
    }
    write_json(root / "manifest.json", manifest)
    return root


class RunComparisonTests(unittest.TestCase):
    def test_visual_difference_with_equal_raw_state_is_renderer_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = make_run(root / "stock", (0, 0, 0, 255), b"vram")
            candidate = make_run(root / "candidate", (1, 0, 0, 255), b"vram")
            summary, exit_code = compare_runs(stock, candidate, root / "report")
            self.assertEqual(exit_code, 2)
            self.assertTrue(summary["validComparison"])
            self.assertEqual(summary["outcome"], "visual-fail")

    def test_raw_ppu_difference_invalidates_renderer_comparison(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            stock = make_run(root / "stock", (0, 0, 0, 255), b"stock-vram")
            candidate = make_run(root / "candidate", (0, 0, 0, 255), b"candidate-vram")
            summary, exit_code = compare_runs(stock, candidate, root / "report")
            self.assertEqual(exit_code, 1)
            self.assertFalse(summary["validComparison"])
            self.assertEqual(summary["comparisons"][0]["outcome"], "invalid-input")


class RecipeTests(unittest.TestCase):
    def test_checked_in_recipe_validates(self):
        recipe = load_recipe(PROJECT / "recipes" / "dkc-saved-states.json")
        self.assertEqual(len(recipe["cases"]), 3)
        self.assertEqual({case["stateKey"] for case in recipe["cases"]}, {"jungle", "cave", "barrel"})


if __name__ == "__main__":
    unittest.main()
