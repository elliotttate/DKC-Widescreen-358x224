#!/usr/bin/env python3
"""Pixel and raw-state comparison engine for SuperZSNES framebuffer oracle runs."""

from __future__ import annotations

import html
import math
from pathlib import Path
from typing import Any

from oracle_common import (
    OracleError,
    RAW_ORACLE_FILES,
    checkpoint_map,
    file_record,
    load_json,
    manifest_path,
    write_json,
)


def _pillow() -> tuple[Any, Any]:
    try:
        from PIL import Image, ImageDraw
    except ImportError as error:
        raise OracleError("Pillow is required; run: python -m pip install -r requirements.txt") from error
    return Image, ImageDraw


def compare_images(
    stock_path: Path,
    candidate_path: Path,
    output_directory: Path,
    channel_tolerance: int = 0,
    max_differing_pixels: int = 0,
) -> dict[str, Any]:
    if not 0 <= channel_tolerance <= 255:
        raise OracleError("channel_tolerance must be 0 through 255.")
    if max_differing_pixels < 0:
        raise OracleError("max_differing_pixels cannot be negative.")
    stock_path = stock_path.resolve()
    candidate_path = candidate_path.resolve()
    if not stock_path.is_file() or not candidate_path.is_file():
        raise OracleError(f"Image does not exist: {stock_path} or {candidate_path}")
    output_directory.mkdir(parents=True, exist_ok=False)
    Image, _ = _pillow()
    with Image.open(stock_path) as opened:
        stock = opened.convert("RGBA")
    with Image.open(candidate_path) as opened:
        candidate = opened.convert("RGBA")

    stock.save(output_directory / "stock.png")
    candidate.save(output_directory / "candidate.png")
    report: dict[str, Any] = {
        "stock": file_record(stock_path),
        "candidate": file_record(candidate_path),
        "thresholds": {
            "channelTolerance": channel_tolerance,
            "maxDifferingPixels": max_differing_pixels,
        },
        "stockDimensions": {"width": stock.width, "height": stock.height},
        "candidateDimensions": {"width": candidate.width, "height": candidate.height},
    }
    if stock.size != candidate.size:
        report.update(
            {
                "compatibleDimensions": False,
                "exactMatch": False,
                "passed": False,
                "error": "Framebuffer dimensions differ; images were not resized.",
            }
        )
        write_json(output_directory / "report.json", report)
        return report

    channel_names = ("red", "green", "blue", "alpha")
    channel_sum = [0, 0, 0, 0]
    channel_sum_sq = [0, 0, 0, 0]
    channel_max = [0, 0, 0, 0]
    channel_nonzero = [0, 0, 0, 0]
    channel_over_tolerance = [0, 0, 0, 0]
    exact_differing_pixels = 0
    differing_pixels = 0
    minimum_x = stock.width
    minimum_y = stock.height
    maximum_x = -1
    maximum_y = -1
    diff_pixels: list[tuple[int, int, int, int]] = []
    heat_pixels: list[tuple[int, int, int, int]] = []

    stock_bytes = stock.tobytes()
    candidate_bytes = candidate.tobytes()
    for index in range(stock.width * stock.height):
        offset = index * 4
        deltas = [
            abs(stock_bytes[offset + channel] - candidate_bytes[offset + channel])
            for channel in range(4)
        ]
        exact_difference = any(delta > 0 for delta in deltas)
        threshold_difference = any(delta > channel_tolerance for delta in deltas)
        if exact_difference:
            exact_differing_pixels += 1
        if threshold_difference:
            differing_pixels += 1
            x = index % stock.width
            y = index // stock.width
            minimum_x = min(minimum_x, x)
            minimum_y = min(minimum_y, y)
            maximum_x = max(maximum_x, x)
            maximum_y = max(maximum_y, y)
        for channel, delta in enumerate(deltas):
            channel_sum[channel] += delta
            channel_sum_sq[channel] += delta * delta
            channel_max[channel] = max(channel_max[channel], delta)
            if delta:
                channel_nonzero[channel] += 1
            if delta > channel_tolerance:
                channel_over_tolerance[channel] += 1

        maximum_delta = max(deltas)
        if maximum_delta:
            amplified = [min(255, delta * 4) for delta in deltas[:3]]
            if not any(amplified):
                amplified = [255, 0, 255]
            diff_pixels.append((amplified[0], amplified[1], amplified[2], 255))
            intensity = max(32, min(255, maximum_delta * 4))
            heat_pixels.append((intensity, min(255, intensity * 2), 0, 255))
        else:
            diff_pixels.append((0, 0, 0, 255))
            heat_pixels.append((0, 0, 0, 255))

    total_pixels = stock.width * stock.height
    sample_count = total_pixels * 4
    diff_image = Image.new("RGBA", stock.size)
    diff_image.putdata(diff_pixels)
    diff_image.save(output_directory / "diff.png")
    heat_image = Image.new("RGBA", stock.size)
    heat_image.putdata(heat_pixels)
    heat_image.save(output_directory / "heatmap.png")
    overlay = Image.blend(stock, candidate, 0.5)
    overlay.save(output_directory / "overlay.png")

    channels = {}
    for index, name in enumerate(channel_names):
        channels[name] = {
            "maxAbsoluteDelta": channel_max[index],
            "meanAbsoluteDelta": channel_sum[index] / total_pixels if total_pixels else 0.0,
            "rmse": math.sqrt(channel_sum_sq[index] / total_pixels) if total_pixels else 0.0,
            "nonzeroPixelCount": channel_nonzero[index],
            "overTolerancePixelCount": channel_over_tolerance[index],
        }
    passed = differing_pixels <= max_differing_pixels
    report.update(
        {
            "compatibleDimensions": True,
            "totalPixels": total_pixels,
            "exactDifferingPixels": exact_differing_pixels,
            "differingPixels": differing_pixels,
            "differingPercent": (100.0 * differing_pixels / total_pixels) if total_pixels else 0.0,
            "differenceBoundingBox": (
                [minimum_x, minimum_y, maximum_x + 1, maximum_y + 1]
                if differing_pixels
                else None
            ),
            "maxAbsoluteChannelDelta": max(channel_max),
            "meanAbsoluteChannelDelta": sum(channel_sum) / sample_count if sample_count else 0.0,
            "rmse": math.sqrt(sum(channel_sum_sq) / sample_count) if sample_count else 0.0,
            "channels": channels,
            "exactMatch": exact_differing_pixels == 0,
            "passed": passed,
            "artifacts": {
                "stock": "stock.png",
                "candidate": "candidate.png",
                "diff": "diff.png",
                "heatmap": "heatmap.png",
                "overlay": "overlay.png",
            },
        }
    )
    write_json(output_directory / "report.json", report)
    return report


def _sha(record: Any) -> str | None:
    return record.get("sha256") if isinstance(record, dict) else None


def _case_map(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {str(case.get("id")): case for case in manifest.get("cases", [])}


def _raw_input_differences(
    stock_checkpoint: dict[str, Any], candidate_checkpoint: dict[str, Any]
) -> list[dict[str, Any]]:
    stock_files = stock_checkpoint.get("rawFiles", {})
    candidate_files = candidate_checkpoint.get("rawFiles", {})
    differences: list[dict[str, Any]] = []
    for name in RAW_ORACLE_FILES:
        stock_hash = _sha(stock_files.get(name))
        candidate_hash = _sha(candidate_files.get(name))
        if stock_hash != candidate_hash or stock_hash is None:
            differences.append(
                {"file": name, "stockSha256": stock_hash, "candidateSha256": candidate_hash}
            )
    return differences


def _write_html(output_directory: Path, summary: dict[str, Any]) -> None:
    rows = []
    for item in summary["comparisons"]:
        artifact = item.get("artifactDirectory")
        links = ""
        if artifact:
            links = " ".join(
                f'<a href="{html.escape(artifact)}/{name}">{label}</a>'
                for name, label in (
                    ("stock.png", "stock"),
                    ("candidate.png", "candidate"),
                    ("diff.png", "diff"),
                    ("heatmap.png", "heat"),
                    ("report.json", "json"),
                )
                if (output_directory / artifact / name).is_file()
            )
        rows.append(
            "<tr>"
            f"<td>{html.escape(item['caseId'])}</td>"
            f"<td>{item['relativeFrame']}</td>"
            f"<td>{html.escape(item['outcome'])}</td>"
            f"<td>{item.get('differingPixels', '')}</td>"
            f"<td>{html.escape(str(item.get('rawInputDifferences', '')))}</td>"
            f"<td>{links}</td>"
            "</tr>"
        )
    body = f"""<!doctype html>
<html><head><meta charset="utf-8"><title>Framebuffer oracle report</title>
<style>body{{font:14px system-ui;margin:2rem}}table{{border-collapse:collapse}}th,td{{border:1px solid #bbb;padding:.4rem;vertical-align:top}}.pass{{color:#087830}}.fail{{color:#a01414}}</style>
</head><body>
<h1>Framebuffer oracle: {html.escape(str(summary['outcome']))}</h1>
<p>Valid comparison: {summary['validComparison']}; passed: {summary['passed']}</p>
<p>Stock: {html.escape(str(summary['stockManifest']))}<br>Candidate: {html.escape(str(summary['candidateManifest']))}</p>
<h2>Suite checks</h2><pre>{html.escape(str(summary['suiteInputDifferences']))}</pre>
<h2>Checkpoints</h2><table><thead><tr><th>Case</th><th>Relative frame</th><th>Outcome</th><th>Differing pixels</th><th>Raw input differences</th><th>Artifacts</th></tr></thead>
<tbody>{''.join(rows)}</tbody></table></body></html>"""
    (output_directory / "index.html").write_text(body, encoding="utf-8")


def compare_runs(
    stock_value: Path,
    candidate_value: Path,
    output_directory: Path,
    channel_tolerance: int = 0,
    max_differing_pixels: int = 0,
) -> tuple[dict[str, Any], int]:
    stock_manifest_path = manifest_path(stock_value)
    candidate_manifest_path = manifest_path(candidate_value)
    stock_manifest = load_json(stock_manifest_path)
    candidate_manifest = load_json(candidate_manifest_path)
    if output_directory.exists():
        raise OracleError(f"Output path already exists; refusing to overwrite: {output_directory}")
    output_directory.mkdir(parents=True)

    suite_differences: list[dict[str, Any]] = []
    for field in ("schemaVersion", "suiteId"):
        if stock_manifest.get(field) != candidate_manifest.get(field):
            suite_differences.append(
                {"field": field, "stock": stock_manifest.get(field), "candidate": candidate_manifest.get(field)}
            )
    for field in ("rom", "recipe"):
        stock_hash = _sha(stock_manifest.get(field))
        candidate_hash = _sha(candidate_manifest.get(field))
        if stock_hash != candidate_hash or stock_hash is None:
            suite_differences.append(
                {"field": f"{field}.sha256", "stock": stock_hash, "candidate": candidate_hash}
            )
    if not stock_manifest.get("completed") or not candidate_manifest.get("completed"):
        suite_differences.append(
            {"field": "completed", "stock": stock_manifest.get("completed"), "candidate": candidate_manifest.get("completed")}
        )

    stock_cases = _case_map(stock_manifest)
    candidate_cases = _case_map(candidate_manifest)
    for case_id in sorted(set(stock_cases) | set(candidate_cases)):
        stock_case = stock_cases.get(case_id)
        candidate_case = candidate_cases.get(case_id)
        if stock_case is None or candidate_case is None:
            suite_differences.append({"field": f"case.{case_id}", "stock": bool(stock_case), "candidate": bool(candidate_case)})
            continue
        for field in ("state",):
            stock_hash = _sha(stock_case.get(field))
            candidate_hash = _sha(candidate_case.get(field))
            if stock_hash != candidate_hash or stock_hash is None:
                suite_differences.append(
                    {"field": f"case.{case_id}.{field}.sha256", "stock": stock_hash, "candidate": candidate_hash}
                )
        for field in ("macro", "controller"):
            if stock_case.get(field) != candidate_case.get(field):
                suite_differences.append(
                    {"field": f"case.{case_id}.{field}", "stock": stock_case.get(field), "candidate": candidate_case.get(field)}
                )

    stock_checkpoints = checkpoint_map(stock_manifest)
    candidate_checkpoints = checkpoint_map(candidate_manifest)
    checkpoint_keys = sorted(set(stock_checkpoints) | set(candidate_checkpoints))
    comparisons: list[dict[str, Any]] = []
    invalid = bool(suite_differences)
    visual_failure = False
    for case_id, relative_frame in checkpoint_keys:
        stock_checkpoint = stock_checkpoints.get((case_id, relative_frame))
        candidate_checkpoint = candidate_checkpoints.get((case_id, relative_frame))
        result: dict[str, Any] = {"caseId": case_id, "relativeFrame": relative_frame}
        if stock_checkpoint is None or candidate_checkpoint is None:
            result.update({"outcome": "invalid-input", "error": "Checkpoint exists in only one run."})
            comparisons.append(result)
            invalid = True
            continue

        raw_differences = _raw_input_differences(stock_checkpoint, candidate_checkpoint)
        result["rawInputDifferences"] = raw_differences
        if raw_differences:
            invalid = True

        artifact_relative = Path("diffs") / case_id / f"f{relative_frame:06d}"
        result["artifactDirectory"] = artifact_relative.as_posix()
        try:
            stock_image = stock_manifest_path.parent / stock_checkpoint["image"]["path"]
            candidate_image = candidate_manifest_path.parent / candidate_checkpoint["image"]["path"]
            image_report = compare_images(
                stock_image,
                candidate_image,
                output_directory / artifact_relative,
                channel_tolerance,
                max_differing_pixels,
            )
            result.update(
                {
                    "differingPixels": image_report.get("differingPixels"),
                    "exactDifferingPixels": image_report.get("exactDifferingPixels"),
                    "exactMatch": image_report.get("exactMatch", False),
                    "imagePassed": image_report.get("passed", False),
                }
            )
            if raw_differences:
                result["outcome"] = "invalid-input"
            elif image_report.get("passed"):
                result["outcome"] = "pass"
            else:
                result["outcome"] = "visual-fail"
                visual_failure = True
        except Exception as error:
            result.update({"outcome": "invalid-input", "error": str(error)})
            invalid = True
        comparisons.append(result)

    if invalid:
        outcome = "invalid-input"
        exit_code = 1
    elif visual_failure:
        outcome = "visual-fail"
        exit_code = 2
    else:
        outcome = "pass"
        exit_code = 0
    summary = {
        "schemaVersion": 1,
        "stockManifest": str(stock_manifest_path),
        "candidateManifest": str(candidate_manifest_path),
        "thresholds": {
            "channelTolerance": channel_tolerance,
            "maxDifferingPixels": max_differing_pixels,
        },
        "suiteInputDifferences": suite_differences,
        "comparisons": comparisons,
        "validComparison": not invalid,
        "passed": exit_code == 0,
        "outcome": outcome,
        "exitCode": exit_code,
    }
    write_json(output_directory / "summary.json", summary)
    _write_html(output_directory, summary)
    return summary, exit_code
