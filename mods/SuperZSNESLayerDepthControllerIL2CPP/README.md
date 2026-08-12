# SuperZSNES Layer Depth Controller IL2CPP

This default-off BepInEx 6 plugin exposes SuperZSNES v0.300's existing hidden
`Gimmick3D` renderer and makes its layer-distance data effective. It does not
modify a ROM or `GameAssembly.dll` on disk.

## What was found

`PPURenderer.UpdateCameras()` has a complete perspective path selected by
`MainMenuManager.GFXModes.Gimmick3D`: both main/sub cameras become perspective,
mouse drag rotates them, and the mouse wheel changes distance. The scene editor
also stores 13 adjacent `zPositions` gaps, a neutral boundary, and 14 scales.

`SetupZPositions()` calculates those values, then unconditionally overwrites
all 13 plane positions with `13-index` and every scale with `1`. That final loop
is why the editor's distance controls have no effect. This plugin patches after
the method and reapplies a validated profile to the actual renderer arrays.

DKC commonly places most scenery in only two SNES BG tilemaps, so priority
planes alone can still look like two large cards. Version 0.3 has an optional,
exact-hash-gated native splitter that places the eight SNES palette groups
within each BG/priority plane on shallow sublayers. Palette identity is authored
in every tilemap entry, so neighboring tiles that form one colored region stay
together. Tile number and flip bits do not affect the group.

Two earlier automatic grouping experiments were rejected. A managed IL2CPP
mesh walker survived ordinary frames but produced three correlated CoreCLR
access violations near the same scene boundary; that evidence does not imply
that every transition crashes. A native per-tile-number split survived the same
timed opening test but visibly shredded coherent logo art into narrow strips.
Neither implementation is used by v0.3. The palette splitter has no managed
per-tile callbacks and restores both native patch sites on unload.

`PerspectiveCompensation=true` scales each plane according to its camera
distance. At zero pitch/yaw all cards remain aligned like the original flat
image; rotating the camera reveals their distance. This avoids the large black
cracks produced by merely moving equally sized cards along Z.

## Plane order

The renderer has a backdrop plus 13 SNES priority planes. Their important DKC
Mode 1 assignments are:

| Plane | Typical assignment |
|---:|---|
| P1 | BG3 low |
| P2 | OBJ priority 0 |
| P4 | BG3 high |
| P5 | OBJ priority 1 |
| P6 | BG2 low |
| P7 | BG1 low |
| P8 | OBJ priority 2 |
| P9 | BG2 high |
| P10 | BG1 high |
| P11 | OBJ priority 3 |
| P12 | BG3 high when Mode 1 BG3-priority is set |

Unused planes remain configurable because other SNES modes assign them.
`PlaneGaps` contains backdrop->P0 followed by P0->P1 through P11->P12.
`NeutralBoundary` anchors one boundary at Z=0, and `Separation` multiplies all
gaps. `PlaneScales` contains backdrop followed by P0..P12; leave all values at
1 and keep perspective compensation enabled for an aligned head-on view.

The detailed split is controlled independently:

```ini
[DetailSplit]
BackgroundPaletteSublayers = true
BG1PaletteOffsets = -0.03,-0.03,-0.01,-0.01,0.01,0.01,0.03,0.03
BG2PaletteOffsets = -0.03,-0.03,-0.01,-0.01,0.01,0.01,0.03,0.03
BG3PaletteOffsets = -0.03,-0.03,-0.01,-0.01,0.01,0.01,0.03,0.03
BG4PaletteOffsets = 0,0,0,0,0,0,0,0
```

Each row maps palette numbers 0..7 directly to world-space Z offsets within
that BG's existing priority plane. Equal values deliberately collapse related
palettes onto the same sublayer; larger differences pull them farther apart.
Set every value to zero to disable the secondary split while retaining the SNES
priority planes. Palette grouping is deterministic but is not object
recognition: two unrelated objects using the same BG and palette stay on the
same sublayer, while one object deliberately using multiple palettes may split.
Authored per-scene depth maps would be required to identify concepts such as
"tree canopy" or "ground" explicitly.

## Widescreen renderer compatibility

The accepted DKC framebuffer renderer flattens BG/OBJ/color math into one final
texture, so individual layer depth cannot exist while
`PresentFramebuffer=true`. Keep that plugin enabled for its automatic 358/398
stock fallback margins, but set:

```ini
[Renderer]
PresentFramebuffer = false
ShadowRenderInterval = 0
```

The install script can make exactly those two changes with
`-UseWithDkcFramebufferRenderer`. This switches presentation to SuperZSNES's
stock per-layer mesh path, which is necessary for real 3D and may cost more CPU
than the optimized framebuffer. Turning presentation back on restores the
fast, pixel-exact flat renderer.

## Build and install

```powershell
& .\build.ps1 -BepInExIl2CppRoot 'C:\path\to\SuperZSNES_v0.300'
& .\install-plugin.ps1 `
  -GameRoot 'C:\path\to\SuperZSNES_v0.300' `
  -Enable3D `
  -UseWithDkcFramebufferRenderer
```

Both commands require a closed emulator. The plugin itself defaults disabled.

## Live controls

- `F6`: toggle 3D.
- `Ctrl+PageUp/PageDown`: select one of the 13 gaps.
- `Ctrl+=` / `Ctrl+-`: increase/decrease the selected gap.
- `Ctrl+Backspace`: reset camera pitch/yaw/zoom to config values.
- Mouse drag / mouse wheel: built-in SuperZSNES perspective rotate/zoom.

Changes made by the gap hotkeys are saved to the BepInEx config. Current state,
selected gap, profile, compatibility warning, and applied-frame count are in
`BepInEx/plugins/SuperZSNESLayerDepthControllerIL2CPP/status.json`.

## Visual verification and limits

Use a full-window capture, not desktop-coordinate cropping. On this machine,
Windows DPI scaling made `CopyFromScreen` captures show only a quadrant or a
different foreground window. WSLSnapit `take_screenshot` targeting the
`SUPERZSNES` game window is the accepted visual-check path.

This remains an experimental presentation mode. Native-width cinematics can
expose stock tilemap edges because real per-layer 3D requires turning off the
accepted flat CPU framebuffer. The native palette splitter has passed its
offline patch/rollback verification and an initial timed opening run, but it
still needs repeated, named transition coverage before it can be called stable.
Use F6 to return to flat stock rendering, or re-enable `PresentFramebuffer` for
the fast, pixel-exact widescreen mode.
