# Performance Track C findings

## Current live normal-speed evidence

PID 31700 advanced from frame 24256 to 26057 over 30.0211 seconds: **59.991 FPS**. SuperZSNES consumed 91.8125 process CPU-seconds, equivalent to **3.058 logical cores** or 9.56% of this 32-logical-CPU machine.

The prototype's memory movement was +48.0 MiB working set and +49.1 MiB private bytes. However, it also made 121 bridge calls and increased the process handle count from 1125 to 1365. The exact two-handles-per-poll relationship proves a material observer effect, so this run is retained as diagnostic evidence only, not a clean memory benchmark.

The earlier independent 30-second process-only observation is cleaner: approximately 3.149 cores, working set 2408.8 to 2463.1 MiB, private bytes 2464.2 to 2514.1 MiB (+49.8 MiB), and handles 1112 to 1115.

## Runtime settings

`SuperZSNESPerformanceGuard/status.json` reported:

- rewind capture disabled;
- history capture disabled;
- `rewindFPS = 0`;
- `numRewindFrames = 0`;
- `historyDisabled = true`.

The Tile Stream Tracer configuration had `AutoArm = false`. The Core Optimizer's two experimental renderer/memory fast paths were both false.

## What is not yet proven

- Exact single-frame hitch/outlier distribution is not exposed by the current read-only bridges.
- Normal versus fast-forward is not yet a controlled A/B; toggling fast-forward advances live gameplay and requires coordination.
- Short-run memory growth does not prove a leak. A clean restart with the fixed automation bridge, followed by the script's five-minute one-second process sample, is required to distinguish steady growth, cache warmup, and sawtooth reclamation.

## Artifacts

- Observer-contaminated prototype: `Runs/20260811-124624-live-readonly-normal/`
- Safe harness: `benchmark.py`
- Offline comparison: `compare.py`
- Protocol and commands: `README.md`
