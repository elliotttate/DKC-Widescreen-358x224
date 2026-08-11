# DKC Tilemap Inspector

An isolated BepInEx 5 plugin for SuperZSNES v0.230 that reconstructs BG1/BG2 directly from SNES VRAM, CGRAM, tilemap registers, and scroll state. It is intended to answer a narrow but important question: is a widescreen discontinuity already present in the emulated PPU data, or is it introduced later by SuperZSNES composition/window capture?

This project does not modify or depend on `DKCWidescreenDebugger` or the DKC widescreen support plugin.

## Outputs

Press **F11** in a loaded game or invoke the loopback `capture` command. Each capture is written below:

`BepInEx\plugins\DKCTilemapInspector\Captures\capture-f########-<timestamp>`

Each folder contains:

- `capture.json`: scroll positions, BGSC layout, VRAM/CHR bases, mode, tile size, previous-frame deltas, and candidate seam/stale columns.
- `bg1-columns.csv` / `bg2-columns.csv`: one row for every 8-pixel column across the full renderer sample (46 columns with the default 7-tile margins).
- `bg1-viewport-raw.png` / `bg2-viewport-raw.png`: background pixels reconstructed directly from SNES tile data and palette RAM.
- `bg*-viewport-annotated.png`: the same data with yellow 358-pixel target bounds, cyan native 256-pixel bounds, and dashed red high-seam candidates.
- `bg*-tilemap.png`: a color-keyed overview of every tilemap entry. Red-heavy cells have the SNES priority bit set.
- Raw `vram.bin`, `cgram.bin`, and `io-registers.bin` snapshots.

Transparent background pixels use a dark checkerboard in the reconstructed PNGs. The heuristics intentionally label findings as candidates: natural artwork edges, transparency, and DKC's look-ahead buffer can resemble stale data.

## Build and install

No running emulator is required to build:

```powershell
& '<superzsnes-source>\Mods\DKCTilemapInspector\build.ps1'
```

Close SuperZSNES before installing or updating the DLL, then run:

```powershell
& '<superzsnes-source>\Mods\DKCTilemapInspector\install-plugin.ps1'
```

The installed DLL is:

`<superzsnes>\BepInEx\plugins\DKCTilemapInspector\DKCTilemapInspector.dll`

Configuration is generated on first launch at:

`<superzsnes>\BepInEx\config\dev.local.superzsnes.dkctilemapinspector.cfg`

Useful settings include `TargetWideWidth = 358`, `RendererExtraTilesPerSide = 7`, the seam threshold, and an optional automatic capture interval. Automatic capture is off by default.

## Programmatic bridge

The plugin opens an authenticated loopback-only bridge, normally on port 17817. Runtime connection data and the random token are written to:

`<superzsnes>\BepInEx\plugins\DKCTilemapInspector\bridge.json`

The protocol matches the companion DKC debugger's simple tab/base64 bridge so an MCP adapter can call it without screen automation. Commands are `status`, `capture`, and `latest`. A ready PowerShell client is included:

```powershell
# Check attachment and current frame
& '.\Invoke-TilemapInspector.ps1' status

# Capture both backgrounds
& '.\Invoke-TilemapInspector.ps1' capture -Layers '1,2' -Reason 'bad-right-edge'

# Return the latest capture path
& '.\Invoke-TilemapInspector.ps1' latest
```

Bridge requests are queued onto Unity's main thread before accessing emulator or texture state.
Version 0.1.1 also bounds simultaneous bridge clients to eight, dispatches them through the CLR thread pool instead of creating an OS thread per connection, and uses monitor-based request completion rather than allocating a `ManualResetEventSlim` for every command. This keeps repeated MCP polling from accumulating thread or wait-handle objects.

An emulator-free stress verifier is included. It exercises 1,232 loopback connections (successful, rejected-token, and malformed requests), checks that workers drain, and rejects material thread/handle growth:

```powershell
dotnet run --project '.\Tests\BridgeStress.csproj' -c Release
```

## Recommended comparison

1. Direct-load the same Jungle Hijinxs state with all widescreen values disabled; capture BG1/BG2.
2. Enable the 7-tile renderer extension without changing the ROM; capture again after one rendered frame.
3. Run the patched ROM with the same state and capture a third time.
4. Compare raw viewport PNGs and `mapRowsChangedSincePrevious` in the CSV files.

If a seam is present in the raw reconstructed image, the ROM/PPU tilemap content or scroll selection is wrong. If the reconstructed background is continuous while the composed emulator window is broken, the fault is downstream in SuperZSNES's expanded background composition/cropping path.
