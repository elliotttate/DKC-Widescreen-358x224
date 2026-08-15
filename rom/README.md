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

The expected output SHA-256 is `03EA182F7D0AA147BD020CB7B00F98E785D8BB00AAA1DBA95F458C33FDBBF34B`.

## Optional 16:9 profile

Add `-Aspect16x9` to any standard or MSU-1 build command to produce a
398x224 framebuffer, the closest whole-pixel width to 16:9 at 224 lines. The
ROM streams and culls against a 400-pixel internal guard, cropped by one pixel
on each side by the framebuffer renderer:

```powershell
./rom/build.ps1 `
  -DisassemblyRoot '<dkc-disassembly-root>' `
  -RomPath '<your-clean-rom>' `
  -Aspect16x9
```

Expected SHA-256 values are:

- Standard: `F6BDF57A563C290E66A7726190DC22C754D4D42DBB4DF62C77C8CE6C05E7D144`
- Deluxe MSU-1: `03A7B36933C11E30561B65FFBA01EC02FC18A124979019FC30154148668DF64B`
- Restoration MSU-1: `D8991560242D3BE1615D86890263E323F47A7560201D93893BD8C8EC53268F05`

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
`F213800099DC4C35D7B69A249FC4A8A98FE9FE8D65FC8724096E6C2C6B568C0E`.
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

- symmetric 56-pixel camera/render margins for 358x224, or 72-pixel margins for 398x224;
- complete initial and moving tilemap streaming for the 368- or 400-pixel internal guard width;
- widened sprite/object activation and culling;
- matched type-9 section-controller spans, preventing missing Slipslide Ride ropes and enemies;
- banana formation coverage, positioning, collision, and 9-bit OAM X handling;
- restored player endpoint ranges at widened terminal cameras;
- retry of missing children in active type-5 groups, fixing the Barrel Cannon Canyon upper-barrel softlock.
- optional DKC Deluxe MSU-1 music selection for tracks 1-60 while retaining SPC sound effects.

## Restoration MSU-1 music build

Traditional DKC restoration packs use the established 27-track mapping rather
than the Deluxe 60-track level-variant table. Build that compatible ROM mode
with:

```powershell
./rom/build.ps1 `
  -DisassemblyRoot '<dkc-disassembly-root>' `
  -RomPath '<clean-dkc-usa-v1.0-rom>' `
  -EnableMsu1Restoration
```

The expected Restoration output SHA-256 is
`CD6DA8C7C981118785014ABF1823BB3877389360587462D2CC247DE3EA2A7A79`.
Prepare the same-basename runtime bundle without copying its PCM data:

```powershell
./rom/setup-msu1-restoration.ps1 `
  -RomPath './artifacts/DKC_Widescreen_358x224_MSU1_Restoration.sfc' `
  -AudioPackPath '<directory-containing-dkc_msu-1.pcm-through-dkc_msu-27.pcm>' `
  -DestinationDirectory '<playable-bundle-directory>'
```

The setup validates all 27 MSU1-PCM headers, sample alignment, and loop points.
It hard-links the tracks under the ROM basename expected by SuperZSNES. Use
`-UseAlternateTrack10` only for a pack that supplies `dkc_msu-10_alt.pcm`.
