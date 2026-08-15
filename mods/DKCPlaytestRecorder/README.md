# DKC Playtest Recorder

`DKCPlaytestRecorder` turns an intermittent playtester report into a deterministic,
portable replay. A normal SuperZSNES `.szst` contains one instant only. SuperZSNES's
rewind history is a separate volatile in-memory ring and controller history is not
stored in either format.

The recorder retains the last 60 seconds of the **resolved SNES controller masks**
and takes a reusable full-machine checkpoint in memory every five seconds. These
checkpoints use the same `SNESMemoryState` implementation as SuperZSNES rewind. No
save-state file, screenshot, PNG, or other disk write occurs during ordinary play.

Press `F10` when a bug is visible, or create:

```text
<SuperZSNES>\BepInEx\plugins\DKCPlaytestRecorder\report.request
```

The request file may contain a short note. At the next safe end-of-Unity-update
boundary, the plugin pauses momentarily, converts the oldest retained checkpoint
into a normal `anchor.szst`, restores the exact current machine state, and writes a
bundle beneath `Bundles`, plus a single shareable `.dkcrepro.zip`. Gameplay
resumes if it was running before the report.

The packaged helper performs the same request without needing the hotkey:

```powershell
.\request-report.ps1 -GameDir '<SuperZSNES>' -Note 'ropes stopped spawning in Slipslide Ride'
```

Each bundle contains:

- `anchor.szst`: a normal, portable SuperZSNES state from before the report;
- `inputs.csv`: every resolved controller mask through the reported frame;
- `replay.json`: compressed exact-frame macros for controllers 1-5;
- `report.wram.bin`: all 128 KiB of endpoint WRAM;
- `manifest.json`: ROM/emulator/state/WRAM hashes and frame alignment;
- `README.txt`: concise handoff instructions.

The ROM is never copied into the bundle.

## Deterministic replay

Install `DKCLevelAutomation` v0.1.3 or newer, start SuperZSNES, and run:

```powershell
python .\cli\replay_bundle.py `
  --bundle '<bundle directory or .dkcrepro.zip>' `
  --rom '<exact widescreen ROM>' `
  --endpoint '<SuperZSNES>\BepInEx\plugins\DKCLevelAutomation\bridge.json'
```

The script rejects a different ROM hash, loads `anchor.szst`, applies the exact five
controller streams, advances precisely the recorded number of emulated frames, and
compares the resulting full-WRAM SHA-256 with the reported endpoint. A matching hash
proves the issue is reproducible; a mismatch is preserved as evidence that another
nondeterministic input or emulator subsystem must be recorded.

## Configuration and safety

- `Recorder.Enabled=true`
- `Recorder.HistorySeconds=60` (10-300)
- `Recorder.CheckpointSeconds=5` (1-30)
- `Recorder.ReportHotkey=F10`
- `Recorder.OutputDirectory=` (blank uses the plugin directory)

Loading a state, resetting, changing ROMs, or using rewind causes the emulated frame
number to jump. The plugin detects that discontinuity and starts a new timeline, so a
bundle never silently crosses an incompatible state boundary. It records the final
controller masks after `DKCLevelAutomation` overrides, which makes automated and
physical-input sessions use the same replay representation.

This first implementation targets the Mono-based SuperZSNES v0.230. The same bundle
format is suitable for a future v0.300 IL2CPP recorder, but this DLL must not be copied
into the IL2CPP build. The plugin verifies the exact supported v0.230
`Assembly-CSharp.dll` SHA-256 before installing any hook and fails closed on every
other emulator build.
