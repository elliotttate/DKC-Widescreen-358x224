# Project status

The repository preserves accepted work, diagnostic tooling, and rejected experiments so results remain reproducible. Runtime patches are version-checked and leave `Assembly-CSharp.dll` unchanged on disk.

## Release tooling

| Project | Status | Purpose |
| --- | --- | --- |
| `tools/DKCWidescreenPatcher` | Recommended | One-click Windows GUI that validates a clean DKC USA v1.0 ROM, applies an embedded BPS variant, verifies the exact output, and never overwrites the original. |
| `scripts/build-release.ps1` | Maintainer tool | Generates standard/Deluxe/Restoration BPS patches from checksum-locked builds, embeds them, independently reapplies all three, rebuilds the IL2CPP renderer, and assembles the GitHub release ZIP. |

## Recommended runtime components

### SuperZSNES v0.300 IL2CPP

| Project | Status | Purpose |
| --- | --- | --- |
| `SuperZSNESDKCFramebufferRendererIL2CPP` | Recommended | Supported DKC Mode 1 CPU compositor and retained-background presentation path. This is the major v0.300 performance improvement. |
| `SuperZSNESPerformanceSuiteIL2CPP` | Optional, all switches default off | Native-code audit probes, bounded backlog recovery, reversible history/rewind guards, and a rejected atlas-upload experiment retained for reproducibility. |
| `SuperZSNESNativeAtlasDirtyFixIL2CPP` | Verified reference, keep disabled | Exact hash/byte-gated native correction for the atlas dirty-flag bug. It removes managed hot callbacks but showed no measurable performance benefit in four matched trials. |
| `SuperZSNESLayerDepthControllerIL2CPP` | Experimental, default off | Exposes v0.300's hidden perspective renderer, restores per-priority depth controls, compensates head-on projection, and optionally subdivides DKC BG priority planes by their authored SNES palette groups. |

The v0.300 benchmark and port decision matrix are in
[the v0.300 optimization report](docs/V0300_OPTIMIZATION_PORT.md). Do not copy
the Mono-targeted v0.230 DLLs into the IL2CPP build.

### SuperZSNES v0.230 Mono

| Project | Status | Purpose |
| --- | --- | --- |
| `SuperZSNESDKCFramebufferRendererIL2CPP` | Recommended for v0.300 only | BepInEx 6/x86 IL2CPP port of the accepted DKC framebuffer compositor. |
| `SuperZSNESDKCFramebufferRenderer` | Recommended | Supported DKC Mode 1 CPU framebuffer and presentation path; the major performance fix. |
| `SuperZSNESFramePacingFix` | Recommended | Charges only frames actually scheduled and preserves bounded normal-speed backlog. |
| `SuperZSNESPerformanceGuard` | Recommended | Disables history/rewind spikes, caps presentation at 120, and limits PPU surfaces to 2x. |
| `SuperZSNESMaterialCacheGuard` | Recommended | Reuses per-background scratch lists; diagnostics remain optional. |
| `SuperZSNESMeshBoundsOptimization` | Recommended with notification batching off | Uses conservative fixed mesh bounds. Keep `BatchMeshNotifications=false`. |
| `SuperZSNESRendererFastPaths` | Recommended | Verified dictionary/HashSet lookup reductions. |
| `SuperZSNESRenderLinesLoopFix` | Recommended | Corrects the 129-iteration OAM loop to process exactly 128 sprites. |
| `SuperZSNESDKCBackgroundStateCache` | Optional | Exact-state stock-background cache; useful on frozen backgrounds and fail-closed elsewhere. |

## Automation and diagnostics

| Project | Purpose |
| --- | --- |
| `DKCLevelAutomation` | Authenticated local control bridge, exact-frame schedules, WRAM conditions, recipes, and regression runner. |
| `DKCWidescreenDebugger` | Full WRAM/VRAM/CGRAM/OAM/PPU capture and authenticated local MCP bridge. |
| `DKCTilemapInspector` | Raw tilemap reconstruction, seam/staleness analysis, and capture bridge. |
| `DKCTileStreamTracer` | Opt-in CPU/PPU/DMA stream trace around the DKC tile streaming routines. |
| `SuperZSNESCadenceCounter` | Lightweight Update/RunFrame cadence and batching counter. |
| `SuperZSNESAllocationProbe` | Allocation and GC investigation. |
| `SuperZSNESAudioTimingProbe` | Audio callback/lock timing investigation. |
| `SuperZSNESPaletteCacheProbe` | Palette-cache churn diagnostics. |
| `SuperZSNESRendererTimingProbe` | Detailed renderer stage timing. |
| `SuperZSNESRuntimePauseProbe` | Runtime-wide pause and scheduler-gating classification. |
| `SuperZSNESPerformanceBench` | Process-only cadence/memory benchmark and comparison scripts. |
| `SuperZSNESFramebufferOracle` | Deterministic pixel/capture comparison harness. |

## Experimental, superseded, or rejected

These projects are retained as evidence and default to disabled or fail-closed behavior.

| Project | Disposition |
| --- | --- |
| `SuperZSNESBackgroundCallGuards` | Rejected after severe missing-geometry visual failure; combined mode is quarantined. |
| `SuperZSNESCoreOptimizations` | ReadMem and tile-material rewrites showed no benefit/regressed; keep switches false. |
| `SuperZSNESDKCWidthMarginOverride` | Rejected: margin 6 exposes only 352 px and clips the requested 358 px view. |
| `SuperZSNESDrawLinesCacheGate` | Experimental, disabled pending broader visual validation. |
| `SuperZSNESFramebufferPresentationPrototype` | Superseded by `SuperZSNESDKCFramebufferRenderer`. |
| `SuperZSNESMeshDynamicUploadOptimization` | Experimental/rejected in the accepted runtime configuration. |
| `SuperZSNESTileMeshStateGuards` | Rejected performance experiment; keep disabled. |
| `SuperZSNESVariableMaterialBatching` | Visual failure; v0.1.1 hard-quarantines enabled mode. |

Read the individual README and [technical worklog](docs/WORKLOG.md) before changing any default-off switch.

The remaining projects in this matrix target the v0.230 Mono build unless their README explicitly says otherwise.
