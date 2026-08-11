# Remaining performance findings audit

Date: 2026-08-11. This is an offline/read-only audit of the shipped SuperZSNES v0.230 `Assembly-CSharp.dll` (`SHA-256 33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED`). No emulator process was launched, stopped, controlled, or modified by this audit.

## Outcome and priority

None of these five findings explains the measured sustained 52-55 Hz Unity `Update` cadence. In the clean 65.134-second AudioTimingProbe interval from `17:40:42Z` through `17:41:47Z`, emulation was 3,908 frames / 59.999 Hz, while 3,536 host Updates ran at 54.288 Hz and averaged 14.244 ms. `RunFrame` averaged 2.456 ms and maxed at 5.964 ms. The sustained limit remains the presented-frame/renderer path inside `Update`, not an emulated-frame, audio, history, or debug-list bottleneck.

Ranked for burst drops:

1. **Palette cache miss/eviction churn during fades: medium-to-high burst risk, low stable-scene risk.** Existing diagnostics observed 1,342 live palette textures followed by a one-per-rendered-frame drain. A dedicated disabled probe was built because this deserves timestamp correlation.
2. **Forced `GC.Collect` plus `Resources.UnloadUnusedAssets`: high stall potential when invoked, but transition/UI-only in the shipped call graph.** It does not run every ordinary fixed-resolution DKC frame.
3. **History screenshot capture: high 20-second spike potential when enabled, but eliminated in the current guarded configuration.**
4. **CPU/SPC `usedPC`: debug-only growth and lookup.** In the normal branch, the lists are not touched per instruction. It cannot explain current sustained cadence.
5. **Audio ratio clamp: negligible direct cost and on the audio callback thread.** Existing timing excludes it as the host-Update limiter; changing it risks latency, underruns, and pitch stability.

## Palette texture cache

Source:

- `TileTextureGen.cs:368-415`: `StartFrame` and scanline CGRAM replay call `CalculatePalTexture`. Every new 512-byte CGRAM CRC creates a 16x16 ARGB32 `Texture2D`, calls `SetPixels` and `Apply`, and records the current renderer frame.
- `TileTextureGen.cs:619-665`: `GenerateTextures` scans the entire `paletteFrameUsed` dictionary and considers entries older than 300 rendered frames. Although it finds every qualifying key, it retains only one key and destroys/removes at most one texture per rendered frame.
- `PPURenderer.cs:1302,1427,1880`: palette lookup occurs at frame start and again for scanline CGRAM changes; stale cleanup runs once at the end of each `GenerateBackgrounds`.

Shipped IL:

- `TileTextureGen.CalculatePalTexture`: token `0x06000678`, RVA `0x00049C48`, 276 IL bytes. `Dictionary.ContainsKey` is `IL_0014`; `Texture2D::.ctor` `IL_00B3`; `SetPixels` `IL_00C9`; `Apply` `IL_00CF`. Callers are `TileTextureGen.StartFrame` at `IL_001D` and `PPURenderer.GenerateBackgrounds` at `IL_0A62`.
- `TileTextureGen.GenerateTextures`: token `0x0600067D`, RVA `0x0004A5DC`, 395 IL bytes. Dictionary enumeration begins at `IL_0112`; `Object.Destroy` is `IL_016B`; the two dictionary removals are `IL_0177/IL_0184`. Its only caller is `PPURenderer.GenerateBackgrounds` at `IL_1FA0`.

Observed evidence from `SuperZSNESMaterialCacheGuard/material-cache.jsonl`:

- Cache count reached 1,342 at `17:05:26.138Z`.
- Subsequent 300-render samples were 1,118, 818, 518, and 218. The exact 300-entry drops in later intervals match the maximum one eviction per rendered frame.
- In a later stable interval, all 25 samples from `17:55:48.930Z` through `17:57:54.021Z` reported exactly 26 palette textures. This rules it out as a continuous stable-Jungle growth source.

Interpretation: a fade or dense scanline palette effect can create several unique textures per rendered frame while cleanup can remove only one. The growing dictionary is fully enumerated every rendered frame, and misses perform native texture creation/upload. Cleanup then extends the churn about five seconds past last use. This is credible for local drops around fades, not the long stable 54 Hz host cadence.

The offline-built `SuperZSNESPaletteCacheProbe` measures lookup calls/misses, cache cardinality, stale evictions, and method timings in five-second windows. It defaults disabled and has not been installed.

## `ClearCache`, forced GC, and asset unload

`TileTextureGen.ClearCache` replaces its dictionaries, dirties every tile/CG region, then calls `Resources.UnloadUnusedAssets` followed by `GC.Collect`. Its shipped method is token `0x06000675`, RVA `0x000499A4`, 401 IL bytes; unload/collect are `IL_0185/IL_018B`. The sole direct caller is `PPURenderer.ClearCache` at `IL_0006`.

Complete `PPURenderer.ClearCache` callers from the shipped DLL:

- `PPURenderer.OnResolutionChanged` `IL_0041`
- `PPURenderer.ResetRenderer` `IL_0001`
- `MasterExecutor.RefreshVisualsNoLighting` `IL_0006`
- `MasterExecutor.RefreshModData` `IL_0006`

`ResetRenderer` is called only by `MasterExecutor.LoadRom` (`IL_01CE`) and `MasterExecutor.Reset` (`IL_0039`). The refresh calls originate from mod/editor UI paths. `OnResolutionChanged` is subscribed during renderer startup and is invoked by `MasterExecutor.Update` only 250 ms after `Screen.width` or `Screen.height` changes (`MasterExecutor.cs:1255-1269`). Thus a fixed-size ordinary DKC frame has no `ClearCache` path.

The DLL contains these direct forced-collection/unload sites:

- `TileTextureGen.ClearCache`: one unload + one full GC.
- `PPURenderer.OnResolutionChanged`: two additional full GC/unload pairs, besides its nested `ClearCache` pair.
- `PPURenderer.ResetRenderer`: one additional pair, besides nested `ClearCache`.
- `MasterExecutor.LoadRom`: initial unload and final GC/unload, in addition to `ResetRenderer`.
- `MasterExecutor.Reset`: one additional pair, in addition to `ResetRenderer`.
- `MasterExecutor.ReturnToGame` and `EscapeBackToMenu`: one unload each.
- `SaveStateSelectOverlay.OnDisable`: one full GC/unload pair.

These can absolutely create a one-off hundreds-of-milliseconds stall during resize, ROM load/reset, menu return, editor refresh, or save-state overlay closure. Removing them globally is unsafe without asset lifetime testing. They are not a sustained fixed-resolution gameplay cause. RuntimePauseProbe plus existing `CLEAR CACHE` / `ON RES CHANGED` logs are sufficient for a controlled resize test; no second diagnostic was built.

## History screenshots and active disablement

`MasterExecutor.Update` calls `UpdateHistoryState` at shipped `IL_063F`. `UpdateHistoryState` is token `0x06000548`, RVA `0x00036084`, 803 IL bytes. It loads `historyDisabled` at `IL_000A` and branches directly to return at `IL_000F`. When enabled, its 20-second timer comparison reaches a full `SNESMemoryState.SaveState(..., true, ...)` at `IL_02D3`.

The snapshot copies roughly 281 KB (274 KiB) of emulator memory and calls `StoreState(saveScreenshot:true)`. `CaptureScreenshot` is token `0x0600053B`, RVA `0x00034DB4`, 255 IL bytes: `Camera.Render` at `IL_0023`, GPU readback through `Texture2D.ReadPixels` at `IL_009B`, full `GetPixels`/alpha rewrite/`SetPixels`, `Apply` at `IL_00DD`, and `EncodeToPNG` at `IL_00F9`. This is a credible periodic spike when enabled.

The current on-disk PerformanceGuard configuration has `DisableHistoryCapture=true`. The read-only runtime status for the present process, written `2026-08-11 18:48:40Z`, reports `disableHistoryCapture=true` and `historyDisabled=true`; the corresponding BepInEx log says the guard loaded with history capture disabled. Current guarded runs therefore return at `IL_000F` and cannot execute the screenshot path.

## CPU/SPC `usedPC`

`CPU65c816.ExecuteInstructionsMainCPU` is token `0x0600046D`, RVA `0x00023014`, 1,268 IL bytes. It calls `MasterExecutor.CPUDebugOn` once per execution slice at `IL_0037`, then branches to the normal non-debug loop at `IL_003C` when false. `usedPC.Contains` (`IL_018F`) and `usedPC.Add` (`IL_021F`) exist only in the debug branch. `CPUDebugOn` currently searches an empty `debugFind` list and returns false unless debug was explicitly enabled. This small slice-level empty-list check is not the reported growing per-instruction list.

`CPUSPC700.ExecuteInstructionsSPC700` is token `0x060004EC`, RVA `0x0002D4A4`, 650 IL bytes. The main `usedPC.Contains/Add` pair (`IL_00BD/IL_00E1`) is behind `SPCDebugOn` and its `brfalse` at `IL_00AF`. A second pair (`IL_0133/IL_0146`) is reachable only when an instruction advanced zero cycles, immediately before the `Unimplemented Opcode!` error path.

The lists do persist after a debug capture, but normal execution no longer searches them. No `cpu_output`, `frame_cpu_output`, `trace`, or `spctrace` file exists in the game root. There is no evidence for either sustained or burst impact in the measured run.

## Audio ratio clamp

`DSPAudio.OnAudioFilterRead` is token `0x06000441`, RVA `0x00020D90`, 867 IL bytes. Its `bufferLock` spans `Monitor.Enter` `IL_003A` through `Monitor.Exit` `IL_035C`. The latency correction `Mathf.Clamp(value, 0.995, 1.005)` is one call at `IL_0131` per audio callback, not per sample and not on Unity's main thread. A more extreme fill-ratio override can subsequently replace the clamped ratio; this is underrun/overflow recovery behavior, not a presentation scheduler.

Across the same clean 65.134-second interval, 12,214 audio callbacks averaged 204.268 microseconds and maxed at 3.390 ms. Buffer-lock waits maxed at 1.558 ms while `RunFrame` still held 59.999 Hz and the host Update average remained 14.244 ms. Changing or widening this clamp cannot recover the sustained host cadence and could destabilize latency/pitch. No fix is justified.

## Test plan

1. **Palette fade A/B:** on separate coordinated restarts, run the same cave-exit state with the real-time RIGHT+Y harness, `--warmup-seconds 0 --measurement-seconds 15`, first probe-off and then palette-probe-on. Align PaletteCacheProbe and CadenceCounter UTC windows. Repeat at least three pairs. A causal signal requires miss/eviction windows to coincide with Update-Hz or max-gap degradation beyond the probe-off distribution; account for probe observer cost.
2. **Stable palette control:** run the clean Jungle recipe for 30 seconds after warmup. Expected result is cache cardinality near the observed stable 26, near-zero misses/evictions, and no relationship to the persistent ~54 Hz Update cadence.
3. **Resize/GC positive control:** with RuntimePauseProbe armed on a later test-only restart, resize exactly once after a stable window. Confirm `ON RES CHANGED`/`CLEAR CACHE`, GC deltas, and a main-thread/runtime-wide gap. Compare with an unchanged-size window. Do not use this as normal gameplay A/B.
4. **History positive control only if needed:** disable the guard in a disposable test session and capture at least 45 seconds so two 20-second boundaries occur; compare against guarded history-off. This is unnecessary for current diagnosis because the active gate is already proven.
5. **CPU/audio:** keep debug/tracing off. Retain AudioTimingProbe only for corroboration; do not change the ratio clamp unless an actual underrun/drop counter problem appears.

No cache policy, history behavior, CPU debug code, audio code, or shared plugin was changed by this audit.
