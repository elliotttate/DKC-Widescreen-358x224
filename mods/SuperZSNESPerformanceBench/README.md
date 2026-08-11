# SuperZSNES Performance Bench

A script-only, read-only benchmark for an already-running SuperZSNES process. It installs no DLL, patches no method, changes no configuration, sends no controller input, and never launches, pauses, resumes, or terminates the emulator.

The offline audit of palette-cache eviction, forced cache collections, history screenshots, CPU debug lists, and audio ratio correction is in `REMAINING_FINDINGS_AUDIT.md`.

## What it measures

- total emulated-frame cadence from one start and one end `DKCLevelAutomation` status observation;
- SuperZSNES process CPU time, normalized both to one logical core and the whole machine;
- working set, private bytes, handle count, bridge response latency, stalled polling windows, and window-level cadence outliers;
- the active Performance Guard rewind/history status, Core Optimizer configuration, and Tile Stream Tracer configuration/status.

The bridge does not expose an exact timestamp for every emulated frame. The harness therefore cannot produce a true individual-frame timing distribution. It deliberately makes only two bridge calls and samples CPU/memory/handles out of process at the requested interval. This avoids contaminating a long memory run with connection churn.

## Capture the current mode

```powershell
cd <superzsnes-source>\Mods\SuperZSNESPerformanceBench
python .\benchmark.py --label normal --duration 30 --interval 0.25
```

Outputs are written under `Runs\<timestamp>-<label>\`:

- `environment.json`: sanitized endpoint metadata and runtime/config snapshots;
- `samples.jsonl`: raw out-of-process CPU/memory/handle samples;
- `frame-observations.json`: the two read-only start/end status observations used for cadence;
- `summary.json`: aggregate cadence, CPU/memory, and outlier statistics.

For a low-overhead five-minute memory/handle trend, sample once per second:

```powershell
python .\benchmark.py --label normal-memory-5m --duration 300 --interval 1.0
```

The summary reports start/end/delta, a least-squares slope per minute, largest one-sample increase/drop, and a conservative sawtooth candidate flag. Inspect `samples.jsonl` before treating a fitted growth slope as a leak: scene changes, captures, JIT warmup, and asset caching all legitimately move memory.

The harness infers `normal-speed-candidate` at 75 FPS or lower and `fast-forward-candidate` above 75 FPS. This is evidence from measured cadence, not a direct read of SuperZSNES's private fast-forward input flag.

## Controlled normal versus fast-forward protocol

1. Use the same ROM/state, scene, window size, renderer settings, and plugin configuration for both samples.
2. Make sure automated controller schedules, CPU tracing, tile tracing, and automatic captures are inactive.
3. Record the normal sample while no fast-forward key is held.
4. Coordinate with the player before holding the configured fast-forward control (default backquote), because doing so advances gameplay.
5. While that control is held continuously, record the fast-forward sample with the same duration and interval.
6. Release fast-forward, then compare the two completed folders offline:

```powershell
python .\compare.py .\Runs\<normal> .\Runs\<fast-forward> --output .\Runs\normal-vs-fast.json
```

Do not compare samples from different scenes as if they isolate fast-forward cost. Both renderer workload and game logic change with the scene.

## Reading the result

- `cadenceFps`: frames advanced divided by wall time across unpaused windows.
- `cpuOneCorePercent`: process CPU seconds divided by wall seconds; values above 100% mean more than one logical core was used.
- `cpuMachinePercent`: the same usage divided by the machine's logical CPU count.
- `cpuSecondsPerEmulatedFrame`: lower is better when scenes are controlled.
- `windowAverageFrameMs`, `stalledRunningWindows`, and window outliers are unavailable under the safe two-observation protocol.
- `workingSetTrend` / `privateBytesTrend`: net delta and fitted bytes-per-minute slope.
- `handleCountTrend`: detects steady handle growth separately from managed/native memory.

Performance Guard's `status.json` is treated as the runtime authority for rewind/history settings. The config file alone is not sufficient because SuperZSNES normally resets rewind values while loading menu settings.

## Bridge observer-effect finding

The originally installed DKCLevelAutomation 0.1.1 bridge leaked about two Windows handles per connection: a per-client `Thread` and an undisposed `ManualResetEventSlim` wait handle. A 4 Hz, 30-second prototype increased the emulator from 1125 to 1365 handles. That run is observer-contaminated and must not be used as a clean long-duration result.

The source bridge now uses the managed thread pool and disposes the completion event only after both producer and waiter finish. Its isolated 500-request test finishes at a delta of one process handle. The currently running emulator still has the old DLL loaded; a later restart/install is required before validating the fix in SuperZSNES. The benchmark retains its two-call protocol even with the fix because it is the lowest-overhead available measurement.
