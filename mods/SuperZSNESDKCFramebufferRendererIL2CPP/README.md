# SuperZSNES v0.300 IL2CPP DKC framebuffer renderer

This is the IL2CPP/BepInEx 6 port of the accepted v0.230 CPU framebuffer renderer. It replaces only the broken legacy wide compositor; it does not include the old performance, automation, tracing, or inspection plugins.

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

Install into a closed disposable copy and explicitly arm presentation:

```powershell
& .\install-plugin.ps1 -GameRoot '<superzsnes-v0300-dev>' -EnablePresentation
```
