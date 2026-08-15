#!/usr/bin/env python3
"""Replay external DKC save states against two ROMs and compare exact-frame state."""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from dkc_level_cli import BridgeError, endpoint_path, request


WRAM_SIZE = 0x20000
ACTOR_SLOTS = 26
SAFE = re.compile(r"[^A-Za-z0-9._-]+")

GLOBAL_FIELDS = {
    "game_mode": (0x002E, False),
    "level_id": (0x0030, False),
    "entrance_id": (0x003E, False),
    "held_p1": (0x0500, False),
    "pressed_p1": (0x0504, False),
    "screen_display": (0x051A, False),
    "current_kong": (0x056F, False),
    "gameplay_flags": (0x0579, False),
    "layer1_x": (0x088B, False),
    "layer1_y": (0x0895, False),
    "camera_y": (0x1A4C, False),
    "camera_x": (0x1A62, False),
    "camera_left_bound": (0x1B23, False),
    "camera_right_bound": (0x1B25, False),
}

ACTOR_TABLES = {
    "displayed_pose": 0x0AE5,
    "x": 0x0B19,
    "oam_z": 0x0B8D,
    "y": 0x0BC1,
    "current_pose": 0x0D11,
    "id": 0x0D45,
    "x_speed": 0x0E89,
    "y_speed": 0x0EF1,
    "state": 0x1029,
    "animation_id": 0x10D1,
}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_name(value: str) -> str:
    return SAFE.sub("-", value).strip("-.") or "item"


def macro_length(macro: str) -> int:
    maximum = -1
    for raw in re.split(r"[;,]", macro):
        segment = raw.strip()
        if not segment:
            continue
        if "=" not in segment:
            raise BridgeError(f"Invalid macro segment: {segment!r}")
        range_text = segment.split("=", 1)[0].strip()
        parts = range_text.split("-", 1)
        try:
            first = int(parts[0], 10)
            last = int(parts[1], 10) if len(parts) == 2 else first
        except ValueError as exc:
            raise BridgeError(f"Invalid macro frame range: {range_text!r}") from exc
        if first < 0 or last < first:
            raise BridgeError(f"Invalid macro frame range: {range_text!r}")
        maximum = max(maximum, last)
    if maximum < 0:
        raise BridgeError("A branch macro must contain at least one frame assignment.")
    return maximum + 1


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def read_u16(memory: bytes, offset: int) -> int:
    return memory[offset] | memory[offset + 1] << 8


def signed16(value: int) -> int:
    return value if value < 0x8000 else value - 0x10000


def summarize_wram(memory: bytes, sprite_names: dict[int, str] | None = None) -> dict[str, Any]:
    if len(memory) != WRAM_SIZE:
        raise BridgeError(f"Expected {WRAM_SIZE} WRAM bytes, received {len(memory)}.")
    sprite_names = sprite_names or {}
    globals_out = {
        name: (signed16(read_u16(memory, offset)) if signed else read_u16(memory, offset))
        for name, (offset, signed) in GLOBAL_FIELDS.items()
    }
    layer_x = globals_out["layer1_x"]
    layer_y = globals_out["layer1_y"]
    actors: list[dict[str, Any]] = []
    for slot in range(ACTOR_SLOTS):
        values = {name: read_u16(memory, base + slot * 2) for name, base in ACTOR_TABLES.items()}
        if values["id"] == 0:
            continue
        values["slot"] = slot
        values["name"] = sprite_names.get(values["id"], f"sprite_0x{values['id']:04X}")
        values["x_speed_signed"] = signed16(values["x_speed"])
        values["y_speed_signed"] = signed16(values["y_speed"])
        values["screen_x_native"] = signed16((values["x"] - layer_x) & 0xFFFF)
        values["screen_y_native"] = signed16((values["y"] - layer_y) & 0xFFFF)
        actors.append(values)
    return {"globals": globals_out, "actors": actors}


def actor_lifecycle(checkpoints: list[dict[str, Any]]) -> list[dict[str, Any]]:
    events: list[dict[str, Any]] = []
    for previous, current in zip(checkpoints, checkpoints[1:]):
        old = {actor["slot"]: actor for actor in previous["summary"]["actors"]}
        new = {actor["slot"]: actor for actor in current["summary"]["actors"]}
        changes: list[dict[str, Any]] = []
        for slot in range(ACTOR_SLOTS):
            before = old.get(slot)
            after = new.get(slot)
            before_id = before["id"] if before else 0
            after_id = after["id"] if after else 0
            if before_id == after_id:
                continue
            changes.append(
                {
                    "slot": slot,
                    "kind": "spawn" if before_id == 0 else "despawn" if after_id == 0 else "replace",
                    "before": before,
                    "after": after,
                }
            )
        events.append(
            {
                "fromRelativeFrame": previous["relativeFrame"],
                "toRelativeFrame": current["relativeFrame"],
                "changes": changes,
            }
        )
    return events


def changed_memory(baseline: bytes, candidate: bytes, range_limit: int = 128) -> dict[str, Any]:
    changed = [index for index, pair in enumerate(zip(baseline, candidate)) if pair[0] != pair[1]]
    ranges: list[dict[str, Any]] = []
    range_count = 0
    if changed:
        first = previous = changed[0]
        for offset in changed[1:] + [WRAM_SIZE + 1]:
            if offset != previous + 1:
                range_count += 1
                if len(ranges) < range_limit:
                    ranges.append(
                        {
                            "start": f"0x{0x7E0000 + first:06X}",
                            "end": f"0x{0x7E0000 + previous:06X}",
                            "bytes": previous - first + 1,
                        }
                    )
                first = offset
            previous = offset
    pages = Counter(index // 0x100 for index in changed)
    return {
        "changedByteCount": len(changed),
        "changedRanges": ranges,
        "changedRangeCount": range_count,
        "changedRangesTruncated": range_count > len(ranges),
        "topChangedPages": [
            {"start": f"0x{0x7E0000 + page * 0x100:06X}", "changedBytes": count}
            for page, count in pages.most_common(32)
        ],
    }


def compare_actors(baseline: list[dict[str, Any]], candidate: list[dict[str, Any]]) -> dict[str, Any]:
    base_by_id: dict[int, list[dict[str, Any]]] = defaultdict(list)
    candidate_by_id: dict[int, list[dict[str, Any]]] = defaultdict(list)
    for actor in baseline:
        base_by_id[actor["id"]].append(actor)
    for actor in candidate:
        candidate_by_id[actor["id"]].append(actor)
    matches: list[dict[str, Any]] = []
    missing: list[dict[str, Any]] = []
    extra: list[dict[str, Any]] = []
    for actor_id in sorted(set(base_by_id) | set(candidate_by_id)):
        available = list(candidate_by_id[actor_id])
        for base_actor in base_by_id[actor_id]:
            if not available:
                missing.append(base_actor)
                continue
            chosen = min(
                available,
                key=lambda actor: abs(signed16((actor["x"] - base_actor["x"]) & 0xFFFF))
                + abs(signed16((actor["y"] - base_actor["y"]) & 0xFFFF)),
            )
            available.remove(chosen)
            matches.append(
                {
                    "id": actor_id,
                    "name": base_actor["name"],
                    "baselineSlot": base_actor["slot"],
                    "candidateSlot": chosen["slot"],
                    "xDelta": signed16((chosen["x"] - base_actor["x"]) & 0xFFFF),
                    "yDelta": signed16((chosen["y"] - base_actor["y"]) & 0xFFFF),
                    "stateEqual": chosen["state"] == base_actor["state"],
                    "baselineState": base_actor["state"],
                    "candidateState": chosen["state"],
                }
            )
        extra.extend(available)
    return {"matched": matches, "missingFromCandidate": missing, "extraInCandidate": extra}


def debugger_endpoint_path(explicit: str | None, automation_endpoint: Path) -> Path | None:
    if explicit:
        path = Path(explicit).resolve()
        if not path.is_file():
            raise BridgeError(f"Debugger endpoint was not found: {path}")
        return path
    sibling = automation_endpoint.parent.parent / "DKCWidescreenDebugger" / "bridge.json"
    return sibling.resolve() if sibling.is_file() else None


def locate_recipe(value: str) -> Path:
    direct = Path(value)
    recipes = Path(__file__).resolve().parent.parent / "recipes"
    for candidate in (direct, recipes / value, recipes / f"{value}.json"):
        if candidate.is_file():
            return candidate.resolve()
    raise BridgeError(f"Differential recipe was not found: {value}")


def validate_recipe(document: Any, path: Path) -> dict[str, Any]:
    if not isinstance(document, dict) or document.get("schema") != 1:
        raise BridgeError(f"{path}: expected an object with schema 1.")
    states = document.get("states")
    if not isinstance(states, list) or not states:
        raise BridgeError(f"{path}: states must be a non-empty list.")
    state_ids: set[str] = set()
    for state in states:
        if not isinstance(state, dict) or not state.get("id") or not state.get("file"):
            raise BridgeError(f"{path}: every state needs id and file.")
        if state["id"] in state_ids:
            raise BridgeError(f"{path}: duplicate state id {state['id']}.")
        state_ids.add(state["id"])
        branches = state.get("branches")
        if not isinstance(branches, list) or not branches:
            raise BridgeError(f"{path}: state {state['id']} needs branches.")
        branch_ids: set[str] = set()
        for branch in branches:
            if not isinstance(branch, dict) or not branch.get("id") or not isinstance(branch.get("macro"), str):
                raise BridgeError(f"{path}: every branch needs id and macro.")
            if branch["id"] in branch_ids:
                raise BridgeError(f"{path}: duplicate branch {branch['id']} in state {state['id']}.")
            branch_ids.add(branch["id"])
            frames = branch.get("checkpoints")
            if not isinstance(frames, list) or not frames or frames[0] != 0:
                raise BridgeError(f"{path}: branch {branch['id']} checkpoints must start at 0.")
            if any(not isinstance(frame, int) or frame < 0 for frame in frames) or frames != sorted(set(frames)):
                raise BridgeError(f"{path}: branch {branch['id']} checkpoints must be sorted unique non-negative integers.")
            if frames[-1] > macro_length(branch["macro"]):
                raise BridgeError(
                    f"{path}: branch {branch['id']} ends at checkpoint {frames[-1]}, beyond its "
                    f"{macro_length(branch['macro'])}-frame schedule."
                )
            for evidence_key in ("screenshotFrames", "fullEvidenceFrames"):
                evidence_frames = branch.get(evidence_key, [])
                if not isinstance(evidence_frames, list) or any(frame not in frames for frame in evidence_frames):
                    raise BridgeError(f"{path}: branch {branch['id']} {evidence_key} must be checkpoint frames.")
    return document


class DifferentialRunner:
    def __init__(
        self,
        recipe: dict[str, Any],
        automation_endpoint: Path,
        debugger_endpoint: Path | None,
        output: Path,
        socket_timeout: float,
    ) -> None:
        self.recipe = recipe
        self.automation_endpoint = automation_endpoint
        self.debugger_endpoint = debugger_endpoint
        self.output = output
        self.socket_timeout = socket_timeout
        self.sprite_names = {
            int(key, 0) if isinstance(key, str) else int(key): str(value)
            for key, value in recipe.get("spriteNames", {}).items()
        }

    def automation(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        return request(self.automation_endpoint, command, arguments or {}, self.socket_timeout)

    def debugger(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        if self.debugger_endpoint is None:
            return None
        return request(self.debugger_endpoint, command, arguments or {}, self.socket_timeout)

    def checkpoint(
        self,
        variant: str,
        state: dict[str, Any],
        branch: dict[str, Any],
        relative_frame: int,
        folder: Path,
    ) -> dict[str, Any]:
        raw = self.automation("snapshot_wram")
        memory = base64.b64decode(raw["data"], validate=True)
        digest = hashlib.sha256(memory).hexdigest().upper()
        if digest != str(raw["sha256"]).upper():
            raise BridgeError(f"Atomic WRAM digest mismatch at {state['id']}/{branch['id']}/f{relative_frame}.")
        if not raw.get("paused"):
            raise BridgeError("Differential checkpoint was not paused; refusing timing-ambiguous evidence.")
        prefix = f"f{relative_frame:05d}"
        wram_path = folder / f"{prefix}-wram.bin.gz"
        with gzip.open(wram_path, "wb", compresslevel=6) as handle:
            handle.write(memory)
        screenshot_path = None
        if relative_frame in branch.get("screenshotFrames", []):
            shot = self.debugger("screenshot", {"target": "composed", "format": "png", "quality": 100})
            if shot:
                screenshot_path = folder / f"{prefix}-composed.png"
                screenshot_path.write_bytes(base64.b64decode(shot.pop("base64"), validate=True))
                shot["copiedPath"] = str(screenshot_path)
        else:
            shot = None
        full_capture = None
        if relative_frame in branch.get("fullEvidenceFrames", []):
            full_capture = self.debugger(
                "capture",
                {"reason": safe_name(f"diff-{variant}-{state['id']}-{branch['id']}-f{relative_frame}")},
            )
        summary = summarize_wram(memory, self.sprite_names)
        expected_level = state.get("expectedLevel")
        if expected_level is not None and summary["globals"]["level_id"] != int(str(expected_level), 0):
            raise BridgeError(
                f"State {state['id']} identified as level {expected_level}, but checkpoint contained "
                f"0x{summary['globals']['level_id']:04X}."
            )
        record = {
            "variant": variant,
            "state": state["id"],
            "branch": branch["id"],
            "relativeFrame": relative_frame,
            "emulatorFrame": raw["frame"],
            "wramSha256": digest,
            "wramPath": str(wram_path),
            "summary": summary,
            "screenshot": shot,
            "fullCapture": full_capture,
        }
        (folder / f"{prefix}.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
        record["_wram"] = memory
        return record

    def run_variant(self, variant: str, rom: Path, states: dict[str, Path]) -> dict[str, Any]:
        self.automation("load_rom", {"path": str(rom), "load_last_state": False})
        result: dict[str, Any] = {
            "rom": str(rom),
            "romSha256": sha256_file(rom),
            "states": {},
        }
        for state in self.recipe["states"]:
            state_path = states[state["id"]]
            state_result = {
                "path": str(state_path),
                "sha256": sha256_file(state_path),
                "identity": state.get("identity", ""),
                "branches": {},
            }
            result["states"][state["id"]] = state_result
            for branch in state["branches"]:
                print(f"{variant}: {state['id']} / {branch['id']}")
                self.automation("load_state_file", {"path": str(state_path)})
                self.automation("pause")
                self.automation("schedule", {"controller": 1, "macro": branch["macro"]})
                folder = self.output / variant / safe_name(state["id"]) / safe_name(branch["id"])
                folder.mkdir(parents=True, exist_ok=True)
                checkpoints: list[dict[str, Any]] = []
                previous = 0
                try:
                    for relative_frame in branch["checkpoints"]:
                        delta = relative_frame - previous
                        if delta:
                            self.automation(
                                "step_frames",
                                {"count": delta, "timeout_ms": int(branch.get("timeoutMs", 60000))},
                            )
                        checkpoints.append(self.checkpoint(variant, state, branch, relative_frame, folder))
                        previous = relative_frame
                finally:
                    self.automation("clear_schedule", {"controller": 1})
                state_result["branches"][branch["id"]] = {
                    "macro": branch["macro"],
                    "hypothesis": branch.get("hypothesis", ""),
                    "checkpoints": checkpoints,
                    "lifecycle": actor_lifecycle(checkpoints),
                }
        return result

    def compare(self, baseline: dict[str, Any], candidate: dict[str, Any]) -> dict[str, Any]:
        comparisons: list[dict[str, Any]] = []
        for state in self.recipe["states"]:
            for branch in state["branches"]:
                base_points = baseline["states"][state["id"]]["branches"][branch["id"]]["checkpoints"]
                candidate_points = candidate["states"][state["id"]]["branches"][branch["id"]]["checkpoints"]
                for base, changed in zip(base_points, candidate_points):
                    global_differences = {
                        name: {"baseline": base["summary"]["globals"][name], "candidate": changed["summary"]["globals"][name]}
                        for name in GLOBAL_FIELDS
                        if base["summary"]["globals"][name] != changed["summary"]["globals"][name]
                    }
                    actors = compare_actors(base["summary"]["actors"], changed["summary"]["actors"])
                    comparisons.append(
                        {
                            "state": state["id"],
                            "branch": branch["id"],
                            "relativeFrame": base["relativeFrame"],
                            "globalDifferences": global_differences,
                            "actors": actors,
                            "memory": changed_memory(base["_wram"], changed["_wram"]),
                            "hasActorPopulationDivergence": bool(actors["missingFromCandidate"] or actors["extraInCandidate"]),
                        }
                    )
        return {"checkpoints": comparisons}

    @staticmethod
    def strip_private(value: Any) -> Any:
        if isinstance(value, dict):
            return {key: DifferentialRunner.strip_private(item) for key, item in value.items() if not key.startswith("_")}
        if isinstance(value, list):
            return [DifferentialRunner.strip_private(item) for item in value]
        return value

    def run(self, baseline_rom: Path, candidate_rom: Path, states: dict[str, Path]) -> dict[str, Any]:
        self.output.mkdir(parents=True, exist_ok=True)
        started = utc_now()
        baseline = self.run_variant("baseline", baseline_rom, states)
        candidate = self.run_variant("candidate", candidate_rom, states)
        comparison = self.compare(baseline, candidate)
        document = self.strip_private(
            {
                "schema": 1,
                "recipe": self.recipe.get("name", "state-differential"),
                "startedUtc": started,
                "completedUtc": utc_now(),
                "automationEndpoint": str(self.automation_endpoint),
                "debuggerEndpoint": str(self.debugger_endpoint) if self.debugger_endpoint else None,
                "baseline": baseline,
                "candidate": candidate,
                "comparison": comparison,
            }
        )
        (self.output / "report.json").write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
        return document


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Replay external DKC states against clean and candidate ROMs without launching or stopping SuperZSNES."
    )
    parser.add_argument("--recipe", required=True, help="Recipe name or JSON path.")
    parser.add_argument("--baseline-rom", required=True)
    parser.add_argument("--candidate-rom", required=True)
    parser.add_argument("--state-dir", help="Directory containing the recipe's external state filenames.")
    parser.add_argument("--state", action="append", default=[], help="Override as STATE_ID=path.")
    parser.add_argument("--automation-endpoint")
    parser.add_argument("--debugger-endpoint")
    parser.add_argument("--no-debugger", action="store_true", help="Skip composed screenshots and full PPU/OAM captures.")
    parser.add_argument("--output", help="Output directory; defaults below DifferentialRuns.")
    parser.add_argument("--socket-timeout", type=float, default=190.0)
    parser.add_argument("--validate-only", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        recipe_path = locate_recipe(args.recipe)
        recipe = validate_recipe(json.loads(recipe_path.read_text(encoding="utf-8")), recipe_path)
        overrides: dict[str, Path] = {}
        for assignment in args.state:
            if "=" not in assignment:
                raise BridgeError("--state must use STATE_ID=path syntax.")
            key, value = assignment.split("=", 1)
            overrides[key] = Path(value).resolve()
        state_dir = Path(args.state_dir).resolve() if args.state_dir else None
        states = {
            state["id"]: overrides.get(state["id"], (state_dir / state["file"] if state_dir else Path(state["file"]).resolve()))
            for state in recipe["states"]
        }
        plan = {
            "ok": True,
            "recipe": recipe.get("name"),
            "states": {key: str(value) for key, value in states.items()},
            "branches": sum(len(state["branches"]) for state in recipe["states"]),
            "checkpointsPerVariant": sum(
                len(branch["checkpoints"]) for state in recipe["states"] for branch in state["branches"]
            ),
        }
        if args.validate_only:
            print(json.dumps(plan, indent=2))
            return 0
        baseline_rom = Path(args.baseline_rom).resolve()
        candidate_rom = Path(args.candidate_rom).resolve()
        missing = [path for path in [baseline_rom, candidate_rom, *states.values()] if not path.is_file()]
        if missing:
            raise BridgeError("Required files were not found: " + ", ".join(str(path) for path in missing))
        automation = endpoint_path(args.automation_endpoint)
        debugger = None if args.no_debugger else debugger_endpoint_path(args.debugger_endpoint, automation)
        if not args.no_debugger and debugger is None:
            raise BridgeError("DKCWidescreenDebugger bridge.json was not found; pass --no-debugger to omit visual/full evidence.")
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output).resolve() if args.output else (
            Path(__file__).resolve().parent.parent / "DifferentialRuns" / f"{safe_name(recipe.get('name', 'differential'))}-{timestamp}"
        )
        runner = DifferentialRunner(recipe, automation, debugger, output, args.socket_timeout)
        report = runner.run(baseline_rom, candidate_rom, states)
        divergent = sum(1 for item in report["comparison"]["checkpoints"] if item["hasActorPopulationDivergence"])
        print(json.dumps({**plan, "output": str(output), "actorPopulationDivergences": divergent}, indent=2))
        return 0
    except (BridgeError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
