# SuperZSNES DKC Framebuffer Renderer

Experimental BepInEx renderer for the canonical `DKC_Widescreen_358x224` ROM. It converts SNES Mode 1 tilemaps, planar tile graphics, OAM, per-scanline register state, per-scanline CGRAM changes, windows, priorities, brightness, and color math into one persistent 358x224 RGBA texture.

The plugin is disabled by default and patches nothing while disabled. When enabled it starts in shadow mode: the stock renderer remains authoritative and one candidate framebuffer is rendered every 60 composite calls. `F10` writes the next supported candidate to the plugin directory.

For automated capture without focusing the window, create an empty `capture.request` file in `BepInEx/plugins/SuperZSNESDKCFramebufferRenderer`; it is consumed on the next Unity update.

`PresentFramebuffer=false` is the safe default for a new install. The canonical development setup has passed exact-frame cache/parallel regressions plus Jungle, cave, and Barrel-state cadence checks and may use `PresentFramebuffer=true`. Unsupported frames fail closed to the stock renderer. Initial unsupported gates include non-Mode-1 frames, mosaic, direct color, interlace/hires, overscan, and active-display OAM writes.

## Build

```powershell
& '.\build.ps1'
```

## Install

Close SuperZSNES first, then:

```powershell
& '.\install-plugin.ps1'
```

On first launch configure `BepInEx/config/dev.local.superzsnes.dkcframebuffer.cfg`:

```ini
[Renderer]
Enabled = true
PresentFramebuffer = false
ShadowRenderInterval = 60
```

## Architecture

- `GenerateBackgrounds` prefix snapshots the frame and creates a CPU reference framebuffer.
- BG1/BG2/BG3 cache preparation runs concurrently into three plugin-owned planes; diagnostics are aggregated after the workers join, so scrolling misses do not serialize three roughly 10 ms rebuilds.
- Shadow mode always lets the legacy renderer run.
- Presentation mode skips legacy `GenerateBackgrounds` only after a supported frame renders successfully.
- `MainScreenBlit.OnRenderImage` is replaced only while a valid candidate is active.
- Legacy main/sub/window cameras are restored on every fallback and plugin shutdown.

The CPU implementation is the correctness reference. The next stage partitions BG1/BG2/BG3 into retained plugin-owned index/priority planes with guard pixels, followed by a GPU or native compositor after the reference output is validated.

## Accepted v0.3 timing result

The same 25-second deterministic moving-camera sample improved from 72.167 Unity updates/s and 199 two-frame batches on v0.2 to 96.351 updates/s and one two-frame batch on v0.3. SNES emulation remained approximately 60 FPS. The v0.2 and v0.3 raw framebuffers at save-state frame 3372 are byte-identical; parallel background preparation changes only scheduling.
