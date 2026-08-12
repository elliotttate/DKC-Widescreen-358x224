# Donkey Kong Country 358x224 widescreen

Source, diagnostics, regression automation, and SuperZSNES runtime mods for a true **358x224** Donkey Kong Country widescreen presentation.

The project has two complementary parts:

- `rom/`: an Asar overlay for the USA v1.0 DKC disassembly. It expands streaming, camera/object ranges, banana formations, player endpoints, and grouped-object retry behavior.
- `mods/`: BepInEx plugins and scripts for SuperZSNES v0.230, including the accepted CPU framebuffer renderer, frame pacing fixes, automation, capture, and diagnostic tooling.

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

## Validate the source-only tree

```powershell
./scripts/validate-source.ps1
```

The check rejects copyrighted/runtime binaries, save data, captures, local absolute paths, and common credential patterns; parses all JSON; parses PowerShell source; and byte-compiles the Python utilities.

## Version targets

- DKC USA v1.0, headerless MD5 `30c5f292ff4cbbfcc00fd8fa96c2de3b`
- Yoshifanatic1 DKC1 disassembly commit `c2080f40469c716923f550706509a0d354229841`
- SuperZSNES v0.230 `Assembly-CSharp.dll` SHA-256 `33ED627F3A29B5DB82ED8F5CFFC8306CCBACAA2743E1408C976666DC06131DED`
- Final widescreen ROM SHA-256 `B4AB46098E48218E70B5349E09E7FE71E344D23E3568F46E956B44C670006D6D`

## Documentation

- [Project status and safety matrix](PROJECTS.md)
- [ROM build instructions](rom/README.md)
- [Technical worklog](docs/WORKLOG.md)
- Per-plugin READMEs under `mods/`

## License and trademarks

The source is distributed under GPL-3.0; see [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md). Donkey Kong Country, Nintendo, Rare, Super Nintendo, and related names and assets belong to their respective owners. This is an independent fan/research project and is not affiliated with or endorsed by them.
