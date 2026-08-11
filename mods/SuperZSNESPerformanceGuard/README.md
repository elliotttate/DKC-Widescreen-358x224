# SuperZSNES Performance Guard

This BepInEx plugin disables two optional background services and fixes an avoidable presentation bottleneck in SuperZSNES v0.230:

- full rewind-state snapshots at 8 Hz (240 in-memory rewind slots), and
- history snapshots with a screenshot every 20 seconds.
- Unity presentation trying to follow a high-refresh desktop (200 Hz on the test PC) even though SNES emulation only needs 60 Hz.
- four 1592x896 PPU render surfaces at large window sizes, reduced to SuperZSNES's existing 796x448 path before the final window upscale.

Both are enabled by default in SuperZSNES even if rewind is not being used. The guard changes runtime settings only; it does not modify `Assembly-CSharp.dll`, ROM data, or save states.

## Install

Build with `dotnet build -c Release`, then copy `bin/Release/netstandard2.1/SuperZSNESPerformanceGuard.dll` to `BepInEx/plugins/SuperZSNESPerformanceGuard/`. Restart SuperZSNES once to load it.

The generated config is `BepInEx/config/dev.local.superzsnes.performanceguard.cfg`. Set either option to `false` and restart if you want the corresponding emulator feature back:

```ini
[BackgroundServices]
DisableRewindCapture = true
DisableHistoryCapture = true
ReleaseAllocatedRewindBuffer = true

[Presentation]
LimitPresentationRate = true
UncappedPresentation = false
TargetPresentationRate = 120
LimitPpuRenderTexturesTo2x = true
PpuRenderTextureScale = 2
```

The presentation limiter sets `QualitySettings.vSyncCount=0` and uses a 120 Hz Unity ceiling at runtime. SNES emulation remains 60/50 Hz; the extra Unity headroom prevents two SNES frames from being consumed inside one presentation update on a 120/144/200 Hz monitor. The 2x surfaces retain integer SNES pixels internally and are then scaled by the emulator's normal final compositor.

`UncappedPresentation=true` keeps VSync off but sets `Application.targetFrameRate=-1`. It is an isolated A/B mode for testing whether Unity's software limiter is sleeping long enough to push a renderer-heavy frame below 60 presentations per second. Keep it only if the same gameplay workload reaches at least 60 Unity updates/second without visual regressions or excessive idle CPU use. It is deliberately off by default.

Runtime state is written once per emulator attachment to `BepInEx/plugins/SuperZSNESPerformanceGuard/status.json`.
