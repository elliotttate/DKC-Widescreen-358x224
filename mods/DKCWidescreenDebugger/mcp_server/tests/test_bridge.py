from __future__ import annotations

import json
import socket
import threading
from pathlib import Path

from superzsnes_mcp.bridge import BridgeClient


def test_bridge_wire_round_trip(tmp_path: Path, monkeypatch) -> None:
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    endpoint = tmp_path / "bridge.json"
    endpoint.write_text(json.dumps({"host": "127.0.0.1", "port": port, "token": "test-token"}))
    monkeypatch.setenv("SUPERZSNES_BRIDGE_FILE", str(endpoint))

    received: list[str] = []

    def fake_game() -> None:
        connection, _ = listener.accept()
        with connection:
            line = connection.makefile("r", encoding="utf-8").readline().rstrip("\n")
            received.append(line)
            request_id = line.split("\t", 1)[0]
            reply = {"id": request_id, "ok": True, "result": {"attached": True, "frame": 12}}
            connection.sendall((json.dumps(reply) + "\n").encode())
        listener.close()

    thread = threading.Thread(target=fake_game)
    thread.start()
    result = BridgeClient(timeout=2).call("get_status", reason="camera edge")
    thread.join(timeout=2)

    assert result == {"attached": True, "frame": 12}
    fields = received[0].split("\t")
    assert fields[1:3] == ["test-token", "get_status"]
    assert len(fields) == 5
