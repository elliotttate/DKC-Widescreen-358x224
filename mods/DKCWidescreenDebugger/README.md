# DKC Widescreen Debugger

A BepInEx 5 plugin built specifically for the managed emulator core in SuperZSNES v0.230. It is intended to turn difficult Donkey Kong Country widescreen failures into reproducible CPU, memory, PPU, and renderer evidence.

It also includes a full stdio MCP server so an LLM can drive the same debugger without operating the in-game overlay. See [`mcp_server/README.md`](mcp_server/README.md) for configuration and the 32-tool surface, including a tool that returns a live PNG or JPEG screenshot as an MCP image.

## Install

The ready-to-use package includes the 32-bit BepInEx build required by `SUPERZSNES.exe`.

1. Close SuperZSNES.
2. Extract the release zip directly into `<superzsnes>` so `winhttp.dll` is beside `SUPERZSNES.exe`.
3. Launch SuperZSNES once. The debugger appears automatically; press **F10** to hide or show it.

The plugin DLL should end up at:

`BepInEx\plugins\DKCWidescreenDebugger\DKCWidescreenDebugger.dll`

Logs and captures are written below:

`BepInEx\plugins\DKCWidescreenDebugger\Sessions\<timestamp>`

## Hotkeys

| Key | Action |
| --- | --- |
| F6 | Pause/resume emulation |
| F7 | Pause and step one emulated frame |
| F8 | Save a complete diagnostic capture |
| F9 | Start/stop the filtered 65C816 instruction trace |
| F10 | Show/hide the debugger |

All keys and safety limits can be changed in `BepInEx\config\dev.local.superzsnes.dkcwidescreendebugger.cfg` after the first run.

## What it adds

- Live frame, scanline, dot, PC, 65C816 registers/flags, SNES background mode, and BG1-BG4 scroll positions.
- Execute breakpoints and read/write watchpoints using 24-bit SNES addresses or ranges.
- Filterable 65C816 instruction traces with SuperZSNES's own disassembly text.
- PPU/I/O register-write tracing, including HDMA-driven writes.
- Typed WRAM watches (`u8`, `s8`, `u16`, `s16`, `u24`, `u32`) with per-frame change history.
- Unknown/exact WRAM searches followed by changed, unchanged, increased, decreased, or exact filters.
- Live memory byte writes while paused.
- Deterministic controller injection for an exact number of emulated frames.
- Live controls for the emulator's widescreen BG, OBJ, Mode 7, and color behavior.
- A one-click DKC baseline matching SuperZSNES's built-in `DKC_Widescreen_358x224` special case: BG 7, OBJ 7, Mode 7 0, color 0.
- BG1-BG4, sprite, and window visibility switches plus scanline, sprite-number, and priority isolation.
- Complete captures containing WRAM, SRAM, VRAM, CGRAM, OAM, I/O registers, CPU state, PPU state, widescreen settings, renderer arrays, and the emulator render textures as PNGs.
- JSONL event history and separate CSV files for CPU, memory read/write, and PPU register traces.
- An authenticated localhost bridge and MCP tools for every control above, including memory searches and live capture requests.

## Address syntax

Addresses are hexadecimal and may be written as `80ABCD`, `$80ABCD`, `0x80ABCD`, or `80:ABCD`. Ranges use a dash and entries use commas:

`80ABCD, 81C000-81C0FF`

Typed WRAM watches use `address:type:name`:

`7E1234:s16:camera_x, 7E1236:s16:camera_y, 7E0042:u8:game_mode`

Multi-byte values are interpreted little-endian, matching the 65C816.

## Suggested DKC workflow

1. Load DKC and apply the DKC baseline under **Live widescreen and layer controls**.
2. Pause at a scene that exposes a bad right or left edge and press F8. This is the known-bad graphics-state bundle.
3. Start an unknown WRAM scan, resume, move horizontally, pause, and filter **Changed**.
4. Resume without moving the camera and filter **Unchanged**. Repeat changed/unchanged passes and use **Increased** or **Decreased** for one travel direction.
5. Add promising addresses as `s16` or `u16` watches. The live PPU BG scroll values help distinguish player coordinates from camera/scroll coordinates.
6. Put a narrow write watchpoint on the winning address. The debugger will pause and record the PC responsible for updating it.
7. Convert that PC to an execute breakpoint, enable a narrow CPU trace filter around its code bank/range, and reproduce the transition.
8. Toggle BG and sprite layers, isolate a sprite number or priority, and constrain rendered scanlines to identify whether an artifact comes from game state, PPU registers/HDMA, or the emulator's widescreen renderer.

## Performance and breakpoint behavior

The hot CPU and read-memory hooks are installed only while their feature is active. Instruction tracing and broad read watchpoints are inherently expensive; use a narrow PC/address range whenever possible. Normal emulation keeps those hooks removed.

SuperZSNES runs a complete emulated frame inside one Unity update. A breakpoint marks the exact instruction/access and sets the emulator's pause flag, but the current frame may finish before the visual pause takes effect. Only the first breakpoint is latched until you resume or step, preventing capture floods.

## Build

The project uses reflection for all `Assembly-CSharp` APIs, so it does not need to redistribute or compile against the game assembly.

```powershell
dotnet build -c Release `
  -p:BepInExRoot='<bepinex>' `
  -p:GameManagedDir='<superzsnes>\SUPERZSNES_Data\Managed'
```

The plugin targets .NET Standard 2.1, which is present in this Unity 6000 Mono build. It was compiled against the official BepInEx 5.4.23.5 x86 package because the game executable is 32-bit.

The localhost bridge uses managed thread-pool dispatch and disposes each request completion event only after both the Unity-thread producer and network waiter finish. Verify the bridge lifecycle without installing or launching SuperZSNES:

```powershell
dotnet run --project .\tests\BridgeHandleLeakTests.csproj -c Release
```

## Compatibility

The hooks are resolved by type and method signature rather than source offsets. This makes the plugin tolerant of method-body changes, including the `Assembly-CSharp.dll.pre-dkc-widescreen` build found beside the game, as long as the named core APIs remain present. Missing optional renderer fields are skipped in captures instead of stopping the plugin.
