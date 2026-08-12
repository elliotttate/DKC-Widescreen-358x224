DONKEY KONG COUNTRY — 358x224 WIDESCREEN
========================================

QUICK START

1. Run DKC-Widescreen-Patcher.exe.
2. Select a clean, headerless Donkey Kong Country USA v1.0 ROM.
   Required MD5: 30c5f292ff4cbbfcc00fd8fa96c2de3b
3. Choose the standard patch, or an MSU-1 compatibility mode.
4. Click "Create widescreen ROM". Your original ROM is never modified.

The BPS patches are also included for users who prefer Floating IPS or another
BPS-compatible patcher.

SUPERZSNES v0.300 SETUP

The ROM hack and emulator renderer work together. To display the true 358x224
view in SuperZSNES v0.300:

1. Install the 32-bit BepInEx 6 IL2CPP build supported by SuperZSNES v0.300.
2. Close SuperZSNES.
3. Copy SuperZSNESDKCFramebufferRendererIL2CPP.dll into:
   SuperZSNES_v0.300\BepInEx\plugins\SuperZSNESDKCFramebufferRendererIL2CPP\
4. Launch SuperZSNES and open the patched ROM.

The plugin automatically falls back to the stock renderer on unsupported
frames. The standard patch retains the original SNES music.

MSU-1 MODES

The Deluxe option supplies only the 60-track Deluxe cue hooks. The Restoration
option supplies the traditional 27-track cue hooks. This release contains no
music. Obtain a compatible music pack separately and rename/link its PCM files
to the patched ROM's basename as described in the repository documentation.

LEGAL

No Donkey Kong Country ROM, extracted game assets, music, emulator executable,
or BepInEx runtime is included. You must provide legally obtained copies.
Donkey Kong Country and related names are trademarks of their respective
owners. This independent fan/research project is not affiliated with Nintendo
or Rare.

Source and full documentation:
https://github.com/elliotttate/DKC-Widescreen-358x224
