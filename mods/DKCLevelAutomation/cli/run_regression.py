#!/usr/bin/env python3
"""Run deterministic DKC regression recipes and correlate TilemapInspector captures."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from dkc_level_cli import BridgeError, endpoint_path, request


TOKEN = re.compile(r"\$\{([A-Z][A-Z0-9_]*)\}")


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_name(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-.")
    return cleaned or "checkpoint"


def locate_recipe(value: str) -> Path:
    direct = Path(value)
    root = Path(__file__).resolve().parent.parent / "recipes"
    candidates = [direct, root / value, root / f"{value}.json"]
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    raise BridgeError("Recipe was not found. Tried: " + ", ".join(str(p) for p in candidates))


def tilemap_endpoint_path(explicit: str | None) -> Path:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit))
    env = os.environ.get("SUPERZSNES_TILEMAP_INSPECTOR_ENDPOINT")
    if env:
        candidates.append(Path(env))
    plugin_dir = Path(__file__).resolve().parent.parent
    candidates.extend(
        [
            plugin_dir.parent / "DKCTilemapInspector" / "bridge.json",
            Path.cwd() / "tilemap-bridge.json",
        ]
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    raise BridgeError(
        "DKCTilemapInspector bridge.json was not found. Install/start that plugin or pass "
        "--tilemap-endpoint. Searched: " + ", ".join(str(p) for p in candidates)
    )


def substitute(value: Any, variables: dict[str, str]) -> Any:
    if isinstance(value, str):
        def replace(match: re.Match[str]) -> str:
            name = match.group(1)
            if name not in variables or variables[name] == "":
                raise BridgeError(f"Recipe requires variable {name}; pass the corresponding option or --var {name}=value.")
            return variables[name]
        return TOKEN.sub(replace, value)
    if isinstance(value, list):
        return [substitute(item, variables) for item in value]
    if isinstance(value, dict):
        return {key: substitute(item, variables) for key, item in value.items()}
    return value


def validate_recipe(document: Any, path: Path) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise BridgeError(f"{path}: recipe root must be an object.")
    if document.get("schema") != 1:
        raise BridgeError(f"{path}: unsupported or missing schema; expected 1.")
    if not isinstance(document.get("name"), str) or not document["name"]:
        raise BridgeError(f"{path}: recipe needs a non-empty name.")
    if not isinstance(document.get("steps"), list) or not document["steps"]:
        raise BridgeError(f"{path}: recipe needs a non-empty steps list.")
    watches = document.get("watches", [])
    if not isinstance(watches, list):
        raise BridgeError(f"{path}: watches must be a list.")
    names: set[str] = set()
    for index, watch in enumerate(watches, 1):
        if not isinstance(watch, dict) or not isinstance(watch.get("name"), str):
            raise BridgeError(f"{path}: watch {index} needs a name.")
        if watch["name"] in names:
            raise BridgeError(f"{path}: duplicate watch name {watch['name']}.")
        names.add(watch["name"])
        if not isinstance(watch.get("address"), str) or watch.get("size") not in (1, 2, 3, 4):
            raise BridgeError(f"{path}: watch {watch['name']} needs an address and size 1-4.")
    for index, step in enumerate(document["steps"], 1):
        if not isinstance(step, dict) or step.get("kind") not in ("automation", "checkpoint", "boundary_scan"):
            raise BridgeError(f"{path}: step {index} has an unsupported kind.")
        if step["kind"] == "automation" and not isinstance(step.get("command"), str):
            raise BridgeError(f"{path}: automation step {index} needs a command.")
        if step["kind"] == "checkpoint" and not isinstance(step.get("label"), str):
            raise BridgeError(f"{path}: checkpoint step {index} needs a label.")
        if step["kind"] == "checkpoint" and "expect" in step:
            expected = step["expect"]
            if not isinstance(expected, dict) or not expected:
                raise BridgeError(f"{path}: checkpoint step {index} expect must be a non-empty object.")
            unknown = [name for name in expected if name not in names]
            if unknown:
                raise BridgeError(f"{path}: checkpoint step {index} expects unknown watches: {unknown}.")
            for name, condition in expected.items():
                if isinstance(condition, dict):
                    if condition.get("op", "eq") not in ("eq", "ne", "lt", "le", "gt", "ge"):
                        raise BridgeError(f"{path}: checkpoint step {index} watch {name} has an unsupported operator.")
                    if "value" not in condition:
                        raise BridgeError(f"{path}: checkpoint step {index} watch {name} needs an expected value.")
                elif not isinstance(condition, (int, str)):
                    raise BridgeError(f"{path}: checkpoint step {index} watch {name} expectation must be a value or object.")
        if step["kind"] == "boundary_scan":
            selected = step.get("watches")
            if not isinstance(selected, list) or not selected:
                raise BridgeError(f"{path}: boundary_scan step {index} needs watch names.")
            unknown = [name for name in selected if name not in names]
            if unknown:
                raise BridgeError(f"{path}: boundary_scan step {index} references unknown watches: {unknown}.")
            if not isinstance(step.get("frames"), int) or step["frames"] < 1:
                raise BridgeError(f"{path}: boundary_scan step {index} needs a positive frames count.")
    return document


class RegressionRunner:
    def __init__(
        self,
        recipe: dict[str, Any],
        recipe_path: Path,
        automation_endpoint: Path,
        tilemap_endpoint: Path | None,
        output: Path,
        socket_timeout: float,
    ) -> None:
        self.recipe = recipe
        self.recipe_path = recipe_path
        self.automation_endpoint = automation_endpoint
        self.tilemap_endpoint = tilemap_endpoint
        self.output = output
        self.socket_timeout = socket_timeout
        self.checkpoints: list[dict[str, Any]] = []
        self.events: list[dict[str, Any]] = []
        self.watch_map = {watch["name"]: watch for watch in recipe.get("watches", [])}
        self.manifest: dict[str, Any] = {
            "schema": 1,
            "recipe": recipe["name"],
            "description": recipe.get("description", ""),
            "recipePath": str(recipe_path),
            "startedUtc": utc_now(),
            "completedUtc": None,
            "automationEndpoint": str(automation_endpoint),
            "tilemapEndpoint": str(tilemap_endpoint) if tilemap_endpoint else None,
            "checkpoints": self.checkpoints,
            "events": self.events,
        }

    def automation(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        return request(self.automation_endpoint, command, arguments or {}, self.socket_timeout)

    def tilemap(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        if self.tilemap_endpoint is None:
            return None
        return request(self.tilemap_endpoint, command, arguments or {}, self.socket_timeout)

    def persist(self) -> None:
        self.output.mkdir(parents=True, exist_ok=True)
        (self.output / "manifest.json").write_text(json.dumps(self.manifest, indent=2) + "\n", encoding="utf-8")
        with (self.output / "events.jsonl").open("w", encoding="utf-8", newline="\n") as handle:
            for event in self.events:
                handle.write(json.dumps(event, separators=(",", ":")) + "\n")

    def read_watch(self, watch: dict[str, Any]) -> dict[str, Any]:
        result = self.automation(
            "read_wram",
            {
                "address": watch["address"],
                "size": watch["size"],
                "signed": bool(watch.get("signed", False)),
            },
        )
        value = int(result["value"])
        record: dict[str, Any] = {
            "name": watch["name"],
            "address": result["address"],
            "size": result["size"],
            "signed": bool(watch.get("signed", False)),
            "value": value,
            "valueHex": result["valueHex"],
            "source": watch.get("source"),
        }
        if watch.get("units") == "pixels":
            record["pixel"] = value
            for modulus in (8, 16, 32):
                record[f"bucket{modulus}"] = value // modulus
                record[f"within{modulus}"] = value % modulus
        return record

    def sample_watches(self, selected: list[str] | None = None) -> dict[str, dict[str, Any]]:
        names = selected or list(self.watch_map)
        return {name: self.read_watch(self.watch_map[name]) for name in names}

    @staticmethod
    def expected_integer(value: Any) -> int:
        if isinstance(value, bool):
            raise BridgeError("Boolean values are not valid WRAM expectations.")
        if isinstance(value, int):
            return value
        if isinstance(value, str):
            return int(value, 0)
        raise BridgeError(f"Unsupported WRAM expectation value: {value!r}")

    @classmethod
    def evaluate_expectations(
        cls, watches: dict[str, dict[str, Any]], expected: dict[str, Any] | None
    ) -> list[dict[str, Any]]:
        if not expected:
            return []
        operators = {
            "eq": lambda actual, wanted: actual == wanted,
            "ne": lambda actual, wanted: actual != wanted,
            "lt": lambda actual, wanted: actual < wanted,
            "le": lambda actual, wanted: actual <= wanted,
            "gt": lambda actual, wanted: actual > wanted,
            "ge": lambda actual, wanted: actual >= wanted,
        }
        results: list[dict[str, Any]] = []
        for name, raw_condition in expected.items():
            condition = raw_condition if isinstance(raw_condition, dict) else {"value": raw_condition}
            operator = str(condition.get("op", "eq"))
            wanted = cls.expected_integer(condition["value"])
            actual = int(watches[name]["value"])
            mask = cls.expected_integer(condition["mask"]) if "mask" in condition else None
            compared_actual = actual & mask if mask is not None else actual
            compared_wanted = wanted & mask if mask is not None else wanted
            passed = bool(operators[operator](compared_actual, compared_wanted))
            results.append(
                {
                    "watch": name,
                    "op": operator,
                    "expected": wanted,
                    "expectedHex": f"0x{wanted:X}",
                    "actual": actual,
                    "actualHex": f"0x{actual:X}",
                    "mask": mask,
                    "passed": passed,
                }
            )
        return results

    def checkpoint(
        self,
        label: str,
        capture: bool = True,
        extra: dict[str, Any] | None = None,
        expected: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        status = self.automation("status")
        watches = self.sample_watches()
        assertions = self.evaluate_expectations(watches, expected)
        reason = safe_name(f"{self.recipe['name']}-{label}")
        tilemap_status = self.tilemap("status") if self.tilemap_endpoint else None
        tilemap_capture = None
        if capture and self.tilemap_endpoint:
            tilemap_capture = self.tilemap(
                "capture",
                {"reason": reason, "layers": str(self.recipe.get("tilemapLayers", "1,2"))},
            )
        record: dict[str, Any] = {
            "sequence": len(self.checkpoints) + 1,
            "label": label,
            "reason": reason,
            "capturedUtc": utc_now(),
            "frame": status.get("frame"),
            "automation": status,
            "watches": watches,
            "assertions": assertions,
            "tilemapStatus": tilemap_status,
            "tilemapCapture": tilemap_capture,
        }
        if extra:
            record["boundary"] = extra
        self.checkpoints.append(record)
        self.events.append(
            {
                "type": "checkpoint",
                "sequence": record["sequence"],
                "label": label,
                "frame": record["frame"],
                "tilemapManifest": tilemap_capture.get("manifest") if isinstance(tilemap_capture, dict) else None,
                "capturedUtc": record["capturedUtc"],
            }
        )
        self.output.mkdir(parents=True, exist_ok=True)
        filename = f"checkpoint-{record['sequence']:02d}-{safe_name(label)}.json"
        (self.output / filename).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
        self.persist()
        print(f"checkpoint {record['sequence']:02d}: {label} (frame {record['frame']})")
        failures = [assertion for assertion in assertions if not assertion["passed"]]
        if failures:
            detail = ", ".join(
                f"{item['watch']} actual={item['actualHex']} {item['op']} expected={item['expectedHex']}"
                for item in failures
            )
            raise BridgeError(f"Checkpoint {label!r} assertion failed: {detail}")
        return record

    def boundary_scan(self, step: dict[str, Any]) -> None:
        selected = list(step["watches"])
        modulus = int(step.get("modulus", 8))
        capture_limit = int(step.get("captureLimit", 16))
        label_prefix = str(step.get("labelPrefix", f"boundary-{modulus}"))
        previous = self.sample_watches(selected)
        captures = 0
        for relative_frame in range(1, int(step["frames"]) + 1):
            self.automation("step_frames", {"count": 1, "timeout_ms": int(step.get("timeoutMs", 10000))})
            current = self.sample_watches(selected)
            crossings: list[dict[str, Any]] = []
            for name in selected:
                old_value = int(previous[name]["value"])
                new_value = int(current[name]["value"])
                old_bucket = old_value // modulus
                new_bucket = new_value // modulus
                if new_bucket != old_bucket:
                    crossings.append(
                        {
                            "watch": name,
                            "modulus": modulus,
                            "fromValue": old_value,
                            "toValue": new_value,
                            "fromBucket": old_bucket,
                            "toBucket": new_bucket,
                            "relativeFrame": relative_frame,
                        }
                    )
            if crossings and captures < capture_limit:
                names = "-".join(item["watch"] for item in crossings)
                self.checkpoint(f"{label_prefix}-f{relative_frame:03d}-{names}", True, {"crossings": crossings})
                captures += 1
            previous = current
        self.events.append(
            {
                "type": "boundary_scan_complete",
                "frames": step["frames"],
                "modulus": modulus,
                "captures": captures,
                "captureLimit": capture_limit,
                "completedUtc": utc_now(),
            }
        )
        self.persist()

    def run(self) -> None:
        self.output.mkdir(parents=True, exist_ok=True)
        self.persist()
        try:
            for index, step in enumerate(self.recipe["steps"], 1):
                kind = step["kind"]
                if kind == "automation":
                    result = self.automation(step["command"], step.get("args", {}))
                    self.events.append(
                        {
                            "type": "automation",
                            "step": index,
                            "command": step["command"],
                            "result": result,
                            "completedUtc": utc_now(),
                        }
                    )
                    self.persist()
                elif kind == "checkpoint":
                    self.checkpoint(
                        step["label"],
                        bool(step.get("capture", True)),
                        expected=step.get("expect"),
                    )
                elif kind == "boundary_scan":
                    self.boundary_scan(step)
            self.manifest["completedUtc"] = utc_now()
            self.manifest["ok"] = True
            self.persist()
        except Exception as exc:
            self.manifest["completedUtc"] = utc_now()
            self.manifest["ok"] = False
            self.manifest["error"] = str(exc)
            self.persist()
            raise


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Run a DKC regression recipe with correlated TilemapInspector captures.")
    parser.add_argument("--recipe", required=True, help="Recipe name or JSON path.")
    parser.add_argument("--rom", help="Value for ${ROM}.")
    parser.add_argument("--state", help="Value for ${STATE}.")
    parser.add_argument("--var", action="append", default=[], help="Additional NAME=value recipe variable.")
    parser.add_argument("--automation-endpoint", help="DKCLevelAutomation bridge.json path.")
    parser.add_argument("--tilemap-endpoint", help="DKCTilemapInspector bridge.json path.")
    parser.add_argument("--no-tilemap", action="store_true", help="Record automation/WRAM checkpoints without invoking TilemapInspector.")
    parser.add_argument("--output", help="Output directory; defaults to RegressionRuns under the plugin folder.")
    parser.add_argument("--socket-timeout", type=float, default=190.0)
    parser.add_argument("--validate-only", action="store_true", help="Validate and expand the recipe without connecting to either plugin.")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        recipe_path = locate_recipe(args.recipe)
        raw = validate_recipe(json.loads(recipe_path.read_text(encoding="utf-8")), recipe_path)
        variables = {"ROM": args.rom or "", "STATE": args.state or ""}
        for assignment in args.var:
            if "=" not in assignment:
                raise BridgeError("--var must use NAME=value syntax: " + assignment)
            key, value = assignment.split("=", 1)
            variables[key.upper()] = value
        recipe = validate_recipe(substitute(raw, variables), recipe_path)
        if args.validate_only:
            print(json.dumps({"ok": True, "recipe": recipe["name"], "steps": len(recipe["steps"]), "watches": len(recipe.get("watches", []))}, indent=2))
            return 0

        automation_endpoint = endpoint_path(args.automation_endpoint)
        tilemap_endpoint = None if args.no_tilemap else tilemap_endpoint_path(args.tilemap_endpoint)
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        output = Path(args.output).resolve() if args.output else (
            Path(__file__).resolve().parent.parent / "RegressionRuns" / f"{safe_name(recipe['name'])}-{timestamp}"
        )
        runner = RegressionRunner(recipe, recipe_path, automation_endpoint, tilemap_endpoint, output, args.socket_timeout)
        runner.run()
        print(json.dumps({"ok": True, "output": str(output), "checkpoints": len(runner.checkpoints)}, indent=2))
        return 0
    except (BridgeError, OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
