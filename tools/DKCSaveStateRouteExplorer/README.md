# DKC save-state route explorer

This standalone Python 3 tool searches short controller routes through an
already running, **paused** SuperZSNES instance using only the
`DKCLevelAutomation` v0.1.3 bridge. It is isolated under this directory and
does not change the automation plugin, ROM, ASM, save state, emulator install,
or emulator lifecycle.

The explorer uses replay-from-root search. It cannot create an intermediate
save state and does not need to: every candidate reloads the same external
state, verifies the same atomic WRAM bytes and emulator frame, installs the
candidate's complete parent-chain macro, and advances it with exact frame
counts. This makes a branch independent of all branches before it.

## Safety boundary

The live command can and will replace the game state and controller input of
the bridge process while it searches. Do not point it at a play session or an
emulator that another diagnostic tool is controlling. Use a manually prepared
dedicated instance and follow the live protocol below.

The program itself never:

- discovers, launches, kills, restarts, or installs SuperZSNES;
- loads or edits a ROM, writes WRAM, creates a save state, or writes the input
  state file;
- enables or disables the optional debug-invincibility plugin;
- connects to a non-loopback address;
- uses any bridge other than an explicitly supplied endpoint.

Its complete live command allowlist is `status`, `load_state_file`,
`snapshot_wram`, `schedule`, `run_frames`, and `clear_schedule`.

`search` fails closed unless all of these are true:

- `bridge.json` reports `DKCLevelAutomation` v0.1.3 and the explicit expected
  PID;
- the emulator is attached, loaded, paused, has both exact-frame hooks, has no
  active frame operation, and has no existing controller schedule;
- the external state matches an explicitly pinned SHA-256;
- `--ack-live-control` was supplied;
- the output directory is new or empty.

At the end, including ordinary search failures, it attempts to reload the root
state, clear all schedules, and leave the emulator paused. It verifies the
external state hash again and records cleanup status. A process crash or lost
bridge can prevent cleanup, so always inspect `failure.json` and the emulator
before resuming play.

## Offline validation

These commands do not read `bridge.json`, discover an endpoint, or open a
socket:

```powershell
$tool = '.\tools\DKCSaveStateRouteExplorer\route_explorer.py'

python $tool validate `
  --recipe .\tools\DKCSaveStateRouteExplorer\samples\croctopus-chase.json

python $tool validate `
  --recipe .\tools\DKCSaveStateRouteExplorer\samples\poison-pond.json

python -m unittest discover `
  -s .\tools\DKCSaveStateRouteExplorer\tests -v
```

`validate` expands every generated pulse into its exact zero-based macro and
prints `"liveBridgeContacted": false`. It deliberately does not require the
sample state filename to exist, because a live run should pass the actual
external state explicitly.

Pin a state without contacting the emulator:

```powershell
python $tool hash-state 'D:\States\croctopus-chase.szst0'
```

## Safe live usage protocol

1. Manually prepare a dedicated SuperZSNES instance with the intended ROM and
   `DKCLevelAutomation` v0.1.3. The explorer will not start, install, or restart
   it.
2. Copy the source state outside the emulator's normal state directory. Keep
   that copy read-only if practical. The explorer opens it only for hashing;
   the bridge also receives it only as a `load_state_file` source.
3. Pause the emulator and clear every existing automation schedule. Stop any
   other controller, stepping, regression, debugger, or state-load client.
4. Run `validate`, then `hash-state`. Review the expanded action set, WRAM
   addresses, goal, forbidden/death rules, maximum nodes, and predicate-check
   cadence. The checked-in waypoints are diagnostic starting points, not
   assertions about a completed route.
5. Read the PID and plugin version from the intended instance's
   `BepInEx\plugins\DKCLevelAutomation\bridge.json`. Do not reuse a PID from an
   earlier process.
6. If survival assistance is part of the experiment, enable or disable
   `DKCDebugInvincibility` separately, then request its fresh `status.json`
   separately. The explorer only reads and verifies that file.
7. Use a new output directory and run the explicitly acknowledged command.
8. When it finishes, confirm the report says `restoredRoot`, `scheduleCleared`,
   and `externalStateHashUnchanged` are true before doing anything else with
   the emulator.

Example (the placeholder values must be replaced):

```powershell
$tool = '.\tools\DKCSaveStateRouteExplorer\route_explorer.py'
$state = 'D:\States\croctopus-chase.immutable.szst0'
$endpoint = 'D:\SuperZSNES\BepInEx\plugins\DKCLevelAutomation\bridge.json'

python $tool search `
  --recipe .\tools\DKCSaveStateRouteExplorer\samples\croctopus-chase.json `
  --state $state `
  --state-sha256 '<64-hex value from hash-state>' `
  --endpoint $endpoint `
  --expect-pid 12345 `
  --ack-live-control `
  --output .\tools\DKCSaveStateRouteExplorer\runs\croc-001
```

Optional read-only invincibility assertion:

```powershell
  --invincibility-status 'D:\SuperZSNES\BepInEx\plugins\DKCDebugInvincibility\status.json' `
  --require-invincibility on
```

Use `off` to require that the override is not applied. This check never creates
`enable.request`, `disable.request`, or `status.request`; refreshing status is a
separate operator action.

## Search model

Actions can be a constant hold:

```json
{"id":"right-b-8","frames":8,"buttons":"RIGHT+B"}
```

or an explicit sequence:

```json
{
  "id":"down-turn",
  "sequence":[
    {"frames":3,"buttons":"DOWN+RIGHT+B"},
    {"frames":5,"buttons":"RIGHT"}
  ]
}
```

`underwaterPulseGenerators` creates the cross product of directions, action
buttons, total lengths, periods, and pulse widths in deterministic JSON order.
For example, total 8, period 4, and pulse 1 produces two one-frame button
pulses at relative frames 0 and 4 while holding the direction throughout.

The search is breadth-first by action depth with a bounded beam. Within a
depth, nodes sort by descending objective score, then fewer exact frames, then
the complete macro text. Each accepted node retains its parent ID. Replaying a
child always starts from the external root, not from the emulator state left by
its parent.

### Progress objective

`objective.terms` is a weighted sum. Each term reads a 1-8 byte little-endian
WRAM field and scores its change from `baseline` (or a fixed numeric
`reference`). `direction` is `maximize` or `minimize`; `weight` and `scale` may
be decimal values. The samples primarily maximize camera X (`$7E1A62`) and
the controlled actor's world X for the supplied state (`$7E0B1D` for
Croctopus Diddy and `$7E0B1B` for Poison Pond Donkey). Actor slots are
save-state-specific, so a new recipe must confirm the controlled actor index
before selecting fields.

### Goal, death, and forbidden predicates

A leaf condition accepts `address`, `size` (1-4), `op`, and either `value` or
`"compareTo":"baseline"`. Operators are `eq`, `ne`, `lt`, `le`, `gt`, and
`ge`. Conditions compose with `all`, `any`, and `not`.

Predicates are checked after each `search.predicateCheckFrames` chunk. Use 1
when a one-frame forbidden or death state must not be missed. A matched death
or forbidden predicate rejects the node; a matched goal stops at that exact
checked frame and emits a correctly truncated macro. Shorter check intervals
are safer but transfer more 128 KiB atomic WRAM snapshots.

The sample death rule detects a reduced `$7E0575` life counter. Debug
invincibility does not protect against pits, crushing, timers, scripted
failure, or every possible death path, so adapt the predicates to the exact
state before using it as a survival oracle.

### Compact WRAM deduplication

`dedup.selectors` extracts only configured WRAM fields, optionally applies a
mask/shift and integer `bucket`, serializes their names and values canonically,
and hashes them with a 96-bit BLAKE2s digest. Exact full-WRAM SHA-256 is still
used to prove every root reload is identical; the compact digest is only the
search-state identity. Include every gameplay field that makes future control
meaningfully different, and use smaller buckets if useful routes collapse as
duplicates.

## Determinism and output

For every candidate, the tool verifies:

- the reloaded root's full WRAM bytes, WRAM SHA-256, and emulator frame exactly
  match the first load;
- `schedule` reports the exact complete macro length and exact override;
- each `run_frames` result reports exactly the requested count;
- each atomic snapshot frame equals `root emulator frame + cumulative exact
  frames`.

The output directory contains:

- `report.json`: all accepted, rejected, and deduplicated nodes, objective
  components, compact-state values, exact emulator/relative frames, preflight,
  cleanup, and state provenance;
- `solution.recipe.json`: the shortest-depth goal (or best progress node), its
  parent chain, full zero-based macro, exact frame count, and a bridge-compatible
  replay script;
- `best-recipes.json`: the top configured number of progress routes;
- `failure.json`: the error and cleanup result if a live search fails.

Generated live evidence belongs under `runs/`, whose contents are ignored by
the local tool directory. No run output is needed for offline unit tests.

## Sample recipes

- `samples/croctopus-chase.json` targets level `$0025`, entrance `$003E`, and
  includes the observed `RIGHT+B` movement plus up/down/right B/Y pulses. Its
  camera `$00C0` goal is the object-window audit's useful observation boundary.
- `samples/poison-pond.json` targets level `$0017`, entrance `$0022`, and
  compares Enguarde B/Y holds, turns, and pulses. Its `$0200` camera goal is a
  provisional waypoint before the later camera object at world X `$0350`.

Both samples intentionally omit a state hash. A live operator must provide the
actual external file and pin its observed hash explicitly.
