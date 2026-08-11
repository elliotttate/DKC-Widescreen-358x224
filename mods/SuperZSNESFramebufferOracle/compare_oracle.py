#!/usr/bin/env python3
"""Command-line entry point for framebuffer oracle image and run comparison."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from oracle_common import OracleError
from oracle_compare import compare_images, compare_runs


def add_thresholds(parser: argparse.ArgumentParser) -> None:
    parser.add_argument(
        "--channel-tolerance", type=int, default=0,
        help="Per-channel absolute delta allowed before a pixel differs (default: exact).",
    )
    parser.add_argument(
        "--max-differing-pixels", type=int, default=0,
        help="Number of differing pixels allowed after tolerance (default: 0).",
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Compare stock and candidate framebuffer oracle output.")
    subparsers = parser.add_subparsers(dest="command", required=True)
    images = subparsers.add_parser("images", help="Compare two individual images per pixel.")
    images.add_argument("stock")
    images.add_argument("candidate")
    images.add_argument("--output", required=True)
    add_thresholds(images)

    runs = subparsers.add_parser("runs", help="Compare and validate two captured oracle runs.")
    runs.add_argument("stock", help="Stock run directory or manifest.json.")
    runs.add_argument("candidate", help="Candidate run directory or manifest.json.")
    runs.add_argument("--output", required=True)
    add_thresholds(runs)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        output = Path(args.output).resolve()
        if args.command == "images":
            if output.exists():
                raise OracleError(f"Output path already exists; refusing to overwrite: {output}")
            report = compare_images(
                Path(args.stock),
                Path(args.candidate),
                output,
                args.channel_tolerance,
                args.max_differing_pixels,
            )
            print(json.dumps({"passed": report["passed"], "report": str(output / "report.json")}, indent=2))
            return 0 if report["passed"] else 2
        summary, exit_code = compare_runs(
            Path(args.stock),
            Path(args.candidate),
            output,
            args.channel_tolerance,
            args.max_differing_pixels,
        )
        print(json.dumps({"outcome": summary["outcome"], "report": str(output / "index.html")}, indent=2))
        return exit_code
    except (OracleError, OSError, ValueError, KeyError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
