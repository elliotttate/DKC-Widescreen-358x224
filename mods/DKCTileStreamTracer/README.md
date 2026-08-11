# DKC Tile Stream Tracer

Standalone BepInEx 5 plugin for targeted tracing of Donkey Kong Country's horizontal tile-stream routines in SuperZSNES. It does not modify `Assembly-CSharp.dll` and does not depend on the widescreen or general debugger plugins.

## Captured data

When armed, the plugin intercepts `CPU65c816.ExecuteNextInstruction` and records instructions within the configured byte window around:

- `$818705`, `$818711`, `$81883F`, `$818857`
- `$8188A8`, `$8188BD`, `$818DFA`, `$818E06`

Each PC row contains CPU A/X/Y/S/D/DB/PB/flags/opcode, frame/scanline/dot/cycles, WRAM words `$088B`, `$08A3`, `$0A75`, `$1A5B`, `$1B23`, `$1B25`, current VMAIN/VMADD, remapped VRAM destination and a 16-byte VRAM preview, active DMA channel/mask, and all non-empty DMA channel descriptors.

The bus trace captures writes to `$2115-$2119`, `$420B-$420C`, and DMA channel registers `$4300-$437A`. This makes the VRAM address visible immediately before `$2118/$2119` performs its increment.

## Build and install

```powershell
& '<superzsnes-source>\Mods\DKCTileStreamTracer\build.ps1'
& '<superzsnes-source>\Mods\DKCTileStreamTracer\install-plugin.ps1'
```

Restart SuperZSNES after installing or replacing the DLL. The default output location is:

`<superzsnes>\BepInEx\plugins\DKCTileStreamTracer\Traces\<timestamp>`

## Arm and control

- Press `F11` to arm/disarm.
- Or write one command to `BepInEx\plugins\DKCTileStreamTracer\control\command.txt`:
  - `arm`
  - `disarm`
  - `toggle`
  - `status`
  - `mark reached-right-edge`
- Read `BepInEx\plugins\DKCTileStreamTracer\control\status.json` for machine-readable status and the active session directory.

PowerShell example:

```powershell
& '<superzsnes-source>\Mods\DKCTileStreamTracer\control-tracer.ps1' arm
& '<superzsnes-source>\Mods\DKCTileStreamTracer\control-tracer.ps1' mark -Message 'reached-right-edge'
& '<superzsnes-source>\Mods\DKCTileStreamTracer\control-tracer.ps1' disarm
```

The command file is consumed and deleted by the plugin. Wait roughly 100 ms before reading the updated status.

## Outputs

- `events.jsonl`: unified machine-readable stream (`pc`, `bus`, and session/mark events).
- `pc-trace.csv`: concise instruction snapshots near the target PCs.
- `ppu-dma.csv`: VRAM-address/data and DMA-register bus activity.

Relevant settings are generated in `BepInEx\config\dev.local.superzsnes.dkctilestreamtracer.cfg`. Capture is off by default, the PC window is ±12 bytes, and the safety limit is 200,000 combined rows per session.
