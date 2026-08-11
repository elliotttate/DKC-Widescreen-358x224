# SuperZSNES Material Cache Guard

Narrow BepInEx 5 fix for unbounded `PPURenderer.tileAddrToMat` retention in SuperZSNES v0.230. Version 0.2 replaces the old periodic whole-map clear with per-`GenerateBackground` scratch-list pooling. It never destroys a Unity `Material`, `Texture`, `MaterialPropertyBlock`, mesh, or other asset.

## Why this cache grows

`tileAddrToMat` maps `(Material, MaterialPropertyBlock)` to `List<PPURenderer.TileInfo>`. Stock `GenerateBackground` clears lists for `usedMaterials`, and clears `usedMaterials` and `matDict`, but never removes old `tileAddrToMat` keys. Material/property-block identity churn therefore retains historical keys, lists, and their backing arrays.

Relevant decompiled source locations:

- `PPURenderer.GenerateBackgrounds`: line 1218.
- `PPURenderer.GenerateBackground`: line 2617; its stock per-BG cleanup is lines 2769-2777.
- `PPURenderer.ProcessMaterial`: line 3600; the sole `tileAddrToMat.Add(..., new List<TileInfo>())` is line 3641.

## Fix behavior

When `ScratchListPool.EnablePerBackgroundScratchListPool=true`:

1. A prefix on the private singular `GenerateBackground` validates every current `tileAddrToMat` value as the exact runtime `List<PPURenderer.TileInfo>` type.
2. It clears each list, returns it to a private pool, and clears `tileAddrToMat` before stock BG generation starts.
3. A one-time `ProcessMaterial` IL transpiler replaces only the list constructor at the sole `tileAddrToMat.Add` site with:

   ```text
   call object ScratchListPool.RentObject()
   castclass List<PPURenderer.TileInfo>
   ```

`TileInfo` remains private; the runtime list type and constructor come from the verified `tileAddrToMat` field. The dictionary therefore contains at most the current SNES BG layer's material keys. Lists and their backing capacity are reused. New lists are allocated only when a BG exceeds the previous simultaneous-list high-water.

If runtime map validation fails, pooling is disabled and the pool is discarded. The transformed rental then constructs a fresh list, preserving stock semantics. Attachment itself fails closed and unpatches if the expected field, private type, constructor, tail `CheckMaterialList` call, singular `GenerateBackground` loop call, or sole `ProcessMaterial` add-site shape differs.

## Configuration

```ini
[ScratchListPool]
EnablePerBackgroundScratchListPool = true

[Diagnostics]
EnableDiagnostics = false
SampleIntervalRenderCalls = 300
```

The pooling fix is controlled only by the new `EnablePerBackgroundScratchListPool` key and defaults to enabled. Legacy `EnablePeriodicScratchMapClear` and `ScratchMapClearIntervalRenderCalls` keys are no longer read and should be removed from an installed configuration.

Diagnostics remain optional and low-frequency. They write:

```text
BepInEx\plugins\SuperZSNESMaterialCacheGuard\material-cache.jsonl
BepInEx\plugins\SuperZSNESMaterialCacheGuard\status.json
```

Samples include current scratch-map count/list capacity, free pooled lists, pool high-water, total list allocations/rentals/returns, material-cache counts, and managed/private/working-set memory.

## Build and offline verification

```powershell
dotnet build "<superzsnes-source>\Mods\SuperZSNESMaterialCacheGuard\SuperZSNESMaterialCacheGuard.csproj" -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File "<superzsnes-source>\Mods\SuperZSNESMaterialCacheGuard\verify.ps1"
```

The verifier uses the actual private `List<PPURenderer.TileInfo>` runtime type. It transforms the shipped 138-instruction `ProcessMaterial` into 139 instructions, checks the single rental/cast replacement without patching any method, and runs 2,001 simulated BG layers. A 64-list first-layer high-water remains exactly 64 allocations across all later varying layers; the scratch dictionary never exceeds the current layer's key count.

Verified target `Assembly-CSharp.dll` SHA-256:

```text
33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED
```

Method metadata:

- `GenerateBackgrounds`: token `0x06000654`, RVA `0x00040544`, 8,113 IL bytes.
- `GenerateBackground`: token `0x06000662`, RVA `0x00044AA8`, 8,907 IL bytes.
- `ProcessMaterial`: token `0x06000665`, RVA `0x000478D8`, 360 IL bytes. The stock list constructor is `IL_015D`; dictionary `Add` is `IL_0162`.

No installer is included. Copy the built DLL only during a coordinated emulator restart.
