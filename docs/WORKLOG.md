# DKC 358x224 Widescreen Engineering Log

This is the living source of truth for the Donkey Kong Country widescreen ROM hack and its SuperZSNES support. Update this file whenever a hypothesis is confirmed, rejected, implemented, or tested. Older handoffs are historical context and may describe superseded builds.

## Project objective

- Rebuildable DKC USA v1.0 ROM hack with real additional world visibility.
- Target presentation: approximately 358x224 on the user's 16:10 PC display.
- Correct background streaming, sprites, object activation, menus, transitions, and level boundaries.
- Emulator-specific support and automation implemented through BepInEx rather than permanent edits to SuperZSNES assemblies.
- Deterministic level loading, input playback, raw PPU/OAM inspection, and full-monitor screenshot verification.

## Canonical runtime files

- ROM: `<workspace>\DKC_Widescreen_358x224.sfc`
- Clean Jungle Hijinxs checkpoint: `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2`
- Emulator: `<superzsnes>\SUPERZSNES.exe`
- Decompiled emulator tree: `<superzsnes-source>`
- ROM patch source: `<workspace>\DKC1_Disassembly\DKC1\Custom\Patches\Widescreen_358x224.asm`
- ROM build script: `<workspace>\DKC1_Disassembly\build-widescreen.ps1`
- Runtime widescreen support source: `<workspace>\SuperZSNES.DKC.Widescreen`
- Installed widescreen support: `<superzsnes>\BepInEx\plugins\DKC-Widescreen\SuperZSNES.DKC.Widescreen.dll`
- WSLSnapit project: `<user-home>\Documents\GitHub\WSLSnapit-MCP`

## Current verified build

As of 2026-08-11:

- Corrected ROM SHA-256: `2EF5CADDDF801197542821AAD03AE8EBDB0F011E863CF307C24EE7663ABA0CD2`
- Installed widescreen-support DLL SHA-256: `B8212A91671084050BB75C78A87919D197D1979810071D5A2D84936E6717A13B`
- BepInEx support version: `0.2.0`
- Widescreen settings during supported gameplay: BG 7, OBJ 7, color 7, Mode 7 0.
- Dynamic gameplay gate uses the widened DKC camera-bound span and returns the renderer to zero extra columns outside supported wide gameplay.
- The ROM renders a 368-pixel-wide safety surface (7 extra 8-pixel columns on each side); SuperZSNES presentation crops about 5 pixels per side for the approximately 358x224 view.

## Background-streaming fix

### Symptom

Fresh Jungle Hijinxs entry originally showed stale/incorrect art on the visible right extension. Earlier screenshots that captured only an internal render target were misleading; final visual judgment must use the full Windows monitor/window.

### Root cause

The stock vertical row builder stages 36 tile entries, which covers the native viewport but not the widened right margin during simultaneous entry pan/vertical streaming. Newly uploaded rows therefore retained stale entries in the wide region.

### Implemented fix

- Wide-only wrappers around the standard vertical row builders at SNES addresses `$81890E` and `$818CEF`.
- The wrapper preserves the stock aligned-Y early-out.
- In wide mode it runs the stock row body twice with Layer1X biases `-$38` and `+$58`, restores `$088B`, and lets the normal full-row DMA execute once.
- Narrow mode jumps directly to the stock body.
- Keep this corrected double-pass implementation. Do not revert to the earlier speculative `+$0138` initializer experiment.

### Evidence

- Validated capture: `capture-f00003370-20260811-111154-105` under the installed `DKCTilemapInspector` captures.
- BG1 visible viewport signatures for ring columns x40 through x48 exactly match the appropriate stock oracle/future streamed columns.
- Raw reconstructed BG1 is continuous across both the native boundary and right extension with no high-seam or stale candidates.
- Remaining full-ring differences were below the current viewport and are a vertical-scroll regression target, not a fresh-entry blocker.
- Full-monitor reference: `<workspace>\DKC-widescreen-fixed-full-window.png`.

## Banana alignment and interaction investigation

### Jungle spin-phase report

The bananas looked slightly misaligned in the widened full-screen view.

### Confirmed result for the Jungle screenshot

This is not an OAM coordinate, widescreen wrap, multi-tile seam, or BG/OBJ camera bug.

- Raw OAM from the clean checkpoint shows the three visible bananas as large 16x16 OBJ entries 15, 14, and 13.
- X positions are 195, 219, and 243: exactly 24 SNES pixels apart.
- All three have exactly Y=143, identical attributes/size, and a common center.
- Tile bases are `$EC`, `$E2`, and `$E8`, which are deliberately different spin-animation phases.
- The middle phase's opaque artwork is one SNES pixel shorter at both top and bottom while remaining center-aligned. Full-screen scaling magnifies this intended silhouette difference.
- Independent ROM-graphics inspection confirms eight animation frames made from four 8x8 cells per 16x16 banana. DKC deliberately phase-staggers bananas in formations with a position/index-dependent frame calculation.
- SuperZSNES places each 8x8 OBJ cell at exact 8-pixel increments and uses the same camera basis for BG and OBJ.

Decision: do not alter authentic coordinates or animation. A phase-lock option could be made later as an explicitly non-authentic cosmetic option, but it is not a bug fix.

This conclusion applies only to the relative silhouettes in the original Jungle screenshot. It did not rule out the separate cave formation-coordinate bug below. A temporary synchronized-spin build was tested and rejected because it addressed the wrong symptom; `!DKC1_Wide_EnableSynchronizedBananas` remains `!FALSE`.

### Cave formation position and pickup bug

Original user reproduction slot: `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst0` (saved around 12:10 local time, later overwritten by the cave-exit reproduction). The original banana scene remains preserved in the debugger captures and deterministic pickup evidence below.

Observed symptom:

- Every static banana-formation tile was 56 SNES pixels too far right.
- A final-OAM correction made the art look correct, but collection still occurred at the old position. The visible banana and its gameplay hitbox therefore disagreed.

Root cause and measured state:

- Layer1X was `$6A70`; widened bounds were `$6938..$6BC8`.
- The authored/native lower bound is `$6900`. Widescreen moves it inward by `$38`.
- `CODE_B8B503` computes formation camera basis `$BE = Layer1X - $1B23`, producing `$0138` instead of the native `$0170`.
- Formation drawing at `$B8BA67` and `$B8BACA` therefore emitted every tile exactly `+$38` on screen.
- Raw OAM proved the grid X positions were `176/208/240`; the correct positions are `120/152/184`. The single banana was `112`; the correct position is `56`.
- Collision is a separate path at `$B8BB2E`. It reloads the authored formation X into local `$56`, so a visual-only OAM correction leaves the pickup mask 56 pixels to the right.

Implemented ROM fix in `Custom\Patches\Widescreen_358x224.asm`:

- `$B8BA67` and `$B8BACA` call `DKC1_Wide_AdjustBananaScreenX`, which subtracts `$38` from the final 16-bit OAM X in wide mode while preserving the stock candidate scan, clipping, animation, and OAM allocation.
- `$B8BB2E` replaces only the four-byte formation-X load with `DKC1_Wide_GetBananaCollisionX`. It subtracts the same `$38` from the local copy stored in `$56`; persistent formation data at `$7EC000` is not changed.
- The adjusted `$56` also flows into `$B8BC68`, so the spawned pickup effect uses the corrected screen position rather than jumping back to the old X.
- The phase-lock experiment remains disabled.

Deterministic validation:

- On the new ROM, the saved cave state starts with banana count 47. Holding RIGHT for 32 frames leaves it at 47 with the player probe at `$018A`; the next frame moves the probe to `$018B` and increments the count to 48 while overlapping the corrected nearby banana.
- With the prior canonical ROM, the same state and 50 RIGHT frames left the count at 47 even after the player probe reached approximately `$01A6`.
- Exact before/after captures: `capture-f00006631-20260811-122539-083` and `capture-f00006632-20260811-122539-764` under debugger session `20260811-115840`.
- Build command: `cmd /c Assemble_DKC1.bat HACK_DKC_Widescreen_358x224` from `<workspace>\DKC1_Disassembly\DKC1`.
- Promoted ROM SHA-256: `83DAA2443F9E910C4158B7A0E722BE6D224F9A54D3BD5A05770BF72A02C582A8`.
- Pre-fix backup: `<workspace>\DKC_Widescreen_358x224.pre-banana-position-logic-fix.sfc`, SHA-256 `D593DA7A07FE52192F67D51EECC33EF4BD5C020516EA42341BBA210BD9CB72BE`.

Reference comparison: `<user-home>\Documents\GitHub\wide-snes` reinforces the architecture used here. Its SMW patch keeps interaction math in world coordinates and patches special render/offscreen paths explicitly. DKC banana formations are likewise a special subsystem that bypasses the normal sprite world-to-screen path, so both its presentation and interaction conversion must share the same widescreen offset.

Remaining banana-formation regression targets:

- Pickup effects in the newly visible negative-X margin can still hit the stock `$B8BC68` negative-X rejection; this is cosmetic and needs a dedicated left-edge test.
- Test a genuinely narrow room because the current helper uses the same runtime wide/narrow bound-span predicate as the other formation correction path.

### Banana formation late-load fix

Observed symptom: banana formations could be visibly inside the widened right side before DKC's private formation renderer emitted them. The normal sprite and object-activation patches did not help because formations use a separate bank-`$B8` scan-and-clip path.

Root cause:

- `$B8B91B` used the stock 256-pixel scan span (`ADC #$0100`).
- `$B8B942` used the matching overlap allowance (`ADC #$010F`).
- `$B8BA11` clipped the per-formation tile chain at `#$0107`.
- The existing final OAM correction then shifted emitted bananas left by `$38`, making the effective right coverage end at only about 207 pixels. Bananas in the new right margin were therefore not emitted until the camera moved farther right.

Implemented fix:

- `$B8B91B`: `ADC #$0100` -> `ADC #$0170`.
- `$B8B942`: `ADC #$010F` -> `ADC #$017F`.
- `$B8BA11`: `LDA #$0107` -> `LDA #$0177`.
- These are operand-only changes. After the existing final `-$38` X correction, the effective right clip is `$013F`, one tile beyond the 312-pixel internal right sample edge. The stock OAM-exhaustion exit and all formation/collision data remain intact.
- The exact second instruction is `$B8B942`; `$B8B936` is the loop's formation-X load and must not be patched.

The clean Jungle checkpoint is unchanged byte-for-byte in OAM and rendered PNG before/after this widening because that particular frame has no omitted right-margin members. The correction is algebraically verified against the cave formation that previously emitted only four of seven intended columns. A dedicated far-right formation save remains useful as a future visual regression fixture.

## Cave exit and terminal movement range

### Reproduction

- Preserved state: `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-cave-exit-repro`.
- State SHA-256: `E71DB0A516AF54F3C63D695C69FA24191193C64CC613FD848833BA1C57BCC107`.
- Scene: Jungle Hijinxs Bonus 1, level `$0009`, entrance `$0006`.
- The logic-only exit-door sprite (ID `$006A`) was active at authored world position `$6CD0,$004F`, with the correct destination entrance `$0008`.
- DK/Rambi were hard-stopped at world X `$6CB8`, exactly 24 pixels short of the door. The door was not culled or misplaced.

### Root cause

The level's stock horizontal bounds are `$6900..$6C00`. Widescreen correctly moves the camera bounds inward by `$38` to `$6938..$6BC8`, but `CODE_BF86DE` still used DKC's native active-Kong endpoint probes:

- Left probe: `playerX - $0012` compared with `$1B23`.
- Right probe: `playerX - $00EE` compared with `$1B25`.

At the widened terminal camera position, the stock right probe zeros the player's horizontal speed at approximately `Layer1X+$F0`, producing the measured `$6CB8` stop. The final 56 world pixels—including the authored exit trigger—became unreachable. Rambi follows the active Kong, so this also occurs while riding.

### Implemented fix

- `$BF86E7..$BF86ED` now calls `DKC1_Wide_GetPlayerLeftBoundaryProbe`.
- `$BF86FA..$BF8700` now calls `DKC1_Wide_GetPlayerRightBoundaryProbe`.
- Both helpers use the existing camera-span predicate. Narrow rooms retain the original `$0012/$00EE` calculations.
- Wide gameplay uses a symmetric `-$38..294` screen range: the left probe is `playerX+$0026` (equivalent to subtracting `$FFDA`) and the right probe subtracts `$0126` (`$00EE+$38`). This restores the original world-space endpoints without moving the camera or the exit object.
- Do not move the ID `$006A` door or special-case its comparison; its `$6CD0` coordinate is authored data and is correct.

### Deterministic validation

- Candidate/canonical ROM loaded with the preserved state and one neutral frame.
- With exact RIGHT+Y input, entrance `$0006` changed to `$0008` after 62 emulated frames.
- A 90-frame checkpoint captured the normal black transition frame at `capture-f00007327-20260811-123856-349`.
- After 400 additional neutral frames, `capture-f00007727-20260811-123903-386` shows DK, Diddy, and Rambi restored in the outdoor Jungle Hijinxs level. The user visually confirmed the completed return.
- `cave-exit-right-y.json` under `DKCLevelAutomation` records the initial exit object/bounds/player state, applies exact RIGHT+Y input, asserts the entrance transition, advances 400 neutral frames through the fade, and then asserts a fully visible outdoor scene (`$051A=$000F`), wide bounds `$0038..$13C8`, and restored Donkey/Diddy sprite IDs.
- The installed end-to-end canonical run passed all three checkpoints at frames 7238, 7328, and 7728. Evidence: `<workspace>\RegressionRuns\cave-exit-canonical-20260811`.
- A full SuperZSNES window capture at the final checkpoint showed the complete outdoor 358x224 view with DK, Diddy, Rambi, and the bonus token. The emulator was then resumed with all deterministic input schedules cleared.

Promoted build:

- Canonical ROM SHA-256: `2EF5CADDDF801197542821AAD03AE8EBDB0F011E863CF307C24EE7663ABA0CD2`.
- Pre-fix backup: `<workspace>\DKC_Widescreen_358x224.pre-banana-coverage-exit-boundary-fix.sfc`, SHA-256 `83DAA2443F9E910C4158B7A0E722BE6D224F9A54D3BD5A05770BF72A02C582A8`.
- Temporary candidate is byte-identical to the promoted canonical ROM.

## Performance investigation

### Confirmed static findings

1. `DKCTileStreamTracer` currently Harmony-patches `CPU65c816.ExecuteNextInstruction` at plugin load and calls its prefix for every emulated CPU instruction even while the tracer is disarmed. The handler exits quickly when disarmed, but the Harmony interception itself remains on the hottest emulator path and is a likely major source of slowdown.
2. `MasterExecutor.Update` copies a complete rewind state at 8 Hz. `MainMenuManager.LoadMainMenuSave` forcibly sets `rewindFPS=8` and `numRewindFrames=240`; the update condition does not consult `rewindDisabled` before taking snapshots.
3. SuperZSNES creates four 1592x896 PPU render textures whenever screen height is above 448. This is an exact 4x internal surface for the fixed 398x224 presentation target. It adds GPU work, though the instruction hook and rewind copies are currently stronger CPU-side suspects.
4. The installed debugger set currently consists of five BepInEx plugins: level automation, tilemap inspector, tile-stream tracer, widescreen debugger, and widescreen support.
5. The BepInEx chainloader recursively scans the debugger MCP's Python virtual environment because it is stored below `BepInEx\plugins`. This mainly affects startup and log noise, not normal emulation frame time.

### Planned/active fixes and tests

- Change the tile-stream tracer to attach hot CPU/PPU hooks only while armed and unpatch them immediately when disarmed.
- Add a configurable performance profile that can disable background rewind/history state capture during normal play without removing debugging functionality.
- Benchmark emulated-frame progress and process CPU time with identical ROM/state/window conditions:
  1. Current diagnostic stack.
  2. Dynamic tracer hooks while disarmed.
  3. Rewind/history snapshots disabled.
  4. Combined optimized configuration.
- Keep the widescreen patch enabled for all comparable runs; then optionally compare native-width rendering to quantify the renderer's added cost.
- Verify final frame pacing in actual gameplay, not only at a paused checkpoint.

Status: implementation and controlled benchmarks are in progress. Do not record performance hypotheses as final until measurements are added here.

### 2026-08-11 implemented performance changes and measurements

- `DKCTileStreamTracer` v0.1.1 now resolves the CPU/PPU methods at load but attaches the per-instruction and per-PPU-write Harmony prefixes only while a trace is armed. Disarming immediately removes both hot hooks. The once-per-frame hook remains for control/status.
- `DKCWidescreenDebugger` v0.1.1 skips watch polling when the watch list is empty and tracks dirty log writers so an idle frame no longer issues redundant filesystem flushes. The installed placeholder `7E0000:u16:example_camera_x` watch was cleared.
- `DKCLevelAutomation` v0.1.1 returns before controller-array reflection when no exact schedule is active.
- New `SuperZSNESPerformanceGuard` v0.1.0 applies runtime-only settings before rewind-buffer setup: `rewindFPS=0`, `numRewindFrames=0`, `rewindSpeed=0`, and `historyDisabled=true`. It is configurable, does not alter the executable or `Assembly-CSharp.dll`, and makes rewind/history unavailable while enabled.

Comparable 10-second-warmup / 20-second Jungle measurements:

- Before: 60.607 emulated fps, 61.953 CPU seconds over 20.014 wall seconds (309.55% of one core), 440.87 MB working set.
- Optimized diagnostics + PerformanceGuard: 59.972 emulated fps, 63.156 CPU seconds over 20.009 wall seconds (315.64% of one core), 375.30 MB working set.
- Confirmed result: nominal frame rate is maintained and rewind-buffer prevention saves 65.57 MB (14.9%) of working set. CPU difference is within run-to-run noise and is not claimed as an improvement.

`MainMemoryMap.ReadMem` Count-guard + `TryGetValue` IL experiment (same ROM/state, 600 warm-up frames, then three consecutive 600-frame samples):

- Stock samples: 10.241 / 10.511 / 10.810 seconds; median 10.511 seconds and 50.938 ms process CPU per emulated frame.
- Rewritten samples: 10.808 / 10.853 / 10.148 seconds; median 10.808 seconds and 55.182 ms process CPU per emulated frame.
- Decision: rejected as a performance optimization (2.8% worse median wall time and 8.3% worse median CPU/frame). The version-checked experiment remains in `SuperZSNESCoreOptimizations` for reproducibility but is disabled by default and in the installed config.

### Initial measured baseline

Controlled Jungle checkpoint measurement with the five-plugin diagnostic stack, after 10 seconds of warmup and over a 20-second sample:

- Emulated rate: 60.61 frames/second.
- Process CPU: 309.6% of one logical core, or approximately 3.10 fully used logical cores by the benchmark's normalization.
- Working set: 440.9 MB.
- A shorter comparison with debugger, tile-stream tracer, and tilemap inspector disabled still measured about 60.6 emulated frames/second and roughly 318% CPU. This indicates that the current user-visible complaint is more likely high CPU cost/frame pacing in SuperZSNES core/background services than failure to sustain the nominal SNES frame count. Longer isolated component benchmarks remain required before attributing exact percentages.

### PerformanceGuard A/B result

Using the same Jungle checkpoint, 10-second warmup, and 20-second measurement window, preventing the 240 rewind slots from being allocated and disabling unused rewind/history background services produced:

- Emulated rate: remained nominal at approximately 60 frames/second.
- Working set: 440.9 MB -> 375.3 MB, a reduction of 65.6 MB or 14.9%.
- CPU: noisy and effectively unchanged in this sample (approximately 3.10 vs 3.16 normalized cores). Do not claim a CPU improvement from this patch.

Conclusion: keep as a configurable memory/startup optimization for normal play, not as the answer to frame-time cost. Exact 600-frame core benchmarks and the `ReadMem` IL fast path are the next isolation tests.

### 2026-08-11 hot-path review, source-verified

The user supplied a focused review of the decompiled v0.230 source. The following patterns were checked against the local files and confirmed exactly:

1. `MainMemoryMap.ReadMem` (`MainMemoryMap.cs`, lines 525-531 in the exported source) performs `cheatCodes.ContainsKey(addr)` and then a second dictionary index lookup on every emulated memory read. This includes opcode fetches, operands, and data accesses. Because a normal Harmony prefix would add overhead on every call, the accepted implementation direction is a one-time IL transpiler or BepInEx preloader/Cecil rewrite. Preserve cheats, but provide a plain empty-cheat fast path and a single `TryGetValue` lookup when cheats exist.
2. `TileTextureGen.GetTileMaterial` (`TileTextureGen.cs`, lines 674-712) repeatedly hashes the same `(int, Texture, uint)` tuple: one `ContainsKey` plus as many as four indexer reads on a hit. Replace with one `TryGetValue` and a local `TileMaterial`.
3. `PPURenderer` repeats `ContainsKey` + indexer in widened per-tile loops: `matDict` around lines 3515/3517 and 3568/3570, `ProcessMaterial` around line 3602, and `tileAddrToMat` around 2771/2773 and 3639/3641. `usedMaterials.Contains(tuple2)` before `HashSet.Add` around 3634 is redundant because `Add` already reports whether an item was new.
4. `MasterExecutor.Update` calls `GenerateBackgrounds()` every Unity frame while paused (around line 1362), rebuilding a static PPU image. A renderer-dirty/last-emulated-frame early-out is a high-value paused/debug-session optimization.
5. The Mode 7 dictionaries have similar double-lookup patterns around `PPURenderer.cs` lines 2085 and 2192. These are lower priority for DKC but belong in the later general-emulator pass.

Implementation order: measure the existing stack; finish low-overhead debug-hook behavior and rewind/history control; benchmark the `ReadMem` IL fast path in isolation; then material-cache rewrites; then paused-render early-out; combine only individually validated changes. The on-disk `Assembly-CSharp.dll` must remain byte-for-byte pristine.

### `ReadMem` IL fast-path benchmark: rejected

Implementation tested: a Harmony transpiler that rewrites the cheat path once at load time, adding an empty-dictionary `Count` guard and replacing `ContainsKey` + indexer with one `TryGetValue`. The on-disk assembly remained unchanged and cheat support was preserved.

Protocol: clean Jungle checkpoint, 600 warmup frames, followed by three consecutive exact 600-frame samples in one process for each configuration. A reversed-order control was used after a single-sample warmup outlier showed that one run was insufficient.

Results:

- Stock wall time: 10.241 / 10.511 / 10.810 seconds; median 10.511 seconds.
- Fast-path wall time: 10.808 / 10.853 / 10.148 seconds; median 10.808 seconds, 2.8% slower.
- Stock CPU/frame median: 50.938 ms.
- Fast-path CPU/frame median: 55.182 ms, 8.3% worse.

Decision: reject and leave `ReadMemCheatFastPath=false` in the installed config. The source remains available as a documented experiment, but it is not enabled in the playable setup. The likely explanation is that the empty `Dictionary.ContainsKey` path is already cheap on this runtime while the added `Count`, local handling, and altered JIT shape cost more than they save. Do not re-enable without a new benchmark on a materially different runtime/build.

### 2026-08-11 renderer regression and rollback

Observed symptom: after the banana investigation and performance experiments, the user reported that widescreen rendering was broken again. No banana-coordinate or OAM patch had been accepted, so the live ROM, widescreen support DLL, and BepInEx configuration were audited before changing game code.

Evidence:

- Canonical ROM SHA-256 remained `D593DA7A07FE52192F67D51EECC33EF4BD5C020516EA42341BBA210BD9CB72BE`.
- Installed DKC widescreen support DLL SHA-256 remained `B8212A91671084050BB75C78A87919D197D1979810071D5A2D84936E6717A13B`.
- Widescreen configuration still selected the 358x224 ROM with seven extra background/object/color tiles.
- `Assembly-CSharp.dll` was not modified on disk.
- The temporary `SuperZSNESCoreOptimizations` setting `TileMaterialCacheFastPath` was still `true` after its benchmark. This was the only newly active renderer-path transpiler and had not passed screenshot/hash visual QA.

Action and result:

- Reverted `<superzsnes>\BepInEx\config\dev.local.superzsnes.coreoptimizations.cfg` to `ReadMemCheatFastPath=false` and `TileMaterialCacheFastPath=false`.
- Relaunched the canonical ROM, loaded `DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2`, stepped one frame, resumed, and inspected the entire primary-monitor output through WSLSnapit.
- The 358-pixel-wide Jungle frame rendered continuously across the right extension again. This isolates the regression to the temporary renderer optimization environment; it was not a ROM/background-streaming or banana-coordinate change.

The tile-material rewrite also failed its performance gate: after a 600-frame warmup, three exact 600-frame samples produced a stock median of 10.511 seconds / 50.938 ms CPU per frame versus 10.584 seconds / 54.245 ms CPU per frame with the rewrite (0.7% slower wall time and 6.5% worse CPU/frame). Decision: permanently reject this implementation and keep it disabled. Any future renderer transpiler must pass both deterministic timing and full-monitor visual comparison before being left active.

## Installed debugging/automation tools

### DKC Level Automation

- Source: `<superzsnes-source>\Mods\DKCLevelAutomation`
- Installed: `<superzsnes>\BepInEx\plugins\DKCLevelAutomation`
- Authenticated loopback bridge, deterministic controller schedules, exact paused frame stepping, WRAM reads/writes and waits, ROM/state loading, cancellation/status, JSON recipes, and regression runner.
- Regression recipes include fresh entry, horizontal right/left movement, vertical jump/Y-boundary coverage, `cave-banana-position-and-pickup.json` for the exact corrected cave pickup transition, and `cave-exit-right-y.json` for the complete door-trigger/fade/outdoor-load sequence.

### DKC Tilemap Inspector

- Source: `<superzsnes-source>\Mods\DKCTilemapInspector`
- Installed: `<superzsnes>\BepInEx\plugins\DKCTilemapInspector`
- Exports VRAM/CGRAM/IO, BG column CSVs, raw reconstructed backgrounds, annotated viewport/seam images, and tilemap overviews.

### DKC Tile Stream Tracer

- Source: `<superzsnes-source>\Mods\DKCTileStreamTracer`
- Installed: `<superzsnes>\BepInEx\plugins\DKCTileStreamTracer`
- Traces targeted streaming PCs and PPU/DMA writes with register/WRAM/VRAM context.
- Current known issue: disarmed hot-path Harmony hooks impose avoidable overhead; fix is active.

### DKC Widescreen Debugger/MCP

- Source: `<superzsnes-source>\Mods\DKCWidescreenDebugger`
- Installed: `<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger`
- Provides capture, CPU/PPU/memory inspection, controller injection, local authenticated bridge, and MCP server.

## Test and screenshot rules

- For final user-visible validation, use WSLSnapit against `processName="SUPERZSNES"` and verify that the returned image contains the complete 1920x1200 game window. This avoids unrelated windows covering a primary-monitor capture. If `PrintWindow` returns black, fall back to the full primary monitor.
- Internal `frame-main.png`, composed render targets, and tilemap reconstructions are diagnostic evidence only; they can omit final scaling/cropping. Use them for exact 358x224/OAM comparisons, not as the sole proof of what the user sees.
- Before launch, check for an existing exact SuperZSNES process and avoid duplicate instances.
- Standard deterministic launch sequence:
  1. Start `SUPERZSNES.exe` with the canonical ROM only if no instance exists.
  2. Use Level Automation to pause, load the clean checkpoint, step exactly one frame, clear schedules, and resume.
  3. Focus the existing process without sending Escape or opening the emulator menu.
  4. Capture the primary monitor and confirm Jungle Hijinxs, a continuous right edge, and `paused=false`.

## Known controls

- Movement: W/A/S/D
- A/B/X/Y: I/K/U/J
- Start: H
- Select: G
- L/R: O/L

## Documentation discipline

For every future change, append a dated entry containing:

- The observed symptom and exact reproduction.
- The hypothesis and evidence used to accept or reject it.
- Files and code locations changed.
- Build commands and resulting hashes.
- Runtime configuration used.
- Automated tests and full-screen visual checks.
- Remaining risks or untested cases.

Do not silently replace this log with a summary. Append or revise the relevant section and preserve superseded conclusions with an explicit note explaining what replaced them.

## 2026-08-11 banana OAM X-high regression investigation

Observed symptom at debugger capture `capture-f00065416-20260811-130945-327`: two bananas appeared above Kong on the far left but could not be collected there, while the expected right-side bananas and their pickup logic remained present.

Raw OAM proves this was one five-member formation, not duplicate enumeration. Its intended screen-X chain was `192, 216, 240, 264, 288`; the emitted chain was `192, 216, 240, 8, 32`. The last two entries retained their correct collision coordinates at 264/288 but lost the OAM X-high bit, wrapping their graphics to 8/32. The stock high-table packer at `$B8BA90` and `$B8BAF3` uses `XBA; ASL` and was written for stock clipping, where positive banana X never exceeds 255.

The first candidate correction mirrored coordinate bit 8 into bit 15 inside `DKC1_Wide_AdjustBananaScreenX` before returning to the stock packer. Source: `DKC1_Disassembly\DKC1\Custom\Patches\Widescreen_358x224.asm`. Build command: `DKC1_Disassembly\build-widescreen.ps1`. Candidate ROM SHA-256: `700FD1AF5C0DAC48C12231CF0932F7FF038228421E81F758CF050CC9DD643D50`; prior canonical backup: `DKC_Widescreen_358x224.pre-banana-oam-xhigh-fix.sfc`, SHA-256 `2EF5CADDDF801197542821AAD03AE8EBDB0F011E863CF307C24EE7663ABA0CD2`.

Do not mark that first encoding attempt final: after loading `DKC_Widescreen_358x224.szst0.pre-cache-guard-20260811-130403` and advancing a frame, capture `capture-f00058438-20260811-131800-383` still reported `192, 216, 240, 8, 32` and upper-OAM pairs of 2 rather than 3 for the last two entries. The user reported that the live presentation looked correct, but the internal OAM failure remains authoritative for this specific reproduction until a subsequent frame/path trace explains the discrepancy. Save-state backups made before the controlled restart are `*.pre-banana-xhigh-fix-20260811-131506`.

## 2026-08-11 frame-drop root cause and active runtime fixes

The timing probe isolated burst drops from normal frame cost. In the decisive stock burst, SuperZSNES paused process-wide for 1.045 seconds while every actual `RunFrame` remained below 4.3 ms; the longest audio-lock wait was only 0.663 ms. The stock accumulator then scheduled at most five frames but subtracted every frame that had become due, permanently discarding backlog. This rejected the audio-lock hypothesis as the primary cause of the large bursts.

`SuperZSNESFramePacingFix` v0.2.0 now rewrites only the normal-speed accumulator debit in memory: it subtracts `min(due, 5)`, retains positive backlog for later Updates, clamps negative residue to zero, and applies a configurable emergency ceiling after the current batch. Fast-forward caps 1-4 remain bit-identical to stock. Installed DLL SHA-256: `F31E972EB04DDAAB3FEA6ACF2D55D1C69DC52A2ECED22AC90105281E86FA12FB`; active config is `Enabled=true`, `EmergencyMaxBacklogFrames=120`. The on-disk `Assembly-CSharp.dll` remains pristine.

The process-wide pauses correlated with an independent renderer retention bug: `PPURenderer.tileAddrToMat` accumulated scratch dictionary keys/lists across backgrounds. A baseline churn test grew from 253 to 210,853 entries in about 35 seconds and retained more than 1.2 million list slots. A periodic whole-map clear was rejected because it introduced deterministic 100-195 ms stalls every five seconds.

`SuperZSNESMaterialCacheGuard` v0.2.0 instead clears the scratch map at each individual background boundary, returns its `List<TileInfo>` values to a type-safe pool, and transpiles the sole list-construction site to rent from that pool. Installed/source DLL SHA-256: `9257B01B1514B38A5A6BAAACBB0F34D07BF2D53C29B8B2937CC902179123B450`. Runtime startup verified one exact `ProcessMaterial` transform and enabled the per-background pool. In the current churn/play session, live scratch keys stayed between 0 and 42, allocations reached a high-water mark of 1,308 and then stopped despite more than nine million rentals, and managed memory cycled around 91-99 MB instead of growing without bound.

An uninterrupted post-fix gameplay interval from 17:19:00Z through 17:20:05Z measured 4,203 frames in 70.05 seconds (60.004 FPS), zero frame-start gaps over 25 ms, no due-over-cap events, a 22.89 ms worst frame-start gap, 5.034 ms worst `RunFrame`, 11.617 ms worst host `Update`, and 2.283 ms worst audio-lock wait. Longer gameplay observation remains active before final acceptance. Deliberate pause/load/debug windows earlier in the same process must not be mixed into this interval.

## 2026-08-11 banana X-high conclusion (supersedes the first one-frame result)

The earlier `capture-f00058438` PPU-only conclusion was one VBlank too early. In that frame the WRAM OAM shadow at `$7E0200/$7E0400` had already encoded the intended five-banana chain as X `192,216,240,264,288`, including X-high pairs `3,3,2,2,2`; the PPU OAM snapshot still contained the preceding frame's upper table. After three complete frames, both WRAM shadow and PPU OAM agreed on the correct chain. Validation capture: `<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\Sessions\20260811-133341\capture-f00069539-20260811-133642-968`.

The accepted implementation remains the widescreen helper's bit-8-to-bit-15 mirror before the stock `XBA; ASL` OAM-high packer. Do **not** patch `$B8BA91/$B8BAF4` from `ASL` to `LSR`; both opcodes remain stock byte `$0A`. This preserves the correct formation/pickup coordinates and prevents positive X positions 256-383 from wrapping visually to 0-127.

## 2026-08-11 Barrel Cannon Canyon initializer fix

Observed symptom: a newly saved Barrel Cannon Canyon position showed a 24-pixel vertical strip of corrupt BG1 art around screen X 56-72. The bad state was preserved as `DKC_Widescreen_358x224.szst0.render-issue-20260811-132348`.

Root cause: final Layer1 X was `$8161`. The wide initializer backstepped by `$0178`, so its temporary sequence began `$7FE9,$7FF1,$7FF9,$8001...`. `DKC1_Wide_GetStreamX` used `BPL` as the initializer discriminator; the first three positive temporary positions incorrectly took the normal `+$0138` path and seeded `$8121,$8129,$8131` instead of `$8161,$8169,$8171`. That is exactly three 8-pixel columns and exactly the observed corrupted viewport positions.

Accepted source fix in `DKC1_Disassembly\DKC1\Custom\Patches\Widescreen_358x224.asm`: under `$0A75==$0008`, compare temporary Layer1 X as an unsigned value against current upper bound `$1B25`, and require it to differ from final target `$1A5E`, before applying `+$0178`. Normal high-world coordinates such as `$8161 < $8C07` now take the standard wide stream path; the special initializer's temporary values remain above its forced `$06C8` upper bound. The signed `BPL` test was removed.

Build completed with `DKC1_Disassembly\build-widescreen.ps1`. Current ROM SHA-256: `EA8BCBC46F5F7E36CE4575636AC767A4C4ABF3529A2268BFA6E83F2CC24E2FF6`. Pre-fix backup: `DKC_Widescreen_358x224.pre-bcc-init-fix-20260811-1328.sfc`, SHA-256 `700FD1AF5C0DAC48C12231CF0932F7FF038228421E81F758CF050CC9DD643D50`.

Important validation limitation: a save state contains its already-corrupt VRAM, so loading the old reproduction cannot prove the initializer repair visually. Validation requires a fresh level initialization/re-entry with the new ROM. The address arithmetic and affected columns are exact, but do not claim the stale save-state image as fresh visual proof.

## 2026-08-11 presentation-cadence diagnosis and optimization status

Clean Jungle evidence separated emulation throughput from visible presentation:

- 3,908 emulated frames / 65.134 seconds = 59.999 Hz.
- 3,536 Unity `Update` calls in the same interval = 54.288 Hz.
- `MasterExecutor.Update` averaged 14.244 ms while `RunFrame` averaged only 2.456 ms.
- `GenerateBackgrounds` accounted for roughly 11.8-12.1 ms per composite, with three active DKC backgrounds at about 3.45-3.52 ms each.
- Texture generation/upload, palette work, and material expiry were each only hundredths of a millisecond per composite.
- GC, audio-lock contention, rewind/history, and the SNES CPU core do not explain the sustained under-60 presentation cadence.

Because `GenerateBackgrounds` runs once after a multi-frame emulation batch, any Unity cadence below 60 necessarily hides intermediate SNES frames. The accumulator fix prevents permanent backlog loss but cannot create missing presentation opportunities.

Active runtime changes:

- `SuperZSNESPerformanceGuard` v0.2.0 disables rewind/history, forces VSync off, requests a high presentation ceiling, and keeps the four internal PPU render textures at SuperZSNES's supported 796x448 size instead of 1592x896. This reduced working memory by roughly 110 MiB in later sessions while preserving the final scaled image.
- `SuperZSNESRendererFastPaths` v0.1.0 rewrites two `DrawLines` material-cache reads to `TryGetValue` and removes a redundant `HashSet.Contains`. In a 120.174-second controlled run it maintained 59.996 emulated Hz, raised Unity cadence to 57.650 Hz, reduced two-frame batches to 287/6,928 updates (4.146%), and recorded zero scheduler drops or >33.3 ms frame-start gaps. This is an improvement, not yet a complete presentation fix.
- `SuperZSNESAudioTimingProbe` and material-cache diagnostics are disabled in the playable configuration. The audio probe performed roughly 288,000 atomic increments per second and is unsuitable as a permanent monitor.
- `SuperZSNESCadenceCounter` is the replacement diagnostic: ordinary main-thread counters only, no audio hooks, atomics, locks, or worker thread.

Rejected experiments:

- A dirty-texture upload gate reduced cadence; `TileTextureGen.GenerateTextures` was already negligible. It remains disabled.
- `TargetPresentationRate=60` reduced host cadence to about 53.7 Hz and was rejected. Higher ceilings provide scheduling headroom but do not by themselves remove the remaining renderer cost.
- `SuperZSNESBackgroundCallGuards` was enabled once with both its empty-clear-loop and `Process2DTiles` dispatch guards. The user immediately observed severe visual corruption. Both settings were disabled, the emulator was restarted, and full-window capture returned to coherent widescreen output. Runtime status now reports `state=disabled`, zero transforms. Treat the plugin as failed visual QA until its branch/control-flow error is isolated; do not re-enable both switches together.

Current next target: avoid the duplicate `DrawLines -> ProcessMaterial` cache path on cache hits. The measured scene made about 7,520 `ProcessMaterial` calls per composite, and roughly 70% returned immediately from an existing `matDict` entry before the caller performed its own second lookup. Any implementation must verify both normal and hi-res paths, null material entries, Harmony composition order, exact v0.230 IL, and full-window video before acceptance.

## 2026-08-11 isolated renderer A/B continuation

All measurements below used the same clean Jungle state, a foreground SuperZSNES window, seven consecutive five-second `SuperZSNESCadenceCounter` windows, VSync off, target presentation rate 120, and 59.99-60.02 emulated frames/second. Full-window WSLSnapit capture was required before each timing run. The on-disk game assembly remained unchanged.

Accepted so far:

- `SuperZSNESMeshBoundsOptimization` v0.1.0 replaces each 2D tile mesh's vertex-scanning `RecalculateBounds()` with a conservative fixed local-space bound while preserving vertex/UV uploads. DLL SHA-256: `98B54EE5AAAF14D401AC83DD3690EF9C58381EB50B832ECA4020DD4FC3D9A417`. It passed full-window visual QA and improved presentation from 54.718 to 56.170 updates/second (+2.65%); two-frame batches fell from 169 to 111 over about 35 seconds. It remains enabled for continued testing.

Measured but rejected:

- `SuperZSNESDrawLinesCacheGate` v0.1.0 was visually correct and removed the duplicate `ProcessMaterial` call on cache hits, but the focused run remained at 54.718 updates/second with 169 two-frame batches. It did not solve or measurably improve pacing and is disabled. DLL SHA-256: `70BCC0CBD6AF06DED555E9354046A1EA7D0D4A0924979C817A62AC0317698A17`.
- `SuperZSNESTileMeshStateGuards` v0.1.0 guarded six unconditional Unity setters. It was visually correct but regressed the bounds-enabled result to 54.977 updates/second and 162 two-frame batches. Getter/comparison overhead exceeded the avoided setters. It is disabled. DLL SHA-256: `A77344BA433ED33DAC3ABDE8ECD857A7213277DB17A687235D0C734BB29760D2`.
- `SuperZSNESPerformanceGuard` v0.3.0 added a reversible native-size 398x224 render-target option. At 1x the full image was correct but softer, and cadence was statistically unchanged at 56.227 updates/second with 114 two-frame batches. This confirms that current pacing is CPU-side, not render-target fill-rate bound. Runtime configuration was restored to the sharper 796x448 (`PpuRenderTextureScale=2`). Installed DLL SHA-256: `AEDC3CBC074DD1DB3E01E68A325F3CDE5C6E18618B7A953259AD450972EF187F`.
- `SuperZSNESMeshDynamicUploadOptimization` v0.1.0 moved the existing `MarkDynamic()` before initial buffer creation. It was visually correct but severely regressed cadence to 48.193 updates/second and 384 two-frame batches, so it is disabled. DLL SHA-256: `DC6944457E03B424D472B66FE7C6DAD3B883028218C029D80DE3CE5BAD8D297B`.

The earlier `SuperZSNESBackgroundCallGuards` visual failure is now hard-quarantined in v0.1.1: the plugin applies no PPURenderer patch when both experimental switches are true. Installed DLL SHA-256: `14576CF7CBDE7EEED80A03EBD8F9C4331572C56E030E2E409F8AE843BC545F98`; both switches remain false.

Bridge handle-leak fixes were installed independently from rendering: DKC Level Automation v0.1.2 (`FC33824A...`), DKC Widescreen Debugger v0.1.2 (`068AA86E...`), and DKC Tilemap Inspector v0.1.1 (`3B79800F...`). Each replaces per-client thread/wait-handle leakage with bounded thread-pool/monitor coordination and passed its standalone connection stress test.

Remaining performance direction: reduce the number of non-empty 2D mesh submissions. A captured background contained 91 material lists and 1,375 tiles; stock power-of-four decomposition emitted up to 239 meshes. Exact variable-count batching could approach one mesh per material list and remove most Unity upload/property-block submissions, but it must preserve material order, index bounds, mesh-pool ownership across BG layers, and visual output before acceptance.

## 2026-08-11 rejected variable batching and platform experiments

`SuperZSNESVariableMaterialBatching` v0.1.0 attempted to collapse each opaque material list into one resized pooled mesh while leaving transparent lists on the stock power-of-four path. DLL SHA-256: `CF7E055A2BBCEE862C6C1643F4476E921A0C1E7FDED7B9351F0360AA0E214FCF`. It failed the required first full-window visual check immediately: the game showed only part of the sky/background and a large black lower region, with foreground and sprites missing. No timing result was accepted. The setting was disabled and a clean D3D11 restart restored the full image.

Offline verification found no wrong Harmony argument index, stack/local mismatch, tile-order/topology error, missing Mode 7 pool repair, or IL-level conflict with the fixed-bounds patch. The failed runtime log/status was overwritten on restart, so the remaining causes cannot be separated safely between reshaping an already-bound pooled Unity mesh and treating material render type/queue as sufficient proof that renderer boundaries may be collapsed. The experiment is retired rather than guessed at. Offline v0.1.1, SHA-256 `587F5427E27EEAEDC756BD6090472C5B23529C82AB24EDC6DE6E39CABEAB586C`, is a fail-closed quarantine that applies no Harmony patch even if `Enabled=true`; the runtime config remains false.

Setting the SuperZSNES process priority to High did not improve cadence and produced a noisier, worse sample. Process priority was restored to Normal; no persistent priority change remains.

Forcing Unity's D3D12 backend initialized successfully but crashed while loading the save state. The crash report is under `<user-home>\AppData\Local\Temp\ZEMU Software Inc_\SUPERZSNES\Crashes`. D3D12 is rejected for this build. The emulator was restarted on its default D3D11 backend, the canonical clean Jungle state was loaded, and a full-process WSLSnapit capture verified a complete, continuous 358-pixel scene with foreground, background, and sprites.

Two real-time right+Y scrolling harness runs are not valid renderer comparisons because the character reached the level map/end state during the measurement interval. Their manifests are `<workspace>\RealtimeScrollRuns\20260811-143055-bounds-only\manifest.json` and `<workspace>\RealtimeScrollRuns\20260811-143201-bounds-first20\manifest.json`. Do not cite their mixed-scene cadence as an optimization result. The seven-window stationary Jungle measurements remain the controlled evidence until a shorter or oscillating in-level recipe is used.

## 2026-08-11 heavy-scene cadence and 240 Hz rejection

Content-dependent measurements explain why some levels look smooth and others look jerky. The SNES core remained approximately 60.0 frames/second throughout, but five-second Unity presentation windows varied from about 45.6 to 70.2 updates/second as scene cost changed. A heavy window at 45.6 updates/second executed 73 two-frame batches in only 228 Unity updates; smoother windows at or above 60 had few or no two-frame batches. Because `GenerateBackgrounds` runs once after the emulation batch, each two-frame batch hides its intermediate SNES frame. The remaining symptom is therefore presentation judder caused by content-dependent renderer cost, not insufficient emulation throughput.

Raising `TargetPresentationRate` from 120 to 240 was tested as a reversible scheduling experiment. It clearly regressed the same clean-state scene: Unity fell as low as 35.4 updates/second, average `Update` time reached about 22 ms, and two-frame batches rose as high as 118 in one five-second window. The higher empty-update pressure competes with rendering on this build. The setting was restored to 120, D3D11 was restarted, the clean state was reloaded, and full-window visual output passed again.

The reported `RenderLines` sprite-loop off-by-one is real in installed v0.230: the terminal `i <= 128` loop visits 129 descriptors and processes the priority-rotation starting sprite twice. Offline `SuperZSNESRenderLinesLoopFix` v0.1.0 changes only the loop bound constant `128 -> 127`; exact IL and all-rotation semantic tests pass. DLL SHA-256: `68817117D8D319C3244D7D2E2D65FFFE70820E0FA7FCDE89218856254A91DB85`. Expected whole-frame benefit is well below 1%, so it is a correctness cleanup rather than the primary cadence fix and remains uninstalled pending isolated visual QA.

A 90 Hz presentation-ceiling experiment was also rejected. On the same clean state its seven five-second windows had a median of about 55.1 Unity updates/second, including windows with 23-69 two-frame batches. This was worse than the corresponding 120 Hz samples. Runtime configuration was restored to `TargetPresentationRate=120`, process priority Normal, VSync off, and 2x PPU surfaces.

The remaining user-supplied findings were audited against actual v0.230 code and existing measurements. `historyDisabled=true` already removes the synchronous 20-second screenshot/PNG path. The proposed tile-atlas dirty gate had already been A/B tested and rejected: `GenerateTextures` measured only about 0.007-0.008 ms per composite and moving the dirty flag regressed cadence. `ClearCache` forced collection/unload is reached by resize, ROM/reset/load, mod refresh, and UI transitions rather than the ordinary fixed-resolution frame loop. CPU/SPC `usedPC` growth is debug-branch guarded. The callback-thread audio ratio clamp is not a host-cadence limiter; prior probe data measured about 204 microseconds average callback duration and at most 1.558 ms main-thread lock wait.

Palette churn remains credible specifically for fade/transition bursts. A recorded cache count peaked at 1,342 textures and then drained roughly one stale entry per rendered frame; stable Jungle later held exactly 26 textures for 125 seconds. A disabled diagnostic-only `SuperZSNESPaletteCacheProbe` v0.1.0 was built, not installed, to capture count/create/evict behavior around a reported transition. DLL SHA-256: `8C61A4476A50016FC6C8DFC53D3C212E83706F80E38D7D8157987DF676E89D85`. This does not explain sustained sub-60 presentation on heavy level scenes.

## 2026-08-11 exact DKC whole-background cache experiment

`SuperZSNESDKCBackgroundStateCache` v0.1.0 was installed as a DKC-only, all-or-none background-layer cache. DLL SHA-256: `3DE3AEDE3771F05ECD507BDA3D1C1966200F774521D943100025D63C8EFD0BE7`. `GenerateBackgrounds` itself always runs, so sprites/OAM, windows, composition, fixed color, mosaic and texture lifecycle continue normally. A single exact decision applies to every active non-Mode7 `GenerateBackground` call; individual layers are never skipped because their mesh pools are shared.

The key performs exact comparisons over full VRAM, current/start CGRAM, PPU registers and active scanline changes, start scrolls/latches, dirty/decode arrays, viewport/disable flags, game width/aspect/enhancement settings, and deep scene configuration. Mode 7, dynamic fonts, replacement materials, malformed state, renderer resets, scene changes and save-state restoration fail closed.

Dry-run mode predicted 147 safe hits in its first 600 frames (24.5% overall) without changing rendering. Active mode then passed the initial full-window check and a 120-frame real-time right+Y scrolling check: the viewport remained coherent with active sprites, impact effect, barrel and both widescreen edges. A later black capture after a 300-frame forced run was confirmed to be the game's death/fade state rather than cache corruption; reloading and stopping before the death produced a valid image. Runtime currently uses `Enabled=true`, `DryRun=false` while further gameplay validation continues.

The current key is deliberately conservative and initially accumulated many `scanline-change-stream` misses. A v0.1.1 follow-up is being audited to compare only the exact BG/window/color scanline registers consumed by the skipped layer builders while keeping any Mode 7 activation as a hard miss. Paused/debug periods inflate the raw hit rate because stock regenerates backgrounds on every paused Unity update; gameplay-only cadence must be judged separately.

With the cache active in a stable gameplay window, presentation reached 97.697 Unity updates/second at 59.937 emulated frames/second with zero two-frame batches. After the separate sprite-loop correction below, another stable window reached 114.395 updates/second at 60.198 emulated frames/second; only four two-frame batches occurred in 572 Unity updates. These high-cadence windows demonstrate that avoiding redundant layer rebuilds restores one-presentation-per-SNES-frame behavior when the exact key hits. They do not yet prove the same gain during continuous camera scrolling, where scroll changes correctly force misses.

`SuperZSNESRenderLinesLoopFix` v0.1.0 was installed and enabled after its exact-IL verification. It changes only the terminal inclusive OAM loop bound from 128 to 127, eliminating the duplicate priority-rotation starting sprite while retaining all 128 entries in identical order. Installed/source DLL SHA-256: `68817117D8D319C3244D7D2E2D65FFFE70820E0FA7FCDE89218856254A91DB85`. Startup logged one successful in-memory transform; the game assembly remains unchanged. It passed a clean-state full-window sprite check and the same 120-frame right+Y movement/impact/barrel check used for cache QA. Expected performance benefit remains below 1%; it is retained primarily as a verified correctness cleanup.

The cache was upgraded to v0.1.1, installed/source SHA-256 `8D5654C6D9FA718F2B9A358B3AA9F609C9A2592FBBF6A58DFC56013AA74D1F6F`. Its scanline key retains exact order/line/address/value for the 33 register cases proven from `GenerateBackground` IL plus `$212C/$212D`, ignores OAM/OBJ/port records not read by the skipped builders, and still scans the raw stream to reject Mode 7. Dry-run remained conservative at 136 predicted hits in 1,200 frames because real PPU-start, CGRAM, dirty, VRAM and relevant scanline changes remained. Active mode passed the clean and 120-frame movement visual regressions. A settled window reached 119.009 Unity updates/second at 59.904 emulated frames/second with zero two-frame batches.

An aligned five-second `RIGHT+Y -> LEFT+Y -> RIGHT+Y` sample exposed the remaining limitation: continuous movement measured 55.581 Unity updates/second at 59.980 emulated frames/second, including 32 two-frame batches. Exact-state caching intentionally misses when base BG scroll changes. The next prototype therefore targets only sub-tile scroll changes inside the same 8-pixel bucket: retain existing geometry, update each active background mesh's `_TileScroll` property, and force stock regeneration on tile-boundary, raster-scroll, VRAM/CGRAM/register, Mode 7, enhancement or malformed-state changes.

Reducing the renderer's widescreen margin from seven to six tiles was audited and rejected without a live negative test. `DrawLines` uses 33+2w raw columns but clamps away its scrolling guard column: w=7 provides 368 visible pixels (five guard pixels per side for the 358 target), whereas w=6 provides only 352 and would leave a three-pixel deficit per side before bilinear sampling. OBJ uses the same 368 vs 352 clamp width. Keep both BG and OBJ margins at seven; the extra column is required guard coverage, not removable overdraw.

## 2026-08-11 60 Hz display, uncapped ceiling, and CPU-limit confirmation

The user changed the active display to 60 Hz. Win32 monitor enumeration confirmed that the SuperZSNES window was on the 3840x2400@60 display (reported to desktop applications as 2560x1600 because of 150% scaling). Native Unity VSync was tested directly and rejected: `vSyncCount=1`, `targetFrameRate=-1` produced only 39.470 Unity updates/second while the emulator advanced at 67.379 frames/second, with 93 two-frame and 15 four-frame batches in one five-second window. This Unity player does not pace correctly from the monitor's native VSync.

`SuperZSNESPerformanceGuard` v0.4.0 added a reversible `UncappedPresentation` option. With VSync still off and `Application.targetFrameRate=-1`, light/cache-hit windows reached about 146 updates/second and an initial partial window reached about 185. The comparable heavy moving window reached only 48.873 updates/second at 60.044 emulated frames/second with 70 two-frame batches. This was worse than the 120-ceiling control and was rejected. Active settings are `LimitPresentationRate=true`, `UncappedPresentation=false`, `TargetPresentationRate=120`, `vSyncCount=0`.

This establishes the meaning of “CPU bound” for DKC: the whole PC is not saturated. DKC's mid-screen palette/register changes fragment its backgrounds into many material/mesh batches that SuperZSNES constructs and submits largely through a serial Unity main-thread path. Light scenes can present far above 60, while palette/scroll-heavy scenes miss the 16.67 ms presentation deadline even on this CPU. The user's quoted SuperZSNES developer explanation is consistent with the measured content dependence.

## 2026-08-11 rejected scroll and mesh micro-optimizations

The proposed exact-cache relaxation for scroll changes inside one 8-pixel bucket was blocked before implementation. `DrawLines` changes mesh X positions from `-(scrollX&7)/8`, changes both clipped edge tiles and their UVs, and introduces/removes the far-edge tile as the phase changes. Vertical low-bit scrolling also changes tile-row selection and scanline segmentation. `_TileScroll` updates or a uniform transform cannot reproduce the stock result; a correct implementation requires guard-tile generation plus explicit edge mesh/material management. Accepted `SuperZSNESDKCBackgroundStateCache` remains v0.1.1 exact-state only.

Temporary hot instrumentation of the moving path measured `GenerateBackgrounds` at 20.561 ms/composite with probe overhead. Its nested totals concentrated in `DrawLines`, `ProcessMaterial`, and about 7,600 `Process2DTiles` dispatches per composite; texture generation, palette calculation itself, and cache expiry remained negligible. The probe was disabled immediately afterward.

The following isolated experiments were visually checked and rejected:

- `OptimizeNoOpProcess2DTilesCalls=true` alone reproduced the severe missing-geometry frame (sky bands plus black lower screen). This proves the Process2DTiles branch rewrite itself—not its former combination with the empty-map guard—causes the corruption. Both BackgroundCallGuards switches remain false.
- Mesh notification batching (`DontNotifyMeshUsers` for vertices+UVs followed by one `MarkModified`) was visually correct but reduced the comparable moving window to 49.732 updates/second with 60 two-frame batches. `SuperZSNESMeshBoundsOptimization` v0.2.0 remains installed with `BatchMeshNotifications=false`; its accepted fixed-bounds behavior is unchanged.
- Launching with Unity `-job-worker-count 4` did not survive an aligned moving test: 48.871 updates/second with 74 multi-frame batches. The emulator is again launched without a worker-count override.
- Replacing only `MeshFilter.mesh` assignment with `MeshFilter.sharedMesh` was visually correct but the aligned moving window fell to 46.561 updates/second with 82 multi-frame batches. `SuperZSNESTileMeshStateGuards` v0.2.0 remains installed with both `Enabled=false` and `UseSharedMeshSetter=false`.

After every rejected test the emulator was cleanly restarted on default D3D11, the canonical Jungle state was reloaded, schedules were cleared, gameplay was resumed, and a full-process WSLSnapit capture confirmed the complete 358x224 image. Current playable PID at handoff was 4560, process priority Normal, paused=false.

### 2026-08-11 — VSync-off ceiling and final graphics-backend check

- `-force-gfx-direct` was visually coherent but regressed the aligned moving Jungle window to **44.375 Unity Updates/s** at **56.968 emulated frames/s**, with **91 two-frame batches and 1 four-frame batch**. Rejected.
- Restored the default D3D11/Unity graphics-job launch with `vSyncCount=0`, `Application.targetFrameRate=120`, and the accepted 2x PPU render surfaces.
- Fully uncapped (`vSyncCount=0`, `targetFrameRate=-1`) measured approximately **146 Updates/s** in sustained light/cache-hit windows (with a partial startup window around **185 Updates/s**), but only **48.873 Updates/s** in the aligned heavy moving Jungle window while emulation remained about **60.044 frames/s**. This proves the ceiling is strongly scene-dependent.
- Native 60 Hz VSync was worse: **39.470 Updates/s**, about **67.379 emulated frames/s**, with severe multi-frame batching. It was rejected.
- Interpretation: DKC is CPU-bound in the serial Unity renderer/material/mesh-submission path. Mid-screen palette/register changes split a frame into many render batches. The whole CPU need not be saturated; one critical main thread exceeding 16.67 ms is sufficient to miss 60 Hz presentation.
- Final runtime restored to the best safe configuration and relaunched as PID 47220 with the clean Jungle checkpoint, normal execution, and no controller schedule. This entry supersedes the earlier PID 4560 handoff note.

## 2026-08-11 DKC framebuffer renderer: confirmed presentation fix

The stock renderer's cadence problem was measured directly at the user's current scene. With VSync off and Unity's target rate at 120, the SNES core remained at 59.8-60.2 frames/second, but the visible Unity window produced only 43.8-46.6 updates/second. Typical five-second windows contained 80-90 updates that advanced two SNES frames and displayed only the second one. The user's description of the result as looking close to 30 FPS was therefore reasonable even though the literal presentation rate was about 45 FPS: skips were uneven and intermediate animation states were hidden.

`SuperZSNESDKCFramebufferRenderer` replaces the DKC Mode 1 Unity tile/material/mesh construction path with a plugin-owned 358x224 CPU framebuffer. It reconstructs per-line PPU/CGRAM state, three Mode 1 backgrounds, OAM, windows, priorities, brightness and color math, then uploads one persistent RGBA texture for final presentation. Unsupported frames fail closed to the stock renderer. The on-disk `Assembly-CSharp.dll` remains pristine at SHA-256 `33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED`.

Initial presentation v0.2 removed the sustained stock ceiling but still rebuilt BG1/BG2/BG3 serially. In the deterministic 30-second Right+Y run, five fully-contained measurement windows averaged 72.167 Unity updates/second and 10.582 ms/update, with 199 two-frame batches in 1,807 updates (11.013%). A BG2 or BG3 miss cost roughly 10 ms; simultaneous misses therefore produced 23-30 ms main-thread frames.

Accepted v0.3.0 prepares the three independent plugin-owned background planes concurrently and aggregates diagnostic counters only after the workers join. Project and installed DLL SHA-256: `D0136609E7A937564DC812FB0629213762598C68CFE439763F690AF848EF68D5`. Build: zero warnings/errors. The planar decode, tilemap addressing, color math, window, retained-cache, and exact v0.230 patch-target verifier all pass.

The same immutable ROM/state/macro comparison measured:

- v0.2: 72.167 Updates/s, 10.582 ms/update, 30.065 ms maximum, 60.026 emulated FPS, 199 two-frame batches/1,807 updates.
- v0.3: 96.351 Updates/s, 6.401 ms/update, 20.331 ms maximum, 59.985 emulated FPS, 1 two-frame batch/2,411 updates.
- Improvement: +33.51% Unity cadence, -39.51% average Update time, and multi-frame share reduced from 11.013% to 0.041%.

Evidence:

- v0.2 manifest: `<workspace>\performance-runs\20260811-161448-framebuffer-present-v020\manifest.json`
- v0.3 manifest: `<workspace>\performance-runs\20260811-161811-framebuffer-present-v030-parallel-bg\manifest.json`
- comparison: `<workspace>\performance-runs\framebuffer-v020-v030-comparison.json`

At exact save-state frame 3372, the pre-parallel v0.2 candidate and v0.3 candidate PNGs are byte-identical, both SHA-256 `593A41AE2E881A1EB47A44BD347DC8D009CC0CCA5E492CCB355AD759411EAB6C`. This proves the parallel change altered timing, not framebuffer pixels. The full-window v0.3 image is `<workspace>\framebuffer-v030-exact-frame3372.png`.

Additional no-input level-state checks all stayed above 60 presentation updates/second with zero renderer fallbacks and zero two-frame batches in their complete measurement windows:

- cave-exit state: 87.955 Updates/s at 59.969 emulated FPS;
- archived Barrel render-issue state: 116.134 Updates/s at 60.166 emulated FPS;
- latest user state: 117.363-118.282 Updates/s at 59.94-59.98 emulated FPS.

The cave and archived Barrel screenshots contain visible seams, but these are not framebuffer-renderer regressions. The archived legacy cave `frame-main.png` at frame 8695 contains the same duplicated sections and black vertical seams. The Barrel state is explicitly the old pre-fix `render-issue` save and preserves its corrupted VRAM. The latest user state is continuous. Keep these files as historical ROM-streaming regressions, not framebuffer presentation oracles.

Current playable configuration is `Renderer.Enabled=true`, `PresentFramebuffer=true`, `ShadowRenderInterval=0`, and `RetainedBackgrounds=true` in `<superzsnes>\BepInEx\config\dev.local.superzsnes.dkcframebuffer.cfg`. The source remains fail-closed/default-off for a fresh install. Remaining QA caveat: a full-window legacy/candidate comparison is visually close but not pixel-identical because the legacy Unity path adds its own camera/filter presentation and transient UI overlays. Unsupported PPU modes and effects intentionally use the legacy path.

## 2026-08-11 Barrel Cannon Canyon missing grouped barrel diagnosis

The user save `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst0` is Barrel Cannon Canyon level `$0006`, entrance/checkpoint `$0019`. Kong is in the bottom barrel at world `$8400,$0074`. The intended target is not the later far-right barrel at `$84C0,$0118`; it is the third child of `DATA_BD9C64`, the automatic barrel at `$842C,$0118`. The same authored group also contains a Zinger at `$842C,$00D0`.

The exact saved state proves an object-lifecycle failure rather than a framebuffer or OAM rendering failure:

- Type-5 group controller `$5D` is active.
- Child bookkeeping `$192B[$8A]=$18` points to the occupied bottom barrel.
- Child bookkeeping `$192B[$8B]=$00` and `$192B[$8C]=$00`; neither the Zinger nor target barrel exists in the normal-sprite table.
- The separate later group is active at `$84C0/$8560/$8600`; its far-right barrel was the small clipped sliver in the initial framebuffer capture and is not the object the user reported.

The relevant stock loader is `CODE_BDFB76`. It marks a type-5 parent active, walks its children once at `CODE_BDFBF5`, and calls `CODE_BDFC59` for each. If `CODE_BDF3F3` cannot obtain both a normal-sprite slot and an OAM allocation, that child remains zero. On every later scan, the nonzero parent flag takes the early `BNE CODE_BDFB72`; missing children are never retried. The widescreen patch expands the type-5 prefetch/retention tests at `$BDFB8F` (`+$0120 -> +$0158`) and `$BDFBAA` (`-$0020 -> -$0058`), increasing simultaneous grouped-object pressure on DKC's fixed pools. The ordered result here—first child present, next two absent—is the loader's partial-allocation signature.

This was verified reversibly. After loading the exact state, only controller `$5D` and its parent-active byte were cleared, then two frames were stepped. With free slots now available, the same group immediately produced:

- slot 9: Zinger ID `$19`, near `$842A,$00CE`, child map `$FF75` / index `$8B`;
- slot 10: barrel cannon ID `$38`, near `$8428,$0118`, child map `$FF74` / index `$8C`;
- the existing occupied barrel remained in slot 12, child map `$FF76` / index `$8A`.

Framebuffer evidence with the forced group retry is `<superzsnes>\BepInEx\plugins\SuperZSNESDKCFramebufferRenderer\candidate-20260811-214040-694.png`; it visibly contains the target barrel above Kong plus the Zinger. The original missing-object candidate is `candidate-20260811-213547-259.png`. The canonical save was reloaded afterward and the emulator was left paused at frame 195505; no ROM, patch source, or persistent game state was changed.

Recommended fix direction: retain the necessary wide activation margins but make active type-5 group controllers retry only child records whose `$192B` byte is still zero when allocation capacity becomes available. This is safer and more general than moving this authored barrel, increasing fixed array sizes, or narrowing all widescreen activation. A regression should load this state, allow the retry, assert IDs `$19/$38` with child indices `$8B/$8C`, and confirm the target barrel is visible and catchable before accepting the ROM build.

## 2026-08-11 Barrel Cannon Canyon grouped-child retry fix

The fix is implemented in `<workspace>\DKC1_Disassembly\DKC1\Custom\Patches\Widescreen_358x224.asm`. The wide patch now hooks type-5 controller entry `$BDFB76` with `JML DKC1_Wide_Type5GroupSpawn`; the helper assembled at `$CA6E07` preserves stock behavior in narrow rooms and preserves the original inactive-parent path. For an already-active wide controller, it reruns the stock child loop at `$BDFBF5`. The stock loop already skips children with nonzero `$192B` bookkeeping, so only previously missing children are retried; existing sprites are neither duplicated nor reset.

The rebuilt canonical ROM is `<workspace>\DKC_Widescreen_358x224.sfc`, SHA-256 `B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D`. The prior ROM is preserved as `<workspace>\DKC_Widescreen_358x224.pre-type5-child-retry-20260811.sfc` (SHA-256 `EA8BCBC46F5F7E36CE4575636AC767A4C4ABF3529A2268BFA6E83F2CC24E2FF6`). ROM bytes at file offset `$3DFB76` begin `5C 07 6E CA`, confirming the installed hook targets `$CA6E07`.

Validation used the exact user state `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst0`. Two frames after load, the unmodified game logic allocated the missing children: Zinger ID `$19` in slot 9 near `$842A,$00CE` with child index `$8B`, and target barrel ID `$38` in slot 10 at `$8428,$0118` with child index `$8C`; the occupied lower barrel and its `$8A` mapping remained intact. The target survived a 300-neutral-frame persistence run. A deterministic firing sweep found the authored transfer at a 24-frame launch delay: the target barrel's state changed from `$0001` to `$0002`, proving Kong entered it and that the result is not merely visual.

Because the framebuffer renderer deliberately accepts only a canonical ROM hash, `SuperZSNESDKCFramebufferRenderer` was rebuilt as v0.3.1 with the new ROM SHA. Source and installed DLL SHA-256 are `36C4968CED5585D2FE6F4213B50311BE3FC48419C1AA33C730D36DB8EE295943`; its full verifier and renderer tests passed. Live status reports `presenting` with no fallback. The accepted live framebuffer capture is `<superzsnes>\BepInEx\plugins\SuperZSNESDKCFramebufferRenderer\candidate-20260811-220321-201.png`.

The durable automation case is `<superzsnes-source>\Mods\DKCLevelAutomation\recipes\barrel-cannon-group-retry.json`. It asserts the exact level/entrance, nonzero `$8A/$8B/$8C` bookkeeping, Zinger/barrel IDs and target coordinates after two frames, then checks persistence after 300 frames. At handoff, the emulator was running the rebuilt canonical ROM with the exact state loaded, the two missing children spawned, controller schedules cleared, and framebuffer presentation active.

## 2026-08-11 Millstone Mayhem scrolling performance fix

The user's test spot was identified from a full debugger capture as Millstone Mayhem (`level $0028`, entrance `$0058`, Layer1/camera X `$2295`, Y `$00D0`). The CPU framebuffer was presenting with no fallback, but live cadence varied from 36-70 Unity updates/second while the SNES core stayed near 60 frames/second. In the worst five-second window, 97 of 181 Unity updates consumed two or more emulated frames, explaining the reported near-30-FPS motion.

The remaining content-dependent cost was the retained background miss path. Each eight-pixel scrolling bucket miss rebuilt a 374x240 guarded plane pixel-by-pixel. Every pixel repeated tilemap addressing, descriptor decoding, flip/priority work, and planar bit extraction. Broad CHR snapshots also treated unrelated DKC VRAM streaming as a reason to invalidate a background.

`SuperZSNESDKCFramebufferRenderer` v0.4.2 replaces that path with exact per-tile retention and tile-block construction:

- only tile graphics referenced by the retained plane participate in its CHR validity key;
- a referenced 2bpp/4bpp tile is validated once per frame and decoded only when its own bytes changed;
- uniform planes are filled in clipped 8x8 blocks, computing one tilemap descriptor per block instead of once per pixel;
- circular VRAM ranges retain exact wrap behavior but use eight-byte comparisons and block copies;
- out-of-range 16x16 subtile indices fail closed to direct decoding and make the plane non-cacheable.

An intermediate v0.4.0 whole-atlas prototype was rejected. It improved host cadence in one run but decoded all 1,024 tiles whenever any byte in the nominal CHR range changed. Millstone counters showed 450 whole-atlas rebuilds for BG1/BG2 and 775 for BG3 in 1,800 frames because DKC stores unrelated streamed data inside those broad ranges. v0.4.1 introduced per-tile validity but still rebuilt planes pixel-by-pixel; it was used only as the correctness oracle for the final tile-block rewrite.

The controlled comparison used the immutable state `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-perf-millstone-20260811` and the same 1,800-frame macro alternating 90 frames of Right+Y and Left+Y. Results:

- v0.3.1 baseline: sustained 86-90 Unity updates/second during the aligned moving windows, with occasional two-frame batches; cumulative framebuffer background/total averages were about 4.27/7.50 ms.
- v0.4.2: 119.4-119.7 Unity updates/second in every full moving window, 59.90-60.15 emulated FPS, and zero 2+ frame batches; framebuffer background/total averages were about 0.31/3.20 ms.
- The saved Millstone framebuffer before and after the rewrite is byte-identical, SHA-256 `6C69F18BCC6B0F7ACB78E68F85B70118BDC894284AD40AD0B89C91F5D115F8A6`.

The v0.4.2 source/build verifier passes with zero warnings/errors. Installed DLL SHA-256 is `BDD6029BBC138B234E02F5888BAF62F8AD020FD42850C862AE06F0E8F32F12D2`; `Assembly-CSharp.dll` remains pristine. Old framebuffer backups must not retain a `.dll` suffix in an active BepInEx plugin directory: BepInEx scans them and may select an older same-GUID plugin. At handoff, the original Millstone performance state was restored, controller schedules were cleared, and gameplay was resumed with v0.4.2 active.

## 2026-08-11 VSync restoration after the performance pass

The visible tearing report was correct. `SuperZSNESPerformanceGuard` still had its earlier software-pacing experiment enabled: `LimitPresentationRate=true` forced `QualitySettings.vSyncCount=0` and `Application.targetFrameRate=120`. That setting was useful when diagnosing renderer starvation, but it was no longer appropriate after the v0.4.2 framebuffer renderer removed the bottleneck.

The accepted runtime setting is now `LimitPresentationRate=false`. After restart, status confirmed `vSyncCount=1`, `targetFrameRate=-1`, 60.000 emulated FPS, and zero two-or-more-frame batches. Unity updates remained about 119.8/s because Windows reported active displays at 120 Hz and 200 Hz; those updates were synchronized rather than software-paced.

`SuperZSNESPerformanceGuard` v0.4.1 changes the fresh-install default to keep stock synchronized presentation. It also tracks whether it applied a software override and restores the exact VSync/target-frame-rate pair captured at load whenever that override is disabled or the plugin unloads. The v0.4.1 build succeeds with zero warnings/errors; tested DLL SHA-256 is `BB03227A480D337655657B2FE72FD66EBA2B94BFFDB3391C894E94ADE665FE60`.

## 2026-08-11 fixed-width world-map wrap correction

The Snow Barrel Blast world map exposed unrelated cave/mountain fragments in both widescreen margins. Raw PPU evidence showed that BG1 and BG2 were fixed 32x32 maps at scroll X=0 (`BGSC $7C/$78`, bases `$F800/$F000`). The 358-pixel renderer samples beyond native X 0..255, so the SNES 32-column tilemap lookup naturally wrapped negative and >=256 coordinates into unrelated parts of the same map. No ROM streaming data was corrupt.

`SuperZSNESDKCFramebufferRenderer` v0.4.3 detects the exact fixed-screen signature across every visible line: Mode 1, BG1 enabled, sub screen and color math disabled, BG1/BG2 X scroll zero, and both maps 32 columns wide. Only those frames render black outside native X 0..255. Scrolling scenes, 64-wide maps, color-math scenes, and normal Mode 9 gameplay stay on the full 358-pixel renderer.

The exact reproduction is preserved as `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-snow-map-wrap-repro`. The accepted output keeps the authored central 256 pixels unchanged and removes both wrapped sections; runtime diagnostics reported `fixedNativePillarboxActive=true` for 600/600 supported map frames. The saved Millstone gameplay oracle remained byte-identical to v0.4.2, SHA-256 `6C69F18BCC6B0F7ACB78E68F85B70118BDC894284AD40AD0B89C91F5D115F8A6`, with the gate false.

Production build and the expanded pillarbox/signature verifier pass with zero warnings/errors. Tested v0.4.3 DLL SHA-256 is `26088E224B973BC145CCC657F5AFDE327033E7EF02A37F9C4D202B880340E49F`.

## 2026-08-11 Slip-Slide Ride rope-position investigation

The exact user state is `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst1`, frame 551552, in Slip-Slide Ride (`level $0051`, entrance `$006D`). It initially appeared that the nearby blue rope was misplaced or nonfunctional. Two independent facts explain the observation without requiring a rope-coordinate patch.

First, the state was saved with DKC's internal pause bit set: `$7E0579=$00C1`, including bit `$0040`. `CODE_80992F` still reads controllers while this bit is set, but skips gameplay updates, so the rope, Kongs, camera, and objects remain frozen. One Start frame changes the value to `$0081` and resumes normal simulation.

Second, the rope's authored, simulated, rendered, and collision coordinates agree. The nearby object is the sole rope record in this section: `SlipSlideRide_Main.bin+$0038` / `DATA_BDD638`, `dw $0001,$02E0,$63E0,DATA_B5BE91`. At the saved frame its active ID `$30` actor is at `$02DD,$63E0`, three pixels left of its `$02E0` horizontal oscillation anchor. With Layer1 X `$0229`, the actor is at native screen X `$00B4`; its OAM art anchor is X `$00B1`, the expected three-pixel sprite-art offset. The framebuffer places it at output X about 228 after adding the 51-pixel left extension. The background uses the same native-to-output mapping.

A reversible stock-ROM comparison loaded this same state, applied the identical `0=START;1-30=NONE` input, and compared it with the widescreen ROM at frame 551583. Both produce rope actor X/Y `$02E2/$63E0`. OAM entries 23-33 are byte-identical in both captures: X 182, Y 231 through 71 in 16-pixel steps, tile `$60`, attributes `$36`, large-size pair 2. The complete OAM snapshots differ only in six bytes belonging to widened banana coverage; no rope byte differs. Captures:

- stock: `<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\Sessions\20260811-214936\capture-f00551583-20260811-223245-769`;
- widescreen: `<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\Sessions\20260811-214936\capture-f00551583-20260811-223259-949`.

The object table also confirms there is no omitted second rope nearby: the preceding and following equivalent `DATA_B5BE91` ropes are authored at X `$0100` and `$0520`, while this rope is at `$02E0`. The widened viewport merely reveals more of the surrounding cave; it does not relocate this rope.

The exact route was tested deterministically. From the state, `0=START;1-8=RIGHT+B;9-420=UP` attaches Diddy to the rope (Kong state `$0025`) and the blue rope carries the Kongs upward automatically; another 420 Up frames continues the ascent. `0-59=RIGHT+B;60-180=RIGHT+Y` then jumps off and advances the level. This verifies the visible rope and the grabbable/collision rope are the same actor.

No ROM or renderer patch was made for this checkpoint. Moving the rope would make it diverge from the original game and its collision path. Treat any later rope-position report as a separate state-specific case, especially for purple ropes or multi-rope sequences, and repeat the authored/live/OAM/collision comparison before changing coordinates.

## 2026-08-12 Slip-Slide Ride shimmer compositor fix

The user state `<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst1` reproduces a foreground ice-shimmer error in the CPU framebuffer renderer. At exact save-state frame 573672, the PPU is uniform Mode 1 with `TM=$13` (BG1/BG2/OBJ main), `TS=$04` (BG3 subscreen), `CGWSEL=$02` (subscreen color operand), and `CGADSUB=$33` (add color math on BG1/BG2/OBJ/backdrop). The bad CPU image displayed large purple BG3 halo shapes across the foreground. Disabling the CPU presenter restored the correct white/cyan glints.

Layer-isolated evidence proved the tilemaps were not corrupt. CPU BG1/BG2 main-priority output matched the legacy main plane structurally, and CPU BG3 contained the intended animated glint art. The divergence appeared only after main/subscreen composition. The pre-fix final candidate is `<superzsnes>\BepInEx\plugins\SuperZSNESDKCFramebufferRenderer\candidate-20260812-144406-627.png`; its diagnostic planes share the same timestamp.

The screenshot tooling itself contained a misleading fallback: `PPURenderer.GetFinalComposedTexture()` returns a `Texture2D`, but `DKCWidescreenDebugger` accepted only `RenderTexture`, so `target=composed` silently returned the raw main plane. `DKCWidescreenDebugger` v0.1.4 now captures the live private `transferScreenRenderTexture` for the full 796x448 widescreen composite, accepts the private subscreen render texture as `target=sub`, and correctly encodes/destroys a returned `Texture2D` fallback. At frame 573672 the exact background-only oracle surfaces are:

- main: session `20260812-105619`, `screenshot-main-f00573672-20260812-105634-629.png`;
- sub: `screenshot-sub-f00573672-20260812-105634-670.png`;
- composed: `screenshot-composed-f00573672-20260812-105634-721.png`.

Those surfaces reveal two legacy-compositor rules the CPU path lacked. An empty subscreen location is opaque black, not CGRAM color 0. A selected additive pixel is blended by the final shader after sRGB-to-linear conversion: each linear channel is raised to `1/1.9`, main and sub are summed, the result is raised to `1.9`, clamped, and encoded to sRGB. Across the captured additive pixels, this model differs from the GPU oracle by at most one 8-bit value per channel. Representative exact readbacks include main/sub/final channel values `16+8 -> 31`, `40+8 -> 53`, `48+8 -> 61`, and `72+8 -> 84`.

`SuperZSNESDKCFramebufferRenderer` v0.4.4 implements both rules. The gamma-aware add is a precomputed 16-brightness x 32-main x 32-sub lookup table (16 KiB), avoiding power functions in the per-pixel loop. The existing SNES 5-bit subtract/half path remains unchanged. Captures now include BG1/BG2/BG3 and main-background-only diagnostic PNGs, with the configured left extension used for window coordinates.

The full verifier builds with zero warnings/errors and includes the captured shader-add fixtures. At the reproduction checkpoint, live retained rendering reported approximately 2.09 ms per supported frame after warmup: 0.18 ms line state, 0.16 ms backgrounds, 0.09 ms sprites, and 1.63 ms composition. The accepted frame is `<superzsnes>\BepInEx\plugins\SuperZSNESDKCFramebufferRenderer\candidate-20260812-150241-223.png`; the corresponding live composed screenshot is debugger session `20260812-110219`, `screenshot-composed-f00573672-20260812-110243-041.png`. A 120-frame neutral animation pass retained the glints without the purple overlay or renderer fallback.

Final shareable build hashes are framebuffer renderer v0.4.4 `A99B13F43025DDD9A3D1693BCB98EC0EED56A7D91938E655394D67D11A427184` and repo-built widescreen debugger v0.1.4 `E572DFFA798CD845E22A6D82A37983BCA83A694C380171BFB23AFFAC4234D302`. `Assembly-CSharp.dll` remains pristine at `33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED`.
