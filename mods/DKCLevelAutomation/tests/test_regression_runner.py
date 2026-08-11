from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "cli"))

import run_regression as regression  # noqa: E402


class FakeRunner(regression.RegressionRunner):
    def __init__(self, recipe: dict, output: Path) -> None:
        super().__init__(recipe, Path("fake-recipe.json"), Path("automation.json"), Path("tilemap.json"), output, 1.0)
        self.frame = 100
        self.y = 7

    def automation(self, command: str, arguments: dict | None = None):
        arguments = arguments or {}
        if command == "status":
            return {"frame": self.frame, "paused": True, "loaded": True}
        if command == "read_wram":
            value = self.y
            return {"address": "0x7E0BC1", "size": 2, "value": value, "valueHex": f"0x{value:X}"}
        if command == "step_frames":
            self.frame += int(arguments["count"])
            self.y += int(arguments["count"])
            return {"framesAdvanced": int(arguments["count"])}
        return {"ok": True}

    def tilemap(self, command: str, arguments: dict | None = None):
        if command == "status":
            return {"frame": self.frame, "attached": True}
        if command == "capture":
            return {"frame": self.frame, "manifest": f"capture-f{self.frame}.json", "folder": f"capture-f{self.frame}"}
        raise AssertionError(command)


class RegressionRunnerTests(unittest.TestCase):
    def test_substitution_and_all_checked_in_recipes_validate(self) -> None:
        for path in sorted((ROOT / "recipes").glob("*.json")):
            raw = regression.validate_recipe(json.loads(path.read_text(encoding="utf-8")), path)
            expanded = regression.substitute(raw, {"ROM": "test.sfc", "STATE": "test.szst0"})
            regression.validate_recipe(expanded, path)

    def test_boundary_crossing_writes_correlated_checkpoint(self) -> None:
        recipe = {
            "schema": 1,
            "name": "test",
            "steps": [{"kind": "checkpoint", "label": "start"}],
            "watches": [
                {
                    "name": "sprite0_y",
                    "address": "0x7E0BC1",
                    "size": 2,
                    "signed": True,
                    "units": "pixels",
                }
            ],
        }
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            runner = FakeRunner(recipe, output)
            runner.boundary_scan(
                {
                    "kind": "boundary_scan",
                    "watches": ["sprite0_y"],
                    "frames": 2,
                    "modulus": 8,
                    "captureLimit": 4,
                    "labelPrefix": "y8",
                }
            )
            self.assertEqual(len(runner.checkpoints), 1)
            checkpoint = runner.checkpoints[0]
            self.assertEqual(checkpoint["frame"], 101)
            self.assertEqual(checkpoint["watches"]["sprite0_y"]["within8"], 0)
            self.assertEqual(checkpoint["boundary"]["crossings"][0]["fromBucket"], 0)
            self.assertEqual(checkpoint["boundary"]["crossings"][0]["toBucket"], 1)
            self.assertEqual(checkpoint["tilemapCapture"]["manifest"], "capture-f101.json")
            self.assertTrue((output / "manifest.json").is_file())
            self.assertTrue((output / "checkpoint-01-y8-f001-sprite0_y.json").is_file())

    def test_checkpoint_expectations_support_hex_values_masks_and_failures(self) -> None:
        watches = {"sprite0_y": {"value": 0x0108}}
        results = regression.RegressionRunner.evaluate_expectations(
            watches,
            {
                "sprite0_y": "0x0108",
            },
        )
        self.assertTrue(results[0]["passed"])

        masked = regression.RegressionRunner.evaluate_expectations(
            watches,
            {
                "sprite0_y": {"op": "eq", "value": "0x0008", "mask": "0x00FF"},
            },
        )
        self.assertTrue(masked[0]["passed"])

        failed = regression.RegressionRunner.evaluate_expectations(
            watches,
            {
                "sprite0_y": {"op": "lt", "value": "0x0100"},
            },
        )
        self.assertFalse(failed[0]["passed"])


if __name__ == "__main__":
    unittest.main()
