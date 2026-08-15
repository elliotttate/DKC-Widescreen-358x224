#!/usr/bin/env python3
"""File-control client for the debug-only DKC invincibility plugin."""

import argparse
import json
import os
import pathlib
import sys
import time


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("enable", "disable", "status"))
    parser.add_argument("--plugin-dir", help="DKCDebugInvincibility runtime directory")
    parser.add_argument("--timeout", type=float, default=3.0)
    args = parser.parse_args()

    configured_root = args.plugin_dir or os.environ.get("SUPERZSNES_DKC_INVINCIBILITY_DIR")
    root = pathlib.Path(configured_root).expanduser() if configured_root else pathlib.Path(__file__).resolve().parent.parent
    status_path = root / "status.json"

    if args.command == "status":
        if not status_path.exists():
            raise SystemExit(f"status.json does not exist yet: {status_path}")
        print(json.dumps(json.loads(status_path.read_text(encoding="utf-8")), indent=2))
        return 0

    root.mkdir(parents=True, exist_ok=True)
    prior_mtime = status_path.stat().st_mtime_ns if status_path.exists() else -1
    request = root / f"{args.command}.request"
    temporary = request.with_suffix(request.suffix + ".tmp")
    temporary.write_text(time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), encoding="ascii")
    os.replace(temporary, request)

    deadline = time.monotonic() + max(0.0, args.timeout)
    wanted = args.command == "enable"
    while time.monotonic() <= deadline:
        if status_path.exists() and status_path.stat().st_mtime_ns != prior_mtime:
            data = json.loads(status_path.read_text(encoding="utf-8"))
            if data.get("desiredEnabled") == wanted:
                print(json.dumps(data, indent=2))
                return 0 if (not wanted or data.get("applied")) else 2
        time.sleep(0.05)
    raise SystemExit(f"Timed out waiting for {request.name} to be consumed. Is SuperZSNES running with the plugin loaded?")


if __name__ == "__main__":
    sys.exit(main())
