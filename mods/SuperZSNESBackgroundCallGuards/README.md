# SuperZSNES Background Call Guards

An isolated, runtime-only BepInEx optimization for the exact SuperZSNES v0.230 `PPURenderer.GenerateBackground` IL. Both switches default to `false`, so merely installing the DLL changes no emulator behavior.

> **Quarantine notice (v0.1.1):** live visual QA produced severe rendering corruption when both switches were enabled together. The exact offline control-flow audit did not reproduce a misplaced branch, but the combined runtime result is authoritative. The plugin now refuses that combination and applies no patch. Keep both switches off in normal use; enable at most one only for a controlled isolation run.

## Optimizations

- `OptimizeNoOpProcess2DTilesCalls`: the stock material tail invokes `Process2DTiles` at thresholds 256, 64, 16, 4, and 1 for every used material. `Process2DTiles` contains only `while (mct >= numTiles)`. The injected signed comparison skips the complete argument load and call when that condition is false; calls that can process a batch are unchanged.
- `OptimizeEmptyScratchClearLoop`: before rebuilding a background, stock enumerates `usedMaterials` and probes `tileAddrToMat` for each key. The material list-pool guard clears `tileAddrToMat` in its prefix. This optimization checks `tileAddrToMat.Count` and skips the stock enumeration only when the map is already empty. Without the pool plugin, a non-empty map takes the untouched stock path.

These are exact no-op eliminations. The plugin does not cache a rendered background, suppress mesh uploads, change a material/property block, alter a Unity asset, or assume that a camera/palette/frame is static.

## Evidence and scope

The measured steady DKC scene spent about 11.8-12.3 ms in `GenerateBackgrounds`, including three `GenerateBackground` calls averaging roughly 3.4-3.6 ms each. Texture upload work was negligible in the same interval. The remaining high-cost region includes the per-material mesh build/submission tail. These guards remove avoidable managed dispatch and collection work there; they do not claim to remove the much larger required `Mesh.SetVertices`, `Mesh.SetUVs`, `Mesh.RecalculateBounds`, renderer property-block, and mesh binding calls.

The captured final-background scratch map had 91 non-empty material lists and 1,375 tiles. Stock therefore dispatches 455 `Process2DTiles` calls for that background. Even the call-maximizing distribution of 1,375 tiles over 91 non-empty lists can enter no more than 239 of the five threshold calls, so the guard removes at least 216/455 (47.5%) of those dispatches in that captured map. The empty-map guard also removes the 91-key `ContainsKey` clear walk when the list-pool prefix has already harvested the map. This quantifies managed work removed, not an expected percentage reduction in total render time; Unity mesh submission still dominates the safe residual work.

The exact v0.230 verifier requires:

- two `usedMaterials` enumerators separated by exactly one `usedMaterials.Clear`;
- exactly five `Process2DTiles` call sites;
- thresholds exactly `256/64/16/4/1` in that order;
- the exact receiver/list/material/remainder/mesh-pool argument shape and matching `mesh{N}` fields.

If the live Harmony chain no longer has that shape, the transpiler returns stock instructions, removes its own patches, and writes `failed-closed` status. If both options are requested, v0.1.1 writes `quarantined-combination` and does not patch `PPURenderer`.

## Build and verify

```powershell
dotnet build .\SuperZSNESBackgroundCallGuards.csproj -c Release
dotnet run --project .\Tests\SemanticTests.csproj -c Release
dotnet build .\Verifier\BackgroundCallGuards.Verifier.csproj -c Release
.\Verifier\bin\Release\net472\BackgroundCallGuards.Verifier.exe
Get-FileHash .\bin\Release\net472\SuperZSNESBackgroundCallGuards.dll -Algorithm SHA256
```

## Optional install/test procedure

No files are installed by this project. For a controlled test, copy only `bin\Release\net472\SuperZSNESBackgroundCallGuards.dll` into the emulator's BepInEx plugin folder, start once to create `BepInEx\config\dev.local.superzsnes.backgroundcallguards.cfg`, stop normally, enable one or both switches, then restart. The runtime status is written to `BepInEx\plugins\SuperZSNESBackgroundCallGuards\status.json`.

For diagnosis only, compare identical save-state intervals with one option enabled at a time. The first acceptance criterion is pixel-identical video across movement, transitions, and animated palettes; timing is secondary. Do not run both options together.
