# SuperZSNES DKC Exact Background-State Cache

This is a conservative, DKC-only, whole-background cache experiment for SuperZSNES v0.230. It is disabled by default and also defaults to diagnostic dry-run when enabled. It never edits the game assembly or ROM.

The plugin does not skip `GenerateBackgrounds`. That method still refreshes composition, windows, fixed-color lines, objects/OAM, global mosaic state, textures, and render targets. One prefix computes a single exact background decision for the frame. Every invocation of the private `GenerateBackground` layer method consults that same decision, so all active non-Mode7 background layers regenerate or all remain on their prior meshes. Individual layers are never cached because the 256/64/16/4/1 mesh pools are shared across layers and Mode 7.

## Safe operating modes

```ini
[Cache]
Enabled = false
DryRun = true
```

- `Enabled=false`: no Harmony patch is applied.
- `Enabled=true, DryRun=true`: calculate exact predicted hits and write counters, but allow every background call.
- `Enabled=true, DryRun=false`: skip all background layer calls on an exact hit. Use only after dry-run shows useful hits and controlled visual testing is available.

Both settings require restart.

## Exact key

The cache compares values exactly; it does not trust a collision-prone hash. The baseline is the input snapshot taken at the `GenerateBackgrounds` prefix and is committed only after the method completes.

The key includes:

- all 65,536 bytes of VRAM;
- current CGRAM and start-of-frame CGRAM;
- all 64 start-of-frame PPU registers and the current BG/window/color IO registers (`$2100`, `$2105-$2114`, `$211A-$2120`, and `$2123-$2133`);
- exact line/address/value/order for the filtered PPU scanline-change stream and exact counts/fields of every CGRAM scanline-change record;
- all eight start-of-frame BG scroll coordinates, fixed color and PPU scroll latches;
- BG and tile/palette dirty/decode arrays that influence batching or texture refresh;
- renderer line count, debug-line limit, BG/window disable flags, viewport ratios and screen dimensions;
- all per-game enhancement, aspect and widescreen settings;
- current/global scene identity and a deep copy of scene detection, widescreen, z-position/z-scale, window, lighting and enhancement configuration.

The v0.1.1 PPU stream filter is derived from the actual v0.230 `GenerateBackground` IL switch. It preserves, in original order, every change to the 33 registers that method reads: `$2100`, `$2105-$2114`, `$211A-$2120`, `$2123-$2125`, `$212A-$212B`, `$212E-$212F`, and `$2130-$2131`. It also preserves `$212C-$212D`, which `GenerateBackgrounds` uses to coordinate layer activation. A register's retained record is compared by exact address, scanline, value, and relative order; the stream is not sorted or collapsed. Mode 7 is independently rejected from the original unfiltered stream.

Other PPU line records (notably OAM/OBJ `$2101-$2104`, VRAM/CGRAM ports, and outer-composition-only records) do not invalidate retained BG meshes because `GenerateBackgrounds`, object generation, and composition still execute. The full VRAM comparison remains deliberate: this prototype makes no attempt to classify OBJ-only VRAM. Any sprite CHR upload anywhere in VRAM still causes a miss, including addresses that might appear disjoint in the current frame.

## Fail-closed rules

No hit is allowed when:

- the loaded filename is not `DKC_Widescreen_358x224`;
- Mode 7 is active at frame start or selected by any scanline PPU change;
- no BG layer is active;
- an array/count/runtime reference has an unexpected shape;
- mod data contains dynamic fonts, replacement materials, tile-UV replacements or 3D tile replacements.

Those enhanced paths maintain extra texture/material state that is not represented by the plain SNES key. Accepting fewer hits is preferable to caching stale visuals.

`PPURenderer.Init`, `ClearCache`, `ResetRenderer`, `UpdateModData`, and `SNESPPU.SetState` invalidate the baseline. An `UpdateModData` that happens after the frame prefix also cancels a provisional hit before any layer call, covering scene detection changes. ROM resets, save-state loads, history and rewind restoration therefore fail closed even when their restored bytes happen to match an earlier baseline.

## Counters

`status.json` is written under the installed plugin directory at startup, shutdown, and every 300 evaluated frames. It contains:

- eligible and total frames;
- predicted exact hits;
- actual skipped frames and layer calls;
- generated frames and allowed layer calls;
- invalidations;
- categorized miss reasons, including scroll/config, register, scanline stream, CGRAM, full VRAM and dirty-state changes.

## Expected benefit and limitations

The expensive layer builders are skipped only while the whole BG state is frozen. Walking roughly 80 KiB of exact state is much cheaper than rebuilding hundreds of tiles, but moving cameras or VRAM animation should correctly produce misses. Full-VRAM comparison may also reject otherwise safe hits when DKC uploads sprite graphics; dry-run is intended to measure that rate before enabling skips.

The cache retains prior background MeshRenderer state while objects and composition continue updating. Test cave/jungle levels, transitions, fades, animated tiles, pause/unpause, save-state load, widescreen/native width changes, and any level using scanline scroll/window effects. Reject immediately for one-frame stale layers, wrong activation, palette delay, seams, or a miss-rate too high to offset comparison cost.

Run `verify.ps1` to build and verify the exact v0.230 call shape, all 33 `GenerateBackground` register-switch cases from the game IL, the two caller activation cases, filtered-stream line/value/order semantics, raw-stream Mode 7 rejection, coordination patch ABI, invalidators, compiled fail-closed gates, and full-VRAM comparison probes. The script reports both installed filenames and hash-matching installed copies; this offline v0.1.1 build is not installed by the script.
