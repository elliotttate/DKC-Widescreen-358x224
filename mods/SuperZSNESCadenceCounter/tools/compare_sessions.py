#!/usr/bin/env python3
"""Compare fully-contained CadenceCounter windows from two paced runs."""

from __future__ import annotations

import argparse
import json
import math
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any


class ComparisonError(RuntimeError):
    pass


def parse_utc(value: str) -> datetime:
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def resolve_input(path: Path) -> dict[str, Any]:
    path = path.resolve()
    if path.is_dir():
        path = path / "windows.jsonl"
    if not path.is_file():
        raise ComparisonError(f"input does not exist: {path}")
    if path.name.lower() == "manifest.json":
        manifest = json.loads(path.read_text(encoding="utf-8"))
        if manifest.get("exactFrameStepping") is not False or manifest.get("pacingMode") != "schedule-plus-resume":
            raise ComparisonError(f"{path}: not a real-time schedule-plus-resume manifest")
        windows_value = manifest.get("cadenceWindows")
        if not windows_value:
            raise ComparisonError(f"{path}: cadenceWindows is missing")
        windows = Path(windows_value)
        if not windows.is_absolute():
            windows = path.parent / windows
        return {
            "source": str(path),
            "windows": windows.resolve(),
            "start": parse_utc(manifest["measurementStartUtc"]),
            "end": parse_utc(manifest["measurementEndUtc"]),
            "manifest": manifest,
        }
    return {"source": str(path), "windows": path, "start": None, "end": None, "manifest": None}


def load_windows(condition: dict[str, Any]) -> tuple[list[dict[str, Any]], int]:
    path: Path = condition["windows"]
    if not path.is_file():
        raise ComparisonError(f"cadence windows do not exist: {path}")
    selected: list[dict[str, Any]] = []
    total_intervals = 0
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ComparisonError(f"{path}:{number}: {exc}") from exc
        if row.get("reason") != "interval":
            continue
        total_intervals += 1
        end = parse_utc(row["utc"])
        start = end - timedelta(seconds=float(row["windowSeconds"]))
        if condition["start"] is not None and start < condition["start"]:
            continue
        if condition["end"] is not None and end > condition["end"]:
            continue
        selected.append(row)
    return selected, total_intervals


def weighted(rows: list[dict[str, Any]], section: str, field: str, weight_field: str) -> float:
    weight = sum(float(row[section].get(weight_field, 0)) for row in rows)
    if weight == 0:
        return 0.0
    return sum(
        float(row[section].get(field, 0)) * float(row[section].get(weight_field, 0))
        for row in rows
    ) / weight


def aggregate(rows: list[dict[str, Any]], total_intervals: int) -> dict[str, Any]:
    seconds = sum(float(row["windowSeconds"]) for row in rows)
    update_count = sum(int(row["updates"]["count"]) for row in rows)
    frame_count = sum(int(row["runFrames"]["count"]) for row in rows)
    buckets = {name: 0 for name in ("0", "1", "2", "3", "4", "5Plus")}
    for row in rows:
        for name in buckets:
            buckets[name] += int(row["runFrames"].get("perUpdate", {}).get(name, 0))
    bucket_total = sum(buckets.values())
    two_plus = buckets["2"] + buckets["3"] + buckets["4"] + buckets["5Plus"]
    unity_values = sorted({
        (int(row.get("unity", {}).get("vSyncCount", -1)), int(row.get("unity", {}).get("targetFrameRate", -1)))
        for row in rows
    })
    renderer_count = sum(int(row.get("renderer", {}).get("count", 0)) for row in rows)
    renderer_avg = 0.0
    if renderer_count:
        renderer_avg = sum(
            float(row.get("renderer", {}).get("avgMs", 0)) * int(row.get("renderer", {}).get("count", 0))
            for row in rows
        ) / renderer_count
    return {
        "windowCount": len(rows),
        "availableIntervalWindows": total_intervals,
        "seconds": round(seconds, 6),
        "unitySettings": [{"vSyncCount": pair[0], "targetFrameRate": pair[1]} for pair in unity_values],
        "updates": {
            "count": update_count,
            "hz": update_count / seconds if seconds else 0.0,
            "avgMs": weighted(rows, "updates", "avgMs", "count"),
            "maxMs": max((float(row["updates"].get("maxMs", 0)) for row in rows), default=0.0),
            "cadenceAvgMs": weighted(rows, "updates", "cadenceAvgMs", "count"),
            "cadenceMaxMs": max((float(row["updates"].get("cadenceMaxMs", 0)) for row in rows), default=0.0),
        },
        "runFrames": {
            "count": frame_count,
            "hz": frame_count / seconds if seconds else 0.0,
            "orphan": sum(int(row["runFrames"].get("orphan", 0)) for row in rows),
            "perUpdate": buckets,
            "twoPlusShare": two_plus / bucket_total if bucket_total else 0.0,
        },
        "renderer": {
            "count": renderer_count,
            "avgMs": renderer_avg,
            "maxMs": max((float(row.get("renderer", {}).get("maxMs", 0)) for row in rows), default=0.0),
        },
    }


def percentage(candidate: float, baseline: float) -> float | None:
    return ((candidate / baseline) - 1.0) * 100.0 if baseline else None


def scroll_delta(manifest: dict[str, Any] | None) -> int | None:
    if not manifest:
        return None
    deltas = manifest.get("wramDelta", {})
    values = [deltas.get("camera_x"), deltas.get("layer1_x")]
    return max((int(value) for value in values if value is not None), default=None)


def checks(
    baseline: dict[str, Any], candidate: dict[str, Any],
    baseline_manifest: dict[str, Any] | None, candidate_manifest: dict[str, Any] | None,
    min_windows: int, min_scroll: int,
) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []

    def add(name: str, passed: bool, category: str, detail: str) -> None:
        results.append({"name": name, "passed": bool(passed), "category": category, "detail": detail})

    for label, value in (("baseline", baseline), ("candidate", candidate)):
        add(f"{label}.fullWindows", value["windowCount"] >= min_windows, "correctness",
            f"{value['windowCount']} selected; require >= {min_windows}")
        hz = value["runFrames"]["hz"]
        add(f"{label}.runFrameHz", 59.5 <= hz <= 60.5, "correctness", f"{hz:.3f} Hz; require 59.5..60.5")
        orphan = value["runFrames"]["orphan"]
        add(f"{label}.orphanFrames", orphan == 0, "correctness", f"{orphan}; require 0")
        add(f"{label}.stableUnitySettings", len(value["unitySettings"]) == 1, "correctness",
            f"observed {value['unitySettings']}")
    add("sameUnitySettings", baseline["unitySettings"] == candidate["unitySettings"], "correctness",
        f"baseline={baseline['unitySettings']} candidate={candidate['unitySettings']}")

    if baseline_manifest and candidate_manifest:
        for key in ("rom", "state"):
            left = baseline_manifest.get(key, {}).get("sha256")
            right = candidate_manifest.get(key, {}).get("sha256")
            add(f"same{key.title()}Hash", bool(left) and left == right, "correctness", f"baseline={left} candidate={right}")
        for label, manifest in (("baseline", baseline_manifest), ("candidate", candidate_manifest)):
            moved = scroll_delta(manifest)
            add(f"{label}.scrollProof", moved is not None and moved >= min_scroll, "correctness",
                f"max(camera_x, layer1_x) delta={moved}; require >= {min_scroll}px")
            elapsed = float(manifest.get("warmupSeconds", 0)) + float(manifest.get("measurementSeconds", 0))
            frame_delta = int(manifest.get("frameDelta", -1))
            progress_hz = frame_delta / elapsed if elapsed > 0 else 0.0
            add(f"{label}.realtimeFrameProgress", 59.5 <= progress_hz <= 60.5, "correctness",
                f"{frame_delta} frames / {elapsed:.3f}s = {progress_hz:.3f} Hz; require 59.5..60.5")

    update_ratio = candidate["updates"]["hz"] / baseline["updates"]["hz"] if baseline["updates"]["hz"] else 0.0
    avg_ratio = candidate["updates"]["avgMs"] / baseline["updates"]["avgMs"] if baseline["updates"]["avgMs"] else math.inf
    share_delta = candidate["runFrames"]["twoPlusShare"] - baseline["runFrames"]["twoPlusShare"]
    add("updateHzNoRegression", update_ratio >= 0.98, "no-regression",
        f"ratio={update_ratio:.4f}; require >= 0.98")
    add("updateAvgNoRegression", avg_ratio <= 1.02, "no-regression",
        f"ratio={avg_ratio:.4f}; require <= 1.02")
    add("twoPlusShareNoRegression", share_delta <= 0.02, "no-regression",
        f"candidate-baseline={share_delta:.4f}; require <= 0.02")
    improved = update_ratio >= 1.05 or avg_ratio <= 0.95
    add("materialImprovement", improved, "improvement",
        f"updateHz ratio={update_ratio:.4f}, updateAvg ratio={avg_ratio:.4f}; require >=1.05 or <=0.95")
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare CadenceCounter sessions or paced-run manifests.")
    parser.add_argument("--baseline", required=True, help="Baseline manifest.json, session folder, or windows.jsonl.")
    parser.add_argument("--candidate", required=True, help="Candidate manifest.json, session folder, or windows.jsonl.")
    parser.add_argument("--min-windows", type=int, default=4)
    parser.add_argument("--min-scroll-pixels", type=int, default=64)
    parser.add_argument("--output", help="Optional JSON report path.")
    parser.add_argument("--enforce", action="store_true", help="Exit nonzero on correctness or no-regression failure.")
    parser.add_argument("--require-improvement", action="store_true", help="Also fail unless the 5%% improvement gate passes.")
    args = parser.parse_args()
    try:
        if args.min_windows < 1 or args.min_scroll_pixels < 0:
            raise ComparisonError("--min-windows must be positive and --min-scroll-pixels cannot be negative")
        baseline_input = resolve_input(Path(args.baseline))
        candidate_input = resolve_input(Path(args.candidate))
        baseline_rows, baseline_total = load_windows(baseline_input)
        candidate_rows, candidate_total = load_windows(candidate_input)
        baseline = aggregate(baseline_rows, baseline_total)
        candidate = aggregate(candidate_rows, candidate_total)
        results = checks(
            baseline, candidate, baseline_input["manifest"], candidate_input["manifest"],
            args.min_windows, args.min_scroll_pixels,
        )
        report = {
            "schema": 1,
            "selection": "reason=interval; fully contained wall-clock windows when manifests are supplied",
            "baseline": {"source": baseline_input["source"], **baseline},
            "candidate": {"source": candidate_input["source"], **candidate},
            "deltaPercent": {
                "updateHz": percentage(candidate["updates"]["hz"], baseline["updates"]["hz"]),
                "updateAvgMs": percentage(candidate["updates"]["avgMs"], baseline["updates"]["avgMs"]),
                "updateCadenceAvgMs": percentage(candidate["updates"]["cadenceAvgMs"], baseline["updates"]["cadenceAvgMs"]),
                "runFrameHz": percentage(candidate["runFrames"]["hz"], baseline["runFrames"]["hz"]),
            },
            "checks": results,
        }
        rendered = json.dumps(report, indent=2) + "\n"
        if args.output:
            output = Path(args.output).resolve()
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(rendered, encoding="utf-8")
        print(rendered, end="")
        failures = [item for item in results if not item["passed"]]
        enforced_categories = {"correctness", "no-regression"} if args.enforce else set()
        if args.require_improvement:
            enforced_categories.add("improvement")
        return 2 if any(item["category"] in enforced_categories for item in failures) else 0
    except (ComparisonError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
