# SuperZSNES Renderer Fast Paths

Version 0.2.0 is a standalone, disabled-by-default BepInEx experiment for SuperZSNES v0.230. It contains five independently switchable, exact-shape Harmony rewrites. It never edits `Assembly-CSharp.dll` on disk.

The v0.2 build in this project has **not** been installed. The runtime plugin folder already contains the earlier v0.1.0 DLL (`DF5FA482...B2555B4D`); the offline verifier distinguishes it from v0.2 and does not alter it.

## Evidence and expected scope

The clean 2026-08-11 interval delivered 3,908 emulated frames in 65.134 seconds (59.999 Hz) but only 3,536 `MasterExecutor.Update` calls (54.288 Hz). `Update` averaged 14.244 ms while `RunFrame` averaged 2.456 ms, leaving about 11.788 ms/update outside the emulation core. Material-pool counters showed 2,115 list rentals/material-key paths per composite render without new list allocation, so repeated tuple-key hashing is a credible CPU target. This is not runtime proof of a speedup; every switch still needs a paired deterministic A/B.

## Exact rewrites

- `DrawLinesMaterialLookup` (v0.1): two `matDict.ContainsKey(key)` + `matDict[key]` reads become `matDict.TryGetValue(key, out value)`. Each successful site removes one tuple hash/dictionary probe.
- `UsedMaterialsAdd` (v0.1): `if (!usedMaterials.Contains(value)) usedMaterials.Add(value)` becomes one unconditional `usedMaterials.Add(value)`. Duplicate `HashSet<T>.Add` is a no-op, so set contents are unchanged.
- `Mode7DataLookup` (v0.2): the get-or-create sequences in `UpdateMode7Tiles` and `CalculateBoundsMesh` become `TryGetValue`; a newly created `List<Vector3>` is retained in the same local that receives a hit. This removes one `mode7data` lookup on both hits and misses. The full-map Mode 7 loop can visit up to 16,384 tile positions, so this has the largest potential benefit when Mode 7 is active. It does not affect ordinary DKC 2D levels.
- `TileListClearLookup` (v0.2): decompiled line 2771 in `GenerateBackground` becomes one `tileAddrToMat.TryGetValue` followed by `Clear` on the exact returned list. It removes the indexer probe when a key exists. With MaterialCacheGuard scratch pooling enabled, its prefix has already harvested and cleared this dictionary, so the stock check normally misses and this rewrite is expected to have little or no benefit in the current configuration.
- `DynamicFontLookup` (v0.2): cache-hit reads in `GetDynamicFontTexture` and `GenerateDynamicFontTexture` become `TryGetValue`, and the generator's `usedDynamicFonts.Contains` + `Add` becomes one `Add`. Benefits are limited to mods/scenes with dynamic-font rendering enabled.

`ProcessMaterial` decompiled line 3602 remains unchanged: it is a single `matDict.ContainsKey` early-out and a `TryGetValue` with a discarded value would not remove a lookup. Line 3639 also remains unchanged: it is one `tileAddrToMat.ContainsKey` on hits and a required `Add` on misses; substituting `TryGetValue` would have identical dictionary-probe counts. The later line-3374 `tileAddrToMat` indexer is required to process the populated list and is retained.

Every transpiler validates the exact v0.230 instruction shape and exact match count. A mismatch throws instead of applying a partial rewrite.

## Build and offline verification

```powershell
dotnet build .\SuperZSNESRendererFastPaths.csproj -c Release
.\verify.ps1
```

The verifier checks the stock game-assembly hash, runs six decoded target transforms without patching the process, validates `UpdateMode7Tiles` directly from on-disk Cecil metadata (desktop PowerShell cannot load that method's Unity `System.Span` local), checks call-count and object-identity/set semantics, and verifies the ProcessMaterial rewrite composes with MaterialCacheGuard in either transpiler order.

Current v0.2 DLL SHA-256: `82991AE2E9C845E8ED5429F230C5E7D017F101471ADD573F14202DE3C8E62DFE`.

## Future controlled A/B

Do not replace the installed v0.1 DLL until a coordinated clean restart. A v0.2 load provides these settings, all defaulting to false:

```ini
[Optimizations]
DrawLinesMaterialLookup = false
UsedMaterialsAdd = false
Mode7DataLookup = false
TileListClearLookup = false
DynamicFontLookup = false
```

Test one switch at a time on the same deterministic state. For ordinary DKC 2D gameplay, prioritize `DrawLinesMaterialLookup`, then `UsedMaterialsAdd`; the three v0.2 additions target Mode 7, stock/no-pool cleanup, and dynamic fonts respectively. Keep MaterialCacheGuard scratch pooling enabled but diagnostics off, and disable timing probes other than AllocationProbe for final samples. Compare full Unity-frame, `MasterExecutor.Update`, and `GenerateBackgrounds` time as well as allocations. Perform a screenshot/hash or TilemapInspector visual regression for every enabled combination.
