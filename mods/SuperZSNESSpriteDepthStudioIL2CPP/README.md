# SuperZSNES Object Depth Studio IL2CPP

This is a v0.300 authoring tool for assigning both whole SNES OAM sprites and
conservatively connected background scenery to custom 3D depth layers. It consists
of a small BepInEx IL2CPP runtime plugin and a separate resizable Windows editor.

## Workflow

1. Run SuperZSNES with the Layer Depth Controller enabled.
2. Start `Studio/SpriteDepthStudio.exe`, or run `launch-studio.ps1` from the installed plugin folder.
3. Press **Capture current frame** in the editor (or F10 in SuperZSNES).
4. Use **All objects**, **Sprites**, or **Background scenery** and scroll through
   the live catalog. Sprite cards preserve natural multi-cell OAM objects; scenery
   cards show visible BG1/BG2/BG3 trees, terrain, sky, bridges, and other connected art.
5. Choose `-12..+12`: negative layers move toward the camera, positive layers move away, and zero restores stock depth.
6. Leave **all matching** clear for an OAM-slot rule, or enable it to apply the layer to matching animated appearances (tile bank, palette, priority, name select, and OBJ size).
7. Scenery starts in **automatic** mode. Uncheck it to author a layer; re-check it
   to remove the override and return to conservative automatic placement.

Profiles are saved immediately and hot-reloaded by the emulator. Captures retain
raw 64 KiB VRAM, OAM, scanline CGRAM, PPU registers, BG scrolls, the component
catalog, and a manifest under `Captures/`, so a mod author can return to exact
moments later.

## Safety model

- The runtime is SHA-256 gated to the audited 32-bit v0.300 `GameAssembly.dll`.
- It hooks exactly two instructions in `PPURenderer.RenderLines`: the stock sprite scale store and stock Z addition.
- A 128-entry offset table is indexed by the current OAM slot. Every tile cell emitted for that sprite receives the same depth and compensation.
- Background cards use the Layer Depth Controller's exact stable component IDs
  and its existing native per-tile table; there is no competing segmentation path.
- Background components join only where neighboring opaque edge pixels touch.
  Large landscapes stay whole unless a transparent boundary safely separates them.
- OAM, collision data, palette/priority attributes, emulation state, and ROM bytes are never changed.
- On unload or failure, all offsets become zero, scales become one, and original native bytes are restored.
- `RequireGimmick3D=true` prevents profiles from changing the normal flat renderer.

## Files

- `Exchange/snapshot.*`: latest editor handoff.
- `Profiles/<rom>-<sha>.json`: reusable authored rules.
- `../SuperZSNESLayerDepthControllerIL2CPP/profiles/level-XXXX.json`: authored
  background-component depth overrides.
- `Captures/<timestamp>/`: preserved source snapshots.
- `status.json`: runtime state and counters.
- `Tools/RenderLines-v0300.asm.txt`: exact audited native body used to select the two insertion points.

## Build and verify

```powershell
& .\verify.ps1 -BepInExIl2CppRoot '<SuperZSNES-v0.300-root>'
```

The tests cover all 128 OAM entries, 2bpp/4bpp background reconstruction,
BGR555 palettes, small/large OBJ geometry, slot and appearance rule precedence,
visible component cropping, native stub register preservation, the exact
GameAssembly hash, and both hook byte windows.

## Known scope

The scenery view shows the visible portion of each whole connected component,
not semantic labels inferred by image recognition. A tree joined to ground by opaque
pixels intentionally remains one safe object. Animated screens may produce many
small cards as hardware tile edges connect and disconnect. Mid-frame OAM or OBJSEL
writes are counted in the manifest so unusual scenes remain visible to the author.
