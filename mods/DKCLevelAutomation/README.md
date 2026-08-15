# DKC Level Automation

An isolated BepInEx 5 plugin for deterministic, programmatic SuperZSNES level tests. It does not modify or depend on `DKCWidescreenDebugger`, and it never starts or stops an emulator process.

## What it provides

- Exact per-emulated-frame controller schedules for SNES controllers 1-5. Scheduled input replaces physical input for that controller instead of OR-ing with it.
- Pause and resume, plus exactly N forward frames while paused.
- Run until a 1-4 byte little-endian WRAM condition matches, with both frame and wall-clock limits.
- Load a ROM, a normal state suffix, or an explicit save-state file.
- Read and write WRAM while paused.
- An authenticated loopback-only TCP bridge and a Python 3 CLI with no third-party packages.
- JSON script execution for repeatable level-test recipes.

## Build and install

The checked-in defaults match the local SuperZSNES/BepInEx layout used to build this project:

```powershell
cd <superzsnes-source>\Mods\DKCLevelAutomation
.\build.ps1
.\install-plugin.ps1 -GameRoot <superzsnes> -SkipBuild
```

For another layout, pass `-BepInExRoot` and `-GameManagedDir` to `build.ps1`, and `-GameRoot` to `install-plugin.ps1`.

The runtime folder is:

```text
<game>\BepInEx\plugins\DKCLevelAutomation\
    DKCLevelAutomation.dll
    README.md
    cli\dkc_level_cli.py
    cli\run_regression.py
    examples\level_test.example.json
    recipes\*.json
```

Start SuperZSNES yourself. On plugin startup it creates `bridge.json` beside the DLL. The file contains the active loopback port and a new random authentication token; it is removed at clean plugin shutdown. The configured port is 17817, with automatic fallback to a free loopback port.

## CLI quick start

When the CLI is in the installed plugin folder it finds `bridge.json` automatically:

```powershell
$cli = "<superzsnes>\BepInEx\plugins\DKCLevelAutomation\cli\dkc_level_cli.py"
python $cli status
python $cli pause
python $cli load-rom "D:\ROMs\Donkey Kong Country (USA).sfc"
python $cli load-state --file "D:\States\jungle-hijinx-start.szst0"
python $cli schedule --controller 1 --macro "0-59=RIGHT+Y;60=B;61-179=RIGHT"
python $cli run --frames 180
python $cli read --address 0x7E1234 --size 2
python $cli snapshot-wram --output .\checkpoint-wram.bin
```

From the source tree, pass the installed endpoint explicitly:

```powershell
python .\cli\dkc_level_cli.py --endpoint "<superzsnes>\BepInEx\plugins\DKCLevelAutomation\bridge.json" status
```

You can instead set `SUPERZSNES_DKC_AUTOMATION_ENDPOINT` to that full path.

## Controller schedule format

Schedules use zero-based frame indices relative to the next emulated frame:

```text
0-59=RIGHT+Y;60=B;61-179=RIGHT
```

Each segment is `FRAME=BUTTONS` or `START-END=BUTTONS`. Segments use `;` or `,`; simultaneous buttons use `+`, `|`, or spaces. Supported names are:

```text
B Y SELECT START UP DOWN LEFT RIGHT A X L R NONE
```

Unassigned frames inside the schedule are neutral. Once the schedule's final frame has passed, that controller remains exactly neutral until `clear-schedule`; physical input does not leak back into a deterministic test. `reset-schedule` rewinds the schedule cursor without changing its contents. Loading a ROM or state clears all schedules to prevent stale input.

`schedule` pauses before installing the macro. `run-macro` installs one controller's schedule and advances exactly its length in one request:

```powershell
python $cli run-macro --controller 1 --macro "0-29=RIGHT+Y;30=B;31-89=RIGHT"
```

For simultaneous multi-controller tests, schedule each controller and then issue one `run` command.

## Frame and WRAM automation

Advance exactly one or N frames while remaining paused:

```powershell
python $cli step
python $cli step --frames 8
python $cli run --frames 300 --timeout-ms 30000
```

Wait by advancing at most N frames, checking immediately before the first frame and after every completed emulated frame:

```powershell
python $cli wait --address 0x7E1234 --size 2 --op ge --value 0x0100 --max-frames 600 --timeout-ms 30000
```

Operators are `eq`, `ne`, `lt`, `le`, `gt`, and `ge`. Values are little-endian. Addresses must be in `$7E0000-$7FFFFF`; sizes are 1-4 bytes. `--mask` applies before comparison, and `--signed` uses the requested byte width for two's-complement comparison (including negative decimal expectations such as `--value -2`). A condition timeout is an error and the CLI exits nonzero.

WRAM reads and writes:

```powershell
python $cli read  --address 0x7E0D84 --size 2 --signed
python $cli write --address 0x7E0D84 --size 2 --value 0x0080
```

Writes pause the emulator first.

`snapshot-wram` copies all 128 KiB of WRAM in one game-thread bridge request and
returns the emulator frame plus a SHA-256 digest. With `--output`, the CLI
validates and decodes the base64 response into a binary file. This avoids
sampling related actor/camera fields across many Unity updates and is the
preferred primitive for differential softlock diagnosis.

## Clean-vs-candidate save-state differential runner

`cli\run_state_differential.py` replays external save states twice: first with a
known-clean ROM and then with a candidate ROM. It never launches or terminates
SuperZSNES. Every branch reloads the original state, installs one exact input
schedule, and samples atomic WRAM at configured relative frames. Its report
contains:

- hashes for both ROMs and every external state;
- camera, layer, bounds, level, entrance, input, pause/gameplay and display state;
- all 26 normal-sprite slots with IDs, positions, speeds, poses, animation IDs,
  state words and native screen-relative positions;
- per-interval spawn/despawn/slot-replacement lifecycle events;
- clean-vs-candidate actor matching by ID and nearest world position, so normal
  slot allocation changes do not look like missing enemies;
- changed WRAM ranges and busiest 256-byte pages;
- selected full-width composed screenshots and optional complete debugger
  captures containing WRAM, OAM, VRAM, CGRAM, PPU and renderer state.

The checked-in `four-user-states-differential` plan identifies the supplied
states as Croctopus Chase (`0x0025`), Poison Pond (`0x0017`), Gang-Plank
Galleon (`0x004C`), and the unlocked world map. Its movement macros are labeled
diagnostic hypotheses. Each is an independent branch, not an assertion that it
is the intended route through the level.

```powershell
python .\cli\run_state_differential.py `
  --recipe four-user-states-differential `
  --baseline-rom "D:\ROMs\Donkey Kong Country (USA).sfc" `
  --candidate-rom "D:\ROMs\DKC_Widescreen_358x224.sfc" `
  --state-dir "D:\States" `
  --automation-endpoint "<superzsnes>\BepInEx\plugins\DKCLevelAutomation\bridge.json" `
  --debugger-endpoint "<superzsnes>\BepInEx\plugins\DKCWidescreenDebugger\bridge.json"
```

Use `--state szst0=<path>` (repeatable) when filenames or locations differ.
Use `--no-debugger` for atomic WRAM-only runs. The runner requires an already
running emulator and leaves it paused; state/ROM files and generated
`DifferentialRuns` evidence are ignored by Git.

## Softlock closure routes

`cli\run_softlock_closure.py` codifies the recovered full Croctopus Chase and
Poison Pond traversals plus the Slipslide Ride type-`$09` transition probe. It
reloads each immutable supplied state, runs the exact 1,860/1,500-frame routes
in checkpointed chunks, and asserts the logic-critical actors and scanner
frontiers. Slipslide moves Layer1 Y through the descriptor's vertical band for
one frame and requires secondary range `$25..$2C`. The default three
repeats must produce byte-identical full-WRAM SHA-256 values at every matching
checkpoint. The runner restores the first selected state, clears all schedules,
and leaves the emulator paused.

```powershell
python .\cli\run_softlock_closure.py `
  --rom '<candidate widescreen ROM>' `
  --state0 '<states>\DKC_Widescreen_358x224.szst0' `
  --state1 '<states>\DKC_Widescreen_358x224.szst1' `
  --state5 '<states>\DKC_Widescreen_358x224.szst5' `
  --automation-endpoint '<SuperZSNES>\BepInEx\plugins\DKCLevelAutomation\bridge.json'
```

## JSON test scripts

Copy `examples\level_test.example.json`, replace the paths and WRAM address with the values for the level under test, then run:

```powershell
python $cli script .\examples\my_level_test.json
```

A script is a list (or an object with a `steps` list) of bridge commands:

```json
[
  {"command":"load_state_file","args":{"path":"D:\\States\\start.szst0"}},
  {"command":"schedule","args":{"controller":1,"macro":"0-119=RIGHT+Y"}},
  {"command":"run_frames","args":{"count":120}},
  {"command":"read_wram","args":{"address":"0x7E1234","size":2}}
]
```

The CLI stops on the first bridge error and returns exit code 1, which makes it usable from PowerShell, Python subprocesses, CI-like local runners, or an MCP wrapper.

## DKC regression recipes and TilemapInspector checkpoints

Five exact-frame recipes correlate deterministic input with `DKCTilemapInspector` captures:

- `fresh-jungle-entry-paused`: captures the exact loaded Jungle state while paused, then the first neutral emulated frame.
- `horizontal-right-then-left`: runs 90 exact frames right+Y and then left+Y, with neutral settling frames and captures at frames 32, 64, and 106 in both directions.
- `vertical-jump-y-boundaries`: performs a running jump and single-steps 96 frames, capturing actual 8-pixel bucket changes in camera Y, layer-1 Y, and normal-sprite slots 0/1.
- `cave-banana-position-and-pickup`: uses the saved cave reproduction to record banana count, player collision probe, formation-local X, camera basis/bounds, and the exact 32-to-33 RIGHT-frame pickup transition for the widescreen formation fix.
- `cave-exit-right-y`: loads the preserved Jungle Hijinxs Bonus 1 cave state, advances one neutral frame, runs exactly 90 RIGHT+Y frames, and asserts entrance `0x0006 -> 0x0008`. It then advances 400 neutral frames through the fade and asserts that the outdoor Jungle scene is fully visible with the wide bounds and both Kongs restored.
- `barrel-cannon-group-retry`: loads the preserved Barrel Cannon Canyon checkpoint where the occupied lower barrel's grouped Zinger and upper target barrel were skipped, advances two frames, asserts that child records `0x8B/0x8C` are allocated as IDs `0x19/0x38`, and verifies that the target remains active for another 300 frames.

The separate `realtime-jungle-right-y-cadence` recipe is for performance A/B tests. Its runner installs `0-7199=RIGHT+Y` while paused, resumes normal emulation, waits seven wall-clock seconds, and marks a 30-second measurement interval. It never calls `run_frames` or `step_frames`, so it preserves the Unity/audio pacing under test.

Run it only when changing the current emulator state is acceptable:

```powershell
$root = "<superzsnes>\BepInEx\plugins\DKCLevelAutomation"
python "$root\cli\run_realtime_scroll.py" `
  --label baseline `
  --rom "<workspace>\DKC_Widescreen_358x224.sfc" `
  --state "<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-widescreen-clean-entry-v2"
```

The runner loads the specified ROM/state, resumes for the sample, pauses at the end, and writes a manifest under `RealtimeScrollRuns`. The manifest records UTC measurement bounds, ROM/state hashes, start/end emulated frames, WRAM camera/layer positions, and the active CadenceCounter `windows.jsonl`. Use the same immutable ROM/state, display size, focus/foreground state, `vSyncCount`, and `targetFrameRate` for both conditions. `--validate-only` checks the recipe and files without connecting to the emulator.

Run one recipe after starting SuperZSNES yourself with both plugins installed:

```powershell
$root = "<superzsnes>\BepInEx\plugins\DKCLevelAutomation"
python "$root\cli\run_regression.py" `
  --recipe horizontal-right-then-left `
  --rom "D:\ROMs\Donkey Kong Country (USA).sfc" `
  --state "D:\States\jungle-hijinx-start.szst0"
```

The cave-exit regression against the preserved local state is:

```powershell
python "$root\cli\run_regression.py" `
  --recipe cave-exit-right-y `
  --rom "<workspace>\DKC_Widescreen_358x224.sfc" `
  --state "<workspace>\DKC_Widescreen_358x224.data.szsnes\DKC_Widescreen_358x224.szst-cave-exit-repro"
```

A checkpoint can include an `expect` object keyed by watch name. Scalar values mean equality; explicit conditions accept `op` (`eq`, `ne`, `lt`, `le`, `gt`, or `ge`), `value`, and optional `mask`. Failed assertions are written into the checkpoint evidence before the runner exits nonzero.

The runner automatically discovers the automation endpoint beside itself and the sibling `DKCTilemapInspector\bridge.json`. Explicit endpoint options are available when using the source tree:

```powershell
python .\cli\run_regression.py `
  --recipe vertical-jump-y-boundaries `
  --rom "D:\ROMs\Donkey Kong Country (USA).sfc" `
  --state "D:\States\jungle-hijinx-start.szst0" `
  --automation-endpoint "<superzsnes>\BepInEx\plugins\DKCLevelAutomation\bridge.json" `
  --tilemap-endpoint "<superzsnes>\BepInEx\plugins\DKCTilemapInspector\bridge.json"
```

Run all three against the same ROM/state with:

```powershell
.\run-regression-suite.ps1 `
  -RomPath "D:\ROMs\Donkey Kong Country (USA).sfc" `
  -StatePath "D:\States\jungle-hijinx-start.szst0"
```

Every run writes `manifest.json`, `events.jsonl`, and one JSON file per checkpoint under `RegressionRuns`. A checkpoint includes the paused automation status, emulator frame, all configured WRAM watches, 8/16/32-pixel buckets and offsets, TilemapInspector status, and the returned TilemapInspector capture/manifest path. The DKC addresses come from the local `RAM_Map_DKC1.asm`: camera X/Y `$7E1A62/$7E1A4C`, layer-1 X/Y `$7E088B/$7E0895`, and normal-sprite X/Y tables `$7E0B19/$7E0BC1`.

Recipe syntax can be checked without a running emulator:

```powershell
python .\cli\run_regression.py --validate-only `
  --recipe fresh-jungle-entry-paused --rom dummy.sfc --state dummy.szst0
```

Use `--no-tilemap` to retain WRAM/status checkpoints without requesting captures. The runners only connect to existing localhost bridges; they never launch or terminate SuperZSNES.

The bridge dispatches client work through the managed thread pool and deterministically disposes each request's completion event after both the game-thread producer and network waiter finish. This prevents status/automation clients from leaking native thread or wait handles. Run the bounded 500-request verification with:

```powershell
dotnet run --project .\tests\BridgeHandleLeakTests.csproj -c Release
```

## Bridge protocol

The transport is UTF-8, one request and one JSON response per line over TCP on `127.0.0.1`. A request line is tab-delimited:

```text
request-id<TAB>token<TAB>command<TAB>base64(key)<TAB>base64(value)...<LF>
```

Responses are:

```json
{"id":"...","ok":true,"result":{}}
{"id":"...","ok":false,"error":"message"}
```

Use the bundled CLI as the reference client. Supported commands are:

- `status`, `pause`, `resume`, `cancel`
- `load_rom(path, load_last_state)`
- `load_state(suffix)`, `load_state_file(path)`
- `schedule(controller, macro)`, `run_macro(controller, macro)`
- `clear_schedule(controller=all)`, `reset_schedule(controller=all)`
- `run_frames(count)`, `step_frames(count)`
- `wait_wram(address, size, op, value, mask?, signed?, max_frames, timeout_ms?)`
- `read_wram(address, size, signed?)`, `write_wram(address, size, value)`
- `snapshot_wram()` (atomic full 128 KiB WRAM, base64 plus frame and SHA-256)

Only one frame-running operation can be active. `status` and `cancel` remain available from a second connection while one is running. The token protects against unrelated local processes accidentally driving the emulator; the bridge never binds to a LAN interface.

## Determinism notes

The plugin Harmony-patches `SNESPPU.UpdateInput` after SuperZSNES has sampled hardware input. It writes the exact 16-bit SNES pad mask into the PPU's private controller array before CPU execution begins. The macro cursor advances once per actual `MasterExecutor.RunFrame`, not once per Unity render frame.

For N-frame stepping, the plugin keeps SuperZSNES paused and sets `StepFrameForward` once, then waits for the `RunFrame` postfix before requesting the next frame. This avoids the emulator's boolean `_progressFrame` collapsing repeated step requests into a single frame.

For best repeatability, load the same ROM/state, install schedules while paused, avoid unrelated debugger tools that also alter input or stepping, and make all assertions from WRAM after a completed emulated frame.
