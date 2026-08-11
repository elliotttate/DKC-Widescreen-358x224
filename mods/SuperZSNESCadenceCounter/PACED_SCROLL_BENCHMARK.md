# Paced Jungle scrolling A/B

## Protocol

Use `DKCLevelAutomation/cli/run_realtime_scroll.py` with its `realtime-jungle-right-y-cadence` recipe. It installs a long scheduled RIGHT+Y input and calls `resume`; it never calls `run_frames` or `step_frames`. Keep CadenceCounter `RendererBreakdown=false` for the clean pacing comparison.

Run the baseline and candidate in separate, otherwise identical emulator sessions and always load the same clean Jungle state. The runner deliberately changes emulator state, so invoke it only after live play is no longer needed.

```powershell
$automationSource = "<superzsnes-source>\Mods\DKCLevelAutomation"
$automationEndpoint = "<superzsnes>\BepInEx\plugins\DKCLevelAutomation\bridge.json"
$cadenceRoot = "<superzsnes>\BepInEx\plugins\SuperZSNESCadenceCounter"
$runs = "<workspace>\RealtimeScrollRuns"
$rom = "<workspace>\DKC_Widescreen_358x224.sfc"
$state = "<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2"

python "$automationSource\cli\run_realtime_scroll.py" `
  --endpoint $automationEndpoint --cadence-root $cadenceRoot --output-root $runs `
  --label baseline --rom $rom --state $state
# Stop normally, change only the condition under test, restart, and repeat:
python "$automationSource\cli\run_realtime_scroll.py" `
  --endpoint $automationEndpoint --cadence-root $cadenceRoot --output-root $runs `
  --label candidate --rom $rom --state $state
```

Each run loads the ROM/state while paused, schedules `0-7199=RIGHT+Y`, resumes for a seven-second wall-clock warmup plus a 30-second wall-clock measurement, then pauses. Its manifest records ROM/state hashes, exact UTC measurement bounds, start/end emulated frames, WRAM camera/layer positions, and the active CadenceCounter file.

Compare the two manifests from the source tree:

```powershell
cd <superzsnes-source>\Mods\SuperZSNESCadenceCounter
python .\tools\compare_sessions.py `
  --baseline "D:\Runs\20260811-190000-baseline\manifest.json" `
  --candidate "D:\Runs\20260811-191000-candidate\manifest.json" `
  --enforce --require-improvement `
  --output "D:\Runs\cadence-comparison.json"
```

When manifests are supplied, the parser accepts only `reason=interval` windows fully contained by the measurement bounds. This excludes warmup, resume/pause edges, and partial boundary windows. Session folders and direct `windows.jsonl` files are also accepted, but they cannot prove identical ROM/state or active scrolling.

Keep these variables fixed: ROM/state hashes, window size/resolution, foreground/focus state, audio configuration, rewind/history settings, `vSyncCount`, `targetFrameRate`, diagnostic plugin set, and controller macro. Use at least three A/B pairs if an OS scheduling outlier changes the conclusion.

## Acceptance metrics

- Correctness: at least four full windows per condition; 59.5-60.5 emulated frames/s; zero orphan frames; stable and identical `vSyncCount`/`targetFrameRate`; matching ROM/state hashes; and at least 64 pixels of positive camera or layer-1 X movement.
- No regression: candidate Update Hz is at least 98% of baseline, weighted Update duration is at most 102% of baseline, and the share of Updates executing two or more emulated frames rises by no more than 0.02.
- Material improvement: Update Hz improves by at least 5%, or weighted Update duration falls by at least 5%. `--require-improvement` enforces this last gate; without it the parser still reports it.

The report includes maximum Update duration and cadence gap but does not hard-gate one maximum. A single OS scheduling outlier is not enough evidence for a renderer change.

## Safe background-geometry reuse design

The decompiled `PPURenderer.DrawLines` derives tile membership from `(scrollX - (scrollX & 7)) >> 3` and the equivalent Y bucket. Fractional-tile movement is separately written to `_TileScroll` in a `MaterialPropertyBlock`. For uniform-scroll 2D layers, geometry can therefore remain unchanged while movement stays inside the same 8-pixel buckets. Avoiding `DrawLines`, material bucketing, `Process2DTiles`, vertex/UV uploads, and bounds work on those frames is potentially high impact.

A plain early return from `GenerateBackground` is unsafe. `GenerateBackgrounds` resets five global mesh-pool indices, and all BG layers share those pools. A later generated layer can overwrite meshes still referenced by a skipped layer. Property blocks also carry per-frame scroll, palette, color, mosaic, window, and clip state. Offset-per-tile, scanline-varying, dynamic-font, and Mode 7 paths need stricter handling.

A safe implementation order is:

1. Give each BG layer persistent ownership of its mesh ranges, or reserve its prior ranges on a cache hit.
2. Separate the geometry key from per-frame material-property state. Key geometry on tile X/Y buckets, BG mode/base/size/CHR layout, dimensions/widescreen parameters, and a signature of scanline and offset-per-tile behavior.
3. Add dirty/version counters for relevant tilemap/CHR VRAM and CGRAM writes plus PPU/window/color-math/scene changes. Refresh property blocks even on geometry hits.
4. Initially enable reuse only for simple uniform-scroll 2D layers: no Mode 7, offset-per-tile, dynamic font, or changing scanline signature.
5. Once same-bucket reuse is proven, update only the entering tile strip at a boundary rather than rebuilding the visible grid.

Validation must compare captures or pixel hashes for all eight sub-tile offsets and both directions across tile boundaries, plus palette animation, window/color math, mosaic, fade, pause, load-state, and scene transitions. No renderer caching patch is included here.
