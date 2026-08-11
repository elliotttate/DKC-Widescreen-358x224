#!/usr/bin/env python3
"""Standard-library fixture test for compare_sessions.py."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path


SCRIPT = Path(__file__).resolve().parent / "compare_sessions.py"


class CompareSessionsTest(unittest.TestCase):
    def make_condition(self, root: Path, name: str, updates: int, avg_ms: float) -> Path:
        condition = root / name
        condition.mkdir()
        windows = condition / "windows.jsonl"
        start = datetime(2026, 8, 11, tzinfo=timezone.utc)
        rows = []
        two = 300 - updates
        one = updates - two
        for index in range(1, 7):
            rows.append({
                "utc": (start + timedelta(seconds=index * 5)).isoformat().replace("+00:00", "Z"),
                "reason": "interval",
                "windowSeconds": 5.0,
                "unity": {"vSyncCount": 0, "targetFrameRate": 240},
                "updates": {
                    "count": updates, "hz": updates / 5.0, "avgMs": avg_ms, "maxMs": avg_ms + 5,
                    "cadenceAvgMs": 5000.0 / updates, "cadenceMaxMs": 30.0,
                },
                "runFrames": {
                    "count": 300, "hz": 60.0, "orphan": 0,
                    "perUpdate": {"0": 0, "1": one, "2": two, "3": 0, "4": 0, "5Plus": 0},
                },
                "renderer": {"count": 0, "avgMs": 0, "maxMs": 0, "layers": []},
            })
        windows.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
        manifest = {
            "schema": 1,
            "kind": "realtime-jungle-scroll-cadence",
            "pacingMode": "schedule-plus-resume",
            "exactFrameStepping": False,
            "warmupSeconds": 7.0,
            "measurementSeconds": 30.0,
            "frameDelta": 2220,
            "measurementStartUtc": start.isoformat().replace("+00:00", "Z"),
            "measurementEndUtc": (start + timedelta(seconds=30)).isoformat().replace("+00:00", "Z"),
            "cadenceWindows": str(windows),
            "rom": {"sha256": "same-rom"},
            "state": {"sha256": "same-state"},
            "wramDelta": {"camera_x": 128, "layer1_x": 128},
        }
        path = condition / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        return path

    def test_manifest_selection_and_improvement_gate(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline = self.make_condition(root, "baseline", updates=250, avg_ms=18.0)
            candidate = self.make_condition(root, "candidate", updates=275, avg_ms=16.0)
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--baseline", str(baseline), "--candidate", str(candidate),
                 "--enforce", "--require-improvement"],
                text=True, capture_output=True, check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
            report = json.loads(result.stdout)
            self.assertEqual(report["baseline"]["windowCount"], 6)
            self.assertAlmostEqual(report["baseline"]["runFrames"]["hz"], 60.0)
            self.assertGreater(report["deltaPercent"]["updateHz"], 5.0)
            self.assertTrue(all(item["passed"] for item in report["checks"]))


if __name__ == "__main__":
    unittest.main()
