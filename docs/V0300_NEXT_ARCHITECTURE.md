# SuperZSNES v0.300: next rendering architecture

## Decision

Build fallback-burst measurement and remove the dominant unsupported-frame
reason before attempting a producer thread or compute compositor.

The retained framebuffer is already the major rewrite. In matched v0.300 runs
it reduced process CPU from 2.9865 cores to 1.2978 cores while holding about 60
emulated frames per second. The SNES core itself averaged about 2.22 ms per
emulated frame. The remaining performance problem is therefore presentation
work and its cadence, not insufficient CPU throughput in the emulated CPU.

The current production status had 19,876 renderer calls, 19,200 framebuffer
frames, and 676 stock fallbacks (3.40%). If those frames are contiguous they
represent about 11.3 seconds of legacy rendering, but the old status format did
not retain their reasons or durations. It also wrote `status.json`
synchronously on every fallback, which could amplify a transition burst.

## What is already implemented

Several items proposed as a future CPU rewrite already exist in
`DkcFrameRasterizer`:

- exact VRAM-validated decoded-tile caches;
- predecoded tile pixels used by retained background planes;
- circular tilemap/scroll-bucket reuse;
- retained raster-effect planes;
- persistent CPU pixel, `Texture2D`, and `RenderTexture` buffers; and
- one final texture upload and blit per supported frame.

The material CPU-renderer opportunities that remain are dirty scanline/8-pixel
strip rebuilding and SIMD main/subscreen color composition. They should follow
fallback coverage because they reduce steady-state CPU, not transition bursts.

## Implemented next step

Framebuffer renderer v0.4.6 / IL2CPP v0.1.1 adds fail-closed telemetry:

- count and rate for each fallback reason;
- average and maximum time in the stock Unity renderer for each reason;
- current and maximum fallback-burst length;
- sparse status persistence instead of one synchronous disk write per fallback
  frame.

The first runtime pass should exercise ROM startup, a level transition, a fade,
a bonus-room transition, Slip-Slide Ride, and the known map/rope/barrel states.
The largest product of `frames × averageStockRendererMs` is the next feature to
implement in the CPU framebuffer. Pixel-oracle captures remain the acceptance
gate.

## Evaluation of the larger rewrites

### Fixed-rate emulation thread and frame ring

Potentially valuable for presentation cadence, but not the next safe patch.
`RunFrame`, PPU state, audio handoff, save/load, input, and Unity-facing state
currently share main-thread assumptions. Moving only `RunFrame` would introduce
races; moving the full machine requires an explicit command queue, immutable
completed-frame snapshots, a lock-free audio ring, pause/save-state barriers,
and deterministic shutdown. Build this only after fallback bursts are removed
and a cadence trace still proves that Unity `Update` is withholding otherwise
ready frames.

### GPU compute compositor

Promising for lowering the roughly 4–7 ms CPU rasterizer cost, but it is mainly
a CPU-efficiency project. DKC depends on scanline palette changes, windows,
main/subscreen priority, brightness, sprites, and color math. The CPU renderer
should remain the exact oracle, with frame-by-frame differential tests, before a
compute path becomes authoritative.

### Dirty strips and SIMD CPU composition

This is the best optimization after fallback coverage. It fits the accepted
architecture, avoids Unity mesh work, and preserves the CPU reference renderer.
The implementation should first expose dirty-region counters, then rebuild only
changed 8-pixel columns/scanline spans and vectorize the final 358x224
main/subscreen blend. It must fall back to a full frame for scanline effects and
other nonlocal state.

### Direct D3D11/SDL frontend

Defer. It removes Unity overhead but also replaces windowing, input, audio,
configuration, UI, save-state integration, MSU-1 presentation, and packaging.
It is effectively a new emulator frontend rather than an optimization plugin.

## Promotion gates

1. No visual difference in the framebuffer oracle suite.
2. No new unsupported reason or longer fallback burst.
3. Approximately 60 emulated frames per second in matched real-time runs.
4. Lower p95/p99 presentation time or fewer multi-frame Unity updates, not only
   a lower process-CPU average.
5. Fail closed to the stock renderer on any unimplemented SNES state.
