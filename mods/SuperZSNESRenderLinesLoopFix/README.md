# SuperZSNES RenderLines Loop Fix

This is an isolated, disabled-by-default BepInEx prototype for the exact SuperZSNES v0.230 `PPURenderer.RenderLines` IL. It changes no file in the emulator installation and does nothing unless explicitly enabled.

## Finding

The stock method decodes the OAM start entry and then runs:

```csharp
for (int i = 0; i <= 128; i++) {
    // complete sprite/tile/material/mesh body
    num = (num + 1) & 0x7f;
}
```

There are 128 OAM entries. For every priority-rotation start `s`, the stock sequence is `s, s+1, ..., s+127, s` modulo 128. Pass 128 is not a sentinel or flush: it runs the complete body on the starting descriptor again. Its decoded X, Y, tile, attributes, size, material, masks, and main/sub camera layer are identical to pass 0. The only computed difference is the depth fraction: `i / 128f` is 0 on the first copy and 1 on the second.

`RenderLines` does not make separate main- and sub-screen scans. Each generated tile mesh is assigned once to layer 7 (main), 8 (sub), or 11 (both), so the duplicate cannot be a required bridge between camera passes. The stock sprite shader is tagged Opaque; the first copy remains at the front of its rotated same-priority depth run, while the second is the same geometry one Z unit behind it.

The patch changes only the terminal constant from 128 to 127, retaining the inclusive `ble` and visiting every OAM entry exactly once in the same rotated order.

## Configuration

```ini
[Optimization]
Enabled = false
```

Changing the setting requires an emulator restart because this is a Harmony transpiler.

## Offline verification

Run `verify.ps1`. It verifies the installed game assembly without changing it:

- exact method RVA `0x43C88`, code size `0xC58`, and terminal `ldloc.2; 1; add; stloc.2; ldloc.2; 128; ble IL_001F; ret`;
- exactly one transformed instruction, `128 -> 127`, with unchanged instruction count and branch;
- all 128 priority-rotation starts retain their exact first 128 entry order and become one visit per entry;
- synthetic OAM proves stock passes 0 and 128 decode the same descriptor;
- main/sub layer mapping remains 7/8/11.

## Expected impact and risks

The deterministic saving is one of 129 outer OAM-entry passes per `RenderLines` call (0.775%). If the starting entry is not visible, that is mostly decode, size, and clipping work. If it is visible, the stock duplicate can also repeat 1 to 64 8x8 tile texture/material/mesh submissions, depending on the active OBJ size mode and clipping. A typical frame should therefore improve by much less than 1% overall; this is a cleanup, not a likely cure for a multi-hertz cadence deficit.

The principal visual risk is a custom/replacement sprite shader that disables ordinary opaque depth behavior or otherwise depends on the redundant rear copy. Controlled QA should cover rotated OAM starts, same-priority overlapping sprites, main/sub color math and windows, mid-frame OAM/forced-blank segments, native and widescreen modes, all OBJ size pairs, and any texture-replacement shader. Keep the switch off until that QA passes.
