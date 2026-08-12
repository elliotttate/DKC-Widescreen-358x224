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

## Deluxe MSU-1 music build

The optional build below combines the same widescreen patch with the 60-track
DKC Deluxe MSU-1 cue map. It is a source port of the Deluxe USA Rev. 2 patch to
this project's USA v1.0 hooks; do not apply the original Deluxe IPS to this ROM.

```powershell
./rom/build.ps1 `
  -DisassemblyRoot '<dkc-disassembly-root>' `
  -RomPath '<clean-dkc-usa-v1.0-rom>' `
  -EnableMsu1Deluxe
```

The expected Deluxe output SHA-256 is
`FD2950B3AAE287E24F8D8B665AFBC3BE0EC3EEC07AA19DE055427DF76BD46AF5`.
The repository contains no audio. Given a matching, legally obtained 60-track
pack, prepare a runnable same-basename bundle without duplicating its roughly
1.44 GiB of PCM data:

```powershell
./rom/setup-msu1-deluxe.ps1 `
  -RomPath './artifacts/DKC_Widescreen_358x224_MSU1_Deluxe.sfc' `
  -AudioPackPath '<directory-containing-dkc_msu-1.pcm-through-dkc_msu-60.pcm>' `
  -DestinationDirectory '<playable-bundle-directory>'
```

The setup script validates every `MSU1` header, sample alignment, and loop
point; creates the required empty same-basename `.msu` marker; and hard-links
the PCM files under the ROM basename SuperZSNES expects. Add
`-UseOptionalGangPlankGalleon` only when the pack includes and you prefer its
optional track 25.

The overlay changes asset extraction/build automation plus these game behaviors:

- symmetric 56-pixel camera and render margins;
- complete initial and moving tilemap streaming for the 368-pixel internal guard width;
- widened sprite/object activation and culling;
- banana formation coverage, positioning, collision, and 9-bit OAM X handling;
- restored player endpoint ranges at widened terminal cameras;
- retry of missing children in active type-5 groups, fixing the Barrel Cannon Canyon upper-barrel softlock.
- optional DKC Deluxe MSU-1 music selection for tracks 1-60 while retaining SPC sound effects.
