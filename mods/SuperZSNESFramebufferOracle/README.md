# SuperZSNES Framebuffer Oracle

This is a script-only regression harness for comparing a forthcoming framebuffer renderer with SuperZSNES's stock output. It does not patch or install anything. A capture run uses the already-installed `DKCLevelAutomation` bridge for exact emulated-frame input and the already-installed `DKCWidescreenDebugger` bridge for the framebuffer and raw PPU inputs.

Offline validation and image comparison never contact the emulator. The `capture` command is intentionally state-changing: it loads the explicit ROM/state, advances the exact requested frames, and leaves the emulator paused with controller schedules cleared. It never launches, resumes, or stops an emulator process.

## What is captured

At each recipe checkpoint the harness records:

- the exact relative and emulator frame numbers from both bridges;
- `frame-composed.png`, falling back to `frame-main.png` only when needed;
- WRAM, VRAM, current/frame-start CGRAM, current/frame-start OAM, and PPU I/O registers;
- serialized PPU state, debugger bridge PPU state, and all other files in the debugger's full capture;
- SHA-256 and byte size for the ROM, state, recipe, image, and every raw artifact.

The stock and candidate runs are paired by `(case id, relative frame)`, never timestamp or capture-folder name. A run comparison is invalid if the ROM, state, recipe, controller macro, checkpoint set, or raw emulation inputs differ. That is deliberate: only equal PPU inputs can isolate a framebuffer renderer difference.

## Setup

Python 3.10 or newer and Pillow are required:

```powershell
Set-Location '<superzsnes-source>\Mods\SuperZSNESFramebufferOracle'
python -m pip install -r requirements.txt
python capture_oracle.py validate --recipe '.\recipes\dkc-saved-states.json'
python -m unittest discover -s tests -v
```

`validate` is offline and does not require a running emulator, endpoint, ROM, or state file.

## Saved-state recipe

[`recipes/dkc-saved-states.json`](recipes/dkc-saved-states.json) defines three deterministic cases:

- `jungle-scroll-right-y`: continuous `RIGHT+Y`, captured at relative frames 0, 1, 8, 16, 32, 64, 96, and 128.
- `cave-exit-right-y`: frame 0 neutral, input frames 1–90 `RIGHT+Y`, then neutral, with checkpoints around the known exit transition.
- `barrel-neutral-animation`: 64 neutral frames so an explicitly selected saved Barrel state is tested without assuming a player action.

Confirmed local state paths are:

```text
Jungle: <workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2
Cave:   <workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-cave-exit-repro
```

No existing state was guessed to be the saved Barrel state. Supply its exact path as `--state barrel=...`. A state key is required only when its case is selected.

## Capture stock and candidate runs

Start SuperZSNES normally with the existing automation and debugger plugins, then run the stock build once and the candidate renderer build once. Use the same ROM, state files, and recipe for both. The example below captures Jungle and cave; add the explicit Barrel state and remove the two `--case` filters for the full suite.

```powershell
$oracle = '<superzsnes-source>\Mods\SuperZSNESFramebufferOracle'
$automation = '<superzsnes>\BepInEx\plugins\DKCLevelAutomation\bridge.json'
$debugger = '<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\bridge.json'
$rom = '<workspace>\DKC_Widescreen_358x224.sfc'
$jungle = '<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2'
$cave = '<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-cave-exit-repro'

Set-Location $oracle
python capture_oracle.py capture `
  --recipe '.\recipes\dkc-saved-states.json' --variant stock --rom $rom `
  --state "jungle=$jungle" --state "cave=$cave" `
  --case jungle-scroll-right-y --case cave-exit-right-y `
  --automation-endpoint $automation --debugger-endpoint $debugger `
  --output '.\runs\stock'
```

After enabling the candidate renderer and restarting SuperZSNES as that renderer requires, repeat with `--variant candidate` and `--output '.\runs\candidate'`. Do not reuse an output directory; the tool refuses to overwrite evidence.

For the full saved-state suite, include:

```powershell
--state "barrel=D:\exact\path\to\saved-barrel.szst"
```

and omit all `--case` arguments.

Endpoint paths may instead be set in `SUPERZSNES_DKC_AUTOMATION_ENDPOINT` and `SUPERZSNES_DKC_DEBUGGER_ENDPOINT`. Authentication tokens are read at request time and are never copied into a run manifest.

## Compare complete runs

Exact comparison is the default:

```powershell
python compare_oracle.py runs '.\runs\stock' '.\runs\candidate' --output '.\reports\stock-vs-candidate'
```

Open `reports\stock-vs-candidate\index.html`. Each checkpoint includes normalized stock/candidate PNGs, an amplified RGB diff, a heatmap, an overlay, and machine-readable `report.json`. `summary.json` is suitable for CI.

Optional thresholds must be explicitly requested:

```powershell
python compare_oracle.py runs '.\runs\stock' '.\runs\candidate' `
  --channel-tolerance 1 --max-differing-pixels 4 `
  --output '.\reports\tolerant'
```

Images are always normalized to RGBA, but are never resized or cropped. A pixel differs when any RGBA channel exceeds `--channel-tolerance`.

Exit codes are:

- `0`: valid comparison and all framebuffer checkpoints passed;
- `1`: invalid evidence or setup error, including unequal raw PPU inputs;
- `2`: valid equal-input comparison with framebuffer differences beyond thresholds.

## Compare two standalone images

```powershell
python compare_oracle.py images '.\stock.png' '.\candidate.png' --output '.\reports\one-frame'
```

The JSON report includes dimensions, exact and thresholded differing-pixel counts, percentage, exclusive-edge bounding box, maximum/mean absolute channel delta, RMSE, and per-channel statistics.

## Evidence layout

```text
run\
  manifest.json
  cases\<case-id>\f<relative-frame>\
    bridge-ppu-state.json
    raw\
      frame-composed.png
      wram-7e7f.bin
      vram.bin
      cgram.bin
      cgram-frame-start.bin
      oam.bin
      oam-frame-start.bin
      io-registers.bin
      ppu-state.json
      ...debugger renderer diagnostics...
```

Because capture evidence is copied out of the debugger session directory, reports remain reproducible after its normal session logs are rotated or removed.
