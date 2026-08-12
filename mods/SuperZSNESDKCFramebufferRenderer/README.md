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

## Accepted v0.4.2 tile-plane rebuild

Millstone Mayhem exposed a remaining scrolling bottleneck. Each eight-pixel camera bucket miss rebuilt a 374x240 guarded background plane pixel-by-pixel, including repeated tilemap addressing and SNES planar extraction for every pixel. v0.4.2 now:

- validates and decodes only the character tiles actually referenced by the retained visible plane;
- ignores unrelated VRAM changes inside the broad nominal CHR range;
- builds uniform planes one clipped 8x8 tile block at a time; and
- compares contiguous VRAM snapshots eight bytes at a time while retaining circular-wrap semantics.

The exact 1,800-frame Millstone left/right macro improved from 86-90 Unity updates/s on v0.3.1 to 119.4-119.7 updates/s on v0.4.2. Every full v0.4.2 window ran at about 60 emulated FPS with zero updates that consumed two or more SNES frames. Background preparation fell from about 4.27 ms to 0.31 ms per rendered frame, and total framebuffer work fell from about 7.5 ms to 3.2 ms. A saved Millstone frame before and after the rewrite is byte-identical (`SHA-256 6C69F18BCC6B0F7ACB78E68F85B70118BDC894284AD40AD0B89C91F5D115F8A6`).

The installed v0.4.2 DLL used for that result has SHA-256 `BDD6029BBC138B234E02F5888BAF62F8AD020FD42850C862AE06F0E8F32F12D2`. A rejected v0.4.0 prototype decoded all 1,024 tiles whenever any byte in a nominal CHR range changed; DKC stores unrelated streamed data in portions of those ranges, so that version rebuilt atlases unnecessarily and was superseded by per-tile validation.
