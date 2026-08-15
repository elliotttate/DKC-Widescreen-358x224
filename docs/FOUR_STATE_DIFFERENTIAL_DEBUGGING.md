# Four-state differential softlock debugging

## Purpose

The three reported softlocks are likely activation, state-machine, collision,
or camera-trigger failures. A screenshot alone cannot distinguish those cases.
The fourth unlocked-world-map state is useful for navigation and fast manual
entry to other levels, but its stale level RAM must not be treated as a level
identity assertion while the map scene is active.

`DKCLevelAutomation` v0.1.3 adds two complementary pieces:

1. `snapshot_wram` atomically copies all 128 KiB of WRAM and returns the exact
   emulated frame and SHA-256 digest.
2. `run_state_differential.py` loads every external state under a clean ROM and
   a candidate ROM, runs identical exact-frame branches, and correlates the
   atomic snapshots with selected composed screenshots and full
   `DKCWidescreenDebugger` bundles.

No state, ROM, screenshot, or binary capture is committed. The checked-in JSON
contains only filenames, identities, hypotheses, input macros, and checkpoint
frames.

## Why this is more reliable

- A checkpoint is taken only while paused, after a completed emulated frame.
- All actor, camera, input, bounds, and game-state fields come from one memory
  copy instead of dozens of bridge calls spread across Unity updates.
- Each input hypothesis starts by reloading the same immutable state. A wrong
  turn or death in one branch cannot contaminate another branch.
- Actor comparison groups by sprite ID and nearest world position. It tolerates
  harmless slot-allocation changes while exposing genuinely missing or extra
  actors.
- Full debugger capture is reserved for selected endpoints. Its GPU readback
  and large file writes happen while emulation is paused, so they cannot change
  the relative-frame schedule.
- Relative frame zero records WRAM but deliberately has no screenshot: the
  transfer texture can still contain the scene shown before the state load.
  Visual evidence starts at relative frame one, after a completed emulated
  frame has rendered the loaded state.
- Both ROM and state hashes are recorded. A later run cannot silently compare a
  different build or user state.

## Current state identities and provisional branches

| State | Observed identity | Diagnostic focus |
|---|---|---|
| `szst0` | Croctopus Chase, level `$0025`, entrance `$003E` | Neutral actor lifecycle, the observed `RIGHT+B` swim, and a downward-turn branch near the wall |
| `szst1` | Poison Pond, level `$0017`, entrance `$0022` | Neutral fish/obstacle lifecycle plus `RIGHT+B` and `RIGHT+Y` Enguarde controls |
| `szst2` | Gang-Plank Galleon, level `$004C`, entrance `$0068` | King K. Rool autonomous lifecycle, normal approach, and approach-with-jump |
| `szst3` | Fully unlocked Gang-Plank Galleon world map | Neutral stability and one deterministic right-navigation smoke test |

The macros deliberately remain hypotheses until the reporter confirms the
precise intended route or the clean-ROM trace identifies it. The runner is most
valuable before that confirmation because it reveals the first relative frame
where actor population or state diverges.

## Reading the report

`report.json` has three main sections:

- `baseline` and `candidate`: per-state, per-branch checkpoint summaries and
  actor lifecycle transitions;
- `comparison.checkpoints[].globalDifferences`: level, entrance, camera, layer,
  bounds, controller and display-state differences;
- `comparison.checkpoints[].actors`: spatially matched actors plus
  `missingFromCandidate` and `extraInCandidate` populations.

The earliest checkpoint with an actor-population divergence is the primary
target. Its candidate and baseline WRAM files show whether the authored object
record/slot bookkeeping also diverged. The selected full capture then supplies
OAM, PPU and renderer evidence to decide whether the object failed to exist or
only failed to render.

Expected widescreen camera/bound differences should not be treated as failures
by themselves. A useful causal chain is an earlier camera/bounds divergence,
then an actor spawn/despawn divergence, followed by a stuck player or unchanged
entrance/state word.

## Validation

The JSON plan can be checked without files or a running emulator:

```powershell
python .\mods\DKCLevelAutomation\cli\run_state_differential.py `
  --recipe four-user-states-differential `
  --baseline-rom clean.sfc --candidate-rom candidate.sfc `
  --state-dir D:\States --validate-only
```

Unit tests cover recipe validation, WRAM decoding, signed actor fields, spatial
actor matching, lifecycle transitions, and changed-memory range reporting.
