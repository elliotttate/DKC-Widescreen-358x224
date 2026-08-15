#!/usr/bin/env python3
"""Deterministic, replay-from-root route search for DKCLevelAutomation v0.1.3.

The live command is intentionally fail-closed: it requires an explicit endpoint,
PID, pinned state hash, and acknowledgement.  Offline validation never reads an
endpoint and never opens a socket.
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
from dataclasses import dataclass, field
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any, Iterable, Protocol


WRAM_BASE = 0x7E0000
WRAM_SIZE = 0x20000
BRIDGE_VERSION = "0.1.3"
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "::1"}
OPS = {"eq", "ne", "lt", "le", "gt", "ge"}
BUTTON_ORDER = ("B", "Y", "SELECT", "START", "UP", "DOWN", "LEFT", "RIGHT", "A", "X", "L", "R")
BUTTON_ALIASES = {"SEL": "SELECT", "ST": "START", "U": "UP", "D": "DOWN", "NONE": "NONE", "NEUTRAL": "NONE", "0": "NONE"}
SAFE_ID = re.compile(r"[^A-Za-z0-9._-]+")


class ExplorerError(RuntimeError):
    """A recipe, safety, bridge, or deterministic-replay failure."""


class Requester(Protocol):
    def request(self, command: str, arguments: dict[str, Any]) -> Any: ...


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_id(value: str) -> str:
    return SAFE_ID.sub("-", value).strip("-.") or "item"


def parse_int(value: Any, label: str) -> int:
    if isinstance(value, bool):
        raise ExplorerError(f"{label} must be an integer, not a boolean.")
    try:
        return int(value, 0) if isinstance(value, str) else int(value)
    except (TypeError, ValueError) as exc:
        raise ExplorerError(f"{label} must be an integer; received {value!r}.") from exc


def bounded_int(value: Any, label: str, minimum: int, maximum: int) -> int:
    result = parse_int(value, label)
    if not minimum <= result <= maximum:
        raise ExplorerError(f"{label} must be between {minimum} and {maximum}; received {result}.")
    return result


def parse_decimal(value: Any, label: str) -> Decimal:
    try:
        result = Decimal(str(value))
    except (InvalidOperation, ValueError) as exc:
        raise ExplorerError(f"{label} must be a finite decimal; received {value!r}.") from exc
    if not result.is_finite():
        raise ExplorerError(f"{label} must be finite.")
    return result


def parse_address(value: Any, size: int, label: str = "address") -> int:
    address = parse_int(value, label)
    if WRAM_BASE <= address < WRAM_BASE + WRAM_SIZE:
        offset = address - WRAM_BASE
    else:
        raise ExplorerError(f"{label} must be in 0x7E0000-0x7FFFFF; received 0x{address:X}.")
    if offset + size > WRAM_SIZE:
        raise ExplorerError(f"{label} plus size extends past WRAM.")
    return offset


def read_value(memory: bytes, spec: dict[str, Any], label: str = "field") -> int:
    size = bounded_int(spec.get("size", 2), f"{label}.size", 1, 8)
    offset = parse_address(spec.get("address"), size, f"{label}.address")
    raw = int.from_bytes(memory[offset : offset + size], "little", signed=False)
    mask_value = spec.get("mask")
    if mask_value is not None:
        mask = parse_int(mask_value, f"{label}.mask")
        if not 0 <= mask < 1 << (size * 8):
            raise ExplorerError(f"{label}.mask does not fit in {size} bytes.")
        raw &= mask
    shift = bounded_int(spec.get("shift", 0), f"{label}.shift", 0, size * 8 - 1)
    raw >>= shift
    if bool(spec.get("signed", False)):
        effective_bits = size * 8 - shift
        sign = 1 << (effective_bits - 1)
        if raw & sign:
            raw -= 1 << effective_bits
    return raw


def canonical_buttons(value: Any, label: str = "buttons") -> str:
    if not isinstance(value, str) or not value.strip():
        raise ExplorerError(f"{label} must be a non-empty button string.")
    names: set[str] = set()
    for token in re.split(r"[+|\s]+", value.strip().upper()):
        if not token:
            continue
        token = BUTTON_ALIASES.get(token, token)
        if token == "NONE":
            continue
        if token not in BUTTON_ORDER:
            raise ExplorerError(f"{label} contains unsupported button {token!r}.")
        names.add(token)
    if {"UP", "DOWN"}.issubset(names):
        raise ExplorerError(f"{label} cannot contain both UP and DOWN.")
    if {"LEFT", "RIGHT"}.issubset(names):
        raise ExplorerError(f"{label} cannot contain both LEFT and RIGHT.")
    return "+".join(name for name in BUTTON_ORDER if name in names) or "NONE"


def combine_buttons(*values: str) -> str:
    combined = "+".join(value for value in values if value and value != "NONE") or "NONE"
    return canonical_buttons(combined)


def compress_frames(frames: Iterable[str]) -> tuple[tuple[int, str], ...]:
    segments: list[tuple[int, str]] = []
    for buttons in frames:
        canonical = canonical_buttons(buttons)
        if segments and segments[-1][1] == canonical:
            count, prior = segments[-1]
            segments[-1] = (count + 1, prior)
        else:
            segments.append((1, canonical))
    if not segments:
        raise ExplorerError("An action must contain at least one frame.")
    return tuple(segments)


@dataclass(frozen=True)
class Action:
    id: str
    segments: tuple[tuple[int, str], ...]
    description: str = ""

    @property
    def frames(self) -> int:
        return sum(count for count, _ in self.segments)

    def expand(self) -> list[str]:
        return [buttons for count, buttons in self.segments for _ in range(count)]


def action_from_document(document: dict[str, Any], index: int) -> Action:
    if not isinstance(document, dict):
        raise ExplorerError(f"actions[{index}] must be an object.")
    action_id = document.get("id")
    if not isinstance(action_id, str) or not action_id.strip():
        raise ExplorerError(f"actions[{index}].id must be a non-empty string.")
    if "sequence" in document:
        sequence = document["sequence"]
        if not isinstance(sequence, list) or not sequence:
            raise ExplorerError(f"actions[{index}].sequence must be a non-empty list.")
        segments = []
        for segment_index, segment in enumerate(sequence):
            if not isinstance(segment, dict):
                raise ExplorerError(f"actions[{index}].sequence[{segment_index}] must be an object.")
            count = bounded_int(segment.get("frames"), f"actions[{index}].sequence[{segment_index}].frames", 1, 10000)
            buttons = canonical_buttons(segment.get("buttons"), f"actions[{index}].sequence[{segment_index}].buttons")
            segments.extend([buttons] * count)
        compressed = compress_frames(segments)
    else:
        count = bounded_int(document.get("frames"), f"actions[{index}].frames", 1, 10000)
        buttons = canonical_buttons(document.get("buttons"), f"actions[{index}].buttons")
        compressed = ((count, buttons),)
    return Action(safe_id(action_id), compressed, str(document.get("description", "")))


def generated_underwater_actions(document: dict[str, Any], index: int) -> list[Action]:
    if not isinstance(document, dict):
        raise ExplorerError(f"underwaterPulseGenerators[{index}] must be an object.")
    prefix = safe_id(str(document.get("idPrefix", "swim")))
    directions = document.get("directions")
    buttons = document.get("buttons")
    totals = document.get("totalFrames")
    periods = document.get("periodFrames")
    pulses = document.get("pulseFrames")
    for name, value in (("directions", directions), ("buttons", buttons), ("totalFrames", totals), ("periodFrames", periods), ("pulseFrames", pulses)):
        if not isinstance(value, list) or not value:
            raise ExplorerError(f"underwaterPulseGenerators[{index}].{name} must be a non-empty list.")
    result: list[Action] = []
    for raw_direction in directions:
        direction = canonical_buttons(raw_direction, f"underwaterPulseGenerators[{index}].directions")
        for raw_button in buttons:
            button = canonical_buttons(raw_button, f"underwaterPulseGenerators[{index}].buttons")
            for raw_total in totals:
                total = bounded_int(raw_total, "underwater totalFrames", 1, 10000)
                for raw_period in periods:
                    period = bounded_int(raw_period, "underwater periodFrames", 1, total)
                    for raw_pulse in pulses:
                        pulse = bounded_int(raw_pulse, "underwater pulseFrames", 1, period)
                        frames = [combine_buttons(direction, button) if frame % period < pulse else direction for frame in range(total)]
                        action_id = safe_id(f"{prefix}-{direction}-{button}-t{total}-p{pulse}-e{period}".lower())
                        result.append(Action(action_id, compress_frames(frames), "Generated underwater direction/button pulse."))
    return result


def macro_from_frames(frames: list[str]) -> str:
    if not frames:
        return ""
    segments: list[str] = []
    start = 0
    current = frames[0]
    for index in range(1, len(frames) + 1):
        if index < len(frames) and frames[index] == current:
            continue
        frame_range = str(start) if start == index - 1 else f"{start}-{index - 1}"
        segments.append(f"{frame_range}={current}")
        if index < len(frames):
            start = index
            current = frames[index]
    return ";".join(segments)


def action_chain_frames(actions: Iterable[Action]) -> list[str]:
    return [buttons for action in actions for buttons in action.expand()]


def validate_condition(expression: Any, label: str) -> None:
    if expression is None:
        return
    if not isinstance(expression, dict):
        raise ExplorerError(f"{label} must be a condition or an all/any/not expression.")
    logical = [key for key in ("all", "any", "not") if key in expression]
    if logical:
        if len(logical) != 1 or any(key in expression for key in ("address", "op", "value", "compareTo")):
            raise ExplorerError(f"{label} must contain exactly one logical operator.")
        key = logical[0]
        if key == "not":
            validate_condition(expression[key], f"{label}.not")
        else:
            children = expression[key]
            if not isinstance(children, list) or not children:
                raise ExplorerError(f"{label}.{key} must be a non-empty list.")
            for index, child in enumerate(children):
                validate_condition(child, f"{label}.{key}[{index}]")
        return
    size = bounded_int(expression.get("size", 2), f"{label}.size", 1, 4)
    parse_address(expression.get("address"), size, f"{label}.address")
    op = str(expression.get("op", "eq")).lower()
    if op not in OPS:
        raise ExplorerError(f"{label}.op must be one of {sorted(OPS)}.")
    compare_to = expression.get("compareTo")
    if compare_to is not None and compare_to != "baseline":
        raise ExplorerError(f"{label}.compareTo only supports 'baseline'.")
    if compare_to is None and "value" not in expression:
        raise ExplorerError(f"{label} requires value or compareTo='baseline'.")
    read_value(bytes(WRAM_SIZE), expression, label)
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


def evaluate_condition(expression: Any, memory: bytes, baseline: bytes) -> bool:
    if expression is None:
        return False
    if "all" in expression:
        return all(evaluate_condition(child, memory, baseline) for child in expression["all"])
    if "any" in expression:
        return any(evaluate_condition(child, memory, baseline) for child in expression["any"])
    if "not" in expression:
        return not evaluate_condition(expression["not"], memory, baseline)
    actual = read_value(memory, expression, str(expression.get("name", "condition")))
    expected = read_value(baseline, expression, "baseline condition") if expression.get("compareTo") == "baseline" else parse_int(expression["value"], "condition.value")
    return compare_values(actual, str(expression.get("op", "eq")).lower(), expected)


def compact_state(memory: bytes, selectors: list[dict[str, Any]]) -> tuple[str, dict[str, int]]:
    values: dict[str, int] = {}
    canonical: list[list[Any]] = []
    for index, selector in enumerate(selectors):
        name = str(selector.get("name", f"field_{index}"))
        if name in values:
            raise ExplorerError(f"Duplicate dedup selector name {name!r}.")
        value = read_value(memory, selector, f"dedup.selectors[{index}]")
        bucket = bounded_int(selector.get("bucket", 1), f"dedup.selectors[{index}].bucket", 1, 1 << 31)
        value //= bucket
        values[name] = value
        canonical.append([name, value])
    encoded = json.dumps(canonical, separators=(",", ":"), ensure_ascii=True).encode("ascii")
    return hashlib.blake2s(encoded, digest_size=12).hexdigest().upper(), values


def objective_score(memory: bytes, baseline: bytes, objective: dict[str, Any]) -> tuple[Decimal, dict[str, dict[str, Any]]]:
    score = Decimal(0)
    values: dict[str, dict[str, Any]] = {}
    for index, term in enumerate(objective["terms"]):
        name = str(term.get("name", f"term_{index}"))
        current = read_value(memory, term, f"objective.terms[{index}]")
        reference_spec = term.get("reference", "baseline")
        reference = read_value(baseline, term, f"objective.terms[{index}] baseline") if reference_spec == "baseline" else parse_int(reference_spec, f"objective.terms[{index}].reference")
        direction = str(term.get("direction", "maximize")).lower()
        sign = Decimal(1) if direction == "maximize" else Decimal(-1)
        weight = parse_decimal(term.get("weight", 1), f"objective.terms[{index}].weight")
        scale = parse_decimal(term.get("scale", 1), f"objective.terms[{index}].scale")
        contribution = sign * weight * Decimal(current - reference) / scale
        score += contribution
        values[name] = {"current": current, "reference": reference, "contribution": decimal_text(contribution)}
    return score, values


def decimal_text(value: Decimal) -> str:
    text = format(value.normalize(), "f")
    return "0" if text == "-0" else text


@dataclass
class Recipe:
    path: Path
    document: dict[str, Any]
    actions: list[Action]
    name: str
    state_file: Path | None
    state_sha256: str | None
    controller: int
    max_depth: int
    beam_width: int
    max_nodes: int
    predicate_check_frames: int
    objective: dict[str, Any]
    goal: Any
    forbidden: Any
    death: Any
    dedup_selectors: list[dict[str, Any]]
    output_top: int


def load_recipe(path: Path) -> Recipe:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ExplorerError(f"Could not read recipe {path}: {exc}") from exc
    if not isinstance(document, dict):
        raise ExplorerError("Recipe root must be an object.")
    if document.get("schema") != 1:
        raise ExplorerError("Recipe schema must be 1.")
    name = document.get("name")
    if not isinstance(name, str) or not name.strip():
        raise ExplorerError("Recipe name must be a non-empty string.")
    raw_actions = document.get("actions", [])
    if not isinstance(raw_actions, list):
        raise ExplorerError("actions must be a list.")
    actions = [action_from_document(item, index) for index, item in enumerate(raw_actions)]
    generators = document.get("underwaterPulseGenerators", [])
    if not isinstance(generators, list):
        raise ExplorerError("underwaterPulseGenerators must be a list.")
    for index, generator in enumerate(generators):
        actions.extend(generated_underwater_actions(generator, index))
    if not actions:
        raise ExplorerError("Recipe must define at least one explicit or generated action.")
    ids = [action.id for action in actions]
    duplicate_ids = sorted({item for item in ids if ids.count(item) > 1})
    if duplicate_ids:
        raise ExplorerError("Action IDs must be unique after generation: " + ", ".join(duplicate_ids))

    state = document.get("state", {})
    if not isinstance(state, dict):
        raise ExplorerError("state must be an object.")
    state_file: Path | None = None
    if state.get("file"):
        state_file = Path(str(state["file"]))
        if not state_file.is_absolute():
            state_file = (path.parent / state_file).resolve()
    state_sha = state.get("sha256")
    if state_sha is not None:
        state_sha = str(state_sha).upper()
        if not re.fullmatch(r"[0-9A-F]{64}", state_sha):
            raise ExplorerError("state.sha256 must be exactly 64 hexadecimal characters.")

    objective = document.get("objective")
    if not isinstance(objective, dict) or not isinstance(objective.get("terms"), list) or not objective["terms"]:
        raise ExplorerError("objective.terms must be a non-empty list.")
    for index, term in enumerate(objective["terms"]):
        if not isinstance(term, dict):
            raise ExplorerError(f"objective.terms[{index}] must be an object.")
        size = bounded_int(term.get("size", 2), f"objective.terms[{index}].size", 1, 8)
        parse_address(term.get("address"), size, f"objective.terms[{index}].address")
        if str(term.get("direction", "maximize")).lower() not in ("maximize", "minimize"):
            raise ExplorerError(f"objective.terms[{index}].direction must be maximize or minimize.")
        reference = term.get("reference", "baseline")
        if reference != "baseline":
            parse_int(reference, f"objective.terms[{index}].reference")
        weight = parse_decimal(term.get("weight", 1), f"objective.terms[{index}].weight")
        scale = parse_decimal(term.get("scale", 1), f"objective.terms[{index}].scale")
        if weight < 0:
            raise ExplorerError(f"objective.terms[{index}].weight cannot be negative; use direction to invert a term.")
        if scale <= 0:
            raise ExplorerError(f"objective.terms[{index}].scale must be positive.")
        read_value(bytes(WRAM_SIZE), term, f"objective.terms[{index}]")

    goal = document.get("goal")
    forbidden = document.get("forbidden")
    death = document.get("death")
    validate_condition(goal, "goal")
    validate_condition(forbidden, "forbidden")
    validate_condition(death, "death")

    dedup = document.get("dedup")
    if not isinstance(dedup, dict) or not isinstance(dedup.get("selectors"), list) or not dedup["selectors"]:
        raise ExplorerError("dedup.selectors must be a non-empty list.")
    selectors = dedup["selectors"]
    names: set[str] = set()
    for index, selector in enumerate(selectors):
        if not isinstance(selector, dict):
            raise ExplorerError(f"dedup.selectors[{index}] must be an object.")
        name_value = str(selector.get("name", f"field_{index}"))
        if name_value in names:
            raise ExplorerError(f"Duplicate dedup selector name {name_value!r}.")
        names.add(name_value)
        read_value(bytes(WRAM_SIZE), selector, f"dedup.selectors[{index}]")
        bounded_int(selector.get("bucket", 1), f"dedup.selectors[{index}].bucket", 1, 1 << 31)

    search = document.get("search", {})
    if not isinstance(search, dict):
        raise ExplorerError("search must be an object.")
    recipe = Recipe(
        path=path.resolve(),
        document=document,
        actions=actions,
        name=name,
        state_file=state_file,
        state_sha256=state_sha,
        controller=bounded_int(document.get("controller", 1), "controller", 1, 5),
        max_depth=bounded_int(search.get("maxDepth", 4), "search.maxDepth", 1, 20),
        beam_width=bounded_int(search.get("beamWidth", 16), "search.beamWidth", 1, 10000),
        max_nodes=bounded_int(search.get("maxNodes", 250), "search.maxNodes", 1, 10000),
        predicate_check_frames=bounded_int(search.get("predicateCheckFrames", 4), "search.predicateCheckFrames", 1, 10000),
        objective=objective,
        goal=goal,
        forbidden=forbidden,
        death=death,
        dedup_selectors=selectors,
        output_top=bounded_int(document.get("outputTop", 10), "outputTop", 1, 1000),
    )
    maximum_frames = max(action.frames for action in actions) * recipe.max_depth
    if maximum_frames > 100000:
        raise ExplorerError(f"A branch could reach {maximum_frames} frames; the offline safety limit is 100000.")
    return recipe


class BridgeClient:
    def __init__(self, endpoint_path: Path, timeout: float, expected_pid: int):
        try:
            info = json.loads(endpoint_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise ExplorerError(f"Could not read endpoint {endpoint_path}: {exc}") from exc
        host = str(info.get("host", "127.0.0.1"))
        if host not in LOOPBACK_HOSTS:
            raise ExplorerError(f"Refusing non-loopback bridge host {host!r}.")
        if str(info.get("pluginVersion", "")) != BRIDGE_VERSION:
            raise ExplorerError(f"DKCLevelAutomation bridge version must be {BRIDGE_VERSION}; endpoint reports {info.get('pluginVersion')!r}.")
        pid = parse_int(info.get("pid"), "endpoint pid")
        if pid != expected_pid:
            raise ExplorerError(f"Endpoint PID {pid} does not match --expect-pid {expected_pid}.")
        if info.get("protocol") != 1:
            raise ExplorerError(f"DKCLevelAutomation bridge protocol must be 1; endpoint reports {info.get('protocol')!r}.")
        port = bounded_int(info.get("port"), "endpoint port", 1, 65535)
        token = info.get("token")
        if not isinstance(token, str) or not token:
            raise ExplorerError("Endpoint token is missing.")
        self.endpoint_path = endpoint_path.resolve()
        self.host = host
        self.port = port
        self.token = token
        self.pid = pid
        self.timeout = timeout

    @staticmethod
    def _encode(value: Any) -> str:
        if isinstance(value, bool):
            text = "true" if value else "false"
        elif value is None:
            text = ""
        elif isinstance(value, (dict, list)):
            raise ExplorerError("Bridge argument values must be scalar.")
        else:
            text = str(value)
        return base64.b64encode(text.encode("utf-8")).decode("ascii")

    def request(self, command: str, arguments: dict[str, Any]) -> Any:
        request_id = uuid.uuid4().hex
        fields = [request_id, self.token, command]
        for key, value in arguments.items():
            fields.extend([self._encode(key), self._encode(value)])
        wire = "\t".join(fields) + "\n"
        try:
            with socket.create_connection((self.host, self.port), timeout=self.timeout) as connection:
                connection.settimeout(self.timeout)
                connection.sendall(wire.encode("utf-8"))
                chunks = bytearray()
                while b"\n" not in chunks:
                    block = connection.recv(65536)
                    if not block:
                        break
                    chunks.extend(block)
        except OSError as exc:
            raise ExplorerError(f"Bridge request {command!r} failed: {exc}") from exc
        if not chunks:
            raise ExplorerError(f"Bridge closed without responding to {command!r}.")
        try:
            reply = json.loads(bytes(chunks).split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ExplorerError(f"Bridge returned malformed JSON for {command!r}.") from exc
        if not reply.get("ok"):
            raise ExplorerError(f"Bridge rejected {command!r}: {reply.get('error', 'unknown error')}")
        return reply.get("result")


def decode_snapshot(result: Any) -> tuple[bytes, int, str]:
    if not isinstance(result, dict):
        raise ExplorerError("snapshot_wram returned a non-object result.")
    try:
        memory = base64.b64decode(result["data"], validate=True)
        frame = int(result["frame"])
        expected = str(result["sha256"]).upper()
    except (KeyError, TypeError, ValueError) as exc:
        raise ExplorerError("snapshot_wram result is missing data, frame, or SHA-256.") from exc
    if len(memory) != WRAM_SIZE:
        raise ExplorerError(f"snapshot_wram returned {len(memory)} bytes instead of {WRAM_SIZE}.")
    digest = hashlib.sha256(memory).hexdigest().upper()
    if digest != expected:
        raise ExplorerError("snapshot_wram data does not match the bridge SHA-256.")
    if result.get("paused") is not True:
        raise ExplorerError("snapshot_wram reported an unpaused emulator.")
    return memory, frame, digest


def verify_preflight(status: Any) -> None:
    if not isinstance(status, dict):
        raise ExplorerError("Bridge status response is not an object.")
    required_true = ("attached", "loaded", "paused", "frameHook", "inputHook")
    missing = [name for name in required_true if status.get(name) is not True]
    if missing:
        raise ExplorerError("Live preflight requires true status fields: " + ", ".join(missing))
    if status.get("active") is not None:
        raise ExplorerError("Another bridge frame operation is active.")
    schedules = status.get("schedules")
    if not isinstance(schedules, list) or any(not isinstance(item, dict) or item.get("enabled") for item in schedules):
        raise ExplorerError("Live preflight requires all controller schedules to be clear.")


def verify_invincibility_status(path: Path, require_applied: bool) -> dict[str, Any]:
    try:
        status = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ExplorerError(f"Could not read invincibility status {path}: {exc}") from exc
    if not isinstance(status, dict):
        raise ExplorerError("Invincibility status must be a JSON object.")
    if status.get("version") != "0.1.0" or status.get("override") != "BFA2A0=60":
        raise ExplorerError("Invincibility status does not identify DKCDebugInvincibility 0.1.0/BFA2A0=60.")
    applied = status.get("applied") is True
    if applied != require_applied:
        wanted = "applied" if require_applied else "not applied"
        raise ExplorerError(f"Invincibility must be {wanted}, but status reports applied={applied}.")
    if require_applied and (status.get("desiredEnabled") is not True or status.get("romValidated") is not True):
        raise ExplorerError("Applied invincibility also requires desiredEnabled=true and romValidated=true.")
    return {key: value for key, value in status.items() if key not in ("token",)}


def verify_invincibility_rom(status: dict[str, Any], automation_status: dict[str, Any]) -> None:
    invincibility_rom = str(status.get("romPath", "")).strip()
    automation_rom = str(automation_status.get("rom", "")).strip()
    if not invincibility_rom or not automation_rom:
        raise ExplorerError("Invincibility and automation status must both identify the loaded ROM path.")
    left = os.path.normcase(os.path.abspath(invincibility_rom))
    right = os.path.normcase(os.path.abspath(automation_rom))
    if left != right:
        raise ExplorerError(f"Invincibility status ROM {invincibility_rom!r} does not match automation ROM {automation_rom!r}.")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            for block in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as exc:
        raise ExplorerError(f"Could not hash state file {path}: {exc}") from exc
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
            raise ExplorerError(f"Could not stat state file {path}: {exc}") from exc
        if not path.is_file():
            raise ExplorerError(f"External state is not a file: {path}")
        return cls(stat.st_size, stat.st_mtime_ns, sha256_file(path))

    def assert_quick(self, path: Path) -> None:
        stat = path.stat()
        if stat.st_size != self.size or stat.st_mtime_ns != self.modified_ns:
            raise ExplorerError("External state size or modification time changed during the search.")

    def assert_full(self, path: Path) -> None:
        self.assert_quick(path)
        if sha256_file(path) != self.sha256:
            raise ExplorerError("External state SHA-256 changed during the search.")


@dataclass
class Node:
    id: str
    parent_id: str | None
    depth: int
    action_id: str | None
    action_frames: int
    frames: int
    macro: str
    status: str
    score: Decimal = Decimal(0)
    objective_values: dict[str, dict[str, Any]] = field(default_factory=dict)
    dedup_key: str | None = None
    dedup_values: dict[str, int] = field(default_factory=dict)
    root_emulator_frame: int | None = None
    final_emulator_frame: int | None = None
    duplicate_of: str | None = None
    predicate: str | None = None
    goal: bool = False

    def as_json(self) -> dict[str, Any]:
        return {
            "id": self.id,
            "parentId": self.parent_id,
            "depth": self.depth,
            "actionId": self.action_id,
            "actionFrames": self.action_frames,
            "cumulativeFrames": self.frames,
            "macro": self.macro,
            "status": self.status,
            "score": decimal_text(self.score),
            "objectiveValues": self.objective_values,
            "dedupKey": self.dedup_key,
            "dedupValues": self.dedup_values,
            "rootEmulatorFrame": self.root_emulator_frame,
            "finalEmulatorFrame": self.final_emulator_frame,
            "duplicateOf": self.duplicate_of,
            "predicate": self.predicate,
            "goal": self.goal,
        }


class SearchEngine:
    def __init__(self, recipe: Recipe, bridge: Requester, state_path: Path, state_guard: FileFingerprint | None = None):
        self.recipe = recipe
        self.bridge = bridge
        self.state_path = state_path.resolve()
        self.state_guard = state_guard
        self.baseline_memory: bytes | None = None
        self.baseline_frame: int | None = None
        self.baseline_wram_sha256: str | None = None
        self.nodes: list[Node] = []
        self.nodes_by_id: dict[str, Node] = {}
        self.evaluations = 0

    def _request(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        return self.bridge.request(command, arguments or {})

    def _load_root(self) -> tuple[bytes, int, str]:
        if self.state_guard:
            self.state_guard.assert_quick(self.state_path)
        result = self._request("load_state_file", {"path": str(self.state_path)})
        if not isinstance(result, dict) or result.get("loaded") is not True or result.get("paused") is not True or result.get("schedulesCleared") is not True:
            raise ExplorerError("load_state_file did not confirm a paused load with schedules cleared.")
        return decode_snapshot(self._request("snapshot_wram"))

    def _new_id(self) -> str:
        return f"n{len(self.nodes):06d}"

    def _record(self, node: Node) -> Node:
        self.nodes.append(node)
        self.nodes_by_id[node.id] = node
        return node

    def _evaluate(self, parent: Node, action: Action, chain: list[Action]) -> Node:
        assert self.baseline_memory is not None and self.baseline_frame is not None and self.baseline_wram_sha256 is not None
        frames = action_chain_frames(chain)
        macro = macro_from_frames(frames)
        node = Node(self._new_id(), parent.id, parent.depth + 1, action.id, action.frames, len(frames), macro, "evaluating")
        initial, initial_frame, initial_sha = self._load_root()
        if initial_sha != self.baseline_wram_sha256 or initial != self.baseline_memory or initial_frame != self.baseline_frame:
            raise ExplorerError("Reloaded root state does not match the baseline WRAM bytes and emulator frame.")
        node.root_emulator_frame = initial_frame
        scheduled = self._request("schedule", {"controller": self.recipe.controller, "macro": macro})
        if not isinstance(scheduled, dict) or scheduled.get("length") != len(frames) or scheduled.get("exactOverride") is not True:
            raise ExplorerError("schedule did not confirm the exact requested macro length/override.")
        executed = 0
        final_memory = initial
        final_frame = initial_frame
        terminal: str | None = None
        try:
            while executed < len(frames):
                chunk = min(self.recipe.predicate_check_frames, len(frames) - executed)
                result = self._request("run_frames", {"count": chunk})
                if not isinstance(result, dict) or result.get("framesAdvanced") != chunk:
                    raise ExplorerError(f"run_frames did not confirm exactly {chunk} advanced frames.")
                executed += chunk
                final_memory, final_frame, _ = decode_snapshot(self._request("snapshot_wram"))
                if final_frame != initial_frame + executed:
                    raise ExplorerError(f"Exact frame mismatch: root {initial_frame} + {executed} != snapshot {final_frame}.")
                if evaluate_condition(self.recipe.death, final_memory, self.baseline_memory):
                    terminal = "death"
                    break
                if evaluate_condition(self.recipe.forbidden, final_memory, self.baseline_memory):
                    terminal = "forbidden"
                    break
                if evaluate_condition(self.recipe.goal, final_memory, self.baseline_memory):
                    terminal = "goal"
                    break
        finally:
            self._request("clear_schedule", {"controller": self.recipe.controller})
        node.frames = executed
        node.macro = macro_from_frames(frames[:executed])
        node.action_frames = max(0, executed - parent.frames)
        node.final_emulator_frame = final_frame
        if terminal in ("death", "forbidden"):
            node.status = "rejected"
            node.predicate = terminal
            return node
        node.score, node.objective_values = objective_score(final_memory, self.baseline_memory, self.recipe.objective)
        node.dedup_key, node.dedup_values = compact_state(final_memory, self.recipe.dedup_selectors)
        node.status = "accepted"
        node.goal = terminal == "goal"
        return node

    @staticmethod
    def _rank_key(node: Node) -> tuple[Decimal, int, str]:
        return (-node.score, node.frames, node.macro)

    def run(self) -> dict[str, Any]:
        baseline, frame, digest = self._load_root()
        self.baseline_memory = baseline
        self.baseline_frame = frame
        self.baseline_wram_sha256 = digest
        root_score, root_values = objective_score(baseline, baseline, self.recipe.objective)
        root_key, root_dedup = compact_state(baseline, self.recipe.dedup_selectors)
        root = self._record(Node("n000000", None, 0, None, 0, 0, "", "accepted", root_score, root_values, root_key, root_dedup, frame, frame, goal=evaluate_condition(self.recipe.goal, baseline, baseline)))
        visited: dict[str, str] = {root_key: root.id}
        frontier: list[tuple[Node, list[Action]]] = [(root, [])]
        goals: list[Node] = [root] if root.goal else []

        for _depth in range(1, self.recipe.max_depth + 1):
            if goals or self.evaluations >= self.recipe.max_nodes:
                break
            next_frontier: list[tuple[Node, list[Action]]] = []
            depth_goals: list[Node] = []
            exhausted = False
            for parent, chain in frontier:
                for action in self.recipe.actions:
                    if self.evaluations >= self.recipe.max_nodes:
                        exhausted = True
                        break
                    self.evaluations += 1
                    node = self._record(self._evaluate(parent, action, chain + [action]))
                    if node.status != "accepted":
                        continue
                    if node.dedup_key in visited:
                        node.status = "duplicate"
                        node.duplicate_of = visited[node.dedup_key]
                        continue
                    assert node.dedup_key is not None
                    visited[node.dedup_key] = node.id
                    next_frontier.append((node, chain + [action]))
                    if node.goal:
                        depth_goals.append(node)
                if exhausted:
                    break
            if depth_goals:
                goals = sorted(depth_goals, key=self._rank_key)
                break
            next_frontier.sort(key=lambda pair: self._rank_key(pair[0]))
            frontier = next_frontier[: self.recipe.beam_width]
            if not frontier:
                break

        accepted = [node for node in self.nodes if node.status == "accepted" and node.depth > 0]
        ranked = sorted(accepted, key=self._rank_key)
        solution = goals[0] if goals else (ranked[0] if ranked else root)
        return {
            "schema": 1,
            "recipe": self.recipe.name,
            "search": {
                "actions": len(self.recipe.actions),
                "maxDepth": self.recipe.max_depth,
                "beamWidth": self.recipe.beam_width,
                "maxNodes": self.recipe.max_nodes,
                "predicateCheckFrames": self.recipe.predicate_check_frames,
                "evaluatedNodes": self.evaluations,
                "uniqueCompactStates": len(visited),
                "goalFound": bool(goals),
                "solutionNodeId": solution.id,
            },
            "baseline": {"emulatorFrame": frame, "wramSha256": digest, "dedupKey": root_key, "dedupValues": root_dedup},
            "nodes": [node.as_json() for node in self.nodes],
            "rankedNodeIds": [node.id for node in ranked[: self.recipe.output_top]],
            "solutionNodeId": solution.id,
        }


def parent_chain(node: Node, nodes_by_id: dict[str, Node]) -> list[Node]:
    chain: list[Node] = []
    current = node
    while current.parent_id is not None:
        chain.append(current)
        current = nodes_by_id[current.parent_id]
    chain.reverse()
    return chain


def output_recipe(node: Node, engine: SearchEngine, state_sha256: str) -> dict[str, Any]:
    chain = parent_chain(node, engine.nodes_by_id)
    steps: list[dict[str, Any]] = [{"command": "load_state_file", "args": {"path": str(engine.state_path)}}]
    if node.frames:
        steps.extend(
            [
                {"command": "schedule", "args": {"controller": engine.recipe.controller, "macro": node.macro}},
                {"command": "run_frames", "args": {"count": node.frames}},
                {"command": "clear_schedule", "args": {"controller": engine.recipe.controller}},
            ]
        )
    return {
        "schema": 1,
        "sourceRecipe": engine.recipe.name,
        "nodeId": node.id,
        "goal": node.goal,
        "externalState": {"path": str(engine.state_path), "sha256": state_sha256},
        "controller": engine.recipe.controller,
        "exactFrames": node.frames,
        "rootEmulatorFrame": node.root_emulator_frame,
        "finalEmulatorFrame": node.final_emulator_frame,
        "macro": node.macro,
        "score": decimal_text(node.score),
        "objectiveValues": node.objective_values,
        "compactState": {"key": node.dedup_key, "values": node.dedup_values},
        "parentChain": [
            {
                "nodeId": item.id,
                "parentId": item.parent_id,
                "depth": item.depth,
                "actionId": item.action_id,
                "actionFramesExecuted": item.action_frames,
                "cumulativeFrames": item.frames,
            }
            for item in chain
        ],
        "bridgeScript": {"steps": steps},
    }


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, sort_keys=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def validation_summary(recipe: Recipe) -> dict[str, Any]:
    action_rows = []
    for action in recipe.actions:
        frames = action.expand()
        action_rows.append({"id": action.id, "frames": len(frames), "macro": macro_from_frames(frames)})
    return {
        "valid": True,
        "liveBridgeContacted": False,
        "recipe": recipe.name,
        "recipePath": str(recipe.path),
        "configuredState": str(recipe.state_file) if recipe.state_file else None,
        "stateSha256Pinned": recipe.state_sha256 is not None,
        "actionCount": len(recipe.actions),
        "maximumCandidateNodes": recipe.max_nodes,
        "maximumDepth": recipe.max_depth,
        "actions": action_rows,
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    validate = subcommands.add_parser("validate", help="Validate and expand a recipe without reading any endpoint or opening a socket.")
    validate.add_argument("--recipe", type=Path, required=True)

    hash_state = subcommands.add_parser("hash-state", help="Print the SHA-256 used to pin an external state; no bridge access.")
    hash_state.add_argument("state", type=Path)

    search = subcommands.add_parser("search", help="Run an explicitly acknowledged search against an existing paused v0.1.3 bridge.")
    search.add_argument("--recipe", type=Path, required=True)
    search.add_argument("--state", type=Path, help="External state path; overrides recipe state.file.")
    search.add_argument("--state-sha256", help="Expected immutable state SHA-256; overrides recipe state.sha256.")
    search.add_argument("--endpoint", type=Path, required=True, help="Explicit DKCLevelAutomation bridge.json path (never auto-discovered).")
    search.add_argument("--expect-pid", type=int, required=True, help="PID that must exactly match bridge.json.")
    search.add_argument("--ack-live-control", action="store_true", help="Acknowledge that every branch reloads and controls the paused emulator.")
    search.add_argument("--output", type=Path, required=True, help="New or empty directory for the report and route recipes.")
    search.add_argument("--socket-timeout", type=float, default=190.0)
    search.add_argument("--invincibility-status", type=Path, help="Optional read-only DKCDebugInvincibility status.json check.")
    search.add_argument("--require-invincibility", choices=("on", "off"), help="Required applied state when --invincibility-status is supplied.")
    return parser


def ensure_empty_output(path: Path) -> Path:
    result = path.resolve()
    if result.exists():
        if not result.is_dir():
            raise ExplorerError(f"Output is not a directory: {result}")
        if any(result.iterdir()):
            raise ExplorerError(f"Output directory must be empty; refusing to overwrite: {result}")
    else:
        result.mkdir(parents=True)
    return result


def run_live(args: argparse.Namespace) -> int:
    if not args.ack_live_control:
        raise ExplorerError("search requires --ack-live-control after following the README safe live protocol.")
    if args.socket_timeout <= 0:
        raise ExplorerError("--socket-timeout must be positive.")
    recipe = load_recipe(args.recipe.resolve())
    state_path = (args.state.resolve() if args.state else recipe.state_file)
    if state_path is None:
        raise ExplorerError("An external state is required via --state or recipe state.file.")
    state_path = state_path.resolve()
    fingerprint = FileFingerprint.capture(state_path)
    pinned = str(args.state_sha256 or recipe.state_sha256 or "").upper()
    if not re.fullmatch(r"[0-9A-F]{64}", pinned):
        raise ExplorerError("Live search requires a 64-hex pinned state hash via --state-sha256 or state.sha256.")
    if fingerprint.sha256 != pinned:
        raise ExplorerError(f"External state SHA-256 mismatch: expected {pinned}, observed {fingerprint.sha256}.")
    invincibility = None
    if bool(args.invincibility_status) != bool(args.require_invincibility):
        raise ExplorerError("Use --invincibility-status and --require-invincibility together.")
    if args.invincibility_status:
        invincibility = verify_invincibility_status(args.invincibility_status.resolve(), args.require_invincibility == "on")
    output = ensure_empty_output(args.output)
    bridge = BridgeClient(args.endpoint.resolve(), args.socket_timeout, args.expect_pid)
    initial_status = bridge.request("status", {})
    verify_preflight(initial_status)
    if invincibility is not None:
        verify_invincibility_rom(invincibility, initial_status)
    engine = SearchEngine(recipe, bridge, state_path, fingerprint)
    state_control_started = False
    result: dict[str, Any] | None = None
    failure: BaseException | None = None
    cleanup: dict[str, Any] = {"attempted": False, "restoredRoot": False, "scheduleCleared": False}
    try:
        state_control_started = True
        result = engine.run()
    except (Exception, KeyboardInterrupt) as exc:  # retain cleanup/reporting on Ctrl+C too
        failure = exc
    finally:
        if state_control_started:
            cleanup["attempted"] = True
            try:
                restored = bridge.request("load_state_file", {"path": str(state_path)})
                cleanup["restoredRoot"] = isinstance(restored, dict) and restored.get("loaded") is True and restored.get("paused") is True
                cleared = bridge.request("clear_schedule", {"controller": "all"})
                cleanup["scheduleCleared"] = isinstance(cleared, dict)
            except Exception as cleanup_error:
                cleanup["error"] = str(cleanup_error)
        try:
            fingerprint.assert_full(state_path)
            cleanup["externalStateHashUnchanged"] = True
        except Exception as hash_error:
            cleanup["externalStateHashUnchanged"] = False
            cleanup["stateHashError"] = str(hash_error)
            if failure is None:
                failure = hash_error
    if failure is None and not all(cleanup.get(name) is True for name in ("restoredRoot", "scheduleCleared", "externalStateHashUnchanged")):
        failure = ExplorerError(f"Post-search cleanup was incomplete: {cleanup}")
    if failure is not None:
        write_json_atomic(output / "failure.json", {"schema": 1, "failedUtc": utc_now(), "error": str(failure), "cleanup": cleanup})
        raise ExplorerError(f"Search failed: {failure}. Cleanup details: {cleanup}")
    assert result is not None
    try:
        final_status = bridge.request("status", {})
        verify_preflight(final_status)
    except Exception as exc:
        write_json_atomic(output / "failure.json", {"schema": 1, "failedUtc": utc_now(), "error": str(exc), "cleanup": cleanup})
        raise ExplorerError(f"Post-cleanup status verification failed: {exc}") from exc
    result.update(
        {
            "createdUtc": utc_now(),
            "bridge": {"endpoint": str(bridge.endpoint_path), "pid": bridge.pid, "pluginVersion": BRIDGE_VERSION},
            "externalState": {"path": str(state_path), "sha256": fingerprint.sha256, "size": fingerprint.size},
            "initialStatus": initial_status,
            "finalStatus": final_status,
            "invincibilityStatus": invincibility,
            "cleanup": cleanup,
        }
    )
    write_json_atomic(output / "report.json", result)
    selected_ids = result["rankedNodeIds"]
    solution_id = result["solutionNodeId"]
    selected = [engine.nodes_by_id[node_id] for node_id in selected_ids]
    solution = engine.nodes_by_id[solution_id]
    recipes = [output_recipe(node, engine, fingerprint.sha256) for node in selected]
    write_json_atomic(output / "best-recipes.json", {"schema": 1, "recipes": recipes})
    write_json_atomic(output / "solution.recipe.json", output_recipe(solution, engine, fingerprint.sha256))
    print(json.dumps({"report": str(output / "report.json"), "solution": str(output / "solution.recipe.json"), **result["search"]}, indent=2))
    return 0


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "validate":
            print(json.dumps(validation_summary(load_recipe(args.recipe.resolve())), indent=2))
            return 0
        if args.command == "hash-state":
            path = args.state.resolve()
            print(json.dumps({"state": str(path), "sha256": sha256_file(path), "liveBridgeContacted": False}, indent=2))
            return 0
        if args.command == "search":
            return run_live(args)
        raise ExplorerError(f"Unsupported command {args.command!r}.")
    except (ExplorerError, OSError, ValueError, KeyboardInterrupt) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
