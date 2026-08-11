# SuperZSNES Allocation Scope Probe

This is a disabled-by-default BepInEx diagnostic for attributing managed allocations and CPU time across the SuperZSNES Unity frame, `MasterExecutor.Update`, `RunFrame`, `PPURenderer.GenerateBackgrounds`, and each `GenerateBackground` call.

It uses Mono's cumulative `GC.GetAllocatedBytesForCurrentThread()` counter. Unlike `GC.GetTotalMemory(false)`, the counter is monotonic and is not confused by collections occurring inside a measured scope. All hot-path metrics are fixed counters; snapshots, JSON construction, and file I/O run on a background writer thread.

## Clean-run conclusion before adding this probe

For the completed 2026-08-11 17:40:37Z-17:41:46.4Z run:

- 3,908 emulated frames over 65.134178 seconds = 59.999222 Hz.
- 3,536 Unity `MasterExecutor.Update` calls = 54.287935 Hz.
- Average `MasterExecutor.Update` duration was 14.243558 ms.
- Average `RunFrame` duration was 2.455905 ms, leaving about 11.788 ms/update in input, render preparation, and `GenerateBackgrounds`.
- Maximum host-update cadence was 27.866 ms and maximum RunFrame-start cadence was 27.7684 ms; there were no >33.3 ms gaps.
- Boehm collection count increased by 55 in 65.162 seconds (0.844/s), while managed memory remained within 81.5-88.7 MB at five-second samples.
- The three generation counters move together because the player uses Unity Mono's Boehm collector (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`); they are not 165 separate generational collections.
- Aligned five-second collection deltas and Unity update rate had Pearson correlation about +0.13. The sample does not support GC as the cause of the sustained 52-55 Hz Unity cadence.
- Emulation still delivered exact 60 Hz because 24-38 updates per five-second window scheduled two SNES frames.

The sustained limit is CPU/frame-budget shaped: `MasterExecutor.Update` consumes about 14.2 ms and the complete Unity cadence is about 18.4 ms. Current aggregate data cannot attribute allocation bytes or the roughly 11.8 ms outside `RunFrame`; this probe supplies those missing measurements.

## Diagnostic overhead assessment

- RuntimePauseProbe does timestamp/counter work in the two measured methods and a sleeping background watchdog. Its periodic JSON/process sampling is off the Unity thread.
- MaterialCacheGuard diagnostics run only every 300 composite render calls (roughly once per 5.5 seconds in this sample). They synchronously reflect over cache structures and write two files, so they should be disabled for final production measurements, but the absence of >33 ms cadence outliers shows they do not explain a sustained 54.3 Hz rate in this run.
- AudioTimingProbe adds several lightweight method hooks plus approximately 1.44 million buffer-lock attempt counters per five seconds (~288,000 atomic increments/s). It allocates only at five-second snapshots, but its CPU cost needs a controlled enabled/disabled A/B before being called immaterial.
- No diagnostic A/B was present in the completed sample, so the evidence does not justify enabling a renderer-affecting mitigation yet. The separate `SuperZSNESRendererFastPaths` project packages two disabled, semantics-preserving lookup experiments for a future controlled A/B; it is not installed or recommended without that test.

## Build and verify

```powershell
dotnet build '<superzsnes-source>\Mods\SuperZSNESAllocationProbe\SuperZSNESAllocationProbe.csproj' -c Release
powershell -ExecutionPolicy Bypass -File '<superzsnes-source>\Mods\SuperZSNESAllocationProbe\verify.ps1'
powershell -ExecutionPolicy Bypass -File '<superzsnes-source>\Mods\SuperZSNESAllocationProbe\analyze-clean-run.ps1'
```

## Install later (not performed by the builder)

1. Stop SuperZSNES normally.
2. Copy `bin\Release\net472\SuperZSNESAllocationProbe.dll` to `BepInEx\plugins\SuperZSNESAllocationProbe\`.
3. Start once, stop normally, set `[Probe] Enabled = true`, and restart.
4. For the cleanest allocation run, disable RuntimePauseProbe, AudioTimingProbe, and MaterialCacheGuard diagnostics. Keep the scratch-list pool enabled.
5. Reproduce the same scene for at least 60 seconds. Results appear under `BepInEx\AllocationProbe\session-*\windows.jsonl`.

Recommended A/B after the allocation run:

1. Baseline: AllocationProbe only, other diagnostic probes disabled.
2. Audio overhead: enable AudioTimingProbe with the same scene and input.
3. Material diagnostics overhead: return AudioTimingProbe to disabled, enable MaterialCacheGuard diagnostics.

Compare `unityFrame.avgUs`, `masterUpdate.avgUs`, and their allocation totals. The SNES frame rate should remain 60 Hz in every run.
