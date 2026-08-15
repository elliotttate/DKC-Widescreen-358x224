#!/usr/bin/env python3
"""Replay a DKCPlaytestRecorder bundle through DKCLevelAutomation."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import socket
import tempfile
import uuid
import zipfile
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def request(endpoint: dict, command: str, **arguments: object) -> dict:
    tokens = [uuid.uuid4().hex, str(endpoint["token"]), command]
    for key, value in arguments.items():
        encoded_key = base64.b64encode(str(key).encode("utf-8")).decode("ascii")
        scalar = "true" if value is True else "false" if value is False else str(value)
        encoded_value = base64.b64encode(scalar.encode("utf-8")).decode("ascii")
        tokens.extend((encoded_key, encoded_value))
    payload = ("\t".join(tokens) + "\n").encode("utf-8")
    with socket.create_connection((str(endpoint.get("host", "127.0.0.1")), int(endpoint["port"])), timeout=190) as client:
        client.settimeout(190)
        client.sendall(payload)
        chunks = bytearray()
        while True:
            block = client.recv(65536)
            if not block:
                break
            chunks.extend(block)
            if b"\n" in block:
                break
    if not chunks:
        raise RuntimeError("automation bridge closed without a response")
    response = json.loads(bytes(chunks).split(b"\n", 1)[0].decode("utf-8"))
    if not response.get("ok"):
        raise RuntimeError(response.get("error", "automation bridge rejected request"))
    return response.get("result", {})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--rom", required=True, type=Path)
    parser.add_argument("--endpoint", required=True, type=Path,
                        help="DKCLevelAutomation bridge.json")
    args = parser.parse_args()

    requested_bundle = args.bundle.resolve()
    temporary = None
    if requested_bundle.is_file():
        temporary = tempfile.TemporaryDirectory(prefix="dkc-repro-")
        bundle = Path(temporary.name).resolve()
        with zipfile.ZipFile(requested_bundle, "r") as archive:
            for item in archive.infolist():
                target = (bundle / item.filename).resolve()
                if bundle not in target.parents and target != bundle:
                    raise SystemExit("repro archive contains an unsafe path")
            archive.extractall(bundle)
    else:
        bundle = requested_bundle
    manifest = json.loads((bundle / "manifest.json").read_text(encoding="utf-8"))
    replay = json.loads((bundle / "replay.json").read_text(encoding="utf-8"))
    endpoint = json.loads(args.endpoint.read_text(encoding="utf-8"))
    actual_rom = sha256(args.rom)
    expected_rom = str(manifest["romSha256"]).upper()
    if actual_rom != expected_rom:
        raise SystemExit(f"ROM hash mismatch: expected {expected_rom}, got {actual_rom}")
    if sha256(bundle / "anchor.szst") != str(manifest["anchorStateSha256"]).upper():
        raise SystemExit("anchor.szst hash does not match manifest.json")

    request(endpoint, "load_rom", path=args.rom.resolve(), load_last_state="false")
    request(endpoint, "load_state_file", path=(bundle / "anchor.szst").resolve())
    for item in replay["controllers"]:
        macro = item["macro"]
        if macro:
            request(endpoint, "schedule", controller=item["controller"], macro=macro)
    result = request(endpoint, "run_frames", count=int(replay["frames"]), timeout_ms=180000)
    request(endpoint, "clear_schedule", controller="all")
    observed = request(endpoint, "snapshot_wram")
    expected = str(manifest["reportWramSha256"]).upper()
    actual = str(observed["sha256"]).upper()
    print(json.dumps({
        "frames": result.get("framesAdvanced"),
        "expectedWramSha256": expected,
        "actualWramSha256": actual,
        "exactWramMatch": actual == expected,
    }, indent=2))
    result_code = 0 if actual == expected else 2
    if temporary is not None:
        temporary.cleanup()
    return result_code


if __name__ == "__main__":
    raise SystemExit(main())
