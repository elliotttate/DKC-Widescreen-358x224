#!/usr/bin/env python3
"""Deterministically minimize a DKCLevelAutomation v0.1.3 input macro.

Offline commands never inspect an endpoint or open a socket.  The live command
requires explicit, pinned inputs and an already paused/idle target process.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import socket
import sys
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol


WRAM_BASE = 0x7E0000
WRAM_SIZE = 0x20000
BRIDGE_VERSION = "0.1.3"
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}
OPS = {"eq", "ne", "lt", "le", "gt", "ge"}
BUTTON_BITS = {
    "B": 0x8000,
    "Y": 0x4000,
    "SELECT": 0x2000,
    "START": 0x1000,
    "UP": 0x0800,
    "DOWN": 0x0400,
    "LEFT": 0x0200,
    "RIGHT": 0x0100,
    "A": 0x0080,
    "X": 0x0040,
    "L": 0x0020,
    "R": 0x0010,
}
BUTTON_ORDER = tuple(BUTTON_BITS)
BUTTON_ALIASES = {"SEL": "SELECT", "ST": "START", "U": "UP", "D": "DOWN", "NONE": "NONE", "NEUTRAL": "NONE", "0": "NONE"}
FULL_BUTTON_MASK = sum(BUTTON_BITS.values())


class MinimizerError(RuntimeError):
    """A recipe, replay determinism, safety, or bridge failure."""


class BudgetExceeded(MinimizerError):
    """The configured number of unique candidate evaluations was exhausted."""


class Requester(Protocol):
    def request(self, command: str, arguments: dict[str, Any]) -> Any: ...


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def parse_int(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise MinimizerError(f"{label} must be an integer, not a boolean.")
    try:
        return int(value, 0) if isinstance(value, str) else int(value)
    except (TypeError, ValueError) as exc:
        raise MinimizerError(f"{label} must be an integer; received {value!r}.") from exc


def bounded_int(value: Any, label: str, minimum: int, maximum: int) -> int:
    result = parse_int(value, label)
    if not minimum <= result <= maximum:
        raise MinimizerError(f"{label} must be between {minimum} and {maximum}; received {result}.")
    return result


def parse_address(value: Any, size: int, label: str) -> int:
    address = parse_int(value, label)
    if not WRAM_BASE <= address < WRAM_BASE + WRAM_SIZE:
        raise MinimizerError(f"{label} must be in 0x7E0000-0x7FFFFF; received 0x{address:X}.")
    offset = address - WRAM_BASE
    if offset + size > WRAM_SIZE:
        raise MinimizerError(f"{label} plus size extends past WRAM.")
    return offset


def canonical_buttons(value: Any, label: str = "buttons") -> tuple[str, int]:
    if not isinstance(value, str) or not value.strip():
        raise MinimizerError(f"{label} must be a non-empty button string.")
    names: set[str] = set()
    for raw in re.split(r"[+|\s]+", value.strip().upper()):
        if not raw:
            continue
        name = BUTTON_ALIASES.get(raw, raw)
        if name == "NONE":
            continue
        if name not in BUTTON_BITS:
            raise MinimizerError(f"{label} contains unsupported button {name!r}.")
        names.add(name)
    if {"UP", "DOWN"}.issubset(names):
        raise MinimizerError(f"{label} cannot contain both UP and DOWN.")
    if {"LEFT", "RIGHT"}.issubset(names):
        raise MinimizerError(f"{label} cannot contain both LEFT and RIGHT.")
    canonical = "+".join(name for name in BUTTON_ORDER if name in names) or "NONE"
    mask = sum(BUTTON_BITS[name] for name in names)
    return canonical, mask


@dataclass(frozen=True)
class Frame:
    origin: int
    buttons: str
    mask: int


def parse_macro(macro: Any) -> list[Frame]:
    if not isinstance(macro, str) or not macro.strip():
        raise MinimizerError("macro must be a non-empty exact controller macro string.")
    assignments: dict[int, tuple[str, int]] = {}
    maximum = -1
    for raw in re.split(r"[;,]", macro):
        segment = raw.strip()
        if not segment:
            continue
        if "=" not in segment:
            raise MinimizerError(f"Invalid macro segment {segment!r}; expected FRAME or START-END=BUTTONS.")
        range_text, buttons_text = segment.split("=", 1)
        bounds = range_text.strip().split("-", 1)
        first = bounded_int(bounds[0].strip(), "macro start frame", 0, 100000)
        last = bounded_int(bounds[1].strip(), "macro end frame", first, 100000) if len(bounds) == 2 else first
        buttons = canonical_buttons(buttons_text, f"macro segment {range_text!r}")
        for frame in range(first, last + 1):
            assignments[frame] = buttons
        maximum = max(maximum, last)
    if maximum < 0:
        raise MinimizerError("macro must assign at least one frame.")
    neutral = ("NONE", 0)
    return [Frame(index, *(assignments.get(index, neutral))) for index in range(maximum + 1)]


def macro_from_frames(frames: list[Frame]) -> str:
    if not frames:
        return ""
    segments: list[str] = []
    start = 0
    current = frames[0].buttons
    for index in range(1, len(frames) + 1):
        if index < len(frames) and frames[index].buttons == current:
            continue
        range_text = str(start) if start == index - 1 else f"{start}-{index - 1}"
        segments.append(f"{range_text}={current}")
        if index < len(frames):
            start = index
            current = frames[index].buttons
    return ";".join(segments)


def collapsed_signature(frames: list[Frame], projection_mask: int) -> tuple[int, ...]:
    if not frames:
        # A zero-input replay still installs a neutral sentinel schedule, so its
        # observable projected controller state is neutral rather than absent.
        return (0,)
    signature: list[int] = []
    for frame in frames:
        value = frame.mask & projection_mask
        if not signature or signature[-1] != value:
            signature.append(value)
    return tuple(signature)


@dataclass(frozen=True)
class TransitionPolicy:
    mode: str
    projection_mask: int
    expected_signature: tuple[int, ...]
    buttons: tuple[str, ...] = ()

    def allows(self, frames: list[Frame]) -> bool:
        if self.mode == "none":
            return True
        return collapsed_signature(frames, self.projection_mask) == self.expected_signature

    def as_json(self) -> dict[str, Any]:
        return {
            "mode": self.mode,
            "buttons": list(self.buttons),
            "projectionMask": f"0x{self.projection_mask:04X}",
            "expectedSignature": [f"0x{value:04X}" for value in self.expected_signature],
        }


def transition_policy(document: Any, original: list[Frame]) -> TransitionPolicy:
    if document is None or document is False or document == "none":
        return TransitionPolicy("none", 0, ())
    if document is True or document == "all":
        return TransitionPolicy("all", FULL_BUTTON_MASK, collapsed_signature(original, FULL_BUTTON_MASK), BUTTON_ORDER)
    if not isinstance(document, dict):
        raise MinimizerError("preserveTransitions must be false, 'none', 'all', true, or an object.")
    mode = str(document.get("mode", "buttons")).lower()
    if mode == "none":
        return TransitionPolicy("none", 0, ())
    if mode == "all":
        return TransitionPolicy("all", FULL_BUTTON_MASK, collapsed_signature(original, FULL_BUTTON_MASK), BUTTON_ORDER)
    if mode != "buttons":
        raise MinimizerError("preserveTransitions.mode must be none, all, or buttons.")
    raw_buttons = document.get("buttons")
    if not isinstance(raw_buttons, list) or not raw_buttons:
        raise MinimizerError("preserveTransitions.buttons must be a non-empty list for buttons mode.")
    names: list[str] = []
    mask = 0
    for index, raw in enumerate(raw_buttons):
        canonical, bits = canonical_buttons(str(raw), f"preserveTransitions.buttons[{index}]")
        if canonical == "NONE" or len(canonical.split("+")) != 1:
            raise MinimizerError("Each preserveTransitions button must name exactly one non-neutral button.")
        if canonical not in names:
            names.append(canonical)
            mask |= bits
    return TransitionPolicy("buttons", mask, collapsed_signature(original, mask), tuple(names))


def read_value(memory: bytes, condition: dict[str, Any], label: str) -> int:
    size = bounded_int(condition.get("size", 2), f"{label}.size", 1, 4)
    offset = parse_address(condition.get("address"), size, f"{label}.address")
    raw = int.from_bytes(memory[offset : offset + size], "little", signed=False)
    if "mask" in condition:
        mask = parse_int(condition["mask"], f"{label}.mask")
        if not 0 <= mask < 1 << (size * 8):
            raise MinimizerError(f"{label}.mask does not fit in {size} bytes.")
        raw &= mask
    shift = bounded_int(condition.get("shift", 0), f"{label}.shift", 0, size * 8 - 1)
    raw >>= shift
    if condition.get("signed"):
        bits = size * 8 - shift
        sign = 1 << (bits - 1)
        if raw & sign:
            raw -= 1 << bits
    return raw


def validate_predicate(expression: Any, label: str) -> None:
    if not isinstance(expression, dict):
        raise MinimizerError(f"{label} must be a WRAM condition or an all/any/not expression.")
    logical = [key for key in ("all", "any", "not") if key in expression]
    if logical:
        if len(logical) != 1 or any(key in expression for key in ("address", "value", "compareTo")):
            raise MinimizerError(f"{label} must contain exactly one logical operator.")
        key = logical[0]
        if key == "not":
            validate_predicate(expression[key], f"{label}.not")
        else:
            children = expression[key]
            if not isinstance(children, list) or not children:
                raise MinimizerError(f"{label}.{key} must be a non-empty list.")
            for index, child in enumerate(children):
                validate_predicate(child, f"{label}.{key}[{index}]")
        return
    read_value(bytes(WRAM_SIZE), expression, label)
    op = str(expression.get("op", "eq")).lower()
    if op not in OPS:
        raise MinimizerError(f"{label}.op must be one of {sorted(OPS)}.")
    compare_to = expression.get("compareTo")
    if compare_to is not None and compare_to != "baseline":
        raise MinimizerError(f"{label}.compareTo only supports 'baseline'.")
    if compare_to is None and "value" not in expression:
        raise MinimizerError(f"{label} requires value or compareTo='baseline'.")
    if compare_to is None:
        parse_int(expression["value"], f"{label}.value")


def compare_values(actual: int, op: str, expected: int) -> bool:
    return {
        "eq": actual == expected,
        "ne": actual != expected,
        "lt": actual < expected,
        "le": actual <= expected,
        "gt": actual > expected,
        "ge": actual >= expected,
    }[op]


def evaluate_predicate(expression: dict[str, Any], memory: bytes, baseline: bytes) -> bool:
    if "all" in expression:
        return all(evaluate_predicate(child, memory, baseline) for child in expression["all"])
    if "any" in expression:
        return any(evaluate_predicate(child, memory, baseline) for child in expression["any"])
    if "not" in expression:
        return not evaluate_predicate(expression["not"], memory, baseline)
    actual = read_value(memory, expression, str(expression.get("name", "predicate")))
    expected = read_value(baseline, expression, "baseline predicate") if expression.get("compareTo") == "baseline" else parse_int(expression["value"], "predicate.value")
    return compare_values(actual, str(expression.get("op", "eq")).lower(), expected)


def predicate_evidence(expression: dict[str, Any], memory: bytes, baseline: bytes, path: str = "outcome") -> list[dict[str, Any]]:
    if "all" in expression or "any" in expression:
        key = "all" if "all" in expression else "any"
        return [row for index, child in enumerate(expression[key]) for row in predicate_evidence(child, memory, baseline, f"{path}.{key}[{index}]")]
    if "not" in expression:
        return predicate_evidence(expression["not"], memory, baseline, f"{path}.not")
    actual = read_value(memory, expression, path)
    expected = read_value(baseline, expression, path + ".baseline") if expression.get("compareTo") == "baseline" else parse_int(expression["value"], path + ".value")
    op = str(expression.get("op", "eq")).lower()
    return [
        {
            "path": path,
            "name": expression.get("name"),
            "address": expression.get("address"),
            "size": expression.get("size", 2),
            "actual": actual,
            "op": op,
            "expected": expected,
            "matched": compare_values(actual, op, expected),
        }
    ]


@dataclass
class Recipe:
    path: Path
    document: dict[str, Any]
    name: str
    controller: int
    state_path: Path | None
    state_sha256: str | None
    rom_path: Path | None
    rom_sha256: str | None
    original: list[Frame]
    policy: TransitionPolicy
    outcome_label: str
    predicate: dict[str, Any]
    settle_frames: int
    confirmations: int
    max_evaluations: int
    frame_timeout_ms: int
    require_root_false: bool


def optional_file(document: dict[str, Any], key: str, recipe_path: Path) -> tuple[Path | None, str | None]:
    value = document.get(key, {})
    if not isinstance(value, dict):
        raise MinimizerError(f"{key} must be an object.")
    file_path = None
    if value.get("file"):
        file_path = Path(str(value["file"]))
        if not file_path.is_absolute():
            file_path = (recipe_path.parent / file_path).resolve()
    digest = value.get("sha256")
    if digest is not None:
        digest = str(digest).upper()
        if not re.fullmatch(r"[0-9A-F]{64}", digest):
            raise MinimizerError(f"{key}.sha256 must be exactly 64 hexadecimal characters.")
    return file_path, digest


def load_recipe(path: Path) -> Recipe:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise MinimizerError(f"Could not read recipe {path}: {exc}") from exc
    if not isinstance(document, dict) or document.get("schema") != 1:
        raise MinimizerError("Recipe root must be an object with schema 1.")
    name = document.get("name")
    if not isinstance(name, str) or not name.strip():
        raise MinimizerError("Recipe name must be a non-empty string.")
    original = parse_macro(document.get("macro"))
    state_path, state_sha = optional_file(document, "state", path)
    rom_path, rom_sha = optional_file(document, "rom", path)
    outcome = document.get("outcome")
    if not isinstance(outcome, dict):
        raise MinimizerError("outcome must be an object.")
    outcome_label = str(outcome.get("label", "failure")).lower()
    if outcome_label not in ("failure", "success"):
        raise MinimizerError("outcome.label must be failure or success.")
    predicate = outcome.get("predicate")
    validate_predicate(predicate, "outcome.predicate")
    require_root_false = outcome.get("requirePredicateFalseAtRoot", True)
    if not isinstance(require_root_false, bool):
        raise MinimizerError("outcome.requirePredicateFalseAtRoot must be true or false.")
    return Recipe(
        path=path.resolve(),
        document=document,
        name=name,
        controller=bounded_int(document.get("controller", 1), "controller", 1, 5),
        state_path=state_path,
        state_sha256=state_sha,
        rom_path=rom_path,
        rom_sha256=rom_sha,
        original=original,
        policy=transition_policy(document.get("preserveTransitions", False), original),
        outcome_label=outcome_label,
        predicate=predicate,
        settle_frames=bounded_int(outcome.get("settleFrames", 0), "outcome.settleFrames", 0, 100000),
        confirmations=bounded_int(document.get("confirmationReplays", 3), "confirmationReplays", 2, 10),
        max_evaluations=bounded_int(document.get("maxEvaluations", 500), "maxEvaluations", 2, 10000),
        frame_timeout_ms=bounded_int(document.get("frameTimeoutMs", 60000), "frameTimeoutMs", 100, 179000),
        require_root_false=require_root_false,
    )


class BridgeClient:
    def __init__(self, endpoint_path: Path, timeout: float, expected_pid: int):
        try:
            info = json.loads(endpoint_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise MinimizerError(f"Could not read endpoint {endpoint_path}: {exc}") from exc
        self.host = str(info.get("host", "127.0.0.1"))
        if self.host not in LOOPBACK_HOSTS:
            raise MinimizerError(f"Refusing non-loopback bridge host {self.host!r}.")
        if info.get("protocol") != 1 or str(info.get("pluginVersion", "")) != BRIDGE_VERSION:
            raise MinimizerError(f"Endpoint must be DKCLevelAutomation {BRIDGE_VERSION}, protocol 1.")
        self.pid = parse_int(info.get("pid"), "endpoint pid")
        if self.pid != expected_pid:
            raise MinimizerError(f"Endpoint PID {self.pid} does not match --expect-pid {expected_pid}.")
        self.port = bounded_int(info.get("port"), "endpoint port", 1, 65535)
        self.token = info.get("token")
        if not isinstance(self.token, str) or not self.token:
            raise MinimizerError("Endpoint token is missing.")
        self.endpoint_path = endpoint_path.resolve()
        self.timeout = timeout

    @staticmethod
    def encode(value: Any) -> str:
        if isinstance(value, bool):
            text = "true" if value else "false"
        elif value is None:
            text = ""
        elif isinstance(value, (dict, list)):
            raise MinimizerError("Bridge arguments must be scalar.")
        else:
            text = str(value)
        return base64.b64encode(text.encode("utf-8")).decode("ascii")

    def request(self, command: str, arguments: dict[str, Any]) -> Any:
        request_id = uuid.uuid4().hex
        fields = [request_id, self.token, command]
        for key, value in arguments.items():
            fields.extend([self.encode(key), self.encode(value)])
        wire = "\t".join(fields) + "\n"
        try:
            with socket.create_connection((self.host, self.port), timeout=self.timeout) as connection:
                connection.settimeout(self.timeout)
                connection.sendall(wire.encode("utf-8"))
                response = bytearray()
                while b"\n" not in response:
                    block = connection.recv(65536)
                    if not block:
                        break
                    response.extend(block)
        except OSError as exc:
            raise MinimizerError(f"Bridge request {command!r} failed: {exc}") from exc
        if not response:
            raise MinimizerError(f"Bridge closed without responding to {command!r}.")
        try:
            reply = json.loads(bytes(response).split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise MinimizerError(f"Bridge returned malformed JSON for {command!r}.") from exc
        if not reply.get("ok"):
            raise MinimizerError(f"Bridge rejected {command!r}: {reply.get('error', 'unknown error')}")
        return reply.get("result")


def decode_snapshot(result: Any) -> tuple[bytes, int, str]:
    if not isinstance(result, dict):
        raise MinimizerError("snapshot_wram returned a non-object result.")
    try:
        memory = base64.b64decode(result["data"], validate=True)
        frame = int(result["frame"])
        expected = str(result["sha256"]).upper()
    except (KeyError, TypeError, ValueError) as exc:
        raise MinimizerError("snapshot_wram result is missing data, frame, or SHA-256.") from exc
    if len(memory) != WRAM_SIZE:
        raise MinimizerError(f"snapshot_wram returned {len(memory)} bytes instead of {WRAM_SIZE}.")
    digest = hashlib.sha256(memory).hexdigest().upper()
    if digest != expected:
        raise MinimizerError("snapshot_wram data does not match the bridge SHA-256.")
    if result.get("paused") is not True:
        raise MinimizerError("snapshot_wram reported an unpaused emulator.")
    return memory, frame, digest


def verify_preflight(status: Any) -> None:
    if not isinstance(status, dict):
        raise MinimizerError("Bridge status response is not an object.")
    required = ("attached", "loaded", "paused", "frameHook", "inputHook")
    missing = [name for name in required if status.get(name) is not True]
    if missing:
        raise MinimizerError("Live preflight requires true status fields: " + ", ".join(missing))
    if status.get("active") is not None:
        raise MinimizerError("Another bridge frame operation is active.")
    schedules = status.get("schedules")
    if not isinstance(schedules, list) or any(not isinstance(item, dict) or item.get("enabled") for item in schedules):
        raise MinimizerError("Live preflight requires all controller schedules to be clear.")
    if not str(status.get("rom", "")).strip():
        raise MinimizerError("Bridge status did not identify the loaded ROM path.")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise MinimizerError(f"Could not hash {path}: {exc}") from exc
    return digest.hexdigest().upper()


@dataclass(frozen=True)
class FileFingerprint:
    size: int
    modified_ns: int
    sha256: str

    @classmethod
    def capture(cls, path: Path) -> "FileFingerprint":
        try:
            stat = path.stat()
        except OSError as exc:
            raise MinimizerError(f"Could not stat {path}: {exc}") from exc
        if not path.is_file():
            raise MinimizerError(f"Input is not a file: {path}")
        return cls(stat.st_size, stat.st_mtime_ns, sha256_file(path))

    def assert_quick(self, path: Path, label: str) -> None:
        try:
            stat = path.stat()
        except OSError as exc:
            raise MinimizerError(f"Could not re-stat {label} {path}: {exc}") from exc
        if stat.st_size != self.size or stat.st_mtime_ns != self.modified_ns:
            raise MinimizerError(f"{label} size or modification time changed during minimization.")

    def assert_full(self, path: Path, label: str) -> None:
        self.assert_quick(path, label)
        if sha256_file(path) != self.sha256:
            raise MinimizerError(f"{label} SHA-256 changed during minimization.")


def origin_ranges(frames: list[Frame]) -> list[dict[str, int]]:
    if not frames:
        return []
    result: list[dict[str, int]] = []
    first = previous = frames[0].origin
    for frame in frames[1:]:
        if frame.origin != previous + 1:
            result.append({"start": first, "end": previous, "frames": previous - first + 1})
            first = frame.origin
        previous = frame.origin
    result.append({"start": first, "end": previous, "frames": previous - first + 1})
    return result


@dataclass
class Evaluation:
    candidate_key: tuple[int, ...]
    reproduced: bool
    input_frames: int
    macro: str
    retained_ranges: list[dict[str, int]]
    final_wram_sha256: str
    confirmations: list[dict[str, Any]]

    def as_json(self) -> dict[str, Any]:
        return {
            "reproduced": self.reproduced,
            "inputFrames": self.input_frames,
            "macro": self.macro,
            "retainedOriginalFrameRanges": self.retained_ranges,
            "finalWramSha256": self.final_wram_sha256,
            "confirmations": self.confirmations,
        }


class ReplayEvaluator:
    def __init__(
        self,
        recipe: Recipe,
        bridge: Requester,
        state_path: Path,
        state_guard: FileFingerprint | None = None,
        rom_guards: list[tuple[Path, FileFingerprint, str]] | None = None,
    ):
        self.recipe = recipe
        self.bridge = bridge
        self.state_path = state_path.resolve()
        self.state_guard = state_guard
        self.rom_guards = rom_guards or []
        self.baseline_memory: bytes | None = None
        self.baseline_frame: int | None = None
        self.baseline_sha256: str | None = None
        self.cache: dict[tuple[int, ...], Evaluation] = {}
        self.trials: list[dict[str, Any]] = []
        self.evaluation_sets = 0

    def _request(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        return self.bridge.request(command, arguments or {})

    def _guard_inputs(self) -> None:
        if self.state_guard:
            self.state_guard.assert_quick(self.state_path, "External state")
        for path, guard, label in self.rom_guards:
            guard.assert_quick(path, label)

    def _load_root(self) -> tuple[bytes, int, str]:
        self._guard_inputs()
        result = self._request("load_state_file", {"path": str(self.state_path)})
        if not isinstance(result, dict) or result.get("loaded") is not True or result.get("paused") is not True or result.get("schedulesCleared") is not True:
            raise MinimizerError("load_state_file did not confirm a paused load with schedules cleared.")
        memory, frame, digest = decode_snapshot(self._request("snapshot_wram"))
        if self.baseline_memory is None:
            self.baseline_memory = memory
            self.baseline_frame = frame
            self.baseline_sha256 = digest
            if self.recipe.require_root_false and evaluate_predicate(self.recipe.predicate, memory, memory):
                raise MinimizerError("Outcome predicate already matches at the root state; set requirePredicateFalseAtRoot=false only for an intentional autonomous outcome.")
        elif memory != self.baseline_memory or frame != self.baseline_frame or digest != self.baseline_sha256:
            raise MinimizerError("Sequential root reload changed WRAM bytes, WRAM SHA-256, or emulator frame.")
        return memory, frame, digest

    def _run_exact(self, count: int) -> None:
        if count == 0:
            return
        result = self._request("run_frames", {"count": count, "timeout_ms": self.recipe.frame_timeout_ms})
        if not isinstance(result, dict) or result.get("framesAdvanced") != count:
            raise MinimizerError(f"run_frames did not confirm exactly {count} advanced frames.")

    def evaluate(self, frames: list[Frame], context: dict[str, Any], force: bool = False) -> tuple[Evaluation, bool]:
        key = tuple(frame.mask for frame in frames)
        if not force and key in self.cache:
            return self.cache[key], True
        if not force and self.evaluation_sets >= self.recipe.max_evaluations:
            raise BudgetExceeded(f"Reached maxEvaluations={self.recipe.max_evaluations}.")
        self.evaluation_sets += 1
        macro = macro_from_frames(frames)
        schedule_macro = macro or "0=NONE"
        confirmations: list[dict[str, Any]] = []
        for replay in range(1, self.recipe.confirmations + 1):
            baseline, root_frame, _ = self._load_root()
            scheduled = self._request("schedule", {"controller": self.recipe.controller, "macro": schedule_macro})
            expected_length = len(frames) if frames else 1
            if not isinstance(scheduled, dict) or scheduled.get("length") != expected_length or scheduled.get("exactOverride") is not True:
                raise MinimizerError("schedule did not confirm the exact requested macro length/override.")
            try:
                self._run_exact(len(frames))
                self._run_exact(self.recipe.settle_frames)
                memory, final_frame, digest = decode_snapshot(self._request("snapshot_wram"))
            finally:
                self._request("clear_schedule", {"controller": self.recipe.controller})
            expected_frame = root_frame + len(frames) + self.recipe.settle_frames
            if final_frame != expected_frame:
                raise MinimizerError(f"Exact frame mismatch: root {root_frame} + input {len(frames)} + settle {self.recipe.settle_frames} != snapshot {final_frame}.")
            matched = evaluate_predicate(self.recipe.predicate, memory, baseline)
            confirmations.append(
                {
                    "replay": replay,
                    "rootEmulatorFrame": root_frame,
                    "finalEmulatorFrame": final_frame,
                    "exactInputFrames": len(frames),
                    "exactSettleFrames": self.recipe.settle_frames,
                    "predicateMatched": matched,
                    "finalWramSha256": digest,
                    "predicateEvidence": predicate_evidence(self.recipe.predicate, memory, baseline),
                }
            )
        outcomes = {item["predicateMatched"] for item in confirmations}
        digests = {item["finalWramSha256"] for item in confirmations}
        frames_seen = {item["finalEmulatorFrame"] for item in confirmations}
        if len(outcomes) != 1 or len(digests) != 1 or len(frames_seen) != 1:
            raise MinimizerError("Confirmation replays were nondeterministic in predicate result, full WRAM SHA-256, or exact final frame.")
        result = Evaluation(key, outcomes.pop(), len(frames), macro, origin_ranges(frames), digests.pop(), confirmations)
        if not force:
            self.cache[key] = result
        self.trials.append({"evaluation": self.evaluation_sets, "forced": force, "context": context, **result.as_json()})
        return result, False


def constant_runs(frames: list[Frame]) -> list[list[Frame]]:
    runs: list[list[Frame]] = []
    for frame in frames:
        if not runs or runs[-1][-1].mask != frame.mask:
            runs.append([frame])
        else:
            runs[-1].append(frame)
    return runs


def partitions(length: int, count: int) -> list[tuple[int, int]]:
    return [(index * length // count, (index + 1) * length // count) for index in range(count)]


@dataclass
class MinimizationResult:
    frames: list[Frame]
    original_evaluation: Evaluation
    final_evaluation: Evaluation
    steps: list[dict[str, Any]]
    minimality: str
    budget_exhausted: bool


class HierarchicalMinimizer:
    def __init__(self, recipe: Recipe, evaluator: ReplayEvaluator):
        self.recipe = recipe
        self.evaluator = evaluator
        self.steps: list[dict[str, Any]] = []
        self.budget_exhausted = False
        self.completed_single_frame_sweep = False

    def _try(self, current: list[Frame], candidate: list[Frame], context: dict[str, Any]) -> bool:
        retained = set(candidate)
        step = {
            **context,
            "beforeFrames": len(current),
            "candidateFrames": len(candidate),
            "removedOriginalFrameRanges": origin_ranges([frame for frame in current if frame not in retained]),
        }
        if not self.recipe.policy.allows(candidate):
            step.update({"accepted": False, "evaluated": False, "reason": "transition-policy"})
            self.steps.append(step)
            return False
        evaluation, cached = self.evaluator.evaluate(candidate, context)
        step.update({"accepted": evaluation.reproduced, "evaluated": True, "cached": cached, "finalWramSha256": evaluation.final_wram_sha256})
        self.steps.append(step)
        return evaluation.reproduced

    def _ddmin_runs(self, current: list[Frame]) -> list[Frame]:
        atoms = constant_runs(current)
        n = 2
        while len(atoms) >= 1:
            count = min(n, len(atoms))
            reduced = False
            for part, (first, last) in enumerate(partitions(len(atoms), count)):
                remaining = atoms[:first] + atoms[last:]
                candidate = [frame for atom in remaining for frame in atom]
                if self._try(current, candidate, {"phase": "segment-ddmin", "partition": part, "partitions": count}):
                    atoms = remaining
                    current = candidate
                    n = max(2, n - 1)
                    reduced = True
                    break
            if reduced:
                if not atoms:
                    break
                continue
            if count >= len(atoms):
                break
            n = min(len(atoms), n * 2)
        return current

    def _hierarchical_ranges(self, current: list[Frame]) -> list[Frame]:
        width = 1
        while width * 2 <= max(1, len(current) // 2):
            width *= 2
        while width >= 1 and current:
            start = 0
            while start < len(current):
                last = min(len(current), start + width)
                candidate = current[:start] + current[last:]
                if self._try(current, candidate, {"phase": "hierarchical-range", "start": start, "endExclusive": last, "width": last - start}):
                    current = candidate
                    start = 0
                else:
                    start += width
            width //= 2
        return current

    def _ddmin_frames(self, current: list[Frame]) -> list[Frame]:
        n = 2
        while len(current) >= 1:
            count = min(n, len(current))
            reduced = False
            for part, (first, last) in enumerate(partitions(len(current), count)):
                candidate = current[:first] + current[last:]
                if self._try(current, candidate, {"phase": "frame-ddmin", "partition": part, "partitions": count}):
                    current = candidate
                    n = max(2, n - 1)
                    reduced = True
                    break
            if reduced:
                if not current:
                    break
                continue
            if count >= len(current):
                break
            n = min(len(current), n * 2)
        return current

    def _single_frame_sweep(self, current: list[Frame]) -> list[Frame]:
        index = 0
        while index < len(current):
            candidate = current[:index] + current[index + 1 :]
            if self._try(current, candidate, {"phase": "single-frame-proof", "index": index, "origin": current[index].origin}):
                current = candidate
            else:
                index += 1
        self.completed_single_frame_sweep = True
        return current

    def run(self) -> MinimizationResult:
        original, _ = self.evaluator.evaluate(self.recipe.original, {"phase": "original-confirmation"})
        if not original.reproduced:
            raise MinimizerError(f"The original macro did not reproduce the configured {self.recipe.outcome_label} predicate in every confirmation replay.")
        current = list(self.recipe.original)
        try:
            current = self._ddmin_runs(current)
            current = self._hierarchical_ranges(current)
            current = self._ddmin_frames(current)
            current = self._single_frame_sweep(current)
        except BudgetExceeded:
            self.budget_exhausted = True
        final, _ = self.evaluator.evaluate(current, {"phase": "final-independent-confirmation"}, force=True)
        if not final.reproduced:
            raise MinimizerError("The final candidate failed its independent deterministic confirmation replays.")
        minimality = "1-minimal-under-transition-policy" if self.completed_single_frame_sweep else "budget-limited"
        return MinimizationResult(current, original, final, self.steps, minimality, self.budget_exhausted)


def bridge_script(recipe: Recipe, state_path: Path, frames: list[Frame]) -> dict[str, Any]:
    macro = macro_from_frames(frames)
    schedule_macro = macro or "0=NONE"
    steps: list[dict[str, Any]] = [
        {"command": "load_state_file", "args": {"path": str(state_path)}},
        {"command": "schedule", "args": {"controller": recipe.controller, "macro": schedule_macro}},
    ]
    if frames:
        steps.append({"command": "run_frames", "args": {"count": len(frames), "timeout_ms": recipe.frame_timeout_ms}})
    if recipe.settle_frames:
        steps.append({"command": "run_frames", "args": {"count": recipe.settle_frames, "timeout_ms": recipe.frame_timeout_ms}})
    steps.append({"command": "snapshot_wram", "args": {}})
    steps.append({"command": "clear_schedule", "args": {"controller": recipe.controller}})
    return {"steps": steps}


def minimal_recipe_document(recipe: Recipe, state_path: Path, state_hash: str, rom_path: Path, rom_hash: str, result: MinimizationResult) -> dict[str, Any]:
    removed = [frame for frame in recipe.original if frame not in set(result.frames)]
    return {
        "schema": 1,
        "sourceRecipe": recipe.name,
        "outcomeLabel": recipe.outcome_label,
        "externalState": {"path": str(state_path), "sha256": state_hash},
        "rom": {"path": str(rom_path), "sha256": rom_hash},
        "controller": recipe.controller,
        "transitionPolicy": recipe.policy.as_json(),
        "minimality": result.minimality,
        "budgetExhausted": result.budget_exhausted,
        "original": {"frames": len(recipe.original), "macro": macro_from_frames(recipe.original)},
        "minimal": {
            "inputFrames": len(result.frames),
            "settleFrames": recipe.settle_frames,
            "macro": macro_from_frames(result.frames),
            "retainedOriginalFrameRanges": origin_ranges(result.frames),
            "removedOriginalFrameRanges": origin_ranges(removed),
        },
        "evidence": result.final_evaluation.as_json(),
        "bridgeScript": bridge_script(recipe, state_path, result.frames),
    }


def validation_summary(recipe: Recipe) -> dict[str, Any]:
    return {
        "valid": True,
        "liveBridgeContacted": False,
        "recipe": recipe.name,
        "recipePath": str(recipe.path),
        "configuredState": str(recipe.state_path) if recipe.state_path else None,
        "configuredRom": str(recipe.rom_path) if recipe.rom_path else None,
        "stateSha256Pinned": recipe.state_sha256 is not None,
        "romSha256Pinned": recipe.rom_sha256 is not None,
        "outcomeLabel": recipe.outcome_label,
        "originalFrames": len(recipe.original),
        "canonicalMacro": macro_from_frames(recipe.original),
        "settleFrames": recipe.settle_frames,
        "confirmationReplays": recipe.confirmations,
        "maxEvaluations": recipe.max_evaluations,
        "transitionPolicy": recipe.policy.as_json(),
    }


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def write_trials(path: Path, trials: list[dict[str, Any]]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text("".join(json.dumps(trial, separators=(",", ":")) + "\n" for trial in trials), encoding="utf-8")
    os.replace(temporary, path)


def ensure_empty_output(path: Path) -> Path:
    result = path.resolve()
    if result.exists():
        if not result.is_dir():
            raise MinimizerError(f"Output is not a directory: {result}")
        if any(result.iterdir()):
            raise MinimizerError(f"Output directory must be empty; refusing to overwrite: {result}")
    else:
        result.mkdir(parents=True)
    return result


def resolve_pinned_file(explicit_path: Path | None, recipe_path: Path | None, explicit_hash: str | None, recipe_hash: str | None, label: str) -> tuple[Path, FileFingerprint]:
    path = (explicit_path.resolve() if explicit_path else recipe_path)
    if path is None:
        raise MinimizerError(f"{label} path is required on the command line or in the recipe.")
    path = path.resolve()
    fingerprint = FileFingerprint.capture(path)
    pinned = str(explicit_hash or recipe_hash or "").upper()
    if not re.fullmatch(r"[0-9A-F]{64}", pinned):
        raise MinimizerError(f"Live minimization requires a pinned {label} SHA-256.")
    if pinned != fingerprint.sha256:
        raise MinimizerError(f"{label} SHA-256 mismatch: expected {pinned}, observed {fingerprint.sha256}.")
    return path, fingerprint


def run_live(args: argparse.Namespace) -> int:
    if not args.ack_live_control:
        raise MinimizerError("minimize requires --ack-live-control after following the README safe live protocol.")
    if args.socket_timeout <= 0:
        raise MinimizerError("--socket-timeout must be positive.")
    recipe = load_recipe(args.recipe.resolve())
    state_path, state_guard = resolve_pinned_file(args.state, recipe.state_path, args.state_sha256, recipe.state_sha256, "external state")
    rom_path, rom_guard = resolve_pinned_file(args.rom, recipe.rom_path, args.rom_sha256, recipe.rom_sha256, "ROM")
    output = ensure_empty_output(args.output)
    bridge = BridgeClient(args.endpoint.resolve(), args.socket_timeout, args.expect_pid)
    initial_status = bridge.request("status", {})
    verify_preflight(initial_status)
    loaded_rom_path = Path(str(initial_status["rom"])).resolve()
    loaded_rom_guard = FileFingerprint.capture(loaded_rom_path)
    if loaded_rom_guard.sha256 != rom_guard.sha256:
        raise MinimizerError(f"Already loaded ROM hash {loaded_rom_guard.sha256} does not match pinned input ROM {rom_guard.sha256}; the minimizer will not load a ROM for you.")
    rom_guards = [(rom_path, rom_guard, "Pinned ROM")]
    if loaded_rom_path != rom_path:
        rom_guards.append((loaded_rom_path, loaded_rom_guard, "Loaded ROM"))
    evaluator = ReplayEvaluator(recipe, bridge, state_path, state_guard, rom_guards)
    minimizer = HierarchicalMinimizer(recipe, evaluator)
    result: MinimizationResult | None = None
    failure: BaseException | None = None
    cleanup: dict[str, Any] = {"attempted": False, "restoredRoot": False, "scheduleCleared": False}
    state_control_started = False
    try:
        state_control_started = True
        result = minimizer.run()
    except (Exception, KeyboardInterrupt) as exc:
        failure = exc
    finally:
        if state_control_started:
            cleanup["attempted"] = True
            try:
                restored = bridge.request("load_state_file", {"path": str(state_path)})
                cleanup["restoredRoot"] = isinstance(restored, dict) and restored.get("loaded") is True and restored.get("paused") is True
                restored_memory, restored_frame, restored_digest = decode_snapshot(bridge.request("snapshot_wram", {}))
                if evaluator.baseline_memory is None:
                    cleanup["rootWramVerified"] = cleanup["restoredRoot"]
                    cleanup["rootVerificationBasis"] = "post-failure pinned-state load; no earlier baseline was available"
                else:
                    cleanup["rootWramVerified"] = (
                        cleanup["restoredRoot"]
                        and restored_memory == evaluator.baseline_memory
                        and restored_frame == evaluator.baseline_frame
                        and restored_digest == evaluator.baseline_sha256
                    )
                    cleanup["rootVerificationBasis"] = "exact baseline WRAM bytes, SHA-256, and emulator frame"
                cleared = bridge.request("clear_schedule", {"controller": "all"})
                cleanup["scheduleCleared"] = isinstance(cleared, dict)
            except Exception as cleanup_error:
                cleanup["error"] = str(cleanup_error)
        for path, guard, label, key in (
            (state_path, state_guard, "External state", "stateHashUnchanged"),
            (rom_path, rom_guard, "Pinned ROM", "romHashUnchanged"),
            (loaded_rom_path, loaded_rom_guard, "Loaded ROM", "loadedRomHashUnchanged"),
        ):
            try:
                guard.assert_full(path, label)
                cleanup[key] = True
            except Exception as hash_error:
                cleanup[key] = False
                cleanup[key + "Error"] = str(hash_error)
                if failure is None:
                    failure = hash_error
    required_cleanup = ("restoredRoot", "rootWramVerified", "scheduleCleared", "stateHashUnchanged", "romHashUnchanged", "loadedRomHashUnchanged")
    if failure is None and not all(cleanup.get(key) is True for key in required_cleanup):
        failure = MinimizerError(f"Post-minimization cleanup was incomplete: {cleanup}")
    if failure is not None:
        write_json_atomic(output / "failure.json", {"schema": 1, "failedUtc": utc_now(), "error": str(failure), "cleanup": cleanup, "trials": evaluator.trials})
        raise MinimizerError(f"Minimization failed: {failure}. Cleanup details: {cleanup}")
    assert result is not None
    try:
        final_status = bridge.request("status", {})
        verify_preflight(final_status)
    except Exception as exc:
        write_json_atomic(output / "failure.json", {"schema": 1, "failedUtc": utc_now(), "error": str(exc), "cleanup": cleanup, "trials": evaluator.trials})
        raise MinimizerError(f"Post-cleanup status verification failed: {exc}") from exc
    minimal = minimal_recipe_document(recipe, state_path, state_guard.sha256, rom_path, rom_guard.sha256, result)
    report = {
        "schema": 1,
        "createdUtc": utc_now(),
        "recipe": recipe.name,
        "bridge": {"endpoint": str(bridge.endpoint_path), "pid": bridge.pid, "pluginVersion": BRIDGE_VERSION},
        "externalState": {"path": str(state_path), "sha256": state_guard.sha256, "size": state_guard.size},
        "rom": {"path": str(rom_path), "sha256": rom_guard.sha256, "size": rom_guard.size},
        "loadedRom": {"path": str(loaded_rom_path), "sha256": loaded_rom_guard.sha256},
        "outcome": {"label": recipe.outcome_label, "predicate": recipe.predicate, "settleFrames": recipe.settle_frames},
        "transitionPolicy": recipe.policy.as_json(),
        "confirmationReplays": recipe.confirmations,
        "evaluationSets": evaluator.evaluation_sets,
        "minimality": result.minimality,
        "budgetExhausted": result.budget_exhausted,
        "original": result.original_evaluation.as_json(),
        "minimal": result.final_evaluation.as_json(),
        "reductionSteps": result.steps,
        "initialStatus": initial_status,
        "finalStatus": final_status,
        "cleanup": cleanup,
    }
    write_json_atomic(output / "report.json", report)
    write_json_atomic(output / "minimal.recipe.json", minimal)
    write_trials(output / "trials.jsonl", evaluator.trials)
    print(
        json.dumps(
            {
                "report": str(output / "report.json"),
                "minimalRecipe": str(output / "minimal.recipe.json"),
                "originalFrames": len(recipe.original),
                "minimalFrames": len(result.frames),
                "minimality": result.minimality,
                "evaluationSets": evaluator.evaluation_sets,
            },
            indent=2,
        )
    )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    validate = commands.add_parser("validate", help="Validate/canonicalize a recipe without reading an endpoint or opening a socket.")
    validate.add_argument("--recipe", type=Path, required=True)
    hashes = commands.add_parser("hash-inputs", help="Hash a state and ROM without bridge access.")
    hashes.add_argument("--state", type=Path, required=True)
    hashes.add_argument("--rom", type=Path, required=True)
    minimize = commands.add_parser("minimize", help="Minimize against an existing paused v0.1.3 bridge.")
    minimize.add_argument("--recipe", type=Path, required=True)
    minimize.add_argument("--state", type=Path)
    minimize.add_argument("--state-sha256")
    minimize.add_argument("--rom", type=Path)
    minimize.add_argument("--rom-sha256")
    minimize.add_argument("--endpoint", type=Path, required=True, help="Explicit bridge.json path; never auto-discovered.")
    minimize.add_argument("--expect-pid", type=int, required=True)
    minimize.add_argument("--ack-live-control", action="store_true")
    minimize.add_argument("--output", type=Path, required=True, help="New or empty evidence directory.")
    minimize.add_argument("--socket-timeout", type=float, default=190.0)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "validate":
            print(json.dumps(validation_summary(load_recipe(args.recipe.resolve())), indent=2))
            return 0
        if args.command == "hash-inputs":
            state = args.state.resolve()
            rom = args.rom.resolve()
            print(json.dumps({"state": {"path": str(state), "sha256": sha256_file(state)}, "rom": {"path": str(rom), "sha256": sha256_file(rom)}, "liveBridgeContacted": False}, indent=2))
            return 0
        if args.command == "minimize":
            return run_live(args)
        raise MinimizerError(f"Unsupported command {args.command!r}.")
    except (MinimizerError, OSError, ValueError, KeyboardInterrupt) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
