#!/usr/bin/env python3
"""Read-only wall-clock/process benchmark for an already-running SuperZSNES."""

from __future__ import annotations

import argparse
import base64
import configparser
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import socket
import statistics
import time
from typing import Any, Iterable
import uuid


DEFAULT_GAME_ROOT = Path(os.environ.get("SUPERZSNES_ROOT", ".deps/SuperZSNES"))
DEFAULT_ENDPOINT_RELATIVE = Path("BepInEx/plugins/DKCLevelAutomation/bridge.json")
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_VM_READ = 0x0010


class BenchmarkError(RuntimeError):
    pass


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def safe_name(value: str) -> str:
    cleaned = "".join(character if character.isalnum() or character in "-_." else "-" for character in value)
    return cleaned.strip("-.") or "sample"


def encode_field(value: Any) -> str:
    if isinstance(value, bool):
        value = "true" if value else "false"
    return base64.b64encode(str(value).encode("utf-8")).decode("ascii")


class ReadOnlyBridge:
    """Minimal client whose public surface intentionally exposes only status."""

    def __init__(self, endpoint_path: Path, timeout: float) -> None:
        self.endpoint_path = endpoint_path.resolve()
        self.timeout = timeout
        try:
            self.endpoint = json.loads(self.endpoint_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            raise BenchmarkError(f"Could not read endpoint {self.endpoint_path}: {exc}") from exc
        for field in ("host", "port", "token", "pid"):
            if field not in self.endpoint:
                raise BenchmarkError(f"Endpoint is missing {field!r}: {self.endpoint_path}")
        if self.endpoint["host"] not in ("127.0.0.1", "localhost", "::1"):
            raise BenchmarkError("Refusing to connect to a non-loopback benchmark endpoint.")

    def status(self) -> dict[str, Any]:
        request_id = uuid.uuid4().hex
        wire = "\t".join((request_id, str(self.endpoint["token"]), "status")) + "\n"
        try:
            with socket.create_connection(
                (str(self.endpoint["host"]), int(self.endpoint["port"])), self.timeout
            ) as connection:
                connection.settimeout(self.timeout)
                connection.sendall(wire.encode("utf-8"))
                response = b""
                while b"\n" not in response:
                    block = connection.recv(65536)
                    if not block:
                        break
                    response += block
                    if len(response) > 1024 * 1024:
                        raise BenchmarkError("Status response exceeded 1 MiB.")
        except OSError as exc:
            raise BenchmarkError(f"Could not query {self.endpoint_path}: {exc}") from exc
        if not response:
            raise BenchmarkError("Automation bridge returned no status response.")
        try:
            message = json.loads(response.split(b"\n", 1)[0].decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise BenchmarkError(f"Automation bridge returned malformed JSON: {exc}") from exc
        if message.get("id") != request_id or not message.get("ok"):
            raise BenchmarkError(str(message.get("error", "Status request failed.")))
        result = message.get("result")
        if not isinstance(result, dict):
            raise BenchmarkError("Status result was not an object.")
        return result


class FILETIME(ctypes.Structure):
    _fields_ = (("low", wintypes.DWORD), ("high", wintypes.DWORD))

    def ticks(self) -> int:
        return (int(self.high) << 32) | int(self.low)


class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
    _fields_ = (
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
        ("PrivateUsage", ctypes.c_size_t),
    )


class WindowsProcessSampler:
    def __init__(self, pid: int) -> None:
        if os.name != "nt":
            raise BenchmarkError("Process sampling currently requires Windows.")
        self.pid = pid
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.kernel32.OpenProcess.restype = wintypes.HANDLE
        self.kernel32.GetProcessTimes.argtypes = (
            wintypes.HANDLE,
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
        )
        self.kernel32.GetProcessTimes.restype = wintypes.BOOL
        self.kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        self.kernel32.GetProcessHandleCount.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        self.kernel32.GetProcessHandleCount.restype = wintypes.BOOL
        self.psapi.GetProcessMemoryInfo.argtypes = (
            wintypes.HANDLE,
            ctypes.POINTER(PROCESS_MEMORY_COUNTERS_EX),
            wintypes.DWORD,
        )
        self.psapi.GetProcessMemoryInfo.restype = wintypes.BOOL
        self.handle = self.kernel32.OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, False, self.pid
        )
        if not self.handle:
            raise BenchmarkError(f"OpenProcess({self.pid}) failed with Windows error {ctypes.get_last_error()}.")

    def close(self) -> None:
        if self.handle:
            self.kernel32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self) -> "WindowsProcessSampler":
        return self

    def __exit__(self, *_: Any) -> None:
        self.close()

    def sample(self) -> dict[str, Any]:
        creation, exit_time, kernel, user = FILETIME(), FILETIME(), FILETIME(), FILETIME()
        if not self.kernel32.GetProcessTimes(
            self.handle,
            ctypes.byref(creation),
            ctypes.byref(exit_time),
            ctypes.byref(kernel),
            ctypes.byref(user),
        ):
            raise BenchmarkError(f"GetProcessTimes failed with Windows error {ctypes.get_last_error()}.")
        memory = PROCESS_MEMORY_COUNTERS_EX()
        memory.cb = ctypes.sizeof(memory)
        if not self.psapi.GetProcessMemoryInfo(self.handle, ctypes.byref(memory), memory.cb):
            raise BenchmarkError(f"GetProcessMemoryInfo failed with Windows error {ctypes.get_last_error()}.")
        handles = wintypes.DWORD()
        if not self.kernel32.GetProcessHandleCount(self.handle, ctypes.byref(handles)):
            raise BenchmarkError(f"GetProcessHandleCount failed with Windows error {ctypes.get_last_error()}.")
        return {
            "cpuTimeSeconds": (kernel.ticks() + user.ticks()) / 10_000_000.0,
            "workingSetBytes": int(memory.WorkingSetSize),
            "peakWorkingSetBytes": int(memory.PeakWorkingSetSize),
            "privateBytes": int(memory.PrivateUsage),
            "pageFaultCount": int(memory.PageFaultCount),
            "handleCount": int(handles.value),
        }


def percentile(values: Iterable[float], probability: float) -> float | None:
    ordered = sorted(float(value) for value in values)
    if not ordered:
        return None
    position = (len(ordered) - 1) * probability
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    fraction = position - lower
    return ordered[lower] * (1.0 - fraction) + ordered[upper] * fraction


def statistics_block(values: Iterable[float]) -> dict[str, float | int | None]:
    data = [float(value) for value in values]
    return {
        "count": len(data),
        "min": min(data) if data else None,
        "mean": statistics.fmean(data) if data else None,
        "p50": percentile(data, 0.50),
        "p90": percentile(data, 0.90),
        "p95": percentile(data, 0.95),
        "p99": percentile(data, 0.99),
        "max": max(data) if data else None,
    }


def trend_block(samples: list[dict[str, Any]], key: str) -> dict[str, Any]:
    points = [
        (float(item["elapsedSeconds"]), float(item[key]))
        for item in samples
        if key in item and item[key] is not None
    ]
    if not points:
        return {
            "count": 0,
            "start": None,
            "end": None,
            "delta": None,
            "slopePerMinute": None,
            "negativeStepCount": 0,
            "largestDrop": None,
            "largestIncrease": None,
            "sawtoothCandidate": False,
        }
    values = [value for _, value in points]
    mean_time = statistics.fmean(timestamp for timestamp, _ in points)
    mean_value = statistics.fmean(values)
    denominator = sum((timestamp - mean_time) ** 2 for timestamp, _ in points)
    slope_per_second = (
        sum((timestamp - mean_time) * (value - mean_value) for timestamp, value in points) / denominator
        if denominator > 0
        else 0.0
    )
    steps = [current - previous for previous, current in zip(values, values[1:])]
    largest_drop = min(steps) if steps else 0.0
    largest_increase = max(steps) if steps else 0.0
    value_range = max(values) - min(values)
    material_drop = abs(largest_drop) >= max(8 * 1024 * 1024, value_range * 0.10)
    return {
        "count": len(points),
        "start": values[0],
        "end": values[-1],
        "delta": values[-1] - values[0],
        "min": min(values),
        "max": max(values),
        "range": value_range,
        "slopePerMinute": slope_per_second * 60.0,
        "negativeStepCount": sum(1 for step in steps if step < 0),
        "largestDrop": largest_drop,
        "largestIncrease": largest_increase,
        "sawtoothCandidate": bool(material_drop and slope_per_second > 0),
        "sawtoothRule": "positive fitted slope plus a single-step drop >= max(8 MiB, 10% of observed range)",
    }


def add_deltas(samples: list[dict[str, Any]], logical_cpus: int) -> None:
    for previous, current in zip(samples, samples[1:]):
        wall = float(current["monotonicSeconds"] - previous["monotonicSeconds"])
        cpu = float(current["cpuTimeSeconds"] - previous["cpuTimeSeconds"])
        current["wallDeltaSeconds"] = wall
        current["cpuDeltaSeconds"] = cpu
        current["cpuOneCorePercent"] = (cpu / wall * 100.0) if wall > 0 else None
        current["cpuMachinePercent"] = (cpu / wall * 100.0 / logical_cpus) if wall > 0 else None
        if "frame" in previous and "frame" in current:
            frames = int(current["frame"] - previous["frame"])
            current["frameDelta"] = frames
            current["fps"] = frames / wall if wall > 0 else None
            current["windowAverageFrameMs"] = (wall * 1000.0 / frames) if frames > 0 else None


def derive_summary(
    samples: list[dict[str, Any]],
    label: str,
    requested_interval: float,
    frame_observations: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    logical_cpus = max(1, os.cpu_count() or 1)
    add_deltas(samples, logical_cpus)
    process_windows = [item for item in samples[1:] if item.get("wallDeltaSeconds", 0) > 0]
    legacy_windows: list[dict[str, Any]] = []
    if frame_observations is None:
        legacy_windows = [
            item
            for item in samples[1:]
            if item.get("wallDeltaSeconds", 0) > 0
            and not item.get("paused", False)
            and item.get("frameDelta", -1) >= 0
        ]
        wall = sum(float(item["wallDeltaSeconds"]) for item in legacy_windows)
        frames = sum(int(item["frameDelta"]) for item in legacy_windows)
        frame_start = samples[0].get("frame") if samples else None
        frame_end = samples[-1].get("frame") if samples else None
        paused_samples = sum(1 for item in samples if item.get("paused", False))
        status_latencies = [float(item["statusResponseLatencyMs"]) for item in samples if "statusResponseLatencyMs" in item]
        cpu_windows = legacy_windows
    else:
        first = frame_observations[0] if frame_observations else None
        last = frame_observations[-1] if frame_observations else None
        continuously_observed_running = bool(
            first
            and last
            and not first.get("paused", False)
            and not last.get("paused", False)
            and int(last.get("frame", 0)) >= int(first.get("frame", 0))
        )
        wall = float(last["monotonicSeconds"] - first["monotonicSeconds"]) if continuously_observed_running else 0.0
        frames = int(last["frame"] - first["frame"]) if continuously_observed_running else 0
        frame_start = first.get("frame") if first else None
        frame_end = last.get("frame") if last else None
        paused_samples = sum(1 for item in frame_observations if item.get("paused", False))
        status_latencies = [float(item["statusResponseLatencyMs"]) for item in frame_observations]
        cpu_windows = process_windows
    cpu_wall = sum(float(item["wallDeltaSeconds"]) for item in cpu_windows)
    cpu = sum(float(item["cpuDeltaSeconds"]) for item in cpu_windows)
    fps = frames / wall if wall > 0 else None
    inferred = "paused-or-no-advance"
    if fps is not None:
        inferred = "fast-forward-candidate" if fps > 75.0 else "normal-speed-candidate"
    window_frame_ms = [
        float(item["windowAverageFrameMs"])
        for item in legacy_windows
        if item.get("frameDelta", 0) > 0 and item.get("windowAverageFrameMs") is not None
    ]
    median_ms = percentile(window_frame_ms, 0.50)
    outlier_threshold = median_ms * 1.5 if median_ms is not None else None
    outliers = [value for value in window_frame_ms if outlier_threshold is not None and value > outlier_threshold]
    return {
        "schema": 1,
        "label": label,
        "samples": len(samples),
        "requestedIntervalSeconds": requested_interval,
        "logicalCpuCount": logical_cpus,
        "frameStart": frame_start,
        "frameEnd": frame_end,
        "frameAdvanceInRunningWindows": frames,
        "runningWindowSeconds": wall,
        "cadenceFps": fps,
        "inferredMode": inferred,
        "cpuSeconds": cpu,
        "cpuSecondsPerEmulatedFrame": cpu / frames if frames > 0 else None,
        "cpuOneCorePercent": cpu / cpu_wall * 100.0 if cpu_wall > 0 else None,
        "cpuMachinePercent": cpu / cpu_wall * 100.0 / logical_cpus if cpu_wall > 0 else None,
        "workingSetBytes": statistics_block(item["workingSetBytes"] for item in samples),
        "privateBytes": statistics_block(item["privateBytes"] for item in samples),
        "handleCount": statistics_block(item["handleCount"] for item in samples),
        "workingSetTrend": trend_block(samples, "workingSetBytes"),
        "privateBytesTrend": trend_block(samples, "privateBytes"),
        "handleCountTrend": trend_block(samples, "handleCount"),
        "statusCalls": len(frame_observations) if frame_observations is not None else len(status_latencies),
        "statusResponseLatencyMs": statistics_block(status_latencies),
        "sampleGapMs": statistics_block(item["wallDeltaSeconds"] * 1000.0 for item in samples[1:]),
        "windowAverageFrameMs": statistics_block(window_frame_ms),
        "stalledRunningWindows": sum(1 for item in legacy_windows if item.get("frameDelta", 0) == 0) if frame_observations is None else None,
        "windowOutlierRule": "windowAverageFrameMs > 1.5 * median",
        "windowOutlierThresholdMs": outlier_threshold,
        "windowOutlierCount": len(outliers),
        "windowOutlierPercent": len(outliers) * 100.0 / len(window_frame_ms) if window_frame_ms else None,
        "pausedSamples": paused_samples,
        "measurementLimit": (
            "The installed read-only bridge exposes a monotonically increasing frame counter, not individual "
            "frame completion timestamps. The safe protocol reads status only at the start and end because the "
            "currently installed bridge leaks handles per connection; exact long-frame/outlier distribution is unavailable."
        ),
    }


def read_json_if_present(path: Path) -> Any:
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"error": str(exc), "path": str(path)}


def read_ini_if_present(path: Path) -> Any:
    if not path.is_file():
        return None
    parser = configparser.ConfigParser()
    parser.optionxform = str
    try:
        parser.read(path, encoding="utf-8")
        return {section: dict(parser.items(section)) for section in parser.sections()}
    except (OSError, configparser.Error) as exc:
        return {"error": str(exc), "path": str(path)}


def environment_snapshot(game_root: Path, bridge: ReadOnlyBridge, first_status: dict[str, Any]) -> dict[str, Any]:
    config_root = game_root / "BepInEx" / "config"
    plugin_root = game_root / "BepInEx" / "plugins"
    return {
        "capturedUtc": utc_now(),
        "gameRoot": str(game_root),
        "endpoint": str(bridge.endpoint_path),
        "endpointMetadata": {
            key: value for key, value in bridge.endpoint.items() if key != "token"
        },
        "initialAutomationStatus": first_status,
        "performanceGuardStatus": read_json_if_present(plugin_root / "SuperZSNESPerformanceGuard" / "status.json"),
        "performanceGuardConfig": read_ini_if_present(
            config_root / "dev.local.superzsnes.performanceguard.cfg"
        ),
        "coreOptimizerConfig": read_ini_if_present(
            config_root / "dev.local.superzsnes.coreoptimizations.cfg"
        ),
        "tileStreamTracerConfig": read_ini_if_present(
            config_root / "dev.local.superzsnes.dkctilestreamtracer.cfg"
        ),
        "tileStreamTracerStatus": read_json_if_present(
            plugin_root / "DKCTileStreamTracer" / "control" / "status.json"
        ),
        "note": "Configuration and status files were read only; no runtime setting was changed.",
        "observerPolicy": "Only start/end bridge status calls; process CPU/memory/handles are sampled out of process.",
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", default=str(DEFAULT_GAME_ROOT))
    parser.add_argument("--endpoint", help="Existing DKCLevelAutomation bridge.json path.")
    parser.add_argument("--pid", type=int, help="Expected SuperZSNES PID; defaults to endpoint PID.")
    parser.add_argument("--duration", type=float, default=30.0, help="Wall-clock sample duration in seconds.")
    parser.add_argument("--interval", type=float, default=0.25, help="Read-only polling interval in seconds.")
    parser.add_argument("--timeout", type=float, default=3.0, help="Per-status request timeout in seconds.")
    parser.add_argument("--label", default="current", help="Human label such as normal or fast-forward.")
    parser.add_argument("--output", help="Output folder; defaults to Runs/<timestamp>-<label>.")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    if args.duration <= 0:
        raise SystemExit("--duration must be positive")
    if args.interval < 0.05:
        raise SystemExit("--interval must be at least 0.05 seconds to limit benchmark overhead")
    game_root = Path(args.game_root).resolve()
    endpoint = Path(args.endpoint).resolve() if args.endpoint else game_root / DEFAULT_ENDPOINT_RELATIVE
    label = safe_name(args.label)
    timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    output = Path(args.output).resolve() if args.output else Path(__file__).resolve().parent / "Runs" / f"{timestamp}-{label}"
    output.mkdir(parents=True, exist_ok=False)

    bridge = ReadOnlyBridge(endpoint, args.timeout)
    first_request_start = time.perf_counter()
    first_status = bridge.status()
    first_request_end = time.perf_counter()
    pid = args.pid if args.pid is not None else int(bridge.endpoint["pid"])
    if pid != int(bridge.endpoint["pid"]):
        raise BenchmarkError(f"Requested PID {pid} does not match endpoint PID {bridge.endpoint['pid']}.")
    environment = environment_snapshot(game_root, bridge, first_status)
    (output / "environment.json").write_text(json.dumps(environment, indent=2) + "\n", encoding="utf-8")

    samples: list[dict[str, Any]] = []
    frame_observations = [
        {
            "kind": "start",
            "utc": utc_now(),
            "monotonicSeconds": first_request_end,
            "statusResponseLatencyMs": (first_request_end - first_request_start) * 1000.0,
            "frame": int(first_status.get("frame", 0)),
            "paused": bool(first_status.get("paused", False)),
            "loaded": bool(first_status.get("loaded", False)),
            "activeAutomation": first_status.get("active"),
        }
    ]
    start = first_request_end
    deadline = start + args.duration
    next_sample = start
    with WindowsProcessSampler(pid) as process:
        sequence = 0
        while True:
            now = time.perf_counter()
            if now < next_sample:
                time.sleep(next_sample - now)
            metrics = process.sample()
            sampled = time.perf_counter()
            samples.append(
                {
                    "sequence": sequence,
                    "utc": utc_now(),
                    "monotonicSeconds": sampled,
                    "elapsedSeconds": sampled - start,
                    **metrics,
                }
            )
            sequence += 1
            if sampled >= deadline:
                break
            next_sample += args.interval
            if next_sample <= sampled:
                next_sample = sampled + args.interval

    final_request_start = time.perf_counter()
    final_status = bridge.status()
    final_request_end = time.perf_counter()
    frame_observations.append(
        {
            "kind": "end",
            "utc": utc_now(),
            "monotonicSeconds": final_request_end,
            "statusResponseLatencyMs": (final_request_end - final_request_start) * 1000.0,
            "frame": int(final_status.get("frame", 0)),
            "paused": bool(final_status.get("paused", False)),
            "loaded": bool(final_status.get("loaded", False)),
            "activeAutomation": final_status.get("active"),
        }
    )
    summary = derive_summary(samples, label, args.interval, frame_observations)
    summary.update(
        {
            "startedUtc": environment["capturedUtc"],
            "completedUtc": utc_now(),
            "pid": pid,
            "rom": first_status.get("rom"),
            "output": str(output),
            "rewindAndHistory": environment.get("performanceGuardStatus"),
        }
    )
    with (output / "samples.jsonl").open("w", encoding="utf-8", newline="\n") as handle:
        for sample in samples:
            handle.write(json.dumps(sample, separators=(",", ":")) + "\n")
    (output / "frame-observations.json").write_text(json.dumps(frame_observations, indent=2) + "\n", encoding="utf-8")
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except BenchmarkError as exc:
        print(f"error: {exc}", file=os.sys.stderr)
        raise SystemExit(1)
