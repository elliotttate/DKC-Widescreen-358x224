#!/usr/bin/env python3
"""Compare two summaries emitted by benchmark.py without touching the emulator."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def load_summary(value: str) -> dict[str, Any]:
    path = Path(value)
    if path.is_dir():
        path = path / "summary.json"
    result = json.loads(path.read_text(encoding="utf-8"))
    result["summaryPath"] = str(path.resolve())
    return result


def ratio(after: Any, before: Any) -> float | None:
    if after is None or before in (None, 0):
        return None
    return float(after) / float(before)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline")
    parser.add_argument("comparison")
    parser.add_argument("--output")
    args = parser.parse_args()
    baseline = load_summary(args.baseline)
    comparison = load_summary(args.comparison)
    result = {
        "schema": 1,
        "baseline": baseline,
        "comparison": comparison,
        "ratios": {
            "cadence": ratio(comparison.get("cadenceFps"), baseline.get("cadenceFps")),
            "cpuSecondsPerFrame": ratio(
                comparison.get("cpuSecondsPerEmulatedFrame"), baseline.get("cpuSecondsPerEmulatedFrame")
            ),
            "cpuOneCorePercent": ratio(
                comparison.get("cpuOneCorePercent"), baseline.get("cpuOneCorePercent")
            ),
            "meanWorkingSet": ratio(
                comparison.get("workingSetBytes", {}).get("mean"),
                baseline.get("workingSetBytes", {}).get("mean"),
            ),
            "p95WindowFrameMs": ratio(
                comparison.get("windowAverageFrameMs", {}).get("p95"),
                baseline.get("windowAverageFrameMs", {}).get("p95"),
            ),
        },
        "caveat": "Comparisons are meaningful only when gameplay scene, window state, plugins, polling interval, and input mode are controlled.",
    }
    rendered = json.dumps(result, indent=2) + "\n"
    if args.output:
        Path(args.output).write_text(rendered, encoding="utf-8")
    print(rendered, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
