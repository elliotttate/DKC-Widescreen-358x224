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
planes alone can still look like two large cards. Version 0.4 added an optional,
exact-hash-gated native splitter that classifies connected opaque tilemap
components. Neighboring cells remain on the same depth plane whenever opaque
edge pixels touch, including a conservative one-pixel diagonal tolerance.
This is the key safety property: a continuous tree, ground strip, or painted
background cannot be cut merely because it changes palette or tile number.

Classification runs only after relevant tilemap/graphics state changes. The
hot native DrawLines path performs one lookup by `(BG, tilemap VRAM address)`;
there is no managed callback per rendered tile. Large connected scenery stays
on its stock plane by default, while compact disconnected components receive
stable shallow depth bands.

Version 0.6 separates **draw ordering** from **scene geometry**. SNES priority
planes still retain their exact front-to-back order, but their spacing is
compressed to a tiny epsilon by default. This prevents a low/high-priority
change inside one tree or terrain painting from becoming a large physical crack
when the camera rotates. Connected-component/profile offsets remain much larger
and are therefore the intentional 3D object distances.

Version 0.7 fixes a separate stock renderer defect that only becomes obvious in
3D. `PPURenderer.RenderLines` used `i <= 128`, then wrapped the OAM slot with
`& 0x7F`. It therefore rendered the priority-rotation starting sprite twice:
once at order Z 0 and again at order Z 1. Flat rendering hid the overlapping
copy, while a tilted camera exposed a detached duplicate piece of a character.
The exact-hash-gated native patch changes only the terminal comparison constant
from 128 to 127, retaining all 128 OAM entries once and in their original order.

Two earlier automatic grouping experiments were rejected. A managed IL2CPP
mesh walker survived ordinary frames but produced three correlated CoreCLR
access violations near the same scene boundary; that evidence does not imply
that every transition crashes. A native per-tile-number split survived the same
timed opening test but visibly shredded coherent logo art into narrow strips.
Neither implementation is used by v0.4. The old palette splitter is retired.
The connected-component splitter restores both native patch sites on unload.

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

The recommended cohesive mode is:

```ini
[Depth]
CompressPriorityPlanes = true
PriorityPlaneSpacing = 0.01
```

This spacing preserves depth-buffer order without turning priority boundaries
into separate cardboard panels. Set `CompressPriorityPlanes=false` only to use
the old priority-as-geometry experiment; then `Separation` controls the gaps.

Keep the duplicate-pass correction enabled for 3D:

```ini
[SpriteCohesion]
RemoveDuplicateOamPass = true
```

The setting changes no OAM data or game logic. It corrects the renderer's
inclusive loop and restores the original bytes when the plugin unloads.

The detailed split is controlled independently:

```ini
[ConnectedComponents]
Enabled = true
DepthBands = 7
Spacing = 0.08
MinimumTiles = 2
MaximumAutoTiles = 64
RefreshIntervalFrames = 4
```

`DepthBands` and `Spacing` control the automatic shallow offsets.
`MinimumTiles` suppresses single-tile noise. `MaximumAutoTiles` leaves large,
continuously connected scenery anchored to its original priority plane.
`RefreshIntervalFrames` controls a coalesced snapshot cadence. Classification
runs on one background worker, retains only the newest queued snapshot, and
publishes a native table only when the address-to-depth mapping changes.
Automatic bands use the component's minimum tilemap address, so palette or
animation-frame changes do not randomly move an otherwise unchanged object.

After a supported scene is rendered, the plugin writes
`components-current.json` next to `status.json`. It lists every component's
stable ID, BG, cell count, bounds, addresses, and current depth. Optional
per-level overrides live in `profiles/level-XXXX.json`:

```json
{
  "version": 1,
  "componentDepths": {
    "BG1-A1234-0123456789ABCDEF": 0.12,
    "BG2-A5678-FEDCBA9876543210": -0.08
  }
}
```

Equal override values deliberately merge separate components visually. A zero
value pins a component back to its stock priority plane. Overrides are clamped
to -4..4 and reload when the profile file changes.

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
selected gap, component inventory/profile state, compatibility warning, and applied-frame count are in
`BepInEx/plugins/SuperZSNESLayerDepthControllerIL2CPP/status.json`.

## Visual verification and limits

Use a full-window capture, not desktop-coordinate cropping. On this machine,
Windows DPI scaling made `CopyFromScreen` captures show only a quadrant or a
different foreground window. WSLSnapit `take_screenshot` targeting the
`SUPERZSNES` game window is the accepted visual-check path.

This remains an experimental presentation mode. Native-width cinematics can
expose stock tilemap edges because real per-layer 3D requires turning off the
accepted flat CPU framebuffer. The native connected-component splitter has
passed its offline model, patch-window, stub, and rollback verification, but it
still needs repeated, named transition coverage before it can be called stable.
It deliberately does not infer semantic objects inside one fully connected
painted mass; that requires authored masks or reconstructed clean plates.
Use F6 to return to flat stock rendering, or re-enable `PresentFramebuffer` for
the fast, pixel-exact widescreen mode.
