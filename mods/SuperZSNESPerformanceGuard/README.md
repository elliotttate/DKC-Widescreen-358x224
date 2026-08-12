# SuperZSNES Performance Guard

This BepInEx plugin disables two optional background services and provides optional presentation controls for SuperZSNES v0.230:

- full rewind-state snapshots at 8 Hz (240 in-memory rewind slots),
- history snapshots with a screenshot every 20 seconds,
- an optional software presentation limiter for targeted high-refresh A/B tests, and
- four 1592x896 PPU render surfaces at large window sizes, reduced to SuperZSNES's existing 796x448 path before the final window upscale.

The two background services run by default in SuperZSNES even if rewind is not being used. The guard changes runtime settings only; it does not modify `Assembly-CSharp.dll`, ROM data, or save states.

## Install

Build with `dotnet build -c Release`, then copy `bin/Release/netstandard2.1/SuperZSNESPerformanceGuard.dll` to `BepInEx/plugins/SuperZSNESPerformanceGuard/`. Restart SuperZSNES once to load it.

The generated config is `BepInEx/config/dev.local.superzsnes.performanceguard.cfg`:

```ini
[BackgroundServices]
DisableRewindCapture = true
DisableHistoryCapture = true
ReleaseAllocatedRewindBuffer = true

[Presentation]
LimitPresentationRate = false
UncappedPresentation = false
TargetPresentationRate = 120
LimitPpuRenderTexturesTo2x = true
PpuRenderTextureScale = 2
```

The recommended/default setting is `LimitPresentationRate=false`, which leaves SuperZSNES's synchronized presentation unchanged. The optional limiter sets `QualitySettings.vSyncCount=0` and uses a 120 Hz Unity ceiling; enable it only for a controlled A/B where synchronized presentation demonstrably collapses multiple SNES frames into one host update. Turning it off at runtime restores the exact VSync and target-frame-rate values captured when the plugin loaded. The 2x surfaces retain integer SNES pixels internally and are then scaled by the emulator's normal final compositor.

`UncappedPresentation=true` keeps VSync off but sets `Application.targetFrameRate=-1`. It is an isolated A/B mode for testing whether Unity's software limiter is sleeping long enough to push a renderer-heavy frame below 60 presentations per second. Keep it only if the same gameplay workload reaches at least 60 Unity updates/second without visual regressions or excessive idle CPU use. It is deliberately off by default.

Runtime state is written once per emulator attachment to `BepInEx/plugins/SuperZSNESPerformanceGuard/status.json`.
