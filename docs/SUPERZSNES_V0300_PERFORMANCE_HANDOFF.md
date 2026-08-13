# SuperZSNES v0.300 performance implementation handoff

## Technical summary

The measured bottleneck in Donkey Kong Country is the legacy Unity tile
presentation path, not SNES CPU/APU execution. Stock SuperZSNES v0.300 held
59.98 emulated frames/s while using 2.99 process CPU cores. The DKC framebuffer
renderer reduced process CPU to 1.61 cores, and its retained-background path
reduced it to 1.30 cores while holding 60.01 emulated frames/s.

Three production-relevant optimizations are implemented together in
[`SuperZSNESDKCFramebufferRendererIL2CPP`](../mods/SuperZSNESDKCFramebufferRendererIL2CPP):

1. A final-frame CPU compositor bypasses the Unity per-tile mesh/material path
   for supported DKC Mode 1 frames.
2. Renderer-owned exact background planes are retained across frames when all
   relevant state is unchanged.
3. Raster-effect frames redraw only rows whose scroll changed when strict
   VRAM/register gates pass.

[`SuperZSNESPerformanceSuiteIL2CPP`](../mods/SuperZSNESPerformanceSuiteIL2CPP)
contains a separate bounded scheduler-backlog correction. It fixes lost
emulation time after a hitch but is not evidence of smoother presentation.

[`SuperZSNESNativeAtlasDirtyFixIL2CPP`](../mods/SuperZSNESNativeAtlasDirtyFixIL2CPP)
is a verified reference implementation rather than a current performance win.
It fixes the atlas dirty-page assignment without managed hot callbacks, but
four matched trials measured no presentation improvement.

Equivalent Mono/v0.230 projects remain useful as readable source references:
`SuperZSNESDKCFramebufferRenderer`, `SuperZSNESFramePacingFix`, and
`SuperZSNESPerformanceGuard`. Additional stock-renderer improvements with
positive or strong correctness evidence are described below rather than mixed
into the v0.300 measured configuration.

## Measured performance

| Configuration | Trials | Process CPU cores | CPU vs stock | Emulated FPS | Working set |
| --- | ---: | ---: | ---: | ---: | ---: |
| Stock v0.300 | 2 | 2.987 | baseline | 59.982 | 463.0 MiB |
| DKC CPU framebuffer | 2 | 1.613 | -46.0% | 59.980 | 580.1 MiB |
| Framebuffer + retained backgrounds | 2 | 1.298 | -56.5% | 60.005 | 570.2 MiB |

Retained backgrounds produced a 79.1% hit rate in the measured scene. The
background stage fell from 1.933 to 0.976 ms (-49.5%), and the complete
framebuffer renderer fell from 5.700 to 4.583 ms (-19.6%).

The later partial-row implementation was measured separately in two 60-second
trials per configuration:

| Stage | Before | Partial rows | Change |
| --- | ---: | ---: | ---: |
| Background preparation | 0.9041 ms | 0.3265 ms | -63.9% |
| Complete framebuffer | 4.3574 ms | 3.6507 ms | -16.2% |
| Unity Update | 6.2920 ms | 5.5456 ms | -11.9% |
| Updates running 2+ emulated frames | 0.717% | 0.222% | -69.0% |

The partial path handled 700 of 715 raster-effect rebuilds per accepted trial.
Average process CPU changed only -1.18% in that follow-up, so the stage-time and
Update-time reductions are the stronger evidence.

## Implementation map

### `SuperZSNESDKCFramebufferRendererIL2CPP`

Repository:
[`mods/SuperZSNESDKCFramebufferRendererIL2CPP`](../mods/SuperZSNESDKCFramebufferRendererIL2CPP)

#### Supported-frame CPU compositor

- Reconstructs DKC Mode 1 backgrounds, OBJ, priority, windows, brightness, and
  color math into a persistent final framebuffer.
- Performs one texture upload and presentation pass instead of rebuilding
  Unity meshes and material buckets for every visible SNES tile.
- Recognizes exact supported ROM and video-state profiles.
- Returns immediately to the stock renderer when a frame is unsupported.
- Measured process CPU reduction: 46.0% versus stock.

Downsides and boundaries:

- This is a DKC-specific supported-mode renderer, not a complete replacement
  for the general SNES PPU renderer.
- Mode 7, unsupported PPU modes, unimplemented effects, and unmatched ROMs use
  the legacy renderer. A burst of fallback frames can therefore restore the
  old CPU cost during transitions or unusual scenes.
- PPU accuracy becomes the responsibility of the new compositor. New support
  requires deterministic pixel-oracle tests for priority, windows, color math,
  scanline changes, sprites, fades, and active-display writes.
- The CPU saving trades for memory. The retained configuration used about
  107 MiB more working set and 104 MiB more private memory than stock at the
  sampled endpoint.
- The mod is pinned to the audited 32-bit v0.300 IL2CPP binary. An upstream
  implementation would use source-level integration rather than native hooks
  and runtime reflection.

Reusable design:

- A capability check is made per frame. Supported frames use the fast path;
  every uncertain state fails closed to the existing renderer.
- The final framebuffer is the ownership boundary. This avoids unsafe reuse of
  shared Unity mesh pools and collapses presentation to one persistent surface.
- Fallback reasons, streak lengths, and renderer time are counted with bounded
  telemetry rather than synchronous per-frame file writes.
- The same architecture can support additional games incrementally by adding
  verified PPU feature profiles without weakening the fallback contract.

#### Exact retained-background planes

- Caches renderer-owned decoded background planes, not stock Unity mesh
  objects.
- Reuses a plane only when the relevant VRAM, register, scroll, palette, video
  mode, dimensions, and scene state match its committed baseline.
- Measured hit rate: 79.1%.
- Measured background-stage reduction: 49.5%.
- Measured process CPU reduction versus framebuffer cache-off: 19.5%.

Downsides and boundaries:

- Exact state snapshots and retained planes account for part of the memory
  increase.
- Conservative invalidation produces harmless misses; incomplete invalidation
  can produce stale pixels. Every newly supported PPU input must be represented
  in the cache key or force a miss.
- Whole-plane retention helps only when a background is unchanged. Continuous
  scrolling and palette/VRAM effects reduce the hit rate.

Reusable design:

- Cached resources are owned by the replacement renderer. No following stock
  layer can overwrite a shared mesh that a cached layer still references.
- A candidate frame is compared with an immutable committed input snapshot.
  State mutated while building a frame is not accidentally treated as the next
  frame's baseline.
- Invalidation is correctness-first and observable through hit/miss counters.

#### Strict raster-effect partial-row refresh

- Detects the recurring DKC BG2 raster effect whose upper-band horizontal
  scroll changes every four frames at line 81.
- Redraws only rows whose X/Y scroll changed when relevant VRAM is byte-equal
  and BGSC, BGMODE, and character-base state are unchanged.
- Executes the existing full rebuild on any other change.
- A standalone fixture compares every partial output pixel with a clean full
  rebuild and verifies that a relevant VRAM write rejects the shortcut.
- Measured background-stage reduction: 63.9% in the affected scene.
- Measured complete-framebuffer reduction: 16.2%.

Downsides and boundaries:

- The optimization applies only to scroll-only changes. Tile, palette,
  character-base, mode, or map-layout changes take the full path.
- Dirty-region rendering increases state-tracking complexity. Extending it to
  columns, tiles, or arbitrary scanline effects requires equivalent oracle
  coverage and exact invalidation rules.
- The result is workload-dependent; static scenes are already handled by full
  retained-background hits.

Reusable design:

- Dirty regions are derived from semantic PPU changes rather than Unity object
  activity.
- The optimized path and full path share the same output representation, which
  allows exact pixel comparison in tests.
- The full rebuild remains the recovery path for every ambiguous condition.

### `SuperZSNESPerformanceSuiteIL2CPP`

Repository:
[`mods/SuperZSNESPerformanceSuiteIL2CPP`](../mods/SuperZSNESPerformanceSuiteIL2CPP)

#### Bounded scheduler-backlog accounting

- Stock `MasterExecutor.Update` runs at most five owed frames but subtracts all
  owed frames from the accumulator. Time beyond the five-frame cap is lost.
- The mod restores only the proven normal-speed `due > 5` backlog, drains it in
  later batches no larger than the stock cap, and leaves fast-forward
  arithmetic unchanged.
- Two paired 500 ms stall tests improved emulated cadence by 0.62-0.73 FPS in
  the short measurement windows.

Downsides and boundaries:

- This is timeline correctness, not a throughput optimization. It performs the
  work that stock discarded after a hitch.
- Catch-up batches can temporarily increase CPU demand.
- SuperZSNES still presents once per Unity Update, so recovered intermediate
  emulated frames are not necessarily displayed. One accepted pair recovered
  emulation time while presentation cadence fell from 58.53 to 55.55 FPS.
- An upstream scheduler can implement the same accounting directly without a
  Harmony postfix and should define an explicit maximum backlog/drop policy.

Reusable design:

- Track `executed = min(due, cap)` separately from `due` and charge only work
  that actually ran.
- Bound retained debt so resume-after-suspend behavior cannot create an
  unbounded catch-up burst.
- Keep emulation cadence and presentation cadence as separate metrics.

#### History and rewind service guards

- The same project can temporarily disable history screenshots and rewind
  capture during `Update`, restoring the user's exact values afterward.
- These switches prevent known synchronous service work but produced no
  measured steady-state throughput gain.

Downsides and boundaries:

- Disabled capture means the corresponding history or rewind feature is not
  populated during the guarded interval.
- Native settings that persistently disable these services are simpler than a
  runtime guard.
- This is a diagnostic/workaround facility, not part of the measured renderer
  speedup.

Reusable design:

- Expensive screenshots, encoding, and state-history maintenance can be moved
  off the emulation/presentation critical path while keeping feature ownership
  separate from the renderer.

### `SuperZSNESNativeAtlasDirtyFixIL2CPP`

Repository:
[`mods/SuperZSNESNativeAtlasDirtyFixIL2CPP`](../mods/SuperZSNESNativeAtlasDirtyFixIL2CPP)

#### Verified native dirty-branch correction

- Stock 2/4/8-bpp tile accessors mark atlas pages dirty before checking the
  corresponding tile dirty byte.
- A managed Harmony experiment observed about 1.26 million false page hits per
  21-second trial but increased presentation work 37.1% because it placed a
  managed callback on every tile access.
- The native reference patch moves the six page-dirty assignments onto the
  existing true-dirty branches with zero managed hot-path callbacks.
- Four matched trials measured 2.6249 ms stock versus 2.6356 ms patched
  presentation (+0.41%) and 1.2350 versus 1.2234 CPU cores (-0.94%). Both
  differences were inside run-to-run noise.

Disposition and downside:

- The source-level bug is real, but correcting it produced no measurable gain
  in the tested DKC workload. The patch remains disabled and is not part of the
  performance configuration.
- It affects only the legacy Unity tile renderer. Supported framebuffer frames
  bypass those accessors.
- Native instruction patches are binary-specific and require hash gates,
  expected-byte checks, rollback, instruction-cache flushing, and failure-safe
  ordering.

Reusable design:

- Hot-path fixes should be placed at the native/source branch, not behind a
  per-call managed detour.
- The plugin demonstrates a fail-closed startup patch: verify the complete
  binary and every instruction window, install hooks before removing stores,
  and roll back partial application.
- The optimization may be remeasured on legacy-renderer-heavy fade, UI, or
  Mode 7 workloads, but the current DKC gameplay evidence is performance-neutral.

## Stock-renderer source changes retained for future versions

These v0.230 projects target code paths bypassed by the supported v0.300 DKC
framebuffer. Their BepInEx DLLs are not ABI-compatible with v0.300. The
underlying source changes remain relevant wherever the general Unity tile
renderer is retained for other games, Mode 7, menus, or fallback frames.

### `SuperZSNESMaterialCacheGuard`

Repository:
[`mods/SuperZSNESMaterialCacheGuard`](../mods/SuperZSNESMaterialCacheGuard)

- Fixes unbounded retention in `PPURenderer.tileAddrToMat`, whose historical
  `(Material, MaterialPropertyBlock)` keys and `List<TileInfo>` backing arrays
  were never removed.
- A churn test grew from 253 to 210,853 keys in about 35 seconds and retained
  more than 1.2 million list slots.
- The accepted implementation scopes the scratch map to one background and
  reuses type-safe lists from a high-water pool.
- In the observed post-fix run, the live map stayed between 0 and 42 keys;
  allocations plateaued at 1,308 across more than nine million rentals, and
  managed memory cycled around 91-99 MiB instead of growing without bound.

Downside and reusable design:

- A periodic whole-map clear was rejected because it caused deterministic
  100-195 ms stalls. Cleanup belongs at the individual-background lifetime
  boundary, with pooled list capacity retained.
- This primarily fixes memory retention and eventual churn rather than the
  supported DKC framebuffer's steady-state cost.

### `SuperZSNESMeshBoundsOptimization`

Repository:
[`mods/SuperZSNESMeshBoundsOptimization`](../mods/SuperZSNESMeshBoundsOptimization)

- Replaces `Mesh.RecalculateBounds()` after every 2D tile-mesh upload with a
  conservative constant local bound and `DontRecalculateBounds` vertex upload.
- A comparable moving-window test improved presentation from 54.718 to 56.170
  updates/s (+2.65%) and reduced two-frame batches from 169 to 111 over about
  35 seconds.
- The separate mesh-notification batching experiment regressed and remains
  disabled.

Downside and reusable design:

- Broad bounds weaken frustum/occlusion culling and need validation for extra
  cameras, reflection probes, editor views, and unusual transforms.
- The source-level form can preassign a verified conservative bound once for
  viewport-clipped dynamic 2D meshes instead of rescanning all vertices.

### `SuperZSNESRendererFastPaths`

Repository:
[`mods/SuperZSNESRendererFastPaths`](../mods/SuperZSNESRendererFastPaths)

- Converts verified `ContainsKey` plus indexer sequences to one `TryGetValue`
  and removes redundant `HashSet.Contains` before `Add` in the stock renderer.
- Separate switches cover ordinary `DrawLines`, Mode 7 data, tile-list cleanup,
  and dynamic-font paths.
- Exact IL and object-identity/set semantics were verified. The measured
  runtime remained stable at 59.996 emulated Hz, but the available evidence
  does not isolate a causal percentage for this mod alone.

Downside and reusable design:

- These are lower-value source cleanups than the framebuffer architecture and
  should not be represented as a measured v0.300 speedup.
- Benefits are path-specific. Mode 7 and dynamic-font rewrites do nothing for
  ordinary DKC Mode 1 gameplay; the retained framebuffer bypasses the ordinary
  material path.
- In source, a single lookup is simpler and avoids the Harmony/native-detour
  risk that made similar hot-path runtime experiments unattractive.

### `SuperZSNESPerformanceGuard`

Repository:
[`mods/SuperZSNESPerformanceGuard`](../mods/SuperZSNESPerformanceGuard)

- Disables optional rewind snapshots and synchronous history screenshots when
  those services are not required.
- Can retain the emulator's existing 796x448 internal PPU surfaces instead of
  allocating four 1592x896 surfaces solely because the window is large.
- Later sessions observed roughly 110 MiB less working memory with the service
  and 2x-surface configuration.

Downside and reusable design:

- Disabling history/rewind removes those features during the disabled period.
- Render-surface sizing must preserve integer SNES pixels and final output
  quality. It trades unused internal oversampling for lower memory/GPU work.
- VSync-off and alternate 90/120/240 Hz limiter experiments were
  content-dependent or regressed and are not part of this retained source
  change.

## Correctness and instrumentation bugs fixed

These are verified fixes in the same repository but are not all performance
optimizations.

| Bug | Repository mod | Fix and status | Performance relevance |
| --- | --- | --- | --- |
| Accumulator charged frames that were due but never executed | `SuperZSNESPerformanceSuiteIL2CPP` | Bounded normal-speed backlog recovery; optional | Corrects lost emulation time after a hitch; can add catch-up work and does not guarantee presentation of intermediate frames. |
| Atlas pages marked dirty for unchanged tiles | `SuperZSNESNativeAtlasDirtyFixIL2CPP` | Six native dirty-branch corrections; verified but disabled | Correctness/source cleanup was performance-neutral in four matched DKC trials. |
| `RenderLines` processed the priority-starting OAM sprite twice | `SuperZSNESLayerDepthControllerIL2CPP` | Native loop limit changes 129 passes to exactly 128; active in the optional 3D controller | Flat rendering normally hides the duplicate; 3D depth separation exposes it. Local work removed is below 1%. |
| Fallback diagnostics wrote `status.json` synchronously on every unsupported frame | `SuperZSNESDKCFramebufferRendererIL2CPP` | Writes at burst start, every 120 fallback frames, and burst end | Prevents the measurement system from adding disk-I/O hitches to fallback bursts. |
| Native-width DKC intro/title/file-select layouts repeated tilemaps into widescreen margins | `SuperZSNESDKCFramebufferRendererIL2CPP` | Exact layout gates paint only the extensions black; supported gameplay remains wide | Visual compatibility fix, not a throughput result. Mode-specific gates preserve normal gameplay. |
| Optional 398-wide Nintendo Presents Mode 5 fallback inherited gameplay margins | `SuperZSNESDKCFramebufferRendererIL2CPP` | Canonical DKC Mode 5 fallback temporarily uses zero stock-renderer margin, then restores settings | Visual compatibility fix for an unsupported-mode fallback. |
| Fixed 32-column DKC maps wrapped unrelated tilemap regions into wide margins | `SuperZSNESDKCFramebufferRendererIL2CPP` | Exact fixed-screen PPU signature renders black outside native X 0..255 | Visual compatibility fix; scrolling/64-column gameplay remains wide. |
| Slip-Slide Ride additive shimmer used the wrong empty-subscreen and blend semantics | `SuperZSNESDKCFramebufferRendererIL2CPP` | Empty subscreen is opaque black; captured gamma-aware add is implemented with a 16 KiB lookup table | Correctness fix in the replacement compositor; verified against captured GPU output within one 8-bit channel value. |
| `tileAddrToMat` retained historical scratch keys and list capacity without bound | `SuperZSNESMaterialCacheGuard` | Per-background scratch lifetime and high-water list pool | Prevents unbounded memory/churn; periodic clearing was rejected because it caused 100-195 ms stalls. |

The duplicate-OAM correction is useful to a future native 3D renderer even if
the broader 3D mod is not adopted: the stock loop's wrapped 129th pass submits
one sprite twice at different order depths. The intro and fallback-margin fixes
illustrate a different reusable rule: native-width scenes should be identified
from exact PPU layout state and pillarboxed at presentation time rather than
globally reducing the gameplay render width.

## Integration facts

- The production performance configuration consists of
  `SuperZSNESDKCFramebufferRendererIL2CPP` with retained backgrounds and the
  automatic strict partial-row path.
- The scheduler recovery is an independent optional correctness change.
- The history/rewind switches are independent service controls, not renderer
  throughput improvements.
- The managed atlas gate is rejected. The native atlas fix is retained only as
  a verified reference and remains disabled.
- Older dictionary, material, mesh, draw-loop, and stock-background-cache mods
  target the renderer that supported framebuffer frames bypass. They are not
  part of the v0.300 implementation set.
- `Assembly-CSharp.dll` and `GameAssembly.dll` remain unchanged on disk; runtime
  mods are exact-version gated and restore native bytes on unload where native
  patching is used.

## Evidence scope and limitations

- Ordinary configurations used two fresh processes, 12 seconds of warmup, and
  approximately 20-21 seconds of measurement with VSync at 1.
- Partial-row results used two fresh 60-second trials per configuration.
- Native atlas results used four fresh-process trials per configuration.
- The benchmark workload is one difficult DKC gameplay scene. Fades, menus,
  Mode 7, unsupported states, and a full-playthrough fallback rate were not
  characterized by the same dataset.
- Process CPU includes emulation, Unity, audio, presentation, and diagnostics.
- Memory values are endpoint samples rather than long-run plateau measurements.
- The large renderer effects are replicated and clearly separated from noise.
  Sub-1% CPU differences are not treated as established gains.

Reviewed data:

- [`docs/benchmarks/v0300/benchmark-results.json`](benchmarks/v0300/benchmark-results.json)
- [`docs/benchmarks/v0300/raster-partial-results.json`](benchmarks/v0300/raster-partial-results.json)
- [`docs/V0300_OPTIMIZATION_PORT.md`](V0300_OPTIMIZATION_PORT.md)
