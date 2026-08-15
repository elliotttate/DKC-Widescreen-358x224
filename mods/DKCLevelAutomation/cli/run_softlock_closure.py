#!/usr/bin/env python3
"""Repeat the proven Croctopus and Poison Pond softlock traversal routes."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from dkc_level_cli import BridgeError, endpoint_path, request


WRAM_SIZE = 0x20000
EXPECTED_STATES = {
    "croctopus": "28512346694F4D09FE1C9E4F08393A7C9BA029876F10DD5D265CA810E48F49BD",
    "poison": "08AC2F357A72AA612E86B27FABE97DF338010D34DFED1F5F9D7AA3C1B2313B38",
    "slipslide": "6C310895C7CE0E0A7DD2A8E2B3CCFF815081B9FA5F9581BC730DC9D7641C65A0",
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def pulse(start: int, count: int, direction: str) -> list[str]:
    result: list[str] = []
    for offset in range(0, count, 8):
        first = start + offset
        result.append(f"{first}=B+{direction}")
        if offset + 1 < count:
            last = min(start + count - 1, first + 7)
            result.append(f"{first + 1}-{last}={direction}")
    return result


def croctopus_macro() -> str:
    parts = ["0-119=B+RIGHT"]
    parts += pulse(120, 240, "UP")
    parts += pulse(360, 180, "UP+RIGHT")
    parts.append("540-779=B+RIGHT")
    parts += pulse(780, 240, "DOWN+RIGHT")
    parts += pulse(1020, 300, "UP+RIGHT")
    parts += ["1320-1619=B+DOWN", "1620-1859=B+RIGHT"]
    return ";".join(parts)


def poison_macro() -> str:
    return "0-119=RIGHT+Y;120-239=DOWN+Y;240-419=DOWN+RIGHT+Y;420-899=UP+RIGHT+Y;900-1499=RIGHT+Y"


CASES = {
    "croctopus": {
        "level": 0x25,
        "entrance": 0x3E,
        "macro": croctopus_macro(),
        "checkpoints": [540, 780, 1020, 1320, 1380, 1620, 1860],
    },
    "poison": {
        "level": 0x17,
        "entrance": 0x22,
        "macro": poison_macro(),
        "checkpoints": [120, 240, 420, 900, 1500],
    },
    "slipslide": {
        "level": 0x51,
        "entrance": 0x6D,
        "checkpoints": [1],
    },
}


def u16(memory: bytes, offset: int) -> int:
    return memory[offset] | memory[offset + 1] << 8


def actors(memory: bytes) -> list[dict[str, int]]:
    result: list[dict[str, int]] = []
    for raw in range(2, 0x34, 2):
        actor_id = u16(memory, 0x0D45 + raw)
        if actor_id == 0:
            continue
        result.append({
            "rawIndex": raw,
            "id": actor_id,
            "x": u16(memory, 0x0B19 + raw),
            "y": u16(memory, 0x0BC1 + raw),
            "state": u16(memory, 0x1029 + raw),
            "source": u16(memory, 0x15FD + raw),
        })
    return result


def summarize(memory: bytes) -> dict[str, Any]:
    return {
        "level": u16(memory, 0x0030),
        "entrance": u16(memory, 0x003E),
        "gameMode": u16(memory, 0x002E),
        "cameraX": u16(memory, 0x1A62),
        "layer1X": u16(memory, 0x088B),
        "primaryCursor": u16(memory, 0x00A0),
        "secondaryCursor": u16(memory, 0x00A2),
        "objectEnd": u16(memory, 0x00A4),
        "sectionState": u16(memory, 0x1E03),
        "sectionPointer": u16(memory, 0x1E05),
        "sectionCurrent": u16(memory, 0x1E07),
        "sectionPending": u16(memory, 0x1E09),
        "sectionLimit": u16(memory, 0x1E0B),
        "sectionPendingEnd": u16(memory, 0x1E0D),
        "lives": u16(memory, 0x0575),
        "actors": actors(memory),
    }


def require_actor(summary: dict[str, Any], actor_id: int, source: int, label: str) -> None:
    if not any(row["id"] == actor_id and abs(row["source"] if row["source"] < 0x8000 else row["source"] - 0x10000) == source
               for row in summary["actors"]):
        raise BridgeError(f"{label}: actor ${actor_id:02X} from source ${source:02X} is not active")


def assert_checkpoint(case_id: str, relative: int, summary: dict[str, Any]) -> None:
    spec = CASES[case_id]
    if summary["level"] != spec["level"] or summary["entrance"] != spec["entrance"]:
        raise BridgeError(
            f"{case_id} f{relative}: expected level/entrance ${spec['level']:02X}/${spec['entrance']:02X}, "
            f"got ${summary['level']:02X}/${summary['entrance']:02X}"
        )
    if summary["lives"] < 1:
        raise BridgeError(f"{case_id} f{relative}: route lost all lives")
    if case_id == "croctopus" and relative == 540:
        if summary["cameraX"] < 0x0155:
            raise BridgeError(f"croctopus f540: camera did not reach $0155 (got ${summary['cameraX']:04X})")
        require_actor(summary, 0x5D, 0x11, "croctopus f540 critical camera object")
        require_actor(summary, 0x26, 0x12, "croctopus f540 dependent object")
    if case_id == "croctopus" and relative == 1860:
        if summary["cameraX"] < 0x01DF or summary["objectEnd"] < 0x0013:
            raise BridgeError("croctopus final scanner/camera frontier was not reached")
        require_actor(summary, 0x5D, 0x11, "croctopus final critical camera object")
    if case_id == "poison" and relative == 1500:
        if summary["cameraX"] < 0x06E7 or summary["primaryCursor"] < 0x0045 or summary["objectEnd"] < 0x0048:
            raise BridgeError("poison final scanner/camera frontier was not reached")
        require_actor(summary, 0x44, 0x45, "poison critical object")
        require_actor(summary, 0x5D, 0x46, "poison later camera object")
    if case_id == "slipslide" and relative == 1:
        expected = (summary["secondaryCursor"], summary["objectEnd"], summary["sectionPointer"], summary["sectionPendingEnd"])
        if expected != (0x0025, 0x002C, 0xD9A0, 0x002C):
            raise BridgeError(
                "slipslide transition did not select secondary $25..$2C / descriptor $D9A0: "
                + ",".join(f"${value:04X}" for value in expected)
            )


def snapshot(endpoint: Path, timeout: float) -> tuple[dict[str, Any], bytes]:
    raw = request(endpoint, "snapshot_wram", {}, timeout)
    memory = base64.b64decode(raw["data"], validate=True)
    if len(memory) != WRAM_SIZE:
        raise BridgeError(f"snapshot_wram returned {len(memory)} bytes, expected {WRAM_SIZE}")
    digest = hashlib.sha256(memory).hexdigest().upper()
    if digest != str(raw["sha256"]).upper():
        raise BridgeError("snapshot_wram digest mismatch")
    return raw, memory


def run_case(endpoint: Path, rom: Path, state: Path, case_id: str, repeat: int,
             output: Path, timeout: float) -> dict[str, Any]:
    spec = CASES[case_id]
    request(endpoint, "load_rom", {"path": rom, "load_last_state": False}, timeout)
    request(endpoint, "load_state_file", {"path": state}, timeout)
    if case_id == "slipslide":
        root_raw, root_memory = snapshot(endpoint, timeout)
        root_summary = summarize(root_memory)
        assert_checkpoint(case_id, 0, root_summary)
        case_dir = output / case_id / f"repeat-{repeat:02d}"
        case_dir.mkdir(parents=True, exist_ok=True)
        (case_dir / "f0000.wram.bin").write_bytes(root_memory)
        request(endpoint, "write_wram", {"address": "0x7E0895", "size": 2, "value": "0x08C0"}, timeout)
        request(endpoint, "run_frames", {"count": 1, "timeout_ms": 175000}, timeout)
        raw, memory = snapshot(endpoint, timeout)
        summary = summarize(memory)
        assert_checkpoint(case_id, 1, summary)
        (case_dir / "f0001.wram.bin").write_bytes(memory)
        return {"case": case_id, "repeat": repeat, "checkpoints": [
            {"relativeFrame": 0, "emulatorFrame": root_raw["frame"], "sha256": root_raw["sha256"], "summary": root_summary},
            {"relativeFrame": 1, "emulatorFrame": raw["frame"], "sha256": raw["sha256"], "summary": summary},
        ]}
    request(endpoint, "run_frames", {"count": 1, "timeout_ms": 175000}, timeout)
    root_raw, root_memory = snapshot(endpoint, timeout)
    root_summary = summarize(root_memory)
    assert_checkpoint(case_id, 0, root_summary)
    case_dir = output / case_id / f"repeat-{repeat:02d}"
    case_dir.mkdir(parents=True, exist_ok=True)
    (case_dir / "f0000.wram.bin").write_bytes(root_memory)
    points: list[dict[str, Any]] = [{
        "relativeFrame": 0, "emulatorFrame": root_raw["frame"],
        "sha256": root_raw["sha256"], "summary": root_summary,
    }]
    request(endpoint, "schedule", {"controller": 1, "macro": spec["macro"]}, timeout)
    previous = 0
    for relative in spec["checkpoints"]:
        request(endpoint, "run_frames", {"count": relative - previous, "timeout_ms": 175000}, timeout)
        raw, memory = snapshot(endpoint, timeout)
        summary = summarize(memory)
        assert_checkpoint(case_id, relative, summary)
        (case_dir / f"f{relative:04d}.wram.bin").write_bytes(memory)
        points.append({
            "relativeFrame": relative, "emulatorFrame": raw["frame"],
            "sha256": raw["sha256"], "summary": summary,
        })
        previous = relative
    request(endpoint, "clear_schedule", {"controller": "all"}, timeout)
    return {"case": case_id, "repeat": repeat, "checkpoints": points}


def verify_repeats(runs: list[dict[str, Any]]) -> None:
    grouped: dict[str, list[dict[str, Any]]] = {}
    for run in runs:
        grouped.setdefault(run["case"], []).append(run)
    for case_id, items in grouped.items():
        oracle = [(p["relativeFrame"], p["sha256"]) for p in items[0]["checkpoints"]]
        for item in items[1:]:
            observed = [(p["relativeFrame"], p["sha256"]) for p in item["checkpoints"]]
            if observed != oracle:
                raise BridgeError(f"{case_id}: repeat {item['repeat']} did not reproduce identical full-WRAM hashes")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom", required=True)
    parser.add_argument("--state0", required=True, help="immutable Croctopus Chase report state")
    parser.add_argument("--state1", required=True, help="immutable Poison Pond report state")
    parser.add_argument("--state5", required=True, help="immutable Slipslide Ride report state")
    parser.add_argument("--case", choices=["all", *CASES], default="all")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--automation-endpoint")
    parser.add_argument("--output")
    parser.add_argument("--socket-timeout", type=float, default=240.0)
    parser.add_argument("--validate-only", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.repeats < 1 or args.repeats > 20:
            raise BridgeError("--repeats must be 1-20")
        rom = Path(args.rom).resolve()
        states = {
            "croctopus": Path(args.state0).resolve(),
            "poison": Path(args.state1).resolve(),
            "slipslide": Path(args.state5).resolve(),
        }
        selected = list(CASES) if args.case == "all" else [args.case]
        for case_id in selected:
            if not states[case_id].is_file():
                raise BridgeError(f"missing {case_id} state: {states[case_id]}")
            actual = sha256_file(states[case_id])
            if actual != EXPECTED_STATES[case_id]:
                raise BridgeError(f"{case_id} state hash mismatch: {actual}")
        if not rom.is_file():
            raise BridgeError(f"ROM not found: {rom}")
        plan = {
            "rom": str(rom), "romSha256": sha256_file(rom), "cases": selected,
            "repeats": args.repeats, "framesPerRepeat": {key: CASES[key]["checkpoints"][-1] + 1 for key in selected},
        }
        if args.validate_only:
            print(json.dumps(plan, indent=2))
            return 0
        endpoint = endpoint_path(args.automation_endpoint)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output).resolve() if args.output else Path(__file__).resolve().parent.parent / "ClosureRuns" / f"softlock-closure-{stamp}"
        output.mkdir(parents=True, exist_ok=True)
        runs: list[dict[str, Any]] = []
        try:
            for repeat in range(1, args.repeats + 1):
                for case_id in selected:
                    print(f"[{case_id}] repeat {repeat}/{args.repeats}", flush=True)
                    runs.append(run_case(endpoint, rom, states[case_id], case_id, repeat, output, args.socket_timeout))
            verify_repeats(runs)
        finally:
            try:
                request(endpoint, "clear_schedule", {"controller": "all"}, args.socket_timeout)
                request(endpoint, "load_state_file", {"path": states[selected[0]]}, args.socket_timeout)
                request(endpoint, "pause", {}, args.socket_timeout)
            except Exception as cleanup_error:
                print(f"cleanup warning: {cleanup_error}", file=sys.stderr)
        report = {
            "schema": 1,
            "createdUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            **plan,
            "stateSha256": {key: EXPECTED_STATES[key] for key in selected},
            "deterministicFullWram": True,
            "runs": runs,
        }
        (output / "report.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"ok": True, "output": str(output), "runs": len(runs), "deterministicFullWram": True}, indent=2))
        return 0
    except (BridgeError, OSError, ValueError, KeyError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
