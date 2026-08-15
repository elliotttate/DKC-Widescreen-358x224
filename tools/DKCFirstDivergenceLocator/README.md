# DKC First-Divergence Locator

This isolated Python tool finds the first WRAM/state difference between a stock
DKC ROM and the 358-wide ROM. It drives an **already-running**
`DKCLevelAutomation` **v0.1.3** bridge and deliberately runs one variant at a
time:

1. load the stock ROM, load the external state, install the exact input
   schedules, and record an atomic WRAM fingerprint after every completed
   frame;
2. load the widescreen ROM, reload the same state, reinstall the same schedules,
   and find the first mismatching aggregate checkpoint window;
3. reload the widescreen replay and refine that window frame-by-frame;
4. replay the located frame independently under each ROM and require both the
   full-WRAM and selected-WRAM hashes to reproduce exactly;
5. report selected and raw byte ranges, named DKC fields, actor-slot changes,
   `$7E192B-$7E1A2A` object-bookkeeping changes, and nearby lifecycle-tracer
   rows when a tracer session is supplied.

There is no simultaneous-emulator or simultaneous-bridge assumption. The tool
does not edit a ROM or ASM file, launch/kill/install/restart an emulator, write
tracer command files, or enable tracer hooks. Its only runtime mutation is the
explicit ROM/state/input/frame control requested through the existing bridge.

## Exact search and expected widescreen differences

The locator uses deterministic sequential replay with checkpoint-window
refinement. `checkpointStride` partitions the search into windows. Each coarse
window hash incorporates every per-frame fingerprint in order; unlike an
endpoint-only comparison, it cannot miss a transient divergence that later
reconverges. The first mismatching window is reloaded and replayed
frame-by-frame. The reported frame is therefore the exact first selected
mismatch, not merely the first mismatching checkpoint.

Both of these are retained:

- `firstRawFrame`: the earliest difference anywhere in 128 KiB WRAM;
- `firstUnexpectedFrame`: the earliest difference among predicate-selected,
  non-ignored bytes.

The bundled recipe includes the `camera_and_bounds` group, then removes it with
`expected_widescreen_camera_bounds`. That profile ignores Layer 1 X/Y, camera
X/Y, camera bounds, and the bank-BD scanner window. Camera widening can thus be
visible as an earlier raw difference without masking the first actor,
bookkeeping, scanner-cursor, section-controller, or core-gameplay difference.
Use explicit `predicate.ignore` ranges for additional known/accepted bytes.

Available include groups are:

- `full_wram`
- `core_gameplay`
- `actor_pool`
- `object_bookkeeping`
- `scanner`
- `section_controller`
- `camera_and_bounds`

Addresses in custom ranges may be WRAM offsets (`0x1B23`) or full addresses
(`0x7E1B23` / `$7E1B23`). A range uses exactly one of `end` (inclusive) or
`length`.

## Offline validation

The recipe schema is [recipe.schema.json](recipe.schema.json). The bundled
[four-state sample](recipes/four-user-states.sample.json) contains all supplied
states (`szst0` through `szst3`) and their neutral/movement branches.

This command performs schema/semantic validation and prints the resolved plan.
It does **not** check ROM/state/endpoint files, read `bridge.json`, create a
socket, or contact a running emulator:

```powershell
python .\tools\DKCFirstDivergenceLocator\first_divergence.py `
  --validate-only `
  --recipe four-user-states.sample
```

Paths and one case can still be supplied in validate-only mode to check their
resolution and spelling without accessing them:

```powershell
python .\tools\DKCFirstDivergenceLocator\first_divergence.py `
  --validate-only `
  --recipe four-user-states.sample `
  --state-dir D:\States `
  --case szst0/right-swim
```

## Controlled run

Coordinate with the person using SuperZSNES first: a run intentionally replaces
the loaded ROM/state and controller input, and leaves the emulator paused. Start
SuperZSNES yourself with `DKCLevelAutomation` v0.1.3 already installed, then
run one state/scenario:

```powershell
python .\tools\DKCFirstDivergenceLocator\first_divergence.py `
  --recipe four-user-states.sample `
  --baseline-rom "D:\ROMs\Donkey Kong Country (USA).sfc" `
  --candidate-rom "D:\ROMs\DKC_Widescreen_358x224.sfc" `
  --state-dir "D:\States" `
  --case szst0/right-swim `
  --automation-endpoint "D:\SuperZSNES\BepInEx\plugins\DKCLevelAutomation\bridge.json" `
  --output "D:\Evidence\szst0-right-swim"
```

Repeat `--case STATE/SCENARIO` to select several cases; omit it to run every
case. Repeat `--state STATE=PATH` to override filenames from the recipe.

The endpoint is rejected unless it is loopback, protocol 1, and reports plugin
version `0.1.3`. Requests are issued serially, and every replay reloads the ROM,
external state, and controller schedule.

## Optional lifecycle correlation

Pass either an exact `DKCObjectLifecycleTracer` session directory or its plugin
root containing `Sessions`:

```powershell
  --lifecycle-session "D:\SuperZSNES\BepInEx\plugins\DKCObjectLifecycleTracer"
```

The locator records append offsets around each confirmation replay, reads only
new `events.jsonl`, `writes.jsonl`, and `scanner.jsonl` rows, and includes rows
within `traceRadiusFrames` of the located emulator frame. This prevents rows
from earlier state-load/replay segments with the same emulator frame number
from being mixed into the result. PC/`pcMeaning`, lifecycle events, scanner
decisions, and bookkeeping writes are preserved when the tracer produced them.
The locator never creates `*-trace-on.request` files, so optional high-cost
scanner tracing remains entirely under user control.

## Output

`report.json` contains:

- immutable ROM/state SHA-256 hashes and exact schedules;
- included and ignored predicate ranges;
- raw and unexpected first-relative-frame numbers;
- the checkpoint window refined to the exact frame;
- replay-confirmed full/selected WRAM hashes and emulator frame numbers;
- all/selected/ignored changed byte ranges with short before/after hex samples;
- named DKC globals and scanner/section fields;
- all changed fields in each of the 26 normal actor slots;
- changed object-record bookmarks and raw actor indexes;
- lifecycle-tracer events/writes/scanner PCs near the frame, if available.

For a located divergence, the two confirmation WRAM images are also saved as
gzip files below `cases/<state>/<scenario>/`. Reports are updated after each
completed case so earlier evidence survives a later interruption.

## Offline tests

The tests use synthetic 128 KiB snapshots and temporary JSONL files only. They
never resolve or contact an automation endpoint:

```powershell
python -m unittest discover `
  -s .\tools\DKCFirstDivergenceLocator\tests `
  -p "test_*.py" -v
```

Coverage includes ignored camera bytes, transient divergence/reconvergence,
selected and raw first-frame reporting, changed-range coalescing, named fields,
actor allocation, object bookkeeping, lifecycle trace slicing, recipe
validation, and the no-contact validate-only path.
