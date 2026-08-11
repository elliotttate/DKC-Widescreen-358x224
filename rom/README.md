# ROM overlay

This directory contains only the source overlay required to build the widescreen hack. It does not contain a ROM or extracted DKC assets.

## Prerequisites

1. A legal, headerless Donkey Kong Country USA v1.0 ROM with MD5 `30c5f292ff4cbbfcc00fd8fa96c2de3b`.
2. A clone of `Yoshifanatic1/Donkey-Kong-Country-1-Disassembly` at commit `c2080f40469c716923f550706509a0d354229841`.
3. SNES ROM Framework v1.2.0 installed into the upstream checkout (including its `Global/` and `Firmware/` directories), plus Windows PowerShell. The upstream project documents the matching framework setup.

## Build

`build.ps1` copies the small GPL overlay into the upstream checkout, validates and stages the user-supplied ROM for asset extraction, runs the noninteractive extractor, and assembles the hack.

```powershell
./rom/build.ps1 `
  -DisassemblyRoot '<dkc-disassembly-root>' `
  -RomPath '<your-clean-rom>' `
  -OutputPath './artifacts/DKC_Widescreen_358x224.sfc'
```

The expected output SHA-256 is `B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D`.

The overlay changes asset extraction/build automation plus these game behaviors:

- symmetric 56-pixel camera and render margins;
- complete initial and moving tilemap streaming for the 368-pixel internal guard width;
- widened sprite/object activation and culling;
- banana formation coverage, positioning, collision, and 9-bit OAM X handling;
- restored player endpoint ranges at widened terminal cameras;
- retry of missing children in active type-5 groups, fixing the Barrel Cannon Canyon upper-barrel softlock.
