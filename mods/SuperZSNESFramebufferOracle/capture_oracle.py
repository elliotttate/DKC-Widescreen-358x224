#!/usr/bin/env python3
"""Capture exact-frame stock/candidate renderer oracle runs from paused SuperZSNES."""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from bridge_client import BridgeClient, BridgeError, resolve_endpoint
from oracle_common import (
    OracleError,
    TOOL_VERSION,
    file_record,
    load_recipe,
    parse_state_mappings,
    sha256_file,
    write_json,
)


HERE = Path(__file__).resolve().parent


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def status_frame(status: Any, label: str) -> int:
    if not isinstance(status, dict) or not isinstance(status.get("frame"), int):
        raise OracleError(f"{label} did not return an integer emulator frame: {status!r}")
    return int(status["frame"])


def capture_checkpoint(
    automation: BridgeClient,
    debugger: BridgeClient,
    output_root: Path,
    case: dict[str, Any],
    relative_frame: int,
    reason_prefix: str,
) -> dict[str, Any]:
    automation_status = automation.request("status", {})
    automation_frame = status_frame(automation_status, "Automation status")
    capture_result = debugger.request(
        "capture", {"reason": f"{reason_prefix}-{case['id']}-f{relative_frame:06d}"}
    )
    if not isinstance(capture_result, dict):
        raise OracleError(f"Debugger capture returned an unexpected result: {capture_result!r}")
    debugger_frame = int(capture_result.get("frame", -1))
    source_capture_value = capture_result.get("path")
    if not isinstance(source_capture_value, str) or not source_capture_value.strip():
        raise OracleError(f"Debugger capture omitted its output path: {capture_result!r}")
    source_capture = Path(source_capture_value).resolve()
    if not source_capture.is_dir():
        raise OracleError(f"Debugger capture folder does not exist: {source_capture}")
    if not (source_capture / "capture.json").is_file():
        raise OracleError(f"Debugger output is not a full capture folder: {source_capture}")

    checkpoint_directory = output_root / "cases" / case["id"] / f"f{relative_frame:06d}"
    raw_directory = checkpoint_directory / "raw"
    checkpoint_directory.mkdir(parents=True, exist_ok=False)
    shutil.copytree(source_capture, raw_directory)
    ppu_bridge_state = debugger.request("get_ppu_state", {})
    write_json(checkpoint_directory / "bridge-ppu-state.json", ppu_bridge_state)

    image_path: Path | None = None
    image_name: str | None = None
    for candidate in case.get("imagePreference", ["frame-composed.png", "frame-main.png"]):
        path = raw_directory / candidate
        if path.is_file():
            image_path = path
            image_name = candidate
            break
    if image_path is None:
        raise OracleError(
            f"Capture {source_capture} has neither requested framebuffer image: "
            + ", ".join(case.get("imagePreference", []))
        )

    try:
        from PIL import Image

        with Image.open(image_path) as image:
            dimensions = {"width": image.width, "height": image.height, "mode": image.mode}
    except ImportError as error:
        raise OracleError("Pillow is required; run: python -m pip install -r requirements.txt") from error

    raw_files: dict[str, Any] = {}
    for path in sorted(raw_directory.rglob("*")):
        if path.is_file():
            raw_files[path.relative_to(raw_directory).as_posix()] = file_record(path, output_root)

    result = {
        "relativeFrame": relative_frame,
        "emulatorFrame": automation_frame,
        "debuggerFrame": debugger_frame,
        "frameAgreement": automation_frame == debugger_frame,
        "automationStatus": automation_status,
        "sourceCapturePath": str(source_capture),
        "captureDirectory": raw_directory.relative_to(output_root).as_posix(),
        "imageTarget": image_name,
        "image": {**file_record(image_path, output_root), **dimensions},
        "rawFiles": raw_files,
        "bridgePpuState": file_record(checkpoint_directory / "bridge-ppu-state.json", output_root),
    }
    if automation_frame != debugger_frame:
        raise OracleError(
            f"Exact-frame capture disagreement for {case['id']} at relative frame {relative_frame}: "
            f"automation={automation_frame}, debugger={debugger_frame}."
        )
    return result


def run_capture(args: argparse.Namespace) -> int:
    recipe_path = Path(args.recipe).resolve()
    recipe = load_recipe(recipe_path)
    rom = Path(args.rom).resolve()
    if not rom.is_file():
        raise OracleError(f"ROM does not exist: {rom}")
    state_paths = parse_state_mappings(args.state)
    selected = set(args.case or [])
    known = {case["id"] for case in recipe["cases"]}
    unknown = selected - known
    if unknown:
        raise OracleError("Unknown case id(s): " + ", ".join(sorted(unknown)))
    cases = [case for case in recipe["cases"] if not selected or case["id"] in selected]
    for case in cases:
        state_key = case["stateKey"]
        if state_key not in state_paths:
            raise OracleError(f"Case {case['id']!r} requires --state {state_key}=PATH")
        if not state_paths[state_key].is_file():
            raise OracleError(f"State for {state_key!r} does not exist: {state_paths[state_key]}")

    output_root = Path(args.output).resolve()
    if output_root.exists():
        raise OracleError(f"Output path already exists; refusing to overwrite: {output_root}")
    output_root.mkdir(parents=True)

    automation_endpoint = resolve_endpoint(
        args.automation_endpoint,
        "SUPERZSNES_DKC_AUTOMATION_ENDPOINT",
        (HERE.parent / "DKCLevelAutomation" / "bridge.json",),
    )
    debugger_endpoint = resolve_endpoint(
        args.debugger_endpoint,
        "SUPERZSNES_DKC_DEBUGGER_ENDPOINT",
        (HERE.parent / "DKCWidescreenDebugger" / "bridge.json",),
    )
    automation = BridgeClient(automation_endpoint, args.socket_timeout)
    debugger = BridgeClient(debugger_endpoint, args.socket_timeout)

    manifest: dict[str, Any] = {
        "schemaVersion": 1,
        "tool": "SuperZSNESFramebufferOracle",
        "toolVersion": TOOL_VERSION,
        "createdUtc": utc_now(),
        "completed": False,
        "variant": args.variant,
        "exactFrameStepping": True,
        "suiteId": recipe["suiteId"],
        "recipe": {"path": str(recipe_path), "sha256": sha256_file(recipe_path)},
        "rom": {"path": str(rom), "sha256": sha256_file(rom)},
        "endpoints": {
            "automation": str(automation_endpoint),
            "debugger": str(debugger_endpoint),
        },
        "cases": [],
    }
    write_json(output_root / "manifest.json", manifest)

    try:
        for case in cases:
            state_path = state_paths[case["stateKey"]]
            case_manifest: dict[str, Any] = {
                "id": case["id"],
                "description": case.get("description", ""),
                "stateKey": case["stateKey"],
                "state": {"path": str(state_path), "sha256": sha256_file(state_path)},
                "controller": case.get("controller", 1),
                "macro": case["macro"],
                "checkpoints": [],
            }
            manifest["cases"].append(case_manifest)
            write_json(output_root / "manifest.json", manifest)

            automation.request("load_rom", {"path": str(rom), "load_last_state": False})
            automation.request("load_state_file", {"path": str(state_path)})
            automation.request(
                "schedule",
                {"controller": case_manifest["controller"], "macro": case_manifest["macro"]},
            )

            previous = 0
            for relative_frame in case["checkpoints"]:
                delta = relative_frame - previous
                if delta:
                    automation.request("run_frames", {"count": delta})
                checkpoint = capture_checkpoint(
                    automation,
                    debugger,
                    output_root,
                    case,
                    relative_frame,
                    args.reason_prefix,
                )
                case_manifest["checkpoints"].append(checkpoint)
                write_json(output_root / "manifest.json", manifest)
                previous = relative_frame
            automation.request("clear_schedule", {"controller": "all"})
        manifest["completed"] = True
        manifest["completedUtc"] = utc_now()
        write_json(output_root / "manifest.json", manifest)
    except Exception as error:
        manifest["error"] = str(error)
        manifest["failedUtc"] = utc_now()
        write_json(output_root / "manifest.json", manifest)
        raise
    finally:
        try:
            automation.request("clear_schedule", {"controller": "all"})
        except Exception:
            pass
        try:
            automation.request("pause", {})
        except Exception:
            pass

    print(json.dumps({"ok": True, "manifest": str(output_root / "manifest.json")}, indent=2))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Capture exact-frame framebuffer and raw-PPU oracle artifacts."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate = subparsers.add_parser("validate", help="Validate a recipe without contacting SuperZSNES.")
    validate.add_argument("--recipe", required=True)

    capture = subparsers.add_parser("capture", help="Load ROM/states and capture an oracle run.")
    capture.add_argument("--recipe", required=True)
    capture.add_argument("--variant", required=True, help="Run label such as stock or candidate-v1.")
    capture.add_argument("--rom", required=True, help="Explicit ROM path.")
    capture.add_argument(
        "--state", action="append", default=[], metavar="KEY=PATH",
        help="Explicit state path for a recipe stateKey; repeat for multiple cases.",
    )
    capture.add_argument("--case", action="append", help="Capture only this case id; repeat as needed.")
    capture.add_argument("--output", required=True, help="New output directory; never overwritten.")
    capture.add_argument("--automation-endpoint", help="DKCLevelAutomation bridge.json path.")
    capture.add_argument("--debugger-endpoint", help="DKCWidescreenDebugger bridge.json path.")
    capture.add_argument("--socket-timeout", type=float, default=190.0)
    capture.add_argument("--reason-prefix", default="framebuffer-oracle")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "validate":
            recipe_path = Path(args.recipe).resolve()
            recipe = load_recipe(recipe_path)
            print(json.dumps({"ok": True, "suiteId": recipe["suiteId"], "cases": len(recipe["cases"])}, indent=2))
            return 0
        return run_capture(args)
    except (OracleError, BridgeError, OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
