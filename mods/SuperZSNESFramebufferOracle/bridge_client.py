#!/usr/bin/env python3
"""Small client for the authenticated SuperZSNES localhost bridges."""

from __future__ import annotations

import base64
import json
import os
import socket
import uuid
from pathlib import Path
from typing import Any, Iterable


class BridgeError(RuntimeError):
    pass


def _encode(value: str) -> str:
    return base64.b64encode(value.encode("utf-8")).decode("ascii")


def _scalar(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if value is None:
        return ""
    if isinstance(value, (dict, list)):
        raise BridgeError("Bridge arguments must be scalar values.")
    return str(value)


def resolve_endpoint(
    explicit: str | None,
    environment_name: str,
    fallback_paths: Iterable[Path] = (),
) -> Path:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit))
    environment_value = os.environ.get(environment_name)
    if environment_value:
        candidates.append(Path(environment_value))
    candidates.extend(fallback_paths)
    for candidate in candidates:
        if candidate.is_file():
            return candidate.resolve()
    searched = "\n  ".join(str(path) for path in candidates) or "(no paths supplied)"
    raise BridgeError(
        f"Endpoint file was not found. Pass its path or set {environment_name}. "
        f"Searched:\n  {searched}"
    )


class BridgeClient:
    def __init__(self, endpoint: Path, timeout: float = 190.0):
        self.endpoint = endpoint.resolve()
        self.timeout = timeout

    def request(self, command: str, arguments: dict[str, Any] | None = None) -> Any:
        try:
            info = json.loads(self.endpoint.read_text(encoding="utf-8"))
            host = str(info.get("host", "127.0.0.1"))
            if host not in ("127.0.0.1", "localhost", "::1"):
                raise BridgeError(f"Refusing non-loopback bridge host {host!r}.")
            port = int(info["port"])
            token = str(info["token"])
        except BridgeError:
            raise
        except Exception as error:
            raise BridgeError(f"Could not read endpoint {self.endpoint}: {error}") from error

        request_id = uuid.uuid4().hex
        fields = [request_id, token, command]
        for key, value in (arguments or {}).items():
            fields.extend((_encode(str(key)), _encode(_scalar(value))))
        wire = ("\t".join(fields) + "\n").encode("utf-8")

        try:
            with socket.create_connection((host, port), timeout=self.timeout) as connection:
                connection.settimeout(self.timeout)
                connection.sendall(wire)
                response = bytearray()
                while b"\n" not in response:
                    block = connection.recv(65536)
                    if not block:
                        break
                    response.extend(block)
        except OSError as error:
            raise BridgeError(f"Bridge request {command!r} failed: {error}") from error

        if not response:
            raise BridgeError(f"Bridge closed without replying to {command!r}.")
        try:
            reply = json.loads(bytes(response).split(b"\n", 1)[0].decode("utf-8"))
        except Exception as error:
            raise BridgeError(f"Bridge returned malformed JSON for {command!r}: {error}") from error
        if reply.get("id") != request_id:
            raise BridgeError(f"Bridge response ID did not match request {request_id}.")
        if not reply.get("ok"):
            raise BridgeError(str(reply.get("error", "Unknown bridge error")))
        return reply.get("result")
