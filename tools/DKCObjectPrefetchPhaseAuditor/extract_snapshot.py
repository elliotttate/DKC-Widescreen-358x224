#!/usr/bin/env python3
"""Extract and verify one raw WRAM frame from a prefetch evidence archive."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import sys
from pathlib import Path
from typing import Any


WRAM_SIZE = 0x20000


def load_index(path: Path) -> dict[int, dict[str, Any]]:
    rows: dict[int, dict[str, Any]] = {}
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        row = json.loads(line)
        frame = int(row["relativeFrame"])
        if frame in rows:
            raise ValueError(f"duplicate relative frame {frame} at index line {line_number}")
        rows[frame] = row
    return rows


def extract(archive: Path, index: Path, relative_frame: int) -> tuple[bytes, dict[str, Any]]:
    rows = load_index(index)
    if relative_frame not in rows:
        raise ValueError(f"relative frame {relative_frame} is absent from {index}")
    row = rows[relative_frame]
    if int(row["length"]) != WRAM_SIZE:
        raise ValueError("index row is not a complete 128 KiB WRAM record")
    with gzip.open(archive, "rb") as handle:
        handle.seek(int(row["uncompressedOffset"]))
        memory = handle.read(WRAM_SIZE)
    if len(memory) != WRAM_SIZE:
        raise ValueError(f"archive ended after {len(memory)} bytes of the requested frame")
    digest = hashlib.sha256(memory).hexdigest().upper()
    if digest != str(row["sha256"]).upper():
        raise ValueError(f"snapshot digest mismatch: index={row['sha256']} extracted={digest}")
    return memory, row


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--index", required=True, type=Path)
    parser.add_argument("--frame", required=True, type=int)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        memory, row = extract(args.archive, args.index, args.frame)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(memory)
        print(json.dumps({"ok": True, "output": str(args.output.resolve()), **row}, indent=2))
        return 0
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
