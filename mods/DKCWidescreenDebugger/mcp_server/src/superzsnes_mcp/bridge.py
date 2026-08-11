from __future__ import annotations

import base64
import json
import os
import socket
import uuid
from pathlib import Path
from typing import Any


DEFAULT_GAME_DIR = Path(os.environ.get("SUPERZSNES_ROOT", ".deps/SuperZSNES"))


class BridgeError(RuntimeError):
    """Raised when SuperZSNES's local debugger bridge rejects a command."""


def endpoint_path() -> Path:
    explicit = os.environ.get("SUPERZSNES_BRIDGE_FILE")
    if explicit:
        return Path(explicit).expanduser().resolve()
    game_dir = Path(os.environ.get("SUPERZSNES_GAME_DIR", str(DEFAULT_GAME_DIR))).expanduser().resolve()
    return game_dir / "BepInEx" / "plugins" / "DKCWidescreenDebugger" / "bridge.json"


def _encode(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


class BridgeClient:
    def __init__(self, timeout: float = 35.0) -> None:
        self.timeout = timeout

    def endpoint(self) -> dict[str, Any]:
        path = endpoint_path()
        if not path.is_file():
            raise BridgeError(
                f"SuperZSNES bridge endpoint not found at {path}. "
                "Start the modded game and load the BepInEx plugin first."
            )
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise BridgeError(f"Could not read bridge endpoint {path}: {exc}") from exc
        for field in ("host", "port", "token"):
            if field not in data:
                raise BridgeError(f"Bridge endpoint is missing '{field}': {path}")
        return data

    def call(self, command: str, **arguments: Any) -> Any:
        endpoint = self.endpoint()
        request_id = uuid.uuid4().hex
        fields = [request_id, str(endpoint["token"]), command]
        for key, value in arguments.items():
            if value is None:
                continue
            if isinstance(value, bool):
                value = "true" if value else "false"
            fields.extend((_encode(str(key)), _encode(str(value))))
        wire = "\t".join(fields) + "\n"

        try:
            with socket.create_connection((str(endpoint["host"]), int(endpoint["port"])), self.timeout) as sock:
                sock.settimeout(self.timeout)
                sock.sendall(wire.encode("utf-8"))
                response = b""
                while b"\n" not in response:
                    block = sock.recv(65536)
                    if not block:
                        break
                    response += block
                    if len(response) > 16 * 1024 * 1024:
                        raise BridgeError("Bridge response exceeded the 16 MiB client limit.")
        except (OSError, TimeoutError) as exc:
            raise BridgeError(
                f"Could not reach SuperZSNES at {endpoint['host']}:{endpoint['port']}: {exc}"
            ) from exc

        if not response:
            raise BridgeError("SuperZSNES closed the bridge connection without a response.")
        try:
            message = json.loads(response.split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise BridgeError(f"Malformed response from SuperZSNES: {exc}") from exc
        if message.get("id") != request_id:
            raise BridgeError("Bridge response ID did not match the request ID.")
        if not message.get("ok"):
            raise BridgeError(str(message.get("error", "Unknown bridge failure")))
        return message.get("result")


client = BridgeClient()
