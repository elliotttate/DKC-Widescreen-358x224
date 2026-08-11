# SuperZSNES DKC Width Margin Override

This is an isolated, disabled-by-default BepInEx experiment. It does not alter the ROM, emulator assembly, or settings file. Its default configuration applies no Harmony patch. When enabled, `ApplyOverride=false` is dry-run only.

## Width audit

For a BG margin `w`, stock `DrawLines` loops over `33 + 2w` columns. That raw count is not the visible width. The method computes clamp boundaries:

```text
xmin = -16 - w
xmax = +16 + w
```

Each world unit is one 8-pixel tile, so the accepted BG width is `(xmax-xmin)*8 = 256 + 16w`. One of the raw columns is the scrolling phase/edge guard and is clipped between the two sides.

| Margin | Raw columns | Raw span | Clamped visible span | Guard vs 358 |
|---:|---:|---:|---:|---:|
| 7 | 47 | 376 px | 368 px | +5 px/side |
| 6 | 45 | 360 px | 352 px | -3 px/side |

Therefore six is not safe for a 358-pixel viewport even in the best-case centered, point-filtered model. Bilinear sampling needs an additional neighboring-texel footprint. The DKC special case also moves all three PPU cameras by `+2.2` world units; any uncompensated shift consumes one edge's guard rather than making six safe. Overscan changes the vertical line count, not the horizontal clamp, so it cannot repair the deficit.

OBJ uses an independent but equivalent envelope. With margin seven, `xClampSize = 256 + 2*(7*8) = 368`; the captured renderer state confirms 368. Margin six would shrink sprite clipping to 352 pixels and can cull or partially clip sprites in the outer three pixels per side of the 358 viewport. OBJ should remain seven.

Runtime evidence used by the verifier:

- `frame-main.png` is 1592x896, the stock 4x `398x224` main render target;
- the 1707x1067 16:10 reference screenshot corresponds to approximately `358.36x224` source pixels;
- the same capture records BG=7, OBJ=7, `xClampSize=368`, and `numLines=224`.

## Configuration

```ini
[Experiment]
Enabled = false
ApplyOverride = false
CandidateBGMargin = 6

[Safety]
ExpectedCurrentBGMargin = 7
FilenameContains = DKC_Widescreen_358x224
```

When armed, the prefix only observes `PPURenderer.GenerateBackground` calls whose loaded filename passes the DKC gate and whose current per-layer value is exactly seven. In apply mode it temporarily changes only the active BG list element during that single layer builder, then restores it in a Harmony finalizer. OBJ is never changed. Status is written at low frequency to `BepInEx/plugins/SuperZSNESDKCWidthMarginOverride/status.json`.

This candidate is provided only to make a controlled negative A/B possible. The geometry audit predicts visible edge loss; do not enable apply mode for normal play.
