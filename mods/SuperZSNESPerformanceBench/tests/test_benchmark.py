from __future__ import annotations

import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

import benchmark  # noqa: E402


class BenchmarkSummaryTests(unittest.TestCase):
    def test_percentile_interpolates(self) -> None:
        self.assertEqual(benchmark.percentile([1, 2, 3], 0.5), 2)
        self.assertEqual(benchmark.percentile([1, 3], 0.5), 2)
        self.assertIsNone(benchmark.percentile([], 0.5))

    def test_summary_excludes_paused_windows(self) -> None:
        samples = [
            {"monotonicSeconds": 0.0, "elapsedSeconds": 0.0, "frame": 100, "cpuTimeSeconds": 10.0, "paused": False, "workingSetBytes": 100, "privateBytes": 200, "handleCount": 10, "statusResponseLatencyMs": 1.0},
            {"monotonicSeconds": 1.0, "elapsedSeconds": 1.0, "frame": 160, "cpuTimeSeconds": 10.5, "paused": False, "workingSetBytes": 110, "privateBytes": 210, "handleCount": 11, "statusResponseLatencyMs": 2.0},
            {"monotonicSeconds": 2.0, "elapsedSeconds": 2.0, "frame": 160, "cpuTimeSeconds": 10.6, "paused": True, "workingSetBytes": 120, "privateBytes": 220, "handleCount": 12, "statusResponseLatencyMs": 3.0},
        ]
        result = benchmark.derive_summary(samples, "normal", 1.0)
        self.assertEqual(result["frameAdvanceInRunningWindows"], 60)
        self.assertEqual(result["cadenceFps"], 60)
        self.assertEqual(result["cpuSeconds"], 0.5)
        self.assertEqual(result["pausedSamples"], 1)

    def test_high_cadence_is_only_a_candidate_inference(self) -> None:
        samples = [
            {"monotonicSeconds": 0.0, "elapsedSeconds": 0.0, "frame": 0, "cpuTimeSeconds": 0.0, "paused": False, "workingSetBytes": 1, "privateBytes": 1, "handleCount": 1, "statusResponseLatencyMs": 1.0},
            {"monotonicSeconds": 1.0, "elapsedSeconds": 1.0, "frame": 200, "cpuTimeSeconds": 1.0, "paused": False, "workingSetBytes": 1, "privateBytes": 1, "handleCount": 1, "statusResponseLatencyMs": 1.0},
        ]
        result = benchmark.derive_summary(samples, "fast", 1.0)
        self.assertEqual(result["inferredMode"], "fast-forward-candidate")

    def test_memory_trend_reports_growth_and_material_drop(self) -> None:
        mebibyte = 1024 * 1024
        samples = [
            {"elapsedSeconds": 0.0, "privateBytes": 100 * mebibyte},
            {"elapsedSeconds": 1.0, "privateBytes": 120 * mebibyte},
            {"elapsedSeconds": 2.0, "privateBytes": 108 * mebibyte},
            {"elapsedSeconds": 3.0, "privateBytes": 140 * mebibyte},
        ]
        trend = benchmark.trend_block(samples, "privateBytes")
        self.assertEqual(trend["delta"], 40 * mebibyte)
        self.assertEqual(trend["largestDrop"], -12 * mebibyte)
        self.assertTrue(trend["sawtoothCandidate"])

    def test_two_status_observations_produce_cadence_without_frame_polling(self) -> None:
        samples = [
            {"monotonicSeconds": 10.0, "elapsedSeconds": 0.0, "cpuTimeSeconds": 20.0, "workingSetBytes": 100, "privateBytes": 200, "handleCount": 10},
            {"monotonicSeconds": 20.0, "elapsedSeconds": 10.0, "cpuTimeSeconds": 25.0, "workingSetBytes": 110, "privateBytes": 210, "handleCount": 10},
        ]
        observations = [
            {"monotonicSeconds": 10.0, "frame": 100, "paused": False, "statusResponseLatencyMs": 2.0},
            {"monotonicSeconds": 20.0, "frame": 700, "paused": False, "statusResponseLatencyMs": 3.0},
        ]
        result = benchmark.derive_summary(samples, "normal", 1.0, observations)
        self.assertEqual(result["cadenceFps"], 60)
        self.assertEqual(result["statusCalls"], 2)
        self.assertEqual(result["handleCountTrend"]["delta"], 0)
        self.assertIsNone(result["stalledRunningWindows"])


if __name__ == "__main__":
    unittest.main()
