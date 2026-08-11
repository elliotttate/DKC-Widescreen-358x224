# SuperZSNES Core Optimizations

This BepInEx plugin contains narrowly scoped, version-checked Harmony IL rewrites for SuperZSNES v0.230. It does not modify `SUPERZSNES_Data/Managed/Assembly-CSharp.dll` on disk.

Version 0.2.0 contains independently configurable experiments for `MainMemoryMap.ReadMem` and `TileTextureGen.GetTileMaterial`.

The tile-material experiment replaces one `ContainsKey` and five repeated indexer lookups of the same `(priority/background, texture, CRC)` tuple with one `TryGetValue` result local. It is disabled by default until the paired runtime benchmark is accepted.

The experiment is **disabled by default**. A paired deterministic benchmark (600 warm-up frames followed by three 600-frame samples) found a stock median of 10.511 seconds / 50.938 ms CPU per emulated frame and a rewrite median of 10.808 seconds / 55.182 ms CPU per emulated frame. It therefore remains available only for reproducibility and is not a recommended optimization.

The transpiler validates the exact v0.230 instruction pattern and refuses to apply if it differs. Runtime application state is in `BepInEx/plugins/SuperZSNESCoreOptimizations/status.json`.

## Configuration

After first launch, edit `BepInEx/config/dev.local.superzsnes.coreoptimizations.cfg` and restart:

```ini
[Optimizations]
ReadMemCheatFastPath = false
TileMaterialCacheFastPath = false
```

Setting it to `false` provides an exact stock-core A/B without removing the plugin.
