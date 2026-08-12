# SuperZSNES v0.300 IL2CPP optimization port

## Outcome

SuperZSNES v0.300 is CPU-limited in the legacy Unity tile presentation path,
not in the SNES execution core. In the controlled Jungle benchmark, stock
v0.300 maintained 59.98 emulated frames/s while using an average 2.99 CPU
cores. Replacing the supported DKC Mode 1 presentation path with the CPU
framebuffer reduced that to 1.61 cores. Enabling exact retained-background
caching reduced it again to 1.30 cores while maintaining 60.01 emulated
frames/s.

The retained framebuffer plus exact background cache is therefore the
recommended production configuration. The new performance suite preserves a
bounded scheduler-backlog fix and reversible service guards, but keeps every
switch off until explicitly enabled. The atlas-upload gate remains available
only as a documented experiment because its per-tile IL2CPP Harmony overhead
made presentation slower even though it proved the native dirty-flag bug. A
subsequent source-equivalent native x86 patch removed that callback cost but was
performance-neutral across four matched trials, so it also remains disabled.

## Native-code audit

The audit used the v0.300 metadata-v39 decompilation with 95.6% Hex-Rays
coverage and its validated IDA database. Relevant current native entry points:

| System | v0.300 native address | Finding |
| --- | ---: | --- |
| `MasterExecutor.Update` | `0x10426580` | Still schedules at most five normal-speed frames and subtracts every due frame. |
| `MasterExecutor.RunFrame` | `0x104245B0` | Core cost remains about 2.1–2.3 ms per emulated frame in the measured scene. |
| `PPURenderer.GenerateBackgrounds` | `0x10387AB0` | Legacy presentation remains the dominant supported-frame CPU cost. |
| `PPURenderer.GenerateBackground` | `0x10383C80` | Avoided by the framebuffer path and reduced by exact retained-background reuse. |
| `PPURenderer.Process2DTiles` | `0x10390DC0` | Still performs Unity mesh submission and bounds recalculation. |
| `PPURenderer.RenderLines` | `0x10392470` | Still performs the old 129-iteration OBJ loop. |
| `TileTextureGen.GetTileMaterial` | `0x103AAB70` | Repeated dictionary probes remain, but the old patch did not benchmark positively. |
| `MainMemoryMap.ReadMem` | `0x1041D2F0` | Cheat lookup remains, but the old fast path regressed controlled performance. |

## Port decisions

| Candidate | v0.300 decision | Evidence and rationale |
| --- | --- | --- |
| Supported DKC CPU framebuffer | **Accepted** | Average process CPU fell 46.0% versus stock. It bypasses the Unity per-tile renderer on supported Mode 1 frames and fails back to stock elsewhere. |
| Exact retained-background cache | **Accepted** | Average process CPU fell another 19.5% versus framebuffer cache-off. Hit rate was 79.1%; average background stage fell from 1.933 to 0.976 ms. |
| Charge only actually scheduled frames | **Ported, optional** | The native bug persists. A controlled 500 ms stall produced four bounded recovery batches and 0.62–0.73 more emulated frames/s than paired stock runs. Fast-forward is untouched. |
| Disable history/rewind work during Update | **Ported, optional** | Uses the existing v0.300 flags and restores the user's values after every Update. It prevents service spikes but is not a steady-state throughput optimization. Prefer the emulator's own settings when available. |
| Managed atlas dirty-page gate | **Rejected for production** | It proved about 1.26 million false page-dirty assignments per 21-second trial, but per-tile IL2CPP Harmony hooks raised measured presentation work from 2.970 to 4.071 ms. |
| Native atlas dirty-branch correction | **Verified, no measurable benefit** | A hash/byte-gated six-site x86 patch moved the page assignment onto the existing true-dirty path with zero managed hot callbacks. Four trials averaged 2.625 ms stock versus 2.636 ms fixed presentation (+0.41%); keep disabled. |
| `ReadMem` cheat fast path | **Not ported** | The v0.230 A/B regressed wall time 2.8% and CPU/frame 8.3%. A native detour on an even hotter IL2CPP path would add more risk. |
| Tile-material and draw-loop dictionary rewrites | **Not ported** | Old controlled runs showed no benefit or visual risk. Supported framebuffer frames bypass those paths. |
| Mesh bounds, material scratch pools, and 128-OBJ loop | **Not ported** | These target the legacy renderer that supported framebuffer frames skip. The OBJ correction is less than 1% of one loop and is not a multi-Hz fix. |
| Old stock-background cache | **Superseded** | The framebuffer renderer owns exact background planes, so it can safely retain them without the shared Unity mesh-pool hazards of the old cache. |

## Benchmark results

Each ordinary scenario used two fresh-process trials with 12 seconds of warmup
and approximately 20–21 seconds of measurement. The harness verified the exact
v0.300 executable, `GameAssembly.dll`, and widescreen ROM hashes before launch.
It wrote isolated configs only inside a disposable emulator copy, sampled
process CPU and plugin monotonic counters, and stopped only the process it had
launched. VSync remained at the emulator value (`vSyncCount=1`) in these runs.

| Scenario | Trials | CPU cores | vs stock | Update Hz | Emulated FPS | Presentation FPS | Presentation work (ms) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Stock v0.300 | 2 | 2.987 | baseline | 158.65 | 59.982 | 59.864 | 2.970 |
| Stock + atlas experiment | 2 | 2.785 | -6.8% | 148.64 | 59.998 | 59.880 | 4.071 |
| CPU framebuffer, cache off | 2 | 1.613 | -46.0% | 165.43 | 59.980 | 59.816 | 5.700 renderer total* |
| CPU framebuffer, cache on | 2 | 1.298 | -56.5% | 183.97 | 60.005 | 59.817 | 4.583 renderer total* |
| Cache on + optional suite | 2 | 1.391 | -53.4% | 184.65 | 60.010 | 59.915 | 4.568 renderer total* |

\* The framebuffer renderer's own timer includes its compositor. The generic
`GenerateBackgrounds` Harmony wrapper has different patch ordering and is not
comparable to the stock presentation timer, so the table deliberately uses the
renderer-native total for those rows.

The cache-on runs recorded 2,134.5 background hits and 565.5 misses on average,
or a 79.1% hit rate. This reduced the framebuffer background stage from 1.933
to 0.976 ms (49.5%) and the renderer's total from 5.700 to 4.583 ms (19.6%).

### Native atlas follow-up

`SuperZSNESNativeAtlasDirtyFixIL2CPP` implements the source-level correction
without a Harmony callback in the tile accessors. It verifies the complete
`GameAssembly.dll` hash and six original instruction windows, then routes only
the existing true-dirty branches through native x86 trampolines. The DLL on disk
is never modified and every switch defaults off.

Four fresh stock-renderer trials per side used the same 12-second warmup and
approximately 20-second measurement. Both configurations held essentially
59.997 emulated FPS. Mean presentation time was 2.6249 ms stock and 2.6356 ms
fixed (+0.41%); median presentation differed by only +0.07%. Mean process CPU
was 1.2350 and 1.2234 cores respectively (-0.94%), with trial variance larger
than the difference. The correct conclusion is no measurable performance gain,
not a small win. The native patch is preserved as a correctness/reference
implementation but is not recommended for production.

### Controlled backlog recovery

Two valid paired trials injected one deliberate ~500 ms main-thread pause after
the measurement baseline was captured. The fixed version retained the unpaid
normal-speed backlog and drained it through stock-sized batches of at most five
frames.

| Pair | Stock emulated FPS | Fixed emulated FPS | Delta | Recovery batches | Retained-backlog charges |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | 58.781 | 59.510 | +0.729 | 4 | 35 |
| 3 | 58.977 | 59.595 | +0.617 | 4 | 36 |

The charge counter is cumulative: a retained frame can be charged again while
the backlog drains, so it is not a count of unique recovered frames. The fix
restores the emulated clock; it cannot display every intermediate frame because
the legacy architecture still calls presentation once per host Update.

## Reproduction

Build the two v0.300 IL2CPP projects against a BepInEx-enabled disposable copy,
install both resulting DLLs, then run:

```powershell
& ./mods/SuperZSNESPerformanceSuiteIL2CPP/run-v0300-benchmark.ps1 `
  -GameRoot '<disposable-v0.300-root>' `
  -RomPath '<verified-widescreen-rom>' `
  -Scenario 'framebuffer-cache-on' `
  -Trial 1 `
  -AllowConfigurationOverwrite
```

The full reviewed aggregate is preserved in
[`docs/benchmarks/v0300/benchmark-results.json`](benchmarks/v0300/benchmark-results.json).

## Limitations and robustness

- Two ordinary trials per configuration are enough to separate the large
  renderer gains, but not enough to claim small differences between cache-on
  and cache-on-plus-suite.
- The test covers one difficult DKC gameplay scene. Unsupported, Mode 7, fade,
  and UI frames intentionally fall back to the stock renderer and need their
  own workload-specific measurements.
- Process CPU includes Unity, audio, the emulator core, and diagnostics. It is
  the correct user-visible cost but not a profiler attribution.
- Windows display scheduling varied between hitch pairs. Conclusions use paired
  stock/fixed runs with similar conditions and do not treat host Update Hz as a
  universal constant.
- Working-set measurements are endpoint samples; framebuffer state increases
  memory while substantially reducing CPU.

## Recommended next steps

1. Ship the IL2CPP framebuffer renderer with `RetainedBackgrounds=true` for the
   supported DKC path.
2. Keep both `GateAtlasUploadsOnTileDirty=false` and the native atlas patch
   disabled. The native/source-equivalent follow-up removed callback overhead
   but did not improve presentation across four matched trials.
3. Enable bounded backlog recovery only when the scheduler fix is desired; keep
   the test stall settings at zero.
4. Use the emulator's own history/rewind-off settings first. The reversible
   guards exist for controlled comparisons and configurations that cannot
   persist those settings.
5. Expand the deterministic benchmark suite to one fade-heavy scene, one
   fallback frame sequence, one Mode 7 scene, and a long memory soak before a
   binary release.
