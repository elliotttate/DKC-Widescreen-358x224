#!/usr/bin/env python3
"""Standard-library CLI for the DKC Level Automation BepInEx bridge."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import socket
import sys
import uuid
from pathlib import Path
from typing import Any


class BridgeError(RuntimeError):
    pass


def endpoint_path(explicit: str | None) -> Path:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit))
    env = os.environ.get("SUPERZSNES_DKC_AUTOMATION_ENDPOINT")
    if env:
        candidates.append(Path(env))
    candidates.extend(
        [
            Path(__file__).resolve().parent.parent / "bridge.json",
            Path.cwd() / "bridge.json",
        ]
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    searched = "\n  ".join(str(p) for p in candidates)
    raise BridgeError(
        "bridge.json was not found. Start SuperZSNES with the plugin installed, "
        "or pass --endpoint. Searched:\n  " + searched
    )


def encode(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


def scalar(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if value is None:
        return ""
    if isinstance(value, (dict, list)):
        raise BridgeError("Bridge argument values must be scalar, not nested JSON.")
    return str(value)


def request(endpoint: Path, command: str, arguments: dict[str, Any], timeout: float) -> Any:
    info = json.loads(endpoint.read_text(encoding="utf-8"))
    request_id = uuid.uuid4().hex
    fields = [request_id, str(info["token"]), command]
    for key, value in arguments.items():
        fields.extend([encode(str(key)), encode(scalar(value))])
    wire = "\t".join(fields) + "\n"
    with socket.create_connection((str(info.get("host", "127.0.0.1")), int(info["port"])), timeout=timeout) as conn:
        conn.settimeout(timeout)
        conn.sendall(wire.encode("utf-8"))
        chunks = bytearray()
        while True:
            block = conn.recv(65536)
            if not block:
                break
            chunks.extend(block)
            if b"\n" in block:
                break
    if not chunks:
        raise BridgeError("The bridge closed the connection without a response.")
    reply = json.loads(bytes(chunks).split(b"\n", 1)[0].decode("utf-8"))
    if not reply.get("ok"):
        raise BridgeError(str(reply.get("error", "Unknown bridge error")))
    return reply.get("result")


def add_timeout(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--timeout-ms", type=int, help="Plugin-side wall-clock timeout for the operation.")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Drive deterministic SuperZSNES DKC tests over localhost.")
    parser.add_argument("--endpoint", help="Path to the runtime bridge.json endpoint file.")
    parser.add_argument("--socket-timeout", type=float, default=190.0, help="Client socket timeout in seconds (default: 190).")
    sub = parser.add_subparsers(dest="subcommand", required=True)

    for name in ("status", "pause", "resume", "cancel"):
        sub.add_parser(name)

    load_rom = sub.add_parser("load-rom")
    load_rom.add_argument("path")
    load_rom.add_argument("--load-last-state", action="store_true")

    load_state = sub.add_parser("load-state")
    state_source = load_state.add_mutually_exclusive_group(required=True)
    state_source.add_argument("--file")
    state_source.add_argument("--suffix")

    schedule = sub.add_parser("schedule")
    schedule.add_argument("--controller", type=int, default=1)
    schedule.add_argument("--macro", required=True, help='Example: "0-29=RIGHT+Y;30=B;31-89=RIGHT"')

    run_macro = sub.add_parser("run-macro")
    run_macro.add_argument("--controller", type=int, default=1)
    run_macro.add_argument("--macro", required=True)
    add_timeout(run_macro)

    clear = sub.add_parser("clear-schedule")
    clear.add_argument("--controller", default="all")
    reset = sub.add_parser("reset-schedule")
    reset.add_argument("--controller", default="all")

    run = sub.add_parser("run")
    run.add_argument("--frames", type=int, required=True)
    add_timeout(run)
    step = sub.add_parser("step")
    step.add_argument("--frames", type=int, default=1)
    add_timeout(step)

    wait = sub.add_parser("wait")
    wait.add_argument("--address", required=True, help="24-bit WRAM address, e.g. 0x7E1234")
    wait.add_argument("--size", type=int, choices=(1, 2, 3, 4), default=1)
    wait.add_argument("--op", choices=("eq", "ne", "lt", "le", "gt", "ge"), default="eq")
    wait.add_argument("--value", required=True)
    wait.add_argument("--mask")
    wait.add_argument("--signed", action="store_true")
    wait.add_argument("--max-frames", type=int, default=3600)
    add_timeout(wait)

    read = sub.add_parser("read")
    read.add_argument("--address", required=True)
    read.add_argument("--size", type=int, choices=(1, 2, 3, 4), default=1)
    read.add_argument("--signed", action="store_true")
    snapshot = sub.add_parser("snapshot-wram")
    snapshot.add_argument("--output", help="Write the atomic 128 KiB WRAM snapshot to this file.")
    write = sub.add_parser("write")
    write.add_argument("--address", required=True)
    write.add_argument("--size", type=int, choices=(1, 2, 3, 4), default=1)
    write.add_argument("--value", required=True)

    script = sub.add_parser("script")
    script.add_argument("path", help="JSON file containing a list of {command, args} steps.")

    raw = sub.add_parser("raw")
    raw.add_argument("command")
    raw.add_argument("pairs", nargs="*", help="Arguments as key=value pairs.")
    return parser


def command_from_args(args: argparse.Namespace) -> tuple[str, dict[str, Any]]:
    name = args.subcommand
    if name in ("status", "pause", "resume", "cancel"):
        return name, {}
    if name == "load-rom":
        return "load_rom", {"path": args.path, "load_last_state": args.load_last_state}
    if name == "load-state":
        return ("load_state_file", {"path": args.file}) if args.file else ("load_state", {"suffix": args.suffix})
    if name in ("schedule", "run-macro"):
        values: dict[str, Any] = {"controller": args.controller, "macro": args.macro}
        if getattr(args, "timeout_ms", None) is not None:
            values["timeout_ms"] = args.timeout_ms
        return name.replace("-", "_"), values
    if name in ("clear-schedule", "reset-schedule"):
        return name.replace("-", "_"), {"controller": args.controller}
    if name in ("run", "step"):
        values = {"count": args.frames}
        if args.timeout_ms is not None:
            values["timeout_ms"] = args.timeout_ms
        return ("run_frames" if name == "run" else "step_frames"), values
    if name == "wait":
        values = {
            "address": args.address,
            "size": args.size,
            "op": args.op,
            "value": args.value,
            "signed": args.signed,
            "max_frames": args.max_frames,
        }
        if args.mask is not None:
            values["mask"] = args.mask
        if args.timeout_ms is not None:
            values["timeout_ms"] = args.timeout_ms
        return "wait_wram", values
    if name == "read":
        return "read_wram", {"address": args.address, "size": args.size, "signed": args.signed}
    if name == "snapshot-wram":
        return "snapshot_wram", {}
    if name == "write":
        return "write_wram", {"address": args.address, "size": args.size, "value": args.value}
    if name == "raw":
        values = {}
        for pair in args.pairs:
            if "=" not in pair:
                raise BridgeError("Raw arguments must use key=value syntax: " + pair)
            key, value = pair.split("=", 1)
            values[key] = value
        return args.command, values
    raise BridgeError("Unsupported CLI command: " + name)


def run_script(path: str, endpoint: Path, timeout: float) -> None:
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    steps = document.get("steps") if isinstance(document, dict) else document
    if not isinstance(steps, list):
        raise BridgeError("Script JSON must be a list, or an object with a steps list.")
    for index, step in enumerate(steps, 1):
        if not isinstance(step, dict) or not isinstance(step.get("command"), str):
            raise BridgeError(f"Script step {index} must contain a string command.")
        values = step.get("args", {k: v for k, v in step.items() if k != "command"})
        if not isinstance(values, dict):
            raise BridgeError(f"Script step {index} args must be an object.")
        result = request(endpoint, step["command"], values, timeout)
        print(json.dumps({"step": index, "command": step["command"], "result": result}, indent=2))


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        endpoint = endpoint_path(args.endpoint)
        if args.subcommand == "script":
            run_script(args.path, endpoint, args.socket_timeout)
        else:
            command, values = command_from_args(args)
            result = request(endpoint, command, values, args.socket_timeout)
            if args.subcommand == "snapshot-wram" and args.output:
                destination = Path(args.output).resolve()
                destination.parent.mkdir(parents=True, exist_ok=True)
                snapshot = base64.b64decode(result["data"], validate=True)
                digest = hashlib.sha256(snapshot).hexdigest().upper()
                if digest != str(result["sha256"]).upper():
                    raise BridgeError("Atomic WRAM snapshot SHA-256 did not match the bridge response.")
                destination.write_bytes(snapshot)
                result = {key: value for key, value in result.items() if key != "data"}
                result["output"] = str(destination)
            print(json.dumps(result, indent=2))
        return 0
    except (BridgeError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
