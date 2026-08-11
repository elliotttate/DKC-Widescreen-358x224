#!/usr/bin/env python3
"""Run a wall-clock-paced DKC scrolling sample through the existing bridge.

This deliberately uses schedule + resume. It never calls run_frames or step_frames,
because either would bypass the real Unity/host cadence this test is meant to measure.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from dkc_level_cli import BridgeError, endpoint_path, request


DEFAULT_RECIPE = Path(__file__).resolve().parent.parent / "recipes" / "realtime-jungle-right-y-cadence.json"
DEFAULT_CADENCE_ROOT = Path(os.environ.get("SUPERZSNES_ROOT", ".deps/SuperZSNES")) / "BepInEx" / "plugins" / "SuperZSNESCadenceCounter"


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-.") or "sample"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_recipe(path: Path) -> dict[str, Any]:
    document = json.loads(path.read_text(encoding="utf-8"))
    required = ("name", "macro", "warmupSeconds", "measurementSeconds", "watches")
    if document.get("schema") != 1 or any(key not in document for key in required):
        raise BridgeError(f"{path}: invalid real-time scrolling recipe")
    if "run_frames" in json.dumps(document) or "step_frames" in json.dumps(document):
        raise BridgeError(f"{path}: paced recipes cannot contain exact-frame commands")
    if float(document["warmupSeconds"]) < 0 or float(document["measurementSeconds"]) <= 0:
        raise BridgeError("warmupSeconds must be nonnegative and measurementSeconds must be positive")
    if not isinstance(document["watches"], list) or not document["watches"]:
        raise BridgeError("recipe watches must be a non-empty list")
    return document


def newest_cadence_file(root: Path) -> Path | None:
    candidates = list(root.glob("session-*/windows.jsonl")) if root.is_dir() else []
    return max(candidates, key=lambda item: item.stat().st_mtime_ns) if candidates else None


def wait_wall_clock(seconds: float) -> None:
    deadline = time.monotonic() + seconds
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return
        time.sleep(min(0.25, remaining))


def read_watches(endpoint: Path, watches: list[dict[str, Any]], timeout: float) -> dict[str, Any]:
    values: dict[str, Any] = {}
    for watch in watches:
        result = request(endpoint, "read_wram", {
            "address": watch["address"],
            "size": int(watch["size"]),
            "signed": bool(watch.get("signed", False)),
        }, timeout)
        values[watch["name"]] = {
            "address": result["address"],
            "size": result["size"],
            "signed": bool(watch.get("signed", False)),
            "value": int(result["value"]),
            "valueHex": result["valueHex"],
            "units": watch.get("units"),
        }
    return values


def delta(start: dict[str, Any], end: dict[str, Any], name: str) -> int | None:
    if name not in start or name not in end:
        return None
    return int(end[name]["value"]) - int(start[name]["value"])


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run a real-time (not exact-stepped) Jungle RIGHT+Y cadence sample."
    )
    parser.add_argument("--rom", required=True, help="ROM used for both A/B runs; use the same immutable file.")
    parser.add_argument("--state", required=True, help="Clean Jungle entry save-state file.")
    parser.add_argument("--recipe", default=str(DEFAULT_RECIPE))
    parser.add_argument("--endpoint", help="Path to DKCLevelAutomation bridge.json.")
    parser.add_argument("--cadence-root", default=str(DEFAULT_CADENCE_ROOT))
    parser.add_argument("--label", required=True, help="Short condition label, e.g. baseline or candidate.")
    parser.add_argument("--output-root", default=str(Path.cwd() / "RealtimeScrollRuns"))
    parser.add_argument("--warmup-seconds", type=float, help="Override recipe warmup (default 7 seconds).")
    parser.add_argument("--measurement-seconds", type=float, help="Override recipe measurement (default 30 seconds).")
    parser.add_argument("--socket-timeout", type=float, default=30.0)
    parser.add_argument("--validate-only", action="store_true", help="Validate inputs without connecting to SuperZSNES.")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        recipe_path = Path(args.recipe).resolve()
        recipe = load_recipe(recipe_path)
        rom = Path(args.rom).resolve()
        state = Path(args.state).resolve()
        if not rom.is_file() or not state.is_file():
            raise BridgeError(f"ROM/state must both exist: rom={rom}, state={state}")
        warmup = float(args.warmup_seconds if args.warmup_seconds is not None else recipe["warmupSeconds"])
        measurement = float(args.measurement_seconds if args.measurement_seconds is not None else recipe["measurementSeconds"])
        if warmup < 0 or measurement <= 0:
            raise BridgeError("warmup must be nonnegative and measurement must be positive")
        if args.validate_only:
            print(json.dumps({
                "valid": True,
                "recipe": str(recipe_path),
                "rom": str(rom),
                "state": str(state),
                "pacingMode": "schedule-plus-resume",
                "exactFrameStepping": False,
                "warmupSeconds": warmup,
                "measurementSeconds": measurement,
            }, indent=2))
            return 0

        endpoint = endpoint_path(args.endpoint)
        cadence_before = newest_cadence_file(Path(args.cadence_root).resolve())
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output_root).resolve() / f"{stamp}-{safe_name(args.label)}"
        output.mkdir(parents=True, exist_ok=False)
        manifest: dict[str, Any] = {
            "schema": 1,
            "kind": "realtime-jungle-scroll-cadence",
            "label": args.label,
            "recipe": str(recipe_path),
            "pacingMode": "schedule-plus-resume",
            "exactFrameStepping": False,
            "macro": recipe["macro"],
            "controller": int(recipe.get("controller", 1)),
            "warmupSeconds": warmup,
            "measurementSeconds": measurement,
            "rom": {"path": str(rom), "sha256": sha256(rom)},
            "state": {"path": str(state), "sha256": sha256(state)},
            "automationEndpoint": str(endpoint),
            "cadenceFileBefore": str(cadence_before) if cadence_before else None,
            "startedUtc": utc_now(),
            "complete": False,
        }
        manifest_path = output / "manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

        resumed = False
        try:
            request(endpoint, "load_rom", {"path": str(rom), "load_last_state": False}, args.socket_timeout)
            request(endpoint, "load_state_file", {"path": str(state)}, args.socket_timeout)
            request(endpoint, "schedule", {
                "controller": int(recipe.get("controller", 1)),
                "macro": recipe["macro"],
            }, args.socket_timeout)
            manifest["startStatus"] = request(endpoint, "status", {}, args.socket_timeout)
            manifest["startWram"] = read_watches(endpoint, recipe["watches"], args.socket_timeout)

            request(endpoint, "resume", {}, args.socket_timeout)
            resumed = True
            manifest["resumedUtc"] = utc_now()
            wait_wall_clock(warmup)
            manifest["measurementStartUtc"] = utc_now()
            wait_wall_clock(measurement)
            manifest["measurementEndUtc"] = utc_now()
            request(endpoint, "pause", {}, args.socket_timeout)
            resumed = False

            manifest["endStatus"] = request(endpoint, "status", {}, args.socket_timeout)
            manifest["endWram"] = read_watches(endpoint, recipe["watches"], args.socket_timeout)
            manifest["frameDelta"] = int(manifest["endStatus"]["frame"]) - int(manifest["startStatus"]["frame"])
            manifest["wramDelta"] = {
                name: delta(manifest["startWram"], manifest["endWram"], name)
                for name in ("camera_x", "camera_y", "layer1_x", "layer1_y")
            }
            cadence_after = newest_cadence_file(Path(args.cadence_root).resolve())
            manifest["cadenceWindows"] = str(cadence_after) if cadence_after else None
            manifest["finishedUtc"] = utc_now()
            manifest["complete"] = True
        finally:
            if resumed:
                try:
                    request(endpoint, "pause", {}, args.socket_timeout)
                except Exception as pause_error:
                    manifest["pauseError"] = str(pause_error)
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

        print(str(manifest_path))
        return 0
    except (BridgeError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
