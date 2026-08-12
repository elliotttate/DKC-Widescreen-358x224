# SuperZSNES DKC Framebuffer Renderer

Experimental BepInEx renderer for the canonical `DKC_Widescreen_358x224` ROM. It converts SNES Mode 1 tilemaps, planar tile graphics, OAM, per-scanline register state, per-scanline CGRAM changes, windows, priorities, brightness, and color math into one persistent 358x224 RGBA texture.

The canonical allowlist includes the standard widescreen build and both
source-built MSU-1 variants: Deluxe 60-track and Restoration 27-track.

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

## Fixed native-width maps in v0.4.3

DKC's world maps use fixed, non-scrolling 32x32 BG1/BG2 tilemaps. Sampling the normal 51-pixel widescreen margins wraps those 256-pixel maps and exposes unrelated map sections on both sides. v0.4.3 recognizes the exact fixed Mode 1 signature across all 224 lines and renders black pillarbox margins while keeping the authored central 256 pixels unchanged. Scrolling scenes, 64-wide tilemaps, color-math scenes, and normal Mode 1 gameplay remain on the full 358-pixel path.

## Slip-Slide Ride color math in v0.4.4

Slip-Slide Ride uses BG3 as a subscreen-only animated ice-shimmer plane (`TM=$13`, `TS=$04`, `CGWSEL=$02`, `CGADSUB=$33`). The earlier CPU compositor used CGRAM color 0 when no subscreen layer covered a pixel and added 5-bit channels directly. SuperZSNES instead represents an empty subscreen pixel as opaque black and runs selected main/subscreen addition through the final shader's sRGB-to-linear, 1.9-power blend, and sRGB encoding. The old CPU behavior left large purple halo shapes visible across the foreground.

v0.4.4 matches the legacy behavior. Its per-pixel path uses a precomputed 16x32x32 byte lookup table, so no power functions run while rendering. Exact channel combinations captured from the legacy main, sub, and composed surfaces are verifier fixtures. At the reproduction checkpoint, the corrected retained renderer averaged about 2.09 ms per supported frame after warmup, including about 1.63 ms for composition, and the animated white/cyan glints remained visible without the purple foreground overlay.

The verified v0.4.4 DLL has SHA-256 `A99B13F43025DDD9A3D1693BCB98EC0EED56A7D91938E655394D67D11A427184`.

## Fallback-burst telemetry in v0.4.6

v0.4.6 measures the stock `GenerateBackgrounds` cost whenever the CPU
framebuffer deliberately fails closed. `status.json` now separates unsupported
reasons, frame counts, consecutive-run lengths, and average/maximum stock
renderer milliseconds. It also removes the old synchronous status-file rewrite
on every fallback frame; status is sampled at burst boundaries and every 120
fallback frames instead. The same instrumentation is shared with the v0.300
IL2CPP port.

v0.4.7 (IL2CPP v0.1.2) also retains the last 32 supported framebuffer renders
at or above 8 ms. Each event breaks the cost into line-state, background,
sprite, and composition stages and records cache activity/rebuilt layers, so a
rare spike can be diagnosed without instrumenting every frame.

v0.4.8 (IL2CPP v0.1.3) uses that evidence to optimize scroll-only raster
effects. When a retained raster plane has identical relevant VRAM, tilemap,
character base, and mode, only scanlines whose X/Y scroll changed are rebuilt.
Any other state or memory change still takes the exact full rebuild path.
`rasterPartialRebuilds` and `rasterPartialRows` expose the accepted fast path.

v0.4.13 (IL2CPP v0.1.8) detects DKC's native-width opening, title, and
file-select asset layouts and paints only their out-of-range side extensions
black, preventing their 32-tile maps from wrapping into the widescreen margins.
It applies the same treatment during the short Mode 9 level-loader interval
where DKC's camera bounds are still `$0000/$0000`.

## Deluxe MSU-1 ROM identity in v0.4.5

v0.4.5 adds the reproducible widescreen + Deluxe MSU-1 ROM hash to the same
fail-closed identity allowlist as the standard widescreen ROM. Rendering code
and pixel output are otherwise unchanged. This matters only on the legacy
SuperZSNES v0.230/BepInEx path; clean SuperZSNES v0.300 is IL2CPP-based and was
first baseline-tested without this or any other BepInEx plugin. Widescreen
presentation on v0.300 now uses the separate
`SuperZSNESDKCFramebufferRendererIL2CPP` project; this Mono DLL is not
compatible with that runtime.

`F10`/`capture.request` now also writes BG1, BG2, BG3, and main-background-only PNGs beside the final candidate. These planes are diagnostic outputs; the final candidate remains the authoritative CPU-renderer image.
