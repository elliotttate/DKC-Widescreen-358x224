# SuperZSNES v0.300 IL2CPP DKC framebuffer renderer

This is the IL2CPP/BepInEx 6 port of the accepted v0.230 CPU framebuffer renderer. It replaces only the broken legacy wide compositor; it does not include the old performance, automation, tracing, or inspection plugins. The same DLL auto-detects verified 358x224 and optional 398x224 ROM profiles.

The supported runtime is the 32-bit Windows IL2CPP build of SuperZSNES v0.300 with BepInEx `Unity.IL2CPP-win-x86` build `6.0.0-be.783+c58c42d`. That build is intentionally pinned because its Cpp2IL version accepts this game's metadata version 39.

Install the pinned BepInEx package into a clean, closed v0.300 copy:

```powershell
& .\install-bepinex-v0300.ps1 `
  -GameRoot '<superzsnes-v0300>' `
  -PackagePath 'path\to\BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.783+c58c42d.zip'
```

The installer verifies the exact v0.300 executable, `GameAssembly.dll`, and BepInEx archive hashes and refuses to overwrite an existing installation.

The plugin is disabled by default. Enable `[Renderer] Enabled=true` and `PresentFramebuffer=true` only in a disposable copy until the visual regression set passes. Unsupported frames fail closed to the stock renderer.

Build:

```powershell
& .\build.ps1 -BepInExIl2CppRoot '<superzsnes-v0300-dev>'
```

The project links the proven rasterizer/controller source from the Mono plugin and compiles its IL2CPP-specific array adapters under the `IL2CPP` symbol. Create `capture.request` in the plugin status directory to capture the next supported frame.

## Fallback-burst telemetry (v0.1.1)

Every unsupported presentation frame runs the stock Unity tile renderer. v0.1.1
records each fail-closed reason, its frame count and longest consecutive run, plus
the average and maximum time spent in the actual stock `GenerateBackgrounds`
call. The fields are written to `status.json` as `fallbackReasons`,
`fallbackRate`, `fallbackRendererAverageMs`, `fallbackRendererMaxMs`, and
`maxFallbackStreak`.

Earlier builds synchronously rewrote `status.json` on every fallback frame.
v0.1.1 writes only at the start of a burst, every 120 fallback frames, and when
the burst ends. This keeps the measurement path from adding a disk-I/O hitch to
the condition being measured.

`RetainedBackgrounds=true` is recommended. In a matched pair of disposable
v0.300 startup runs, it reduced the background stage from 2.5847 ms to
1.4862 ms (42.5%) and the complete rasterizer from 7.7664 ms to 6.9705 ms
(10.3%), with a 77.41% per-layer cache-hit rate. This cache is part of this
renderer and is independent of the old v0.230 Mono performance plugins.

## Slow-event attribution and partial raster rows (v0.1.3)

v0.1.2 added bounded diagnostics for supported renders at or above 8 ms. The
stage breakdown identified BG2's line-81 horizontal-scroll effect as a
four-frame recurring full-plane rebuild. v0.1.3 keeps the exact retained state
but refreshes only rows whose scroll changed when relevant VRAM is byte-equal
and BGSC, BGMODE, and CHR base are unchanged. Every other case still performs
the full build.

Two 60-second trials per side measured average background preparation at
0.9041 ms before and 0.3265 ms after (-63.9%), with total framebuffer work at
4.3574 versus 3.6507 ms (-16.2%). The verifier compares the partial plane with
a clean full build pixel-for-pixel and confirms a relevant VRAM write rejects
the fast path. `status.json` reports `rasterPartialRebuilds`,
`rasterPartialRows`, and bounded `slowRenderEvents` evidence.

v0.1.4 adds the locked 27-track Restoration MSU-1 ROM hash to the canonical
allowlist. Rendering behavior is unchanged; the additional source-built ROM
differs only in its music hooks and uses the same widescreen graphics patch.

v0.1.5 detects DKC's exact native-width opening-screen tilemap/character-bank
layout and paints the 51-pixel extensions black. This prevents wrapped intro
art while leaving the native center, HDMA, sprites, fades, and all gameplay
widescreen rendering unchanged.

v0.1.6 adds the following file-select screen's separate exact three-map and
character-bank layout to the same native-width treatment.

v0.1.7 adds both source-verified title-splash layouts. A different character
bank keeps the similar game-over screen outside this rule.

v0.1.9 also masks only the extensions during the short Mode 9 level-loader
state where DKC's camera bounds are still `$0000/$0000`. Full widescreen
returns immediately when the level installs its real bounds.

Install into a closed disposable copy and explicitly arm presentation:

```powershell
& .\install-plugin.ps1 -GameRoot '<superzsnes-v0300-dev>' -EnablePresentation
```
