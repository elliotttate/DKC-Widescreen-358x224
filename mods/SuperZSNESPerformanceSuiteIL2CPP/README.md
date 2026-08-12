# SuperZSNES v0.300 IL2CPP performance suite

This project ports only the v0.230 optimizations that remain applicable and
can be implemented fail-closed through IL2CPP Harmony prefixes/postfixes. It
also provides one shared low-overhead counter for stock and optimized A/B runs.
All switches default to false and the master diagnostics switch defaults off.

Current v0.300 native evidence:

- `MasterExecutor.Update` at `0x10426580` still runs at most five normal-speed
  frames but subtracts every due frame at `0x10427470`, permanently discarding
  backlog after a hitch. The postfix recovers only the proven `due > 5` and
  exactly-five-executed normal path; fast-forward remains untouched.
- `UpdateHistoryState` and rewind capture still execute from the main Update.
  The service guard temporarily sets the two existing disabled flags only for
  the duration of Update, then restores the user's exact values.
- the 2/4/8-bpp accessors still mark atlas pages dirty before checking the
  corresponding per-tile dirty byte. The optional atlas gate preserves a page
  upload if any tile on it changed and clears only false-positive page flags.

The old material, mesh-bounds, dictionary, RenderLines-loop, and stock whole-BG
cache experiments are not blindly ported. Supported DKC gameplay skips that
stock per-tile renderer through `SuperZSNESDKCFramebufferRendererIL2CPP`; native
IL transpilers are unavailable in the IL2CPP runtime, and those changes would
add risk primarily to short fallback/unsupported frames.

Build:

```powershell
& .\build.ps1 -BepInExIl2CppRoot '<bepinex-enabled-v0.300>'
```

Configuration:

```ini
[Diagnostics]
Enabled = false
StatusEveryUpdates = 120
SlowEventThresholdMs = 8.0
InjectStallAfterUpdates = 0
InjectStallMilliseconds = 0

[Optimizations]
RecoverDroppedBacklog = false
EmergencyMaxBacklogFrames = 120
DisableHistoryCapture = false
DisableRewindCapture = false
GateAtlasUploadsOnTileDirty = false
```

The benchmark harness also accepts `stock-native-atlas` when the separate
`SuperZSNESNativeAtlasDirtyFixIL2CPP` project is installed in the disposable
copy. That scenario disables both the framebuffer and managed atlas gate,
enables the six-site native correction, and verifies its runtime status before
sampling. Four matched trials found no measurable presentation improvement, so
the native plugin also remains disabled outside controlled tests.

`status.json` reports Update, RunFrame, and presentation totals/timings; bounded
`slowRunFrameEvents` and `slowUpdateEvents` rings include video-mode/change/dirty
tile context only for calls at or above `SlowEventThresholdMs`; the
0/1/2/3/4/5/6+ RunFrame-per-Update histogram; backlog recovery batches and
cumulative retained-backlog frame charges; service-guard activity; atlas
suppression; memory; VSync/target frame rate; and hook errors. A retained frame
can be charged again on later batches while the backlog drains, so the charge
counter is intentionally not described as a unique recovered-frame count.

The test-only stall settings must remain zero in normal use. The benchmark
script uses them only in its two `*-stall-*` scenarios to compare stock dropped
time with bounded backlog recovery under the same deliberate 500 ms pause.
