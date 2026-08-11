# SuperZSNES Indexed Framebuffer Presentation Prototype

This is an isolated, runtime-only BepInEx presentation skeleton for SuperZSNES v0.230. It does not contain a SNES CPU renderer yet. It defines a guarded API through which a separate renderer can submit one final-composed indexed framebuffer, then bypasses legacy `PPURenderer.GenerateBackgrounds` only when that provider accepts the current DKC frame.

It is disabled by default, defaults to dry-run when enabled, does not edit `Assembly-CSharp.dll`, and was not installed by the build or verifier.

## Why this presentation point

The exact v0.230 chain is:

1. `PPURenderer.GenerateBackgrounds` builds BG, OBJ, window, fixed-color and Mode 7 meshes.
2. separate main/sub/window cameras render those meshes into private ARGB32 render textures;
3. `MainScreenBlit.OnRenderImage` combines the main/sub textures with its `blitMaterial` into `transferRenderTexture`;
4. the same method applies aspect ratio, scanline/pixel filtering and the final screen transfer through its private `_transferMaterialUsed`.

A final RGBA CPU framebuffer cannot safely enter step 3: the SNES composite material expects SuperZSNES-specific main/sub/window semantics rather than ordinary final color. The safe prototype hook therefore replaces steps 1-3 for accepted frames and reuses step 4 exactly. Unsupported frames return to the complete stock chain.

No suitable guaranteed-resident indexed-palette shader exists in the v0.230 managed API. `Shader.Find` is unsafe in a player build because unreferenced shaders may be stripped. The prototype instead performs deterministic CPU palette expansion and uses only Unity APIs proven present in the shipped assemblies:

- persistent `Texture2D(..., TextureFormat.RGBA32, false)`;
- `Texture2D.LoadRawTextureData(byte[])` and `Apply(false, false)`;
- persistent `RenderTexture(..., RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)`;
- `Graphics.Blit(Texture, RenderTexture)`;
- the existing `_transferMaterialUsed` with `Graphics.Blit(Texture, RenderTexture, Material)`.

The upload texture and presentation render target are recreated only when dimensions change. Normal frames reuse both resources and the RGBA staging array.

## Configuration

```ini
[Prototype]
Enabled = false
DryRun = true

[Framebuffer]
Width = 398
Height = 224
```

- `Enabled=false`: no Harmony patches are applied.
- `Enabled=true, DryRun=true`: a registered provider is evaluated and accepted frames are uploaded, but the stock mesh and screen paths still execute.
- `Enabled=true, DryRun=false`: accepted frames skip all of `GenerateBackgrounds` and are substituted in `MainScreenBlit.OnRenderImage`.

The stock transfer shader uses a hard-coded 398x224 pixel grid, so 398x224 is the conservative default. A 358x224 DKC image can be centered in that canvas with 20 pixels on each side. Other configured dimensions are experimental because the stock second-stage pixel grid remains 398x224.

## Provider API

A separate BepInEx CPU renderer references this DLL, implements `IIndexedFramebufferSource`, and registers once:

```csharp
public bool TryRenderFrame(
    IndexedFramebufferRequest request,
    IndexedFramebuffer framebuffer,
    out bool rowsAreTopDown,
    out string rejectionReason)
{
    rowsAreTopDown = true;
    rejectionReason = null;
    // Write exactly Width*Height palette indices.
    RenderPpu(request.Ppu, framebuffer.Indices);
    // Write all 256 RGBA entries, including alpha.
    BuildPalette(request.Ppu, framebuffer.Palette);
    return true;
}
```

Call `FramebufferPresentationApi.Register(source)` after both plugins load and `Unregister(source)` on shutdown. Registration is single-owner and idempotent for the same instance. The callback runs synchronously on Unity's main thread. Returning `false` is the normal way to request the stock renderer for a frame.

The provider must produce final-composed color: BG priority, OBJ, windows, color math, brightness, mosaic and any desired widescreen borders are its responsibility. Palette index zero has no special meaning to this uploader. Top-down rows are vertically reversed during expansion because Unity raw texture row zero is the bottom row.

## Fail-closed behavior

The plugin refuses substitution when:

- the loaded filename is not `DKC_Widescreen_358x224`;
- no provider is registered or the provider rejects the frame;
- the PPU state shape or presentation objects differ from v0.230;
- Mode 7 is active initially or selected by a scanline write;
- the stock `_UIFade` composite effect is active;
- the provider, uploader or presentation step throws.

`PPURenderer.Init`, `PPURenderer.ResetRenderer`, and `SNESPPU.SetState` invalidate a ready frame. This covers renderer recreation and save-state/history restoration. If the final presentation step unexpectedly fails after meshes were bypassed, the original `OnRenderImage` is allowed to run and the next frame returns to stock; that exceptional frame may display the previous stock render, which is why active mode remains prototype-only.

## Verification and status

Run `verify.ps1` to build and verify:

- exact v0.230 `GenerateBackgrounds` and private `OnRenderImage` shapes;
- the stock three-`Graphics.Blit` control-flow surface and transfer fields;
- all required Unity constructors, formats, upload calls and Blit overloads;
- compiled DKC, Mode 7 and UI-fade fallback guards;
- persistent-upload IL;
- exact RGBA palette expansion and top-down/bottom-up row behavior;
- single-owner provider registration;
- that no hash-matching plugin was installed.

When enabled, `status.json` records provider frames, predicted substitutions, actual mesh bypasses, presented frames, stock fallbacks, failures and categorized rejection reasons.

## Remaining renderer work

This proves the runtime presentation seam, not PPU correctness. A production CPU renderer still needs exact per-scanline register replay, tile modes, OBJ limits/priority, windows, color math, mosaic, interlace/overscan and widened DKC visibility. Start with non-Mode7 398x224 final-composed frames and keep dry-run enabled until image-by-image comparisons match the stock renderer.
