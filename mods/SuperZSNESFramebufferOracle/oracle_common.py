#!/usr/bin/env python3
"""Shared schema, hashing, and manifest helpers for the framebuffer oracle."""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any, Iterable


TOOL_VERSION = "0.1.0"
SCHEMA_VERSION = 1
IMAGE_FILES = ("frame-composed.png", "frame-main.png")
RAW_ORACLE_FILES = (
    "wram-7e7f.bin",
    "sram.bin",
    "vram.bin",
    "cgram.bin",
    "cgram-frame-start.bin",
    "oam.bin",
    "oam-frame-start.bin",
    "io-registers.bin",
    "cpu-state.json",
    "ppu-state.json",
)
BUTTONS = {
    "B", "Y", "SELECT", "SEL", "START", "ST", "UP", "U", "DOWN", "D",
    "LEFT", "RIGHT", "A", "X", "L", "R", "NONE", "NEUTRAL", "0",
}


class OracleError(RuntimeError):
    pass


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_record(path: Path, root: Path | None = None) -> dict[str, Any]:
    result: dict[str, Any] = {
        "size": path.stat().st_size,
        "sha256": sha256_file(path),
    }
    result["path"] = str(path.relative_to(root) if root else path)
    return result


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as error:
        raise OracleError(f"Could not parse JSON {path}: {error}") from error


def validate_macro(macro: str, required_frames: int) -> None:
    if not isinstance(macro, str) or not macro.strip():
        raise OracleError("macro must be a non-empty string")
    maximum = -1
    for raw in re.split(r"[;,]", macro):
        segment = raw.strip()
        if not segment:
            continue
        if "=" not in segment:
            raise OracleError(f"Invalid macro segment {segment!r}; expected FRAME=BUTTONS")
        frame_range, button_text = (piece.strip() for piece in segment.split("=", 1))
        match = re.fullmatch(r"(\d+)(?:-(\d+))?", frame_range)
        if not match:
            raise OracleError(f"Invalid macro frame range {frame_range!r}")
        first = int(match.group(1))
        last = int(match.group(2) or match.group(1))
        if last < first:
            raise OracleError(f"Macro range ends before it starts: {frame_range!r}")
        maximum = max(maximum, last)
        words = [word for word in re.split(r"[+| ]+", button_text) if word]
        if not words or any(word.upper() not in BUTTONS for word in words):
            raise OracleError(f"Invalid controller buttons in macro segment {segment!r}")
    if required_frames > 0 and maximum < required_frames - 1:
        raise OracleError(
            f"Macro ends at input frame {maximum}, but checkpoint {required_frames} "
            "requires input frames through that point. Extend the macro explicitly."
        )


def validate_recipe(document: Any) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise OracleError("Recipe root must be a JSON object.")
    if document.get("schemaVersion") != SCHEMA_VERSION:
        raise OracleError(f"Recipe schemaVersion must be {SCHEMA_VERSION}.")
    if not isinstance(document.get("suiteId"), str) or not document["suiteId"].strip():
        raise OracleError("Recipe suiteId must be a non-empty string.")
    cases = document.get("cases")
    if not isinstance(cases, list) or not cases:
        raise OracleError("Recipe cases must be a non-empty list.")
    case_ids: set[str] = set()
    for index, case in enumerate(cases):
        prefix = f"cases[{index}]"
        if not isinstance(case, dict):
            raise OracleError(f"{prefix} must be an object.")
        case_id = case.get("id")
        state_key = case.get("stateKey")
        if not isinstance(case_id, str) or not re.fullmatch(r"[A-Za-z0-9_.-]+", case_id):
            raise OracleError(f"{prefix}.id must contain only letters, digits, dot, dash, or underscore.")
        if case_id in case_ids:
            raise OracleError(f"Duplicate case id {case_id!r}.")
        case_ids.add(case_id)
        if not isinstance(state_key, str) or not re.fullmatch(r"[A-Za-z0-9_.-]+", state_key):
            raise OracleError(f"{prefix}.stateKey is invalid.")
        controller = case.get("controller", 1)
        if not isinstance(controller, int) or not 1 <= controller <= 4:
            raise OracleError(f"{prefix}.controller must be 1 through 4.")
        checkpoints = case.get("checkpoints")
        if (
            not isinstance(checkpoints, list)
            or not checkpoints
            or any(not isinstance(value, int) or value < 0 for value in checkpoints)
            or checkpoints != sorted(set(checkpoints))
            or checkpoints[0] != 0
        ):
            raise OracleError(
                f"{prefix}.checkpoints must be sorted unique non-negative integers starting at 0."
            )
        validate_macro(case.get("macro"), checkpoints[-1])
        preferences = case.get("imagePreference", list(IMAGE_FILES))
        if (
            not isinstance(preferences, list)
            or not preferences
            or any(value not in IMAGE_FILES for value in preferences)
        ):
            raise OracleError(f"{prefix}.imagePreference contains an unsupported capture image.")
    return document


def load_recipe(path: Path) -> dict[str, Any]:
    return validate_recipe(load_json(path))


def parse_state_mappings(values: Iterable[str]) -> dict[str, Path]:
    mappings: dict[str, Path] = {}
    for value in values:
        if "=" not in value:
            raise OracleError(f"State mapping must use KEY=PATH syntax: {value!r}")
        key, raw_path = value.split("=", 1)
        key = key.strip()
        if not key or key in mappings:
            raise OracleError(f"State key is empty or repeated: {key!r}")
        mappings[key] = Path(raw_path.strip()).resolve()
    return mappings


def manifest_path(value: Path) -> Path:
    value = value.resolve()
    return value / "manifest.json" if value.is_dir() else value


def checkpoint_map(manifest: dict[str, Any]) -> dict[tuple[str, int], dict[str, Any]]:
    result: dict[tuple[str, int], dict[str, Any]] = {}
    for case in manifest.get("cases", []):
        case_id = str(case.get("id", ""))
        for checkpoint in case.get("checkpoints", []):
            key = (case_id, int(checkpoint["relativeFrame"]))
            if key in result:
                raise OracleError(f"Duplicate checkpoint in manifest: {key}")
            result[key] = checkpoint
    return result
