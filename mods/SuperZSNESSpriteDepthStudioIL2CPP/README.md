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
   Click any card image to open a maximized pixel inspector. The wheel zooms
   around the pointer, left-drag pans, double-click or `F` fits, `1` selects
   100%, and `F11` toggles borderless full screen.
5. Choose `-12..+12`: negative layers move toward the camera, positive layers move away, and zero restores stock depth.
6. Leave **all matching** clear for an OAM-slot rule, or enable it to apply the layer to matching animated appearances (tile bank, palette, priority, name select, and OBJ size).
7. Scenery starts in **automatic** mode. Uncheck it to author a layer; re-check it
   to remove the override and return to conservative automatic placement.
8. The toolbar's **Foreground ground cutout** controls create a separate front
   path plane. Choose its source BG, edge seed, depth, tiny vertical offset, and
   independent X/Y projection size. The tested Jungle preset uses front Z
   `-4.00`, offset `-0.125` (one SNES pixel lower), and size `5.50 x 1.00` to
   keep the cropped cutout edges outside a tilted view without making the
   ground taller. The `-4.00` depth also keeps it clear of stock priority
   planes that otherwise produce a rectangular depth-test occlusion.

Profiles are saved immediately and hot-reloaded by the emulator. Captures retain
raw 64 KiB VRAM, OAM, scanline CGRAM, PPU registers, BG scrolls, the component
catalog, and a manifest under `Captures/`, so a mod author can return to exact
moments later.

Foreground extraction follows the live BG's natural path boundary; it does not
use a hand-painted rectangular mask. Only connected sand-colour surface pixels
are uploaded, while unrelated opaque rock/scenery below BG1 is made
transparent. Unsupported PPU modes automatically hide the plane rather than
displaying stale art.

For DKC, captures also read the disassembly-documented normal-sprite WRAM
tables (`$0D45` IDs, `$0B19/$0BC1` positions, and pose fields). Matching viewer
groups receive names such as **Donkey Kong**, **Barrel Cannon**, **Swinging
Rope**, or **Zinger**, and the current level ID is shown by name. Anonymous OAM
effects and painted BG scenery retain deterministic technical labels.

These names come from Yoshifanatic1's DKC1 disassembly rather than image
recognition. The disassembly also names thousands of dynamic pose graphics
(for example Donkey Kong animation frames), which can support a later pose-name
catalog. It does not represent an individual tree or leaf cluster as a gameplay
object: DKC paints those into BG tilemaps, so scenery remains named by level,
BG, and stable connected-component ID. Actor-to-OAM labels fail closed unless a
match is geometrically strong; the active nonzero-pose Kong is additionally
matched to the dominant multi-part player group.

## Safety model

- The runtime is SHA-256 gated to the audited 32-bit v0.300 `GameAssembly.dll`.
- It hooks exactly two instructions in `PPURenderer.RenderLines`: the stock sprite scale store and stock Z addition.
- A 128-entry offset table is indexed by the current OAM slot. Every tile cell emitted for that sprite receives the same depth and compensation.
- v0.3 compresses the stock `i/128` OAM-order distance to a configurable tiny
  ordering epsilon. Slot order is preserved, but multi-OAM characters no longer
  pull apart into visible cards under camera rotation.
- Layer Depth Controller v0.7 removes the stock 129th duplicate OAM pass. That
  complementary fix prevents the priority-rotation starting cell from appearing
  a second time when a tilted camera separates its two stock Z submissions.
- Background cards use the Layer Depth Controller's exact stable component IDs
  and its existing native per-tile table; there is no competing segmentation path.
- Background components join only where neighboring opaque edge pixels touch.
  Large landscapes stay whole unless a transparent boundary safely separates them.
- OAM, collision data, palette/priority attributes, emulation state, and ROM bytes are never changed.
- On unload or failure, all offsets become zero, scales become one, and original native bytes are restored.
- `RequireGimmick3D=true` prevents profiles from changing the normal flat renderer.

## Files

- `Exchange/snapshot.*`: latest editor handoff.
- `Exchange/load-state.request`: optional test-control file containing an exact
  save-state path, or `suffix:-last`; it is consumed on the Unity main thread.
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
