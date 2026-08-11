# SuperZSNES Palette Cache Probe

A startup-disabled diagnostic for the shipped SuperZSNES v0.230 palette texture cache. It measures `TileTextureGen.CalculatePalTexture`, `GenerateTextures`, and `ClearCache` without changing their results or cache policy.

With `Probe.Enabled=false` (the default), it installs no Harmony patches and opens no output file. This project has not been installed into the game.

When a later coordinated test is approved, copy the built DLL to its own plugin folder, start once to generate configuration, close normally, then set:

```ini
[Probe]
Enabled = true
WindowSeconds = 5
```

Run the clean Jungle real-time recipe through a fade/transition and align `windows.jsonl` UTC windows with CadenceCounter. Compare a second probe-off run because timing every palette lookup has observer cost.

Output reports lookup calls/misses and timing, cache min/max/end, one-per-frame stale evictions, and `ClearCache` duration. A cache miss is detected from the dictionary count increasing across the original call; an eviction is detected from it decreasing across `GenerateTextures`.

Build and verify offline:

```powershell
dotnet build .\SuperZSNESPaletteCacheProbe.csproj -c Release
.\verify.ps1
```
