# SuperZSNES DrawLines Cache Gate

This standalone BepInEx optimization moves each `PPURenderer.DrawLines` material-cache lookup before `ProcessMaterial`:

```text
hit:  TryGetValue -> use cached tuple
miss: TryGetValue -> ProcessMaterial -> retrieve inserted tuple -> use it
```

The measured profile reports about 7,520 DrawLines-to-ProcessMaterial calls per presented frame with roughly 70% cache hits. RendererFastPaths v0.1 still calls `ProcessMaterial` on every tile; its internal early `ContainsKey` and the caller's `TryGetValue` mean two tuple hashes plus a method call on a hit. This gate reduces a hit to one `TryGetValue` and no `ProcessMaterial` call.

At a 70% hit rate, cache-check/retrieval probes (excluding the required insertion `Add` on misses) fall from about 15,040/frame to approximately 12,032/frame (5,264 hits × 1 plus 2,256 misses × 3), a 20% reduction. Including that unchanged miss insertion, the estimate is 17,296 to 14,288 dictionary operations/frame, or about 17.4%. It also removes about 5,264 private method calls per presented frame. A miss costs one extra probe because `ProcessMaterial` retains its own defensive early check; this is deliberate to keep the callee independently safe.

Null `(Material, MaterialPropertyBlock)` cache entries are retained. The first DrawLines path still renders a null material only when `hiResH` allows it; the second high-resolution path still requires a non-null material.

## Harmony compatibility

The transpiler accepts either stock DrawLines IL or RendererFastPaths v0.1-normalized IL. It declares a soft BepInEx dependency and a Harmony `after` constraint on `dev.local.superzsnes.rendererfastpaths`, so the existing lookup normalizer executes first regardless of plugin registration order. The offline verifier registers the two real transpilers in both orders against the actual v0.230 `PPURenderer.DrawLines` method and compares the resulting live Harmony instruction chains.

The optimization is disabled by default and has not been installed.

Current DLL SHA-256: `70BCC0CBD6AF06DED555E9354046A1EA7D0D4A0924979C817A62AC0317698A17`.

## Build and verify

```powershell
dotnet build .\SuperZSNESDrawLinesCacheGate.csproj -c Release
.\verify.ps1
```

Future config, after a coordinated install and restart:

```ini
[Optimization]
Enabled = false
```

Use the deterministic 2D DKC scene and AllocationProbe/RendererTimingProbe for the A/B. Keep RendererFastPaths v0.1 `DrawLinesMaterialLookup=true`, MaterialCacheGuard pooling enabled with diagnostics off, and compare `GenerateBackgrounds`, full `MasterExecutor.Update`, and full Unity-frame time. A visual/OAM/tilemap regression remains required before enabling by default.
