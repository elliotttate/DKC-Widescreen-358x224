# Project status

The repository preserves accepted work, diagnostic tooling, and rejected experiments so results remain reproducible. Runtime patches are version-checked and leave `Assembly-CSharp.dll` unchanged on disk.

## Recommended runtime components

| Project | Status | Purpose |
| --- | --- | --- |
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
