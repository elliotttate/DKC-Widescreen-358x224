# SuperZSNES Runtime Pause Probe

This is a restart-time, disabled-by-default BepInEx diagnostic for SuperZSNES v0.230. It was built to distinguish four superficially similar causes of a visible freeze:

1. The emulator is deliberately gated (`!_executing`, `_gamePaused`, or `EmuUIInterface.emuState != Normal`) while Unity continues updating.
2. The Unity main thread stalls while another managed thread remains schedulable.
3. The managed runtime/process is paused or descheduled as a whole.
4. A collection occurs across a runtime-wide pause.

It does not modify ROM data, save states, emulator settings, or widescreen behavior.

## Safety and overhead

`Probe.Enabled` defaults to `false`. In that state the plugin starts no thread and installs no Harmony patches. Enabling requires a process restart.

When enabled, steady-state work is limited to timestamp/counter operations at `MasterExecutor.Update` and `RunFrame`, plus a sleeping 25 ms background heartbeat. JSON allocation and process-counter queries occur only for threshold events and a low-frequency five-second sample. Pause/resume call stacks are captured only on an actual false-to-true or true-to-false pause transition. RunFrame correlation also reads watchdog staleness so a runtime-wide pause is still detected if Unity's main thread resumes before the watchdog thread gets its first post-pause timeslice.

## Build

```powershell
dotnet build '<superzsnes-source>\Mods\SuperZSNESRuntimePauseProbe\SuperZSNESRuntimePauseProbe.csproj' -c Release
powershell -ExecutionPolicy Bypass -File '<superzsnes-source>\Mods\SuperZSNESRuntimePauseProbe\verify.ps1'
```

The output DLL is:

`bin\Release\net472\SuperZSNESRuntimePauseProbe.dll`

## Install later (not performed by the builder)

1. Stop SuperZSNES normally.
2. Copy the DLL to `<superzsnes>\BepInEx\plugins\SuperZSNESRuntimePauseProbe\`.
3. Start once so BepInEx creates `BepInEx\config\dev.local.superzsnes.runtimepauseprobe.cfg`, then stop normally.
4. Set `[Probe] Enabled = true` and restart.
5. Reproduce for at least 60 seconds without automation requests, save-state UI, or debugger hotkeys.
6. Stop normally and inspect `BepInEx\RuntimePauseProbe\session-*\events.jsonl`.

Do not leave it armed for ordinary play; it is a diagnostic instrument.

## Interpreting events

| Classification/event | Meaning |
|---|---|
| `emulation-gated` | Unity updates continued, but one or more updates were inside the emulator's execution/pause/UI-state gate. Check the nearby `pause-control-transition`, `control-marker`, `emulation-state-transition`, and managed stack. |
| `scheduler-no-frame-with-updates` | Unity entered `MasterExecutor.Update`, the pause/UI gate was not observed, but no emulated frame was started. |
| `unity-main-thread-stall` | `MasterExecutor.Update` stopped arriving, while the independent watchdog kept normal cadence. Look for another Unity callback, rendering, driver, or main-thread I/O. |
| `runtime-wide-pause-with-gc` | Both Unity and the managed watchdog stopped for a comparable interval, and collection counts changed. This is strong correlation, not proof of exact GC pause duration. |
| `runtime-wide-pause-or-process-deschedule` | Both Unity and watchdog stopped without a collection-count change. Investigate OS scheduling, process suspension, native code, power/display, or driver events. |

`GC.CollectionCount` only proves that a collection completed between observations. It does not expose exact Unity/Mono collection pause duration. For final attribution of runtime-wide events, use an ETW/WPR trace in a separately coordinated run.

## Why this probe was added

In the 2026-08-11 17:22 UTC timing sample, the two reported 620.838 ms and 957.390 ms values were **RunFrame start gaps**, not process-wide suspensions:

- The corresponding maximum `MasterExecutor.Update` start gaps were only 180.897 ms and 188.865 ms.
- Audio callback start gaps were only 183.017 ms and 183.927 ms.
- `MasterExecutor.Update` itself topped out at 22.525 ms and 27.193 ms; `RunFrame` topped out at 3.475 ms and 3.753 ms in those windows.
- The first window had 408 master updates but only 343 scheduler decisions (65 updates returned before the scheduler).
- The second had 360 master updates but only 255 scheduler decisions (105 updates returned before the scheduler).

Those missing scheduler decisions mean the updates took a pre-scheduler path. In v0.230 those paths include `_executing == false`, `_gamePaused || uiInterface.emuState != Normal` (lines 1355-1364), escape/menu handling, and rewind. Rewind capture was disabled in this run and `_numRewinds` should therefore be zero, which makes the execution/pause/UI-state paths the leading explanation. The aggregate data cannot distinguish them, but it does rule out treating the 620/957 ms values themselves as whole-process suspensions. The remaining roughly 180 ms gaps shared by the master-update and audio-callback cadences could still be GC/runtime pauses or OS descheduling; the watchdog and collection deltas are specifically intended to separate those.
