# SuperZSNES Cadence Counter

This is a disabled-by-default BepInEx diagnostic for SuperZSNES v0.230. It counts Unity `MasterExecutor.Update` calls, executed `RunFrame` calls, and the 0/1/2/3/4/5+ frames-per-Update distribution in five-second windows.

Unlike `SuperZSNESAudioTimingProbe`, it does not patch the audio callback or audio locks and contains no `Interlocked`, `Monitor`, concurrent collection, or worker thread. All counters are ordinary fields touched only on Unity's main thread. Disk output is one buffered JSON line per aggregation window.

Optional `RendererBreakdown=true` adds main-thread `Stopwatch` timing around `PPURenderer.GenerateBackgrounds` and each active `GenerateBackground` layer. Leave it false for the lowest-overhead pacing A/B; enable it only to apportion renderer cost.

## Configuration

```ini
[Counter]
Enabled = false
WindowSeconds = 5
RendererBreakdown = false
LogWindows = false
```

Output is written under `BepInEx/plugins/SuperZSNESCadenceCounter/session-*/windows.jsonl`. The project does not install itself and does not modify `Assembly-CSharp.dll`.

The repeatable real-time scrolling procedure, comparison gates, and geometry-reuse design are documented in `PACED_SCROLL_BENCHMARK.md`. The comparison parser is `tools/compare_sessions.py`.

## Renderer audit

`PPURenderer.GenerateBackgrounds` already ORs SNES `TM` and `TS` across scanlines and invokes `GenerateBackground` only for layers present on at least one main or sub-screen line. A layer used only on the sub-screen is not hidden: it can affect color math and must still be generated.

The sampled DKC frame has `BGMODE=$09`, `TM=$17`, `TS=$17`, and `CGADSUB=$93`. BG1, BG2, BG3, and OBJ are all active; BG4 is already skipped. Disabling BG3 merely because it looks like a secondary layer is therefore unsafe.

Each active background is rebuilt every presented frame: scanline state is replayed, visible tile entries are decoded, material buckets are rebuilt, and mesh vertex/UV arrays are uploaded followed by `Mesh.RecalculateBounds`. Consecutive DKC captures show stable CGRAM and background VRAM while moving, but BG1/BG2/BG3 scroll positions all change. A whole-layer cache cannot generally hit during camera motion.

Even a stationary-camera cache needs more than an early return. The five global mesh pools and their indices are shared across all four BG layers. A skipped layer's previous `TileMeshData` objects continue to reference those meshes; unless the cached layer's previous per-pool ranges are reserved, the following layer overwrites the same meshes and corrupts the cached image. A correct cache must also invalidate on relevant VRAM/CHR, CGRAM, PPU line changes, scroll, BG mode/base/size, TM/TS/window/color math, widescreen settings, scene material data, dimensions, and debug toggles.

Small redundant operations exist—reassigning render textures in `RefreshTextures`, an unused `GetIORegisters`/`GetCGMemory` call inside each layer, and a duplicate Mode 7 cleanup condition—but they are getter/property-scale work and are not credible explanations for a 14-15 ms Update. No renderer-skip patch is included until layer timings and cache hit rates justify the more complex mesh-reservation design.
