# SuperZSNES Renderer Timing Probe

This is a runtime-only, disabled-by-default BepInEx diagnostic for SuperZSNES v0.230. It decomposes the cost that sits inside `MasterExecutor.Update()` but outside `RunFrame()`.

## What it measures

Five-second JSONL windows contain inclusive timings and histograms for:

- `MasterExecutor.Update` and `RunFrame`
- `PPURenderer.GenerateBackgrounds` and each singular `GenerateBackground`
- `RefreshTextures`, scene selection, `SetupZPositions`, and palette calculation
- `TileTextureGen.StartFrame`, `GenerateTextures`, and `CheckMaterialList`
- cache/list/property-block cardinalities sampled once per window

`EnableHotPathInstrumentation=true` additionally times `DrawLines`, `ProcessMaterial`, `Process2DTiles`, `GetTileMaterial`, and the 2/4/8-bpp texture getters. It is off by default because thousands of Harmony prefix/postfix calls can perturb the workload being measured.

The probe deliberately does not patch `Texture2D.Apply` itself. Unity's method is a native-engine boundary, and a Harmony detour there would be both risky and difficult to interpret. `TileTextureGen.GenerateTextures` includes its `SetPixelData`/`Apply` work; the optional texture-getter timings show the CPU-side decode component.

## Optional dirty-upload gate

The source has an independently verifiable redundant-upload pattern:

1. `StartFrame` clears `texture2bitDirty`, `texture4bitDirty`, and `texture8bitDirty`.
2. Every `GetNbppTexture` call sets its whole bank flag even when `SNESPPU._dirtyNbpp[tile] == 0`.
3. `GenerateTextures` uploads and calls `Apply()` for every set bank.

`GateTextureUploadsOnActualTileDirty=true` moves each bank flag store inside the corresponding SNES tile-dirty branch. An unchanged visible bank is therefore not re-uploaded. A changed visible tile still updates its CPU buffer and flags the bank; a changed invisible tile retains its SNES dirty byte until it is requested later.

This option defaults to `false`. At startup it verifies all three exact v0.230 IL shapes and requires exactly three transformations. Any mismatch aborts and unpatches the plugin.

## Build

```powershell
dotnet build '<superzsnes-source>\Mods\SuperZSNESRendererTimingProbe\SuperZSNESRendererTimingProbe.csproj' -c Release
dotnet run --project '<superzsnes-source>\Mods\SuperZSNESRendererTimingProbe\Tests\RendererTimingProbe.Tests.csproj' -c Release
dotnet build '<superzsnes-source>\Mods\SuperZSNESRendererTimingProbe\Verifier\RendererTimingProbe.Verifier.csproj' -c Release
& '<superzsnes-source>\Mods\SuperZSNESRendererTimingProbe\Verifier\bin\Release\net472\RendererTimingProbe.Verifier.exe'
```

The last command loads the exact installed v0.230 managed assemblies, verifies the renderer call graph, materializes all three transformed instruction streams, and requires `3/3` matches.

Output DLL:

`bin\Release\net472\SuperZSNESRendererTimingProbe.dll`

## Install and test later

No install or emulator launch is performed by the builder.

1. Stop SuperZSNES normally.
2. Copy the DLL to `<superzsnes>\BepInEx\plugins\SuperZSNESRendererTimingProbe\`.
3. Start once to create `BepInEx\config\dev.local.superzsnes.renderertimingprobe.cfg`, then stop normally.
4. Set `[Probe] Enabled = true`; keep both hot-path instrumentation and the optimization false for the first 60-second baseline.
5. Reproduce the same level and movement. Logs appear under `BepInEx\RendererTimingProbe\renderer-timing-*.jsonl`.
6. If `TileTextureGen.GenerateTextures` is material, perform a separate A/B run with `GateTextureUploadsOnActualTileDirty = true`. Do not enable hot-path timing in the optimization comparison.
7. Compare update rate, `GenerateBackgrounds`, `GenerateTextures`, and visual/VRAM behavior. Exercise static screens, animated tiles, palette animation, level transitions, save/load, pause/single-step, Mode 7, and texture mods before leaving the optimization enabled.

Inclusive child timings should not be added together as if exclusive. In particular, each singular `GenerateBackground` includes `DrawLines`, material lookup, and mesh submission.
