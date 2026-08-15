#!/usr/bin/env python3
"""Find the first deterministic stock-vs-wide DKC WRAM divergence.

The runtime path talks only to an already-running DKCLevelAutomation v0.1.3
bridge.  The comparison/model functions are intentionally independent of the
bridge so validation and tests are completely offline.
"""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import json
import re
import socket
import sys
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Sequence


WRAM_SIZE = 0x20000
WRAM_BASE = 0x7E0000
REQUIRED_BRIDGE_VERSION = "0.1.3"
SAFE_NAME = re.compile(r"[^A-Za-z0-9._-]+")


class LocatorError(RuntimeError):
    """A user-facing recipe, replay, or evidence error."""


@dataclass(frozen=True)
class MemoryRange:
    name: str
    start: int
    end: int

    @property
    def length(self) -> int:
        return self.end - self.start + 1


FIELD_DEFINITIONS: dict[str, tuple[int, int, bool]] = {
    "game_state": (0x002E, 2, False),
    "level_id": (0x0030, 2, False),
    "entrance_id": (0x003E, 2, False),
    "current_actor_index": (0x0082, 2, False),
    "scanner_cursor_primary": (0x00A0, 1, False),
    "scanner_cursor_secondary": (0x00A2, 1, False),
    "scanner_record_index": (0x00A4, 1, False),
    "scanner_window_left": (0x00EF, 2, False),
    "scanner_window_right": (0x00F1, 2, False),
    "held_p1": (0x0500, 2, False),
    "pressed_p1": (0x0504, 2, False),
    "screen_display": (0x051A, 2, False),
    "current_kong": (0x056F, 2, False),
    "gameplay_flags": (0x0579, 2, False),
    "operating_mode": (0x0A75, 2, False),
    "layer1_x": (0x088B, 2, False),
    "layer1_y": (0x0895, 2, False),
    "camera_y": (0x1A4C, 2, False),
    "camera_x": (0x1A62, 2, False),
    "camera_left_bound": (0x1B23, 2, False),
    "camera_right_bound": (0x1B25, 2, False),
    "section_state": (0x1E03, 2, False),
    "section_pointer": (0x1E05, 2, False),
    "section_current": (0x1E07, 2, False),
    "section_pending": (0x1E09, 2, False),
    "section_limit": (0x1E0B, 2, False),
}

ACTOR_TABLES: dict[str, tuple[int, bool]] = {
    "displayed_pose": (0x0AE5, False),
    "x": (0x0B19, False),
    "oam_z": (0x0B8D, False),
    "y": (0x0BC1, False),
    "graphics": (0x0C69, False),
    "current_pose": (0x0D11, False),
    "id": (0x0D45, False),
    "x_speed": (0x0E89, True),
    "y_speed": (0x0EF1, True),
    "state": (0x1029, False),
    "animation_id": (0x10D1, False),
    "source_record": (0x15FD, True),
}

NAMED_GROUPS: dict[str, list[MemoryRange]] = {
    "full_wram": [MemoryRange("full_wram", 0, WRAM_SIZE - 1)],
    "core_gameplay": [
        MemoryRange(name, offset, offset + size - 1)
        for name, (offset, size, _signed) in FIELD_DEFINITIONS.items()
        if name
        in {
            "game_state",
            "level_id",
            "entrance_id",
            "held_p1",
            "pressed_p1",
            "screen_display",
            "current_kong",
            "gameplay_flags",
            "operating_mode",
        }
    ],
    "actor_pool": [
        MemoryRange(f"actor_{name}", base, base + 26 * 2 - 1)
        for name, (base, _signed) in ACTOR_TABLES.items()
    ],
    "object_bookkeeping": [MemoryRange("object_bookkeeping", 0x192B, 0x1A2A)],
    "scanner": [
        MemoryRange("current_actor_index", 0x0082, 0x0083),
        MemoryRange("scanner_cursor_primary", 0x00A0, 0x00A0),
        MemoryRange("scanner_cursor_secondary", 0x00A2, 0x00A2),
        MemoryRange("scanner_record_index", 0x00A4, 0x00A4),
        MemoryRange("scanner_window", 0x00EF, 0x00F2),
    ],
    "section_controller": [MemoryRange("section_controller", 0x1E03, 0x1E0C)],
    "camera_and_bounds": [
        MemoryRange("layer1_x", 0x088B, 0x088C),
        MemoryRange("layer1_y", 0x0895, 0x0896),
        MemoryRange("camera_y", 0x1A4C, 0x1A4D),
        MemoryRange("camera_x", 0x1A62, 0x1A63),
        MemoryRange("camera_bounds", 0x1B23, 0x1B26),
        MemoryRange("scanner_window", 0x00EF, 0x00F2),
    ],
}

IGNORE_PROFILES: dict[str, list[MemoryRange]] = {
    "expected_widescreen_camera_bounds": list(NAMED_GROUPS["camera_and_bounds"]),
}


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_name(value: str) -> str:
    return SAFE_NAME.sub("-", value).strip("-.") or "case"


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def parse_integer(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise LocatorError(f"{label} must be an integer, not a boolean.")
    if isinstance(value, int):
        return value
    if not isinstance(value, str) or not value.strip():
        raise LocatorError(f"{label} must be an integer or numeric string.")
    text = value.strip()
    if text.startswith("$"):
        text = "0x" + text[1:]
    try:
        return int(text, 0)
    except ValueError as exc:
        raise LocatorError(f"{label} has invalid integer {value!r}.") from exc


def parse_offset(value: Any, label: str) -> int:
    address = parse_integer(value, label)
    if WRAM_BASE <= address <= WRAM_BASE + WRAM_SIZE - 1:
        return address - WRAM_BASE
    if 0 <= address < WRAM_SIZE:
        return address
    raise LocatorError(f"{label} must be a WRAM offset or address in $7E0000-$7FFFFF.")


def reject_unknown_keys(document: dict[str, Any], allowed: set[str], label: str) -> None:
    unknown = set(document) - allowed
    if unknown:
        raise LocatorError(f"{label} contains unknown keys: {', '.join(sorted(unknown))}.")


def parse_range(document: Any, label: str) -> MemoryRange:
    if not isinstance(document, dict):
        raise LocatorError(f"{label} must be an object.")
    reject_unknown_keys(document, {"name", "start", "end", "length"}, label)
    start = parse_offset(document.get("start"), f"{label}.start")
    has_end = "end" in document
    has_length = "length" in document
    if has_end == has_length:
        raise LocatorError(f"{label} needs exactly one of end or length.")
    if has_end:
        end = parse_offset(document["end"], f"{label}.end")
    else:
        length = parse_integer(document["length"], f"{label}.length")
        if length <= 0:
            raise LocatorError(f"{label}.length must be positive.")
        end = start + length - 1
    if end < start or end >= WRAM_SIZE:
        raise LocatorError(f"{label} is outside 128 KiB WRAM or has end before start.")
    return MemoryRange(str(document.get("name", label)), start, end)


def macro_length(macro: str) -> int:
    maximum = -1
    for raw in re.split(r"[;,]", macro):
        segment = raw.strip()
        if not segment:
            continue
        if "=" not in segment:
            raise LocatorError(f"Invalid macro segment {segment!r}.")
        frame_range = segment.split("=", 1)[0].strip()
        pieces = frame_range.split("-", 1)
        try:
            first = int(pieces[0], 10)
            last = int(pieces[1], 10) if len(pieces) == 2 else first
        except ValueError as exc:
            raise LocatorError(f"Invalid macro frame range {frame_range!r}.") from exc
        if first < 0 or last < first:
            raise LocatorError(f"Invalid macro frame range {frame_range!r}.")
        maximum = max(maximum, last)
    if maximum < 0:
        raise LocatorError("A controller macro must contain at least one frame assignment.")
    return maximum + 1


def validate_recipe(document: Any, source: str = "recipe") -> dict[str, Any]:
    if not isinstance(document, dict) or document.get("schemaVersion") != 1:
        raise LocatorError(f"{source}: expected an object with schemaVersion 1.")
    reject_unknown_keys(
        document,
        {
            "$schema",
            "schemaVersion",
            "name",
            "description",
            "checkpointStride",
            "traceRadiusFrames",
            "spriteNames",
            "predicate",
            "states",
        },
        source,
    )
    if not isinstance(document.get("name"), str) or not document["name"]:
        raise LocatorError(f"{source}: name must be a non-empty string.")
    sprite_names = document.get("spriteNames", {})
    if not isinstance(sprite_names, dict) or any(not isinstance(value, str) for value in sprite_names.values()):
        raise LocatorError(f"{source}: spriteNames must map numeric ids to strings.")
    for key in sprite_names:
        parse_integer(key, f"{source}: spriteNames key")
    predicate = document.get("predicate")
    if not isinstance(predicate, dict):
        raise LocatorError(f"{source}: predicate must be an object.")
    reject_unknown_keys(predicate, {"includeGroups", "include", "ignoreProfiles", "ignore"}, f"{source}: predicate")
    groups = predicate.get("includeGroups", [])
    ranges = predicate.get("include", [])
    profiles = predicate.get("ignoreProfiles", [])
    ignored = predicate.get("ignore", [])
    if not isinstance(groups, list) or any(group not in NAMED_GROUPS for group in groups):
        raise LocatorError(f"{source}: predicate.includeGroups contains an unknown named group.")
    if not isinstance(ranges, list) or not isinstance(ignored, list):
        raise LocatorError(f"{source}: predicate include/ignore values must be arrays.")
    if not isinstance(profiles, list) or any(profile not in IGNORE_PROFILES for profile in profiles):
        raise LocatorError(f"{source}: predicate.ignoreProfiles contains an unknown profile.")
    for index, item in enumerate(ranges):
        parse_range(item, f"predicate.include[{index}]")
    for index, item in enumerate(ignored):
        parse_range(item, f"predicate.ignore[{index}]")
    if not groups and not ranges:
        raise LocatorError(f"{source}: predicate must select includeGroups and/or include ranges.")

    stride = document.get("checkpointStride", 30)
    if not isinstance(stride, int) or isinstance(stride, bool) or stride < 1:
        raise LocatorError(f"{source}: checkpointStride must be a positive integer.")
    radius = document.get("traceRadiusFrames", 2)
    if not isinstance(radius, int) or isinstance(radius, bool) or radius < 0:
        raise LocatorError(f"{source}: traceRadiusFrames must be a non-negative integer.")
    states = document.get("states")
    if not isinstance(states, list) or not states:
        raise LocatorError(f"{source}: states must be a non-empty array.")
    state_ids: set[str] = set()
    for state_index, state in enumerate(states):
        label = f"states[{state_index}]"
        if not isinstance(state, dict) or not isinstance(state.get("id"), str) or not state["id"]:
            raise LocatorError(f"{source}: {label}.id must be a non-empty string.")
        reject_unknown_keys(state, {"id", "file", "identity", "expectedLevel", "scenarios"}, f"{source}: {label}")
        if state["id"] in state_ids:
            raise LocatorError(f"{source}: duplicate state id {state['id']!r}.")
        state_ids.add(state["id"])
        if not isinstance(state.get("file"), str) or not state["file"]:
            raise LocatorError(f"{source}: {label}.file must be a non-empty string.")
        if "expectedLevel" in state:
            parse_integer(state["expectedLevel"], f"{source}: {label}.expectedLevel")
        scenarios = state.get("scenarios")
        if not isinstance(scenarios, list) or not scenarios:
            raise LocatorError(f"{source}: {label}.scenarios must be a non-empty array.")
        scenario_ids: set[str] = set()
        for scenario_index, scenario in enumerate(scenarios):
            item_label = f"{label}.scenarios[{scenario_index}]"
            if not isinstance(scenario, dict) or not isinstance(scenario.get("id"), str) or not scenario["id"]:
                raise LocatorError(f"{source}: {item_label}.id must be a non-empty string.")
            reject_unknown_keys(
                scenario,
                {"id", "description", "maxFrame", "timeoutMs", "inputs"},
                f"{source}: {item_label}",
            )
            if scenario["id"] in scenario_ids:
                raise LocatorError(f"{source}: duplicate scenario id {scenario['id']!r} in {state['id']}.")
            scenario_ids.add(scenario["id"])
            maximum = scenario.get("maxFrame")
            if not isinstance(maximum, int) or isinstance(maximum, bool) or maximum < 0:
                raise LocatorError(f"{source}: {item_label}.maxFrame must be a non-negative integer.")
            inputs = scenario.get("inputs")
            if not isinstance(inputs, dict) or not inputs:
                raise LocatorError(f"{source}: {item_label}.inputs must map controller numbers to macros.")
            for controller, macro in inputs.items():
                try:
                    number = int(controller)
                except (TypeError, ValueError) as exc:
                    raise LocatorError(f"{source}: {item_label} controller {controller!r} is invalid.") from exc
                if number < 1 or number > 5 or not isinstance(macro, str):
                    raise LocatorError(f"{source}: {item_label} controllers must be 1-5 with string macros.")
                if macro_length(macro) < maximum:
                    raise LocatorError(
                        f"{source}: {item_label} macro for controller {number} ends before maxFrame {maximum}."
                    )
            timeout = scenario.get("timeoutMs", 60000)
            if not isinstance(timeout, int) or isinstance(timeout, bool) or timeout < 1000:
                raise LocatorError(f"{source}: {item_label}.timeoutMs must be an integer of at least 1000.")
    build_selection(predicate)  # proves the selection is non-empty after ignores
    return document


def build_selection(predicate: dict[str, Any]) -> tuple[bytes, list[MemoryRange], list[MemoryRange]]:
    included: list[MemoryRange] = []
    for group in predicate.get("includeGroups", []):
        included.extend(NAMED_GROUPS[group])
    included.extend(parse_range(item, f"predicate.include[{index}]") for index, item in enumerate(predicate.get("include", [])))
    ignored: list[MemoryRange] = []
    for profile in predicate.get("ignoreProfiles", []):
        ignored.extend(IGNORE_PROFILES[profile])
    ignored.extend(parse_range(item, f"predicate.ignore[{index}]") for index, item in enumerate(predicate.get("ignore", [])))
    mask = bytearray(WRAM_SIZE)
    for region in included:
        mask[region.start : region.end + 1] = b"\x01" * region.length
    for region in ignored:
        mask[region.start : region.end + 1] = b"\x00" * region.length
    if not any(mask):
        raise LocatorError("The predicate selects no WRAM bytes after applying ignores.")
    return bytes(mask), included, ignored


def selected_bytes(memory: bytes, mask: bytes) -> bytes:
    if len(memory) != WRAM_SIZE:
        raise LocatorError(f"Expected {WRAM_SIZE} WRAM bytes, received {len(memory)}.")
    if len(mask) != WRAM_SIZE:
        raise LocatorError("Internal predicate mask has the wrong length.")
    return bytes(value for value, selected in zip(memory, mask) if selected)


def frame_fingerprint(memory: bytes, mask: bytes) -> dict[str, str]:
    return {"full": sha256_bytes(memory), "selected": sha256_bytes(selected_bytes(memory, mask))}


def aggregate_fingerprints(frames: Sequence[dict[str, Any]], key: str) -> str:
    digest = hashlib.sha256()
    for frame in frames:
        digest.update(bytes.fromhex(str(frame[key])))
    return digest.hexdigest().upper()


def analyze_snapshot_sequences(baseline: Sequence[bytes], candidate: Sequence[bytes], mask: bytes) -> dict[str, Any]:
    """Pure synthetic/offline first-divergence oracle used by tests and callers."""
    if len(baseline) != len(candidate):
        raise LocatorError("Baseline and candidate snapshot sequences must have the same length.")
    first_raw = None
    first_selected = None
    for frame, (left, right) in enumerate(zip(baseline, candidate)):
        if len(left) != WRAM_SIZE or len(right) != WRAM_SIZE:
            raise LocatorError("Every synthetic snapshot must contain exactly 128 KiB WRAM.")
        if first_raw is None and left != right:
            first_raw = frame
        if first_selected is None and selected_bytes(left, mask) != selected_bytes(right, mask):
            first_selected = frame
    return {"firstRawFrame": first_raw, "firstSelectedFrame": first_selected}


def read_value(memory: bytes, offset: int, size: int, signed: bool = False) -> int:
    return int.from_bytes(memory[offset : offset + size], "little", signed=signed)


def hex_value(value: int, size: int) -> str:
    return f"0x{value & ((1 << (size * 8)) - 1):0{size * 2}X}"


def changed_offsets(baseline: bytes, candidate: bytes, mask: bytes | None = None) -> list[int]:
    if len(baseline) != WRAM_SIZE or len(candidate) != WRAM_SIZE:
        raise LocatorError("Snapshot comparison requires two complete 128 KiB WRAM images.")
    if mask is None:
        return [index for index, (left, right) in enumerate(zip(baseline, candidate)) if left != right]
    return [
        index
        for index, (left, right, selected) in enumerate(zip(baseline, candidate, mask))
        if selected and left != right
    ]


def changed_ranges(baseline: bytes, candidate: bytes, mask: bytes | None = None, limit: int = 256) -> dict[str, Any]:
    offsets = changed_offsets(baseline, candidate, mask)
    ranges: list[dict[str, Any]] = []
    total_ranges = 0
    if offsets:
        first = previous = offsets[0]
        for offset in offsets[1:] + [WRAM_SIZE + 1]:
            if offset != previous + 1:
                total_ranges += 1
                if len(ranges) < limit:
                    left = baseline[first : previous + 1]
                    right = candidate[first : previous + 1]
                    ranges.append(
                        {
                            "start": f"0x{WRAM_BASE + first:06X}",
                            "end": f"0x{WRAM_BASE + previous:06X}",
                            "length": previous - first + 1,
                            "baselineHex": left[:32].hex().upper(),
                            "candidateHex": right[:32].hex().upper(),
                            "hexTruncated": len(left) > 32,
                        }
                    )
                first = offset
            previous = offset
    return {
        "changedByteCount": len(offsets),
        "changedRangeCount": total_ranges,
        "ranges": ranges,
        "rangesTruncated": total_ranges > len(ranges),
    }


def named_field_differences(baseline: bytes, candidate: bytes) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for name, (offset, size, signed) in FIELD_DEFINITIONS.items():
        left = read_value(baseline, offset, size, signed)
        right = read_value(candidate, offset, size, signed)
        if left == right:
            continue
        result.append(
            {
                "field": name,
                "address": f"0x{WRAM_BASE + offset:06X}",
                "size": size,
                "baseline": left,
                "candidate": right,
                "baselineHex": hex_value(left, size),
                "candidateHex": hex_value(right, size),
            }
        )
    return result


def decode_actor(memory: bytes, slot: int, sprite_names: dict[int, str]) -> dict[str, Any]:
    actor: dict[str, Any] = {"slot": slot, "actorIndex": slot * 2, "actorIndexHex": f"0x{slot * 2:02X}"}
    for field, (base, signed) in ACTOR_TABLES.items():
        actor[field] = read_value(memory, base + slot * 2, 2, signed)
    actor["name"] = sprite_names.get(actor["id"], f"sprite_0x{actor['id'] & 0xFFFF:04X}")
    return actor


def actor_differences(baseline: bytes, candidate: bytes, sprite_names: dict[int, str]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for slot in range(26):
        left = decode_actor(baseline, slot, sprite_names)
        right = decode_actor(candidate, slot, sprite_names)
        differences = {
            field: {"baseline": left[field], "candidate": right[field]}
            for field in ACTOR_TABLES
            if left[field] != right[field]
        }
        if differences:
            result.append(
                {
                    "slot": slot,
                    "actorIndex": slot * 2,
                    "actorIndexHex": f"0x{slot * 2:02X}",
                    "baselineId": hex_value(left["id"], 2),
                    "candidateId": hex_value(right["id"], 2),
                    "baselineName": left["name"],
                    "candidateName": right["name"],
                    "kind": "spawn"
                    if left["id"] == 0 and right["id"] != 0
                    else "despawn"
                    if left["id"] != 0 and right["id"] == 0
                    else "replace"
                    if left["id"] != right["id"]
                    else "mutate",
                    "fields": differences,
                }
            )
    return result


def bookkeeping_differences(baseline: bytes, candidate: bytes) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for record in range(0x100):
        offset = 0x192B + record
        left = baseline[offset]
        right = candidate[offset]
        if left != right:
            result.append(
                {
                    "recordIndex": record,
                    "recordIndexHex": f"0x{record:02X}",
                    "address": f"0x{WRAM_BASE + offset:06X}",
                    "baselineActorIndex": left,
                    "candidateActorIndex": right,
                    "baselineActorIndexHex": f"0x{left:02X}",
                    "candidateActorIndexHex": f"0x{right:02X}",
                }
            )
    return result


def describe_divergence(
    baseline: bytes,
    candidate: bytes,
    mask: bytes,
    sprite_names: dict[int, str] | None = None,
    ignored_ranges: Sequence[MemoryRange] = (),
) -> dict[str, Any]:
    ignored_mask_data = bytearray(WRAM_SIZE)
    for region in ignored_ranges:
        ignored_mask_data[region.start : region.end + 1] = b"\x01" * region.length
    ignored_mask = bytes(ignored_mask_data)
    unselected_mask = bytes(0 if selected else 1 for selected in mask)
    fields = named_field_differences(baseline, candidate)
    for field in fields:
        offset = parse_offset(field["address"], f"field {field['field']} address")
        size = int(field["size"])
        field["predicateSelected"] = any(mask[offset : offset + size])
        field["expectedIgnored"] = any(ignored_mask[offset : offset + size])
    return {
        "selectedMemory": changed_ranges(baseline, candidate, mask),
        "allMemory": changed_ranges(baseline, candidate),
        "ignoredMemory": changed_ranges(baseline, candidate, ignored_mask),
        "unselectedMemory": changed_ranges(baseline, candidate, unselected_mask),
        "namedDkcFields": fields,
        "actorSlots": actor_differences(baseline, candidate, sprite_names or {}),
        "objectBookkeeping": bookkeeping_differences(baseline, candidate),
    }


class BridgeClient:
    def __init__(self, endpoint: Path, timeout: float) -> None:
        self.endpoint = endpoint.resolve()
        try:
            self.info = json.loads(self.endpoint.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise LocatorError(f"Could not read automation endpoint {self.endpoint}: {exc}") from exc
        host = str(self.info.get("host", ""))
        if host not in {"127.0.0.1", "localhost", "::1"}:
            raise LocatorError(f"Refusing non-loopback automation endpoint host {host!r}.")
        if self.info.get("pluginVersion") != REQUIRED_BRIDGE_VERSION:
            raise LocatorError(
                f"Expected DKCLevelAutomation {REQUIRED_BRIDGE_VERSION}, endpoint reports "
                f"{self.info.get('pluginVersion')!r}."
            )
        if self.info.get("protocol") != 1:
            raise LocatorError(f"Expected automation bridge protocol 1, got {self.info.get('protocol')!r}.")
        self.timeout = timeout

    @staticmethod
    def _encode(value: Any) -> str:
        if isinstance(value, bool):
            value = "true" if value else "false"
        return base64.b64encode(str(value).encode("utf-8")).decode("ascii")

    def request(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        request_id = uuid.uuid4().hex
        fields = [request_id, str(self.info["token"]), command]
        for key, value in (arguments or {}).items():
            fields.extend([self._encode(key), self._encode(value)])
        wire = ("\t".join(fields) + "\n").encode("utf-8")
        try:
            with socket.create_connection((str(self.info["host"]), int(self.info["port"])), self.timeout) as conn:
                conn.settimeout(self.timeout)
                conn.sendall(wire)
                received = bytearray()
                while b"\n" not in received:
                    block = conn.recv(65536)
                    if not block:
                        break
                    received.extend(block)
        except OSError as exc:
            raise LocatorError(f"Automation bridge request {command!r} failed: {exc}") from exc
        if not received:
            raise LocatorError(f"Automation bridge closed without replying to {command!r}.")
        try:
            reply = json.loads(bytes(received).split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise LocatorError(f"Automation bridge returned invalid JSON for {command!r}.") from exc
        if not reply.get("ok"):
            raise LocatorError(str(reply.get("error", f"Automation command {command!r} failed.")))
        return reply.get("result")


class TraceSliceReader:
    STREAMS = ("events.jsonl", "writes.jsonl", "scanner.jsonl")

    def __init__(self, session: Path | None) -> None:
        self.session = self.resolve_session(session) if session else None

    @staticmethod
    def resolve_session(path: Path) -> Path:
        resolved = path.resolve()
        if (resolved / "events.jsonl").is_file():
            return resolved
        sessions = resolved / "Sessions"
        if sessions.is_dir():
            candidates = sorted((item for item in sessions.iterdir() if item.is_dir()), key=lambda item: item.name)
            if candidates:
                return candidates[-1]
        raise LocatorError(f"Lifecycle tracer session was not found below {resolved}.")

    def mark(self) -> dict[str, int]:
        if self.session is None:
            return {}
        return {
            name: (self.session / name).stat().st_size if (self.session / name).is_file() else 0
            for name in self.STREAMS
        }

    def rows_since(self, mark: dict[str, int]) -> list[dict[str, Any]]:
        if self.session is None:
            return []
        result: list[dict[str, Any]] = []
        for name in self.STREAMS:
            path = self.session / name
            if not path.is_file():
                continue
            with path.open("rb") as handle:
                handle.seek(mark.get(name, 0))
                data = handle.read()
            for raw_line in data.splitlines():
                try:
                    row = json.loads(raw_line.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError):
                    continue
                if isinstance(row, dict):
                    row["traceStream"] = name
                    result.append(row)
        return result

    def nearby(self, rows: Iterable[dict[str, Any]], frame: int, radius: int) -> list[dict[str, Any]]:
        nearby_rows = []
        for row in rows:
            observed = row.get("frame")
            if isinstance(observed, int) and abs(observed - frame) <= radius:
                nearby_rows.append(row)
        nearby_rows.sort(key=lambda row: (row.get("frame", -1), row.get("line", -1), row.get("dot", -1)))
        return nearby_rows[:512]


def decode_snapshot(result: Any, label: str) -> tuple[bytes, dict[str, Any]]:
    if not isinstance(result, dict) or result.get("encoding") != "base64":
        raise LocatorError(f"{label}: snapshot_wram returned an unexpected result.")
    try:
        memory = base64.b64decode(result["data"], validate=True)
    except (KeyError, ValueError) as exc:
        raise LocatorError(f"{label}: snapshot_wram returned invalid base64.") from exc
    if len(memory) != WRAM_SIZE:
        raise LocatorError(f"{label}: snapshot_wram returned {len(memory)} bytes instead of {WRAM_SIZE}.")
    digest = sha256_bytes(memory)
    if digest != str(result.get("sha256", "")).upper():
        raise LocatorError(f"{label}: snapshot_wram SHA-256 did not match its payload.")
    if not result.get("paused"):
        raise LocatorError(f"{label}: snapshot was not paused; exact-frame evidence is ambiguous.")
    return memory, {"emulatorFrame": result.get("frame"), "full": digest}


class FirstDivergenceRunner:
    def __init__(
        self,
        recipe: dict[str, Any],
        client: BridgeClient,
        output: Path,
        trace: TraceSliceReader,
    ) -> None:
        self.recipe = recipe
        self.client = client
        self.output = output
        self.trace = trace
        self.mask, self.included, self.ignored = build_selection(recipe["predicate"])
        self.sprite_names = {
            parse_integer(key, f"spriteNames[{key!r}]"): str(value)
            for key, value in recipe.get("spriteNames", {}).items()
        }

    def prepare(self, rom: Path, state: Path, scenario: dict[str, Any]) -> None:
        self.client.request("load_rom", {"path": str(rom), "load_last_state": False})
        self.client.request("load_state_file", {"path": str(state)})
        self.client.request("pause")
        for controller in sorted(scenario["inputs"], key=int):
            self.client.request(
                "schedule",
                {"controller": int(controller), "macro": scenario["inputs"][controller]},
            )

    def snapshot(self, label: str) -> tuple[bytes, dict[str, Any]]:
        memory, metadata = decode_snapshot(self.client.request("snapshot_wram"), label)
        metadata["selected"] = sha256_bytes(selected_bytes(memory, self.mask))
        return memory, metadata

    def record_baseline(self, rom: Path, state: Path, scenario: dict[str, Any]) -> list[dict[str, Any]]:
        self.prepare(rom, state, scenario)
        frames: list[dict[str, Any]] = []
        try:
            _memory, metadata = self.snapshot("baseline frame 0")
            frames.append(metadata)
            for relative_frame in range(1, scenario["maxFrame"] + 1):
                self.client.request(
                    "step_frames",
                    {"count": 1, "timeout_ms": scenario.get("timeoutMs", 60000)},
                )
                _memory, metadata = self.snapshot(f"baseline frame {relative_frame}")
                frames.append(metadata)
        finally:
            for controller in scenario["inputs"]:
                try:
                    self.client.request("clear_schedule", {"controller": int(controller)})
                except LocatorError:
                    pass
        return frames

    def compare_candidate(
        self,
        rom: Path,
        state: Path,
        scenario: dict[str, Any],
        baseline: list[dict[str, Any]],
    ) -> dict[str, Any]:
        self.prepare(rom, state, scenario)
        first_raw = None
        first_window = None
        current_window: list[dict[str, Any]] = []
        stride = int(self.recipe.get("checkpointStride", 30))
        final_metadata = None
        try:
            for relative_frame in range(0, scenario["maxFrame"] + 1):
                if relative_frame:
                    self.client.request(
                        "step_frames",
                        {"count": 1, "timeout_ms": scenario.get("timeoutMs", 60000)},
                    )
                _memory, metadata = self.snapshot(f"candidate frame {relative_frame}")
                final_metadata = metadata
                if first_raw is None and metadata["full"] != baseline[relative_frame]["full"]:
                    first_raw = relative_frame
                current_window.append(metadata)
                window_end = (relative_frame + 1) % stride == 0 or relative_frame == scenario["maxFrame"]
                if window_end:
                    window_start = relative_frame - len(current_window) + 1
                    baseline_window = baseline[window_start : relative_frame + 1]
                    if aggregate_fingerprints(current_window, "selected") != aggregate_fingerprints(
                        baseline_window, "selected"
                    ):
                        first_window = {"start": window_start, "end": relative_frame}
                        break
                    current_window.clear()
        finally:
            for controller in scenario["inputs"]:
                try:
                    self.client.request("clear_schedule", {"controller": int(controller)})
                except LocatorError:
                    pass
        return {
            "firstRawFrame": first_raw,
            "firstDivergentWindow": first_window,
            "finalMetadata": final_metadata,
        }

    def refine_candidate_window(
        self,
        rom: Path,
        state: Path,
        scenario: dict[str, Any],
        baseline: list[dict[str, Any]],
        window: dict[str, int],
    ) -> dict[str, Any]:
        self.prepare(rom, state, scenario)
        try:
            if window["start"]:
                self.client.request(
                    "step_frames",
                    {"count": window["start"], "timeout_ms": scenario.get("timeoutMs", 60000)},
                )
            for relative_frame in range(window["start"], window["end"] + 1):
                if relative_frame != window["start"]:
                    self.client.request(
                        "step_frames",
                        {"count": 1, "timeout_ms": scenario.get("timeoutMs", 60000)},
                    )
                memory, metadata = self.snapshot(f"candidate refinement frame {relative_frame}")
                if metadata["selected"] != baseline[relative_frame]["selected"]:
                    return {
                        "firstSelectedFrame": relative_frame,
                        "candidateAtSelected": {"memory": memory, "metadata": metadata},
                    }
        finally:
            for controller in scenario["inputs"]:
                try:
                    self.client.request("clear_schedule", {"controller": int(controller)})
                except LocatorError:
                    pass
        raise LocatorError(
            f"Candidate checkpoint window {window['start']}..{window['end']} changed aggregate hash "
            "but contained no per-frame selected mismatch during refinement; replay is nondeterministic."
        )

    def confirmation_replay(
        self,
        variant: str,
        rom: Path,
        state: Path,
        scenario: dict[str, Any],
        relative_frame: int,
        expected: dict[str, Any],
    ) -> dict[str, Any]:
        mark = self.trace.mark()
        self.prepare(rom, state, scenario)
        try:
            if relative_frame:
                self.client.request(
                    "step_frames",
                    {"count": relative_frame, "timeout_ms": scenario.get("timeoutMs", 60000)},
                )
            memory, metadata = self.snapshot(f"{variant} confirmation frame {relative_frame}")
        finally:
            for controller in scenario["inputs"]:
                try:
                    self.client.request("clear_schedule", {"controller": int(controller)})
                except LocatorError:
                    pass
        if metadata["full"] != expected["full"] or metadata["selected"] != expected["selected"]:
            raise LocatorError(
                f"{variant} replay was nondeterministic at relative frame {relative_frame}: "
                f"expected full/selected {expected['full']}/{expected['selected']}, got "
                f"{metadata['full']}/{metadata['selected']}."
            )
        rows = self.trace.rows_since(mark)
        return {
            "memory": memory,
            "metadata": metadata,
            "traceRows": self.trace.nearby(
                rows,
                int(metadata["emulatorFrame"]),
                int(self.recipe.get("traceRadiusFrames", 2)),
            ),
        }

    def write_snapshot(self, folder: Path, variant: str, memory: bytes) -> str:
        path = folder / f"{variant}-first-divergence-wram.bin.gz"
        with gzip.open(path, "wb", compresslevel=6) as handle:
            handle.write(memory)
        return str(path)

    def run_case(
        self,
        baseline_rom: Path,
        candidate_rom: Path,
        state: dict[str, Any],
        state_path: Path,
        scenario: dict[str, Any],
    ) -> dict[str, Any]:
        print(f"locating: {state['id']} / {scenario['id']}", flush=True)
        baseline = self.record_baseline(baseline_rom, state_path, scenario)
        compared = self.compare_candidate(candidate_rom, state_path, scenario, baseline)
        refined = (
            self.refine_candidate_window(
                candidate_rom,
                state_path,
                scenario,
                baseline,
                compared["firstDivergentWindow"],
            )
            if compared["firstDivergentWindow"] is not None
            else None
        )
        first = refined["firstSelectedFrame"] if refined else None
        stride = int(self.recipe.get("checkpointStride", 30))
        result: dict[str, Any] = {
            "state": state["id"],
            "stateIdentity": state.get("identity", ""),
            "statePath": str(state_path),
            "stateSha256": sha256_file(state_path),
            "scenario": scenario["id"],
            "inputs": scenario["inputs"],
            "maxFrame": scenario["maxFrame"],
            "firstRawFrame": compared["firstRawFrame"],
            "firstUnexpectedFrame": first,
            "search": {
                "method": "deterministic sequential replay with checkpoint-window refinement",
                "checkpointStride": stride,
                "snapshotsPerCompletedFrame": True,
                "transientDivergencesPreserved": True,
                "firstDivergentWindow": compared["firstDivergentWindow"],
                "refinementReplay": None
                if first is None
                else {
                    "start": compared["firstDivergentWindow"]["start"],
                    "end": first,
                    "locatedFrame": first,
                },
            },
        }
        confirmation_frame = first if first is not None else scenario["maxFrame"]
        candidate_expected = (
            refined["candidateAtSelected"]["metadata"]
            if first is not None
            else compared["finalMetadata"]
        )
        if candidate_expected is None:
            raise LocatorError("Candidate replay produced no final snapshot metadata.")

        baseline_confirm = self.confirmation_replay(
            "baseline",
            baseline_rom,
            state_path,
            scenario,
            confirmation_frame,
            baseline[confirmation_frame],
        )
        candidate_confirm = self.confirmation_replay(
            "candidate",
            candidate_rom,
            state_path,
            scenario,
            confirmation_frame,
            candidate_expected,
        )
        expected_level = state.get("expectedLevel")
        if expected_level is not None:
            wanted = parse_integer(expected_level, f"state {state['id']} expectedLevel")
            for variant, confirmation in (("baseline", baseline_confirm), ("candidate", candidate_confirm)):
                actual = read_value(confirmation["memory"], FIELD_DEFINITIONS["level_id"][0], 2)
                if actual != wanted:
                    raise LocatorError(
                        f"{state['id']}/{scenario['id']} {variant} loaded level 0x{actual:04X}, expected 0x{wanted:04X}."
                    )
        result["determinism"] = {
            "confirmed": True,
            "relativeFrame": confirmation_frame,
            "baseline": baseline_confirm["metadata"],
            "candidate": candidate_confirm["metadata"],
        }
        result["lifecycleTrace"] = {
            "available": self.trace.session is not None,
            "session": str(self.trace.session) if self.trace.session else None,
            "radiusFrames": int(self.recipe.get("traceRadiusFrames", 2)),
            "baseline": baseline_confirm["traceRows"],
            "candidate": candidate_confirm["traceRows"],
        }
        if first is not None:
            folder = self.output / "cases" / safe_name(state["id"]) / safe_name(scenario["id"])
            folder.mkdir(parents=True, exist_ok=True)
            result["snapshots"] = {
                "baseline": self.write_snapshot(folder, "baseline", baseline_confirm["memory"]),
                "candidate": self.write_snapshot(folder, "candidate", candidate_confirm["memory"]),
            }
            result["difference"] = describe_divergence(
                baseline_confirm["memory"],
                candidate_confirm["memory"],
                self.mask,
                self.sprite_names,
                self.ignored,
            )
        return result

    def run(
        self,
        baseline_rom: Path,
        candidate_rom: Path,
        states: dict[str, Path],
        selected_cases: set[str],
    ) -> dict[str, Any]:
        self.output.mkdir(parents=True, exist_ok=True)
        report: dict[str, Any] = {
            "schemaVersion": 1,
            "tool": "DKCFirstDivergenceLocator",
            "requiredAutomationVersion": REQUIRED_BRIDGE_VERSION,
            "startedUtc": utc_now(),
            "completedUtc": None,
            "recipe": self.recipe.get("name", "unnamed"),
            "baselineRom": {"path": str(baseline_rom), "sha256": sha256_file(baseline_rom)},
            "candidateRom": {"path": str(candidate_rom), "sha256": sha256_file(candidate_rom)},
            "predicate": {
                "selectedByteCount": sum(self.mask),
                "includedRanges": [range_data(region) for region in self.included],
                "ignoredRanges": [range_data(region) for region in self.ignored],
            },
            "cases": [],
        }
        report_path = self.output / "report.json"
        for state in self.recipe["states"]:
            for scenario in state["scenarios"]:
                case_key = f"{state['id']}/{scenario['id']}"
                if selected_cases and case_key not in selected_cases:
                    continue
                report["cases"].append(
                    self.run_case(baseline_rom, candidate_rom, state, states[state["id"]], scenario)
                )
                report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        report["completedUtc"] = utc_now()
        report["summary"] = {
            "casesRun": len(report["cases"]),
            "casesWithUnexpectedDivergence": sum(
                case["firstUnexpectedFrame"] is not None for case in report["cases"]
            ),
            "casesWithoutSelectedDivergence": sum(
                case["firstUnexpectedFrame"] is None for case in report["cases"]
            ),
        }
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        return report


def range_data(region: MemoryRange) -> dict[str, Any]:
    return {
        "name": region.name,
        "start": f"0x{WRAM_BASE + region.start:06X}",
        "end": f"0x{WRAM_BASE + region.end:06X}",
        "length": region.length,
    }


def locate_recipe(value: str) -> Path:
    direct = Path(value)
    bundled = Path(__file__).resolve().parent / "recipes"
    for candidate in (direct, bundled / value, bundled / f"{value}.json"):
        if candidate.is_file():
            return candidate.resolve()
    raise LocatorError(f"Recipe was not found: {value}")


def resolve_states(recipe: dict[str, Any], state_dir: Path | None, assignments: list[str]) -> dict[str, Path]:
    overrides: dict[str, Path] = {}
    for assignment in assignments:
        if "=" not in assignment:
            raise LocatorError("--state must use STATE_ID=path syntax.")
        state_id, path = assignment.split("=", 1)
        overrides[state_id] = Path(path).resolve()
    known = {state["id"] for state in recipe["states"]}
    unknown = set(overrides) - known
    if unknown:
        raise LocatorError("--state named unknown state ids: " + ", ".join(sorted(unknown)))
    return {
        state["id"]: overrides.get(
            state["id"],
            ((state_dir / state["file"]).resolve() if state_dir else Path(state["file"]).resolve()),
        )
        for state in recipe["states"]
    }


def case_keys(recipe: dict[str, Any]) -> set[str]:
    return {f"{state['id']}/{scenario['id']}" for state in recipe["states"] for scenario in state["scenarios"]}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Sequentially replay stock and widescreen DKC ROMs through DKCLevelAutomation v0.1.3 "
            "and report the first selected WRAM divergence."
        )
    )
    parser.add_argument("--recipe", required=True, help="Recipe name or JSON path.")
    parser.add_argument("--baseline-rom", help="Known-stock/clean ROM path.")
    parser.add_argument("--candidate-rom", help="Widescreen ROM path.")
    parser.add_argument("--state-dir", help="Directory containing recipe state filenames.")
    parser.add_argument("--state", action="append", default=[], help="Override one state as STATE_ID=path.")
    parser.add_argument(
        "--case",
        action="append",
        default=[],
        help="Run only STATE_ID/SCENARIO_ID (repeatable). Defaults to every case.",
    )
    parser.add_argument("--automation-endpoint", help="Path to DKCLevelAutomation bridge.json.")
    parser.add_argument(
        "--lifecycle-session",
        help="Optional DKCObjectLifecycleTracer session or plugin root; read-only correlation only.",
    )
    parser.add_argument("--output", help="Output directory; defaults below this tool's DivergenceRuns.")
    parser.add_argument("--socket-timeout", type=float, default=190.0)
    parser.add_argument(
        "--validate-only",
        action="store_true",
        help="Validate recipe/schema and print the replay plan without checking files or reading/connecting to a bridge.",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        recipe_path = locate_recipe(args.recipe)
        recipe = validate_recipe(json.loads(recipe_path.read_text(encoding="utf-8")), str(recipe_path))
        state_dir = Path(args.state_dir).resolve() if args.state_dir else None
        states = resolve_states(recipe, state_dir, args.state)
        selected_cases = set(args.case)
        unknown_cases = selected_cases - case_keys(recipe)
        if unknown_cases:
            raise LocatorError("Unknown --case values: " + ", ".join(sorted(unknown_cases)))
        mask, included, ignored = build_selection(recipe["predicate"])
        plan = {
            "ok": True,
            "mode": "validate-only" if args.validate_only else "run",
            "recipe": recipe.get("name"),
            "recipePath": str(recipe_path),
            "states": {key: str(value) for key, value in states.items()},
            "cases": sorted(selected_cases or case_keys(recipe)),
            "selectedByteCount": sum(mask),
            "includedRangeCount": len(included),
            "ignoredRangeCount": len(ignored),
            "automationContacted": False,
        }
        if args.validate_only:
            print(json.dumps(plan, indent=2))
            return 0
        if not args.baseline_rom or not args.candidate_rom or not args.automation_endpoint:
            raise LocatorError("Run mode requires --baseline-rom, --candidate-rom, and --automation-endpoint.")
        baseline_rom = Path(args.baseline_rom).resolve()
        candidate_rom = Path(args.candidate_rom).resolve()
        endpoint = Path(args.automation_endpoint).resolve()
        needed_states = {
            state_id
            for state_id, path in states.items()
            if any(key.startswith(state_id + "/") for key in (selected_cases or case_keys(recipe)))
        }
        missing = [path for path in [baseline_rom, candidate_rom, endpoint] if not path.is_file()]
        missing.extend(states[state_id] for state_id in needed_states if not states[state_id].is_file())
        if missing:
            raise LocatorError("Required files were not found: " + ", ".join(str(path) for path in missing))
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output).resolve() if args.output else (
            Path(__file__).resolve().parent / "DivergenceRuns" / f"{safe_name(recipe.get('name', 'run'))}-{timestamp}"
        )
        trace = TraceSliceReader(Path(args.lifecycle_session)) if args.lifecycle_session else TraceSliceReader(None)
        runner = FirstDivergenceRunner(recipe, BridgeClient(endpoint, args.socket_timeout), output, trace)
        report = runner.run(baseline_rom, candidate_rom, states, selected_cases)
        print(json.dumps({**plan, "automationContacted": True, "output": str(output), **report["summary"]}, indent=2))
        return 0
    except (LocatorError, OSError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
