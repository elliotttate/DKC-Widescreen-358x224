# Donkey Kong Country widescreen

Source, diagnostics, regression automation, and SuperZSNES runtime mods for a true **358x224** Donkey Kong Country widescreen presentation, with an optional near-exact **16:9 398x224** profile.

The project has two complementary parts:

- `rom/`: an Asar overlay for the USA v1.0 DKC disassembly. It expands streaming, camera/object ranges, banana formations, player endpoints, and grouped-object retry behavior.
- `mods/`: BepInEx plugins and scripts for SuperZSNES v0.230, plus a separate BepInEx 6 IL2CPP port of the accepted framebuffer renderer for v0.300.

## Easiest installation

Download the latest package from [GitHub Releases](https://github.com/elliotttate/DKC-Widescreen-358x224/releases/latest), extract it, and run `DKC-Widescreen-Patcher.exe`. The patcher verifies a clean, headerless DKC USA v1.0 ROM, offers 358x224 and 398x224 profiles with standard or MSU-1-compatible music hooks, writes a new ROM, and verifies the exact result. It never modifies the original ROM. The release also includes the SuperZSNES v0.300 IL2CPP renderer DLL and standalone BPS patches.

The final renderer keeps the SNES core near 60 FPS while replacing SuperZSNES's expensive per-tile Unity presentation path for supported DKC Mode 1 frames. The accepted v0.4.2 retained-tile rewrite raised the difficult Millstone Mayhem scrolling case from 86-90 to 119.4-119.7 presentation updates/s, while eliminating every multi-frame batch in the controlled 1,800-frame test.

## What is intentionally absent

This repository contains **no ROM, extracted game assets, save states, SuperZSNES executable, decompiled `Assembly-CSharp.dll`, BepInEx binaries, or gameplay captures**. You must provide legally obtained copies locally. The ignore and validation rules reject those artifacts.

## Recommended components

The primary supported path is:

1. Build the ROM overlay with [rom/README.md](rom/README.md).
2. Build `SuperZSNESDKCFramebufferRenderer` and the recommended runtime helpers listed in [PROJECTS.md](PROJECTS.md).
3. Install the resulting DLLs under the matching SuperZSNES BepInEx plugin directories while SuperZSNES is closed.
4. Use `DKCLevelAutomation`, `DKCWidescreenDebugger`, and `DKCTilemapInspector` for deterministic regression checks.

All performance experiments are included for auditability, but several are quarantined or rejected. Do not enable an experiment merely because it builds; consult [PROJECTS.md](PROJECTS.md) and each project README.

## Build the BepInEx projects

Prerequisites:

- Windows and PowerShell 7 or Windows PowerShell 5.1
- .NET SDK capable of building .NET Framework 4.7.2 and netstandard2.1 projects
- BepInEx 5 x86 extracted locally
- SuperZSNES v0.230 installed locally

```powershell
./build-mods.ps1 `
  -BepInExRoot '<bepinex-root>' `
  -SuperZSNESRoot '<superzsnes-root>'
```

The same paths can be supplied through `BEPINEX_ROOT`, `SUPERZSNES_ROOT`, and `SUPERZSNES_MANAGED_DIR`. Build output remains under each project's ignored `bin/` directory.

For SuperZSNES v0.300, use [the separate IL2CPP renderer project](mods/SuperZSNESDKCFramebufferRendererIL2CPP/README.md). It pins the 32-bit BepInEx IL2CPP build that supports v0.300 metadata version 39. The companion [IL2CPP performance suite](mods/SuperZSNESPerformanceSuiteIL2CPP/README.md) contains only the old fixes that still map safely to current native methods; all switches default off. The experimental [Object Depth Studio](mods/SuperZSNESSpriteDepthStudioIL2CPP/README.md) catalogs both complete OAM sprites and conservatively connected BG1/BG2/BG3 scenery for authored 3D placement. Do not install the v0.230 performance/debug plugins into v0.300 because the Mono and IL2CPP ABIs are incompatible.

## Validate the source-only tree

```powershell
./scripts/validate-source.ps1
```

The check rejects copyrighted/runtime binaries, save data, captures, local absolute paths, and common credential patterns; parses all JSON; parses PowerShell source; and byte-compiles the Python utilities.

## Version targets

- DKC USA v1.0, headerless MD5 `30c5f292ff4cbbfcc00fd8fa96c2de3b`
- Yoshifanatic1 DKC1 disassembly commit `c2080f40469c716923f550706509a0d354229841`
- SuperZSNES v0.230 `Assembly-CSharp.dll` SHA-256 `33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED`
- SuperZSNES v0.300 x86 IL2CPP executable SHA-256 `B83358E453C9378A37AA0E43D22886AD49EE426F1ECF381B4F84A3A49F54FDD6`
- SuperZSNES v0.300 `GameAssembly.dll` SHA-256 `0A5582B26EF2596FFA504AC6C1282E145EFA093B49EFD22974D4F2C74561271A`
- Final widescreen ROM SHA-256 `B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D`
- Optional widescreen + Deluxe MSU-1 ROM SHA-256 `FD2950B3AAE287E24F8D8B665AFBC3BE0EC3EEC07AA19DE055427DF76BD46AF5`
- Optional widescreen + 27-track Restoration MSU-1 ROM SHA-256 `4484CB5374F3C04E9F8DA1880C21D85D0C0403286CFABB65639BAD7CFC55A5A5`
- Optional 398x224 ROM SHA-256 `52272D471CF52B9F18FBA900DE3A5EC2E0D0B337061CCBB4DC2C8F945DCA6CFA`
- Optional 398x224 + Deluxe MSU-1 ROM SHA-256 `C858CBFBD14C8C0F1D3435541242B948A6737E325CB2FAC5F914FE725FE2B1C1`
- Optional 398x224 + Restoration MSU-1 ROM SHA-256 `E25B79726C1A552F4AFE150AE2A224A01385FA693F1C5C014C07C84A5DC94144`

## Documentation

- [Project status and safety matrix](PROJECTS.md)
- [ROM build instructions](rom/README.md)
- [Technical worklog](docs/WORKLOG.md)
- [SuperZSNES v0.300 optimization port and benchmark](docs/V0300_OPTIMIZATION_PORT.md)
- [MSU-1 music design and Deluxe integration](docs/MSU1_MUSIC_REPLACEMENT_PLAN.md)
- Per-plugin READMEs under `mods/`

## License and trademarks

The source is distributed under GPL-3.0; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). Donkey Kong Country, Nintendo, Rare, Super Nintendo, and related names and assets belong to their respective owners. This is an independent fan/research project and is not affiliated with or endorsed by them.
